using System.Linq;
using CamelWorks.Core.Automation;
using CamelWorks.Core.Testing;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class GraphModelTests
    {
        [Fact]
        public void A_new_node_already_carries_its_defaults_so_it_runs_unconfigured()
        {
            // The zero-setup rule, on the canvas: a node you have just dropped does something.
            var node = new GraphNode("n1", "filter.where");

            Assert.Equal("Element", node.Setting("category"));
            Assert.Equal("Equals", node.Setting("operator"));
        }

        [Fact]
        public void An_input_takes_one_wire_and_connecting_a_second_replaces_the_first()
        {
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("a", "input.everything"));
            graph.Nodes.Add(new GraphNode("b", "input.selection"));
            graph.Nodes.Add(new GraphNode("c", "view.select"));

            graph.Connect(new GraphWire("a", "out", "c", "in"));
            graph.Connect(new GraphWire("b", "out", "c", "in"));

            Assert.Single(graph.Wires);
            Assert.Equal("b", graph.Wires[0].FromNode);
        }

        [Fact]
        public void Removing_a_node_takes_its_wires_with_it()
        {
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("a", "input.everything"));
            graph.Nodes.Add(new GraphNode("b", "view.select"));
            graph.Connect(new GraphWire("a", "out", "b", "in"));

            graph.Remove("a");

            Assert.Empty(graph.Wires);
            Assert.Single(graph.Nodes);
        }

        [Fact]
        public void A_graph_survives_a_round_trip()
        {
            var graph = new Graph("Nightly");
            graph.Nodes.Add(new GraphNode("a", "input.everything") { X = 40, Y = 90 });
            graph.Nodes.Add(new GraphNode("b", "view.colour"));
            graph.Nodes[1].Settings["colour"] = "#123456";
            graph.Connect(new GraphWire("a", "out", "b", "in"));

            var back = Graph.FromJson(graph.ToJson());

            Assert.Equal("Nightly", back.Name);
            Assert.Equal(40, back.Nodes[0].X);
            Assert.Equal("#123456", back.Nodes[1].Setting("colour"));
            Assert.Single(back.Wires);
        }

        [Fact]
        public void A_wire_to_a_node_that_is_gone_is_dropped_rather_than_kept()
        {
            // Kept, it would draw from a node to the origin and look like a bug in the canvas.
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("a", "input.everything"));
            graph.Wires.Add(new GraphWire("a", "out", "vanished", "in"));

            Assert.Empty(Graph.FromJson(graph.ToJson()).Wires);
        }
    }

    public class GraphRunnerTests
    {
        private static Graph Chain()
        {
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("all", "input.everything"));
            graph.Nodes.Add(new GraphNode("where", "filter.where"));
            graph.Nodes.Add(new GraphNode("colour", "view.colour"));
            graph.Connect(new GraphWire("all", "out", "where", "in"));
            graph.Connect(new GraphWire("where", "out", "colour", "in"));
            return graph;
        }

        [Fact]
        public void A_chain_runs_in_order_and_each_step_says_what_it_did()
        {
            var host = new FakeGraphHost(10) { Matches = 4 };

            var run = GraphRunner.Run(Chain(), host);

            Assert.Equal(3, run.Ran);
            Assert.Empty(run.Problems);
            Assert.Equal(new[] { "Everything", "Where", "Colour" }, run.Steps.Select(s => s.Title).ToArray());
            Assert.Contains("colour:#cc3333 x4", host.Did);
        }

        [Fact]
        public void A_node_with_nothing_wired_in_is_skipped_and_says_which_input()
        {
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("colour", "view.colour"));

            var run = GraphRunner.Run(graph, new FakeGraphHost());

            Assert.Equal(0, run.Ran);
            Assert.Contains("elements", run.Steps[0].Outcome);
            Assert.Empty(run.Problems);
        }

        [Fact]
        public void Everything_downstream_of_a_skipped_node_is_skipped_too()
        {
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("where", "filter.where"));
            graph.Nodes.Add(new GraphNode("colour", "view.colour"));
            graph.Connect(new GraphWire("where", "out", "colour", "in"));

            var run = GraphRunner.Run(graph, new FakeGraphHost());

            Assert.Equal(0, run.Ran);
            Assert.Equal(2, run.Skipped);
        }

        [Fact]
        public void A_cycle_stops_the_whole_run_and_names_the_nodes_in_it()
        {
            // Not just the cycle — the run. A graph with a loop has no defined answer, and running
            // the acyclic part of it would produce a real-looking result from half a job.
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("a", "view.colour"));
            graph.Nodes.Add(new GraphNode("b", "view.select"));
            graph.Connect(new GraphWire("a", "out", "b", "in"));
            graph.Connect(new GraphWire("b", "out", "a", "in"));

            var run = GraphRunner.Run(graph, new FakeGraphHost());

            Assert.Empty(run.Steps);
            Assert.Single(run.Problems);
            Assert.Contains("loop", run.Problems[0]);
            Assert.Contains("Colour", run.Problems[0]);
        }

        [Fact]
        public void A_subtract_really_subtracts()
        {
            var host = new FakeGraphHost(10) { Matches = 4 };

            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("all", "input.everything"));
            graph.Nodes.Add(new GraphNode("some", "filter.where"));
            graph.Nodes.Add(new GraphNode("rest", "set.subtract"));
            graph.Nodes.Add(new GraphNode("pick", "view.select"));

            graph.Connect(new GraphWire("all", "out", "some", "in"));
            graph.Connect(new GraphWire("all", "out", "rest", "a"));
            graph.Connect(new GraphWire("some", "out", "rest", "b"));
            graph.Connect(new GraphWire("rest", "out", "pick", "in"));

            GraphRunner.Run(graph, host);

            Assert.Contains("select x6", host.Did);
        }

        [Fact]
        public void A_bad_colour_stops_that_node_without_stopping_the_run()
        {
            var graph = Chain();
            graph.Find("colour")!.Settings["colour"] = "puce";

            var run = GraphRunner.Run(graph, new FakeGraphHost());

            Assert.Equal(2, run.Ran);
            Assert.Contains("puce", run.Steps[2].Outcome);
        }

        [Fact]
        public void A_node_kind_this_build_does_not_have_is_named_rather_than_ignored()
        {
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("x", "something.from.2029"));

            var run = GraphRunner.Run(graph, new FakeGraphHost());

            Assert.Contains("something.from.2029", run.Steps[0].Outcome);
        }

        [Fact]
        public void A_terminal_node_writes_and_produces_nothing()
        {
            var host = new FakeGraphHost(6);

            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("all", "input.everything"));
            graph.Nodes.Add(new GraphNode("sum", "data.takeoff"));
            graph.Nodes.Add(new GraphNode("out", "csv.write"));
            graph.Find("out")!.Settings["path"] = @"C:\out\takeoff.csv";

            graph.Connect(new GraphWire("all", "out", "sum", "in"));
            graph.Connect(new GraphWire("sum", "out", "out", "in"));

            var run = GraphRunner.Run(graph, host);

            Assert.Equal(3, run.Ran);
            Assert.Contains(host.Did, d => d.StartsWith("csv:"));
        }

        [Fact]
        public void A_table_cannot_be_wired_into_an_input_that_wants_elements()
        {
            // The runner does not trust the canvas to have enforced it: a hand-edited file can say
            // anything, and the failure has to be a skipped node rather than a cast exception.
            var graph = new Graph();
            graph.Nodes.Add(new GraphNode("board", "clash.board"));
            graph.Nodes.Add(new GraphNode("pick", "view.select"));
            graph.Wires.Add(new GraphWire("board", "out", "pick", "in"));

            var run = GraphRunner.Run(graph, new FakeGraphHost());

            Assert.Equal(1, run.Ran);
            Assert.False(run.Steps.First(s => s.NodeId == "pick").Ran);
        }
    }
}
