using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Navisworks.Api;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Sets;

namespace CamelWorks.Nav
{
    /// <summary>What running a compiled set expression produced.</summary>
    public sealed class SearchOutcome
    {
        internal SearchOutcome(IReadOnlyList<ElementKey> keys, ModelItemCollection items, bool isLive,
                               IReadOnlyList<string> notes)
        {
            Keys = keys; Items = items; IsLive = isLive; Notes = notes;
        }

        /// <summary>The elements it matched.</summary>
        public IReadOnlyList<ElementKey> Keys { get; }

        /// <summary>The same elements as the host holds them.</summary>
        public ModelItemCollection Items { get; }

        /// <summary>
        /// True when the whole expression fitted into one host search, and can therefore be saved
        /// as a search set that re-runs.
        ///
        /// The distinction is worth surfacing rather than hiding. A live search set picks up
        /// tomorrow's ductwork; a fixed selection set does not, and a coordinator who thinks they
        /// have the first while holding the second finds out three weeks later.
        /// </summary>
        public bool IsLive { get; }

        /// <summary>Anything the user should know about how it ran.</summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>How many elements matched.</summary>
        public int Count => Keys.Count;

        /// <inheritdoc />
        public override string ToString() =>
            Count.ToString(CultureInfo.InvariantCulture) + (Count == 1 ? " element" : " elements")
            + (IsLive ? ", as a live search" : ", fixed");
    }

    /// <summary>
    /// Runs a compiled <see cref="SearchPlan"/> against the host, and publishes it as a native set.
    ///
    /// The compiler above produces disjunctive normal form because that is the only shape the host
    /// can run: one search ANDs its conditions, and the only way to get an OR is more than one
    /// search. This class is where that shape meets the API — union the clauses, intersect the
    /// included sets, and only then subtract, because subtracting first gives a different and wrong
    /// answer whenever a subtracted element would have been intersected away anyway.
    ///
    /// <b>String comparisons ignore case.</b> The host would honour case if asked; coordination
    /// data does not, and a set that misses "DUCT" because the rule said "Duct" is a bug report
    /// waiting to be filed.
    /// </summary>
    public sealed class NavSearch
    {
        private readonly NavDocument _document;

        /// <summary>Create a runner.</summary>
        /// <param name="document">The document to search.</param>
        public NavSearch(NavDocument document) =>
            _document = document ?? throw new ArgumentNullException(nameof(document));

        /// <summary>Run a plan.</summary>
        /// <param name="plan">The compiled expression.</param>
        public SearchOutcome Run(SearchPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var notes = new List<string>(plan.Warnings);
            var result = new ModelItemCollection();

            if (plan.MatchesNothing)
                return new SearchOutcome(Array.Empty<ElementKey>(), result, false, notes);

            if (plan.MatchesEverything)
            {
                foreach (var item in Everything()) result.Add(item);
                return new SearchOutcome(KeysOf(result), result, true, notes);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var clause in plan.Clauses)
            {
                foreach (var item in RunClause(clause, notes))
                {
                    // Union across clauses. Two clauses of a compiled OR routinely overlap, and the
                    // host collection would happily hold the same element twice.
                    var key = NavKeys.Of(item).ToString();
                    if (key.Length > 0 && !seen.Add(key)) continue;

                    result.Add(item);
                }
            }

            var live = plan.Clauses.Count == 1 && IsNative(plan.Clauses[0]);

            if (!live)
                notes.Add("This expression needs more than one host search, so it can only be saved "
                          + "as a fixed selection set — it will not pick up elements added later.");

            return new SearchOutcome(KeysOf(result), result, live, notes);
        }

