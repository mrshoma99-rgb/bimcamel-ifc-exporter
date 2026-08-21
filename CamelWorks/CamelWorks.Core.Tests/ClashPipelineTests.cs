using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Clash;
using CamelWorks.Core.Findings;
using CamelWorks.Core.Identity;
using Xunit;

namespace CamelWorks.Core.Tests
{
    /// <summary>Shared fixtures for the clash tests.</summary>
    internal static class ClashFixture
    {
        internal static readonly string ArchScope = ElementKey.ScopeOf(@"C:\p\ARCH.nwc");
        internal static readonly string MepScope = ElementKey.ScopeOf(@"C:\p\MEP.nwc");

        internal static ElementKey Arch(string name) =>
            ElementKey.FromTreePath(ArchScope, @"C:\p\ARCH.nwc", "L03", name);

        internal static ElementKey Mep(string name) =>
            ElementKey.FromTreePath(MepScope, @"C:\p\MEP.nwc", "L03", name);

        internal static ClashItem Item(
            string name, string? level = "L03", double x = 0, double y = 0, double z = 0,
            string modelA = "ARCH", string modelB = "MEP", string test = "L03 MEP v STR")
        {
            return new ClashItem(ClashKey.Create(Arch(name + "-a"), Mep(name + "-b"), x, y, z), test)
            {
                Level = level, ModelA = modelA, ModelB = modelB, X = x, Y = y, Z = z,
            };
        }

        internal static ClashPipelineOptions Stack(params IGroupingRule[] rules) =>
            new ClashPipelineOptions { Grouping = rules };
    }

    public class ClashPipelineTests
    {
        private static ClashItem Item(
            string name, string? level = "L03", double x = 0, double y = 0, double z = 0,
            string modelA = "ARCH", string modelB = "MEP") =>
            ClashFixture.Item(name, level, x, y, z, modelA, modelB);

        // ---------------------------------------------------------------------------------
        // Zero setup — the first click has to produce a grouped board
        // ---------------------------------------------------------------------------------

        [Fact]
        public void With_nothing_configured_the_default_stack_still_groups()
        {
            // A coordinator who just installed CamelWorks and opened a federation gets a grouped
            // board on the first click. Designing a rule stack is not a prerequisite.
            var result = ClashPipeline.Run(new[] { Item("a"), Item("b"), Item("c", x: 100) });

            Assert.Equal(2, result.Groups.Count);        // two proximity buckets
            Assert.Equal(3, result.Funnel.Input);
            Assert.Equal(0, result.Funnel.Suppressed);
        }

        [Fact]
        public void An_empty_run_produces_an_empty_board_not_an_error()
        {
            var result = ClashPipeline.Run(Array.Empty<ClashItem>());

            Assert.Empty(result.Groups);
            Assert.Equal(0, result.Funnel.Input);
        }

        [Fact]
        public void An_unconfigured_stack_and_an_empty_one_are_different_things()
        {
            // Null means "nobody chose", so the default runs. Empty means "explicitly flat", which
            // is a legitimate view — and collapsing the two would make a flat list impossible.
            var items = new[] { Item("a"), Item("b", x: 500) };

            var unconfigured = ClashPipeline.Run(items, new ClashPipelineOptions { Grouping = null });
            var flat = ClashPipeline.Run(items, ClashFixture.Stack());

            Assert.Equal(2, unconfigured.Groups.Count);
            Assert.Single(flat.Groups);
            Assert.Equal("Ungrouped", flat.Groups[0].Name);
            Assert.Equal(2, flat.Funnel.Ungroupable);
        }

