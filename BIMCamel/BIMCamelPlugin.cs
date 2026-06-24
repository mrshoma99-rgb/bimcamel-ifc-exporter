using System;
using System.IO;
using System.Reflection;
using Autodesk.Navisworks.Api.Plugins;

namespace BIMCamel
{
    /// <summary>
    /// Ribbon command that toggles the BIMCamel IFC Exporter dock pane.
    /// The XAML layout (BIMCamel.xaml) must deploy to an en-US\ subfolder next to the DLL.
    /// </summary>
    [Plugin("BIMCamel.Command", "BIMCamel",
        DisplayName = "BIMCamel IFC Exporter",
        ToolTip = "Fast Navisworks → IFC export (IFC4 / IFC2x3)")]
    [RibbonLayout("BIMCamel.xaml")]
    [RibbonTab("ID_Tab_BIMCamel")]
    [Command("ID_Button_OpenPane",
        DisplayName = "IFC exporter",
        Icon = "Resources\\export_16.png",
        LargeIcon = "Resources\\export_32.png",
        ToolTip = "Open the BIMCamel IFC exporter panel")]
    [Command("ID_Button_About",
        DisplayName = "About",
        Icon = "Resources\\camel_16.png",
        LargeIcon = "Resources\\camel_32.png",
        ToolTip = "About BIMCamel — bimcamel.com")]
    public class BIMCamelPlugin : CommandHandlerPlugin
    {
        // The dock-pane lookup key is "<pluginId>.<developerId>".
        private const string DockPaneKey = "BIMCamel.ExportDockPane.BIMCamel";

        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            AssemblyResolver.Ensure();

            try
            {
                if (commandId == "ID_Button_About") { BIMCamel.UI.AboutDialog.Show(); return 0; }

                var record = Autodesk.Navisworks.Api.Application.Plugins.FindPlugin(DockPaneKey);
                if (record == null)
                {
                    ShowError(
                        "The BIMCamel exporter panel is not registered with Navisworks " +
                        "(looked up \"" + DockPaneKey + "\" and found nothing).\n\n" +
                        "This almost always means the BIMCamel.dll in this Navisworks year folder was built " +
                        "against a different Navisworks release. A DLL built for the wrong year still loads its " +
                        "ribbon button but its dock pane silently fails to register.\n\n" +
                        "Fix: install the build that matches this Navisworks version " +
                        "(2024 = API v21, 2025 = v22, 2026 = v23, 2027 = v24).");
                    return 0;
                }

                if (!(record is DockPanePluginRecord dockRecord))
                {
                    ShowError("The BIMCamel panel registered as an unexpected plugin type: " +
                              record.GetType().Name + ".");
                    return 0;
                }

                if (!dockRecord.IsLoaded)
                    dockRecord.LoadPlugin();

                if (dockRecord.LoadedPlugin is DockPanePlugin dockPane)
                    dockPane.Visible = !dockPane.Visible;
                else
                    ShowError("The BIMCamel panel failed to load (LoadedPlugin was " +
                              (dockRecord.LoadedPlugin?.GetType().Name ?? "null") + ").");
            }
            catch (Exception ex)
            {
                ShowError("BIMCamel could not open the IFC exporter panel:\n\n" + ex);
            }

            return 0;
        }

        // Navisworks swallows exceptions thrown from a command handler, so any failure here would
        // otherwise be invisible (the journal just shows BEGIN.CMD/END.CMD with nothing happening).
        // Surface it to the user instead.
        internal static void ShowError(string message) =>
            System.Windows.Forms.MessageBox.Show(
                message, "BIMCamel IFC Exporter",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);

        public override CommandState CanExecuteCommand(string commandId) =>
            new CommandState { IsEnabled = true, IsVisible = true };
    }

    /// <summary>
    /// Resolves side-by-side dependency DLLs from the plugin folder.
    /// Mirrors the proven pattern used by the sibling NavisworksExporter.
    /// </summary>
    internal static class AssemblyResolver
    {
        private static bool _registered;

        public static void Ensure()
        {
            if (_registered) return;
            _registered = true;

            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var name = new AssemblyName(args.Name).Name + ".dll";
                var path = Path.Combine(pluginDir!, name);
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }
    }
}
