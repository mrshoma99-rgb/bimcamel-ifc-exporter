using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CamelWorks.Core.Report;
using Xunit;

namespace CamelWorks.Core.Tests
{
    /// <summary>Builds genuine PNG files, so the reader is tested against the real format.</summary>
    internal static class PngFixture
    {
        /// <summary>An 8-bit PNG. Colour type 2 is RGB, 6 is RGBA, 0 is grey.</summary>
        internal static byte[] Make(int width, int height, int colourType)
        {
            var samples = colourType == 6 ? 4 : colourType == 2 ? 3 : 1;

            // One filter byte per row, then the pixels. Filter 0 is "none", which keeps the
            // fixture readable and still exercises the unfilter path's default branch.
            var raw = new byte[height * ((width * samples) + 1)];
            var at = 0;

            for (var y = 0; y < height; y++)
            {
                raw[at++] = 0;

                for (var x = 0; x < width; x++)
                    for (var s = 0; s < samples; s++)
                        raw[at++] = (byte)(((x * 37) + (y * 11) + (s * 59)) & 0xFF);
            }

            var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            var header = new MemoryStream();
            WriteInt(header, width);
            WriteInt(header, height);
            header.WriteByte(8);                       // bit depth
            header.WriteByte((byte)colourType);
            header.WriteByte(0);                       // compression
            header.WriteByte(0);                       // filter
            header.WriteByte(0);                       // interlace
            Chunk(png, "IHDR", header.ToArray());

            Chunk(png, "IDAT", Zlib(raw));
            Chunk(png, "IEND", Array.Empty<byte>());

            return png.ToArray();
        }

        private static byte[] Zlib(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);

                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(data, 0, data.Length);

                uint a = 1, b = 0;
                foreach (var value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
                var adler = (b << 16) | a;

                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);

