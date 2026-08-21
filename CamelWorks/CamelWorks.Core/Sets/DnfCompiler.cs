using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Sets
{
    /// <summary>
    /// Turns a set expression into something the host can run.
    ///
    /// Three passes, in order:
    ///
    /// 1. <b>Negation normal form.</b> Push every NOT down to the leaves by De Morgan, so the tree
    ///    below is nothing but ANDs, ORs and signed literals. Constants fold away here.
    /// 2. <b>Distribution.</b> AND over OR, until the tree is an OR of ANDs. This is the pass that
    ///    can explode, so it is the pass that counts.
    /// 3. <b>Simplification.</b> Drop duplicate literals, drop clauses that contradict themselves,
    ///    drop clauses another clause already covers, and order everything canonically.
    ///
    /// The third pass earns its place. Set expressions are built by dragging rows around a UI, and
    /// they accumulate redundancy: the same condition twice, a term ANDed with its own negation, a
    /// clause that is strictly narrower than one beside it. Without simplification a plan runs four
    /// host searches where one would do, on a model where each takes seconds.
    ///
    /// What it deliberately does NOT do is detect a tautology spread across clauses. <c>a or not a</c>
    /// compiles to two clauses — search for a, and separately take everything and subtract a — whose
    /// union is indeed everything. Recognising that in general is the co-NP-hard half of the problem,
    /// and paying for it would slow down every ordinary set to flatter a set nobody writes. The plan
    /// is correct; it just runs one search more than a person would.
    /// </summary>
    public static class DnfCompiler
    {
        /// <summary>The default clause budget.</summary>
        public const int DefaultMaxClauses = 512;

        /// <summary>Compile an expression into an executable plan.</summary>
        /// <param name="expression">The set, as boolean algebra.</param>
        /// <param name="maxClauses">The clause budget; distribution is exponential in the worst case.</param>
        /// <exception cref="SetExpressionTooComplexException">The expression exceeds the budget.</exception>
        public static SearchPlan Compile(SetExpression expression, int maxClauses = DefaultMaxClauses)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));
            if (maxClauses < 1) throw new ArgumentOutOfRangeException(nameof(maxClauses), "the budget must be at least one clause");

            var nnf = ToNegationNormalForm(expression, negate: false);
            var clauses = Distribute(nnf, maxClauses);
            var warnings = new List<string>();

            // An empty clause is an empty AND, which is "everything". One of those in a union makes
            // the whole union everything, however many other clauses there are.
            if (clauses.Any(c => c.Count == 0))
            {
                warnings.Add("this set matches everything in the model — one branch has no conditions left after simplification");
                return new SearchPlan(Array.Empty<SearchClause>(), true, warnings);
            }

            clauses = Simplify(clauses);

            if (clauses.Count == 0)
            {
                warnings.Add("this set matches nothing — every branch contains a condition together with its own negation");
                return new SearchPlan(Array.Empty<SearchClause>(), false, warnings);
            }

            var emitted = clauses.Select(Emit).ToList();

            foreach (var clause in emitted.Where(c => c.StartsFromEverything))
                warnings.Add("'" + clause.Describe() + "' has nothing positive to search for, so it starts from every element in the model");

            if (emitted.Count > 32)
                warnings.Add("this set compiles to " + emitted.Count.ToString(CultureInfo.InvariantCulture)
                             + " separate searches; saving the common part as its own set would cut that down");

            return new SearchPlan(emitted, false, warnings);
        }

        // ---------------------------------------------------------------------------------
        // 1. Negation normal form
        // ---------------------------------------------------------------------------------

        private static Node ToNegationNormalForm(SetExpression e, bool negate) => e switch
        {
            ConditionExpression c => new LiteralNode(new Literal(negate, c.Condition, null)),
            SetReferenceExpression r => new LiteralNode(new Literal(negate, null, r.SetId)),
            ConstantExpression k => new ConstantNode(negate ? !k.Value : k.Value),

            // Double negation collapses here rather than needing its own pass.
            NotExpression n => ToNegationNormalForm(n.Inner, !negate),

            // De Morgan. Under a negation an AND becomes an OR and vice versa, which is the whole
            // reason NOT can be pushed to the leaves at all.
            AndExpression a => Junction(a.Parts, and: !negate, negate),
            OrExpression o => Junction(o.Parts, and: negate, negate),

            _ => throw new NotSupportedException("unknown expression node: " + e.GetType().Name),
        };

        private static Node Junction(IReadOnlyList<SetExpression> parts, bool and, bool negate)
        {
            var built = new List<Node>();

            foreach (var part in parts)
            {
                var node = ToNegationNormalForm(part, negate);

                if (node is ConstantNode k)
                {
                    // An absorbing constant ends it: false in an AND, true in an OR.
                    if (k.Value != and) return new ConstantNode(k.Value);
                    continue;   // the identity: true in an AND, false in an OR. Drop it.
                }

                // Flatten same-kind children so the distribution pass sees the widest possible
                // junction and does not distribute over a nesting that was only syntax.
                if (and && node is AndNode nestedAnd) built.AddRange(nestedAnd.Parts);
                else if (!and && node is OrNode nestedOr) built.AddRange(nestedOr.Parts);
                else built.Add(node);
            }

            if (built.Count == 0) return new ConstantNode(and);
            if (built.Count == 1) return built[0];
            return and ? new AndNode(built) : (Node)new OrNode(built);
        }

        // ---------------------------------------------------------------------------------
        // 2. Distribution
        // ---------------------------------------------------------------------------------

        private static List<List<Literal>> Distribute(Node node, int maxClauses)
        {
            switch (node)
            {
                case LiteralNode l:
                    return new List<List<Literal>> { new List<Literal> { l.Value } };

                // True is one clause with no conditions; false is no clauses at all. Both are the
                // algebra, and both are shapes the caller has to handle anyway.
                case ConstantNode k:
                    return k.Value
                        ? new List<List<Literal>> { new List<Literal>() }
                        : new List<List<Literal>>();

                case OrNode o:
                {
                    var all = new List<List<Literal>>();
                    foreach (var part in o.Parts)
                    {
                        all.AddRange(Distribute(part, maxClauses));
                        if (all.Count > maxClauses) throw new SetExpressionTooComplexException(all.Count, maxClauses);
                    }

                    return all;
                }

                case AndNode a:
                {
                    var product = new List<List<Literal>> { new List<Literal>() };

                    foreach (var part in a.Parts)
                    {
                        var next = Distribute(part, maxClauses);

                        // The multiplication that makes this exponential. Checked before it is
                        // spent, not after: the point of a budget is to refuse the work, and
                        // allocating 2^20 lists first would defeat it.
                        var size = (long)product.Count * next.Count;
                        if (size > maxClauses)
                            throw new SetExpressionTooComplexException((int)Math.Min(size, int.MaxValue), maxClauses);

                        var combined = new List<List<Literal>>((int)size);
                        foreach (var left in product)
                            foreach (var right in next)
                            {
                                var merged = new List<Literal>(left.Count + right.Count);
                                merged.AddRange(left);
                                merged.AddRange(right);
                                combined.Add(merged);
                            }

                        product = combined;
                    }

                    return product;
                }

                default:
                    throw new NotSupportedException("unknown node: " + node.GetType().Name);
            }
        }

        // ---------------------------------------------------------------------------------
        // 3. Simplification
        // ---------------------------------------------------------------------------------

        private static List<List<Literal>> Simplify(List<List<Literal>> clauses)
        {
            var cleaned = new List<List<Literal>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var clause in clauses)
            {
                var byKey = new Dictionary<string, Literal>(StringComparer.Ordinal);
                var contradicts = false;

                foreach (var literal in clause)
                {
                    if (byKey.TryGetValue(literal.Key, out var existing))
                    {
                        // The same condition asserted both ways. The clause is empty by
                        // construction; no search needs to run to find that out.
                        if (existing.Negated != literal.Negated) { contradicts = true; break; }
                        continue;   // idempotence: the same literal twice is once
                    }

                    byKey[literal.Key] = literal;
                }

                if (contradicts) continue;

                // A clause cannot become empty here — an empty one was already handled as
                // "everything" before simplification ran — so every survivor has a literal.
                var ordered = byKey.Values.OrderBy(l => l.Signed, StringComparer.Ordinal).ToList();

                if (seen.Add(Identify(ordered))) cleaned.Add(ordered);
            }

            // Absorption: a ∨ (a ∧ b) = a. A clause whose literals are a superset of another
            // clause's contributes nothing the narrower one has not already matched.
            var keySets = cleaned.Select(c => new HashSet<string>(c.Select(l => l.Signed), StringComparer.Ordinal)).ToList();
            var kept = new List<List<Literal>>();

            for (var i = 0; i < cleaned.Count; i++)
            {
                var absorbed = false;

                for (var j = 0; j < cleaned.Count && !absorbed; j++)
                {
                    if (i == j) continue;
                    if (keySets[j].Count > keySets[i].Count) continue;

                    // Equal-sized sets cannot absorb each other — duplicates were already removed,
                    // so two clauses of the same size differ somewhere and both must stay.
                    if (keySets[j].Count == keySets[i].Count) continue;

                    if (keySets[j].IsSubsetOf(keySets[i])) absorbed = true;
                }

                if (!absorbed) kept.Add(cleaned[i]);
            }

            return kept.OrderBy(Identify, StringComparer.Ordinal).ToList();
        }

        // No separator: every Signed value is self-delimiting, so concatenation is unambiguous.
        private static string Identify(IEnumerable<Literal> clause) =>
            string.Concat(clause.Select(l => l.Signed));

        private static SearchClause Emit(List<Literal> clause)
        {
            var include = new List<SetCondition>();
            var exclude = new List<SetCondition>();
            var includeSets = new List<string>();
            var excludeSets = new List<string>();

            foreach (var literal in clause)
            {
                if (literal.Condition != null)
                {
                    if (literal.Negated) exclude.Add(literal.Condition); else include.Add(literal.Condition);
                }
                else if (literal.SetId != null)
                {
                    if (literal.Negated) excludeSets.Add(literal.SetId); else includeSets.Add(literal.SetId);
                }
            }

            return new SearchClause(include, exclude, includeSets, excludeSets);
        }

        // ---------------------------------------------------------------------------------

        private readonly struct Literal
        {
            internal Literal(bool negated, SetCondition? condition, string? setId)
            {
                Negated = negated;
                Condition = condition;
                SetId = setId;
                // Both forms are self-delimiting: a condition's Canonical is length-prefixed,
                // and the set id is prefixed here. That is what lets a clause be identified by
                // concatenating its literals with no separator between them — a separator would
                // collide the first time a property value contained it.
                Key = condition != null
                    ? "c" + condition.Canonical
                    : "s" + (setId?.Length ?? -1).ToString(CultureInfo.InvariantCulture) + ":" + setId;
            }

            internal bool Negated { get; }

            internal SetCondition? Condition { get; }

            internal string? SetId { get; }

            /// <summary>Identity ignoring the sign — two literals with one key are the same test.</summary>
            internal string Key { get; }

            /// <summary>Identity including the sign.</summary>
            internal string Signed => (Negated ? "!" : "+") + Key;
        }

        private abstract class Node
        {
        }

        private sealed class LiteralNode : Node
        {
            internal LiteralNode(Literal value) => Value = value;

            internal Literal Value { get; }
        }

        private sealed class ConstantNode : Node
        {
            internal ConstantNode(bool value) => Value = value;

            internal bool Value { get; }
        }

        private sealed class AndNode : Node
        {
            internal AndNode(IReadOnlyList<Node> parts) => Parts = parts;

            internal IReadOnlyList<Node> Parts { get; }
        }

        private sealed class OrNode : Node
        {
            internal OrNode(IReadOnlyList<Node> parts) => Parts = parts;

            internal IReadOnlyList<Node> Parts { get; }
        }
    }
}
