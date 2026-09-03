using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using BIMCamel.Collect;
using BIMCamel.Data;

namespace BIMCamel.Geometry
{
    /// <summary>Options bundle for an extraction pass (avoids a long positional parameter list).</summary>
    public sealed class ExtractOptions
    {
        public bool Props;
        public bool Materials;
        public double WeldTol;                       // in the vertices' own units (caller sets correctly)
        /// <summary>
        /// Model-unit → metre factor for base quantities, or 0 when the user turned quantities off
        /// (v5 E4). Non-zero makes the welder measure the mesh in the loops it already runs, so the
        /// exporter never walks the coordinates a third time. Only the PLAIN path uses this: the
        /// instanced path measures once per unique geometry in the exporter, which is 6.7x fewer
        /// measurements than per fragment on the prova model.
        /// </summary>
        public double QtyScale;

        /// <summary>
        /// Skip re-reading geometry for elements that LOOK like a repeat of one already read
        /// (v5 E1) — instanced path only. Off by default and deliberately so: it trades a
        /// measured, self-checking assumption for roughly the model's instancing ratio in read
        /// time. See <see cref="GeometryCache"/> for the identity used and how it verifies itself.
        /// </summary>
        public bool FastInstancing;

        /// <summary>Memory the shape cache may hold before it starts evicting. 0 = the default
        /// (512 MB). An evicted shape is simply read again, so this only trades time for space.</summary>
        public long FastInstancingBudgetBytes;

        /// <summary>
        /// Drop geometry whose bounding box is smaller than this, IN THE VERTICES' OWN UNITS —
        /// the same convention as <see cref="WeldTol"/>, so the caller converts (0 = keep
        /// everything — v6 Z7). Washers, fasteners and screen-only detail are a large share of the
        /// triangle count on plant models and contribute nothing to a coordination deliverable.
        ///
        /// This REMOVES objects rather than approximating them, which is why it is off by default
        /// and why every dropped fragment is counted and reported. True mesh decimation is a
        /// different thing and is deliberately not here: v3 A7 established we can only coarsen, and
        /// the weld tolerance already is a vertex-clustering decimator.
        /// </summary>
        public double MinFragmentSize;

        public HashSet<string>? PsetFilter;
        public Dictionary<string, string>? ClassMap; // itemKey → classKey (encoded class|predef)
        public Dictionary<string, string>? ClassificationMap; // itemKey → classification code (set rule)
        public Dictionary<string, string>? GroupMap;         // itemKey → set name (IfcGroup export)
        public List<ParamMapRule>? ParamMap;
        public PropertyRoles? Roles;
        /// <summary>Per-operation ItemKey memo, shared with BuildSetMaps so keys
        /// computed while resolving the rules are not recomputed per element.</summary>
        public System.Collections.Generic.Dictionary<Autodesk.Navisworks.Api.ModelItem, string>? KeyCache;
    }

    /// <summary>One element's world-space triangle mesh + semantic role values, ready for IFC.</summary>
    public sealed class ElementMesh
    {
        public string Name = "";
        public Guid InstanceGuid;
        public List<double> Vertices = new List<double>();
        public List<int> Indices = new List<int>();
        public List<IfcProp>? Properties;
        public Material? Material;
        public string? ClassKey;              // F5 mapped IFC class (encoded "class|predef")
        public string GroupName = "";         // Navisworks set this element belongs to (IfcGroup)
        public string TypeName = "";          // → IfcElementType grouping
        public string Level = "";             // → IfcBuildingStorey
        public string MaterialName = "";      // → IfcMaterial
        public string ClassCode = "";         // → IfcClassificationReference

        /// <summary>Base quantities, measured while welding (v5 E4). Null when quantities were not
        /// requested — the exporter then falls back to its own pass.</summary>
        public MeshQty? Quantities;
    }

    /// <summary>
    /// Streams world-space triangle meshes + semantic roles per element via the COM geometry path,
    /// one element at a time (v3 Part A4). The exporter pulls from this lazily and writes each mesh
    /// immediately, so the whole model is never materialised in memory at once.
    /// MUST run on the Navisworks UI thread (STA — IMPLEMENTATION_PLAN.md §5).
    /// </summary>
    public static class MeshExtractor
    {
        public static IEnumerable<ElementMesh> ExtractStream(IEnumerable<ModelItem> items, ExtractOptions o, Action<int>? onProgress = null)
        {
            bool hasClass = o.ClassMap != null && o.ClassMap.Count > 0;
            bool hasCode = o.ClassificationMap != null && o.ClassificationMap.Count > 0;
            bool hasGroup = o.GroupMap != null && o.GroupMap.Count > 0;
            bool hasRoles = o.Roles != null && o.Roles.Any;
            int done = 0;

            // One COM conversion per CHUNK of items rather than one per item (v5 E3).
            foreach (var chunk in ComScope.Chunks(items, ComScope.ChunkSize))
            {
                var paths = ComScope.Convert(chunk);
                foreach (var item in chunk)
                {
                    ElementMesh? em;
                    // One unreadable item must not abort the whole export: before this, a COM failure
                    // or OutOfMemory on a single heavy mesh threw out of the iterator, leaving a
                    // truncated IFC on disk (the writer's footer/flush never ran).
                    try
                    {
                        // Not in the bulk map → convert this one on its own, exactly as before.
                        var mine = paths.TryGetValue(item, out var lst)
                            ? (IEnumerable<InwOaPath3>)lst
                            : ComScope.PathsFor(item);
                        em = BuildElement(item, mine, o, hasClass, hasCode, hasGroup, hasRoles);
                    }
                    catch (Exception ex) { ExportIssues.Fail(SafeName(item), ex); em = null; }

                    done++;
                    onProgress?.Invoke(done);
                    if (em != null) yield return em;
                }
            }
        }

