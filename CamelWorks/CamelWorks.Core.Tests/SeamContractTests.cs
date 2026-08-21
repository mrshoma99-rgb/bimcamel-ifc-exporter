using System;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Testing;
using Xunit;

namespace CamelWorks.Core.Tests
{
    /// <summary>
    /// The contract every implementation of the seam has to keep. Written against
    /// <see cref="FakeDocument"/> here; the same assertions run against the real host in the
    /// shipped self-test, which is the only post-release regression instrument that exists.
    /// </summary>
    public class SeamContractTests
    {
        private static (FakeDocument doc, FakeSource arch, FakeItem slab) Setup()
        {
            var doc = new FakeDocument();
            var arch = doc.AddModel("ARCH", @"C:\proj\ARCH.nwc");
            var slab = doc.AddItem(arch, "Slab 200", "Floors", "RC 200",
                new BoundingBox(0, 0, 3, 10, 8, 3.2), true, "Level 03", "Floors");
            return (doc, arch, slab);
        }

        // ---------------------------------------------------------------------------------
        // The generation counter — G1, and the reason this seam exists at all
        // ---------------------------------------------------------------------------------

        [Fact]
        public void An_item_held_across_a_generation_bump_throws_rather_than_lying()
        {
            var (doc, _, slab) = Setup();

            Assert.Equal("Floors", slab.Category);   // fine before the bump
            doc.BumpGeneration("append MEP");

            // The real host may return nonsense here instead of throwing, which is exactly why
            // the bug is hard to find in the field. The fake is deliberately harsher.
            Assert.Throws<StaleItemException>(() => slab.Property("Element", "Name"));
        }

        [Fact]
        public void Re_resolving_by_key_after_a_bump_is_the_supported_way_back()
        {
            var (doc, _, slab) = Setup();
            var key = slab.Key;                       // a value, not a handle — this survives

            doc.BumpGeneration("refresh ARCH");

            var again = doc.Resolve(key);
            Assert.NotNull(again);
            Assert.Equal("Slab 200", again!.DisplayName);
        }

        [Fact]
        public void An_element_deleted_in_the_next_revision_resolves_to_null_not_an_error()
        {
            var (doc, _, slab) = Setup();
            var key = slab.Key;

            doc.RemoveItem(key);
            doc.BumpGeneration("refresh ARCH");

            // Ordinary outcome. The caller reports it unmatched; it is not an exception.
            Assert.Null(doc.Resolve(key));
        }

        // ---------------------------------------------------------------------------------
        // Writes — preview first, commit once, roll back on the way out
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Preview_reports_what_commit_will_do_without_doing_it()
        {
            var (doc, _, slab) = Setup();

            using var tx = doc.BeginWrite("Status stamp");
            tx.SetProperty(slab.Key, "CamelWorks", "Status", "Installed");

            var preview = tx.Preview();
            Assert.Equal(1, preview.AffectedElements);
            Assert.Equal(1, preview.PropertyWrites);
            Assert.False(preview.IsEmpty);

            // Nothing has been written yet.
            Assert.Null(doc.Resolve(slab.Key)!.Property("CamelWorks", "Status"));

            Assert.Equal(1, tx.Commit());
            Assert.Equal("Installed", doc.Resolve(slab.Key)!.Property("CamelWorks", "Status"));
        }

        [Fact]
        public void Preview_names_the_keys_that_no_longer_resolve_rather_than_dropping_them()
        {
            var (doc, _, slab) = Setup();
            var gone = slab.Key;
            doc.RemoveItem(gone);

            using var tx = doc.BeginWrite("Bulk edit");
            tx.SetProperty(gone, "CamelWorks", "Zone", "Z1");

            var preview = tx.Preview();
            Assert.Equal(0, preview.AffectedElements);
            Assert.Single(preview.Unresolved);
            Assert.Contains("no longer found", preview.ToString());
        }

        [Fact]
        public void Committing_twice_throws_because_a_double_apply_is_silent_corruption()
        {
            var (doc, _, slab) = Setup();

            using var tx = doc.BeginWrite("Status stamp");
            tx.SetProperty(slab.Key, "CamelWorks", "Status", "Installed");
            tx.Commit();

            Assert.Throws<InvalidOperationException>(() => tx.Commit());
        }

