using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UiSnapshot
{
    public sealed class Item
    {
        public string Name { get; set; } = "";
        public bool Checked { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>One object with every property the three grids bind to.</summary>
    public sealed class Row
    {
        public ObservableCollection<string> Sets { get; } = new ObservableCollection<string> { "Walls - External", "Doors" };
        public ObservableCollection<string> Classes { get; } = new ObservableCollection<string> { "Wall", "Door", "Slab" };
        public ObservableCollection<string> Categories { get; } = new ObservableCollection<string> { "(any category)", "Element", "Item" };
        public ObservableCollection<string> AvailableProps { get; } = new ObservableCollection<string> { "Category", "Type", "Mark" };
        public ObservableCollection<string> Psets { get; } = new ObservableCollection<string> { "Pset_WallCommon" };
        public ObservableCollection<string> ParamNames { get; } = new ObservableCollection<string> { "FireRating" };
        public ObservableCollection<string> PredefinedOptions { get; } = new ObservableCollection<string> { "STANDARD", "NOTDEFINED" };
        public string[] Tests { get; } = { "equals", "contains", "starts with" };
        public string Set { get; set; } = "Walls - External";
        public string IfcClass { get; set; } = "Wall";
        public string Predefined { get; set; } = "STANDARD";
        public string Classification { get; set; } = "EF_25_10";
        public string SourceCategory { get; set; } = "Element";
        public string SourceProperty { get; set; } = "Fire Rating";
        public string TargetPset { get; set; } = "Pset_WallCommon";
        public string TargetName { get; set; } = "FireRating";
        public string Category { get; set; } = "Element";
        public string Property { get; set; } = "Category";
        public string Test { get; set; } = "equals";
        public string Value { get; set; } = "Walls";
        public int Number { get; set; } = 1;
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string xamlPath = args.Length > 0 ? args[0] : "ExporterView.xaml";
            string outDir = args.Length > 1 ? args[1] : "snapshots";
            Directory.CreateDirectory(outDir);

            var xaml = File.ReadAllText(xamlPath);
            xaml = Regex.Replace(xaml, @"\s+x:Class=""[^""]*""", "");
            xaml = Regex.Replace(xaml, @"\s+(Click|TextChanged|Checked|Unchecked|SelectionChanged|Loaded)=""On[A-Za-z]*""", "");
            var view = (UserControl)XamlReader.Parse(xaml);

            Fill(view);

            foreach (int width in new[] { 480, 360 })
            {
                var tabs = (TabControl)view.FindName("Tabs");
                for (int i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    var name = ((TabItem)tabs.Items[i]).Name;
                    Render(view, width, Path.Combine(outDir, $"{width}-{i}-{name}.png"));
                }
                tabs.SelectedIndex = 0;
            }
            // What a user sees on opening the pane: a fixed viewport with the footer.
            Render(view, 480, Path.Combine(outDir, "viewport-480x820.png"), 820);
            // The batch scope, with its set list and output folder.
            ShowBatch(view);
            Render(view, 480, Path.Combine(outDir, "480-export-batch.png"));
            try { RenderTokenMenu(view, Path.Combine(outDir, "token-menu.png")); }
            catch (Exception ex) { Console.WriteLine("token menu: " + ex); }
            Console.WriteLine("Wrote " + Directory.GetFiles(outDir, "*.png").Length + " snapshots to " + Path.GetFullPath(outDir));
            return 0;
        }

        private static T F<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T;

        private static void Fill(UserControl v)
        {
            if (F<Image>(v, "LogoMark") is Image logo)
            {
                var png = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mark_white.png");
                if (File.Exists(png)) logo.Source = new BitmapImage(new Uri(png));
            }
            F<TextBlock>(v, "LblSubtitle").Text = "v0.12.5 · Coordination";

            // File name editor, as ExporterView.FileName.cs builds it: Model file _ Schema _ Date.
            var chips = F<WrapPanel>(v, "NameChips");
            void Chip(string label, bool live = true)
            {
                var b = new Border { Style = (Style)v.FindResource(live ? "NameChip" : "NameChipOff") };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
                foreach (var g in new[] { "‹", "›", "×" }) sp.Children.Add(new Button { Style = (Style)v.FindResource("ChipCtl"), Content = g });
                b.Child = sp; chips.Children.Add(b);
            }
            void Text(string t, double min = 0)
            {
                var tb = new TextBox { Style = (Style)v.FindResource("NameText"), Text = t };
                if (min > 0) tb.MinWidth = min;
                chips.Children.Add(tb);
            }
            Chip("Model file"); Text("_"); Chip("Schema"); Text("_"); Chip("Date"); Text("_"); Chip("Set name", false); Text("", 40);
            F<System.Windows.Documents.Run>(v, "LblNamePreview").Text = "Tower_Federated_IFC4_2026-10-23.ifczip";

            var scope = F<StackPanel>(v, "ScopeOptions");
            string[] labels = { "Whole model (visible)", "Current selection", "Active section box", "Saved set / search set", "Multiple sets → one IFC each (batch)" };
            string[] hints = { "7 models", "1,204 selected", "", "12 sets", "3 of 7 ticked" };
            for (int i = 0; i < labels.Length; i++)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = labels[i], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var hint = new TextBlock { Text = hints[i], FontSize = 12, FontWeight = FontWeights.Normal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)v.FindResource("Muted") };
                Grid.SetColumn(hint, 1); row.Children.Add(hint);
                scope.Children.Add(new RadioButton { Content = row, GroupName = "scope", IsChecked = i == 0, Style = (Style)v.FindResource("OptionCard") });
            }

            void Items(string name, params string[] items)
            {
                var c = F<ComboBox>(v, name);
                if (c == null) return;
                foreach (var t in items) c.Items.Add(t);
                c.SelectedIndex = 0;
            }
            Items("CmbUnits", "Auto (from model)", "Millimeters", "Meters");
            Items("CmbGroups", "(nothing)", "IfcGroup", "IfcSystem", "IfcZone");
            Items("CmbBasePoint", "Geometry origin (recommended)", "Model origin (keeps world coordinates)", "Custom base point");
            Items("CmbSavedSet", "External Walls");
            F<ListBox>(v, "CmbSchema").SelectedIndex = 0;
            F<ListBox>(v, "CmbQuality").SelectedIndex = 1;
            F<ListBox>(v, "CmbPropSource").SelectedIndex = 1;

            // Switches: the defaults the pane starts with.
            foreach (var n in new[] { "ChkZip", "ChkInstancing", "ChkMergeParts", "ChkSkipEmptyProps", "ChkValidate", "ChkProfile",
                                      "ChkProps", "ChkMaterials", "ChkQuantities", "ChkAutoClassify", "ChkGeoref" })
                if (F<CheckBox>(v, n) is CheckBox cb) cb.IsChecked = true;
            foreach (var n in new[] { "TxtSurveyE", "TxtSurveyN", "TxtSurveyElev", "TxtRot" }) F<TextBox>(v, n).Text = "0";
            F<System.Windows.Documents.Run>(v, "LblPreview").Text = "0.000, 0.000, 0.000 m";
            F<TextBox>(v, "TxtProject").Tag = "Tower_Federated";
            F<TextBlock>(v, "TxtSmartSummary").Text =
                "Sets found     : 12\nScope          : Whole model — 48,112 element(s)\nProperty sets  : 10 discovered (Data tab)\n" +
                "Semantic roles : 3 filled, 0 kept as you set them\nMapping rules  : 4 added, 0 filled in, 8 set(s) unmatched\n" +
                "Full check     : 41,980 mapped · 6,132 proxy\nReview the AUTO sections on Data and Mapping, then Export.";

            if (F<ToggleButton>(v, "TglAdvanced") is ToggleButton t) { t.IsChecked = true; t.Tag = "defaults"; }
            if (F<FrameworkElement>(v, "AdvancedBody") is FrameworkElement ab) ab.Visibility = Visibility.Visible;

            // Pre-flight rows, as ExporterView.PreflightRow builds them.
            var pf = F<StackPanel>(v, "PreflightHost");
            void Pre(string tone, string head, string body, string jump)
            {
                string edge = tone == "ok" ? "Ok" : tone == "amber" ? "AmberTx" : "Faint";
                string bg = tone == "ok" ? "OkBg" : tone == "amber" ? "AmberBg" : "Card2";
                string bd = tone == "ok" ? "OkBd" : tone == "amber" ? "AmberBd" : "Bd";
                var outer = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(3, 0, 0, 0), Margin = new Thickness(0, 0, 0, 6), Background = (Brush)v.FindResource(edge) };
                var inner = new Border { CornerRadius = new CornerRadius(0, 7, 7, 0), BorderThickness = new Thickness(0, 1, 1, 1), Padding = new Thickness(10, 7, 10, 8),
                                         Background = (Brush)v.FindResource(bg), BorderBrush = (Brush)v.FindResource(bd) };
                outer.Child = inner;
                var dock = new DockPanel();
                if (jump != null)
                {
                    var link = new Button { Style = (Style)v.FindResource("LinkBtn"), Content = jump, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
                    DockPanel.SetDock(link, Dock.Right); dock.Children.Add(link);
                }
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = head, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)v.FindResource("Text") });
                if (tone == "amber")
                    text.Children.Add(new TextBlock { Text = body, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 2, 0, 0), Foreground = (Brush)v.FindResource("Text") });
                dock.Children.Add(text); inner.Child = dock; pf.Children.Add(outer);
            }
            Pre("ok", "IFC4 · units read from the model", "", null);
            Pre("amber", "Federated units differ", "2 of 7 models declare metres, the rest millimetres. One scale applies to the whole export, so one discipline would ship at the wrong size.", "Project");
            Pre("grey", "No georeferencing set", "", null);
            Pre("grey", "Not measured yet", "", null);

            // Role rows, as AddRoleRow builds them.
            var roles = F<StackPanel>(v, "RoleHost");
            var roleData = new[]
            {
                ("Type → IfcElementType + ObjectType", "Element", "Family and Type", "filled by Smart setup", "Accent"),
                ("Level → IfcBuildingStorey", "Element", "Level", "filled by Smart setup", "Accent"),
                ("Material → IfcMaterial", "", "", "blank · role off", "Muted"),
                ("Classification → IfcClassificationReference", "Element", "Assembly Code", "set by you", "Muted"),
                ("Element name → IFC Name (blank = the Navisworks name)", "", "", "blank · role off", "Muted"),
            };
            foreach (var (label, c, p, tag, tone) in roleData)
            {
                var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 9, 10, 10), Margin = new Thickness(0, 0, 0, 8),
                                          BorderBrush = (Brush)v.FindResource("Bd"), Background = (Brush)v.FindResource("Card2") };
                var sp = new StackPanel();
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var tg = new TextBlock { Text = tag, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), Foreground = (Brush)v.FindResource(tone) };
                DockPanel.SetDock(tg, Dock.Right); head.Children.Add(tg);
                head.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(head);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var cat = new ComboBox { IsEditable = true, Text = c, Tag = "(any category)" };
                var par = new ComboBox { IsEditable = true, Text = p, Tag = "Property" };
                Grid.SetColumn(par, 2); grid.Children.Add(cat); grid.Children.Add(par);
                sp.Children.Add(grid); border.Child = sp; roles.Children.Add(border);
            }

            var cats = new[] { ("Element", "41 properties"), ("Revit Type", "38 properties"), ("Pset_WallCommon", "9 properties"), ("Dimensions", "12 properties"),
                               ("Identity Data", "15 properties"), ("Phasing", "3 properties"), ("Item", "tree bookkeeping"), ("Transform", "tree bookkeeping"), ("Geometry", "tree bookkeeping") };
            F<ListBox>(v, "LstCategories").ItemsSource = cats.Select(x => new Item { Name = x.Item1, Detail = x.Item2, Checked = x.Item2 != "tree bookkeeping" }).ToList();
            F<TextBlock>(v, "LblCatCount").Text = "6 of 9 property sets will be exported";
            F<TextBlock>(v, "LblCatsSub").Text = "From the last scan. Unticked sets are left out of the IFC; tree bookkeeping starts unticked.";
            var sets = new[] { "External Walls", "Floors – Structure", "Doors", "Ducts – Supply", "Level 02 – Structure", "Level 02 - Structure", "Pipes – Heating" };
            F<ListBox>(v, "LstBatch").ItemsSource = sets.Select((x, i) => new Item { Name = x, Checked = i < 3, Detail = Regex.Replace(x, @"[^\w]+", "_").Trim('_') + ".ifczip" }).ToList();
            F<TextBlock>(v, "LblBatchCount").Text = "3 of 7 sets ticked · one IFC each";

            foreach (var g in new[] { "GridParams", "GridMap", "GridPropRules" })
                if (F<ItemsControl>(v, g) is ItemsControl ic) ic.ItemsSource = new[] { new Row { Number = 1 }, new Row { Number = 2, Classification = "" } };
        }

        private static void ShowBatch(UserControl v)
        {
            var scope = F<StackPanel>(v, "ScopeOptions");
            ((RadioButton)scope.Children[4]).IsChecked = true;
            F<FrameworkElement>(v, "BatchRow").Visibility = Visibility.Visible;
            F<TextBlock>(v, "LblExportBtn").Text = "Export 3 set(s) → IFC4 .ifczip";
            var tabs = (TabControl)v.FindName("Tabs");
            tabs.SelectedIndex = 0;
        }

        /// <summary>The + Token pop-up, filled like ExporterView.OnAddToken fills it, in the Whole model scope.</summary>
        private static void RenderTokenMenu(UserControl view, string path)
        {
            var tokens = new[]
            {
                ("Model file", "Tower_Federated", true), ("Project", "Tower", true), ("Schema", "IFC4", true),
                ("Set name", "not in this scope", false), ("Selected element", "not in this scope", false), ("Scope", "model", true),
                ("Date", "2026-10-23", true), ("Time", "1437", true), ("User", "jdoe", true),
            };
            var list = F<StackPanel>(view, "TokenList");
            foreach (var (label, sample, live) in tokens)
            {
                var row = new DockPanel();
                var s = new TextBlock { Text = sample, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), Foreground = (Brush)view.FindResource("Muted") };
                DockPanel.SetDock(s, Dock.Right); row.Children.Add(s);
                row.Children.Add(new TextBlock { Text = label });
                list.Children.Add(new Button { Style = (Style)view.FindResource("TokenRow"), Content = row, Opacity = live ? 1 : 0.55 });
            }
            // A Popup does not render off screen, so draw its card on its own.
            var popup = F<System.Windows.Controls.Primitives.Popup>(view, "TokenPopup");
            var card = (Border)popup.Child;
            popup.Child = null;
            BindingOperations.ClearBinding(card, FrameworkElement.WidthProperty);
            card.Width = 440;
            var host = new Border { Background = (Brush)view.Resources["Pane"], Padding = new Thickness(12), Child = card };
            host.Resources.MergedDictionaries.Add(view.Resources);
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            int w = (int)Math.Ceiling(host.ActualWidth), h = (int)Math.Ceiling(host.ActualHeight);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        private static void Render(UserControl view, int width, string path, int viewportHeight = 0)
        {
            // Tall first so nothing scrolls, then crop to the selected tab's content.
            double h = viewportHeight > 0 ? viewportHeight : 6000;
            view.Measure(new Size(width, h));
            view.Arrange(new Rect(0, 0, width, h));
            view.UpdateLayout();

            int outH = (int)h;
            if (viewportHeight == 0)
            {
                var tabs = (TabControl)view.FindName("Tabs");
                var sv = ((TabItem)tabs.SelectedItem).Content as ScrollViewer;
                if (sv?.Content is FrameworkElement content)
                {
                    // The content is stretched to the viewport; its own height is what it asks for.
                    content.Measure(new Size(content.ActualWidth, double.PositiveInfinity));
                    var top = content.TranslatePoint(new Point(0, 0), view).Y;
                    outH = (int)Math.Min(h, Math.Ceiling(top + content.DesiredSize.Height + 16));
                    // Measuring at another size upsets star-sized grid columns; lay out again.
                    view.InvalidateMeasure();
                    view.Measure(new Size(width, h));
                    view.Arrange(new Rect(0, 0, width, h));
                    view.UpdateLayout();
                }
            }

            var rtb = new RenderTargetBitmap(width, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(view);
            BitmapSource src = rtb;
            if (outH < (int)h) src = new CroppedBitmap(rtb, new Int32Rect(0, 0, width, outH));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }
}
