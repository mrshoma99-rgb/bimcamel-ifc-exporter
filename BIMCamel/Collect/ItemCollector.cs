using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace BIMCamel.Collect
{
    /// <summary>
    /// Walks the model tree to collect leaf-level items that carry geometry, and resolves the
    /// active section box. Leaf-walk + section-box logic are ported from the proven
    /// NavisworksExporter.ElementCollector.
    ///
    /// IMPORTANT: call from the Navisworks main (UI) thread only — the read API is
    /// single-threaded (the STA constraint that caps the geometry hot path, plan §5).
    /// </summary>
    public static class ItemCollector
    {
        public static List<ModelItem> GetAllLeafItemsWithGeometry(Document doc, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            int visited = 0;
            if (doc != null)
                CollectLeaves(doc.Models.RootItems, result, includeHidden: true, onProgress, ref visited);
            return result;
        }

        public static List<ModelItem> GetVisibleLeafItemsWithGeometry(Document doc, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            int visited = 0;
            if (doc != null)
                CollectLeaves(doc.Models.RootItems, result, includeHidden: false, onProgress, ref visited);
            return result;
        }

        /// <summary>Resolve any selection of items down to their geometry leaves.</summary>
        public static List<ModelItem> ResolveLeaves(IEnumerable<ModelItem> items, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            int visited = 0;
            CollectLeaves(items, result, includeHidden: true, onProgress, ref visited);
            return result;
        }

        /// <summary>
        /// Visible geometry leaves whose bounding box overlaps the active section box.
        /// Throws <see cref="InvalidOperationException"/> if no section box is active.
        /// </summary>
        public static List<ModelItem> GetItemsInSectionBox(Document doc, Action<int>? onProgress = null)
        {
            if (doc == null) return new List<ModelItem>();

            var box = TryGetSectionBoxBounds();
            if (box == null)
                throw new InvalidOperationException(
                    "No active section box found. Enable a section box in Navisworks, " +
                    "or pick a different scope.");

            var leaves = new List<ModelItem>();
            int visited = 0;
            CollectLeaves(doc.Models.RootItems, leaves, includeHidden: false, onProgress, ref visited);
            return leaves.Where(i => OverlapsBox(i, box)).ToList();
        }

        /// <summary>
        /// All saved selection AND search sets in the document, recursing folders. A search set is a
        /// <see cref="SelectionSet"/> with <c>Search != null</c>; both kinds live in
        /// <c>doc.SelectionSets</c> but are often organised inside folders, which the previous
        /// top-level-only scan skipped — so search sets never appeared in the mapping dropdown.
        /// </summary>
        public static List<SelectionSet> GetSelectionSets(Document doc)
        {
            var result = new List<SelectionSet>();
            try { CollectSets(doc.SelectionSets.ToSavedItemCollection(), result); }
            catch { /* no sets */ }
            return result;
        }

        private static void CollectSets(SavedItemCollection items, List<SelectionSet> result)
        {
            foreach (SavedItem si in items)
            {
                if (si is SelectionSet ss) result.Add(ss);
                else if (si is FolderItem fi && fi.Children != null) CollectSets(fi.Children, result);
            }
        }

        /// <summary>Geometry leaves resolved from a saved selection or search set.</summary>
        public static List<ModelItem> GetItemsFromSet(Document doc, SelectionSet set, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            try
            {
                ModelItemCollection? mic =
                    set.HasExplicitModelItems ? set.ExplicitModelItems :
                    set.Search != null ? set.Search.FindAll(doc, false) : null;
                int visited = 0;
                if (mic != null) CollectLeaves(mic, result, includeHidden: true, onProgress, ref visited);
            }
            catch { /* unresolved set */ }
            return result;
        }

        /// <summary>
        /// Stable per-item key for matching set membership to scope items across separate
        /// traversals: InstanceGuid when present, else a tree path (display names). Must be
        /// computed identically wherever it's used.
        /// </summary>
        public static string ItemKey(ModelItem item)
        {
            try
            {
                if (item.InstanceGuid != Guid.Empty) return "G:" + item.InstanceGuid.ToString();
                var parts = new List<string>();
                foreach (var a in item.AncestorsAndSelf)
                    parts.Add(a.DisplayName ?? a.ClassName ?? "");
                return "P:" + string.Join("/", parts);
            }
            catch { return "P:" + (item.DisplayName ?? ""); }
        }

        /// <summary>
        /// Build a map of item-key → IFC class key from set→class rules. Resolves each set once;
        /// earlier rules win on overlap.
        /// </summary>
        public static Dictionary<string, string> BuildClassMap(Document doc, IEnumerable<(SelectionSet set, string classKey)> rules)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (set, classKey) in rules)
            {
                if (set == null || string.IsNullOrEmpty(classKey)) continue;
                foreach (var leaf in GetItemsFromSet(doc, set))
                {
                    var k = ItemKey(leaf);
                    if (!map.ContainsKey(k)) map[k] = classKey;
                }
            }
            return map;
        }

        /// <summary>
        /// World-space min corner of the scope, from each item's <see cref="ModelItem.BoundingBox()"/>
        /// (cheap — no triangle reads). Returned in the model's own units; callers scale to metres.
        /// Used by the exporter for the base-point offset so we never traverse all vertices just to
        /// find the origin (v3 Part A3). Returns (0,0,0) if nothing has a bounding box.
        /// </summary>
        public static (double x, double y, double z) ScopeMinCorner(IEnumerable<ModelItem> items, Action<int>? onProgress = null)
        {
            double mnX = double.MaxValue, mnY = double.MaxValue, mnZ = double.MaxValue;
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    var bb = item.BoundingBox();
                    if (bb != null)
                    {
                        if (bb.Min.X < mnX) mnX = bb.Min.X;
                        if (bb.Min.Y < mnY) mnY = bb.Min.Y;
                        if (bb.Min.Z < mnZ) mnZ = bb.Min.Z;
                    }
                }
                catch { /* skip odd nodes */ }
                if ((++n & 0x3FF) == 0) onProgress?.Invoke(n);
            }
            if (mnX == double.MaxValue) return (0, 0, 0);
            return (mnX, mnY, mnZ);
        }

        // Walks the model tree on the UI thread (the API is STA). On large models this is slow, so
        // it reports nodes visited every 1024 so the caller can pump the message loop and the UI
        // does not appear frozen (v3 follow-up: the pre-dialog freeze was this walk with no feedback).
        private static void CollectLeaves(IEnumerable<ModelItem> items, List<ModelItem> result, bool includeHidden, Action<int>? onProgress, ref int visited)
        {
            foreach (var item in items)
            {
                visited++;
                if ((visited & 0x3FF) == 0) onProgress?.Invoke(visited);

                if (!includeHidden && item.IsHidden) continue;

                if (item.Children.Any())
                    CollectLeaves(item.Children, result, includeHidden, onProgress, ref visited);
                else if (item.HasGeometry)
                    result.Add(item);
            }
        }

        // ── Section box (axis-aligned) reconstructed from COM clipping planes ────────
        private sealed class Box
        {
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        }

        private static Box? TryGetSectionBoxBounds()
        {
            try
            {
                var sectionView = ComApiBridge.State.CurrentSectionView as InwOpAnonView;
                var clipPlanes = sectionView?.ClippingPlanes();
                if (clipPlanes == null) return null;

                double xMin = double.MinValue, yMin = double.MinValue, zMin = double.MinValue;
                double xMax = double.MaxValue, yMax = double.MaxValue, zMax = double.MaxValue;
                int found = 0;
                const double tol = 0.001;

                foreach (InwOaClipPlane plane in clipPlanes)
                {
                    if (!plane.Enabled) continue;
                    var p = plane.Plane;
                    var n = p.GetNormal();
                    double dist = p.distance();
                    double nx = n.data1, ny = n.data2, nz = n.data3;

                    if (Math.Abs(nx - 1) < tol && Math.Abs(ny) < tol && Math.Abs(nz) < tol) { xMin = Math.Max(xMin, -dist); found++; }
                    else if (Math.Abs(nx + 1) < tol && Math.Abs(ny) < tol && Math.Abs(nz) < tol) { xMax = Math.Min(xMax, dist); found++; }
                    else if (Math.Abs(ny - 1) < tol && Math.Abs(nx) < tol && Math.Abs(nz) < tol) { yMin = Math.Max(yMin, -dist); found++; }
                    else if (Math.Abs(ny + 1) < tol && Math.Abs(nx) < tol && Math.Abs(nz) < tol) { yMax = Math.Min(yMax, dist); found++; }
                    else if (Math.Abs(nz - 1) < tol && Math.Abs(nx) < tol && Math.Abs(ny) < tol) { zMin = Math.Max(zMin, -dist); found++; }
                    else if (Math.Abs(nz + 1) < tol && Math.Abs(nx) < tol && Math.Abs(ny) < tol) { zMax = Math.Min(zMax, dist); found++; }
                }

                if (found < 2) return null;

                if (xMin == double.MinValue) xMin = -1e10;
                if (yMin == double.MinValue) yMin = -1e10;
                if (zMin == double.MinValue) zMin = -1e10;
                if (xMax == double.MaxValue) xMax = 1e10;
                if (yMax == double.MaxValue) yMax = 1e10;
                if (zMax == double.MaxValue) zMax = 1e10;

                return new Box { MinX = xMin, MinY = yMin, MinZ = zMin, MaxX = xMax, MaxY = yMax, MaxZ = zMax };
            }
            catch
            {
                return null;
            }
        }

        private static bool OverlapsBox(ModelItem item, Box b)
        {
            try
            {
                var bb = item.BoundingBox();
                if (bb == null) return false;
                return bb.Min.X <= b.MaxX && bb.Max.X >= b.MinX &&
                       bb.Min.Y <= b.MaxY && bb.Max.Y >= b.MinY &&
                       bb.Min.Z <= b.MaxZ && bb.Max.Z >= b.MinZ;
            }
            catch { return false; }
        }
    }
}
