using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Report;
using CamelWorks.Core.Sets;

namespace CamelWorks.Core.Automation
{
    /// <summary>
    /// What a graph needs the host to actually do.
    ///
    /// The seam again, and for the same reason: every node above this line is a pure decision about
    /// what to do next, so the whole runner — ordering, cycle detection, type checking, what
    /// happens when an input is missing — is provable on a Linux CI job with no Navisworks anywhere
    /// near it.
    /// </summary>
    public interface IGraphHost
    {
        /// <summary>Every element loaded.</summary>
        IReadOnlyList<ElementKey> Everything();

        /// <summary>Whatever is selected in the host right now.</summary>
        IReadOnlyList<ElementKey> Selection();

        /// <summary>The contents of a named host set. Empty when there is no such set.</summary>
        /// <param name="name">The set's name.</param>
        IReadOnlyList<ElementKey> Set(string name);

        /// <summary>Those of <paramref name="within"/> that match a condition.</summary>
        /// <param name="within">The elements to narrow.</param>
        /// <param name="condition">The test.</param>
        IReadOnlyList<ElementKey> Where(IReadOnlyList<ElementKey> within, SetCondition condition);

        /// <summary>Override colour.</summary>
        /// <param name="keys">The elements.</param>
        /// <param name="colour">The colour.</param>
        void Colour(IReadOnlyList<ElementKey> keys, Colour colour);

        /// <summary>Override transparency, 0 to 1.</summary>
        /// <param name="keys">The elements.</param>
        /// <param name="value">The transparency.</param>
        void Transparency(IReadOnlyList<ElementKey> keys, double value);

        /// <summary>Show or hide.</summary>
        /// <param name="keys">The elements.</param>
        /// <param name="visible">True to show.</param>
        void Visible(IReadOnlyList<ElementKey> keys, bool visible);

        /// <summary>Make these the host's current selection.</summary>
        /// <param name="keys">The elements.</param>
        void Select(IReadOnlyList<ElementKey> keys);

        /// <summary>Write a property, returning how many elements changed.</summary>
        /// <param name="keys">The elements.</param>
        /// <param name="property">The property name.</param>
        /// <param name="value">The value.</param>
        int Write(IReadOnlyList<ElementKey> keys, string property, string? value);

        /// <summary>Sum a measure over the elements, grouped.</summary>
        /// <param name="keys">The elements.</param>
        /// <param name="measure">What to measure.</param>
        /// <param name="group">What to group by.</param>
        TableBlock Takeoff(IReadOnlyList<ElementKey> keys, string measure, string group);

        /// <summary>The clash board as a table.</summary>
        TableBlock ClashBoard();

        /// <summary>Write a report.</summary>
        /// <param name="path">Where to write.</param>
        /// <param name="format">PDF, XLSX or HTML.</param>
        /// <param name="title">The report's title.</param>
        /// <param name="table">What to put in it.</param>
        void Report(string path, string format, string title, TableBlock table);

        /// <summary>Write comma-separated text.</summary>
        /// <param name="path">Where to write.</param>
        /// <param name="table">What to write.</param>
        void Csv(string path, TableBlock table);
    }

    /// <summary>What one node did.</summary>
    public sealed class GraphStep
    {
        internal GraphStep(string nodeId, string title, bool ran, string outcome)
        {
            NodeId = nodeId; Title = title; Ran = ran; Outcome = outcome;
        }

        /// <summary>Which node.</summary>
        public string NodeId { get; }

        /// <summary>How the node reads.</summary>
        public string Title { get; }

        /// <summary>Whether it ran, as opposed to being skipped.</summary>
        public bool Ran { get; }

        /// <summary>What it did, or why it did not.</summary>
        public string Outcome { get; }

        /// <inheritdoc />
        public override string ToString() => Title + ": " + Outcome;
    }

    /// <summary>What a whole run did.</summary>
    public sealed class GraphRun
    {
        internal GraphRun(IReadOnlyList<GraphStep> steps, IReadOnlyList<string> problems)
        {
            Steps = steps; Problems = problems;
        }

        /// <summary>What each node did, in the order they ran.</summary>
        public IReadOnlyList<GraphStep> Steps { get; }

        /// <summary>Anything that stopped part of the graph running.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>How many nodes actually ran.</summary>
        public int Ran => Steps.Count(s => s.Ran);

        /// <summary>How many were skipped.</summary>
        public int Skipped => Steps.Count(s => !s.Ran);

        /// <inheritdoc />
        public override string ToString()
        {
            var s = Ran.ToString(CultureInfo.InvariantCulture) + " of "
                    + Steps.Count.ToString(CultureInfo.InvariantCulture) + " nodes ran";

            if (Skipped > 0) s += ", " + Skipped.ToString(CultureInfo.InvariantCulture) + " skipped";
            if (Problems.Count > 0) s += ", " + Problems.Count.ToString(CultureInfo.InvariantCulture) + " problems";
            return s;
        }
    }

