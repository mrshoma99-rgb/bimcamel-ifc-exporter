using System;
using System.Collections.Generic;
using System.Linq;

namespace CamelWorks.Core.Automation
{
    /// <summary>What travels along a wire.</summary>
    public enum PortType
    {
        /// <summary>A set of elements.</summary>
        Keys = 0,

        /// <summary>Rows and columns — a takeoff, a clash board, anything a report can hold.</summary>
        Table = 1,
    }

    /// <summary>One connection point on a node.</summary>
    public sealed class NodePort
    {
        internal NodePort(string id, string title, PortType type)
        {
            Id = id; Title = title; Type = type;
        }

        /// <summary>Stable id, used by wires.</summary>
        public string Id { get; }

        /// <summary>How it reads on the node.</summary>
        public string Title { get; }

        /// <summary>What it carries. A wire between two different types is refused.</summary>
        public PortType Type { get; }

        /// <inheritdoc />
        public override string ToString() => Title;
    }

    /// <summary>What kind of control a setting needs.</summary>
    public enum SettingKind
    {
        /// <summary>Free text.</summary>
        Text = 0,

        /// <summary>A number.</summary>
        Number = 1,

        /// <summary>One of a fixed list.</summary>
        Choice = 2,
    }

    /// <summary>One thing a node needs told.</summary>
    public sealed class NodeSetting
    {
        internal NodeSetting(string id, string title, SettingKind kind, string @default,
                             IReadOnlyList<string>? choices = null)
        {
            Id = id; Title = title; Kind = kind; Default = @default; Choices = choices ?? Array.Empty<string>();
        }

        /// <summary>Stable id.</summary>
        public string Id { get; }

        /// <summary>How it reads on the node.</summary>
        public string Title { get; }

        /// <summary>What kind of control it needs.</summary>
        public SettingKind Kind { get; }

        /// <summary>What it is before anybody changes it — so a freshly dropped node already runs.</summary>
        public string Default { get; }

        /// <summary>The options, when it is a choice.</summary>
        public IReadOnlyList<string> Choices { get; }

        /// <inheritdoc />
        public override string ToString() => Title;
    }

    /// <summary>One kind of node.</summary>
    public sealed class NodeKind
    {
        internal NodeKind(string id, string group, string title, string summary,
                          NodePort[] inputs, NodePort[] outputs, NodeSetting[] settings)
        {
            Id = id; Group = group; Title = title; Summary = summary;
            Inputs = inputs; Outputs = outputs; Settings = settings;
        }

        /// <summary>Stable id, written to the file.</summary>
        public string Id { get; }

        /// <summary>Which section of the add menu it appears under.</summary>
        public string Group { get; }

        /// <summary>How it reads.</summary>
        public string Title { get; }

        /// <summary>One line saying what it does.</summary>
        public string Summary { get; }

        /// <summary>Its inputs, in order.</summary>
        public IReadOnlyList<NodePort> Inputs { get; }

        /// <summary>Its outputs, in order.</summary>
        public IReadOnlyList<NodePort> Outputs { get; }

        /// <summary>What it needs told.</summary>
        public IReadOnlyList<NodeSetting> Settings { get; }

        /// <summary>A port by id, input or output.</summary>
        /// <param name="portId">The port id.</param>
        public NodePort? Port(string? portId) =>
            Inputs.Concat(Outputs).FirstOrDefault(p => string.Equals(p.Id, portId, StringComparison.Ordinal));

        /// <inheritdoc />
        public override string ToString() => Title;
    }

    /// <summary>
    /// Every node the canvas offers.
    ///
    /// <b>Each one is a service the ribbon already drives.</b> That is the whole architecture of
    /// this product in one list: nothing here is a second implementation of anything, so a fix to
    /// the takeoff or the appearance planner reaches the graph and the buttons at the same time —
    /// and neither front door can quietly drift from the other.
    ///
    /// Every setting has a default that works, so a node dropped on the canvas runs without being
    /// configured first.
    /// </summary>
    public static class NodeCatalogue
    {
        private static readonly NodePort[] None = Array.Empty<NodePort>();
        private static readonly NodeSetting[] NoSettings = Array.Empty<NodeSetting>();

        private static NodePort Keys(string id, string title) => new NodePort(id, title, PortType.Keys);
        private static NodePort Table(string id, string title) => new NodePort(id, title, PortType.Table);

        private static NodeSetting Text(string id, string title, string value = "") =>
            new NodeSetting(id, title, SettingKind.Text, value);

        private static NodeSetting Number(string id, string title, string value) =>
            new NodeSetting(id, title, SettingKind.Number, value);

        private static NodeSetting Choice(string id, string title, string value, params string[] choices) =>
            new NodeSetting(id, title, SettingKind.Choice, value, choices);

