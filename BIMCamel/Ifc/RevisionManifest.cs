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
        public const string Header = "BIMCamel-manifest 1";

        public string Schema = "";
        public string Written = "";
        public readonly Dictionary<string, ulong> Elements = new Dictionary<string, ulong>(StringComparer.Ordinal);

        /// <summary>Sidecar path for an IFC: "model.ifc" → "model.ifc.bcmanifest".</summary>
        public static string PathFor(string ifcPath) => ifcPath + ".bcmanifest";

        public void Save(string ifcPath)
        {
            using var w = new StreamWriter(PathFor(ifcPath), false, new UTF8Encoding(false));
            w.WriteLine(Header);
            w.WriteLine("schema=" + Schema);
            w.WriteLine("written=" + Written);
            w.WriteLine("elements=" + Elements.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var kv in Elements) w.WriteLine(kv.Key + "\t" + kv.Value.ToString("x16"));
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
                if (r.ReadLine() != Header) return null;   // a format we don't understand is not a baseline
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    int tab = line.IndexOf('\t');
                    if (tab < 0)
                    {
                        if (line.StartsWith("schema=", StringComparison.Ordinal)) m.Schema = line.Substring(7);
                        else if (line.StartsWith("written=", StringComparison.Ordinal)) m.Written = line.Substring(8);
                        continue;
                    }
                    var id = line.Substring(0, tab);
                    if (ulong.TryParse(line.Substring(tab + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
                        m.Elements[id] = h;
                }
                return m;
            }
            catch { return null; }   // an unreadable baseline must never fail an export
        }

        public sealed class Diff
        {
            public int New, Deleted, Modified, Unchanged;
            public string PreviousWritten = "";
            public bool SchemaChanged;
            public int Total => New + Deleted + Modified + Unchanged;

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

        public static Diff Compare(RevisionManifest previous, RevisionManifest current)
        {
            var d = new Diff { PreviousWritten = previous.Written, SchemaChanged = previous.Schema != current.Schema };
            foreach (var kv in current.Elements)
            {
                if (!previous.Elements.TryGetValue(kv.Key, out var old)) d.New++;
                else if (old != kv.Value) d.Modified++;
                else d.Unchanged++;
            }
            foreach (var kv in previous.Elements)
                if (!current.Elements.ContainsKey(kv.Key)) d.Deleted++;
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
