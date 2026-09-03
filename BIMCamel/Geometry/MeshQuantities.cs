using System;
using System.Collections.Generic;

namespace BIMCamel.Geometry
{
    /// <summary>Base quantities computed from a triangle mesh (metres), for IfcElementQuantity.</summary>
    public struct MeshQty
    {
        public double Volume;   // m³ (gross, from closed-ish mesh; abs of signed sum)
        public double Area;     // m² (total surface)
        public double Dx, Dy, Dz; // bounding box size (m)

        // Bounding-box corners (m). Kept so the instanced path can transform a local mesh's box
        // into world space per instance and union the results — without these it could only
        // report the first unique geometry's LOCAL extents, which is not the element's size.
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public bool HasBox;

        /// <summary>Vertical extent — the natural reading of "height" for a building element.</summary>
        public double Height => Dz;
        /// <summary>Longer of the two plan extents.</summary>
        public double Length => Math.Max(Dx, Dy);
        /// <summary>Shorter of the two plan extents.</summary>
        public double Width => Math.Min(Dx, Dy);
    }

    /// <summary>
    /// Computes base quantities from the tessellated mesh (plan: IfcElementQuantity). Volume via the
    /// signed-tetrahedron (divergence) method, surface area via triangle areas, sizes via the AABB.
    /// Volume/area are exact for closed meshes and rigid-transform invariant; for open meshes volume
    /// is approximate — reported as a best-effort gross quantity. <paramref name="scale"/> converts
    /// raw model-unit vertices to metres.
    /// </summary>
    public static class MeshQuantities
    {
        public static MeshQty Compute(List<double> verts, List<int> indices, double scale)
        {
            double vol6 = 0, area2 = 0;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int a = indices[i] * 3, b = indices[i + 1] * 3, c = indices[i + 2] * 3;
                double ax = verts[a] * scale, ay = verts[a + 1] * scale, az = verts[a + 2] * scale;
                double bx = verts[b] * scale, by = verts[b + 1] * scale, bz = verts[b + 2] * scale;
                double cx = verts[c] * scale, cy = verts[c + 1] * scale, cz = verts[c + 2] * scale;

                // signed volume of tetra (origin, a, b, c) = dot(a, cross(b, c)) / 6
                double crx = by * cz - bz * cy, cry = bz * cx - bx * cz, crz = bx * cy - by * cx;
                vol6 += ax * crx + ay * cry + az * crz;

                // triangle area = 0.5 * |（b-a) x (c-a)|
                double ux = bx - ax, uy = by - ay, uz = bz - az;
                double vx = cx - ax, vy = cy - ay, vz = cz - az;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                area2 += Math.Sqrt(nx * nx + ny * ny + nz * nz);
            }

            for (int i = 0; i < verts.Count; i += 3)
            {
                double x = verts[i] * scale, y = verts[i + 1] * scale, z = verts[i + 2] * scale;
                if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
                if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
            }
            return Finish(vol6, area2, minX, minY, minZ, maxX, maxY, maxZ);
        }

        /// <summary>
        /// Turns the raw accumulators into a <see cref="MeshQty"/>. Shared with
        /// <see cref="MeshWelder"/>, which gathers the same sums inside its own loops (v5 E4) —
        /// having ONE finalisation is what keeps the folded numbers identical to this pass's.
        /// </summary>
        internal static MeshQty Finish(double vol6, double area2,
                                       double minX, double minY, double minZ,
                                       double maxX, double maxY, double maxZ)
        {
            bool hasBox = minX != double.MaxValue;
            if (!hasBox) { minX = minY = minZ = maxX = maxY = maxZ = 0; }

            return new MeshQty
            {
                Volume = Math.Abs(vol6) / 6.0,
                Area = area2 / 2.0,
                Dx = maxX - minX, Dy = maxY - minY, Dz = maxZ - minZ,
                MinX = minX, MinY = minY, MinZ = minZ,
                MaxX = maxX, MaxY = maxY, MaxZ = maxZ,
                HasBox = hasBox
            };
        }
    }

    /// <summary>
    /// Running world-space axis-aligned box, used by the instanced path to union each instance's
    /// transformed local box so an element reports its own size rather than one component's.
    /// </summary>
    public struct WorldBox
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public bool Any;

        /// <summary>Transforms the 8 corners of a local box by rotation (column-major 3x3) +
        /// translation and extends this box to cover them.</summary>
        public void AddTransformed(in MeshQty local, double[] rot, double[] trans, double ox, double oy, double oz)
        {
            if (!local.HasBox) return;
            for (int c = 0; c < 8; c++)
            {
                double lx = (c & 1) == 0 ? local.MinX : local.MaxX;
                double ly = (c & 2) == 0 ? local.MinY : local.MaxY;
                double lz = (c & 4) == 0 ? local.MinZ : local.MaxZ;
                double wx = rot[0] * lx + rot[3] * ly + rot[6] * lz + trans[0] - ox;
                double wy = rot[1] * lx + rot[4] * ly + rot[7] * lz + trans[1] - oy;
                double wz = rot[2] * lx + rot[5] * ly + rot[8] * lz + trans[2] - oz;
                if (!Any) { MinX = MaxX = wx; MinY = MaxY = wy; MinZ = MaxZ = wz; Any = true; continue; }
                if (wx < MinX) MinX = wx; if (wx > MaxX) MaxX = wx;
                if (wy < MinY) MinY = wy; if (wy > MaxY) MaxY = wy;
                if (wz < MinZ) MinZ = wz; if (wz > MaxZ) MaxZ = wz;
            }
        }

        public double Dx => Any ? MaxX - MinX : 0;
        public double Dy => Any ? MaxY - MinY : 0;
        public double Dz => Any ? MaxZ - MinZ : 0;
    }
}
