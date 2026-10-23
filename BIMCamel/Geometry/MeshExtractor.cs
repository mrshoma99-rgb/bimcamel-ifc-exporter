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
        /// and why every dropped fragment is counted and reported. Approximating is
        /// <see cref="SimplifyTol"/>.
        /// </summary>
        public double MinFragmentSize;

        /// <summary>
        /// Mesh simplification tolerance, IN THE VERTICES' OWN UNITS like <see cref="WeldTol"/>
        /// (0 = off, the default). Each welded mesh is reduced by <see cref="MeshDecimator"/> so no
        /// surface moves further than this. Instanced path: once per unique local mesh, and every
        /// instance reuses the result. Plain path: once per element.
        /// </summary>
        public double SimplifyTol;

        public HashSet<string>? PsetFilter;
        public Dictionary<string, string>? ClassMap; // itemKey → classKey (encoded class|predef)
        public Dictionary<string, string>? ClassificationMap; // itemKey → classification code (set rule)
        public Dictionary<string, string>? GroupMap;         // itemKey → set name (IfcGroup export)
        public List<ParamMapRule>? ParamMap;
        /// <summary>Class / classification chosen by a property value; fills only what the set
        /// rules left empty.</summary>
        public List<PropertyRule>? PropRules;
        public PropertyRoles? Roles;
        /// <summary>Per-operation ItemKey memo, shared with BuildSetMaps so keys
        /// computed while resolving the rules are not recomputed per element.</summary>
        public System.Collections.Generic.Dictionary<Autodesk.Navisworks.Api.ModelItem, string>? KeyCache;

        /// <summary>Classify elements no set rule mapped by the fixed rules in
        /// <see cref="RuleClassifier"/> instead of leaving them as proxies.</summary>
        public bool AutoClassify;
        /// <summary>Model units → metres, for the rules' shape check.</summary>
        public double ClassifyUnitScale = 1;
        /// <summary>Per-export memo of each node's Revit category (see RuleClassifier.ClassifyItem).</summary>
        public System.Collections.Generic.Dictionary<Autodesk.Navisworks.Api.ModelItem, string?>? CategoryCache;
    }

    /// <summary>One element's world-space triangle mesh + semantic role values, ready for IFC.</summary>
    public sealed class ElementMesh
    {
        public string Name = "";
        public Guid InstanceGuid;
        /// <summary>Tree-path key (<see cref="ItemCollector.ItemKey(ModelItem)"/>), set only when
        /// <see cref="InstanceGuid"/> is empty, so the GlobalId fallback is stable across exports.</summary>
        public string SourceKey = "";
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
        /// <summary>Triangles before simplification; 0 = not simplified (same as Indices).</summary>
        public int SourceTriangles;

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
            // Indexes each shared node's fragments once, instead of k² calls per node (InstanceGroups).
            var groups = new InstanceGroups();

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
                        long tl = Probe.Now;
                        bool mapped = paths.TryGetValue(item, out var lst);
                        Probe.Add(Probe.K.SelMap, tl);
                        var mine = mapped ? lst! : ComScope.PathsFor(item);
                        em = BuildElement(item, mine, o, groups, hasClass, hasCode, hasGroup, hasRoles);
                    }
                    catch (Exception ex) { ExportIssues.Fail(SafeName(item), ex); em = null; }

                    done++;
                    long tp = Probe.Now;
                    onProgress?.Invoke(done);
                    Probe.Add(Probe.K.Progress, tp);
                    if (em != null) yield return em;
                }
            }
        }

        /// <summary>Reads one item's mesh + semantics; null when it contributes nothing.</summary>
        private static ElementMesh? BuildElement(ModelItem item, IEnumerable<InwOaPath3> comPaths, ExtractOptions o, InstanceGroups groups, bool hasClass, bool hasCode, bool hasGroup, bool hasRoles)
        {
            long ts = ExportTiming.Now;
            var sink = new PrimitiveSink();
            foreach (InwOaPath3 path in comPaths)
                foreach (InwOaFragment3 frag in groups.Own(path, out _))   // not the other instances'
                {
                    long tm = Probe.Now;
                    sink.CurrentTransform = ReadMatrix(frag);
                    tm = Probe.Lap(Probe.K.Matrix, tm);
                    // eNONE: PrimitiveSink reads only v.coord, so asking for normals made
                    // Navisworks generate and marshal a per-vertex field we then discarded.
                    frag.GenerateSimplePrimitives(nwEVertexProperty.eNONE, sink);
                    Probe.Add(Probe.K.Triangles, tm);
                    ExportTiming.Fragments++;
                }
            ExportTiming.ReadTicks += ExportTiming.Now - ts;

            if (sink.TriangleCount == 0) { ExportIssues.NoTriangles++; return null; }

            // Ground truth: what we read must fit in the box Navisworks gives this item. The sink
            // tracked the world extents while reading, so only the item's box costs a call.
            long tb = Probe.Now;
            BoundsCheck.Check(item, 1.0, 10 * o.WeldTol, sink.MinX, sink.MinY, sink.MinZ, sink.MaxX, sink.MaxY, sink.MaxZ);
            Probe.Add(Probe.K.Hash, tb);

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
                Probe.Add(Probe.K.Weld, ts);
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

            int sourceTris = 0;
            if (o.SimplifyTol > 0)
            {
                ts = ExportTiming.Now;
                int before = idx.Count / 3;
                if (MeshDecimator.Simplify(ref verts, ref idx, o.SimplifyTol))
                {
                    sourceTris = before;
                    // The quantities describe what is written, as on the instanced path.
                    if (measuredOk) measured = MeshQuantities.Compute(verts, idx, o.QtyScale);
                }
                ExportTiming.SimplifyCount(before, idx.Count / 3, ExportTiming.Now - ts);
                Probe.Add(Probe.K.Simplify, ts);
            }

            long tk0 = Probe.Now;
            string key = hasClass || hasCode || hasGroup ? ItemCollector.ItemKey(item, o.KeyCache) : "";
            tk0 = Probe.Lap(Probe.K.ItemKey, tk0);
            var em = new ElementMesh
            {
                Vertices = verts,
                Indices = idx,
                SourceTriangles = sourceTris,
                ClassKey = hasClass && o.ClassMap!.TryGetValue(key, out var ck) ? ck : null,
                Quantities = measuredOk ? measured : (MeshQty?)null
            };
            em.Name = item.DisplayName ?? "";
            tk0 = Probe.Lap(Probe.K.ItemName, tk0);
            em.InstanceGuid = item.InstanceGuid;
            tk0 = Probe.Lap(Probe.K.ItemGuid, tk0);
            em.Material = o.Materials ? PropertyHarvester.GetMaterial(item) : null;
            tk0 = Probe.Lap(Probe.K.Colour, tk0);
            if (em.InstanceGuid == Guid.Empty) { em.SourceKey = ItemCollector.ItemKey(item, o.KeyCache); Probe.Add(Probe.K.ItemKey, tk0); }
            ts = ExportTiming.Now;
            // ONE pass over this item's properties for both the psets and the roles (v5 E2).
            em.Properties = PropertyHarvester.HarvestAndRoles(item, o.Props, o.PsetFilter, hasRoles ? o.Roles : null, out var rv);
            if (em.Properties != null) PsetCatalog.Apply(em.Properties, o.ParamMap);
            if (hasRoles)
            {
                em.TypeName = rv.Type; em.Level = rv.Level; em.MaterialName = rv.Material; em.ClassCode = rv.Classification;
                if (!string.IsNullOrWhiteSpace(rv.Name)) em.Name = rv.Name.Trim();
            }
            // A set rule is an explicit decision by the user; it outranks whatever the source
            // property happened to contain.
            if (hasCode && o.ClassificationMap!.TryGetValue(key, out var cc)) em.ClassCode = cc;
            if (o.PropRules != null)
            {
                string? pk = em.ClassKey, pc = hasCode && o.ClassificationMap!.ContainsKey(key) ? em.ClassCode : null;
                long tr = ExportTiming.Now;
                PropertyRules.Apply(item, o.PropRules, ref pk, ref pc);
                ExportTiming.PropRuleTicks += ExportTiming.Now - tr;
                Probe.Add(Probe.K.PropRules, tr);
                em.ClassKey = pk; if (!string.IsNullOrEmpty(pc)) em.ClassCode = pc!;
            }
            if (hasGroup && o.GroupMap!.TryGetValue(key, out var gn)) em.GroupName = gn;
            // No set rule claimed it: try the fixed rules (category, IFC class, name words) before
            // it falls to IfcBuildingElementProxy. Counted inside HarvestTicks — it is a property read.
            if (o.AutoClassify && string.IsNullOrEmpty(em.ClassKey))
            {
                long tk = ExportTiming.Now;
                var guess = RuleClassifier.ClassifyItem(item, o.ClassifyUnitScale, o.CategoryCache);
                ExportTiming.ClassifyTicks += ExportTiming.Now - tk;
                if (guess is { } guessed)
                {
                    long ta = Probe.Now;
                    em.ClassKey = guessed.ClassKey;
                    em.Properties = RuleClassifier.Apply(guessed, em.Properties, o.Props);
                    Probe.Add(Probe.K.ClassifyRules, ta);
                }
            }
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
