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

        // ── min corner gathered BY the walk (v5 S1) ───────────────────────────────
        // ScopeMinCorner used to be a second full pass calling BoundingBox() on every collected
        // leaf. The walk already visits each of those leaves, so it accumulates the corner as it
        // goes and the second pass disappears. Statics for the same reason HiddenSkipped is one:
        // UI-thread only, set by the walk, read by the caller immediately afterwards.
        //
        // Valid ONLY when the returned list is exactly what the walk collected. A scope that
        // post-filters the walk's result (the section box) or unions several walks (batch) must
        // clear the flag, because the corner would then cover items the export will not write.
        public static bool MinCornerValid;
        private static double _mnX, _mnY, _mnZ;

        /// <summary>Min corner of the last walk, in model units. Only meaningful while
        /// <see cref="MinCornerValid"/> is true.</summary>
        public static (double x, double y, double z) LastMinCorner =>
            _mnX == double.MaxValue ? (0, 0, 0) : (_mnX, _mnY, _mnZ);

        /// <summary>Carries the walk's mutable state so the recursion keeps a short signature.</summary>
        private sealed class Walk
        {
            public readonly List<ModelItem> Result;
            public readonly bool IncludeHidden;
            public readonly Action<int>? OnProgress;
            public readonly bool WantMin;
            public int Visited;
            public Walk(List<ModelItem> result, bool includeHidden, Action<int>? onProgress, bool wantMin)
            { Result = result; IncludeHidden = includeHidden; OnProgress = onProgress; WantMin = wantMin; }
        }

        private static void BeginWalk() { MinCornerValid = true; _mnX = _mnY = _mnZ = double.MaxValue; }

        /// <summary>Publish what the walk cost, for the scan decomposition on the report (v5 S4).</summary>
        private static void EndWalk(Walk w) { Geometry.ExportTiming.NodesVisited += w.Visited; }

        public static List<ModelItem> GetAllLeafItemsWithGeometry(Document doc, Action<int>? onProgress = null, bool wantMinCorner = false)
        {
            BeginWalk();
            var result = new List<ModelItem>();
            if (doc != null)
            {
                var w = new Walk(result, true, onProgress, wantMinCorner);
                CollectLeaves(doc.Models.RootItems, w);
                EndWalk(w);
            }
            return result;
        }

        public static List<ModelItem> GetVisibleLeafItemsWithGeometry(Document doc, Action<int>? onProgress = null, bool wantMinCorner = false)
        {
            HiddenSkipped = 0;
            BeginWalk();
            var result = new List<ModelItem>();
            if (doc != null)
            {
                var w = new Walk(result, false, onProgress, wantMinCorner);
                CollectLeaves(doc.Models.RootItems, w);
                EndWalk(w);
            }
            return result;
        }

        /// <summary>Resolve any selection of items down to their geometry leaves.</summary>
        public static List<ModelItem> ResolveLeaves(IEnumerable<ModelItem> items, Action<int>? onProgress = null, bool wantMinCorner = false)
        {
            BeginWalk();
            var result = new List<ModelItem>();
            var w = new Walk(result, true, onProgress, wantMinCorner);
            CollectLeaves(items, w);
            EndWalk(w);
            return result;
        }

        /// <summary>
        /// Geometry leaves, but stopping as soon as <paramref name="cap"/> of them are in hand
        /// (v5 S3). The mapping property scan reads a sample of ~1,000 items and never needs the
        /// full leaf list — it used to pay a whole-model traversal to obtain one.
        ///
        /// SPREAD, and its honest limit. Filling the cap out of the first model of a federation
        /// would describe one discipline, and the roles this proposes are only as good as the
        /// spread of properties it saw — the same reason <c>Spread</c> strides instead of taking a
        /// head. So the budget is divided fairly across the roots, and again across each root's own
        /// top-level children (levels, systems, zones — whatever the source grouped by), so the
        /// sample lands in several places in each model.
        ///
        /// It is still a HEAD sample within each of those branches, which the full walk was not.
        /// That is the price of not traversing the model: a branch whose property schema changes
        /// deep inside it can be described by its first few elements. For proposing role mappings —
        /// which the user reviews before exporting — that trade is worth it; nothing here decides
        /// what gets exported.
        /// </summary>
        public static List<ModelItem> CollectSample(Document doc, int cap, Action<int>? onProgress = null)
        {
            var result = new List<ModelItem>();
            if (doc == null || cap <= 0) return result;
            HiddenSkipped = 0;
            BeginWalk();
            MinCornerValid = false;   // a sample is not the scope; never let it set a base point

            // The branches the budget is split across: each root's children, or the root itself
            // when it has none. Disjoint subtrees, so the results can never duplicate and no
            // dedup set (and no O(n^2) ModelItem comparison) is needed.
            var branches = new List<ModelItem>();
            try
            {
                foreach (var root in doc.Models.RootItems)
                {
                    int before = branches.Count;
                    foreach (var child in root.Children) branches.Add(child);
                    if (branches.Count == before) branches.Add(root);
                }
            }
            catch { /* odd tree — fall back to whatever we gathered */ }
            if (branches.Count == 0) return result;

            // Each branch is walked exactly once, for its own share of the budget. Branches with
            // fewer leaves than their share simply leave the sample short of the cap — deliberately
            // not topped up from the others: re-walking a drained branch would re-collect the very
            // same leading leaves, and a sample of 800 well-spread items beats 1,000 with 200
            // duplicates in it.
            var w = new Walk(result, false, onProgress, false);
            int share = Math.Max(1, cap / branches.Count);
            foreach (var b in branches)
            {
                if (result.Count >= cap) break;
                CollectFromCapped(b, w, Math.Min(cap, result.Count + share));
            }
            EndWalk(w);
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
            var boxWalk = new Walk(leaves, false, onProgress, false);
            CollectLeaves(doc.Models.RootItems, boxWalk);
            EndWalk(boxWalk);
            // The result is a FILTERED subset of what the walk collected, so the walk's min corner
            // would cover items this scope excludes. Caller must fall back to ScopeMinCorner.
            MinCornerValid = false;
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
        public static List<ModelItem> GetItemsFromSet(Document doc, SelectionSet set, Action<int>? onProgress = null, bool wantMinCorner = false)
        {
            var result = new List<ModelItem>();
            BeginWalk();
            try
            {
                ModelItemCollection? mic =
                    set.HasExplicitModelItems ? set.ExplicitModelItems :
                    set.Search != null ? set.Search.FindAll(doc, false) : null;
                if (mic != null)
                {
                    var w = new Walk(result, true, onProgress, wantMinCorner);
                    CollectLeaves(mic, w);
                    EndWalk(w);
                }
            }
            // A Stop request travels out through onProgress, and this catch-all
            // would have turned it into "this set resolved to nothing" — silently
            // narrowing the export instead of abandoning it.
            catch (OperationCanceledException) { throw; }
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
        /// every scope item again during preview or export. ModelItem overrides
        /// Equals/GetHashCode, so it hashes by value across separate traversals
        /// (the batch-scope union dedup relies on the same property), and a
        /// per-operation dictionary collapses those repeats. Null cache = plain computation; a miss is
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
                    // Ticking through the resolution too, not just the loop below:
                    // for a search set the FindAll and the walk of its results are
                    // the slow half, and passing no callback here left the UI
                    // unpumped — and unstoppable — for all of it.
                    resolved[rule.Set] = leaves = GetItemsFromSet(doc, rule.Set, onProgress);
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
        private static void CollectLeaves(IEnumerable<ModelItem> items, Walk w)
        {
            foreach (var item in items)
                CollectFrom(item, w);
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
        private static bool CollectFrom(ModelItem item, Walk w)
        {
            w.Visited++;
            if ((w.Visited & 0x3FF) == 0) w.OnProgress?.Invoke(w.Visited);

            if (!w.IncludeHidden && item.IsHidden) { HiddenSkipped++; return false; }

            bool any = false, hadChildren = false;
            foreach (var child in item.Children)
            {
                hadChildren = true;
                if (CollectFrom(child, w)) any = true;
            }

            if (any) return true;              // descendants covered this subtree's geometry
            if (!item.HasGeometry) return false;

            Take(item, w);
            if (hadChildren) Geometry.ExportIssues.BranchGeometryRecovered++;
            return true;
        }

        /// <summary>As <see cref="CollectFrom"/>, but abandons the subtree once the result list has
        /// reached <paramref name="limit"/> items (v5 S3 sampling).</summary>
        private static bool CollectFromCapped(ModelItem item, Walk w, int limit)
        {
            if (w.Result.Count >= limit) return false;

            w.Visited++;
            if ((w.Visited & 0x3FF) == 0) w.OnProgress?.Invoke(w.Visited);

            if (!w.IncludeHidden && item.IsHidden) { HiddenSkipped++; return false; }

            bool any = false, hadChildren = false;
            foreach (var child in item.Children)
            {
                hadChildren = true;
                if (CollectFromCapped(child, w, limit)) any = true;
                if (w.Result.Count >= limit) return true;
            }

            if (any) return true;
            if (!item.HasGeometry) return false;

            Take(item, w);
            if (hadChildren) Geometry.ExportIssues.BranchGeometryRecovered++;
            return true;
        }

        /// <summary>
        /// Accept one geometry leaf, folding its bounding box into the running min corner when the
        /// caller asked for one (v5 S1). This is the pass that <c>ScopeMinCorner</c> used to make
        /// separately over the finished list — same BoundingBox() call, same items, same result,
        /// one traversal instead of two.
        /// </summary>
        private static void Take(ModelItem item, Walk w)
        {
            w.Result.Add(item);
            if (!w.WantMin) return;
            try
            {
                var bb = item.BoundingBox();
                if (bb == null) return;
                if (bb.Min.X < _mnX) _mnX = bb.Min.X;
                if (bb.Min.Y < _mnY) _mnY = bb.Min.Y;
                if (bb.Min.Z < _mnZ) _mnZ = bb.Min.Z;
            }
            catch { /* skip odd nodes, exactly as ScopeMinCorner did */ }
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
