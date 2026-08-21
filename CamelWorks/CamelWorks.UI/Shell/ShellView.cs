using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using CamelWorks.UI.Views;

namespace CamelWorks.UI.Shell
{
    /// <summary>
    /// The pane's chrome: a workspace switcher, a tab strip, a search field and the content.
    ///
    /// Built in code rather than XAML because the layout is driven by <see cref="Workspaces"/> —
    /// markup would either duplicate that list or bind to it through a pile of templates, and the
    /// list is the thing three separate parts of the product have to agree on.
    ///
    /// <b>It collapses by label, never to glyphs.</b> Below about 900 device-independent pixels the
    /// switcher becomes Home plus a labelled dropdown. A row of unlabelled icons saves the same
    /// space and costs the user the ability to find anything, which is the opposite of what a
    /// product with this much surface needs.
    /// </summary>
    public sealed class ShellView : UserControl
    {
        /// <summary>Below this width the switcher collapses to a dropdown.</summary>
        public const double NarrowWidth = 900;

        private readonly string _paneId;
        private readonly StackPanel _switcher = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly ComboBox _switcherNarrow = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
        private readonly StackPanel _tabs = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly Border _tabStrip;
        private readonly TextBox _search = new TextBox { Width = 190, Padding = new Thickness(4, 2, 4, 2) };
        private readonly ListBox _searchResults = new ListBox { MaxHeight = 220, Visibility = Visibility.Collapsed };
        private readonly ContentControl _content = new ContentControl();

        private Workspace _workspace;
        private WorkspaceTab? _tab;
        private bool _narrow;

        /// <summary>Create the shell for one pane.</summary>
        /// <param name="paneId">Which pane this is, so it shows only its own workspaces.</param>
        public ShellView(string paneId)
        {
            _paneId = paneId ?? Workspaces.MainPane;
            _workspace = Workspaces.All.First(w => w.PaneId == _paneId);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            root.Children.Add(Header());

            _tabStrip = new Border
            {
                Padding = new Thickness(8, 0, 8, 0),
                Child = _tabs,
            };

            Grid.SetRow(_tabStrip, 1);
            root.Children.Add(_tabStrip);

            var body = new Grid();
            body.Children.Add(_content);
            body.Children.Add(new Border
            {
                Child = _searchResults,
                Background = SystemColors.WindowBrush,
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Width = 340,
                Margin = new Thickness(0, 0, 8, 0),
            });

            Grid.SetRow(body, 2);
            root.Children.Add(body);

            Content = root;

            _search.TextChanged += (s, e) => Filter();
            _searchResults.SelectionChanged += (s, e) => Jump();
            _switcherNarrow.SelectionChanged += (s, e) => PickNarrow();
            SizeChanged += (s, e) => Reflow(e.NewSize.Width);

            BuildSwitcher();
            Show(_workspace.Id, null);
        }

        /// <summary>Bring the shell to a workspace and tab.</summary>
        /// <param name="workspaceId">The workspace, or null to stay where it is.</param>
        /// <param name="tabId">The tab, or null for the workspace's first.</param>
        public void Show(string? workspaceId, string? tabId)
        {
            var workspace = Workspaces.Find(workspaceId);

            // A route into the other pane's workspace is ignored rather than obeyed. The ribbon
            // opens the right pane first; obeying it here as well would leave both panes showing
            // the same screen.
            if (workspace != null && workspace.PaneId == _paneId) _workspace = workspace;

            _tab = _workspace.Tabs.FirstOrDefault(
                       t => string.Equals(t.Id, tabId, StringComparison.OrdinalIgnoreCase))
                   ?? _workspace.Tabs.FirstOrDefault();

            BuildSwitcher();
            BuildTabs();
            ShowContent();
        }

