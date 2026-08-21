using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Appearance;
using CamelWorks.Core.Clash;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Project;
using CamelWorks.Core.Sets;
using CamelWorks.Core.Store;
using CamelWorks.Core.Testing;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class DisciplineTests
    {
        [Theory]
        [InlineData("AR-Tower-01.nwc", "Architecture")]
        [InlineData("PRJ_STR_Podium.ifc", "Structure")]
        [InlineData("mech-level-03.nwd", "Mechanical")]
        [InlineData("Site_HVAC_Riser.rvt", "Mechanical")]
        [InlineData("C:/models/PRJ-FP-Sprinklers.nwc", "Fire")]
        public void Reads_the_discipline_out_of_the_file_name(string name, string expected) =>
            Assert.Equal(expected, Disciplines.Guess(name));

        [Fact]
        public void A_single_letter_only_counts_as_the_first_token()
        {
            // "Tower A" is a building, not an architectural model, and a product that guesses
            // otherwise has to be corrected on every project — which is worse than not guessing.
            Assert.Null(Disciplines.Guess("Tower A.nwc"));
            Assert.Equal("Architecture", Disciplines.Guess("A-Tower.nwc"));
        }

        [Fact]
        public void Longer_evidence_wins()
        {
            // A stray "S" earlier in the name must not beat the word "MECHANICAL" later in it.
            Assert.Equal("Mechanical", Disciplines.Guess("S-Block Mechanical.nwc"));
        }

        [Fact]
        public void Nothing_recognisable_is_null_rather_than_a_guess() =>
            Assert.Null(Disciplines.Guess("Model 42.nwd"));
    }

    public class ProjectProfileTests
    {
        private sealed class Source : IModelSource
        {
            public Source(string name, string path) { DisplayName = name; SourcePath = path; }

            public string DisplayName { get; }
            public string SourcePath { get; }
            public string Scope => ElementKey.ScopeOf(SourcePath);
        }

        [Fact]
        public void An_empty_document_still_produces_a_complete_profile()
        {
            // The zero-setup rule at its hardest point: nothing is open, and every feature that
            // reads the profile must still get an answer.
            var profile = ProjectProfile.Derive(Array.Empty<IModelSource>());

            Assert.Equal("Untitled project", profile.ProjectName);
            Assert.Equal(0.010, profile.ClashTolerance);
            Assert.Equal(5.0, profile.ClashProximity);
            Assert.Empty(profile.Parties);
            Assert.Contains(profile.Settings, s => s.Key == ProfileKeys.LevelSource);
        }

        [Fact]
        public void Parties_come_from_the_disciplines_the_model_names_imply()
        {
            var profile = ProjectProfile.Derive(new IModelSource[]
            {
                new Source("AR-Tower.nwc", @"C:\Jobs\Riverside\AR-Tower.nwc"),
                new Source("STR-Tower.nwc", @"C:\Jobs\Riverside\STR-Tower.nwc"),
                new Source("MEP-Mech.nwc", @"C:\Jobs\Riverside\MEP-Mech.nwc"),
            });

            Assert.Equal("Riverside", profile.ProjectName);
            Assert.Equal(new[] { "Architecture", "Structure", "Mechanical" }, profile.Parties.ToArray());
        }

        [Fact]
        public void An_override_replaces_the_derived_value_and_says_so()
        {
            var profile = ProjectProfile.Derive(Array.Empty<IModelSource>());
            var tolerance = profile.Find(ProfileKeys.ClashTolerance);

            Assert.NotNull(tolerance);
            Assert.Equal("derived", tolerance!.Source);

            tolerance.Override = "0.025";

            Assert.Equal(0.025, profile.ClashTolerance);
            Assert.Equal("you", tolerance.Source);
            Assert.Equal("0.010", tolerance.Derived);
        }

        [Fact]
        public void Only_overrides_are_saved_and_they_come_back_onto_a_fresh_derivation()
        {
            var models = new IModelSource[] { new Source("AR-Tower.nwc", @"C:\Jobs\Riverside\AR-Tower.nwc") };

            var profile = ProjectProfile.Derive(models);
            profile.Find(ProfileKeys.ProjectName)!.Override = "Riverside Phase 2";

            var saved = profile.OverridesToJson();

            Assert.Equal(new[] { ProfileKeys.ProjectName }, saved.Keys.ToArray());

            var reopened = ProjectProfile.Derive(models);
            reopened.ApplyOverrides(saved);

            Assert.Equal("Riverside Phase 2", reopened.ProjectName);
        }

        [Fact]
        public void An_override_for_a_model_that_is_no_longer_loaded_is_kept_aside_not_dropped()
        {
            // The model is unloaded for an afternoon; its discipline must still be there tomorrow.
            var profile = ProjectProfile.Derive(Array.Empty<IModelSource>());

            var saved = JsonValue.Object().Set(ProfileKeys.DisciplinePrefix + "gone", JsonValue.String("Plumbing"));

            var unknown = new Dictionary<string, string>(StringComparer.Ordinal);
            profile.ApplyOverrides(saved, unknown);

            Assert.Equal("Plumbing", unknown[ProfileKeys.DisciplinePrefix + "gone"]);
        }
    }

    public class ActivityLogTests
    {
        private static long Ticks(double hours) => TimeSpan.FromHours(hours).Ticks;

        [Fact]
        public void Never_is_said_plainly() => Assert.Equal("never", ActivityLog.Ago(0, Ticks(100)));

        [Theory]
        [InlineData(0.001, "just now")]
        [InlineData(0.5, "30 minutes ago")]
        [InlineData(1, "1 hour ago")]
        [InlineData(5, "5 hours ago")]
        [InlineData(30, "yesterday")]
        [InlineData(24 * 5, "5 days ago")]
        public void Elapsed_time_reads_the_way_a_person_would_say_it(double hoursAgo, string expected)
        {
            var now = Ticks(24 * 400);
            Assert.Equal(expected, ActivityLog.Ago(now - Ticks(hoursAgo), now));
        }

        [Fact]
        public void The_newest_entry_of_a_kind_is_the_one_reported()
        {
            var log = new ActivityLog();
            log.Record(new Activity(ActivityKind.Report, 100, "old report"));
            log.Record(new Activity(ActivityKind.Regroup, 200, "a regroup"));
            log.Record(new Activity(ActivityKind.Report, 300, "new report"));

            Assert.Equal("new report", log.Latest(ActivityKind.Report)!.Summary);
            Assert.Null(log.Latest(ActivityKind.Reconcile));
        }

        [Fact]
        public void The_log_stops_growing_and_keeps_the_newest()
        {
            var log = new ActivityLog();

            for (var i = 0; i < ActivityLog.Keep + 50; i++)
                log.Record(new Activity(ActivityKind.Write, i, "write " + i));

            Assert.Equal(ActivityLog.Keep, log.Entries.Count);
            Assert.Equal("write " + (ActivityLog.Keep + 49), log.Entries[0].Summary);
        }

        [Fact]
        public void A_log_survives_a_round_trip()
        {
            var log = new ActivityLog();
            log.Record(new Activity(ActivityKind.Report, 4242, "wrote a PDF", "12 pages"));

            var back = ActivityLog.FromJson(log.ToJson());

            Assert.Single(back.Entries);
            Assert.Equal(4242, back.Entries[0].WhenTicks);
            Assert.Equal("12 pages", back.Entries[0].Detail);
        }
    }

    public class ProjectStoreTests
    {
        [Fact]
        public void With_nowhere_to_save_the_store_still_works_for_this_session()
        {
            var store = ProjectStore.Open(new InMemoryFileSystem(), null, null);

            Assert.True(store.IsMemoryOnly);

            store.Section(ProjectStore.SetsSection).Set("anything", JsonValue.String("kept in memory"));

            Assert.False(store.Save());
            Assert.Contains("nowhere to save", store.LastSaveProblem);
            Assert.Equal("kept in memory", store.Section(ProjectStore.SetsSection)["anything"].AsString());
        }

        [Fact]
        public void The_sidecar_goes_beside_the_document()
        {
            var (path, where) = ProjectStore.Locate(@"C:\Jobs\Riverside\Federated.nwf", null);

            Assert.NotNull(path);
            Assert.Contains(ProjectStore.SidecarFolder, path);
            Assert.EndsWith("Federated" + ProjectStore.Extension, path);
            Assert.Contains("beside the document", where);
        }

        [Fact]
        public void Nothing_is_written_until_something_is_saved()
        {
            var fs = new InMemoryFileSystem();

            var store = ProjectStore.Open(fs, @"C:\Jobs\Riverside\Federated.nwf", null);

            Assert.Equal(LoadOutcome.Missing, store.Outcome);
            Assert.Empty(fs.AllPaths);

            store.Section(ProjectStore.ProfileSection).Set(ProfileKeys.ProjectName, JsonValue.String("Riverside"));

            Assert.True(store.Save());
            Assert.Single(fs.AllPaths);
        }

        [Fact]
        public void A_file_from_a_newer_build_opens_read_only_and_refuses_to_be_written_over()
        {
            var path = ProjectStore.Locate(@"C:\Jobs\Riverside\Federated.nwf", null).Path!;

            var fs = new InMemoryFileSystem().With(path, "{\"schemaVersion\":999,\"sets\":[]}");

            var store = ProjectStore.Open(fs, @"C:\Jobs\Riverside\Federated.nwf", null);

            Assert.True(store.IsReadOnly);
            Assert.False(store.Save());
            Assert.Contains("newer CamelWorks", store.LastSaveProblem);
            Assert.Equal("{\"schemaVersion\":999,\"sets\":[]}", fs.ReadAllText(path));
        }

        [Fact]
        public void A_synced_folder_is_named_as_one_and_the_reason_is_given()
        {
            var store = ProjectStore.Open(new InMemoryFileSystem(),
                @"C:\Users\anna\OneDrive - Riverside\Jobs\Federated.nwf", null);

            Assert.Equal(SubstrateKind.SyncRoot, store.Substrate);
            Assert.Contains("will not take a lock", store.ConcurrencyNote);
        }

        [Fact]
        public void An_unsaved_document_falls_back_to_the_users_own_folder()
        {
            var store = ProjectStore.Open(new InMemoryFileSystem(), null, @"C:\Users\anna\AppData\CamelWorks");

            Assert.False(store.IsMemoryOnly);
            Assert.EndsWith(ProjectStore.UnsavedName, store.Path);
            Assert.True(store.Save());
        }

        [Fact]
        public void Activity_is_carried_through_a_save_and_reopen()
        {
            var fs = new InMemoryFileSystem();

            var store = ProjectStore.Open(fs, @"C:\Jobs\Riverside\Federated.nwf", null);
            Assert.True(store.Record(ActivityKind.Regroup, 777, "grouped 412 results into 38"));

            var reopened = ProjectStore.Open(fs, @"C:\Jobs\Riverside\Federated.nwf", null);

            Assert.Equal("grouped 412 results into 38", reopened.Activity.Latest(ActivityKind.Regroup)!.Summary);
        }
    }

    public class ClashRuleSetTests
    {
        [Fact]
        public void An_empty_rule_set_still_runs_the_default_grouping_stack()
        {
            // What makes the board open populated on a model nobody has configured.
            var options = new ClashRuleSet().ToOptions();

            Assert.Equal(3, options.Grouping!.Count);
            Assert.Empty(options.Filters);
        }

        [Fact]
        public void A_rule_this_build_does_not_understand_matches_nothing()
        {
            // The direction of the failure is the point. A suppress rule that fell back to
            // "everything" would empty the board and look like there were no clashes.
            var spec = new PredicateSpec("something-from-2029");

            var item = new ClashItem(
                ClashKey.Create(ElementKey.FromTreePath("m", null, "a"),
                                ElementKey.FromTreePath("m", null, "b"), 1, 2, 3),
                "Ducts vs Structure");

            Assert.False(spec.Build().Matches(item));
        }

        [Fact]
        public void An_unknown_grouping_rule_is_dropped_rather_than_stubbed()
        {
            Assert.Null(new GroupingSpec("something-from-2029").Build());
            Assert.NotNull(new GroupingSpec("level").Build());
        }

        [Fact]
        public void A_property_grouping_with_no_property_named_is_not_built()
        {
            Assert.Null(new GroupingSpec("property").Build());
            Assert.NotNull(new GroupingSpec("property") { Name = "System" }.Build());
        }

        [Fact]
        public void Rules_survive_a_round_trip_with_their_nesting_intact()
        {
            var rules = new ClashRuleSet();

            var when = new PredicateSpec("all");
            when.Parts.Add(new PredicateSpec("maxVolume") { A = 0.0005 });
            when.Parts.Add(new PredicateSpec("not"));
            when.Parts[1].Parts.Add(new PredicateSpec("category") { Name = "Insulation" });

            rules.Filters.Add(new FilterSpec("Tolerance noise", when, suppress: true));
            rules.Grouping.Add(new GroupingSpec("proximity") { A = 2.5 });
            rules.Assigns.Add(new AssignSpec(new PredicateSpec("bothInSet") { Name = "Level 3" }, "Mechanical", "High"));
            rules.Pinned["Level 3 riser"] = new[] { "c1", "c2" };

            var back = ClashRuleSet.FromJson(rules.ToJson());

            Assert.Equal("Tolerance noise", back.Filters[0].Name);
            Assert.True(back.Filters[0].Suppress);
            Assert.Equal("Insulation", back.Filters[0].When.Parts[1].Parts[0].Name);
            Assert.Equal(2.5, back.Grouping[0].A);
            Assert.Equal("High", back.Assigns[0].Priority);
            Assert.Equal(new[] { "c1", "c2" }, back.Pinned["Level 3 riser"].ToArray());
        }

        [Fact]
        public void A_disabled_rule_stays_in_the_list_and_out_of_the_run()
        {
            var rules = new ClashRuleSet();
            rules.Filters.Add(new FilterSpec("Off", new PredicateSpec("always"), suppress: true) { IsEnabled = false });

            Assert.Single(rules.Filters);
            Assert.Empty(rules.ToOptions().Filters);
            Assert.False(ClashRuleSet.FromJson(rules.ToJson()).Filters[0].IsEnabled);
        }
    }

    public class PersistenceTests
    {
        [Fact]
        public void A_set_expression_survives_a_round_trip()
        {
            var expression = SetExpression.And(
                SetExpression.Where("Element", "Category", SetOperator.Equals, "Ducts"),
                SetExpression.Not(SetExpression.Where("Item", "Layer", SetOperator.Contains, "Demo")),
                SetExpression.Or(
                    SetExpression.InSet("set-1", "Level 3"),
                    SetExpression.Where("Element", "Width", SetOperator.GreaterThan, "0.5")));

            var back = SetExpressionJson.Read(SetExpressionJson.Write(expression));

            Assert.Equal(expression.Describe(), back.Describe());
        }

        [Fact]
        public void An_unreadable_expression_becomes_nothing_rather_than_everything()
        {
            // Direction matters again: a layer whose rule failed to load must colour nothing, not
            // the entire federation.
            Assert.Same(SetExpression.Nothing, SetExpressionJson.Read(JsonValue.Object()));
            Assert.Same(SetExpression.Nothing, SetExpressionJson.Read(null));
        }

        [Fact]
        public void A_numeric_condition_whose_value_stopped_parsing_is_dropped_not_thrown()
        {
            var damaged = JsonValue.Object()
                .Set("op", JsonValue.String("where"))
                .Set("category", JsonValue.String("Element"))
                .Set("property", JsonValue.String("Width"))
                .Set("comparison", JsonValue.String("GreaterThan"))
                .Set("value", JsonValue.String("wide"));

            Assert.Same(SetExpression.Nothing, SetExpressionJson.Read(damaged));
        }

        [Fact]
        public void A_rule_layer_and_a_selection_layer_stay_different_things()
        {
            // The distinction the whole Appearance Manager rests on. Flattening a rule layer to the
            // keys it happened to cover at save time is the bug this test exists to catch.
            var rule = new AppearanceLayer("l1", "Demolition",
                LayerTarget.Set(SetExpression.Where("Item", "Layer", SetOperator.Contains, "Demo")))
            {
                Visible = false,
                Note = "hidden for the Tuesday review",
            };

            var selection = new AppearanceLayer("l2", "Spot check",
                LayerTarget.Elements(new[]
                {
                    ElementKey.FromTreePath("model", null, "a", "b"),
                    ElementKey.FromTreePath("model", null, "c"),
                }))
            {
                Colour = new Colour(0x33, 0x66, 0x99),
                Transparency = 0.4,
            };

            var back = LayerStackJson.Read(LayerStackJson.Write(new[] { rule, selection }));

            Assert.True(back[0].Target.ReResolves);
            Assert.False(back[0].Visible);
            Assert.Equal("hidden for the Tuesday review", back[0].Note);

            Assert.False(back[1].Target.ReResolves);
            Assert.Equal(2, back[1].Target.Keys.Count);
            Assert.Equal(new Colour(0x33, 0x66, 0x99), back[1].Colour);
            Assert.Equal(0.4, back[1].Transparency);
        }

        [Fact]
        public void A_layer_that_decides_nothing_stays_deciding_nothing()
        {
            var layer = new AppearanceLayer("l1", "Just a note", LayerTarget.Everything());

            var back = LayerStackJson.Read(LayerStackJson.Write(new[] { layer }))[0];

            Assert.Null(back.Visible);
            Assert.Null(back.Colour);
            Assert.Null(back.Transparency);
            Assert.True(back.IsEmpty);
        }

        [Fact]
        public void The_set_library_hands_out_ids_that_are_free()
        {
            var library = new SetLibrary();

            var first = library.NextId("level");
            library.Put(new SavedSet(first, "Level 3", SetExpression.Everything));

            var second = library.NextId("level");

            Assert.NotEqual(first, second);
            Assert.Null(library.Find(second));

            library.Remove(first);
            Assert.Empty(library.Sets);
        }
    }
}
