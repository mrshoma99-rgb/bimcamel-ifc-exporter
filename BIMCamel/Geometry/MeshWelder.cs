using System;
using System.Collections.Generic;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Welds coincident vertices within a mesh (P7 / F13). GenerateSimplePrimitives emits three
    /// unique vertices per triangle, so even an exact weld collapses shared corners and shrinks
    /// the point list substantially; a larger tolerance trades fidelity for smaller files.
    /// Mutates the supplied lists in place. tol is in the same units as the vertices.
    /// </summary>
    public static class MeshWelder
    {
        /// <summary>
        /// Welds in place, handing back the new lists by reference. The lists are SWAPPED rather
        /// than copied back into the originals: the old Clear()+AddRange() cost a second full pass
        /// over every coordinate of every mesh, which on a 6.5 M-triangle model is ~20 M doubles
        /// copied for nothing.
        /// </summary>
        public static void Weld(ref List<double> verts, ref List<int> indices, double tol)
            => Weld(ref verts, ref indices, tol, 0.0, out _);

        /// <summary>
        /// Welds, and — when <paramref name="qtyScale"/> is greater than zero — computes the base
        /// quantities of the welded result in the SAME loops (v5 E4).
        ///
        /// Every coordinate of every element used to be walked four times: welded, written, measured
        /// by <see cref="MeshQuantities.Compute"/>, and hashed for the revision manifest. The weld
        /// already visits each surviving vertex once (as it is appended) and each surviving triangle
        /// once (as it is remapped and degenerate-checked) — which is exactly what the quantities
        /// need — so measuring here costs no extra traversal and removes a whole pass.
        ///
        /// The numbers are identical to <c>Compute(weldedVerts, weldedIndices, qtyScale)</c> by
        /// construction: same post-weld vertices for the box, same non-degenerate triangles for the
        /// volume and area, same scale, same finalisation.
        /// </summary>
        public static void Weld(ref List<double> verts, ref List<int> indices, double tol, double qtyScale, out MeshQty qty)
        {
            qty = default;
            bool wantQty = qtyScale > 0;
            if (tol <= 0 || verts.Count == 0)
            {
                // Nothing to weld — the caller still needs the quantities, and there is no loop
                // here to fold them into, so fall back to the standalone pass.
                if (wantQty) qty = MeshQuantities.Compute(verts, indices, qtyScale);
                return;
            }

            double inv = 1.0 / tol;
            var map = new Dictionary<(long, long, long), int>(verts.Count / 3);
            var newVerts = new List<double>(verts.Count);
            var remap = new int[verts.Count / 3];

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            for (int v = 0; v < remap.Length; v++)
            {
                int i = v * 3;
                var key = ((long)Math.Round(verts[i] * inv),
                           (long)Math.Round(verts[i + 1] * inv),
                           (long)Math.Round(verts[i + 2] * inv));
                if (!map.TryGetValue(key, out int ni))
                {
                    ni = newVerts.Count / 3;
                    newVerts.Add(verts[i]); newVerts.Add(verts[i + 1]); newVerts.Add(verts[i + 2]);
                    map[key] = ni;

                    // The box covers every welded vertex — the same set Compute() looped over,
                    // gathered here as each one is created.
                    if (wantQty)
                    {
                        double x = verts[i] * qtyScale, y = verts[i + 1] * qtyScale, z = verts[i + 2] * qtyScale;
                        if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
                        if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
                    }
                }
                remap[v] = ni;
            }

            // Remap indices and drop degenerate triangles (two shared corners after welding).
            var newIdx = new List<int>(indices.Count);
            double vol6 = 0, area2 = 0;
            for (int k = 0; k + 2 < indices.Count; k += 3)
            {
                int a = remap[indices[k]], b = remap[indices[k + 1]], c = remap[indices[k + 2]];
                if (a == b || b == c || a == c) continue;
                newIdx.Add(a); newIdx.Add(b); newIdx.Add(c);

                if (!wantQty) continue;
                int ia = a * 3, ib = b * 3, ic = c * 3;
                double ax = newVerts[ia] * qtyScale, ay = newVerts[ia + 1] * qtyScale, az = newVerts[ia + 2] * qtyScale;
                double bx = newVerts[ib] * qtyScale, by = newVerts[ib + 1] * qtyScale, bz = newVerts[ib + 2] * qtyScale;
                double cx = newVerts[ic] * qtyScale, cy = newVerts[ic + 1] * qtyScale, cz = newVerts[ic + 2] * qtyScale;

                // signed volume of tetra (origin, a, b, c) = dot(a, cross(b, c)) / 6
                double crx = by * cz - bz * cy, cry = bz * cx - bx * cz, crz = bx * cy - by * cx;
                vol6 += ax * crx + ay * cry + az * crz;

                // triangle area = 0.5 * |(b-a) x (c-a)|
                double ux = bx - ax, uy = by - ay, uz = bz - az;
                double vx = cx - ax, vy = cy - ay, vz = cz - az;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                area2 += Math.Sqrt(nx * nx + ny * ny + nz * nz);
            }

            verts = newVerts;
            indices = newIdx;
            if (wantQty) qty = MeshQuantities.Finish(vol6, area2, minX, minY, minZ, maxX, maxY, maxZ);
        }
    }
}
