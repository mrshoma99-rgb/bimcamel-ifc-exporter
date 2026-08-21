using System;
using System.Collections.Generic;
using System.Linq;
using CamelWorks.Core.Data;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class HeadroomTests
    {
        private static HeadroomElement At(string id, double x, double y, double z,
                                          double sizeX = 4, double sizeY = 4, double sizeZ = 0.2) =>
            new HeadroomElement(id)
            {
                MinX = x, MaxX = x + sizeX,
                MinY = y, MaxY = y + sizeY,
                MinZ = z, MaxZ = z + sizeZ,
            };

        [Fact]
        public void Finds_the_gap_a_clash_test_never_will()
        {
            // The whole reason this exists: a duct 1.9 m above a floor clashes with nothing,
            // because there is air between them.
            var floor = At("slab", 0, 0, 0);
            var duct = At("duct", 1, 1, 2.0, sizeZ: 0.4);

            var result = Headroom.Check(new[] { floor }, new[] { duct }, 2.100);

            Assert.Single(result.Spans);
            Assert.Equal(1.8, result.Spans[0].Clear, 3);
            Assert.Equal("duct", result.Spans[0].Obstruction.Id);
        }

        [Fact]
        public void Something_high_enough_is_not_reported()
        {
            var result = Headroom.Check(new[] { At("slab", 0, 0, 0) },
                                        new[] { At("duct", 1, 1, 2.5) }, 2.100);

            Assert.Empty(result.Spans);
            Assert.Null(result.Worst);
            Assert.Contains("nothing below it", result.ToString());
        }

        [Fact]
        public void Something_that_does_not_pass_overhead_is_not_reported()
        {
            // Twenty metres away in plan. Low, and irrelevant.
            var result = Headroom.Check(new[] { At("slab", 0, 0, 0) },
                                        new[] { At("duct", 20, 20, 1.5) }, 2.100);

            Assert.Empty(result.Spans);
        }

        [Fact]
        public void Something_through_the_floor_is_a_clash_not_a_headroom_problem()
        {
            // Starts below the floor's top surface, so the gap is negative.
            var result = Headroom.Check(new[] { At("slab", 0, 0, 0) },
                                        new[] { At("pipe", 1, 1, -0.5, sizeZ: 3) }, 2.100);

            Assert.Empty(result.Spans);
        }

        [Fact]
        public void Each_surface_reports_only_its_worst_obstruction()
        {
            // A slab under a plant room has a thousand things above it and one of them is lowest.
            // Reporting all thousand buries the answer.
            var floor = At("slab", 0, 0, 0);

            var obstructions = new[]
            {
                At("duct", 1, 1, 2.0),
                At("tray", 1, 1, 1.7),
                At("pipe", 1, 1, 1.9),
            };

            var result = Headroom.Check(new[] { floor }, obstructions, 2.100);

            Assert.Single(result.Spans);
            Assert.Equal("tray", result.Spans[0].Obstruction.Id);
        }

        [Fact]
        public void Results_come_back_worst_first()
        {
            var floors = new[] { At("a", 0, 0, 0), At("b", 100, 0, 0), At("c", 200, 0, 0) };

            var obstructions = new[]
            {
                At("over-a", 1, 1, 2.0),
                At("over-b", 101, 1, 1.5),
                At("over-c", 201, 1, 1.8),
            };

            var result = Headroom.Check(floors, obstructions, 2.100);

            Assert.Equal(new[] { "over-b", "over-c", "over-a" },
                         result.Spans.Select(s => s.Obstruction.Id).ToArray());
        }

        [Fact]
        public void An_element_spanning_the_whole_plate_is_still_found_by_the_floors_under_it()
        {
            // The bucket for oversized elements has to be searched by every query, not only by
            // oversized queries. Miss that and the one thing everybody cares about is invisible.
            var ceiling = new HeadroomElement("ceiling")
            {
                MinX = -100000, MaxX = 100000,
                MinY = -100000, MaxY = 100000,
                MinZ = 1.8, MaxZ = 1.9,
            };

            var result = Headroom.Check(new[] { At("slab", 0, 0, 0) }, new[] { ceiling }, 2.100);

            Assert.Single(result.Spans);
            Assert.Equal("ceiling", result.Spans[0].Obstruction.Id);
        }

        [Fact]
        public void A_surface_spanning_the_whole_plate_still_finds_ordinary_things_above_it()
        {
            var slab = new HeadroomElement("slab")
            {
                MinX = -100000, MaxX = 100000,
                MinY = -100000, MaxY = 100000,
                MinZ = -0.2, MaxZ = 0,
            };

            var result = Headroom.Check(new[] { slab }, new[] { At("duct", 1, 1, 1.6) }, 2.100);

            Assert.Single(result.Spans);
        }

        [Fact]
        public void Running_one_list_against_itself_reports_the_pairs_and_not_the_selves()
        {
            // What happens when somebody picks "everything" for both sides, which is the default
            // and has to behave. A slab does not obstruct itself; the one above it does.
            var lower = At("lower", 0, 0, 0);
            var upper = At("upper", 0, 0, 1.9);

            var all = new[] { lower, upper };

            var result = Headroom.Check(all, all, 2.100);

            Assert.Single(result.Spans);
            Assert.Equal("lower", result.Spans[0].Floor.Id);
            Assert.Equal("upper", result.Spans[0].Obstruction.Id);
        }

        [Fact]
        public void A_corrupt_bounding_box_is_skipped_rather_than_poisoning_the_grid()
        {
            var broken = new HeadroomElement("broken")
            {
                MinX = double.NaN, MaxX = double.NaN,
                MinY = 0, MaxY = 1, MinZ = 1, MaxZ = 2,
            };

            var result = Headroom.Check(new[] { At("slab", 0, 0, 0) }, new[] { broken }, 2.100);

            Assert.Empty(result.Spans);
            Assert.Equal(0, result.Obstructions);
        }

        [Fact]
        public void The_reported_position_is_where_the_two_actually_overlap()
        {
            // Not the centre of either one: a coordinator sent to the middle of a forty-metre duct
            // is nowhere near the place it is too low.
            var floor = At("slab", 0, 0, 0, sizeX: 40, sizeY: 10);
            var duct = At("duct", 30, 2, 1.8, sizeX: 40, sizeY: 1);

            var result = Headroom.Check(new[] { floor }, new[] { duct }, 2.100);

            Assert.Equal(35, result.Spans[0].X, 3);
            Assert.Equal(2.5, result.Spans[0].Y, 3);
        }

        [Fact]
        public void Nothing_to_check_is_an_empty_result_not_an_error()
        {
            var result = Headroom.Check(Array.Empty<HeadroomElement>(), Array.Empty<HeadroomElement>());

            Assert.Empty(result.Spans);
            Assert.Equal(0, result.Floors);
        }

        [Fact]
        public void An_impossible_minimum_is_refused_rather_than_reporting_everything()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Headroom.Check(Array.Empty<HeadroomElement>(), Array.Empty<HeadroomElement>(), 0));
        }
    }
}
