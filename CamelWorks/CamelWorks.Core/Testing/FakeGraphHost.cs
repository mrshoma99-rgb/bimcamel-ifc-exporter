using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Automation;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Report;
using CamelWorks.Core.Sets;

namespace CamelWorks.Core.Testing
{
    /// <summary>
    /// A graph host that records what it was asked to do instead of doing it.
    ///
    /// Every question worth asking about the runner — does a cycle stop the run, is a node with a
    /// missing input skipped, does everything downstream of a skipped node get skipped too, does a
    /// subtract really subtract — is a question about the order and the arguments, not about
    /// Navisworks. So they are all answerable here, on a Linux CI job.
    /// </summary>
    public sealed class FakeGraphHost : IGraphHost
    {
        private readonly List<ElementKey> _all = new List<ElementKey>();

        /// <summary>Create a host holding a given number of elements.</summary>
        /// <param name="elements">How many elements the model has.</param>
        public FakeGraphHost(int elements = 10)
        {
            for (var i = 0; i < elements; i++)
                _all.Add(ElementKey.FromTreePath("model", null, "item " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        /// <summary>Everything the host was asked to do, in order.</summary>
        public IList<string> Did { get; } = new List<string>();

        /// <summary>What <see cref="Selection"/> returns.</summary>
        public IList<ElementKey> Selected { get; } = new List<ElementKey>();

        /// <summary>Named sets, by name.</summary>
        public IDictionary<string, IReadOnlyList<ElementKey>> Sets { get; } =
            new Dictionary<string, IReadOnlyList<ElementKey>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many of the elements a Where matches. The first N are returned.</summary>
        public int Matches { get; set; } = 3;

        /// <summary>Every element.</summary>
        public IReadOnlyList<ElementKey> Everything() { Did.Add("everything"); return _all; }

        /// <inheritdoc />
        public IReadOnlyList<ElementKey> Selection() { Did.Add("selection"); return Selected.ToList(); }

        /// <inheritdoc />
        public IReadOnlyList<ElementKey> Set(string name)
        {
            Did.Add("set:" + name);
            return Sets.TryGetValue(name, out var keys) ? keys : Array.Empty<ElementKey>();
        }

        /// <inheritdoc />
        public IReadOnlyList<ElementKey> Where(IReadOnlyList<ElementKey> within, SetCondition condition)
        {
            Did.Add("where:" + condition.Canonical + " in " + within.Count);
            return within.Take(Matches).ToList();
        }

        /// <inheritdoc />
        public void Colour(IReadOnlyList<ElementKey> keys, Colour colour) =>
            Did.Add("colour:" + colour + " x" + keys.Count);

        /// <inheritdoc />
        public void Transparency(IReadOnlyList<ElementKey> keys, double value) =>
            Did.Add("transparency:" + value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " x" + keys.Count);

        /// <inheritdoc />
        public void Visible(IReadOnlyList<ElementKey> keys, bool visible) =>
            Did.Add((visible ? "show" : "hide") + " x" + keys.Count);

        /// <inheritdoc />
        public void Select(IReadOnlyList<ElementKey> keys) => Did.Add("select x" + keys.Count);

        /// <inheritdoc />
        public int Write(IReadOnlyList<ElementKey> keys, string property, string? value)
        {
            Did.Add("write:" + property + "=" + (value ?? string.Empty) + " x" + keys.Count);
            return keys.Count;
        }

        /// <inheritdoc />
        public TableBlock Takeoff(IReadOnlyList<ElementKey> keys, string measure, string group)
        {
            Did.Add("takeoff:" + measure + " by " + group + " x" + keys.Count);

            var table = new TableBlock("Group", "Count");
            table.Row(group, keys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return table;
        }

        /// <inheritdoc />
        public TableBlock ClashBoard()
        {
            Did.Add("board");

            var table = new TableBlock("Group", "Clashes");
            table.Row("Level 3 riser", "12");
            return table;
        }

        /// <inheritdoc />
        public void Report(string path, string format, string title, TableBlock table) =>
            Did.Add("report:" + format + " " + path + " (" + table.Rows.Count + " rows)");

        /// <inheritdoc />
        public void Csv(string path, TableBlock table) =>
            Did.Add("csv:" + path + " (" + table.Rows.Count + " rows)");
    }
}
