using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Data;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Project;
using CamelWorks.Nav;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// The Data workspace: read what the models carry, write what they should, and get numbers out
    /// of both.
    /// </summary>
    public static class DataViews
    {
        private const string WholeModel = "Everything loaded";
        private const string Selection = "Current selection";

        // -----------------------------------------------------------------------------------
        // Data manager
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Browse and edit properties in one place.
        ///
        /// The scope defaults to the current selection rather than the whole federation, because
        /// the whole federation is the answer nobody wants first and the one that takes longest to
        /// produce.
        /// </summary>
        public static UIElement Manager()
        {
            var scope = Selection;
            var results = Ui.Stack();
            var editor = Ui.Stack();

            var picker = Ui.Choice(new[] { Selection, WholeModel }, scope, pick => scope = pick);

            var read = Ui.Runner("Read properties", results, job =>
            {
                var session = Host.Current;
                if (session == null) { results.Children.Add(Ui.Line(Host.NoModel)); return; }

                var keys = new List<ElementKey>();
                var values = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var blanks = new Dictionary<string, int>(StringComparer.Ordinal);

                var traversal = scope == Selection ? TraversalScope.CurrentSelection : TraversalScope.WholeDocument;

                foreach (var item in session.Model.Traverse(traversal))
                {
                    if (!job.Step(keys.Count, "elements read")) return;
                    keys.Add(item.Key);

                    foreach (var property in item.Properties())
                    {
                        var name = property.Category + " / " + property.Name;

                        if (string.IsNullOrEmpty(property.Value))
                        {
                            blanks[name] = blanks.TryGetValue(name, out var n) ? n + 1 : 1;
                            continue;
                        }

                        if (!values.TryGetValue(name, out var set))
                            values[name] = set = new HashSet<string>(StringComparer.Ordinal);

                        // Only the first few distinct values are ever shown, and a property with
                        // one value per element would otherwise hold the whole federation in memory.
                        if (set.Count < 6) set.Add(property.Value!);
                    }
                }

                if (keys.Count == 0)
                {
                    results.Children.Add(Ui.Empty(
                        scope == Selection ? "Nothing is selected." : "Nothing is loaded.",
                        scope == Selection
                            ? "Select elements in the model, or switch the scope to everything loaded."
                            : "Append a model first."));
                    return;
                }

                var rows = values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p =>
                    new TableRow(p.Key,
                        p.Key,
                        p.Value.Count == 1 ? p.Value.First() : "(" + p.Value.Count + (p.Value.Count > 5 ? "+" : string.Empty) + " values)",
                        blanks.TryGetValue(p.Key, out var blank) && blank > 0
                            ? blank.ToString(CultureInfo.InvariantCulture) + " blank"
                            : string.Empty)).ToList();

                results.Children.Add(Ui.Line(Ui.Count(keys.Count, "element") + ", "
                    + Ui.Count(rows.Count, "property"), 1, true));

                results.Children.Add(Ui.Table(rows, ("Property", 260), ("Value", 200), ("", 0)));

                editor.Children.Clear();
                editor.Children.Add(Writer(session, keys));
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Data"),
                Ui.Sub("Every property the elements carry, folded to one row each, with what varies said "
                       + "plainly rather than shown as the first element's value."),
                Ui.Field("Scope", picker),
                read,
                results,
                editor));
        }

        /// <summary>
        /// The write half, built only once something has been read — because the set of elements it
        /// writes to is exactly the set that was read, and offering to write before then would mean
        /// guessing at what.
        /// </summary>
        private static UIElement Writer(Session session, IReadOnlyList<ElementKey> keys)
        {
            var name = Ui.Text("Zone", null, 150);
            var value = Ui.Text(string.Empty, null, 200);
            var status = Ui.Stack();

            var apply = Ui.Runner("Write to " + Ui.Count(keys.Count, "element"), status, job =>
            {
                if (name.Text.Trim().Length == 0)
                {
                    status.Children.Add(Ui.Line("Give the property a name first.", 0.8));
                    return;
                }

                using (var write = new NavWriteTransaction(session.Model, "CamelWorks: set " + name.Text.Trim()))
                {
                    foreach (var key in keys)
                        write.SetProperty(key, NavWriteTransaction.TabName, name.Text.Trim(), value.Text);

                    var preview = write.Preview();

                    if (preview.IsEmpty)
                    {
                        status.Children.Add(Ui.Empty("Nothing to write.", "None of those elements still resolve."));
                        return;
                    }

                    job.Say("Writing...");
                    var written = write.Commit();

                    status.Children.Add(Ui.Line("Wrote " + Ui.Count(written, "element") + ".", 1, true));

                    if (preview.Unresolved.Count > 0)
                        status.Children.Add(Ui.Line(Ui.Count(preview.Unresolved.Count, "element")
                            + " could not be found and were skipped.", 0.7));

                    session.Record(ActivityKind.Write,
                        "set " + name.Text.Trim() + " on " + Ui.Count(written, "element"));
                }
            });

            return Ui.Card("Write a property",
                "Goes into a tab of its own called \"" + NavWriteTransaction.TabName
                + "\", so it is never mixed with the authoring tool's data.",
                Ui.Field("Name", name),
                Ui.Field("Value", value),
                apply,
                Ui.Note("Ctrl+Z will not undo this. Navisworks does not put property writes on its undo "
                        + "stack, so CamelWorks records what it changed in the project file instead of "
                        + "pretending otherwise."),
                status);
        }

        // -----------------------------------------------------------------------------------
        // Levels and zones
        // -----------------------------------------------------------------------------------

        /// <summary>Derive levels from whatever the model has, and optionally stamp them on.</summary>
        public static UIElement Zones()
        {
            var results = Ui.Stack();
            var after = Ui.Stack();

            var run = Ui.Runner("Derive levels", results, job =>
            {
                var session = Host.Current;
                if (session == null) { results.Children.Add(Ui.Line(Host.NoModel)); return; }

                var scale = session.MetresPerUnit;

                var named = new List<KeyValuePair<string, double>>();
                var elevations = new List<double>();
                var items = new List<(ElementKey Key, double Base, double Top)>();

                foreach (var item in session.Model.Traverse(TraversalScope.WholeDocument))
                {
                    if (!job.Step(items.Count, "elements read")) return;
                    if (!item.HasGeometry) continue;

                    var bounds = item.Bounds;
                    items.Add((item.Key, bounds.MinZ * scale, bounds.MaxZ * scale));
                    elevations.Add(bounds.MinZ * scale);

                    var level = item.Property("Element", "Level") ?? item.Property("Item", "Level");

                    if (level != null && named.Count < 4000)
                        named.Add(new KeyValuePair<string, double>(level, bounds.MinZ * scale));
                }

                job.Say("Working out levels...");

                var fromModel = LevelSet.FromModel(named);
                var levels = fromModel.Bands.Count > 0 ? fromModel : LevelSet.Derive(elevations);

                results.Children.Add(Ui.Line(levels.ToString(), 1, true));

                results.Children.Add(Ui.Line(levels.IsFromModel
                    ? "Read from the models' own Level property."
                    : "The models carry no usable level property, so these were inferred from a histogram "
                      + "of element base heights.", 0.75));

                if (levels.Bands.Count == 0)
                {
                    results.Children.Add(Ui.Empty("No levels.",
                        "Nothing loaded carries geometry to infer them from."));
                    return;
                }

                var rows = levels.Bands.Select(b => new TableRow(b,
                    b.Name,
                    b.Elevation.ToString("0.000", CultureInfo.InvariantCulture) + " m",
                    b.Support > 0 ? Ui.Count(b.Support, "element") : string.Empty,
                    b.IsDerived ? "inferred" : "from the model")).ToList();

                results.Children.Add(Ui.Table(rows,
                    ("Level", 140), ("Elevation", 100), ("Elements", 110), ("Source", 110)));

                after.Children.Clear();
                after.Children.Add(Stamp(session, levels, items));
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Levels & zones"),
                Ui.Sub("Uses the models' own levels where they have them and a height histogram where they "
                       + "do not — and says which, because a level called \"Level +3.600\" is honest about "
                       + "being a guess in a way that \"Level 2\" is not."),
                run,
                results,
                after));
        }

        private static UIElement Stamp(Session session, LevelSet levels,
                                       IReadOnlyList<(ElementKey Key, double Base, double Top)> items)
        {
            var status = Ui.Stack();

            var apply = Ui.Runner("Stamp the level onto every element", status, job =>
            {
                using (var write = new NavWriteTransaction(session.Model, "CamelWorks: assign levels"))
                {
                    var placed = 0;

                    foreach (var item in items)
                    {
                        if (!job.Step(placed, "elements placed")) return;

                        var level = levels.LevelOf(item.Base, item.Top);
                        if (level == null) continue;

                        write.SetProperty(item.Key, NavWriteTransaction.TabName, "Level", level);
                        placed++;
                    }

                    if (placed == 0)
                    {
                        status.Children.Add(Ui.Empty("Nothing to stamp.",
                            "No element sits inside any of the derived bands."));
                        return;
                    }

                    job.Say("Writing...");
                    var written = write.Commit();

                    status.Children.Add(Ui.Line("Stamped " + Ui.Count(written, "element") + ".", 1, true));
                    session.Record(ActivityKind.Write, "stamped levels onto " + Ui.Count(written, "element"));
                }
            });

            return Ui.Card("Put the level on the elements",
                "Writes Level into the CamelWorks tab, so clash grouping, reports and set rules can all "
                + "use it — including on models that never had a level property to begin with.",
                apply,
                status);
        }

        // -----------------------------------------------------------------------------------
        // Takeoff
        // -----------------------------------------------------------------------------------

        /// <summary>Sum the numeric properties the model carries, grouped, reporting what it could not read.</summary>
        public static UIElement Takeoff()
        {
            var results = Ui.Stack();

            var measure = "Volume";
            var group = "Category";

            var measurePicker = Ui.Choice(new[] { "Volume", "Area", "Length", "Count" }, measure, p => measure = p);
            var groupPicker = Ui.Choice(new[] { "Category", "Type", "Model", "Level" }, group, p => group = p);

            var run = Ui.Runner("Sum this model", results, job =>
            {
                var session = Host.Current;
                if (session == null) { results.Children.Add(Ui.Line(Host.NoModel)); return; }

                var lines = new List<TakeoffLine>();

                foreach (var item in session.Model.Traverse(TraversalScope.WholeDocument))
                {
                    if (!job.Step(lines.Count, "elements read")) return;

                    var value = measure == "Count" ? "1" : Measure(item, measure);

                    lines.Add(new TakeoffLine(item.DisplayName, GroupOf(item, group), value));
                }

                job.Say("Adding up...");
                var result = Core.Data.Takeoff.Sum(lines);

                results.Children.Add(Ui.Line(result.ToString(), 1, true));

                if (result.Groups.Count == 0)
                {
                    results.Children.Add(Ui.Empty("Nothing to measure.", "Nothing loaded carries that property."));
                    return;
                }

                var rows = result.Groups.OrderByDescending(g => g.Count).Select(g => new TableRow(g,
                    g.Name,
                    g.Count.ToString("N0", CultureInfo.InvariantCulture),
                    g.Total?.ToString() ?? (g.MixedKinds ? "(mixed units)" : "-"),
                    g.Unreadable > 0 ? g.Unreadable.ToString(CultureInfo.InvariantCulture) + " unreadable" : string.Empty)
                {
                    Tone = g.IsComplete ? Tone.Plain : Tone.Warn,
                }).ToList();

                results.Children.Add(Ui.Table(rows,
                    ("Group", 190), ("Count", 80), ("Total", 130), ("", 0)));

                if (!result.IsComplete)
                    results.Children.Add(Ui.Note("Some values could not be read as quantities. They are counted "
                        + "and named rather than dropped, because a total that quietly excludes what it could "
                        + "not parse is worse than no total."));

                session.Record(ActivityKind.Report, "took off " + measure.ToLowerInvariant() + " by "
                                                    + group.ToLowerInvariant() + ": " + result);
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Takeoff"),
                Ui.Sub("Sums whatever the elements carry, with no mapping table to build first."),
                Ui.Field("Measure", measurePicker),
                Ui.Field("Group by", groupPicker),
                run,
                results));
        }

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
