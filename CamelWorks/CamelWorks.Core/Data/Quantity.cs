using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Data
{
    /// <summary>What a quantity measures. Only compatible kinds can be added.</summary>
    public enum QuantityKind
    {
        /// <summary>No unit — a count, a ratio, a bare number.</summary>
        Scalar = 0,

        /// <summary>A length.</summary>
        Length = 1,

        /// <summary>An area.</summary>
        Area = 2,

        /// <summary>A volume.</summary>
        Volume = 3,

        /// <summary>A mass.</summary>
        Mass = 4,

        /// <summary>An angle.</summary>
        Angle = 5,
    }

    /// <summary>
    /// A number with a unit, parsed from what the host actually gives us.
    ///
    /// The host hands properties over as display strings with the unit baked in: "1200 mm",
    /// "1.2 m", "3'-6\"", "4,5 m²". A takeoff that sums those as text, or strips the letters and
    /// adds the numbers, produces a total that is wrong by whatever mixture of units the
    /// federation happens to contain — and it looks completely plausible, which is the problem.
    ///
    /// <b>An unrecognised unit is refused, not guessed.</b> A total that silently dropped the
    /// values it could not read is worse than no total at all: nobody can tell it is short. What
    /// the parser cannot read, the takeoff counts and reports.
    /// </summary>
    public readonly struct Quantity : IEquatable<Quantity>
    {
        private Quantity(double value, QuantityKind kind)
        {
            Value = value;
            Kind = kind;
        }

        /// <summary>The value, in the base unit for its kind: metres, square metres, cubic metres, kilograms, degrees.</summary>
        public double Value { get; }

        /// <summary>What it measures.</summary>
        public QuantityKind Kind { get; }

        /// <summary>A bare number.</summary>
        public static Quantity Scalar(double value) => new Quantity(value, QuantityKind.Scalar);

        /// <summary>A length in metres.</summary>
        public static Quantity Metres(double value) => new Quantity(value, QuantityKind.Length);

        /// <summary>An area in square metres.</summary>
        public static Quantity SquareMetres(double value) => new Quantity(value, QuantityKind.Area);

        /// <summary>A volume in cubic metres.</summary>
        public static Quantity CubicMetres(double value) => new Quantity(value, QuantityKind.Volume);

        /// <summary>A mass in kilograms.</summary>
        public static Quantity Kilograms(double value) => new Quantity(value, QuantityKind.Mass);

        /// <summary>An angle in degrees.</summary>
        public static Quantity Degrees(double value) => new Quantity(value, QuantityKind.Angle);

        /// <summary>
        /// Parse a display string.
        /// </summary>
        /// <param name="text">What the host gave us.</param>
        /// <param name="quantity">The parsed value, converted to its base unit.</param>
        /// <returns>False for anything it cannot read, which the caller must then report.</returns>
        public static bool TryParse(string? text, out Quantity quantity)
        {
            quantity = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text!.Trim();

            // Feet and inches first: 3'-6", 3' 6", 3'6", 6". Nothing else in the vocabulary uses
            // those marks, and the compound form has to be handled before the number is read,
            // since there are two numbers in it.
            if (TryParseImperial(s, out quantity)) return true;

            var split = SplitNumber(s);
            if (split == null) return false;

            var (number, unit) = split.Value;
            if (!TryParseNumber(number, out var value)) return false;

            if (unit.Length == 0)
            {
                quantity = Scalar(value);
                return true;
            }

            if (!Units.TryGetValue(unit, out var conversion)) return false;

            quantity = new Quantity(value * conversion.Factor, conversion.Kind);
            return true;
        }

        /// <summary>
        /// Add two quantities.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The kinds differ. Adding a length to an area is not a rounding problem to be tolerated;
        /// it is a takeoff that has silently gone wrong, and the sooner it stops the better.
        /// </exception>
        public Quantity Add(Quantity other)
        {
            if (Kind != other.Kind)
                throw new InvalidOperationException(
                    "cannot add " + Kind.ToString().ToLowerInvariant() + " to " + other.Kind.ToString().ToLowerInvariant());

            return new Quantity(Value + other.Value, Kind);
        }

        /// <summary>Multiply by a plain number.</summary>
        public Quantity Times(double factor) => new Quantity(Value * factor, Kind);

        /// <summary>The unit symbol for this kind.</summary>
        public string Unit => Kind switch
        {
            QuantityKind.Length => "m",
            QuantityKind.Area => "m2",
            QuantityKind.Volume => "m3",
            QuantityKind.Mass => "kg",
            QuantityKind.Angle => "deg",
            _ => string.Empty,
        };

        /// <inheritdoc />
        public override string ToString()
        {
            var number = Value.ToString("0.###", CultureInfo.InvariantCulture);
            return Unit.Length == 0 ? number : number + " " + Unit;
        }

        /// <inheritdoc />
        public bool Equals(Quantity other) => Kind == other.Kind && Value.Equals(other.Value);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Quantity q && Equals(q);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked { return (Value.GetHashCode() * 397) ^ (int)Kind; }
        }

        /// <summary>Value equality.</summary>
        public static bool operator ==(Quantity a, Quantity b) => a.Equals(b);

        /// <summary>Value inequality.</summary>
        public static bool operator !=(Quantity a, Quantity b) => !a.Equals(b);

        // -----------------------------------------------------------------------------------

        private static readonly Dictionary<string, (double Factor, QuantityKind Kind)> Units =
            new Dictionary<string, (double, QuantityKind)>(StringComparer.OrdinalIgnoreCase)
            {
                ["mm"] = (0.001, QuantityKind.Length),
                ["cm"] = (0.01, QuantityKind.Length),
                ["m"] = (1, QuantityKind.Length),
                ["km"] = (1000, QuantityKind.Length),
                ["in"] = (0.0254, QuantityKind.Length),
                ["ft"] = (0.3048, QuantityKind.Length),
                ["yd"] = (0.9144, QuantityKind.Length),

                ["mm2"] = (1e-6, QuantityKind.Area),
                ["cm2"] = (1e-4, QuantityKind.Area),
                ["m2"] = (1, QuantityKind.Area),
                ["ft2"] = (0.09290304, QuantityKind.Area),
                ["sf"] = (0.09290304, QuantityKind.Area),

                ["mm3"] = (1e-9, QuantityKind.Volume),
                ["cm3"] = (1e-6, QuantityKind.Volume),
                ["m3"] = (1, QuantityKind.Volume),
                ["l"] = (0.001, QuantityKind.Volume),
                ["ft3"] = (0.028316846592, QuantityKind.Volume),
                ["cf"] = (0.028316846592, QuantityKind.Volume),

                ["g"] = (0.001, QuantityKind.Mass),
                ["kg"] = (1, QuantityKind.Mass),
                ["t"] = (1000, QuantityKind.Mass),
                ["lb"] = (0.45359237, QuantityKind.Mass),

                ["deg"] = (1, QuantityKind.Angle),
                ["rad"] = (180 / Math.PI, QuantityKind.Angle),
            };

        private static (string Number, string Unit)? SplitNumber(string text)
        {
            var i = 0;
            if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;

            var digits = 0;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == ','
                                       || text[i] == ' ' || text[i] == '\u00A0'))
            {
                if (char.IsDigit(text[i])) digits++;

                // A space only belongs to the number while it is separating groups of digits, as
                // in "1 200 mm". The moment it is followed by anything else, it is the gap before
                // the unit.
                if ((text[i] == ' ' || text[i] == '\u00A0')
                    && !(i + 1 < text.Length && char.IsDigit(text[i + 1]))) break;

                i++;
            }

            if (digits == 0) return null;

            return (text.Substring(0, i).Trim(), Normalise(text.Substring(i).Trim()));
        }

        private static string Normalise(string unit)
        {
            if (unit.Length == 0) return unit;

            // The host writes superscripts, and a lookup that does not know them refuses values it
            // could perfectly well read.
            var s = unit.Replace("²", "2").Replace("³", "3")
                        .Replace("^2", "2").Replace("^3", "3");

            // Trailing full stop from "ft." and similar.
            if (s.Length > 1 && s[s.Length - 1] == '.') s = s.Substring(0, s.Length - 1);

            return s.Trim();
        }

        private static bool TryParseNumber(string text, out double value)
        {
            var s = text.Replace(" ", string.Empty).Replace("\u00A0", string.Empty);

            // "4,5" is four and a half in half of Europe and four thousand five hundred in the
            // other half. It is decided by shape, not by the machine's locale: a comma with
            // exactly three digits after it and no full stop anywhere is a thousands separator.
            if (s.IndexOf(',') >= 0 && s.IndexOf('.') < 0)
            {
                var parts = s.Split(',');
                var thousands = parts.Length > 1 && parts.Skip(1).All(p => p.Length == 3);
                s = thousands ? string.Concat(parts) : s.Replace(',', '.');
            }
            else
            {
                s = s.Replace(",", string.Empty);
            }

            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseImperial(string text, out Quantity quantity)
        {
            quantity = default;

            var feetMark = text.IndexOf('\'');
            var inchMark = text.IndexOf('"');
            if (feetMark < 0 && inchMark < 0) return false;

            double metres = 0;
            var rest = text;

            if (feetMark >= 0)
            {
                if (!TryParseNumber(text.Substring(0, feetMark), out var feet)) return false;
                metres += feet * 0.3048;
                rest = text.Substring(feetMark + 1).TrimStart(' ', '-');
            }

            if (inchMark >= 0)
            {
                var inchText = rest.Substring(0, rest.IndexOf('"'));
                if (inchText.Trim().Length > 0)
                {
                    if (!TryParseNumber(inchText, out var inches)) return false;
                    metres += inches * 0.0254;
                }
            }
            else if (rest.Trim().Length > 0)
            {
                // Something after the feet mark that is not inches. Refused rather than ignored.
                return false;
            }

            quantity = Metres(metres);
            return true;
        }
    }
}
