using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CamelWorks.Core.Store
{
    /// <summary>The kinds a <see cref="JsonValue"/> can be.</summary>
    public enum JsonKind
    {
        /// <summary>JSON null.</summary>
        Null = 0,

        /// <summary>true or false.</summary>
        Bool = 1,

        /// <summary>A number. Held as a double and as its original text, so a round-trip is lossless.</summary>
        Number = 2,

        /// <summary>A string.</summary>
        String = 3,

        /// <summary>An ordered array.</summary>
        Array = 4,

        /// <summary>An object with ordered keys.</summary>
        Object = 5,
    }

    /// <summary>
    /// A minimal JSON value.
    ///
    /// Hand-rolled rather than taken from a package, for the reason the whole project holds to: a
    /// free plug-in that must load into four host years alongside whatever else the user has
    /// installed cannot afford an assembly-binding argument with somebody else's copy of the same
    /// library. The architecture test enforces it.
    ///
    /// Two properties beyond "it parses JSON", both of which the sidecar store depends on:
    ///
    /// * <b>Key order is preserved.</b> A file that reorders its keys on every save produces a
    ///   meaningless diff, and these files sit in the user's project folder next to their models.
    /// * <b>Numbers keep their original text.</b> Reading 1.10 and writing 1.1 is a spurious diff;
    ///   reading a long id and writing it in exponent form is data loss.
    /// </summary>
    public sealed class JsonValue
    {
        private readonly List<KeyValuePair<string, JsonValue>>? _members;
        private readonly List<JsonValue>? _items;
        private readonly string? _text;
        private readonly bool _bool;
        private readonly double _number;

        private JsonValue(JsonKind kind, string? text = null, bool b = false, double number = 0,
                          List<JsonValue>? items = null, List<KeyValuePair<string, JsonValue>>? members = null)
        {
            Kind = kind; _text = text; _bool = b; _number = number; _items = items; _members = members;
        }

        /// <summary>What this value is.</summary>
        public JsonKind Kind { get; }

        /// <summary>JSON null.</summary>
        public static JsonValue Null { get; } = new JsonValue(JsonKind.Null);

        /// <summary>A boolean.</summary>
        public static JsonValue Bool(bool value) => new JsonValue(JsonKind.Bool, b: value);

        /// <summary>A string.</summary>
        public static JsonValue String(string? value) =>
            value == null ? Null : new JsonValue(JsonKind.String, text: value);

        /// <summary>A number, written in invariant culture with no exponent for ordinary magnitudes.</summary>
        public static JsonValue Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "JSON has no NaN or Infinity");
            return new JsonValue(JsonKind.Number, text: value.ToString("R", CultureInfo.InvariantCulture), number: value);
        }

        /// <summary>An integer.</summary>
        public static JsonValue Number(long value) =>
            new JsonValue(JsonKind.Number, text: value.ToString(CultureInfo.InvariantCulture), number: value);

        internal static JsonValue NumberFromText(string text, double parsed) =>
            new JsonValue(JsonKind.Number, text: text, number: parsed);

        /// <summary>An empty array.</summary>
        public static JsonValue Array() => new JsonValue(JsonKind.Array, items: new List<JsonValue>());

        /// <summary>An array of the given items.</summary>
        public static JsonValue Array(IEnumerable<JsonValue> items) =>
            new JsonValue(JsonKind.Array, items: new List<JsonValue>(items));

        /// <summary>An empty object.</summary>
        public static JsonValue Object() =>
            new JsonValue(JsonKind.Object, members: new List<KeyValuePair<string, JsonValue>>());

        // ---------------------------------------------------------------------------------
        // Reading
        // ---------------------------------------------------------------------------------

        /// <summary>The boolean value, or <paramref name="fallback"/> when this is not a bool.</summary>
        public bool AsBool(bool fallback = false) => Kind == JsonKind.Bool ? _bool : fallback;

        /// <summary>The numeric value, or <paramref name="fallback"/> when this is not a number.</summary>
        public double AsDouble(double fallback = 0) => Kind == JsonKind.Number ? _number : fallback;

        /// <summary>The integer value, or <paramref name="fallback"/> when this is not a number.</summary>
        public long AsLong(long fallback = 0) =>
            Kind == JsonKind.Number ? (long)Math.Round(_number, MidpointRounding.AwayFromZero) : fallback;

        /// <summary>The string value, or <paramref name="fallback"/> when this is not a string.</summary>
        public string? AsString(string? fallback = null) => Kind == JsonKind.String ? _text : fallback;

        /// <summary>Array items. Empty for anything that is not an array.</summary>
        public IReadOnlyList<JsonValue> Items =>
            _items ?? (IReadOnlyList<JsonValue>)System.Array.Empty<JsonValue>();

        /// <summary>Object keys, in file order. Empty for anything that is not an object.</summary>
        public IReadOnlyList<string> Keys =>
            _members?.Select(m => m.Key).ToList() ?? (IReadOnlyList<string>)System.Array.Empty<string>();

        /// <summary>True when this object has the given key.</summary>
        public bool Has(string key) => _members != null && _members.Any(m => m.Key == key);

        /// <summary>
        /// The member with this key, or <see cref="Null"/> when absent. Absence is ordinary — an
        /// older file simply lacks a newer key — so this never throws.
        /// </summary>
        public JsonValue this[string key]
        {
            get
            {
                if (_members == null) return Null;
                foreach (var m in _members) if (m.Key == key) return m.Value;
                return Null;
            }
        }

        // ---------------------------------------------------------------------------------
        // Writing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Set a member, keeping its original position when it already exists and appending when it
        /// does not. Position stability is what keeps the diff of a re-saved file small.
        /// </summary>
        public JsonValue Set(string key, JsonValue value)
        {
            if (_members == null) throw new InvalidOperationException("not an object");
            if (key == null) throw new ArgumentNullException(nameof(key));

            for (var i = 0; i < _members.Count; i++)
            {
                if (_members[i].Key != key) continue;
                _members[i] = new KeyValuePair<string, JsonValue>(key, value ?? Null);
                return this;
            }

            _members.Add(new KeyValuePair<string, JsonValue>(key, value ?? Null));
            return this;
        }

        /// <summary>Set a string member.</summary>
        public JsonValue Set(string key, string? value) => Set(key, String(value));

        /// <summary>Set a numeric member.</summary>
        public JsonValue Set(string key, long value) => Set(key, Number(value));

        /// <summary>Set a numeric member.</summary>
        public JsonValue Set(string key, double value) => Set(key, Number(value));

        /// <summary>Set a boolean member.</summary>
        public JsonValue Set(string key, bool value) => Set(key, Bool(value));

        /// <summary>Remove a member. Returns true when it was there.</summary>
        public bool Remove(string key)
        {
            if (_members == null) return false;
            for (var i = 0; i < _members.Count; i++)
            {
                if (_members[i].Key != key) continue;
                _members.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>Append to an array.</summary>
        public JsonValue Add(JsonValue value)
        {
            if (_items == null) throw new InvalidOperationException("not an array");
            _items.Add(value ?? Null);
            return this;
        }

        /// <summary>Serialise. Indented by default, because these files are read and diffed by people.</summary>
        public string ToJson(bool indented = true)
        {
            var sb = new StringBuilder();
            Write(sb, indented, 0);
            return sb.ToString();
        }

        /// <inheritdoc />
        public override string ToString() => ToJson(indented: false);

        private void Write(StringBuilder sb, bool indented, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(_bool ? "true" : "false"); break;
                case JsonKind.Number: sb.Append(_text); break;
                case JsonKind.String: WriteString(sb, _text!); break;

                case JsonKind.Array:
                    if (_items!.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('[');
                    for (var i = 0; i < _items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        NewLine(sb, indented, depth + 1);
                        _items[i].Write(sb, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append(']');
                    break;

                case JsonKind.Object:
                    if (_members!.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    for (var i = 0; i < _members.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        NewLine(sb, indented, depth + 1);
                        WriteString(sb, _members[i].Key);
                        sb.Append(':');
                        if (indented) sb.Append(' ');
                        _members[i].Value.Write(sb, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
