using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Data
{
    /// <summary>One element's contribution to a takeoff.</summary>
    public sealed class TakeoffLine
    {
        /// <summary>Create a line.</summary>
        /// <param name="id">The element, for the examples in the result.</param>
        /// <param name="group">What it is subtotalled under — a level, a type, a system.</param>
        /// <param name="value">The measured property, exactly as the host gave it.</param>
        public TakeoffLine(string id, string group, string? value)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Group = group ?? throw new ArgumentNullException(nameof(group));
            Value = value;
        }

        /// <summary>The element.</summary>
        public string Id { get; }

        /// <summary>What it is subtotalled under.</summary>
        public string Group { get; }

        /// <summary>The raw property value.</summary>
        public string? Value { get; }
    }

    /// <summary>One subtotal.</summary>
    public sealed class TakeoffGroup
    {
        internal TakeoffGroup(string name, int count, Quantity? total, int unreadable,
                              bool mixedKinds, IReadOnlyList<string> unreadableExamples)
        {
            Name = name; Count = count; Total = total; Unreadable = unreadable;
            MixedKinds = mixedKinds; UnreadableExamples = unreadableExamples;
        }

        /// <summary>The group.</summary>
        public string Name { get; }

        /// <summary>
        /// How many elements are in it.
        ///
        /// Always right, even when the total is not. A count is the one number that survives every
        /// unit problem, and it is often the one somebody actually wanted.
        /// </summary>
        public int Count { get; }

        /// <summary>The subtotal, or null when there is no honest one to give.</summary>
        public Quantity? Total { get; }

        /// <summary>How many values could not be read.</summary>
        public int Unreadable { get; }

        /// <summary>
        /// True when the group mixed incompatible units — lengths and areas in one column.
        ///
        /// There is no total in that case, deliberately. It means the property being measured is
        /// not the same property across the group, which is a mapping mistake, and a number that
        /// added them would be wrong in a way nobody could see.
        /// </summary>
        public bool MixedKinds { get; }

        /// <summary>A few of the values that could not be read, so the cause is visible.</summary>
        public IReadOnlyList<string> UnreadableExamples { get; }

        /// <summary>True when every value in the group was read.</summary>
        public bool IsComplete => Unreadable == 0 && !MixedKinds;

        /// <inheritdoc />
        public override string ToString()
        {
            var s = Name + " — " + Count.ToString("N0", CultureInfo.InvariantCulture)
                  + (Count == 1 ? " element" : " elements");

            if (MixedKinds) return s + " · no total: the values are not all the same kind of measurement";

            if (Total != null) s += " · " + Total.Value;
            if (Unreadable > 0)
                s += " · " + Unreadable.ToString("N0", CultureInfo.InvariantCulture) + " values unreadable";

            return s;
        }
    }

    /// <summary>The whole takeoff.</summary>
    public sealed class TakeoffResult
    {
        internal TakeoffResult(IReadOnlyList<TakeoffGroup> groups)
        {
            Groups = groups;
        }

        /// <summary>The subtotals, in group-name order.</summary>
        public IReadOnlyList<TakeoffGroup> Groups { get; }

        /// <summary>Elements counted.</summary>
        public int Count => Groups.Sum(g => g.Count);

        /// <summary>Values that could not be read anywhere in the takeoff.</summary>
        public int Unreadable => Groups.Sum(g => g.Unreadable);

        /// <summary>The grand total, when every group measures the same kind and all read.</summary>
        public Quantity? Total
        {
            get
            {
                var totals = Groups.Where(g => g.Total != null).Select(g => g.Total!.Value).ToList();
                if (totals.Count == 0) return null;
                if (totals.Select(t => t.Kind).Distinct().Count() > 1) return null;

                return totals.Aggregate((a, b) => a.Add(b));
            }
        }

        /// <summary>
        /// True when nothing was dropped.
        ///
        /// The flag a quantity surveyor needs before pasting the number into a cost plan, and the
        /// reason the unreadable count is on the face of the result rather than in a log.
        /// </summary>
        public bool IsComplete => Groups.All(g => g.IsComplete);

        /// <summary>The one-line readout.</summary>
        public override string ToString()
        {
            var s = Count.ToString("N0", CultureInfo.InvariantCulture) + " elements in "
                  + Groups.Count.ToString(CultureInfo.InvariantCulture)
                  + (Groups.Count == 1 ? " group" : " groups");

            var total = Total;
            if (total != null) s += " · " + total.Value;

            if (Unreadable > 0)
                s += " · " + Unreadable.ToString("N0", CultureInfo.InvariantCulture)
                   + " values unreadable, so this total is short";

            return s;
        }
    }

    /// <summary>
    /// Adds up measured properties, and says what it could not add.
    ///
    /// The host gives properties as display strings with the unit baked in, and a federation mixes
    /// them freely — millimetres from one discipline, metres from another, feet and inches from a
    /// consultant. Summing the numbers off the front of those strings produces a total that is
    /// wrong by whatever mixture the model happens to contain and looks entirely plausible, which
    /// is why it survives into a cost plan.
    ///
    /// So: everything is converted to a base unit before it is added, incompatible kinds are
    /// refused rather than added, and whatever could not be read is counted and shown with
    /// examples. A short total that says it is short is useful. A short total that does not is not.
    /// </summary>
    public static class Takeoff
    {
        /// <summary>How many distinct unreadable values are kept as examples per group.</summary>
        public const int ExampleCount = 5;

        /// <summary>Add up the lines.</summary>
        public static TakeoffResult Sum(IEnumerable<TakeoffLine> lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));

            var groups = new List<TakeoffGroup>();

            foreach (var group in lines.Where(l => l != null)
                                       .GroupBy(l => l.Group, StringComparer.Ordinal)
                                       .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var count = 0;
                var unreadable = 0;
                var examples = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                Quantity? total = null;
                var mixed = false;

                foreach (var line in group)
                {
                    count++;

                    if (!Quantity.TryParse(line.Value, out var quantity))
                    {
                        // A blank value is not a broken one: plenty of elements simply do not carry
                        // the property being measured, and calling that unreadable would bury the
                        // values that really are malformed.
                        if (string.IsNullOrWhiteSpace(line.Value)) continue;

                        unreadable++;
                        if (examples.Count < ExampleCount && seen.Add(line.Value!)) examples.Add(line.Value!);
                        continue;
                    }

                    if (total == null) { total = quantity; continue; }

                    if (total.Value.Kind != quantity.Kind)
                    {
                        // Lengths and areas in one column means the property is not the same
                        // property across the group. There is no honest sum, so there is no sum.
                        mixed = true;
                        continue;
                    }

                    total = total.Value.Add(quantity);
                }

                groups.Add(new TakeoffGroup(group.Key, count, mixed ? null : total, unreadable, mixed, examples));
            }

            return new TakeoffResult(groups);
        }
    }
}
