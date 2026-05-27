using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using BIMCamel.Collect;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// PHASE-0 DE-RISKING SPIKE (IMPLEMENTATION_PLAN.md §10, gate #1).
    ///
    /// Goal: prove we can actually pull triangle geometry out of Navisworks through the only
    /// path the product exposes — the COM bridge + InwOaFragment3.GenerateSimplePrimitives +
    /// an InwSimplePrimitivesCB callback — and that the local→world transform is correct.
    ///
    /// It extracts the mesh for the current selection (or the first geometry leaf in the model),
    /// reports triangle/vertex counts and the world-space bounding box, and writes a Wavefront
    /// .obj so the result can be eyeballed in any viewer. NOT production code — no IFC yet.
    ///
    /// VERIFY-ON-FIRST-RUN (these are exactly what the spike exists to confirm against the
    /// installed interop DLL, since docs lag the API):
    ///   • InwSimpleVertex.coord is a 3-float Array.
    ///   • InwOaFragment3.GetLocalToWorldMatrix().Matrix is a 16-double Array (column-major).
    ///   • GenerateSimplePrimitives(nwEVertexProperty.eNORMAL, callback) enumerates triangles.
    /// If the bounding box matches the element's real position in the model, the transform
    /// convention in PrimitiveSink is right; if it's mirrored/rotated, switch to row-major there.
    /// </summary>
    public static class GeometrySpike
    {
        public sealed class Result
        {
            public int ItemCount;
            public int FragmentCount;
            public int TriangleCount;
            public int VertexCount;
            public string BBox = "";
            public string? ObjPath;
            public string FirstItemName = "";
            public string FirstItemIfcGuid = "";
            public bool GuidStable;
        }

        /// <summary>
        /// Run the spike. <paramref name="objPath"/> is where the .obj is written (optional).
        /// Must be called on the Navisworks UI thread.
        /// </summary>
        public static Result Run(Document doc, string? objPath)
        {
            if (doc == null) throw new InvalidOperationException("No active document.");

            // 1) Choose the items to extract: current selection, else first geometry leaf.
            List<ModelItem> items;
            var selected = doc.CurrentSelection.SelectedItems;
            if (selected != null && selected.Count > 0)
            {
                items = ItemCollector.ResolveLeaves(selected);
            }
            else
            {
                items = ItemCollector.GetVisibleLeafItemsWithGeometry(doc);
                if (items.Count > 1) items = items.GetRange(0, 1); // just the first, for a quick spike
            }

            if (items.Count == 0)
                throw new InvalidOperationException(
                    "No geometry found. Select an element (or open a model with geometry) and try again.");

            // 2) Convert to a COM selection and walk fragments.
            var modelItemColl = new ModelItemCollection();
            foreach (var it in items) modelItemColl.Add(it);

            InwOpSelection comSel = ComApiBridge.ToInwOpSelection(modelItemColl);

            var sink = new PrimitiveSink();
            int fragCount = 0;

            foreach (InwOaPath3 path in comSel.Paths())
            {
                foreach (InwOaFragment3 frag in path.Fragments())
                {
                    sink.CurrentTransform = ReadMatrix(frag);
                    // eNORMAL = also request per-vertex normals; we ignore them in the spike.
                    frag.GenerateSimplePrimitives(nwEVertexProperty.eNORMAL, sink);
                    fragCount++;
                }
            }

            // 3) IFC GUID check on the first item (Phase-0 gate #3: stability).
            var first = items[0];
            var ifcGuid = Ifc.IfcGuid.ToIfcGuid(first.InstanceGuid);

            var result = new Result
            {
                ItemCount = items.Count,
                FragmentCount = fragCount,
                TriangleCount = sink.TriangleCount,
                VertexCount = sink.Vertices.Count / 3,
                FirstItemName = first.DisplayName ?? "(unnamed)",
                FirstItemIfcGuid = ifcGuid,
                GuidStable = Ifc.IfcGuid.VerifyStable(first.InstanceGuid),
                BBox = sink.TriangleCount == 0
                    ? "(no triangles)"
                    : $"[{sink.MinX:0.###}, {sink.MinY:0.###}, {sink.MinZ:0.###}] → " +
                      $"[{sink.MaxX:0.###}, {sink.MaxY:0.###}, {sink.MaxZ:0.###}]"
            };

            // 4) Optional .obj dump for visual verification.
            if (!string.IsNullOrEmpty(objPath) && sink.TriangleCount > 0)
            {
                WriteObj(objPath!, sink);
                result.ObjPath = objPath;
            }

            return result;
        }

        /// <summary>Read the fragment's local→world matrix as 16 column-major doubles.</summary>
        private static double[]? ReadMatrix(InwOaFragment3 frag)
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
            catch
            {
                // If the matrix can't be read, fall back to identity so the spike still
                // reports triangle counts (placement will be wrong — that's a finding).
                return null;
            }
        }

        private static void WriteObj(string path, PrimitiveSink sink)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# VeloIFC Phase-0 geometry spike export (world coordinates)");
            var ci = CultureInfo.InvariantCulture;

            for (int i = 0; i < sink.Vertices.Count; i += 3)
                sb.AppendLine($"v {sink.Vertices[i].ToString(ci)} {sink.Vertices[i + 1].ToString(ci)} {sink.Vertices[i + 2].ToString(ci)}");

            // OBJ face indices are 1-based.
            for (int i = 0; i < sink.Indices.Count; i += 3)
                sb.AppendLine($"f {sink.Indices[i] + 1} {sink.Indices[i + 1] + 1} {sink.Indices[i + 2] + 1}");

            File.WriteAllText(path, sb.ToString());
        }
    }
}
