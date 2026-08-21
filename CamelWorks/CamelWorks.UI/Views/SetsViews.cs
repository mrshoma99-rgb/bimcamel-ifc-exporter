using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Appearance;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Project;
using CamelWorks.Core.Sets;
using CamelWorks.Nav;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// The Sets &amp; Views workspace: build sets by rule, decide what the model looks like, and keep
    /// viewpoints in order.
    /// </summary>
    public static class SetsViews
    {
        /// <summary>The boolean set builder.</summary>
        public static UIElement Builder() => new SetBuilderScreen().Build();

        /// <summary>The layers system.</summary>
        public static UIElement Appearance() => new AppearanceScreen().Build();

        /// <summary>Bulk viewpoint operations.</summary>
        public static UIElement Viewpoints() => new ViewpointScreen().Build();

        // ===================================================================================
        // Resolving a layer target or a set expression against the host
        // ===================================================================================

        /// <summary>
        /// Turns a layer's target into the elements it currently covers.
        ///
        /// A rule target re-runs its search every time this is asked, which is the whole point of a
        /// rule target: the layer picks up elements added since it was made. Results are cached for
        /// the life of one fold so that ten layers over the same set do not run ten searches.
        /// </summary>
        internal sealed class HostResolver : ILayerResolver
        {
            private readonly Session _session;
            private readonly Dictionary<string, IReadOnlyCollection<ElementKey>> _cache =
                new Dictionary<string, IReadOnlyCollection<ElementKey>>(StringComparer.Ordinal);

            internal HostResolver(Session session) => _session = session;

            public IReadOnlyCollection<ElementKey> Resolve(LayerTarget target)
            {
                if (target.Expression == null) return target.Keys;

                var signature = target.Expression.Describe();

                if (_cache.TryGetValue(signature, out var cached)) return cached;

                var outcome = _session.Search.Run(DnfCompiler.Compile(target.Expression));
                _cache[signature] = outcome.Keys;
                return outcome.Keys;
            }
        }

        // ===================================================================================
        // Set builder
        // ===================================================================================

        /// <summary>
        /// The set builder, held as a small object rather than a function because it edits a list
        /// the screen redraws itself from.
        ///
        /// <b>The editor's shape is disjunctive normal form.</b> Groups are ORed, conditions inside
        /// a group are ANDed, and any condition can be negated. That is not a simplification of
        /// boolean algebra — it is all of it, and it happens to be the exact shape the host can run,
        /// so what the user builds and what Navisworks executes are the same structure.
        /// </summary>
        private sealed class SetBuilderScreen
        {
            private readonly List<List<Condition>> _groups = new List<List<Condition>> { new List<Condition>() };
            private readonly StackPanel _editor = Ui.Stack();
            private readonly StackPanel _results = Ui.Stack();
            private readonly StackPanel _saved = Ui.Stack();
            private readonly TextBox _name = Ui.Text("New set", null, 220);

            private sealed class Condition
            {
                internal bool Negated;
                internal string Category = "Element";
                internal string Property = "Category";
                internal SetOperator Operator = SetOperator.Equals;
                internal string Value = string.Empty;
                internal string? SetReference;
            }

            internal UIElement Build()
            {
                var session = Host.Current;

                if (session == null)
                    return Ui.Scroll(Ui.Stack(Ui.Heading("Sets"), Ui.Sub(Host.NoModel)));

                Redraw();
                RedrawSaved(session);

                return Ui.Scroll(Ui.Stack(
                    Ui.Heading("Sets"),
                    Ui.Sub("Groups are ORed together; conditions inside a group are ANDed. Any condition can "
                           + "be negated, and a negation compiles to a subtraction rather than a not-equals — "
                           + "those are different sets, because a host condition on a property an element does "
                           + "not have matches nothing."),
                    Ui.Card("Rule", null, _editor),
                    Ui.Field("Name", _name),
                    Actions(session),
                    _results,
                    Ui.Card("Saved sets", "Kept in the project file, so they travel with the job.", _saved)));
            }

            private UIElement Actions(Session session)
            {
                var preview = Ui.Runner("Run", _results, job =>
                {
                    var plan = DnfCompiler.Compile(Expression());

                    _results.Children.Add(Ui.Line(plan.Explain(), 1, true));

                    foreach (var warning in plan.Warnings) _results.Children.Add(Ui.Note(warning));

                    if (plan.MatchesNothing)
                    {
                        _results.Children.Add(Ui.Empty("Matches nothing.",
                            "The rule contradicts itself — a condition and its negation are both required."));
                        return;
                    }

                    job.Say("Searching...");
                    var outcome = session.Search.Run(plan);

                    _results.Children.Add(Ui.Line(Ui.Count(outcome.Count, "element") + " found", 1, true));

                    foreach (var note in outcome.Notes.Skip(plan.Warnings.Count))
                        _results.Children.Add(Ui.Note(note));

                    if (outcome.Count > 0) session.Model.Select(outcome.Keys);
                });

                var save = Ui.Button("Save to project", () =>
                {
                    _results.Children.Clear();

                    var library = SetLibrary.FromJson(session.Store.Section(ProjectStore.SetsSection)["items"]);
                    var title = _name.Text.Trim();

                    if (title.Length == 0)
                    {
                        _results.Children.Add(Ui.Line("Give the set a name first.", 0.8));
                        return;
                    }

                    var existing = library.Sets.FirstOrDefault(
                        s => string.Equals(s.Name, title, StringComparison.OrdinalIgnoreCase));

                    library.Put(new SavedSet(existing?.Id ?? library.NextId("set"), title, Expression()));

                    session.Store.Section(ProjectStore.SetsSection).Set("items", library.ToJson());

                    _results.Children.Add(session.Store.Save()
                        ? Ui.Line("Saved.", 0.8)
                        : Ui.Problem(session.Store.LastSaveProblem ?? "Could not write the project file."));

                    RedrawSaved(session);
                });

                var publish = Ui.Runner("Publish as a Navisworks set", _results, job =>
                {
                    var title = _name.Text.Trim();

                    if (title.Length == 0)
                    {
                        _results.Children.Add(Ui.Line("Give the set a name first.", 0.8));
                        return;
                    }

                    job.Say("Searching...");
                    var outcome = session.Search.Publish(title, DnfCompiler.Compile(Expression()));

                    _results.Children.Add(Ui.Line(
                        "Published \"" + title + "\" — " + outcome, 1, true));

                    _results.Children.Add(outcome.IsLive
                        ? Ui.Note("Saved as a search set: it re-runs, so elements added later appear in it.")
                        : Ui.Note("Saved as a selection set holding these exact elements. This rule needs more "
                                  + "than one search, and Navisworks search sets can only hold one."));
                });

                return Ui.Across(save, preview, publish);
            }

            private SetExpression Expression()
            {
                var clauses = new List<SetExpression>();

                foreach (var group in _groups)
                {
                    var parts = new List<SetExpression>();

                    foreach (var condition in group)
                    {
                        SetExpression leaf;

                        if (condition.SetReference != null)
                        {
                            leaf = SetExpression.InSet(condition.SetReference, condition.SetReference);
                        }
                        else
                        {
                            if (condition.Category.Trim().Length == 0) continue;

                            try
                            {
                                leaf = SetExpression.Where(new SetCondition(
                                    condition.Category.Trim(),
                                    condition.Property.Trim().Length == 0 ? null : condition.Property.Trim(),
                                    condition.Operator,
                                    NeedsValue(condition.Operator) ? condition.Value : null));
                            }
                            catch (ArgumentException)
                            {
                                // A half-typed row — a numeric comparison with no number in it yet.
                                // Skipped rather than throwing, so the preview stays usable while
                                // the rule is being written.
                                continue;
                            }
                        }

                        parts.Add(condition.Negated ? SetExpression.Not(leaf) : leaf);
                    }

                    if (parts.Count > 0) clauses.Add(SetExpression.And(parts.ToArray()));
                }

                return clauses.Count == 0 ? SetExpression.Nothing : SetExpression.Or(clauses.ToArray());
            }

            private static bool NeedsValue(SetOperator op) =>
                op != SetOperator.Defined && op != SetOperator.HasCategory;

            private void Redraw()
            {
                _editor.Children.Clear();

                for (var g = 0; g < _groups.Count; g++)
                {
                    var group = _groups[g];

                    if (g > 0) _editor.Children.Add(Ui.Line("or", 0.6, true));

                    foreach (var condition in group.ToList())
                        _editor.Children.Add(Row(group, condition));

                    var add = Ui.Button("and...", () => { group.Add(new Condition()); Redraw(); });
                    add.MinWidth = 70;
                    _editor.Children.Add(Ui.Across(add));
                }

                _editor.Children.Add(Ui.Across(
                    Ui.Button("Add an OR group", () => { _groups.Add(new List<Condition>()); Redraw(); })));
            }

            private UIElement Row(List<Condition> group, Condition condition)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };

                row.Children.Add(Ui.Check("not", condition.Negated, v => condition.Negated = v));
                row.Children.Add(Ui.Text(condition.Category, v => condition.Category = v, 110));
                row.Children.Add(Ui.Text(condition.Property, v => condition.Property = v, 110));

                row.Children.Add(Ui.Choice(
                    Enum.GetNames(typeof(SetOperator)),
                    condition.Operator.ToString(),
                    v => { if (Enum.TryParse<SetOperator>(v, out var op)) condition.Operator = op; },
                    150));

                row.Children.Add(Ui.Text(condition.Value, v => condition.Value = v, 130));

                var remove = Ui.Button("x", () => { group.Remove(condition); Redraw(); });
                remove.MinWidth = 26;
                row.Children.Add(remove);

                foreach (var child in row.Children.OfType<FrameworkElement>())
                    child.Margin = new Thickness(0, 0, 5, 0);

                return row;
            }

            private void RedrawSaved(Session session)
            {
                _saved.Children.Clear();

                var library = SetLibrary.FromJson(session.Store.Section(ProjectStore.SetsSection)["items"]);

                if (library.Sets.Count == 0)
                {
                    _saved.Children.Add(Ui.Empty("None saved yet.",
                        "Sets you save here are stored in the project file beside the model, not in your "
                        + "own profile, so whoever opens the job next has them too."));
                    return;
                }

                var rows = library.Sets
                    .Select(s => new TableRow(s, s.Name, s.Expression.Describe()))
                    .ToList();

                _saved.Children.Add(Ui.Table(rows, ("Set", 160), ("Rule", 0)).OnPick(item =>
                {
                    if (item is SavedSet set) Load(set);
                }));
            }

            private void Load(SavedSet set)
            {
                _name.Text = set.Name;
                _groups.Clear();

                foreach (var clause in Clauses(set.Expression))
                {
                    var group = new List<Condition>();

                    foreach (var part in Parts(clause))
                    {
                        var negated = part is NotExpression;
                        var inner = negated ? ((NotExpression)part).Inner : part;

                        if (inner is ConditionExpression c)
                            group.Add(new Condition
                            {
                                Negated = negated,
                                Category = c.Condition.Category,
                                Property = c.Condition.Property ?? string.Empty,
                                Operator = c.Condition.Operator,
                                Value = c.Condition.Value ?? string.Empty,
                            });
                        else if (inner is SetReferenceExpression r)
                            group.Add(new Condition { Negated = negated, SetReference = r.SetId });
                    }

                    _groups.Add(group);
                }

                if (_groups.Count == 0) _groups.Add(new List<Condition>());

                Redraw();
            }

            private static IEnumerable<SetExpression> Clauses(SetExpression expression) =>
                expression is OrExpression union ? union.Parts : new[] { expression };

            private static IEnumerable<SetExpression> Parts(SetExpression expression) =>
                expression is AndExpression all ? all.Parts : new[] { expression };
        }

        // ===================================================================================
        // Appearance
        // ===================================================================================

        /// <summary>
        /// The layers system.
        ///
        /// The question the host cannot answer is "what is hidden right now, and why" — and it is
        /// the question that stops a federation being handed over. Navisworks hides an element and
        /// forgets everything about the act; a week later four hundred elements are missing and
        /// nobody dares un-hide them in case something was hidden for a reason.
        ///
        /// So every change goes through a named layer that says what it decides and why, the fold
        /// reports which layer won each property and which were overruled, and overrides CamelWorks
        /// did not author are shown and never cleared.
        /// </summary>
        private sealed class AppearanceScreen
        {
            private readonly List<AppearanceLayer> _layers = new List<AppearanceLayer>();
            private readonly StackPanel _stack = Ui.Stack();
            private readonly StackPanel _results = Ui.Stack();
            private readonly StackPanel _adder = Ui.Stack();

            internal UIElement Build()
            {
                var session = Host.Current;

                if (session == null)
                    return Ui.Scroll(Ui.Stack(Ui.Heading("Appearance"), Ui.Sub(Host.NoModel)));

                _layers.AddRange(LayerStackJson.Read(session.Store.Section(ProjectStore.LayersSection)["stack"]));

                Redraw(session);
                BuildAdder(session);

                return Ui.Scroll(Ui.Stack(
                    Ui.Heading("Appearance"),
                    Ui.Sub("Every override and every hidden element, as a stack of named layers. The layer at "
                           + "the top wins, property by property — a layer that only sets a colour leaves "
                           + "visibility to the one underneath."),
                    Ui.Card("Layers", "Top of the list wins.", _stack),
                    _adder,
                    Actions(session),
                    _results));
            }

            private UIElement Actions(Session session)
            {
                var apply = Ui.Runner("Apply the stack", _results, job =>
                {
                    job.Say("Resolving layers...");

                    var fold = AppearanceStack.Fold(_layers, new HostResolver(session));

                    job.Say("Reading what the model looks like now...");

                    var keys = fold.Elements.Select(e => e.Key).ToList();
                    var current = session.View.ReadAppearance(keys);

                    var plan = AppearancePlanner.Plan(fold, current);

                    _results.Children.Add(Ui.Line(fold.ToString(), 1, true));
                    _results.Children.Add(Ui.Line(plan.Explain(), 0.8));

                    if (plan.Foreign.Count > 0)
                        _results.Children.Add(Ui.Note(Ui.Count(plan.Foreign.Count, "element")
                            + " carry an override CamelWorks did not author. They are left exactly as they are: "
                            + "clearing somebody else's work because it was in the way is how a tool loses trust."));

                    if (plan.IsEmpty)
                    {
                        _results.Children.Add(Ui.Empty("Nothing to change.",
                            "The model already looks the way this stack says it should."));
                    }
                    else
                    {
                        job.Say("Applying...");
                        Execute(session, plan);

                        _results.Children.Add(Ui.Line("Applied.", 1, true));
                        session.Record(ActivityKind.Appearance, "applied the appearance stack: " + plan.Explain());
                    }

                    foreach (var report in fold.Layers.Where(l => l.IsDead))
                        _results.Children.Add(Ui.Note("\"" + report.Layer.Name + "\" covers "
                            + Ui.Count(report.Covers, "element") + " and decides nothing about any of them — "
                            + "every property it sets is overruled by a layer above it."));

                    Explain(session, fold);
                });

                var save = Ui.Button("Save the stack", () =>
                {
                    _results.Children.Clear();

                    session.Store.Section(ProjectStore.LayersSection).Set("stack", LayerStackJson.Write(_layers));

                    _results.Children.Add(session.Store.Save()
                        ? Ui.Line("Saved to " + session.Store.Where + ".", 0.8)
                        : Ui.Problem(session.Store.LastSaveProblem ?? "Could not write the project file."));
                });

                var reset = Ui.Button("Show everything", () =>
                {
                    _results.Children.Clear();

                    // Deliberately not "clear all overrides": that would also throw away colouring
                    // somebody applied by hand outside CamelWorks.
                    var all = new List<ElementKey>();

                    foreach (var layer in _layers)
                        all.AddRange(new HostResolver(session).Resolve(layer.Target));

                    session.View.SetVisible(all.Distinct().ToList(), true);
                    _results.Children.Add(Ui.Line("Un-hid " + Ui.Count(all.Distinct().Count(), "element")
                                                  + " the stack covers. Colours are left alone.", 0.8));
                });

                return Ui.Across(apply, save, reset);
            }

            private static void Execute(Session session, AppearancePlan plan)
            {
                if (plan.Clear.Count > 0) session.View.ClearOverrides(plan.Clear);
                if (plan.Hide.Count > 0) session.View.SetVisible(plan.Hide, false);
                if (plan.Show.Count > 0) session.View.SetVisible(plan.Show, true);

                foreach (var batch in plan.Colours) session.View.SetColour(batch.Keys, batch.Colour);

                foreach (var batch in plan.Transparencies)
                    session.View.SetTransparency(batch.Keys, batch.Transparency);
            }

            private void Explain(Session session, AppearanceFold fold)
            {
                var selected = session.Model.SelectedKeys();
                if (selected.Count == 0) return;

                var appearance = fold.For(selected[0]);
                if (appearance == null) return;

                _results.Children.Add(Ui.Card("The selected element",
                    "Why it looks the way it does.", Ui.Line(appearance.Explain(), 0.85)));
            }

            private void Redraw(Session session)
            {
                _stack.Children.Clear();

                if (_layers.Count == 0)
                {
                    _stack.Children.Add(Ui.Empty("No layers yet.",
                        "Add one from the current selection or from a saved set. Until then CamelWorks is not "
                        + "deciding anything about how the model looks."));
                    return;
                }

                // Held bottom-first, because that is the order AppearanceStack.Fold applies it and
                // the order it is saved in. Shown the other way up, the way every layers panel does.
                for (var i = _layers.Count - 1; i >= 0; i--)
                {
                    var layer = _layers[i];
                    var index = i;

                    var row = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };

                    row.Children.Add(Ui.Check(string.Empty, layer.IsEnabled, v => layer.IsEnabled = v));

                    row.Children.Add(Ui.Line(layer.Name, 1, true));
                    row.Children.Add(Ui.Pill(layer.Target.ReResolves ? "rule" : "fixed",
                        layer.Target.ReResolves ? Tone.Good : Tone.Plain));
                    row.Children.Add(Ui.Line(layer.Target.Description, 0.7));
                    row.Children.Add(Ui.Line(Effects(layer), 0.85));

                    var up = Ui.Button("up", () => Move(session, index, 1));
                    var down = Ui.Button("down", () => Move(session, index, -1));
                    var drop = Ui.Button("remove", () => { _layers.RemoveAt(index); Redraw(session); });

                    foreach (var button in new[] { up, down, drop }) button.MinWidth = 54;

                    row.Children.Add(up);
                    row.Children.Add(down);
                    row.Children.Add(drop);

                    foreach (var child in row.Children.OfType<FrameworkElement>())
                        child.Margin = new Thickness(0, 0, 6, 0);

                    _stack.Children.Add(row);

                    if (!string.IsNullOrEmpty(layer.Note))
                        _stack.Children.Add(Ui.Line(layer.Note!, 0.6));
                }
            }

            private static string Effects(AppearanceLayer layer)
            {
                var parts = new List<string>();

                if (layer.Visible != null) parts.Add(layer.Visible.Value ? "show" : "hide");
                if (layer.Colour != null) parts.Add(layer.Colour.Value.ToString());

                if (layer.Transparency != null)
                    parts.Add(layer.Transparency.Value.ToString("0%", CultureInfo.InvariantCulture) + " transparent");

                return parts.Count == 0 ? "decides nothing" : string.Join(", ", parts);
            }

            private void Move(Session session, int index, int by)
            {
                var to = index + by;
                if (to < 0 || to >= _layers.Count) return;

                var layer = _layers[index];
                _layers.RemoveAt(index);
                _layers.Insert(to, layer);

                Redraw(session);
            }

            private void BuildAdder(Session session)
            {
                var name = Ui.Text("New layer", null, 170);
                var note = Ui.Text(string.Empty, null, 240);
                var colour = Ui.Text(string.Empty, null, 90);
                var transparency = Ui.Text(string.Empty, null, 70);

                var decides = "leave visibility alone";
                var visibility = Ui.Choice(new[] { "leave visibility alone", "show", "hide" }, decides, v => decides = v);

                var from = "Current selection";
                var library = SetLibrary.FromJson(session.Store.Section(ProjectStore.SetsSection)["items"]);

                var sources = new List<string> { "Current selection", "Everything" };
                sources.AddRange(library.Sets.Select(s => "Set: " + s.Name));

                var source = Ui.Choice(sources, from, v => from = v);

                var add = Ui.Button("Add layer", () =>
                {
                    LayerTarget target;

                    if (from == "Everything")
                    {
                        target = LayerTarget.Everything();
                    }
                    else if (from.StartsWith("Set: ", StringComparison.Ordinal))
                    {
                        var set = library.Sets.FirstOrDefault(
                            s => string.Equals(s.Name, from.Substring(5), StringComparison.Ordinal));

                        if (set == null) return;
                        target = LayerTarget.Set(set.Expression, set.Name);
                    }
                    else
                    {
                        var keys = session.Model.SelectedKeys();

                        if (keys.Count == 0)
                        {
                            _results.Children.Clear();
                            _results.Children.Add(Ui.Line("Nothing is selected.", 0.8));
                            return;
                        }

                        target = LayerTarget.Elements(keys);
                    }

                    var layer = new AppearanceLayer(
                        "layer-" + (_layers.Count + 1).ToString(CultureInfo.InvariantCulture)
                        + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
                        name.Text.Trim().Length == 0 ? "Layer " + (_layers.Count + 1) : name.Text.Trim(),
                        target)
                    {
                        Note = note.Text.Trim().Length == 0 ? null : note.Text.Trim(),
                        Visible = decides == "show" ? true : decides == "hide" ? (bool?)false : null,
                    };

                    if (Colour.TryParse(colour.Text, out var parsed)) layer.Colour = parsed;

                    if (double.TryParse(transparency.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                                        out var t) && t >= 0 && t <= 1)
                        layer.Transparency = t;

                    // Top of the stack wins, and a layer you just made is the one you are thinking
                    // about — so it goes on top, which in fold order is the end of the list.
                    _layers.Add(layer);
                    Redraw(session);
                });

                _adder.Children.Add(Ui.Card("Add a layer", null,
                    Ui.Field("Name", name),
                    Ui.Field("Covers", source),
                    Ui.Field("Visibility", visibility),
                    Ui.Field("Colour (#rrggbb)", colour),
                    Ui.Field("Transparency (0-1)", transparency),
                    Ui.Field("Why", note),
                    Ui.Across(add)));
            }
        }

        // ===================================================================================
        // Viewpoints
        // ===================================================================================

        /// <summary>Bulk rename, renumber and re-folder, plus saving one from where you are.</summary>
        private sealed class ViewpointScreen
        {
            private readonly StackPanel _list = Ui.Stack();
            private readonly StackPanel _results = Ui.Stack();

            internal UIElement Build()
            {
                var session = Host.Current;

                if (session == null)
                    return Ui.Scroll(Ui.Stack(Ui.Heading("Viewpoints"), Ui.Sub(Host.NoModel)));

                Redraw(session);

                var pattern = Ui.Text("{n}. {name}", null, 200);
                var start = Ui.Text("1", null, 60);

                var renumber = Ui.Runner("Rename all", _results, job =>
                {
                    var views = session.Viewpoints.All();

                    if (!int.TryParse(start.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        n = 1;

                    var done = 0;

                    foreach (var view in views)
                    {
                        if (!job.Step(done, "renamed")) break;

                        var name = pattern.Text
                            .Replace("{n}", n.ToString("00", CultureInfo.InvariantCulture))
                            .Replace("{name}", view.Name)
                            .Replace("{folder}", view.Folder ?? string.Empty);

                        if (!string.Equals(name, view.Name, StringComparison.Ordinal))
                        {
                            session.Viewpoints.Rename(view.Id, name);
                            done++;
                        }

                        n++;
                    }

                    _results.Children.Add(Ui.Line("Renamed " + Ui.Count(done, "viewpoint") + ".", 1, true));
                    Redraw(session);
                });

                var save = Ui.Button("Save this view", () =>
                {
                    _results.Children.Clear();

                    var view = session.Viewpoints.SaveCurrent(
                        "View " + (session.Viewpoints.All().Count + 1).ToString(CultureInfo.InvariantCulture));

                    _results.Children.Add(Ui.Line("Saved \"" + view.Name + "\".", 0.8));
                    Redraw(session);
                });

                return Ui.Scroll(Ui.Stack(
                    Ui.Heading("Viewpoints"),
                    Ui.Sub("A saved viewpoint carries the overrides that were in force when it was made, so "
                           + "applying one changes what the Appearance stack says is true. The list says which "
                           + "ones do."),
                    Ui.Across(save),
                    Ui.Card("Rename in bulk",
                        "{n} is the number, {name} the current name, {folder} the folder.",
                        Ui.Field("Pattern", pattern),
                        Ui.Field("Start at", start),
                        renumber),
                    _results,
                    Ui.Card("Saved viewpoints", null, _list)));
            }

            private void Redraw(Session session)
            {
                _list.Children.Clear();

                var views = session.Viewpoints.All();

                if (views.Count == 0)
                {
                    _list.Children.Add(Ui.Empty("None saved.",
                        "Save one from wherever the camera is now."));
                    return;
                }

                var rows = views.Select(v => new TableRow(v,
                    v.Name,
                    v.Folder ?? string.Empty,
                    v.HasOverrides ? "carries overrides" : string.Empty,
                    v.HasRedlines ? "has redlines" : string.Empty)).ToList();

                _list.Children.Add(Ui.Table(rows,
                    ("Viewpoint", 200), ("Folder", 120), ("", 130), ("", 0))
                    .OnPick(item =>
                    {
                        if (item is SavedView view) session.Viewpoints.Apply(view.Id);
                    }));
            }
        }
    }
}
