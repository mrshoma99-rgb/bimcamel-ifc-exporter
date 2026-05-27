using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace BIMCamel.Ifc
{
    /// <summary>
    /// Lightweight, dependency-free structural validation of the written IFC (F12). Checks the
    /// STEP envelope and that every entity reference resolves to a defined entity with no duplicate
    /// ids. It is NOT a full schema/MVD validation — that is the job of the optional, isolated
    /// third-party validator component (plan §12). Returns an empty list when the file looks sound.
    /// </summary>
    public static class IfcValidator
    {
        private static readonly Regex Def = new Regex(@"^#(\d+)\s*=", RegexOptions.Compiled);
        private static readonly Regex RefRx = new Regex(@"#(\d+)", RegexOptions.Compiled);

        public static List<string> Validate(string path)
        {
            var issues = new List<string>();
            var defined = new HashSet<int>();
            bool header = false, schema = false, data = false;
            int dup = 0;

            // Pass 1: collect defined ids (stream — the file can be gigabytes; v4/A8 — never ReadAllLines).
            using (var r = new StreamReader(path))
            {
                string? line; bool first = true;
                while ((line = r.ReadLine()) != null)
                {
                    if (first) { if (line.StartsWith("ISO-10303-21")) header = true; first = false; }
                    if (line.StartsWith("FILE_SCHEMA")) schema = true;
                    else if (line.StartsWith("DATA;")) data = true;
                    var dm = Def.Match(line);
                    if (dm.Success && !defined.Add(int.Parse(dm.Groups[1].Value))) dup++;
                }
            }

            // Pass 2: check every reference resolves.
            int missing = 0;
            var sample = new List<int>();
            using (var r = new StreamReader(path))
            {
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    if (!Def.IsMatch(line)) continue;
                    foreach (Match m in RefRx.Matches(line))
                    {
                        int id = int.Parse(m.Groups[1].Value);
                        if (!defined.Contains(id)) { missing++; if (sample.Count < 5) sample.Add(id); }
                    }
                }
            }

            if (!header) issues.Add("missing ISO-10303-21 header");
            if (!schema) issues.Add("missing FILE_SCHEMA");
            if (!data) issues.Add("missing DATA section");
            if (dup > 0) issues.Add($"{dup} duplicate entity id(s)");
            if (missing > 0) issues.Add($"{missing} dangling reference(s), e.g. #{string.Join(", #", sample)}");
            return issues;
        }
    }
}
