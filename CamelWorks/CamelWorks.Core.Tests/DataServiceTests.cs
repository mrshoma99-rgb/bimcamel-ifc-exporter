using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Data;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class LevelSetTests
    {
        private static IEnumerable<double> Storeys(params (double Elevation, int Count)[] storeys) =>
            storeys.SelectMany(s => Enumerable.Repeat(s.Elevation, s.Count));

        [Fact]
        public void Levels_the_model_declares_are_used_as_they_are()
        {
            var levels = LevelSet.FromModel(new[]
            {
                new KeyValuePair<string, double>("L03", 7.2),
                new KeyValuePair<string, double>("L01", 0.0),
                new KeyValuePair<string, double>("L02", 3.6),
            });

            Assert.True(levels.IsFromModel);
            Assert.Equal(new[] { "L01", "L02", "L03" }, levels.Bands.Select(b => b.Name));
            Assert.Equal("L02", levels.LevelAt(4.0));
        }

        [Fact]
        public void Levels_are_inferred_when_the_model_has_none()
        {
            // The zero-setup rule. A federation routinely has no usable levels — one discipline
            // exports storeys as properties, one puts them in the tree, one has neither — and
            // refusing to work there means failing on exactly the models that need this most.
            var levels = LevelSet.Derive(Storeys((0.0, 50), (3.6, 50), (7.2, 50)));

            Assert.False(levels.IsFromModel);
            Assert.Equal(3, levels.Bands.Count);
            Assert.Equal("Level +0.000", levels.Bands[0].Name);
            Assert.Equal("Level +3.600", levels.Bands[1].Name);
        }

        [Fact]
        public void Inferred_levels_are_named_after_their_height_and_never_numbered()
        {
            // "Level 2" is a claim about the building, and a report that says Level 2 when the
            // client calls it Level 1 gets the whole report doubted.
            var levels = LevelSet.Derive(Storeys((-4.5, 30), (0.0, 30)));

            Assert.Equal("Level -4.500", levels.Bands[0].Name);
            Assert.DoesNotContain(levels.Bands, b => b.Name == "Level 1");
        }

        [Fact]
        public void Inference_gives_the_same_answer_whatever_order_the_elements_arrive_in()
        {
            // Everything downstream is keyed on these names: group names, report sections, takeoff
            // subtotals. A clustering pass would be order-dependent; a histogram is not.
            var elevations = Storeys((0.0, 40), (3.6, 40), (7.2, 40)).ToList();

            var forward = LevelSet.Derive(elevations).Bands.Select(b => b.Name).ToList();
            var reversed = LevelSet.Derive(Enumerable.Reverse(elevations)).Bands.Select(b => b.Name).ToList();

            Assert.Equal(forward, reversed);
        }

        [Fact]
        public void One_stray_element_does_not_become_a_storey()
        {
            // Without a support threshold, a single mis-placed family at 47.3m becomes a level and
            // every report grows a storey nobody recognises.
            var elevations = Storeys((0.0, 100), (3.6, 100)).Concat(new[] { 47.3 });

            var levels = LevelSet.Derive(elevations);

            Assert.Equal(2, levels.Bands.Count);
            Assert.Equal(1, levels.DiscardedClusters);
            Assert.Contains("discarded", levels.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_floor_whose_elements_straddle_a_bin_edge_is_still_one_level()
        {
            // Bins are merged before support is tested, or a real floor splits into two halves
            // that each fall below the threshold and both vanish.
            var elevations = Enumerable.Range(0, 100).Select(i => 3.60 + ((i % 2) * 0.29));

            var levels = LevelSet.Derive(elevations);

            Assert.Single(levels.Bands);
        }

        [Fact]
        public void A_model_with_no_storeys_at_all_gets_one_band_rather_than_invented_ones()
        {
            // Elements spread evenly up a tower crane or a terrain mesh. Inventing storeys out of
            // noise would be worse than admitting there is one.
            var levels = LevelSet.Derive(Enumerable.Range(0, 300).Select(i => i * 0.5));

            Assert.Single(levels.Bands);
            Assert.True(levels.Bands[0].IsDerived);
        }

        [Fact]
        public void The_level_of_a_clash_is_the_level_of_the_point_not_of_the_element()
        {
            // A riser through six storeys clashes on the storey where the conflict is. Sending
            // somebody to the floor the pipe starts from wastes their morning.
            var levels = LevelSet.FromModel(new[]
            {
                new KeyValuePair<string, double>("L01", 0.0),
                new KeyValuePair<string, double>("L02", 3.6),
                new KeyValuePair<string, double>("L03", 7.2),
            });

            Assert.Equal("L03", levels.LevelAt(8.0));      // the clash point
            Assert.Equal("L01", levels.LevelOf(0.2, 9.0)); // the riser itself
        }

        [Fact]
        public void An_element_reports_every_level_it_passes_through()
        {
            var levels = LevelSet.FromModel(new[]
            {
                new KeyValuePair<string, double>("L01", 0.0),
                new KeyValuePair<string, double>("L02", 3.6),
                new KeyValuePair<string, double>("L03", 7.2),
            });

            Assert.Equal(new[] { "L01", "L02", "L03" }, levels.Spans(0.2, 9.0));
            Assert.Equal(new[] { "L02" }, levels.Spans(4.0, 5.0));
        }

        [Fact]
        public void Below_the_lowest_level_is_null_rather_than_the_lowest()
        {
            var levels = LevelSet.FromModel(new[] { new KeyValuePair<string, double>("L01", 0.0) });

            Assert.Null(levels.LevelAt(-2.0));
            Assert.Equal("L01", levels.LevelAt(0.0));
        }

        [Fact]
        public void No_elements_is_an_empty_set_not_an_error()
        {
            var levels = LevelSet.Derive(Array.Empty<double>());

            Assert.Empty(levels.Bands);
            Assert.Equal("no levels", levels.ToString());
            Assert.Null(levels.LevelAt(3.0));
        }
    }

    public class QuantityTests
    {
        private static Quantity Parse(string text)
        {
            Assert.True(Quantity.TryParse(text, out var quantity), "could not parse: " + text);
            return quantity;
        }

        [Fact]
        public void Units_are_converted_rather_than_stripped()
        {
            // Summing the numbers off the front of these strings gives 1200 + 1.2 + 120, which is
            // wrong by whatever mixture the federation happens to contain and looks plausible.
            Assert.Equal(1.2, Parse("1200 mm").Value, 9);
            Assert.Equal(1.2, Parse("1.2 m").Value, 9);
            Assert.Equal(1.2, Parse("120 cm").Value, 9);
        }

        [Fact]
        public void A_superscript_is_understood_because_the_host_writes_them()
        {
            Assert.Equal(QuantityKind.Area, Parse("4.5 m²").Kind);
            Assert.Equal(QuantityKind.Volume, Parse("0.25 m³").Kind);
            Assert.Equal(QuantityKind.Area, Parse("4.5 m2").Kind);
        }

        [Fact]
        public void Feet_and_inches_are_read_in_every_form_the_host_writes_them()
        {
            Assert.Equal(1.0668, Parse("3'-6\"").Value, 6);
            Assert.Equal(1.0668, Parse("3' 6\"").Value, 6);
            Assert.Equal(1.0668, Parse("3'6\"").Value, 6);
            Assert.Equal(0.1524, Parse("6\"").Value, 6);
            Assert.Equal(0.9144, Parse("3'").Value, 6);
        }

        [Fact]
        public void A_decimal_comma_is_decided_by_shape_not_by_the_machines_locale()
        {
            // "4,5" is four and a half in half of Europe and four thousand five hundred in the
            // other half. Reading it with the ambient culture makes a takeoff mean different
            // things on two machines in the same office.
            Assert.Equal(4.5, Parse("4,5 m").Value, 9);
            Assert.Equal(4500, Parse("4,500 m").Value, 9);      // three digits after: thousands
            Assert.Equal(1200, Parse("1 200 mm").Value * 1000, 6);
        }

        [Fact]
        public void An_unrecognised_unit_is_refused_rather_than_guessed()
        {
            // A total that silently dropped what it could not read is worse than no total: nobody
            // can tell it is short.
            Assert.False(Quantity.TryParse("about 1200", out _));
            Assert.False(Quantity.TryParse("1200 furlongs", out _));
            Assert.False(Quantity.TryParse("", out _));
            Assert.False(Quantity.TryParse(null, out _));
        }

        [Fact]
        public void A_bare_number_is_a_scalar_not_a_length()
        {
            Assert.Equal(QuantityKind.Scalar, Parse("14").Kind);
        }

        [Fact]
        public void Adding_incompatible_kinds_stops_rather_than_producing_a_number()
        {
            // Adding a length to an area is a takeoff that has silently gone wrong, and the sooner
            // it stops the better.
            Assert.Throws<InvalidOperationException>(() => Quantity.Metres(1).Add(Quantity.SquareMetres(1)));
            Assert.Equal(3, Quantity.Metres(1).Add(Quantity.Metres(2)).Value, 9);
        }

        [Fact]
        public void A_quantity_reads_back_in_its_base_unit()
        {
            Assert.Equal("1.2 m", Parse("1200 mm").ToString());
            Assert.Equal("14", Parse("14").ToString());
        }
    }

    public class TakeoffTests
    {
        [Fact]
        public void Values_in_different_units_add_up_correctly()
        {
            var result = Takeoff.Sum(new[]
            {
                new TakeoffLine("a", "L01", "1200 mm"),
                new TakeoffLine("b", "L01", "1.2 m"),
                new TakeoffLine("c", "L01", "3'-11.244\""),
            });

            var group = Assert.Single(result.Groups);
            Assert.Equal(3, group.Count);
            Assert.Equal(3.6, group.Total!.Value.Value, 3);
            Assert.True(result.IsComplete);
        }

        [Fact]
        public void What_cannot_be_read_is_counted_and_shown_rather_than_dropped()
        {
            var result = Takeoff.Sum(new[]
            {
                new TakeoffLine("a", "L01", "1200 mm"),
                new TakeoffLine("b", "L01", "about a metre"),
                new TakeoffLine("c", "L01", "about a metre"),
                new TakeoffLine("d", "L01", "~2m"),
            });

            var group = Assert.Single(result.Groups);
            Assert.Equal(4, group.Count);                  // the count is still right
            Assert.Equal(3, group.Unreadable);
            Assert.Equal(2, group.UnreadableExamples.Count);   // distinct, so the cause is visible
            Assert.False(result.IsComplete);
            Assert.Contains("this total is short", result.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_blank_value_is_not_an_unreadable_one()
        {
            // Plenty of elements simply do not carry the property being measured. Calling that
            // unreadable would bury the values that really are malformed.
            var result = Takeoff.Sum(new[]
            {
                new TakeoffLine("a", "L01", "1.5 m"),
                new TakeoffLine("b", "L01", null),
                new TakeoffLine("c", "L01", "   "),
            });

            var group = Assert.Single(result.Groups);
            Assert.Equal(3, group.Count);
            Assert.Equal(0, group.Unreadable);
            Assert.Equal(1.5, group.Total!.Value.Value, 9);
        }

        [Fact]
        public void A_group_mixing_lengths_and_areas_gets_no_total_at_all()
        {
            // It means the property is not the same property across the group, which is a mapping
            // mistake — and a number that added them would be wrong in a way nobody could see.
            var result = Takeoff.Sum(new[]
            {
                new TakeoffLine("a", "L01", "1.5 m"),
                new TakeoffLine("b", "L01", "2 m2"),
            });

            var group = Assert.Single(result.Groups);
            Assert.True(group.MixedKinds);
            Assert.Null(group.Total);
            Assert.Equal(2, group.Count);
            Assert.Contains("not all the same kind", group.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Subtotals_are_in_a_stable_order()
        {
            var result = Takeoff.Sum(new[]
            {
                new TakeoffLine("a", "L03", "1 m"),
                new TakeoffLine("b", "L01", "1 m"),
                new TakeoffLine("c", "L02", "1 m"),
            });

            Assert.Equal(new[] { "L01", "L02", "L03" }, result.Groups.Select(g => g.Name));
            Assert.Equal(3, result.Total!.Value.Value, 9);
        }

        [Fact]
        public void A_grand_total_is_withheld_when_the_groups_do_not_measure_the_same_thing()
        {
            var result = Takeoff.Sum(new[]
            {
                new TakeoffLine("a", "Lengths", "1 m"),
                new TakeoffLine("b", "Areas", "1 m2"),
            });

            Assert.Null(result.Total);
        }

        [Fact]
        public void An_empty_takeoff_is_an_empty_result()
        {
            var result = Takeoff.Sum(Array.Empty<TakeoffLine>());

            Assert.Empty(result.Groups);
            Assert.True(result.IsComplete);
            Assert.Null(result.Total);
        }
    }

    public class ModelHealthTests
    {
        private static HealthElement At(string id, double x, double y, double z, string source = "ARCH")
        {
            return new HealthElement(id, source)
            {
                Name = id, Category = "Walls", X = x, Y = y, Z = z,
                SizeX = 1, SizeY = 1, SizeZ = 1, PropertyCount = 4,
            };
        }

        private static List<HealthElement> Building(int count = 40) =>
            Enumerable.Range(0, count).Select(i => At("e" + i, 100 + i, 200, 0)).ToList();

        [Fact]
        public void A_stack_of_elements_on_the_origin_is_found()
        {
            // A failed insertion. Everything here clashes with everything else here, and the model
            // opens looking like a building.
            var all = Building();
            all.Add(At("bad-1", 0, 0, 0));
            all.Add(At("bad-2", 0, 0, 0));

            var report = ModelHealth.Check(all);

            var finding = report.Findings.Single(f => f.Rule == "origin-stack");
            Assert.Equal(2, finding.Count);
            Assert.Contains("shared coordinates", finding.Fix, StringComparison.Ordinal);
        }

        [Fact]
        public void A_model_that_is_simply_authored_around_zero_is_not_flagged()
        {
            // Flagging every small model authored near the origin is how a health check gets
            // ignored, and then it is not there when it matters.
            var all = Enumerable.Range(0, 40).Select(i => At("e" + i, i * 0.001, 0, 0)).ToList();

            Assert.DoesNotContain(ModelHealth.Check(all).Findings, f => f.Rule == "origin-stack");
        }

        [Fact]
        public void A_model_placed_a_kilometre_away_is_found()
        {
            var all = Building();
            all.Add(At("survey-1", 100000, 200000, 0, "MEP"));

            var finding = ModelHealth.Check(all).Findings.Single(f => f.Rule == "stray-geometry");

            Assert.Equal(1, finding.Count);
            Assert.Contains("survey point", finding.Fix, StringComparison.Ordinal);
        }

        [Fact]
        public void One_far_element_cannot_hide_itself_from_the_outlier_test()
        {
            // The reason this uses a median absolute deviation rather than a mean and a standard
            // deviation: a single element far enough away drags a mean past itself.
            var all = Building();
            all.Add(At("stray", 1e9, 1e9, 0));

            Assert.Contains(ModelHealth.Check(all).Findings, f => f.Rule == "stray-geometry");
        }

        [Fact]
        public void A_model_loaded_twice_is_found_and_named_as_such()
        {
            // It doubles every clash count in the report, and nothing else says so.
            var all = Building();
            all.Add(At("w-1", 500, 500, 0, "ARCH"));
            all.Add(At("w-1", 500, 500, 0, "ARCH-copy"));

            var finding = ModelHealth.Check(all).Findings.Single(f => f.Rule == "duplicate-elements");

            Assert.Equal(2, finding.Count);
            Assert.Contains("appended twice", finding.Fix, StringComparison.Ordinal);
            Assert.Contains("span two models", finding.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public void Geometry_with_no_size_is_found()
        {
            var all = Building();
            var flat = At("annotation", 300, 300, 0);
            flat.SizeX = flat.SizeY = flat.SizeZ = 0;
            all.Add(flat);

            Assert.Equal(1, ModelHealth.Check(all).Findings.Single(f => f.Rule == "degenerate-geometry").Count);
        }

        [Fact]
        public void Unnamed_and_property_less_elements_are_found_separately()
        {
            var all = Building();

            var unnamed = At("u1", 300, 300, 0);
            unnamed.Name = "  ";
            all.Add(unnamed);

            var bare = At("b1", 310, 300, 0);
            bare.PropertyCount = 0;
            all.Add(bare);

            var report = ModelHealth.Check(all);

            Assert.Equal(1, report.Findings.Single(f => f.Rule == "unnamed-elements").Count);
            Assert.Equal(1, report.Findings.Single(f => f.Rule == "no-properties").Count);
        }

        [Fact]
        public void Findings_carry_examples_so_they_can_be_checked_rather_than_believed()
        {
            var all = Building();
            for (var i = 0; i < 20; i++) all.Add(At("origin-" + i, 0, 0, 0));

            var finding = ModelHealth.Check(all).Findings.Single(f => f.Rule == "origin-stack");

            Assert.Equal(20, finding.Count);
            Assert.Equal(ModelHealth.ExampleCount, finding.Examples.Count);
            Assert.All(finding.Examples, e => Assert.Contains("(ARCH)", e, StringComparison.Ordinal));
        }

        [Fact]
        public void A_clean_model_says_so()
        {
            var report = ModelHealth.Check(Building());

            Assert.True(report.IsClean);
            Assert.Contains("nothing found", report.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void The_worst_finding_comes_first()
        {
            var all = Building();
            for (var i = 0; i < 10; i++) all.Add(At("origin-" + i, 0, 0, 0));

            var unnamed = At("u1", 300, 300, 0);
            unnamed.Name = null;
            all.Add(unnamed);

            Assert.Equal("origin-stack", ModelHealth.Check(all).Findings[0].Rule);
        }

        [Fact]
        public void An_empty_model_is_not_an_error()
        {
            var report = ModelHealth.Check(Array.Empty<HealthElement>());

            Assert.True(report.IsClean);
            Assert.Equal(0, report.ElementsChecked);
        }
    }
}
