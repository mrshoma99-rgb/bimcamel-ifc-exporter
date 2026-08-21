using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace CamelWorks.Core.Report
{
    /// <summary>One sheet to be written.</summary>
    public sealed class XlsxSheet
    {
        /// <summary>Create a sheet.</summary>
        public XlsxSheet(string name)
        {
            Name = Sanitise(name);
            if (Name.Length == 0) throw new ArgumentException("sheet name is required", nameof(name));
        }

        /// <summary>The tab name, already sanitised.</summary>
        public string Name { get; }

        /// <summary>Rows of cells. A null cell is written as empty.</summary>
        public IList<IReadOnlyList<string?>> Rows { get; } = new List<IReadOnlyList<string?>>();

        /// <summary>True to freeze the first row, which every report sheet wants.</summary>
        public bool FreezeHeader { get; set; } = true;

        /// <summary>Append a row.</summary>
        public XlsxSheet Row(params string?[] cells)
        {
            Rows.Add(cells ?? Array.Empty<string?>());
            return this;
        }

        /// <summary>
        /// Excel refuses a sheet name over 31 characters or containing <c>: \ / ? * [ ]</c>, and
        /// refuses the whole file rather than the sheet. Sanitising here means a group name from a
        /// model can never produce a workbook that will not open.
        /// </summary>
        public static string Sanitise(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            var sb = new StringBuilder();
            foreach (var c in name!.Trim())
            {
                if (c == ':' || c == '\\' || c == '/' || c == '?' || c == '*' || c == '[' || c == ']') sb.Append('-');
                else if (c < 0x20) continue;
                else sb.Append(c);
            }

            var s = sb.ToString().Trim('\'');
            return s.Length > 31 ? s.Substring(0, 31) : s;
        }
    }

    /// <summary>
    /// A minimal XLSX writer.
    ///
    /// Hand-rolled for the same reason as the JSON: the architecture test forbids a third-party
    /// runtime, and a free plug-in loading into four host years alongside whatever else the user
    /// has installed cannot afford an assembly-binding argument with somebody else's copy of a
    /// spreadsheet library. This is also what keeps EPPlus — Polyform Noncommercial, and therefore
    /// unusable in a product advertised as free for commercial use — out of the bundle.
    ///
    /// Deliberately small: strings and numbers, one header row, a frozen pane, auto-width. No
    /// formulas, no styling beyond bold headers, no charts. Everything a report needs and nothing
    /// that would grow a format surface nobody asked for.
    ///
    /// <b>Every value is written as an inline string except recognised numbers.</b> Excel's
    /// helpfulness is the enemy here: left to guess, it turns "L03-1" into a date and a leading
    /// zero into nothing. A takeoff quantity must survive the round trip exactly.
    /// </summary>
    public static class XlsxWriter
    {
        /// <summary>Write a workbook to bytes.</summary>
        public static byte[] Write(IReadOnlyList<XlsxSheet> sheets)
        {
            if (sheets == null) throw new ArgumentNullException(nameof(sheets));
            if (sheets.Count == 0) throw new ArgumentException("a workbook needs at least one sheet", nameof(sheets));

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sheets)
            {
                if (!names.Add(s.Name))
                    throw new ArgumentException("two sheets are named '" + s.Name + "'; Excel refuses the file", nameof(sheets));
            }

            using var buffer = new MemoryStream();

            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
                Add(zip, "_rels/.rels", RootRels);
                Add(zip, "xl/workbook.xml", Workbook(sheets));
                Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
                Add(zip, "xl/styles.xml", Styles);

                for (var i = 0; i < sheets.Count; i++)
                    Add(zip, "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml", Sheet(sheets[i]));
            }

            return buffer.ToArray();
        }

        /// <summary>Write a single-sheet workbook from a report table.</summary>
        public static byte[] Write(string sheetName, TableBlock table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));

            var sheet = new XlsxSheet(sheetName);
            sheet.Row(table.Headers.Select(h => (string?)h).ToArray());
            foreach (var row in table.Rows) sheet.Row(row.ToArray());

            return Write(new[] { sheet });
        }

        private static void Add(ZipArchive zip, string path, string content)
        {
            // No compression level games: these files are small, and Store vs Optimal has produced
            // reader quirks in the wild often enough not to be worth the bytes.
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string ContentTypes(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");

            for (var i = 1; i <= sheetCount; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i.ToString(CultureInfo.InvariantCulture))
                  .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");

            sb.Append("</Types>");
            return sb.ToString();
        }

        private const string RootRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private static string Workbook(IReadOnlyList<XlsxSheet> sheets)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");

            for (var i = 0; i < sheets.Count; i++)
            {
                sb.Append("<sheet name=\"");
                EscapeAttr(sb, sheets[i].Name);
                sb.Append("\" sheetId=\"").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" r:id=\"rId").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\"/>");
            }

            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string WorkbookRels(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

            for (var i = 1; i <= sheetCount; i++)
                sb.Append("<Relationship Id=\"rId").Append(i.ToString(CultureInfo.InvariantCulture))
                  .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet")
                  .Append(i.ToString(CultureInfo.InvariantCulture)).Append(".xml\"/>");

            // The styles part is always the last relationship id.
            sb.Append("<Relationship Id=\"rId").Append((sheetCount + 1).ToString(CultureInfo.InvariantCulture))
              .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        // Two cell formats: 0 = normal, 1 = bold. Enough for a header row and nothing more.
        private const string Styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
            "<borders count=\"1\"><border/></borders>" +
            "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf xfId=\"0\"/><xf xfId=\"0\" fontId=\"1\" applyFont=\"1\"/></cellXfs>" +
            "</styleSheet>";

        private static string Sheet(XlsxSheet sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            AppendColumnWidths(sb, sheet);

            if (sheet.FreezeHeader && sheet.Rows.Count > 1)
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\">")
                  .Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>")
                  .Append("</sheetView></sheetViews>");

            sb.Append("<sheetData>");

            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var row = sheet.Rows[r];
                sb.Append("<row r=\"").Append((r + 1).ToString(CultureInfo.InvariantCulture)).Append("\">");

                for (var c = 0; c < row.Count; c++)
                    AppendCell(sb, Reference(c, r + 1), row[c], isHeader: r == 0);

                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static void AppendColumnWidths(StringBuilder sb, XlsxSheet sheet)
        {
            var columns = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count);
            if (columns == 0) return;

            sb.Append("<cols>");
            for (var c = 0; c < columns; c++)
            {
                var longest = sheet.Rows
                    .Where(r => c < r.Count)
                    .Select(r => r[c]?.Length ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();

                // Clamped: one pathological cell should not produce a column nobody can scroll past.
                var width = Math.Min(60, Math.Max(9, longest + 2));
                sb.Append("<col min=\"").Append((c + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" max=\"").Append((c + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" width=\"").Append(width.ToString(CultureInfo.InvariantCulture))
                  .Append("\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
        }

        private static void AppendCell(StringBuilder sb, string reference, string? value, bool isHeader)
        {
            sb.Append("<c r=\"").Append(reference).Append('"');
            if (isHeader) sb.Append(" s=\"1\"");

            if (string.IsNullOrEmpty(value))
            {
                sb.Append("/>");
                return;
            }

            if (IsPlainNumber(value!))
            {
                sb.Append("><v>").Append(value).Append("</v></c>");
                return;
            }

            // Inline string, not a shared-string table: it costs a few bytes and removes an entire
            // class of index-mismatch corruption, in a writer nobody is going to profile.
            sb.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
            EscapeText(sb, value!);
            sb.Append("</t></is></c>");
        }

        /// <summary>
        /// Whether a value should be written as a number.
        ///
        /// Strict on purpose. A leading zero, a leading plus, whitespace, an exponent or anything
        /// Excel might reinterpret stays a string — because "007" becoming 7, or "L03-1" becoming a
        /// date, is a takeoff quantity or a group name silently changing on the way to the client.
        /// </summary>
        private static bool IsPlainNumber(string value)
        {
            if (value.Length == 0 || value.Length > 15) return false;
            if (value != value.Trim()) return false;

            var i = 0;
            if (value[0] == '-') i = 1;
            if (i >= value.Length) return false;

            // A leading zero is significant to a human — a drawing number, a level code — so it is
            // never a number to us. "0" alone is fine.
            if (value[i] == '0' && value.Length > i + 1 && value[i + 1] != '.') return false;

            var seenDot = false;
            for (; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '.')
                {
                    if (seenDot) return false;
                    seenDot = true;
                    continue;
                }
                if (c < '0' || c > '9') return false;
            }

            return true;
        }

        private static string Reference(int columnIndex, int rowNumber)
        {
            var sb = new StringBuilder();
            var c = columnIndex;
            do
            {
                sb.Insert(0, (char)('A' + (c % 26)));
                c = (c / 26) - 1;
            } while (c >= 0);

            sb.Append(rowNumber.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void EscapeAttr(StringBuilder sb, string text)
        {
            foreach (var c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }
        }

        private static void EscapeText(StringBuilder sb, string text)
        {
            foreach (var c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    default:
                        // XML 1.0 cannot carry most control characters at all, escaped or not.
                        // Dropping them beats writing a file Excel refuses to open.
                        if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                        sb.Append(c);
                        break;
                }
            }
        }
    }
}
