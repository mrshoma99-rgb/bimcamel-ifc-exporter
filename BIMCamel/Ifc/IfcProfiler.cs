using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BIMCamel.Ifc
{
    /// <summary>
    /// Read-only size diagnostic (v4 F0): streams the written IFC and sums bytes per STEP entity
    /// type (the keyword after "#id="). Tells us where the file size actually goes so the
    /// reduction work (quantities, transforms, property-set dedup) is aimed at the real top
    /// consumers rather than an estimate. Streams line-by-line — never loads the whole file.
    /// </summary>
    public static class IfcProfiler
    {
        public static string Profile(string path, int topN = 14)
        {
            var bytes = new Dictionary<string, long>(StringComparer.Ordinal);
            var count = new Dictionary<string, long>(StringComparer.Ordinal);
            long total = 0;

            using (var r = new StreamReader(path))
            {
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    long len = line.Length + 1; // approximate on-disk bytes (+1 for the newline)
                    total += len;
                    if (line.Length == 0 || line[0] != '#') continue;

                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    int paren = line.IndexOf('(', eq + 1);
                    int end = paren < 0 ? line.Length : paren;
                    if (end <= eq + 1) continue;
                    string type = line.Substring(eq + 1, end - eq - 1);

                    bytes.TryGetValue(type, out long b); bytes[type] = b + len;
                    count.TryGetValue(type, out long c); count[type] = c + 1;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Output size profile  (total {total / (1024.0 * 1024.0):N1} MB, top {topN} entity types by bytes):");
            foreach (var kv in bytes.OrderByDescending(k => k.Value).Take(topN))
            {
                double mb = kv.Value / (1024.0 * 1024.0);
                double pct = total > 0 ? 100.0 * kv.Value / total : 0;
                count.TryGetValue(kv.Key, out long c);
                sb.AppendLine($"  {kv.Key,-34} {mb,8:N1} MB  {pct,5:N1}%  ({c:N0} entities)");
            }
            return sb.ToString();
        }
    }
}