        // ---------------------------------------------------------------------------------
        // Order: suppress before group, assign after
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Suppression_runs_before_grouping_so_ignored_results_never_reach_the_board()
        {
            // Grouping 4,000 into 400 does not stop 3,000 being things already decided not to be
            // clashes — and carry-over does not cover it, because a fresh re-export produces NEW
            // results matching a known-ignorable pattern, arriving as New every week.
            var items = new[] { Item("a"), Item("b"), Item("c") };
            items[0].OverlapVolume = 0.000_01;   // touching, not clashing
            items[1].OverlapVolume = 0.5;
            items[2].OverlapVolume = 0.5;

            var result = ClashPipeline.Run(items, new ClashPipelineOptions
            {
                Filters = new[]
                {
                    new FilterRule("Touching only", ClashPredicates.MaxOverlapVolume(0.0001), suppress: true),
                },
            });

            Assert.Equal(1, result.Funnel.Suppressed);
            Assert.Single(result.Suppressed);
            Assert.DoesNotContain(result.Groups.SelectMany(g => g.Items), i => ReferenceEquals(i, items[0]));
        }

        [Fact]
        public void Suppressed_results_are_retrievable_never_discarded()
        {
            // An unauditable hide-button is not something a coordinator can hand to a client.
            var item = Item("a");
            item.OverlapVolume = 0;

            var result = ClashPipeline.Run(new[] { item }, new ClashPipelineOptions
            {
                Filters = new[] { new FilterRule("Zero volume", ClashPredicates.MaxOverlapVolume(0), true) },
            });

            Assert.Same(item, Assert.Single(result.Suppressed));
            Assert.Contains("Zero volume", result.Funnel.RulesApplied);
        }

        [Fact]
        public void A_flag_rule_marks_without_removing()
        {
            var item = Item("a");
            item.SetsA.Add("Fire rated");

            var result = ClashPipeline.Run(new[] { item }, new ClashPipelineOptions
            {
                Filters = new[]
                {
                    new FilterRule("Fire", ClashPredicates.EitherInSet("Fire rated"), suppress: false),
                },
            });

            Assert.Single(result.Flagged);
            Assert.Empty(result.Suppressed);
            Assert.Single(result.Groups.SelectMany(g => g.Items));
        }

        [Fact]
        public void Assignment_happens_after_grouping_because_the_group_is_the_unit()
        {
            var items = new[] { Item("a"), Item("b") };
            foreach (var i in items) i.SetsA.Add("Ductwork");

            var options = ClashFixture.Stack(GroupingRules.ByLevel());
            options.Assigns = new[] { new AssignRule(ClashPredicates.EitherInSet("Ductwork"), "MEP", "High") };

            var result = ClashPipeline.Run(items, options);

            var group = Assert.Single(result.Groups);
            Assert.Equal("MEP", group.AssignedTo);
            Assert.Equal("High", group.Priority);
            Assert.False(group.AssignedByHand);
            Assert.Equal(1, result.Funnel.Assigned);
            Assert.Equal(0, result.Funnel.Unassigned);
        }

        [Fact]
        public void An_assign_rule_never_overwrites_a_human_decision()
        {
            // Rules fill the gaps; they do not relitigate what somebody already decided. The rule
            // below matches, and would assign MEP if it were allowed to.
            var item = Item("a");
            item.SetsA.Add("Ductwork");

            var rules = new[] { new AssignRule(ClashPredicates.EitherInSet("Ductwork"), "MEP", "Low") };

            var first = ClashPipeline.Run(new[] { item }, new ClashPipelineOptions
            {
                Grouping = new[] { GroupingRules.ByLevel() },
                Assigns = rules,
            });
            Assert.Equal("MEP", first.Groups[0].AssignedTo);   // the rule does fire, unopposed

            // A person overrules it, and that decision comes back into the next run.
            var decisions = new Dictionary<string, GroupDecision>(StringComparer.Ordinal)
            {
                [first.Groups[0].Name] = new GroupDecision("Structures", "High"),
            };

            var again = ClashPipeline.Run(new[] { item }, new ClashPipelineOptions
            {
                Grouping = new[] { GroupingRules.ByLevel() },
                Assigns = rules,
                Decisions = decisions,
            });

            Assert.Equal("Structures", again.Groups[0].AssignedTo);
            Assert.Equal("High", again.Groups[0].Priority);
            Assert.True(again.Groups[0].AssignedByHand);
            Assert.Equal(1, again.Funnel.Assigned);
        }

