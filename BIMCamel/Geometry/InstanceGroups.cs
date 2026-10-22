using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Navisworks' own instancing, used instead of rediscovered.
    ///
    /// When a geometry node is placed at k paths, <c>InwOaPath3.Fragments()</c> on ANY of those
    /// paths returns all k instances' fragments, each carrying its own path. That list is what
    /// <see cref="ComScope.OwnFragments"/> filters, and it is also a free, exact statement that
    /// those k elements share one shape. This class keeps it:
    ///
    /// <list type="bullet">
    /// <item>The first member seen enumerates the list once and indexes every member's fragments by
    /// path. A later member reads its own path id (one COM call) and takes its fragments from that
    /// index, with no <c>Fragments()</c> call and no <c>ArrayData</c> read per fragment. The plain
    /// filter costs k² calls per node (315,000 for the stadium's 561-row node, 10⁸ for a block
    /// placed 10,000 times); this costs about 2k.</item>
    /// <item>For members with exactly one fragment, the welded local mesh read for the first member
    /// is reused for the rest: their triangles are not read again, only their matrix. Triangle
    /// reads are 82–92% of an export. Unlike Fast instancing this is not a guess about the model,
    /// but it is still checked: every <see cref="VerifyEvery"/>-th reuse is read for real and
    /// compared, and a group that disagrees is never trusted again.</item>
    /// </list>
    ///
    /// A member's entry is dropped once it has been taken, so memory follows the groups that are
    /// partly exported, and <see cref="MaxOpenFragments"/> caps it: past the cap, new groups are
    /// simply not indexed and their members fall back to the plain per-path filter. UI thread only.
    /// </summary>
    internal sealed class InstanceGroups
    {
        public const int MaxOpenFragments = 250_000;
        public const int VerifyEvery = 32;

        public enum Outcome { Unknown, Mesh, NoTriangles, TooSmall, Collapsed }

        /// <summary>One shared geometry node and what reading it produced.</summary>
        public sealed class Group
        {
            public readonly List<InwOaFragment3> Frags;
            public int Open;                   // indexed members not yet taken
            public Outcome Result;             // what the first single-fragment read produced
            public LocalMesh? Mesh;            // its welded local mesh, colourless
            public DedupKey ShapeKey;          // Mesh's key without colour, for verification
            public int UsesSinceVerify;
            public bool Distrusted;
            public Group(List<InwOaFragment3> frags) { Frags = frags; }
        }

        private readonly Dictionary<string, (Group group, List<int> idx)> _members = new(StringComparer.Ordinal);
        private int _openFrags;

        /// <summary>
        /// This path's own fragments. <paramref name="group"/> is set when they form a group whose
        /// shape may be shared — the path is an instance with exactly one fragment of its own.
        /// </summary>
        public List<InwOaFragment3> Own(InwOaPath3 path, out Group? group)
        {
            group = null;
            string? mine;
            try { mine = Key(path.ArrayData); } catch { mine = null; }

            if (mine != null && _members.TryGetValue(mine, out var hit))
            {
                _members.Remove(mine);
                if (--hit.group.Open == 0) _openFrags -= hit.group.Frags.Count;
                var own = new List<InwOaFragment3>(hit.idx.Count);
                foreach (int i in hit.idx) own.Add(hit.group.Frags[i]);
                ExportTiming.ForeignFragmentsSkipped += hit.group.Frags.Count - own.Count;
                ExportTiming.GroupLookups++;
                if (own.Count == 1) group = hit.group;
                return own;
            }

            var all = new List<InwOaFragment3>();
            foreach (InwOaFragment3 frag in path.Fragments()) all.Add(frag);
            if (all.Count < 2 || mine == null) return all;   // nothing to choose between, or no id

            var byMember = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < all.Count; i++)
            {
                string? k;
                try { k = Key(all[i].path.ArrayData); } catch { return all; }
                if (k == null) return all;
                if (!byMember.TryGetValue(k, out var l)) { l = new List<int>(1); byMember[k] = l; }
                l.Add(i);
            }
            // An element must never lose its geometry to the filter: no match keeps them all.
            if (!byMember.TryGetValue(mine, out var ownIdx)) { ExportTiming.OwnFragmentFallbacks++; return all; }
            ExportTiming.ForeignFragmentsSkipped += all.Count - ownIdx.Count;

            var g = new Group(all);
            if (byMember.Count > 1 && _openFrags + all.Count <= MaxOpenFragments)
            {
                foreach (var kv in byMember)
                    if (kv.Key != mine) { _members[kv.Key] = (g, kv.Value); g.Open++; }
                _openFrags += all.Count;
            }
            var result = new List<InwOaFragment3>(ownIdx.Count);
            foreach (int i in ownIdx) result.Add(all[i]);
            if (result.Count == 1) group = g;
            return result;
        }

        /// <summary>A path's id as a string: equal ids, equal strings. Null when unreadable.</summary>
        private static string? Key(object? arrayData)
        {
            if (arrayData is not Array a) return null;
            var parts = new string[a.Length];
            int i = 0;
            foreach (var o in a) parts[i++] = Convert.ToInt32(o).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }
    }
}
