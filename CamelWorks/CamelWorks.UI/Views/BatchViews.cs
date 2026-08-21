using System.Linq;
using System.Windows;
using CamelWorks.Core.Project;

namespace CamelWorks.UI.Views
{
    /// <summary>The Batch workspace: saved jobs and what they did when they last ran.</summary>
    public static class BatchViews
    {
        /// <summary>Jobs and their run history.</summary>
        public static UIElement Jobs()
        {
            var session = Host.Current;

            if (session == null)
                return Ui.Scroll(Ui.Stack(Ui.Heading("Jobs"), Ui.Sub(Host.NoModel)));

            var history = session.Store.Activity.Entries
                .Where(e => e.Kind == ActivityKind.Job)
                .Take(20)
                .ToList();

            var body = Ui.Stack();

            if (history.Count == 0)
                body.Children.Add(Ui.Empty("Nothing has run yet.",
                    "A job is a saved graph plus the settings it runs with. Build one on the Automate "
                    + "canvas and it appears here."));
            else
                body.Children.Add(Ui.Table(
                    history.Select(e => new TableRow(e, e.Summary,
                        ActivityLog.Ago(e.WhenTicks, session.NowTicks), e.Detail ?? string.Empty)).ToList(),
                    ("Job", 240), ("When", 120), ("", 0)));

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Jobs"),
                Ui.Sub("What has been run on this project, and what it did."),
                Ui.Card("History", null, body)));
        }
    }
}
