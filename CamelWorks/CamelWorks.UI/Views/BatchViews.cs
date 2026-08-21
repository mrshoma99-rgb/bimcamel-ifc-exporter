using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CamelWorks.Core.Automation;
using CamelWorks.Core.Project;
using CamelWorks.Core.Store;

namespace CamelWorks.UI.Views
{
    /// <summary>The Batch workspace: saved jobs, run without opening the canvas, and what they did.</summary>
    public static class BatchViews
    {
        /// <summary>Jobs and their run history.</summary>
        public static UIElement Jobs()
        {
            var session = Host.Current;

            if (session == null)
                return Ui.Scroll(Ui.Stack(Ui.Heading("Jobs"), Ui.Sub(Host.NoModel)));

            var log = Ui.Stack();
            var graphs = Saved(session);
            Graph? picked = graphs.FirstOrDefault();

            var body = Ui.Stack();

            if (graphs.Count == 0)
            {
                body.Children.Add(Ui.Empty("No jobs saved on this project.",
                    "A job is a graph and the settings it runs with. Build one on the Automate canvas and "
                    + "press Save job; it goes in the project file, so whoever opens the model next has it too."));
            }
            else
            {
                var rows = graphs.Select(g => new TableRow(g,
                    g.Name,
                    Ui.Count(g.Nodes.Count, "node"),
                    string.Join(", ", g.Nodes.Select(n => n.Definition?.Title ?? n.Kind).Distinct().Take(4)))).ToList();

                body.Children.Add(Ui.Table(rows, ("Job", 170), ("Size", 90), ("What it does", 0))
                    .OnPick(item => picked = item as Graph));

                body.Children.Add(Ui.Runner("Run the selected job", log, job =>
                {
                    if (picked == null)
                    {
                        log.Children.Add(Ui.Line("Pick a job first.", 0.8));
                        return;
                    }

                    var result = GraphRunner.Run(picked, new NavGraphHost(session), job.Say);

                    log.Children.Add(Ui.Line(picked.Name + " — " + result, 1, true));

                    foreach (var problem in result.Problems) log.Children.Add(Ui.Problem(problem));

                    foreach (var step in result.Steps)
                        log.Children.Add(Ui.Line((step.Ran ? "ran  " : "skip ") + step.Title + " — " + step.Outcome,
                            step.Ran ? 0.85 : 0.6));

                    session.Record(ActivityKind.Job, "ran \"" + picked.Name + "\": " + result);
                }));

                body.Children.Add(Ui.Note("A job runs on the model that is open now, using whatever is selected "
                    + "now. Nothing in it is scheduled or unattended: Navisworks has to be running, and so do you."));
            }

            var history = session.Store.Activity.Entries
                .Where(e => e.Kind == ActivityKind.Job)
                .Take(20)
                .ToList();

            var past = history.Count == 0
                ? (UIElement)Ui.Empty("Nothing has run yet.", "The result of every run is kept here.")
                : Ui.Table(history.Select(e => new TableRow(e,
                        e.Summary,
                        ActivityLog.Ago(e.WhenTicks, session.NowTicks),
                        e.Detail ?? string.Empty)).ToList(),
                    ("Run", 300), ("When", 120), ("", 0));

            return Ui.Scroll(Ui.Stack(
                Ui.Heading("Jobs"),
                Ui.Sub("Saved graphs, run without opening the canvas."),
                Ui.Card("Jobs", null, body),
                log,
                Ui.Card("History", null, past)));
        }

        private static IReadOnlyList<Graph> Saved(Session session)
        {
            var section = session.Store.Section(ProjectStore.JobsSection);
            if (section["items"].Kind != JsonKind.Array) return Array.Empty<Graph>();

            return section["items"].Items.Select(Graph.FromJson).ToList();
        }
    }
}
