using System;
using System.Globalization;
using System.Text;

namespace CamelWorks.Core.Sets
{
    /// <summary>
    /// The comparisons a condition can make.
    ///
    /// <b>There are no negated operators here, and that is deliberate.</b> "Not equals" is not the
    /// complement of "equals" over a model: a host search for <c>Fire Rating != 60</c> does not
    /// return the elements that have no Fire Rating property at all, because a condition on an
    /// absent property matches nothing. The complement of "equals 60" does include them — they are
    /// certainly not 60.
    ///
    /// Anybody who has built a set that quietly omitted every untagged element has met this. So
    /// negation lives in the expression tree and compiles to a subtraction, where it is exact,
    /// rather than to an operator that is nearly right.
    /// </summary>
    public enum SetOperator
    {
        /// <summary>Property value equals the given value.</summary>
        Equals = 0,

        /// <summary>Property value contains the given text.</summary>
        Contains = 1,

        /// <summary>Property value starts with the given text.</summary>
        StartsWith = 2,

        /// <summary>Property value ends with the given text.</summary>
        EndsWith = 3,

        /// <summary>Property value matches a wildcard pattern, with <c>*</c> and <c>?</c>.</summary>
        WildcardMatch = 4,

        /// <summary>Property value is numerically greater than the given value.</summary>
        GreaterThan = 5,

        /// <summary>Property value is numerically greater than or equal to the given value.</summary>
        GreaterThanOrEqual = 6,

        /// <summary>Property value is numerically less than the given value.</summary>
        LessThan = 7,

        /// <summary>Property value is numerically less than or equal to the given value.</summary>
        LessThanOrEqual = 8,

        /// <summary>The property exists at all, whatever its value.</summary>
        Defined = 9,

        /// <summary>The element carries the named property category.</summary>
        HasCategory = 10,
    }

    /// <summary>
    /// One property test — the leaf of a set expression, and the unit the host can execute.
    /// </summary>
    public sealed class SetCondition : IEquatable<SetCondition>
    {
        /// <summary>Create a condition.</summary>
        /// <param name="category">Property category, as the host names it. Required.</param>
        /// <param name="property">Property name. Null only for <see cref="SetOperator.HasCategory"/>.</param>
        /// <param name="op">The comparison.</param>
        /// <param name="value">The value to compare against; ignored by Defined and HasCategory.</param>
        public SetCondition(string category, string? property, SetOperator op, string? value = null)
        {
            Category = string.IsNullOrWhiteSpace(category)
                ? throw new ArgumentException("category is required", nameof(category))
                : category;

            if (op != SetOperator.HasCategory && string.IsNullOrWhiteSpace(property))
                throw new ArgumentException("property is required for this operator", nameof(property));

            if (NeedsValue(op) && value == null)
                throw new ArgumentException("this operator needs a value", nameof(value));

            if (IsNumeric(op) && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException("a numeric comparison needs a value that parses as a number", nameof(value));

            Property = property;
            Operator = op;
            Value = value;
        }

        /// <summary>Property category.</summary>
        public string Category { get; }

        /// <summary>Property name, or null for a category test.</summary>
        public string? Property { get; }

        /// <summary>The comparison.</summary>
        public SetOperator Operator { get; }

        /// <summary>The value compared against, where the operator uses one.</summary>
        public string? Value { get; }

        /// <summary>True when the operator compares numerically rather than as text.</summary>
        public bool IsNumericComparison => IsNumeric(Operator);

        /// <summary>
        /// The value as a number. Only meaningful for a numeric comparison, where the constructor
        /// has already proved it parses.
        /// </summary>
        public double NumericValue =>
            double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

        /// <summary>
        /// A stable text form, used to sort literals and to spot that two of them are the same.
        ///
        /// Length-prefixed rather than separated by a delimiter. Any delimiter can appear inside a
        /// property name — a set of families named "A|B" and one named "A" with a value "B" would
        /// canonicalise identically, and the compiler would fold two different conditions into one.
        /// A length prefix cannot collide. Ordinal and culture-free throughout, so a set compiled
        /// in Istanbul compiles identically in Oslo.
        /// </summary>
        public string Canonical
        {
            get
            {
                var s = new StringBuilder();
                Pack(s, Category);
                Pack(s, Property);
                s.Append(((int)Operator).ToString(CultureInfo.InvariantCulture)).Append(':');
                Pack(s, Value);
                return s.ToString();
            }
        }

        /// <summary>How the condition reads on screen.</summary>
        public string Describe() => Operator switch
        {
            SetOperator.HasCategory => "has category '" + Category + "'",
            SetOperator.Defined => Category + "." + Property + " is present",
            SetOperator.Equals => Category + "." + Property + " = '" + Value + "'",
            SetOperator.Contains => Category + "." + Property + " contains '" + Value + "'",
            SetOperator.StartsWith => Category + "." + Property + " starts with '" + Value + "'",
            SetOperator.EndsWith => Category + "." + Property + " ends with '" + Value + "'",
            SetOperator.WildcardMatch => Category + "." + Property + " matches '" + Value + "'",
            SetOperator.GreaterThan => Category + "." + Property + " > " + Value,
            SetOperator.GreaterThanOrEqual => Category + "." + Property + " >= " + Value,
            SetOperator.LessThan => Category + "." + Property + " < " + Value,
            SetOperator.LessThanOrEqual => Category + "." + Property + " <= " + Value,
            _ => Category + "." + Property,
        };

        private static void Pack(StringBuilder s, string? part)
        {
            if (part == null) { s.Append("-1:"); return; }
            s.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
        }

        private static bool NeedsValue(SetOperator op) =>
            op != SetOperator.Defined && op != SetOperator.HasCategory;

        private static bool IsNumeric(SetOperator op) =>
            op == SetOperator.GreaterThan || op == SetOperator.GreaterThanOrEqual
            || op == SetOperator.LessThan || op == SetOperator.LessThanOrEqual;

        /// <inheritdoc />
        public bool Equals(SetCondition? other) =>
            other != null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as SetCondition);

        /// <inheritdoc />
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);

        /// <inheritdoc />
        public override string ToString() => Describe();
    }
}
