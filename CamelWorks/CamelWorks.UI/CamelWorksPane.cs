using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Autodesk.Navisworks.Api.Plugins;
using CamelWorks.UI.Shell;

namespace CamelWorks.UI
{
    /// <summary>
    /// The shared behaviour of both CamelWorks panes.
    ///
    /// A <c>DockPanePlugin</c> first appears floating, and the API exposes no initial dock edge —
    /// so CamelWorks declares the proportions its screens need and otherwise <b>never resizes,
    /// closes, undocks or re-tabs a pane it did not create</b>. Moving somebody's panel layout
    /// around because a button was pressed is the fastest way to make a tool feel hostile.
    /// </summary>
    public abstract class CamelWorksPaneBase : DockPanePlugin
    {
        private ElementHost? _host;
        private ShellView? _shell;

        /// <summary>Which workspace this pane shows.</summary>
        protected abstract string PaneId { get; }

        /// <summary>Bring the pane to a workspace and tab.</summary>
        /// <param name="workspaceId">The workspace.</param>
        /// <param name="tabId">The tab, or null for the workspace's first.</param>
        public void Show(string? workspaceId, string? tabId)
        {
            _shell?.Show(workspaceId, tabId);
        }

        /// <inheritdoc />
        public override Control CreateControlPane()
        {
            _shell = new ShellView(PaneId);

            // WPF content inside the host's WinForms pane. ElementHost is the supported bridge and
            // the one the existing exporter pane already uses in this product.
            _host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = _shell,
            };

            _host.CreateControl();
            return _host;
        }

        /// <inheritdoc />
        public override void DestroyControlPane(Control pane)
        {
            _shell = null;

            if (_host == null) return;

            _host.Child = null;
            _host.Dispose();
            _host = null;
        }
    }

    /// <summary>The main pane: Home and five workspaces.</summary>
    [Plugin("CamelWorks.Pane", "CamelWorks",
        DisplayName = "CamelWorks",
        ToolTip = "Coordination, data and delivery tools")]
    [DockPanePlugin(470, 640,
        AutoScroll = false,
        MinimumHeight = 340,
        MinimumWidth = 470,
        FixedSize = false)]
    public class CamelWorksPane : CamelWorksPaneBase
    {
        /// <inheritdoc />
        protected override string PaneId => Workspaces.MainPane;
    }

    /// <summary>
    /// The graph canvas, in its own pane.
    ///
    /// Separate rather than a seventh tab because the canvas needs the full width, and a pane
    /// sized for a board is the wrong shape for it. Two panes also lets somebody dock the canvas
    /// on one screen and the board on another, which is how the two are actually used together.
    /// </summary>
    [Plugin("CamelWorks.AutomatePane", "CamelWorks",
        DisplayName = "CamelWorks Automate",
        ToolTip = "The graph canvas")]
    [DockPanePlugin(900, 560,
        AutoScroll = false,
        MinimumHeight = 340,
        MinimumWidth = 640,
        FixedSize = false)]
    public class CamelWorksAutomatePane : CamelWorksPaneBase
    {
        /// <inheritdoc />
        protected override string PaneId => Workspaces.AutomatePane;
    }
}
