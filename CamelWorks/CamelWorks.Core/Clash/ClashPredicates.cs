using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Clash
{
    /// <summary>A condition over one clash result.</summary>
    public interface IClashPredicate
    {
        /// <summary>How the rule reads on screen and in the report appendix.</summary>
        string Describe();

        /// <summary>Whether this result matches.</summary>
        bool Matches(ClashItem item);
    }

    /// <summary>
    /// The predicates the Suppress, Flag and Assign sections share.
    ///
    /// One vocabulary across all three, because a coordinator who has learned to express
    /// "insulation touching a wall, less than 5mm, same model" for suppression should not have to
    /// learn a different way of saying it to assign it to somebody.
    ///
    /// <b>Every predicate is false when the data it needs is missing.</b> A rule about overlap
    /// volume cannot decide anything about a result whose engine reported none, and treating
    /// absent as zero would suppress results nobody asked to suppress — silently, which is the
    /// worst way for a filter to be wrong.
    /// </summary>
    public static class ClashPredicates
    {
        /// <summary>Matches everything. The identity, useful as a default.</summary>
        public static IClashPredicate Always() => new AlwaysPredicate();

        /// <summary>Overlap volume at or below a threshold — the "touching, not clashing" rule.</summary>
        public static IClashPredicate MaxOverlapVolume(double cubicMetres) =>
            new VolumePredicate(cubicMetres);

        /// <summary>Signed distance within a range, inclusive.</summary>
        public static IClashPredicate DistanceBetween(double minMetres, double maxMetres) =>
            new DistancePredicate(minMetres, maxMetres);

        /// <summary>Crossing angle within a range in degrees, inclusive.</summary>
        public static IClashPredicate AngleBetween(double minDegrees, double maxDegrees) =>
            new AnglePredicate(minDegrees, maxDegrees);

        /// <summary>Both participants belong to the same model.</summary>
        public static IClashPredicate SameModel() => new SameModelPredicate();

        /// <summary>At least one participant is in the named set.</summary>
        public static IClashPredicate EitherInSet(string setName) => new InSetPredicate(setName, requireBoth: false);

        /// <summary>Both participants are in the named set.</summary>
        public static IClashPredicate BothInSet(string setName) => new InSetPredicate(setName, requireBoth: true);

        /// <summary>A property equals a value, case-insensitively.</summary>
        public static IClashPredicate PropertyEquals(string key, string? value) =>
            new PropertyPredicate(key, value, exact: true);

        /// <summary>A property contains a value, case-insensitively.</summary>
        public static IClashPredicate PropertyContains(string key, string value) =>
            new PropertyPredicate(key, value, exact: false);

        /// <summary>Either participant's category matches, case-insensitively.</summary>
        public static IClashPredicate EitherCategoryIs(string category) => new CategoryPredicate(category);

        /// <summary>Negation.</summary>
        public static IClashPredicate Not(IClashPredicate inner) => new NotPredicate(inner);

        /// <summary>All must match. An empty list matches nothing, not everything — see the remarks.</summary>
        public static IClashPredicate All(params IClashPredicate[] parts) => new AllPredicate(parts);

        /// <summary>Any may match.</summary>
        public static IClashPredicate Any(params IClashPredicate[] parts) => new AnyPredicate(parts);

        private sealed class AlwaysPredicate : IClashPredicate
        {
            public string Describe() => "every result";
            public bool Matches(ClashItem item) => true;
        }

        private sealed class VolumePredicate : IClashPredicate
        {
            private readonly double _max;
            public VolumePredicate(double max) => _max = max;

            public string Describe() =>
                "overlap volume at most " + _max.ToString("0.####", CultureInfo.InvariantCulture) + " m3";

            // Absent volume does not match: treating it as zero would suppress results nobody
            // asked to suppress.
            public bool Matches(ClashItem item) => item.OverlapVolume.HasValue && item.OverlapVolume.Value <= _max;
        }

        private sealed class DistancePredicate : IClashPredicate
        {
            private readonly double _min, _max;
            public DistancePredicate(double min, double max) { _min = min; _max = max; }

            public string Describe() =>
                "distance between " + _min.ToString("0.####", CultureInfo.InvariantCulture)
                + " and " + _max.ToString("0.####", CultureInfo.InvariantCulture) + " m";

            public bool Matches(ClashItem item) =>
                item.Distance.HasValue && item.Distance.Value >= _min && item.Distance.Value <= _max;
        }

        private sealed class AnglePredicate : IClashPredicate
        {
            private readonly double _min, _max;
            public AnglePredicate(double min, double max) { _min = min; _max = max; }

            public string Describe() =>
                "crossing angle between " + _min.ToString("0.#", CultureInfo.InvariantCulture)
                + "° and " + _max.ToString("0.#", CultureInfo.InvariantCulture) + "°";

            // A slab has no dominant axis, so its angle is genuinely undefined rather than zero.
            public bool Matches(ClashItem item) =>
                item.CrossingAngleDegrees.HasValue
                && item.CrossingAngleDegrees.Value >= _min
                && item.CrossingAngleDegrees.Value <= _max;
        }

        private sealed class SameModelPredicate : IClashPredicate
        {
            public string Describe() => "both items in the same model";
            public bool Matches(ClashItem item) => item.IsSameModel;
        }

        private sealed class InSetPredicate : IClashPredicate
        {
            private readonly string _set;
            private readonly bool _both;
            public InSetPredicate(string set, bool requireBoth)
            {
                _set = set ?? throw new ArgumentNullException(nameof(set));
                _both = requireBoth;
            }

            public string Describe() => (_both ? "both items" : "either item") + " in set '" + _set + "'";

            public bool Matches(ClashItem item) =>
                _both
                    ? item.SetsA.Contains(_set) && item.SetsB.Contains(_set)
                    : item.SetsA.Contains(_set) || item.SetsB.Contains(_set);
        }

        private sealed class PropertyPredicate : IClashPredicate
        {
            private readonly string _key;
            private readonly string? _value;
            private readonly bool _exact;

            public PropertyPredicate(string key, string? value, bool exact)
            {
                _key = key ?? throw new ArgumentNullException(nameof(key));
                _value = value;
                _exact = exact;
            }

            public string Describe() =>
                "property " + _key + (_exact ? " = '" : " contains '") + (_value ?? string.Empty) + "'";

            public bool Matches(ClashItem item)
            {
                if (!item.Properties.TryGetValue(_key, out var actual)) return false;
                if (actual == null) return _value == null;
                if (_value == null) return false;

                return _exact
                    ? string.Equals(actual, _value, StringComparison.OrdinalIgnoreCase)
                    : actual.IndexOf(_value, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private sealed class CategoryPredicate : IClashPredicate
        {
            private readonly string _category;
            public CategoryPredicate(string category) =>
                _category = category ?? throw new ArgumentNullException(nameof(category));

            public string Describe() => "either item is a " + _category;

            public bool Matches(ClashItem item) =>
                string.Equals(item.CategoryA, _category, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.CategoryB, _category, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class NotPredicate : IClashPredicate
        {
            private readonly IClashPredicate _inner;
            public NotPredicate(IClashPredicate inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

            public string Describe() => "not (" + _inner.Describe() + ")";
            public bool Matches(ClashItem item) => !_inner.Matches(item);
        }

        private sealed class AllPredicate : IClashPredicate
        {
            private readonly IClashPredicate[] _parts;
            public AllPredicate(IClashPredicate[] parts) => _parts = parts ?? Array.Empty<IClashPredicate>();

            public string Describe() =>
                _parts.Length == 0 ? "nothing" : string.Join(" and ", _parts.Select(p => p.Describe()));

            // An empty All matches NOTHING, not everything. The mathematical identity would be
            // "everything", and a half-built suppression rule that silently suppresses the entire
            // board is not a defensible default.
            public bool Matches(ClashItem item) => _parts.Length != 0 && _parts.All(p => p.Matches(item));
        }

        private sealed class AnyPredicate : IClashPredicate
        {
            private readonly IClashPredicate[] _parts;
            public AnyPredicate(IClashPredicate[] parts) => _parts = parts ?? Array.Empty<IClashPredicate>();

            public string Describe() =>
                _parts.Length == 0 ? "nothing" : string.Join(" or ", _parts.Select(p => p.Describe()));

            public bool Matches(ClashItem item) => _parts.Any(p => p.Matches(item));
        }
    }
}
