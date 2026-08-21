using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Appearance;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Sets;
using Xunit;

namespace CamelWorks.Core.Tests
{
    /// <summary>
    /// A resolver over a fixed universe. Explicit targets return their own keys; a set expression
    /// returns whatever the test registered for it, and <see cref="SetExpression.Everything"/>
    /// returns the universe — which is what makes an isolate two ordinary layers.
    /// </summary>
    internal sealed class FakeResolver : ILayerResolver
    {
        private readonly Dictionary<string, IReadOnlyCollection<ElementKey>> _sets =
            new Dictionary<string, IReadOnlyCollection<ElementKey>>(StringComparer.Ordinal);

        internal FakeResolver(params ElementKey[] universe) => Universe = universe;

        internal IReadOnlyCollection<ElementKey> Universe { get; }

        internal int Calls { get; private set; }

        internal FakeResolver With(string description, params ElementKey[] keys)
        {
            _sets[description] = keys;
            return this;
        }

        public IReadOnlyCollection<ElementKey> Resolve(LayerTarget target)
        {
            Calls++;

            if (!target.ReResolves) return target.Keys;
            if (_sets.TryGetValue(target.Description, out var keys)) return keys;
            return Universe;
        }
    }

    public class AppearanceStackTests
    {
        private static readonly string Scope = ElementKey.ScopeOf(@"C:\p\ARCH.nwc");

        private static ElementKey K(string name) => ElementKey.FromTreePath(Scope, @"C:\p\ARCH.nwc", name);

        private static readonly ElementKey A = K("a");
        private static readonly ElementKey B = K("b");
        private static readonly ElementKey C = K("c");

        private static readonly Colour Red = new Colour(255, 0, 0);
        private static readonly Colour Blue = new Colour(0, 0, 255);

        private static AppearanceLayer Layer(string id, params ElementKey[] keys) =>
            new AppearanceLayer(id, id, LayerTarget.Elements(keys));

        // ---------------------------------------------------------------------------------
        // Precedence
        // ---------------------------------------------------------------------------------

        [Fact]
        public void The_top_of_the_stack_wins()
        {
            var bottom = Layer("bottom", A);
            bottom.Colour = Red;
            var top = Layer("top", A);
            top.Colour = Blue;

            var fold = AppearanceStack.Fold(new[] { bottom, top }, new FakeResolver(A));

            Assert.Equal(Blue, fold.For(A)!.Colour);
        }

        [Fact]
        public void A_layer_that_sets_only_a_colour_leaves_visibility_to_the_one_below()
        {
            // Without per-property precedence a stack is not a stack: every layer would be a full
            // replacement of the one below, and the second layer you add would undo the first.
            var hider = Layer("hide", A);
            hider.Visible = false;
            var colourer = Layer("colour", A);
            colourer.Colour = Red;

            var fold = AppearanceStack.Fold(new[] { hider, colourer }, new FakeResolver(A));

            var a = fold.For(A)!;
            Assert.False(a.Visible);        // the hide below survives
            Assert.Equal(Red, a.Colour);
        }

        [Fact]
        public void An_isolate_is_two_ordinary_layers_not_a_special_mode()
        {
            // Hide everything, then show these. That it falls out of the ordinary rules is the
            // point — an isolate that was its own mode could not be combined with anything else.
            var hideAll = new AppearanceLayer("all", "Hide everything", LayerTarget.Everything());
            hideAll.Visible = false;

            var showThese = Layer("keep", B);
            showThese.Visible = true;

            var fold = AppearanceStack.Fold(new[] { hideAll, showThese }, new FakeResolver(A, B, C));

            Assert.False(fold.For(A)!.Visible);
            Assert.True(fold.For(B)!.Visible);
            Assert.False(fold.For(C)!.Visible);
            Assert.Equal(2, fold.Hidden);
        }

