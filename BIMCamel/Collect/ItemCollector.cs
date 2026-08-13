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

            var box = TryGetSectionBoxBounds(doc);
            if (box == null)
                throw new InvalidOperationException(
                    "No active section box found. Enable sectioning in Navisworks " +
                    "(Viewpoint → Enable Sectioning, Box mode — axis-aligned section planes " +
                    "work too), or pick a different scope.");

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

        /// <summary>One mapping-grid row resolved to its set: what class and classification it assigns.</summary>
        public readonly struct SetRule
        {
            public readonly SelectionSet Set;
            public readonly string ClassKey;
            public readonly string Classification;
            public SetRule(SelectionSet set, string classKey, string classification)
            { Set = set; ClassKey = classKey ?? ""; Classification = classification ?? ""; }
        }

        /// <summary>item-key → assigned value, for both things a set rule can assign.</summary>
        public sealed class SetMaps
        {
            public readonly Dictionary<string, string> Class = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Classification = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool Any => Class.Count > 0 || Classification.Count > 0;
        }

        /// <summary>
        /// Resolve set→class and set→classification rules in ONE pass over the sets. Earlier rules
        /// win on overlap, per assignment kind — a row that only sets a classification does not
        /// consume the class slot, so a broad "all walls → IfcWall" rule and a narrow "external
        /// walls → Uniclass code" rule compose instead of competing.
        /// </summary>
        public static SetMaps BuildSetMaps(Document doc, IEnumerable<SetRule> rules)
        {
            var maps = new SetMaps();
            foreach (var rule in rules)
            {
                if (rule.Set == null) continue;
                bool wantsClass = rule.ClassKey.Length > 0;
                bool wantsCode = rule.Classification.Length > 0;
                if (!wantsClass && !wantsCode) continue;
                foreach (var leaf in GetItemsFromSet(doc, rule.Set))
                {
                    var k = ItemKey(leaf);
                    if (wantsClass && !maps.Class.ContainsKey(k)) maps.Class[k] = rule.ClassKey;
                    if (wantsCode && !maps.Classification.ContainsKey(k)) maps.Classification[k] = rule.Classification;
                }
            }
            return maps;
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
                CollectFrom(item, result, includeHidden, onProgress, ref visited);
        }

        /// <summary>
        /// Collects the geometry-bearing nodes of one subtree; returns true when the subtree
        /// contributed anything.
        ///
        /// Geometry is NOT always attached to a childless node. SolidWorks / Inventor parts
        /// routinely carry their mesh on the part node while hanging reference planes, coordinate
        /// systems, annotations or solid bodies underneath it. The previous rule — recurse whenever
        /// the node has children, and only take it when childless — therefore walked into those
        /// children, found no geometry, and lost the entire part.
        ///
        /// We now fall back to a node's own geometry whenever nothing beneath it produced any.
        /// That strictly adds elements relative to the old behaviour and can never double-export,
        /// because a node is only taken when its descendants contributed nothing — so a multibody
        /// part still exports as its separate child bodies, exactly as before.
        /// </summary>
        private static bool CollectFrom(ModelItem item, List<ModelItem> result, bool includeHidden, Action<int>? onProgress, ref int visited)
        {
            visited++;
            if ((visited & 0x3FF) == 0) onProgress?.Invoke(visited);

            if (!includeHidden && item.IsHidden) return false;

            bool any = false, hadChildren = false;
            foreach (var child in item.Children)
            {
                hadChildren = true;
                if (CollectFrom(child, result, includeHidden, onProgress, ref visited)) any = true;
            }

            if (any) return true;              // descendants covered this subtree's geometry
            if (!item.HasGeometry) return false;

            result.Add(item);
            if (hadChildren) Geometry.ExportIssues.BranchGeometryRecovered++;
            return true;
        }

        // ── Section box resolution ───────────────────────────────────────────────

        /// <summary>
        /// The active section box as an axis-aligned world box, or null when sectioning is off.
        /// Primary source is the .NET API's <c>View.GetClippingPlanes()</c> JSON — the documented
        /// cross-year surface, which also carries the real Box-mode section box (parsed by
        /// <see cref="SectionBoxJson"/>). The legacy COM <c>CurrentSectionView</c> route is kept
        /// only as a fallback: it throws a COMException on Navisworks 2026 (issue #24) and never
        /// saw Box-mode sections at all.
        /// </summary>
        private static SectionBox? TryGetSectionBoxBounds(Document doc)
            => TryGetSectionBoxFromDotNet(doc) ?? TryGetSectionBoxFromCom();

        private static SectionBox? TryGetSectionBoxFromDotNet(Document doc)
        {
            try
            {
                var json = doc.ActiveView?.GetClippingPlanes();
                return string.IsNullOrWhiteSpace(json) ? null : SectionBoxJson.Parse(json!);
            }
            catch
            {
                return null;
            }
        }

        private static SectionBox? TryGetSectionBoxFromCom()
        {
            try
            {
                // Throws COMException on Navisworks 2026 — only reached when the .NET JSON route
                // above yielded nothing, and the whole method is exception-safe.
                var sectionView = ComApiBridge.State.CurrentSectionView as InwOpAnonView;
                var clipPlanes = sectionView?.ClippingPlanes();
                if (clipPlanes == null) return null;

                var acc = new SectionBoxJson.AxisBoundsAccumulator();
                foreach (InwOaClipPlane plane in clipPlanes)
                {
                    if (!plane.Enabled) continue;
                    var p = plane.Plane;
                    var n = p.GetNormal();
                    acc.AddPlane(n.data1, n.data2, n.data3, p.distance());
                }
                return acc.ToBox();
            }
            catch
            {
                return null;
            }
        }


        private static bool OverlapsBox(ModelItem item, SectionBox b)
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
