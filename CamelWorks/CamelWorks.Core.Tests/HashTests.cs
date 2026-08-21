using System;
using System.Linq;
using CamelWorks.Core.Identity;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class HashTests
    {
        [Fact]
        public void Is_stable_for_the_same_input()
        {
            Assert.Equal(Hash.Of(16, "a", "b"), Hash.Of(16, "a", "b"));
        }

        [Fact]
        public void Parts_cannot_be_reassociated_into_the_same_hash()
        {
            // The classic delimiter bug: ("a","bc") and ("ab","c") must not collide.
            Assert.NotEqual(Hash.Of(16, "a", "bc"), Hash.Of(16, "ab", "c"));
        }

        [Fact]
        public void A_null_part_is_distinct_from_an_empty_part()
        {
            Assert.NotEqual(Hash.Of(16, "x", null), Hash.Of(16, "x", ""));
        }

        [Fact]
        public void A_part_containing_the_separator_cannot_forge_another_grouping()
        {
            // Escaping means a caller cannot smuggle a delimiter through a property value.
            Assert.NotEqual(Hash.Of(16, "ab"), Hash.Of(16, "a", "b"));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(64)]
        public void Produces_exactly_the_requested_width_in_lowercase_hex(int width)
        {
            var h = Hash.Of(width, "anything");
            Assert.Equal(width, h.Length);
            Assert.True(h.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), h);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65)]
        public void Rejects_an_impossible_width(int width)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Hash.Of(width, "x"));
        }

        // ---------------------------------------------------------------------------------
        // Quantise
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(0.0, 0L)]
        [InlineData(0.124, 0L)]
        [InlineData(0.126, 1L)]
        [InlineData(-0.126, -1L)]
        [InlineData(10.0, 40L)]
        [InlineData(-10.0, -40L)]
        public void Quantise_buckets_on_the_grid(double value, long expected)
        {
            Assert.Equal(expected, Hash.Quantise(value, 0.25));
        }

        [Fact]
        public void Quantise_is_symmetric_about_zero()
        {
            // Banker's rounding would send +0.5 and -0.5 to the same bucket (0), which puts a
            // seam through the origin. AwayFromZero keeps the two sides mirror images.
            Assert.Equal(-Hash.Quantise(0.125, 0.25), Hash.Quantise(-0.125, 0.25));
            Assert.Equal(1L, Hash.Quantise(0.125, 0.25));
            Assert.Equal(-1L, Hash.Quantise(-0.125, 0.25));
        }

        [Fact]
        public void Quantise_is_stable_for_a_repeated_value()
        {
            const double v = 1234.56789;
            Assert.Equal(Hash.Quantise(v, 0.001), Hash.Quantise(v, 0.001));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Quantise_refuses_a_non_finite_coordinate(double value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Hash.Quantise(value, 0.25));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void Quantise_refuses_an_impossible_grid(double grid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Hash.Quantise(1.0, grid));
        }
    }

    public class ArchitectureTests
    {
        /// <summary>
        /// The seam, asserted rather than hoped for. CamelWorks.Core must stay free of Autodesk
        /// references: it is what lets Core run on the Linux CI job, and what stops host-specific
        /// behaviour leaking into logic that four host years have to share.
        /// </summary>
        [Fact]
        public void Core_references_no_Autodesk_assembly()
        {
            var core = typeof(ElementKey).Assembly;

            var offenders = core.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(n => n.StartsWith("Autodesk", StringComparison.OrdinalIgnoreCase)
                         || n.IndexOf("Navisworks", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            Assert.True(offenders.Count == 0,
                "CamelWorks.Core must not reference Autodesk assemblies, but references: "
                + string.Join(", ", offenders));
        }

        /// <summary>
        /// Core must also stay free of third-party runtime packages — the project rule that keeps
        /// the bundle dependency-free, and the reason EPPlus (Polyform Noncommercial) can never
        /// ship in a product advertised as free for commercial use.
        /// </summary>
        [Fact]
        public void Core_references_only_the_framework()
        {
            var allowedPrefixes = new[] { "System", "netstandard", "mscorlib", "Microsoft.Win32" };

            var offenders = typeof(ElementKey).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(n => !allowedPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                .ToList();

            Assert.True(offenders.Count == 0,
                "CamelWorks.Core must depend only on the framework, but references: "
                + string.Join(", ", offenders));
        }
    }
}
