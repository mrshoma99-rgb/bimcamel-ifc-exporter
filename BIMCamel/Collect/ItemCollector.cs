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
        /// <summary>Hidden subtree roots skipped by the last visible-only walk.
        /// UI-thread only, like everything here; reset by the walk entry points.
        /// The pre-flight panel shows it so "I hid a discipline for viewing"
        /// does not silently become "I shipped a deliverable without it".</summary>
        public static int HiddenSkipped;

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
            HiddenSkipped = 0;
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

            HiddenSkipped = 0;
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
        /// Memoised <see cref="ItemKey"/>. The GUID-less fallback is an
        /// O(tree-depth) COM walk per call, and one operation computes keys for
        /// the same items repeatedly: set members during rule resolution, then
        /// every scope item again during preview or export. ModelItem hashes by
        /// value across separate traversals (the SampleLeaves dedup set already
        /// relies on this, shipped and verified), so a per-operation dictionary
        /// collapses those repeats. Null cache = plain computation; a miss is
        /// only ever a lost optimisation, never a wrong key.
        /// </summary>
        public static string ItemKey(ModelItem item, Dictionary<ModelItem, string>? cache)
        {
            if (cache == null) return ItemKey(item);
            if (cache.TryGetValue(item, out var k)) return k;
            return cache[item] = ItemKey(item);
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
            /// <summary>itemKey → the name of the first set that claimed it, for IfcGroup export.</summary>
            public readonly Dictionary<string, string> Group = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool Any => Class.Count > 0 || Classification.Count > 0;
        }

        /// <summary>
        /// Resolve set→class and set→classification rules in ONE pass over the sets. Earlier rules
        /// win on overlap, per assignment kind — a row that only sets a classification does not
        /// consume the class slot, so a broad "all walls → IfcWall" rule and a narrow "external
        /// walls → Uniclass code" rule compose instead of competing.
        /// </summary>
        public static SetMaps BuildSetMaps(Document doc, IEnumerable<SetRule> rules, Action<int>? onProgress = null,
            Dictionary<ModelItem, string>? keyCache = null)
        {
            // onProgress matters here: search-set rules each run a model-wide
            // FindAll, so a long rule list is a long stretch of work — without
            // ticks the UI cannot pump and Navisworks appears frozen.
            var maps = new SetMaps();
            int visited = 0;
            // Two rules on the same set — a broad class rule composing with a
            // narrow classification rule is the documented pattern — used to
            // resolve that set once per rule, and for a search set each
            // resolution is a model-wide FindAll. Resolve each distinct set
            // once; rule order (earlier wins) is unchanged.
            var resolved = new Dictionary<SelectionSet, List<ModelItem>>();
            foreach (var rule in rules)
            {
                if (rule.Set == null) continue;
                bool wantsClass = rule.ClassKey.Length > 0;
                bool wantsCode = rule.Classification.Length > 0;
                if (!wantsClass && !wantsCode) continue;
                string setName = rule.Set.DisplayName ?? "";
                if (!resolved.TryGetValue(rule.Set, out var leaves))
                    resolved[rule.Set] = leaves = GetItemsFromSet(doc, rule.Set);
                foreach (var leaf in leaves)
                {
                    var k = ItemKey(leaf, keyCache);
                    if (wantsClass && !maps.Class.ContainsKey(k)) maps.Class[k] = rule.ClassKey;
                    if (wantsCode && !maps.Classification.ContainsKey(k)) maps.Classification[k] = rule.Classification;
                    if (setName.Length > 0 && !maps.Group.ContainsKey(k)) maps.Group[k] = setName;
                    if ((++visited & 511) == 0) onProgress?.Invoke(visited);
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

            if (!includeHidden && item.IsHidden) { HiddenSkipped++; return false; }

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

        /// <summary>
        /// A BOUNDED sample of geometry leaves, for the property scan that fills the Semantics
        /// and Properties tabs.
        ///
        /// The scan only ever looks at the first ~1000 items (PropertyHarvester.ScanCategoryParams
        /// stops there), but it used to be handed the result of a FULL tree walk — so a federated
        /// model with half a million nodes was walked in its entirety, on the UI thread, to sample
        /// 0.2% of it. That is what made "Detect from model" appear to hang Navisworks.
        ///
        /// This stops as soon as it has enough. The budget is split across the loaded models and
        /// then topped up, so the sample spans disciplines instead of being 1000 architectural
        /// elements from whichever model happens to load first — the roles it auto-fills are only
        /// as good as the spread of properties it saw.
        /// </summary>
        public static List<ModelItem> SampleLeaves(Document doc, int cap, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            if (doc == null || cap <= 0) return result;

            var roots = new List<ModelItem>();
            try { foreach (var r in doc.Models.RootItems) roots.Add(r); } catch { return result; }
            if (roots.Count == 0) return result;

            int visited = 0;
            int perModel = Math.Max(1, cap / roots.Count);
            // Pass 2 re-walks, so without this the same node would be sampled twice.
            var seen = new HashSet<ModelItem>();

            // Pass 1: an even slice from each model.
            foreach (var root in roots)
            {
                if (result.Count >= cap) break;
                TakeFrom(root, result, seen, Math.Min(result.Count + perModel, cap), onProgress, ref visited);
            }
            // Pass 2: top up from whichever models still have more to give, so a federation where
            // one model holds nearly everything still fills the sample. Only runs when pass 1
            // came up short — i.e. when the models are small and re-walking them is cheap.
            foreach (var root in roots)
            {
                if (result.Count >= cap) break;
                TakeFrom(root, result, seen, cap, onProgress, ref visited);
            }
            return result;
        }

        /// <summary>
        /// Same shape as <see cref="CollectFrom"/> but abandons the walk the moment
        /// <paramref name="limit"/> is reached. Sampling does not need the branch-geometry
        /// recovery rule to be exact — it needs to stop early — so this deliberately does not
        /// count recovered branches into the export diagnostics.
        /// </summary>
        private static bool TakeFrom(ModelItem item, List<ModelItem> result, HashSet<ModelItem> seen, int limit, Action<int>? onProgress, ref int visited)
        {
            if (result.Count >= limit) return false;
            visited++;
            if ((visited & 0xFF) == 0) onProgress?.Invoke(visited);
            if (item.IsHidden) return false;

            bool any = false;
            foreach (var child in item.Children)
            {
                if (result.Count >= limit) return any;
                if (TakeFrom(child, result, seen, limit, onProgress, ref visited)) any = true;
            }

            if (any) return true;
            if (!item.HasGeometry) return false;
            if (result.Count >= limit) return false;
            if (!seen.Add(item)) return true;   // already sampled on an earlier pass
            result.Add(item);
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
