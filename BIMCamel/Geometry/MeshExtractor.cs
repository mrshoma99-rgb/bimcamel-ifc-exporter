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
        public HashSet<string>? PsetFilter;
        public Dictionary<string, string>? ClassMap; // itemKey → classKey (encoded class|predef)
        public List<ParamMapRule>? ParamMap;
        public PropertyRoles? Roles;
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
        public string TypeName = "";          // → IfcElementType grouping
        public string Level = "";             // → IfcBuildingStorey
        public string MaterialName = "";      // → IfcMaterial
        public string ClassCode = "";         // → IfcClassificationReference
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
            bool hasRoles = o.Roles != null && o.Roles.Any;
            int done = 0;

            foreach (var item in items)
            {
                long ts = ExportTiming.Now;
                var coll = new ModelItemCollection { item };
                InwOpSelection comSel = ComApiBridge.ToInwOpSelection(coll);
                ExportTiming.ComConvertTicks += ExportTiming.Now - ts; ExportTiming.ComConverts++;

                ts = ExportTiming.Now;
                var sink = new PrimitiveSink();
                foreach (InwOaPath3 path in comSel.Paths())
                    foreach (InwOaFragment3 frag in path.Fragments())
                    {
                        sink.CurrentTransform = ReadMatrix(frag);
                        frag.GenerateSimplePrimitives(nwEVertexProperty.eNORMAL, sink);
                        ExportTiming.Fragments++;
                    }
                ExportTiming.ReadTicks += ExportTiming.Now - ts;

                ElementMesh? em = null;
                if (sink.TriangleCount > 0)
                {
                    if (o.WeldTol > 0) { ts = ExportTiming.Now; MeshWelder.Weld(sink.Vertices, sink.Indices, o.WeldTol); ExportTiming.WeldTicks += ExportTiming.Now - ts; }

                    em = new ElementMesh
                    {
                        Name = item.DisplayName ?? "",
                        InstanceGuid = item.InstanceGuid,
                        Vertices = sink.Vertices,
                        Indices = sink.Indices,
                        Material = o.Materials ? PropertyHarvester.GetMaterial(item) : null,
                        ClassKey = hasClass && o.ClassMap!.TryGetValue(ItemCollector.ItemKey(item), out var ck) ? ck : null
                    };
                    ts = ExportTiming.Now;
                    if (o.Props)
                    {
                        em.Properties = PropertyHarvester.Harvest(item, o.PsetFilter);
                        PsetCatalog.Apply(em.Properties, o.ParamMap);
                    }
                    if (hasRoles)
                    {
                        var rv = PropertyHarvester.ReadRoles(item, o.Roles!);
                        em.TypeName = rv.Type; em.Level = rv.Level; em.MaterialName = rv.Material; em.ClassCode = rv.Classification;
                    }
                    ExportTiming.HarvestTicks += ExportTiming.Now - ts;
                }

                done++;
                onProgress?.Invoke(done);
                if (em != null) yield return em;
            }
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
