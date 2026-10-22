using System;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Ground truth for every exported element: does what we wrote fit inside the box Navisworks
    /// itself gives the item?
    ///
    /// Until this existed nothing compared the output with the model, which is how an instanced
    /// node's fragment list went unnoticed: every one of k elements was written with all k copies,
    /// so a single seat-row element spanned the whole 95 m bowl. One <c>BoundingBox()</c> call per
    /// element would have flagged that on the first export. Navisworks' box can be looser than the
    /// triangles but never tighter, so only "sticks out" is checked — an element smaller than its
    /// box is normal (welding, the size filter, a box built from fragment extents).
    /// </summary>
    internal static class BoundsCheck
    {
        /// <summary>
        /// Compares an exported box with the item's own. <paramref name="scale"/> converts
        /// Navisworks' model units into the units of the exported box (1 on the plain path, the
        /// unit scale on the instanced one); <paramref name="slack"/> is an absolute allowance in
        /// those units for weld snapping and coordinate rounding.
        /// </summary>
        public static void Check(ModelItem item, double scale, double slack,
                                 double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            if (!(minX <= maxX)) return;   // nothing measured (or NaN)
            BoundingBox3D? bb;
            try { bb = item.BoundingBox(); } catch { return; }
            if (bb == null) return;
            double nx0 = bb.Min.X * scale, ny0 = bb.Min.Y * scale, nz0 = bb.Min.Z * scale;
            double nx1 = bb.Max.X * scale, ny1 = bb.Max.Y * scale, nz1 = bb.Max.Z * scale;
            if (!(nx0 <= nx1 && ny0 <= ny1 && nz0 <= nz1)) return;   // empty or inverted box

            double nd = Diag(nx1 - nx0, ny1 - ny0, nz1 - nz0);
            double over = Math.Max(Math.Max(Math.Max(nx0 - minX, maxX - nx1), Math.Max(ny0 - minY, maxY - ny1)),
                                   Math.Max(nz0 - minZ, maxZ - nz1));
            // 1% of the element's own size, plus the absolute allowance: generous enough that
            // rounding never trips it, tight enough that a copy placed one bay over always does.
            if (over <= slack + 0.01 * nd) return;

            double ed = Diag(maxX - minX, maxY - minY, maxZ - minZ);
            ExportIssues.OutsideBox(MeshExtractor.SafeName(item), nd > 0 ? ed / nd : double.PositiveInfinity);
        }

        private static double Diag(double dx, double dy, double dz) => Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