        [Fact]
        public void A_disabled_layer_decides_nothing_but_still_reports_what_it_would_cover()
        {
            // A layers panel where switching something off makes its count vanish is a panel you
            // cannot plan with.
            var off = Layer("off", A, B);
            off.Colour = Red;
            off.IsEnabled = false;

            var fold = AppearanceStack.Fold(new[] { off }, new FakeResolver(A, B));

            Assert.Null(fold.For(A)!.Colour);
            var report = Assert.Single(fold.Layers);
            Assert.Equal(2, report.Covers);
            Assert.Equal(0, report.Effective);
            Assert.False(report.IsDead);          // off, not dead — a different thing entirely
        }

        // ---------------------------------------------------------------------------------
        // "Why does this look like that"
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Every_layer_that_had_a_view_is_recorded_winners_and_losers_alike()
        {
            // In the host, "why is this hidden" is answerable only by undoing things until it
            // reappears. Here it is a row — and so is the fact that another layer wanted it shown.
            var wantedShown = Layer("show", A);
            wantedShown.Visible = true;
            var wonHidden = Layer("hide", A);
            wonHidden.Visible = false;

            var fold = AppearanceStack.Fold(new[] { wantedShown, wonHidden }, new FakeResolver(A));

            var a = fold.For(A)!;
            Assert.False(a.Visible);
            Assert.Equal(2, a.Decisions.Count);

            var winner = a.Decisions.Single(d => d.Survived);
            Assert.Equal("hide", winner.LayerId);

            var loser = a.Decisions.Single(d => !d.Survived);
            Assert.Equal("show", loser.LayerId);
            Assert.Equal("hide", loser.OverruledBy);
        }

        [Fact]
        public void The_winning_decision_is_listed_first_because_that_is_the_question_asked()
        {
            var bottom = Layer("bottom", A);
            bottom.Colour = Red;
            var top = Layer("top", A);
            top.Colour = Blue;

            var fold = AppearanceStack.Fold(new[] { bottom, top }, new FakeResolver(A));

            Assert.True(fold.For(A)!.Decisions[0].Survived);
            Assert.Equal("top", fold.For(A)!.Decisions[0].LayerId);
        }

        [Fact]
        public void An_element_no_layer_decides_anything_about_says_so()
        {
            var covers = Layer("covers", A);   // points at A, decides nothing about it

            var fold = AppearanceStack.Fold(new[] { covers }, new FakeResolver(A, B));

            Assert.Null(fold.For(B));          // never covered, so never folded

            var a = fold.For(A)!;
            Assert.True(a.IsUntouched);
            Assert.True(a.Visible);
            Assert.Contains("no layer affects", a.Explain(), StringComparison.Ordinal);
        }

