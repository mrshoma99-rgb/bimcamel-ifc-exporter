using System;
using System.Linq;
using CamelWorks.Core.Identity;
using Xunit;

namespace CamelWorks.Core.Tests
{
    /// <summary>
    /// The properties the triage board's whole value rests on. If any of these break, carry-over
    /// silently attributes one clash's sign-off to another, which looks correct to everyone in the
    /// room — so these are worth more than their line count suggests.
    /// </summary>
    public class ClashKeyTests
    {
        private static readonly string ScopeArch = ElementKey.ScopeOf(@"C:\proj\ARCH.nwc");
        private static readonly string ScopeMep = ElementKey.ScopeOf(@"C:\proj\MEP.nwc");

        private static ElementKey Slab(string? scope = null) =>
            ElementKey.FromTreePath(scope ?? ScopeArch, @"C:\proj\ARCH.nwc", "Level 03", "Floors", "Slab 200");

        private static ElementKey Duct(string? scope = null) =>
            ElementKey.FromTreePath(scope ?? ScopeMep, @"C:\proj\MEP.nwc", "Level 03", "Ducts", "Supply 400x250");

        // ---------------------------------------------------------------------------------
        // THE test. This is the case that killed the ordinal design.
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Fixing_one_of_three_penetrations_does_not_renumber_the_others()
        {
            var slab = Slab();
            var duct = Duct();

            // Three penetrations along the duct run, this week.
            var before = new[]
            {
                ClashKey.Create(slab, duct, 10.0, 4.0, 9.0),
                ClashKey.Create(slab, duct, 14.0, 4.0, 9.0),
                ClashKey.Create(slab, duct, 18.0, 4.0, 9.0),
            };

            // Next week the middle one is fixed and the test re-runs. The engine now reports two
            // results — and with a dense 1..n ordinal, the third would arrive numbered "2" and
            // inherit the second's status, assignee and sign-off.
            var after = new[]
            {
                ClashKey.Create(slab, duct, 10.0, 4.0, 9.0),
                ClashKey.Create(slab, duct, 18.0, 4.0, 9.0),
            };

            Assert.Equal(before[0], after[0]);
            Assert.Equal(before[2], after[1]);
            Assert.DoesNotContain(before[1], after);
        }

        [Fact]
        public void Distinct_positions_on_the_same_pair_are_distinct_keys()
        {
            var slab = Slab();
            var duct = Duct();

            var a = ClashKey.Create(slab, duct, 10.0, 4.0, 9.0);
            var b = ClashKey.Create(slab, duct, 14.0, 4.0, 9.0);

            Assert.NotEqual(a, b);
            Assert.Equal(a.PairId, b.PairId); // same pair, different occurrence
        }

        // ---------------------------------------------------------------------------------
        // Ordering, drift tolerance, confidence
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Participant_order_does_not_matter()
        {
            var slab = Slab();
            var duct = Duct();

            Assert.Equal(
                ClashKey.Create(slab, duct, 1.0, 2.0, 3.0),
                ClashKey.Create(duct, slab, 1.0, 2.0, 3.0));
        }

        [Fact]
        public void A_point_that_drifts_across_a_cell_boundary_is_found_among_the_neighbours()
        {
            var slab = Slab();
            var duct = Duct();

            // Sitting almost exactly on a cell edge, then nudged a millimetre the other side.
            var before = ClashKey.Create(slab, duct, 10.125, 4.0, 9.0);
            var after = ClashKey.Create(slab, duct, 10.1249, 4.0, 9.0);

            // Exact equality is allowed to fail here — that is the whole reason NeighbourCells exists.
            Assert.Contains(before, after.NeighbourCells());
        }

        [Fact]
        public void Neighbour_cells_are_the_full_block_and_lead_with_the_exact_match()
        {
            var key = ClashKey.Create(Slab(), Duct(), 0.0, 0.0, 0.0);
            var cells = key.NeighbourCells().ToList();

            Assert.Equal(27, cells.Count);
            Assert.Equal(key, cells[0]);              // exact match first, so first-hit prefers it
            Assert.Equal(27, cells.Distinct().Count()); // no duplicates
        }

        [Fact]
        public void Weakest_rung_reports_the_confidence_of_a_match()
        {
            var strong = ElementKey.FromInstanceGuid(ScopeArch, Guid.NewGuid());
            var weak = ElementKey.FromGeometry(ScopeMep, "Ducts", "Rect", 0.4, 0.25, 3.0);

            Assert.Equal(KeyRung.InstanceGuid,
                ClashKey.Create(strong, ElementKey.FromInstanceGuid(ScopeMep, Guid.NewGuid()), 0, 0, 0).WeakestRung);

            Assert.Equal(KeyRung.Geometry, ClashKey.Create(strong, weak, 0, 0, 0).WeakestRung);
        }

        // ---------------------------------------------------------------------------------
        // Wire form
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Round_trips_through_its_wire_form()
        {
            var key = ClashKey.Create(Slab(), Duct(), -12.5, 4.0, 9.75);

            Assert.True(ClashKey.TryParse(key.ToString(), out var back));
            Assert.Equal(key, back);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("garbage")]
        [InlineData("1:aaaaaaaa:0123456789abcdef|1:bbbbbbbb:0123456789abcdef")]      // no cell part
        [InlineData("1:aaaaaaaa:0123456789abcdef|1:bbbbbbbb:0123456789abcdef|1,2")]  // short cell
        [InlineData("1:aaaaaaaa:0123456789abcdef|1:bbbbbbbb:0123456789abcdef|a,b,c")]
        public void Malformed_wire_forms_return_false_rather_than_throwing(string? text)
        {
            Assert.False(ClashKey.TryParse(text, out var key));
            Assert.True(key.IsEmpty);
        }

        [Fact]
        public void Refuses_to_key_an_unresolved_participant()
        {
            Assert.Throws<ArgumentException>(() => ClashKey.Create(default, Duct(), 0, 0, 0));
            Assert.Throws<ArgumentException>(() => ClashKey.Create(Slab(), default, 0, 0, 0));
        }
    }
}