        [Fact]
        public void A_decision_about_a_group_that_no_longer_exists_is_simply_ignored()
        {
            // The stack changed, so last week's group name is gone. That is not an error and must
            // not resurrect an empty group on the board.
            var decisions = new Dictionary<string, GroupDecision>(StringComparer.Ordinal)
            {
                ["L07"] = new GroupDecision("Structures"),
            };

            var result = ClashPipeline.Run(new[] { Item("a") }, new ClashPipelineOptions
            {
                Grouping = new[] { GroupingRules.ByLevel() },
                Decisions = decisions,
            });

            Assert.Equal("L03", Assert.Single(result.Groups).Name);
            Assert.Null(result.Groups[0].AssignedTo);
        }

        // ---------------------------------------------------------------------------------
        // Grouping
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Chained_rules_build_a_subgroup_name()
        {
            // Native Navisworks has no subgroups at all, which is why one pipe through one slab
            // arrives as a thousand rows.
            var item = Item("a");
            item.Grid = "C4";

            var result = ClashPipeline.Run(new[] { item },
                ClashFixture.Stack(GroupingRules.ByLevel(), GroupingRules.ByGrid(), GroupingRules.ByModelPair()));

            Assert.Equal("L03 · C4 · ARCH v MEP", result.Groups[0].Name);
        }

        [Fact]
        public void A_rule_that_does_not_apply_contributes_no_segment()
        {
            // A model with no grids should produce "L03", not "L03 · Unknown".
            var item = Item("a");
            item.Grid = null;

            var result = ClashPipeline.Run(new[] { item },
                ClashFixture.Stack(GroupingRules.ByLevel(), GroupingRules.ByGrid()));

            Assert.Equal("L03", result.Groups[0].Name);
        }

        [Fact]
        public void Results_no_rule_can_place_are_counted_rather_than_hidden()
        {
            // A model with no levels produces thousands of these, and the cause is fixable — so it
            // is reported rather than swept into an "Unknown" bucket.
            var result = ClashPipeline.Run(new[] { Item("a", level: null) },
                ClashFixture.Stack(GroupingRules.ByLevel()));

            Assert.Equal(1, result.Funnel.Ungroupable);
            Assert.Equal("Ungrouped", result.Groups[0].Name);
        }

        [Fact]
        public void The_model_pair_is_order_independent()
        {
            var ab = Item("a", modelA: "ARCH", modelB: "MEP");
            var ba = Item("b", modelA: "MEP", modelB: "ARCH");

            var result = ClashPipeline.Run(new[] { ab, ba }, ClashFixture.Stack(GroupingRules.ByModelPair()));

            Assert.Single(result.Groups);
            Assert.Equal("ARCH v MEP", result.Groups[0].Name);
        }

        [Fact]
        public void Proximity_grouping_is_stable_across_runs_of_identical_data()
        {
            // A clustering pass would be order-dependent and produce different groups on a re-run
            // of the same data, which is the one thing grouping must never do.
            var items = new[] { Item("a", x: 1), Item("b", x: 2), Item("c", x: 30) };

            var first = ClashPipeline.Run(items, ClashFixture.Stack(GroupingRules.ByProximity(5)))
                .Groups.Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var second = ClashPipeline.Run(items.Reverse(), ClashFixture.Stack(GroupingRules.ByProximity(5)))
                .Groups.Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            Assert.Equal(first, second);
        }

        [Fact]
        public void Same_item_grouping_collects_every_result_on_one_pair()
        {
            // "One pipe passes through forty joists", which native reports as forty unrelated rows.
            var a = ClashFixture.Arch("Pipe");
            var b = ClashFixture.Mep("Joist");

            var items = Enumerable.Range(0, 5)
                .Select(i => new ClashItem(ClashKey.Create(a, b, i * 3.0, 0, 0), "T") { Level = "L03" })
                .ToList();

            var result = ClashPipeline.Run(items, ClashFixture.Stack(GroupingRules.BySameItem()));

            Assert.Single(result.Groups);
            Assert.Equal(5, result.Groups[0].Items.Count);
        }