        [Fact]
        public void The_explanation_reads_as_an_answer_not_a_dump()
        {
            var under = Layer("Level filter", A);
            under.Visible = false;
            var over = Layer("Snagging", A);
            over.Visible = true;

            var fold = AppearanceStack.Fold(new[] { under, over }, new FakeResolver(A));

            var text = fold.For(A)!.Explain();
            Assert.Contains("visibility shown from 'Snagging'", text, StringComparison.Ordinal);
            Assert.Contains("1 other layer overruled", text, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // Layer reports
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_layer_completely_overruled_from_above_is_flagged_as_doing_nothing()
        {
            // A stack worked on for a month accumulates these, and somebody deleting layers needs
            // to know which are already inert — otherwise the only safe move is to delete none.
            var buried = Layer("buried", A);
            buried.Colour = Red;
            var on_top = Layer("on top", A);
            on_top.Colour = Blue;

            var fold = AppearanceStack.Fold(new[] { buried, on_top }, new FakeResolver(A));

            Assert.True(fold.Layers[0].IsDead);
            Assert.False(fold.Layers[1].IsDead);
            Assert.Contains("fully overruled", fold.Layers[0].ToString(), StringComparison.Ordinal);
            Assert.Contains("1 doing nothing", fold.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_layer_partly_overruled_is_not_dead()
        {
            var wide = Layer("wide", A, B);
            wide.Colour = Red;
            var narrow = Layer("narrow", A);
            narrow.Colour = Blue;

            var fold = AppearanceStack.Fold(new[] { wide, narrow }, new FakeResolver(A, B));

            Assert.Equal(2, fold.Layers[0].Covers);
            Assert.Equal(1, fold.Layers[0].Effective);
            Assert.Equal(1, fold.Layers[0].Overruled);
            Assert.False(fold.Layers[0].IsDead);
        }

        [Fact]
        public void A_layer_still_counts_as_effective_when_only_one_of_its_properties_survives()
        {
            var both = Layer("both", A);
            both.Colour = Red;
            both.Visible = false;
            var stealsColour = Layer("colour only", A);
            stealsColour.Colour = Blue;

            var fold = AppearanceStack.Fold(new[] { both, stealsColour }, new FakeResolver(A));

            Assert.Equal(1, fold.Layers[0].Effective);    // its hide survived
            Assert.False(fold.Layers[0].IsDead);
        }

        [Fact]
        public void A_layer_that_matches_nothing_right_now_says_that_and_not_something_else()
        {
            var empty = new AppearanceLayer("gone", "Level 7", LayerTarget.Elements(Array.Empty<ElementKey>()));
            empty.Visible = false;

            var fold = AppearanceStack.Fold(new[] { empty }, new FakeResolver(A));

            Assert.Contains("matches nothing right now", Assert.Single(fold.Layers).ToString(),
                StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // Targets
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_rule_target_re_resolves_but_a_list_target_does_not()
        {
            // The difference a coordinator feels: a layer built from a selection is a photograph,
            // one built from a search re-runs. The host's "override by selection" quietly stops
            // being true a week after it is set.
            Assert.False(LayerTarget.Elements(new[] { A }).ReResolves);
            Assert.True(LayerTarget.Set(SetExpression.Where("Element", "Category", SetOperator.Equals, "Ducts")).ReResolves);
            Assert.True(LayerTarget.Everything().ReResolves);
        }

        [Fact]
        public void A_rule_target_picks_up_elements_that_were_not_there_before()
        {
            var ducts = SetExpression.Where("Element", "Category", SetOperator.Equals, "Ducts");
            var layer = new AppearanceLayer("d", "Ducts", LayerTarget.Set(ducts, "ducts"));
            layer.Colour = Red;

            var week1 = AppearanceStack.Fold(new[] { layer }, new FakeResolver(A, B, C).With("ducts", A));
            var week2 = AppearanceStack.Fold(new[] { layer }, new FakeResolver(A, B, C).With("ducts", A, B));

            Assert.Single(week1.Elements);
            Assert.Equal(2, week2.Elements.Count);
        }

        [Fact]
        public void A_duplicated_element_in_a_target_is_counted_once()
        {
            var layer = new AppearanceLayer("d", "d", LayerTarget.Elements(new[] { A, A, B }));

            Assert.Equal(2, layer.Target.Keys.Count);
        }

        [Fact]
        public void A_layer_needs_an_id_a_name_and_a_target()
        {
            Assert.Throws<ArgumentException>(() => new AppearanceLayer(" ", "n", LayerTarget.Everything()));
            Assert.Throws<ArgumentException>(() => new AppearanceLayer("i", " ", LayerTarget.Everything()));
            Assert.Throws<ArgumentNullException>(() => new AppearanceLayer("i", "n", null!));
        }

        [Fact]
        public void A_layer_that_decides_nothing_says_so()
        {
            var layer = Layer("noop", A);

            Assert.True(layer.IsEmpty);
            Assert.Contains("decides nothing",
                AppearanceStack.Fold(new[] { layer }, new FakeResolver(A)).Layers[0].ToString(),
                StringComparison.Ordinal);
        }

        [Fact]
        public void A_note_is_carried_because_the_host_loses_it_the_moment_you_hide_something()
        {
            // A federation handed over with four hundred hidden elements and no note is a
            // federation nobody dares un-hide.
            var layer = Layer("l", A);
            layer.Visible = false;
            layer.Note = "Temporary — awaiting revised structural model, JG 12/03";

            Assert.Contains("JG 12/03", layer.Note, StringComparison.Ordinal);
        }
    }
}
