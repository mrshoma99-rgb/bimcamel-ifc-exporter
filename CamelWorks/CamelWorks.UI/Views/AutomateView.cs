using System.Windows;

namespace CamelWorks.UI.Views
{
    /// <summary>The graph canvas.</summary>
    public static class AutomateView
    {
        /// <summary>Build the canvas.</summary>
        public static UIElement Build() =>
            Ui.Scroll(Ui.Stack(
                Ui.Heading("Canvas"),
                Ui.Sub("Wire the same services the ribbon drives into a graph, and run it as a job."),
                Ui.Note("The canvas is not connected in this build yet.")));
    }
}
