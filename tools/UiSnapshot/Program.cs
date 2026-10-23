using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UiSnapshot
{
    public sealed class Item
    {
        public string Name { get; set; } = "";
        public bool Checked { get; set; }
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

            foreach (int width in new[] { 440, 360 })
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
            Render(view, 440, Path.Combine(outDir, "viewport-440x820.png"), 820);
            Console.WriteLine("Wrote " + Directory.GetFiles(outDir, "*.png").Length + " snapshots to " + Path.GetFullPath(outDir));
            return 0;
        }

        private static T F<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T;

        private static void Fill(UserControl v)
        {
            // File name editor, as ExporterView.FileName.cs builds it: Model file _ Schema _ Date.
            var chips = F<WrapPanel>(v, "NameChips");
            if (chips != null)
            {
                void Chip(string label)
                {
                    var b = new Border { Style = (Style)v.FindResource("NameChip") };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    var t = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "BadgeAutoTx");
                    var x = new TextBlock { Text = "×", Margin = new Thickness(6, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
                    x.SetResourceReference(TextBlock.ForegroundProperty, "BadgeAutoTx");
                    sp.Children.Add(t); sp.Children.Add(x); b.Child = sp; chips.Children.Add(b);
                }
                void Text(string s, double min = 0)
                {
                    var tb = new TextBox { Style = (Style)v.FindResource("NameText"), Text = s };
                    if (min > 0) tb.MinWidth = min;
                    chips.Children.Add(tb);
                }
                Chip("Model file"); Text("_"); Chip("Schema"); Text("_rev-A_"); Chip("Date"); Text("", 40);
                F<TextBlock>(v, "LblNamePreview").Text = "→ PTS_Protos_CC_IFC4_rev-A_2026-10-23.ifczip";
            }
            var scope = F<StackPanel>(v, "ScopeOptions");
            string[] labels = { "Whole model (visible)", "Current selection", "Active section box", "Saved set / search set", "Multiple sets → one IFC each (batch)" };
            for (int i = 0; i < labels.Length; i++)
                scope.Children.Add(new RadioButton { Content = labels[i], GroupName = "scope", IsChecked = i == 0, Style = (Style)v.FindResource("OptionCard") });
            F<TextBlock>(v, "LblScopeHint").Text = "12,408 elements selected in Navisworks.";

            void Items(string name, params string[] items)
            {
                var c = F<ComboBox>(v, name);
                if (c == null) return;
                foreach (var s in items) c.Items.Add(s);
                c.SelectedIndex = 0;
            }
            Items("CmbSchema", "IFC4 (recommended)", "IFC2x3");
            Items("CmbUnits", "Auto (from model)", "Millimetres", "Metres");
            Items("CmbQuality", "Balanced (recommended)", "Small file", "High detail");
            Items("CmbPropSource", "Geometry + parent object (recommended)", "Geometry only", "Geometry + all parents");
            Items("CmbGroups", "No", "IfcGroup", "IfcSystem", "IfcZone");
            Items("CmbBasePoint", "Geometry origin (recommended)", "Model origin (keeps world coordinates)", "Custom base point");
            Items("CmbSavedSet", "Walls - External");

            // Checkboxes: the defaults the pane starts with.
            foreach (var n in new[] { "ChkZip", "ChkInstancing", "ChkMergeParts", "ChkSkipEmptyProps", "ChkValidate", "ChkProfile",
                                      "ChkProps", "ChkMaterials", "ChkQuantities", "ChkAutoClassify", "ChkGeoref" })
                if (F<CheckBox>(v, n) is CheckBox cb) cb.IsChecked = true;

            if (F<ToggleButton>(v, "TglAdvanced") is ToggleButton t) { t.IsChecked = true; t.Tag = "2 changed"; }
            if (F<FrameworkElement>(v, "AdvancedBody") is FrameworkElement ab) ab.Visibility = Visibility.Visible;

            // Pre-flight rows, as RenderPreflight builds them.
            var pf = F<StackPanel>(v, "PreflightHost");
            void Pre(string tone, string text, string jump)
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var dot = new TextBlock { Text = "●", FontSize = 9, Foreground = (Brush)v.FindResource(tone), Margin = new Thickness(0, 3, 7, 0), VerticalAlignment = VerticalAlignment.Top };
                var body = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)v.FindResource("Text"), LineHeight = 15 };
                body.Inlines.Add(text);
                if (jump != null) { body.Inlines.Add("  "); body.Inlines.Add(new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(jump)) { Foreground = (Brush)v.FindResource("Accent"), TextDecorations = null }); }
                Grid.SetColumn(body, 1); g.Children.Add(dot); g.Children.Add(body); pf.Children.Add(g);
            }
            Pre("Ok", "Schema IFC4 · units millimetres (from model)", null);
            Pre("AmberTx", "Mapping — 8,120 matched a rule · 4,288 did not (34.6%) and export as proxy", "→ Mapping");
            Pre("Ok", "Storeys — 6 distinct levels", null);
            Pre("Muted", "Georeferencing off — coordinates are local", "→ Project");

            // Role rows, as AddRoleRow builds them.
            var roles = F<StackPanel>(v, "RoleHost");
            foreach (var label in new[] { "Type → IfcElementType + ObjectType", "Level → IfcBuildingStorey", "Material → IfcMaterial",
                                          "Classification → IfcClassificationReference", "Element name → IFC Name (blank = the Navisworks name)" })
            {
                var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 9) };
                border.SetResourceReference(Border.BorderBrushProperty, "Bd");
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var cat = new ComboBox { IsEditable = true, Height = 30, Text = "Element" };
                var par = new ComboBox { IsEditable = true, Height = 30, Text = "Family and Type" };
                Grid.SetColumn(par, 2); grid.Children.Add(cat); grid.Children.Add(par);
                sp.Children.Add(grid); border.Child = sp; roles.Children.Add(border);
            }

            if (F<ListBox>(v, "LstCategories") is ListBox lc)
                lc.ItemsSource = new[] { "Element", "Identity Data", "Constraints", "Dimensions", "Phasing", "Item" }.Select((s, i) => new Item { Name = s, Checked = i != 4 }).ToList();
            foreach (var g in new[] { "GridParams", "GridMap", "GridPropRules" })
                if (F<ItemsControl>(v, g) is ItemsControl ic) ic.ItemsSource = new[] { new Row(), new Row() };
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
