using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Appearance;
using CamelWorks.Core.Identity;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class AppearancePlannerTests
    {
        private static readonly string Scope = ElementKey.ScopeOf(@"C:\p\ARCH.nwc");

        private static ElementKey K(string name) => ElementKey.FromTreePath(Scope, @"C:\p\ARCH.nwc", name);

        private static readonly ElementKey A = K("a");
        private static readonly ElementKey B = K("b");

        private static readonly Colour Red = new Colour(255, 0, 0);
        private static readonly Colour Blue = new Colour(0, 0, 255);

        private static AppearanceLayer Layer(string id, params ElementKey[] keys) =>
            new AppearanceLayer(id, id, LayerTarget.Elements(keys));

        private static AppearanceFold Fold(params AppearanceLayer[] layers) =>
            AppearanceStack.Fold(layers, new FakeResolver(A, B));

        private static AppearanceState State(ElementKey key, Colour? colour = null, double? transparency = null,
                                             bool hidden = false, bool foreign = false) =>
            new AppearanceState(key, colour, transparency, hidden, foreign);

        // ---------------------------------------------------------------------------------
        // A diff, not a repaint
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Nothing_is_written_when_the_document_already_looks_right()
        {
            // Toggling one layer on a federation must not rewrite two hundred thousand overrides
            // to the values they already hold.
            var layer = Layer("l", A);
            layer.Colour = Red;

            var plan = AppearancePlanner.Plan(Fold(layer), new[] { State(A, colour: Red) });

            Assert.True(plan.IsEmpty);
            Assert.Equal("nothing to change", plan.Explain());
        }

        [Fact]
        public void Only_the_elements_that_differ_are_written()
        {
            var layer = Layer("l", A, B);
            layer.Colour = Red;

            var plan = AppearancePlanner.Plan(Fold(layer),
                new[] { State(A, colour: Red), State(B, colour: Blue) });

            var batch = Assert.Single(plan.Colours);
            Assert.Equal(Red, batch.Colour);
            Assert.Equal(B, Assert.Single(batch.Keys));
        }

        [Fact]
        public void Elements_wanting_the_same_colour_go_in_one_call()
        {
            var layer = Layer("l", A, B);
            layer.Colour = Red;

            var plan = AppearancePlanner.Plan(Fold(layer), Array.Empty<AppearanceState>());

            Assert.Equal(2, Assert.Single(plan.Colours).Keys.Count);
            Assert.Equal(1, plan.Writes);
        }

        [Fact]
        public void Transparency_is_compared_at_the_resolution_the_host_stores_it_at()
        {
            // The host keeps transparency in eight bits, so 0.5 reads back as 128/255. Comparing
            // exactly would find a difference on every element on every fold.
            var layer = Layer("l", A);
            layer.Transparency = 0.5;

            var plan = AppearancePlanner.Plan(Fold(layer), new[] { State(A, transparency: 128.0 / 255.0) });

            Assert.True(plan.IsEmpty);
        }

        [Fact]
        public void A_transparency_that_really_changed_is_written()
        {
            var layer = Layer("l", A);
            layer.Transparency = 0.5;

            var plan = AppearancePlanner.Plan(Fold(layer), new[] { State(A, transparency: 0.2) });

            Assert.Single(plan.Transparencies);
        }

        // ---------------------------------------------------------------------------------
        // Undoing: the part the host cannot do at all
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Deleting_a_layer_returns_its_elements_to_normal()
        {
            // The stack is now empty, but the document still carries what the layer painted.
            var plan = AppearancePlanner.Plan(Fold(), new[] { State(A, colour: Red) });

            Assert.Equal(A, Assert.Single(plan.Clear));
            Assert.Contains("1 reset", plan.Explain(), StringComparison.Ordinal);
        }

        [Fact]
        public void Disabling_a_layer_returns_its_elements_to_normal_too()
        {
            // The layer still covers the element, so the element is still in the fold — but it
            // decides nothing about it any more, and a fold entry is not a claim.
            var layer = Layer("l", A);
            layer.Colour = Red;
            layer.IsEnabled = false;

            var plan = AppearancePlanner.Plan(Fold(layer), new[] { State(A, colour: Red) });

            Assert.Equal(A, Assert.Single(plan.Clear));
        }

        [Fact]
        public void An_element_losing_one_override_but_keeping_another_is_cleared_then_repainted()
        {
            // The host has no way to remove a colour on its own; the only eraser is a per-element
            // clear, which takes transparency and visibility with it. So this element has to
            // appear in both lists, and the order Clear-then-paint is what makes that correct.
            var layer = Layer("l", A);
            layer.Colour = Red;      // keeps its colour
                                     // and loses the transparency it used to have

            var plan = AppearancePlanner.Plan(Fold(layer),
                new[] { State(A, colour: Red, transparency: 0.5) });

            Assert.Equal(A, Assert.Single(plan.Clear));
            Assert.Equal(A, Assert.Single(Assert.Single(plan.Colours).Keys));   // repainted after
            Assert.Empty(plan.Transparencies);
        }

        [Fact]
        public void A_hidden_element_that_is_cleared_is_hidden_again_afterwards()
        {
            var layer = Layer("l", A);
            layer.Visible = false;   // still hidden, but its colour is gone

            var plan = AppearancePlanner.Plan(Fold(layer),
                new[] { State(A, colour: Red, hidden: true) });

            Assert.Single(plan.Clear);
            Assert.Equal(A, Assert.Single(plan.Hide));   // re-asserted, because the clear undid it
        }

        [Fact]
        public void An_element_the_clear_will_reveal_is_not_also_told_to_show()
        {
            // The clear restores visibility on its own; adding a Show call would be a wasted write.
            var plan = AppearancePlanner.Plan(Fold(), new[] { State(A, colour: Red, hidden: true) });

            Assert.Single(plan.Clear);
            Assert.Empty(plan.Show);
        }

        [Fact]
        public void Showing_an_element_needs_no_clear_when_nothing_else_changes()
        {
            var layer = Layer("l", A);
            layer.Visible = true;

            var plan = AppearancePlanner.Plan(Fold(layer), new[] { State(A, hidden: true) });

            Assert.Empty(plan.Clear);
            Assert.Equal(A, Assert.Single(plan.Show));
        }

        // ---------------------------------------------------------------------------------
        // Somebody else's overrides
        // ---------------------------------------------------------------------------------

        [Fact]
        public void An_override_somebody_else_made_is_reported_and_left_exactly_where_it_is()
        {
            // A manager that quietly resets a colleague's work the first time it runs is a manager
            // nobody can leave switched on.
            var plan = AppearancePlanner.Plan(Fold(), new[] { State(A, colour: Blue, foreign: true) });

            Assert.Empty(plan.Clear);
            Assert.Equal(A, Assert.Single(plan.Foreign));
            Assert.Contains("overridden by somebody else, left alone", plan.Explain(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_layer_that_deliberately_targets_a_foreign_element_still_wins()
        {
            // Foreignness protects elements nobody claimed. Pointing a layer at one is a decision,
            // and the decision is allowed to stand.
            var layer = Layer("l", A);
            layer.Colour = Red;

            var plan = AppearancePlanner.Plan(Fold(layer), new[] { State(A, colour: Blue, foreign: true) });

            Assert.Empty(plan.Foreign);
            Assert.Equal(Red, Assert.Single(plan.Colours).Colour);
        }

        [Fact]
        public void A_disabled_layer_does_not_turn_a_foreign_override_into_ours_to_wipe()
        {
            // The trap: the element is in the fold because a switched-off layer covers it, but
            // nothing decides anything about it, so it is still somebody else's.
            var layer = Layer("l", A);
            layer.Colour = Red;
            layer.IsEnabled = false;

            var plan = AppearancePlanner.Plan(Fold(layer), new[] { State(A, colour: Blue, foreign: true) });

            Assert.Empty(plan.Clear);
            Assert.Equal(A, Assert.Single(plan.Foreign));
        }

        [Fact]
        public void A_pristine_element_is_not_reported_as_anything()
        {
            var plan = AppearancePlanner.Plan(Fold(), new[] { State(A), State(B, foreign: true) });

            Assert.True(plan.IsEmpty);
            Assert.Empty(plan.Foreign);
        }

        // ---------------------------------------------------------------------------------
        // Determinism and cost
        // ---------------------------------------------------------------------------------

        [Fact]
        public void The_same_stack_produces_the_same_plan_whichever_order_the_state_arrives_in()
        {
            // The preview a user reads has to be the plan Apply then runs.
            var layer = Layer("l", A, B);
            layer.Colour = Red;

            var forward = AppearancePlanner.Plan(Fold(layer), new[] { State(A), State(B) });
            var reversed = AppearancePlanner.Plan(Fold(layer), new[] { State(B), State(A) });

            Assert.Equal(
                forward.Colours.Single().Keys.Select(k => k.ToString()),
                reversed.Colours.Single().Keys.Select(k => k.ToString()));
        }

        [Fact]
        public void The_plan_says_what_it_will_cost_before_it_runs()
        {
            var hide = Layer("hide", A);
            hide.Visible = false;
            var colour = Layer("colour", B);
            colour.Colour = Red;

            var plan = AppearancePlanner.Plan(Fold(hide, colour), Array.Empty<AppearanceState>());

            Assert.Equal(2, plan.Writes);      // one hide call, one colour call
            Assert.Contains("1 to hide", plan.Explain(), StringComparison.Ordinal);
            Assert.Contains("1 to colour", plan.Explain(), StringComparison.Ordinal);
            Assert.Contains("2 writes", plan.Explain(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_null_reading_is_treated_as_an_untouched_document_not_an_error()
        {
            var layer = Layer("l", A);
            layer.Colour = Red;

            var plan = AppearancePlanner.Plan(Fold(layer), null);

            Assert.Single(plan.Colours);
        }
    }
}