        private UIElement Header()
        {
            var header = new Grid { Margin = new Thickness(8, 8, 8, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(_switcherNarrow);
            left.Children.Add(_switcher);
            header.Children.Add(left);

            // The search field is permanently visible rather than behind a button. Find a Tool is
            // the answer to a surface this size, and a search you have to find first is not one.
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(new TextBlock
            {
                Text = "Find a tool",
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7,
            });
            right.Children.Add(_search);

            Grid.SetColumn(right, 1);
            header.Children.Add(right);

            _switcherNarrow.Visibility = Visibility.Collapsed;
            return header;
        }

        private void BuildSwitcher()
        {
            _switcher.Children.Clear();
            _switcherNarrow.Items.Clear();

            foreach (var workspace in Workspaces.All.Where(w => w.PaneId == _paneId))
            {
                var current = workspace;

                var button = new ToggleButton
                {
                    Content = workspace.Title,
                    IsChecked = ReferenceEquals(workspace, _workspace),
                    Margin = new Thickness(0, 0, 4, 0),
                    Padding = new Thickness(10, 3, 10, 3),
                };

                button.Checked += (s, e) => Show(current.Id, null);
                _switcher.Children.Add(button);

                _switcherNarrow.Items.Add(workspace.Title);
                if (ReferenceEquals(workspace, _workspace)) _switcherNarrow.SelectedIndex = _switcherNarrow.Items.Count - 1;
            }
        }

        private void BuildTabs()
        {
            _tabs.Children.Clear();
            _tabStrip.Visibility = _workspace.HasTabStrip ? Visibility.Visible : Visibility.Collapsed;

            if (!_workspace.HasTabStrip) return;

            foreach (var tab in _workspace.Tabs)
            {
                var current = tab;

                var button = new ToggleButton
                {
                    Content = tab.Title,
                    IsChecked = ReferenceEquals(tab, _tab),
                    Margin = new Thickness(0, 0, 2, 0),
                    Padding = new Thickness(8, 2, 8, 2),
                };

                button.Checked += (s, e) => Show(_workspace.Id, current.Id);
                _tabs.Children.Add(button);
            }
        }

        private void ShowContent() =>
            _content.Content = TabViews.For(_workspace, _tab, Go);

        /// <summary>
        /// Navigate to a "workspace/tab" route, for the screens that offer a way onward.
        ///
        /// Home is the reason this exists: a front door whose three steps do not take you to them
        /// is a poster, not a front door.
        /// </summary>
        private void Go(string route)
        {
            var (workspace, tab) = Workspaces.Route(route);
            if (workspace != null) Show(workspace.Id, tab?.Id);
        }

        private void Reflow(double width)
        {
            var narrow = width > 0 && width < NarrowWidth;
            if (narrow == _narrow) return;

            _narrow = narrow;
            _switcher.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            _switcherNarrow.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PickNarrow()
        {
            var title = _switcherNarrow.SelectedItem as string;
            if (title == null) return;

            var workspace = Workspaces.All.FirstOrDefault(
                w => w.PaneId == _paneId && string.Equals(w.Title, title, StringComparison.Ordinal));

            if (workspace != null && !ReferenceEquals(workspace, _workspace)) Show(workspace.Id, null);
        }

        private void Filter()
        {
            var query = _search.Text;

            if (string.IsNullOrWhiteSpace(query))
            {
                _searchResults.Visibility = Visibility.Collapsed;
                return;
            }

            _searchResults.Items.Clear();

            foreach (var command in CommandCatalogue.Search(query).Take(12))
                _searchResults.Items.Add(new ListBoxItem { Content = command.Title, Tag = command });

            _searchResults.Visibility = _searchResults.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Jump()
        {
            var item = _searchResults.SelectedItem as ListBoxItem;
            var command = item?.Tag as RibbonCommand;
            if (command?.Route == null) return;

            var (workspace, tab) = Workspaces.Route(command.Route);
            if (workspace == null) return;

            _search.Text = string.Empty;
            _searchResults.Visibility = Visibility.Collapsed;

            Show(workspace.Id, tab?.Id);
        }
    }
}
