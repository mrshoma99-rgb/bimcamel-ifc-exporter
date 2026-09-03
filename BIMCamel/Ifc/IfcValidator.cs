using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
        // v5 E6: these three checks used to be compiled Regexes run against EVERY line of a file
        // the prova says reaches 2 GB — tens of millions of Match calls, each allocating a Match
        // and its Groups, on a pass that is not even inside the export stopwatch. They are all
        // simple character tests, so they are written as such. What is validated is unchanged: the
        // issue list for a given file must come out identical.

        /// <summary>
        /// "#123=IFCWALL(" at the start of a line. Returns the id, the entity name, and the index
        /// just past the opening parenthesis — the same three things the old regex yielded.
        /// </summary>
        private static bool TryParseDef(string line, out int id, out string ent, out int argStart)
        {
            id = 0; ent = ""; argStart = 0;
            int n = line.Length;
            if (n < 4 || line[0] != '#') return false;

            int i = 1;
            long val = 0;
            while (i < n && line[i] >= '0' && line[i] <= '9') { val = val * 10 + (line[i] - '0'); i++; }
            if (i == 1 || val > int.MaxValue) return false;

            while (i < n && IsSpace(line[i])) i++;
            if (i >= n || line[i] != '=') return false;
            i++;
            while (i < n && IsSpace(line[i])) i++;

            int nameStart = i;
            while (i < n && IsNameChar(line[i])) i++;
            if (i == nameStart) return false;
            int nameLen = i - nameStart;

            while (i < n && IsSpace(line[i])) i++;
            if (i >= n || line[i] != '(') return false;

            id = (int)val;
            ent = line.Substring(nameStart, nameLen);
            argStart = i + 1;
            return true;
        }

        private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v';
        private static bool IsNameChar(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

        /// <summary>Valid IFC GlobalId: 22 characters from the base-64 alphabet IFC actually uses.</summary>
        private static bool IsGuid(string s)
        {
            if (s.Length != 22) return false;
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                          || c == '_' || c == '$';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>An enumeration token — .SOMETHING. — as IFC spells it: ^\.[A-Z][A-Z0-9_]*\.$
        /// over line[start..end) which the caller has already trimmed.</summary>
        private static bool IsEnumToken(string line, int start, int end)
        {
            int len = end - start;
            if (len < 3 || line[start] != '.' || line[end - 1] != '.') return false;
            char first = line[start + 1];
            if (first < 'A' || first > 'Z') return false;
            for (int i = start + 2; i < end - 1; i++)
            {
                char c = line[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
            }
            return true;
        }

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

        /// <summary>Running counts for one file, so the argument scanner can report without a
        /// long ref-parameter list.</summary>
        private sealed class Tally
        {
            public int Dup, DupGuid, BadGuid, EmptyAgg, BadEnum;
            public string FirstBadEnum = "", FirstEmptyAgg = "", FirstDupGuid = "";
        }

        public static List<string> Validate(string path)
        {
            var issues = new List<string>();
            var defined = new HashSet<int>();
            var guids = new HashSet<string>(StringComparer.Ordinal);
            bool header = false, schema = false, data = false;
            var t = new Tally();

            // Pass 1: entity ids, GlobalIds, aggregates, enum tokens.
            using (var r = new StreamReader(path))
            {
                string? line; bool first = true;
                while ((line = r.ReadLine()) != null)
                {
                    if (first) { if (line.StartsWith("ISO-10303-21")) header = true; first = false; }
                    if (line.StartsWith("FILE_SCHEMA")) schema = true;
                    else if (line.StartsWith("DATA;")) data = true;

                    if (!TryParseDef(line, out int id, out string ent, out int argStart)) continue;
                    if (!defined.Add(id)) t.Dup++;

                    ScanArgs(line, argStart, ent, NoEmptyAggregate.Contains(ent), guids, t);
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
                    if (!TryParseDef(line, out _, out _, out _)) continue;
                    foreach (int id in RefsOutsideStrings(line))
                        if (!defined.Contains(id)) { missing++; if (sample.Count < 5) sample.Add(id); }
                }
            }

            if (!header) issues.Add("missing ISO-10303-21 header");
            if (!schema) issues.Add("missing FILE_SCHEMA");
            if (!data) issues.Add("missing DATA section");
            if (t.Dup > 0) issues.Add($"{t.Dup} duplicate entity id(s)");
            if (missing > 0) issues.Add($"{missing} dangling reference(s), e.g. #{string.Join(", #", sample)}");
            if (t.DupGuid > 0) issues.Add($"{t.DupGuid} duplicate GlobalId(s), e.g. {t.FirstDupGuid}");
            if (t.BadGuid > 0) issues.Add($"{t.BadGuid} malformed GlobalId(s)");
            if (t.EmptyAgg > 0) issues.Add($"{t.EmptyAgg} empty mandatory aggregate(s), first in {t.FirstEmptyAgg}");
            if (t.BadEnum > 0) issues.Add($"{t.BadEnum} malformed enumeration token(s), e.g. {t.FirstBadEnum} — check the PredefinedType column");
            return issues;
        }

        /// <summary>
        /// Walks one entity's argument list ONCE, doing all three per-argument checks in place
        /// (v5 E6). This replaces SplitArgs, which materialised a List plus a StringBuilder plus a
        /// string per argument for every line in the file — on a multi-GB export that allocation
        /// was a larger cost than the regex it fed.
        ///
        /// Argument boundaries are found exactly as SplitArgs found them: top-level commas only,
        /// respecting nesting and STEP's doubled-quote escape, stopping at the parenthesis that
        /// closes the entity. The only string allocated is a genuine 22-character GlobalId, which
        /// has to be interned anyway to detect duplicates.
        /// </summary>
        private static void ScanArgs(string line, int start, string ent, bool noEmpty, HashSet<string> guids, Tally t)
        {
            int n = line.Length;
            int depth = 0; bool inStr = false;
            int argStart = start, argIndex = 0;
            bool emptyReported = false;

            for (int i = start; i <= n; i++)
            {
                bool end = false;
                if (i == n) end = true;                        // truncated line: treat the tail as an argument
                else
                {
                    char c = line[i];
                    if (inStr)
                    {
                        if (c == '\'')
                        {
                            if (i + 1 < n && line[i + 1] == '\'') { i++; continue; }
                            inStr = false;
                        }
                        continue;
                    }
                    if (c == '\'') { inStr = true; continue; }
                    if (c == '(') { depth++; continue; }
                    if (c == ')')
                    {
                        if (depth == 0) end = true;            // closed the entity
                        else { depth--; continue; }
                    }
                    else if (c == ',' && depth == 0) end = true;
                }

                if (!end) continue;

                CheckArg(line, argStart, i, argIndex, ent, noEmpty, guids, t, ref emptyReported);
                argIndex++;
                argStart = i + 1;

                // The entity's closing parenthesis ends the argument list.
                if (i < n && line[i] == ')' && depth == 0) return;
            }
        }

        /// <summary>The three per-argument checks, over line[from..to) with the ends trimmed.</summary>
        private static void CheckArg(string line, int from, int to, int index, string ent, bool noEmpty,
                                     HashSet<string> guids, Tally t, ref bool emptyReported)
        {
            while (from < to && IsSpace(line[from])) from++;
            while (to > from && IsSpace(line[to - 1])) to--;
            int len = to - from;
            if (len <= 0) return;

            // GlobalId is always the first attribute of an IfcRoot subtype, which is exactly the
            // set of entities whose first argument is a 22-char quoted string.
            if (index == 0 && len == 24 && line[from] == '\'' && line[to - 1] == '\'')
            {
                var inner = line.Substring(from + 1, 22);
                if (!IsGuid(inner)) t.BadGuid++;
                else if (!guids.Add(inner)) { t.DupGuid++; if (t.FirstDupGuid.Length == 0) t.FirstDupGuid = inner; }
                return;
            }

            if (noEmpty && !emptyReported && len == 2 && line[from] == '(' && line[from + 1] == ')')
            {
                t.EmptyAgg++;
                if (t.FirstEmptyAgg.Length == 0) t.FirstEmptyAgg = ent;
                emptyReported = true;
                return;
            }

            // Enumeration tokens come from user-typed PredefinedType text, so a stray space or
            // punctuation silently produces a file no reader will accept.
            if (len > 1 && line[from] == '.' && line[to - 1] == '.' && !IsEnumToken(line, from, to))
            {
                t.BadEnum++;
                if (t.FirstBadEnum.Length == 0) t.FirstBadEnum = ent + " " + line.Substring(from, len);
            }
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