        /// <summary>Every node kind, in menu order.</summary>
        public static IReadOnlyList<NodeKind> All { get; } = new[]
        {
            new NodeKind("input.everything", "Pick", "Everything",
                "Every element loaded.",
                None, new[] { Keys("out", "elements") }, NoSettings),

            new NodeKind("input.selection", "Pick", "Selection",
                "Whatever is selected in Navisworks when the graph runs.",
                None, new[] { Keys("out", "elements") }, NoSettings),

            new NodeKind("input.set", "Pick", "Saved set",
                "The contents of a Navisworks selection or search set, by name.",
                None, new[] { Keys("out", "elements") },
                new[] { Text("name", "Set name") }),

            new NodeKind("filter.where", "Narrow", "Where",
                "Keeps only the elements whose property matches.",
                new[] { Keys("in", "elements") }, new[] { Keys("out", "elements") },
                new[]
                {
                    Text("category", "Category", "Element"),
                    Text("property", "Property", "Category"),
                    Choice("operator", "Comparison", "Equals",
                        "Equals", "Contains", "StartsWith", "EndsWith", "WildcardMatch",
                        "GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual", "Defined"),
                    Text("value", "Value"),
                }),

            new NodeKind("set.union", "Combine", "Union",
                "Everything in either side.",
                new[] { Keys("a", "A"), Keys("b", "B") }, new[] { Keys("out", "elements") }, NoSettings),

            new NodeKind("set.intersect", "Combine", "Intersect",
                "Only what is in both sides.",
                new[] { Keys("a", "A"), Keys("b", "B") }, new[] { Keys("out", "elements") }, NoSettings),

            new NodeKind("set.subtract", "Combine", "Subtract",
                "A, without anything that is also in B.",
                new[] { Keys("a", "A"), Keys("b", "B") }, new[] { Keys("out", "elements") }, NoSettings),

            new NodeKind("view.colour", "Look", "Colour",
                "Overrides the colour, and passes the elements on so more can be done to them.",
                new[] { Keys("in", "elements") }, new[] { Keys("out", "elements") },
                new[] { Text("colour", "Colour (#rrggbb)", "#cc3333") }),

            new NodeKind("view.transparency", "Look", "Transparency",
                "0 is opaque, 1 is invisible.",
                new[] { Keys("in", "elements") }, new[] { Keys("out", "elements") },
                new[] { Number("value", "Transparency", "0.7") }),

            new NodeKind("view.visibility", "Look", "Show or hide",
                "Hides or shows the elements.",
                new[] { Keys("in", "elements") }, new[] { Keys("out", "elements") },
                new[] { Choice("state", "State", "hide", "hide", "show") }),

            new NodeKind("view.select", "Look", "Select",
                "Makes these the current selection in Navisworks.",
                new[] { Keys("in", "elements") }, new[] { Keys("out", "elements") }, NoSettings),

            new NodeKind("data.write", "Data", "Write a property",
                "Writes into the CamelWorks tab, never into the authoring tool's own.",
                new[] { Keys("in", "elements") }, new[] { Keys("out", "elements") },
                new[] { Text("name", "Property", "Zone"), Text("value", "Value") }),

            new NodeKind("data.takeoff", "Data", "Takeoff",
                "Sums a numeric property, grouped.",
                new[] { Keys("in", "elements") }, new[] { Table("out", "table") },
                new[]
                {
                    Choice("measure", "Measure", "Volume", "Volume", "Area", "Length", "Count"),
                    Choice("group", "Group by", "Category", "Category", "Type", "Model", "Level"),
                }),

            new NodeKind("clash.board", "Data", "Clash board",
                "The board as the rules leave it, as a table.",
                None, new[] { Table("out", "table") }, NoSettings),

            new NodeKind("report.write", "Deliver", "Report",
                "Writes the table as a PDF, spreadsheet or web page.",
                new[] { Table("in", "table") }, None,
                new[]
                {
                    Text("path", "File"),
                    Choice("format", "Format", "PDF", "PDF", "XLSX", "HTML"),
                    Text("title", "Title", "CamelWorks report"),
                }),

            new NodeKind("csv.write", "Deliver", "CSV",
                "Writes the table as comma-separated text.",
                new[] { Table("in", "table") }, None,
                new[] { Text("path", "File") }),
        };

        /// <summary>A kind by id, or null when a saved graph names one this build does not have.</summary>
        /// <param name="id">The kind id.</param>
        public static NodeKind? Find(string? id) =>
            All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));

        /// <summary>The groups, in menu order.</summary>
        public static IReadOnlyList<string> Groups { get; } =
            All.Select(k => k.Group).Distinct(StringComparer.Ordinal).ToList();
    }
}
