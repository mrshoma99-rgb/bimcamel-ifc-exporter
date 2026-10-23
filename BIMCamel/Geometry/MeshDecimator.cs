using System;
using System.Collections.Generic;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Mesh simplification by half-edge collapse, ordered by quadric error (Garland &amp; Heckbert
    /// 1997) and bounded by a distance tolerance rather than a triangle budget.
    ///
    /// Navisworks fixes the tessellation when the NWC is made, and some sources tessellate far
    /// beyond what a coordination model needs. The welder only merges coincident corners; this
    /// removes triangles from flat and gently curved regions.
    ///
    /// The tolerance is a real bound, checked, not inferred from the quadric. Quadric error alone
    /// let a 6 m sliver prism lose 50 mm of length at a 5 mm setting (a vertex sliding along its own
    /// planes costs nothing), and a free-placed optimum ended up 3.3 m off the surface. So:
    /// <list type="bullet">
    /// <item>A collapse keeps one of the edge's two ORIGINAL vertices, so every output vertex lies
    /// on the input surface.</item>
    /// <item>Every surviving vertex remembers the original vertices it absorbed, and a collapse is
    /// refused unless each of those (and the removed vertex) stays within <c>tol</c> of the
    /// triangles that will surround the kept vertex.</item>
    /// <item>The quadric only orders the work (cheapest first) and stops it: once the cheapest
    /// remaining collapse costs more than <c>tol²</c>, nothing further is tried.</item>
    /// </list>
    /// Points a NEIGHBOUR absorbed earlier are not re-checked when a collapse reshapes one of its
    /// triangles, so the bound is per collapse, not a full Hausdorff guarantee; on the ice stadium's
    /// 12,916 unique meshes the measured worst vertex deviation stayed within the setting (see the
    /// commit that added this).
    ///
    /// Collapses are also refused when they would change the topology (link condition, an interior
    /// edge between two boundary vertices, a closed fan shrinking below three faces), flip a face,
    /// or touch a non-manifold edge. Deterministic: the same input always gives the same output,
    /// which is what lets every instance of a shape reuse one simplified mesh.
    /// </summary>
    public static class MeshDecimator
    {
        /// <summary>Meshes with fewer triangles than this are returned unchanged.</summary>
        public const int MinTriangles = 8;

        /// <summary>A vertex may absorb at most this many original vertices. Bounds the cost of the
        /// distance check (it walks them all) on large flat regions, at the price of leaving a few
        /// more triangles there.</summary>
        private const int MaxAbsorbed = 512;

        /// <summary>
        /// Simplifies <paramref name="verts"/>/<paramref name="indices"/> so no collapse moves the
        /// surface by more than <paramref name="tol"/> (same units as the vertices). Returns false,
        /// leaving the lists untouched, when nothing could be removed.
        /// </summary>
        public static bool Simplify(ref List<double> verts, ref List<int> indices, double tol)
        {
            int n = verts.Count / 3, m = indices.Count / 3;
            if (tol <= 0 || m < MinTriangles) return false;
            var s = new State(verts, indices, n, m, tol);
            if (!s.Run()) return false;
            s.Emit(out verts, out indices);
            return true;
        }

        private struct Entry
        {
            public double Cost;
            public int Keep, Drop, StampKeep, StampDrop;
        }

        private sealed class State
        {
            private readonly double[] _p;          // positions, 3 per vertex (never move: half-edge collapses)
            private readonly double[] _q;          // quadrics, 10 per vertex (upper triangle of 4x4)
            private readonly int[] _t;             // triangle corners, 3 per triangle
            private readonly bool[] _tDead;
            private readonly List<int>[] _adj;     // vertex → incident triangles (may hold dead ones)
            private readonly List<int>?[] _absorbed; // vertex → original vertices merged into it
            private readonly int[] _stamp;         // bumped whenever a vertex's fan changes
            private readonly bool[] _vDead, _boundary, _locked;
            private readonly int[] _mark;          // scratch for neighbour sets
            private int _markGen;
            private readonly List<Entry> _heap = new List<Entry>();
            private readonly List<int> _fan = new List<int>();   // scratch: the kept vertex's new fan, 3 corners each
            private readonly double _tol, _tol2;
            private readonly int _n;
            private int _alive;

            public State(List<double> verts, List<int> idx, int n, int m, double tol)
            {
                _n = n;
                _tol = tol; _tol2 = tol * tol;
                _p = verts.ToArray();
                _t = idx.ToArray();
                _tDead = new bool[m];
                _q = new double[n * 10];
                _adj = new List<int>[n];
                for (int i = 0; i < n; i++) _adj[i] = new List<int>(6);
                _absorbed = new List<int>?[n];
                _stamp = new int[n];
                _vDead = new bool[n];
                _boundary = new bool[n];
                _locked = new bool[n];
                _mark = new int[n];
                _alive = m;

                var edges = new Dictionary<long, int>(m * 3 / 2 + 1, EdgeHash.Instance);
                for (int t = 0; t < m; t++)
                {
                    int a = _t[t * 3], b = _t[t * 3 + 1], c = _t[t * 3 + 2];
                    _adj[a].Add(t); _adj[b].Add(t); _adj[c].Add(t);
                    if (Normal(a, b, c, out double nx, out double ny, out double nz))
                    {
                        double d = -(nx * _p[a * 3] + ny * _p[a * 3 + 1] + nz * _p[a * 3 + 2]);
                        AddPlane(a, nx, ny, nz, d); AddPlane(b, nx, ny, nz, d); AddPlane(c, nx, ny, nz, d);
                    }
                    Count(edges, a, b); Count(edges, b, c); Count(edges, c, a);
                }

                // Open boundaries: a plane through the edge, perpendicular to its face.
                // Non-manifold edges: pin both ends; there is no well-defined collapse there.
                for (int t = 0; t < m; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = _t[t * 3 + k], b = _t[t * 3 + (k + 1) % 3];
                        int cnt = edges[EdgeKey(a, b)];
                        if (cnt > 2) { _locked[a] = _locked[b] = true; continue; }
                        if (cnt != 1) continue;
                        _boundary[a] = _boundary[b] = true;
                        if (!Normal(_t[t * 3], _t[t * 3 + 1], _t[t * 3 + 2], out double fx, out double fy, out double fz)) continue;
                        double ex = _p[b * 3] - _p[a * 3], ey = _p[b * 3 + 1] - _p[a * 3 + 1], ez = _p[b * 3 + 2] - _p[a * 3 + 2];
                        double px = ey * fz - ez * fy, py = ez * fx - ex * fz, pz = ex * fy - ey * fx;
                        double len = Math.Sqrt(px * px + py * py + pz * pz);
                        if (len <= 0) continue;
                        px /= len; py /= len; pz /= len;
                        double d = -(px * _p[a * 3] + py * _p[a * 3 + 1] + pz * _p[a * 3 + 2]);
                        AddPlane(a, px, py, pz, d); AddPlane(b, px, py, pz, d);
                    }

                foreach (var e in edges.Keys) Push((int)(e >> 32), (int)(e & 0xFFFFFFFF));
            }

            public bool Run()
            {
                int start = _alive;
                while (_heap.Count > 0)
                {
                    var e = Pop();
                    if (e.Cost > _tol2) break;   // the heap is ordered: nothing cheaper is left
                    if (_vDead[e.Keep] || _vDead[e.Drop] || _stamp[e.Keep] != e.StampKeep || _stamp[e.Drop] != e.StampDrop) continue;
                    Collapse(e.Keep, e.Drop);
                }
                return _alive < start;
            }

            public void Emit(out List<double> verts, out List<int> indices)
            {
                var remap = new int[_n];
                for (int i = 0; i < _n; i++) remap[i] = -1;
                verts = new List<double>();
                indices = new List<int>(_alive * 3);
                for (int t = 0; t < _tDead.Length; t++)
                {
                    if (_tDead[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = _t[t * 3 + k];
                        if (remap[v] < 0)
                        {
                            remap[v] = verts.Count / 3;
                            verts.Add(_p[v * 3]); verts.Add(_p[v * 3 + 1]); verts.Add(_p[v * 3 + 2]);
                        }
                        indices.Add(remap[v]);
                    }
                }
            }

            // ── collapse ────────────────────────────────────────────────────────────────

            private void Collapse(int u, int v)   // v merges into u, which stays where it is
            {
                if (!Allowed(u, v)) return;

                foreach (int t in _adj[v])
                {
                    if (_tDead[t]) continue;
                    int b = t * 3;
                    if (_t[b] == u || _t[b + 1] == u || _t[b + 2] == u) { _tDead[t] = true; _alive--; continue; }
                    for (int k = 0; k < 3; k++) if (_t[b + k] == v) _t[b + k] = u;
                    _adj[u].Add(t);
                }
                _adj[v].Clear();
                _adj[u].RemoveAll(t => _tDead[t]);

                var mine = _absorbed[u] ??= new List<int>();
                mine.Add(v);
                if (_absorbed[v] != null) { mine.AddRange(_absorbed[v]!); _absorbed[v] = null; }

                for (int i = 0; i < 10; i++) _q[u * 10 + i] += _q[v * 10 + i];
                _boundary[u] |= _boundary[v];
                _vDead[v] = true;
                _stamp[u]++; _stamp[v]++;

                // Re-cost every edge around the kept vertex: its quadric grew. (A neighbour's fan
                // changed too, but its queued edges stay valid: their cost depends only on the two
                // quadrics, and the geometry is re-checked when an edge is popped.)
                int g = ++_markGen;
                foreach (int t in _adj[u])
                    for (int k = 0; k < 3; k++)
                    {
                        int w = _t[t * 3 + k];
                        if (w == u || _mark[w] == g) continue;
                        _mark[w] = g;
                        Push(u, w);
                    }
            }

            private bool Allowed(int u, int v)
            {
                int absorbed = 1 + (_absorbed[u]?.Count ?? 0) + (_absorbed[v]?.Count ?? 0);
                if (absorbed > MaxAbsorbed) return false;

                // Link condition: the vertices both u and v see must be exactly the apexes of the
                // faces on edge uv. Anything else pinches the surface.
                int g = ++_markGen;
                foreach (int t in _adj[u])
                {
                    if (_tDead[t]) continue;
                    for (int k = 0; k < 3; k++) { int w = _t[t * 3 + k]; if (w != u) _mark[w] = g; }
                }
                int common = 0, shared = 0, fanU = 0, fanV = 0;
                int g2 = ++_markGen;
                foreach (int t in _adj[v])
                {
                    if (_tDead[t]) continue;
                    fanV++;
                    int b = t * 3;
                    if (_t[b] == u || _t[b + 1] == u || _t[b + 2] == u) shared++;
                    for (int k = 0; k < 3; k++)
                    {
                        int w = _t[b + k];
                        if (w == u || w == v) continue;
                        if (_mark[w] == g) { _mark[w] = g2; common++; }
                    }
                }
                if (shared == 0 || common != shared) return false;
                // An interior edge between two boundary vertices would join two boundary loops.
                if (shared == 2 && _boundary[u] && _boundary[v]) return false;
                foreach (int t in _adj[u]) if (!_tDead[t]) fanU++;
                // A closed fan below three faces is a flattened tetrahedron.
                if (!_boundary[u] && !_boundary[v] && fanU + fanV - 2 * shared < 3) return false;

                // The kept vertex's fan after the collapse; no face of it may flip or degenerate.
                _fan.Clear();
                foreach (int t in _adj[u])
                {
                    if (_tDead[t]) continue;
                    int b = t * 3;
                    if (_t[b] == v || _t[b + 1] == v || _t[b + 2] == v) continue;   // removed by the collapse
                    _fan.Add(_t[b]); _fan.Add(_t[b + 1]); _fan.Add(_t[b + 2]);
                }
                foreach (int t in _adj[v])
                {
                    if (_tDead[t]) continue;
                    int b = t * 3;
                    int a0 = _t[b], a1 = _t[b + 1], a2 = _t[b + 2];
                    if (a0 == u || a1 == u || a2 == u) continue;
                    if (!Normal(a0, a1, a2, out double ox, out double oy, out double oz)) continue;
                    if (a0 == v) a0 = u; else if (a1 == v) a1 = u; else a2 = u;
                    if (!Normal(a0, a1, a2, out double nx, out double ny, out double nz)) return false;
                    if (nx * ox + ny * oy + nz * oz < 0.2) return false;   // turned by more than ~78°
                    _fan.Add(a0); _fan.Add(a1); _fan.Add(a2);
                }
                if (_fan.Count == 0) return false;

                // The tolerance, checked: the removed vertex and everything merged so far must stay
                // within tol of the surface that will replace them.
                if (!Near(v)) return false;
                if (_absorbed[v] != null) foreach (int w in _absorbed[v]!) if (!Near(w)) return false;
                if (_absorbed[u] != null) foreach (int w in _absorbed[u]!) if (!Near(w)) return false;
                return true;
            }

            private bool Near(int w)
            {
                double px = _p[w * 3], py = _p[w * 3 + 1], pz = _p[w * 3 + 2];
                for (int i = 0; i < _fan.Count; i += 3)
                    if (Dist2(px, py, pz, _fan[i], _fan[i + 1], _fan[i + 2]) <= _tol2) return true;
                return false;
            }

            /// <summary>Squared distance from a point to triangle (a, b, c) — Ericson, Real-Time
            /// Collision Detection, 5.1.5.</summary>
            private double Dist2(double px, double py, double pz, int a, int b, int c)
            {
                double ax = _p[a * 3], ay = _p[a * 3 + 1], az = _p[a * 3 + 2];
                double bx = _p[b * 3], by = _p[b * 3 + 1], bz = _p[b * 3 + 2];
                double cx = _p[c * 3], cy = _p[c * 3 + 1], cz = _p[c * 3 + 2];
                double abx = bx - ax, aby = by - ay, abz = bz - az;
                double acx = cx - ax, acy = cy - ay, acz = cz - az;
                double apx = px - ax, apy = py - ay, apz = pz - az;
                double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
                double qx, qy, qz;
                if (d1 <= 0 && d2 <= 0) { qx = ax; qy = ay; qz = az; return Sq(px - qx, py - qy, pz - qz); }
                double bpx = px - bx, bpy = py - by, bpz = pz - bz;
                double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
                if (d3 >= 0 && d4 <= d3) return Sq(px - bx, py - by, pz - bz);
                double vc = d1 * d4 - d3 * d2;
                if (vc <= 0 && d1 >= 0 && d3 <= 0)
                {
                    double s = d1 / (d1 - d3);
                    return Sq(px - (ax + s * abx), py - (ay + s * aby), pz - (az + s * abz));
                }
                double cpx = px - cx, cpy = py - cy, cpz = pz - cz;
                double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
                if (d6 >= 0 && d5 <= d6) return Sq(px - cx, py - cy, pz - cz);
                double vb = d5 * d2 - d1 * d6;
                if (vb <= 0 && d2 >= 0 && d6 <= 0)
                {
                    double s = d2 / (d2 - d6);
                    return Sq(px - (ax + s * acx), py - (ay + s * acy), pz - (az + s * acz));
                }
                double va = d3 * d6 - d5 * d4;
                if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
                {
                    double s = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                    return Sq(px - (bx + s * (cx - bx)), py - (by + s * (cy - by)), pz - (bz + s * (cz - bz)));
                }
                double den = 1.0 / (va + vb + vc);
                double sv = vb * den, sw = vc * den;
                return Sq(px - (ax + abx * sv + acx * sw), py - (ay + aby * sv + acy * sw), pz - (az + abz * sv + acz * sw));
            }

            private static double Sq(double x, double y, double z) => x * x + y * y + z * z;

            // ── costs ───────────────────────────────────────────────────────────────────

            /// <summary>Queues edge (a, b) in whichever direction is cheaper. A pinned end is
            /// always the one kept.</summary>
            private void Push(int a, int b)
            {
                if (_locked[a] && _locked[b]) return;
                double ea = _locked[b] ? double.MaxValue : ErrAt(a, b);   // keep a, drop b
                double eb = _locked[a] ? double.MaxValue : ErrAt(b, a);   // keep b, drop a
                int keep = ea <= eb ? a : b, drop = keep == a ? b : a;
                double cost = Math.Max(0, Math.Min(ea, eb));
                HeapPush(new Entry { Cost = cost, Keep = keep, Drop = drop, StampKeep = _stamp[keep], StampDrop = _stamp[drop] });
            }

            /// <summary>The combined quadric of both ends, evaluated at <paramref name="keep"/>.</summary>
            private double ErrAt(int keep, int other)
            {
                double x = _p[keep * 3], y = _p[keep * 3 + 1], z = _p[keep * 3 + 2];
                int i = keep * 10, j = other * 10;
                double q0 = _q[i] + _q[j], q1 = _q[i + 1] + _q[j + 1], q2 = _q[i + 2] + _q[j + 2], q3 = _q[i + 3] + _q[j + 3];
                double q4 = _q[i + 4] + _q[j + 4], q5 = _q[i + 5] + _q[j + 5], q6 = _q[i + 6] + _q[j + 6];
                double q7 = _q[i + 7] + _q[j + 7], q8 = _q[i + 8] + _q[j + 8], q9 = _q[i + 9] + _q[j + 9];
                return q0 * x * x + 2 * q1 * x * y + 2 * q2 * x * z + 2 * q3 * x
                     + q4 * y * y + 2 * q5 * y * z + 2 * q6 * y
                     + q7 * z * z + 2 * q8 * z + q9;
            }

            private void AddPlane(int v, double a, double b, double c, double d)
            {
                int o = v * 10;
                _q[o] += a * a; _q[o + 1] += a * b; _q[o + 2] += a * c; _q[o + 3] += a * d;
                _q[o + 4] += b * b; _q[o + 5] += b * c; _q[o + 6] += b * d;
                _q[o + 7] += c * c; _q[o + 8] += c * d; _q[o + 9] += d * d;
            }

            private bool Normal(int a, int b, int c, out double nx, out double ny, out double nz)
            {
                double ux = _p[b * 3] - _p[a * 3], uy = _p[b * 3 + 1] - _p[a * 3 + 1], uz = _p[b * 3 + 2] - _p[a * 3 + 2];
                double vx = _p[c * 3] - _p[a * 3], vy = _p[c * 3 + 1] - _p[a * 3 + 1], vz = _p[c * 3 + 2] - _p[a * 3 + 2];
                nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len <= 1e-30) return false;
                nx /= len; ny /= len; nz /= len;
                return true;
            }

            /// <summary>long.GetHashCode is lo ^ hi, so every key (a&lt;&lt;32)|b with the same a ^ b
            /// collides: a 191k-triangle mesh spent 36 s in the edge table. Mix the bits instead.</summary>
            private sealed class EdgeHash : IEqualityComparer<long>
            {
                public static readonly EdgeHash Instance = new EdgeHash();
                public bool Equals(long x, long y) => x == y;
                public int GetHashCode(long k) => (int)(((ulong)k * 0x9E3779B97F4A7C15UL) >> 32);
            }

            private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            private static void Count(Dictionary<long, int> edges, int a, int b)
            {
                long k = EdgeKey(a, b);
                edges.TryGetValue(k, out int c);
                edges[k] = c + 1;
            }

            // ── binary min-heap on Cost; ties broken by vertex ids so the order is deterministic ──

            private static bool Less(in Entry x, in Entry y) =>
                x.Cost < y.Cost || (x.Cost == y.Cost && (x.Keep < y.Keep || (x.Keep == y.Keep && x.Drop < y.Drop)));

            private void HeapPush(Entry e)
            {
                _heap.Add(e);
                int i = _heap.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (!Less(_heap[i], _heap[p])) break;
                    (_heap[i], _heap[p]) = (_heap[p], _heap[i]);
                    i = p;
                }
            }

            private Entry Pop()
            {
                var top = _heap[0];
                int last = _heap.Count - 1;
                _heap[0] = _heap[last];
                _heap.RemoveAt(last);
                int i = 0, n = _heap.Count;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, s = i;
                    if (l < n && Less(_heap[l], _heap[s])) s = l;
                    if (r < n && Less(_heap[r], _heap[s])) s = r;
                    if (s == i) break;
                    (_heap[i], _heap[s]) = (_heap[s], _heap[i]);
                    i = s;
                }
                return top;
            }
        }
    }
}