        /// <summary>Reads one item's mesh + semantics; null when it contributes nothing.</summary>
        private static ElementMesh? BuildElement(ModelItem item, IEnumerable<InwOaPath3> comPaths, ExtractOptions o, bool hasClass, bool hasCode, bool hasGroup, bool hasRoles)
        {
            long ts = ExportTiming.Now;
            var sink = new PrimitiveSink();
            foreach (InwOaPath3 path in comPaths)
                foreach (InwOaFragment3 frag in path.Fragments())
                {
                    sink.CurrentTransform = ReadMatrix(frag);
                    // eNONE: PrimitiveSink reads only v.coord, so asking for normals made
                    // Navisworks generate and marshal a per-vertex field we then discarded.
                    frag.GenerateSimplePrimitives(nwEVertexProperty.eNONE, sink);
                    ExportTiming.Fragments++;
                }
            ExportTiming.ReadTicks += ExportTiming.Now - ts;

            if (sink.TriangleCount == 0) { ExportIssues.NoTriangles++; return null; }

            // v6 Z7. PrimitiveSink accumulated the extents as it read, so this costs nothing.
            if (o.MinFragmentSize > 0 && sink.TriangleCount > 0)
            {
                double sx = sink.MaxX - sink.MinX, sy = sink.MaxY - sink.MinY, sz = sink.MaxZ - sink.MinZ;
                if (Math.Max(sx, Math.Max(sy, sz)) < o.MinFragmentSize) { ExportIssues.TooSmall++; return null; }
            }

            var verts = sink.Vertices;
            var idx = sink.Indices;
            // Weld and measure in one traversal (v5 E4). QtyScale is 0 when the user turned base
            // quantities off, and the measuring code is then skipped entirely.
            MeshQty measured = default;
            bool measuredOk = false;
            if (o.WeldTol > 0)
            {
                ts = ExportTiming.Now;
                MeshWelder.Weld(ref verts, ref idx, o.WeldTol, o.QtyScale, out measured);
                measuredOk = o.QtyScale > 0;
                ExportTiming.WeldTicks += ExportTiming.Now - ts;
            }
            else if (o.QtyScale > 0)
            {
                ts = ExportTiming.Now;
                measured = MeshQuantities.Compute(verts, idx, o.QtyScale);
                measuredOk = true;
                ExportTiming.QtyTicks += ExportTiming.Now - ts;
            }

            // Welding can leave every triangle degenerate (a part smaller than the tolerance).
            // That used to sail through here and get dropped, uncounted, by the exporter.
            if (idx.Count == 0) { ExportIssues.CollapsedByWeld++; return null; }

            string key = hasClass || hasCode || hasGroup ? ItemCollector.ItemKey(item, o.KeyCache) : "";
            var em = new ElementMesh
            {
                Name = item.DisplayName ?? "",
                InstanceGuid = item.InstanceGuid,
                Vertices = verts,
                Indices = idx,
                Material = o.Materials ? PropertyHarvester.GetMaterial(item) : null,
                ClassKey = hasClass && o.ClassMap!.TryGetValue(key, out var ck) ? ck : null,
                Quantities = measuredOk ? measured : (MeshQty?)null
            };
            ts = ExportTiming.Now;
            // ONE pass over this item's properties for both the psets and the roles (v5 E2).
            em.Properties = PropertyHarvester.HarvestAndRoles(item, o.Props, o.PsetFilter, hasRoles ? o.Roles : null, out var rv);
            if (em.Properties != null) PsetCatalog.Apply(em.Properties, o.ParamMap);
            if (hasRoles)
            {
                em.TypeName = rv.Type; em.Level = rv.Level; em.MaterialName = rv.Material; em.ClassCode = rv.Classification;
            }
            // A set rule is an explicit decision by the user; it outranks whatever the source
            // property happened to contain.
            if (hasCode && o.ClassificationMap!.TryGetValue(key, out var cc)) em.ClassCode = cc;
            if (hasGroup && o.GroupMap!.TryGetValue(key, out var gn)) em.GroupName = gn;
            ExportTiming.HarvestTicks += ExportTiming.Now - ts;
            return em;
        }

        /// <summary>Item name for a diagnostic message; never throws.</summary>
        internal static string SafeName(ModelItem item)
        {
            try { return item.DisplayName ?? item.ClassDisplayName ?? ""; }
            catch { return ""; }
        }

        internal static double[]? ReadMatrix(InwOaFragment3 frag)
        {
            try
            {
                var t = (InwLTransform3f3)frag.GetLocalToWorldMatrix();
                var arr = (Array)t.Matrix;
                int lb = arr.GetLowerBound(0);
                var m = new double[16];
                for (int i = 0; i < 16; i++)
                    m[i] = Convert.ToDouble(arr.GetValue(lb + i));
                return m;
            }
            catch { return null; }
        }
    }
}
