using System;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;
using CamelWorks.UI.Shell;
using CamelWorks.UI.Views;

namespace CamelWorks.UI
{
    /// <summary>
    /// The CamelWorks ribbon tab and its twenty-five commands.
    ///
    /// Every button either opens the pane on a named workspace and tab, acts immediately, or opens
    /// a small menu. <b>No button opens a modal that duplicates a pane screen.</b> True modals are
    /// reserved for terminal file operations that need a save path and nothing else, because a
    /// modal over a 3D view hides the very thing the user is deciding about.
    ///
    /// The command ids here must match CamelWorks.xaml exactly. They also appear in
    /// <see cref="CommandCatalogue"/>, which is the one place the routing, the tooltips and the
    /// Find a Tool index come from — a test asserts the two lists agree, because a command present
    /// in one and missing from the other is invisible in exactly one place.
    ///
    /// <b>No icons yet.</b> The buttons render as text, which is legitimate and honest; a ribbon
    /// where twenty-five buttons share one placeholder glyph is worse than one with none, and the
    /// icons are a design deliverable rather than something to invent here.
    /// </summary>
    [Plugin("CamelWorks.Command", "CamelWorks",
        DisplayName = "CamelWorks",
        ToolTip = "Coordination, data and delivery tools for Navisworks — bimcamel.com")]
    [RibbonLayout("CamelWorks.xaml")]
    [RibbonTab("ID_Tab_CamelWorks")]
    [Command("ID_CW_HealthCheck", DisplayName = "Health Check", ToolTip = "One scorecard over models, sets and data")]
    [Command("ID_CW_ProjectSetup", DisplayName = "Project Setup", ToolTip = "What CamelWorks already derived, with an override on each line")]
    [Command("ID_CW_FixLinks", DisplayName = "Fix Broken Links", ToolTip = "Repoint broken paths and find missing sources")]
    [Command("ID_CW_ProjectProfile", DisplayName = "Project Profile", ToolTip = "Save, load, or start from a template pack")]
    [Command("ID_CW_Triage", DisplayName = "Clash Triage", ToolTip = "The board — opens populated and grouped")]
    [Command("ID_CW_Review", DisplayName = "Review", ToolTip = "Review mode over the board's current filter")]
    [Command("ID_CW_ClashTests", DisplayName = "Clash Tests", ToolTip = "The matrix builder and Run")]
    [Command("ID_CW_ClashRules", DisplayName = "Clash Rules", ToolTip = "Suppress and flag, then group, then assign")]
    [Command("ID_CW_Headroom", DisplayName = "Headroom", ToolTip = "Floor set against target set, onto the board")]
    [Command("ID_CW_ClashReport", DisplayName = "Clash Report", ToolTip = "PDF, XLSX or HTML from a built-in template")]
    [Command("ID_CW_Bcf", DisplayName = "BCF", ToolTip = "Export and import, BCF 2.1 and 3.0")]
    [Command("ID_CW_Merge", DisplayName = "Merge", ToolTip = "One conflict preview for everything inbound")]
    [Command("ID_CW_DataManager", DisplayName = "Data Manager", ToolTip = "Browse and edit properties in one place")]
    [Command("ID_CW_Excel", DisplayName = "Excel", ToolTip = "Properties out, properties back in, or import a sheet")]
    [Command("ID_CW_Zones", DisplayName = "Levels & Zones", ToolTip = "Derive levels from whatever the model has")]
    [Command("ID_CW_Takeoff", DisplayName = "Takeoff", ToolTip = "Sum the numeric properties it finds, grouped")]
    [Command("ID_CW_Sets", DisplayName = "Sets", ToolTip = "Boolean set building, compiled to native search sets")]
    [Command("ID_CW_Appearance", DisplayName = "Appearance", ToolTip = "The layers system — what is hidden and overridden, and why")]
    [Command("ID_CW_Viewpoints", DisplayName = "Viewpoints", ToolTip = "Bulk rename, renumber, re-folder and render")]
    [Command("ID_CW_SectionBox", DisplayName = "Section Box", ToolTip = "Box the selection, a clash or a group — and toggle without losing it")]
    [Command("ID_CW_Batch", DisplayName = "Batch", ToolTip = "Jobs and their run history")]
    [Command("ID_CW_Graph", DisplayName = "Graph Editor", ToolTip = "The graph canvas")]
    [Command("ID_CW_ExportIfc", DisplayName = "Export IFC", ToolTip = "The IFC exporter, full pane")]
    [Command("ID_CW_Export", DisplayName = "Export", ToolTip = "glTF, GLB, CSV or XLSX")]
    [Command("ID_CW_Help", DisplayName = "Help", ToolTip = "Guide, limitations, shortcuts, diagnostics and about")]
    public class CamelWorksPlugin : CommandHandlerPlugin
    {
        /// <summary>
        /// Create the handler.
        ///
        /// Registers the side-by-side assembly resolver — see <see cref="CamelWorksPaneBase"/> for
        /// why both entry points do it.
        /// </summary>
        public CamelWorksPlugin() => Host.Ensure();

