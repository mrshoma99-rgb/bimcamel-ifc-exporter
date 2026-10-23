using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using P = BIMCamel.Geometry.Probe;

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
            /// <summary>
            /// Subtrees already walked, and whether each contributed. Only for walks that start from
            /// an arbitrary item list (a selection, a set's members, search results): those lists
            /// routinely hold a node AND some of its descendants — a search whose condition holds on
            /// a pipe and on its branches, a selection made from such a search — and walking each
            /// entry independently walked the shared subtrees again and collected their leaves
            /// twice. Twice in the scope is exported twice: the same element, same GlobalId.
            /// The whole-model walks start from disjoint roots and leave this null.
            /// </summary>
            public readonly Dictionary<ModelItem, bool>? Done;
            public Walk(List<ModelItem> result, bool includeHidden, Action<int>? onProgress, bool wantMin, bool dedup = false)
            {
                Result = result; IncludeHidden = includeHidden; OnProgress = onProgress; WantMin = wantMin;
                if (dedup) Done = new Dictionary<ModelItem, bool>();
            }
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
            var w = new Walk(result, true, onProgress, wantMinCorner, dedup: true);
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
            => GetItemsFromSet(doc, set, onProgress, wantMinCorner, -1, null);

        /// <param name="rule">The rule's number in the pane, for the per-set probe; -1 records nothing.</param>
        /// <param name="scope">When given, a search set is searched over these nodes only (see
        /// <see cref="SearchScope"/>); its leaves outside the scope are then not returned.</param>
        private static List<ModelItem> GetItemsFromSet(Document doc, SelectionSet set, Action<int>? onProgress, bool wantMinCorner, int rule, SearchScope? scope)
        {
            var result = new List<ModelItem>();
            BeginWalk();
            try
            {
                long ts = Geometry.ExportTiming.Now;
                bool search = !set.HasExplicitModelItems && set.Search != null;
                bool scoped = false;
                ModelItemCollection? mic = null;
                if (set.HasExplicitModelItems) mic = set.ExplicitModelItems;
                else if (set.Search != null)
                {
                    if (scope != null) { mic = scope.FindAll(doc, set.Search); scoped = mic != null; }
                    if (mic == null) mic = set.Search.FindAll(doc, false);
                }
                long tf = Geometry.ExportTiming.Now;
                Geometry.ExportTiming.SetSearchTicks += tf - ts;
                P.Add(P.K.ScanFindAll, ts);
                int found = 0;
                if (mic != null)
                {
                    found = mic.Count;
                    var w = new Walk(result, true, onProgress, wantMinCorner, dedup: true);
                    CollectLeaves(mic, w);
                    EndWalk(w);
                }
                if (rule >= 0) P.SetResolved(rule, search, scoped, tf - ts, Geometry.ExportTiming.Now - tf, found, result.Count);
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
        /// traversals: InstanceGuid when present, else a tree path (display names, root first).
        /// Must be computed identically wherever it's used — the memoised overload below builds
        /// exactly the same string, just without re-reading shared ancestors.
        /// </summary>
        public static string ItemKey(ModelItem item) => ItemKeyCore(item, null);

        /// <summary>
        /// Memoised <see cref="ItemKey"/>. The GUID-less fallback is an
        /// O(tree-depth) COM walk per call, and one operation computes keys for
        /// the same items repeatedly: set members during rule resolution, then
        /// every scope item again during preview or export. ModelItem overrides
        /// Equals/GetHashCode, so it hashes by value across separate traversals
        /// (the batch-scope union dedup relies on the same property), and a
        /// per-operation dictionary collapses those repeats. Null cache = plain computation; a miss is
        /// only ever a lost optimisation, never a wrong key.
        ///
        /// Passing an <see cref="ItemKeyCache"/> (what <see cref="ScanCache"/> hands out) also
        /// memoises the ancestor PATHS, so a GUID-less leaf costs one Parent read and one name read
        /// instead of a walk to the root: the siblings under one Revit element, or the thousands of
        /// leaves under one level, share every path segment above them.
        /// </summary>
        public static string ItemKey(ModelItem item, Dictionary<ModelItem, string>? cache)
        {
            if (cache == null) return ItemKey(item);
            long t = P.Now;
            bool hit = cache.TryGetValue(item, out var k);
            P.Add(P.K.ScanKeyMemo, t);
            if (hit) return k;
            k = ItemKeyCore(item, (cache as ItemKeyCache)?.Paths);
            t = P.Now;
            cache[item] = k;
            P.Add(P.K.ScanKeyMemo, t);
            return k;
        }

        private static string ItemKeyCore(ModelItem item, Dictionary<ModelItem, string>? paths)
        {
            try
            {
                long t = P.Now;
                var g = item.InstanceGuid;
                P.Add(P.K.ScanKeyGuid, t);
                if (g != Guid.Empty) return "G:" + g.ToString();
                t = P.Now;
                var path = "P:" + PathOf(item, paths);
                P.Add(P.K.ScanKeyPath, t);
                return path;
            }
            catch { return "P:" + SafeDisplayName(item); }
        }

        /// <summary>Display-name path from the model root down to <paramref name="item"/>.</summary>
        private static string PathOf(ModelItem item, Dictionary<ModelItem, string>? paths)
        {
            if (paths != null && paths.TryGetValue(item, out var known)) return known;

            string self = item.DisplayName ?? item.ClassName ?? "";
            var parent = ItemTree.Parent(item);
            string path;
            if (parent == null) path = self;
            else if (paths != null) path = PathOf(parent, paths) + "/" + self;
            else
            {
                // Uncached: one walk up, then join root-first — the same string as the memo builds.
                var parts = new List<string> { self };
                for (var a = parent; a != null; a = ItemTree.Parent(a)) parts.Add(a.DisplayName ?? a.ClassName ?? "");
                parts.Reverse();
                path = string.Join("/", parts);
            }
            if (paths != null) paths[item] = path;
            return path;
        }

        private static string SafeDisplayName(ModelItem item)
        {
            try { return item.DisplayName ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// The item-key memo plus a memo of ancestor paths for the GUID-less fallback. Still a
        /// <c>Dictionary&lt;ModelItem,string&gt;</c> of final keys, so every existing caller keeps
        /// its signature; <see cref="ItemKey(ModelItem, Dictionary{ModelItem, string})"/> notices the
        /// subtype and uses the path memo. Same lifetime rules as the keys: valid for one document.
        /// </summary>
        public sealed class ItemKeyCache : Dictionary<ModelItem, string>
        {
            public readonly Dictionary<ModelItem, string> Paths = new Dictionary<ModelItem, string>();
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
        /// The nodes whose search match can reach one of the scope's leaves: the leaves and all of
        /// their ancestors.
        ///
        /// A set's maps are only ever looked up for the export's own leaves. A match contributes
        /// the geometry leaves at or under it, so only a match on a scope leaf or on one of its
        /// ancestors can reach the scope. A match below a scope leaf adds nothing, because the
        /// leaf was taken only when nothing beneath it had geometry. So searching just these
        /// nodes, each on its own, gives the scope's leaves exactly the classes, codes and groups
        /// that a model-wide FindAll would. On a cold session most of a FindAll is Navisworks
        /// loading property data for the nodes it looks at, so fewer nodes is less loading.
        ///
        /// The saved search's own "search in" items and locations still decide which nodes count,
        /// so a search saved over one part of the model stays limited to that part. When the
        /// search's items can't be read the caller falls back to the model-wide FindAll.
        /// </summary>
        internal sealed class SearchScope
        {
            private readonly List<ModelItem> _nodes = new List<ModelItem>();
            private readonly Dictionary<ModelItem, ModelItem?> _parent = new Dictionary<ModelItem, ModelItem?>();

            public SearchScope(List<ModelItem> leaves)
            {
                foreach (var leaf in leaves)
                {
                    var a = leaf;
                    while (a != null && !_parent.ContainsKey(a))
                    {
                        var p = ItemTree.Parent(a);
                        _parent[a] = p;
                        _nodes.Add(a);
                        a = p;
                    }
                }
            }

            /// <summary>The search's matches among the scope nodes, or null to search the whole model.</summary>
            public ModelItemCollection? FindAll(Document doc, Search src)
            {
                try
                {
                    var loc = src.Locations;
                    if (loc != SearchLocations.Self && loc != SearchLocations.Descendants && loc != SearchLocations.DescendantsAndSelf)
                        return null;
                    var roots = new HashSet<ModelItem>();
                    foreach (var r in src.Selection.GetSelectedItems(doc)) roots.Add(r);
                    if (roots.Count == 0) return null;

                    var under = new Dictionary<ModelItem, bool>();
                    var pick = new ModelItemCollection();
                    foreach (var n in _nodes)
                    {
                        bool self = roots.Contains(n);
                        bool keep = loc == SearchLocations.Self ? self
                                  : loc == SearchLocations.Descendants ? Under(n, roots, under)
                                  : self || Under(n, roots, under);
                        if (keep) pick.Add(n);
                    }
                    if (pick.Count == 0) return new ModelItemCollection();

                    var s = new Search();
                    foreach (SearchCondition c in src.SearchConditions) s.SearchConditions.Add(c);
                    s.Selection.CopyFrom(pick);
                    s.Locations = SearchLocations.Self;
                    return s.FindAll(doc, false);
                }
                catch (OperationCanceledException) { throw; }
                catch { return null; }
            }

            /// <summary>Whether a proper ancestor of <paramref name="n"/> is one of the roots.</summary>
            private bool Under(ModelItem n, HashSet<ModelItem> roots, Dictionary<ModelItem, bool> memo)
            {
                var chain = new List<ModelItem>();
                bool result;
                var cur = n;
                while (true)
                {
                    if (memo.TryGetValue(cur, out result)) break;
                    _parent.TryGetValue(cur, out var p);
                    if (p == null) { result = false; break; }
                    if (roots.Contains(p)) { result = true; break; }
                    chain.Add(cur);
                    cur = p;
                }
                memo[cur] = result;
                foreach (var c in chain) memo[c] = result;
                return result;
            }
        }

        /// <summary>
        /// Identity of a scope across sessions: its leaf count and an order-free hash of their item
        /// keys. Maps resolved for one scope (see <see cref="SearchScope"/>) are only reused for
        /// the same leaves.
        /// </summary>
        public static string ScopeSignature(List<ModelItem> leaves, Dictionary<ModelItem, string>? keyCache)
        {
            ulong sum = 0, mix = 0;
            foreach (var leaf in leaves)
            {
                ulong h = 14695981039346656037UL;
                foreach (char ch in ItemKey(leaf, keyCache)) { h ^= ch; h *= 1099511628211UL; }
                sum += h;
                mix ^= h * 0x9E3779B97F4A7C15UL;
            }
            return $"{leaves.Count}:{sum:x16}{mix:x16}";
        }

        /// <summary>
        /// Resolve set→class and set→classification rules in ONE pass over the sets. Earlier rules
        /// win on overlap, per assignment kind — a row that only sets a classification does not
        /// consume the class slot, so a broad "all walls → IfcWall" rule and a narrow "external
        /// walls → Uniclass code" rule compose instead of competing.
        /// </summary>
        public static SetMaps BuildSetMaps(Document doc, IEnumerable<SetRule> rules, Action<int>? onProgress = null,
            Dictionary<ModelItem, string>? keyCache = null, List<ModelItem>? scope = null)
        {
            // Only the scope's own leaves are looked up in the maps, so a search need not look at
            // the rest of the model (see SearchScope). Null scope: the whole model.
            var searchScope = scope != null ? new SearchScope(scope) : null;
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
            int ruleNo = 0;
            foreach (var rule in rules)
            {
                ruleNo++;
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
                {
                    resolved[rule.Set] = leaves = GetItemsFromSet(doc, rule.Set, onProgress, false, ruleNo, searchScope);
                    Geometry.ExportTiming.SetMembers += leaves.Count;
                }
                foreach (var leaf in leaves)
                {
                    var k = ItemKey(leaf, keyCache);
                    long tm = P.Now;
                    if (wantsClass && !maps.Class.ContainsKey(k)) maps.Class[k] = rule.ClassKey;
                    if (wantsCode && !maps.Classification.ContainsKey(k)) maps.Classification[k] = rule.Classification;
                    if (setName.Length > 0 && !maps.Group.ContainsKey(k)) maps.Group[k] = setName;
                    P.Add(P.K.ScanMaps, tm);
                    if ((++visited & 511) == 0) { long tp = P.Now; onProgress?.Invoke(visited); P.Add(P.K.ScanPump, tp); }
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
                    long tb = P.Now;
                    var bb = item.BoundingBox();
                    P.Add(P.K.ScanBox, tb);
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
            if (w.Done == null) return CollectFromCore(item, w);
            long t = P.Now;
            bool seen = w.Done.TryGetValue(item, out bool prior);
            P.Add(P.K.ScanDedup, t);
            if (seen) { Geometry.ExportTiming.OverlapsSkipped++; return prior; }
            bool any = CollectFromCore(item, w);
            t = P.Now;
            w.Done[item] = any;
            P.Add(P.K.ScanDedup, t);
            return any;
        }

        private static bool CollectFromCore(ModelItem item, Walk w)
        {
            w.Visited++;
            if ((w.Visited & 0x3FF) == 0) { long tp = P.Now; w.OnProgress?.Invoke(w.Visited); P.Add(P.K.ScanPump, tp); }

            if (!w.IncludeHidden)
            {
                long th = P.Now;
                bool hidden = item.IsHidden;
                P.Add(P.K.ScanHidden, th);
                if (hidden) { HiddenSkipped++; return false; }
            }

            bool any = false, hadChildren = false;
            // Stepped by hand so the probe times Navisworks producing each child, not the
            // recursion into it.
            long t = P.Now;
            var e = item.Children.GetEnumerator();
            try
            {
                while (true)
                {
                    bool more = e.MoveNext();
                    P.AddTime(P.K.ScanChildren, t);
                    if (!more) break;
                    hadChildren = true;
                    var child = e.Current;
                    ItemTree.Saw(child, item);   // so nobody has to ask Navisworks for it later
                    if (CollectFrom(child, w)) any = true;
                    t = P.Now;
                }
            }
            finally { (e as IDisposable)?.Dispose(); }
            P.Calls(P.K.ScanChildren, 1);

            if (any) return true;              // descendants covered this subtree's geometry
            long tg = P.Now;
            bool geo = item.HasGeometry;
            P.Add(P.K.ScanHasGeometry, tg);
            if (!geo) return false;

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
                ItemTree.Saw(child, item);
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
