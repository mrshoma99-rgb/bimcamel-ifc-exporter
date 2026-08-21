using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Navisworks.Api.Plugins;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Data;
using CamelWorks.Core.Project;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// The Project workspace: is this federation fit to work on, what did CamelWorks work out about
    /// it, what is loaded, and the IFC exporter.
    /// </summary>
    public static class ProjectViews
    {
        /// <summary>The dock pane key of the BIMCamel IFC exporter, which ships alongside.</summary>
        public const string ExporterPaneKey = "BIMCamel.ExportDockPane.BIMCamel";

        // -----------------------------------------------------------------------------------
        // Health
        // -----------------------------------------------------------------------------------

        /// <summary>One scorecard over the federation, run on demand.</summary>
        public static UIElement Health()
        {
            var results = Ui.Stack();

            var run = Ui.Runner("Check this model", results, job =>
            {
                var session = Host.Current;
                if (session == null) { results.Children.Add(Ui.Line(Host.NoModel)); return; }

                var elements = new List<HealthElement>();
                var scale = session.MetresPerUnit;

                foreach (var item in session.Model.Traverse(TraversalScope.WholeDocument))
                {
                    if (elements.Count % 2000 == 0 && !job.Step(elements.Count, "elements read")) return;

                    var bounds = item.Bounds;

                    elements.Add(new HealthElement(item.Key.ToString(), item.Model.DisplayName)
                    {
                        Name = item.DisplayName,
                        Category = item.Category,
                        X = bounds.CentreX * scale,
                        Y = bounds.CentreY * scale,
                        Z = bounds.CentreZ * scale,
                        SizeX = bounds.SizeX * scale,
                        SizeY = bounds.SizeY * scale,
                        SizeZ = bounds.SizeZ * scale,

                        // Only the zero case is ever asked about, and counting every property on
                        // every element of a federation costs more than the whole rest of the check.
                        PropertyCount = item.Properties().Any() ? 1 : 0,
                    });
                }

                job.Say("Checking...");
                var report = ModelHealth.Check(elements);

                results.Children.Add(Ui.Line(report.ToString(), 1, true));

                if (report.IsClean)
                {
                    results.Children.Add(Ui.Empty("Nothing found.",
                        "That is a real result, not an empty screen — every rule ran and none matched."));
                    return;
                }

                foreach (var finding in report.Findings)
                {
                    results.Children.Add(Ui.Line(finding.Summary, 1, true));
                    results.Children.Add(Ui.Line(finding.Fix, 0.75));
                    results.Children.Add(Ui.Line("e.g. " + string.Join(", ", finding.Examples), 0.55));
                }

                session.Record(ActivityKind.Reconcile, "checked model health: " + report);
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Health"),
                Ui.Sub("Six checks over geometry, placement and data. Nothing to configure — the rules "
                       + "are built in and the thresholds scale to the model rather than assuming metres."),
                run,
                results));
        }

        // -----------------------------------------------------------------------------------
        // Setup
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// What CamelWorks already derived, with an override on each line.
        ///
        /// <b>This screen is skippable and says so.</b> Every value on it already has an answer, so
        /// nothing here has to be visited before any other feature works — which is the difference
        /// between a product you can try in a minute and one that starts with an afternoon of forms.
        /// </summary>
        public static UIElement Setup()
        {
            var session = Host.Current;

            if (session == null)
                return Ui.Scroll(Ui.Stack(Ui.Heading("Setup"), Ui.Sub(Host.NoModel)));

            var profile = session.Profile;
            var status = Ui.Stack();
            var rows = Ui.Stack();

            foreach (var setting in profile.Settings) rows.Children.Add(Row(setting));

            var save = Ui.Button("Save overrides", () =>
            {
                status.Children.Clear();

                // Written whole rather than merged: the profile object already carries every
                // override in force, including the ones loaded from the file.
                var saved = profile.OverridesToJson();
                var section = session.Store.Section(ProjectStore.ProfileSection);

                foreach (var key in section.Keys.ToList()) section.Remove(key);
                foreach (var key in saved.Keys) section.Set(key, saved[key]);

                status.Children.Add(session.Store.Save()
                    ? Ui.Line("Saved " + Ui.Count(saved.Keys.Count, "override") + " to " + session.Store.Where, 0.8)
                    : Ui.Problem(session.Store.LastSaveProblem ?? "The project file could not be written."));
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Setup"),
                Ui.Sub("Everything here already has a value, worked out from the model. You never have "
                       + "to visit this screen; change a line only when the guess is wrong."),
                Ui.Card("Derived values", null, rows),
                Ui.Across(save),
                status));
        }

        private static UIElement Row(ProfileSetting setting)
        {
            var value = Ui.Stack();

            UIElement editor = setting.Choices.Count > 0
                ? (UIElement)Ui.Choice(setting.Choices, setting.Value, pick => setting.Override = pick, 200)
                : Ui.Text(setting.Value, typed => setting.Override = typed, 200);

            var source = Ui.Line(setting.IsOverridden ? "you" : "derived: " + (setting.Derived ?? "nothing"), 0.55);

            value.Children.Add(editor);
            value.Children.Add(source);
            value.Children.Add(Ui.Line(setting.Because, 0.55));

            return Ui.Field(setting.Title, value);
        }

        // -----------------------------------------------------------------------------------
        // Models
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// The federation: what is loaded, from where, and whether the source has moved on.
        ///
        /// The last column is the one that matters on a Monday. Navisworks will happily open a
        /// federation whose sources changed a fortnight ago and show it without comment, and the
        /// coordination that follows is against a model nobody is building.
        /// </summary>
        public static UIElement Models()
        {
            var session = Host.Current;

            if (session == null)
                return Ui.Scroll(Ui.Stack(Ui.Heading("Models"), Ui.Sub(Host.NoModel)));

            var savedAt = FileTime(session.SavedPath);
            var rows = new List<TableRow>();
            var missing = 0;
            var stale = 0;

            foreach (var model in session.Model.Models)
            {
                var when = FileTime(model.SourcePath);
                string state;
                Tone tone;

                if (string.IsNullOrWhiteSpace(model.SourcePath))
                {
                    state = "no source path";
                    tone = Tone.Plain;
                }
                else if (when == null)
                {
                    state = "not found on disk";
                    tone = Tone.Bad;
                    missing++;
                }
                else if (savedAt != null && when > savedAt)
                {
                    state = "changed since this file was saved";
                    tone = Tone.Warn;
                    stale++;
                }
                else
                {
                    state = when.Value.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
                    tone = Tone.Plain;
                }

                rows.Add(new TableRow(model,
                    model.DisplayName,
                    session.Profile.DisciplineOf(model.Scope) ?? "-",
                    state,
                    model.SourcePath)
                {
                    Tone = tone,
                });
            }

            var summary = Ui.Across(
                Ui.Pill(Ui.Count(rows.Count, "model")),
                missing > 0 ? Ui.Pill(missing.ToString(CultureInfo.InvariantCulture) + " not found", Tone.Bad) : null,
                stale > 0 ? Ui.Pill(stale.ToString(CultureInfo.InvariantCulture) + " changed since save", Tone.Warn) : null);

            var body = Ui.Stack(summary);

            if (missing > 0)
                body.Children.Add(Ui.Note("A model whose source cannot be found still displays: Navisworks is "
                    + "showing you its own cached copy. Repoint it, or refresh from the folder it moved to, "
                    + "before coordinating against it."));

            if (rows.Count == 0)
                body.Children.Add(Ui.Empty("Nothing is loaded.", "Append or merge a model to start."));
            else
                body.Children.Add(Ui.Table(rows,
                    ("Model", 150), ("Discipline", 100), ("Source", 190), ("Path", 0)));

            body.Children.Add(Repoint(session));

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Models"),
                Ui.Sub("What is loaded, from where, and whether the file on disk has moved on since this "
                       + "federation was saved."),
                body));
        }

        private static UIElement Repoint(Session session)
        {
            var results = Ui.Stack();
            var folder = Ui.Text(string.Empty, null, 300);

            var run = Ui.Runner("Look for missing sources", results, job =>
            {
                var where = folder.Text.Trim();

                if (where.Length == 0)
                {
                    results.Children.Add(Ui.Line("Type a folder to search first.", 0.8));
                    return;
                }

                if (!Directory.Exists(where))
                {
                    results.Children.Add(Ui.Problem("There is no folder at " + where + "."));
                    return;
                }

                var found = 0;
                var looked = 0;

                foreach (var model in session.Model.Models)
                {
                    if (!job.Step(looked++, "models checked")) return;

                    if (string.IsNullOrWhiteSpace(model.SourcePath) || File.Exists(model.SourcePath)) continue;

                    var name = System.IO.Path.GetFileName(model.SourcePath);

                    var candidates = Directory.GetFiles(where, name, SearchOption.AllDirectories);

                    if (candidates.Length == 0)
                    {
                        results.Children.Add(Ui.Line(model.DisplayName + " — no file called " + name
                                                     + " under that folder.", 0.75));
                        continue;
                    }

                    found++;
                    results.Children.Add(Ui.Line(model.DisplayName + " — found at " + candidates[0], 1, true));

                    if (candidates.Length > 1)
                        results.Children.Add(Ui.Line("and " + (candidates.Length - 1) + " more with the same name; "
                            + "the first is shown.", 0.55));
                }

                if (found == 0 && looked > 0)
                    results.Children.Add(Ui.Empty("Nothing to repoint.",
                        "Every loaded model's source file is where the federation expects it."));
                else
                    results.Children.Add(Ui.Note("CamelWorks reports where the files are; it does not rewrite "
                        + "the federation. Repointing is done in Navisworks with Project > Refresh or by "
                        + "re-appending the file, so that what changes your project file is always your own "
                        + "deliberate action."));
            });

            return Ui.Card("Find moved files",
                "Searches a folder for anything a loaded model can no longer find.",
                Ui.Field("Search folder", folder),
                run,
                results);
        }

        private static DateTime? FileTime(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                                      || e is ArgumentException || e is NotSupportedException)
            {
                // An unreachable share or a path the OS will not even parse. Unknown, not missing:
                // telling somebody their model is gone because a VPN dropped would be worse.
                return null;
            }
        }

        // -----------------------------------------------------------------------------------
        // Export IFC
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// The IFC exporter, which is its own pane.
        ///
        /// Opened rather than re-hosted. The exporter is a finished tool with its own screen, and
        /// duplicating it here to make the tab look full would mean two copies of a settings form
        /// that must not disagree.
        /// </summary>
        public static UIElement ExportIfc()
        {
            var status = Ui.Stack();

            var open = Ui.Button("Open the IFC exporter", () =>
            {
                status.Children.Clear();

                var record = Autodesk.Navisworks.Api.Application.Plugins.FindPlugin(ExporterPaneKey);

                if (!(record is DockPanePluginRecord pane))
                {
                    status.Children.Add(Ui.Problem(
                        "The BIMCamel IFC exporter is not loaded in this Navisworks. It installs alongside "
                        + "CamelWorks; if you installed only part of the bundle, install the rest, and check "
                        + "that the build matches this Navisworks release."));
                    return;
                }

                if (!pane.IsLoaded) pane.LoadPlugin();

                if (pane.LoadedPlugin is DockPanePlugin dock)
                {
                    dock.Visible = true;
                    status.Children.Add(Ui.Line("Opened. It docks as its own panel.", 0.75));
                }
            });

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Export IFC"),
                Ui.Sub("Writes IFC4 or IFC2x3 from whatever is loaded, including geometry Navisworks itself "
                       + "cannot export. It has its own panel because it is a long-running job with its own "
                       + "settings."),
                Ui.Across(open),
                status));
        }
    }
}
