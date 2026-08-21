using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Clash
{
    /// <summary>One axis a result can be grouped along.</summary>
    public interface IGroupingRule
    {
        /// <summary>How the rule reads in the stack editor.</summary>
        string Name { get; }

        /// <summary>
        /// This result's value along the axis, or null when the axis does not apply to it.
        ///
        /// Null is meaningful: a result with no level cannot be grouped by level, and inventing
        /// "Unknown" would bury it in a bucket with everything else the rule could not place. The
        /// pipeline surfaces those separately instead.
        /// </summary>
        string? KeyFor(ClashItem item);
    }

    /// <summary>
    /// The grouping axes.
    ///
    /// Chained: each rule contributes a segment, and the group name is the segments joined. Two
    /// rules give "L03 · C4"; three give "L03 · C4 · HVAC v Struct". Native Navisworks has no
    /// subgroups at all, which is why one pipe through one slab arrives as a thousand rows.
    /// </summary>
    public static class GroupingRules
    {
        /// <summary>Group by level.</summary>
        public static IGroupingRule ByLevel() => new Simple("Level", i => i.Level);

        /// <summary>Group by nearest grid intersection.</summary>
        public static IGroupingRule ByGrid() => new Simple("Grid", i => i.Grid);

        /// <summary>Group by zone.</summary>
        public static IGroupingRule ByZone() => new Simple("Zone", i => i.Zone);

        /// <summary>Group by which two models are involved.</summary>
        public static IGroupingRule ByModelPair() => new Simple("Model pair", i => i.ModelPair);

        /// <summary>Group by which two disciplines are involved.</summary>
        public static IGroupingRule ByDisciplinePair() => new Simple("Discipline", i => i.DisciplinePair);

        /// <summary>Group by the clash test that produced the result.</summary>
        public static IGroupingRule ByTest() => new Simple("Test", i => i.TestName);

        /// <summary>Group by MEP system, preferring whichever participant has one.</summary>
        public static IGroupingRule BySystem() => new Simple("System", i => i.SystemA ?? i.SystemB);

        /// <summary>Group by an arbitrary property.</summary>
        public static IGroupingRule ByProperty(string key) =>
            new Simple("Property " + key, i => i.Properties.TryGetValue(key, out var v) ? v : null);

        /// <summary>
        /// Group every result involving the same element together — the answer to "one pipe passes
        /// through forty joists", which native Navisworks reports as forty unrelated rows.
        /// </summary>
        public static IGroupingRule BySameItem() => new SameItemRule();

        /// <summary>
        /// Group results that are physically near each other, within a radius.
        ///
        /// Snapping to a grid rather than clustering: a true clustering pass is order-dependent and
        /// would produce different groups on a re-run of identical data, which is the one thing
        /// grouping must never do. A grid is stable, and the seam it introduces is handled the same
        /// way the clash key handles it — by being coarse relative to what it separates.
        /// </summary>
        public static IGroupingRule ByProximity(double radiusMetres) => new ProximityRule(radiusMetres);

        private sealed class Simple : IGroupingRule
        {
            private readonly Func<ClashItem, string?> _key;
            public Simple(string name, Func<ClashItem, string?> key) { Name = name; _key = key; }

            public string Name { get; }

            public string? KeyFor(ClashItem item)
            {
                var v = _key(item);
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }

        private sealed class SameItemRule : IGroupingRule
        {
            public string Name => "Same item";

            public string? KeyFor(ClashItem item) =>
                item.Key.IsEmpty ? null : item.Key.PairId;
        }

        private sealed class ProximityRule : IGroupingRule
        {
            private readonly double _radius;
            public ProximityRule(double radius)
            {
                if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius), "radius must be positive");
                _radius = radius;
            }

            public string Name => "Within " + _radius.ToString("0.##", CultureInfo.InvariantCulture) + " m";

            public string? KeyFor(ClashItem item)
            {
                var x = Hash.Quantise(item.X, _radius);
                var y = Hash.Quantise(item.Y, _radius);
                var z = Hash.Quantise(item.Z, _radius);
                return x.ToString(CultureInfo.InvariantCulture) + ","
                     + y.ToString(CultureInfo.InvariantCulture) + ","
                     + z.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>One assign rule: a condition, and who it makes responsible.</summary>
    public sealed class AssignRule
    {
        /// <summary>Create an assign rule.</summary>
        public AssignRule(IClashPredicate when, string party, string? priority = null, int? dueOffsetDays = null)
        {
            When = when ?? throw new ArgumentNullException(nameof(when));
            Party = string.IsNullOrWhiteSpace(party) ? throw new ArgumentException("party is required", nameof(party)) : party;
            Priority = priority;
            DueOffsetDays = dueOffsetDays;
        }

        /// <summary>The condition.</summary>
        public IClashPredicate When { get; }

        /// <summary>Responsible party id.</summary>
        public string Party { get; }

        /// <summary>Default priority to apply.</summary>
        public string? Priority { get; }

        /// <summary>Days from now the due date should be set to.</summary>
        public int? DueOffsetDays { get; }
    }

    /// <summary>One suppression or flag rule.</summary>
    public sealed class FilterRule
    {
        /// <summary>Create a rule.</summary>
        public FilterRule(string name, IClashPredicate when, bool suppress)
        {
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("name is required", nameof(name)) : name;
            When = when ?? throw new ArgumentNullException(nameof(when));
            Suppress = suppress;
        }

        /// <summary>How the rule is named in the appendix.</summary>
        public string Name { get; }

        /// <summary>The condition.</summary>
        public IClashPredicate When { get; }

        /// <summary>True to remove from the board; false to keep but mark.</summary>
        public bool Suppress { get; }
    }
}