    /// <summary>
    /// Runs a graph.
    ///
    /// Two decisions worth stating, because both could reasonably go the other way:
    ///
    /// 1. <b>A cycle stops the whole run.</b> Not the cycle, the run. A graph with a loop in it has
    ///    no defined answer, and running the acyclic part of it would produce a real-looking result
    ///    from half a job.
    /// 2. <b>A node with a missing input is skipped, not failed.</b> Everything downstream of it is
    ///    skipped too, and every one of them says why. A half-built graph is the ordinary state of
    ///    one being written, and refusing to run any of it until every wire is in place would make
    ///    the canvas useless exactly while it is being used.
    /// </summary>
    public static class GraphRunner
    {
        private sealed class Value
        {
            internal IReadOnlyList<ElementKey>? Keys;
            internal TableBlock? Table;
        }

        /// <summary>
        /// Run a graph.
        /// </summary>
        /// <param name="graph">What to run.</param>
        /// <param name="host">What does the work.</param>
        /// <param name="say">Called with progress, or null.</param>
        public static GraphRun Run(Graph graph, IGraphHost host, Action<string>? say = null)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (host == null) throw new ArgumentNullException(nameof(host));

            var steps = new List<GraphStep>();
            var problems = new List<string>();

            var order = Order(graph, problems);

            if (order == null) return new GraphRun(steps, problems);

            var outputs = new Dictionary<string, Value>(StringComparer.Ordinal);

            foreach (var node in order)
            {
                var definition = node.Definition;

                if (definition == null)
                {
                    steps.Add(new GraphStep(node.Id, node.Kind,
                        false, "this build has no node called \"" + node.Kind + "\""));
                    continue;
                }

                say?.Invoke(definition.Title);

                var inputs = new Dictionary<string, Value>(StringComparer.Ordinal);
                var missing = definition.Inputs.FirstOrDefault(p => !Gather(graph, outputs, node, p, inputs));

                if (missing != null)
                {
                    steps.Add(new GraphStep(node.Id, definition.Title, false,
                        "nothing is wired into \"" + missing.Title + "\""));
                    continue;
                }

                try
                {
                    var ran = Evaluate(node, definition, inputs, host, out var produced, out var outcome);

                    if (produced != null) outputs[node.Id] = produced;

                    steps.Add(new GraphStep(node.Id, definition.Title, ran, outcome));
                }
                catch (Exception e)
                {
                    // One node failing does not take the run with it: everything downstream is
                    // skipped for want of its output, which is what the user needs to see anyway.
                    steps.Add(new GraphStep(node.Id, definition.Title, false, e.Message));
                    problems.Add(definition.Title + ": " + e.Message);
                }
            }

            return new GraphRun(steps, problems);
        }

        private static bool Gather(Graph graph, IReadOnlyDictionary<string, Value> outputs,
                                   GraphNode node, NodePort port, IDictionary<string, Value> into)
        {
            var wire = graph.Into(node.Id, port.Id);
            if (wire == null) return false;

            if (!outputs.TryGetValue(wire.FromNode, out var value)) return false;

            if (port.Type == PortType.Keys && value.Keys == null) return false;
            if (port.Type == PortType.Table && value.Table == null) return false;

            into[port.Id] = value;
            return true;
        }

