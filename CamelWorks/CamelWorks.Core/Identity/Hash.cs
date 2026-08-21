using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CamelWorks.Core.Identity
{
    /// <summary>
    /// The one hashing primitive identity uses: SHA-256 truncated to a fixed hex width.
    ///
    /// Why not <c>string.GetHashCode</c> or <c>HashCode.Combine</c>: both are unstable across
    /// process runs and across .NET versions BY DESIGN. Every value produced here is written into
    /// a sidecar file and later compared against a value produced by a different build, on a
    /// different machine, months later. It has to be a real digest.
    /// </summary>
    public static class Hash
    {
        /// <summary>Hex width of a component hash. 16 hex chars = 64 bits.</summary>
        public const int ComponentWidth = 16;

        /// <summary>
        /// Hex width of a model-scope hash. 8 hex chars = 32 bits — it only has to separate the
        /// handful of models loaded into one document, never the world.
        /// </summary>
        public const int ScopeWidth = 8;

        // Unit separator, and a distinct marker for "this part was null". Both are control
        // characters precisely because they cannot occur in a Navisworks path or property name.
        private const char Sep = '\u001F';
        private const char NullMark = '\u0000';

        /// <summary>
        /// Hash the given parts to <paramref name="hexWidth"/> lowercase hex characters.
        /// Parts are escaped and separated, so ("a","bc") and ("ab","c") cannot collide, and a
        /// null part is distinct from an empty one.
        /// </summary>
        public static string Of(int hexWidth, params string?[] parts)
        {
            if (hexWidth <= 0 || hexWidth > 64)
                throw new ArgumentOutOfRangeException(nameof(hexWidth), "hexWidth must be 1..64");
            if (parts == null) throw new ArgumentNullException(nameof(parts));

            var sb = new StringBuilder();
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(Sep);
                Escape(sb, parts[i]);
            }

            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hexWidth + 2);
                for (var i = 0; i < digest.Length && hex.Length < hexWidth; i++)
                    hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString(0, hexWidth);
            }
        }

        private static void Escape(StringBuilder sb, string? part)
        {
            if (part == null) { sb.Append(NullMark); return; }
            foreach (var c in part)
            {
                if (c == Sep || c == NullMark || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
        }

        /// <summary>
        /// Quantise a coordinate onto a grid as a stable integer. Used wherever a position takes
        /// part in an identity — never a raw double, whose last bits differ between runs.
        /// </summary>
        public static long Quantise(double value, double grid)
        {
            if (grid <= 0 || double.IsNaN(grid) || double.IsInfinity(grid))
                throw new ArgumentOutOfRangeException(nameof(grid), "grid must be finite and positive");
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "coordinate must be finite");

            // AwayFromZero rather than banker's rounding: a value sitting exactly on a grid
            // boundary must land the same way every time, and symmetrically about zero.
            var rounded = Math.Round(value / grid, MidpointRounding.AwayFromZero);

            if (rounded > long.MaxValue || rounded < long.MinValue)
                throw new ArgumentOutOfRangeException(nameof(value), "coordinate is out of range for this grid");

            return (long)rounded;
        }
    }
}
