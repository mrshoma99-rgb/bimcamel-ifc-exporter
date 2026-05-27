using BIMCamel.Geometry;

namespace BIMCamel.Ifc
{
    /// <summary>
    /// Transform applied to raw model-unit vertices before they are written: world =
    /// value * Scale - Offset. Scale converts model units → metres; Offset lifts the
    /// (often huge survey) origin out of the geometry and into the IfcSite placement.
    /// </summary>
    public readonly struct CoordTransform
    {
        public readonly double Scale, Ox, Oy, Oz;
        public CoordTransform(double scale, double ox, double oy, double oz)
        { Scale = scale; Ox = ox; Oy = oy; Oz = oz; }

        public double X(double v) => v * Scale - Ox;
        public double Y(double v) => v * Scale - Oy;
        public double Z(double v) => v * Scale - Oz;
    }

    /// <summary>
    /// The dual-schema seam (IMPLEMENTATION_PLAN.md §6): the ONLY place IFC4 and IFC2x3 geometry
    /// genuinely diverge. Each writer emits one element's mesh and returns the id of the geometric
    /// representation item to wrap in an IfcShapeRepresentation.
    ///
    /// v3 Part A2: both writers now stream tokens straight to the buffered stream (no per-mesh
    /// StringBuilder, no whole-mesh string passed to Write). This is the change that lets large
    /// models export without exhausting memory.
    /// </summary>
    public interface IMeshWriter
    {
        string RepresentationType { get; }
        int WriteMesh(StreamingStepWriter w, ElementMesh mesh, CoordTransform t);
    }

    /// <summary>IFC4: compact IfcTriangulatedFaceSet over an IfcCartesianPointList3D.</summary>
    public sealed class Ifc4MeshWriter : IMeshWriter
    {
        public string RepresentationType => "Tessellation";

        public int WriteMesh(StreamingStepWriter w, ElementMesh mesh, CoordTransform t)
        {
            var verts = mesh.Vertices;
            var idx = mesh.Indices;

            // IFCCARTESIANPOINTLIST3D( ( (x,y,z),(x,y,z),... ) )  — streamed, no big string.
            int pl = w.Begin("IFCCARTESIANPOINTLIST3D");
            w.Tok('(');
            for (int i = 0; i < verts.Count; i += 3)
            {
                if (i > 0) w.Sep();
                w.Tok('(');
                w.WriteReal(t.X(verts[i])); w.Sep();
                w.WriteReal(t.Y(verts[i + 1])); w.Sep();
                w.WriteReal(t.Z(verts[i + 2]));
                w.Tok(')');
            }
            w.Tok(')');
            w.End();

            // IFCTRIANGULATEDFACESET(#pl,$,$,( (i,i,i),... ),$)  — CoordIndex is 1-based.
            int fs = w.Begin("IFCTRIANGULATEDFACESET");
            w.RefTok(pl); w.Sep(); w.Tok("$"); w.Sep(); w.Tok("$"); w.Sep();
            w.Tok('(');
            for (int i = 0; i < idx.Count; i += 3)
            {
                if (i > 0) w.Sep();
                w.Tok('(');
                w.WriteIntRaw(idx[i] + 1); w.Sep();
                w.WriteIntRaw(idx[i + 1] + 1); w.Sep();
                w.WriteIntRaw(idx[i + 2] + 1);
                w.Tok(')');
            }
            w.Tok(')'); w.Sep(); w.Tok("$");
            w.End();
            return fs;
        }
    }

    /// <summary>
    /// IFC2x3: IfcFaceBasedSurfaceModel. Chosen over IfcFacetedBrep because Navisworks meshes are
    /// frequently open / non-manifold, which IfcFacetedBrep's planar-closed-shell rules reject
    /// (IMPLEMENTATION_PLAN.md §6). One IfcFace per triangle (schema-mandated — 2x3 has no compact
    /// tessellation entity), but everything is streamed: shared points, and the IfcConnectedFaceSet
    /// face-ref list is emitted from a pooled int[] reused across elements rather than a
    /// List+StringBuilder. This removes the per-triangle string churn that crashed large 2x3 exports.
    /// </summary>
    public sealed class Ifc2x3MeshWriter : IMeshWriter
    {
        public string RepresentationType => "SurfaceModel";

        private int[] _faceIds = new int[1024]; // reused across WriteMesh calls; grown as needed

        public int WriteMesh(StreamingStepWriter w, ElementMesh mesh, CoordTransform t)
        {
            var verts = mesh.Vertices;
            var idx = mesh.Indices;

            int vCount = verts.Count / 3;
            var ptIds = new int[vCount];
            for (int v = 0; v < vCount; v++)
            {
                int i = v * 3;
                int id = w.Begin("IFCCARTESIANPOINT");
                w.Tok('(');
                w.WriteReal(t.X(verts[i])); w.Sep();
                w.WriteReal(t.Y(verts[i + 1])); w.Sep();
                w.WriteReal(t.Z(verts[i + 2]));
                w.Tok(')');
                w.End();
                ptIds[v] = id;
            }

            int triCount = idx.Count / 3;
            if (_faceIds.Length < triCount) _faceIds = new int[triCount];

            for (int tri = 0; tri < triCount; tri++)
            {
                int i = tri * 3;
                int loop = w.Begin("IFCPOLYLOOP");
                w.Tok('(');
                w.RefTok(ptIds[idx[i]]); w.Sep();
                w.RefTok(ptIds[idx[i + 1]]); w.Sep();
                w.RefTok(ptIds[idx[i + 2]]);
                w.Tok(')');
                w.End();

                int bound = w.Begin("IFCFACEOUTERBOUND");
                w.RefTok(loop); w.Sep(); w.Tok(".T.");
                w.End();

                int face = w.Begin("IFCFACE");
                w.Tok('('); w.RefTok(bound); w.Tok(')');
                w.End();
                _faceIds[tri] = face;
            }

            int cfs = w.Begin("IFCCONNECTEDFACESET");
            w.Tok('(');
            for (int i = 0; i < triCount; i++) { if (i > 0) w.Sep(); w.RefTok(_faceIds[i]); }
            w.Tok(')');
            w.End();

            int model = w.Begin("IFCFACEBASEDSURFACEMODEL");
            w.Tok('('); w.RefTok(cfs); w.Tok(')');
            w.End();
            return model;
        }
    }
}
