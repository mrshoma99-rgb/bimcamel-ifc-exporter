using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using BIMCamel.Collect;
using BIMCamel.Data;
using BIMCamel.Geometry;
using BIMCamel.Ifc;
using BIMCamel.Profiles;
using ET = BIMCamel.Geometry.ExportTiming;
using NavApp = Autodesk.Navisworks.Api.Application;
using Color = System.Drawing.Color;
using Application = System.Windows.Forms.Application;
using Cursor = System.Windows.Forms.Cursor;

namespace BIMCamel.UI
{
    /// <summary>
    /// BIMCamel IFC Exporter dock pane. Tabs: Export / Properties / Semantics / Mapping / Structure.
    /// All inputs are visual; properties are category-qualified to disambiguate same-named params.
    /// </summary>
    [Plugin("BIMCamel.ExportDockPane", "BIMCamel", DisplayName = "BIMCamel IFC Exporter")]
    [DockPanePlugin(470, 640, FixedSize = false)]
    public class ExportDockPane : DockPanePlugin
    {
        // Export tab
        private ComboBox _cmbSchema = null!, _cmbUnits = null!, _cmbScope = null!, _cmbSavedSet = null!, _cmbQuality = null!, _cmbBasePoint = null!;
        private TextBox _txtE = null!, _txtN = null!, _txtElev = null!, _txtRot = null!;
        private Label _lblPreview = null!, _lblScopeHint = null!;
        private CheckBox _chkGeoref = null!, _chkInstancing = null!, _chkValidate = null!, _chkProfile = null!;
        private CheckBox _chkSplit = null!;       // size-split into multiple files
        private TextBox _txtSplitMb = null!;      // size limit in MB
        private CheckedListBox _clbBatchSets = null!; // batch: pick multiple sets → one IFC each
        private TextBox _txtReport = null!;
        private List<SelectionSet> _savedSets = new List<SelectionSet>();
        private List<SelectionSet> _batchSets = new List<SelectionSet>();
        // Properties tab (merged with parameter mapping)
        private CheckBox _chkProps = null!, _chkMaterials = null!, _chkQuantities = null!;
        private CheckedListBox _clbCategories = null!;
        private DataGridView _gridParams = null!;
        private DataGridViewComboBoxColumn _colParamCat = null!, _colParamName = null!;
        // Semantics tab (category-qualified roles)
        private ComboBox _catType = null!, _parType = null!, _catLevel = null!, _parLevel = null!,
                         _catMat = null!, _parMat = null!, _catCls = null!, _parCls = null!;
        private Dictionary<string, List<string>> _catParams = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Mapping tab (set → class)
        private DataGridView _grid = null!;
        private DataGridViewComboBoxColumn _colMapSet = null!;
        private List<SelectionSet> _mapSets = new List<SelectionSet>();
        // Structure tab
        private TextBox _txtProject = null!, _txtSite = null!, _txtBuilding = null!, _txtStorey = null!;
        // Action bar
        private TabControl _tabs = null!;
        private Button _btnExport = null!;
        private ProgressBar _progress = null!;
        private Label _lblStatus = null!;
        private readonly System.Diagnostics.Stopwatch _tickClock = new System.Diagnostics.Stopwatch();
        private long _lastTickMs;
        private long _peakHeap; // sampled peak managed heap during export (diagnostics)

        private const string AnyCat = "(any category)";

        public override Control CreateControlPane()
        {
            AssemblyResolver.Ensure();
            var root = new Panel { Dock = DockStyle.Fill };
            var title = new Label
            {
                Text = "  BIMCamel IFC Exporter", Dock = DockStyle.Top, Height = 34,
                Font = new Font("Segoe UI Semibold", 12f), TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(0, 112, 192), ForeColor = Color.White
            };
            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
            _tabs.TabPages.Add(BuildExportTab());
            _tabs.TabPages.Add(BuildPropertiesTab());
            _tabs.TabPages.Add(BuildSemanticsTab());
            _tabs.TabPages.Add(BuildMappingTab());
            _tabs.TabPages.Add(BuildStructureTab());

            root.Controls.Add(_tabs);
            root.Controls.Add(BuildActionBar());
            root.Controls.Add(title);
            WireCrossControls();
            return root;
        }

        public override void DestroyControlPane(Control pane) => pane?.Dispose();

