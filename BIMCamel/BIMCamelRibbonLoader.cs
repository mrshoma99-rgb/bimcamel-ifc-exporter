using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Navisworks.Api.Plugins;
using Autodesk.Windows;

namespace BIMCamel
{
    /// <summary>
    /// Builds the BIMCamel ribbon tab programmatically so it appears in every Navisworks
    /// year (2024/2025/2026). The static [RibbonLayout] attribute approach silently fails
    /// when the DLL was compiled against a different year's API than the one installed.
    /// </summary>
    [Plugin("BIMCamel.RibbonLoader", "BIMCamel", DisplayName = "BIMCamel Ribbon Loader")]
    public class BIMCamelRibbonLoader : EventWatcherPlugin
    {
        public override void OnLoaded()
        {
            Autodesk.Navisworks.Api.Application.GuiCreated += OnGuiCreated;
        }

        private void OnGuiCreated(object sender, EventArgs e)
        {
            Autodesk.Navisworks.Api.Application.GuiCreated -= OnGuiCreated;
            // NWRibbonControl is populated after GuiCreated — defer to idle
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(AddBIMCamelTab));
        }

        private static System.Windows.Media.ImageSource LoadIcon(string fileName)
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var path = Path.Combine(dir, "Resources", fileName);
            return new System.Windows.Media.Imaging.BitmapImage(new Uri(path, UriKind.Absolute));
        }

        private static void AddBIMCamelTab()
        {
            try
            {
                var roamer = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "navisworks.gui.roamer");
                if (roamer == null) return;

                var nwType = roamer.GetType("Autodesk.Navisworks.Gui.Roamer.AIRLook.NWRibbonControl");
                var instanceProp = nwType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var ribbon = instanceProp?.GetValue(null) as RibbonControl;
                if (ribbon == null) return;

                var existing = ribbon.Tabs.FirstOrDefault(t => t.Id == "ID_Tab_BIMCamel");
                if (existing != null) ribbon.Tabs.Remove(existing);

                var btnOpen = new RibbonButton
                {
                    Id             = "ID_Button_OpenPane",
                    Text           = "IFC Exporter",
                    ShowText       = true,
                    Size           = RibbonItemSize.Large,
                    Orientation    = Orientation.Vertical,
                    IsEnabled      = true,
                    LargeImage     = LoadIcon("export_32.png"),
                    ToolTip        = new RibbonToolTip
                    {
                        Title   = "IFC Exporter",
                        Content = "Open the BIMCamel IFC exporter panel",
                    },
                    CommandHandler = new RibbonRelayCommand(ToggleDockPane),
                };

                var btnAbout = new RibbonButton
                {
                    Id             = "ID_Button_About",
                    Text           = "About",
                    ShowText       = true,
                    Size           = RibbonItemSize.Large,
                    Orientation    = Orientation.Vertical,
                    IsEnabled      = true,
                    LargeImage     = LoadIcon("camel_32.png"),
                    ToolTip        = new RibbonToolTip
                    {
                        Title   = "About BIMCamel",
                        Content = "About BIMCamel — bimcamel.com",
                    },
                    CommandHandler = new RibbonRelayCommand(UI.AboutDialog.Show),
                };

                var panelSource = new RibbonPanelSource
                {
                    Id    = "BIMCamel_Panel",
                    Title = "IFC Export",
                };
                panelSource.Items.Add(btnOpen);
                panelSource.Items.Add(btnAbout);

                var panel = new RibbonPanel { Source = panelSource };

                var tab = new RibbonTab
                {
                    Id        = "ID_Tab_BIMCamel",
                    Title     = "BIMCamel",
                    IsVisible = true,
                };
                tab.Panels.Add(panel);

                ribbon.Tabs.Add(tab);
            }
            catch { /* never crash Navisworks startup */ }
        }

        private static void ToggleDockPane()
        {
            var record = Autodesk.Navisworks.Api.Application.Plugins
                .FindPlugin("BIMCamel.ExportDockPane.BIMCamel");

            if (record is DockPanePluginRecord dockRecord)
            {
                if (!dockRecord.IsLoaded)
                    dockRecord.LoadPlugin();

                if (dockRecord.LoadedPlugin is DockPanePlugin dockPane)
                    dockPane.Visible = !dockPane.Visible;
            }
        }

        public override void OnUnloading() { }
    }

    internal sealed class RibbonRelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        internal RibbonRelayCommand(Action execute) { _execute = execute; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) { _execute(); }
    }
}