        /// <summary>
        /// Run one node.
        ///
        /// Returns whether it did its job, which is not the same as whether it produced a value: a
        /// report node produces nothing and has still worked, and a colour node given "puce"
        /// produces nothing and has not. Conflating the two is how a run reports three of three
        /// nodes ran while one of them refused.
        /// </summary>
        private static bool Evaluate(GraphNode node, NodeKind definition, IDictionary<string, Value> inputs,
                                     IGraphHost host, out Value? produced, out string outcome)
        {
            produced = null;

            IReadOnlyList<ElementKey> In(string port) => inputs[port].Keys ?? Array.Empty<ElementKey>();

            switch (node.Kind)
            {
                case "input.everything":
                    produced = Keys(host.Everything(), out outcome);
                    return true;

                case "input.selection":
                    produced = Keys(host.Selection(), out outcome);
                    return true;

                case "input.set":
                    produced = Keys(host.Set(node.Setting("name")), out outcome);
                    return true;

                case "filter.where":
                {
                    var condition = Condition(node);

                    if (condition == null)
                    {
                        outcome = "the comparison and the value do not go together";
                        return false;
                    }

                    produced = Keys(host.Where(In("in"), condition), out outcome);
                    return true;
                }

                case "set.union":
                    produced = Keys(In("a").Concat(In("b")).Distinct().ToList(), out outcome);
                    return true;

                case "set.intersect":
                {
                    var other = new HashSet<ElementKey>(In("b"));
                    produced = Keys(In("a").Where(other.Contains).ToList(), out outcome);
                    return true;
                }

                case "set.subtract":
                {
                    var other = new HashSet<ElementKey>(In("b"));
                    produced = Keys(In("a").Where(k => !other.Contains(k)).ToList(), out outcome);
                    return true;
                }

                case "view.colour":
                {
                    if (!Colour.TryParse(node.Setting("colour"), out var colour))
                    {
                        outcome = "\"" + node.Setting("colour") + "\" is not a colour like #cc3333";
                        return false;
                    }

                    host.Colour(In("in"), colour);
                    produced = Keys(In("in"), out outcome, "coloured");
                    return true;
                }

                case "view.transparency":
                    host.Transparency(In("in"), Math.Min(1, Math.Max(0, node.Number("value", 0.7))));
                    produced = Keys(In("in"), out outcome, "made transparent");
                    return true;

                case "view.visibility":
                    host.Visible(In("in"), node.Setting("state") == "show");
                    produced = Keys(In("in"), out outcome, node.Setting("state") == "show" ? "shown" : "hidden");
                    return true;

                case "view.select":
                    host.Select(In("in"));
                    produced = Keys(In("in"), out outcome, "selected");
                    return true;

                case "data.write":
                {
                    var written = host.Write(In("in"), node.Setting("name"), node.Setting("value"));
                    outcome = written.ToString("N0", CultureInfo.InvariantCulture) + " written";
                    produced = new Value { Keys = In("in") };
                    return true;
                }

                case "data.takeoff":
                {
                    var table = host.Takeoff(In("in"), node.Setting("measure"), node.Setting("group"));
                    outcome = table.Rows.Count.ToString("N0", CultureInfo.InvariantCulture) + " rows";
                    produced = new Value { Table = table };
                    return true;
                }

                case "clash.board":
                {
                    var table = host.ClashBoard();
                    outcome = table.Rows.Count.ToString("N0", CultureInfo.InvariantCulture) + " rows";
                    produced = new Value { Table = table };
                    return true;
                }

                case "report.write":
                {
                    var path = node.Setting("path");

                    if (path.Trim().Length == 0)
                    {
                        outcome = "no file to write to";
                        return false;
                    }

                    host.Report(path, node.Setting("format"), node.Setting("title"), inputs["in"].Table!);
                    outcome = "wrote " + path;
                    return true;
                }

                case "csv.write":
                {
                    var path = node.Setting("path");

                    if (path.Trim().Length == 0)
                    {
                        outcome = "no file to write to";
                        return false;
                    }

                    host.Csv(path, inputs["in"].Table!);
                    outcome = "wrote " + path;
                    return true;
                }

                default:
                    outcome = "this build does not know how to run " + definition.Title;
                    return false;
            }
        }

        private static Value Keys(IReadOnlyList<ElementKey> keys, out string outcome, string? verb = null)
        {
            outcome = keys.Count.ToString("N0", CultureInfo.InvariantCulture)
                      + (keys.Count == 1 ? " element" : " elements")
                      + (verb == null ? string.Empty : " " + verb);

            return new Value { Keys = keys };
        }

        private static SetCondition? Condition(GraphNode node)
        {
            if (!Enum.TryParse<SetOperator>(node.Setting("operator"), out var op)) return null;

            var category = node.Setting("category").Trim();
            if (category.Length == 0) return null;

            var property = node.Setting("property").Trim();

            try
            {
                return new SetCondition(category, property.Length == 0 ? null : property, op,
                                        op == SetOperator.Defined || op == SetOperator.HasCategory
                                            ? null
                                            : node.Setting("value"));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// The order to run in, or null when the graph has a cycle.
        ///
        /// Depth-first with three colours rather than Kahn's algorithm, because the useful thing to
        /// report is not "there is a cycle somewhere" but which nodes are in it.
        /// </summary>
        private static IReadOnlyList<GraphNode>? Order(Graph graph, ICollection<string> problems)
        {
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<GraphNode>();
            var path = new List<string>();
            var cyclic = false;

            void Visit(GraphNode node)
            {
                if (cyclic) return;

                if (state.TryGetValue(node.Id, out var mark))
                {
                    if (mark != 1) return;

                    var at = path.IndexOf(node.Id);
                    var loop = path.Skip(at < 0 ? 0 : at).Concat(new[] { node.Id });

                    problems.Add("These nodes are wired in a loop, so the graph has no order to run in: "
                                 + string.Join(" -> ", loop.Select(id => graph.Find(id)?.Definition?.Title ?? id))
                                 + ".");

                    cyclic = true;
                    return;
                }

                state[node.Id] = 1;
                path.Add(node.Id);

                foreach (var wire in graph.Wires.Where(w => string.Equals(w.ToNode, node.Id, StringComparison.Ordinal)))
                {
                    var source = graph.Find(wire.FromNode);
                    if (source != null) Visit(source);
                    if (cyclic) return;
                }

                path.RemoveAt(path.Count - 1);
                state[node.Id] = 2;
                order.Add(node);
            }

            foreach (var node in graph.Nodes)
            {
                Visit(node);
                if (cyclic) return null;
            }

            return order;
        }
    }
}