                return output.ToArray();
            }
        }

        private static void Chunk(Stream png, string type, byte[] data)
        {
            WriteInt(png, data.Length);

            var body = new byte[4 + data.Length];
            Encoding.ASCII.GetBytes(type, 0, 4, body, 0);
            Array.Copy(data, 0, body, 4, data.Length);

            png.Write(body, 0, body.Length);
            WriteInt(png, (int)Crc32(body));
        }

        private static void WriteInt(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        // Real CRCs, so the fixture is a file any tool would accept rather than one only our own
        // reader tolerates.
        private static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;

            foreach (var b in data)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }

    public class PngImageTests
    {
        [Fact]
        public void An_opaque_png_is_passed_through_without_being_decoded()
        {
            // PDF's FlateDecode understands PNG's own row predictors, so an opaque image needs no
            // decoding at all — which is both far faster and far less to get wrong than decoding
            // and re-encoding several megabytes of screenshot.
            Assert.True(PngImage.TryRead(PngFixture.Make(8, 4, colourType: 2), out var image));

            Assert.True(image!.PassedThrough);
            Assert.Equal("DeviceRGB", image.ColourSpace);
            Assert.Equal(3, image.Colours);
            Assert.Null(image.Alpha);
            Assert.Equal(8, image.Width);
        }

        [Fact]
        public void An_image_with_alpha_is_split_into_colour_and_a_soft_mask()
        {
            // PDF keeps alpha in a separate image, so this one has to be decoded, unfiltered,
            // split and re-compressed. Navisworks snapshots routinely have an alpha channel, and
            // the choice otherwise is between a wrong picture and no picture.
            Assert.True(PngImage.TryRead(PngFixture.Make(6, 3, colourType: 6), out var image));

            Assert.False(image!.PassedThrough);
            Assert.Equal("DeviceRGB", image.ColourSpace);
            Assert.NotNull(image.Alpha);
        }

        [Fact]
        public void Grey_is_understood_as_well_as_colour()
        {
            Assert.True(PngImage.TryRead(PngFixture.Make(4, 4, colourType: 0), out var image));

            Assert.Equal("DeviceGray", image!.ColourSpace);
            Assert.Equal(1, image.Colours);
        }

        [Fact]
        public void Anything_unsupported_is_refused_rather_than_half_read()
        {
            // A report with an obviously missing picture is fixable; one with a corrupt image is
            // not even noticed.
            Assert.False(PngImage.TryRead(null, out _));
            Assert.False(PngImage.TryRead(new byte[] { 1, 2, 3 }, out _));
            Assert.False(PngImage.TryRead(Encoding.ASCII.GetBytes("not a png at all, really"), out _));
        }

        [Fact]
        public void A_truncated_file_does_not_throw()
        {
            var png = PngFixture.Make(8, 8, colourType: 2);
            var truncated = new byte[png.Length / 2];
            Array.Copy(png, truncated, truncated.Length);

            Assert.False(PngImage.TryRead(truncated, out _));
        }
    }

    public class PdfReportWriterTests
    {
        private static ReportDocument Document()
        {
            var document = new ReportDocument("Riverside — clash report")
            {
                Subtitle = "Stage 4 coordination, week 12",
            };

            document.CoverFacts.Add(new KeyValuePair<string, string?>("Federation", "Riverside.nwf"));
            document.CoverFacts.Add(new KeyValuePair<string, string?>("Prepared by", "J. Green"));

            document.Blocks.Add(new HeadingBlock("Summary", 1));
            document.Blocks.Add(new ParagraphBlock(
                "1,910 results were reduced to 214 groups by three suppression rules. "
                + "Every number below is clickable back to the rows behind it."));

            var table = new TableBlock(new[] { "Group", "Level", "Party", "Results" });
            for (var i = 0; i < 60; i++)
                table.Rows.Add(new[]
                {
                    "ARCH v MEP · L0" + ((i % 6) + 1),
                    "L0" + ((i % 6) + 1),
                    i % 2 == 0 ? "Structures" : "MEP",
                    (i * 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

            document.Blocks.Add(table);
            document.Blocks.Add(new PageBreakBlock());
            document.Blocks.Add(new HeadingBlock("Evidence", 1));

            var image = new ImageBlock(PngFixture.Make(240, 160, colourType: 6), "Riser 3, looking north");
            image.Callouts.Add(new Callout(1, 0.35, 0.4));
            image.Callouts.Add(new Callout(2, 0.7, 0.62));
            document.Blocks.Add(image);

            var legend = new LegendBlock("Status");
            legend.Entries.Add(new LegendEntry("New", "#c0392b", 44));
            legend.Entries.Add(new LegendEntry("Reviewed", "#2980b9", 170));
            document.Blocks.Add(legend);

            var disclosure = new DisclosureBlock("What this report does not say");
            disclosure.Lines.Add("3 perspective viewpoints had their field of view clamped.");
            disclosure.Lines.Add("41 referenced elements have no IFC GUID, so a receiving tool cannot select them.");
            document.Blocks.Add(disclosure);

            return document;
        }

        private static byte[] Write(ReportDocument document)
        {
            using (var buffer = new MemoryStream())
            {
                PdfReportWriter.Write(buffer, document);
                return buffer.ToArray();
            }
        }

        private static string AsText(byte[] pdf)
        {
            var sb = new StringBuilder(pdf.Length);
            foreach (var b in pdf) sb.Append((char)b);
            return sb.ToString();
        }

        [Fact]
        public void The_file_has_the_structure_a_reader_looks_for()
        {
            var text = AsText(Write(Document()));

            Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
            Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);
            Assert.Contains("/Type /Pages", text, StringComparison.Ordinal);
            Assert.Contains("startxref", text, StringComparison.Ordinal);
            Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        }

        [Fact]
        public void The_binary_marker_is_present_so_a_transfer_tool_does_not_mangle_it()
        {
            // Without it a well-meaning FTP client translates the line endings inside an image
            // stream and quietly destroys the file.
            var pdf = Write(Document());

            Assert.Contains(pdf.Skip(9).Take(6), b => b > 127);
        }

        [Fact]
        public void The_cross_reference_offsets_point_at_the_objects_they_claim_to()
        {
            // The one part of a PDF a reader will not forgive. An off-by-one here produces a file
            // that opens in one viewer and fails in another, which is the worst way to find out.
            var pdf = Write(Document());
            var text = AsText(pdf);

            var startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
            var offset = int.Parse(text.Substring(startxref + 9).Trim().Split('\n')[0].Trim(),
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal("xref", text.Substring(offset, 4));

            var lines = text.Substring(offset).Split('\n');
            var count = int.Parse(lines[1].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);

            for (var i = 1; i < count; i++)
            {
                var entry = int.Parse(lines[1 + i + 1].Substring(0, 10),
                    System.Globalization.CultureInfo.InvariantCulture);

                Assert.Equal(i + " 0 obj", text.Substring(entry, (i + " 0 obj").Length));
            }
        }

        [Fact]
        public void A_long_table_runs_onto_more_pages_and_repeats_its_headers()
        {
            // A page of unlabelled columns is not something a reader can use, and it is the single
            // most common way a generated report becomes unreadable.
            var text = AsText(Write(Document()));

            var pages = CountOf(text, "/Type /Page\n") + CountOf(text, "/Type /Page ");
            Assert.True(pages >= 3, "expected the table to run onto several pages, got " + pages);

            Assert.True(CountOf(text, "(Group) Tj") >= 2, "the header should be repeated on each page");
        }

        [Fact]
        public void An_image_becomes_an_xobject_with_a_soft_mask()
        {
            var text = AsText(Write(Document()));

            Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
            Assert.Contains("/SMask", text, StringComparison.Ordinal);
            Assert.Contains("/XObject <<", text, StringComparison.Ordinal);
            Assert.Contains(" Do Q", text, StringComparison.Ordinal);
        }

        [Fact]
        public void An_opaque_image_declares_the_predictors_its_stream_still_carries()
        {
            // Declaring them on a re-compressed stream would have the reader undo filters that are
            // not there, which produces a picture of noise.
            var document = new ReportDocument("t");
            document.Blocks.Add(new ImageBlock(PngFixture.Make(16, 8, colourType: 2), null));

            var text = AsText(Write(document));

            Assert.Contains("/Predictor 15", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/SMask", text, StringComparison.Ordinal);
        }

        [Fact]
        public void An_image_that_cannot_be_read_is_named_rather_than_skipped()
        {
            var document = new ReportDocument("t");
            document.Blocks.Add(new ImageBlock(new byte[] { 1, 2, 3 }, "Riser 3"));

            var text = AsText(Write(document));

            Assert.Contains("could not be embedded", text, StringComparison.Ordinal);
            Assert.Contains("(Riser 3) Tj", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Punctuation_the_product_emits_survives_into_the_file()
        {
            // The report text is full of middle dots and en dashes. Encoding the content stream as
            // ASCII would turn every one of them into a question mark.
            var document = new ReportDocument("t");
            document.Blocks.Add(new ParagraphBlock("214 groups · 44 unassigned — see below"));

            var pdf = Write(document);

            Assert.Contains(pdf, b => b == 0xB7);   // middle dot
            Assert.Contains(pdf, b => b == 0x97);   // em dash
        }

        [Fact]
        public void A_character_with_no_equivalent_is_transliterated_rather_than_lost()
        {
            var document = new ReportDocument("t");
            document.Blocks.Add(new ParagraphBlock("1,910 → 214"));

            var text = AsText(Write(document));

            Assert.Contains("1,910 > 214", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Brackets_in_the_text_do_not_end_the_string_early()
        {
            var document = new ReportDocument("t");
            document.Blocks.Add(new ParagraphBlock("Riser 3 (agreed) \\ north"));

            var text = AsText(Write(document));

            Assert.Contains(@"Riser 3 \(agreed\) \\ north", text, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_report_is_still_a_valid_file()
        {
            var text = AsText(Write(new ReportDocument("Nothing to report")));

            Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);
            Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------
        // The dump the structural check reads. See CamelWorks/schemas/pdf/validate.sh.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Writes_the_file_the_structural_check_reads()
        {
            // Same reasoning as the BCF dump: assertions over the bytes prove the writer did what
            // it meant to, and prove nothing about whether a reader will accept the result. qpdf
            // walks the object graph and the cross-reference table properly.
            var folder = Path.Combine(AppContext.BaseDirectory, "pdf-validation");
            Directory.CreateDirectory(folder);

            File.WriteAllBytes(Path.Combine(folder, "report.pdf"), Write(Document()));
            File.WriteAllBytes(Path.Combine(folder, "empty.pdf"), Write(new ReportDocument("Nothing to report")));

            var opaque = new ReportDocument("Opaque image");
            opaque.Blocks.Add(new ImageBlock(PngFixture.Make(32, 24, colourType: 2), "opaque"));
            File.WriteAllBytes(Path.Combine(folder, "opaque-image.pdf"), Write(opaque));

            Assert.True(new FileInfo(Path.Combine(folder, "report.pdf")).Length > 1000);
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            var at = 0;

            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }

            return count;
        }
    }
}
