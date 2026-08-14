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
            Loaded += (_, _) => { UpdateScopeHint(); PreviewBasePoint(); RefreshModelStatus(); RefreshSets(); RenderPreflight(); };
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
            AddItems(CmbGroups, new[] { "(nothing)", "IfcGroup", "IfcSystem", "IfcZone" }); CmbGroups.SelectedIndex = 0;
            AddItems(CmbQuality, new[] { "Balanced", "Small file", "High detail" }); CmbQuality.SelectedIndex = 0;
            AddItems(CmbBasePoint, new[] { "Geometry origin — recommended (real coords kept in georeferencing)", "Model origin (no offset — keeps world coords)", "Custom base point" }); CmbBasePoint.SelectedIndex = 0;

            // Validation defaults ON: it exists to catch the mistakes this exporter can make, and
            // silently shipping an invalid file is worse than the second it costs to check.
            ChkGeoref.IsChecked = true; ChkInstancing.IsChecked = true; ChkValidate.IsChecked = true; ChkProfile.IsChecked = true;
            ChkProps.IsChecked = true; ChkMaterials.IsChecked = true; ChkQuantities.IsChecked = true; ChkSplit.IsChecked = false;
            TxtE.Text = TxtN.Text = TxtElev.Text = TxtRot.Text = "0";
            TxtSurveyE.Text = TxtSurveyN.Text = TxtSurveyElev.Text = "0";

            CmbBasePoint.SelectionChanged += (_, _) => { CoordRow.Visibility = CmbBasePoint.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; PreviewBasePoint(); };
            TxtE.TextChanged += (_, _) => PreviewBasePoint();
            TxtN.TextChanged += (_, _) => PreviewBasePoint();
            TxtElev.TextChanged += (_, _) => PreviewBasePoint();
            void SyncProps() => LstCategories.IsEnabled = ChkProps.IsChecked == true;
            ChkProps.Checked += (_, _) => SyncProps(); ChkProps.Unchecked += (_, _) => SyncProps(); SyncProps();
            void SyncGeoref() => GeorefRow.Visibility = ChkGeoref.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ChkGeoref.Checked += (_, _) => SyncGeoref(); ChkGeoref.Unchecked += (_, _) => SyncGeoref(); SyncGeoref();
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
            switch (idx)
            {
                case 1: LblScopeHint.Text = "→ select items in Navisworks first"; break;
                case 2: LblScopeHint.Text = "→ enable a section box in Navisworks first"; break;
                case 3: LblScopeHint.Text = "→ pick a saved set below"; RefreshSets(); break;
                case 4: LblScopeHint.Text = "→ tick sets below; each becomes its own IFC (choose an output folder)"; RefreshSets(); break;
                default: LblScopeHint.Text = ""; break;
            }
        }

        private void RefreshModelStatus()
        {
            var doc = NavApp.ActiveDocument;
            LblModel.Text = (doc != null && doc.Models.Count > 0) ? "● Model loaded" : "";
        }

        // ── button handlers ───────────────────────────────────────────────────────
        private void OnBatchAll(object s, RoutedEventArgs e) { foreach (var c in _batchItems) c.Checked = true; }
        private void OnBatchNone(object s, RoutedEventArgs e) { foreach (var c in _batchItems) c.Checked = false; }
        private void OnScanSelection(object s, RoutedEventArgs e) { _cancelRequested = false; ScanSelection(); }
        private void OnCatsAll(object s, RoutedEventArgs e) { foreach (var c in _cats) c.Checked = true; }
        private void OnCatsNone(object s, RoutedEventArgs e) { foreach (var c in _cats) c.Checked = false; }
        private void OnParamAdd(object s, RoutedEventArgs e) => _paramRows.Add(new ParamRow { SourceCategory = AnyCat });
        private void OnParamRemove(object s, RoutedEventArgs e) { if (GridParams.SelectedItem is ParamRow r) _paramRows.Remove(r); }
        private void OnMapAdd(object s, RoutedEventArgs e) => _mapRows.Add(new MapRow());
        private void OnMapRemove(object s, RoutedEventArgs e) { if (GridMap.SelectedItem is MapRow r) _mapRows.Remove(r); }
        private void OnMapClear(object s, RoutedEventArgs e) => _mapRows.Clear();
        private void OnPreviewMapping(object s, RoutedEventArgs e)
        {
            _cancelRequested = false;
            try { PreviewMapping(); }
            catch (OperationCanceledException) { SetStatus("Preview stopped."); }
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
                var items = ResolveScope(doc, CollectTick);
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
                sb.AppendLine(digest ?? "Preview        : did not run — open the Mapping tab for the reason");

                sb.Append("Review the ✨ AUTO sections on Data and Mapping, then Export.");
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
                var sel = doc.CurrentSelection.SelectedItems;
                if (sel == null || sel.Count == 0) { SetStatus("Select elements in Navisworks, then Scan selection."); return false; }
                var items = ItemCollector.ResolveLeaves(sel, CollectTick);
                if (items.Count == 0) { SetStatus("No geometry elements to scan."); return false; }
                return ScanFrom(items, "selected");
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
            var sample = Spread(items, SampleCap);
            _catParams = PropertyHarvester.ScanCategoryParams(sample, SampleCap);

            var cats = _catParams.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var allParams = _catParams.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

            // Psets checklist — unticks survive a rescan by name. Rebuilding
            // all-ticked meant every scan quietly reverted the user's pset
            // filter: the same proposal-beats-decision bug as the roles.
            var unticked = new HashSet<string>(_cats.Where(c => !c.Checked).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            _cats.Clear(); foreach (var c in cats) _cats.Add(new CheckItem(c, !unticked.Contains(c)));

            // grid catalogs (categories + the property-name suggestion list)
            ParamRow.SharedCategories.Clear(); ParamRow.SharedCategories.Add(AnyCat); foreach (var c in cats) ParamRow.SharedCategories.Add(c);
            foreach (var row in _paramRows) row.Refresh();

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

            string read = sample.Count < items.Count
                ? $"a sample of {sample.Count:N0} of {items.Count:N0} {sourceLabel} elements"
                : $"all {items.Count:N0} {sourceLabel} elements";
            SetStatus($"Scanned {cats.Count} categories / {allParams.Count} properties from {read} — {_lastRolesFilled} role(s) filled, {_lastRolesKept} kept.");
            return true;
        }

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
            cat.Text = string.IsNullOrEmpty(guess.Category) ? "" : guess.Category;
            PopulateParamCombo(par, cat.Text, keepText: false);
            par.Text = guess.Name ?? "";
            if (!string.IsNullOrWhiteSpace(par.Text)) _lastRolesFilled++;
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

                var schema = CmbSchema.SelectedIndex == 1 ? IfcSchema.Ifc2x3 : IfcSchema.Ifc4;
                string schemaName = schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2x3";
                bool batch = ScopeIndex() == 4;
                long splitLimit = SplitLimitBytes();
                (double unitScale, string unitName) = ResolveUnits(doc);
                var coords = BuildCoords();
                var names = new SpatialNames
                {
                    Project = Def(TxtProject.Text, "BIMCamel Export"), Site = Def(TxtSite.Text, "Site"),
                    Building = Def(TxtBuilding.Text, "Building"), Storey = Def(TxtStorey.Text, "Storey"),
                    // Real storey elevations, when the model's grids name the levels.
                    LevelElevations = ModelSurvey.LevelElevations(doc, unitScale),
                    ClassificationSystem = (TxtClassSystem.Text ?? "").Trim(),
                    GroupEntity = CmbGroups.SelectedIndex switch { 1 => "IFCGROUP", 2 => "IFCSYSTEM", 3 => "IFCZONE", _ => "" }
                };
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

                var maps = new ItemCollector.SetMaps();
                // One ItemKey memo shared between rule resolution and the
                // per-element lookups in the extractor — the same items get
                // their key computed once instead of twice.
                var keyCache = setRules.Count > 0 ? new Dictionary<ModelItem, string>() : null;
                if (setRules.Count > 0) { SetStatus("Resolving mapping sets…"); PumpUi(Dispatcher); maps = ItemCollector.BuildSetMaps(doc, setRules, CollectTick, keyCache); }
                var opts = BuildExtractOptions(maps);
                opts.KeyCache = keyCache;

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
            catch (OperationCanceledException)
            {
                // Only reachable from the collect phase — BeginBusy withdraws Stop
                // before the first byte is written — so "no file" is a fact, not a hope.
                SetStatus("Export stopped while collecting — no file was written.");
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
            CoordOptions coords, SpatialNames names, List<ItemCollector.SetRule> setRules, double weldTolMetres, int coordDecimals, long splitLimit)
        {
            var chosen = new List<SelectionSet>();
            for (int i = 0; i < _batchItems.Count && i < _sets.Count; i++)
                if (_batchItems[i].Checked) chosen.Add(_sets[i]);
            if (chosen.Count == 0) { SetStatus("Tick at least one set to export (batch scope)."); return; }

            using var fbd = new WF.FolderBrowserDialog { Description = "Choose an output folder — one IFC per set" };
            if (fbd.ShowDialog() != WF.DialogResult.OK) return;
            string folder = fbd.SelectedPath;

            var maps = new ItemCollector.SetMaps();
            var keyCache = setRules.Count > 0 ? new Dictionary<ModelItem, string>() : null;
            if (setRules.Count > 0) { SetStatus("Resolving mapping sets…"); PumpUi(Dispatcher); maps = ItemCollector.BuildSetMaps(doc, setRules, CollectTick, keyCache); }
            var opts = BuildExtractOptions(maps);
            opts.KeyCache = keyCache;

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

        private ExtractOptions BuildExtractOptions(ItemCollector.SetMaps maps) => new ExtractOptions
        {
            Props = ChkProps.IsChecked == true, Materials = ChkMaterials.IsChecked == true,
            PsetFilter = SelectedCategories(), ClassMap = maps.Class, ClassificationMap = maps.Classification,
            GroupMap = CmbGroups.SelectedIndex > 0 ? maps.Group : null,
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
            // Reset here, not only inside the visible-only walks: scopes that
            // include hidden items never touch the counter, and the pre-flight
            // panel reads it right after this call.
            ItemCollector.HiddenSkipped = 0;
            switch (ScopeIndex())
            {
                case 1:
                    var sel = doc.CurrentSelection.SelectedItems;
                    if (sel == null || sel.Count == 0) { SetStatus("Nothing selected. Select elements or choose another scope."); return null; }
                    return ItemCollector.ResolveLeaves(sel, onProgress);
                case 2:
                    try { return ItemCollector.GetItemsInSectionBox(doc, onProgress); }
                    // Stop is not a "no section box" problem: let it unwind rather
                    // than popping a message box that blames the user's setup.
                    catch (OperationCanceledException) { throw; }
                    catch (Exception sx) { SetStatus(sx.Message); WF.MessageBox.Show(sx.Message, "BIMCamel", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information); return null; }
                case 3:
                    int si = CmbSavedSet.SelectedIndex;
                    if (_sets.Count == 0 || si < 0 || si >= _sets.Count) { SetStatus("Pick a saved set (Scope = Saved set)."); return null; }
                    return ItemCollector.GetItemsFromSet(doc, _sets[si], onProgress);
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
                    return union;
                }
                default:
                    return ItemCollector.GetVisibleLeafItemsWithGeometry(doc, onProgress);
            }
        }

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
            long ms = _tickClock.ElapsedMilliseconds;
            if (ms - _lastTickMs < 120) return;
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
            if (doc == null || doc.Models.Count == 0) { TxtPreview.Text = "Open a model first."; return null; }
            try
            {
                BeginBusyMarquee("Previewing mapping…", cancelable: true);
                var schema = CmbSchema.SelectedIndex == 1 ? IfcSchema.Ifc2x3 : IfcSchema.Ifc4;
                var items = scope ?? ResolveScope(doc, CollectTick);
                if (items == null) { TxtPreview.Text = "Pick a scope first."; return null; }

                var rules = BuildSetRules();
                SetStatus("Resolving mapping sets…"); PumpUi(Dispatcher);
                var keyCache = rules.Count > 0 ? new Dictionary<ModelItem, string>() : null;
                var maps = rules.Count > 0 ? ItemCollector.BuildSetMaps(doc, rules, CollectTick, keyCache) : new ItemCollector.SetMaps();

                int mapped = 0, unmapped = 0, degraded = 0, classified = 0, walked = 0;
                var byClass = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var it in items)
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
                    else unmapped++;
                }

                var facts = new PreflightFacts
                {
                    When = DateTime.Now, ScopeLabel = ScopeLabel(), Fingerprint = PreflightFingerprint(),
                    Items = items.Count, Mapped = mapped, Unmapped = unmapped, Degraded = degraded,
                    Classified = classified, Rules = rules.Count,
                    ByClass = new Dictionary<string, int>(byClass, StringComparer.Ordinal),
                    HiddenExcluded = ItemCollector.HiddenSkipped,
                };

                var sb = new StringBuilder();
                sb.AppendLine($"Scope       : {ScopeLabel()}   {items.Count:N0} items");
                sb.AppendLine($"Schema      : {(schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2x3")}");
                sb.AppendLine($"Rules       : {rules.Count} set rule(s)");
                sb.AppendLine();
                sb.AppendLine($"Mapped      : {mapped:N0}" + (items.Count > 0 ? $"  ({100.0 * mapped / items.Count:0.#}%)" : ""));
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
                SetStatus($"Preview: {mapped:N0} mapped, {proxy:N0} proxy of {items.Count:N0} items.");
                _preflight = facts;
                RenderPreflight();
                double pct = items.Count > 0 ? 100.0 * mapped / items.Count : 0;
                return $"Preview        : {mapped:N0} of {items.Count:N0} elements map ({pct:0.#}%), {proxy:N0} proxy — details on the Mapping tab";
            }
            catch (OperationCanceledException)
            {
                // Rethrown so a stopped preview inside Smart setup stops the CHAIN
                // instead of reporting itself as a step that merely "did not run".
                // _preflight is untouched — the panel keeps the previous, correctly
                // fingerprinted facts rather than inventing partial ones.
                TxtPreview.Text = "Preview stopped before it finished.";
                throw;
            }
            catch (Exception ex)
            {
                TxtPreview.Text = "Preview failed: " + ex.Message;
                SetStatus("Preview failed: " + ex.Message);
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
            var levels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Count; i += step)
            {
                try
                {
                    var rv = PropertyHarvester.ReadRoles(items[i], roles);
                    n++;
                    if (!string.IsNullOrWhiteSpace(rv.Type)) type++;
                    if (!string.IsNullOrWhiteSpace(rv.Level)) { level++; levels.Add(rv.Level.Trim()); }
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
        }
        private PreflightFacts? _preflight;

        /// <summary>Everything the preview numbers depend on. Changing any of it
        /// makes the stored facts a description of a different export.</summary>
        private string PreflightFingerprint() =>
            ScopeIndex() + "|" + CmbSavedSet.SelectedIndex + "|" +
            string.Join(",", _batchItems.Where(b => b.Checked).Select(b => b.Name)) + "|" +
            GridToText() + "|" + RolesToText();

        private void RenderPreflight()
        {
            PreflightHost.Children.Clear();
            var doc = NavApp.ActiveDocument;
            bool schema2x3 = CmbSchema.SelectedIndex == 1;
            var f = _preflight;
            bool stale = f != null && f.Fingerprint != PreflightFingerprint();

            LblPreflightWhen.Text = f == null
                ? "Checks that need a scan fill in after ✨ Smart setup (above) or 🔍 Preview (Mapping tab)."
                : $"Scanned {f.When:HH:mm} · {f.ScopeLabel}" + (stale ? "  —  ⚠ settings changed since; re-run Smart setup or Preview" : "");

            Brush ok = (Brush)FindResource("Ok"), warn = (Brush)FindResource("AmberTx"),
                  info = (Brush)FindResource("Muted"), dim = (Brush)FindResource("Faint");
            string tail = stale ? "  (before the change)" : "";

            // ── scan-dependent rows ─────────────────────────────────────────
            int degradedLive = 0;
            if (f == null)
            {
                PreflightRow(dim, "Mapping · storeys · roles · geometry — pending a scan");
            }
            else
            {
                // A green tick that can be stale is worse than no panel: stale
                // scan rows drop to neutral so only CURRENT facts show green.
                Brush okNow = stale ? info : ok;

                string hidden = f.HiddenExcluded > 0 ? $" · {f.HiddenExcluded:N0} hidden subtree(s) excluded" : "";
                PreflightRow(okNow, $"Scope — {f.Items:N0} elements collected{hidden}{tail}");

                // Re-derive the IFC2x3 degradation against the schema selected
                // NOW — the preview-time value describes a different export the
                // moment the schema combo changes.
                if (schema2x3 && f.ByClass != null)
                    foreach (var kv in f.ByClass)
                        if (TypeMapping.Catalog.TryGetValue(kv.Key, out var c) && c.Ifc2x3.Length == 0) degradedLive += kv.Value;
                int proxyLive = f.Unmapped + degradedLive;
                double unmappedPct = f.Items > 0 ? 100.0 * f.Unmapped / f.Items : 0;

                if (f.Rules == 0)
                    // Zero rules is a deliberate geometry-only hand-off, not a
                    // defect — neutral, never amber.
                    PreflightRow(info, "Mapping — no rules: every element exports as IfcBuildingElementProxy (fine for a geometry hand-off)", "→ Mapping");
                else if (unmappedPct > 20)
                    PreflightRow(warn, $"Mapping — {f.Mapped:N0} matched a rule · {f.Unmapped:N0} did not ({unmappedPct:0.#}%) and export as proxy{tail}", "→ Mapping");
                else
                    PreflightRow(okNow, $"Mapping — {f.Mapped:N0} matched a rule · {proxyLive:N0} will export as proxy{tail}");

                if (f.RolesConfigured && f.RoleSample > 0)
                {
                    string samp = f.SamplePartial ? $"sample {f.RoleSample} of {f.Items:N0}" : $"all {f.RoleSample}";
                    string storeys = f.SamplePartial ? $"≥ {f.DistinctLevels}" : f.DistinctLevels.ToString();
                    if (f.LevelPct < 60 || f.DistinctLevels <= 1)
                        PreflightRow(warn, $"Storeys — ≈{f.LevelPct}% of elements have a level · {storeys} distinct store{(f.DistinctLevels == 1 ? "y — check the Level role" : "ys")} ({samp}){tail}", "→ Data (Level role)");
                    else if (f.LevelPct < 90)
                        PreflightRow(info, $"Storeys — ≈{f.LevelPct}% have a level · {storeys} distinct storeys ({samp}){tail}");
                    else
                        PreflightRow(okNow, $"Storeys — ≈{f.LevelPct}% have a level · {storeys} distinct storeys ({samp}){tail}");
                    PreflightRow(info, $"Roles — Type ≈{f.TypePct}% · Material ≈{f.MatPct}% · Classification ≈{f.ClsPct}% ({samp}){tail}");
                }
                else
                {
                    PreflightRow(info, "Storeys & roles — no roles set: single fallback storey, no types/materials from properties", "→ Data");
                }

                if (schema2x3 && degradedLive > 0)
                    PreflightRow(warn, $"Schema — IFC2x3: {degradedLive:N0} rule-matched element(s) use classes IFC2x3 cannot represent → they fall to proxy. Export IFC4 to keep them.");

                PreflightRow(info, $"Geometry — {(f.GeomSampled ? "≈" : "")}{f.Prims:N0} primitives · instancing {(ChkInstancing.IsChecked == true ? "on" : "off")}{tail}");
            }

            // ── instant rows (always live, no scan needed) ──────────────────
            // Severity rule: unset is NORMAL (grey) — a local-grid project has
            // no CRS and that is not a defect. Amber is reserved for
            // CONTRADICTIONS: data that will silently not be written.
            var coords = BuildCoords();
            if (ChkGeoref.IsChecked != true && coords.HasGeorefData)
                PreflightRow(warn, "Georeferencing — CRS/survey point filled in, but writing is OFF: nothing will be written", "→ Coordinates");
            else if (ChkGeoref.IsChecked != true)
                PreflightRow(info, "Georeferencing — off (placement still carries the world position)");
            else if (schema2x3 && coords.HasGeorefData)
                PreflightRow(warn, "Georeferencing — configured, but IfcMapConversion does not exist in IFC2x3: it will not be written. Export IFC4 to keep it.", "→ Coordinates");
            else if (!coords.HasGeorefData)
                PreflightRow(info, "Georeferencing — on, but no CRS or survey point set: nothing to write (fine on a local grid)", "→ Coordinates");
            else
                PreflightRow(ok, $"Georeferencing — IfcMapConversion · {(string.IsNullOrWhiteSpace(coords.CrsName) ? "local CRS" : coords.CrsName)}");

            // Cheap contradictions a coordinator would want caught before, not
            // after, a 40-minute export:
            if (ChkProps.IsChecked == true && _cats.Count > 0 && _cats.All(c => !c.Checked))
                PreflightRow(warn, "Properties — export is ON but every property set is unticked: no properties will be written", "→ Data");
            if (ScopeIndex() == 4)
            {
                var ticked = new List<string>();
                for (int i = 0; i < _batchItems.Count && i < _sets.Count; i++)
                    if (_batchItems[i].Checked) ticked.Add(Sanitize(_batchItems[i].Name));
                if (ticked.Count == 0)
                    PreflightRow(warn, "Batch — no sets ticked: nothing would export");
                else if (ticked.Distinct(StringComparer.OrdinalIgnoreCase).Count() < ticked.Count)
                    PreflightRow(warn, "Batch — two ticked sets sanitise to the same file name: one IFC would overwrite the other. Rename a set in Navisworks.");
            }
            var dupNames = new HashSet<string>(
                _sets.GroupBy(x => x.DisplayName ?? "").Where(g => g.Key.Length > 0 && g.Count() > 1).Select(g => g.Key),
                StringComparer.Ordinal);
            if (dupNames.Count > 0 && _mapRows.Any(r => r.Set != null && dupNames.Contains(r.Set)))
                PreflightRow(warn, "Mapping — a rule references a set NAME that exists more than once in this document; rules bind to the first. Rename the duplicate in Navisworks.", "→ Mapping");

            if (doc != null && doc.Models.Count > 0)
            {
                try
                {
                    (double _, string unitName) = ResolveUnits(doc);
                    var models = ModelSurvey.Survey(doc);
                    bool sameOrigin = true, sameUnits = true;
                    for (int i = 1; i < models.Count; i++)
                    {
                        if (Math.Abs(models[i].Ox - models[0].Ox) > 1e-6 ||
                            Math.Abs(models[i].Oy - models[0].Oy) > 1e-6 ||
                            Math.Abs(models[i].Oz - models[0].Oz) > 1e-6) sameOrigin = false;
                        if (!string.Equals(models[i].Units, models[0].Units, StringComparison.Ordinal)) sameUnits = false;
                    }
                    if (models.Count > 1 && !sameUnits)
                        // One unitScale is applied to EVERY model at export
                        // (ResolveUnits) — mixed declared units means at least
                        // one discipline exports at the wrong size.
                        PreflightRow(warn, $"Federation — {models.Count} models with MIXED declared units; one scale ({unitName}) is applied to all", "→ Coordinates (origins report)");
                    else if (models.Count > 1 && !sameOrigin)
                        // Different origins are how correct federation positions
                        // disciplines — informational, not a warning.
                        PreflightRow(info, $"Federation — {models.Count} models, differing origins (normal) · units {unitName}", "→ Coordinates (origins report)");
                    else
                        PreflightRow(ok, models.Count > 1
                            ? $"Federation — {models.Count} models agree on origin and units · {unitName}"
                            : $"Units — {unitName}");
                }
                catch { PreflightRow(info, "Federation — could not read model metadata"); }
            }
            else
            {
                PreflightRow(dim, "No model open");
            }

            // Exactly one Schema row: the degraded warning above replaces it
            // only when it actually fired — judged by the LIVE derivation, the
            // same one the warning itself uses.
            if (!(schema2x3 && degradedLive > 0))
                PreflightRow(info, schema2x3
                    ? "Schema — IFC2x3 (no georeferencing entity; some classes unavailable — IFC4 recommended unless the receiver requires 2x3)"
                    : "Schema — IFC4");
        }

        /// <summary>One ✓/⚠ row: coloured dot, wrapping text, optional go-fix-it pointer.</summary>
        private void PreflightRow(Brush tone, string text, string? jump = null)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new TextBlock { Text = "●", FontSize = 9, Foreground = tone, Margin = new Thickness(0, 3, 7, 0), VerticalAlignment = VerticalAlignment.Top };
            var body = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Text"), LineHeight = 15 };
            body.Inlines.Add(text);
            if (jump != null)
            {
                body.Inlines.Add("  ");
                body.Inlines.Add(new System.Windows.Documents.Run(jump) { Foreground = (Brush)FindResource("Accent") });
            }
            Grid.SetColumn(dot, 0); Grid.SetColumn(body, 1);
            grid.Children.Add(dot); grid.Children.Add(body);
            PreflightHost.Children.Add(grid);
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
            sb.AppendLine($"Georef      : {GeorefLine(s, schema)}");
            sb.AppendLine($"Elements    : {s.ElementCount:N0}");
            sb.AppendLine($"Triangles   : {s.TriangleCount:N0}");
            // Anything that did not reach the IFC is stated explicitly — elements used to vanish
            // with no trace anywhere in the UI or the report.
            var issues = BIMCamel.Geometry.ExportIssues.Report();
            if (issues.Length > 0) { sb.AppendLine("Not exported:"); sb.Append(issues); }
            sb.AppendLine(s.Instanced
                ? $"Instancing  : ON · {s.UniqueGeometries:N0} unique / {s.InstanceCount:N0} instances" + (s.UniqueGeometries > 0 ? $"  (×{(double)s.InstanceCount / s.UniqueGeometries:0.0})" : "")
                : "Instancing  : off");
            sb.AppendLine($"Class rules : {ruleCount} set rule(s)");
            sb.Append(MappingLines(s));
            if (s.Revision != null) sb.Append(s.Revision.Report());
            sb.AppendLine($"Storeys     : {s.StoreyCount}");
            sb.AppendLine($"Type objects: {s.TypeCount}   Materials: {s.MaterialCount}   Classifications: {s.ClassificationCount}");
            if (s.GroupCount > 0) sb.AppendLine($"Groups      : {s.GroupCount} (from Navisworks sets)");
            sb.AppendLine($"Property sets: {s.PsetUnique:N0} unique / {s.PsetRefs:N0} refs" + (s.PsetUnique > 0 ? $"  (×{(double)s.PsetRefs / s.PsetUnique:0.0} shared)" : ""));
            sb.AppendLine($"Quantities  : {(s.QuantitiesWritten ? "computed (volume/area/length/width/height)" : "none")}");
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
            Validate = ChkValidate.IsChecked == true, Quantities = ChkQuantities.IsChecked == true, Mapping = GridToText(),
            Crs = (TxtCrs.Text ?? "").Trim(), SurveyE = ParseD(TxtSurveyE.Text), SurveyN = ParseD(TxtSurveyN.Text), SurveyElev = ParseD(TxtSurveyElev.Text),
            Roles = RolesToText(), ParamRules = ParamRulesToText(),
            ProjectName = TxtProject.Text ?? "", SiteName = TxtSite.Text ?? "", BuildingName = TxtBuilding.Text ?? "", StoreyName = TxtStorey.Text ?? "",
            ClassificationSystem = (TxtClassSystem.Text ?? "").Trim(),
            Split = ChkSplit.IsChecked == true, SplitMb = TxtSplitMb.Text ?? "200",
            Groups = CmbGroups.SelectedIndex
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
            ChkInstancing.IsChecked = p.Instancing; ChkValidate.IsChecked = p.Validate; ChkQuantities.IsChecked = p.Quantities;
            TxtCrs.Text = p.Crs ?? ""; TxtSurveyE.Text = Inv(p.SurveyE); TxtSurveyN.Text = Inv(p.SurveyN); TxtSurveyElev.Text = Inv(p.SurveyElev);
            if (!string.IsNullOrWhiteSpace(p.ProjectName)) TxtProject.Text = p.ProjectName;
            if (!string.IsNullOrWhiteSpace(p.SiteName)) TxtSite.Text = p.SiteName;
            if (!string.IsNullOrWhiteSpace(p.BuildingName)) TxtBuilding.Text = p.BuildingName;
            if (!string.IsNullOrWhiteSpace(p.StoreyName)) TxtStorey.Text = p.StoreyName;
            TxtClassSystem.Text = p.ClassificationSystem ?? "";
            ChkSplit.IsChecked = p.Split; if (!string.IsNullOrWhiteSpace(p.SplitMb)) TxtSplitMb.Text = p.SplitMb;
            CmbGroups.SelectedIndex = Clamp(p.Groups, CmbGroups.Items.Count);
            TextToRoles(p.Roles); TextToParamRules(p.ParamRules);
            TextToGrid(p.Mapping);
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
            Line(_catType, _parType); Line(_catLevel, _parLevel); Line(_catMat, _parMat); Line(_catCls, _parCls);
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
            Set(0, _catType, _parType); Set(1, _catLevel, _parLevel); Set(2, _catMat, _parMat); Set(3, _catCls, _parCls);
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
