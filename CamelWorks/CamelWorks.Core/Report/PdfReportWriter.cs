using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CamelWorks.Core.Report
{
    /// <summary>
    /// Writes a <see cref="ReportDocument"/> as a PDF, by hand.
    ///
    /// By hand because every PDF library that would do this is either a dependency CamelWorks
    /// cannot take on its licence terms or a large one to carry for the small part of it used
    /// here. The format is old, stable and well specified; what is written below is the subset a
    /// coordination report actually needs — text, rules, tables, and images.
    ///
    /// Only the two standard Helvetica faces are used, and nothing is embedded. Every PDF reader
    /// in existence has them, the file stays small, and there is no font licence to think about
    /// before shipping a report to a client.
    /// </summary>
    public static class PdfReportWriter
    {
        /// <summary>Page width in points — A4 portrait.</summary>
        public const double PageWidth = 595.28;

        /// <summary>Page height in points — A4 portrait.</summary>
        public const double PageHeight = 841.89;

        /// <summary>Margin in points, a shade over two centimetres.</summary>
        public const double Margin = 56;

        /// <summary>Write the report.</summary>
        /// <param name="output">Where to write. Left open.</param>
        /// <param name="document">The report.</param>
        public static void Write(Stream output, ReportDocument document)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (document == null) throw new ArgumentNullException(nameof(document));

            var pdf = new Pdf();
            var layout = new Layout(pdf);

            layout.Cover(document);

            foreach (var block in document.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock heading: layout.Heading(heading); break;
                    case ParagraphBlock paragraph: layout.Paragraph(paragraph.Text); break;
                    case PageBreakBlock _: layout.NewPage(); break;
                    case TableBlock table: layout.Table(table); break;
                    case ImageBlock image: layout.Image(image); break;
                    case LegendBlock legend: layout.Legend(legend); break;
                    case DisclosureBlock disclosure: layout.Disclosure(disclosure); break;
                }
            }

            layout.Finish();
            pdf.WriteTo(output);
        }

        // -----------------------------------------------------------------------------------
        // Layout
        // -----------------------------------------------------------------------------------

        private sealed class Layout
        {
            private const double BodySize = 9.5;
            private const double Leading = 13;

            private readonly Pdf _pdf;
            private readonly StringBuilder _content = new StringBuilder();
            private readonly List<string> _images = new List<string>();
            private double _y = PageHeight - Margin;
            private int _pageNumber;
            private bool _open;

            internal Layout(Pdf pdf) => _pdf = pdf;

            private double Right => PageWidth - Margin;

            private double Usable => Right - Margin;

            internal void Cover(ReportDocument document)
            {
                Open();

                _y = PageHeight - Margin - 120;
                Text(document.Title, Margin, _y, 24, bold: true);
                _y -= 30;

                if (!string.IsNullOrWhiteSpace(document.Subtitle))
                {
                    Text(document.Subtitle!, Margin, _y, 12, bold: false, grey: 0.35);
                    _y -= 24;
                }

                Rule(_y);
                _y -= 24;

                foreach (var fact in document.CoverFacts)
                {
                    Text(fact.Key, Margin, _y, BodySize, bold: true);
                    Text(fact.Value ?? string.Empty, Margin + 150, _y, BodySize, bold: false);
                    _y -= Leading + 2;
                }

                NewPage();
            }

            internal void Heading(HeadingBlock heading)
            {
                var size = heading.Level <= 1 ? 16 : heading.Level == 2 ? 12.5 : 10.5;

                Need(size + 14);
                _y -= size * 0.6;
                Text(heading.Text, Margin, _y, size, bold: true);
                _y -= size * 0.9;

                if (heading.Level <= 1)
                {
                    Rule(_y + 4);
                    _y -= 6;
                }
            }

            internal void Paragraph(string? text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;

                foreach (var line in Wrap(text!, Usable, BodySize, bold: false))
                {
                    Need(Leading);
                    Text(line, Margin, _y, BodySize, bold: false);
                    _y -= Leading;
                }

                _y -= 6;
            }

            internal void Table(TableBlock table)
            {
                if (table.Headers.Count == 0) return;

                var widths = Columns(table);
                var headerHeight = RowHeight(table.Headers.Select(h => (string?)h).ToList(), widths, bold: true);

                Need(headerHeight + Leading);
                HeaderRow(table, widths, headerHeight);

                foreach (var row in table.Rows)
                {
                    var height = RowHeight(row, widths, bold: false);

                    if (_y - height < Margin)
                    {
                        // A table split across a page must repeat its headers. A page of unlabelled
                        // columns is not something a reader can use, and it is the single most
                        // common way a generated report becomes unreadable.
                        NewPage();
                        HeaderRow(table, widths, headerHeight);
                    }

                    Row(row, widths, height, bold: false);
                    Rule(_y + 2, 0.85);
                }

                _y -= 8;
            }

            internal void Image(ImageBlock block)
            {
                if (!PngImage.TryRead(block.PngData, out var image) || image == null)
                {
                    // Named rather than skipped. A report with an obviously missing picture is
                    // fixable; one that quietly lost it is not even noticed.
                    Need(Leading * 2);
                    Text("[image could not be embedded]", Margin, _y, BodySize, bold: false, grey: 0.5);
                    _y -= Leading;
                    if (!string.IsNullOrWhiteSpace(block.Caption)) Paragraph(block.Caption);
                    return;
                }

                var scale = Math.Min(1, Usable / image.Width);
                var width = image.Width * scale;
                var height = image.Height * scale;

                if (height > PageHeight - (2 * Margin))
                {
                    scale = (PageHeight - (2 * Margin)) / image.Height;
                    width = image.Width * scale;
                    height = image.Height * scale;
                }

                Need(height + Leading);

                var name = _pdf.AddImage(image);
                if (!_images.Contains(name)) _images.Add(name);

                _y -= height;
                _content.Append("q ").Append(N(width)).Append(" 0 0 ").Append(N(height)).Append(' ')
                        .Append(N(Margin)).Append(' ').Append(N(_y)).Append(" cm /").Append(name)
                        .Append(" Do Q\n");

                foreach (var callout in block.Callouts)
                {
                    // Callout coordinates are fractions of the image, so they survive the scaling
                    // above. Anything else would put the marker somewhere else on every page size.
                    var cx = Margin + (callout.X * width);
                    var cy = _y + height - (callout.Y * height);

                    Circle(cx, cy, 8, 0.86, 0.15, 0.15);
                    var label = callout.Number.ToString(CultureInfo.InvariantCulture);
                    Text(label, cx - (Width(label, 8, true) / 2), cy - 2.8, 8, bold: true, grey: 1);
                }

                _y -= 6;
                if (!string.IsNullOrWhiteSpace(block.Caption))
                {
                    Text(block.Caption!, Margin, _y, 8.5, bold: false, grey: 0.4);
                    _y -= Leading;
                }

                _y -= 6;
            }

            internal void Legend(LegendBlock legend)
            {
                if (!string.IsNullOrWhiteSpace(legend.Title))
                {
                    Need(Leading);
                    Text(legend.Title!, Margin, _y, 10, bold: true);
                    _y -= Leading + 2;
                }

                foreach (var entry in legend.Entries)
                {
                    Need(Leading);

                    var colour = Colour(entry.ColourHex);
                    _content.Append(N(colour.R)).Append(' ').Append(N(colour.G)).Append(' ').Append(N(colour.B))
                            .Append(" rg ").Append(N(Margin)).Append(' ').Append(N(_y - 1))
                            .Append(" 9 9 re f\n");

                    var text = entry.Label;
                    if (entry.Count != null)
                        text += "  (" + entry.Count.Value.ToString("N0", CultureInfo.InvariantCulture) + ")";

                    Text(text, Margin + 15, _y, BodySize, bold: false);
                    _y -= Leading;
                }

                _y -= 6;
            }

            internal void Disclosure(DisclosureBlock disclosure)
            {
                Need(Leading * 2);
                Text(disclosure.Heading, Margin, _y, 10, bold: true);
                _y -= Leading + 2;

                foreach (var line in disclosure.Lines)
                    foreach (var wrapped in Wrap(line, Usable - 12, 8.5, bold: false))
                    {
                        Need(Leading);
                        Text(wrapped, Margin + 12, _y, 8.5, bold: false, grey: 0.3);
                        _y -= 11.5;
                    }

                _y -= 8;
            }

            internal void NewPage()
            {
                Close();
                Open();
                _y = PageHeight - Margin;
            }

            internal void Finish() => Close();

            // ---------------------------------------------------------------------------

            private void Open()
            {
                if (_open) return;

                _content.Clear();
                _images.Clear();
                _pageNumber++;
                _open = true;
            }

            private void Close()
            {
                if (!_open) return;

                // The footer is written last, so the page number is right even though the layout
                // never knew in advance how many pages a table would take.
                var footer = "Page " + _pageNumber.ToString(CultureInfo.InvariantCulture);
                Text(footer, Right - Width(footer, 8, false), Margin - 22, 8, bold: false, grey: 0.5);

                _pdf.AddPage(_content.ToString(), _images);
                _open = false;
            }

            private void Need(double height)
            {
                if (_y - height >= Margin) return;
                NewPage();
            }

            private void Text(string text, double x, double y, double size, bool bold, double grey = 0)
            {
                if (string.IsNullOrEmpty(text)) return;

                _content.Append(N(grey)).Append(' ').Append(N(grey)).Append(' ').Append(N(grey)).Append(" rg\n");
                _content.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(N(size)).Append(" Tf ")
                        .Append(N(x)).Append(' ').Append(N(y)).Append(" Td (")
                        .Append(Escape(text)).Append(") Tj ET\n");
            }

            private void Rule(double y, double grey = 0.75)
            {
                _content.Append(N(grey)).Append(' ').Append(N(grey)).Append(' ').Append(N(grey)).Append(" RG 0.6 w ")
                        .Append(N(Margin)).Append(' ').Append(N(y)).Append(" m ")
                        .Append(N(Right)).Append(' ').Append(N(y)).Append(" l S\n");
            }

            private void Circle(double x, double y, double r, double red, double green, double blue)
            {
                // Four Béziers. The magic number is the standard circular approximation constant;
                // a polygon would show its corners at the sizes these markers are drawn at.
                var k = r * 0.5523;

                _content.Append(N(red)).Append(' ').Append(N(green)).Append(' ').Append(N(blue)).Append(" rg\n");
                _content.Append(N(x + r)).Append(' ').Append(N(y)).Append(" m ");
                _content.Append(N(x + r)).Append(' ').Append(N(y + k)).Append(' ').Append(N(x + k)).Append(' ').Append(N(y + r)).Append(' ').Append(N(x)).Append(' ').Append(N(y + r)).Append(" c ");
                _content.Append(N(x - k)).Append(' ').Append(N(y + r)).Append(' ').Append(N(x - r)).Append(' ').Append(N(y + k)).Append(' ').Append(N(x - r)).Append(' ').Append(N(y)).Append(" c ");
                _content.Append(N(x - r)).Append(' ').Append(N(y - k)).Append(' ').Append(N(x - k)).Append(' ').Append(N(y - r)).Append(' ').Append(N(x)).Append(' ').Append(N(y - r)).Append(" c ");
                _content.Append(N(x + k)).Append(' ').Append(N(y - r)).Append(' ').Append(N(x + r)).Append(' ').Append(N(y - k)).Append(' ').Append(N(x + r)).Append(' ').Append(N(y)).Append(" c f\n");
            }

            private double[] Columns(TableBlock table)
            {
                var count = table.Headers.Count;
                var weights = new double[count];

                // Column widths follow the widest content, capped, so one long description column
                // does not squeeze five short ones into unreadable slivers.
                for (var i = 0; i < count; i++)
                {
                    weights[i] = Width(table.Headers[i], BodySize, true);

                    foreach (var row in table.Rows)
                        if (i < row.Count)
                            weights[i] = Math.Max(weights[i], Math.Min(220, Width(row[i] ?? string.Empty, BodySize, false)));
                }

                var total = weights.Sum();
                if (total <= 0) return Enumerable.Repeat(Usable / count, count).ToArray();

                return weights.Select(w => Usable * w / total).ToArray();
            }

            private double RowHeight(IReadOnlyList<string?> cells, double[] widths, bool bold)
            {
                var lines = 1;

                for (var i = 0; i < widths.Length && i < cells.Count; i++)
                    lines = Math.Max(lines, Wrap(cells[i] ?? string.Empty, widths[i] - 8, BodySize, bold).Count);

                return (lines * 11.5) + 5;
            }

            private void HeaderRow(TableBlock table, double[] widths, double height)
            {
                _content.Append("0.93 0.93 0.93 rg ").Append(N(Margin)).Append(' ').Append(N(_y - height + 4))
                        .Append(' ').Append(N(Usable)).Append(' ').Append(N(height)).Append(" re f\n");

                Row(table.Headers.Select(h => (string?)h).ToList(), widths, height, bold: true);
                Rule(_y + 2, 0.55);
            }

            private void Row(IReadOnlyList<string?> cells, double[] widths, double height, bool bold)
            {
                var top = _y;
                var x = Margin;

                for (var i = 0; i < widths.Length; i++)
                {
                    var text = i < cells.Count ? cells[i] ?? string.Empty : string.Empty;
                    var y = top - 8;

                    foreach (var line in Wrap(text, widths[i] - 8, BodySize, bold))
                    {
                        Text(line, x + 4, y, BodySize, bold);
                        y -= 11.5;
                    }

                    x += widths[i];
                }

                _y = top - height;
            }

            private static string N(double value) =>
                Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

            private static (double R, double G, double B) Colour(string? hex)
            {
                if (Abstractions.Colour.TryParse(hex, out var colour))
                    return (colour.R / 255.0, colour.G / 255.0, colour.B / 255.0);

                return (0.5, 0.5, 0.5);
            }

            private static List<string> Wrap(string text, double width, double size, bool bold)
            {
                var lines = new List<string>();
                if (width <= 0) { lines.Add(text); return lines; }

                foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
                {
                    var line = new StringBuilder();

                    foreach (var word in paragraph.Split(' '))
                    {
                        var candidate = line.Length == 0 ? word : line + " " + word;

                        if (Width(candidate, size, bold) <= width || line.Length == 0)
                        {
                            if (line.Length > 0) line.Append(' ');
                            line.Append(word);
                            continue;
                        }

                        lines.Add(line.ToString());
                        line.Clear();
                        line.Append(word);
                    }

                    lines.Add(line.ToString());
                }

                return lines;
            }

            private static double Width(string text, double size, bool bold)
            {
                double total = 0;
                var widths = bold ? Metrics.Bold : Metrics.Regular;

                foreach (var c in text)
                {
                    var code = WinAnsi.Byte(c);
                    total += code >= 32 && code < 127 ? widths[code - 32] : 556;
                }

                return total * size / 1000.0;
            }

            private static string Escape(string text)
            {
                var sb = new StringBuilder(text.Length + 8);

                foreach (var c in text)
                {
                    var b = WinAnsi.Byte(c);

                    if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\') sb.Append('\\');
                    sb.Append((char)b);
                }

                return sb.ToString();
            }
        }

        // -----------------------------------------------------------------------------------
        // The file
        // -----------------------------------------------------------------------------------

        private sealed class Pdf
        {
            private readonly List<byte[]> _objects = new List<byte[]>();
            private readonly List<int> _pages = new List<int>();
            private readonly List<(string Name, int Id)> _images = new List<(string, int)>();
            private readonly List<List<string>> _pageImages = new List<List<string>>();

            internal string AddImage(PngImage image)
            {
                var name = "Im" + (_images.Count + 1).ToString(CultureInfo.InvariantCulture);

                var dictionary = new StringBuilder();
                dictionary.Append("/Type /XObject /Subtype /Image /Width ")
                          .Append(image.Width.ToString(CultureInfo.InvariantCulture))
                          .Append(" /Height ").Append(image.Height.ToString(CultureInfo.InvariantCulture))
                          .Append(" /BitsPerComponent 8 /ColorSpace ");

                if (image.ColourSpace == "Indexed" && image.Palette != null)
                {
                    dictionary.Append("[/Indexed /DeviceRGB ")
                              .Append(((image.Palette.Length / 3) - 1).ToString(CultureInfo.InvariantCulture))
                              .Append(" <").Append(Hex(image.Palette)).Append(">]");
                }
                else
                {
                    dictionary.Append('/').Append(image.ColourSpace);
                }

                dictionary.Append(" /Filter /FlateDecode");

                // Only a passed-through stream still carries PNG's row predictors. Declaring them
                // on a re-compressed one would have the reader undo filters that are not there.
                if (image.PassedThrough)
                    dictionary.Append(" /DecodeParms << /Predictor 15 /Colors ")
                              .Append(image.Colours.ToString(CultureInfo.InvariantCulture))
                              .Append(" /BitsPerComponent 8 /Columns ")
                              .Append(image.Width.ToString(CultureInfo.InvariantCulture)).Append(" >>");

                if (image.Alpha != null)
                {
                    var mask = "/Type /XObject /Subtype /Image /Width "
                             + image.Width.ToString(CultureInfo.InvariantCulture)
                             + " /Height " + image.Height.ToString(CultureInfo.InvariantCulture)
                             + " /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode";

                    dictionary.Append(" /SMask ").Append(Add(mask, image.Alpha)).Append(" 0 R");
                }

                var id = Add(dictionary.ToString(), image.Data);
                _images.Add((name, id));
                return name;
            }

            internal void AddPage(string content, List<string> images)
            {
                _pages.Add(Add(string.Empty, Latin1(content)));
                _pageImages.Add(new List<string>(images));
            }

            internal void WriteTo(Stream output)
            {
                // Objects 1 and 2 are the catalogue and the page tree, and both need ids that are
                // known before the pages they point at exist — so they are reserved first and
                // filled in here.
                var fontRegular = Add("/Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding", null);
                var fontBold = Add("/Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding", null);

                var pageIds = new List<int>();

                for (var i = 0; i < _pages.Count; i++)
                {
                    var resources = new StringBuilder();
                    resources.Append("<< /Font << /F1 ").Append(fontRegular).Append(" 0 R /F2 ")
                             .Append(fontBold).Append(" 0 R >>");

                    var used = _pageImages[i];
                    if (used.Count > 0)
                    {
                        resources.Append(" /XObject <<");
                        foreach (var name in used)
                        {
                            var image = _images.FirstOrDefault(x => x.Name == name);
                            if (image.Name != null)
                                resources.Append(" /").Append(name).Append(' ').Append(image.Id).Append(" 0 R");
                        }

                        resources.Append(" >>");
                    }

                    resources.Append(" >>");

                    pageIds.Add(Add("/Type /Page /Parent PAGES 0 R /MediaBox [0 0 "
                                    + Round(PageWidth) + " " + Round(PageHeight) + "] /Resources "
                                    + resources + " /Contents " + _pages[i] + " 0 R", null));
                }

                var tree = Add("/Type /Pages /Kids [" + string.Join(" ", pageIds.Select(id => id + " 0 R"))
                               + "] /Count " + pageIds.Count.ToString(CultureInfo.InvariantCulture), null);

                var catalogue = Add("/Type /Catalog /Pages " + tree + " 0 R", null);

                // The page objects were written before the tree existed, so they carry a
                // placeholder for its id. Patched here rather than by a second pass over the
                // whole file.
                foreach (var id in pageIds)
                {
                    var text = new StringBuilder(_objects[id - 1].Length);
                    foreach (var b in _objects[id - 1]) text.Append((char)b);

                    _objects[id - 1] = Latin1(text.ToString()
                        .Replace("PAGES", tree.ToString(CultureInfo.InvariantCulture)));
                }

                Emit(output, catalogue);
            }

            /// <summary>
            /// Add one object.
            /// </summary>
            /// <param name="entries">
            /// The dictionary's CONTENTS, without the enclosing braces. Taking it that way is not
            /// a style choice: an image dictionary contains a nested DecodeParms dictionary, and
            /// splicing /Length in before the last ">>" of the whole thing closes the object at
            /// the wrong brace and truncates it.
            /// </param>
            /// <param name="stream">The stream, when the object has one.</param>
            private int Add(string entries, byte[]? stream)
            {
                var id = _objects.Count + 1;

                using (var buffer = new MemoryStream())
                {
                    var head = "<< " + entries;
                    if (stream != null) head += " /Length " + stream.Length.ToString(CultureInfo.InvariantCulture);
                    head += " >>";

                    Write(buffer, id.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + head + "\n");

                    if (stream != null)
                    {
                        Write(buffer, "stream\n");
                        buffer.Write(stream, 0, stream.Length);
                        Write(buffer, "\nendstream\n");
                    }

                    Write(buffer, "endobj\n");
                    _objects.Add(buffer.ToArray());
                }

                return id;
            }

            private void Emit(Stream output, int catalogue)
            {
                var offsets = new List<int>();
                var position = 0;

                position += Write(output, "%PDF-1.4\n");

                // A comment of high bytes, which is what tells a transfer tool the file is binary.
                // Without it a well-meaning FTP client will translate the line endings inside an
                // image stream and quietly destroy the file.
                position += Write(output, "%âãÏÓ\n");

                foreach (var obj in _objects)
                {
                    offsets.Add(position);
                    output.Write(obj, 0, obj.Length);
                    position += obj.Length;
                }

                var xref = position;
                Write(output, "xref\n0 " + (_objects.Count + 1).ToString(CultureInfo.InvariantCulture) + "\n");
                Write(output, "0000000000 65535 f \n");

                foreach (var offset in offsets)
                    Write(output, offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

                Write(output, "trailer\n<< /Size " + (_objects.Count + 1).ToString(CultureInfo.InvariantCulture)
                              + " /Root " + catalogue.ToString(CultureInfo.InvariantCulture) + " 0 R >>\n");
                Write(output, "startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
            }

            private static int Write(Stream output, string text)
            {
                var bytes = Latin1(text);
                output.Write(bytes, 0, bytes.Length);
                return bytes.Length;
            }

            // One char, one byte. Encoding.ASCII would replace every WinAnsi byte above 127 with a
            // question mark — precisely the punctuation the escape step took care to keep.
            private static byte[] Latin1(string text)
            {
                var bytes = new byte[text.Length];
                for (var i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
                return bytes;
            }

            private static string Round(double value) =>
                Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

            private static string Hex(byte[] bytes)
            {
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Unicode to the single-byte encoding the standard fonts use.
        ///
        /// WinAnsi is Latin-1 with a different 0x80-0x9F block, which is where the punctuation the
        /// product actually emits lives — the en dash, the curly quotes, the bullet. Those are
        /// mapped; the handful of symbols with no WinAnsi equivalent are transliterated rather than
        /// dropped, because a report reading "214 groups -> 12 assigned" is fine and one reading
        /// "214 groups ? 12 assigned" looks broken.
        /// </summary>
        private static class WinAnsi
        {
            internal static byte Byte(char c)
            {
                if (c >= 0x20 && c <= 0x7E) return (byte)c;
                if (c >= 0xA0 && c <= 0xFF) return (byte)c;

                switch (c)
                {
                    case '€': return 0x80;   // euro
                    case '‚': return 0x82;
                    case 'ƒ': return 0x83;
                    case '„': return 0x84;
                    case '…': return 0x85;   // ellipsis
                    case '†': return 0x86;
                    case '‡': return 0x87;
                    case 'ˆ': return 0x88;
                    case '‰': return 0x89;
                    case '‹': return 0x8B;
                    case '‘': return 0x91;
                    case '’': return 0x92;
                    case '“': return 0x93;
                    case '”': return 0x94;
                    case '•': return 0x95;   // bullet
                    case '–': return 0x96;   // en dash
                    case '—': return 0x97;   // em dash
                    case '›': return 0x9B;
                    case '→': return (byte)'>';    // arrow: no WinAnsi equivalent
                    case '·': return 0xB7;         // middle dot
                    case '×': return 0xD7;         // multiplication sign
                    case '\t': return (byte)' ';
                    default: return (byte)'?';
                }
            }
        }

        /// <summary>
        /// Helvetica character widths, in thousandths of the font size, for the printable ASCII
        /// range. Without them every wrap would be a guess, and a guess that runs long puts text
        /// through the margin on somebody's client deliverable.
        /// </summary>
        private static class Metrics
        {
            internal static readonly short[] Regular =
            {
                278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
                556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
                1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
                667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
                333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
                556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
            };

            internal static readonly short[] Bold =
            {
                278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
                556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
                975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
                667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
                333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
                611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
            };
        }
    }
}
