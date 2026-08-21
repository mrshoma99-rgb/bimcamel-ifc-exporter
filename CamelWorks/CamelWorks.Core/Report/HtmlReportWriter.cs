using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CamelWorks.Core.Report
{
    /// <summary>
    /// Renders a <see cref="ReportDocument"/> as one self-contained HTML file.
    ///
    /// Self-contained matters more than it looks: this file gets emailed. Images are embedded as
    /// data URIs and the CSS is inline, so it survives being forwarded, opened from a download
    /// folder, or read on a phone on site — none of which a folder of sibling assets survives.
    ///
    /// Callouts and the legend are drawn as inline SVG over the image rather than composited into
    /// it. That keeps the whole engine free of raster work, which is what lets it stay
    /// netstandard2.0 and be verified on the Linux CI job by comparing output text.
    /// </summary>
    public static class HtmlReportWriter
    {
        /// <summary>Render to HTML.</summary>
        public static string Write(ReportDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            var sb = new StringBuilder();
            sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n<title>");
            Escape(sb, document.Title);
            sb.Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n");

            WriteCover(sb, document);
            foreach (var block in document.Blocks) WriteBlock(sb, block);

            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        private static void WriteCover(StringBuilder sb, ReportDocument d)
        {
            sb.Append("<header class=\"cover\">\n<h1>");
            Escape(sb, d.Title);
            sb.Append("</h1>\n");

            if (!string.IsNullOrWhiteSpace(d.Subtitle))
            {
                sb.Append("<p class=\"subtitle\">");
                Escape(sb, d.Subtitle!);
                sb.Append("</p>\n");
            }

            if (d.CoverFacts.Count > 0)
            {
                sb.Append("<table class=\"facts\">\n");
                foreach (var fact in d.CoverFacts)
                {
                    sb.Append("<tr><th>");
                    Escape(sb, fact.Key);
                    sb.Append("</th><td>");
                    Escape(sb, fact.Value ?? string.Empty);
                    sb.Append("</td></tr>\n");
                }
                sb.Append("</table>\n");
            }

            sb.Append("</header>\n");
        }

        private static void WriteBlock(StringBuilder sb, ReportBlock block)
        {
            switch (block)
            {
                case HeadingBlock h:
                    var tag = "h" + (h.Level + 1).ToString(CultureInfo.InvariantCulture);  // h1 is the title
                    sb.Append('<').Append(tag).Append('>');
                    Escape(sb, h.Text);
                    sb.Append("</").Append(tag).Append(">\n");
                    break;

                case ParagraphBlock p:
                    sb.Append("<p>");
                    Escape(sb, p.Text);
                    sb.Append("</p>\n");
                    break;

                case PageBreakBlock _:
                    sb.Append("<div class=\"pagebreak\"></div>\n");
                    break;

                case TableBlock t:
                    WriteTable(sb, t);
                    break;

                case ImageBlock i:
                    WriteImage(sb, i);
                    break;

                case LegendBlock l:
                    WriteLegend(sb, l);
                    break;

                case DisclosureBlock d:
                    WriteDisclosure(sb, d);
                    break;

                default:
                    throw new NotSupportedException("no HTML rendering for " + block.GetType().Name);
            }
        }

        private static void WriteTable(StringBuilder sb, TableBlock t)
        {
            sb.Append("<table class=\"data\">\n<thead><tr>");
            foreach (var h in t.Headers)
            {
                sb.Append("<th>");
                Escape(sb, h);
                sb.Append("</th>");
            }
            sb.Append("</tr></thead>\n<tbody>\n");

            foreach (var row in t.Rows)
            {
                sb.Append("<tr>");
                foreach (var cell in row)
                {
                    sb.Append("<td>");
                    Escape(sb, cell ?? string.Empty);   // null renders empty, never "null"
                    sb.Append("</td>");
                }
                sb.Append("</tr>\n");
            }

            sb.Append("</tbody>\n</table>\n");
        }

        private static void WriteImage(StringBuilder sb, ImageBlock image)
        {
            sb.Append("<figure>\n");

            if (image.IsMissing)
            {
                // Said, not skipped. A gap where an image should be reads as "there was nothing to
                // show", which is a different and much better-sounding claim than the truth.
                sb.Append("<div class=\"missing\">This view could not be rendered.</div>\n");
            }
            else
            {
                sb.Append("<div class=\"shot\">\n<img alt=\"\" src=\"data:image/png;base64,");
                sb.Append(Convert.ToBase64String(image.PngData!));
                sb.Append("\">\n");

                if (image.Callouts.Count > 0)
                {
                    sb.Append("<svg class=\"callouts\" viewBox=\"0 0 100 100\" preserveAspectRatio=\"none\">\n");
                    foreach (var c in image.Callouts)
                    {
                        var x = (c.X * 100).ToString("0.###", CultureInfo.InvariantCulture);
                        var y = (c.Y * 100).ToString("0.###", CultureInfo.InvariantCulture);
                        sb.Append("<circle cx=\"").Append(x).Append("\" cy=\"").Append(y).Append("\" r=\"2.6\"/>");
                        sb.Append("<text x=\"").Append(x).Append("\" y=\"").Append(y).Append("\">")
                          .Append(c.Number.ToString(CultureInfo.InvariantCulture)).Append("</text>\n");
                    }
                    sb.Append("</svg>\n");
                }

                sb.Append("</div>\n");
            }

            if (!string.IsNullOrWhiteSpace(image.Caption))
            {
                sb.Append("<figcaption>");
                Escape(sb, image.Caption!);
                sb.Append("</figcaption>\n");
            }

            sb.Append("</figure>\n");
        }

        private static void WriteLegend(StringBuilder sb, LegendBlock legend)
        {
            sb.Append("<div class=\"legend\">\n");

            if (!string.IsNullOrWhiteSpace(legend.Title))
            {
                sb.Append("<h4>");
                Escape(sb, legend.Title!);
                sb.Append("</h4>\n");
            }

            foreach (var e in legend.Entries)
            {
                sb.Append("<div class=\"key\"><span class=\"swatch\" style=\"background:");
                Escape(sb, SafeColour(e.ColourHex));
                sb.Append("\"></span>");
                Escape(sb, e.Label);
                if (e.Count.HasValue)
                    sb.Append(" <span class=\"count\">(")
                      .Append(e.Count.Value.ToString(CultureInfo.InvariantCulture)).Append(")</span>");
                sb.Append("</div>\n");
            }

            sb.Append("</div>\n");
        }

        private static void WriteDisclosure(StringBuilder sb, DisclosureBlock d)
        {
            if (d.IsEmpty) return;

            sb.Append("<section class=\"disclosure\">\n<h3>");
            Escape(sb, d.Heading);
            sb.Append("</h3>\n<ul>\n");
            foreach (var line in d.Lines)
            {
                sb.Append("<li>");
                Escape(sb, line);
                sb.Append("</li>\n");
            }
            sb.Append("</ul>\n</section>\n");
        }

        /// <summary>
        /// Only <c>#rrggbb</c> reaches a style attribute. A colour arriving from a project file is
        /// data, and data does not get to close a quote and start writing CSS.
        /// </summary>
        private static string SafeColour(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "#000000";
            var s = hex!.Trim();
            if (s.Length != 7 || s[0] != '#') return "#000000";

            for (var i = 1; i < 7; i++)
            {
                var c = char.ToLowerInvariant(s[i]);
                var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok) return "#000000";
            }

            return s;
        }

        private static void Escape(StringBuilder sb, string text)
        {
            foreach (var c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
        }

        private const string Css =
            "body{font:13px/1.5 -apple-system,Segoe UI,Roboto,sans-serif;margin:0;padding:24px;color:#1a1a1a}" +
            "h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:22px 0 8px;border-bottom:1px solid #ddd;padding-bottom:4px}" +
            "h3{font-size:14px;margin:16px 0 6px}h4{font-size:13px;margin:10px 0 4px}" +
            ".subtitle{color:#666;margin:0 0 12px}" +
            "table{border-collapse:collapse;width:100%;margin:8px 0}" +
            "table.facts{width:auto}table.facts th{text-align:left;padding-right:16px;color:#666;font-weight:400}" +
            "table.data th{background:#f4f4f4;text-align:left}" +
            "table.data th,table.data td{border:1px solid #ddd;padding:4px 8px;vertical-align:top}" +
            "figure{margin:12px 0}figcaption{color:#666;margin-top:4px}" +
            ".shot{position:relative;display:inline-block;max-width:100%}" +
            ".shot img{max-width:100%;height:auto;display:block}" +
            ".callouts{position:absolute;inset:0;width:100%;height:100%}" +
            ".callouts circle{fill:#c00;stroke:#fff;stroke-width:.5}" +
            ".callouts text{fill:#fff;font-size:3px;text-anchor:middle;dominant-baseline:central}" +
            ".missing{border:1px dashed #bbb;color:#888;padding:24px;text-align:center}" +
            ".legend{margin:8px 0}.key{display:inline-block;margin:0 14px 4px 0}" +
            ".swatch{display:inline-block;width:11px;height:11px;margin-right:5px;border:1px solid #0003;vertical-align:-1px}" +
            ".count{color:#888}" +
            ".disclosure{margin:18px 0;padding:10px 14px;background:#fbf7e8;border-left:3px solid #d8b64a}" +
            ".pagebreak{page-break-after:always}" +
            "@media print{body{padding:0}}";
    }
}
