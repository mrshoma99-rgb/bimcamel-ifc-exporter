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

            if (commandId == "ID_Button_About") { BIMCamel.UI.AboutDialog.Show(); return 0; }

            var record = Autodesk.Navisworks.Api.Application.Plugins.FindPlugin(DockPaneKey);
            if (record is DockPanePluginRecord dockRecord)
            {
                if (!dockRecord.IsLoaded)
                    dockRecord.LoadPlugin();

                if (dockRecord.LoadedPlugin is DockPanePlugin dockPane)
                    dockPane.Visible = !dockPane.Visible;
            }

            return 0;
        }

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
