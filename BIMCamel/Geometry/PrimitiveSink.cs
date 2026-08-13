using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Callback that Navisworks invokes once per primitive while walking a fragment's geometry
    /// (via InwOaFragment3.GenerateSimplePrimitives). We keep only triangles.
    ///
    /// The vertices arrive in the fragment's LOCAL coordinate space; <see cref="CurrentTransform"/>
    /// (the fragment's local→world matrix, set by the caller before each fragment) is applied
    /// here so the collected mesh is already in WORLD coordinates.
    ///
    /// Performance note: this per-vertex callback IS the export's cost centre (82–92% of wall
    /// clock on real models, a near-constant ~2.9 us/vertex). IMPLEMENTATION_PLAN.md §5 P4 hoped
    /// to replace it with a bulk Marshal.Copy of a vertex buffer — that is not possible.
    /// GenerateSimplePrimitives is the ONLY geometry-read surface Navisworks exposes (verified by
    /// reflecting both Autodesk.Navisworks.Api and .Interop.ComApi: no bulk vertex/triangle/mesh
    /// buffer API exists in either), and fragment geometry handles, which would have let us skip
    /// re-reading repeated meshes, are unusable: InwOaFragment3.Geometry throws
    /// COMException "&lt;&lt;NavisWorks Error - Not implemented&gt;&gt;" on a real install. So the work
    /// here is to make each callback cheap, not to avoid the callbacks.
    /// </summary>
    public sealed class PrimitiveSink : InwSimplePrimitivesCB
    {
        /// <summary>World-space vertices: flat list of (x,y,z) triples.</summary>
        public readonly List<double> Vertices = new List<double>();

        /// <summary>Triangle vertex indices (into <see cref="Vertices"/> as vertex#, not double#).</summary>
        public readonly List<int> Indices = new List<int>();

        public int TriangleCount { get; private set; }

        /// <summary>
        /// Column-major 4x4 local→world matrix for the fragment currently being walked.
        /// 16 doubles; null means identity.
        /// </summary>
        public double[]? CurrentTransform { get; set; }

        /// <summary>
        /// Uniform factor applied to every ordinate as it is read (model units → metres for the
        /// instanced path). Folding it in here removes a separate per-vertex scaling pass and the
        /// second vertex list it had to fill — on a 6.5 M-triangle model that was ~20 M multiply +
        /// List.Add operations per export. 1.0 (the default) leaves coordinates untouched.
        /// </summary>
        public double Scale { get; set; } = 1.0;

        // Running world-space bounding box (for the spike's sanity report).
        public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;

        // Reused scratch for the fast coord read (v4 S1).
        private readonly float[] _c3 = new float[3];

        public void Triangle(InwSimpleVertex v1, InwSimpleVertex v2, InwSimpleVertex v3)
        {
            Indices.Add(AddVertex(v1));
            Indices.Add(AddVertex(v2));
            Indices.Add(AddVertex(v3));
            TriangleCount++;
        }

        // We only need triangles for mesh export; the other primitive kinds are ignored.
        public void Line(InwSimpleVertex v1, InwSimpleVertex v2) { }
        public void Point(InwSimpleVertex v1) { }
        public void SnapPoint(InwSimpleVertex v1) { }

        private int AddVertex(InwSimpleVertex v)
        {
            // v.coord surfaces as a 1-based Single[*] SAFEARRAY (rarely a Double[] variant). A
            // direct 'is' check selects the fast, no-allocation Array.Copy path with NO exception
            // ever thrown on either branch — unlike a try/catch, this stays correct regardless of
            // how often a fresh PrimitiveSink is created. That matters here: the instanced path
            // constructs a NEW PrimitiveSink per FRAGMENT (hundreds of thousands per export), so an
            // exception-driven "learn once" flag living on the instance never gets to stay learned;
            // if coord were ever not float[] on some Navisworks build, every single fragment would
            // pay a real CLR exception on its first vertex instead of the type check settling it once.
            var c = (Array)v.coord;
            int lb = c.GetLowerBound(0); // COM SAFEARRAYs may be 1-based
            double lx, ly, lz;
            if (c is float[])
            {
                Array.Copy(c, lb, _c3, 0, 3);
                lx = _c3[0]; ly = _c3[1]; lz = _c3[2];
            }
            else
            {
                lx = Convert.ToDouble(c.GetValue(lb));
                ly = Convert.ToDouble(c.GetValue(lb + 1));
                lz = Convert.ToDouble(c.GetValue(lb + 2));
            }

            // local → world
            double wx, wy, wz;
            var m = CurrentTransform;
            if (m == null)
            {
                wx = lx; wy = ly; wz = lz;
            }
            else
            {
                // column-major: world = M * [x y z 1]^T
                wx = m[0] * lx + m[4] * ly + m[8] * lz + m[12];
                wy = m[1] * lx + m[5] * ly + m[9] * lz + m[13];
                wz = m[2] * lx + m[6] * ly + m[10] * lz + m[14];
            }

            double s = Scale;
            if (s != 1.0) { wx *= s; wy *= s; wz *= s; }

            Vertices.Add(wx); Vertices.Add(wy); Vertices.Add(wz);

            if (wx < MinX) MinX = wx; if (wx > MaxX) MaxX = wx;
            if (wy < MinY) MinY = wy; if (wy > MaxY) MaxY = wy;
            if (wz < MinZ) MinZ = wz; if (wz > MaxZ) MaxZ = wz;

            return (Vertices.Count / 3) - 1;
        }
    }
}
