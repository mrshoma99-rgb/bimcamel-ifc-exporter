using System;
using System.Collections.Generic;
using System.Globalization;

namespace CamelWorks.Core.Identity
{
    /// <summary>
    /// Names one clash result, stably enough that its status, assignee, due date and comments
    /// survive a model re-export and a re-run of the test.
    ///
    /// <b>The occurrence discriminator is a quantised position, never an ordinal.</b> This is the
    /// single most important decision in the type. An earlier design numbered the <i>n</i> results
    /// on one element pair 1..<i>n</i> along the dominant axis. That renumbers the moment the count
    /// changes — which is precisely the run that matters, because a coordinator fixing one of three
    /// penetrations is the normal case. Two of three results would silently inherit a third's
    /// history. A position-derived discriminator is unmoved by its neighbours disappearing.
    ///
    /// Quantising creates a boundary problem of its own: a clash point sitting near a cell edge can
    /// fall either side between runs. That is handled at MATCH time, not at key time — see
    /// <see cref="NeighbourCells"/>. The key stays an exact, hashable value; the tolerance lives in
    /// the lookup.
    /// </summary>
    public readonly struct ClashKey : IEquatable<ClashKey>
    {
        /// <summary>Default cell size, in model units (metres). Coarse enough to absorb a small
        /// geometry nudge, fine enough to separate two genuinely different conflicts.</summary>
        public const double DefaultCell = 0.25;

        /// <summary>The lower-sorting of the two participants. Ordering makes (A,B) and (B,A) one clash.</summary>
        public ElementKey A { get; }

        /// <summary>The higher-sorting of the two participants.</summary>
        public ElementKey B { get; }

        /// <summary>Quantised clash position — the occurrence discriminator.</summary>
        public long CellX { get; }

        /// <summary>Quantised clash position — the occurrence discriminator.</summary>
        public long CellY { get; }

        /// <summary>Quantised clash position — the occurrence discriminator.</summary>
        public long CellZ { get; }

        private ClashKey(ElementKey a, ElementKey b, long x, long y, long z)
        {
            A = a; B = b; CellX = x; CellY = y; CellZ = z;
        }

        /// <summary>True for a key that was never assigned.</summary>
        public bool IsEmpty => A.IsEmpty && B.IsEmpty;

        /// <summary>
        /// Build a key from the two participants and the clash point.
        /// </summary>
        /// <param name="first">One participant, in any order.</param>
        /// <param name="second">The other participant, in any order.</param>
        /// <param name="pointX">Clash point, model units.</param>
        /// <param name="pointY">Clash point, model units.</param>
        /// <param name="pointZ">Clash point, model units.</param>
        /// <param name="cell">Cell size; defaults to <see cref="DefaultCell"/>.</param>
        public static ClashKey Create(
            ElementKey first,
            ElementKey second,
            double pointX,
            double pointY,
            double pointZ,
            double cell = DefaultCell)
        {
            if (first.IsEmpty || second.IsEmpty)
                throw new ArgumentException("both participants must be resolved before a clash key can be made");

            // Canonical order, so the same physical conflict keys identically whichever way the
            // clash engine happened to report the pair.
            var a = first;
            var b = second;
            if (string.CompareOrdinal(a.ToString(), b.ToString()) > 0)
            {
                a = second;
                b = first;
            }

            return new ClashKey(
                a, b,
                Hash.Quantise(pointX, cell),
                Hash.Quantise(pointY, cell),
                Hash.Quantise(pointZ, cell));
        }

        /// <summary>
        /// This key and its 26 neighbours — the 3×3×3 cell block centred on it.
        ///
        /// Carry-over looks the previous run's keys up in this set rather than by exact equality,
        /// so a clash point that drifted across a cell boundary still matches. Returns the centre
        /// cell FIRST, so a caller taking the first hit prefers the exact match.
        /// </summary>
        public IEnumerable<ClashKey> NeighbourCells()
        {
            yield return this;
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                yield return new ClashKey(A, B, CellX + dx, CellY + dy, CellZ + dz);
            }
        }

        /// <summary>
        /// The pair alone, ignoring position — every result on these two elements.
        /// Used for "the same two things still clash somewhere" reporting, and for the cross-test
        /// duplicate collapse, which must NOT use exact key equality: two tests with different
        /// tolerances report different intersection points on the same pair, so the same physical
        /// conflict quantises into different cells.
        /// </summary>
        public string PairId => A.ToString() + "|" + B.ToString();

        /// <summary>Wire form: <c>pair|x,y,z</c>.</summary>
        public override string ToString() =>
            IsEmpty
                ? string.Empty
                : PairId + "|" +
                  CellX.ToString(CultureInfo.InvariantCulture) + "," +
                  CellY.ToString(CultureInfo.InvariantCulture) + "," +
                  CellZ.ToString(CultureInfo.InvariantCulture);

        /// <summary>Parse the wire form. Returns false rather than throwing on anything malformed.</summary>
        public static bool TryParse(string? text, out ClashKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text!.Split('|');
            if (parts.Length != 3) return false;
            if (!ElementKey.TryParse(parts[0], out var a)) return false;
            if (!ElementKey.TryParse(parts[1], out var b)) return false;

            var cells = parts[2].Split(',');
            if (cells.Length != 3) return false;
            if (!long.TryParse(cells[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)) return false;
            if (!long.TryParse(cells[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y)) return false;
            if (!long.TryParse(cells[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var z)) return false;

            key = new ClashKey(a, b, x, y, z);
            return true;
        }

        /// <summary>
        /// The weakest rung either participant rests on — the confidence of any match made on this
        /// key. A pair of rung-1 keys is a fact; anything touching rung 3 is a proposal.
        /// </summary>
        public KeyRung WeakestRung => A.Rung > B.Rung ? A.Rung : B.Rung;

        /// <inheritdoc />
        public bool Equals(ClashKey other) =>
            A.Equals(other.A) && B.Equals(other.B)
            && CellX == other.CellX && CellY == other.CellY && CellZ == other.CellZ;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ClashKey k && Equals(k);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var h = A.GetHashCode();
                h = (h * 397) ^ B.GetHashCode();
                h = (h * 397) ^ CellX.GetHashCode();
                h = (h * 397) ^ CellY.GetHashCode();
                h = (h * 397) ^ CellZ.GetHashCode();
                return h;
            }
        }

        /// <summary>Value equality.</summary>
        public static bool operator ==(ClashKey a, ClashKey b) => a.Equals(b);

        /// <summary>Value inequality.</summary>
        public static bool operator !=(ClashKey a, ClashKey b) => !a.Equals(b);
    }
}