        [Fact]
        public void Disposing_without_committing_rolls_back_so_a_failure_cannot_half_apply()
        {
            var (doc, _, slab) = Setup();

            using (var tx = doc.BeginWrite("Abandoned edit"))
            {
                tx.SetProperty(slab.Key, "CamelWorks", "Status", "Installed");
            } // no Commit

            Assert.Null(doc.Resolve(slab.Key)!.Property("CamelWorks", "Status"));
            Assert.Contains(doc.Log, l => l.StartsWith("write.rollback", StringComparison.Ordinal));
        }

        [Fact]
        public void The_effect_log_proves_the_order_not_just_the_outcome()
        {
            var (doc, _, slab) = Setup();
            doc.ClearLog();

            using var tx = doc.BeginWrite("Status stamp");
            tx.SetProperty(slab.Key, "CamelWorks", "Status", "Installed");
            tx.Preview();
            tx.Commit();

            var effects = doc.Log.Where(l => l.StartsWith("write.", StringComparison.Ordinal)).ToList();

            // State-only assertions cannot tell "previewed then wrote" from "wrote then previewed".
            Assert.Equal(3, effects.Count);
            Assert.StartsWith("write.begin", effects[0], StringComparison.Ordinal);
            Assert.StartsWith("write.preview", effects[1], StringComparison.Ordinal);
            Assert.StartsWith("write.commit", effects[2], StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // Traversal
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Whole_document_is_the_zero_configuration_default()
        {
            var (doc, arch, _) = Setup();
            doc.AddItem(arch, "Wall 1", "Walls", "Blockwork");

            // No set, no profile, no prior state — this is what every screen opens on.
            Assert.Equal(2, doc.Traverse(TraversalScope.WholeDocument).Count());
        }

        [Fact]
        public void Traversing_a_set_that_does_not_exist_yields_nothing_rather_than_throwing()
        {
            var (doc, _, _) = Setup();

            // A set that has been renamed or deleted is ordinary; Set Health reports it, the walk
            // does not blow up mid-report.
            Assert.Empty(doc.Traverse(TraversalScope.Set("Nonexistent")));
        }

        [Fact]
        public void Traversal_scopes_select_what_they_say_they_do()
        {
            var (doc, arch, slab) = Setup();
            var wall = doc.AddItem(arch, "Wall 1", "Walls", "Blockwork");

            doc.DefineSet("Slabs", slab.Key);
            doc.Select(wall.Key);

            Assert.Equal(new[] { "Slab 200" },
                doc.Traverse(TraversalScope.Set("Slabs")).Select(i => i.DisplayName).ToArray());
            Assert.Equal(new[] { "Wall 1" },
                doc.Traverse(TraversalScope.CurrentSelection).Select(i => i.DisplayName).ToArray());
            Assert.Equal(new[] { "Slab 200" },
                doc.Traverse(TraversalScope.FromKeys(new[] { slab.Key })).Select(i => i.DisplayName).ToArray());
        }

        [Fact]
        public void An_empty_document_is_a_normal_state_every_screen_must_handle()
        {
            var doc = new FakeDocument();

            Assert.True(doc.IsEmpty);
            Assert.Empty(doc.Models);
            Assert.Empty(doc.Traverse(TraversalScope.WholeDocument));
        }

        // ---------------------------------------------------------------------------------
        // Appearance — the layer asymmetry that decides the Appearance Manager's architecture
        // ---------------------------------------------------------------------------------

        [Fact]
        public void The_temporary_layer_refuses_per_element_clear_because_the_host_has_none()
        {
            var (doc, _, slab) = Setup();
            var view = new FakeViewSession(doc) { Layer = OverrideLayer.Temporary };

            view.SetColour(new[] { slab.Key }, new Colour(255, 0, 0));

            // Permanent resets a collection; temporary only resets everything. Pretending
            // otherwise is how a "clear these three" silently clears the whole model.
            Assert.Throws<NotSupportedException>(() => view.ClearOverrides(new[] { slab.Key }));
            view.ClearAllOverrides();  // the only thing the host actually offers
        }

        [Fact]
        public void The_temporary_layer_cannot_be_read_back_and_says_so()
        {
            var (doc, _, slab) = Setup();
            var view = new FakeViewSession(doc) { Layer = OverrideLayer.Temporary };

            // Returning "no overrides" here would claim a completeness the host does not offer.
            Assert.Throws<NotSupportedException>(() => view.ReadAppearance(new[] { slab.Key }));
        }

        [Fact]
        public void The_permanent_layer_clears_per_element_and_reads_back()
        {
            var (doc, _, slab) = Setup();
            var view = new FakeViewSession(doc);

            view.SetColour(new[] { slab.Key }, new Colour(0, 128, 255));
            var state = view.ReadAppearance(new[] { slab.Key }).Single();

            Assert.Equal("#0080ff", state.Colour!.Value.ToString());
            Assert.False(state.IsForeign);
            Assert.False(state.IsPristine);

            view.ClearOverrides(new[] { slab.Key });
            Assert.True(view.ReadAppearance(new[] { slab.Key }).Single().IsPristine);
        }

        [Fact]
        public void An_override_CamelWorks_did_not_author_is_reported_as_foreign()
        {
            var (doc, _, slab) = Setup();
            var view = new FakeViewSession(doc);

            // Somebody else's Appearance Profiler run, or a colleague's session.
            view.SeedForeignColour(slab.Key, new Colour(255, 0, 255));

            var state = view.ReadAppearance(new[] { slab.Key }).Single();
            Assert.True(state.IsForeign);   // the answer to "why is this thing pink"
        }

        [Fact]
        public void Hidden_state_is_part_of_the_appearance_reading_not_a_separate_system()
        {
            var (doc, _, slab) = Setup();
            var view = new FakeViewSession(doc);

            view.SetVisible(new[] { slab.Key }, false);

            var state = view.ReadAppearance(new[] { slab.Key }).Single();
            Assert.True(state.IsHidden);
            Assert.False(state.IsPristine);
        }

        [Fact]
        public void A_section_box_grows_by_its_context_margin()
        {
            var (doc, _, slab) = Setup();
            var view = new FakeViewSession(doc);

            view.SetSectionBox(slab.Bounds, 1.5);

            var box = view.SectionBox!.Value;
            Assert.Equal(-1.5, box.MinX, 6);
            Assert.Equal(11.5, box.MaxX, 6);
            Assert.True(view.SectionBoxEnabled);

            // Non-destructive toggle: the box survives being switched off.
            view.SetSectionBoxEnabled(false);
            Assert.False(view.SectionBoxEnabled);
            Assert.NotNull(view.SectionBox);
        }
    }

    public class BoundingBoxTests
    {
        [Fact]
        public void Reports_size_and_centre()
        {
            var box = new BoundingBox(0, 0, 0, 10, 4, 2);

            Assert.Equal(10, box.SizeX, 6);
            Assert.Equal(4, box.SizeY, 6);
            Assert.Equal(2, box.SizeZ, 6);
            Assert.Equal(5, box.CentreX, 6);
        }

        [Fact]
        public void Touching_boxes_intersect_and_separated_ones_do_not()
        {
            var a = new BoundingBox(0, 0, 0, 1, 1, 1);

            Assert.True(a.Intersects(new BoundingBox(1, 0, 0, 2, 1, 1)));      // face to face
            Assert.True(a.Intersects(new BoundingBox(0.5, 0.5, 0.5, 2, 2, 2))); // overlapping
            Assert.False(a.Intersects(new BoundingBox(1.1, 0, 0, 2, 1, 1)));    // clear
        }

        [Fact]
        public void Union_contains_both()
        {
            var u = new BoundingBox(0, 0, 0, 1, 1, 1).Union(new BoundingBox(5, -2, 0, 6, -1, 3));

            Assert.Equal(0, u.MinX, 6);
            Assert.Equal(-2, u.MinY, 6);
            Assert.Equal(6, u.MaxX, 6);
            Assert.Equal(3, u.MaxZ, 6);
        }
    }

    public class ColourTests
    {
        [Theory]
        [InlineData("#ff8000", 255, 128, 0)]
        [InlineData("ff8000", 255, 128, 0)]
        [InlineData("  #000000  ", 0, 0, 0)]
        public void Parses_hex_with_or_without_a_hash(string text, byte r, byte g, byte b)
        {
            Assert.True(Colour.TryParse(text, out var c));
            Assert.Equal(r, c.R);
            Assert.Equal(g, c.G);
            Assert.Equal(b, c.B);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("#fff")]
        [InlineData("nothex")]
        [InlineData("#gg0000")]
        public void Refuses_anything_else_without_throwing(string? text)
        {
            Assert.False(Colour.TryParse(text, out _));
        }

        [Fact]
        public void Round_trips()
        {
            Assert.True(Colour.TryParse(new Colour(1, 2, 3).ToString(), out var back));
            Assert.Equal(3, back.B);
        }
    }
}
