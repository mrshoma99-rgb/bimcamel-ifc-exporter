using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CamelWorks.Nav;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// Help: what this is, what it will not do, and what state it is in right now.
    ///
    /// A window rather than a tab, and the one modal in the product that is not a file dialog. It
    /// has to work when the panel does not — "the pane will not open" is exactly when somebody
    /// needs the diagnostics — so it depends on as little as possible and shows what it can.
    ///
    /// <b>The limitations section is not an apology, it is documentation.</b> Every line in it is a
    /// thing that surprises somebody once and then costs them an afternoon: Ctrl+Z not undoing a
    /// property write, clash tools missing on Simulate, the temporary override layer being
    /// unreadable. Stating them where they can be found beats explaining them one support email at
    /// a time.
    /// </summary>
    public static class HelpWindow
    {
        /// <summary>Show the window.</summary>
        public static void Show()
        {
            var body = Ui.Stack(
                Ui.Card("CamelWorks",
                    "Coordination, data and delivery tools for Navisworks, from bimcamel.com.",
                    Ui.Line("Free to use, including commercially. Not free to resell: the source is "
                            + "published under Apache 2.0 with the Commons Clause, which permits everything "
                            + "except selling the software itself.", 0.85)),

                Ui.Card("How it is meant to be used", null,
                    Ui.Line("Open a model and press a button. Nothing has to be configured first — every "
                            + "screen derives what it needs and tells you what it derived, and Setup exists "
                            + "to correct a guess rather than to be filled in before you start.", 0.85),
                    Ui.Line("The weekly cycle is the front door: bring in the new models, run the rules "
                            + "over what that broke, send out the result.", 0.85),
                    Ui.Line("Find a tool, top right of the panel, searches what things do as well as what "
                            + "they are called. \"Why is this thing hidden\" finds Appearance.", 0.85)),

                Ui.Card("What it will not do",
                    "Each of these is a real limit, not a missing feature list.",
                    Bullet("Ctrl+Z does not undo a property write. Navisworks does not put custom property "
                           + "writes on its undo stack. CamelWorks records what it changed in the project "
                           + "file instead of pretending otherwise."),
                    Bullet("Clash tools need Navisworks Manage. Simulate has no clash engine, so the board, "
                           + "the clash rules and BCF-from-clashes are unavailable there. Everything else works."),
                    Bullet("The temporary override layer cannot be read back. Navisworks offers no way to ask "
                           + "what is on it, so the Appearance stack works on the permanent layer and says so "
                           + "rather than quietly reporting half the truth."),
                    Bullet("Nothing runs unattended. A job runs when you run it, in the Navisworks you have "
                           + "open. There is no scheduler and no headless mode."),
                    Bullet("A set built from a rule that needs more than one search is saved as a fixed "
                           + "selection set, because a Navisworks search set holds one search. CamelWorks says "
                           + "which you got rather than letting you find out later."),
                    Bullet("The buttons have no icons yet. Twenty-five copies of one placeholder glyph would "
                           + "be worse than none.")),

                Diagnostics());

            var window = new Window
            {
                Title = "CamelWorks",
                Width = 720,
                Height = 640,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(14),
                    Content = body,
                },
            };

            window.ShowDialog();
        }

        private static UIElement Bullet(string text) => Ui.Line("- " + text, 0.85);

        private static UIElement Diagnostics()
        {
            var rows = Ui.Stack();

            void Say(string label, string value) => rows.Children.Add(Ui.Field(label, Ui.Line(value, 0.85)));

            var assembly = Assembly.GetExecutingAssembly();

            Say("Build", assembly.GetName().Version?.ToString() ?? "unknown");
            Say("Installed in", Folder(assembly));
            Say("Navisworks", NavisworksVersion());

            var session = Host.Current;

            if (session == null)
            {
                Say("Document", "nothing open");
                return Ui.Card("Diagnostics", "What to quote in a bug report.", rows);
            }

            Say("Document", session.SavedPath ?? "never saved");
            Say("Models", session.Model.Models.Count.ToString(CultureInfo.InvariantCulture));
            Say("Units", session.Document.Units + " (1 unit = "
                         + NavUnits.MetresPerUnit(session.Document.Units)
                             .ToString("0.######", CultureInfo.InvariantCulture) + " m)");

            Say("Project file", session.Store.Path ?? "memory only");
            Say("Stored on", session.Store.Substrate.ToString());
            Say("Project file state", session.Store.Outcome.ToString().ToLowerInvariant()
                                      + (session.Store.UsedBackup ? ", recovered from backup" : string.Empty));

            Say("Clash engine", session.Clash.IsAvailable
                ? session.Clash.Tests().Count.ToString(CultureInfo.InvariantCulture) + " tests"
                : session.ClashProblem ?? "unavailable");

            return Ui.Card("Diagnostics", "What to quote in a bug report.", rows);
        }

        private static string Folder(Assembly assembly)
        {
            try
            {
                return System.IO.Path.GetDirectoryName(assembly.Location) ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static string NavisworksVersion()
        {
            try
            {
                var api = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Autodesk.Navisworks.Api");

                var version = api?.GetName().Version;

                // The API's major version is not the year: 21 is 2024, and each year since adds one.
                // Worth translating, because the number people need in a bug report is the year.
                return version == null
                    ? "unknown"
                    : "API v" + version.Major.ToString(CultureInfo.InvariantCulture)
                      + " (" + (version.Major + 2003).ToString(CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
