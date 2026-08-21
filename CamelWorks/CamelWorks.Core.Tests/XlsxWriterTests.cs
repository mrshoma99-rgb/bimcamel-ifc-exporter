using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CamelWorks.Core.Report;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class XlsxWriterTests
    {
        private static string Part(byte[] xlsx, string path)
        {
            using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
            var entry = zip.GetEntry(path);
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static string[] Paths(byte[] xlsx)
        {
            using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
            return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        [Fact]
        public void Produces_the_parts_a_reader_requires()
        {
            var bytes = XlsxWriter.Write(new[] { new XlsxSheet("Takeoff").Row("A") });

            Assert.Equal(
                new[]
                {
                    "[Content_Types].xml",
                    "_rels/.rels",
                    "xl/_rels/workbook.xml.rels",
                    "xl/styles.xml",
                    "xl/workbook.xml",
                    "xl/worksheets/sheet1.xml",
                },
                Paths(bytes));
        }

        [Fact]
        public void Is_a_real_zip_that_round_trips()
        {
            var bytes = XlsxWriter.Write(new[] { new XlsxSheet("S").Row("hello") });

            Assert.Equal(0x50, bytes[0]);   // "PK"
            Assert.Equal(0x4B, bytes[1]);
            Assert.Contains("hello", Part(bytes, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // The thing that actually matters: Excel not reinterpreting our data
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("42")]
        [InlineData("-17")]
        [InlineData("0")]
        [InlineData("3.25")]
        [InlineData("0.5")]
        public void Plain_numbers_are_written_as_numbers(string value)
        {
            var sheet = Part(XlsxWriter.Write(new[] { new XlsxSheet("S").Row("h").Row(value) }),
                "xl/worksheets/sheet1.xml");

            Assert.Contains("<v>" + value + "</v>", sheet, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("007")]           // a drawing number, not seven
        [InlineData("L03-1")]         // Excel would make this a date
        [InlineData("1-2")]           // and this
        [InlineData("+5")]
        [InlineData(" 5")]
        [InlineData("5 ")]
        [InlineData("1e6")]
        [InlineData("1.2.3")]
        [InlineData("12345678901234567890")]
        public void Anything_Excel_might_reinterpret_stays_a_string(string value)
        {
            // "007" becoming 7, or "L03-1" becoming a date, is a takeoff quantity or a group name
            // silently changing on the way to the client.
            var sheet = Part(XlsxWriter.Write(new[] { new XlsxSheet("S").Row("h").Row(value) }),
                "xl/worksheets/sheet1.xml");

            Assert.Contains("t=\"inlineStr\"", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("<v>", sheet, StringComparison.Ordinal);
        }

        [Fact]
        public void Whitespace_inside_a_string_is_preserved()
        {
            var sheet = Part(XlsxWriter.Write(new[] { new XlsxSheet("S").Row("  padded  ") }),
                "xl/worksheets/sheet1.xml");

            Assert.Contains("xml:space=\"preserve\"", sheet, StringComparison.Ordinal);
            Assert.Contains(">  padded  <", sheet, StringComparison.Ordinal);
        }

        [Fact]
        public void A_null_or_empty_cell_is_written_as_an_empty_cell()
        {
            var sheet = Part(XlsxWriter.Write(new[] { new XlsxSheet("S").Row("a", null, "") }),
                "xl/worksheets/sheet1.xml");

            Assert.Contains("<c r=\"B1\" s=\"1\"/>", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("null", sheet, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // Files that will not open are worse than missing features
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("L03: MEP v STR", "L03- MEP v STR")]
        [InlineData("a/b\\c?d*e[f]g", "a-b-c-d-e-f-g")]
        [InlineData("  trimmed  ", "trimmed")]
        public void Sheet_names_are_sanitised_because_Excel_refuses_the_whole_file(string input, string expected)
        {
            Assert.Equal(expected, XlsxSheet.Sanitise(input));
        }

        [Fact]
        public void A_long_sheet_name_is_truncated_to_the_limit()
        {
            Assert.Equal(31, XlsxSheet.Sanitise(new string('x', 60)).Length);
        }

        [Fact]
        public void Two_sheets_with_the_same_name_are_refused_up_front()
        {
            // Excel refuses the file; better to fail where the cause is obvious.
            Assert.Throws<ArgumentException>(() =>
                XlsxWriter.Write(new[] { new XlsxSheet("Data"), new XlsxSheet("data") }));
        }

        [Fact]
        public void A_workbook_needs_a_sheet()
        {
            Assert.Throws<ArgumentException>(() => XlsxWriter.Write(Array.Empty<XlsxSheet>()));
        }

        [Fact]
        public void Control_characters_are_dropped_rather_than_producing_an_unopenable_file()
        {
            // XML 1.0 cannot carry them at all, escaped or not.
            var sheet = Part(XlsxWriter.Write(new[] { new XlsxSheet("S").Row("bell\u0007here") }),
                "xl/worksheets/sheet1.xml");

            Assert.Contains("bellhere", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("\u0007", sheet, StringComparison.Ordinal);
        }

        [Fact]
        public void Markup_in_a_value_or_a_sheet_name_is_escaped()
        {
            var bytes = XlsxWriter.Write(new[] { new XlsxSheet("A&B").Row("<tag> & \"quote\"") });

            Assert.Contains("name=\"A&amp;B\"", Part(bytes, "xl/workbook.xml"), StringComparison.Ordinal);
            Assert.Contains("&lt;tag&gt; &amp; \"quote\"", Part(bytes, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // Shape
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Cell_references_carry_past_the_twenty_sixth_column()
        {
            var wide = new XlsxSheet("S");
            wide.Row(Enumerable.Range(0, 30).Select(i => (string?)("c" + i)).ToArray());

            var sheet = Part(XlsxWriter.Write(new[] { wide }), "xl/worksheets/sheet1.xml");

            Assert.Contains("r=\"Z1\"", sheet, StringComparison.Ordinal);
            Assert.Contains("r=\"AA1\"", sheet, StringComparison.Ordinal);
            Assert.Contains("r=\"AD1\"", sheet, StringComparison.Ordinal);
        }

        [Fact]
        public void The_header_row_is_bold_and_frozen()
        {
            var sheet = Part(XlsxWriter.Write(new[] { new XlsxSheet("S").Row("Header").Row("body") }),
                "xl/worksheets/sheet1.xml");

            Assert.Contains("s=\"1\"", sheet, StringComparison.Ordinal);
            Assert.Contains("state=\"frozen\"", sheet, StringComparison.Ordinal);
        }

        [Fact]
        public void A_header_only_sheet_is_not_frozen_because_there_is_nothing_to_scroll()
        {
            var sheet = Part(XlsxWriter.Write(new[] { new XlsxSheet("S").Row("Header") }),
                "xl/worksheets/sheet1.xml");

            Assert.DoesNotContain("frozen", sheet, StringComparison.Ordinal);
        }

        [Fact]
        public void Column_width_is_clamped_so_one_bad_cell_cannot_ruin_a_sheet()
        {
            var s = new XlsxSheet("S").Row("h").Row(new string('x', 500));

            var sheet = Part(XlsxWriter.Write(new[] { s }), "xl/worksheets/sheet1.xml");

            Assert.Contains("width=\"60\"", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("width=\"502\"", sheet, StringComparison.Ordinal);
        }

        [Fact]
        public void Multiple_sheets_are_all_declared_and_related()
        {
            var bytes = XlsxWriter.Write(new[]
            {
                new XlsxSheet("Summary").Row("a"),
                new XlsxSheet("Detail").Row("b"),
            });

            Assert.Contains("xl/worksheets/sheet2.xml", Paths(bytes));
            Assert.Contains("name=\"Detail\"", Part(bytes, "xl/workbook.xml"), StringComparison.Ordinal);
            Assert.Contains("sheet2.xml", Part(bytes, "xl/_rels/workbook.xml.rels"), StringComparison.Ordinal);
            // Styles takes the id after the sheets.
            Assert.Contains("Id=\"rId3\"", Part(bytes, "xl/_rels/workbook.xml.rels"), StringComparison.Ordinal);
        }

        [Fact]
        public void A_report_table_converts_straight_to_a_sheet()
        {
            var table = new TableBlock("Group", "Count").Row("L03-C4", "12").Row("L03-D2", null);

            var sheet = Part(XlsxWriter.Write("Clashes", table), "xl/worksheets/sheet1.xml");

            Assert.Contains("Group", sheet, StringComparison.Ordinal);
            Assert.Contains("<v>12</v>", sheet, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_sheet_still_produces_a_valid_workbook()
        {
            // Zero setup: exporting before anything has been configured is a normal thing to do.
            var bytes = XlsxWriter.Write(new[] { new XlsxSheet("Empty") });

            Assert.Contains("<sheetData></sheetData>", Part(bytes, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        }
    }
}
