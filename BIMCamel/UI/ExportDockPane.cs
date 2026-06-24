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
            try
            {
                var host = new ElementHost
                {
                    Dock = DockStyle.Fill,
                    Child = new ExporterView()
                };
                return host;
            }
            catch (System.Exception ex)
            {
                // Navisworks swallows exceptions thrown from CreateControlPane: the pane just never
                // appears and the user is left clicking the ribbon button with no feedback. Show the
                // real reason both in the pane and as a dialog so the failure is diagnosable.
                BIMCamelPlugin.ShowError("BIMCamel could not build the exporter panel:\n\n" + ex);
                return new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Text = "BIMCamel failed to load the exporter panel.\r\n\r\n" + ex
                };
            }
        }

        public override void DestroyControlPane(Control pane) => pane?.Dispose();
    }
}
