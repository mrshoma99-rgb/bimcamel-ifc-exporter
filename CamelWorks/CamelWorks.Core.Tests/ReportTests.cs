using System;
using System.Linq;
using System.Text;
using CamelWorks.Core.Report;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class ReportDocumentTests
    {
        [Fact]
        public void A_report_needs_a_title()
        {
            Assert.Throws<ArgumentException>(() => new ReportDocument("  "));
        }

        [Fact]
        public void Cover_facts_keep_the_order_they_were_added()
        {
            // A cover that reorders itself between two revisions of the same report is one nobody
            // can compare side by side.
            var d = new ReportDocument("t").Fact("Project", "Job 1").Fact("Run", "2026-08-21").Fact("Scope", "L03");

            Assert.Equal(new[] { "Project", "Run", "Scope" }, d.CoverFacts.Select(f => f.Key).ToArray());
        }

        [Fact]
        public void A_row_with_the_wrong_number_of_cells_is_refused()
        {
            // A report whose columns silently shift is worse than one that fails to build.
            var t = new TableBlock("A", "B", "C");

            Assert.Throws<ArgumentException>(() => t.Row("1", "2"));
            Assert.Throws<ArgumentException>(() => t.Row("1", "2", "3", "4"));
            t.Row("1", "2", "3");
        }

        [Fact]
        public void A_table_needs_at_least_one_column()
        {
            Assert.Throws<ArgumentException>(() => new TableBlock());
        }

        [Fact]
        public void Heading_levels_are_clamped_rather_than_rejected()
        {
            // A caller computing a depth should not be able to crash a report by nesting too far.
            Assert.Equal(1, new HeadingBlock("x", 0).Level);
            Assert.Equal(1, new HeadingBlock("x", -3).Level);
            Assert.Equal(4, new HeadingBlock("x", 9).Level);
        }

        [Fact]
        public void A_callout_is_clamped_into_the_frame_rather_than_dropped()
        {
            // A clash centre can project just outside the frame; dropping the marker would
            // silently lose that row's location.
            Assert.Equal(0.0, new Callout(1, -5, 0.5).X, 6);
            Assert.Equal(1.0, new Callout(1, 9, 0.5).X, 6);
            Assert.Equal(0.5, new Callout(1, double.NaN, 0.5).X, 6);
        }

        [Fact]
        public void Callouts_are_numbered_from_one()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Callout(0, 0.5, 0.5));
        }

        [Fact]
        public void A_missing_image_knows_it_is_missing()
        {
            Assert.True(new ImageBlock(null).IsMissing);
            Assert.True(new ImageBlock(Array.Empty<byte>()).IsMissing);
            Assert.False(new ImageBlock(new byte[] { 1, 2, 3 }).IsMissing);
        }

        [Fact]
        public void An_empty_disclosure_says_so_so_a_writer_can_omit_it()
        {
            var d = new DisclosureBlock("Not shown");
            Assert.True(d.IsEmpty);

            d.Line("  ");            // whitespace is not a disclosure
            Assert.True(d.IsEmpty);

            d.Line("2 images failed to render");
            Assert.False(d.IsEmpty);
        }
    }

    public class HtmlReportWriterTests
    {
        private static ReportDocument Sample()
        {
            var d = new ReportDocument("Coordination report") { Subtitle = "L03 HVAC v Struct" };
            d.Fact("Project", "Job 1");
            d.Heading("Summary");
            d.Add(new TableBlock("Group", "Status", "Assignee")
                .Row("L03-C4-MEP", "Active", "MEP")
                .Row("L03-D2-MEP", "Resolved", null));
            return d;
        }

        [Fact]
        public void Produces_one_self_contained_document()
        {
            // This file gets emailed. A folder of sibling assets does not survive being forwarded.
            var html = HtmlReportWriter.Write(Sample());

            Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
            Assert.Contains("<style>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<link", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        }

        [Fact]
        public void An_image_is_embedded_rather_than_referenced()
        {
            var d = new ReportDocument("t");
            d.Add(new ImageBlock(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "A view"));

            var html = HtmlReportWriter.Write(d);

            Assert.Contains("src=\"data:image/png;base64,iVBORw==\"", html, StringComparison.Ordinal);
            Assert.Contains("A view", html, StringComparison.Ordinal);
        }

        [Fact]
        public void A_failed_render_is_stated_not_skipped()
        {
            // A gap where an image should be reads as "there was nothing to show" — a different
            // and much better-sounding claim than the truth.
            var d = new ReportDocument("t");
            d.Add(new ImageBlock(null, "Group 12"));

            var html = HtmlReportWriter.Write(d);

            Assert.Contains("could not be rendered", html, StringComparison.Ordinal);
            Assert.Contains("Group 12", html, StringComparison.Ordinal);
        }

        [Fact]
        public void Callouts_are_drawn_as_vector_over_the_image()
        {
            // Vector, not composited: it is what keeps the engine free of raster work and
            // therefore verifiable on a Linux runner.
            var image = new ImageBlock(new byte[] { 1 }, "x");
            image.Callouts.Add(new Callout(1, 0.25, 0.75));
            image.Callouts.Add(new Callout(2, 0.5, 0.5));

            var html = HtmlReportWriter.Write(new ReportDocument("t").Add(image));

            Assert.Contains("<svg class=\"callouts\"", html, StringComparison.Ordinal);
            Assert.Contains("cx=\"25\" cy=\"75\"", html, StringComparison.Ordinal);
            Assert.Contains(">1</text>", html, StringComparison.Ordinal);
            Assert.Contains(">2</text>", html, StringComparison.Ordinal);
        }

        [Fact]
        public void A_null_cell_renders_empty_never_as_the_word_null()
        {
            var html = HtmlReportWriter.Write(Sample());

            Assert.Contains("<td>Resolved</td><td></td>", html, StringComparison.Ordinal);
            Assert.DoesNotContain(">null<", html, StringComparison.Ordinal);
        }

        [Fact]
        public void Text_from_the_model_cannot_break_out_of_the_markup()
        {
            // Element names come from the model. A name containing markup is not an attack, it is
            // Tuesday — but it still has to be escaped.
            var d = new ReportDocument("Report <b>&</b>");
            d.Add(new TableBlock("Name").Row("<script>alert(1)</script>"));

            var html = HtmlReportWriter.Write(d);

            Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
            Assert.Contains("Report &lt;b&gt;&amp;", html, StringComparison.Ordinal);
        }

        [Fact]
        public void A_colour_from_a_project_file_cannot_inject_css()
        {
            // A colour arriving from a project file is data, and data does not get to close a
            // quote and start writing style rules.
            var legend = new LegendBlock("By status")
                .Entry("Bad", "red;} body{display:none} .x{")
                .Entry("Good", "#00ff00", 12);

            var html = HtmlReportWriter.Write(new ReportDocument("t").Add(legend));

            Assert.DoesNotContain("display:none", html, StringComparison.Ordinal);
            Assert.Contains("background:#000000", html, StringComparison.Ordinal);   // fell back
            Assert.Contains("background:#00ff00", html, StringComparison.Ordinal);
            Assert.Contains("(12)", html, StringComparison.Ordinal);
        }

        [Fact]
        public void A_disclosure_is_rendered_when_it_has_content_and_omitted_when_empty()
        {
            var withContent = new DisclosureBlock("Not shown in this report")
                .Line("412 results suppressed by 6 rules");

            Assert.Contains("412 results suppressed",
                HtmlReportWriter.Write(new ReportDocument("t").Add(withContent)), StringComparison.Ordinal);

            Assert.DoesNotContain("Not shown in this report",
                HtmlReportWriter.Write(new ReportDocument("t").Add(new DisclosureBlock("Not shown in this report"))),
                StringComparison.Ordinal);
        }

        [Fact]
        public void Headings_start_below_the_title()
        {
            // The document title owns h1; a level-1 heading is h2, or the outline is wrong for
            // anything reading the structure.
            var html = HtmlReportWriter.Write(new ReportDocument("t").Heading("Section", 1).Heading("Sub", 2));

            Assert.Contains("<h2>Section</h2>", html, StringComparison.Ordinal);
            Assert.Contains("<h3>Sub</h3>", html, StringComparison.Ordinal);
        }

        [Fact]
        public void A_page_break_survives_into_print()
        {
            var html = HtmlReportWriter.Write(new ReportDocument("t").PageBreak());

            Assert.Contains("pagebreak", html, StringComparison.Ordinal);
            Assert.Contains("page-break-after:always", html, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_report_still_renders_a_valid_document()
        {
            // Zero setup: a report over a model with nothing configured is a normal thing to ask
            // for, and it must not produce a broken file.
            var html = HtmlReportWriter.Write(new ReportDocument("Nothing yet"));

            Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
            Assert.EndsWith("</html>\n", html, StringComparison.Ordinal);
            Assert.Contains("Nothing yet", html, StringComparison.Ordinal);
        }
    }
}