        /// <summary>The dock-pane lookup key: "&lt;pluginId&gt;.&lt;developerId&gt;".</summary>
        public const string MainPaneKey = "CamelWorks.Pane.CamelWorks";

        /// <summary>The graph canvas pane's lookup key.</summary>
        public const string AutomatePaneKey = "CamelWorks.AutomatePane.CamelWorks";

        /// <inheritdoc />
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            var command = CommandCatalogue.Find(commandId);

            if (command == null)
            {
                // A button in the XAML with no entry in the catalogue. Named rather than ignored:
                // silently doing nothing is the one behaviour a user cannot report usefully.
                Warn("CamelWorks has no handler for the command \"" + commandId + "\".\n\n"
                     + "This means the ribbon layout and the command catalogue have got out of step, "
                     + "which is a bug in this build rather than anything you did.");
                return 0;
            }

            try
            {
                switch (command.Kind)
                {
                    case CommandKind.Pane:
                        OpenPane(command.Route);
                        break;

                    case CommandKind.Menu:
                    case CommandKind.Dialog:
                    case CommandKind.Act:
                        NotYetWired(command);
                        break;
                }
            }
            catch (Exception e)
            {
                // A plug-in command that throws takes the host's message loop with it in some
                // versions. Whatever went wrong, it stops here and is shown as text.
                Warn(command.Title + " could not run.\n\n" + e.Message);
            }

            return 0;
        }

        /// <summary>
        /// Whether a command is available right now.
        ///
        /// Every one of these needs a model, so with nothing open they are shown but disabled —
        /// visible, so the tab does not look empty and the user can see what the product offers,
        /// and disabled, because a button that looks available and then says "open a model first"
        /// has wasted the click and taught nothing.
        /// </summary>
        /// <param name="commandId">The command being asked about.</param>
        public override CommandState CanExecuteCommand(string commandId)
        {
            var document = Autodesk.Navisworks.Api.Application.ActiveDocument;
            var ready = document != null && !document.IsClear;

            return new CommandState(ready);
        }

        private void OpenPane(string? route)
        {
            var (workspace, tab) = Workspaces.Route(route);
            if (workspace == null) return;

            var key = workspace.PaneId == Workspaces.AutomatePane ? AutomatePaneKey : MainPaneKey;

            var record = Autodesk.Navisworks.Api.Application.Plugins.FindPlugin(key);

            if (record == null)
            {
                // Nearly always a DLL built against a different Navisworks year: it loads, its
                // ribbon appears, and its dock pane silently fails to register. Worth saying in
                // full, because the symptom points nowhere near the cause.
                Warn("The CamelWorks panel is not registered with Navisworks (looked up \"" + key
                     + "\" and found nothing).\n\n"
                     + "This almost always means the CamelWorks.UI.dll in this Navisworks year folder was "
                     + "built against a different release. A DLL built for the wrong year still shows its "
                     + "ribbon buttons but its dock pane does not register.\n\n"
                     + "Fix: install the build matching this Navisworks version "
                     + "(2024 = API v21, 2025 = v22, 2026 = v23, 2027 = v24).");
                return;
            }

            if (!(record is DockPanePluginRecord dockRecord))
            {
                Warn("The CamelWorks panel registered as an unexpected plugin type: " + record.GetType().Name + ".");
                return;
            }

            if (!dockRecord.IsLoaded) dockRecord.LoadPlugin();

            if (dockRecord.LoadedPlugin is CamelWorksPaneBase pane)
            {
                pane.Visible = true;
                pane.Show(workspace.Id, tab?.Id);
            }
        }

        private void NotYetWired(RibbonCommand command)
        {
            // Stated plainly. A button that does nothing at all reads as a broken product; a button
            // that says what it will do and that it is not connected yet reads as an unfinished one,
            // which is the truth.
            Warn(command.Title + " is not connected in this build yet.\n\n" + command.Tooltip);
        }

        private static void Warn(string message) =>
            MessageBox.Show(message, "CamelWorks", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