        [Fact]
        public void A_pinned_group_is_never_re_derived()
        {
            // This is exactly what "keep existing groups" means; without it every re-run destroys
            // last week's hand grouping.
            var items = new[] { Item("a"), Item("b", x: 500) };

            var result = ClashPipeline.Run(items, new ClashPipelineOptions
            {
                PinnedGroups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["Riser 3 — agreed with STR"] = new[] { items[0].Key.ToString(), items[1].Key.ToString() },
                },
            });

            var group = Assert.Single(result.Groups);
            Assert.Equal("Riser 3 — agreed with STR", group.Name);
            Assert.True(group.IsPinned);
            Assert.Equal(2, group.Items.Count);   // together despite being 500 m apart
        }

        [Fact]
        public void A_pin_made_now_beats_a_group_carried_from_last_run()
        {
            // Both are hand decisions, so the newer one wins. Anything else would make moving a
            // result out of last week's group impossible.
            var item = Item("a");
            item.CarriedGroup = "Riser 3";

            var result = ClashPipeline.Run(new[] { item }, new ClashPipelineOptions
            {
                PinnedGroups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["Riser 4"] = new[] { item.Key.ToString() },
                },
            });

            Assert.Equal("Riser 4", Assert.Single(result.Groups).Name);
        }

        [Fact]
        public void A_carried_group_outranks_the_stack_and_stays_pinned()
        {
            var carried = Item("a");
            carried.CarriedGroup = "Riser 3";

            var result = ClashPipeline.Run(new[] { carried, Item("b") },
                ClashFixture.Stack(GroupingRules.ByLevel()));

            var riser = result.Groups.Single(g => g.Name == "Riser 3");
            Assert.True(riser.IsPinned);
            Assert.Single(riser.Items);
            Assert.False(result.Groups.Single(g => g.Name == "L03").IsPinned);
        }

        [Fact]
        public void A_group_id_is_derived_from_its_name_so_it_is_stable()
        {
            var a = ClashPipeline.Run(new[] { Item("x") }, ClashFixture.Stack(GroupingRules.ByLevel()));
            var b = ClashPipeline.Run(new[] { Item("y") }, ClashFixture.Stack(GroupingRules.ByLevel()));

            Assert.Equal(a.Groups[0].Id, b.Groups[0].Id);
        }

        // ---------------------------------------------------------------------------------
        // The funnel
        // ---------------------------------------------------------------------------------

        [Fact]
        public void The_funnel_reconciles_the_board_with_the_model()
        {
            // This is what a coordinator defends when a client asks why the board says 2 and the
            // model says 4.
            var items = new[] { Item("a"), Item("b"), Item("c"), Item("d") };
            items[0].OverlapVolume = 0;
            items[1].OverlapVolume = 0;
            foreach (var i in items.Skip(2)) { i.OverlapVolume = 1; i.SetsA.Add("Ductwork"); }

            var result = ClashPipeline.Run(items, new ClashPipelineOptions
            {
                Filters = new[] { new FilterRule("Zero volume", ClashPredicates.MaxOverlapVolume(0), true) },
                Grouping = new[] { GroupingRules.ByLevel() },
                Assigns = new[] { new AssignRule(ClashPredicates.EitherInSet("Ductwork"), "MEP") },
            });

            Assert.Equal(4, result.Funnel.Input);
            Assert.Equal(2, result.Funnel.Suppressed);
            Assert.Equal(1, result.Funnel.Groups);
            Assert.Equal(1, result.Funnel.Assigned);
            Assert.Equal(0, result.Funnel.Unassigned);

            var text = result.Funnel.ToString();
            Assert.Contains("4 results", text, StringComparison.Ordinal);
            Assert.Contains("2 suppressed by 1 rules", text, StringComparison.Ordinal);
            Assert.Contains("1 groups", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Collapsed_duplicates_are_counted_in_the_funnel_not_hidden_from_it()
        {
            // A collapse that removes rows and leaves no trace in the numbers is the same
            // unauditable hide-button the suppressed count exists to avoid.
            var result = ClashPipeline.Run(new[] { Item("a") }, new ClashPipelineOptions
            {
                Grouping = new[] { GroupingRules.ByLevel() },
                CollapsedDuplicates = 860,
            });

            Assert.Equal(861, result.Funnel.Input);
            Assert.Equal(860, result.Funnel.Duplicates);
            Assert.Contains("860 duplicates collapsed", result.Funnel.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Unassigned_is_the_number_that_decides_whether_Thursday_is_finished()
        {
            var result = ClashPipeline.Run(new[] { Item("a"), Item("b", x: 500) },
                ClashFixture.Stack(GroupingRules.ByProximity(5)));

            Assert.Equal(2, result.Funnel.Groups);
            Assert.Equal(0, result.Funnel.Assigned);
            Assert.Equal(2, result.Funnel.Unassigned);
        }
    }

    public class ClashPredicateTests
    {
        private static ClashItem Bare() =>
            new ClashItem(default, "T") { ModelA = "ARCH", ModelB = "MEP" };

        [Fact]
        public void A_predicate_is_false_when_the_data_it_needs_is_missing()
        {
            // Treating an absent overlap volume as zero would suppress results nobody asked to
            // suppress — silently, which is the worst way for a filter to be wrong.
            var item = Bare();
            Assert.Null(item.OverlapVolume);

            Assert.False(ClashPredicates.MaxOverlapVolume(1).Matches(item));
            Assert.False(ClashPredicates.DistanceBetween(-1, 1).Matches(item));
            Assert.False(ClashPredicates.AngleBetween(0, 90).Matches(item));
        }

        [Fact]
        public void An_undefined_crossing_angle_is_not_zero()
        {
            // A slab has no dominant axis. A rule about crossing angle must not treat that as a
            // perfect right angle or a perfect parallel.
            var slab = Bare();
            slab.CrossingAngleDegrees = null;

            Assert.False(ClashPredicates.AngleBetween(0, 5).Matches(slab));
        }

        [Fact]
        public void An_empty_All_matches_nothing_rather_than_everything()
        {
            // The mathematical identity is "everything", and a half-built suppression rule that
            // silently suppresses the entire board is not a defensible default.
            Assert.False(ClashPredicates.All().Matches(Bare()));
            Assert.False(ClashPredicates.Any().Matches(Bare()));
        }

        [Fact]
        public void Same_model_detects_the_usual_modelling_artefact()
        {
            var item = Bare();
            Assert.False(ClashPredicates.SameModel().Matches(item));

            item.ModelB = "ARCH";
            Assert.True(ClashPredicates.SameModel().Matches(item));
        }

        [Fact]
        public void Set_membership_distinguishes_either_from_both()
        {
            var item = Bare();
            item.SetsA.Add("Insulation");

            Assert.True(ClashPredicates.EitherInSet("Insulation").Matches(item));
            Assert.False(ClashPredicates.BothInSet("Insulation").Matches(item));

            item.SetsB.Add("insulation");   // case-insensitive
            Assert.True(ClashPredicates.BothInSet("Insulation").Matches(item));
        }

        [Fact]
        public void Property_matching_handles_exact_contains_and_absence()
        {
            var item = Bare();
            item.Properties["Element/Type"] = "Rectangular Duct";

            Assert.True(ClashPredicates.PropertyEquals("Element/Type", "rectangular duct").Matches(item));
            Assert.True(ClashPredicates.PropertyContains("Element/Type", "Duct").Matches(item));
            Assert.False(ClashPredicates.PropertyEquals("Element/Type", "Pipe").Matches(item));
            Assert.False(ClashPredicates.PropertyEquals("Element/Missing", "x").Matches(item));
        }

        [Fact]
        public void Composition_reads_as_the_rule_the_user_wrote()
        {
            var rule = ClashPredicates.All(
                ClashPredicates.SameModel(),
                ClashPredicates.Not(ClashPredicates.EitherInSet("Structure")));

            Assert.Contains("both items in the same model", rule.Describe(), StringComparison.Ordinal);
            Assert.Contains("not (", rule.Describe(), StringComparison.Ordinal);
        }
    }
}
