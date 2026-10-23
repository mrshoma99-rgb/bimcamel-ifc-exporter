using Autodesk.Navisworks.Api;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// An element's <c>BoundingBox()</c>, read once per element instead of once per consumer.
    ///
    /// Three readers want the same box for the same element, one right after another: the fast-
    /// instancing key (<see cref="GeometryCache.KeyFor"/>), the ground-truth check
    /// (<see cref="BoundsCheck"/>) and auto-classify's shape veto. Each used to make its own COM
    /// call. Items are processed one at a time, so remembering only the LAST item read is enough to
    /// collapse those into one, without holding anything for the rest of the model. Matched by
    /// reference: every reader is handed the same <see cref="ModelItem"/> object, and a different
    /// object (even for the same node) simply reads again — a miss is lost time, never a wrong box.
    /// UI-thread only, like the read API.
    /// </summary>
    internal static class ItemBox
    {
        private static ModelItem? _item;
        private static BoundingBox3D? _box;

        /// <summary>The item's box, or null when it has none or cannot be read.</summary>
        public static BoundingBox3D? Of(ModelItem item)
        {
            if (ReferenceEquals(item, _item)) return _box;
            long t = ExportTiming.Now;
            BoundingBox3D? box;
            try { box = item.BoundingBox(); } catch { box = null; }
            ExportTiming.BoxTicks += ExportTiming.Now - t;
            ExportTiming.BoxReads++;
            _item = item; _box = box;
            return box;
        }

        /// <summary>Drop the remembered box, so nothing outlives the export that read it.</summary>
        public static void Reset() { _item = null; _box = null; }
    }
}
