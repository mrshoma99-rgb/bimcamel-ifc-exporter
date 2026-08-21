using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Automation;
using CamelWorks.Core.Data;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Report;
using CamelWorks.Core.Sets;
using CamelWorks.Nav;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// What a graph node actually does, in Navisworks.
    ///
    /// Every method here is a call into a service the ribbon also calls. That is the point of the
    /// two front doors: the graph is not a second implementation of anything, so a node and the
    /// button beside it cannot come to disagree.
    /// </summary>
    public sealed class NavGraphHost : IGraphHost
    {
        private readonly Session _session;
        private IReadOnlyList<ElementKey>? _everything;

        /// <summary>Create a host over one session.</summary>
        /// <param name="session">The document to work on.</param>
        public NavGraphHost(Session session) =>
            _session = session ?? throw new ArgumentNullException(nameof(session));

        /// <inheritdoc />
        public IReadOnlyList<ElementKey> Everything() =>
            _everything ??= _session.Model.Traverse(TraversalScope.WholeDocument).Select(i => i.Key).ToList();

        /// <inheritdoc />
        public IReadOnlyList<ElementKey> Selection() => _session.Model.SelectedKeys();

        /// <inheritdoc />
        public IReadOnlyList<ElementKey> Set(string name) =>
            _session.Model.Traverse(TraversalScope.Set(name)).Select(i => i.Key).ToList();

        /// <inheritdoc />
        public IReadOnlyList<ElementKey> Where(IReadOnlyList<ElementKey> within, SetCondition condition)
        {
            var found = _session.Search.Run(DnfCompiler.Compile(SetExpression.Where(condition)));

            // Narrowed rather than replaced: the node's contract is "keep the ones that match", and
            // a host search always answers about the whole model.
            var matched = new HashSet<ElementKey>(found.Keys);
            return within.Where(matched.Contains).ToList();
        }

        /// <inheritdoc />
        public void Colour(IReadOnlyList<ElementKey> keys, Colour colour) => _session.View.SetColour(keys, colour);

        /// <inheritdoc />
        public void Transparency(IReadOnlyList<ElementKey> keys, double value) =>
            _session.View.SetTransparency(keys, value);

        /// <inheritdoc />
        public void Visible(IReadOnlyList<ElementKey> keys, bool visible) => _session.View.SetVisible(keys, visible);

        /// <inheritdoc />
        public void Select(IReadOnlyList<ElementKey> keys) => _session.Model.Select(keys);

        /// <inheritdoc />
        public int Write(IReadOnlyList<ElementKey> keys, string property, string? value)
        {
            if (string.IsNullOrWhiteSpace(property)) return 0;

            using (var write = new NavWriteTransaction(_session.Model, "CamelWorks: " + property))
            {
                foreach (var key in keys) write.SetProperty(key, NavWriteTransaction.TabName, property, value);

                return write.Commit();
            }
        }

        /// <inheritdoc />
        public TableBlock Takeoff(IReadOnlyList<ElementKey> keys, string measure, string group)
        {
            var lines = new List<TakeoffLine>();

            foreach (var key in keys)
            {
                var item = _session.Model.Resolve(key);
                if (item == null) continue;

                lines.Add(new TakeoffLine(item.DisplayName, GroupOf(item, group),
                    measure == "Count" ? "1" : Measure(item, measure)));
            }

            var result = Core.Data.Takeoff.Sum(lines);
            var table = new TableBlock("Group", "Count", "Total", "Unreadable");

            foreach (var line in result.Groups.OrderByDescending(g => g.Count))
                table.Row(line.Name,
                    line.Count.ToString(CultureInfo.InvariantCulture),
                    line.Total?.ToString() ?? (line.MixedKinds ? "(mixed units)" : string.Empty),
                    line.Unreadable > 0 ? line.Unreadable.ToString(CultureInfo.InvariantCulture) : string.Empty);

            return table;
        }

        /// <inheritdoc />
        public TableBlock ClashBoard()
        {
            var notes = new List<string>();
            var board = CoordinateViews.Build(_session, Job.Silent(), notes);

            if (board == null)
            {
                // An empty table rather than an exception: a graph whose clash node found nothing
                // should write an empty report and say so, not stop the whole run.
                var empty = new TableBlock("Group", "Clashes");
                return empty;
            }

            var table = new TableBlock("Group", "Clashes", "Assigned to", "Priority", "Level", "Models");

            foreach (var group in board.Groups)
                table.Row(group.Name,
                    group.Items.Count.ToString(CultureInfo.InvariantCulture),
                    group.AssignedTo ?? "unassigned",
                    group.Priority ?? string.Empty,
                    group.Items.FirstOrDefault()?.Level ?? string.Empty,
                    group.Items.FirstOrDefault()?.ModelPair ?? string.Empty);

            return table;
        }

        /// <inheritdoc />
        public void Report(string path, string format, string title, TableBlock table)
        {
            var document = new ReportDocument(string.IsNullOrWhiteSpace(title) ? "CamelWorks report" : title)
            {
                Subtitle = _session.Profile.ProjectName,
            };

            document.Fact("Project", _session.Profile.ProjectName);
            document.Fact("Author", _session.Profile.Author);
            document.Fact("Date", DateTime.Now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
            document.Fact("Rows", table.Rows.Count.ToString("N0", CultureInfo.InvariantCulture));
            document.Add(table);

            switch (format)
            {
                case "XLSX":
                    File.WriteAllBytes(path, XlsxWriter.Write("Report", table));
                    break;

                case "HTML":
                    File.WriteAllText(path, HtmlReportWriter.Write(document));
                    break;

                default:
                {
                    using (var stream = File.Create(path)) PdfReportWriter.Write(stream, document);
                    break;
                }
            }
        }

        /// <inheritdoc />
        public void Csv(string path, TableBlock table)
        {
            var text = new StringBuilder();

            text.AppendLine(string.Join(",", table.Headers.Select(Escape)));

            foreach (var row in table.Rows)
                text.AppendLine(string.Join(",", row.Select(cell => Escape(cell ?? string.Empty))));

            // UTF-8 with a byte order mark, because the one thing every CSV consumer on Windows
            // does badly is guess the encoding, and Excel guesses ANSI without it.
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        }

        private static string Escape(string value) =>
            value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
                ? value
                : "\"" + value.Replace("\"", "\"\"") + "\"";

        private static string? Measure(IModelItem item, string measure) =>
            item.Property("Element", measure)
            ?? item.Property("Item", measure)
            ?? item.Property("Revit Material Takeoff", measure)
            ?? item.Property(NavWriteTransaction.TabName, measure);

        private static string GroupOf(IModelItem item, string group) => group switch
        {
            "Type" => item.TypeName ?? "(no type)",
            "Model" => item.Model.DisplayName,
            "Level" => item.Property(NavWriteTransaction.TabName, "Level")
                       ?? item.Property("Element", "Level")
                       ?? item.Property("Item", "Level")
                       ?? "(no level)",
            _ => item.Category ?? "(no category)",
        };
    }
}
