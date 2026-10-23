using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BIMCamel.Data;
using BIMCamel.Profiles;
using WF = System.Windows.Forms;

namespace BIMCamel.UI
{
    /// <summary>
    /// The pane's memory: settings survive a Navisworks restart, profiles say which one is loaded
    /// and whether it has been changed since, and one menu offers presets and a reset.
    /// </summary>
    public partial class ExporterView
    {
        /// <summary>The pane as a fresh install shows it, captured before anything is restored —
        /// the target of "Reset all settings".</summary>
        private ExportProfile _defaults = null!;
        private UiPrefs _ui = new UiPrefs();
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        /// <summary>Set while settings are being applied in bulk, so the hundred change events a
        /// profile load raises do not each schedule a save.</summary>
        private bool _applying;
        /// <summary>The loaded profile as JSON, to tell whether the pane still matches it.</summary>
        private string _profileJson = "";

        private void InitSession()
        {
            _defaults = Capture();
            _ui = SettingsStore.LoadUi();

            var last = SettingsStore.LoadLast();
            if (last != null) ApplyQuietly(last);
            if (!string.IsNullOrEmpty(_ui.ProfileName) && last != null) _profileJson = ExportProfile.ToJson(last);

            if (_ui.Dark) ApplyTheme(true);
            if (_ui.WarningDismissed) WarnBanner.Visibility = Visibility.Collapsed;
            TglAdvanced.IsChecked = _ui.AdvancedOpen;
            UpdateProfileLabel();
            UpdateExportLabel();

            // Every edit anywhere in the pane bubbles one of these. Listening at the root means a
            // control added later is remembered without anyone having to wire it up.
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleSave()));
            AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleSave()));
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => ScheduleSave()));
            AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleSave()));
            AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => ScheduleSave()));
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSession(); };
            // Navisworks does not always unload a docked pane on exit, so the debounced save above
            // is the real guarantee; this only catches the last second before a pane is closed.
            Unloaded += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveSession(); } };
        }

        private void ScheduleSave()
        {
            if (_applying || _defaults == null) return;
            UpdateExportLabel(); UpdateNamePreview();
            _saveTimer.Stop(); _saveTimer.Start();
        }

        private void SaveSession()
        {
            SettingsStore.SaveLast(Capture());
            _ui.Dark = _dark;
            _ui.WarningDismissed = WarnBanner.Visibility != Visibility.Visible;
            _ui.AdvancedOpen = TglAdvanced.IsChecked == true;
            SettingsStore.SaveUi(_ui);
            UpdateProfileLabel();
        }

        private void ApplyQuietly(ExportProfile p)
        {
            _applying = true;
            try { Apply(p); }
            finally { _applying = false; }
        }

        private static readonly string VersionText = PluginVersion();

        private static string PluginVersion()
        {
            var v = typeof(ExporterView).Assembly.GetName().Version;
            return v == null ? "" : "v" + v.Major + "." + v.Minor + "." + Math.Max(v.Build, 0);
        }

        // "IFC Exporter v0.12.0 · Profile •": the version is dimmed so the name still leads.
        private void UpdateProfileLabel()
        {
            string profile = "";
            if (!string.IsNullOrEmpty(_ui.ProfileName))
            {
                bool changed = _profileJson.Length > 0 && ExportProfile.ToJson(Capture()) != _profileJson;
                profile = " · " + _ui.ProfileName + (changed ? "  •" : "");
            }
            LblSubtitle.Inlines.Clear();
            LblSubtitle.Inlines.Add(new System.Windows.Documents.Run("IFC Exporter"));
            if (VersionText.Length > 0)
                LblSubtitle.Inlines.Add(new System.Windows.Documents.Run(" " + VersionText)
                    { Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), FontSize = 11 });
            if (profile.Length > 0) LblSubtitle.Inlines.Add(new System.Windows.Documents.Run(profile));
        }

        private void LoadedProfile(string name, ExportProfile p)
        {
            ApplyQuietly(p);
            _ui.ProfileName = name;
            // Compared against what the pane holds AFTER applying, not the file: a profile written
            // by an older version fills new fields with defaults, and that is not a user's change.
            _profileJson = ExportProfile.ToJson(Capture());
            AfterBulkChange();
        }

        private void AfterBulkChange()
        {
            UpdateScopeHint(); PreviewBasePoint(); UpdateAdvancedSummary(); RenderPreflight(); UpdateExportLabel();
            SaveSession();
        }

        // ── the profile menu ─────────────────────────────────────────────────────
        private void OnProfileMenu(object sender, RoutedEventArgs e)
        {
            var menu = NewPaneMenu(BtnLoadProfile);
            void Item(string header, string tip, Action act)
            {
                var mi = new MenuItem { Header = header, ToolTip = tip };
                mi.Click += (_, _) => act();
                menu.Items.Add(mi);
            }
            Item("Load profile from file…", "Replace every setting with a saved .json profile.", LoadProfileFromFile);
            menu.Items.Add(NewPaneMenuSeparator());
            Item("Preset: Coordination model", "A light IFC4 for clash and review: instancing, compression, properties from the parent object, no quantities. Mapping, roles, names and coordinates are kept.", () => ApplyPreset(Preset.Coordination));
            Item("Preset: Smallest file", "Everything that shrinks the file: coarser welding, shared type properties, tiny parts left out, no quantities. Mapping, roles, names and coordinates are kept.", () => ApplyPreset(Preset.Small));
            Item("Preset: Full data hand-off", "Everything the model carries: properties from all parent levels, materials, quantities, blank values kept. Mapping, roles, names and coordinates are kept.", () => ApplyPreset(Preset.FullData));
            menu.Items.Add(NewPaneMenuSeparator());
            Item("Reset all settings to defaults…", "Put every tab back the way a fresh install shows it, including mapping rules, roles and coordinates.", ResetAll);
            menu.IsOpen = true;
        }

        private void LoadProfileFromFile()
        {
            try
            {
                using var ofd = new WF.OpenFileDialog { Title = "Load BIMCamel profile", Filter = "BIMCamel profile (*.json)|*.json" };
                if (ofd.ShowDialog() != WF.DialogResult.OK) return;
                LoadedProfile(System.IO.Path.GetFileNameWithoutExtension(ofd.FileName), ExportProfile.Load(ofd.FileName));
                SetStatus("Profile loaded.");
            }
            catch (Exception ex) { SetStatus("Could not load the profile: " + ex.Message); }
        }

        private enum Preset { Coordination, Small, FullData }

        /// <summary>
        /// A preset sets only HOW the file is written — schema, quality, size and data switches.
        /// What the file SAYS (mapping rules, roles, names, coordinates) is project work, and a
        /// preset that wiped it would be a reset under a friendlier name.
        /// </summary>
        private void ApplyPreset(Preset preset)
        {
            _applying = true;
            try
            {
                CmbSchema.SelectedIndex = 0;          // IFC4
                ChkZip.IsChecked = true;
                ApplyAdvancedDefaults();
                ChkProps.IsChecked = true; ChkMaterials.IsChecked = true;
                switch (preset)
                {
                    case Preset.Coordination:
                        CmbQuality.SelectedIndex = 0;
                        CmbPropSource.SelectedIndex = (int)PropSource.ParentObject;
                        ChkQuantities.IsChecked = false;
                        break;
                    case Preset.Small:
                        CmbQuality.SelectedIndex = 1;
                        CmbPropSource.SelectedIndex = (int)PropSource.ParentObject;
                        ChkQuantities.IsChecked = false;
                        ChkShareTypeProps.IsChecked = true;
                        ChkMinSize.IsChecked = true; TxtMinSizeMm.Text = "5";
                        ChkProfile.IsChecked = false;
                        break;
                    case Preset.FullData:
                        CmbQuality.SelectedIndex = 0;
                        CmbPropSource.SelectedIndex = (int)PropSource.AllParents;
                        ChkQuantities.IsChecked = true;
                        ChkSkipEmptyProps.IsChecked = false;
                        break;
                }
            }
            finally { _applying = false; }
            AfterBulkChange();
            SetStatus(preset switch
            {
                Preset.Small => "Preset applied: Smallest file.",
                Preset.FullData => "Preset applied: Full data hand-off.",
                _ => "Preset applied: Coordination model."
            } + " Mapping, roles, names and coordinates were kept.");
        }

        private void ResetAll()
        {
            if (WF.MessageBox.Show("Reset every setting on every tab to its default?\n\nMapping rules, roles, parameter mappings and coordinates are cleared too. Save a profile first if you may want them back.",
                    "BIMCamel", WF.MessageBoxButtons.OKCancel, WF.MessageBoxIcon.Question) != WF.DialogResult.OK) return;
            _applying = true;
            try
            {
                Apply(_defaults);
                // Apply keeps names and rule lists when the profile has none — right for an old
                // profile, wrong for a reset — so those are cleared explicitly.
                TxtProject.Text = _defaults.ProjectName; TxtSite.Text = _defaults.SiteName;
                TxtBuilding.Text = _defaults.BuildingName; TxtStorey.Text = _defaults.StoreyName;
                _paramRows.Clear();
                foreach (var (cat, par) in new[] { (_catType, _parType), (_catLevel, _parLevel), (_catMat, _parMat), (_catCls, _parCls), (_catName, _parName) })
                { cat.Text = ""; par.Text = ""; }
            }
            finally { _applying = false; }
            _ui.ProfileName = ""; _profileJson = "";
            AfterBulkChange();
            SetStatus("All settings reset to defaults.");
        }
    }
}
