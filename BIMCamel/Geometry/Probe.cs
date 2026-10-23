using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Time and call count for every kind of Navisworks call the scan and the export make.
    ///
    /// The phase lines on the report (Collect, COM convert, Prop harvest…) say which STAGE was
    /// slow, not which CALL, and each left an "Other" no one could explain. Every probe here wraps
    /// one API call, or one tight group of them, and no probe contains another, so the probes of a
    /// phase add up to that phase and whatever is left over is printed as "not probed". The
    /// cost per call is the number that matters: it tells a call that is slow from a call that is
    /// merely frequent, and comparing two runs in one Navisworks session tells a call that is slow
    /// from a call that is paying for Navisworks loading the model on first touch.
    ///
    /// Read thread (Navisworks UI thread) only, like the read API, except <see cref="K.QueueWait"/>,
    /// which is that same thread blocked on the writer. A probe costs two timestamp reads.
    /// </summary>
    public static class Probe
    {
        public enum K
        {
            // ── scan ────────────────────────────────────────────────────────────────
            ScanChildren,      // item.Children: getting the collection and stepping through it
            ScanHidden,        // item.IsHidden
            ScanHasGeometry,   // item.HasGeometry
            ScanDedup,         // overlap memo: ModelItem hashing and equality
            ScanFindAll,       // Search.FindAll / ExplicitModelItems for a mapping set
            ScanKeyGuid,       // item.InstanceGuid for an item key
            ScanKeyPath,       // tree path for a GUID-less item key (Parent and DisplayName reads)
            ScanKeyMemo,       // item-key memo: ModelItem hashing and equality
            ScanMaps,          // filling the class / classification / group maps
            ScanBox,           // BoundingBox() for the base point
            ScanPump,          // progress and UI pumps
            // ── export ──────────────────────────────────────────────────────────────
            SelConvert,        // ComApiBridge.ToInwOpSelection over a chunk
            SelPaths,          // stepping through the selection's Paths()
            SelToItem,         // ComApiBridge.ToModelItem, one per path
            SelMap,            // path map: ModelItem hashing and equality
            SelSingle,         // per-item conversion for items the chunk map missed
            PathId,            // path.ArrayData read and turned into an id
            Fragments,         // path.Fragments(): getting and stepping through the list
            FragPathId,        // each fragment's path.ArrayData, when an instance group is indexed
            ItemName,          // item.DisplayName
            ItemGuid,          // item.InstanceGuid
            ItemKey,           // item key for the set rules (memo lookup, or a fresh key)
            Colour,            // item.Geometry.ActiveColor / ActiveTransparency
            Box,               // item.BoundingBox()
            ShapeNames,        // fast-instancing key: Parent.DisplayName, ClassDisplayName
            Triangles,         // GenerateSimplePrimitives
            Matrix,            // GetLocalToWorldMatrix
            Weld,              // vertex welding (managed)
            Hash,              // mesh content keys, baking, bounds maths (managed)
            Simplify,          // mesh simplification (managed)
            PropCategories,    // item.PropertyCategories: the collection, stepping through it, names
            PropValues,        // category.Properties: stepping through, names, values, formatting
            PropAncestors,     // finding the parent nodes to read
            PropAncestorRead,  // reading a parent node's properties (once per run of siblings)
            PropRules,         // property-value class rules
            ClassifyNode,      // auto-classify: ClassName, IsLayer, ClassDisplayName, DisplayName, HasModel, Parent
            ClassifyCategory,  // auto-classify: the Revit Element › Category lookup
            ClassifyMemo,      // auto-classify memos: ModelItem hashing and equality
            ClassifyRules,     // auto-classify: the rules themselves, and applying a guess (managed)
            Progress,          // progress bar and UI pumps
            QueueWait,         // waiting for the writer thread (a full queue)
        }

        private const int ScanLast = (int)K.ScanPump;
        private static readonly int Count = Enum.GetValues(typeof(K)).Length;
        private static readonly long[] _ticks = new long[Count];
        private static readonly long[] _calls = new long[Count];

        public static long Now
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Stopwatch.GetTimestamp();
        }

        /// <summary>Adds the time since <paramref name="start"/> to <paramref name="k"/>, as one call.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add(K k, long start)
        {
            if ((int)k <= ScanLast && !_scanning) return;
            _ticks[(int)k] += Stopwatch.GetTimestamp() - start;
            _calls[(int)k]++;
        }

        /// <summary>As <see cref="Add"/>, and returns the new timestamp so the next probe can start
        /// from it: consecutive laps cover a stretch of code with no gaps and no overlap.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Lap(K k, long start)
        {
            long now = Stopwatch.GetTimestamp();
            if ((int)k <= ScanLast && !_scanning) return now;
            _ticks[(int)k] += now - start;
            _calls[(int)k]++;
            return now;
        }

        /// <summary>As <see cref="Lap"/>, but adds no call: for a loop whose steps alternate between
        /// two probes, the calls are counted once by the caller.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Mark(K k, long start)
        {
            long now = Stopwatch.GetTimestamp();
            if ((int)k <= ScanLast && !_scanning) return now;
            _ticks[(int)k] += now - start;
            return now;
        }

        /// <summary>Time without a call: the walk's Children probe counts nodes, not each step.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddTime(K k, long start)
        {
            if ((int)k <= ScanLast && !_scanning) return;
            _ticks[(int)k] += Stopwatch.GetTimestamp() - start;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Calls(K k, long n)
        {
            if ((int)k <= ScanLast && !_scanning) return;
            _calls[(int)k] += n;
        }

        /// <summary>
        /// Scan probes count only between <see cref="ResetScan"/> and <see cref="ResetExport"/>.
        /// The same code runs in both phases (the export computes item keys too, a batch export
        /// resolves its sets), and the export times those calls under its own probes, so the
        /// scan's lines must not keep growing while it runs.
        /// </summary>
        private static bool _scanning;

        public static void ResetScan()
        {
            for (int i = 0; i <= ScanLast; i++) { _ticks[i] = 0; _calls[i] = 0; }
            _scanning = true;
        }

        /// <summary>Exports started since Navisworks loaded the plugin. The first one in a session
        /// pays for Navisworks loading the model's geometry and properties; a later one finds them
        /// in memory, so the same model can take very different times on run 1 and run 2.</summary>
        public static int SessionRun { get; private set; }

        public static void ResetExport()
        {
            SessionRun++;
            for (int i = ScanLast + 1; i < Count; i++) { _ticks[i] = 0; _calls[i] = 0; }
            _scanning = false;
        }

        private static readonly Dictionary<K, string> Labels = new Dictionary<K, string>
        {
            [K.ScanChildren] = "Children", [K.ScanHidden] = "IsHidden", [K.ScanHasGeometry] = "HasGeometry",
            [K.ScanDedup] = "overlap memo", [K.ScanFindAll] = "set FindAll", [K.ScanKeyGuid] = "key: InstanceGuid",
            [K.ScanKeyPath] = "key: tree path", [K.ScanKeyMemo] = "key memo", [K.ScanMaps] = "rule maps",
            [K.ScanBox] = "BoundingBox", [K.ScanPump] = "UI pump",
            [K.SelConvert] = "ToInwOpSelection", [K.SelPaths] = "Paths()", [K.SelToItem] = "ToModelItem",
            [K.SelMap] = "path map", [K.SelSingle] = "per-item convert", [K.PathId] = "path id",
            [K.Fragments] = "Fragments()", [K.FragPathId] = "fragment path id", [K.ItemName] = "DisplayName",
            [K.ItemGuid] = "InstanceGuid", [K.ItemKey] = "item key", [K.Colour] = "colour",
            [K.Box] = "BoundingBox", [K.ShapeNames] = "shape key names", [K.Triangles] = "triangles",
            [K.Matrix] = "matrix", [K.Weld] = "weld", [K.Hash] = "hash/bake/bounds", [K.Simplify] = "simplify",
            [K.PropCategories] = "prop categories", [K.PropValues] = "prop values",
            [K.PropAncestors] = "parent lookup", [K.PropAncestorRead] = "parent props", [K.PropRules] = "prop rules",
            [K.ClassifyNode] = "classify: node", [K.ClassifyCategory] = "classify: category",
            [K.ClassifyMemo] = "classify: memo", [K.ClassifyRules] = "classify: rules",
            [K.Progress] = "UI pump", [K.QueueWait] = "writer wait",
        };

        /// <summary>The per-call table for one phase, most expensive first, with what no probe
        /// covered. <paramref name="phaseMs"/> is the phase's wall clock.</summary>
        public static void Report(StringBuilder sb, bool scan, double phaseMs)
        {
            int lo = scan ? 0 : ScanLast + 1, hi = scan ? ScanLast : Count - 1;
            var rows = new List<int>();
            double sum = 0;
            for (int i = lo; i <= hi; i++)
            {
                if (_calls[i] == 0 && _ticks[i] == 0) continue;
                rows.Add(i);
                sum += ExportTiming.Ms(_ticks[i]);
            }
            if (rows.Count == 0) return;
            rows.Sort((a, b) => _ticks[b].CompareTo(_ticks[a]));

            sb.AppendLine(scan ? "  ── scan, per call ──" : "  ── export read thread, per call ──");
            sb.AppendLine($"  {"call",-18}{"ms",10}{"calls",13}{"µs/call",10}{"share",7}");
            foreach (int i in rows)
            {
                double ms = ExportTiming.Ms(_ticks[i]);
                double us = _calls[i] > 0 ? ms * 1000.0 / _calls[i] : 0;
                double share = phaseMs > 0 ? 100.0 * ms / phaseMs : 0;
                sb.AppendLine($"  {Labels[(K)i],-18}{ms,10:N0}{_calls[i],13:N0}{us,10:N1}{share,6:N0}%");
            }
            double rest = phaseMs - sum;
            sb.AppendLine($"  {"not probed",-18}{Math.Max(0, rest),10:N0}{"",13}{"",10}{(phaseMs > 0 ? 100.0 * Math.Max(0, rest) / phaseMs : 0),6:N0}%");
        }
    }
}
