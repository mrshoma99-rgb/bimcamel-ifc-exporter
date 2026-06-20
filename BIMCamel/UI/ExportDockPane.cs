using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Autodesk.Navisworks.Api.Plugins;

namespace BIMCamel.UI
{
    /// <summary>
    /// BIMCamel IFC Exporter dock pane. The pane hosts a modern WPF view (<see cref="ExporterView"/>)
    /// inside the WinForms control Navisworks expects, via <see cref="ElementHost"/>. All export /
    /// scan / profile logic lives in the view; this class is just the plug-in shell.
    /// </summary>
    [Plugin("BIMCamel.ExportDockPane", "BIMCamel", DisplayName = "BIMCamel IFC Exporter")]
    [DockPanePlugin(470, 640, FixedSize = false)]
    public class ExportDockPane : DockPanePlugin
    {
        public override Control CreateControlPane()
        {
            AssemblyResolver.Ensure();
            var host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = new ExporterView()
            };
            return host;
        }

        public override void DestroyControlPane(Control pane) => pane?.Dispose();
    }
}
