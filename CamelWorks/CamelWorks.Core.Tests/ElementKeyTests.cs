using System;
using CamelWorks.Core.Identity;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class ElementKeyTests
    {
        private const string ArchPath = @"C:\proj\ARCH.nwc";
        private const string MepPath = @"C:\proj\MEP.nwc";

        private static string ArchScope => ElementKey.ScopeOf(ArchPath);
        private static string MepScope => ElementKey.ScopeOf(MepPath);

        // ---------------------------------------------------------------------------------
        // Scoping — the zero-false-match requirement
        // ---------------------------------------------------------------------------------

        [Fact]
        public void The_same_tree_path_in_two_models_is_two_different_elements()
        {
            // Every discipline model exports "Level 03 > <category> > <name>". Without scoping,
            // an architectural and a structural element could key identically and carry-over
            // would hand one's sign-off to the other.
            var a = ElementKey.FromTreePath(ArchScope, ArchPath, "Level 03", "Generic", "Item 1");
            var b = ElementKey.FromTreePath(MepScope, MepPath, "Level 03", "Generic", "Item 1");

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void The_unscoped_value_is_diagnostic_and_never_part_of_equality()
        {
            // Identical tree paths, different models: the scoped values differ (above), but the
            // unscoped values match — which is exactly the signal a "looks like" report wants,
            // and exactly what must never drive a match.
            var a = ElementKey.FromTreePath(ArchScope, "same/path.nwc", "L03", "Item");
            var b = ElementKey.FromTreePath(MepScope, "same/path.nwc", "L03", "Item");

            Assert.Equal(a.UnscopedValue, b.UnscopedValue);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Refuses_to_build_without_a_model_scope()
        {
            Assert.Throws<ArgumentException>(() => ElementKey.FromTreePath("", ArchPath, "L03"));
            Assert.Throws<ArgumentException>(() => ElementKey.FromInstanceGuid("", Guid.NewGuid()));
        }

        [Fact]
        public void Model_scope_ignores_separator_and_case()
        {
            // The same file reached over a mapped drive and a UNC path is the same model.
            Assert.Equal(ElementKey.ScopeOf(@"C:\Proj\Arch.nwc"), ElementKey.ScopeOf("c:/proj/ARCH.NWC"));
        }

        // ---------------------------------------------------------------------------------
        // Determinism
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Is_deterministic_across_separate_constructions()
        {
            var g = Guid.Parse("2f1c9a44-5b7e-4c0d-9a11-8e2b6f3d7c05");

            Assert.Equal(
                ElementKey.FromInstanceGuid(ArchScope, g).ToString(),
                ElementKey.FromInstanceGuid(ArchScope, g).ToString());

            Assert.Equal(
                ElementKey.FromTreePath(ArchScope, ArchPath, "L03", "Walls", "W-12").ToString(),
                ElementKey.FromTreePath(ArchScope, ArchPath, "L03", "Walls", "W-12").ToString());
        }

        [Fact]
        public void Tree_path_segments_cannot_be_reshuffled_into_the_same_key()
        {
            var a = ElementKey.FromTreePath(ArchScope, ArchPath, "L03", "Walls");
            var b = ElementKey.FromTreePath(ArchScope, ArchPath, "L03Walls");
            var c = ElementKey.FromTreePath(ArchScope, ArchPath, "Walls", "L03");

            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void An_empty_instance_guid_is_refused_so_the_caller_falls_through()
        {
            Assert.Throws<ArgumentException>(() => ElementKey.FromInstanceGuid(ArchScope, Guid.Empty));
        }

        // ---------------------------------------------------------------------------------
        // Rung 3 — the weak rung, and why it is shaped the way it is
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Geometry_rung_survives_a_ninety_degree_rotation()
        {
            // Navisworks only exposes a world-space AXIS-ALIGNED box, so rotating an element
            // permutes its extents. Sorting them makes the signature survive that.
            var upright = ElementKey.FromGeometry(ArchScope, "Ducts", "Rect", 0.40, 0.25, 3.00);
            var rotated = ElementKey.FromGeometry(ArchScope, "Ducts", "Rect", 3.00, 0.40, 0.25);

            Assert.Equal(upright, rotated);
        }

        [Fact]
        public void Geometry_rung_ignores_position_and_notices_size()
        {
            // Position is exactly what moves between revisions, so it is not in the signature.
            // Size is what identifies the part.
            var small = ElementKey.FromGeometry(ArchScope, "Ducts", "Rect", 0.40, 0.25, 3.00);
            var large = ElementKey.FromGeometry(ArchScope, "Ducts", "Rect", 0.60, 0.25, 3.00);

            Assert.NotEqual(small, large);
        }

        [Fact]
        public void Geometry_rung_separates_categories_and_types()
        {
            var duct = ElementKey.FromGeometry(ArchScope, "Ducts", "Rect", 1, 1, 1);
            var pipe = ElementKey.FromGeometry(ArchScope, "Pipes", "Rect", 1, 1, 1);
            var other = ElementKey.FromGeometry(ArchScope, "Ducts", "Oval", 1, 1, 1);

            Assert.NotEqual(duct, pipe);
            Assert.NotEqual(duct, other);
        }

        [Fact]
        public void Geometry_rung_tolerates_sub_grid_noise()
        {
            var a = ElementKey.FromGeometry(ArchScope, "Ducts", "Rect", 0.400_0, 0.250_0, 3.0);
            var b = ElementKey.FromGeometry(ArchScope, "Ducts", "Rect", 0.400_2, 0.249_8, 3.0);

            Assert.Equal(a, b);
        }

        // ---------------------------------------------------------------------------------
        // Rungs and wire form
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Rung_is_part_of_identity()
        {
            // A rung-3 match is not the same claim as a rung-1 match, so the two must not be
            // interchangeable even in the impossible case that their hashes agreed.
            var byGuid = ElementKey.FromInstanceGuid(ArchScope, Guid.NewGuid());
            Assert.Equal(KeyRung.InstanceGuid, byGuid.Rung);
            Assert.Equal(KeyRung.TreePath, ElementKey.FromTreePath(ArchScope, ArchPath, "x").Rung);
            Assert.Equal(KeyRung.Geometry, ElementKey.FromGeometry(ArchScope, "c", "t", 1, 1, 1).Rung);
        }

        [Fact]
        public void Round_trips_through_its_wire_form()
        {
            var key = ElementKey.FromTreePath(ArchScope, ArchPath, "L03", "Walls", "W-12");

            Assert.True(ElementKey.TryParse(key.ToString(), out var back));
            Assert.Equal(key, back);
            Assert.Equal(key.Rung, back.Rung);
            Assert.Equal(key.ModelScope, back.ModelScope);
        }

        [Fact]
        public void Wire_form_leads_with_the_rung_so_keys_sort_strongest_first()
        {
            var strong = ElementKey.FromInstanceGuid(ArchScope, Guid.NewGuid()).ToString();
            var weak = ElementKey.FromGeometry(ArchScope, "c", "t", 1, 1, 1).ToString();

            Assert.StartsWith("1:", strong, StringComparison.Ordinal);
            Assert.StartsWith("3:", weak, StringComparison.Ordinal);
            Assert.True(string.CompareOrdinal(strong, weak) < 0);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nope")]
        [InlineData("9:aaaaaaaa:0123456789abcdef")]  // rung out of range
        [InlineData("1:short:0123456789abcdef")]     // scope wrong width
        [InlineData("1:aaaaaaaa:tooshort")]          // value wrong width
        [InlineData("1:aaaaaaaa")]                   // missing field
        public void Malformed_wire_forms_return_false_rather_than_throwing(string? text)
        {
            Assert.False(ElementKey.TryParse(text, out var key));
            Assert.True(key.IsEmpty);
        }

        [Fact]
        public void Default_is_empty_and_prints_as_nothing()
        {
            var key = default(ElementKey);
            Assert.True(key.IsEmpty);
            Assert.Equal(string.Empty, key.ToString());
        }
    }
}
