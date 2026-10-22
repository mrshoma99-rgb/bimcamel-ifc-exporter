using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using WF = System.Windows.Forms;

namespace BIMCamel.UI
{
    /// <summary>
    /// The parameter mapping as a CSV file, so a long list can be written in Excel and shared
    /// between projects. Four columns: source category, source property, target pset, target name.
    /// </summary>
    public partial class ExporterView
    {
        private const string ParamCsvHeader = "Source category,Source property,Target pset,Target name";

        private void OnParamExportCsv(object sender, RoutedEventArgs e)
        {
            var rows = _paramRows.Where(r => !string.IsNullOrWhiteSpace(r.SourceProperty)).ToList();
            if (rows.Count == 0) { SetStatus("No parameter mappings to export."); return; }
            try
            {
                using var sfd = new WF.SaveFileDialog { Title = "Export parameter mapping", Filter = "CSV file (*.csv)|*.csv", FileName = "parameter-mapping.csv" };
                if (sfd.ShowDialog() != WF.DialogResult.OK) return;
                var sb = new StringBuilder().AppendLine(ParamCsvHeader);
                foreach (var r in rows)
                    sb.AppendLine(string.Join(",", new[] { r.SourceCategory == AnyCat ? "" : r.SourceCategory, r.SourceProperty, r.TargetPset, r.TargetName }.Select(CsvField)));
                // BOM so Excel opens accented property names correctly.
                File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
                SetStatus($"Exported {rows.Count} parameter mapping(s).");
            }
            catch (Exception ex) { SetStatus("Could not export the CSV: " + ex.Message); }
        }

        private void OnParamImportCsv(object sender, RoutedEventArgs e)
        {
            try
            {
                using var ofd = new WF.OpenFileDialog { Title = "Import parameter mapping", Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*" };
                if (ofd.ShowDialog() != WF.DialogResult.OK) return;
                int added = 0, skipped = 0;
                var existing = new HashSet<string>(_paramRows.Select(RowKey), StringComparer.OrdinalIgnoreCase);
                bool first = true;
                foreach (var line in File.ReadAllLines(ofd.FileName))
                {
                    if (line.Trim().Length == 0) continue;
                    // Excel writes ";" in locales where "," is the decimal separator.
                    char sep = line.IndexOf(';') >= 0 && line.IndexOf(',') < 0 ? ';' : ',';
                    var f = CsvSplit(line, sep);
                    bool header = first && f.Count > 1 && f[1].IndexOf("property", StringComparison.OrdinalIgnoreCase) >= 0;
                    first = false;
                    if (header) continue;
                    if (f.Count < 2 || f[1].Trim().Length == 0) { skipped++; continue; }
                    string F(int i) => i < f.Count ? f[i].Trim() : "";
                    var row = new ParamRow { SourceCategory = F(0).Length == 0 ? AnyCat : F(0), SourceProperty = F(1), TargetPset = F(2), TargetName = F(3) };
                    if (!existing.Add(RowKey(row))) { skipped++; continue; }
                    _paramRows.Add(row);
                    added++;
                }
                SetStatus($"Imported {added} parameter mapping(s)" + (skipped > 0 ? $", {skipped} skipped (blank or already listed)." : "."));
            }
            catch (Exception ex) { SetStatus("Could not import the CSV: " + ex.Message); }
        }

        private static string RowKey(ParamRow r) => (r.SourceCategory == AnyCat ? "" : r.SourceCategory) + "|" + r.SourceProperty + "|" + r.TargetPset + "|" + r.TargetName;

        private static string CsvField(string? v)
        {
            v ??= "";
            return v.IndexOfAny(new[] { ',', '"', ';', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        private static List<string> CsvSplit(string line, char sep)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cur.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == sep) { result.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            result.Add(cur.ToString());
            return result;
        }
    }
}
