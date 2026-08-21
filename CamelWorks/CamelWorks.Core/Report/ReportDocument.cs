using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Report
{
    /// <summary>
    /// What a report is, independent of what it is rendered into.
    ///
    /// Four screens emit reports — the coordination report, Project Health, Takeoff and the
    /// viewpoint contact sheet. Building the writers first and the model afterwards is how a
    /// product ends up with four subtly different reports; building the model first is why there is
    /// one. A writer's whole job is to render this faithfully, and a writer that needs a new block
    /// type is telling you the model is missing something.
    /// </summary>
    public sealed class ReportDocument
    {
        /// <summary>Create a report.</summary>
        public ReportDocument(string title) =>
            Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("title is required", nameof(title)) : title;

        /// <summary>Shown on the cover and in the file metadata.</summary>
        public string Title { get; }

        /// <summary>Optional subtitle — typically the project or the scope.</summary>
        public string? Subtitle { get; set; }

        /// <summary>
        /// Cover facts, printed as a table. Ordered, because a cover that reorders itself between
        /// two revisions of the same report is one nobody can compare.
        /// </summary>
        public IList<KeyValuePair<string, string?>> CoverFacts { get; } = new List<KeyValuePair<string, string?>>();

        /// <summary>The body.</summary>
        public IList<ReportBlock> Blocks { get; } = new List<ReportBlock>();

        /// <summary>Add a cover fact.</summary>
        public ReportDocument Fact(string label, string? value)
        {
            CoverFacts.Add(new KeyValuePair<string, string?>(label, value));
            return this;
        }

        /// <summary>Append a block.</summary>
        public ReportDocument Add(ReportBlock block)
        {
            Blocks.Add(block ?? throw new ArgumentNullException(nameof(block)));
            return this;
        }

        /// <summary>Append a heading.</summary>
        public ReportDocument Heading(string text, int level = 1) => Add(new HeadingBlock(text, level));

        /// <summary>Append a paragraph.</summary>
        public ReportDocument Paragraph(string text) => Add(new ParagraphBlock(text));

        /// <summary>Append a page break.</summary>
        public ReportDocument PageBreak() => Add(new PageBreakBlock());
    }

    /// <summary>One piece of a report.</summary>
    public abstract class ReportBlock
    {
        internal ReportBlock() { }
    }

    /// <summary>A heading.</summary>
    public sealed class HeadingBlock : ReportBlock
    {
        /// <summary>Create a heading.</summary>
        public HeadingBlock(string text, int level = 1)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Level = level < 1 ? 1 : level > 4 ? 4 : level;
        }

        /// <summary>The text.</summary>
        public string Text { get; }

        /// <summary>1 to 4.</summary>
        public int Level { get; }
    }

    /// <summary>A paragraph of prose.</summary>
    public sealed class ParagraphBlock : ReportBlock
    {
        /// <summary>Create a paragraph.</summary>
        public ParagraphBlock(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

        /// <summary>The text.</summary>
        public string Text { get; }
    }

    /// <summary>Forces the next block onto a new page in paginated output; ignored elsewhere.</summary>
    public sealed class PageBreakBlock : ReportBlock
    {
    }

    /// <summary>A table.</summary>
    public sealed class TableBlock : ReportBlock
    {
        /// <summary>Create a table.</summary>
        public TableBlock(params string[] headers)
        {
            if (headers == null || headers.Length == 0)
                throw new ArgumentException("a table needs at least one column", nameof(headers));
            Headers = headers;
        }

        /// <summary>Column headers.</summary>
        public IReadOnlyList<string> Headers { get; }

        /// <summary>Rows. A cell may be null, which renders as empty rather than as "null".</summary>
        public IList<IReadOnlyList<string?>> Rows { get; } = new List<IReadOnlyList<string?>>();

        /// <summary>
        /// Add a row. A row with the wrong number of cells is refused rather than padded: a report
        /// whose columns silently shift is worse than one that fails to build.
        /// </summary>
        public TableBlock Row(params string?[] cells)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (cells.Length != Headers.Count)
                throw new ArgumentException(
                    "row has " + cells.Length.ToString(CultureInfo.InvariantCulture) + " cells but the table has "
                    + Headers.Count.ToString(CultureInfo.InvariantCulture) + " columns", nameof(cells));

            Rows.Add(cells);
            return this;
        }
    }

    /// <summary>An image, with numbered callouts drawn over it.</summary>
    public sealed class ImageBlock : ReportBlock
    {
        /// <summary>Create an image block.</summary>
        /// <param name="pngData">PNG bytes, or null when the render failed.</param>
        /// <param name="caption">Caption printed beneath.</param>
        public ImageBlock(byte[]? pngData, string? caption = null)
        {
            PngData = pngData;
            Caption = caption;
        }

        /// <summary>PNG bytes, or null.</summary>
        public byte[]? PngData { get; }

        /// <summary>Caption printed beneath the image.</summary>
        public string? Caption { get; }

        /// <summary>
        /// Numbered callouts, projected at render time from each result's position through the
        /// image camera.
        ///
        /// They are computed rather than authored, which is the whole reason they replaced a
        /// freehand annotation editor: there is nothing to place, nothing to re-anchor when the
        /// view changes, and nothing that can go stale. They are correct at every image size and
        /// on every regeneration.
        /// </summary>
        public IList<Callout> Callouts { get; } = new List<Callout>();

        /// <summary>
        /// True when the render failed. A report says so in place of the image rather than leaving
        /// a gap, because a silently missing image reads as "there was nothing to show".
        /// </summary>
        public bool IsMissing => PngData == null || PngData.Length == 0;
    }

    /// <summary>One numbered marker over an image, in normalised image coordinates.</summary>
    public readonly struct Callout
    {
        /// <summary>Create a callout.</summary>
        /// <param name="number">Matches the row number in the table beneath the image.</param>
        /// <param name="x">0 at the left edge, 1 at the right.</param>
        /// <param name="y">0 at the top edge, 1 at the bottom.</param>
        public Callout(int number, double x, double y)
        {
            if (number < 1) throw new ArgumentOutOfRangeException(nameof(number), "callouts are numbered from 1");
            Number = number;
            // Normalised coordinates are clamped rather than rejected: a clash centre can project
            // just outside the frame, and dropping the marker would silently lose a row's location.
            X = Clamp(x);
            Y = Clamp(y);
        }

        /// <summary>The number shown in the marker.</summary>
        public int Number { get; }

        /// <summary>Normalised horizontal position, 0 to 1.</summary>
        public double X { get; }

        /// <summary>Normalised vertical position, 0 to 1.</summary>
        public double Y { get; }

        private static double Clamp(double v) => double.IsNaN(v) ? 0.5 : v < 0 ? 0 : v > 1 ? 1 : v;

        /// <inheritdoc />
        public override string ToString() =>
            Number.ToString(CultureInfo.InvariantCulture) + "@"
            + X.ToString("0.###", CultureInfo.InvariantCulture) + ","
            + Y.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A colour legend, printed beside a coloured view.
    ///
    /// Drawn as vector text and swatches rather than baked into the image. That is not an
    /// aesthetic choice: the host offers no way to draw an overlay into a saved viewpoint, so the
    /// legend has to be the report's job — and keeping it vector is what lets the whole report
    /// engine stay free of raster compositing and therefore Linux-testable.
    /// </summary>
    public sealed class LegendBlock : ReportBlock
    {
        /// <summary>Create a legend.</summary>
        public LegendBlock(string? title = null) => Title = title;

        /// <summary>Optional heading above the swatches.</summary>
        public string? Title { get; }

        /// <summary>Entries, in the order they should be printed.</summary>
        public IList<LegendEntry> Entries { get; } = new List<LegendEntry>();

        /// <summary>Add an entry.</summary>
        public LegendBlock Entry(string label, string colourHex, int? count = null)
        {
            Entries.Add(new LegendEntry(label, colourHex, count));
            return this;
        }
    }

    /// <summary>One row of a legend.</summary>
    public readonly struct LegendEntry
    {
        /// <summary>Create an entry.</summary>
        public LegendEntry(string label, string colourHex, int? count = null)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            ColourHex = colourHex ?? "#000000";
            Count = count;
        }

        /// <summary>What the colour means.</summary>
        public string Label { get; }

        /// <summary>The swatch colour, as <c>#rrggbb</c>.</summary>
        public string ColourHex { get; }

        /// <summary>How many elements carry it, when known.</summary>
        public int? Count { get; }
    }

    /// <summary>
    /// A block that says what the report could NOT show.
    ///
    /// It has its own type so it cannot be forgotten. A report that silently omits what it could
    /// not render — a failed image, a group whose viewpoint no longer resolves, suppressed results,
    /// redlines CamelWorks cannot see — is one a coordinator hands to a client while quietly
    /// understating the problem. Every writer renders this; none may skip it.
    /// </summary>
    public sealed class DisclosureBlock : ReportBlock
    {
        /// <summary>Create a disclosure block.</summary>
        public DisclosureBlock(string heading) =>
            Heading = string.IsNullOrWhiteSpace(heading) ? "Not shown in this report" : heading;

        /// <summary>The heading.</summary>
        public string Heading { get; }

        /// <summary>One line per thing not shown.</summary>
        public IList<string> Lines { get; } = new List<string>();

        /// <summary>Add a line.</summary>
        public DisclosureBlock Line(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) Lines.Add(text);
            return this;
        }

        /// <summary>True when there is nothing to disclose, so a writer can omit the heading.</summary>
        public bool IsEmpty => Lines.Count == 0;
    }
}
