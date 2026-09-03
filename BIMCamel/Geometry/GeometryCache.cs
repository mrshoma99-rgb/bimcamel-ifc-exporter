using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// One cached element shape: the local meshes it produced, tagged with the fragment each came
    /// from, plus enough bookkeeping to replay the element exactly without re-reading a triangle.
    /// </summary>
    internal sealed class CachedShape
    {
        public readonly List<int> FragIndex = new List<int>();
        public readonly List<LocalMesh> Meshes = new List<LocalMesh>();
        public readonly List<DedupKey> Keys = new List<DedupKey>();

        /// <summary>Fragments that welded down to nothing, so the issue tally stays truthful on a
        /// cache hit as well as on a real read.</summary>
        public int CollapsedFrags;

        /// <summary>Occurrences served since this shape was last verified against a real read.</summary>
        public int UsesSinceVerify;

        /// <summary>Rough retained size, for the memory budget.</summary>
        public long Bytes;
    }

    /// <summary>
    /// Recognises a repeated shape BEFORE its triangles are read (v5 E1) — the only thing in the v5
    /// plan that beats the read ceiling without a native shim.
    ///
    /// THE PROBLEM IT SOLVES. The prova model has 683,917 instances of 102,384 unique geometries.
    /// The exporter already writes each unique mesh once, which is why the file is geometry-cheap.
    /// The EXTRACTOR does not: <see cref="InstancedExtractor"/> computes its dedup key from the
    /// vertices, so it must read them first. About 85% of the most expensive work in the export is
    /// therefore re-reading shapes we already hold.
    ///
    /// WHY A GUESS IS NEEDED AT ALL. Navisworks exposes no geometry identity to key on:
    /// <c>InwOaFragment3.Geometry</c> throws "Not implemented" on a real install, and reflection
    /// over both assemblies found no mesh or handle surface. So the identity has to be inferred
    /// from things that ARE cheap to read: the item's place in the tree, its category, its overall
    /// SIZE (not position), its fragment count and its colour. Two occurrences of the same family
    /// type agree on all five.
    ///
    /// WHY THE GUESS IS SAFE TO SHIP. It is not trusted; it is TESTED, continuously. Every
    /// <see cref="VerifyEvery"/> occurrences of a shape, the geometry is read for real and the
    /// resulting content hashes are compared against the cached ones. A match buys another
    /// <see cref="VerifyEvery"/>. A mismatch evicts the shape, blacklists its key for the rest of
    /// the export, and is counted and surfaced on the report — a wrong mesh is the worst thing this
    /// exporter could produce, so the failure is loud and self-correcting rather than silent.
    /// The sampling costs ~5% of the reads it saves.
    ///
    /// It stays OFF by default regardless. The report tells a user whether the assumption held on
    /// THEIR model, which is the only evidence that should turn it on.
    /// </summary>
    internal sealed class GeometryCache
    {
        /// <summary>Read one occurrence in this many for real and check the cached shape.</summary>
        public const int VerifyEvery = 20;

        private readonly Dictionary<string, CachedShape> _shapes = new Dictionary<string, CachedShape>(StringComparer.Ordinal);
        private readonly HashSet<string> _blacklist = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();   // FIFO eviction
        private readonly long _budgetBytes;
        private long _bytes;

        private readonly StringBuilder _key = new StringBuilder(128);

        public GeometryCache(long budgetBytes) { _budgetBytes = budgetBytes > 0 ? budgetBytes : 512L * 1024 * 1024; }

        /// <summary>
        /// The candidate identity of this occurrence, or null when it cannot be formed (and the
        /// element must therefore be read normally).
        ///
        /// Everything in it is cheap: the parent's name is the type node in a Revit/Plant tree, the
        /// class name is the category, the SIZE of the bounding box is position-independent (so the
        /// same part in two places agrees), and the colour is included because the instanced path
        /// folds colour into the mesh dedup key — without it a red copy would inherit a grey one's
        /// styling.
        /// </summary>
        public string? KeyFor(ModelItem item, int fragmentCount, Data.Material? mat)
        {
            try
            {
                var bb = item.BoundingBox();
                if (bb == null) return null;

                _key.Length = 0;
                _key.Append(item.Parent?.DisplayName ?? "").Append('\u0001')
                    .Append(item.ClassDisplayName ?? item.ClassName ?? "").Append('\u0001')
                    .Append(Q(bb.Max.X - bb.Min.X)).Append('\u0001')
                    .Append(Q(bb.Max.Y - bb.Min.Y)).Append('\u0001')
                    .Append(Q(bb.Max.Z - bb.Min.Z)).Append('\u0001')
                    .Append(fragmentCount);
                if (mat != null)
                    _key.Append('\u0001').Append(Q(mat.R)).Append(',').Append(Q(mat.G)).Append(',')
                        .Append(Q(mat.B)).Append(',').Append(Q(mat.Transparency));
                return _key.ToString();
            }
            catch { return null; }
        }

        /// <summary>Quantise to 0.1 mm so floating-point noise never splits a shape in two.</summary>
        private static long Q(double v) => (long)Math.Round(v * 10000.0);

        /// <summary>The cached shape for this key, or null when it is unknown or blacklisted.</summary>
        public CachedShape? Get(string key)
        {
            if (_blacklist.Contains(key)) return null;
            return _shapes.TryGetValue(key, out var s) ? s : null;
        }

        /// <summary>True when this hit is due for a real read to check it is still right.</summary>
        public static bool DueForVerify(CachedShape s) => s.UsesSinceVerify >= VerifyEvery;

        public void Store(string key, CachedShape shape)
        {
            if (_blacklist.Contains(key) || _shapes.ContainsKey(key)) return;
            _shapes[key] = shape;
            _order.Enqueue(key);
            _bytes += shape.Bytes;
            Evict();
        }

        /// <summary>
        /// The guess was WRONG for this key: drop it and never trust it again in this export. The
        /// occurrence that found the mismatch has already been read for real, so nothing incorrect
        /// reaches the file — this only stops the bad shape being served again.
        /// </summary>
        public void Reject(string key)
        {
            if (_shapes.TryGetValue(key, out var s)) { _bytes -= s.Bytes; _shapes.Remove(key); }
            _blacklist.Add(key);
            ExportTiming.GeomMismatches++;
        }

        /// <summary>
        /// Hold the cache inside its byte budget. An evicted shape is not a correctness problem —
        /// its next occurrence is simply read for real again — so plain FIFO is enough and avoids
        /// the bookkeeping an LRU would add to the hot path.
        /// </summary>
        private void Evict()
        {
            while (_bytes > _budgetBytes && _order.Count > 0)
            {
                var k = _order.Dequeue();
                if (!_shapes.TryGetValue(k, out var s)) continue;
                _bytes -= s.Bytes;
                _shapes.Remove(k);
                ExportTiming.GeomEvictions++;
            }
        }

        public static long SizeOf(LocalMesh m) => m.Vertices.Count * 8L + m.Indices.Count * 4L + 64L;
    }
}
