using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Clash;
using CamelWorks.Core.Findings;
using CamelWorks.Core.Identity;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class ClashCarryOverTests
    {
        private static readonly ElementKey Pipe = ClashFixture.Mep("Pipe");
        private static readonly ElementKey Beam = ClashFixture.Arch("Beam");

        // One conflict on one pair, at a chosen point. Cell size is ClashKey.DefaultCell (0.25 m),
        // so x = 0.3 lands one cell over from x = 0 and x = -0.3 lands one cell under.
        private static ClashItem At(double x, string test = "Hard") =>
            new ClashItem(ClashKey.Create(Pipe, Beam, x, 0, 0), test) { Level = "L03", X = x };

        private static ClashSnapshot SnapshotOf(FindingStatus status, double x = 0,
                                                string? group = null, bool pinned = false) =>
            new ClashSnapshot(new[] { new ClashRecord(ClashKey.Create(Pipe, Beam, x, 0, 0), status, group, pinned) });

        // ---------------------------------------------------------------------------------
        // The four states
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_first_run_has_no_history_so_everything_is_new()
        {
            var item = At(0);

            var delta = ClashCarryOver.Apply(new[] { item }, null);

            Assert.Same(item, Assert.Single(delta.New));
            Assert.Equal(FindingStatus.New, item.Status);
            Assert.Empty(delta.Resolved);
        }

        [Fact]
        public void A_result_that_was_there_before_is_active_not_new()
        {
            // The whole point. Without this, every re-export presents last month's board as if
            // nobody had ever looked at it.
            var item = At(0);

            var delta = ClashCarryOver.Apply(new[] { item }, SnapshotOf(FindingStatus.Active));

            Assert.Empty(delta.New);
            Assert.Same(item, Assert.Single(delta.Persisting));
            Assert.Equal(FindingStatus.Active, item.Status);
        }

        [Fact]
        public void Only_a_human_judgement_carries_as_truth()
        {
            // Reviewed and Approved are the only two the engine does not recompute, so they are the
            // only two safe to read back. Carrying "Resolved" would hide a live conflict.
            var reviewed = At(0);
            ClashCarryOver.Apply(new[] { reviewed }, SnapshotOf(FindingStatus.Reviewed));
            Assert.Equal(FindingStatus.Reviewed, reviewed.Status);

            var approved = At(0);
            ClashCarryOver.Apply(new[] { approved }, SnapshotOf(FindingStatus.Approved));
            Assert.Equal(FindingStatus.Approved, approved.Status);

            var wasNew = At(0);
            ClashCarryOver.Apply(new[] { wasNew }, SnapshotOf(FindingStatus.New));
            Assert.Equal(FindingStatus.Active, wasNew.Status);
        }

        [Fact]
        public void A_resolved_result_that_comes_back_is_regressed_not_new()
        {
            // A fix that got undone. Native clash detection has no memory of it at all — the result
            // reappears as New, and nothing distinguishes it from something nobody has looked at.
            var item = At(0);

            var delta = ClashCarryOver.Apply(new[] { item }, SnapshotOf(FindingStatus.Resolved));

            Assert.Same(item, Assert.Single(delta.Regressed));
            Assert.Empty(delta.New);
            Assert.Empty(delta.Persisting);
            Assert.Equal(FindingStatus.Active, item.Status);   // has a history, so not New
            Assert.Contains("1 regressed", delta.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void An_approved_result_that_stays_is_persisting_not_regressed()
        {
            // Approved means "accepted, no change needed", so it is expected to still be there.
            // Calling that a regression would flood the report with things nobody needs to see.
            var item = At(0);

            var delta = ClashCarryOver.Apply(new[] { item }, SnapshotOf(FindingStatus.Approved));

            Assert.Empty(delta.Regressed);
            Assert.Same(item, Assert.Single(delta.Persisting));
        }

        [Fact]
        public void A_result_that_vanished_is_reported_resolved()
        {
            var delta = ClashCarryOver.Apply(Array.Empty<ClashItem>(), SnapshotOf(FindingStatus.Active));

            var gone = Assert.Single(delta.Resolved);
            Assert.Equal(FindingStatus.Active, gone.Status);
            Assert.Contains("1 resolved", delta.ToString(), StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // Matching
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_point_that_drifted_across_a_cell_boundary_still_matches()
        {
            // Quantising creates a boundary problem; it is handled at match time, not key time.
            var moved = At(0.3);   // one cell over from where it was

            var delta = ClashCarryOver.Apply(new[] { moved }, SnapshotOf(FindingStatus.Reviewed));

            Assert.Same(moved, Assert.Single(delta.Persisting));
            Assert.Equal(FindingStatus.Reviewed, moved.Status);
            Assert.Equal(1, delta.MatchedOnNeighbourCell);
        }

        [Fact]
        public void Every_exact_match_is_claimed_before_any_tolerance_is_spent()
        {
            // The drifted result sorts FIRST (its cell is -1, and '-' sorts before '0'), so a
            // per-item match would let it claim the record belonging to the result sitting exactly
            // on it. Two passes stop that.
            var drifted = At(-0.3);
            var exact = At(0);

            var delta = ClashCarryOver.Apply(new[] { drifted, exact }, SnapshotOf(FindingStatus.Reviewed));

            Assert.Equal(FindingStatus.Reviewed, exact.Status);
            Assert.Equal(FindingStatus.New, drifted.Status);
            Assert.Equal(0, delta.MatchedOnNeighbourCell);
        }

        [Fact]
        public void One_previous_result_can_only_be_inherited_once()
        {
            // Two results either side of a seam must not both inherit the same history — that
            // would duplicate somebody's sign-off onto a conflict nobody signed off.
            var below = At(-0.3);
            var above = At(0.3);

            var delta = ClashCarryOver.Apply(new[] { below, above }, SnapshotOf(FindingStatus.Approved));

            Assert.Single(delta.Persisting);
            Assert.Single(delta.New);
            Assert.Equal(1, delta.MatchedOnNeighbourCell);
        }

        [Fact]
        public void Two_results_sharing_a_key_exactly_do_not_both_inherit_one_history()
        {
            // Two tests can report the same pair in the same cell. The first claims the record;
            // the second is a new result, not a second inheritor.
            var hard = At(0, "Hard");
            var clearance = At(0, "Clearance");

            var delta = ClashCarryOver.Apply(new[] { hard, clearance }, SnapshotOf(FindingStatus.Reviewed));

            Assert.Single(delta.Persisting);
            Assert.Single(delta.New);
        }

        [Fact]
        public void Carry_over_is_independent_of_the_order_results_arrive_in()
        {
            // The board must not depend on the order the engine happened to enumerate results in.
            var snapshot = SnapshotOf(FindingStatus.Approved);

            var forward = new[] { At(-0.3), At(0.3) };
            ClashCarryOver.Apply(forward, snapshot);

            var reversed = new[] { At(0.3), At(-0.3) };
            ClashCarryOver.Apply(reversed, snapshot);

            Assert.Equal(
                forward.Where(i => i.Status == FindingStatus.Approved).Select(i => i.Key.ToString()),
                reversed.Where(i => i.Status == FindingStatus.Approved).Select(i => i.Key.ToString()));
        }

        [Fact]
        public void A_match_on_a_geometry_key_is_counted_because_it_is_only_a_proposal()
        {
            // A rung-3 match carrying somebody's Approved onto what might be a different element is
            // the one way carry-over can quietly do harm.
            var a = ElementKey.FromGeometry(ClashFixture.ArchScope, "Beams", "UB 305x165", 6, 0.3, 0.165);
            var b = ElementKey.FromGeometry(ClashFixture.MepScope, "Pipes", "DN150", 4, 0.15, 0.15);

            var item = new ClashItem(ClashKey.Create(a, b, 0, 0, 0), "Hard");
            var snapshot = new ClashSnapshot(new[]
            {
                new ClashRecord(ClashKey.Create(a, b, 0, 0, 0), FindingStatus.Approved),
            });

            var delta = ClashCarryOver.Apply(new[] { item }, snapshot);

            Assert.Equal(KeyRung.Geometry, item.Key.WeakestRung);
            Assert.Equal(1, delta.WeakMatches);
        }

        // ---------------------------------------------------------------------------------
        // Groups: carry-over runs before the stack re-derives
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_hand_made_group_survives_a_re_run_but_a_derived_one_re_derives()
        {
            // Carrying a derived name would freeze the board against its own rules — adding a Grid
            // rule would then change nothing.
            var handMade = At(0);
            ClashCarryOver.Apply(new[] { handMade }, SnapshotOf(FindingStatus.Active, group: "Riser 3", pinned: true));
            Assert.Equal("Riser 3", handMade.CarriedGroup);

            var derived = At(0);
            ClashCarryOver.Apply(new[] { derived }, SnapshotOf(FindingStatus.Active, group: "L03 · C4", pinned: false));
            Assert.Null(derived.CarriedGroup);
        }

        [Fact]
        public void A_hand_group_and_a_decision_both_survive_a_full_round_trip()
        {
            // The end-to-end guarantee: run, hand-group, hand-assign, snapshot, re-run — and the
            // week's work is still on the board.
            var week1 = new[] { ClashFixture.Item("a"), ClashFixture.Item("b", x: 500) };

            var first = ClashPipeline.Run(week1, new ClashPipelineOptions
            {
                PinnedGroups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["Riser 3"] = new[] { week1[0].Key.ToString(), week1[1].Key.ToString() },
                },
            });
            first.Groups[0].AssignByHand("Structures");

            var snapshot = ClashSnapshot.Of(first, "Week 1");
            Assert.Equal("Week 1", snapshot.Label);

            // A fresh export: same elements, same places, nothing pinned in this session.
            var week2 = new[] { ClashFixture.Item("a"), ClashFixture.Item("b", x: 500) };
            var delta = ClashCarryOver.Apply(week2, snapshot);

            var second = ClashPipeline.Run(week2, new ClashPipelineOptions { Decisions = snapshot.Decisions });

            Assert.Equal(2, delta.Persisting.Count);
            var group = Assert.Single(second.Groups);
            Assert.Equal("Riser 3", group.Name);
            Assert.True(group.IsPinned);
            Assert.Equal("Structures", group.AssignedTo);
            Assert.True(group.AssignedByHand);
        }

        [Fact]
        public void A_snapshot_records_suppressed_results_so_they_do_not_return_as_new()
        {
            // Drop them and every one arrives as New the moment somebody turns the rule off.
            var item = ClashFixture.Item("a");
            item.OverlapVolume = 0;

            var run = ClashPipeline.Run(new[] { item }, new ClashPipelineOptions
            {
                Filters = new[] { new FilterRule("Zero volume", ClashPredicates.MaxOverlapVolume(0), true) },
            });

            var snapshot = ClashSnapshot.Of(run);
            Assert.Single(snapshot.Records);

            var next = ClashFixture.Item("a");
            var delta = ClashCarryOver.Apply(new[] { next }, snapshot);

            Assert.Empty(delta.New);
            Assert.Single(delta.Persisting);
        }

        [Fact]
        public void An_empty_snapshot_is_not_an_error()
        {
            var delta = ClashCarryOver.Apply(new[] { At(0) }, ClashSnapshot.Empty);

            Assert.Single(delta.New);
            Assert.Empty(delta.Resolved);
        }
    }
}
