using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CamelWorks.Core.Store
{
    /// <summary>Thrown when a sidecar file is not valid JSON. Carries the offset, because "the
    /// settings file is corrupt" with no position is a support ticket nobody can act on.</summary>
    public sealed class JsonParseException : Exception
    {
        /// <summary>Character offset the parser stopped at.</summary>
        public int Offset { get; }

        /// <summary>Create the exception.</summary>
        public JsonParseException(string message, int offset)
            : base(message + " (at offset " + offset.ToString(CultureInfo.InvariantCulture) + ")")
            => Offset = offset;
    }

    /// <summary>
    /// A strict, allocation-light JSON parser.
    ///
    /// Strict on purpose: it rejects trailing commas, comments and single quotes rather than
    /// guessing. These files are written by CamelWorks, so a file that is not valid JSON is a
    /// symptom — a half-written file from a crash, or a sync client's conflicted copy — and
    /// guessing at its meaning turns a detectable problem into a silent wrong answer.
    /// </summary>
    public static class JsonReader
    {
        /// <summary>Parse, or throw <see cref="JsonParseException"/>.</summary>
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            var pos = 0;
            SkipWhitespace(text, ref pos);
            var value = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);

            if (pos != text.Length)
                throw new JsonParseException("trailing content after the top-level value", pos);

            return value;
        }

        /// <summary>Parse, returning false rather than throwing. For reading files that may be damaged.</summary>
        public static bool TryParse(string? text, out JsonValue value)
        {
            value = JsonValue.Null;
            if (text == null) return false;
            try { value = Parse(text); return true; }
            catch (JsonParseException) { return false; }
        }

        private static JsonValue ParseValue(string s, ref int pos)
        {
            if (pos >= s.Length) throw new JsonParseException("unexpected end of input", pos);

            switch (s[pos])
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return JsonValue.String(ParseString(s, ref pos));
                case 't': Expect(s, ref pos, "true"); return JsonValue.Bool(true);
                case 'f': Expect(s, ref pos, "false"); return JsonValue.Bool(false);
                case 'n': Expect(s, ref pos, "null"); return JsonValue.Null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static JsonValue ParseObject(string s, ref int pos)
        {
            var obj = JsonValue.Object();
            pos++; // {
            SkipWhitespace(s, ref pos);

            if (pos < s.Length && s[pos] == '}') { pos++; return obj; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"')
                    throw new JsonParseException("expected a quoted member name", pos);

                var key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);

                if (pos >= s.Length || s[pos] != ':')
                    throw new JsonParseException("expected ':' after the member name", pos);
                pos++;

                SkipWhitespace(s, ref pos);
                obj.Set(key, ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);

                if (pos >= s.Length) throw new JsonParseException("unterminated object", pos);
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return obj; }

                throw new JsonParseException("expected ',' or '}'", pos);
            }
        }

        private static JsonValue ParseArray(string s, ref int pos)
        {
            var arr = JsonValue.Array();
            pos++; // [
            SkipWhitespace(s, ref pos);

            if (pos < s.Length && s[pos] == ']') { pos++; return arr; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                arr.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);

                if (pos >= s.Length) throw new JsonParseException("unterminated array", pos);
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return arr; }

                throw new JsonParseException("expected ',' or ']'", pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();

            while (true)
            {
                if (pos >= s.Length) throw new JsonParseException("unterminated string", pos);

                var c = s[pos++];
                if (c == '"') return sb.ToString();

                if (c != '\\')
                {
                    if (c < 0x20) throw new JsonParseException("unescaped control character in a string", pos - 1);
                    sb.Append(c);
                    continue;
                }

                if (pos >= s.Length) throw new JsonParseException("unterminated escape", pos);
                var e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new JsonParseException("truncated \\u escape", pos);
                        if (!int.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out var code))
                            throw new JsonParseException("malformed \\u escape", pos);
                        sb.Append((char)code);
                        pos += 4;
                        break;
                    default:
                        throw new JsonParseException("unknown escape '\\" + e + "'", pos - 1);
                }
            }
        }

        private static JsonValue ParseNumber(string s, ref int pos)
        {
            var start = pos;

            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length && ((s[pos] >= '0' && s[pos] <= '9') || s[pos] == '.'
                   || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '-' || s[pos] == '+')) pos++;

            if (pos == start) throw new JsonParseException("expected a value", start);

            var text = s.Substring(start, pos - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                throw new JsonParseException("malformed number '" + text + "'", start);

            // The original text is kept so a re-save does not turn 1.10 into 1.1, or a long id
            // into exponent form. See JsonValue.
            return JsonValue.NumberFromText(text, parsed);
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new JsonParseException("expected '" + literal + "'", pos);
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else return;
            }
        }
    }
}
