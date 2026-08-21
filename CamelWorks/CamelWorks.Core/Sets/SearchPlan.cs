using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Sets
{
    /// <summary>
    /// One conjunct of the compiled plan: a single host search, narrowed by saved sets, with
    /// subtractions applied afterwards.
    ///
    /// The order inside a clause is fixed and matters. Every <see cref="Include"/> condition goes
    /// into ONE host search, because the host ANDs the conditions of a search together for free.
    /// Then the <see cref="IncludeSets"/> are intersected. Only then are the subtractions applied.
    /// Subtracting before intersecting gives a different, wrong answer whenever a subtracted
    /// element would have been excluded by the intersection anyway.
    /// </summary>
    public sealed class SearchClause
    {
        internal SearchClause(IReadOnlyList<SetCondition> include, IReadOnlyList<SetCondition> exclude,
                              IReadOnlyList<string> includeSets, IReadOnlyList<string> excludeSets)
        {
            Include = include; Exclude = exclude; IncludeSets = includeSets; ExcludeSets = excludeSets;
        }

        /// <summary>Conditions the host ANDs into one search.</summary>
        public IReadOnlyList<SetCondition> Include { get; }

        /// <summary>
        /// Conditions whose matches are removed afterwards — one host search each.
        ///
        /// A negation is always compiled this way, never into a negated operator, because those
        /// two are not the same set: a host condition on an absent property matches nothing, so
        /// <c>Fire Rating != 60</c> silently drops every element with no fire rating, while
        /// <c>not (Fire Rating = 60)</c> must keep them.
        /// </summary>
        public IReadOnlyList<SetCondition> Exclude { get; }

        /// <summary>Saved sets intersected with the search result.</summary>
        public IReadOnlyList<string> IncludeSets { get; }

        /// <summary>Saved sets subtracted from it.</summary>
        public IReadOnlyList<string> ExcludeSets { get; }

        /// <summary>
        /// True when the clause has nothing positive to start from, so it must begin with every
        /// element in the model and subtract. Correct, and expensive — <c>not (X)</c> on its own
        /// is exactly this shape, and on a large federation the host will feel it.
        /// </summary>
        public bool StartsFromEverything => Include.Count == 0 && IncludeSets.Count == 0;

        /// <summary>How many searches the host runs for this clause.</summary>
        public int NativeSearches => (Include.Count > 0 ? 1 : 0) + Exclude.Count;

        /// <summary>How the clause reads.</summary>
        public string Describe()
        {
            var positives = Include.Select(c => c.Describe())
                .Concat(IncludeSets.Select(s => "in set '" + s + "'"))
                .ToList();

            var negatives = Exclude.Select(c => c.Describe())
                .Concat(ExcludeSets.Select(s => "in set '" + s + "'"))
                .ToList();

            var s = positives.Count == 0 ? "everything" : string.Join(" and ", positives);
            if (negatives.Count > 0) s += " except " + string.Join(" or ", negatives);
            return s;
        }

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>
    /// The compiled form of a set expression: a union of clauses the host can execute.
    ///
    /// Disjunctive normal form is not an arbitrary choice of shape. It is the only shape the host
    /// can run: one search ANDs its conditions, and the sole way to get an OR is to run more than
    /// one search and union the results. So an expression must become an OR of ANDs or it cannot
    /// become anything.
    /// </summary>
    public sealed class SearchPlan
    {
        internal SearchPlan(IReadOnlyList<SearchClause> clauses, bool matchesEverything,
                            IReadOnlyList<string> warnings)
        {
            Clauses = clauses;
            MatchesEverything = matchesEverything;
            Warnings = warnings;
        }

        /// <summary>The clauses, unioned.</summary>
        public IReadOnlyList<SearchClause> Clauses { get; }

        /// <summary>True when the expression reduced to everything in the model.</summary>
        public bool MatchesEverything { get; }

        /// <summary>
        /// True when the expression reduced to the empty set — usually because it contradicts
        /// itself. Distinct from "the search found nothing", which nobody can know until it runs.
        /// </summary>
        public bool MatchesNothing => !MatchesEverything && Clauses.Count == 0;

        /// <summary>
        /// What the user should know before running it: contradictions, tautologies, and clauses
        /// that have to walk the whole model. Surfaced rather than silently endured.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Total host searches across every clause.</summary>
        public int NativeSearches => Clauses.Sum(c => c.NativeSearches);

        /// <summary>The one-line readout.</summary>
        public string Explain()
        {
            if (MatchesEverything) return "matches everything in the model";
            if (MatchesNothing) return "matches nothing — the expression contradicts itself";

            var s = Clauses.Count.ToString(CultureInfo.InvariantCulture)
                  + (Clauses.Count == 1 ? " clause" : " clauses") + " · "
                  + NativeSearches.ToString(CultureInfo.InvariantCulture)
                  + (NativeSearches == 1 ? " search" : " searches");

            var subtractions = Clauses.Sum(c => c.Exclude.Count + c.ExcludeSets.Count);
            if (subtractions > 0)
                s += " · " + subtractions.ToString(CultureInfo.InvariantCulture) + " subtracted";

            var wholeModel = Clauses.Count(c => c.StartsFromEverything);
            if (wholeModel > 0)
                s += " · " + wholeModel.ToString(CultureInfo.InvariantCulture) + " starting from the whole model";

            return s;
        }

        /// <inheritdoc />
        public override string ToString() => Explain();
    }

    /// <summary>
    /// Thrown when an expression cannot be put into disjunctive normal form within the clause
    /// budget.
    ///
    /// Distribution is exponential in the worst case: n disjunctions of two terms each become
    /// 2^n clauses. A dozen ORs chained with ANDs is enough to hang the host, so the compiler
    /// refuses instead — with the number it reached, so the message can say something useful
    /// rather than "too complex".
    /// </summary>
    public sealed class SetExpressionTooComplexException : Exception
    {
        /// <summary>Create the exception.</summary>
        public SetExpressionTooComplexException(int reached, int limit)
            : base("this set expands to at least " + reached.ToString(CultureInfo.InvariantCulture)
                   + " clauses, over the limit of " + limit.ToString(CultureInfo.InvariantCulture)
                   + "; split it into saved sets and combine those instead")
        {
            Reached = reached;
            Limit = limit;
        }

        /// <summary>Create the exception with a message.</summary>
        public SetExpressionTooComplexException(string message) : base(message)
        {
        }

        /// <summary>Create the exception with a message and an inner exception.</summary>
        public SetExpressionTooComplexException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>Create the exception.</summary>
        public SetExpressionTooComplexException()
            : base("this set is too complex to compile")
        {
        }

        /// <summary>How many clauses the compiler had reached when it stopped.</summary>
        public int Reached { get; }

        /// <summary>The limit it was working to.</summary>
        public int Limit { get; }
    }
}
