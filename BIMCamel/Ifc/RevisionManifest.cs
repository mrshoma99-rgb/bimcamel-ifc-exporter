using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BIMCamel.Ifc
{
    /// <summary>
    /// A tiny sidecar written next to each exported IFC: one line per element, GlobalId plus a
    /// content hash. Re-exporting the same model then answers the question a CDE actually asks —
    /// what changed since the last issue?
    ///
    /// Stable GlobalIds alone are not enough for that. They tell you an element is the SAME
    /// element; they cannot tell you whether it moved, was reclassified or was remodelled. The
    /// hash covers the semantics we write plus a geometry signature, so a diff can separate
    /// NEW / DELETED / MODIFIED / UNCHANGED instead of just listing ids.
    ///
    /// Plain text on purpose: a 500k-element manifest stays small, streams, and is itself
    /// diffable with ordinary tools.
    /// </summary>
    public sealed class RevisionManifest
    {
        public const string Header = "BIMCamel-manifest 2";

        /// <summary>What version 1 wrote. Still read as a baseline — see <see cref="Load"/>.</summary>
        public const string HeaderV1 = "BIMCamel-manifest 1";

        public string Schema = "";
        public string Written = "";
        public readonly Dictionary<string, ulong> Elements = new Dictionary<string, ulong>(StringComparer.Ordinal);

        /// <summary>
        /// What each element is, so a change list can name it.
        ///
        /// Version 2's whole reason for existing. A list of forty GlobalIds is not something a
        /// coordinator can act on; "IFC-4123 Duct — Rectangular Duct 400x250 — MODIFIED" is. The
        /// names matter most for DELETED, where the element is no longer in the model and the
        /// previous manifest is the only place left that knows what it was.
        /// </summary>
        public readonly Dictionary<string, Entry> Names = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>What an element was called and what kind of thing it was.</summary>
        public readonly struct Entry
        {
            public Entry(string? name, string? type) { Name = name ?? ""; Type = type ?? ""; }

            public string Name { get; }
            public string Type { get; }
        }

        /// <summary>Record an element: its hash, and what it is.</summary>
        public void Put(string guid, ulong hash, string? name, string? type)
        {
            Elements[guid] = hash;
            Names[guid] = new Entry(name, type);
        }

        /// <summary>What an element was called, or a blank entry when the manifest does not know.</summary>
        public Entry NameOf(string guid) => Names.TryGetValue(guid, out var e) ? e : new Entry("", "");

        /// <summary>Sidecar path for an IFC: "model.ifc" → "model.ifc.bcmanifest".</summary>
        public static string PathFor(string ifcPath) => ifcPath + ".bcmanifest";

        public void Save(string ifcPath)
        {
            using var w = new StreamWriter(PathFor(ifcPath), false, new UTF8Encoding(false));
            w.WriteLine(Header);
            w.WriteLine("schema=" + Schema);
            w.WriteLine("written=" + Written);
            w.WriteLine("elements=" + Elements.Count.ToString(CultureInfo.InvariantCulture));

            // id, hash, name, type. Tab-separated because a name may contain a comma and will,
            // and because the whole point of a text manifest is that ordinary tools can diff it.
            // Tabs and newlines inside a name are replaced rather than escaped: a name is a label,
            // not a payload, and an escaping scheme is a parser nobody needs.
            foreach (var kv in Elements)
            {
                var e = NameOf(kv.Key);
                w.WriteLine(kv.Key + "\t" + kv.Value.ToString("x16") + "\t" + Clean(e.Name) + "\t" + Clean(e.Type));
            }
        }

        /// <summary>Reads a prior manifest, or null when there isn't one (the first export).</summary>
        public static RevisionManifest? Load(string ifcPath)
        {
            string p = PathFor(ifcPath);
            if (!File.Exists(p)) return null;
            try
            {
                var m = new RevisionManifest();
                using var r = new StreamReader(p);

                var header = r.ReadLine();

                // Version 1 is still a baseline. It knows every id and every hash, so it answers
                // NEW / DELETED / MODIFIED / UNCHANGED exactly as well as version 2 does — it just
                // cannot name a deleted element. Refusing it would throw away a real comparison to
                // avoid an empty column, and would make the first export after an upgrade report
                // every element in the model as new.
                if (header != Header && header != HeaderV1) return null;

                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;

                    var parts = line.Split('\t');

                    if (parts.Length < 2)
                    {
                        if (line.StartsWith("schema=", StringComparison.Ordinal)) m.Schema = line.Substring(7);
                        else if (line.StartsWith("written=", StringComparison.Ordinal)) m.Written = line.Substring(8);
                        continue;
                    }

                    if (!ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
                        continue;

                    m.Put(parts[0], h,
                          parts.Length > 2 ? parts[2] : "",
                          parts.Length > 3 ? parts[3] : "");
                }
                return m;
            }
            catch { return null; }   // an unreadable baseline must never fail an export
        }

        public sealed class Diff
        {
            public string PreviousWritten = "";
            public bool SchemaChanged;

            /// <summary>True when the baseline predates version 2 and cannot name a deleted element.</summary>
            public bool BaselineHasNoNames;

            // The ids themselves, not only the counts. A summary that says "312 modified" tells a
            // coordinator that something happened; it does not tell them what, and the manifest is
            // the only thing that knows. Kept in the order the current model has them, so a change
            // list reads in the same order as the model tree.
            public readonly List<string> NewIds = new List<string>();
            public readonly List<string> DeletedIds = new List<string>();
            public readonly List<string> ModifiedIds = new List<string>();
            public readonly List<string> UnchangedIds = new List<string>();

            public int New => NewIds.Count;
            public int Deleted => DeletedIds.Count;
            public int Modified => ModifiedIds.Count;
            public int Unchanged => UnchangedIds.Count;
            public int Total => New + Deleted + Modified + Unchanged;

            /// <summary>How many elements are not the same as last time.</summary>
            public int Changed => New + Deleted + Modified;

            public string Report()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Revision    : vs export of {(PreviousWritten.Length > 0 ? PreviousWritten : "unknown date")}");
                if (SchemaChanged) sb.AppendLine("     · schema changed since the last export — treat the comparison with care");
                sb.AppendLine($"     NEW       {New,9:N0}");
                sb.AppendLine($"     DELETED   {Deleted,9:N0}");
                sb.AppendLine($"     MODIFIED  {Modified,9:N0}");
                sb.AppendLine($"     UNCHANGED {Unchanged,9:N0}");
                return sb.ToString();
            }
        }

        /// <summary>Change-list path for an IFC: "model.ifc" → "model.ifc.changes.csv".</summary>
        public static string ChangesPathFor(string ifcPath) => ifcPath + ".changes.csv";

        /// <summary>
        /// The change list: one row per element that is not the same as last time.
        ///
        /// A CSV because the person who needs it opens it in Excel, filters it and sends it on.
        /// UNCHANGED is deliberately left out — on a real model it is 95% of the rows and it is the
        /// one answer nobody is looking for. The counts in the summary still include it, so the
        /// file being short is not the same as nothing having happened.
        /// </summary>
        /// <returns>False when it could not be written. An export is never failed by this.</returns>
        public static bool SaveChanges(string ifcPath, Diff diff,
                                       RevisionManifest previous, RevisionManifest current)
        {
            try
            {
                using var w = new StreamWriter(ChangesPathFor(ifcPath), false, new UTF8Encoding(true));

                w.WriteLine("Change,GlobalId,Name,Type");

                foreach (var id in diff.NewIds) Row(w, "NEW", id, current.NameOf(id));

                // Named from the PREVIOUS manifest, because the element is not in this model any
                // more and nothing else remembers what it was.
                foreach (var id in diff.DeletedIds) Row(w, "DELETED", id, previous.NameOf(id));

                foreach (var id in diff.ModifiedIds) Row(w, "MODIFIED", id, current.NameOf(id));

                return true;
            }
            catch
            {
                // The IFC is the deliverable; the change list is a convenience. A read-only folder
                // must not turn a finished export into a failed one.
                return false;
            }
        }

        private static void Row(StreamWriter w, string change, string id, Entry e) =>
            w.WriteLine(change + "," + Csv(id) + "," + Csv(e.Name) + "," + Csv(e.Type));

        private static string Csv(string? value)
        {
            var v = value ?? "";
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
                ? v
                : "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>A tab or a newline in a name would break the manifest's own format.</summary>
        private static string Clean(string? value) =>
            (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        public static Diff Compare(RevisionManifest previous, RevisionManifest current)
        {
            var d = new Diff
            {
                PreviousWritten = previous.Written,
                SchemaChanged = previous.Schema != current.Schema,

                // A version 1 baseline knows every id and every hash and no names at all. Worth
                // saying, because the DELETED rows in the change list will be blank and that looks
                // like a bug rather than like a manifest written before names existed.
                BaselineHasNoNames = previous.Elements.Count > 0 && previous.Names.Count == 0,
            };

            foreach (var kv in current.Elements)
            {
                if (!previous.Elements.TryGetValue(kv.Key, out var old)) d.NewIds.Add(kv.Key);
                else if (old != kv.Value) d.ModifiedIds.Add(kv.Key);
                else d.UnchangedIds.Add(kv.Key);
            }

            foreach (var kv in previous.Elements)
                if (!current.Elements.ContainsKey(kv.Key)) d.DeletedIds.Add(kv.Key);

            return d;
        }

        /// <summary>
        /// FNV-1a over whatever identifies this element's content. Callers mix in the semantics
        /// they hold; geometry strength differs by path and is documented at each call site.
        /// </summary>
        public struct Hasher
        {
            private ulong _h;
            public static Hasher Start() => new Hasher { _h = 14695981039346656037UL };
            public void Add(string? s)
            {
                if (s != null) foreach (char c in s) { _h ^= c; _h *= 1099511628211UL; }
                _h ^= 0x1FUL; _h *= 1099511628211UL;   // field separator
            }
            public void Add(long v) { unchecked { _h ^= (ulong)v; _h *= 1099511628211UL; } }
            /// <summary>Quantised to 0.1 mm so floating-point noise doesn't read as a change.</summary>
            public void Add(double v) => Add((long)Math.Round(v * 10000.0));
            public ulong Value => _h;
        }
    }
}
