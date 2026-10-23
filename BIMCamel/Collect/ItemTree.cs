using System.Collections.Generic;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Collect
{
    /// <summary>
    /// Each node's parent, remembered instead of asked for again.
    ///
    /// <c>ModelItem.Parent</c> is one of the dearest calls in the read API. The per-call profile of
    /// a large plant model put it at about 250–300 µs, against well under 1 µs for
    /// <c>DisplayName</c> or <c>InstanceGuid</c>. And every element asked for its parent up to four
    /// times: for its item key's tree path, for the parent nodes whose properties it reads, for
    /// the fast-instancing shape key and for auto-classify. That was roughly 250 s of a 520 s
    /// export.
    ///
    /// The tree walk already knows the answer, because it reached every child through its
    /// parent's <c>Children</c>. So the walk records each child's parent here, and everyone else
    /// reads it from here. A node the walk didn't reach (a selection entry, an ancestor above the
    /// walk) is asked once and then remembered. Matched by value (ModelItem equality, about
    /// 0.2 µs a lookup). Valid for one document: <see cref="ScanCache"/> clears it when the
    /// document changes. UI thread only, like the read API.
    /// </summary>
    public static class ItemTree
    {
        private static readonly Dictionary<ModelItem, ModelItem?> _parents = new Dictionary<ModelItem, ModelItem?>();

        /// <summary>Past this many nodes the map is cleared and refilled: a miss only costs a
        /// Parent read, and a 10-million-node federation must not hold them all.</summary>
        private const int Cap = 4_000_000;

        /// <summary>The walk saw <paramref name="child"/> under <paramref name="parent"/>.</summary>
        public static void Saw(ModelItem child, ModelItem parent)
        {
            if (_parents.Count >= Cap) _parents.Clear();
            _parents[child] = parent;
        }

        /// <summary>Lookups answered from the map, and Parent reads made, since the last
        /// <see cref="ResetCounts"/>. Counts only: the time lands in whichever probe called.</summary>
        public static long Hits, Reads;

        public static void ResetCounts() { Hits = 0; Reads = 0; }

        /// <summary>The node's parent, or null at the top of the tree.</summary>
        public static ModelItem? Parent(ModelItem item)
        {
            if (_parents.TryGetValue(item, out var p)) { Hits++; return p; }
            Reads++;
            try { p = item.Parent; } catch { p = null; }
            if (_parents.Count >= Cap) _parents.Clear();
            _parents[item] = p;
            return p;
        }

        /// <summary>Forget everything: a different document has different nodes.</summary>
        public static void Clear() => _parents.Clear();
    }
}
