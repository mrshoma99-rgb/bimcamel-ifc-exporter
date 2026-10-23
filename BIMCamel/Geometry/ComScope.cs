using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Converts model items to their COM geometry paths in BOUNDED CHUNKS instead of one at a time
    /// (v5 E3; v4 called this S2 and left it unbuilt).
    ///
    /// Both extractors used to do, per element:
    /// <code>
    ///     var coll = new ModelItemCollection { item };
    ///     InwOpSelection comSel = ComApiBridge.ToInwOpSelection(coll);
    /// </code>
    /// — 674,641 bridge conversions on the prova model, each with its own setup cost, to read one
    /// item. The report's "COM convert" line exists precisely to measure this.
    ///
    /// WHY CHUNKS AND NOT ONE BIG CONVERSION. v4 said "convert the whole scope once", which prices
    /// the time and not the memory: a single COM selection over 674k items is a large allocation
    /// inside Navisworks, on an export that already watches its peak heap. A few thousand at a time
    /// removes essentially all of the per-item cost while keeping the working set flat.
    ///
    /// WHY THE FALLBACK EXISTS. <c>Paths()</c> is not documented to come back in the order the
    /// collection went in, so paths are mapped back to their <see cref="ModelItem"/> rather than
    /// paired positionally. If that map-back ever comes up empty for an item — a shape of model we
    /// have not seen, an API year that behaves differently — that item is converted on its own,
    /// exactly as before. The optimisation can therefore cost time in a bad case, but it can never
    /// silently drop an element from the export, which is the only outcome that would matter.
    ///
    /// UI-thread only, like everything that touches the read API.
    /// </summary>
    internal static class ComScope
    {
        /// <summary>Items per COM conversion. Large enough that the per-item setup disappears into
        /// the noise, small enough that the selection never becomes the memory story.</summary>
        public const int ChunkSize = 2048;

        /// <summary>Splits the scope into chunks without materialising the whole thing.</summary>
        public static IEnumerable<List<ModelItem>> Chunks(IEnumerable<ModelItem> items, int size)
        {
            var chunk = new List<ModelItem>(size);
            foreach (var item in items)
            {
                chunk.Add(item);
                if (chunk.Count < size) continue;
                yield return chunk;
                chunk = new List<ModelItem>(size);
            }
            if (chunk.Count > 0) yield return chunk;
        }

        /// <summary>
        /// One COM conversion for the whole chunk, indexed by the item each path belongs to.
        /// Returns an empty map when the bulk conversion is unusable — every item then falls back.
        /// </summary>
        public static Dictionary<ModelItem, List<InwOaPath3>> Convert(List<ModelItem> chunk)
        {
            var map = new Dictionary<ModelItem, List<InwOaPath3>>();
            long t = ExportTiming.Now;
            try
            {
                var coll = new ModelItemCollection();
                foreach (var item in chunk) coll.Add(item);
                InwOpSelection sel = ComApiBridge.ToInwOpSelection(coll);
                ExportTiming.ComConverts++;
                long tp = Probe.Lap(Probe.K.SelConvert, t);

                // Timed on its own: one bridge call per path, so on a big chunk this — not the
                // conversion above — may be the bulk of the line. Stepped by hand so the probes
                // split producing each path from mapping it back.
                long tm = ExportTiming.Now;
                try
                {
                    var e = sel.Paths().GetEnumerator();
                    tp = Probe.Lap(Probe.K.SelPaths, tp);
                    while (true)
                    {
                        bool more = e.MoveNext();
                        tp = Probe.Lap(Probe.K.SelPaths, tp);
                        if (!more) break;
                        var path = (InwOaPath3)e.Current;
                        ModelItem? owner;
                        try { owner = ComApiBridge.ToModelItem(path); }
                        catch { tp = Probe.Lap(Probe.K.SelToItem, tp); continue; }
                        tp = Probe.Lap(Probe.K.SelToItem, tp);
                        if (owner == null) continue;
                        if (!map.TryGetValue(owner, out var lst)) { lst = new List<InwOaPath3>(); map[owner] = lst; }
                        lst.Add(path);
                        tp = Probe.Lap(Probe.K.SelMap, tp);
                    }
                }
                finally { ExportTiming.ComMapTicks += ExportTiming.Now - tm; }
            }
            catch
            {
                // The bulk conversion failed outright. Not fatal and not even unusual on an odd
                // model — every item in this chunk simply takes the old per-item path.
                map.Clear();
            }
            finally { ExportTiming.ComConvertTicks += ExportTiming.Now - t; }
            return map;
        }

        /// <summary>
        /// One chunk's paths, item by item, the cheapest way that works on this model.
        ///
        /// The chunk conversion above is cheap one way and dear the other. Turning 2,048 items
        /// into a COM selection costs under a millisecond, but mapping each path back to its item
        /// (<c>ToModelItem</c>) cost about 500 µs a path on a large plant model: 97 s of a 520 s
        /// export. So each item is now converted straight to its own path
        /// (<c>ToInwOaPath</c>, the cheap direction), with no map back at all.
        ///
        /// The direct call is trusted only after it has proved itself: the first
        /// <see cref="TrialCalls"/> calls of an export are timed, and if they fail or average more
        /// than <see cref="MaxDirectMicros"/>, the rest of the export goes back to chunk
        /// conversion. An item the direct call cannot convert takes the chunk route too, so no
        /// element can lose its geometry to this.
        /// </summary>
        internal sealed class ChunkPaths
        {
            private readonly List<ModelItem> _chunk;
            private Dictionary<ModelItem, List<InwOaPath3>>? _map;
            private int _mapFrom = int.MaxValue;
            public ChunkPaths(List<ModelItem> chunk) { _chunk = chunk; }

            /// <summary>The paths of the chunk's <paramref name="i"/>-th item.</summary>
            public List<InwOaPath3> For(int i)
            {
                var item = _chunk[i];
                if (Direct && _map == null)
                {
                    var p = DirectPath(item);
                    if (p != null) return new List<InwOaPath3>(1) { p };
                }
                // The chunk route, for the rest of this chunk only.
                if (_map == null || i < _mapFrom)
                {
                    _mapFrom = i;
                    _map = Convert(i == 0 ? _chunk : _chunk.GetRange(i, _chunk.Count - i));
                }
                long tl = Probe.Now;
                bool mapped = _map.TryGetValue(item, out var lst);
                Probe.Add(Probe.K.SelMap, tl);
                return mapped ? lst! : PathsFor(item);
            }
        }

        public const int TrialCalls = 256;
        public const double MaxDirectMicros = 150;

        /// <summary>Whether this export converts items one by one (see <see cref="ChunkPaths"/>).</summary>
        public static bool Direct = true;
        private static long _trialTicks;
        private static int _trialCalls;

        /// <summary>Start of an export: give the direct call its trial again.</summary>
        public static void ResetMode() { Direct = true; _trialTicks = 0; _trialCalls = 0; }

        private static InwOaPath3? DirectPath(ModelItem item)
        {
            long t = Probe.Now;
            InwOaPath3? p;
            try { p = ComApiBridge.ToInwOaPath(item) as InwOaPath3; }
            catch { p = null; }
            long dt = Probe.Now - t;
            Probe.Add(Probe.K.SelDirect, t);
            ExportTiming.ComConvertTicks += dt;
            if (_trialCalls < TrialCalls)
            {
                _trialCalls++; _trialTicks += dt;
                if (p == null || (_trialCalls == TrialCalls && ExportTiming.Ms(_trialTicks) * 1000.0 / TrialCalls > MaxDirectMicros))
                    Direct = false;
            }
            return p;
        }

        /// <summary>The original per-item conversion, for items the chunk map did not cover.</summary>
        public static List<InwOaPath3> PathsFor(ModelItem item)
        {
            long t = ExportTiming.Now;
            var paths = new List<InwOaPath3>(1);
            try
            {
                var coll = new ModelItemCollection { item };
                var sel = ComApiBridge.ToInwOpSelection(coll);
                ExportTiming.ComConverts++;
                // Gathered here rather than yielded, so the probe covers the whole conversion.
                foreach (InwOaPath3 path in sel.Paths()) paths.Add(path);
            }
            finally { ExportTiming.ComConvertTicks += ExportTiming.Now - t; Probe.Add(Probe.K.SelSingle, t); }
            return paths;
        }

        /// <summary>
        /// The fragments that belong to THIS path. <c>InwOaPath3.Fragments()</c> lists the
        /// fragments of the path's geometry node, and when that node is instanced (one geometry
        /// placed at many paths — <c>GetNextInstance</c>) the list holds every instance's fragment,
        /// each with its own path and world matrix. Taking them all wrote every instance into every
        /// element: in the ice stadium sample one seat-row element carried all 561 rows, 95 m
        /// across, and the file held each of those triangles 561 times over.
        ///
        /// A fragment is ours when its <c>path.ArrayData</c> equals the path's. If the comparison
        /// cannot be made, or keeps nothing although there were fragments, all of them are returned
        /// as before: an element must never lose its geometry to this filter.
        /// </summary>
        public static List<InwOaFragment3> OwnFragments(InwOaPath3 path)
        {
            var all = new List<InwOaFragment3>();
            foreach (InwOaFragment3 frag in path.Fragments()) all.Add(frag);
            if (all.Count < 2) return all;   // nothing to choose between

            int[]? mine = PathKey(path.ArrayData);
            if (mine == null) return all;
            var own = new List<InwOaFragment3>(all.Count);
            foreach (var frag in all)
            {
                int[]? theirs;
                try { theirs = PathKey(frag.path.ArrayData); }
                catch { return all; }
                if (theirs == null) return all;
                if (Same(mine, theirs)) own.Add(frag);
            }
            if (own.Count == 0) { ExportTiming.OwnFragmentFallbacks++; return all; }
            ExportTiming.ForeignFragmentsSkipped += all.Count - own.Count;
            return own;
        }

        private static int[]? PathKey(object? arrayData)
        {
            if (arrayData is not Array a) return null;
            var k = new int[a.Length];
            int i = 0;
            foreach (var o in a) k[i++] = System.Convert.ToInt32(o);
            return k;
        }

        private static bool Same(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
