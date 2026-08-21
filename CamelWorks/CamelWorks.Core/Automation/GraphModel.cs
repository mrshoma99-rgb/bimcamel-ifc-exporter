using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Store;

namespace CamelWorks.Core.Automation
{
    /// <summary>One node on the canvas.</summary>
    public sealed class GraphNode
    {
        /// <summary>Create a node.</summary>
        /// <param name="id">Stable id, referenced by wires.</param>
        /// <param name="kind">One of <see cref="NodeCatalogue"/>.</param>
        public GraphNode(string id, string kind)
        {
            Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("id is required", nameof(id)) : id;
            Kind = string.IsNullOrWhiteSpace(kind) ? throw new ArgumentException("kind is required", nameof(kind)) : kind;

            foreach (var setting in Definition?.Settings ?? Array.Empty<NodeSetting>())
                Settings[setting.Id] = setting.Default;
        }

        /// <summary>Stable id.</summary>
        public string Id { get; }

        /// <summary>Which kind of node this is.</summary>
        public string Kind { get; }

        /// <summary>Where it sits on the canvas.</summary>
        public double X { get; set; }

        /// <summary>Where it sits on the canvas.</summary>
        public double Y { get; set; }

        /// <summary>What it has been told, by setting id.</summary>
        public IDictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The kind, or null when a saved graph names one this build does not have.</summary>
        public NodeKind? Definition => NodeCatalogue.Find(Kind);

        /// <summary>A setting's value, falling back to its default and then to empty.</summary>
        /// <param name="id">The setting id.</param>
        public string Setting(string id) =>
            Settings.TryGetValue(id, out var value) && value != null
                ? value
                : Definition?.Settings.FirstOrDefault(s => s.Id == id)?.Default ?? string.Empty;

        /// <summary>A setting parsed as a number, or the fallback.</summary>
        /// <param name="id">The setting id.</param>
        /// <param name="fallback">What to use when it does not parse.</param>
        public double Number(string id, double fallback = 0) =>
            double.TryParse(Setting(id), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        /// <summary>Serialise.</summary>
        public JsonValue ToJson()
        {
            var settings = JsonValue.Object();
            foreach (var pair in Settings) settings.Set(pair.Key, JsonValue.String(pair.Value));

            return JsonValue.Object()
                .Set("id", JsonValue.String(Id))
                .Set("kind", JsonValue.String(Kind))
                .Set("x", JsonValue.Number(X))
                .Set("y", JsonValue.Number(Y))
                .Set("settings", settings);
        }

        /// <summary>Read one back, or null.</summary>
        /// <param name="json">The candidate.</param>
        public static GraphNode? FromJson(JsonValue? json)
        {
            if (json == null || json.Kind != JsonKind.Object) return null;

            var id = json["id"].AsString();
            var kind = json["kind"].AsString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(kind)) return null;

            var node = new GraphNode(id!, kind!) { X = json["x"].AsDouble(), Y = json["y"].AsDouble() };

            var settings = json["settings"];
            foreach (var key in settings.Keys) node.Settings[key] = settings[key].AsString() ?? string.Empty;

            return node;
        }

        /// <inheritdoc />
        public override string ToString() => (Definition?.Title ?? Kind) + " [" + Id + "]";
    }

    /// <summary>One wire between two nodes.</summary>
    public sealed class GraphWire
    {
        /// <summary>Create a wire.</summary>
        /// <param name="fromNode">The node it leaves.</param>
        /// <param name="fromPort">The output port.</param>
        /// <param name="toNode">The node it arrives at.</param>
        /// <param name="toPort">The input port.</param>
        public GraphWire(string fromNode, string fromPort, string toNode, string toPort)
        {
            FromNode = fromNode ?? throw new ArgumentNullException(nameof(fromNode));
            FromPort = fromPort ?? throw new ArgumentNullException(nameof(fromPort));
            ToNode = toNode ?? throw new ArgumentNullException(nameof(toNode));
            ToPort = toPort ?? throw new ArgumentNullException(nameof(toPort));
        }

        /// <summary>The node it leaves.</summary>
        public string FromNode { get; }

        /// <summary>The output port.</summary>
        public string FromPort { get; }

        /// <summary>The node it arrives at.</summary>
        public string ToNode { get; }

        /// <summary>The input port.</summary>
        public string ToPort { get; }

        /// <summary>Serialise.</summary>
        public JsonValue ToJson() =>
            JsonValue.Object()
                .Set("from", JsonValue.String(FromNode))
                .Set("fromPort", JsonValue.String(FromPort))
                .Set("to", JsonValue.String(ToNode))
                .Set("toPort", JsonValue.String(ToPort));

