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

                foreach (InwOaPath3 path in sel.Paths())
                {
                    ModelItem? owner;
                    try { owner = ComApiBridge.ToModelItem(path); }
                    catch { continue; }
                    if (owner == null) continue;
                    if (!map.TryGetValue(owner, out var lst)) { lst = new List<InwOaPath3>(); map[owner] = lst; }
                    lst.Add(path);
                }
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

        /// <summary>The original per-item conversion, for items the chunk map did not cover.</summary>
        public static IEnumerable<InwOaPath3> PathsFor(ModelItem item)
        {
            long t = ExportTiming.Now;
            InwOpSelection sel;
            try
            {
                var coll = new ModelItemCollection { item };
                sel = ComApiBridge.ToInwOpSelection(coll);
                ExportTiming.ComConverts++;
            }
            finally { ExportTiming.ComConvertTicks += ExportTiming.Now - t; }

            foreach (InwOaPath3 path in sel.Paths()) yield return path;
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
