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
        public string TypeName = "", Level = "", MaterialName = "", ClassCode = "";
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
            bool hasRoles = o.Roles != null && o.Roles.Any;
            int done = 0;

            foreach (var item in items)
            {
                var el = new InstancedElement
                {
                    Name = item.DisplayName ?? "",
                    InstanceGuid = item.InstanceGuid,
                    ClassKey = hasClass && o.ClassMap!.TryGetValue(Collect.ItemCollector.ItemKey(item), out var ck) ? ck : null
                };
                var itemMat = o.Materials ? Data.PropertyHarvester.GetMaterial(item) : null; // per-item colour
                long tc = ExportTiming.Now;
                var coll = new ModelItemCollection { item };
                InwOpSelection comSel = ComApiBridge.ToInwOpSelection(coll);
                ExportTiming.ComConvertTicks += ExportTiming.Now - tc; ExportTiming.ComConverts++;

                foreach (InwOaPath3 path in comSel.Paths())
                {
                    foreach (InwOaFragment3 frag in path.Fragments())
                    {
                        long tr = ExportTiming.Now;
                        var sink = new PrimitiveSink(); // CurrentTransform null → LOCAL coordinates
                        frag.GenerateSimplePrimitives(nwEVertexProperty.eNORMAL, sink);
                        ExportTiming.ReadTicks += ExportTiming.Now - tr; ExportTiming.Fragments++;
                        if (sink.TriangleCount == 0) continue;

                        // local geometry in metres
                        var lm = new LocalMesh { Indices = sink.Indices, Material = itemMat };
                        lm.Vertices.Capacity = sink.Vertices.Count;
                        for (int i = 0; i < sink.Vertices.Count; i++)
                            lm.Vertices.Add(sink.Vertices[i] * unitScale);
                        if (o.WeldTol > 0) { long tw = ExportTiming.Now; MeshWelder.Weld(lm.Vertices, lm.Indices, o.WeldTol); ExportTiming.WeldTicks += ExportTiming.Now - tw; }

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
                        inst.Key = Key(lm);   // the exporter dedups by this key, per output file (option C)
                        el.Instances.Add(inst);
                    }
                }

                done++;
                onProgress?.Invoke(done);
                if (el.Instances.Count == 0) continue; // no triangles → don't harvest or emit it

                // Harvest only now that we know the item is actually exported — skips property reads
                // for the (often ~half) of HasGeometry leaves that produce no triangles (v4).
                long th = ExportTiming.Now;
                if (o.Props)
                {
                    el.Properties = Data.PropertyHarvester.Harvest(item, o.PsetFilter);
                    Data.PsetCatalog.Apply(el.Properties, o.ParamMap);
                }
                if (hasRoles)
                {
                    var rv = Data.PropertyHarvester.ReadRoles(item, o.Roles!);
                    el.TypeName = rv.Type; el.Level = rv.Level; el.MaterialName = rv.Material; el.ClassCode = rv.Classification;
                }
                ExportTiming.HarvestTicks += ExportTiming.Now - th;

                yield return el;
            }
        }

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
