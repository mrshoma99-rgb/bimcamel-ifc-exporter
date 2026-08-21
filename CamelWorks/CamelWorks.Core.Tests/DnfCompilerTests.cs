using System;
using System.Linq;
using CamelWorks.Core.Sets;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class DnfCompilerTests
    {
        private static SetExpression Where(string property, string value) =>
            SetExpression.Where("Element", property, SetOperator.Equals, value);

        private static readonly SetExpression Ducts = Where("Category", "Ducts");
        private static readonly SetExpression Pipes = Where("Category", "Pipes");
        private static readonly SetExpression L03 = Where("Level", "L03");

        // ---------------------------------------------------------------------------------
        // The shape the host can run
        // ---------------------------------------------------------------------------------

        [Fact]
        public void One_condition_compiles_to_one_search()
        {
            var plan = DnfCompiler.Compile(Ducts);

            var clause = Assert.Single(plan.Clauses);
            Assert.Single(clause.Include);
            Assert.Empty(clause.Exclude);
            Assert.Equal(1, plan.NativeSearches);
        }

        [Fact]
        public void An_AND_is_one_search_because_the_host_ANDs_conditions_for_free()
        {
            var plan = DnfCompiler.Compile(Ducts & L03);

            var clause = Assert.Single(plan.Clauses);
            Assert.Equal(2, clause.Include.Count);
            Assert.Equal(1, plan.NativeSearches);
        }

        [Fact]
        public void An_OR_becomes_two_searches_because_that_is_the_only_way_to_get_one()
        {
            // A host search ANDs its conditions; the sole route to an OR is more than one search,
            // unioned. That is why disjunctive normal form is the target and not a preference.
            var plan = DnfCompiler.Compile(Ducts | Pipes);

            Assert.Equal(2, plan.Clauses.Count);
            Assert.All(plan.Clauses, c => Assert.Single(c.Include));
        }

        [Fact]
        public void AND_distributes_over_OR()
        {
            // (ducts or pipes) and L03  ->  (ducts and L03) or (pipes and L03)
            var plan = DnfCompiler.Compile((Ducts | Pipes) & L03);

            Assert.Equal(2, plan.Clauses.Count);
            Assert.All(plan.Clauses, c => Assert.Equal(2, c.Include.Count));
            Assert.All(plan.Clauses, c => Assert.Contains(c.Include, x => x.Value == "L03"));
        }

        [Fact]
        public void Nesting_that_was_only_syntax_does_not_survive()
        {
            // And(And(a, b), c) and And(a, b, c) must compile identically, or an expression's cost
            // would depend on how the user happened to click it together.
            var nested = SetExpression.And(SetExpression.And(Ducts, L03), Pipes);
            var flat = SetExpression.And(Ducts, L03, Pipes);

            Assert.Equal(
                DnfCompiler.Compile(nested).Clauses.Single().Describe(),
                DnfCompiler.Compile(flat).Clauses.Single().Describe());
        }

        // ---------------------------------------------------------------------------------
        // Negation
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_negation_compiles_to_a_subtraction_never_to_a_negated_operator()
        {
            // The heart of it. A host condition on an absent property matches nothing, so
            // "Fire Rating != 60" silently drops every element with no fire rating — while
            // "not (Fire Rating = 60)" must keep them. Only a subtraction gets that right.
            var plan = DnfCompiler.Compile(L03 & !Ducts);

            var clause = Assert.Single(plan.Clauses);
            Assert.Single(clause.Include);
            Assert.Equal("L03", clause.Include[0].Value);
            Assert.Single(clause.Exclude);
            Assert.Equal("Ducts", clause.Exclude[0].Value);
            Assert.Equal(2, clause.NativeSearches);   // one to find, one to subtract
        }

        [Fact]
        public void There_is_no_negated_operator_to_compile_to()
        {
            // Stated as a test so that adding one later trips over this rather than the user.
            var names = Enum.GetNames(typeof(SetOperator));

            Assert.DoesNotContain(names, n => n.StartsWith("Not", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Undefined"));
        }

        [Fact]
        public void De_Morgan_turns_a_negated_AND_into_two_clauses()
        {
            // not (ducts and L03)  ->  (not ducts) or (not L03)
            var plan = DnfCompiler.Compile(!(Ducts & L03));

            Assert.Equal(2, plan.Clauses.Count);
            Assert.All(plan.Clauses, c => Assert.Single(c.Exclude));
            Assert.All(plan.Clauses, c => Assert.True(c.StartsFromEverything));
        }

        [Fact]
        public void De_Morgan_turns_a_negated_OR_into_one_clause_with_two_subtractions()
        {
            // not (ducts or pipes)  ->  (not ducts) and (not pipes)
            var plan = DnfCompiler.Compile(!(Ducts | Pipes));

            var clause = Assert.Single(plan.Clauses);
            Assert.Equal(2, clause.Exclude.Count);
        }

        [Fact]
        public void Double_negation_collapses()
        {
            var plan = DnfCompiler.Compile(!!Ducts);

            var clause = Assert.Single(plan.Clauses);
            Assert.Single(clause.Include);
            Assert.Empty(clause.Exclude);
        }

        [Fact]
        public void A_clause_with_nothing_positive_says_so_rather_than_running_quietly()
        {
            // Correct, and expensive. On a large federation the host will feel it, so the user
            // hears about it before pressing the button rather than afterwards.
            var plan = DnfCompiler.Compile(!Ducts);

            Assert.True(Assert.Single(plan.Clauses).StartsFromEverything);
            Assert.Contains(plan.Warnings, w => w.Contains("every element in the model"));
            Assert.Contains("starting from the whole model", plan.Explain(), StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // Simplification
        // ---------------------------------------------------------------------------------

        [Fact]
        public void The_same_condition_twice_is_once()
        {
            var plan = DnfCompiler.Compile(Ducts & Where("Category", "Ducts"));

            Assert.Single(Assert.Single(plan.Clauses).Include);
        }

        [Fact]
        public void A_clause_that_contradicts_itself_is_dropped_without_running_a_search()
        {
            var plan = DnfCompiler.Compile(Pipes | (Ducts & !Ducts));

            var clause = Assert.Single(plan.Clauses);
            Assert.Equal("Pipes", clause.Include[0].Value);
        }

        [Fact]
        public void An_expression_that_contradicts_itself_entirely_matches_nothing()
        {
            // Distinct from "the search found nothing", which nobody can know until it runs.
            var plan = DnfCompiler.Compile(Ducts & !Ducts);

            Assert.True(plan.MatchesNothing);
            Assert.Empty(plan.Clauses);
            Assert.Contains("matches nothing", plan.Explain(), StringComparison.Ordinal);
        }

        [Fact]
        public void Absorption_drops_a_clause_another_already_covers()
        {
            // a or (a and b) = a. Without this the plan runs a second search whose every result the
            // first already returned.
            var plan = DnfCompiler.Compile(Ducts | (Ducts & L03));

            var clause = Assert.Single(plan.Clauses);
            Assert.Single(clause.Include);
            Assert.Equal("Ducts", clause.Include[0].Value);
        }

        [Fact]
        public void Two_clauses_of_the_same_size_both_survive()
        {
            // Absorption must not fire between clauses that merely have the same shape.
            var plan = DnfCompiler.Compile((Ducts & L03) | (Pipes & L03));

            Assert.Equal(2, plan.Clauses.Count);
        }

        [Fact]
        public void Identical_clauses_are_deduplicated()
        {
            var plan = DnfCompiler.Compile(Ducts | Where("Category", "Ducts"));

            Assert.Single(plan.Clauses);
        }

        [Fact]
        public void Compilation_is_deterministic_regardless_of_how_the_expression_was_built()
        {
            // Two runs of the same set must produce the same plan, or nothing downstream can cache
            // or compare them.
            var one = DnfCompiler.Compile((Ducts | Pipes) & L03);
            var two = DnfCompiler.Compile(L03 & (Pipes | Ducts));

            Assert.Equal(
                one.Clauses.Select(c => c.Describe()),
                two.Clauses.Select(c => c.Describe()));
        }

        // ---------------------------------------------------------------------------------
        // Constants
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Everything_and_nothing_are_the_identities_they_should_be()
        {
            Assert.Single(DnfCompiler.Compile(Ducts & SetExpression.Everything).Clauses);
            Assert.True(DnfCompiler.Compile(Ducts & SetExpression.Nothing).MatchesNothing);
            Assert.True(DnfCompiler.Compile(Ducts | SetExpression.Everything).MatchesEverything);
            Assert.Single(DnfCompiler.Compile(Ducts | SetExpression.Nothing).Clauses);
        }

        [Fact]
        public void An_unfinished_builder_row_does_not_change_the_rows_around_it()
        {
            // An empty AND is everything and an empty OR is nothing — the algebraic identities, and
            // here also the useful ones.
            Assert.True(DnfCompiler.Compile(SetExpression.And()).MatchesEverything);
            Assert.True(DnfCompiler.Compile(SetExpression.Or()).MatchesNothing);
        }

        [Fact]
        public void Negating_everything_is_nothing()
        {
            Assert.True(DnfCompiler.Compile(!SetExpression.Everything).MatchesNothing);
            Assert.True(DnfCompiler.Compile(!SetExpression.Nothing).MatchesEverything);
        }

        // ---------------------------------------------------------------------------------
        // Saved sets as terms
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_saved_set_can_be_a_term_on_either_side()
        {
            var approved = SetExpression.InSet("set-7", "Approved deviations");
            var plan = DnfCompiler.Compile((Ducts | Pipes) & !approved);

            Assert.Equal(2, plan.Clauses.Count);
            Assert.All(plan.Clauses, c => Assert.Equal("set-7", Assert.Single(c.ExcludeSets)));
            Assert.All(plan.Clauses, c => Assert.False(c.StartsFromEverything));
        }

        [Fact]
        public void A_clause_narrowed_only_by_a_saved_set_does_not_start_from_everything()
        {
            var plan = DnfCompiler.Compile(SetExpression.InSet("set-7") & !Ducts);

            var clause = Assert.Single(plan.Clauses);
            Assert.False(clause.StartsFromEverything);
            Assert.Single(clause.IncludeSets);
        }

        [Fact]
        public void A_set_reference_is_identified_by_id_not_by_the_name_shown()
        {
            // Sets get renamed. A plan keyed on the display name would silently start meaning
            // something else the first time somebody tidied up the set list.
            var a = SetExpression.InSet("set-7", "Approved deviations");
            var b = SetExpression.InSet("set-7", "Approved deviations (2024)");

            Assert.Single(DnfCompiler.Compile(a | b).Clauses);
        }

        // ---------------------------------------------------------------------------------
        // The budget
        // ---------------------------------------------------------------------------------

        [Fact]
        public void An_expression_that_would_explode_is_refused_rather_than_run()
        {
            // n disjunctions ANDed together become 2^n clauses. Twenty of them would hang the host.
            var parts = Enumerable.Range(0, 20)
                .Select(i => Where("A" + i, "x") | Where("B" + i, "y"))
                .ToArray();

            var ex = Assert.Throws<SetExpressionTooComplexException>(
                () => DnfCompiler.Compile(SetExpression.And(parts)));

            Assert.Equal(DnfCompiler.DefaultMaxClauses, ex.Limit);
            Assert.Contains("split it into saved sets", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_budget_is_checked_before_the_work_is_spent_not_after()
        {
            // Allocating the product and then measuring it would defeat the point of a budget.
            var parts = Enumerable.Range(0, 24)
                .Select(i => Where("A" + i, "x") | Where("B" + i, "y"))
                .ToArray();

            var ex = Assert.Throws<SetExpressionTooComplexException>(
                () => DnfCompiler.Compile(SetExpression.And(parts), maxClauses: 8));

            Assert.True(ex.Reached > 8);
        }

        [Fact]
        public void A_wide_but_shallow_expression_stays_within_budget()
        {
            var parts = Enumerable.Range(0, 100).Select(i => Where("A" + i, "x")).ToArray();

            var plan = DnfCompiler.Compile(SetExpression.Or(parts));

            Assert.Equal(100, plan.Clauses.Count);
            Assert.Contains(plan.Warnings, w => w.Contains("separate searches"));
        }

        // ---------------------------------------------------------------------------------
        // Readouts
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_clause_reads_as_the_set_the_user_asked_for()
        {
            var plan = DnfCompiler.Compile(L03 & !Ducts);

            var text = Assert.Single(plan.Clauses).Describe();
            Assert.Contains("Element.Level = 'L03'", text, StringComparison.Ordinal);
            Assert.Contains("except", text, StringComparison.Ordinal);
        }

        [Fact]
        public void The_plan_says_what_it_will_cost_before_it_runs()
        {
            var plan = DnfCompiler.Compile((Ducts | Pipes) & !L03);

            Assert.Equal(2, plan.Clauses.Count);
            Assert.Equal(4, plan.NativeSearches);          // two searches, two subtractions
            Assert.Contains("2 clauses", plan.Explain(), StringComparison.Ordinal);
            Assert.Contains("2 subtracted", plan.Explain(), StringComparison.Ordinal);
        }
    }

    public class SetConditionTests
    {
        [Fact]
        public void A_delimiter_inside_a_property_name_cannot_forge_another_condition()
        {
            // Canonical is length-prefixed for this reason. With a separator, these two would
            // canonicalise the same way and the compiler would fold them into one.
            var a = new SetCondition("Element", "A:1", SetOperator.Equals, "B");
            var b = new SetCondition("Element", "A", SetOperator.Equals, "1:B");

            Assert.NotEqual(a.Canonical, b.Canonical);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void An_absent_value_and_an_empty_one_are_different_conditions()
        {
            var defined = new SetCondition("Element", "Mark", SetOperator.Defined);
            var empty = new SetCondition("Element", "Mark", SetOperator.Equals, string.Empty);

            Assert.NotEqual(defined.Canonical, empty.Canonical);
        }

        [Fact]
        public void A_numeric_comparison_needs_a_number()
        {
            Assert.Throws<ArgumentException>(
                () => new SetCondition("Element", "Width", SetOperator.GreaterThan, "wide"));

            var ok = new SetCondition("Element", "Width", SetOperator.GreaterThan, "300");
            Assert.True(ok.IsNumericComparison);
            Assert.Equal(300d, ok.NumericValue);
        }

        [Fact]
        public void A_number_parses_the_same_way_in_every_locale()
        {
            // "1,5" is one and a half in half of Europe. Parsing with the ambient culture would
            // make a set mean different things on two machines in the same office.
            var c = new SetCondition("Element", "Width", SetOperator.LessThan, "1.5");

            Assert.Equal(1.5d, c.NumericValue);
        }

        [Fact]
        public void A_category_test_needs_no_property_but_everything_else_does()
        {
            var category = new SetCondition("Element", null, SetOperator.HasCategory);
            Assert.Contains("has category", category.Describe(), StringComparison.Ordinal);

            Assert.Throws<ArgumentException>(
                () => new SetCondition("Element", null, SetOperator.Equals, "x"));
        }
    }
}
