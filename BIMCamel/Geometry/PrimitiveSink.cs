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
    /// Phase-0 note: this implementation favours clarity over speed (it uses Array.GetValue,
    /// which is known-slow for this API). The performance pass (IMPLEMENTATION_PLAN.md §5, P4)
    /// will replace per-element access with a bulk Marshal.Copy of the vertex buffer.
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

        // Running world-space bounding box (for the spike's sanity report).
        public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;

        // Reused scratch + a one-shot fallback flag for the fast coord read (v4 S1).
        private readonly float[] _c3 = new float[3];
        private bool _coordIsFloat = true;

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
            // v.coord surfaces as a 1-based Single[*] SAFEARRAY. The old path boxed every ordinate
            // (Convert.ToDouble(GetValue)) — ~3 boxings per vertex, tens of millions per model.
            // Array.Copy into a reused float[3] is a typed block copy with no boxing (v4 S1).
            // A documented (rare) variant returns doubles; on the first type mismatch we latch to
            // the slow path so the export still succeeds.
            var c = (Array)v.coord;
            int lb = c.GetLowerBound(0); // COM SAFEARRAYs may be 1-based
            double lx, ly, lz;
            if (_coordIsFloat)
            {
                try { Array.Copy(c, lb, _c3, 0, 3); lx = _c3[0]; ly = _c3[1]; lz = _c3[2]; }
                catch { _coordIsFloat = false; lx = Convert.ToDouble(c.GetValue(lb)); ly = Convert.ToDouble(c.GetValue(lb + 1)); lz = Convert.ToDouble(c.GetValue(lb + 2)); }
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

            Vertices.Add(wx); Vertices.Add(wy); Vertices.Add(wz);

            if (wx < MinX) MinX = wx; if (wx > MaxX) MaxX = wx;
            if (wy < MinY) MinY = wy; if (wy > MaxY) MaxY = wy;
            if (wz < MinZ) MinZ = wz; if (wz > MaxZ) MaxZ = wz;

            return (Vertices.Count / 3) - 1;
        }
    }
}
