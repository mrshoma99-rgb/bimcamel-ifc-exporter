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
            if (minX == double.MaxValue) { minX = minY = minZ = maxX = maxY = maxZ = 0; }

            return new MeshQty
            {
                Volume = Math.Abs(vol6) / 6.0,
                Area = area2 / 2.0,
                Dx = maxX - minX, Dy = maxY - minY, Dz = maxZ - minZ
            };
        }
    }
}
