using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BIMCamel.Ifc
{
    /// <summary>
    /// Dependency-free structural validation of the written IFC (F12).
    ///
    /// This is deliberately NOT a full EXPRESS/MVD validation — that needs a schema engine. What it
    /// does instead is check the things THIS exporter can plausibly get wrong, because those are
    /// the failures that actually reach users: a malformed STEP envelope, a reference to an entity
    /// that was never written, a duplicated or malformed GlobalId, an empty aggregate where the
    /// schema demands at least one member (the defect that made Revit reject whole files), and
    /// enumeration tokens built from free text the user typed.
    ///
    /// Both passes stream — an export can be gigabytes, so nothing is ever read whole.
    /// </summary>
    public static class IfcValidator
    {
        private static readonly Regex Def = new Regex(@"^#(\d+)\s*=\s*([A-Za-z0-9_]+)\s*\(", RegexOptions.Compiled);

        /// <summary>Valid IFC GlobalId: 22 characters from the base-64 alphabet IFC actually uses.</summary>
        private static readonly Regex GuidRx = new Regex(@"^[0-9A-Za-z_$]{22}$", RegexOptions.Compiled);

        /// <summary>An enumeration token — .SOMETHING. — as IFC spells it.</summary>
        private static readonly Regex EnumRx = new Regex(@"^\.[A-Z][A-Z0-9_]*\.$", RegexOptions.Compiled);

        /// <summary>
        /// Entities whose first aggregate argument is a LIST [1:?] — an empty one makes the file
        /// schema-invalid, and strict readers reject the whole file rather than the entity.
        /// </summary>
        private static readonly HashSet<string> NoEmptyAggregate = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IFCCARTESIANPOINTLIST3D", "IFCTRIANGULATEDFACESET", "IFCPOLYLOOP", "IFCCONNECTEDFACESET",
            "IFCFACEBASEDSURFACEMODEL", "IFCFACE", "IFCSHAPEREPRESENTATION", "IFCPRODUCTDEFINITIONSHAPE",
            "IFCRELDEFINESBYPROPERTIES", "IFCRELDEFINESBYTYPE", "IFCRELASSOCIATESMATERIAL",
            "IFCRELASSOCIATESCLASSIFICATION", "IFCRELCONTAINEDINSPATIALSTRUCTURE", "IFCRELAGGREGATES",
            "IFCPROPERTYSET", "IFCELEMENTQUANTITY", "IFCUNITASSIGNMENT",
        };

        public static List<string> Validate(string path)
        {
            var issues = new List<string>();
            var defined = new HashSet<int>();
            var guids = new HashSet<string>(StringComparer.Ordinal);
            bool header = false, schema = false, data = false;
            int dup = 0, dupGuid = 0, badGuid = 0, emptyAgg = 0, badEnum = 0;
            string firstBadEnum = "", firstEmptyAgg = "", firstDupGuid = "";

            // Pass 1: entity ids, GlobalIds, aggregates, enum tokens.
            using (var r = new StreamReader(path))
            {
                string? line; bool first = true;
                while ((line = r.ReadLine()) != null)
                {
                    if (first) { if (line.StartsWith("ISO-10303-21")) header = true; first = false; }
                    if (line.StartsWith("FILE_SCHEMA")) schema = true;
                    else if (line.StartsWith("DATA;")) data = true;

                    var dm = Def.Match(line);
                    if (!dm.Success) continue;
                    if (!defined.Add(int.Parse(dm.Groups[1].Value))) dup++;

                    string ent = dm.Groups[2].Value;
                    var args = SplitArgs(line, dm.Index + dm.Length);

                    // GlobalId is always the first attribute of an IfcRoot subtype, which is
                    // exactly the set of entities whose first argument is a 22-char string.
                    if (args.Count > 0)
                    {
                        var a0 = args[0].Trim();
                        if (a0.Length >= 2 && a0[0] == '\'' && a0[a0.Length - 1] == '\'')
                        {
                            var inner = a0.Substring(1, a0.Length - 2);
                            if (inner.Length == 22)
                            {
                                if (!GuidRx.IsMatch(inner)) { badGuid++; }
                                else if (!guids.Add(inner)) { dupGuid++; if (firstDupGuid.Length == 0) firstDupGuid = inner; }
                            }
                        }
                    }

                    if (NoEmptyAggregate.Contains(ent))
                        foreach (var a in args)
                            if (a.Trim() == "()")
                            {
                                emptyAgg++;
                                if (firstEmptyAgg.Length == 0) firstEmptyAgg = ent;
                                break;
                            }

                    // Enumeration tokens come from user-typed PredefinedType text, so a stray
                    // space or punctuation silently produces a file no reader will accept.
                    foreach (var a in args)
                    {
                        var t = a.Trim();
                        if (t.Length > 1 && t[0] == '.' && t[t.Length - 1] == '.' && !EnumRx.IsMatch(t))
                        {
                            badEnum++;
                            if (firstBadEnum.Length == 0) firstBadEnum = ent + " " + t;
                        }
                    }
                }
            }

            // Pass 2: every reference resolves. References are collected OUTSIDE quoted strings —
            // a property value containing "#123" is text, not a reference, and counting it produced
            // phantom "dangling reference" reports.
            int missing = 0;
            var sample = new List<int>();
            using (var r = new StreamReader(path))
            {
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    var dm = Def.Match(line);
                    if (!dm.Success) continue;
                    foreach (int id in RefsOutsideStrings(line))
                        if (!defined.Contains(id)) { missing++; if (sample.Count < 5) sample.Add(id); }
                }
            }

            if (!header) issues.Add("missing ISO-10303-21 header");
            if (!schema) issues.Add("missing FILE_SCHEMA");
            if (!data) issues.Add("missing DATA section");
            if (dup > 0) issues.Add($"{dup} duplicate entity id(s)");
            if (missing > 0) issues.Add($"{missing} dangling reference(s), e.g. #{string.Join(", #", sample)}");
            if (dupGuid > 0) issues.Add($"{dupGuid} duplicate GlobalId(s), e.g. {firstDupGuid}");
            if (badGuid > 0) issues.Add($"{badGuid} malformed GlobalId(s)");
            if (emptyAgg > 0) issues.Add($"{emptyAgg} empty mandatory aggregate(s), first in {firstEmptyAgg}");
            if (badEnum > 0) issues.Add($"{badEnum} malformed enumeration token(s), e.g. {firstBadEnum} — check the PredefinedType column");
            return issues;
        }

        /// <summary>
        /// Splits an entity's argument list on top-level commas, respecting nesting and STEP's
        /// doubled-quote escape. Starts just after the opening parenthesis.
        /// </summary>
        private static List<string> SplitArgs(string line, int start)
        {
            var args = new List<string>();
            var cur = new StringBuilder();
            int depth = 0; bool inStr = false;
            for (int i = start; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '\'') { cur.Append("''"); i++; continue; }
                        inStr = false;
                    }
                    cur.Append(c);
                    continue;
                }
                if (c == '\'') { inStr = true; cur.Append(c); continue; }
                if (c == '(') { depth++; cur.Append(c); continue; }
                if (c == ')')
                {
                    if (depth == 0) { args.Add(cur.ToString()); return args; } // closed the entity
                    depth--; cur.Append(c); continue;
                }
                if (c == ',' && depth == 0) { args.Add(cur.ToString()); cur.Clear(); continue; }
                cur.Append(c);
            }
            if (cur.Length > 0) args.Add(cur.ToString());
            return args;
        }

        /// <summary>Entity references on a line, ignoring anything inside a quoted string.</summary>
        private static IEnumerable<int> RefsOutsideStrings(string line)
        {
            bool inStr = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '\'') { i++; continue; }
                        inStr = false;
                    }
                    continue;
                }
                if (c == '\'') { inStr = true; continue; }
                if (c != '#') continue;
                int j = i + 1, val = 0;
                while (j < line.Length && line[j] >= '0' && line[j] <= '9') { val = val * 10 + (line[j] - '0'); j++; }
                if (j > i + 1)
                {
                    // Skip the definition itself (#12=…), which is not a reference.
                    if (i != 0) yield return val;
                    i = j - 1;
                }
            }
        }
    }
}
