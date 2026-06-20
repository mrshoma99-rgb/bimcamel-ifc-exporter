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
        private List<SelectionSet> _savedSets = new List<SelectionSet>();
        private List<SelectionSet> _batchSets = new List<SelectionSet>();
        private List<SelectionSet> _mapSets = new List<SelectionSet>();
        private readonly ObservableCollection<CheckItem> _cats = new ObservableCollection<CheckItem>();
        private readonly ObservableCollection<CheckItem> _batchItems = new ObservableCollection<CheckItem>();
        private readonly ObservableCollection<ParamRow> _paramRows = new ObservableCollection<ParamRow>();
        private readonly ObservableCollection<MapRow> _mapRows = new ObservableCollection<MapRow>();
        private Dictionary<string, List<string>> _catParams = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // scope radios + role combos (built in code)
        private readonly List<RadioButton> _scopeRadios = new List<RadioButton>();
        private readonly ObservableCollection<string> _roleCats = new ObservableCollection<string> { AnyCat };
        private ComboBox _catType = null!, _parType = null!, _catLevel = null!, _parLevel = null!,
                         _catMat = null!, _parMat = null!, _catCls = null!, _parCls = null!;

        // progress
        private readonly Stopwatch _tickClock = new Stopwatch();
        private long _lastTickMs;
        private long _peakHeap;
        private bool _dark;

        public ExporterView()
        {
            InitializeComponent();
            SetLogo();
            BuildScopeOptions();
            BuildRoleRows();
            InitControls();
            InitGridsAndLists();
            Loaded += (_, _) => { UpdateScopeHint(); PreviewBasePoint(); RefreshModelStatus(); };
        }

        // Show the real BIMCamel logo. Prefer the embedded resource; fall back to the loose icon next
        // to the assembly (the bundle ships Resources\camel_32.png alongside the DLL).
        private void SetLogo()
        {
            try { LogoImg.Source = new BitmapImage(new Uri("pack://application:,,,/BIMCamel;component/Resources/camel_32.png")); return; }
            catch { }
            try
            {
                var dir = System.IO.Path.GetDirectoryName(typeof(ExporterView).Assembly.Location);
                if (dir != null)
                {
                    var p = System.IO.Path.Combine(dir, "Resources", "camel_32.png");
                    if (System.IO.File.Exists(p)) LogoImg.Source = new BitmapImage(new Uri(p));
                }
            }
            catch { }
        }

        private void OnDismissWarning(object sender, RoutedEventArgs e) => WarnBanner.Visibility = Visibility.Collapsed;

        // ── one-time setup ──────────────────────────────────────────────────────
        private void InitControls()
        {
            AddItems(CmbSchema, new[] { "IFC4", "IFC2x3" }); CmbSchema.SelectedIndex = 0;
            AddItems(CmbUnits, new[] { "Auto (from model)", "Millimeters", "Centimeters", "Meters", "Feet", "Inches" }); CmbUnits.SelectedIndex = 0;
            AddItems(CmbQuality, new[] { "Balanced", "Small file", "High detail" }); CmbQuality.SelectedIndex = 0;
            AddItems(CmbBasePoint, new[] { "Geometry origin — recommended (real coords kept in georeferencing)", "Model origin (no offset — keeps world coords)", "Custom base point" }); CmbBasePoint.SelectedIndex = 0;

            ChkGeoref.IsChecked = true; ChkInstancing.IsChecked = true; ChkValidate.IsChecked = false; ChkProfile.IsChecked = true;
            ChkProps.IsChecked = true; ChkMaterials.IsChecked = true; ChkQuantities.IsChecked = true; ChkSplit.IsChecked = false;
            TxtE.Text = TxtN.Text = TxtElev.Text = TxtRot.Text = "0";

            CmbBasePoint.SelectionChanged += (_, _) => { CoordRow.Visibility = CmbBasePoint.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; PreviewBasePoint(); };
            TxtE.TextChanged += (_, _) => PreviewBasePoint();
            TxtN.TextChanged += (_, _) => PreviewBasePoint();
            TxtElev.TextChanged += (_, _) => PreviewBasePoint();
            void SyncProps() => LstCategories.IsEnabled = ChkProps.IsChecked == true;
            ChkProps.Checked += (_, _) => SyncProps(); ChkProps.Unchecked += (_, _) => SyncProps(); SyncProps();
        }

        private void InitGridsAndLists()
        {
            LstCategories.ItemsSource = _cats;
            LstBatch.ItemsSource = _batchItems;
            GridParams.ItemsSource = _paramRows;
            GridMap.ItemsSource = _mapRows;

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
                var rb = new RadioButton
                {
                    Content = labels[i], GroupName = "scope", IsChecked = i == 0,
                    Style = (Style)FindResource("OptionCard"), ToolTip = tips[i]
                };
                rb.Checked += (_, _) => UpdateScopeHint();
                _scopeRadios.Add(rb);
                ScopeOptions.Children.Add(rb);
            }
        }

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
        }

        private (ComboBox cat, ComboBox par) AddRoleRow(string label)
        {
            var border = new Border
            {
                BorderBrush = (Brush)FindResource("Bd"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 9)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var cat = new ComboBox { IsEditable = true, ItemsSource = _roleCats, Height = 30, ToolTip = "Source category that carries this role (scan first to populate)." };
            var par = new ComboBox { IsEditable = true, Height = 30, ToolTip = "Source property that carries this role. Leave blank to turn the role off." };
            Grid.SetColumn(cat, 0); Grid.SetColumn(par, 2);
            grid.Children.Add(cat); grid.Children.Add(par);
            sp.Children.Add(grid);
            border.Child = sp;
            RoleHost.Children.Add(border);
            cat.SelectionChanged += (_, _) => PopulateParamCombo(par, cat.Text, keepText: false);
            return (cat, par);
        }

        private static void AddItems(ComboBox cmb, IEnumerable<string> items) { foreach (var i in items) cmb.Items.Add(i); }

        // ── theme toggle ──────────────────────────────────────────────────────────
        private void OnToggleTheme(object sender, RoutedEventArgs e)
        {
            _dark = !_dark;
            BtnTheme.Content = _dark ? "☀" : "🌙";
            void Set(string key, string hex) => Resources[key] = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
            if (_dark)
            {
                Set("Accent", "#3AA0F0"); Set("AccentHover", "#5BB3F5"); Set("AccentPress", "#2A8AD6");
                Set("Bg", "#15171B"); Set("Pane", "#1C1F24"); Set("Card", "#23272E"); Set("Card2", "#262B32");
                Set("Text", "#E7EAEE"); Set("Muted", "#9AA3AD"); Set("Faint", "#6B7480");
                Set("Bd", "#333941"); Set("BdStrong", "#3D444D"); Set("Field", "#1B1E23"); Set("FieldBd", "#3D444D");
                Set("Nav", "#1A1D22"); Set("NavActive", "#1F2D3D");
                Set("AmberBg", "#3A2F12"); Set("AmberBd", "#5C4A1E"); Set("AmberTx", "#F0C66A");
                Set("ConsoleBg", "#0A0C10"); Set("ConsoleTx", "#BFE0C8"); Set("Ok", "#46C97E");
            }
            else
            {
                Set("Accent", "#0070C0"); Set("AccentHover", "#0D83DA"); Set("AccentPress", "#005A9C");
                Set("Bg", "#EEF1F5"); Set("Pane", "#F7F8FA"); Set("Card", "#FFFFFF"); Set("Card2", "#FBFCFD");
                Set("Text", "#1F2329"); Set("Muted", "#6B7480"); Set("Faint", "#9AA3AD");
                Set("Bd", "#E1E5EA"); Set("BdStrong", "#CDD3DA"); Set("Field", "#FFFFFF"); Set("FieldBd", "#CDD3DA");
                Set("Nav", "#F0F2F5"); Set("NavActive", "#E6F0FA");
                Set("AmberBg", "#FFF7E6"); Set("AmberBd", "#FFE1A8"); Set("AmberTx", "#8A5A00");
                Set("ConsoleBg", "#0E1116"); Set("ConsoleTx", "#CFE3D5"); Set("Ok", "#1A8F4C");
            }
        }

        // ── scope hint / reveals ────────────────────────────────────────────────
        private void UpdateScopeHint()
        {
            int idx = ScopeIndex();
            SavedSetRow.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
            BatchRow.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
            switch (idx)
            {
                case 1: LblScopeHint.Text = "→ select items in Navisworks first"; break;
                case 2: LblScopeHint.Text = "→ enable a section box in Navisworks first"; break;
                case 3: LblScopeHint.Text = "→ pick a saved set below"; PopulateSets(); break;
                case 4: LblScopeHint.Text = "→ tick sets below; each becomes its own IFC (choose an output folder)"; PopulateBatchSets(); break;
                default: LblScopeHint.Text = ""; break;
            }
        }

        private void RefreshModelStatus()
        {
            var doc = NavApp.ActiveDocument;
            LblModel.Text = (doc != null && doc.Models.Count > 0) ? "● Model loaded" : "";
        }

        // ── button handlers ───────────────────────────────────────────────────────
        private void OnBatchRefresh(object s, RoutedEventArgs e) => PopulateBatchSets();
        private void OnBatchAll(object s, RoutedEventArgs e) { foreach (var c in _batchItems) c.Checked = true; }
        private void OnBatchNone(object s, RoutedEventArgs e) { foreach (var c in _batchItems) c.Checked = false; }
        private void OnScanSelection(object s, RoutedEventArgs e) => ScanModel(true);
        private void OnScanModel(object s, RoutedEventArgs e) => ScanModel(false);
        private void OnCatsAll(object s, RoutedEventArgs e) { foreach (var c in _cats) c.Checked = true; }
        private void OnCatsNone(object s, RoutedEventArgs e) { foreach (var c in _cats) c.Checked = false; }
        private void OnDetectRoles(object s, RoutedEventArgs e) => ScanModel(false);
        private void OnParamAdd(object s, RoutedEventArgs e) => _paramRows.Add(new ParamRow { SourceCategory = AnyCat });
        private void OnParamRemove(object s, RoutedEventArgs e) { if (GridParams.SelectedItem is ParamRow r) _paramRows.Remove(r); }
        private void OnMapRefresh(object s, RoutedEventArgs e) => RefreshMapSets();
        private void OnMapAdd(object s, RoutedEventArgs e) => _mapRows.Add(new MapRow());
        private void OnMapClear(object s, RoutedEventArgs e) => _mapRows.Clear();
        private void OnPreviewBasePoint(object s, RoutedEventArgs e) => PreviewBasePoint();
        private void OnCopyReport(object s, RoutedEventArgs e) { if (!string.IsNullOrEmpty(TxtReport.Text)) try { System.Windows.Clipboard.SetText(TxtReport.Text); } catch { } }

        // ── unified scan ──────────────────────────────────────────────────────────
        private void ScanModel(bool fromSelection)
        {
            var doc = NavApp.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { SetStatus("Open a model first."); return; }

            List<ModelItem> items;
            if (fromSelection)
            {
                var sel = doc.CurrentSelection.SelectedItems;
                if (sel == null || sel.Count == 0) { SetStatus("Select elements in Navisworks, then Scan selection."); return; }
                items = ItemCollector.ResolveLeaves(sel);
            }
            else items = ResolveScope(doc) ?? ItemCollector.GetVisibleLeafItemsWithGeometry(doc);
            if (items.Count == 0) { SetStatus("No geometry elements to scan."); return; }

            BeginBusyMarquee("Scanning properties…");
            try
            {
                int cap = fromSelection ? Math.Max(items.Count, 50) : Math.Min(items.Count, 1000);
                _catParams = PropertyHarvester.ScanCategoryParams(items, cap);

                var cats = _catParams.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                var allParams = _catParams.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

                // psets checklist
                _cats.Clear(); foreach (var c in cats) _cats.Add(new CheckItem(c, true));

                // grid catalogs (categories + the property-name suggestion list)
                ParamRow.SharedCategories.Clear(); ParamRow.SharedCategories.Add(AnyCat); foreach (var c in cats) ParamRow.SharedCategories.Add(c);
                foreach (var row in _paramRows) row.Refresh();

                // role category combos
                _roleCats.Clear(); _roleCats.Add(AnyCat); foreach (var c in cats) _roleCats.Add(c);

                // auto-fill roles from a guess
                var g = PropertyHarvester.GuessRoles(_catParams);
                ApplyRole(_catType, _parType, g.Type);
                ApplyRole(_catLevel, _parLevel, g.Level);
                ApplyRole(_catMat, _parMat, g.Material);
                ApplyRole(_catCls, _parCls, g.Classification);

                SetStatus($"Scanned {cats.Count} categories / {allParams.Count} properties{(fromSelection ? " (selection)" : " (sampled)")} — roles auto-filled.");
            }
            catch (Exception ex) { SetStatus("Scan failed: " + ex.Message); }
            finally { EndBusy(); }
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

        private void ApplyRole(ComboBox cat, ComboBox par, PropRef guess)
        {
            cat.Text = string.IsNullOrEmpty(guess.Category) ? "" : guess.Category;
            PopulateParamCombo(par, cat.Text, keepText: false);
            par.Text = guess.Name ?? "";
        }

        private PropertyRoles BuildRoles() => new PropertyRoles
        {
            Type = MakeRef(_catType, _parType),
            Level = MakeRef(_catLevel, _parLevel),
            Material = MakeRef(_catMat, _parMat),
            Classification = MakeRef(_catCls, _parCls)
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

        // ── set → class mapping ─────────────────────────────────────────────────
        private void RefreshMapSets()
        {
            var doc = NavApp.ActiveDocument;
            _mapSets = doc != null ? ItemCollector.GetSelectionSets(doc) : new List<SelectionSet>();
            MapRow.SharedSets.Clear();
            foreach (var s in _mapSets) MapRow.SharedSets.Add(s.DisplayName ?? "(set)");
            SetStatus(_mapSets.Count == 0 ? "No saved sets — create some in Navisworks first." : $"{_mapSets.Count} sets available for mapping.");
        }

        private List<(SelectionSet, string)> BuildSetRules()
        {
            var rules = new List<(SelectionSet, string)>();
            foreach (var row in _mapRows)
            {
                var setName = row.Set; var cls = row.IfcClass; var predef = row.Predefined;
                if (string.IsNullOrEmpty(setName) || string.IsNullOrEmpty(cls)) continue;
                var set = _mapSets.FirstOrDefault(s => string.Equals(s.DisplayName, setName, StringComparison.Ordinal));
                if (set != null && TypeMapping.Catalog.ContainsKey(cls!)) rules.Add((set, TypeMapping.Encode(cls!, predef)));
            }
            return rules;
        }

        // ── export action ──────────────────────────────────────────────────────
        private void OnExport(object sender, RoutedEventArgs e)
        {
            try
            {
                var doc = NavApp.ActiveDocument;
                if (doc == null || doc.Models.Count == 0) { SetStatus("Open a model first."); return; }

                var schema = CmbSchema.SelectedIndex == 1 ? IfcSchema.Ifc2x3 : IfcSchema.Ifc4;
                string schemaName = schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2x3";
                bool batch = ScopeIndex() == 4;
                long splitLimit = SplitLimitBytes();
                (double unitScale, string unitName) = ResolveUnits(doc);
                var coords = BuildCoords();
                var names = new SpatialNames { Project = Def(TxtProject.Text, "BIMCamel Export"), Site = Def(TxtSite.Text, "Site"), Building = Def(TxtBuilding.Text, "Building"), Storey = Def(TxtStorey.Text, "Storey") };
                var setRules = BuildSetRules();
                double weldTolMetres = CmbQuality.SelectedIndex switch { 1 => 1e-3, 2 => 1e-6, _ => 1e-4 };
                int coordDecimals = CmbQuality.SelectedIndex switch { 1 => 3, 2 => 6, _ => 4 };

                if (batch) { RunBatchExport(doc, schema, schemaName, unitScale, unitName, coords, names, setRules, weldTolMetres, coordDecimals, splitLimit); return; }

                using var sfd = new WF.SaveFileDialog { Title = "Export IFC", Filter = "IFC file (*.ifc)|*.ifc", FileName = $"{ModelBaseName(doc)}_{schemaName}.ifc", DefaultExt = "ifc" };
                if (sfd.ShowDialog() != WF.DialogResult.OK) return;

                var scanSw = Stopwatch.StartNew();
                BeginBusyMarquee("Collecting elements…");
                var items = ResolveScope(doc, CollectTick);
                if (items == null) return;
                if (items.Count == 0) { SetStatus("No geometry elements in scope."); return; }

                var classMap = new Dictionary<string, string>();
                if (setRules.Count > 0) { SetStatus("Resolving mapping sets…"); PumpUi(Dispatcher); classMap = ItemCollector.BuildClassMap(doc, setRules); }
                var opts = BuildExtractOptions(classMap);

                SetStatus("Computing model extents…"); PumpUi(Dispatcher);
                var sm = ItemCollector.ScopeMinCorner(items, n => CollectTick(n));
                var geomMin = (sm.x * unitScale, sm.y * unitScale, sm.z * unitScale);
                scanSw.Stop();

                BeginBusy(items.Count);
                ExportTiming.Reset();
                long baseHeap = GC.GetTotalMemory(false); _peakHeap = baseHeap;
                var heapTimer = new System.Threading.Timer(_ => { long m = GC.GetTotalMemory(false); if (m > _peakHeap) _peakHeap = m; }, null, 0, 200);
                var sw = Stopwatch.StartNew();

                var summary = new ExportSummary();
                RunOneExport(sfd.FileName, schema, items, unitScale, coords, names, opts, weldTolMetres, coordDecimals, geomMin, splitLimit, summary, "Exporting");

                sw.Stop(); heapTimer.Dispose();
                FinishReport(summary, schemaName, ScopeLabel(), unitName, items.Count, scanSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, setRules.Count, baseHeap);
            }
            catch (Exception ex)
            {
                SetStatus("Export failed: " + ex.Message);
                SetReport("Export FAILED\n──────────────────────────────\n" + ex.Message);
                WF.MessageBox.Show(ex.ToString(), "BIMCamel export — error", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Error);
            }
            finally { EndBusy(); }
        }

        private void RunBatchExport(Document doc, IfcSchema schema, string schemaName, double unitScale, string unitName,
            CoordOptions coords, SpatialNames names, List<(SelectionSet, string)> setRules, double weldTolMetres, int coordDecimals, long splitLimit)
        {
            var chosen = new List<SelectionSet>();
            for (int i = 0; i < _batchItems.Count && i < _batchSets.Count; i++)
                if (_batchItems[i].Checked) chosen.Add(_batchSets[i]);
            if (chosen.Count == 0) { SetStatus("Tick at least one set to export (batch scope)."); return; }

            using var fbd = new WF.FolderBrowserDialog { Description = "Choose an output folder — one IFC per set" };
            if (fbd.ShowDialog() != WF.DialogResult.OK) return;
            string folder = fbd.SelectedPath;

            var classMap = new Dictionary<string, string>();
            if (setRules.Count > 0) { SetStatus("Resolving mapping sets…"); PumpUi(Dispatcher); classMap = ItemCollector.BuildClassMap(doc, setRules); }
            var opts = BuildExtractOptions(classMap);

            var scanSw = Stopwatch.StartNew();
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
                string baseName = Sanitize(set.DisplayName ?? $"set{setNum}") + "_" + schemaName + ".ifc";
                RunOneExport(System.IO.Path.Combine(folder, baseName), schema, items, unitScale, coords, names, opts, weldTolMetres, coordDecimals, geomMin, splitLimit, summary, $"Set {setNum}/{chosen.Count}");
            }
            sw.Stop(); heapTimer.Dispose();
            string scopeLabel = $"Batch — {chosen.Count} set(s)" + (skipped > 0 ? $", {skipped} empty skipped" : "");
            FinishReport(summary, schemaName, scopeLabel, unitName, totalItems, scanSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, setRules.Count, baseHeap);
        }

        private long SplitLimitBytes()
        {
            if (ChkSplit.IsChecked != true) return 0;
            return double.TryParse(TxtSplitMb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var mb) && mb > 0
                ? (long)(mb * 1024 * 1024) : 0;
        }

        private ExtractOptions BuildExtractOptions(Dictionary<string, string> classMap) => new ExtractOptions
        {
            Props = ChkProps.IsChecked == true, Materials = ChkMaterials.IsChecked == true,
            PsetFilter = SelectedCategories(), ClassMap = classMap,
            ParamMap = BuildParamRules(), Roles = BuildRoles()
        };

        private void RunOneExport(string basePath, IfcSchema schema, List<ModelItem> items, double unitScale, CoordOptions coords, SpatialNames names,
            ExtractOptions opts, double weldTolMetres, int coordDecimals, (double x, double y, double z) geomMin, long splitLimit, ExportSummary summary, string verb)
        {
            SwitchToDeterminate(items.Count);
            if (ChkInstancing.IsChecked == true)
            {
                opts.WeldTol = weldTolMetres;
                var stream = InstancedExtractor.ExtractStream(items, unitScale, opts, p => Tick(p, items.Count, verb));
                IfcExporter.ExportInstanced(basePath, schema, stream, Environment.UserName, unitScale, coords, ChkQuantities.IsChecked == true, coordDecimals, geomMin, names, splitLimit, summary);
            }
            else
            {
                opts.WeldTol = weldTolMetres / unitScale;
                var stream = MeshExtractor.ExtractStream(items, opts, p => Tick(p, items.Count, verb));
                IfcExporter.Export(basePath, schema, stream, Environment.UserName, unitScale, coords, ChkQuantities.IsChecked == true, coordDecimals, geomMin, names, splitLimit, summary);
            }
        }

        private void FinishReport(ExportSummary summary, string schemaName, string scopeLabel, string unitName, int scopeItemCount, long scanMs, long exportMs, int ruleCount, long baseHeap)
        {
            string validation = "not run";
            if (ChkValidate.IsChecked == true && summary.Files.Count > 0)
            {
                SetStatus("Validating…"); PumpUi(Dispatcher);
                var issues = new List<string>();
                foreach (var f in summary.Files) { var iss = IfcValidator.Validate(f); if (iss.Count > 0) issues.Add(System.IO.Path.GetFileName(f) + ": " + string.Join("; ", iss)); }
                validation = issues.Count == 0 ? $"passed ({summary.Files.Count} file(s))" : "ISSUES — " + string.Join(" | ", issues);
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

            string? openTarget = summary.Files.Count == 1 ? summary.Files[0] : (summary.Files.Count > 0 ? System.IO.Path.GetDirectoryName(summary.Files[0]) : null);
            if (openTarget != null && WF.MessageBox.Show($"Exported {summary.FileCount} file(s) · {summary.ElementCount:N0} elements ({summary.FileSizeBytes / 1024.0:N0} KB).\n\nOpen now?",
                    "BIMCamel export complete", WF.MessageBoxButtons.YesNo, WF.MessageBoxIcon.Information) == WF.DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(openTarget) { UseShellExecute = true }); }
                catch { try { Process.Start("explorer.exe", $"\"{openTarget}\""); } catch { } }
            }
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

        private List<ModelItem>? ResolveScope(Document doc, Action<int>? onProgress = null)
        {
            switch (ScopeIndex())
            {
                case 1:
                    var sel = doc.CurrentSelection.SelectedItems;
                    if (sel == null || sel.Count == 0) { SetStatus("Nothing selected. Select elements or choose another scope."); return null; }
                    return ItemCollector.ResolveLeaves(sel, onProgress);
                case 2:
                    try { return ItemCollector.GetItemsInSectionBox(doc, onProgress); }
                    catch (Exception sx) { SetStatus(sx.Message); WF.MessageBox.Show(sx.Message, "BIMCamel", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information); return null; }
                case 3:
                    int si = CmbSavedSet.SelectedIndex;
                    if (_savedSets.Count == 0 || si < 0 || si >= _savedSets.Count) { SetStatus("Pick a saved set (Scope = Saved set)."); return null; }
                    return ItemCollector.GetItemsFromSet(doc, _savedSets[si], onProgress);
                default:
                    return ItemCollector.GetVisibleLeafItemsWithGeometry(doc, onProgress);
            }
        }

        private CoordOptions BuildCoords() => new CoordOptions
        {
            Mode = CmbBasePoint.SelectedIndex switch { 1 => BasePointMode.ModelOrigin, 2 => BasePointMode.Custom, _ => BasePointMode.GeometryOrigin },
            WriteGeoref = ChkGeoref.IsChecked == true,
            CustomEastings = ParseD(TxtE.Text), CustomNorthings = ParseD(TxtN.Text), CustomElevation = ParseD(TxtElev.Text), RotationDeg = ParseD(TxtRot.Text)
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
            Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = false;
            Progress.Minimum = 0; Progress.Maximum = Math.Max(1, max); Progress.Value = 0;
            _tickClock.Restart(); _lastTickMs = 0; Mouse.OverrideCursor = Cursors.Wait;
        }
        private void Tick(int done, int max, string verb)
        {
            long ms = _tickClock.ElapsedMilliseconds;
            if (done != max && ms - _lastTickMs < 500) return;
            _lastTickMs = ms;
            if (done <= Progress.Maximum) Progress.Value = done;
            LblStatus.Text = $"{verb} {done}/{max}…";
            long t = ET.Now; PumpUi(Dispatcher); ET.UiTicks += ET.Now - t; ET.UiPumps++;
        }
        private void CollectTick(int visited)
        {
            long ms = _tickClock.ElapsedMilliseconds;
            if (ms - _lastTickMs < 120) return;
            _lastTickMs = ms;
            LblStatus.Text = $"Scanning model… {visited:N0} items"; PumpUi(Dispatcher);
        }
        private void BeginBusyMarquee(string status)
        {
            BtnExport.IsEnabled = false; Tabs.IsEnabled = false;
            Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true;
            _tickClock.Restart(); _lastTickMs = 0;
            Mouse.OverrideCursor = Cursors.Wait; SetStatus(status); PumpUi(Dispatcher);
        }
        private void SwitchToDeterminate(int max)
        {
            Progress.IsIndeterminate = false; Progress.Minimum = 0; Progress.Maximum = Math.Max(1, max); Progress.Value = 0;
            _tickClock.Restart(); _lastTickMs = 0;
        }
        private void EndBusy()
        {
            Mouse.OverrideCursor = null; Progress.Visibility = Visibility.Collapsed; Progress.IsIndeterminate = false;
            BtnExport.IsEnabled = true; Tabs.IsEnabled = true;
        }

        // Flush the WPF dispatcher (layout/render) then pump the host Win32 loop, so the hosted UI
        // stays live during the synchronous export. Mirrors the old Application.DoEvents() calls.
        private static void PumpUi(Dispatcher d)
        {
            d.Invoke(() => { }, DispatcherPriority.Background);
            WF.Application.DoEvents();
        }

        // ── base point preview ────────────────────────────────────────────────────
        private void PreviewBasePoint()
        {
            var doc = NavApp.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { LblPreview.Text = "Base point preview: open a model"; return; }
            (double scale, _) = ResolveUnits(doc);
            double x, y, z;
            switch (CmbBasePoint.SelectedIndex)
            {
                case 1: x = y = z = 0; break;
                case 2: x = ParseD(TxtE.Text); y = ParseD(TxtN.Text); z = ParseD(TxtElev.Text); break;
                default: var bb = ModelMinCorner(doc); x = bb.Item1 * scale; y = bb.Item2 * scale; z = bb.Item3 * scale; break;
            }
            LblPreview.Text = $"Base point preview: [{x:N3}, {y:N3}, {z:N3}] m";
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
            sb.AppendLine($"Georef      : {(s.GeorefWritten ? "IfcMapConversion written" : (schema == "IFC2x3" ? "baked into placement (IFC2x3)" : "off"))}");
            sb.AppendLine($"Elements    : {s.ElementCount:N0}");
            sb.AppendLine($"Triangles   : {s.TriangleCount:N0}");
            sb.AppendLine(s.Instanced
                ? $"Instancing  : ON · {s.UniqueGeometries:N0} unique / {s.InstanceCount:N0} instances" + (s.UniqueGeometries > 0 ? $"  (×{(double)s.InstanceCount / s.UniqueGeometries:0.0})" : "")
                : "Instancing  : off");
            sb.AppendLine($"Class rules : {ruleCount} set→class");
            sb.AppendLine($"Storeys     : {s.StoreyCount}");
            sb.AppendLine($"Type objects: {s.TypeCount}   Materials: {s.MaterialCount}   Classifications: {s.ClassificationCount}");
            sb.AppendLine($"Property sets: {s.PsetUnique:N0} unique / {s.PsetRefs:N0} refs" + (s.PsetUnique > 0 ? $"  (×{(double)s.PsetRefs / s.PsetUnique:0.0} shared)" : ""));
            sb.AppendLine($"Quantities  : {(s.QuantitiesWritten ? "computed (volume/area/length)" : "none")}");
            sb.AppendLine($"{(s.FileCount > 1 ? "Total size  " : "File size   ")}: {s.FileSizeBytes / 1024.0:N0} KB");
            sb.AppendLine($"Scan        : {scanMs:N0} ms  (tree walk + extents)");
            sb.AppendLine($"Export      : {ms:N0} ms  ({tps:N0} tris/s)");
            double com = ET.Ms(ET.ComConvertTicks), rd = ET.Ms(ET.ReadTicks), hv = ET.Ms(ET.HarvestTicks), wl = ET.Ms(ET.WeldTicks);
            double gw = ET.Ms(ET.GeomWriteTicks), pw = ET.Ms(ET.PropWriteTicks), qw = ET.Ms(ET.QtyTicks), ui = ET.Ms(ET.UiTicks);
            double other = ms - com - rd - hv - wl - gw - pw - qw - ui; if (other < 0) other = 0;
            sb.AppendLine($"  COM convert : {com,10:N0} ms   ({ET.ComConverts:N0} items)");
            sb.AppendLine($"  Geometry rd : {rd,10:N0} ms   ({ET.Fragments:N0} fragments)");
            sb.AppendLine($"  Prop harvest: {hv,10:N0} ms");
            sb.AppendLine($"  Weld        : {wl,10:N0} ms");
            sb.AppendLine($"  Geom write  : {gw,10:N0} ms");
            sb.AppendLine($"  Prop write  : {pw,10:N0} ms");
            sb.AppendLine($"  Qty compute : {qw,10:N0} ms");
            sb.AppendLine($"  UI pump     : {ui,10:N0} ms   ({ET.UiPumps:N0} DoEvents)");
            sb.AppendLine($"  Other       : {other,10:N0} ms");
            sb.AppendLine($"Peak heap   : {peakHeap / 1048576.0:N0} MB  (start {baseHeap / 1048576.0:N0} MB)");
            sb.AppendLine($"Validation  : {validation}");
            sb.AppendLine($"Exported    : {DateTime.Now:yyyy-MM-dd HH:mm}");

            double harvestPerEl = s.ElementCount > 0 ? hv / s.ElementCount : 0;
            if (s.ElementCount > 1000 && harvestPerEl > 2.0)
            {
                sb.AppendLine();
                sb.AppendLine("──────────────────────────────");
                sb.AppendLine($"⚠  SLOW PROPERTY HARVEST: {harvestPerEl:0.0} ms/element (normal < 1 ms).");
                sb.AppendLine("   This almost always means an ACTIVE DataTools / external-database link:");
                sb.AppendLine("   every object triggers a database query, which is slow — and if the link");
                sb.AppendLine("   is broken the console fills with 'DATATOOLS_SQL_EXEC ... Sheet1$' errors.");
                sb.AppendLine("   Fix: the console error names the missing object; open Home → DataTools,");
                sb.AppendLine("   then deactivate/delete that link and re-export.");
            }

            if (!string.IsNullOrEmpty(profile)) { sb.AppendLine("──────────────────────────────"); sb.Append(profile); }
            return sb.ToString();
        }

        // ── profiles ───────────────────────────────────────────────────────────────
        private void OnSaveProfile(object s, RoutedEventArgs e)
        {
            try
            {
                using var sfd = new WF.SaveFileDialog { Title = "Save BIMCamel profile", Filter = "BIMCamel profile (*.json)|*.json", FileName = "bimcamel_profile.json", DefaultExt = "json" };
                if (sfd.ShowDialog() != WF.DialogResult.OK) return;
                ExportProfile.Save(Capture(), sfd.FileName); SetStatus("Profile saved.");
            }
            catch (Exception ex) { WF.MessageBox.Show(ex.Message, "BIMCamel", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Error); }
        }
        private void OnLoadProfile(object s, RoutedEventArgs e)
        {
            try
            {
                using var ofd = new WF.OpenFileDialog { Title = "Load BIMCamel profile", Filter = "BIMCamel profile (*.json)|*.json" };
                if (ofd.ShowDialog() != WF.DialogResult.OK) return;
                Apply(ExportProfile.Load(ofd.FileName)); SetStatus("Profile loaded.");
            }
            catch (Exception ex) { WF.MessageBox.Show(ex.Message, "BIMCamel", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Error); }
        }
        private ExportProfile Capture() => new ExportProfile
        {
            Schema = CmbSchema.SelectedIndex, Units = CmbUnits.SelectedIndex, Scope = ScopeIndex(),
            Quality = CmbQuality.SelectedIndex, BasePoint = CmbBasePoint.SelectedIndex,
            CustomE = ParseD(TxtE.Text), CustomN = ParseD(TxtN.Text), CustomElev = ParseD(TxtElev.Text), Rotation = ParseD(TxtRot.Text),
            Georef = ChkGeoref.IsChecked == true, Props = ChkProps.IsChecked == true, Materials = ChkMaterials.IsChecked == true, Instancing = ChkInstancing.IsChecked == true,
            Validate = ChkValidate.IsChecked == true, Mapping = GridToText()
        };
        private void Apply(ExportProfile p)
        {
            CmbSchema.SelectedIndex = Clamp(p.Schema, CmbSchema.Items.Count);
            CmbUnits.SelectedIndex = Clamp(p.Units, CmbUnits.Items.Count);
            int sc = Clamp(p.Scope, _scopeRadios.Count); _scopeRadios[sc].IsChecked = true;
            CmbQuality.SelectedIndex = Clamp(p.Quality, CmbQuality.Items.Count);
            CmbBasePoint.SelectedIndex = Clamp(p.BasePoint, CmbBasePoint.Items.Count);
            TxtE.Text = Inv(p.CustomE); TxtN.Text = Inv(p.CustomN); TxtElev.Text = Inv(p.CustomElev); TxtRot.Text = Inv(p.Rotation);
            ChkGeoref.IsChecked = p.Georef; ChkProps.IsChecked = p.Props; ChkMaterials.IsChecked = p.Materials;
            ChkInstancing.IsChecked = p.Instancing; ChkValidate.IsChecked = p.Validate;
            TextToGrid(p.Mapping);
        }
        private string GridToText()
        {
            var sb = new StringBuilder();
            foreach (var row in _mapRows)
            {
                var t = (row.Set ?? "").Trim(); var c = (row.IfcClass ?? "").Trim(); var pd = (row.Predefined ?? "").Trim();
                if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(c))
                    sb.AppendLine(string.IsNullOrEmpty(pd) ? $"{t} => {c}" : $"{t} => {c} :: {pd}");
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
                string predef = ""; int pp = rhs.IndexOf("::", StringComparison.Ordinal);
                if (pp >= 0) { predef = rhs.Substring(pp + 2).Trim(); rhs = rhs.Substring(0, pp).Trim(); }
                if (setName.Length == 0 || !TypeMapping.Catalog.ContainsKey(rhs)) continue;
                if (!MapRow.SharedSets.Contains(setName)) MapRow.SharedSets.Add(setName);
                _mapRows.Add(new MapRow { Set = setName, IfcClass = rhs, Predefined = predef });
            }
        }

        // ── sets ─────────────────────────────────────────────────────────────────
        private void PopulateBatchSets()
        {
            var doc = NavApp.ActiveDocument;
            _batchSets = doc != null ? ItemCollector.GetSelectionSets(doc) : new List<SelectionSet>();
            _batchItems.Clear();
            foreach (var s in _batchSets) _batchItems.Add(new CheckItem(s.DisplayName ?? "(set)", false));
            SetStatus(_batchSets.Count == 0 ? "No saved/search sets — create some in Navisworks first." : $"{_batchSets.Count} sets available — tick the ones to export.");
        }
        private void PopulateSets()
        {
            var doc = NavApp.ActiveDocument;
            _savedSets = doc != null ? ItemCollector.GetSelectionSets(doc) : new List<SelectionSet>();
            CmbSavedSet.Items.Clear();
            if (_savedSets.Count == 0) CmbSavedSet.Items.Add("(no saved sets in document)");
            else foreach (var s in _savedSets) CmbSavedSet.Items.Add(s.DisplayName ?? "(set)");
            CmbSavedSet.SelectedIndex = 0;
        }

        // ── small helpers ──────────────────────────────────────────────────────────
        private (double scale, string name) ResolveUnits(Document doc) => CmbUnits.SelectedIndex switch
        {
            1 => (0.001, "mm (forced)"), 2 => (0.01, "cm (forced)"), 3 => (1.0, "m (forced)"),
            4 => (0.3048, "ft (forced)"), 5 => (0.0254, "in (forced)"), _ => UnitsToMetre(doc.Units)
        };
        private void SetStatus(string text) => LblStatus.Text = text;
        private void SetReport(string text) => TxtReport.Text = text.Replace("\n", Environment.NewLine);
        private static double ParseD(string s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0.0;
        private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
        private static int Clamp(int v, int count) => v < 0 ? 0 : (v >= count ? count - 1 : v);
        private static string Def(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

        private static (double scale, string name) UnitsToMetre(Units u)
        {
            switch (u)
            {
                case Units.Meters: return (1.0, "m");
                case Units.Centimeters: return (0.01, "cm");
                case Units.Millimeters: return (0.001, "mm");
                case Units.Kilometers: return (1000.0, "km");
                case Units.Feet: return (0.3048, "ft");
                case Units.Inches: return (0.0254, "in");
                case Units.Yards: return (0.9144, "yd");
                case Units.Miles: return (1609.344, "mi");
                default: return (1.0, u.ToString());
            }
        }
        private static string ModelBaseName(Document doc)
        {
            try { var fn = doc.FileName; if (!string.IsNullOrEmpty(fn)) return System.IO.Path.GetFileNameWithoutExtension(fn); }
            catch { }
            return "BIMCamel_export";
        }
    }
}
