using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Clash;
using CamelWorks.Core.Data;
using CamelWorks.Core.Findings;
using CamelWorks.Core.Identity;
using CamelWorks.Core.OpenBim;
using CamelWorks.Core.Project;
using CamelWorks.Core.Report;
using CamelWorks.Core.Store;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// The Coordinate workspace: the board, the tests behind it, the rules that shape it, and the
    /// two things that leave the building — a report and a BCF file.
    /// </summary>
    public static class CoordinateViews
    {
        /// <summary>
        /// The last board that was built, so Report and BCF work on what the user is looking at.
        ///
        /// Held rather than rebuilt because rebuilding would silently disagree: the report would be
        /// of a board with different grouping from the one on screen if a rule had been edited in
        /// between, and nothing would say so.
        /// </summary>
        internal static ClashPipelineResult? LastBoard { get; private set; }

        /// <summary>What the last board was of, for a report's cover.</summary>
        internal static string? LastBoardLabel { get; private set; }

        // ===================================================================================
        // Building the board
        // ===================================================================================

        /// <summary>
        /// Read the clash engine and run the rules over it.
        ///
        /// Every coordinate is scaled to metres here, once, because every threshold above this line
        /// — a grouping distance, a tolerance, a proximity band — is stated in metres, and a
        /// millimetre model would otherwise group everything within five millimetres and report one
        /// group per clash.
        /// </summary>
        internal static ClashPipelineResult? Build(Session session, Job job, ICollection<string> notes)
        {
            if (!session.Clash.IsAvailable)
            {
                notes.Add(session.ClashProblem ?? "There is no clash engine in this Navisworks edition.");
                return null;
            }

            var scale = session.MetresPerUnit;
            var items = new List<ClashItem>();
            var levels = Levels(session, job, scale);

            foreach (var test in session.Clash.Tests())
            {
                if (!job.Step(items.Count, "results read")) return null;

                foreach (var result in session.Clash.Results(test.Id))
                {
                    var item = new ClashItem(result.Key(), test.Name)
                    {
                        TestFolder = test.Folder,
                        X = result.PointX * scale,
                        Y = result.PointY * scale,
                        Z = result.PointZ * scale,
                        ModelA = ModelOf(session, result.A),
                        ModelB = ModelOf(session, result.B),
                        DisciplineA = session.Profile.DisciplineOf(result.A.ModelScope),
                        DisciplineB = session.Profile.DisciplineOf(result.B.ModelScope),
                        CategoryA = CategoryOf(session, result.A),
                        CategoryB = CategoryOf(session, result.B),
                        Level = levels.LevelAt(result.PointZ * scale),
                    };

                    if (result.HostAssignedTo != null) item.Properties["Host/AssignedTo"] = result.HostAssignedTo;
                    if (result.HostStatus != null) item.Properties["Host/Status"] = result.HostStatus;

                    items.Add(item);
                }
            }

            if (items.Count == 0)
            {
                notes.Add("The clash engine has tests but no results. Run them in Clash Detective first.");
                return null;
            }

            job.Say("Carrying over the last run...");

            var rules = ClashRuleSet.FromJson(session.Store.Section(ProjectStore.ClashSection)["rules"]);
            var snapshot = Snapshot(session);
            var delta = ClashCarryOver.Apply(items, snapshot);

            job.Say("Applying rules...");

            var options = rules.ToOptions(session.Profile.ClashProximity);
            options.Decisions = snapshot?.Decisions ?? options.Decisions;

            var board = ClashPipeline.Run(items, options);

            notes.Add(delta.ToString());
            if (rules.IsDefault) notes.Add("Nothing is configured, so the built-in rules ran: " + board.Funnel);

            LastBoard = board;
            LastBoardLabel = session.Profile.ProjectName;

            Save(session, board);
            return board;
        }

        private static LevelSet Levels(Session session, Job job, double scale)
        {
            job.Say("Working out levels...");

            var elevations = new List<double>();

            foreach (var item in session.Model.Traverse(TraversalScope.WholeDocument))
            {
                if (!item.HasGeometry) continue;
                elevations.Add(item.Bounds.MinZ * scale);

                if (elevations.Count % 5000 == 0 && !job.Step(elevations.Count, "elements measured"))
                    return LevelSet.Empty;
            }

            return LevelSet.Derive(elevations);
        }

        private static string? ModelOf(Session session, ElementKey key) =>
            session.Model.Models.FirstOrDefault(
                m => string.Equals(m.Scope, key.ModelScope, StringComparison.Ordinal))?.DisplayName;

        private static string? CategoryOf(Session session, ElementKey key) =>
            session.Model.Resolve(key)?.Category;

        private static ClashSnapshot? Snapshot(Session session)
        {
            var saved = session.Store.Section(ProjectStore.ClashSection)["snapshot"];
            if (saved.Kind != JsonKind.Array) return null;

            var records = new List<ClashRecord>();

            foreach (var entry in saved.Items)
            {
                if (!ClashKey.TryParse(entry["key"].AsString(), out var key)) continue;

                var status = Enum.TryParse<FindingStatus>(entry["status"].AsString(), out var parsed)
                    ? parsed
                    : FindingStatus.New;

                records.Add(new ClashRecord(key, status, entry["group"].AsString(),
                    entry["pinned"].AsString() == "yes", entry["test"].AsString()));
            }

            var decisions = new Dictionary<string, GroupDecision>(StringComparer.Ordinal);
            var saveDecisions = session.Store.Section(ProjectStore.ClashSection)["decisions"];

            foreach (var name in saveDecisions.Keys)
                decisions[name] = new GroupDecision(saveDecisions[name]["party"].AsString(),
                                                    saveDecisions[name]["priority"].AsString());

            return new ClashSnapshot(records, decisions);
        }

        private static void Save(Session session, ClashPipelineResult board)
        {
            var snapshot = ClashSnapshot.Of(board);

            var array = JsonValue.Array(snapshot.Records.Select(r =>
            {
                var json = JsonValue.Object()
                    .Set("key", JsonValue.String(r.Key.ToString()))
                    .Set("status", JsonValue.String(r.Status.ToString()));

                if (r.GroupName != null) json.Set("group", JsonValue.String(r.GroupName));
                if (r.GroupWasPinned) json.Set("pinned", JsonValue.String("yes"));
                if (r.TestName != null) json.Set("test", JsonValue.String(r.TestName));
                return json;
            }));

            session.Store.Section(ProjectStore.ClashSection).Set("snapshot", array);
            session.Record(ActivityKind.Regroup, "ran the rules: " + board.Funnel);
        }

        // ===================================================================================
        // Triage
        // ===================================================================================

        /// <summary>The board.</summary>
        public static UIElement Triage()
        {
            var results = Ui.Stack();
            var detail = Ui.Stack();

            var run = Ui.Runner("Load the board", results, job =>
            {
                var session = Host.Current;
                if (session == null) { results.Children.Add(Ui.Line(Host.NoModel)); return; }

                var notes = new List<string>();
                var board = Build(session, job, notes);

                foreach (var note in notes) results.Children.Add(Ui.Note(note));

                if (board == null) return;

                results.Children.Add(Ui.Across(
                    Ui.Figure(board.Funnel.Input.ToString("N0", CultureInfo.InvariantCulture), "from the engine"),
                    Ui.Figure(board.Funnel.Groups.ToString("N0", CultureInfo.InvariantCulture), "groups"),
                    Ui.Figure(board.Funnel.Unassigned.ToString("N0", CultureInfo.InvariantCulture), "unassigned",
                        board.Funnel.Unassigned > 0 ? Tone.Warn : Tone.Good),
                    Ui.Figure(board.Funnel.Suppressed.ToString("N0", CultureInfo.InvariantCulture), "suppressed")));

                results.Children.Add(Ui.Line(board.Funnel.ToString(), 0.8));

                if (board.Funnel.RulesApplied.Count > 0)
                    results.Children.Add(Ui.Line("Rules: " + string.Join(", ", board.Funnel.RulesApplied), 0.6));

                if (board.Groups.Count == 0)
                {
                    results.Children.Add(Ui.Empty("Nothing on the board.",
                        "Every result was suppressed by a rule. The funnel above says by which."));
                    return;
                }

                var rows = board.Groups.Select(g => new TableRow(g,
                    g.Name,
                    g.Items.Count.ToString("N0", CultureInfo.InvariantCulture),
                    g.AssignedTo ?? "-",
                    g.Priority ?? string.Empty,
                    g.IsPinned ? "pinned" : string.Empty)
                {
                    Tone = g.AssignedTo == null ? Tone.Warn : Tone.Plain,
                }).ToList();

                results.Children.Add(Ui.Table(rows,
                    ("Group", 220), ("Clashes", 80), ("Assigned to", 120), ("Priority", 90), ("", 0))
                    .OnPick(item =>
                    {
                        detail.Children.Clear();
                        if (item is ClashGroup group) detail.Children.Add(Detail(session, group));
                    }));
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Triage"),
                Ui.Sub("The engine's results, grouped into work. The funnel says how a count of several "
                       + "thousand became this one — every row removed is accounted for rather than "
                       + "quietly dropped."),
                run,
                results,
                detail));
        }

        private static UIElement Detail(Session session, ClashGroup group)
        {
            var status = Ui.Stack();

            var rows = group.Items.Take(300).Select(item => new TableRow(item,
                item.TestName,
                item.Level ?? "-",
                item.ModelPair,
                (item.CategoryA ?? "?") + " v " + (item.CategoryB ?? "?"),
                item.Status.ToString())).ToList();

            var table = Ui.Table(rows,
                ("Test", 130), ("Level", 90), ("Models", 170), ("What", 170), ("Status", 90))
                .OnPick(item =>
                {
                    if (!(item is ClashItem clash)) return;

                    var keys = new[] { clash.Key.A, clash.Key.B };
                    session.Model.Select(keys);
                    session.View.ZoomTo(keys, 2.0);
                });

            var parties = session.Profile.Parties.ToList();
            if (parties.Count == 0) parties.Add("Unassigned");

            var party = parties[0];
            var picker = Ui.Choice(parties, party, p => party = p);

            var assign = Ui.Button("Assign this group", () =>
            {
                status.Children.Clear();
                group.AssignByHand(party);

                var section = session.Store.Section(ProjectStore.ClashSection);
                var decisions = section["decisions"];

                if (decisions.Kind != JsonKind.Object)
                {
                    decisions = JsonValue.Object();
                    section.Set("decisions", decisions);
                }

                decisions.Set(group.Name, JsonValue.Object().Set("party", JsonValue.String(party)));

                status.Children.Add(session.Store.Save()
                    ? Ui.Line("\"" + group.Name + "\" goes to " + party
                              + ". A hand assignment outranks the rules and survives the next run.", 0.8)
                    : Ui.Problem(session.Store.LastSaveProblem ?? "Could not write the project file."));
            });

            return Ui.Card(group.Name,
                Ui.Count(group.Items.Count, "clash") + (group.Items.Count > 300 ? ", first 300 shown" : string.Empty),
                Ui.Field("Assign to", picker),
                Ui.Across(assign),
                status,
                table);
        }

        // ===================================================================================
        // Tests
        // ===================================================================================

        /// <summary>What the clash engine holds, and when each test last ran.</summary>
        public static UIElement Tests()
        {
            var session = Host.Current;

            if (session == null)
                return Ui.Scroll(Ui.Stack(Ui.Heading("Tests"), Ui.Sub(Host.NoModel)));

            if (!session.Clash.IsAvailable)
                return Ui.Scroll(Ui.Stack(
                    Ui.Heading("Tests"),
                    Ui.Note(session.ClashProblem ?? "There is no clash engine in this Navisworks edition.")));

            var tests = session.Clash.Tests();

            if (tests.Count == 0)
                return Ui.Scroll(Ui.Stack(
                    Ui.Heading("Tests"),
                    Ui.Empty("No clash tests in this document.",
                        "Set them up in Clash Detective. CamelWorks reads the results and turns them into "
                        + "work; it does not replace the engine that produces them.")));

            var rows = tests.Select(t => new TableRow(t,
                t.Name,
                t.Folder ?? string.Empty,
                t.ResultCount.ToString("N0", CultureInfo.InvariantCulture),
                t.LastRunTicks > 0
                    ? new DateTime(t.LastRunTicks).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)
                    : "never run")
            {
                Tone = t.LastRunTicks > 0 ? Tone.Plain : Tone.Warn,
            }).ToList();

            var stale = tests.Count(t => t.LastRunTicks <= 0);

            var body = Ui.Stack(
                Ui.Across(
                    Ui.Pill(Ui.Count(tests.Count, "test")),
                    Ui.Pill(Ui.Count(tests.Sum(t => t.ResultCount), "result")),
                    stale > 0 ? Ui.Pill(stale + " never run", Tone.Warn) : null),
                Ui.Table(rows, ("Test", 200), ("Folder", 120), ("Results", 90), ("Last run", 150)));

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Tests"),
                Ui.Sub("The clash engine's own tests. A test that has never run contributes nothing to the "
                       + "board, which is worth seeing before wondering where the clashes went."),
                body));
        }

        // ===================================================================================
        // Rules
        // ===================================================================================

        /// <summary>Suppress and flag, then group, then assign — one pipeline, saved with the project.</summary>
        public static UIElement Rules() => new RulesScreen().Build();

        private sealed class RulesScreen
        {
            private ClashRuleSet _rules = new ClashRuleSet();
            private readonly StackPanel _filters = Ui.Stack();
            private readonly StackPanel _grouping = Ui.Stack();
            private readonly StackPanel _assigns = Ui.Stack();
            private readonly StackPanel _status = Ui.Stack();

            internal UIElement Build()
            {
                var session = Host.Current;

                if (session == null)
                    return Ui.Scroll(Ui.Stack(Ui.Heading("Rules"), Ui.Sub(Host.NoModel)));

                _rules = ClashRuleSet.FromJson(session.Store.Section(ProjectStore.ClashSection)["rules"]);

                Redraw(session);

                var save = Ui.Button("Save rules", () =>
                {
                    _status.Children.Clear();

                    session.Store.Section(ProjectStore.ClashSection).Set("rules", _rules.ToJson());

                    _status.Children.Add(session.Store.Save()
                        ? Ui.Line("Saved. The board uses these the next time it loads.", 0.8)
                        : Ui.Problem(session.Store.LastSaveProblem ?? "Could not write the project file."));
                });

                return Ui.Scroll(Ui.Stack(
                    Ui.Heading("Rules"),
                    Ui.Sub("Suppress and flag first, then group, then assign. The order is fixed because it is "
                           + "the only one that makes sense: assigning a group is only meaningful once the "
                           + "group exists, and grouping noise wastes the grouping."),
                    Ui.Note("With nothing here, the board still works: it groups by model pair, then level, "
                            + "then proximity, and assigns nothing. These rules are how you narrow that, not "
                            + "how you switch it on."),
                    Ui.Card("Suppress and flag", "Applied to every result before anything else.", _filters),
                    Ui.Card("Group", "Outermost rule first. Empty means the built-in stack.", _grouping),
                    Ui.Card("Assign", "First match wins.", _assigns),
                    Ui.Across(save),
                    _status));
            }

            private void Redraw(Session session)
            {
                _filters.Children.Clear();
                _grouping.Children.Clear();
                _assigns.Children.Clear();

                foreach (var filter in _rules.Filters.ToList())
                {
                    var row = new WrapPanel();
                    row.Children.Add(Ui.Check(string.Empty, filter.IsEnabled, v => filter.IsEnabled = v));
                    row.Children.Add(Ui.Pill(filter.Suppress ? "suppress" : "flag",
                        filter.Suppress ? Tone.Warn : Tone.Plain));
                    row.Children.Add(Ui.Line(filter.Name, 1, true));
                    row.Children.Add(Ui.Line(filter.When.Describe(), 0.75));

                    var drop = Ui.Button("remove", () => { _rules.Filters.Remove(filter); Redraw(session); });
                    drop.MinWidth = 60;
                    row.Children.Add(drop);

                    Space(row);
                    _filters.Children.Add(row);
                }

                _filters.Children.Add(NewFilter(session));

                foreach (var group in _rules.Grouping.ToList())
                {
                    var row = new WrapPanel();
                    row.Children.Add(Ui.Line(group.Describe(), 1));

                    var drop = Ui.Button("remove", () => { _rules.Grouping.Remove(group); Redraw(session); });
                    drop.MinWidth = 60;
                    row.Children.Add(drop);

                    Space(row);
                    _grouping.Children.Add(row);
                }

                _grouping.Children.Add(NewGrouping(session));

                foreach (var assign in _rules.Assigns.ToList())
                {
                    var row = new WrapPanel();
                    row.Children.Add(Ui.Check(string.Empty, assign.IsEnabled, v => assign.IsEnabled = v));
                    row.Children.Add(Ui.Line(assign.Party, 1, true));
                    row.Children.Add(Ui.Line("gets " + assign.When.Describe(), 0.75));

                    var drop = Ui.Button("remove", () => { _rules.Assigns.Remove(assign); Redraw(session); });
                    drop.MinWidth = 60;
                    row.Children.Add(drop);

                    Space(row);
                    _assigns.Children.Add(row);
                }

                _assigns.Children.Add(NewAssign(session));
            }

            private static void Space(Panel row)
            {
                foreach (var child in row.Children.OfType<FrameworkElement>())
                    child.Margin = new Thickness(0, 2, 7, 2);
            }

            private UIElement NewFilter(Session session)
            {
                var name = Ui.Text("New rule", null, 140);
                var suppress = Ui.Choice(new[] { "suppress", "flag" }, "suppress", null, 100);
                var editor = new SpecEditor(PredicateSpec.Kinds);

                var add = Ui.Button("Add", () =>
                {
                    var spec = editor.Spec();
                    if (spec == null) return;

                    _rules.Filters.Add(new FilterSpec(
                        name.Text.Trim().Length == 0 ? spec.Describe() : name.Text.Trim(),
                        spec,
                        (suppress.SelectedItem as string) != "flag"));

                    Redraw(session);
                });

                return Ui.Stack(Ui.Across(name, suppress), editor.View, Ui.Across(add));
            }

            private UIElement NewGrouping(Session session)
            {
                var editor = new SpecEditor(GroupingSpec.Kinds);

                var add = Ui.Button("Add", () =>
                {
                    var kind = editor.Kind();
                    if (kind == null) return;

                    _rules.Grouping.Add(new GroupingSpec(kind.Id)
                    {
                        A = editor.Number(),
                        Name = editor.Name(),
                    });

                    Redraw(session);
                });

                return Ui.Stack(editor.View, Ui.Across(add));
            }

            private UIElement NewAssign(Session session)
            {
                var parties = session.Profile.Parties.ToList();
                if (parties.Count == 0) parties.Add("Unassigned");

                var party = parties[0];
                var picker = Ui.Choice(parties, party, p => party = p);
                var editor = new SpecEditor(PredicateSpec.Kinds);

                var add = Ui.Button("Add", () =>
                {
                    var spec = editor.Spec();
                    if (spec == null) return;

                    _rules.Assigns.Add(new AssignSpec(spec, party));
                    Redraw(session);
                });

                return Ui.Stack(Ui.Across(picker), editor.View, Ui.Across(add));
            }
        }

        /// <summary>
        /// One editor that can draw every rule, because every rule says what it needs.
        ///
        /// Without this each of the thirteen predicates and ten grouping rules would need its own
        /// row of controls, and adding a rule to Core would mean remembering to add one here.
        /// </summary>
        private sealed class SpecEditor
        {
            private readonly ComboBox _kind;
            private readonly TextBox _first = Ui.Text(string.Empty, null, 110);
            private readonly TextBox _second = Ui.Text(string.Empty, null, 110);
            private readonly TextBlock _hint = Ui.Line(string.Empty, 0.6);
            private readonly IReadOnlyList<SpecKind> _kinds;

            internal SpecEditor(IReadOnlyList<SpecKind> kinds)
            {
                _kinds = kinds;

                _kind = Ui.Choice(kinds.Select(k => k.Title), kinds[0].Title, _ => Update(), 190);
                Update();

                View = Ui.Across(_kind, _first, _second, _hint);
            }

            internal UIElement View { get; }

            internal SpecKind? Kind()
            {
                var title = _kind.SelectedItem as string;
                return _kinds.FirstOrDefault(k => string.Equals(k.Title, title, StringComparison.Ordinal));
            }

            internal double Number() =>
                double.TryParse(_first.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

            internal string? Name() => _first.Text.Trim().Length == 0 ? null : _first.Text.Trim();

            internal PredicateSpec? Spec()
            {
                var kind = Kind();
                if (kind == null) return null;

                var spec = new PredicateSpec(kind.Id);

                switch (kind.Input)
                {
                    case SpecInput.Number:
                        spec.A = Number();
                        break;

                    case SpecInput.Range:
                        spec.A = Number();
                        spec.B = double.TryParse(_second.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                                                 out var b) ? b : 0;
                        break;

                    case SpecInput.Text:
                        spec.Name = Name();
                        break;

                    case SpecInput.NameAndValue:
                        spec.Name = Name();
                        spec.Value = _second.Text;
                        break;
                }

                return spec;
            }

            private void Update()
            {
                var kind = Kind();
                if (kind == null) return;

                _hint.Text = kind.Hint;
                _first.Visibility = kind.Input == SpecInput.None || kind.Input == SpecInput.Nested
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                _second.Visibility = kind.Input == SpecInput.Range || kind.Input == SpecInput.NameAndValue
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        // ===================================================================================
        // Report
        // ===================================================================================

        /// <summary>PDF, XLSX or HTML from the board, with nothing to configure first.</summary>
        public static UIElement Report()
        {
            var results = Ui.Stack();
            var path = Ui.Text(string.Empty, null, 340);
            var format = "PDF";
            var picker = Ui.Choice(new[] { "PDF", "XLSX", "HTML" }, format, f => format = f);

            var write = Ui.Runner("Write the report", results, job =>
            {
                var session = Host.Current;
                if (session == null) { results.Children.Add(Ui.Line(Host.NoModel)); return; }

                var board = LastBoard;

                if (board == null)
                {
                    var notes = new List<string>();
                    board = Build(session, job, notes);
                    foreach (var note in notes) results.Children.Add(Ui.Note(note));
                }

                if (board == null) return;

                var file = path.Text.Trim();

                if (file.Length == 0)
                {
                    file = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        session.Profile.ProjectName + " clash report." + format.ToLowerInvariant());

                    path.Text = file;
                }

                job.Say("Building the report...");
                var document = Document(session, board);

                job.Say("Writing...");

                switch (format)
                {
                    case "XLSX":
                        File.WriteAllBytes(file, XlsxWriter.Write("Clashes", Table(board)));
                        break;

                    case "HTML":
                        File.WriteAllText(file, HtmlReportWriter.Write(document));
                        break;

                    default:
                    {
                        using (var stream = File.Create(file)) PdfReportWriter.Write(stream, document);
                        break;
                    }
                }

                results.Children.Add(Ui.Line("Wrote " + file, 1, true));
                session.Record(ActivityKind.Report, "wrote a " + format + " clash report", file);
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Report"),
                Ui.Sub("Of the board as it stands. Nothing to lay out first — the template is built in, and "
                       + "the numbers on the cover are the funnel's, so the report and the screen agree."),
                Ui.Field("Format", picker),
                Ui.Field("Write to", path),
                write,
                results));
        }

        private static ReportDocument Document(Session session, ClashPipelineResult board)
        {
            var document = new ReportDocument(session.Profile.ProjectName + " — clash report")
            {
                Subtitle = "Produced by CamelWorks",
            };

            document.Fact("Project", session.Profile.ProjectName);
            document.Fact("Author", session.Profile.Author);
            document.Fact("Date", DateTime.Now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
            document.Fact("From the engine", board.Funnel.Input.ToString("N0", CultureInfo.InvariantCulture));
            document.Fact("On the board", board.Groups.Sum(g => g.Items.Count).ToString("N0", CultureInfo.InvariantCulture));
            document.Fact("Groups", board.Groups.Count.ToString("N0", CultureInfo.InvariantCulture));
            document.Fact("Unassigned", board.Funnel.Unassigned.ToString("N0", CultureInfo.InvariantCulture));

            document.Heading("How this list was produced");
            document.Paragraph(board.Funnel.ToString());

            document.Paragraph(
                "Every result the engine produced is accounted for above: what was removed, by which rule, "
                + "and what remains. A report that quietly showed a smaller number than the engine found "
                + "would be worse than no report.");

            document.Heading("Groups");
            document.Add(Summary(board));

            document.PageBreak();
            document.Heading("Every clash");
            document.Add(Table(board));

            return document;
        }

        private static TableBlock Summary(ClashPipelineResult board)
        {
            var table = new TableBlock("Group", "Clashes", "Assigned to", "Priority");

            foreach (var group in board.Groups)
                table.Row(group.Name,
                    group.Items.Count.ToString("N0", CultureInfo.InvariantCulture),
                    group.AssignedTo ?? "unassigned",
                    group.Priority ?? string.Empty);

            return table;
        }

        private static TableBlock Table(ClashPipelineResult board)
        {
            var table = new TableBlock("Group", "Test", "Level", "Models", "What", "Status", "X", "Y", "Z");

            foreach (var group in board.Groups)
                foreach (var item in group.Items)
                    table.Row(group.Name,
                        item.TestName,
                        item.Level ?? string.Empty,
                        item.ModelPair,
                        (item.CategoryA ?? "?") + " v " + (item.CategoryB ?? "?"),
                        item.Status.ToString(),
                        item.X.ToString("0.00", CultureInfo.InvariantCulture),
                        item.Y.ToString("0.00", CultureInfo.InvariantCulture),
                        item.Z.ToString("0.00", CultureInfo.InvariantCulture));

            return table;
        }

        // ===================================================================================
        // BCF
        // ===================================================================================

        /// <summary>Export the board as BCF, or read somebody else's back in.</summary>
        public static UIElement Bcf()
        {
            var results = Ui.Stack();
            var path = Ui.Text(string.Empty, null, 340);
            var version = "2.1";
            var picker = Ui.Choice(new[] { "2.1", "3.0" }, version, v => version = v);

            var export = Ui.Runner("Export the board", results, job =>
            {
                var session = Host.Current;
                if (session == null) { results.Children.Add(Ui.Line(Host.NoModel)); return; }

                var board = LastBoard;

                if (board == null)
                {
                    var notes = new List<string>();
                    board = Build(session, job, notes);
                    foreach (var note in notes) results.Children.Add(Ui.Note(note));
                }

                if (board == null) return;

                var file = path.Text.Trim();

                if (file.Length == 0)
                {
                    file = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        session.Profile.ProjectName + ".bcfzip");

                    path.Text = file;
                }

                job.Say("Building topics...");
                var topics = Topics(session, board);

                job.Say("Writing...");

                using (var stream = File.Create(file))
                {
                    var result = BcfWriter.Write(stream, topics,
                        version == "3.0" ? BcfVersion.V30 : BcfVersion.V21,
                        new BcfProject(session.Profile.ProjectName, session.Profile.ProjectName));

                    results.Children.Add(Ui.Line(result.ToString(), 1, true));

                    foreach (var note in result.Notes) results.Children.Add(Ui.Note(note));
                }

                results.Children.Add(Ui.Line("Wrote " + file, 0.8));
                session.Record(ActivityKind.Exchange, "exported BCF " + version, file);
            });

            var import = Ui.Runner("Read a BCF file", results, job =>
            {
                var session = Host.Current;
                if (session == null) { results.Children.Add(Ui.Line(Host.NoModel)); return; }

                var file = path.Text.Trim();

                if (!File.Exists(file))
                {
                    results.Children.Add(Ui.Problem("There is no file at " + file + "."));
                    return;
                }

                using (var stream = File.OpenRead(file))
                {
                    var read = BcfReader.Read(stream);

                    results.Children.Add(Ui.Line(read.ToString(), 1, true));

                    foreach (var warning in read.Warnings) results.Children.Add(Ui.Note(warning));

                    if (read.Topics.Count == 0)
                    {
                        results.Children.Add(Ui.Empty("No topics in that file.",
                            "It read as a BCF archive but carried nothing."));
                        return;
                    }

                    var rows = read.Topics.Select(t => new TableRow(t,
                        t.Title,
                        t.TopicStatus,
                        t.AssignedTo ?? "-",
                        t.CreationAuthor,
                        Ui.Count(t.Comments.Count, "comment"))).ToList();

                    results.Children.Add(Ui.Table(rows,
                        ("Topic", 240), ("Status", 90), ("Assigned to", 120), ("Author", 130), ("", 0)));
                }

                session.Record(ActivityKind.Exchange, "read a BCF file", file);
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("BCF"),
                Ui.Sub("One neutral model, both versions. 2.1 is what most tools still read; 3.0 keeps the "
                       + "camera's aspect ratio, which 2.1 has nowhere to put."),
                Ui.Field("Version", picker),
                Ui.Field("File", path),
                Ui.Across(export, import),
                results));
        }

        private static IReadOnlyList<BcfTopic> Topics(Session session, ClashPipelineResult board)
        {
            var topics = new List<BcfTopic>();
            var author = session.Profile.Author;
            var now = new DateTimeOffset(DateTime.UtcNow);

            foreach (var group in board.Groups)
            {
                // One topic per group, not per clash. A group is the unit somebody fixes; a BCF file
                // with one topic per penetration is the export everybody complains about.
                var topic = new BcfTopic(BcfGuid.For(group.Id), group.Name, now, author)
                {
                    TopicType = "Clash",
                    TopicStatus = "Open",
                    AssignedTo = group.AssignedTo,
                    Priority = group.Priority,
                    Description = Ui.Count(group.Items.Count, "clash") + " in " + group.Name
                                  + ". " + board.Funnel,
                };

                foreach (var label in new[] { group.Items.FirstOrDefault()?.Level, group.Items.FirstOrDefault()?.ModelPair })
                    if (!string.IsNullOrEmpty(label)) topic.Labels.Add(label!);

                topics.Add(topic);
            }

            return topics;
        }
    }
}
