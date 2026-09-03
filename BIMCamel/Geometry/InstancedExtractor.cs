using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace BIMCamel.Geometry
{
    /// <summary>A unique local-space (metre) mesh, shared by all its instances.</summary>
    public sealed class LocalMesh
    {
        public List<double> Vertices = new List<double>();
        public List<int> Indices = new List<int>();
        public Data.Material? Material; // surface colour (folded into the dedup key so colour variants split)
    }

    /// <summary>
    /// One placement of a fragment's local mesh: the mesh itself + its dedup key (so the exporter can
    /// dedup per output file — v-features option C), plus rotation (3 columns) + world translation
    /// (metres, pre-offset). The exporter dedups by <see cref="Key"/> within each file; repeated
    /// geometry's mesh is re-sent here per element but only written once per file.
    /// </summary>
    public sealed class MeshInstance
    {
        public LocalMesh Mesh = null!;
        public DedupKey Key;
        public double[] Rotation = new double[9]; // column-major 3x3: [c0(0..2), c1(3..5), c2(6..8)]
        public double[] Translation = new double[3];
    }

    public sealed class InstancedElement
    {
        public string Name = "";
        public Guid InstanceGuid;
        public List<MeshInstance> Instances = new List<MeshInstance>();
        public List<Data.IfcProp>? Properties;
        public string? ClassKey;
        public string TypeName = "", Level = "", MaterialName = "", ClassCode = "", GroupName = "";
    }

    /// <summary>
    /// Fixed-size geometry dedup key: two independent 64-bit hashes over the mesh quantised to 0.1 mm
    /// plus vertex/triangle counts and colour. Public so the exporter can dedup per output file.
    /// </summary>
    public readonly struct DedupKey : IEquatable<DedupKey>
    {
        public readonly ulong H0, H1;
        public readonly int V, T;
        public DedupKey(ulong h0, ulong h1, int v, int t) { H0 = h0; H1 = h1; V = v; T = t; }
        public bool Equals(DedupKey o) => H0 == o.H0 && H1 == o.H1 && V == o.V && T == o.T;
        public override bool Equals(object? o) => o is DedupKey k && Equals(k);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)(H0 ^ (H0 >> 32));
                h = h * 31 + (int)(H1 ^ (H1 >> 32));
                h = h * 31 + V; h = h * 31 + T;
                return h;
            }
        }
    }

    /// <summary>
    /// Geometry instancing (IMPLEMENTATION_PLAN.md §5/F2; v3 Part A4). Streams one element at a time:
    /// extracts each fragment's LOCAL geometry plus its local→world transform, deduplicates identical
    /// local meshes (so repeated parts — bolts, fittings… — are stored once and referenced many times
    /// via IfcMappedItem), and yields each element carrying only the geometries it newly introduced.
    /// Nothing is held for the whole model except the small dedup hash table, so peak memory is
    /// bounded regardless of model size.
    ///
    /// Dedup key = a 128-bit hash of the mesh quantized to 0.1 mm plus vertex/triangle counts (see
    /// <see cref="DedupKey"/>). UI-thread only (STA — the read API is single-threaded).
    /// </summary>
    public static class InstancedExtractor
    {
        public static IEnumerable<InstancedElement> ExtractStream(IEnumerable<ModelItem> items, double unitScale, ExtractOptions o, Action<int>? onProgress = null)
        {
            bool hasClass = o.ClassMap != null && o.ClassMap.Count > 0;
            bool hasCode = o.ClassificationMap != null && o.ClassificationMap.Count > 0;
            bool hasGroup = o.GroupMap != null && o.GroupMap.Count > 0;
            bool hasRoles = o.Roles != null && o.Roles.Any;
            int done = 0;

            // Opt-in shape cache that lets a repeated element skip its triangle reads (v5 E1).
            var cache = o.FastInstancing ? new GeometryCache(o.FastInstancingBudgetBytes) : null;

            // One COM conversion per CHUNK of items rather than one per item (v5 E3).
            foreach (var chunk in ComScope.Chunks(items, ComScope.ChunkSize))
            {
                var paths = ComScope.Convert(chunk);
                foreach (var item in chunk)
                {
                    InstancedElement? el;
                    // One unreadable item must not abort the whole export: before this, a COM failure
                    // or OutOfMemory on a single heavy mesh threw out of the iterator, leaving a
                    // truncated IFC on disk (the writer's footer/flush never ran).
                    try
                    {
                        // Not in the bulk map → convert this one on its own, exactly as before.
                        var mine = paths.TryGetValue(item, out var lst)
                            ? (IEnumerable<InwOaPath3>)lst
                            : ComScope.PathsFor(item);
                        el = BuildElement(item, mine, unitScale, o, cache, hasClass, hasCode, hasGroup, hasRoles);
                    }
                    catch (Exception ex) { ExportIssues.Fail(MeshExtractor.SafeName(item), ex); el = null; }

                    done++;
                    onProgress?.Invoke(done);
                    if (el != null) yield return el;
                }
            }
        }

        /// <summary>Reads one item's instanced meshes + semantics; null when it contributes nothing.</summary>
        private static InstancedElement? BuildElement(ModelItem item, IEnumerable<InwOaPath3> comPaths, double unitScale,
                                                     ExtractOptions o, GeometryCache? cache,
                                                     bool hasClass, bool hasCode, bool hasGroup, bool hasRoles)
        {
            string key = hasClass || hasCode || hasGroup ? Collect.ItemCollector.ItemKey(item, o.KeyCache) : "";
            var el = new InstancedElement
            {
                Name = item.DisplayName ?? "",
                InstanceGuid = item.InstanceGuid,
                ClassKey = hasClass && o.ClassMap!.TryGetValue(key, out var ck) ? ck : null
            };
            var itemMat = o.Materials ? Data.PropertyHarvester.GetMaterial(item) : null; // per-item colour

            // The fragments, gathered once. Enumerating them costs nothing near a triangle read,
            // and the count is part of the shape identity below (v5 E1).
            var frags = new List<InwOaFragment3>();
            foreach (InwOaPath3 path in comPaths)
                foreach (InwOaFragment3 frag in path.Fragments())
                    frags.Add(frag);

            string? shapeKey = cache?.KeyFor(item, frags.Count, itemMat);
            CachedShape? hit = shapeKey != null ? cache!.Get(shapeKey) : null;
            // Every VerifyEvery-th occurrence is read for real and checked against the cache.
            bool verifying = hit != null && GeometryCache.DueForVerify(hit);

            int collapsedFrags;
            if (hit != null && !verifying)
            {
                collapsedFrags = ReplayCached(el, hit, frags, unitScale);
                hit.UsesSinceVerify++;
                ExportTiming.GeomReadsSkipped += frags.Count;
            }
            else
            {
                var fresh = ReadFragments(el, frags, unitScale, itemMat, o.WeldTol);
                collapsedFrags = fresh.CollapsedFrags;

                if (verifying)
                {
                    ExportTiming.GeomVerifications++;
                    hit!.UsesSinceVerify = 0;
                    // The guess said these two occurrences are the same shape. If the real read
                    // disagrees, the shape is wrong for this key: drop it and stop trusting it.
                    // This element keeps the geometry just read, so the FILE is always correct.
                    if (!SameShape(hit, fresh)) cache!.Reject(shapeKey!);
                }
                else if (shapeKey != null && cache != null)
                {
                    cache.Store(shapeKey, fresh);
                }
            }

            if (el.Instances.Count == 0)
            {
                // Nothing survived. Distinguish "this node never had triangles" (a group or
                // annotation — normal) from "the weld ate all of it" (a real, actionable loss).
                if (collapsedFrags > 0) ExportIssues.CollapsedByWeld++; else ExportIssues.NoTriangles++;
                return null; // don't harvest or emit it
            }

            // Some parts of this element were welded away but others survived, so no element
            // was lost — counted separately so the report cannot imply phantom data loss.
            ExportIssues.CollapsedFragments += collapsedFrags;

            // Harvest only now that we know the item is actually exported — skips property reads
            // for the (often ~half) of HasGeometry leaves that produce no triangles (v4).
            long th = ExportTiming.Now;
            // ONE pass over this item's properties for both the psets and the roles (v5 E2).
            el.Properties = Data.PropertyHarvester.HarvestAndRoles(item, o.Props, o.PsetFilter, hasRoles ? o.Roles : null, out var rv);
            if (el.Properties != null) Data.PsetCatalog.Apply(el.Properties, o.ParamMap);
            if (hasRoles)
            {
                el.TypeName = rv.Type; el.Level = rv.Level; el.MaterialName = rv.Material; el.ClassCode = rv.Classification;
            }
            // A set rule is an explicit decision by the user; it outranks whatever the source
            // property happened to contain.
            if (hasCode && o.ClassificationMap!.TryGetValue(key, out var cc)) el.ClassCode = cc;
            if (hasGroup && o.GroupMap!.TryGetValue(key, out var gn)) el.GroupName = gn;
            ExportTiming.HarvestTicks += ExportTiming.Now - th;

            return el;
        }

        /// <summary>
        /// The real thing: read every fragment's triangles, weld, hash, and build this element's
        /// instances. Also returns the shape, ready to cache.
        /// </summary>
        private static CachedShape ReadFragments(InstancedElement el, List<InwOaFragment3> frags, double unitScale,
                                                 Data.Material? itemMat, double weldTol)
        {
            var shape = new CachedShape();
            for (int fi = 0; fi < frags.Count; fi++)
            {
                var frag = frags[fi];
                long tr = ExportTiming.Now;
                // Scale folded into the read: local geometry arrives already in metres,
                // so there is no second pass and no second vertex list.
                var sink = new PrimitiveSink { Scale = unitScale }; // CurrentTransform null → LOCAL coords
                // eNONE: PrimitiveSink reads only v.coord, so asking for normals made
                // Navisworks generate and marshal a per-vertex field we then discarded.
                frag.GenerateSimplePrimitives(nwEVertexProperty.eNONE, sink);
                ExportTiming.ReadTicks += ExportTiming.Now - tr; ExportTiming.Fragments++;
                if (sink.TriangleCount == 0) continue;

                var lv = sink.Vertices;
                var li = sink.Indices;
                if (weldTol > 0) { long tw = ExportTiming.Now; MeshWelder.Weld(ref lv, ref li, weldTol); ExportTiming.WeldTicks += ExportTiming.Now - tw; }
                var lm = new LocalMesh { Vertices = lv, Indices = li, Material = itemMat };

                // Welding can leave every triangle degenerate (a part smaller than the
                // tolerance). Emitting that instance wrote an EMPTY IfcCartesianPointList3D
                // / CoordIndex — both are LIST [1:?], so the whole file became schema
                // invalid and strict readers (Revit) refuse it.
                if (li.Count == 0) { shape.CollapsedFrags++; continue; }

                var dk = Key(lm);
                shape.FragIndex.Add(fi);
                shape.Meshes.Add(lm);
                shape.Keys.Add(dk);
                shape.Bytes += GeometryCache.SizeOf(lm);

                el.Instances.Add(Place(frag, lm, dk, unitScale));
            }
            return shape;
        }

        /// <summary>
        /// Rebuild this element from a cached shape WITHOUT reading a triangle: only each
        /// fragment's local→world matrix is read, which is one COM call and no marshalled vertices.
        /// Returns the collapsed-fragment count the original read recorded, so the issue tally does
        /// not quietly change depending on whether an occurrence hit the cache.
        /// </summary>
        private static int ReplayCached(InstancedElement el, CachedShape shape, List<InwOaFragment3> frags, double unitScale)
        {
            for (int i = 0; i < shape.Meshes.Count; i++)
            {
                int fi = shape.FragIndex[i];
                if (fi < 0 || fi >= frags.Count) continue;   // shape/fragment mismatch: skip, never guess
                el.Instances.Add(Place(frags[fi], shape.Meshes[i], shape.Keys[i], unitScale));
            }
            return shape.CollapsedFrags;
        }

        /// <summary>Places one local mesh using its fragment's local→world matrix.</summary>
        private static MeshInstance Place(InwOaFragment3 frag, LocalMesh lm, DedupKey dk, double unitScale)
        {
            // local→world matrix (model units, column-major 4x4)
            var m = MeshExtractor.ReadMatrix(frag);
            var inst = new MeshInstance();
            if (m == null)
            {
                inst.Rotation = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
                inst.Translation = new double[] { 0, 0, 0 };
            }
            else
            {
                inst.Rotation = new[] { m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10] };
                inst.Translation = new[] { m[12] * unitScale, m[13] * unitScale, m[14] * unitScale };
            }
            inst.Mesh = lm;
            inst.Key = dk;   // the exporter dedups by this key, per output file (option C)
            return inst;
        }

        /// <summary>
        /// Did the cached shape and a fresh read of another occurrence actually produce the same
        /// geometry? Compares the 128-bit content hashes, fragment for fragment — the same keys the
        /// exporter dedups on, so agreement here IS agreement about what would be written.
        /// </summary>
        private static bool SameShape(CachedShape cached, CachedShape fresh)
        {
            if (cached.Meshes.Count != fresh.Meshes.Count) return false;
            if (cached.CollapsedFrags != fresh.CollapsedFrags) return false;
            for (int i = 0; i < cached.Keys.Count; i++)
            {
                if (cached.FragIndex[i] != fresh.FragIndex[i]) return false;
                if (!cached.Keys[i].Equals(fresh.Keys[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// Content key for one local mesh, in a single hashing pass. Colour is folded in, so
        /// identical geometry in two colours stays two unique meshes (each styled once on its
        /// own IfcRepresentationMap).
        /// </summary>
        private static DedupKey Key(LocalMesh lm)
        {
            const ulong P0 = 1099511628211UL, P1 = 1099511628219UL; // two distinct odd multipliers
            ulong h0 = 14695981039346656037UL;
            ulong h1 = 1469598103934665600UL ^ 0x9E3779B97F4A7C15UL;

            var verts = lm.Vertices;
            for (int i = 0; i < verts.Count; i++)
            {
                ulong q = (ulong)(long)Math.Round(verts[i] * 10000.0); // quantise to 0.1 mm
                h0 = (h0 ^ q) * P0;
                h1 = (h1 ^ q) * P1;
            }
            var idx = lm.Indices;
            for (int i = 0; i < idx.Count; i++)
            {
                ulong q = (uint)idx[i];
                h0 = (h0 ^ q) * P0;
                h1 = (h1 ^ q) * P1;
            }

            // Fold quantised colour in so identical geometry in different colours becomes distinct
            // unique meshes (each styled once on its IfcRepresentationMap) — v4 D.
            if (lm.Material is Data.Material m)
            {
                ulong c = (ulong)(uint)(
                    ((long)Math.Round(m.R * 255) << 24) | ((long)Math.Round(m.G * 255) << 16) |
                    ((long)Math.Round(m.B * 255) << 8) | (long)Math.Round(m.Transparency * 255));
                h0 = (h0 ^ c) * P0; h1 = (h1 ^ c) * P1;
            }
            return new DedupKey(h0, h1, verts.Count / 3, idx.Count / 3);
        }
    }
}
