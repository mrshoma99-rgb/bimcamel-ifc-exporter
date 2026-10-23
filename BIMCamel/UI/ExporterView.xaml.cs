using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Navisworks.Api;
using BIMCamel.Collect;
using BIMCamel.Data;
using BIMCamel.Geometry;
using BIMCamel.Ifc;
using BIMCamel.Profiles;
using ET = BIMCamel.Geometry.ExportTiming;
using NavApp = Autodesk.Navisworks.Api.Application;
using WF = System.Windows.Forms;

namespace BIMCamel.UI
{
    /// <summary>
    /// Modern WPF dock-pane UI for the BIMCamel IFC exporter. Hosts the same export / scan /
    /// profile logic as before; only the presentation layer changed (WinForms → WPF via ElementHost).
    /// </summary>
    public partial class ExporterView : UserControl
    {
        private const string AnyCat = "(any category)";

        // dynamic data
        //
        // ONE list of the document's saved sets, feeding all three surfaces that
        // show them (saved-set combo, batch checklist, mapping dropdowns). There
        // used to be three separate lists refreshed by separate buttons, which
        // meant they could disagree about what sets existed — and two of the
        // buttons existed only to re-sync them.
        private List<SelectionSet> _sets = new List<SelectionSet>();
        private readonly ObservableCollection<CheckItem> _cats = new ObservableCollection<CheckItem>();
        private readonly ObservableCollection<CheckItem> _batchItems = new ObservableCollection<CheckItem>();
        private readonly ObservableCollection<ParamRow> _paramRows = new ObservableCollection<ParamRow>();
        private readonly ObservableCollection<MapRow> _mapRows = new ObservableCollection<MapRow>();
        private readonly ObservableCollection<PropRuleRow> _propRules = new ObservableCollection<PropRuleRow>();

        /// <summary>What the scan already worked out, so Smart setup → preview → export computes
        /// each item key and resolves each mapping set ONCE instead of three times (v5 S2).</summary>
        private readonly ScanCache _scan = new ScanCache();
        private Dictionary<string, List<string>> _catParams = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // scope radios + role combos (built in code)
        private readonly List<RadioButton> _scopeRadios = new List<RadioButton>();
        private readonly ObservableCollection<string> _roleCats = new ObservableCollection<string> { AnyCat };
        private ComboBox _catType = null!, _parType = null!, _catLevel = null!, _parLevel = null!,
                         _catMat = null!, _parMat = null!, _catCls = null!, _parCls = null!,
                         _catName = null!, _parName = null!;

        // progress
        private readonly Stopwatch _tickClock = new Stopwatch();
        private long _lastTickMs;
        private long _peakHeap;
        private bool _dark;

        public ExporterView()
        {
            InitializeComponent();
            InitLogo();
            BuildScopeOptions();
            BuildRoleRows();
            InitControls();
            InitGridsAndLists();
            InitNameEditor();
            InitSession();
            Loaded += (_, _) => { AttachDocumentEvents(); RefreshModelStatus(); RefreshSets(); UpdateScopeHint(); PreviewBasePoint(); RenderPreflight(); };
            Unloaded += (_, _) => DetachDocumentEvents();
            // The set list self-refreshes when the Mapping tab opens (replacing
            // the old per-tab Refresh buttons), and the pre-flight rows refresh
            // when the Export tab opens — they show live config state, and this
            // is the moment the user comes back to commit. OriginalSource guard:
            // selection changes inside child ComboBoxes bubble up here too.
            Tabs.SelectionChanged += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
                if (ReferenceEquals(Tabs.SelectedItem, TabMapping)) RefreshSets();
                if (ReferenceEquals(Tabs.SelectedItem, TabExport)) RenderPreflight();
            };
            // Non-blocking, once-a-day update check; prompts on the UI thread if a newer release exists.
            UpdateCheck.Run(action => Dispatcher.BeginInvoke(action));
        }

        // Pop-up menus live outside the pane's visual tree, so they get its resources merged in;
        // that keeps the theme brushes (and a dark-mode switch) working inside them.
        private ContextMenu NewPaneMenu(UIElement? target)
        {
            var menu = new ContextMenu();
            menu.Resources.MergedDictionaries.Add(Resources);
            menu.Style = (Style)FindResource("PaneMenu");
            if (target != null)
            {
                menu.PlacementTarget = target;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            }
            return menu;
        }

        private Separator NewPaneMenuSeparator() => new Separator { Style = (Style)FindResource("PaneMenuSep") };

        private void OnDismissWarning(object sender, RoutedEventArgs e) => WarnBanner.Visibility = Visibility.Collapsed;

        private void OnAbout(object sender, RoutedEventArgs e) => AboutDialog.Show();

        /// <summary>The white camel in the brand bar, embedded in the assembly so it shows wherever
        /// the bundle is installed.</summary>
        private void InitLogo()
        {
            try { LogoMark.Source = new BitmapImage(new Uri("pack://application:,,,/BIMCamel;component/Resources/mark_white.png", UriKind.Absolute)); }
            catch { LogoMark.Visibility = Visibility.Collapsed; }
        }

        /// <summary>Quality as profiles store it (0 Balanced, 1 Small file, 2 High detail). The
        /// segments read Small / Balanced / High left to right, so the two orders differ; the swap
        /// is its own inverse.</summary>
        private static readonly int[] QualityOrder = { 1, 0, 2 };
        private int Quality
        {
            get { int i = CmbQuality.SelectedIndex; return i < 0 ? 0 : QualityOrder[i]; }
            set => CmbQuality.SelectedIndex = QualityOrder[Clamp(value, QualityOrder.Length)];
        }

        /// <summary>A segmented or radio list always has one choice: Ctrl+click would otherwise
        /// leave it with none.</summary>
        private static void KeepOneSelected(ListBox list)
        {
            list.SelectionChanged += (_, e) =>
            {
                if (list.SelectedIndex < 0 && e.RemovedItems.Count > 0) list.SelectedItem = e.RemovedItems[0];
            };
        }

        // ── one-time setup ──────────────────────────────────────────────────────
        private void InitControls()
        {
            CmbSchema.SelectedIndex = 0;
            AddItems(CmbUnits, new[] { "Auto (from model)", "Millimeters", "Centimeters", "Meters", "Feet", "Inches" }); CmbUnits.SelectedIndex = 0;
            AddItems(CmbGroups, new[] { "(nothing)", "IfcGroup", "IfcSystem", "IfcZone" }); CmbGroups.SelectedIndex = 0;
            Quality = 0;
            foreach (var list in new[] { CmbSchema, CmbQuality, CmbPropSource }) KeepOneSelected(list);
            // Item order = PropSource values (stored in profiles). A fresh pane defaults to the parent
            // object, because geometry-only misses Revit's Element tab entirely; a profile saved
            // before the option existed loads as 0 and keeps exporting exactly what it used to.
            CmbPropSource.SelectionChanged += (_, _) => SyncPropSource();
            CmbPropSource.SelectedIndex = (int)PropSource.ParentObject;
            AddItems(CmbBasePoint, new[] { "Geometry origin (recommended)", "Model origin (keeps world coordinates)", "Custom base point" }); CmbBasePoint.SelectedIndex = 0;

            ChkGeoref.IsChecked = true;
            // v6. Compression is a pure win, so it defaults ON.
            ChkZip.IsChecked = true;
            ChkProps.IsChecked = true; ChkMaterials.IsChecked = true; ChkQuantities.IsChecked = true;
            ChkAutoClassify.IsChecked = true;
            ApplyAdvancedDefaults();
            InitAdvancedCard();
            TxtE.Text = TxtN.Text = TxtElev.Text = TxtRot.Text = "0";
            TxtSurveyE.Text = TxtSurveyN.Text = TxtSurveyElev.Text = "0";

            CmbBasePoint.SelectionChanged += (_, _) => { CoordRow.Visibility = CmbBasePoint.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; PreviewBasePoint(); };
            TxtE.TextChanged += (_, _) => PreviewBasePoint();
            TxtN.TextChanged += (_, _) => PreviewBasePoint();
            TxtElev.TextChanged += (_, _) => PreviewBasePoint();
            void SyncProps() => LstCategories.IsEnabled = CmbPropSource.IsEnabled = ChkProps.IsChecked == true;
            ChkProps.Checked += (_, _) => SyncProps(); ChkProps.Unchecked += (_, _) => SyncProps(); SyncProps();
            // Details shows every pre-flight row's explanation as well as the full check breakdown.
            void SyncDetails() { CheckDetails.Visibility = TglCheckDetails.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; RenderPreflight(); }
            TglCheckDetails.Checked += (_, _) => { SyncDetails(); TglCheckDetails.Content = "Hide details"; };
            TglCheckDetails.Unchecked += (_, _) => { SyncDetails(); TglCheckDetails.Content = "Show details"; };
            // The fields stay in view, dimmed, so what would be written is still readable.
            void SyncGeoref() { bool on = ChkGeoref.IsChecked == true; GeorefRow.IsEnabled = on; GeorefRow.Opacity = on ? 1 : 0.45; }
            ChkGeoref.Checked += (_, _) => SyncGeoref(); ChkGeoref.Unchecked += (_, _) => SyncGeoref(); SyncGeoref();
        }

        // ── Advanced options card ───────────────────────────────────────────────
        // Every option in the collapsed Advanced card, with its default. One table
        // drives the defaults, the Reset link and the "N changed" header count, so
        // the three can never disagree.
        private (CheckBox box, bool def)[] AdvancedDefaults() => new[]
        {
            // Validation defaults ON: it exists to catch the mistakes this exporter can make, and
            // silently shipping an invalid file is worse than the second it costs to check.
            (ChkInstancing, true), (ChkValidate, true), (ChkProfile, true),
            // Fast instancing stays OFF until a run proves the guess holds on this model (v5 E1).
            (ChkFastInstancing, false),
            // Element-level instancing defaults ON: a many-part element otherwise becomes one
            // placed copy per fragment, which viewers cannot load at stadium scale.
            (ChkMergeParts, true),
            // v6. Dropping blank properties is a pure win, so it defaults ON.
            // Type-level sharing MOVES data to where a simpler reader may not look, and the size
            // filter REMOVES objects — both change what a recipient sees, so both default OFF.
            (ChkSkipEmptyProps, true), (ChkShareTypeProps, false), (ChkMinSize, false),
            // Simplification changes the shapes a recipient sees, so it is opt-in like the size filter.
            (ChkSimplify, false),
            (ChkSplit, false),
        };

        private void ApplyAdvancedDefaults()
        {
            foreach (var (box, def) in AdvancedDefaults()) box.IsChecked = def;
        }

        private void InitAdvancedCard()
        {
            void SyncBody() => AdvancedBody.Visibility = TglAdvanced.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            TglAdvanced.Checked += (_, _) => SyncBody(); TglAdvanced.Unchecked += (_, _) => SyncBody(); SyncBody();

            // Fast instancing and part merging only mean anything inside instancing (the export
            // reads them there), so grey them out rather than let it look active when instancing is off.
            void SyncFast() => ChkFastInstancing.IsEnabled = ChkMergeParts.IsEnabled = ChkInstancing.IsChecked == true;
            ChkInstancing.Checked += (_, _) => SyncFast(); ChkInstancing.Unchecked += (_, _) => SyncFast(); SyncFast();

            foreach (var (box, _) in AdvancedDefaults())
            {
                box.Checked += (_, _) => UpdateAdvancedSummary();
                box.Unchecked += (_, _) => UpdateAdvancedSummary();
            }
            UpdateAdvancedSummary();
        }

        private void UpdateAdvancedSummary()
        {
            int changed = AdvancedDefaults().Count(x => (x.box.IsChecked == true) != x.def);
            TglAdvanced.Tag = changed == 0 ? "defaults" : changed + " changed";
        }

        private void OnResetAdvanced(object sender, RoutedEventArgs e)
        {
            ApplyAdvancedDefaults();
            TxtMinSizeMm.Text = "5"; TxtSimplifyMm.Text = "10"; TxtSplitMb.Text = "200";
            SetStatus("Advanced options reset to defaults.");
        }

        /// <summary>Push the "read properties from" choice to the harvester at once, so the next
        /// scan, pre-flight or export all read the same tree levels.</summary>
        private void SyncPropSource()
        {
            int i = CmbPropSource.SelectedIndex;
            PropertyHarvester.Source = i >= 0 && i <= (int)PropSource.AllParents ? (PropSource)i : PropSource.Geometry;
            PropertyHarvester.ResetCache();
        }