        /// <summary>
        /// Save a plan as a native set the host shows in its own panel.
        /// </summary>
        /// <param name="name">What to call it.</param>
        /// <param name="plan">The compiled expression.</param>
        public SearchOutcome Publish(string name, SearchPlan plan)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));

            var outcome = Run(plan);

            var set = outcome.IsLive && plan.Clauses.Count == 1
                ? new SelectionSet(NativeSearch(plan.Clauses[0])!)
                : new SelectionSet(outcome.Items);

            set.DisplayName = name;

            // Replace rather than duplicate: publishing the same set twice is what a user does when
            // they change the rule, and two sets with one name is the outcome nobody wants.
            var existing = _document.Sets()
                .FirstOrDefault(s => string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));

            if (existing != null) _document.Document.SelectionSets.Remove(existing);

            _document.Document.SelectionSets.AddCopy(set);
            return outcome;
        }

        private IEnumerable<ModelItem> RunClause(SearchClause clause, ICollection<string> notes)
        {
            ModelItemCollection working;

            if (clause.Include.Count > 0)
            {
                working = Execute(Build(clause.Include));
            }
            else if (clause.IncludeSets.Count > 0)
            {
                working = new ModelItemCollection();
                foreach (var item in ItemsOfSet(clause.IncludeSets[0])) working.Add(item);
            }
            else
            {
                notes.Add("One part of this expression starts from every element in the model, "
                          + "because it only says what to leave out. On a large federation that is slow.");

                working = new ModelItemCollection();
                foreach (var item in Everything()) working.Add(item);
            }

            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModelItem item in working) keep.Add(NavKeys.Of(item).ToString());

            // Intersections first. See the type remarks for why this cannot be reordered.
            var skipFirstSet = clause.Include.Count == 0;

            for (var i = skipFirstSet ? 1 : 0; i < clause.IncludeSets.Count; i++)
            {
                var members = new HashSet<string>(
                    ItemsOfSet(clause.IncludeSets[i]).Select(x => NavKeys.Of(x).ToString()), StringComparer.Ordinal);

                keep.IntersectWith(members);
            }

            foreach (var condition in clause.Exclude)
                foreach (var item in Execute(Build(new[] { condition })))
                    keep.Remove(NavKeys.Of(item).ToString());

            foreach (var name in clause.ExcludeSets)
                foreach (var item in ItemsOfSet(name))
                    keep.Remove(NavKeys.Of(item).ToString());

            foreach (ModelItem item in working)
                if (keep.Contains(NavKeys.Of(item).ToString()))
                    yield return item;
        }

        private ModelItemCollection Execute(Search search)
        {
            try
            {
                return search.FindAll(_document.Document, false);
            }
            catch (Exception)
            {
                // A condition the host rejects at run time — a property category that does not
                // exist in this federation, most often. An empty result is the right answer.
                return new ModelItemCollection();
            }
        }

        private Search Build(IEnumerable<SetCondition> conditions)
        {
            var search = new Search();
            search.Selection.SelectAll();
            search.Locations = SearchLocations.DescendantsAndSelf;

            foreach (var condition in conditions)
            {
                var native = Translate(condition);
                if (native != null) search.SearchConditions.Add(native);
            }

            return search;
        }

        private Search? NativeSearch(SearchClause clause) =>
            IsNative(clause) ? Build(clause.Include) : null;

        private static bool IsNative(SearchClause clause) =>
            clause.Include.Count > 0 && clause.Exclude.Count == 0
            && clause.IncludeSets.Count == 0 && clause.ExcludeSets.Count == 0;

        /// <summary>
        /// One condition, as the host expresses it.
        ///
        /// The numeric comparisons go through <c>CompareWith</c> with a real double rather than a
        /// display string, because the host compares display strings lexically — which puts "9"
        /// after "10" and makes every width filter quietly wrong.
        /// </summary>
        private static SearchCondition? Translate(SetCondition condition)
        {
            if (condition.Operator == SetOperator.HasCategory)
                return SearchCondition.HasCategoryByDisplayName(condition.Category);

            var property = condition.Property;
            if (string.IsNullOrWhiteSpace(property)) return null;

            var has = SearchCondition.HasPropertyByDisplayName(condition.Category, property!);
            var value = condition.Value ?? string.Empty;

            switch (condition.Operator)
            {
                case SetOperator.Defined:
                    return has;

                case SetOperator.Equals:
                    return has.EqualValue(VariantData.FromDisplayString(value)).IgnoreStringValueCase();

                case SetOperator.Contains:
                    return has.DisplayStringContains(value).IgnoreStringValueCase();

                case SetOperator.StartsWith:
                    return has.DisplayStringWildcard(Escape(value) + "*").IgnoreStringValueCase();

                case SetOperator.EndsWith:
                    return has.DisplayStringWildcard("*" + Escape(value)).IgnoreStringValueCase();

                case SetOperator.WildcardMatch:
                    return has.DisplayStringWildcard(value).IgnoreStringValueCase();

                case SetOperator.GreaterThan:
                    return Numeric(has, condition, SearchConditionComparison.NumericGreaterThan);

                case SetOperator.GreaterThanOrEqual:
                    return Numeric(has, condition, SearchConditionComparison.NumericGreaterThanOrEqual);

                case SetOperator.LessThan:
                    return Numeric(has, condition, SearchConditionComparison.NumericLessThan);

                case SetOperator.LessThanOrEqual:
                    return Numeric(has, condition, SearchConditionComparison.NumericLessThanOrEqual);

                default:
                    return null;
            }
        }

        private static SearchCondition Numeric(SearchCondition has, SetCondition condition,
                                               SearchConditionComparison comparison) =>
            has.CompareWith(comparison, VariantData.FromDouble(condition.NumericValue));

        // The host's wildcard syntax has no escape, so a literal * or ? in a StartsWith value would
        // silently widen the match. Replacing them with ? at least keeps the length right and the
        // match narrow, and it is the only option the API leaves.
        private static string Escape(string value) => value.Replace('*', '?');

        private IEnumerable<ModelItem> Everything() =>
            _document.Traverse(CamelWorks.Core.Abstractions.TraversalScope.WholeDocument)
                     .OfType<NavModelItem>()
                     .Select(i => i.Item);

        private IEnumerable<ModelItem> ItemsOfSet(string name)
        {
            var set = _document.Sets()
                .FirstOrDefault(s => string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));

            if (set == null) return Enumerable.Empty<ModelItem>();

            try
            {
                return set.Search != null
                    ? set.Search.FindAll(_document.Document, false).Cast<ModelItem>()
                    : set.GetSelectedItems().Cast<ModelItem>();
            }
            catch (Exception)
            {
                return Enumerable.Empty<ModelItem>();
            }
        }

        private static IReadOnlyList<ElementKey> KeysOf(ModelItemCollection items)
        {
            var keys = new List<ElementKey>(items.Count);

            foreach (ModelItem item in items)
            {
                var key = NavKeys.Of(item);
                if (!key.IsEmpty) keys.Add(key);
            }

            return keys;
        }
    }
}
