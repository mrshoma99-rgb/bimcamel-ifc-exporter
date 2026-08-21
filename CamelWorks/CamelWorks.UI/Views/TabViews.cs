using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Data;
using CamelWorks.Nav;
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
    /// </summary>
    public static class TabViews
    {
        /// <summary>The view for a workspace and tab.</summary>
        public static UIElement For(Workspace workspace, WorkspaceTab? tab)
        {
            if (tab == null) return Frame(workspace.Title, "Nothing to show.", null);

            switch (workspace.Id + "/" + tab.Id)
            {
                case "Project/Health": return Health(tab);
                case "Data/Takeoff": return Takeoff(tab);
                case "Data/Zones": return Zones(tab);
                default: return Frame(tab.Title, tab.Summary, null);
            }
        }

        // -----------------------------------------------------------------------------------
        // Wired to the services that are finished
        // -----------------------------------------------------------------------------------

        private static UIElement Health(WorkspaceTab tab)
        {
            var results = new StackPanel();

            return Frame(tab.Title, tab.Summary, Run("Check this model", results, () =>
            {
                var elements = Elements().Select(item => new HealthElement(item.DisplayName, item.Model.DisplayName)
                {
                    Name = item.DisplayName,
                    Category = item.Category,
                    X = item.Bounds.CentreX, Y = item.Bounds.CentreY, Z = item.Bounds.CentreZ,
                    SizeX = item.Bounds.SizeX, SizeY = item.Bounds.SizeY, SizeZ = item.Bounds.SizeZ,
                    PropertyCount = item.Properties().Any() ? 1 : 0,   // only the zero case is asked about
                }).ToList();

                var report = ModelHealth.Check(elements);

                results.Children.Add(Line(report.ToString(), 1, true));

                if (report.IsClean)
                {
                    results.Children.Add(Line("Nothing found. That is a real result, not an empty screen.", 0.7));
                    return;
                }

                foreach (var finding in report.Findings)
                {
                    results.Children.Add(Line(finding.Summary, 1, true));
                    results.Children.Add(Line(finding.Fix, 0.75));
                    results.Children.Add(Line("e.g. " + string.Join(", ", finding.Examples), 0.55));
                }
            }), results);
        }

        private static UIElement Takeoff(WorkspaceTab tab)
        {
            var results = new StackPanel();

            return Frame(tab.Title, tab.Summary, Run("Sum this model", results, () =>
            {
                // Grouped by category and measured on whatever numeric property the elements carry.
                // No mapping to set up first: the point is that it produces a number on a raw model,
                // and says what it could not read.
                var lines = new List<TakeoffLine>();

                foreach (var item in Elements())
                {
                    var value = item.Property("Item", "Volume")
                                ?? item.Property("Element", "Volume")
                                ?? item.Property("Item", "Area")
                                ?? item.Property("Element", "Area");

                    lines.Add(new TakeoffLine(item.DisplayName, item.Category ?? "(no category)", value));
                }

                var result = Core.Data.Takeoff.Sum(lines);
                results.Children.Add(Line(result.ToString(), 1, true));

                foreach (var group in result.Groups.OrderByDescending(g => g.Count).Take(40))
                {
                    results.Children.Add(Line(group.ToString()));

                    if (group.UnreadableExamples.Count > 0)
                        results.Children.Add(Line("could not read: "
                            + string.Join(", ", group.UnreadableExamples), 0.55));
                }
            }), results);
        }

        private static UIElement Zones(WorkspaceTab tab)
        {
            var results = new StackPanel();

            return Frame(tab.Title, tab.Summary, Run("Derive levels", results, () =>
            {
                var elevations = Elements().Where(e => e.HasGeometry).Select(e => e.Bounds.MinZ).ToList();
                var levels = LevelSet.Derive(elevations);

                results.Children.Add(Line(levels.ToString(), 1, true));

                foreach (var band in levels.Bands)
                    results.Children.Add(Line(band.Name
                        + (band.Support > 0
                            ? "   " + band.Support.ToString("N0", CultureInfo.InvariantCulture) + " elements"
                            : string.Empty)));

                if (levels.Bands.Count == 0)
                    results.Children.Add(Line("No geometry to infer levels from.", 0.7));
            }), results);
        }

        // -----------------------------------------------------------------------------------

        private static IEnumerable<IModelItem> Elements()
        {
            var document = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (document == null || document.IsClear) return Enumerable.Empty<IModelItem>();

            return new NavDocument(document).Traverse(TraversalScope.WholeDocument);
        }

        private static Button Run(string caption, Panel results, Action work)
        {
            var button = new Button
            {
                Content = caption,
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 8),
            };

            button.Click += (s, e) =>
            {
                results.Children.Clear();
                button.IsEnabled = false;

                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    // Shown in the pane rather than thrown at the host. A plug-in exception that
                    // escapes takes the message loop with it in some versions, and the user would
                    // see a crash rather than a sentence about what failed.
                    results.Children.Clear();
                    results.Children.Add(Line("This could not run: " + ex.Message, 0.8));
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };

            return button;
        }

        private static UIElement Frame(string title, string summary, UIElement? action, UIElement? results = null)
        {
            var panel = new StackPanel { Margin = new Thickness(10) };

            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });

            panel.Children.Add(new TextBlock
            {
                Text = summary,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });

            if (action != null) panel.Children.Add(action);

            if (results != null)
            {
                panel.Children.Add(results);
            }
            else if (action == null)
            {
                // Said plainly. A screen that shows only a title reads as broken; one that says it
                // is not connected yet reads as unfinished, which is the truth.
                panel.Children.Add(new TextBlock
                {
                    Text = "Not connected in this build yet.",
                    Margin = new Thickness(0, 10, 0, 0),
                    Opacity = 0.55,
                });
            }

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            };
        }

        private static TextBlock Line(string text, double opacity = 1, bool bold = false) =>
            new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Opacity = opacity,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, bold ? 8 : 1, 0, 0),
            };
    }
}