        private Control BuildActionBar()
        {
            // A TableLayoutPanel lays the three rows out deterministically (button / progress / status)
            // with no Dock z-order overlap — the old Fill button sat on top of the bottom-docked
            // progress bar, so it appeared to clip under the button.
            var bar = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(10, 6, 10, 6) };
            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // button fills remaining
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 16f)); // progress
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f)); // status

            _btnExport = new Button
            {
                Text = "Export IFC", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 3),
                BackColor = Color.FromArgb(0, 112, 192), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 11f), Cursor = Cursors.Hand
            };
            _btnExport.Click += RunExport;
            _progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(0), Visible = false };
            _lblStatus = new Label { Text = "Ready.", Dock = DockStyle.Fill, Margin = new Padding(0), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft };

            tbl.Controls.Add(_btnExport, 0, 0);
            tbl.Controls.Add(_progress, 0, 1);
            tbl.Controls.Add(_lblStatus, 0, 2);
            bar.Controls.Add(tbl);
            return bar;
        }

        // ── Export tab ──────────────────────────────────────────────────────────
        private TabPage BuildExportTab()
        {
            var page = NewPage("Export");
            _cmbSchema = LabeledCombo("IFC schema", new[] { "IFC4", "IFC2x3" }, 0);
            _cmbUnits = LabeledCombo("Source units", new[] { "Auto (from model)", "Millimeters", "Centimeters", "Meters", "Feet", "Inches" }, 0);
            _cmbScope = LabeledCombo("Scope  (required)", new[] { "Whole model (visible)", "Current selection", "Active section box", "Saved set / search set", "Multiple sets → one IFC each (batch)" }, 0);
            _lblScopeHint = new Label { Text = "", Dock = DockStyle.Top, Height = 16, ForeColor = Color.FromArgb(176, 96, 0), Font = new Font("Segoe UI", 8f, FontStyle.Italic) };
            _cmbSavedSet = LabeledCombo("Saved set", new[] { "— choose 'Saved set' scope —" }, 0);
            _cmbSavedSet.Parent!.Visible = false;

            // Batch: multi-select list of sets (one IFC each), shown only for the batch scope.
            var batchHolder = new Panel { Dock = DockStyle.Top, Height = 132, Padding = new Padding(0, 2, 0, 2), Visible = false };
            var batchBtns = WrapRow();
            var btnBatchRefresh = SmallButton("Refresh sets"); btnBatchRefresh.Click += (_, _) => PopulateBatchSets();
            var btnBatchAll = SmallButton("All"); btnBatchAll.Click += (_, _) => { for (int i = 0; i < _clbBatchSets.Items.Count; i++) _clbBatchSets.SetItemChecked(i, true); };
            var btnBatchNone = SmallButton("None"); btnBatchNone.Click += (_, _) => { for (int i = 0; i < _clbBatchSets.Items.Count; i++) _clbBatchSets.SetItemChecked(i, false); };
            batchBtns.Controls.AddRange(new Control[] { btnBatchRefresh, btnBatchAll, btnBatchNone });
            _clbBatchSets = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, Font = new Font("Segoe UI", 8.75f), IntegralHeight = false };
            var batchLbl = new Label { Text = "Sets to export (one IFC each):", Dock = DockStyle.Top, Height = 16, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            batchHolder.Controls.Add(_clbBatchSets); batchHolder.Controls.Add(batchBtns); batchHolder.Controls.Add(batchLbl);

            // Size split (applies to single or batch export).
            var splitRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, WrapContents = false, AutoSize = false };
            _chkSplit = new CheckBox { Text = "Split when larger than", AutoSize = true, Margin = new Padding(0, 5, 4, 0), Font = new Font("Segoe UI", 9f) };
            _txtSplitMb = new TextBox { Text = "200", Width = 55, Margin = new Padding(0, 2, 0, 0) };
            var mbLbl = new Label { Text = "MB  (approx — files may exceed by one element)", AutoSize = true, Margin = new Padding(4, 6, 0, 0), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f) };
            splitRow.Controls.AddRange(new Control[] { _chkSplit, _txtSplitMb, mbLbl });

            _cmbQuality = LabeledCombo("Geometry quality", new[] { "Balanced", "Small file", "High detail" }, 0);
            _cmbBasePoint = LabeledCombo("Base point", new[] { "Geometry origin — recommended (real coords kept in georeferencing)", "Model origin (no offset — keeps world coords)", "Custom base point" }, 0);

            var coordHolder = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 2, 0, 2), Visible = false };
            var coordLbl = new Label { Text = "E, N, Elev (m), rotation (°)", Dock = DockStyle.Top, Height = 15, Font = new Font("Segoe UI", 8f) };
            var fields = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, WrapContents = true, AutoSize = false };
            _txtE = SmallBox(); _txtN = SmallBox(); _txtElev = SmallBox(); _txtRot = SmallBox();
            fields.Controls.AddRange(new Control[] { _txtE, _txtN, _txtElev, _txtRot });
            coordHolder.Controls.Add(fields); coordHolder.Controls.Add(coordLbl);
            _cmbBasePoint.SelectedIndexChanged += (_, _) => coordHolder.Visible = _cmbBasePoint.SelectedIndex == 2;

            _lblPreview = new Label { Text = "Base point preview: —", Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Font = new Font("Consolas", 8.25f) };
            var btnPreview = Linkish("Preview base point"); btnPreview.Click += (_, _) => PreviewBasePoint();

            _chkGeoref = Check("Write IFC4 georeferencing (IfcMapConversion)", true);
            _chkInstancing = Check("Optimize repeated geometry (instancing)", true);
            _chkValidate = Check("Validate output after export", false);
            _chkProfile = Check("Profile output size (slower)", true);

            var profileRow = WrapRow();
            var btnSave = SmallButton("Save profile…"); btnSave.Click += SaveProfile;
            var btnLoad = SmallButton("Load profile…"); btnLoad.Click += LoadProfile;
            profileRow.Controls.Add(btnSave); profileRow.Controls.Add(btnLoad);

            _txtReport = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Consolas", 8.5f), Text = "Configure and click Export IFC." };
            var btnCopy = Linkish("Copy report"); btnCopy.Click += (_, _) => { if (!string.IsNullOrEmpty(_txtReport.Text)) Clipboard.SetText(_txtReport.Text); };

            page.Controls.Add(_txtReport);
            page.Controls.Add(btnCopy);
            page.Controls.Add(SectionLabel("Report"));
            page.Controls.Add(profileRow);
            page.Controls.Add(_chkProfile);
            page.Controls.Add(_chkValidate);
            page.Controls.Add(_chkInstancing);
            page.Controls.Add(_chkGeoref);
            page.Controls.Add(splitRow);
            page.Controls.Add(btnPreview);
            page.Controls.Add(_lblPreview);
            page.Controls.Add(coordHolder);
            page.Controls.Add(_cmbBasePoint.Parent!);
            page.Controls.Add(_cmbQuality.Parent!);
            page.Controls.Add(batchHolder);
            page.Controls.Add(_cmbSavedSet.Parent!);
            page.Controls.Add(_lblScopeHint);
            page.Controls.Add(_cmbScope.Parent!);
            page.Controls.Add(_cmbUnits.Parent!);
            page.Controls.Add(_cmbSchema.Parent!);

            // Up-front advisory (added last → sits at the very top of the tab). DataTools links can't
            // be detected via the API, so we warn proactively: they make export drastically slower.
            var dtNote = new Label
            {
                Text = "⚠ Before exporting: deactivate any DataTools / external-database links (Home → DataTools).\r\n" +
                       "They run a database query per object — drastically slowing export — and flood the console\r\n" +
                       "with 'DATATOOLS_SQL_EXEC … Sheet1$' errors if broken. The console error names the bad link.",
                Dock = DockStyle.Top, Height = 52, ForeColor = Color.FromArgb(140, 70, 0),
                BackColor = Color.FromArgb(255, 248, 225), Font = new Font("Segoe UI", 8f, FontStyle.Italic),
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 2, 6, 2)
            };
            page.Controls.Add(dtNote);
            return page;
        }

        // ── Properties tab (which Psets to export + parameter mapping) ────────────
        private TabPage BuildPropertiesTab()
        {
            var page = NewPage("Properties");
            _chkProps = Check("Export properties (property sets)", true);
            _chkMaterials = Check("Export materials / colours", true);
            _chkQuantities = Check("Compute base quantities (volume / area / length)", true);

            _clbCategories = new CheckedListBox { Dock = DockStyle.Top, Height = 150, CheckOnClick = true, Font = new Font("Segoe UI", 8.75f), IntegralHeight = false };

            var scanRow = WrapRow();
            var btnScanSel = SmallButton("Scan selection (fast)"); btnScanSel.Click += (_, _) => ScanModel(true);
            var btnScanModel = SmallButton("Scan model (sample)"); btnScanModel.Click += (_, _) => ScanModel(false);
            var btnAll = SmallButton("All"); btnAll.Click += (_, _) => SetAllCats(true);
            var btnNone = SmallButton("None"); btnNone.Click += (_, _) => SetAllCats(false);
            scanRow.Controls.AddRange(new Control[] { btnScanSel, btnScanModel, btnAll, btnNone });

            var hint = new Label { Text = "Tip: select representative elements → \"Scan selection\". Tick which property sets to export (nothing scanned = all). Scanning also feeds the Semantics roles and the mapping below.", Dock = DockStyle.Top, Height = 50, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f, FontStyle.Italic) };

            // Parameter mapping grid (rename / relocate) — category-qualified source.
            _gridParams = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = true, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
                Font = new Font("Segoe UI", 8.5f), EditMode = DataGridViewEditMode.EditOnEnter
            };
            _gridParams.DataError += (_, e) => e.ThrowException = false;
            _colParamCat = new DataGridViewComboBoxColumn { HeaderText = "Source category", FillWeight = 26, FlatStyle = FlatStyle.Flat };
            _colParamName = new DataGridViewComboBoxColumn { HeaderText = "Source property", FillWeight = 26, FlatStyle = FlatStyle.Flat };
            var colPset = new DataGridViewComboBoxColumn { HeaderText = "Target Pset", FillWeight = 26, FlatStyle = FlatStyle.Flat };
            colPset.Items.AddRange(PsetCatalog.PsetNames().Cast<object>().ToArray());
            var colName = new DataGridViewComboBoxColumn { HeaderText = "Target name (blank = keep)", FillWeight = 22, FlatStyle = FlatStyle.Flat };
            colName.Items.AddRange(PsetCatalog.AllParamNames().Cast<object>().ToArray());
            _gridParams.Columns.AddRange(_colParamCat, _colParamName, colPset, colName);
            _gridParams.EditingControlShowing += (s, ev) =>
            {
                if (!(ev.Control is ComboBox cb)) return;
                cb.DropDownStyle = ComboBoxStyle.DropDown;
                var cell = _gridParams.CurrentCell;
                // "Source property" column (1): show only the parameters of this row's chosen category.
                if (cell != null && cell.ColumnIndex == 1)
                {
                    string cat = _gridParams.Rows[cell.RowIndex].Cells[0].Value?.ToString() ?? "";
                    cb.Items.Clear();
                    IEnumerable<string> names = string.IsNullOrEmpty(cat) || cat == AnyCat
                        ? _catParams.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s2 => s2, StringComparer.OrdinalIgnoreCase)
                        : (_catParams.TryGetValue(cat, out var l) ? (IEnumerable<string>)l : new List<string>());
                    foreach (var n in names) cb.Items.Add(n);
                }
            };
            // When the category changes, clear the property cell so it can't keep a now-invalid value.
            _gridParams.CellValueChanged += (s, ev) =>
            {
                if (ev.ColumnIndex == 0 && ev.RowIndex >= 0)
                    _gridParams.Rows[ev.RowIndex].Cells[1].Value = null;
            };
            _gridParams.CurrentCellDirtyStateChanged += (s, ev) =>
            {
                if (_gridParams.IsCurrentCellDirty && _gridParams.CurrentCell is DataGridViewComboBoxCell)
                    _gridParams.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            page.Controls.Add(_gridParams);
            page.Controls.Add(SectionLabel("Parameter mapping (rename / relocate — pick category to disambiguate)"));
            page.Controls.Add(hint);
            page.Controls.Add(_clbCategories);
            page.Controls.Add(SectionLabel("Property sets to include"));
            page.Controls.Add(scanRow);
            page.Controls.Add(_chkQuantities);
            page.Controls.Add(_chkMaterials);
            page.Controls.Add(_chkProps);
            return page;
        }

        // ── Semantics tab (category-qualified roles) ──────────────────────────────
        private TabPage BuildSemanticsTab()
        {
            var page = NewPage("Semantics");
            (_catType, _parType) = RoleRow("Type → IfcElementType + ObjectType");
            (_catLevel, _parLevel) = RoleRow("Level → IfcBuildingStorey");
            (_catMat, _parMat) = RoleRow("Material → IfcMaterial");
            (_catCls, _parCls) = RoleRow("Classification → IfcClassificationReference");

            var btnRow = WrapRow();
            var btnDetect = SmallButton("Detect from model (scan + auto-fill)"); btnDetect.Click += (_, _) => ScanModel(false);
            btnRow.Controls.Add(btnDetect);
            var hint = new Label { Text = "Pick the source category, then the parameter (disambiguates same-named params). \"Detect\" scans + auto-fills; override freely. Blank = role off. Type objects & classification are written for IFC4.", Dock = DockStyle.Top, Height = 56, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f, FontStyle.Italic) };

            page.Controls.Add(_catCls.Parent!.Parent!);
            page.Controls.Add(_catMat.Parent!.Parent!);
            page.Controls.Add(_catLevel.Parent!.Parent!);
            page.Controls.Add(_catType.Parent!.Parent!);
            page.Controls.Add(SectionLabel("Map source properties to IFC roles"));
            page.Controls.Add(btnRow);
            page.Controls.Add(hint);
            return page;
        }

        // Returns (categoryCombo, parameterCombo); holders nested so .Parent.Parent = the row panel.
        private (ComboBox cat, ComboBox par) RoleRow(string label)
        {
            // Height must fit the label + two stacked combos or they clip onto each other and the row
            // below (label 18 + cat 24 + par 24 + padding ≈ 74).
            var holder = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(0, 4, 0, 6) };
            var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 18, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            var par = new ComboBox { Dock = DockStyle.Top, Height = 24, Margin = new Padding(0, 2, 0, 0), DropDownStyle = ComboBoxStyle.DropDown };
            var cat = new ComboBox { Dock = DockStyle.Top, Height = 24, DropDownStyle = ComboBoxStyle.DropDown };
            // wrap each combo in a tiny holder so the page can add via .Parent.Parent uniformly
            var inner = new Panel { Dock = DockStyle.Fill };
            inner.Controls.Add(par); inner.Controls.Add(cat);
            holder.Controls.Add(inner); holder.Controls.Add(lbl);
            cat.SelectedIndexChanged += (_, _) => PopulateParamCombo(par, cat.Text, keepText: false);
            return (cat, par);
        }

        private void PopulateParamCombo(ComboBox par, string categoryText, bool keepText)
        {
            string prev = par.Text;
            par.Items.Clear();
            par.Items.Add("");
            IEnumerable<string> names = string.IsNullOrEmpty(categoryText) || categoryText == AnyCat
                ? _catParams.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                : (_catParams.TryGetValue(categoryText, out var l) ? l : new List<string>());
            foreach (var n in names) par.Items.Add(n);
            if (keepText) par.Text = prev;
        }

        // ── Mapping tab (set → class) ─────────────────────────────────────────────
        private TabPage BuildMappingTab()
        {
            var page = NewPage("Mapping");
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = true, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
                Font = new Font("Segoe UI", 8.75f), EditMode = DataGridViewEditMode.EditOnEnter
            };
            _grid.DataError += (_, e) => e.ThrowException = false;
            _colMapSet = new DataGridViewComboBoxColumn { HeaderText = "Navisworks set", FillWeight = 40, FlatStyle = FlatStyle.Flat };
            var colClass = new DataGridViewComboBoxColumn { HeaderText = "Export as IFC class", FillWeight = 34, FlatStyle = FlatStyle.Flat };
            colClass.Items.AddRange(TypeMapping.Keys().Cast<object>().ToArray());
            var colPredef = new DataGridViewTextBoxColumn { HeaderText = "Predefined type (IFC4, optional)", FillWeight = 26 };
            _grid.Columns.AddRange(_colMapSet, colClass, colPredef);

            var btnRow = WrapRow();
            var btnRefresh = SmallButton("Refresh sets"); btnRefresh.Click += (_, _) => RefreshMapSets();
            var btnClear = SmallButton("Clear rules"); btnClear.Click += (_, _) => _grid.Rows.Clear();
            btnRow.Controls.Add(btnRefresh); btnRow.Controls.Add(btnClear);
            var hint = new Label { Text = "Assign a Navisworks selection/search set to an IFC class. Build sets in Navisworks, then \"Refresh sets\". Unmapped elements stay IfcBuildingElementProxy.", Dock = DockStyle.Top, Height = 44, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f, FontStyle.Italic) };

            page.Controls.Add(_grid);
            page.Controls.Add(SectionLabel("Map Navisworks sets → IFC classes"));
            page.Controls.Add(btnRow);
            page.Controls.Add(hint);
            return page;
        }

        private void RefreshMapSets()
        {
            var doc = NavApp.ActiveDocument;
            _mapSets = doc != null ? ItemCollector.GetSelectionSets(doc) : new List<SelectionSet>();
            _colMapSet.Items.Clear();
            foreach (var s in _mapSets) _colMapSet.Items.Add(s.DisplayName ?? "(set)");
            SetStatus(_mapSets.Count == 0 ? "No saved sets — create some in Navisworks first." : $"{_mapSets.Count} sets available for mapping.");
        }

        private List<(SelectionSet, string)> BuildSetRules()
        {
            var rules = new List<(SelectionSet, string)>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                var setName = row.Cells[0].Value?.ToString();
                var cls = row.Cells[1].Value?.ToString();
                var predef = row.Cells[2].Value?.ToString();
                if (string.IsNullOrEmpty(setName) || string.IsNullOrEmpty(cls)) continue;
                var set = _mapSets.FirstOrDefault(s => string.Equals(s.DisplayName, setName, StringComparison.Ordinal));
                if (set != null && TypeMapping.Catalog.ContainsKey(cls!)) rules.Add((set, TypeMapping.Encode(cls!, predef)));
            }
            return rules;
        }

        // ── Structure tab ─────────────────────────────────────────────────────────
        private TabPage BuildStructureTab()
        {
            var page = NewPage("Structure");
            _txtProject = LabeledText("Project name", "BIMCamel Export");
            _txtSite = LabeledText("Site name", "Site");
            _txtBuilding = LabeledText("Building name", "Building");
            _txtStorey = LabeledText("Default storey (items with no Level)", "Storey");
            var hint = new Label { Text = "Storeys are taken from the Level property you set on the Semantics tab. The name below is only the fallback storey for items without a level.", Dock = DockStyle.Top, Height = 44, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f, FontStyle.Italic) };

            page.Controls.Add(_txtStorey.Parent!);
            page.Controls.Add(_txtBuilding.Parent!);
            page.Controls.Add(_txtSite.Parent!);
            page.Controls.Add(_txtProject.Parent!);
            page.Controls.Add(hint);
            return page;
        }

        // ── unified scan (feeds psets checklist + role combos + param grid columns) ─
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
                SwitchToDeterminate(0); _progress.Style = ProgressBarStyle.Marquee; Application.DoEvents();
                _catParams = PropertyHarvester.ScanCategoryParams(items, cap);

                var cats = _catParams.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                var allParams = _catParams.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

                // psets checklist
                _clbCategories.Items.Clear();
                foreach (var c in cats) _clbCategories.Items.Add(c, true);

                // param-map grid source columns
                _colParamCat.Items.Clear(); _colParamCat.Items.Add(AnyCat); foreach (var c in cats) _colParamCat.Items.Add(c);
                _colParamName.Items.Clear(); foreach (var p in allParams) _colParamName.Items.Add(p);

                // role category combos
                foreach (var cc in new[] { _catType, _catLevel, _catMat, _catCls })
                {
                    string prev = cc.Text;
                    cc.Items.Clear(); cc.Items.Add(AnyCat); foreach (var c in cats) cc.Items.Add(c);
                    cc.Text = prev;
                }

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
            foreach (DataGridViewRow row in _gridParams.Rows)
            {
                if (row.IsNewRow) continue;
                var src = row.Cells[1].Value?.ToString()?.Trim();
                if (string.IsNullOrEmpty(src)) continue;
                var cat = row.Cells[0].Value?.ToString()?.Trim() ?? "";
                rules.Add(new ParamMapRule
                {
                    SourceCategory = cat == AnyCat ? "" : cat,
                    Source = src!,
                    TargetPset = row.Cells[2].Value?.ToString()?.Trim() ?? "",
                    TargetName = row.Cells[3].Value?.ToString()?.Trim() ?? ""
                });
            }
            return rules;
        }

        // ── Export action ───────────────────────────────────────────────────────
        private void RunExport(object? sender, EventArgs e)
        {
            try
            {
                var doc = NavApp.ActiveDocument;
                if (doc == null || doc.Models.Count == 0) { SetStatus("Open a model first."); return; }

                var schema = _cmbSchema.SelectedIndex == 1 ? IfcSchema.Ifc2x3 : IfcSchema.Ifc4;
                string schemaName = schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2x3";
                bool batch = _cmbScope.SelectedIndex == 4;
                long splitLimit = SplitLimitBytes();
                (double unitScale, string unitName) = ResolveUnits(doc);
                var coords = BuildCoords();
                var names = new SpatialNames { Project = Def(_txtProject.Text, "BIMCamel Export"), Site = Def(_txtSite.Text, "Site"), Building = Def(_txtBuilding.Text, "Building"), Storey = Def(_txtStorey.Text, "Storey") };
                var setRules = BuildSetRules();
                double weldTolMetres = _cmbQuality.SelectedIndex switch { 1 => 1e-3, 2 => 1e-6, _ => 1e-4 };
                int coordDecimals = _cmbQuality.SelectedIndex switch { 1 => 3, 2 => 6, _ => 4 }; // precision tied to weld

                if (batch) { RunBatchExport(doc, schema, schemaName, unitScale, unitName, coords, names, setRules, weldTolMetres, coordDecimals, splitLimit); return; }

                // ── single scope ──────────────────────────────────────────────────
                using var sfd = new SaveFileDialog { Title = "Export IFC", Filter = "IFC file (*.ifc)|*.ifc", FileName = $"{ModelBaseName(doc)}_{schemaName}.ifc", DefaultExt = "ifc" };
                if (sfd.ShowDialog() != DialogResult.OK) return;

                var scanSw = Stopwatch.StartNew();
                BeginBusyMarquee("Collecting elements…");
                var items = ResolveScope(doc, CollectTick);
                if (items == null) return;
                if (items.Count == 0) { SetStatus("No geometry elements in scope."); return; }

                var classMap = new Dictionary<string, string>();
                if (setRules.Count > 0) { SetStatus("Resolving mapping sets…"); Application.DoEvents(); classMap = ItemCollector.BuildClassMap(doc, setRules); }
                var opts = BuildExtractOptions(classMap);

                SetStatus("Computing model extents…"); Application.DoEvents();
                var sm = ItemCollector.ScopeMinCorner(items, n => CollectTick(n));
                var geomMin = (sm.x * unitScale, sm.y * unitScale, sm.z * unitScale);
                scanSw.Stop();

                BeginBusy(items.Count);
                BIMCamel.Geometry.ExportTiming.Reset();
                long baseHeap = GC.GetTotalMemory(false); _peakHeap = baseHeap;
                var heapTimer = new System.Threading.Timer(_ => { long m = GC.GetTotalMemory(false); if (m > _peakHeap) _peakHeap = m; }, null, 0, 200);
                var sw = Stopwatch.StartNew();

                var summary = new ExportSummary();
                RunOneExport(sfd.FileName, schema, items, unitScale, coords, names, opts, weldTolMetres, coordDecimals, geomMin, splitLimit, summary, "Exporting");

                sw.Stop(); heapTimer.Dispose();
                FinishReport(summary, schemaName, _cmbScope.Text, unitName, items.Count, scanSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, setRules.Count, baseHeap);
            }
            catch (Exception ex)
            {
                SetStatus("Export failed: " + ex.Message);
                SetReport("Export FAILED\n──────────────────────────────\n" + ex.Message);
                MessageBox.Show(ex.ToString(), "BIMCamel export — error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndBusy(); }
        }

        // Batch: each ticked set → its own IFC (or parts) in one folder; all share one base-point
        // offset (from the whole model) so the files overlay correctly when loaded together.
        private void RunBatchExport(Document doc, IfcSchema schema, string schemaName, double unitScale, string unitName,
            CoordOptions coords, SpatialNames names, List<(SelectionSet, string)> setRules, double weldTolMetres, int coordDecimals, long splitLimit)
        {
            var chosen = new List<SelectionSet>();
            for (int i = 0; i < _clbBatchSets.Items.Count && i < _batchSets.Count; i++)
                if (_clbBatchSets.GetItemChecked(i)) chosen.Add(_batchSets[i]);
            if (chosen.Count == 0) { SetStatus("Tick at least one set to export (batch scope)."); return; }

            using var fbd = new FolderBrowserDialog { Description = "Choose an output folder — one IFC per set" };
            if (fbd.ShowDialog() != DialogResult.OK) return;
            string folder = fbd.SelectedPath;

            var classMap = new Dictionary<string, string>();
            if (setRules.Count > 0) { SetStatus("Resolving mapping sets…"); Application.DoEvents(); classMap = ItemCollector.BuildClassMap(doc, setRules); }
            var opts = BuildExtractOptions(classMap);

            // Shared offset from the whole model (original coordinates), computed once for all sets.
            var scanSw = Stopwatch.StartNew();
            BeginBusyMarquee("Preparing batch…");
            var mc = ModelMinCorner(doc);
            var geomMin = (mc.Item1 * unitScale, mc.Item2 * unitScale, mc.Item3 * unitScale);
            scanSw.Stop();

            BeginBusy(1);
            BIMCamel.Geometry.ExportTiming.Reset();
            long baseHeap = GC.GetTotalMemory(false); _peakHeap = baseHeap;
            var heapTimer = new System.Threading.Timer(_ => { long m = GC.GetTotalMemory(false); if (m > _peakHeap) _peakHeap = m; }, null, 0, 200);
            var sw = Stopwatch.StartNew();

            var summary = new ExportSummary();
            int setNum = 0, totalItems = 0, skipped = 0;
            foreach (var set in chosen)
            {
                setNum++;
                SetStatus($"Set {setNum}/{chosen.Count}: {set.DisplayName} — collecting…"); Application.DoEvents();
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
            if (!_chkSplit.Checked) return 0;
            return double.TryParse(_txtSplitMb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var mb) && mb > 0
                ? (long)(mb * 1024 * 1024) : 0;
        }

        private ExtractOptions BuildExtractOptions(Dictionary<string, string> classMap) => new ExtractOptions
        {
            Props = _chkProps.Checked, Materials = _chkMaterials.Checked,
            PsetFilter = SelectedCategories(), ClassMap = classMap,
            ParamMap = BuildParamRules(), Roles = BuildRoles()
        };

        // Extract + write one scope into basePath (single file, or "_NNN" parts when splitting),
        // appending results to the shared summary.
        private void RunOneExport(string basePath, IfcSchema schema, List<ModelItem> items, double unitScale, CoordOptions coords, SpatialNames names,
            ExtractOptions opts, double weldTolMetres, int coordDecimals, (double x, double y, double z) geomMin, long splitLimit, ExportSummary summary, string verb)
        {
            SwitchToDeterminate(items.Count);
            if (_chkInstancing.Checked)
            {
                opts.WeldTol = weldTolMetres;
                var stream = InstancedExtractor.ExtractStream(items, unitScale, opts, p => Tick(p, items.Count, verb));
                IfcExporter.ExportInstanced(basePath, schema, stream, Environment.UserName, unitScale, coords, _chkQuantities.Checked, coordDecimals, geomMin, names, splitLimit, summary);
            }
            else
            {
                opts.WeldTol = weldTolMetres / unitScale;
                var stream = MeshExtractor.ExtractStream(items, opts, p => Tick(p, items.Count, verb));
                IfcExporter.Export(basePath, schema, stream, Environment.UserName, unitScale, coords, _chkQuantities.Checked, coordDecimals, geomMin, names, splitLimit, summary);
            }
        }

        private void FinishReport(ExportSummary summary, string schemaName, string scopeLabel, string unitName, int scopeItemCount, long scanMs, long exportMs, int ruleCount, long baseHeap)
        {
            string validation = "not run";
            if (_chkValidate.Checked && summary.Files.Count > 0)
            {
                SetStatus("Validating…"); Application.DoEvents();
                var issues = new List<string>();
                foreach (var f in summary.Files) { var iss = IfcValidator.Validate(f); if (iss.Count > 0) issues.Add(System.IO.Path.GetFileName(f) + ": " + string.Join("; ", iss)); }
                validation = issues.Count == 0 ? $"passed ({summary.Files.Count} file(s))" : "ISSUES — " + string.Join(" | ", issues);
            }
            string profile = "";
            if (_chkProfile.Checked && summary.Files.Count > 0)
            {
                SetStatus("Profiling output size…"); Application.DoEvents();
                try { profile = (summary.Files.Count > 1 ? $"(profile of first of {summary.Files.Count} files)\n" : "") + IfcProfiler.Profile(summary.Files[0]); }
                catch (Exception px) { profile = "profile failed: " + px.Message; }
            }
            SetReport(BuildReport(summary, schemaName, scopeLabel, unitName, scopeItemCount, scanMs, exportMs, ruleCount, validation, profile, baseHeap, _peakHeap));
            SetStatus($"Done · {summary.FileCount} file(s) · {summary.ElementCount:N0} elements · {summary.FileSizeBytes / 1024.0:N0} KB · {exportMs:N0} ms");

            string? openTarget = summary.Files.Count == 1 ? summary.Files[0] : (summary.Files.Count > 0 ? System.IO.Path.GetDirectoryName(summary.Files[0]) : null);
            if (openTarget != null && MessageBox.Show($"Exported {summary.FileCount} file(s) · {summary.ElementCount:N0} elements ({summary.FileSizeBytes / 1024.0:N0} KB).\n\nOpen now?",
                    "BIMCamel export complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(openTarget) { UseShellExecute = true }); }
                catch { try { Process.Start("explorer.exe", $"\"{openTarget}\""); } catch { } }
            }
        }

        private static string Sanitize(string name)
        {
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            name = name.Trim();
            return name.Length == 0 ? "set" : name;
        }

        private List<ModelItem>? ResolveScope(Document doc, Action<int>? onProgress = null)
        {
            switch (_cmbScope.SelectedIndex)
            {
                case 1:
                    var sel = doc.CurrentSelection.SelectedItems;
                    if (sel == null || sel.Count == 0) { SetStatus("Nothing selected. Select elements or choose another scope."); return null; }
                    return ItemCollector.ResolveLeaves(sel, onProgress);
                case 2:
                    try { return ItemCollector.GetItemsInSectionBox(doc, onProgress); }
                    catch (Exception sx) { SetStatus(sx.Message); MessageBox.Show(sx.Message, "BIMCamel", MessageBoxButtons.OK, MessageBoxIcon.Information); return null; }
                case 3:
                    int si = _cmbSavedSet.SelectedIndex;
                    if (_savedSets.Count == 0 || si < 0 || si >= _savedSets.Count) { SetStatus("Pick a saved set (Scope = Saved set)."); return null; }
                    return ItemCollector.GetItemsFromSet(doc, _savedSets[si], onProgress);
                default:
                    return ItemCollector.GetVisibleLeafItemsWithGeometry(doc, onProgress);
            }
        }

        private CoordOptions BuildCoords() => new CoordOptions
        {
            Mode = _cmbBasePoint.SelectedIndex switch { 1 => BasePointMode.ModelOrigin, 2 => BasePointMode.Custom, _ => BasePointMode.GeometryOrigin },
            WriteGeoref = _chkGeoref.Checked,
            CustomEastings = ParseD(_txtE.Text), CustomNorthings = ParseD(_txtN.Text), CustomElevation = ParseD(_txtElev.Text), RotationDeg = ParseD(_txtRot.Text)
        };

        private HashSet<string>? SelectedCategories()
        {
            if (!_chkProps.Checked) return null;
            if (_clbCategories.Items.Count == 0) return null; // not scanned → all
            return new HashSet<string>(_clbCategories.CheckedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase);
        }

        // ── progress feedback ─────────────────────────────────────────────────────
        private void BeginBusy(int max)
        {
            _btnExport.Enabled = false; _tabs.Enabled = false;
            _progress.Visible = true; _progress.Style = ProgressBarStyle.Continuous;
            _progress.Minimum = 0; _progress.Maximum = Math.Max(1, max); _progress.Value = 0;
            _tickClock.Restart(); _lastTickMs = 0; Cursor.Current = Cursors.WaitCursor;
        }
        private void Tick(int done, int max, string verb)
        {
            long ms = _tickClock.ElapsedMilliseconds;
            // 500 ms (2×/s) is plenty to stay responsive. DoEvents pumps the Navisworks message loop
            // and is surprisingly expensive (~tens of ms each); at the old 80 ms cadence it dominated
            // long exports. Measure it so the report shows the cost (v4).
            if (done != max && ms - _lastTickMs < 500) return;
            _lastTickMs = ms;
            if (done <= _progress.Maximum) _progress.Value = done;
            _lblStatus.Text = $"{verb} {done}/{max}…";
            long t = ET.Now; Application.DoEvents(); ET.UiTicks += ET.Now - t; ET.UiPumps++;
        }
        // Progress during the model-tree walk (count unknown up front → marquee + "scanned" count).
        // Pumps the message loop so a large-model collection does not look like a freeze.
        private void CollectTick(int visited)
        {
            long ms = _tickClock.ElapsedMilliseconds;
            if (ms - _lastTickMs < 120) return;
            _lastTickMs = ms;
            _lblStatus.Text = $"Scanning model… {visited:N0} items"; Application.DoEvents();
        }
        private void BeginBusyMarquee(string status)
        {
            _btnExport.Enabled = false; _tabs.Enabled = false;
            _progress.Visible = true; _progress.Style = ProgressBarStyle.Marquee;
            _tickClock.Restart(); _lastTickMs = 0;
            Cursor.Current = Cursors.WaitCursor; SetStatus(status); Application.DoEvents();
        }
        private void SwitchToDeterminate(int max)
        {
            _progress.Style = ProgressBarStyle.Continuous; _progress.Minimum = 0; _progress.Maximum = Math.Max(1, max); _progress.Value = 0;
            _tickClock.Restart(); _lastTickMs = 0;
        }
        private void Writing(string schemaName) { _progress.Style = ProgressBarStyle.Marquee; SetStatus($"Writing {schemaName} file…"); Application.DoEvents(); }
        private void EndBusy() { Cursor.Current = Cursors.Default; _progress.Visible = false; _progress.Style = ProgressBarStyle.Continuous; _btnExport.Enabled = true; _tabs.Enabled = true; }

        // ── base point preview ────────────────────────────────────────────────────
        private void PreviewBasePoint()
        {
            var doc = NavApp.ActiveDocument;
            if (doc == null || doc.Models.Count == 0) { _lblPreview.Text = "Base point preview: open a model"; return; }
            (double scale, _) = ResolveUnits(doc);
            double x, y, z;
            switch (_cmbBasePoint.SelectedIndex)
            {
                case 1: x = y = z = 0; break;
                case 2: x = ParseD(_txtE.Text); y = ParseD(_txtN.Text); z = ParseD(_txtElev.Text); break;
                default: var bb = ModelMinCorner(doc); x = bb.Item1 * scale; y = bb.Item2 * scale; z = bb.Item3 * scale; break;
            }
            _lblPreview.Text = $"Base point preview: [{x:N3}, {y:N3}, {z:N3}] m";
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

        // ── report ─────────────────────────────────────────────────────────────────
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
            // Where the export time went (COM-convert vs per-triangle read tells us S2 vs S3 payoff).
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

            // Safeguard: slow per-element harvest almost always means an active DataTools / external
            // database link — every object triggers a DB query (and floods the console with
            // DATATOOLS_SQL_EXEC errors if broken). Normal in-memory harvest is well under 1 ms/element.
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
                sb.AppendLine("   then deactivate/delete that link and re-export. (Removing a broken link");
                sb.AppendLine("   here cut harvest from ~18 min to ~30 s.)");
            }

            if (!string.IsNullOrEmpty(profile)) { sb.AppendLine("──────────────────────────────"); sb.Append(profile); }
            return sb.ToString();
        }

        // ── profiles (export-tab settings + set→class mapping) ─────────────────────
        private void SaveProfile(object? sender, EventArgs e)
        {
            try
            {
                using var sfd = new SaveFileDialog { Title = "Save BIMCamel profile", Filter = "BIMCamel profile (*.json)|*.json", FileName = "bimcamel_profile.json", DefaultExt = "json" };
                if (sfd.ShowDialog() != DialogResult.OK) return;
                ExportProfile.Save(Capture(), sfd.FileName); SetStatus("Profile saved.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "BIMCamel", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void LoadProfile(object? sender, EventArgs e)
        {
            try
            {
                using var ofd = new OpenFileDialog { Title = "Load BIMCamel profile", Filter = "BIMCamel profile (*.json)|*.json" };
                if (ofd.ShowDialog() != DialogResult.OK) return;
                Apply(ExportProfile.Load(ofd.FileName)); SetStatus("Profile loaded.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "BIMCamel", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private ExportProfile Capture() => new ExportProfile
        {
            Schema = _cmbSchema.SelectedIndex, Units = _cmbUnits.SelectedIndex, Scope = _cmbScope.SelectedIndex,
            Quality = _cmbQuality.SelectedIndex, BasePoint = _cmbBasePoint.SelectedIndex,
            CustomE = ParseD(_txtE.Text), CustomN = ParseD(_txtN.Text), CustomElev = ParseD(_txtElev.Text), Rotation = ParseD(_txtRot.Text),
            Georef = _chkGeoref.Checked, Props = _chkProps.Checked, Materials = _chkMaterials.Checked, Instancing = _chkInstancing.Checked,
            Validate = _chkValidate.Checked, Mapping = GridToText()
        };
        private void Apply(ExportProfile p)
        {
            _cmbSchema.SelectedIndex = Clamp(p.Schema, _cmbSchema.Items.Count);
            _cmbUnits.SelectedIndex = Clamp(p.Units, _cmbUnits.Items.Count);
            _cmbScope.SelectedIndex = Clamp(p.Scope, _cmbScope.Items.Count);
            _cmbQuality.SelectedIndex = Clamp(p.Quality, _cmbQuality.Items.Count);
            _cmbBasePoint.SelectedIndex = Clamp(p.BasePoint, _cmbBasePoint.Items.Count);
            _txtE.Text = Inv(p.CustomE); _txtN.Text = Inv(p.CustomN); _txtElev.Text = Inv(p.CustomElev); _txtRot.Text = Inv(p.Rotation);
            _chkGeoref.Checked = p.Georef; _chkProps.Checked = p.Props; _chkMaterials.Checked = p.Materials;
            _chkInstancing.Checked = p.Instancing; _chkValidate.Checked = p.Validate;
            TextToGrid(p.Mapping);
        }
        private string GridToText()
        {
            var sb = new StringBuilder();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                var t = row.Cells[0].Value?.ToString()?.Trim(); var c = row.Cells[1].Value?.ToString()?.Trim();
                var pd = row.Cells[2].Value?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(c))
                    sb.AppendLine(string.IsNullOrEmpty(pd) ? $"{t} => {c}" : $"{t} => {c} :: {pd}");
            }
            return sb.ToString();
        }
        private void TextToGrid(string? text)
        {
            _grid.Rows.Clear();
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var line in text!.Replace("\r", "").Split('\n'))
            {
                var l = line.Trim(); int a = l.IndexOf("=>", StringComparison.Ordinal);
                if (a < 0) continue;
                var setName = l.Substring(0, a).Trim(); var rhs = l.Substring(a + 2).Trim();
                string predef = ""; int pp = rhs.IndexOf("::", StringComparison.Ordinal);
                if (pp >= 0) { predef = rhs.Substring(pp + 2).Trim(); rhs = rhs.Substring(0, pp).Trim(); }
                if (setName.Length == 0 || !TypeMapping.Catalog.ContainsKey(rhs)) continue;
                if (!_colMapSet.Items.Contains(setName)) _colMapSet.Items.Add(setName);
                _grid.Rows.Add(setName, rhs, predef);
            }
        }

        // ── cross-control wiring ───────────────────────────────────────────────────
        private void WireCrossControls()
        {
            // Materials now work with instancing (colour is shared on each IfcRepresentationMap), so
            // the checkbox is always enabled.
            void SyncProps() { _clbCategories.Enabled = _chkProps.Checked; }
            _chkProps.CheckedChanged += (_, _) => SyncProps(); SyncProps();
            _cmbScope.SelectedIndexChanged += (_, _) => UpdateScopeHint(); UpdateScopeHint();
            _cmbBasePoint.SelectedIndexChanged += (_, _) => PreviewBasePoint();
            _txtE.TextChanged += (_, _) => PreviewBasePoint(); _txtN.TextChanged += (_, _) => PreviewBasePoint(); _txtElev.TextChanged += (_, _) => PreviewBasePoint();
        }
        private void UpdateScopeHint()
        {
            bool batch = _cmbScope.SelectedIndex == 4;
            _cmbSavedSet.Parent!.Visible = _cmbScope.SelectedIndex == 3;
            _clbBatchSets.Parent!.Visible = batch;
            switch (_cmbScope.SelectedIndex)
            {
                case 1: _lblScopeHint.Text = "→ select items in Navisworks first"; break;
                case 2: _lblScopeHint.Text = "→ enable a section box in Navisworks first"; break;
                case 3: _lblScopeHint.Text = "→ pick a saved set below"; PopulateSets(); break;
                case 4: _lblScopeHint.Text = "→ tick sets below; each becomes its own IFC (choose an output folder)"; PopulateBatchSets(); break;
                default: _lblScopeHint.Text = ""; break;
            }
        }
        private void PopulateBatchSets()
        {
            var doc = NavApp.ActiveDocument;
            _batchSets = doc != null ? ItemCollector.GetSelectionSets(doc) : new List<SelectionSet>();
            _clbBatchSets.Items.Clear();
            foreach (var s in _batchSets) _clbBatchSets.Items.Add(s.DisplayName ?? "(set)", false);
            SetStatus(_batchSets.Count == 0 ? "No saved/search sets — create some in Navisworks first." : $"{_batchSets.Count} sets available — tick the ones to export.");
        }
        private void PopulateSets()
        {
            var doc = NavApp.ActiveDocument;
            _savedSets = doc != null ? ItemCollector.GetSelectionSets(doc) : new List<SelectionSet>();
            _cmbSavedSet.Items.Clear();
            if (_savedSets.Count == 0) _cmbSavedSet.Items.Add("(no saved sets in document)");
            else foreach (var s in _savedSets) _cmbSavedSet.Items.Add(s.DisplayName ?? "(set)");
            _cmbSavedSet.SelectedIndex = 0;
        }

        private void RunSpike(object? sender, EventArgs e) { } // (diagnostics spike removed from UI; kept for future)
        private void SetAllCats(bool on) { for (int i = 0; i < _clbCategories.Items.Count; i++) _clbCategories.SetItemChecked(i, on); }

        // ── small builders ──────────────────────────────────────────────────────────
        private static TabPage NewPage(string title) => new TabPage(title) { Padding = new Padding(12), BackColor = Color.White };
        private static Label SectionLabel(string t) => new Label { Text = t, Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI Semibold", 9f) };
        private static CheckBox Check(string t, bool c) => new CheckBox { Text = t, Checked = c, Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 9f) };
        private static Button SmallButton(string t) => new Button { Text = t, AutoSize = true, FlatStyle = FlatStyle.System, Margin = new Padding(0, 0, 6, 4) };
        private static LinkLabel Linkish(string t) => new LinkLabel { Text = t, Dock = DockStyle.Top, Height = 18, Font = new Font("Segoe UI", 8.5f) };
        private static TextBox SmallBox() => new TextBox { Width = 80, Text = "0", Margin = new Padding(0, 0, 6, 0) };
        private static FlowLayoutPanel WrapRow() => new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = true, AutoSize = false, FlowDirection = FlowDirection.LeftToRight };

        private static ComboBox LabeledCombo(string label, string[] items, int def)
        {
            var holder = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 3, 0, 4) };
            var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 17, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            var cmb = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            cmb.Items.AddRange(items); cmb.SelectedIndex = def;
            holder.Controls.Add(cmb); holder.Controls.Add(lbl);
            return cmb;
        }
        private static TextBox LabeledText(string label, string val)
        {
            var holder = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 3, 0, 4) };
            var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 17, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            var txt = new TextBox { Dock = DockStyle.Top, Text = val };
            holder.Controls.Add(txt); holder.Controls.Add(lbl);
            return txt;
        }

        private (double scale, string name) ResolveUnits(Document doc) => _cmbUnits.SelectedIndex switch
        {
            1 => (0.001, "mm (forced)"), 2 => (0.01, "cm (forced)"), 3 => (1.0, "m (forced)"),
            4 => (0.3048, "ft (forced)"), 5 => (0.0254, "in (forced)"), _ => UnitsToMetre(doc.Units)
        };
        private void SetStatus(string text) => _lblStatus.Text = text;
        private void SetReport(string text) => _txtReport.Text = text.Replace("\n", Environment.NewLine);
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
