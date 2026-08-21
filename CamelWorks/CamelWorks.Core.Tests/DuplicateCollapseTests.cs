using System;
using System.Linq;
using CamelWorks.Core.Clash;
using CamelWorks.Core.Identity;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class DuplicateCollapseTests
    {
        private static readonly ElementKey Duct = ClashFixture.Mep("Duct");
        private static readonly ElementKey Beam = ClashFixture.Arch("Beam");
        private static readonly ElementKey OtherBeam = ClashFixture.Arch("Beam 2");

        private static ClashItem On(ElementKey a, ElementKey b, string test, double x, double? volume = null) =>
            new ClashItem(ClashKey.Create(a, b, x, 0, 0), test) { X = x, Level = "L03", OverlapVolume = volume };

        [Fact]
        public void Two_tests_reporting_one_conflict_collapse_to_one_row()
        {
            // A federation runs a hard-clash test and a clearance test over the same two models.
            // One duct through one beam is otherwise resolved twice, in two places, with two
            // statuses — the second of which nobody updates.
            var hard = On(Duct, Beam, "Hard", 0.00, volume: 0.4);
            var clearance = On(Duct, Beam, "Clearance", 0.4);

            var result = DuplicateCollapse.Across(new[] { hard, clearance });

            Assert.Same(hard, Assert.Single(result.Kept));
            Assert.Same(clearance, Assert.Single(result.Collapsed));
            Assert.Same(hard, result.FoldedInto[clearance]);
        }

        [Fact]
        public void Exact_key_equality_would_have_missed_it()
        {
            // Two tests with different tolerances compute different intersection points, so the
            // same conflict quantises into different cells. This is why the match is a band.
            var hard = On(Duct, Beam, "Hard", 0.00, volume: 0.4);
            var clearance = On(Duct, Beam, "Clearance", 0.4);

            Assert.NotEqual(hard.Key, clearance.Key);
            Assert.Equal(1, DuplicateCollapse.Across(new[] { hard, clearance }).Count);
        }

        [Fact]
        public void Two_results_from_one_test_are_never_collapsed()
        {
            // One pipe through one beam twice, half a metre apart, is two genuine penetrations —
            // the case the positional key exists to keep separate.
            var first = On(Duct, Beam, "Hard", 0.0, volume: 0.4);
            var second = On(Duct, Beam, "Hard", 0.4, volume: 0.4);

            var result = DuplicateCollapse.Across(new[] { first, second });

            Assert.Equal(2, result.Kept.Count);
            Assert.Empty(result.Collapsed);
        }

        [Fact]
        public void Different_pairs_at_the_same_place_never_collapse()
        {
            // A conflict between two other elements at the same point is a different conflict,
            // however close it sits.
            var one = On(Duct, Beam, "Hard", 0, volume: 0.4);
            var two = On(Duct, OtherBeam, "Clearance", 0);

            var result = DuplicateCollapse.Across(new[] { one, two });

            Assert.Equal(2, result.Kept.Count);
            Assert.Empty(result.Collapsed);
        }

        [Fact]
        public void Results_further_apart_than_the_band_are_two_conflicts()
        {
            var hard = On(Duct, Beam, "Hard", 0, volume: 0.4);
            var clearance = On(Duct, Beam, "Clearance", 40);

            Assert.Empty(DuplicateCollapse.Across(new[] { hard, clearance }).Collapsed);
        }

        [Fact]
        public void The_more_severe_test_wins_so_the_board_keeps_the_hard_clash()
        {
            // Folding a hard clash into a clearance miss would demote a real conflict to a warning.
            var hard = On(Duct, Beam, "Hard", 0.4, volume: 0.4);
            var clearance = On(Duct, Beam, "Clearance", 0.00);

            var result = DuplicateCollapse.Across(new[] { clearance, hard });

            Assert.Same(hard, Assert.Single(result.Kept));
        }

        [Fact]
        public void With_nothing_to_choose_between_them_the_fold_is_still_deterministic()
        {
            // Neither test reported a volume. Two runs of identical data must still fold the same
            // way, so the ordinal test name breaks the tie.
            var a = On(Duct, Beam, "Alpha", 0.00);
            var b = On(Duct, Beam, "Beta", 0.4);

            var forward = DuplicateCollapse.Across(new[] { a, b });
            var reversed = DuplicateCollapse.Across(new[] { b, a });

            Assert.Same(a, Assert.Single(forward.Kept));
            Assert.Same(a, Assert.Single(reversed.Kept));
        }

        [Fact]
        public void Collapsed_results_are_retrievable_never_discarded()
        {
            // A coordinator asked why the Clearance test reports 900 and the board shows 40 needs
            // to be able to open the 860.
            var hard = On(Duct, Beam, "Hard", 0, volume: 0.4);
            var clearance = On(Duct, Beam, "Clearance", 0.4);

            var result = DuplicateCollapse.Across(new[] { hard, clearance });

            Assert.Single(result.Collapsed);
            Assert.Single(result.FoldedInto);
            Assert.Same(hard, result.FoldedInto[clearance]);
        }

        [Fact]
        public void A_collapse_feeds_the_funnel_so_the_numbers_still_reconcile()
        {
            var hard = On(Duct, Beam, "Hard", 0, volume: 0.4);
            var clearance = On(Duct, Beam, "Clearance", 0.4);

            var collapse = DuplicateCollapse.Across(new[] { hard, clearance });
            var run = ClashPipeline.Run(collapse.Kept, new ClashPipelineOptions
            {
                Grouping = new[] { GroupingRules.ByLevel() },
                CollapsedDuplicates = collapse.Count,
            });

            Assert.Equal(2, run.Funnel.Input);           // still reconciles with what the engine said
            Assert.Equal(1, run.Funnel.Duplicates);
            Assert.Single(run.Groups[0].Items);
        }

        [Fact]
        public void A_band_of_zero_or_less_is_rejected_rather_than_silently_collapsing_everything()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => { DuplicateCollapse.Across(Array.Empty<ClashItem>(), 0); });
        }

        [Fact]
        public void An_empty_run_collapses_to_nothing()
        {
            var result = DuplicateCollapse.Across(Array.Empty<ClashItem>());

            Assert.Empty(result.Kept);
            Assert.Equal(0, result.Count);
        }
    }
}
