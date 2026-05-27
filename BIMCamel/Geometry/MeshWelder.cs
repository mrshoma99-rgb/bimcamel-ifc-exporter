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
        public static void Weld(List<double> verts, List<int> indices, double tol)
        {
            if (tol <= 0 || verts.Count == 0) return;

            double inv = 1.0 / tol;
            var map = new Dictionary<(long, long, long), int>(verts.Count / 3);
            var newVerts = new List<double>(verts.Count);
            var remap = new int[verts.Count / 3];

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
                }
                remap[v] = ni;
            }

            // Remap indices and drop degenerate triangles (two shared corners after welding).
            var newIdx = new List<int>(indices.Count);
            for (int k = 0; k + 2 < indices.Count; k += 3)
            {
                int a = remap[indices[k]], b = remap[indices[k + 1]], c = remap[indices[k + 2]];
                if (a == b || b == c || a == c) continue;
                newIdx.Add(a); newIdx.Add(b); newIdx.Add(c);
            }

            verts.Clear(); verts.AddRange(newVerts);
            indices.Clear(); indices.AddRange(newIdx);
        }
    }
}