        /// <summary>Read one back, or null.</summary>
        /// <param name="json">The candidate.</param>
        public static GraphWire? FromJson(JsonValue? json)
        {
            if (json == null || json.Kind != JsonKind.Object) return null;

            var from = json["from"].AsString();
            var to = json["to"].AsString();
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;

            return new GraphWire(from!, json["fromPort"].AsString() ?? "out", to!,
                                 json["toPort"].AsString() ?? "in");
        }

        /// <inheritdoc />
        public override string ToString() => FromNode + "." + FromPort + " -> " + ToNode + "." + ToPort;
    }

    /// <summary>
    /// A saved graph: nodes, the wires between them, and a name.
    ///
    /// Kept as data with no behaviour of its own beyond keeping itself consistent, so that the
    /// canvas, the runner and the file are all looking at the same thing rather than three
    /// representations that have to be converted between.
    /// </summary>
    public sealed class Graph
    {
        /// <summary>Create a graph.</summary>
        /// <param name="name">What it is called.</param>
        public Graph(string name = "New job") => Name = name;

        /// <summary>What it is called.</summary>
        public string Name { get; set; }

        /// <summary>The nodes.</summary>
        public IList<GraphNode> Nodes { get; } = new List<GraphNode>();

        /// <summary>The wires.</summary>
        public IList<GraphWire> Wires { get; } = new List<GraphWire>();

        /// <summary>A node by id, or null.</summary>
        /// <param name="id">The node id.</param>
        public GraphNode? Find(string? id) =>
            id == null ? null : Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));

        /// <summary>An id no node is using.</summary>
        /// <param name="kind">The node kind, used as the stem.</param>
        public string NextId(string kind)
        {
            var stem = kind.Replace('.', '-');

            for (var n = 1; ; n++)
            {
                var candidate = stem + "-" + n.ToString(CultureInfo.InvariantCulture);
                if (Find(candidate) == null) return candidate;
            }
        }

        /// <summary>
        /// Add a wire, replacing whatever was already arriving at that input.
        ///
        /// An input takes one wire. Two would mean the node had to decide which won, and every
        /// answer to that is a surprise to somebody — so connecting a second one disconnects the
        /// first, visibly, rather than silently doing something.
        /// </summary>
        /// <param name="wire">The wire.</param>
        public void Connect(GraphWire wire)
        {
            if (wire == null) throw new ArgumentNullException(nameof(wire));

            var existing = Wires.Where(w => string.Equals(w.ToNode, wire.ToNode, StringComparison.Ordinal)
                                            && string.Equals(w.ToPort, wire.ToPort, StringComparison.Ordinal))
                                .ToList();

            foreach (var old in existing) Wires.Remove(old);

            Wires.Add(wire);
        }

        /// <summary>Remove a node and every wire touching it.</summary>
        /// <param name="id">The node id.</param>
        public void Remove(string id)
        {
            var node = Find(id);
            if (node != null) Nodes.Remove(node);

            foreach (var wire in Wires.Where(w => w.FromNode == id || w.ToNode == id).ToList())
                Wires.Remove(wire);
        }

        /// <summary>The wire arriving at an input, or null.</summary>
        /// <param name="nodeId">The node.</param>
        /// <param name="portId">The input port.</param>
        public GraphWire? Into(string nodeId, string portId) =>
            Wires.FirstOrDefault(w => string.Equals(w.ToNode, nodeId, StringComparison.Ordinal)
                                      && string.Equals(w.ToPort, portId, StringComparison.Ordinal));

        /// <summary>Serialise.</summary>
        public JsonValue ToJson() =>
            JsonValue.Object()
                .Set("name", JsonValue.String(Name))
                .Set("nodes", JsonValue.Array(Nodes.Select(n => n.ToJson())))
                .Set("wires", JsonValue.Array(Wires.Select(w => w.ToJson())));

        /// <summary>
        /// Read a graph back.
        ///
        /// A wire whose ends no longer exist is dropped rather than kept, because a wire to nowhere
        /// would draw across the canvas from a node to the origin and look like a bug in the
        /// canvas rather than a missing node.
        /// </summary>
        /// <param name="json">The saved graph.</param>
        public static Graph FromJson(JsonValue? json)
        {
            var graph = new Graph();
            if (json == null || json.Kind != JsonKind.Object) return graph;

            graph.Name = json["name"].AsString() ?? "New job";

            foreach (var item in json["nodes"].Items)
            {
                var node = GraphNode.FromJson(item);
                if (node != null) graph.Nodes.Add(node);
            }

            foreach (var item in json["wires"].Items)
            {
                var wire = GraphWire.FromJson(item);

                if (wire != null && graph.Find(wire.FromNode) != null && graph.Find(wire.ToNode) != null)
                    graph.Wires.Add(wire);
            }

            return graph;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Name + " — " + Nodes.Count + " nodes, " + Wires.Count + " wires";
    }
}
