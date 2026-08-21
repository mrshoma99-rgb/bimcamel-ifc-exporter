using System;
using System.Windows;
using CamelWorks.UI.Shell;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// Builds the content for one tab.
    ///
    /// <b>Nothing runs when a tab is opened.</b> Every screen here can end up walking a federation
    /// of a few hundred thousand elements, and doing that because somebody clicked a tab freezes
    /// the pane with no warning and no way to stop it. Each tab shows what it will do and a button
    /// to do it — which is also what makes the zero-setup rule honest, since the button works on a
    /// raw model with nothing configured.
    ///
    /// This class is only the switchboard. The screens themselves live one file per workspace, so
    /// that adding a tab does not mean editing a thousand-line file every other screen also lives
    /// in.
    /// </summary>
    public static class TabViews
    {
        /// <summary>
        /// The view for a workspace and tab.
        /// </summary>
        /// <param name="workspace">Which workspace.</param>
        /// <param name="tab">Which tab, or null when the workspace has only one screen.</param>
        /// <param name="goTo">Navigate the pane to a "workspace/tab" route.</param>
        public static UIElement For(Workspace workspace, WorkspaceTab? tab, Action<string> goTo)
        {
            var route = workspace.Id + "/" + (tab?.Id ?? string.Empty);

            try
            {
                switch (route)
                {
                    case "Home/This week": return HomeView.Build(goTo);

                    case "Project/Health": return ProjectViews.Health();
                    case "Project/Setup": return ProjectViews.Setup();
                    case "Project/Models": return ProjectViews.Models();
                    case "Project/Export": return ProjectViews.ExportIfc();

                    case "Coordinate/Triage": return CoordinateViews.Triage();
                    case "Coordinate/Tests": return CoordinateViews.Tests();
                    case "Coordinate/Rules": return CoordinateViews.Rules();
                    case "Coordinate/Report": return CoordinateViews.Report();
                    case "Coordinate/BCF": return CoordinateViews.Bcf();

                    case "Data/Data": return DataViews.Manager();
                    case "Data/Zones": return DataViews.Zones();
                    case "Data/Takeoff": return DataViews.Takeoff();

                    case "Sets/Sets": return SetsViews.Builder();
                    case "Sets/Appearance": return SetsViews.Appearance();
                    case "Sets/Viewpoints": return SetsViews.Viewpoints();

                    case "Batch/Jobs": return BatchViews.Jobs();
                    case "Automate/Canvas": return AutomateView.Build();

                    default:
                        return Ui.Scroll(Ui.Stack(
                            Ui.Heading(tab?.Title ?? workspace.Title),
                            Ui.Sub(tab?.Summary ?? string.Empty)));
                }
            }
            catch (Exception e)
            {
                // A screen that throws while being built would otherwise take the pane with it and
                // leave the user with an empty panel and no idea which tab did it.
                return Ui.Scroll(Ui.Stack(
                    Ui.Heading(tab?.Title ?? workspace.Title),
                    Ui.Problem("This screen could not be built: " + Ui.Describe(e))));
            }
        }
    }
}
