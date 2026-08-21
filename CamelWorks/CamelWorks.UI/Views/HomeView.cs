using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CamelWorks.Core.Project;


namespace CamelWorks.UI.Views
{
    /// <summary>
    /// The front door: the weekly cycle, when each step last happened, and the way to each.
    ///
    /// A coordinator's week has a shape — bring in the new models, run the rules over what that
    /// broke, send out the result — and a product whose front page is a menu makes them rebuild
    /// that shape in their head every Monday. This screen is the shape, with the state filled in.
    /// </summary>
    public static class HomeView
    {
        /// <summary>Build the screen.</summary>
        /// <param name="goTo">Navigate the pane to a "workspace/tab" route.</param>
        public static UIElement Build(Action<string> goTo)
        {
            var session = Host.Current;

            if (session == null)
                return Ui.Scroll(Ui.Stack(
                    Ui.Heading("CamelWorks"),
                    Ui.Sub(Host.NoModel)));

            var body = Ui.Stack(
                Header(session),
                Cycle(session, goTo),
                Recent(session));

            return Ui.Scroll(body);
        }

        private static UIElement Header(Session session)
        {
            var chips = Ui.Across(
                Ui.Pill(Ui.Count(session.Model.Models.Count, "model")),
                Ui.Pill(session.Document.Units.ToString()),
                Ui.Pill(session.Clash.IsAvailable
                    ? Ui.Count(session.Clash.Tests().Count, "clash test")
                    : "no clash engine", session.Clash.IsAvailable ? Tone.Plain : Tone.Warn));

            var body = Ui.Stack(chips, Ui.Line("Project file " + session.Store.Where, 0.7));

            if (session.Store.UsedBackup)
                body.Children.Add(Ui.Note("The project file could not be read and its backup was used instead. "
                                          + "Nothing is lost that was saved before the last write."));

            if (session.Store.IsReadOnly)
                body.Children.Add(Ui.Note(session.Store.ReadOnlyReason ?? string.Empty));

            if (session.Store.ConcurrencyNote.Length > 0)
                body.Children.Add(Ui.Note(session.Store.ConcurrencyNote));

            return Ui.Card(session.Profile.ProjectName, null, body);
        }

        private static UIElement Cycle(Session session, Action<string> goTo)
        {
            var rows = Ui.Stack();

            foreach (var kind in ActivityKind.Cycle)
            {
                var latest = session.Store.Activity.Latest(kind);

                var line = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = Ui.Stack(
                    Ui.Line(ActivityKind.Title(kind), 1, true),
                    Ui.Line(latest == null
                        ? ActivityKind.Purpose(kind)
                        : latest.Summary, 0.75),
                    Ui.Line(latest == null
                        ? "Never on this project."
                        : ActivityLog.Ago(latest.WhenTicks, session.NowTicks), 0.55));

                line.Children.Add(left);

                var button = Ui.Button(GoLabel(kind), () => goTo(RouteFor(kind)));
                button.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(button, 1);
                line.Children.Add(button);

                rows.Children.Add(line);
            }

            return Ui.Card("The week",
                "Reconcile, regroup, report. Each opens where the work actually happens.", rows);
        }

        private static string RouteFor(string kind) => kind switch
        {
            ActivityKind.Reconcile => "Project/Models",
            ActivityKind.Regroup => "Coordinate/Triage",
            _ => "Coordinate/Report",
        };

        private static string GoLabel(string kind) => kind switch
        {
            ActivityKind.Reconcile => "Models",
            ActivityKind.Regroup => "Board",
            _ => "Reports",
        };

        private static UIElement Recent(Session session)
        {
            var entries = session.Store.Activity.Entries.Take(12).ToList();

            if (entries.Count == 0)
                return Ui.Card("Recent", null,
                    Ui.Empty("Nothing has happened on this project yet.",
                        "CamelWorks writes a line here every time it changes the model, produces a "
                        + "report or runs the rules — so a project handed over mid-job says what was done to it."));

            var rows = entries.Select(e => new TableRow(e,
                ActivityKind.Title(e.Kind),
                e.Summary,
                ActivityLog.Ago(e.WhenTicks, session.NowTicks))).ToList();

            return Ui.Card("Recent", null,
                Ui.Table(rows, ("What", 110), ("", 0), ("When", 110)));
        }
    }
}