        private void InitGridsAndLists()
        {
            LstCategories.ItemsSource = _cats;
            LstBatch.ItemsSource = _batchItems;
            GridParams.ItemsSource = _paramRows;
            GridMap.ItemsSource = _mapRows;
            GridPropRules.ItemsSource = _propRules;
            WireRuleList(GridMap, _mapRows, draggable: true);
            WireRuleList(GridPropRules, _propRules, draggable: true);
            WireRuleList(GridParams, _paramRows, draggable: false);
            LstCategories.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => UpdateCatCount()));
            LstCategories.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => UpdateCatCount()));
            LstBatch.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => UpdateBatchCount()));
            LstBatch.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => UpdateBatchCount()));
            ChkZip.Checked += (_, _) => UpdateBatchNames(); ChkZip.Unchecked += (_, _) => UpdateBatchNames();
            // Rule cards are made of combos and boxes that swallow the click, so
            // focusing any field selects its card, for Alt+arrow moves.
            foreach (var list in new ListBox[] { GridMap, GridPropRules })
                list.AddHandler(UIElement.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((o, e) =>
                {
                    for (var d = e.NewFocus as DependencyObject; d != null && !ReferenceEquals(d, o); d = VisualTreeHelper.GetParent(d))
                        if (d is ListBoxItem item) { item.IsSelected = true; return; }
                }));

            // shared catalogs for the grids
            ParamRow.SharedCategories.Clear(); ParamRow.SharedCategories.Add(AnyCat);
            ParamRow.SharedPsets.Clear(); foreach (var p in PsetCatalog.PsetNames()) ParamRow.SharedPsets.Add(p);
            ParamRow.SharedParamNames.Clear(); foreach (var p in PsetCatalog.AllParamNames()) ParamRow.SharedParamNames.Add(p);
            ParamRow.Resolver = ResolvePropsForCategory;
            MapRow.SharedClasses.Clear(); foreach (var c in TypeMapping.Keys()) MapRow.SharedClasses.Add(c);
        }

        private IEnumerable<string> ResolvePropsForCategory(string cat)
        {
            if (string.IsNullOrEmpty(cat) || cat == AnyCat)
                return _catParams.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
            return _catParams.TryGetValue(cat, out var l) ? l : new List<string>();
        }

        private void BuildScopeOptions()
        {
            string[] labels =
            {
                "Whole model (visible)", "Current selection", "Active section box",
                "Saved set / search set", "Multiple sets → one IFC each (batch)"
            };
            string[] tips =
            {
                "Export every visible element in the model.",
                "Export only what is currently selected in Navisworks.",
                "Export only the elements inside the active section box.",
                "Export a single saved selection/search set (pick it below).",
                "Export several sets at once — one IFC file per set (pick an output folder)."
            };
            for (int i = 0; i < labels.Length; i++)
            {
                // Label left, a live count or state right (UpdateScopeHint fills it).
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = labels[i], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var hint = new TextBlock { FontSize = 12, FontWeight = FontWeights.Normal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                Grid.SetColumn(hint, 1); row.Children.Add(hint);
                _scopeHints.Add(hint);
                var rb = new RadioButton
                {
                    Content = row, GroupName = "scope", IsChecked = i == 0,
                    Style = (Style)FindResource("OptionCard"), ToolTip = tips[i]
                };
                rb.Checked += (_, _) => UpdateScopeHint();
                _scopeRadios.Add(rb);
                ScopeOptions.Children.Add(rb);
            }
        }

        private readonly List<TextBlock> _scopeHints = new List<TextBlock>();

        private int ScopeIndex()
        {
            for (int i = 0; i < _scopeRadios.Count; i++) if (_scopeRadios[i].IsChecked == true) return i;
            return 0;
        }

        private void BuildRoleRows()
        {
            (_catType, _parType) = AddRoleRow("Type → IfcElementType + ObjectType");
            (_catLevel, _parLevel) = AddRoleRow("Level → IfcBuildingStorey");
            (_catMat, _parMat) = AddRoleRow("Material → IfcMaterial");
            (_catCls, _parCls) = AddRoleRow("Classification → IfcClassificationReference");
            (_catName, _parName) = AddRoleRow("Element name → IFC Name (blank = the Navisworks name)");
        }

        private (ComboBox cat, ComboBox par) AddRoleRow(string label)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 9, 10, 10), Margin = new Thickness(0, 0, 0, 8)
            };
            // A resource REFERENCE, not the brush resolved now, so the row follows a theme switch.
            border.SetResourceReference(Border.BorderBrushProperty, "Bd");
            border.SetResourceReference(Border.BackgroundProperty, "Card2");
            var sp = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var tag = new TextBlock { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(tag, Dock.Right);
            head.Children.Add(tag);
            head.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(head);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var cat = new ComboBox { IsEditable = true, ItemsSource = _roleCats, Tag = AnyCat, ToolTip = "Source category that carries this role (scan first to populate)." };
            var par = new ComboBox { IsEditable = true, Tag = "Property", ToolTip = "Source property that carries this role. Leave blank to turn the role off." };
            Grid.SetColumn(cat, 0); Grid.SetColumn(par, 2);
            grid.Children.Add(cat); grid.Children.Add(par);
            sp.Children.Add(grid);
            border.Child = sp;
            RoleHost.Children.Add(border);
            cat.SelectionChanged += (_, _) => PopulateParamCombo(par, cat.Text, keepText: false);
            _roleTags[par] = tag;
            // A role the user types over is theirs from then on.
            par.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
            {
                if (!_fillingRole && _roleAuto.TryGetValue(par, out var v) && v != par.Text) _roleAuto.Remove(par);
                UpdateRoleTag(par);
            }));
            UpdateRoleTag(par);
            return (cat, par);
        }

        /// <summary>Property combo → the value Smart setup put there, while it is still that value.</summary>
        private readonly Dictionary<ComboBox, string> _roleAuto = new Dictionary<ComboBox, string>();
        private readonly Dictionary<ComboBox, TextBlock> _roleTags = new Dictionary<ComboBox, TextBlock>();
        private bool _fillingRole;

        /// <summary>Who decided this role: Smart setup, the user, or nobody (role off).</summary>
        private void UpdateRoleTag(ComboBox par)
        {
            if (!_roleTags.TryGetValue(par, out var tag)) return;
            if (string.IsNullOrWhiteSpace(par.Text)) { tag.Text = "blank · role off"; tag.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }
            else if (_roleAuto.ContainsKey(par)) { tag.Text = "filled by Smart setup"; tag.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); }
            else { tag.Text = "set by you"; tag.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }
        }

        private static void AddItems(ComboBox cmb, IEnumerable<string> items) { foreach (var i in items) cmb.Items.Add(i); }

        // ── theme toggle ──────────────────────────────────────────────────────────
        private void OnToggleTheme(object sender, RoutedEventArgs e) => ApplyTheme(!_dark);

        private void ApplyTheme(bool dark)
        {
            _dark = dark;
            // The button shows the theme a click switches TO.
            IcoTheme.Tag = _dark
                ? System.Windows.Media.Geometry.Parse("M12,7 A5,5 0 1 0 12.01,7 Z M12,1 L12,3 M12,21 L12,23 M1,12 L3,12 M21,12 L23,12 M4.2,4.2 L5.6,5.6 M18.4,18.4 L19.8,19.8 M4.2,19.8 L5.6,18.4 M18.4,5.6 L19.8,4.2")
                : FindResource("G_theme");
            System.Windows.Media.Color C(string hex) => (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex);
            void Set(string key, string hex) => Resources[key] = new SolidColorBrush(C(hex));
            void Brand(string a, string b) => Resources["BrandBar"] = new LinearGradientBrush(C(a), C(b), 0);
            if (_dark)
            {
                Set("Accent", "#3AA0F0"); Set("AccentHover", "#5BB3F5"); Set("AccentPress", "#2A8AD6");
                Set("Bg", "#15171B"); Set("Pane", "#1C1F24"); Set("Card", "#23272E"); Set("Card2", "#262B32");
                Set("Text", "#E7EAEE"); Set("Muted", "#9AA3AD"); Set("Faint", "#8C95A0");
                Set("Bd", "#333941"); Set("BdStrong", "#3D444D"); Set("Field", "#2B3138"); Set("FieldBd", "#3D444D");
                Set("Nav", "#282D34"); Set("NavActive", "#1F2D3D"); Set("Track", "#2E343B"); Set("FamBatch", "#A9CF5C");
                Set("Ok", "#46C97E"); Set("OkBg", "#12301F"); Set("OkBd", "#245638");
                Set("AmberBg", "#3A2F12"); Set("AmberBd", "#5C4A1E"); Set("AmberTx", "#F0C66A");
                Set("RedBg", "#3A1D1D"); Set("RedBd", "#5C2B2B"); Set("RedTx", "#F08C84");
                Set("ConsoleBg", "#0A0C10"); Set("ConsoleTx", "#BFE0C8");
                Set("BadgeReqBg", "#12301F"); Set("BadgeReqBd", "#245638"); Set("BadgeReqTx", "#46C97E");
                Set("BadgeOptBg", "#282D34"); Set("BadgeOptBd", "#333941"); Set("BadgeOptTx", "#9AA3AD");
                Set("BadgeAutoBg", "#152836"); Set("BadgeAutoBd", "#274966"); Set("BadgeAutoTx", "#7CC0F5");
                Brand("#1B5E92", "#2277B8");
            }
            else
            {
                Set("Accent", "#0070C0"); Set("AccentHover", "#0D83DA"); Set("AccentPress", "#005A9C");
                Set("Bg", "#EEF1F5"); Set("Pane", "#F7F8FA"); Set("Card", "#FFFFFF"); Set("Card2", "#FBFCFD");
                Set("Text", "#1F2329"); Set("Muted", "#5C646F"); Set("Faint", "#767E89");
                Set("Bd", "#E1E5EA"); Set("BdStrong", "#CDD3DA"); Set("Field", "#FFFFFF"); Set("FieldBd", "#CDD3DA");
                Set("Nav", "#EAEDF1"); Set("NavActive", "#E6F0FA"); Set("Track", "#D8DDE3"); Set("FamBatch", "#4D7C0F");
                Set("Ok", "#1A8F4C"); Set("OkBg", "#E4F6EC"); Set("OkBd", "#B7E3C9");
                Set("AmberBg", "#FFF7E6"); Set("AmberBd", "#FFE1A8"); Set("AmberTx", "#8A5A00");
                Set("RedBg", "#FDECEC"); Set("RedBd", "#F3C6C6"); Set("RedTx", "#B3261E");
                Set("ConsoleBg", "#0E1116"); Set("ConsoleTx", "#CFE3D5");
                Set("BadgeReqBg", "#E4F6EC"); Set("BadgeReqBd", "#B7E3C9"); Set("BadgeReqTx", "#1A8F4C");
                Set("BadgeOptBg", "#EAEDF1"); Set("BadgeOptBd", "#E1E5EA"); Set("BadgeOptTx", "#5C646F");
                Set("BadgeAutoBg", "#E6F0FA"); Set("BadgeAutoBd", "#BBD8F2"); Set("BadgeAutoTx", "#0F5FA0");
                Brand("#0070C0", "#0D83DA");
            }
            // The pre-flight rows are code-built with resolved brushes, so they
            // do not retint through DynamicResource — rebuild them.
            RenderPreflight();
        }

        // ── scope hint / reveals ────────────────────────────────────────────────
        private void UpdateScopeHint()
        {
            int idx = ScopeIndex();
            SavedSetRow.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
            BatchRow.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
            if (idx == 3 || idx == 4) RefreshSets();
            int selected = SelectedCount();
            // The amber line under the rows only says what is missing; the rows carry the counts.
            LblScopeHint.Text = idx == 1 && selected == 0 ? "Select items in Navisworks first."
                              : idx == 3 && _sets.Count == 0 ? "This document has no saved sets or search sets."
                              : "";
            if (_scopeHints.Count == 5)
            {
                int models = NavApp.ActiveDocument?.Models.Count ?? 0;
                _scopeHints[0].Text = models > 1 ? $"{models} models" : "";
                _scopeHints[1].Text = selected > 0 ? $"{selected:N0} selected" : "nothing selected";
                _scopeHints[2].Text = "";
                _scopeHints[3].Text = _sets.Count == 1 ? "1 set" : $"{_sets.Count:N0} sets";
            }
            UpdateBatchCount();
            RefreshNameEditor();
        }

        private void RefreshModelStatus()
        {
            var doc = NavApp.ActiveDocument;
            // The model's name is not on show any more; it is the title's tooltip and what a blank
            // project name becomes.
            if (doc == null || doc.Models.Count == 0) { LblTitle.ToolTip = "No model open"; TxtProject.Tag = ""; return; }
            string name = ModelBaseName(doc);
            LblTitle.ToolTip = name + (doc.Models.Count > 1 ? $" · {doc.Models.Count} models" : "");
            TxtProject.Tag = name;
        }

        // ── button handlers ───────────────────────────────────────────────────────
        // All and None act on the sets the search shows; ticks of hidden ones are kept.
        private void OnBatchAll(object s, RoutedEventArgs e) { foreach (var c in VisibleBatch()) c.Checked = true; UpdateBatchCount(); }
        private void OnBatchNone(object s, RoutedEventArgs e) { foreach (var c in VisibleBatch()) c.Checked = false; UpdateBatchCount(); }
        private IEnumerable<CheckItem> VisibleBatch()
        {
            var f = (TxtBatchFilter.Text ?? "").Trim();
            return f.Length == 0 ? _batchItems : _batchItems.Where(c => c.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        private void OnBatchFilter(object s, TextChangedEventArgs e)
        {
            var f = (TxtBatchFilter.Text ?? "").Trim();
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_batchItems);
            view.Filter = f.Length == 0 ? null : (Predicate<object>)(o => o is CheckItem c && c.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            bool none = f.Length > 0 && !VisibleBatch().Any();
            LblBatchEmpty.Text = none ? $"No set matches “{f}”." : "";
            LblBatchEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>"3 of 7 sets ticked", in the batch box, on its scope row and on the Export button.</summary>
        private void UpdateBatchCount()
        {
            int ticked = _batchItems.Count(b => b.Checked);
            LblBatchCount.Text = $"{ticked} of {_batchItems.Count} sets ticked · one IFC each";
            if (_scopeHints.Count == 5) _scopeHints[4].Text = $"{ticked} of {_batchItems.Count} ticked";
            UpdateExportLabel();
        }

        /// <summary>Each batch row shows the file it would write.</summary>
        private void UpdateBatchNames()
        {
            string ext = ChkZip.IsChecked == true ? ".ifczip" : ".ifc";
            foreach (var b in _batchItems) b.Detail = Sanitize(b.Name) + ext;
        }

        private void UpdateCatCount()
        {
            int on = _cats.Count(c => c.Checked);
            LblCatCount.Text = _cats.Count == 0 ? "Every property set will be exported" : $"{on} of {_cats.Count} property sets will be exported";
        }
        private void OnScanSelection(object s, RoutedEventArgs e) { _cancelRequested = false; ScanSelection(); }
        private void OnCatsAll(object s, RoutedEventArgs e) { foreach (var c in VisibleCats()) c.Checked = true; }
        private void OnCatsNone(object s, RoutedEventArgs e) { foreach (var c in VisibleCats()) c.Checked = false; }
        private IEnumerable<CheckItem> VisibleCats()
        {
            var f = (TxtCatFilter.Text ?? "").Trim();
            return f.Length == 0 ? _cats : _cats.Where(c => c.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        private void OnCatFilter(object s, TextChangedEventArgs e)
        {
            var f = (TxtCatFilter.Text ?? "").Trim();
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_cats);
            view.Filter = f.Length == 0 ? null : (Predicate<object>)(o => o is CheckItem c && c.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        private void OnParamAdd(object s, RoutedEventArgs e) => _paramRows.Add(new ParamRow { SourceCategory = AnyCat });
        private void OnMapAdd(object s, RoutedEventArgs e) => _mapRows.Add(new MapRow());
        private void OnMapClear(object s, RoutedEventArgs e) => _mapRows.Clear();
        private void OnPropRuleAdd(object s, RoutedEventArgs e) => _propRules.Add(new PropRuleRow { Category = AnyCat });

        /// <summary>
        /// One behaviour for every list of rule cards: × removes the card, cards are numbered,
        /// and (for rule lists, where order decides which rule wins) the grip drags a card to a
        /// new place and Alt+↑/↓ moves the focused one.
        /// </summary>
        private void WireRuleList<T>(ListBox list, ObservableCollection<T> rows, bool draggable) where T : class, INumbered
        {
            void Renumber() { for (int i = 0; i < rows.Count; i++) rows[i].Number = i + 1; }
            rows.CollectionChanged += (_, _) => Renumber();
            Renumber();

            list.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is Button b && "remove".Equals(b.Tag) && b.DataContext is T r) { rows.Remove(r); e.Handled = true; }
            }));
            if (!draggable) return;

            list.PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Alt || !(e.SystemKey == Key.Up || e.SystemKey == Key.Down)) return;
                if (!(list.SelectedItem is T r)) return;
                int i = rows.IndexOf(r), j = i + (e.SystemKey == Key.Up ? -1 : 1);
                if (i >= 0 && j >= 0 && j < rows.Count) rows.Move(i, j);
                e.Handled = true;
            };

            System.Windows.Point? down = null; T? dragged = null;
            list.PreviewMouseLeftButtonDown += (_, e) =>
            {
                down = null; dragged = null;
                for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, list); d = VisualTreeHelper.GetParent(d))
                    if (d is FrameworkElement fe && "grip".Equals(fe.Tag) && fe.DataContext is T r) { down = e.GetPosition(list); dragged = r; return; }
            };
            list.PreviewMouseMove += (_, e) =>
            {
                if (down == null || dragged == null || e.LeftButton != MouseButtonState.Pressed) return;
                var d = e.GetPosition(list) - down.Value;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                var item = dragged; down = null; dragged = null;
                var src = list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                if (src != null) src.Tag = "drag";
                try { DragDrop.DoDragDrop(list, new DataObject("BIMCamelRule", item), DragDropEffects.Move); }
                finally { if (src != null) src.Tag = null; ClearDropMarks(list); }
            };
            ListBoxItem? TargetOf(DragEventArgs e)
            {
                for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, list); d = VisualTreeHelper.GetParent(d))
                    if (d is ListBoxItem it) return it;
                return null;
            }
            list.DragOver += (_, e) =>
            {
                if (!(e.Data.GetData("BIMCamelRule") is T)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
                e.Effects = DragDropEffects.Move; e.Handled = true;
                var target = TargetOf(e);
                ClearDropMarks(list, keep: target);
                if (target != null && !"drag".Equals(target.Tag)) target.Tag = "drop";
            };
            list.DragLeave += (_, _) => ClearDropMarks(list);
            list.Drop += (_, e) =>
            {
                ClearDropMarks(list);
                if (!(e.Data.GetData("BIMCamelRule") is T r)) return;
                var target = TargetOf(e);
                int from = rows.IndexOf(r);
                // Dropping on card k puts the dragged card at k; off the cards, at the end.
                int to = target?.DataContext is T t ? rows.IndexOf(t) : rows.Count - 1;
                if (from >= 0 && to >= 0 && from != to) rows.Move(from, to);
                list.SelectedItem = r;
                e.Handled = true;
            };
        }

        private static void ClearDropMarks(ListBox list, ListBoxItem? keep = null)
        {
            foreach (var o in list.Items)
                if (list.ItemContainerGenerator.ContainerFromItem(o) is ListBoxItem it && !ReferenceEquals(it, keep) && "drop".Equals(it.Tag)) it.Tag = null;
        }
        private void OnPreviewMapping(object s, RoutedEventArgs e)
        {
            _cancelRequested = false;
            try { PreviewMapping(); }
            catch (OperationCanceledException) { SetStatus("Check stopped."); }
            finally { EndBusy(); }
        }
        private void OnFederationReport(object s, RoutedEventArgs e)
        {
            var doc = NavApp.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { SetStatus("Open a model first."); return; }
            TxtCoordReport.Text = ("Federated model origins\n──────────────────────────────\n" + ModelSurvey.FederationReport(doc)).Replace("\n", Environment.NewLine);
            CoordReportBox.Visibility = Visibility.Visible;
            SetStatus("Federation report ready below.");
        }

        /// <summary>Show the storeys the export will create; they come from the check, so run it
        /// first when it has not run.</summary>
        private void OnCheckStoreys(object s, RoutedEventArgs e)
        {
            StoreysBox.Visibility = Visibility.Visible;
            if (_preflight == null) OnPreviewMapping(s, e);
        }

        // ── smart setup ───────────────────────────────────────────────────────────
        /// <summary>
        /// Every auto-detect the pane has, in dependency order, behind one button:
        /// sets → property/role scan → set→class proposal → mapping preview.
        /// These were five buttons on three tabs, two of which called the same
        /// method under different names ("Scan model" and "Detect from model").
        ///
        /// Nothing the user typed is ever overwritten: roles fill only when blank,
        /// auto-map only fills empty rules, and the pset ticks reset only when the
        /// scan discovers a different pset list (same as any rescan).
        ///
        /// It reads the EXPORT SCOPE, once. The first version sampled the whole
        /// document for properties and then let the preview walk the scope
        /// separately: two traversals, the first of them describing elements the
        /// export was never going to touch — so picking "Current selection" and
        /// running Smart setup still cost a full-model walk.
        /// </summary>
        private void OnSmartSetup(object sender, RoutedEventArgs e)
        {
            var doc = NavApp.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { SetStatus("Open a model first."); return; }
            // One busy bracket around the WHOLE chain. The steps self-bracket,
            // and their interim EndBusy calls would re-enable the UI between
            // steps — the message pump would then dispatch any clicks queued
            // during the previous step straight into a half-configured pane
            // (including a second Smart setup, or Export). _busyChain makes the
            // interim releases no-ops; only the finally below really releases.
            _cancelRequested = false;
            _busyChain = true;
            BeginBusyMarquee("Smart setup — collecting scope…", cancelable: true);
            try
            {
                var sb = new StringBuilder();
                RefreshSets();
                sb.AppendLine($"Sets found     : {_sets.Count}");

                // Resolved ONCE, then handed to both the property scan and the
                // preview. This is the whole cost of Smart setup on a large
                // model, so doing it twice was doubling the wait.
                ET.ResetScan();
                long tc = ET.Now;
                var items = ResolveScope(doc, CollectTick);
                ET.CollectTicks += ET.Now - tc;
                if (items == null) { TxtSmartSummary.Text = "Smart setup needs a usable scope — see the message below the progress bar."; return; }
                if (items.Count == 0) { TxtSmartSummary.Text = "No geometry elements in this scope."; SetStatus("No geometry elements in scope."); return; }
                sb.AppendLine($"Scope          : {ScopeLabel()} — {items.Count:N0} element(s)");

                if (!ScanFrom(items, "scope")) { TxtSmartSummary.Text = "Property scan found nothing to read."; return; }
                sb.AppendLine($"Property sets  : {_cats.Count} discovered (Data tab)");
                sb.AppendLine($"Semantic roles : {_lastRolesFilled} filled, {_lastRolesKept} kept as you set them");

                var (added, filled, unmatched) = AutoMapSets();
                sb.AppendLine($"Mapping rules  : {added} added, {filled} filled in, {unmatched} set(s) unmatched");

                // A failed preview must not read as success: say so in the
                // summary, and keep whatever error the step left in the status
                // line instead of overwriting it with "complete".
                var digest = PreviewMapping(items);
                sb.AppendLine(digest ?? "Full check     : did not run — see the status line for the reason");

                sb.Append("Review the AUTO sections on Data and Mapping, then Export.");
                TxtSmartSummary.Text = sb.ToString().Replace("\n", Environment.NewLine);
                if (digest != null) SetStatus("Smart setup complete — review, then Export IFC.");
            }
            catch (OperationCanceledException)
            {
                // Partial work is kept on purpose: the roles and rules it managed
                // to fill are still proposals the user can accept, and throwing
                // them away would make Stop cost more than waiting.
                TxtSmartSummary.Text = "Smart setup stopped. Anything it had already filled in is kept — run it again to finish.";
                SetStatus("Smart setup stopped.");
            }
            catch (Exception ex)
            {
                TxtSmartSummary.Text = "Smart setup failed: " + ex.Message;
                SetStatus("Smart setup failed: " + ex.Message);
            }
            finally { _busyChain = false; EndBusy(); }
        }
        private void OnCopyReport(object s, RoutedEventArgs e) { if (!string.IsNullOrEmpty(TxtReport.Text)) try { System.Windows.Clipboard.SetText(TxtReport.Text); } catch { } }

        // ── unified scan ──────────────────────────────────────────────────────────
        // How the last scan's role auto-fill landed, for the Smart-setup summary.
        private int _lastRolesFilled, _lastRolesKept;

        /// <summary>The property scan never looks past this many items, so anything
        /// bigger is sampled rather than read in full.</summary>
        private const int SampleCap = 1000;

        /// <summary>
        /// "Scan selection instead" — deliberately independent of the export scope,
        /// for the case where the elements carrying the properties you care about
        /// are not the ones you are exporting.
        /// </summary>
        private bool ScanSelection()
        {
            var doc = NavApp.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { SetStatus("Open a model first."); return false; }

            // The busy indicator has to come up BEFORE the tree walk, not after it. Collecting is
            // the slow part, and starting the marquee afterwards meant the UI sat frozen for the
            // whole walk with no sign anything was happening.
            BeginBusyMarquee("Scanning properties…", cancelable: true);
            try
            {
                ET.ResetScan();
                var sel = doc.CurrentSelection.SelectedItems;
                long tc = ET.Now;
                List<ModelItem> items;
                string label;
                if (sel == null || sel.Count == 0)
                {
                    // Nothing selected used to be a dead end ("select something first"). The scan
                    // only ever reads a sample, so it can answer from one without resolving the
                    // model at all (v5 S3): CollectSample stops walking the moment it has enough,
                    // spread across the roots and their top-level branches.
                    items = ItemCollector.CollectSample(doc, SampleCap, CollectTick);
                    label = "sampled";
                }
                else
                {
                    items = ItemCollector.ResolveLeaves(sel, CollectTick);
                    label = "selected";
                }
                ET.CollectTicks += ET.Now - tc;
                if (items.Count == 0) { SetStatus("No geometry elements to scan."); return false; }
                return ScanFrom(items, label);
            }
            catch (OperationCanceledException) { SetStatus("Scan stopped."); return false; }
            catch (Exception ex) { SetStatus("Scan failed: " + ex.Message); return false; }
            finally { EndBusy(); }
        }

        /// <summary>
        /// The scan proper, over an already-resolved list — so the caller decides
        /// what "the model" means (the export scope for Smart setup, the current
        /// selection for the manual button) and the list is walked only once.
        /// Above <see cref="SampleCap"/> it samples with an even stride rather than
        /// taking the first N: the first N of a federation is one discipline, and
        /// the roles this proposes are only as good as the spread of properties it saw.
        /// </summary>
        private bool ScanFrom(List<ModelItem> items, string sourceLabel)
        {
            SetStatus("Reading properties…"); PumpUi(Dispatcher);
            ThrowIfCancelled();
            long tp = ET.Now;
            var sample = Spread(items, SampleCap);
            PropertyHarvester.ResetCache();
            _catParams = PropertyHarvester.ScanCategoryParams(sample, SampleCap);
            ET.PropScanTicks += ET.Now - tp;
            ET.SampledItems = sample.Count;

            var cats = _catParams.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var allParams = _catParams.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

            // Psets checklist — unticks survive a rescan by name. Rebuilding
            // all-ticked meant every scan quietly reverted the user's pset
            // filter: the same proposal-beats-decision bug as the roles.
            var unticked = new HashSet<string>(_cats.Where(c => !c.Checked).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            // Categories the user has already seen keep whatever they decided; only categories that
            // are NEW to this pane get a default, and for Navisworks' own bookkeeping ones that
            // default is off (v6 Z5). Same rule as everywhere else here: a proposal must never beat
            // a decision.
            var known = new HashSet<string>(_cats.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            _cats.Clear();
            foreach (var c in cats)
                _cats.Add(new CheckItem(c, known.Contains(c) ? !unticked.Contains(c) : !IsBookkeeping(c))
                {
                    Detail = IsBookkeeping(c) ? "tree bookkeeping" : _catParams[c].Count == 1 ? "1 property" : $"{_catParams[c].Count} properties"
                });
            LblCatsSub.Text = "From the last scan. Unticked sets are left out of the IFC; tree bookkeeping starts unticked.";
            UpdateCatCount();

            // grid catalogs (categories + the property-name suggestion list)
            ParamRow.SharedCategories.Clear(); ParamRow.SharedCategories.Add(AnyCat); foreach (var c in cats) ParamRow.SharedCategories.Add(c);
            foreach (var row in _paramRows) row.Refresh();
            foreach (var row in _propRules) row.Refresh();

            // Capture the user's role choices BEFORE touching _roleCats:
            // clearing that ItemsSource cascades SelectionChanged →
            // PopulateParamCombo(keepText:false), which blanks par.Text — so
            // reading "is this role blank?" AFTER the clear would see every
            // role as blank and overwrite the very decisions the fill-blank
            // guard exists to protect.
            var priorType = (_catType.Text, _parType.Text);
            var priorLevel = (_catLevel.Text, _parLevel.Text);
            var priorMat = (_catMat.Text, _parMat.Text);
            var priorCls = (_catCls.Text, _parCls.Text);
            var priorName = (_catName.Text, _parName.Text);

            // role category combos
            _roleCats.Clear(); _roleCats.Add(AnyCat); foreach (var c in cats) _roleCats.Add(c);

            // Auto-fill roles from a guess — but only the BLANK ones. The old
            // behaviour overwrote whatever the user had chosen, which made
            // re-scanning destructive; a proposal must never beat a decision.
            _lastRolesFilled = _lastRolesKept = 0;
            var g = PropertyHarvester.GuessRoles(_catParams);
            FillRole(_catType, _parType, priorType.Item1, priorType.Item2, g.Type);
            FillRole(_catLevel, _parLevel, priorLevel.Item1, priorLevel.Item2, g.Level);
            FillRole(_catMat, _parMat, priorMat.Item1, priorMat.Item2, g.Material);
            FillRole(_catCls, _parCls, priorCls.Item1, priorCls.Item2, g.Classification);
            // Never guessed: the Navisworks name is a fine default, so this stays the user's call.
            FillRole(_catName, _parName, priorName.Item1, priorName.Item2, default);

            string read = sample.Count < items.Count
                ? $"a sample of {sample.Count:N0} of {items.Count:N0} {sourceLabel} elements"
                : $"all {items.Count:N0} {sourceLabel} elements";
            SetStatus($"Scanned {cats.Count} categories / {allParams.Count} properties from {read} — {_lastRolesFilled} role(s) filled, {_lastRolesKept} kept.");
            return true;
        }

        /// <summary>
        /// Navisworks' own tree bookkeeping, which describes the VIEWER's model rather than the
        /// building: "Item" is the node itself, "Transform" its placement, "Geometry" its render
        /// state. All three are noise in an IFC deliverable and all three were exported by default.
        /// Only the exact names — a user category that merely contains one of these words is theirs.
        /// </summary>
        private static readonly HashSet<string> Bookkeeping =
            new HashSet<string>(new[] { "Item", "Transform", "Geometry" }, StringComparer.OrdinalIgnoreCase);

        private static bool IsBookkeeping(string category) => Bookkeeping.Contains(category);

        /// <summary>At most <paramref name="cap"/> items, taken with an even stride
        /// so the sample spans the whole list instead of its first slice.</summary>
        private static List<ModelItem> Spread(List<ModelItem> src, int cap)
        {
            if (cap <= 0 || src.Count <= cap) return src;
            var picked = new List<ModelItem>(cap);
            double step = (double)src.Count / cap;      // > 1 here, so no index repeats
            for (int i = 0; i < cap; i++) picked.Add(src[(int)(i * step)]);
            return picked;
        }

        private void PopulateParamCombo(ComboBox par, string categoryText, bool keepText)
        {
            string prev = par.Text;
            var names = (string.IsNullOrEmpty(categoryText) || categoryText == AnyCat)
                ? _catParams.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
                : (_catParams.TryGetValue(categoryText, out var l) ? new List<string>(l) : new List<string>());
            names.Insert(0, "");
            par.ItemsSource = names;
            if (keepText) par.Text = prev;
        }

        /// <summary>A role counts as "set" when its property was non-blank BEFORE
        /// the scan mutated the combo sources; a set role is the user's decision
        /// and survives every rescan. Prior values are passed in rather than read
        /// from the combos because the _roleCats rebuild may already have blanked
        /// them (see the capture above).</summary>
        private void FillRole(ComboBox cat, ComboBox par, string priorCat, string priorPar, PropRef guess)
        {
            if (!string.IsNullOrWhiteSpace(priorPar))
            {
                // Restore the choice on top of the refreshed suggestion lists.
                cat.Text = priorCat ?? "";
                PopulateParamCombo(par, cat.Text, keepText: false);
                par.Text = priorPar;
                _lastRolesKept++;
                return;
            }
            _fillingRole = true;
            try
            {
                cat.Text = string.IsNullOrEmpty(guess.Category) ? "" : guess.Category;
                PopulateParamCombo(par, cat.Text, keepText: false);
                par.Text = guess.Name ?? "";
            }
            finally { _fillingRole = false; }
            if (!string.IsNullOrWhiteSpace(par.Text)) { _lastRolesFilled++; _roleAuto[par] = par.Text; }
            else _roleAuto.Remove(par);
            UpdateRoleTag(par);
        }

        private PropertyRoles BuildRoles() => new PropertyRoles
        {
            Type = MakeRef(_catType, _parType),
            Level = MakeRef(_catLevel, _parLevel),
            Material = MakeRef(_catMat, _parMat),
            Classification = MakeRef(_catCls, _parCls),
            Name = MakeRef(_catName, _parName)
        };
        private static PropRef MakeRef(ComboBox cat, ComboBox par)
        {
            string c = cat.Text == AnyCat ? "" : (cat.Text ?? "").Trim();
            return new PropRef(c, (par.Text ?? "").Trim());
        }

        private List<ParamMapRule> BuildParamRules()
        {
            var rules = new List<ParamMapRule>();
            foreach (var row in _paramRows)
            {
                var src = (row.SourceProperty ?? "").Trim();
                if (string.IsNullOrEmpty(src)) continue;
                var cat = (row.SourceCategory ?? "").Trim();
                rules.Add(new ParamMapRule
                {
                    SourceCategory = cat == AnyCat ? "" : cat,
                    Source = src,
                    TargetPset = (row.TargetPset ?? "").Trim(),
                    TargetName = (row.TargetName ?? "").Trim()
                });
            }
            return rules;
        }

        // ── sets (one refresh for every surface that shows them) ────────────────
        /// <summary>
        /// Re-reads the document's saved/search sets and updates the three places
        /// they appear: the saved-set combo, the batch checklist and the mapping
        /// dropdowns. Cheap (a document read, no tree walk), so it runs on load,
        /// on scope change, when the Mapping tab opens, and at the start of Smart
        /// setup — which is why no tab needs a Refresh button any more.
        /// Batch ticks and the combo selection survive by name.
        /// </summary>
        private void RefreshSets()
        {
            var doc = NavApp.ActiveDocument;
            // Re-reading the document's sets is exactly the moment to assume set MEMBERSHIP may
            // have moved under us — a search set re-evaluates against the model, and nothing tells
            // us when the model changed. Rule EDITS are caught separately, by the rules signature.
            _scan.Bind(doc);
            _scan.DropMaps();
            _sets = doc != null ? ItemCollector.GetSelectionSets(doc) : new List<SelectionSet>();

            // saved-set combo, keeping the current pick if it still exists
            string? prevPick = CmbSavedSet.SelectedItem as string;
            CmbSavedSet.Items.Clear();
            if (_sets.Count == 0) CmbSavedSet.Items.Add("(no saved sets in document)");
            else foreach (var s in _sets) CmbSavedSet.Items.Add(s.DisplayName ?? "(set)");
            int keep = prevPick == null ? -1 : CmbSavedSet.Items.IndexOf(prevPick);
            CmbSavedSet.SelectedIndex = keep >= 0 ? keep : 0;

            // batch checklist, keeping ticks by name
            var ticked = new HashSet<string>(_batchItems.Where(b => b.Checked).Select(b => b.Name), StringComparer.Ordinal);
            _batchItems.Clear();
            foreach (var s in _sets)
            {
                string n = s.DisplayName ?? "(set)";
                _batchItems.Add(new CheckItem(n, ticked.Contains(n)));
            }
            UpdateBatchNames();
            if (_scopeHints.Count == 5) _scopeHints[3].Text = _sets.Count == 1 ? "1 set" : $"{_sets.Count:N0} sets";
            UpdateBatchCount();

            // Mapping dropdown catalog — synced IN PLACE, never Clear()ed. The
            // grid's set column is a TwoWay SelectedItem binding; emptying the
            // list even momentarily makes WPF push null into row.Set, i.e. a
            // refresh would silently wipe loaded mapping rules. Names referenced
            // by existing rules stay listed even when the document no longer has
            // that set (matching how profile load has always behaved).
            var wanted = new List<string>();
            foreach (var s in _sets) wanted.Add(s.DisplayName ?? "(set)");
            foreach (var r in _mapRows)
                if (!string.IsNullOrWhiteSpace(r.Set) && !wanted.Contains(r.Set)) wanted.Add(r.Set);
            for (int i = MapRow.SharedSets.Count - 1; i >= 0; i--)
                if (!wanted.Contains(MapRow.SharedSets[i])) MapRow.SharedSets.RemoveAt(i);
            foreach (var n in wanted)
                if (!MapRow.SharedSets.Contains(n)) MapRow.SharedSets.Add(n);
        }

        /// <summary>
        /// Mapping-grid rows resolved to their sets. A row may assign a class, a classification
        /// code, or both — a row with only a classification is still a rule, so it is no longer
        /// dropped for having an empty class column.
        /// </summary>
        private List<ItemCollector.SetRule> BuildSetRules()
        {
            var rules = new List<ItemCollector.SetRule>();
            foreach (var row in _mapRows)
            {
                var setName = row.Set;
                if (string.IsNullOrWhiteSpace(setName)) continue;
                var cls = (row.IfcClass ?? "").Trim();
                var code = (row.Classification ?? "").Trim();
                bool hasClass = cls.Length > 0 && TypeMapping.Catalog.ContainsKey(cls);
                if (!hasClass && code.Length == 0) continue;
                var set = _sets.FirstOrDefault(s => string.Equals(s.DisplayName, setName, StringComparison.Ordinal));
                if (set == null) continue;
                rules.Add(new ItemCollector.SetRule(set, hasClass ? TypeMapping.Encode(cls, row.Predefined) : "", code));
            }
            return rules;
        }

        // ── export action ──────────────────────────────────────────────────────
        private void OnExport(object sender, RoutedEventArgs e)
        {
            _cancelRequested = false;
            try
            {
                var doc = NavApp.ActiveDocument;
                if (doc == null || doc.Models.Count == 0) { SetStatus("Open a model first."); return; }
                // Reset BEFORE scope collection — the collector counts recovered branch geometry.
                BIMCamel.Geometry.ExportIssues.Reset();
                RuleClassifier.Reset();

                var schema = CmbSchema.SelectedIndex == 1 ? IfcSchema.Ifc2x3 : IfcSchema.Ifc4;
                string schemaName = schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2x3";
                bool batch = ScopeIndex() == 4;
                long splitLimit = SplitLimitBytes();
                (double unitScale, string unitName) = ResolveUnits(doc);
                var coords = BuildCoords();
                var names = new SpatialNames
                {
                    Project = Def(TxtProject.Text, ModelBaseName(doc)), Site = Def(TxtSite.Text, "Site"),
                    Building = Def(TxtBuilding.Text, "Building"), Storey = Def(TxtStorey.Text, "Storey"),
                    // Real storey elevations, when the model's grids name the levels.
                    LevelElevations = ModelSurvey.LevelElevations(doc, unitScale),
                    ClassificationSystem = (TxtClassSystem.Text ?? "").Trim(),
                    ClassificationSource = (TxtClassSource.Text ?? "").Trim(),
                    ClassificationEdition = (TxtClassEdition.Text ?? "").Trim(),
                    ClassificationEditionDate = (TxtClassEditionDate.Text ?? "").Trim(),
                    ClassificationLocation = (TxtClassLocation.Text ?? "").Trim(),
                    GroupEntity = CmbGroups.SelectedIndex switch { 1 => "IFCGROUP", 2 => "IFCSYSTEM", 3 => "IFCZONE", _ => "" }
                };
                var setRules = BuildSetRules();
                double weldTolMetres = Quality switch { 1 => 1e-3, 2 => 1e-6, _ => 1e-4 };
                int coordDecimals = Quality switch { 1 => 3, 2 => 6, _ => 4 };

                if (batch) { RunBatchExport(doc, schema, schemaName, unitScale, unitName, coords, names, setRules, weldTolMetres, coordDecimals, splitLimit); return; }

                bool zip = ChkZip.IsChecked == true;
                string ext = zip ? "ifczip" : "ifc";
                using var sfd = new WF.SaveFileDialog
                {
                    Title = "Export IFC",
                    Filter = zip ? "Compressed IFC (*.ifczip)|*.ifczip|IFC file (*.ifc)|*.ifc"
                                 : "IFC file (*.ifc)|*.ifc|Compressed IFC (*.ifczip)|*.ifczip",
                    FileName = $"{FileNameFor(ModelBaseName(doc), schemaName, null)}.{ext}",
                    InitialDirectory = StartFolder(),
                    DefaultExt = ext
                };
                if (sfd.ShowDialog() != WF.DialogResult.OK) return;
                // The extension the user actually settled on decides the form — the checkbox only
                // seeds the dialog, and a typed ".ifc" must not be silently zipped (v6 Z1).
                string outPath = sfd.FileName;

                var scanSw = Stopwatch.StartNew();
                ET.ResetScan();
                ReleaseModelItems();
                BeginBusyMarquee("Collecting elements…");
                // The walk no longer reads a bounding box per leaf for the base point: on a large
                // selection that was most of the Collect time. ScopeMin below samples instead.
                long tc = ET.Now;
                var items = ResolveScope(doc, CollectTick);
                ET.CollectTicks += ET.Now - tc;
                if (items == null) return;
                if (items.Count == 0) { SetStatus("No geometry elements in scope."); return; }

                // One ItemKey memo shared between rule resolution and the per-element lookups in
                // the extractor — and now across the whole session, not just this button (v5 S2).
                var maps = ResolveMaps(doc, setRules);
                var opts = BuildExtractOptions(maps);
                opts.KeyCache = _scan.Keys;

                var sm = ScopeMin(doc, items, coords, n => CollectTick(n));
                var geomMin = (sm.x * unitScale, sm.y * unitScale, sm.z * unitScale);
                scanSw.Stop();

                BeginBusy(items.Count);
                ExportTiming.Reset();
                long baseHeap = GC.GetTotalMemory(false); _peakHeap = baseHeap;
                var heapTimer = new System.Threading.Timer(_ => { long m = GC.GetTotalMemory(false); if (m > _peakHeap) _peakHeap = m; }, null, 0, 200);
                var sw = Stopwatch.StartNew();

                var summary = new ExportSummary();
                RunOneExport(outPath, schema, items, unitScale, coords, names, opts, weldTolMetres, coordDecimals, geomMin, splitLimit, summary, "Exporting");

                sw.Stop(); heapTimer.Dispose(); Probe.EndExport(_scan.Keys.Count);
                FinishReport(summary, schemaName, ScopeLabel(), unitName, items.Count, scanSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, setRules.Count, baseHeap);
            }
            catch (OperationCanceledException)
            {
                // Only reachable from the collect phase — BeginBusy withdraws Stop
                // before the first byte is written — so "no file" is a fact, not a hope.
                SetStatus("Export stopped while collecting — no file was written.");
            }
            catch (Exception ex)
            {
                // The plain message first, where the user is already looking; the stack trace goes
                // below it in the report, for Copy or Save report, rather than in a modal dialog.
                SetStatus("Export failed: " + ex.Message);
                SetReport("Export FAILED\n──────────────────────────────\n" + ex.Message
                          + "\n\nNo usable file was written. If this keeps happening, use Save report or Copy and send the details below with your issue.\n\n"
                          + "Details\n──────────────────────────────\n" + ex);
                ShowReport();
            }
            finally { EndBusy(); }
        }

        private void RunBatchExport(Document doc, IfcSchema schema, string schemaName, double unitScale, string unitName,
            CoordOptions coords, SpatialNames names, List<ItemCollector.SetRule> setRules, double weldTolMetres, int coordDecimals, long splitLimit)
        {
            var chosen = new List<SelectionSet>();
            for (int i = 0; i < _batchItems.Count && i < _sets.Count; i++)
                if (_batchItems[i].Checked) chosen.Add(_sets[i]);
            if (chosen.Count == 0) { SetStatus("Tick at least one set to export (batch scope)."); return; }

            // The Output folder field, when it names a folder that exists; otherwise ask.
            string folder = (TxtBatchFolder.Text ?? "").Trim();
            if (folder.Length == 0 || !System.IO.Directory.Exists(folder))
            {
                using var fbd = new WF.FolderBrowserDialog { Description = "Choose an output folder — one IFC per set", SelectedPath = folder.Length > 0 ? folder : StartFolder() };
                if (fbd.ShowDialog() != WF.DialogResult.OK) return;
                folder = fbd.SelectedPath;
                TxtBatchFolder.Text = folder;
            }

            var scanSw = Stopwatch.StartNew();
            ET.ResetScan();
            ReleaseModelItems();
            var maps = ResolveMaps(doc, setRules);
            var opts = BuildExtractOptions(maps);
            opts.KeyCache = _scan.Keys;

            BeginBusyMarquee("Preparing batch…");
            var mc = ModelMinCorner(doc);
            var geomMin = (mc.Item1 * unitScale, mc.Item2 * unitScale, mc.Item3 * unitScale);
            scanSw.Stop();

            BeginBusy(1);
            ExportTiming.Reset();
            long baseHeap = GC.GetTotalMemory(false); _peakHeap = baseHeap;
            var heapTimer = new System.Threading.Timer(_ => { long m = GC.GetTotalMemory(false); if (m > _peakHeap) _peakHeap = m; }, null, 0, 200);
            var sw = Stopwatch.StartNew();

            var summary = new ExportSummary();
            int setNum = 0, totalItems = 0, skipped = 0;
            foreach (var set in chosen)
            {
                setNum++;
                SetStatus($"Set {setNum}/{chosen.Count}: {set.DisplayName} — collecting…"); PumpUi(Dispatcher);
                var items = ItemCollector.GetItemsFromSet(doc, set, CollectTick);
                if (items.Count == 0) { skipped++; continue; }
                totalItems += items.Count;
                // Batch honours the compression choice too (v6 Z1) — one archive per set.
                string baseName = FileNameFor(ModelBaseName(doc), schemaName, set.DisplayName ?? $"set{setNum}")
                                  + (ChkZip.IsChecked == true ? BIMCamel.Ifc.IfcSource.ZipExtension : ".ifc");
                RunOneExport(System.IO.Path.Combine(folder, baseName), schema, items, unitScale, coords, names, opts, weldTolMetres, coordDecimals, geomMin, splitLimit, summary, $"Set {setNum}/{chosen.Count}");
            }
            sw.Stop(); heapTimer.Dispose(); Probe.EndExport(_scan.Keys.Count);
            string scopeLabel = $"Batch — {chosen.Count} set(s)" + (skipped > 0 ? $", {skipped} empty skipped" : "");
            FinishReport(summary, schemaName, scopeLabel, unitName, totalItems, scanSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, setRules.Count, baseHeap);
        }

        private long SplitLimitBytes()
        {
            if (ChkSplit.IsChecked != true) return 0;
            return double.TryParse(TxtSplitMb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var mb) && mb > 0
                ? (long)(mb * 1024 * 1024) : 0;
        }

        /// <summary>Elements with more parts than this are merged into one shape when
        /// "Merge multi-part elements" is on. Small assemblies keep per-part instancing, which is
        /// what shares a bolt between different elements.</summary>
        private const int MergePartsAbove = 16;

        private ExtractOptions BuildExtractOptions(ItemCollector.SetMaps maps) => new ExtractOptions
        {
            Props = ChkProps.IsChecked == true, Materials = ChkMaterials.IsChecked == true,
            PsetFilter = SelectedCategories(), ClassMap = maps.Class, ClassificationMap = maps.Classification,
            GroupMap = CmbGroups.SelectedIndex > 0 ? maps.Group : null,
            ParamMap = BuildParamRules(), Roles = BuildRoles(), PropRules = BuildPropRules(),
            AutoClassify = ChkAutoClassify.IsChecked == true,
            CategoryCache = new Dictionary<ModelItem, string?>()
        };

        /// <summary>Complete property-rule rows, or null when there are none (the extractors skip
        /// the whole step on null).</summary>
        private List<PropertyRule>? BuildPropRules()
        {
            var rules = new List<PropertyRule>();
            foreach (var r in _propRules)
            {
                var prop = (r.Property ?? "").Trim(); var cls = (r.IfcClass ?? "").Trim(); var code = (r.Classification ?? "").Trim();
                bool hasClass = cls.Length > 0 && TypeMapping.Catalog.ContainsKey(cls);
                if (prop.Length == 0 || (r.Value ?? "").Trim().Length == 0 || (!hasClass && code.Length == 0)) continue;
                rules.Add(new PropertyRule
                {
                    Source = new PropRef(r.Category == AnyCat ? "" : (r.Category ?? "").Trim(), prop),
                    Test = r.TestKind, Value = r.Value!.Trim(),
                    ClassKey = hasClass ? TypeMapping.Encode(cls, r.Predefined) : "", Classification = code
                });
            }
            return rules.Count > 0 ? rules : null;
        }

        /// <summary>
        /// Drop every ModelItem the session caches hold, then force a full GC, so the export starts
        /// with no wrappers left over from earlier runs. A second export in the same session read
        /// the tree several times slower (Children about 16×, Parent about 2×) while COM, property
        /// and triangle calls kept their speed, and it started with five times the managed heap.
        /// Live or unfinalized wrappers slowing tree navigation is the suspect; the report's
        /// "release + full GC" and "released" lines show what this cost and freed. The string-keyed
        /// set maps survive, so unchanged set rules still skip FindAll.
        /// </summary>
        private void ReleaseModelItems()
        {
            long t = Probe.Now;
            long before = GC.GetTotalMemory(false);
            int held = _scan.Keys.Count + ItemTree.Count;
            _scan.ReleaseItems();
            ItemTree.Clear();
            RuleClassifier.ReleaseItems();
            PropertyHarvester.ResetCache();
            ItemBox.Reset();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Probe.Released(held, before, t);
        }

        private void RunOneExport(string basePath, IfcSchema schema, List<ModelItem> items, double unitScale, CoordOptions coords, SpatialNames names,
            ExtractOptions opts, double weldTolMetres, int coordDecimals, (double x, double y, double z) geomMin, long splitLimit, ExportSummary summary, string verb)
        {
            SwitchToDeterminate(items.Count);
            bool quantities = ChkQuantities.IsChecked == true;
            opts.ClassifyUnitScale = unitScale;   // the rules' shape check reads item boxes in model units
            string author = Environment.UserName;
            bool shareTypeProps = ChkShareTypeProps.IsChecked == true;   // v6 Z3
            PropertyHarvester.SkipEmptyValues = ChkSkipEmptyProps.IsChecked == true;   // v6 Z4
            SyncPropSource();   // also drops ancestor reads cached by an earlier run
            // v6 Z7. Expressed in the vertices' own units, the same convention as WeldTol: the
            // instanced path reads in metres, the plain path in model units.
            double minSizeMetres = ChkMinSize.IsChecked == true
                && double.TryParse(TxtMinSizeMm.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var mm) && mm > 0
                ? mm / 1000.0 : 0;
            double simplifyMetres = ChkSimplify.IsChecked == true
                && double.TryParse(TxtSimplifyMm.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var smm) && smm > 0
                ? smm / 1000.0 : 0;

            // v5 E7: the extractor runs here, on the Navisworks UI thread (the read API is STA and
            // cannot move); the writer runs behind ExportPipeline on a background thread. Both
            // exporter entry points already take a lazy IEnumerable, so the seam needed no change
            // — the elements simply arrive over a bounded queue instead of a direct pull.
            if (ChkInstancing.IsChecked == true)
            {
                opts.WeldTol = weldTolMetres;
                // Instanced: quantities are measured per UNIQUE geometry inside the writer, not per
                // fragment here — 6.7x fewer measurements on the prova model (v5 E4).
                opts.QtyScale = 0;
                opts.FastInstancing = ChkFastInstancing.IsChecked == true;
                opts.MinFragmentSize = minSizeMetres;
                opts.SimplifyTol = simplifyMetres;
                int mergeAbove = ChkMergeParts.IsChecked == true ? MergePartsAbove : 0;
                var stream = InstancedExtractor.ExtractStream(items, unitScale, opts, p => Tick(p, items.Count, verb));
                ExportPipeline.Run(stream, s =>
                    IfcExporter.ExportInstanced(basePath, schema, s, author, unitScale, coords, quantities, coordDecimals, geomMin, names, splitLimit, summary, shareTypeProps, mergeAbove));
            }
            else
            {
                opts.WeldTol = weldTolMetres / unitScale;
                // Plain: one element, one weld, one measurement — folded into the weld (v5 E4).
                opts.QtyScale = quantities ? unitScale : 0;
                opts.MinFragmentSize = minSizeMetres / unitScale;
                opts.SimplifyTol = simplifyMetres / unitScale;
                var stream = MeshExtractor.ExtractStream(items, opts, p => Tick(p, items.Count, verb));
                ExportPipeline.Run(stream, s =>
                    IfcExporter.Export(basePath, schema, s, author, unitScale, coords, quantities, coordDecimals, geomMin, names, splitLimit, summary, shareTypeProps));
            }
        }

        private void FinishReport(ExportSummary summary, string schemaName, string scopeLabel, string unitName, int scopeItemCount, long scanMs, long exportMs, int ruleCount, long baseHeap)
        {
            string validation = "not run";
            if (ChkValidate.IsChecked == true && summary.Files.Count > 0)
            {
                SetStatus("Validating…"); PumpUi(Dispatcher);
                var issues = new List<string>();
                // Timed since v5 E6: validation streams the whole output twice, so on a multi-GB
                // export it is wall clock the user waits through — and it sits OUTSIDE the export
                // stopwatch, so until now the report never admitted it existed.
                long tv = ET.Now;
                foreach (var f in summary.Files) { var iss = IfcValidator.Validate(f); if (iss.Count > 0) issues.Add(System.IO.Path.GetFileName(f) + ": " + string.Join("; ", iss)); }
                ET.ValidateTicks += ET.Now - tv;
                validation = (issues.Count == 0 ? $"passed ({summary.Files.Count} file(s))" : "ISSUES — " + string.Join(" | ", issues))
                             + $"  [{ET.Ms(ET.ValidateTicks):N0} ms]";
            }
            string profile = "";
            if (ChkProfile.IsChecked == true && summary.Files.Count > 0)
            {
                SetStatus("Profiling output size…"); PumpUi(Dispatcher);
                try { profile = (summary.Files.Count > 1 ? $"(profile of first of {summary.Files.Count} files)\n" : "") + IfcProfiler.Profile(summary.Files[0]); }
                catch (Exception px) { profile = "profile failed: " + px.Message; }
            }
            SetReport(BuildReport(summary, schemaName, scopeLabel, unitName, scopeItemCount, scanMs, exportMs, ruleCount, validation, profile, baseHeap, _peakHeap));
            SetStatus($"Done · {summary.FileCount} file(s) · {summary.ElementCount:N0} elements · {summary.FileSizeBytes / 1024.0:N0} KB · {exportMs:N0} ms");

            ShowOutputs(summary.Files);
        }

        private string ScopeLabel() => ScopeIndex() switch
        {
            1 => "Current selection", 2 => "Active section box", 3 => "Saved set / search set",
            4 => "Multiple sets (batch)", _ => "Whole model (visible)"
        };

        private static string Sanitize(string name)
        {
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            name = name.Trim();
            return name.Length == 0 ? "set" : name;
        }

        /// <param name="wantMinCorner">Ask the walk to accumulate the scope's minimum corner as it
        /// goes (v5 S1). Only the export needs it, and only when the base point is the geometry
        /// origin — every other caller passes false and pays nothing.</param>
        private List<ModelItem>? ResolveScope(Document doc, Action<int>? onProgress = null, bool wantMinCorner = false)
        {
            // Reset here, not only inside the visible-only walks: scopes that
            // include hidden items never touch the counter, and the pre-flight
            // panel reads it right after this call.
            ItemCollector.HiddenSkipped = 0;
            ItemCollector.MinCornerValid = false;
            switch (ScopeIndex())
            {
                case 1:
                    var sel = doc.CurrentSelection.SelectedItems;
                    if (sel == null || sel.Count == 0) { SetStatus("Nothing selected. Select elements or choose another scope."); return null; }
                    return ItemCollector.ResolveLeaves(sel, onProgress, wantMinCorner);
                case 2:
                    try { return ItemCollector.GetItemsInSectionBox(doc, onProgress); }
                    // Stop is not a "no section box" problem: let it unwind rather
                    // than popping a message box that blames the user's setup.
                    catch (OperationCanceledException) { throw; }
                    catch (Exception sx) { SetStatus(sx.Message); WF.MessageBox.Show(sx.Message, "BIMCamel", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information); return null; }
                case 3:
                    int si = CmbSavedSet.SelectedIndex;
                    if (_sets.Count == 0 || si < 0 || si >= _sets.Count) { SetStatus("Pick a saved set (Scope = Saved set)."); return null; }
                    return ItemCollector.GetItemsFromSet(doc, _sets[si], onProgress, wantMinCorner);
                case 4:
                {
                    // The union of the ticked batch sets. Export never reaches
                    // this (RunBatchExport iterates per set), but PREVIEW does —
                    // and it used to fall through to whole-model, so the numbers
                    // said "Multiple sets (batch)" while measuring everything.
                    var chosen = new List<SelectionSet>();
                    for (int i = 0; i < _batchItems.Count && i < _sets.Count; i++)
                        if (_batchItems[i].Checked) chosen.Add(_sets[i]);
                    if (chosen.Count == 0) { SetStatus("Tick at least one set (batch scope)."); return null; }
                    var union = new List<ModelItem>();
                    var seenItems = new HashSet<ModelItem>();   // value-equal across walks
                    foreach (var set in chosen)
                        foreach (var it in ItemCollector.GetItemsFromSet(doc, set, onProgress))
                            if (seenItems.Add(it)) union.Add(it);
                    // Several walks, then a dedup: the last walk's min corner describes only its own
                    // set, not the union. Fall back to the explicit pass (v5 S1).
                    ItemCollector.MinCornerValid = false;
                    return union;
                }
                default:
                    return ItemCollector.GetVisibleLeafItemsWithGeometry(doc, onProgress, wantMinCorner);
            }
        }

        /// <summary>
        /// Resolve the mapping rules to item-key maps, reusing the last resolution when the rules
        /// are unchanged (v5 S2). Every caller — preview, export, batch — goes through here, so the
        /// model-wide <c>Search.FindAll</c> behind each set runs once per rule list, not once per
        /// button press.
        /// </summary>
        private ItemCollector.SetMaps ResolveMaps(Document doc, List<ItemCollector.SetRule> rules)
        {
            _scan.Bind(doc);
            if (rules.Count == 0) return new ItemCollector.SetMaps();

            var cached = _scan.Maps(rules);
            if (cached != null) return cached;

            SetStatus("Resolving mapping sets…"); PumpUi(Dispatcher);
            long t = ET.Now;
            var maps = ItemCollector.BuildSetMaps(doc, rules, CollectTick, _scan.Keys);
            ET.SetResolveTicks += ET.Now - t;
            ET.SetsResolved = rules.Count;
            _scan.StoreMaps(rules, maps);
            return maps;
        }

        /// <summary>
        /// The scope's minimum corner in MODEL units, for the geometry-origin base point.
        ///
        /// It only has to be NEAR the geometry: the placement chain puts every element back at its
        /// world position whatever the origin is, so the corner decides how many digits the stored
        /// coordinates carry and nothing else. Reading every leaf's <c>BoundingBox()</c> for an
        /// exact minimum cost a COM call per element (most of the Collect time on a large
        /// selection), so:
        ///  1. Not the geometry origin (model origin, custom point) — not needed at all.
        ///  2. Whole model — the model roots' boxes, a handful of calls; the base-point preview has
        ///     always shown exactly this value.
        ///  3. Any narrower scope — the minimum over an even-strided sample of at most
        ///     <see cref="MinCornerSample"/> leaves, so a selection far from the rest of a
        ///     federation keeps small coordinates. A leaf below the sampled corner simply gets a
        ///     slightly negative local coordinate, which IFC allows.
        /// </summary>
        private (double x, double y, double z) ScopeMin(Document doc, List<ModelItem> items, CoordOptions coords, Action<int>? onProgress)
        {
            if (coords.Mode != BasePointMode.GeometryOrigin) return (0, 0, 0);

            long t = ET.Now;
            try
            {
                if (ScopeIndex() == 0) { var m = ModelMinCorner(doc); return (m.Item1, m.Item2, m.Item3); }
                return ItemCollector.ScopeMinCorner(Spread(items, MinCornerSample), onProgress);
            }
            finally { ET.MinCornerTicks += ET.Now - t; }
        }

        private const int MinCornerSample = 2048;

        private CoordOptions BuildCoords() => new CoordOptions
        {
            Mode = CmbBasePoint.SelectedIndex switch { 1 => BasePointMode.ModelOrigin, 2 => BasePointMode.Custom, _ => BasePointMode.GeometryOrigin },
            WriteGeoref = ChkGeoref.IsChecked == true,
            CustomEastings = ParseD(TxtE.Text), CustomNorthings = ParseD(TxtN.Text), CustomElevation = ParseD(TxtElev.Text),
            SurveyEastings = ParseD(TxtSurveyE.Text), SurveyNorthings = ParseD(TxtSurveyN.Text), SurveyElevation = ParseD(TxtSurveyElev.Text),
            RotationDeg = ParseD(TxtRot.Text),
            CrsName = (TxtCrs.Text ?? "").Trim()
        };

        private HashSet<string>? SelectedCategories()
        {
            if (ChkProps.IsChecked != true) return null;
            if (_cats.Count == 0) return null; // not scanned → all
            return new HashSet<string>(_cats.Where(c => c.Checked).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        }

        // ── progress feedback ───────────────────────────────────────────────────
        private void BeginBusy(int max)
        {
            BtnExport.IsEnabled = false; Tabs.IsEnabled = false;
            // Title-bar buttons sit outside Tabs; the UI pumps messages during
            // export, so they must be locked too or a profile load could mutate
            // settings mid-export.
            BtnSaveProfile.IsEnabled = BtnLoadProfile.IsEnabled = false;
            Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = false;
            Progress.Minimum = 0; Progress.Maximum = Math.Max(1, max); Progress.Value = 0;
            // Writing starts here. Withdraw Stop and drop any pending request, so a
            // click that landed during the read phase cannot surface mid-file.
            _cancelRequested = false; ShowCancel(false);
            _tickClock.Restart(); _lastTickMs = 0; Mouse.OverrideCursor = Cursors.Wait;
        }
        private void Tick(int done, int max, string verb)
        {
            long ms = _tickClock.ElapsedMilliseconds;
            if (done != max && ms - _lastTickMs < 1000) return;
            _lastTickMs = ms;
            if (done <= Progress.Maximum) Progress.Value = done;
            LblStatus.Text = $"{verb} {done}/{max}…";
            long t = ET.Now; PumpUi(Dispatcher); ET.UiTicks += ET.Now - t; ET.UiPumps++;
        }
        private void CollectTick(int visited)
        {
            // Before the throttle, not after: the click that set the flag arrived
            // during a pump, and making the user wait out the rest of the interval
            // is the difference between "Stop" and "Stop, eventually".
            ThrowIfCancelled();
            // 400 ms, not 120: each pump lets Navisworks repaint, and with a large selection
            // highlighted a repaint is not free. On a large model a pump can cost
            // tens of milliseconds; at 120 ms that is a noticeable share of a long walk. Stop still lands
            // within half a second.
            long ms = _tickClock.ElapsedMilliseconds;
            if (ms - _lastTickMs < 400) return;
            _lastTickMs = ms;
            LblStatus.Text = $"Scanning… {visited:N0} items"; PumpUi(Dispatcher);
        }

        // ── stopping a scan ───────────────────────────────────────────────────────
        // Everything long here runs on the UI thread, because the Navisworks API is
        // STA — the walks stay responsive only by pumping the message loop from
        // their progress ticks. That pump is also what makes Stop possible at all:
        // the click is dispatched from inside the walk and sets this flag, and the
        // next tick throws to unwind. No background thread, no torn COM state.
        private bool _cancelRequested;

        private void OnCancelWork(object sender, RoutedEventArgs e)
        {
            _cancelRequested = true;
            // The Btn template has no disabled visual, so greying it out alone
            // would read as "the click did nothing" — the label carries the
            // acknowledgement instead. Stopping lands on the next tick, which
            // on a slow set resolution can be a second or two away.
            BtnCancel.IsEnabled = false;
            BtnCancel.Content = "Stopping…";
            SetStatus("Stopping…");
        }

        private void ThrowIfCancelled()
        {
            if (_cancelRequested) throw new OperationCanceledException();
        }

        /// <summary>Stop is offered while we are READING the model and never while
        /// writing: a half-written IFC is worse than a slow one, so the write phase
        /// (<see cref="BeginBusy"/>) takes the button away again.</summary>
        private void ShowCancel(bool on)
        {
            BtnCancel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Restore the label too, or a run that followed a stopped one would
            // offer a button permanently reading "Stopping…".
            if (on && !_cancelRequested) { BtnCancel.IsEnabled = true; BtnCancel.Content = "Stop"; }
        }

        private void BeginBusyMarquee(string status, bool cancelable = false)
        {
            BtnExport.IsEnabled = false; Tabs.IsEnabled = false;
            BtnSaveProfile.IsEnabled = BtnLoadProfile.IsEnabled = false;
            Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true;
            ShowCancel(cancelable);
            _tickClock.Restart(); _lastTickMs = 0;
            Mouse.OverrideCursor = Cursors.Wait; SetStatus(status); PumpUi(Dispatcher);
        }
        private void SwitchToDeterminate(int max)
        {
            Progress.IsIndeterminate = false; Progress.Minimum = 0; Progress.Maximum = Math.Max(1, max); Progress.Value = 0;
            _tickClock.Restart(); _lastTickMs = 0;
        }
        // True while Smart setup chains several self-bracketing steps: their
        // interim EndBusy calls become no-ops so the UI stays locked for the
        // whole run, and only the chain's own finally really releases it.
        private bool _busyChain;

        private void EndBusy()
        {
            if (_busyChain) return;
            Mouse.OverrideCursor = null; Progress.Visibility = Visibility.Collapsed; Progress.IsIndeterminate = false;
            ShowCancel(false); _cancelRequested = false;
            BtnExport.IsEnabled = true; Tabs.IsEnabled = true;
            BtnSaveProfile.IsEnabled = BtnLoadProfile.IsEnabled = true;
        }

        // Flush the WPF dispatcher (layout/render) then pump the host Win32 loop, so the hosted UI
        // stays live during the synchronous export. Mirrors the old Application.DoEvents() calls.
        private static void PumpUi(Dispatcher d)
        {
            d.Invoke(() => { }, DispatcherPriority.Background);
            WF.Application.DoEvents();
        }

        // ── mapping: auto-propose + pre-export preview ────────────────────────────
        /// <summary>
        /// Proposes a class for every Navisworks set whose name looks like a known category
        /// ("Walls" → Wall, "Doors" → Door…). Only fills rows that have no class yet, and never
        /// silently replaces a decision the user already made — the result is a proposal to
        /// review, which is why it reports how many rows still need attention.
        /// Runs from Smart setup; returns its counts for the summary.
        /// </summary>
        private (int added, int filled, int unmatched) AutoMapSets()
        {
            if (_sets.Count == 0) RefreshSets();
            if (_sets.Count == 0) { SetStatus("No saved sets — create some in Navisworks first."); return (0, 0, 0); }

            // Index existing rows by set so a second run tops up rather than duplicating.
            var existing = new Dictionary<string, MapRow>(StringComparer.Ordinal);
            foreach (var r in _mapRows) if (!string.IsNullOrWhiteSpace(r.Set) && !existing.ContainsKey(r.Set)) existing[r.Set] = r;

            int filled = 0, added = 0, unmatched = 0;
            foreach (var set in _sets)
            {
                string name = set.DisplayName ?? "";
                if (name.Length == 0) continue;
                string? cls = TypeMapping.GuessClass(name);
                if (cls == null) { unmatched++; continue; }

                if (existing.TryGetValue(name, out var row))
                {
                    if (string.IsNullOrWhiteSpace(row.IfcClass)) { row.IfcClass = cls; filled++; }
                }
                else
                {
                    _mapRows.Add(new MapRow { Set = name, IfcClass = cls });
                    added++;
                }
            }

            SetStatus($"Auto-map: {added} rule(s) added, {filled} filled in, {unmatched} set(s) had no obvious class — review before exporting.");
            return (added, filled, unmatched);
        }

        /// <summary>
        /// Resolves the rules against the current scope BEFORE exporting and reports what would
        /// happen: how many elements map, to which classes, how many fall back to proxy, and how
        /// much of the semantics is actually present. Everything here already existed — it was
        /// just never run until after the files were on disk.
        /// </summary>
        /// <param name="scope">Already-resolved scope items, when the caller has
        /// them (Smart setup does). Re-walking the scope here was the single most
        /// expensive duplicate in that chain.</param>
        private string? PreviewMapping(List<ModelItem>? scope = null)
        {
            var doc = NavApp.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { TxtPreview.Text = LblMapSummary.Text = "Open a model first."; return null; }
            try
            {
                BeginBusyMarquee("Checking the export…", cancelable: true);
                var schema = CmbSchema.SelectedIndex == 1 ? IfcSchema.Ifc2x3 : IfcSchema.Ifc4;
                var items = scope ?? ResolveScope(doc, CollectTick);
                if (items == null) { TxtPreview.Text = LblMapSummary.Text = "Pick a scope first."; return null; }

                var rules = BuildSetRules();
                var maps = ResolveMaps(doc, rules);
                var keyCache = _scan.Keys;

                int mapped = 0, unmapped = 0, degraded = 0, classified = 0, walked = 0;
                var byClass = new Dictionary<string, int>(StringComparer.Ordinal);
                // No class or classification rule resolved to anything: every item is unmapped and
                // unclassified by definition, and computing its key (a COM walk for GUID-less items)
                // would only confirm that, once per element in the scope.
                var propRules = BuildPropRules();
                if (!PropertyRules.AnyClassRule(propRules)) propRules = null;
                List<ModelItem>? leftOver = propRules != null ? new List<ModelItem>() : null;
                if (!maps.Any) { unmapped = items.Count; leftOver = propRules != null ? items : null; }
                else foreach (var it in items)
                {
                    // ItemKey is an O(tree-depth) COM walk for GUID-less items;
                    // tick so the UI pumps through large scopes.
                    if ((++walked & 511) == 0) CollectTick(walked);
                    string key = ItemCollector.ItemKey(it, keyCache);
                    if (maps.Classification.ContainsKey(key)) classified++;
                    if (maps.Class.TryGetValue(key, out var ck))
                    {
                        mapped++;
                        string friendly = TypeMapping.Friendly(ck);
                        byClass.TryGetValue(friendly, out int n); byClass[friendly] = n + 1;
                        // The silent trap: a class the user mapped that 2x3 cannot represent.
                        if (schema == IfcSchema.Ifc2x3 && TypeMapping.Catalog.TryGetValue(friendly, out var c) && c.Ifc2x3.Length == 0) degraded++;
                    }
                    else { unmapped++; leftOver?.Add(it); }
                }

                // Property rules fill only what the set rules left empty, so they are tried on the
                // leftovers alone. Reading properties is the slow part, so a large remainder is
                // sampled and the counts scaled.
                int byProp = 0;
                if (propRules != null && leftOver != null && leftOver.Count > 0)
                {
                    SyncPropSource();
                    const int MaxSample = 20000;
                    int step = leftOver.Count > MaxSample ? leftOver.Count / MaxSample : 1, seen = 0;
                    var propClass = new Dictionary<string, int>(StringComparer.Ordinal);
                    int hits = 0, hitsDegraded = 0;
                    for (int i = 0; i < leftOver.Count; i += step)
                    {
                        if ((++seen & 255) == 0) CollectTick(seen);
                        string? ck = null, cc = null;
                        PropertyRules.Apply(leftOver[i], propRules, ref ck, ref cc);
                        if (string.IsNullOrEmpty(ck)) continue;
                        hits++;
                        string friendly = TypeMapping.Friendly(ck!);
                        propClass.TryGetValue(friendly, out int n); propClass[friendly] = n + 1;
                        if (schema == IfcSchema.Ifc2x3 && TypeMapping.Catalog.TryGetValue(friendly, out var c) && c.Ifc2x3.Length == 0) hitsDegraded++;
                    }
                    double scale = seen > 0 ? (double)leftOver.Count / seen : 0;
                    byProp = (int)Math.Round(hits * scale);
                    foreach (var kv in propClass)
                    {
                        byClass.TryGetValue(kv.Key, out int n); byClass[kv.Key] = n + (int)Math.Round(kv.Value * scale);
                    }
                    mapped += byProp; unmapped -= byProp; degraded += (int)Math.Round(hitsDegraded * scale);
                }

                var facts = new PreflightFacts
                {
                    When = DateTime.Now, ScopeLabel = ScopeLabel(), Fingerprint = PreflightFingerprint(),
                    Items = items.Count, Mapped = mapped, Unmapped = unmapped, Degraded = degraded,
                    Classified = classified, Rules = rules.Count + (propRules?.Count ?? 0),
                    ByClass = new Dictionary<string, int>(byClass, StringComparer.Ordinal),
                    HiddenExcluded = ItemCollector.HiddenSkipped,
                };

                var sb = new StringBuilder();
                sb.AppendLine($"Scope       : {ScopeLabel()}   {items.Count:N0} items");
                sb.AppendLine($"Schema      : {(schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2x3")}");
                sb.AppendLine($"Rules       : {rules.Count} set rule(s)" + (propRules != null ? $", {propRules.Count} property rule(s)" : ""));
                sb.AppendLine();
                sb.AppendLine($"Mapped      : {mapped:N0}" + (items.Count > 0 ? $"  ({100.0 * mapped / items.Count:0.#}%)" : ""));
                if (byProp > 0) sb.AppendLine($"     · {byProp:N0} of them by property rules" + (leftOver!.Count > 20000 ? " (estimated from a sample)" : ""));
                int proxy = unmapped + degraded;
                if (proxy > 0)
                {
                    sb.AppendLine($"⚠  Proxy    : {proxy:N0}  ({(items.Count > 0 ? 100.0 * proxy / items.Count : 0):0.#}%) will export as IfcBuildingElementProxy");
                    if (unmapped > 0) sb.AppendLine($"     · {unmapped:N0} match no rule");
                    if (degraded > 0) sb.AppendLine($"     · {degraded:N0} are mapped but their class has no IFC2x3 entity — export as IFC4 to keep them");
                }
                else sb.AppendLine("Proxy       : none");

                if (byClass.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("By IFC class:");
                    foreach (var kv in byClass.OrderByDescending(k => k.Value).Take(12))
                        sb.AppendLine($"  {kv.Key,-30} {kv.Value,9:N0}");
                    if (byClass.Count > 12) sb.AppendLine($"  … and {byClass.Count - 12} more");
                }

                sb.AppendLine();
                sb.AppendLine($"Classified  : {classified:N0} by set rule" + (classified == 0 ? "  (add codes in the Classification column)" : ""));
                sb.Append(RoleCoverage(items, facts));
                sb.Append(GeometryEstimate(items, facts, CollectTick));

                TxtPreview.Text = sb.ToString().Replace("\n", Environment.NewLine);
                SetStatus($"Check: {mapped:N0} mapped, {proxy:N0} proxy of {items.Count:N0} items.");
                LblMapSummary.Text = $"Last check {DateTime.Now:HH:mm}: {mapped:N0} of {items.Count:N0} elements matched a rule, {proxy:N0} will export as proxy. Full breakdown: Export tab, Pre-flight.";
                _preflight = facts;
                RenderPreflight();
                RenderStoreys(doc, facts);
                double pct = items.Count > 0 ? 100.0 * mapped / items.Count : 0;
                return $"Full check     : {mapped:N0} of {items.Count:N0} elements map ({pct:0.#}%), {proxy:N0} proxy — details under Pre-flight";
            }
            catch (OperationCanceledException)
            {
                // Rethrown so a stopped preview inside Smart setup stops the CHAIN
                // instead of reporting itself as a step that merely "did not run".
                // _preflight is untouched — the panel keeps the previous, correctly
                // fingerprinted facts rather than inventing partial ones.
                TxtPreview.Text = LblMapSummary.Text = "The check stopped before it finished.";
                throw;
            }
            catch (Exception ex)
            {
                TxtPreview.Text = LblMapSummary.Text = "The check failed: " + ex.Message;
                SetStatus("Check failed: " + ex.Message);
                return null;
            }
            finally { EndBusy(); }
        }

        /// <summary>
        /// How often each semantic role property is actually present, from a bounded sample —
        /// reading every property on a 500k-element model just to preview would cost as much as
        /// the export itself.
        /// </summary>
        private string RoleCoverage(List<ModelItem> items, PreflightFacts? facts = null)
        {
            var roles = BuildRoles();
            if (!roles.Any || items.Count == 0) return "";
            const int MaxSample = 500;
            int step = items.Count > MaxSample ? items.Count / MaxSample : 1;
            int n = 0, type = 0, level = 0, mat = 0, cls = 0;
            // Distinct level VALUES, not just presence: a wrong Level role often
            // still "has a value" on every element — one bogus storey. The count
            // of distinct storeys is the number that exposes it instantly.
            var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Count; i += step)
            {
                try
                {
                    var rv = PropertyHarvester.ReadRoles(items[i], roles);
                    n++;
                    if (!string.IsNullOrWhiteSpace(rv.Type)) type++;
                    if (!string.IsNullOrWhiteSpace(rv.Level))
                    {
                        level++;
                        string lv = rv.Level.Trim();
                        levels.TryGetValue(lv, out int seen); levels[lv] = seen + 1;
                    }
                    if (!string.IsNullOrWhiteSpace(rv.Material)) mat++;
                    if (!string.IsNullOrWhiteSpace(rv.Classification)) cls++;
                }
                catch { }
            }
            if (facts != null && n > 0)
            {
                facts.RoleSample = n; facts.SamplePartial = step > 1;
                facts.TypePct = 100 * type / n; facts.LevelPct = 100 * level / n;
                facts.MatPct = 100 * mat / n; facts.ClsPct = 100 * cls / n;
                facts.DistinctLevels = levels.Count;
                facts.LevelCounts = levels;
                facts.RolesConfigured = true;
            }
            if (n == 0) return "";
            string Pct(int v) => $"{100.0 * v / n:0}%";
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"Property coverage (sample of {n:N0}):");
            sb.AppendLine($"  Type → IfcElementType          {Pct(type)}");
            sb.AppendLine($"  Level → IfcBuildingStorey      {Pct(level)}");
            sb.AppendLine($"  Material → IfcMaterial         {Pct(mat)}");
            sb.AppendLine($"  Classification property        {Pct(cls)}");
            return sb.ToString();
        }

        /// <summary>
        /// Rough geometry size from Navisworks' own per-item counters. These are metadata reads —
        /// they do NOT walk the triangles, which is the part that dominates a real export — so the
        /// estimate is nearly free even on a very large model.
        /// </summary>
        private static string GeometryEstimate(List<ModelItem> items, PreflightFacts? facts = null, Action<int>? tick = null)
        {
            // "Nearly free" was true per item and false in aggregate: item.Geometry
            // is still a COM property read, and half a million of them is a visible
            // stall at the end of every preview. Above the cap we read a strided
            // sample and scale — the number was always billed as an estimate, and
            // the label now says which kind it is.
            const int MaxSample = 20000;
            var sample = Spread(items, MaxSample);
            long prims = 0, frags = 0; int counted = 0, seen = 0;
            foreach (var it in sample)
            {
                if ((++seen & 1023) == 0) tick?.Invoke(seen);
                try
                {
                    var g = it.Geometry;
                    if (g == null) continue;
                    prims += g.PrimitiveCount; frags += g.FragmentCount; counted++;
                }
                catch { }
            }
            bool sampled = sample.Count < items.Count;
            if (sampled && counted > 0)
            {
                double scale = (double)items.Count / sample.Count;
                prims = (long)(prims * scale); frags = (long)(frags * scale);
            }
            if (facts != null) { facts.Prims = prims; facts.Frags = frags; facts.GeomSampled = sampled; }
            if (counted == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(sampled
                ? $"Geometry     : ~{prims:N0} primitives across ~{frags:N0} fragments (scaled from a {sample.Count:N0}-item sample)"
                : $"Geometry     : {prims:N0} primitives across {frags:N0} fragments ({counted:N0} items)");
            return sb.ToString();
        }

        // ── pre-flight panel ──────────────────────────────────────────────────────
        /// <summary>
        /// What the last mapping preview learned, kept for the pre-flight rows.
        /// Fingerprint records the settings the numbers were computed under, so
        /// the panel can say "settings changed since" instead of quietly showing
        /// counts that no longer describe the export the button would run.
        /// </summary>
        private sealed class PreflightFacts
        {
            public DateTime When;
            public string ScopeLabel = "";
            public string Fingerprint = "";
            public int Items, Mapped, Unmapped, Degraded, Classified, Rules;
            public bool RolesConfigured;
            public int RoleSample, TypePct, LevelPct, MatPct, ClsPct, DistinctLevels;
            public bool SamplePartial;
            public long Prims, Frags;
            /// <summary>Prims/Frags were scaled from a sample, so the pre-flight row
            /// has to hedge them rather than print an estimate as a count.</summary>
            public bool GeomSampled;
            public int HiddenExcluded;
            /// <summary>Per-friendly-class rule-match counts, so the 2x3
            /// degradation can be RE-derived against the schema selected NOW —
            /// baking it at preview time made the row lie after a schema flip.</summary>
            public Dictionary<string, int>? ByClass;
            /// <summary>Storey name → elements carrying it in the role sample.</summary>
            public Dictionary<string, int>? LevelCounts;
        }
        private PreflightFacts? _preflight;

        /// <summary>Everything the preview numbers depend on. Changing any of it
        /// makes the stored facts a description of a different export.</summary>
        private string PreflightFingerprint() =>
            ScopeIndex() + "|" + CmbSavedSet.SelectedIndex + "|" +
            string.Join(",", _batchItems.Where(b => b.Checked).Select(b => b.Name)) + "|" +
            GridToText() + "|" + PropRulesToText() + "|" + RolesToText() + "|" + CmbPropSource.SelectedIndex;

        private void RenderPreflight()
        {
            PreflightHost.Children.Clear();
            _warnTabs.Clear();
            var doc = NavApp.ActiveDocument;
            bool schema2x3 = CmbSchema.SelectedIndex == 1;
            string schemaName = schema2x3 ? "IFC2x3" : "IFC4";
            var f = _preflight;
            bool stale = f != null && f.Fingerprint != PreflightFingerprint();

            LblPreflightWhen.Text = f == null
                ? "Live rows only"
                : $"Scanned {f.When:HH:mm} · {f.ScopeLabel}" + (stale ? " · scope or source changed since" : "");

            // Re-derive the IFC2x3 degradation against the schema selected NOW: the preview-time
            // value describes a different export the moment the schema changes.
            int degradedLive = 0;
            if (f != null && schema2x3 && f.ByClass != null)
                foreach (var kv in f.ByClass)
                    if (TypeMapping.Catalog.TryGetValue(kv.Key, out var c) && c.Ifc2x3.Length == 0) degradedLive += kv.Value;

            // ── live rows: settings and model facts, no scan needed ─────────────
            // Severity rule: unset is NORMAL (grey); a local-grid project has no CRS and that is
            // not a defect. Amber is reserved for CONTRADICTIONS: data that will silently not be
            // written. Nothing here blocks Export.
            string units = CmbUnits.SelectedIndex > 0 ? "units " + ((string)CmbUnits.SelectedItem).ToLowerInvariant() : "units read from the model";
            PreflightRow(Tone.Ok, $"{schemaName} · {units}", schema2x3
                ? "IFC2x3 has no entity for IfcMapConversion or a handful of newer classes. It also stores each triangle as separate face entities, so the file is typically several times larger than in IFC4."
                : "Schema and units are set.", schema2x3 ? "Export (Schema)" : null);
            if (degradedLive > 0)
                PreflightRow(Tone.Amber, $"IFC2x3 drops {degradedLive:N0} mapped element(s) to proxy",
                    "Their classes do not exist in IFC2x3, so they export as IfcBuildingElementProxy. Export IFC4 to keep them.", "Export (Schema)");

            if (doc != null && doc.Models.Count > 0)
            {
                try
                {
                    (double _, string unitName) = ResolveUnits(doc);
                    var models = ModelSurvey.Survey(doc);
                    bool sameOrigin = true;
                    for (int i = 1; i < models.Count; i++)
                        if (Math.Abs(models[i].Ox - models[0].Ox) > 1e-6 || Math.Abs(models[i].Oy - models[0].Oy) > 1e-6 ||
                            Math.Abs(models[i].Oz - models[0].Oz) > 1e-6) sameOrigin = false;
                    var byUnits = models.GroupBy(m => m.Units ?? "").OrderByDescending(g => g.Count()).ToList();
                    if (models.Count > 1 && byUnits.Count > 1)
                    {
                        // One unit scale is applied to EVERY model at export (ResolveUnits), so mixed
                        // declared units means at least one discipline exports at the wrong size.
                        string rest = byUnits.Count == 2 ? "the rest " + Plain(byUnits[1].Key) : "the rest other units";
                        PreflightRow(Tone.Amber, "Federated units differ",
                            $"{byUnits[0].Count()} of {models.Count} models declare {Plain(byUnits[0].Key)}, {rest}. One scale applies to the whole export, so one discipline would ship at the wrong size.",
                            "Project (origins report)");
                    }
                    else if (models.Count > 1 && !sameOrigin)
                        // Different origins are how federation positions disciplines: informational.
                        PreflightRow(Tone.Grey, $"{models.Count} models, differing origins", $"Normal for a federated model. Units {unitName}.", "Project (origins report)");
                    else if (models.Count > 1)
                        PreflightRow(Tone.Ok, $"{models.Count} models agree on origin and units", $"Units {unitName}.");
                }
                catch { PreflightRow(Tone.Grey, "Could not read model metadata", "Origins and units of the loaded models could not be compared."); }
            }
            else PreflightRow(Tone.Grey, "No model open", "Open a model to check it.");

            var coords = BuildCoords();
            string crs = string.IsNullOrWhiteSpace(coords.CrsName) ? "a local CRS" : coords.CrsName;
            if (ChkGeoref.IsChecked == true && coords.HasGeorefData && schema2x3)
                PreflightRow(Tone.Amber, "Georeferencing is dropped in IFC2x3", "IFC2x3 has no map conversion. The placement still carries the world offset.", "Project");
            else if (ChkGeoref.IsChecked != true && coords.HasGeorefData)
                PreflightRow(Tone.Amber, "CRS is filled in but georeferencing is off", $"{crs} will not be written.", "Project");
            else if (ChkGeoref.IsChecked != true || !coords.HasGeorefData)
                PreflightRow(Tone.Grey, "No georeferencing set", "A local grid is written. Normal for a local project.");
            else
                PreflightRow(Tone.Ok, $"Georeferenced to {crs}", "IfcMapConversion is written.");

            if (ChkProps.IsChecked == true && _cats.Count > 0 && _cats.All(c => !c.Checked))
                PreflightRow(Tone.Amber, "Properties on, every property set unticked", "No properties will be written.", "Data");
            if (ScopeIndex() == 4)
            {
                var ticked = new List<string>();
                for (int i = 0; i < _batchItems.Count && i < _sets.Count; i++)
                    if (_batchItems[i].Checked) ticked.Add(Sanitize(_batchItems[i].Name));
                if (ticked.Count == 0)
                    PreflightRow(Tone.Grey, "No sets ticked for batch", "Tick at least one set above.");
                else if (ticked.Distinct(StringComparer.OrdinalIgnoreCase).Count() < ticked.Count)
                    PreflightRow(Tone.Amber, "Two batch sets would write the same file", "Their sanitised names are identical, so one would overwrite the other. Rename a set in Navisworks.");
            }
            var dupNames = new HashSet<string>(
                _sets.GroupBy(x => x.DisplayName ?? "").Where(g => g.Key.Length > 0 && g.Count() > 1).Select(g => g.Key),
                StringComparer.Ordinal);
            if (dupNames.Count > 0 && _mapRows.Any(r => r.Set != null && dupNames.Contains(r.Set)))
                PreflightRow(Tone.Amber, "A rule's set name exists twice", "Rules bind to the first set with that name. Rename the duplicate in Navisworks.", "Mapping");

            // ── scanned rows: only after Smart setup or Run full check ─────────
            if (f == null)
            {
                PreflightRow(Tone.Grey, "Not measured yet", "Elements, rule match, storeys and role coverage fill after Smart setup or Run full check.");
            }
            else
            {
                // A green tick that can be stale is worse than no row: stale scan rows drop to
                // grey, so only CURRENT facts show green.
                string tail = stale ? " · stale" : "";
                Tone okNow = stale ? Tone.Grey : Tone.Ok;

                PreflightRow(okNow, $"{f.Items:N0} elements collected{tail}", f.HiddenExcluded > 0
                    ? $"{f.HiddenExcluded:N0} hidden subtree(s) excluded (hidden in Navisworks)."
                    : "Nothing hidden in Navisworks was skipped.");

                double pct = f.Items > 0 ? 100.0 * f.Mapped / f.Items : 0;
                if (f.Rules == 0)
                    // Zero rules is a deliberate geometry-only hand-off, not a defect.
                    PreflightRow(Tone.Grey, $"No mapping rules{tail}", "Every element exports as IfcBuildingElementProxy unless auto-classified. Fine for a geometry hand-off.", "Mapping");
                else if (pct >= 90)
                    PreflightRow(okNow, $"Rule match {pct:0.#}%{tail}", "Counts only your rules. Auto-classified elements still show as proxies here.");
                else
                    PreflightRow(stale ? Tone.Grey : Tone.Amber, $"Rule match {pct:0.#}% · {f.Mapped:N0} of {f.Items:N0}{tail}",
                        "The rest export as IfcBuildingElementProxy unless auto-classified.", "Mapping");

                string samp = f.SamplePartial ? $"sample of {f.RoleSample:N0}" : $"all {f.RoleSample:N0}";
                string storeys = f.SamplePartial ? $"≥ {f.DistinctLevels}" : f.DistinctLevels.ToString();
                if (string.IsNullOrWhiteSpace(_parLevel.Text))
                    PreflightRow(stale ? Tone.Grey : Tone.Amber, $"No Level role{tail}", "Every element lands on the fallback storey.", "Data (Level role)");
                else if (f.RoleSample == 0 || f.LevelPct < 60 || f.DistinctLevels <= 1)
                    PreflightRow(stale ? Tone.Grey : Tone.Amber, $"{storeys} distinct store{(f.DistinctLevels == 1 ? "y" : "ys")} · ≈{f.LevelPct}% have a level{tail}",
                        $"Level role reads {_parLevel.Text} ({samp}). Elements without it land on the fallback storey.", "Data (Level role)");
                else
                    PreflightRow(okNow, $"{storeys} distinct storeys{tail}", $"Level role reads {_parLevel.Text} on ≈{f.LevelPct}% of elements ({samp}).");
                if (f.RolesConfigured && f.RoleSample > 0)
                    PreflightRow(Tone.Grey, $"Role coverage{tail}", $"Type ≈{f.TypePct}% · Material ≈{f.MatPct}% · Classification ≈{f.ClsPct}% ({samp}).");

                PreflightRow(Tone.Grey, $"{(f.GeomSampled ? "≈" : "")}{f.Prims:N0} primitives{tail}",
                    $"Geometry is counted, never read, by the check. Instancing {(ChkInstancing.IsChecked == true ? "on" : "off")}.");
            }
            UpdateTabDots();
        }

        private static string Plain(string units) => string.IsNullOrEmpty(units) ? "no units" : units.ToLowerInvariant();

        private enum Tone { Ok, Amber, Grey }

        /// <summary>
        /// One pre-flight row: a tinted card with a 3 px edge in its tone, a bold head, the
        /// explanation under it, and a link to the tab that fixes it. Amber rows always explain
        /// themselves; the rest only with Show details on.
        /// </summary>
        private void PreflightRow(Tone tone, string head, string body, string? jump = null)
        {
            string edge = tone == Tone.Ok ? "Ok" : tone == Tone.Amber ? "AmberTx" : "Faint";
            string bg = tone == Tone.Ok ? "OkBg" : tone == Tone.Amber ? "AmberBg" : "Card2";
            string bd = tone == Tone.Ok ? "OkBd" : tone == Tone.Amber ? "AmberBd" : "Bd";
            var outer = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(3, 0, 0, 0), Margin = new Thickness(0, 0, 0, 6) };
            outer.SetResourceReference(Border.BackgroundProperty, edge);
            var inner = new Border { CornerRadius = new CornerRadius(0, 7, 7, 0), BorderThickness = new Thickness(0, 1, 1, 1), Padding = new Thickness(10, 7, 10, 8) };
            inner.SetResourceReference(Border.BackgroundProperty, bg);
            inner.SetResourceReference(Border.BorderBrushProperty, bd);
            outer.Child = inner;

            var dock = new DockPanel();
            if (jump != null)
            {
                // The link names the tab; the jump lands on the field where the row names one.
                string label = jump.Split(' ')[0];
                var link = new Button { Style = (Style)FindResource("LinkBtn"), Content = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
                link.Click += (_, _) => JumpTo(jump);
                DockPanel.SetDock(link, Dock.Right);
                dock.Children.Add(link);
                var tab = JumpTab(jump);
                if (tab != null && tone == Tone.Amber) _warnTabs.Add(tab);
            }
            var text = new StackPanel();
            var h = new TextBlock { Text = head, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            text.Children.Add(h);
            if (tone == Tone.Amber || TglCheckDetails.IsChecked == true)
            {
                var b = new TextBlock { Text = body, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 2, 0, 0) };
                b.SetResourceReference(TextBlock.ForegroundProperty, tone == Tone.Amber ? "Text" : "Muted");
                text.Children.Add(b);
            }
            dock.Children.Add(text);
            inner.Child = dock;
            PreflightHost.Children.Add(outer);
        }

        /// <summary>Tabs that hold the fix for a current pre-flight warning; each gets a dot.</summary>
        private readonly HashSet<TabItem> _warnTabs = new HashSet<TabItem>();

        private TabItem? JumpTab(string jump)
        {
            if (jump.Contains("Mapping")) return TabMapping;
            if (jump.Contains("Data")) return TabData;
            if (jump.Contains("Project")) return TabCoords;
            if (jump.Contains("Export")) return TabExport;
            return null;
        }

        private void JumpTo(string jump)
        {
            var tab = JumpTab(jump);
            if (tab == null) return;
            Tabs.SelectedItem = tab;
            // Land on the field, not just the tab, where the row names one.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (jump.Contains("Level role")) { _parLevel.BringIntoView(); _parLevel.Focus(); }
                else if (jump.Contains("origins report")) OnFederationReport(this, new RoutedEventArgs());
                else if (jump.Contains("Schema")) { CmbSchema.BringIntoView(); CmbSchema.Focus(); }
                else if (ReferenceEquals(tab, TabCoords)) ChkGeoref.BringIntoView();
                else if (ReferenceEquals(tab, TabData) && jump == "Data") LstCategories.BringIntoView();
            }), DispatcherPriority.Loaded);
        }

        private void UpdateTabDots()
        {
            DotData.Visibility = _warnTabs.Contains(TabData) ? Visibility.Visible : Visibility.Collapsed;
            DotMapping.Visibility = _warnTabs.Contains(TabMapping) ? Visibility.Visible : Visibility.Collapsed;
            DotCoords.Visibility = _warnTabs.Contains(TabCoords) ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The storeys the export will create, as the last check saw them, with the
        /// elevation the model's grids give each one.</summary>
        private void RenderStoreys(Document doc, PreflightFacts f)
        {
            if (f.LevelCounts == null || f.LevelCounts.Count == 0)
            {
                TxtStoreys.Text = f.RolesConfigured
                    ? "No element in the sample has a level. Check the Level role on the Data tab."
                    : "No Level role is set, so every element goes on the fallback storey.";
                return;
            }
            Dictionary<string, double>? elev = null;
            try { elev = ModelSurvey.LevelElevations(doc, ResolveUnits(doc).scale); } catch { }
            var sb = new StringBuilder();
            sb.AppendLine(f.SamplePartial ? $"From a sample of {f.RoleSample:N0} elements:" : $"From all {f.RoleSample:N0} elements:");
            foreach (var kv in f.LevelCounts.OrderBy(k => elev != null && elev.TryGetValue(k.Key, out var kz) ? kz : double.MaxValue).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string zs = elev != null && elev.TryGetValue(kv.Key, out var e) ? $"{e,9:0.000} m" : "  (no grid level)";
                sb.AppendLine($"{kv.Key,-28} {zs}   {kv.Value,7:N0} el.");
            }
            TxtStoreys.Text = sb.ToString().TrimEnd().Replace("\n", Environment.NewLine);
        }

        // ── base point preview ────────────────────────────────────────────────────
        private void PreviewBasePoint()
        {
            var doc = NavApp.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { LblPreview.Text = "open a model"; return; }
            (double scale, _) = ResolveUnits(doc);
            double x, y, z;
            switch (CmbBasePoint.SelectedIndex)
            {
                case 1: x = y = z = 0; break;
                case 2: x = ParseD(TxtE.Text); y = ParseD(TxtN.Text); z = ParseD(TxtElev.Text); break;
                default: var bb = ModelMinCorner(doc); x = bb.Item1 * scale; y = bb.Item2 * scale; z = bb.Item3 * scale; break;
            }
            LblPreview.Text = $"{x:N3}, {y:N3}, {z:N3} m";
        }
        private static (double, double, double) ModelMinCorner(Document doc)
        {
            double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
            try
            {
                foreach (var root in doc.Models.RootItems)
                {
                    var bb = root.BoundingBox(); if (bb == null) continue;
                    if (bb.Min.X < mnX) mnX = bb.Min.X; if (bb.Min.Y < mnY) mnY = bb.Min.Y; if (bb.Min.Z < mnZ) mnZ = bb.Min.Z;
                }
            }
            catch { }
            if (mnX == double.MaxValue) { mnX = mnY = mnZ = 0; }
            return (mnX, mnY, mnZ);
        }

        // ── report ───────────────────────────────────────────────────────────────
        private static string BuildReport(ExportSummary s, string schema, string scope, string units, int scopeItemCount, long scanMs, long ms, int ruleCount, string validation, string profile, long baseHeap, long peakHeap)
        {
            double tps = ms > 0 ? s.TriangleCount / (ms / 1000.0) : 0;
            var sb = new StringBuilder();
            sb.AppendLine("BIMCamel IFC Export report");
            sb.AppendLine("──────────────────────────────");
            sb.AppendLine(s.FileCount > 1
                ? $"Files       : {s.FileCount}  (first: {System.IO.Path.GetFileName(s.Path)})"
                : $"File        : {System.IO.Path.GetFileName(s.Path)}");
            sb.AppendLine($"Schema      : {schema}");
            sb.AppendLine($"Scope       : {scope}  ({scopeItemCount} items)");
            sb.AppendLine($"Source units: {units} → metres");
            sb.AppendLine($"Base point  : {s.BasePointMode}  rotation {s.RotationDeg:0.###}°");
            sb.AppendLine($"Site offset : [{s.OffsetX:N3}, {s.OffsetY:N3}, {s.OffsetZ:N3}] m");
            sb.AppendLine($"Georef      : {GeorefLine(s, schema)}");
            sb.AppendLine($"Elements    : {s.ElementCount:N0}");
            sb.AppendLine(s.SourceTriangleCount > s.TriangleCount
                ? $"Triangles   : {s.TriangleCount:N0}  (simplified from {s.SourceTriangleCount:N0}, {100.0 * s.TriangleCount / s.SourceTriangleCount:0.#} %)"
                : $"Triangles   : {s.TriangleCount:N0}");
            // Unique meshes (instanced) or elements (plain): what the simplifier itself saw.
            if (ET.SimplifiedMeshes > 0)
                sb.AppendLine($"Simplify    : {ET.SimplifiedMeshes:N0} mesh(es), {ET.SimplifyTrisIn:N0} → {ET.SimplifyTrisOut:N0} triangles before placement"
                              + (ET.SimplifyReused > 0 ? $" · {ET.SimplifyReused:N0} reuse(s)" : "")
                              + $" · {ET.Ms(ET.SimplifyTicks):N0} ms");
            // Anything that did not reach the IFC is stated explicitly — elements used to vanish
            // with no trace anywhere in the UI or the report.
            var issues = BIMCamel.Geometry.ExportIssues.Report();
            if (issues.Length > 0) { sb.AppendLine("Not exported:"); sb.Append(issues); }
            sb.AppendLine(s.Instanced
                ? $"Instancing  : ON · {s.UniqueGeometries:N0} unique / {s.InstanceCount:N0} instances" + (s.UniqueGeometries > 0 ? $"  (×{(double)s.InstanceCount / s.UniqueGeometries:0.0})" : "")
                : "Instancing  : off");
            if (s.MergedElements > 0)
                sb.AppendLine($"Merged parts: {s.MergedElements:N0} element(s) as one shape each · {s.MergedShapes:N0} distinct");
            sb.AppendLine($"Class rules : {ruleCount} set rule(s)");
            if (s.GuidCollisions > 0)
                sb.AppendLine($"GlobalIds   : {s.GuidCollisions:N0} element(s) shared a GlobalId with another (model appended twice?) and got a derived one");
            var auto = RuleClassifier.Report();
            if (auto.Length > 0) sb.AppendLine(auto);
            sb.Append(MappingLines(s));
            if (s.Revision != null)
            {
                sb.Append(s.Revision.Report());

                // Named, not implied. A change list nobody knows was written is a change list
                // nobody opens, and the whole point of it is that somebody acts on it.
                if (s.ChangesWritten)
                    sb.AppendLine($"     · change list written beside the IFC: {s.Revision.Changed:N0} rows in *.ifc.changes.csv");

                if (s.Revision.BaselineHasNoNames)
                    sb.AppendLine("     · the previous manifest predates element names, so DELETED rows "
                                  + "in the change list have no name — the next export will have them");
            }
            sb.AppendLine($"Storeys     : {s.StoreyCount}");
            sb.AppendLine($"Type objects: {s.TypeCount}   Materials: {s.MaterialCount}   Classifications: {s.ClassificationCount}");
            if (s.GroupCount > 0) sb.AppendLine($"Groups      : {s.GroupCount} (from Navisworks sets)");
            sb.AppendLine($"Property sets: {s.PsetUnique:N0} unique / {s.PsetRefs:N0} refs" + (s.PsetUnique > 0 ? $"  (×{(double)s.PsetRefs / s.PsetUnique:0.0} shared)" : ""));
            if (s.PropValueReuses > 0)
                sb.AppendLine($"Prop values : {s.PropValueReuses:N0} reused instead of written again");
            if (s.TypeSharedPsets > 0 || s.TypeShareNameMismatch > 0)
            {
                sb.AppendLine($"  type-shared : {s.TypeSharedPsets:N0} pset(s) hoisted onto IfcElementType");
                sb.AppendLine(s.TypeShareNameMismatch == 0
                    ? "  faithful    : every occurrence was expressible as an override"
                    : $"  ⚠ {s.TypeShareNameMismatch:N0} occurrence(s) had different property NAMES than their type. "
                      + "IFC cannot express \"my type says this but I do not have it\", so those wrote their full "
                      + "set and may ALSO inherit a type property they did not carry. Untick "
                      + "\"Share type-level properties\" if that matters for this model.");
            }
            sb.AppendLine($"Quantities  : {(s.QuantitiesWritten ? "computed (volume/area/length/width/height)" : "none")}"
                          + (s.QtyUnique > 0 ? $"  —  {s.QtyUnique:N0} unique / {s.QtyRefs:N0} refs (×{(double)s.QtyRefs / s.QtyUnique:0.0} shared)" : ""));
            bool zipped = s.Files.Count > 0 && BIMCamel.Ifc.IfcSource.IsZip(s.Files[0]);
            sb.AppendLine($"{(s.FileCount > 1 ? "Total size  " : "File size   ")}: {s.FileSizeBytes / 1024.0:N0} KB"
                          + (zipped ? "  (compressed .ifczip)" : ""));
            sb.AppendLine($"Scan        : {scanMs:N0} ms  (tree walk + extents)");
            // v5 S4: one Scan number could not say whether the tree walk, the set searches or the
            // property sample was the slow half — so S1/S2/S3 could only ever be plausible. The
            // decomposition is what makes them checkable on a real model.
            {
                double ct = ET.Ms(ET.CollectTicks), sr = ET.Ms(ET.SetResolveTicks);
                double mc = ET.Ms(ET.MinCornerTicks), ps = ET.Ms(ET.PropScanTicks);
                double sother = scanMs - ct - sr - mc - ps; if (sother < 0) sother = 0;
                sb.AppendLine($"  Collect     : {ct,10:N0} ms   ({ET.NodesVisited:N0} nodes"
                              + (ET.OverlapsSkipped > 0 ? $", {ET.OverlapsSkipped:N0} overlapping entr(ies) walked once" : "") + ")");
                sb.AppendLine($"  Set resolve : {sr,10:N0} ms   ({ET.SetsResolved:N0} rule(s), {ET.SetMembers:N0} member leaves)");
                if (ET.SetSearchTicks > 0) sb.AppendLine($"    searches  : {ET.Ms(ET.SetSearchTicks),10:N0} ms   (Navisworks Search.FindAll)");
                sb.AppendLine($"  Min corner  : {mc,10:N0} ms" + (ET.MinCornerTicks == 0 ? "   (not needed)" : ""));
                if (ET.PropScanTicks > 0) sb.AppendLine($"  Prop scan   : {ps,10:N0} ms   ({ET.SampledItems:N0} sampled)");
                sb.AppendLine($"  Other       : {sother,10:N0} ms");
            }
            sb.AppendLine($"Export      : {ms:N0} ms  ({tps:N0} tris/s)");
            double com = ET.Ms(ET.ComConvertTicks), rd = ET.Ms(ET.ReadTicks), hv = ET.Ms(ET.HarvestTicks), wl = ET.Ms(ET.WeldTicks);
            double qc = ET.Ms(ET.QtyTicks);
            double gw = ET.Ms(ET.GeomWriteTicks), pw = ET.Ms(ET.PropWriteTicks), qw = ET.Ms(ET.QtyWriteTicks), ui = ET.Ms(ET.UiTicks);
            // Read and write now overlap on two threads (v5 E7), so the parts no longer sum to the
            // wall clock — the read side IS the wall clock and the write side hides inside it. The
            // buckets are still each thread's own honest total; "Other" is what the read thread did
            // outside them, never a negative remainder.
            double readSide = com + rd + hv + wl + qc + ui;
            double other = ms - readSide; if (other < 0) other = 0;
            sb.AppendLine($"  COM convert : {com,10:N0} ms   ({ET.ComConverts:N0} conversion(s))");
            if (ET.ComMapTicks > 0) sb.AppendLine($"    map back  : {ET.Ms(ET.ComMapTicks),10:N0} ms   (paths → items)");
            sb.AppendLine($"  Geometry rd : {rd,10:N0} ms   ({ET.Fragments:N0} fragments)");
            if (ET.ForeignFragmentsSkipped > 0 || ET.OwnFragmentFallbacks > 0)
                sb.AppendLine($"  Instances   : {ET.ForeignFragmentsSkipped:N0} fragment(s) of other instances left out" +
                              (ET.OwnFragmentFallbacks > 0 ? $", {ET.OwnFragmentFallbacks:N0} path(s) kept all (no own match)" : ""));
            if (ET.GroupReadsShared > 0 || ET.GroupVerifications > 0)
                sb.AppendLine($"  Shared reads: {ET.GroupReadsShared:N0} fragment read(s) reused from Navisworks instances · "
                              + $"{ET.GroupVerifications:N0} re-checked, {ET.GroupMismatches:N0} mismatch(es)");
            sb.AppendLine($"  Prop harvest: {hv,10:N0} ms");
            if (ET.PropRuleTicks > 0) sb.AppendLine($"    prop rules: {ET.Ms(ET.PropRuleTicks),10:N0} ms");
            if (ET.ClassifyTicks > 0) sb.AppendLine($"    auto-class: {ET.Ms(ET.ClassifyTicks),10:N0} ms");
            if (ET.BoxReads > 0) sb.AppendLine($"  Bounding box: {ET.Ms(ET.BoxTicks),10:N0} ms   ({ET.BoxReads:N0} read(s), one per element)");
            sb.AppendLine($"  Weld        : {wl,10:N0} ms");
            sb.AppendLine($"  Qty compute : {qc,10:N0} ms");
            sb.AppendLine($"  UI pump     : {ui,10:N0} ms   ({ET.UiPumps:N0} pumps)");
            sb.AppendLine($"  Other (read): {other,10:N0} ms");
            sb.AppendLine($"  ── writer thread (overlapped) ──");
            sb.AppendLine($"  Geom write  : {gw,10:N0} ms");
            sb.AppendLine($"  Prop write  : {pw,10:N0} ms");
            sb.AppendLine($"  Qty write   : {qw,10:N0} ms");
            if (ET.GeomReadsSkipped > 0 || ET.GeomVerifications > 0)
            {
                sb.AppendLine($"  ── fast instancing (v5 E1) ──");
                sb.AppendLine($"  Reads skipped : {ET.GeomReadsSkipped,8:N0} fragment(s)");
                sb.AppendLine($"  Verified      : {ET.GeomVerifications,8:N0} sample(s), {ET.GeomMismatches:N0} mismatch(es)");
                if (ET.GeomEvictions > 0) sb.AppendLine($"  Evicted       : {ET.GeomEvictions,8:N0} (memory budget)");
                if (ET.GeomMismatches > 0)
                    sb.AppendLine("  ⚠  A mismatch means the type+size guess was WRONG for that shape. Those "
                                + "candidates were dropped and re-read, so this file is correct — but the "
                                + "assumption does not hold on this model, and Fast instancing should be off.");
            }
            // Per call, not per stage: the stage lines above say where the time went, these say
            // which Navisworks call spent it and what one call costs (Probe).
            sb.AppendLine($"Per call    : export #{Probe.SessionRun} in this Navisworks session"
                          + (Probe.SessionRun == 1 ? " (first: includes Navisworks loading the model)" : ""));
            Probe.Report(sb, true, scanMs);
            Probe.Report(sb, false, ms);
            sb.AppendLine($"Peak heap   : {peakHeap / 1048576.0:N0} MB  (start {baseHeap / 1048576.0:N0} MB)");
            sb.AppendLine($"Validation  : {validation}");
            sb.AppendLine($"Exported    : {DateTime.Now:yyyy-MM-dd HH:mm}");

            double harvestPerEl = s.ElementCount > 0 ? hv / s.ElementCount : 0;
            if (s.ElementCount > 1000 && harvestPerEl > 2.0)
            {
                sb.AppendLine();
                sb.AppendLine("──────────────────────────────");
                sb.AppendLine($"⚠  SLOW PROPERTY HARVEST: {harvestPerEl:0.0} ms/element (normal < 1 ms).");
                sb.AppendLine("   One known cause is an ACTIVE DataTools / external-database link: every");
                sb.AppendLine("   object triggers a database query, and if the link is broken the console");
                sb.AppendLine("   fills with 'DATATOOLS_SQL_EXEC ... Sheet1$' errors. Fix: open Home →");
                sb.AppendLine("   DataTools, then deactivate/delete that link and re-export.");
                sb.AppendLine("   No such errors? Then Navisworks itself was slow on this run: compare the");
                sb.AppendLine("   per-call table and the machine lines with an earlier report.");
            }

            if (!string.IsNullOrEmpty(profile)) { sb.AppendLine("──────────────────────────────"); sb.Append(profile); }
            return sb.ToString();
        }


        /// <summary>
        /// Georeferencing state in plain words. "off" used to cover three different situations,
        /// including the one where the box was ticked but nothing was written.
        /// </summary>
        private static string GeorefLine(ExportSummary s, string schema)
        {
            if (s.GeorefWritten)
            {
                string crs = string.IsNullOrWhiteSpace(s.CrsName) ? "LOCAL (no CRS given)" : s.CrsName;
                return $"IfcMapConversion written · CRS {crs} · survey point [{s.SurveyE:N3}, {s.SurveyN:N3}, {s.SurveyH:N3}] m";
            }
            if (s.GeorefSkippedNoData) return "requested but SKIPPED — no CRS or survey point given (see Options)";
            if (schema == "IFC2x3") return "not available in IFC2x3 — placement carries the offset";
            return "off";
        }

        /// <summary>
        /// How the class mapping actually landed. Without this the report could say "1 set rule"
        /// while 499,988 of 500,000 elements silently became IfcBuildingElementProxy.
        /// </summary>
        private static string MappingLines(ExportSummary s)
        {
            if (s.ElementCount == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine($"Mapped      : {s.MappedCount:N0} of {s.ElementCount:N0} elements");
            if (s.ProxyTotal > 0)
            {
                sb.AppendLine($"⚠  Proxy    : {s.ProxyTotal:N0} element(s) ({s.ProxyPercent:0.#}%) exported as IfcBuildingElementProxy");
                if (s.ProxyUnmapped > 0) sb.AppendLine($"     · {s.ProxyUnmapped:N0} matched no mapping rule");
                if (s.ProxyDegraded2x3 > 0)
                {
                    sb.AppendLine($"     · {s.ProxyDegraded2x3:N0} WERE mapped, but the class has no IFC2x3 entity");
                    sb.AppendLine("       (export as IFC4 to keep those classes)");
                }
            }
            // Top classes by count, so the breakdown stays readable on a big model.
            if (s.ByEntity.Count > 0)
            {
                var top = s.ByEntity.OrderByDescending(kv => kv.Value).Take(8).ToList();
                foreach (var kv in top) sb.AppendLine($"     {kv.Key,-34} {kv.Value,9:N0}");
                if (s.ByEntity.Count > top.Count) sb.AppendLine($"     … and {s.ByEntity.Count - top.Count} more class(es)");
            }
            return sb.ToString();
        }

        // ── profiles ───────────────────────────────────────────────────────────────
        private void OnSaveProfile(object s, RoutedEventArgs e)
        {
            try
            {
                using var sfd = new WF.SaveFileDialog { Title = "Save BIMCamel profile", Filter = "BIMCamel profile (*.json)|*.json", FileName = "bimcamel_profile.json", DefaultExt = "json" };
                if (sfd.ShowDialog() != WF.DialogResult.OK) return;
                var p = Capture();
                ExportProfile.Save(p, sfd.FileName);
                _ui.ProfileName = System.IO.Path.GetFileNameWithoutExtension(sfd.FileName);
                _profileJson = ExportProfile.ToJson(p);
                SaveSession();
                SetStatus("Profile saved.");
            }
            catch (Exception ex) { WF.MessageBox.Show(ex.Message, "BIMCamel", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Error); }
        }
        private ExportProfile Capture() => new ExportProfile
        {
            Schema = CmbSchema.SelectedIndex, Units = CmbUnits.SelectedIndex, Scope = ScopeIndex(),
            Quality = Quality, BasePoint = CmbBasePoint.SelectedIndex,
            CustomE = ParseD(TxtE.Text), CustomN = ParseD(TxtN.Text), CustomElev = ParseD(TxtElev.Text), Rotation = ParseD(TxtRot.Text),
            Georef = ChkGeoref.IsChecked == true, Props = ChkProps.IsChecked == true, Materials = ChkMaterials.IsChecked == true, Instancing = ChkInstancing.IsChecked == true,
            FastInstancing = ChkFastInstancing.IsChecked == true, MergeParts = ChkMergeParts.IsChecked == true,
            Zip = ChkZip.IsChecked == true, ShareTypeProps = ChkShareTypeProps.IsChecked == true,
            SkipEmptyProps = ChkSkipEmptyProps.IsChecked == true,
            PropSource = CmbPropSource.SelectedIndex,
            MinSize = ChkMinSize.IsChecked == true, MinSizeMm = (TxtMinSizeMm.Text ?? "").Trim(),
            Simplify = ChkSimplify.IsChecked == true, SimplifyMm = (TxtSimplifyMm.Text ?? "").Trim(),
            Validate = ChkValidate.IsChecked == true, Quantities = ChkQuantities.IsChecked == true, NoAutoClassify = ChkAutoClassify.IsChecked != true, Mapping = GridToText(),
            Crs = (TxtCrs.Text ?? "").Trim(), SurveyE = ParseD(TxtSurveyE.Text), SurveyN = ParseD(TxtSurveyN.Text), SurveyElev = ParseD(TxtSurveyElev.Text),
            Roles = RolesToText(), ParamRules = ParamRulesToText(), PropRules = PropRulesToText(),
            ProjectName = TxtProject.Text ?? "", SiteName = TxtSite.Text ?? "", BuildingName = TxtBuilding.Text ?? "", StoreyName = TxtStorey.Text ?? "",
            ClassificationSystem = (TxtClassSystem.Text ?? "").Trim(),
            ClassificationSource = (TxtClassSource.Text ?? "").Trim(),
            ClassificationEdition = (TxtClassEdition.Text ?? "").Trim(),
            ClassificationEditionDate = (TxtClassEditionDate.Text ?? "").Trim(),
            ClassificationLocation = (TxtClassLocation.Text ?? "").Trim(),
            Split = ChkSplit.IsChecked == true, SplitMb = TxtSplitMb.Text ?? "200",
            Groups = CmbGroups.SelectedIndex,
            FileNameTemplate = (TxtFileName.Text ?? "").Trim()
        };
        private void Apply(ExportProfile p)
        {
            CmbSchema.SelectedIndex = Clamp(p.Schema, CmbSchema.Items.Count);
            CmbUnits.SelectedIndex = Clamp(p.Units, CmbUnits.Items.Count);
            int sc = Clamp(p.Scope, _scopeRadios.Count); _scopeRadios[sc].IsChecked = true;
            Quality = p.Quality;
            CmbBasePoint.SelectedIndex = Clamp(p.BasePoint, CmbBasePoint.Items.Count);
            TxtE.Text = Inv(p.CustomE); TxtN.Text = Inv(p.CustomN); TxtElev.Text = Inv(p.CustomElev); TxtRot.Text = Inv(p.Rotation);
            ChkGeoref.IsChecked = p.Georef; ChkProps.IsChecked = p.Props; ChkMaterials.IsChecked = p.Materials;
            ChkInstancing.IsChecked = p.Instancing; ChkValidate.IsChecked = p.Validate; ChkQuantities.IsChecked = p.Quantities; ChkAutoClassify.IsChecked = !p.NoAutoClassify;
            ChkFastInstancing.IsChecked = p.FastInstancing; ChkMergeParts.IsChecked = p.MergeParts;
            ChkZip.IsChecked = p.Zip; ChkShareTypeProps.IsChecked = p.ShareTypeProps;
            ChkSkipEmptyProps.IsChecked = p.SkipEmptyProps;
            CmbPropSource.SelectedIndex = Clamp(p.PropSource, CmbPropSource.Items.Count);
            ChkMinSize.IsChecked = p.MinSize;
            ChkSimplify.IsChecked = p.Simplify;
            if (!string.IsNullOrWhiteSpace(p.SimplifyMm)) TxtSimplifyMm.Text = p.SimplifyMm;
            if (!string.IsNullOrWhiteSpace(p.MinSizeMm)) TxtMinSizeMm.Text = p.MinSizeMm;
            TxtCrs.Text = p.Crs ?? ""; TxtSurveyE.Text = Inv(p.SurveyE); TxtSurveyN.Text = Inv(p.SurveyN); TxtSurveyElev.Text = Inv(p.SurveyElev);
            if (!string.IsNullOrWhiteSpace(p.ProjectName)) TxtProject.Text = p.ProjectName;
            if (!string.IsNullOrWhiteSpace(p.SiteName)) TxtSite.Text = p.SiteName;
            if (!string.IsNullOrWhiteSpace(p.BuildingName)) TxtBuilding.Text = p.BuildingName;
            if (!string.IsNullOrWhiteSpace(p.StoreyName)) TxtStorey.Text = p.StoreyName;
            TxtClassSystem.Text = p.ClassificationSystem ?? "";
            TxtClassSource.Text = p.ClassificationSource ?? "";
            TxtClassEdition.Text = p.ClassificationEdition ?? "";
            TxtClassEditionDate.Text = p.ClassificationEditionDate ?? "";
            TxtClassLocation.Text = p.ClassificationLocation ?? "";
            ChkSplit.IsChecked = p.Split; if (!string.IsNullOrWhiteSpace(p.SplitMb)) TxtSplitMb.Text = p.SplitMb;
            CmbGroups.SelectedIndex = Clamp(p.Groups, CmbGroups.Items.Count);
            TxtFileName.Text = string.IsNullOrWhiteSpace(p.FileNameTemplate) ? DefaultNameTemplate : p.FileNameTemplate;
            TextToRoles(p.Roles); TextToParamRules(p.ParamRules);
            TextToGrid(p.Mapping);
            TextToPropRules(p.PropRules);
        }

        // Mapping grid: "set => class :: predefined @@ classification". The class half is now
        // optional, since a row may exist purely to assign a classification code.
        private string GridToText()
        {
            var sb = new StringBuilder();
            foreach (var row in _mapRows)
            {
                var t = (row.Set ?? "").Trim(); var c = (row.IfcClass ?? "").Trim();
                var pd = (row.Predefined ?? "").Trim(); var cf = (row.Classification ?? "").Trim();
                if (t.Length == 0 || (c.Length == 0 && cf.Length == 0)) continue;
                var line = new StringBuilder($"{t} => {c}");
                if (pd.Length > 0) line.Append(" :: ").Append(pd);
                if (cf.Length > 0) line.Append(" @@ ").Append(cf);
                sb.AppendLine(line.ToString());
            }
            return sb.ToString();
        }
        private void TextToGrid(string? text)
        {
            _mapRows.Clear();
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var line in text!.Replace("\r", "").Split('\n'))
            {
                var l = line.Trim(); int a = l.IndexOf("=>", StringComparison.Ordinal);
                if (a < 0) continue;
                var setName = l.Substring(0, a).Trim(); var rhs = l.Substring(a + 2).Trim();
                string classif = ""; int cc = rhs.IndexOf("@@", StringComparison.Ordinal);
                if (cc >= 0) { classif = rhs.Substring(cc + 2).Trim(); rhs = rhs.Substring(0, cc).Trim(); }
                string predef = ""; int pp = rhs.IndexOf("::", StringComparison.Ordinal);
                if (pp >= 0) { predef = rhs.Substring(pp + 2).Trim(); rhs = rhs.Substring(0, pp).Trim(); }
                if (setName.Length == 0) continue;
                // An unknown class name is dropped, but a classification-only row still loads.
                if (rhs.Length > 0 && !TypeMapping.Catalog.ContainsKey(rhs)) rhs = "";
                if (rhs.Length == 0 && classif.Length == 0) continue;
                if (!MapRow.SharedSets.Contains(setName)) MapRow.SharedSets.Add(setName);
                _mapRows.Add(new MapRow { Set = setName, IfcClass = rhs, Predefined = predef, Classification = classif });
            }
        }

        // Property roles — four "category|property" lines, in a fixed order.
        private string RolesToText()
        {
            var sb = new StringBuilder();
            void Line(ComboBox cat, ComboBox par) => sb.AppendLine(((cat.Text == AnyCat ? "" : cat.Text) ?? "") + "|" + (par.Text ?? ""));
            Line(_catType, _parType); Line(_catLevel, _parLevel); Line(_catMat, _parMat); Line(_catCls, _parCls); Line(_catName, _parName);
            return sb.ToString();
        }
        private void TextToRoles(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var lines = text!.Replace("\r", "").Split('\n');
            void Set(int i, ComboBox cat, ComboBox par)
            {
                if (i >= lines.Length) return;
                int bar = lines[i].IndexOf('|');
                if (bar < 0) return;
                var c = lines[i].Substring(0, bar).Trim();
                cat.Text = c.Length == 0 ? AnyCat : c;
                par.Text = lines[i].Substring(bar + 1).Trim();
            }
            // A profile from before the name role has four lines; it means "the Navisworks name".
            _catName.Text = AnyCat; _parName.Text = "";
            Set(0, _catType, _parType); Set(1, _catLevel, _parLevel); Set(2, _catMat, _parMat); Set(3, _catCls, _parCls); Set(4, _catName, _parName);
        }

        // Parameter rules — one "srcCategory | srcProperty | targetPset | targetName" per line.
        private string ParamRulesToText()
        {
            var sb = new StringBuilder();
            foreach (var r in _paramRows)
            {
                var src = (r.SourceProperty ?? "").Trim();
                if (src.Length == 0) continue;
                sb.AppendLine($"{(r.SourceCategory ?? "").Trim()}|{src}|{(r.TargetPset ?? "").Trim()}|{(r.TargetName ?? "").Trim()}");
            }
            return sb.ToString();
        }
        private void TextToParamRules(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _paramRows.Clear();
            foreach (var line in text!.Replace("\r", "").Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                var f = line.Split('|');
                if (f.Length < 2 || f[1].Trim().Length == 0) continue;
                _paramRows.Add(new ParamRow
                {
                    SourceCategory = f[0].Trim(),
                    SourceProperty = f[1].Trim(),
                    TargetPset = f.Length > 2 ? f[2].Trim() : "",
                    TargetName = f.Length > 3 ? f[3].Trim() : ""
                });
            }
        }

        // Property rules — "category|property|test|value|class|predefined|classification" per line.
        private string PropRulesToText()
        {
            var sb = new StringBuilder();
            foreach (var r in _propRules)
            {
                var prop = (r.Property ?? "").Trim();
                if (prop.Length == 0) continue;
                string cat = r.Category == AnyCat ? "" : (r.Category ?? "").Trim();
                sb.AppendLine(string.Join("|", cat, prop, r.Test, (r.Value ?? "").Trim(), (r.IfcClass ?? "").Trim(),
                    (r.Predefined ?? "").Trim(), (r.Classification ?? "").Trim()));
            }
            return sb.ToString();
        }
        private void TextToPropRules(string? text)
        {
            _propRules.Clear();
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var line in text!.Replace("\r", "").Split('\n'))
            {
                var f = line.Split('|');
                if (f.Length < 4 || f[1].Trim().Length == 0) continue;
                string F(int i) => i < f.Length ? f[i].Trim() : "";
                var cls = F(4);
                if (cls.Length > 0 && !TypeMapping.Catalog.ContainsKey(cls)) cls = "";
                _propRules.Add(new PropRuleRow
                {
                    Category = F(0).Length == 0 ? AnyCat : F(0), Property = F(1),
                    Test = Array.IndexOf(PropRuleRow.SharedTests, F(2)) >= 0 ? F(2) : "equals",
                    Value = F(3), IfcClass = cls, Predefined = F(5), Classification = F(6)
                });
            }
        }

        // ── small helpers ──────────────────────────────────────────────────────────
        private (double scale, string name) ResolveUnits(Document doc) => CmbUnits.SelectedIndex switch
        {
            1 => (0.001, "mm (forced)"), 2 => (0.01, "cm (forced)"), 3 => (1.0, "m (forced)"),
            4 => (0.3048, "ft (forced)"), 5 => (0.0254, "in (forced)"), _ => UnitsToMetre(doc.Units)
        };
        private void SetStatus(string text) => LblStatus.Text = text;
        private void SetReport(string text)
        {
            TxtReport.Text = text.Replace("\n", Environment.NewLine);
            BtnSaveReport.IsEnabled = text.Length > 0;
        }
        private static double ParseD(string s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0.0;
        private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
        private static int Clamp(int v, int count) => v < 0 ? 0 : (v >= count ? count - 1 : v);
        private static string Def(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

        // One unit table, owned by ModelSurvey (which needs it per model, not just per document).
        private static (double scale, string name) UnitsToMetre(Units u) => ModelSurvey.UnitsToMetre(u);
        private static string ModelBaseName(Document doc)
        {
            try { var fn = doc.FileName; if (!string.IsNullOrEmpty(fn)) return System.IO.Path.GetFileNameWithoutExtension(fn); }
            catch { }
            return "BIMCamel_export";
        }
    }
}
