using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using WF = System.Windows.Forms;

namespace BIMCamel.UI
{
    /// <summary>
    /// What happens around the written file: its name, where the save dialog starts, and the
    /// buttons that reach it afterwards.
    /// </summary>
    public partial class ExporterView
    {
        private readonly List<string> _lastOutputs = new List<string>();

        /// <summary>Where the next save dialog opens: the folder of the last export, if it still exists.</summary>
        private string StartFolder()
        {
            string f = _ui.LastExportFolder ?? "";
            return f.Length > 0 && System.IO.Directory.Exists(f) ? f : "";
        }

        private void RememberFolder(string pathOrFolder)
        {
            try
            {
                string f = System.IO.Directory.Exists(pathOrFolder) ? pathOrFolder : (System.IO.Path.GetDirectoryName(pathOrFolder) ?? "");
                if (f.Length > 0) { _ui.LastExportFolder = f; SettingsStoreSaveUi(); }
            }
            catch { }
        }

        private void SettingsStoreSaveUi() => BIMCamel.Profiles.SettingsStore.SaveUi(_ui);

        private void ShowOutputs(IReadOnlyList<string> files)
        {
            _lastOutputs.Clear();
            _lastOutputs.AddRange(files);
            BtnOpenFile.IsEnabled = files.Count == 1;
            BtnOpenFolder.IsEnabled = files.Count > 0;
            if (files.Count > 0) RememberFolder(files[0]);
            ShowReport();
        }

        /// <summary>Bring the report into view: after an export it is the thing to read, and it
        /// sits at the bottom of a long tab.</summary>
        private void ShowReport()
        {
            Tabs.SelectedItem = TabExport;
            Dispatcher.BeginInvoke(new Action(() => LblReportHead.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnOpenOutput(object s, RoutedEventArgs e)
        {
            if (_lastOutputs.Count == 0) return;
            Launch(_lastOutputs[0]);
        }

        private void OnOpenOutputFolder(object s, RoutedEventArgs e)
        {
            if (_lastOutputs.Count == 0) return;
            try { Process.Start("explorer.exe", $"/select,\"{_lastOutputs[0]}\""); }
            catch { Launch(System.IO.Path.GetDirectoryName(_lastOutputs[0]) ?? ""); }
        }

        private void Launch(string target)
        {
            if (target.Length == 0) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("Could not open it: " + ex.Message); }
        }

        private void OnSaveReport(object s, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtReport.Text)) return;
            try
            {
                string start = _lastOutputs.Count > 0 ? (System.IO.Path.GetDirectoryName(_lastOutputs[0]) ?? "") : StartFolder();
                string name = _lastOutputs.Count > 0 ? System.IO.Path.GetFileNameWithoutExtension(_lastOutputs[0]) + "_report.txt" : "bimcamel_report.txt";
                using var sfd = new WF.SaveFileDialog { Title = "Save export report", Filter = "Text file (*.txt)|*.txt", FileName = name, DefaultExt = "txt", InitialDirectory = start };
                if (sfd.ShowDialog() != WF.DialogResult.OK) return;
                System.IO.File.WriteAllText(sfd.FileName, TxtReport.Text);
                SetStatus("Report saved.");
            }
            catch (Exception ex) { SetStatus("Could not save the report: " + ex.Message); }
        }
    }
}
