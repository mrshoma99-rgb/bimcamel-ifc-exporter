using System;
using System.Collections.Generic;
using System.Linq;

namespace CamelWorks.Core.Sets
{
    /// <summary>
    /// A set, described as boolean algebra over conditions and other sets.
    ///
    /// The host's own search is one flat list of conditions with no nesting and no set arithmetic,
    /// which is why "everything on level 3 that is ductwork or pipework, except anything in the
    /// approved-deviations set" is normally built by hand, three times, and rebuilt every time the
    /// model changes. Here it is one expression, and <see cref="DnfCompiler"/> turns it into
    /// something the host can actually run.
    ///
    /// The tree is closed — every node is one of the six types in this file — so the compiler can
    /// switch over it exhaustively rather than guessing.
    /// </summary>
    public abstract class SetExpression
    {
        internal SetExpression()
        {
        }

        /// <summary>Everything in the model. The identity for AND.</summary>
        public static SetExpression Everything { get; } = new ConstantExpression(true);

        /// <summary>The empty set. The identity for OR.</summary>
        public static SetExpression Nothing { get; } = new ConstantExpression(false);

        /// <summary>Elements matching one condition.</summary>
        public static SetExpression Where(SetCondition condition) => new ConditionExpression(condition);

        /// <summary>Elements matching one condition, built inline.</summary>
        public static SetExpression Where(string category, string? property, SetOperator op, string? value = null) =>
            new ConditionExpression(new SetCondition(category, property, op, value));

        /// <summary>Members of a saved set, by its stable id.</summary>
        public static SetExpression InSet(string setId, string? displayName = null) =>
            new SetReferenceExpression(setId, displayName);

        /// <summary>Everything not in the inner expression.</summary>
        public static SetExpression Not(SetExpression inner) => new NotExpression(inner);

        /// <summary>Intersection. With no parts, this is <see cref="Everything"/>.</summary>
        public static SetExpression And(params SetExpression[] parts) =>
            Combine(parts, and: true);

        /// <summary>Union. With no parts, this is <see cref="Nothing"/>.</summary>
        public static SetExpression Or(params SetExpression[] parts) =>
            Combine(parts, and: false);

        /// <summary>Intersection.</summary>
        public static SetExpression operator &(SetExpression left, SetExpression right) => And(left, right);

        /// <summary>Union.</summary>
        public static SetExpression operator |(SetExpression left, SetExpression right) => Or(left, right);

        /// <summary>Complement.</summary>
        public static SetExpression operator !(SetExpression operand) => Not(operand);

        /// <summary>How the expression reads on screen and in a report.</summary>
        public abstract string Describe();

        /// <inheritdoc />
        public override string ToString() => Describe();

        private static SetExpression Combine(SetExpression[]? parts, bool and)
        {
            // An empty AND is Everything and an empty OR is Nothing — the algebraic identities, and
            // here they are also the useful ones: an unfinished builder row should not change the
            // meaning of the rows around it.
            if (parts == null || parts.Length == 0) return and ? Everything : Nothing;

            var flat = new List<SetExpression>();
            foreach (var part in parts)
            {
                if (part == null) throw new ArgumentException("a part is null", nameof(parts));

                // Flatten while building, so And(And(a, b), c) and And(a, b, c) are the same tree
                // and compile to the same plan.
                if (and && part is AndExpression a) flat.AddRange(a.Parts);
                else if (!and && part is OrExpression o) flat.AddRange(o.Parts);
                else flat.Add(part);
            }

            if (flat.Count == 1) return flat[0];
            return and ? new AndExpression(flat) : new OrExpression(flat);
        }
    }

    /// <summary>A leaf: one property test.</summary>
    public sealed class ConditionExpression : SetExpression
    {
        /// <summary>Create a leaf.</summary>
        public ConditionExpression(SetCondition condition) =>
            Condition = condition ?? throw new ArgumentNullException(nameof(condition));

        /// <summary>The test.</summary>
        public SetCondition Condition { get; }

        /// <inheritdoc />
        public override string Describe() => Condition.Describe();
    }

    /// <summary>A leaf: membership of another saved set.</summary>
    public sealed class SetReferenceExpression : SetExpression
    {
        /// <summary>Create a reference.</summary>
        public SetReferenceExpression(string setId, string? displayName = null)
        {
            SetId = string.IsNullOrWhiteSpace(setId)
                ? throw new ArgumentException("set id is required", nameof(setId))
                : setId;
            DisplayName = displayName;
        }

        /// <summary>The referenced set's stable id.</summary>
        public string SetId { get; }

        /// <summary>Its name, for display only. Never used for identity — sets get renamed.</summary>
        public string? DisplayName { get; }

        /// <inheritdoc />
        public override string Describe() => "in set '" + (DisplayName ?? SetId) + "'";
    }

    /// <summary>Complement.</summary>
    public sealed class NotExpression : SetExpression
    {
        /// <summary>Create a complement.</summary>
        public NotExpression(SetExpression inner) =>
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));

        /// <summary>What is being complemented.</summary>
        public SetExpression Inner { get; }

        /// <inheritdoc />
        public override string Describe() => "not (" + Inner.Describe() + ")";
    }

    /// <summary>Intersection of two or more parts.</summary>
    public sealed class AndExpression : SetExpression
    {
        internal AndExpression(IReadOnlyList<SetExpression> parts) => Parts = parts;

        /// <summary>The parts.</summary>
        public IReadOnlyList<SetExpression> Parts { get; }

        /// <inheritdoc />
        public override string Describe() =>
            "(" + string.Join(" and ", Parts.Select(p => p.Describe())) + ")";
    }

    /// <summary>Union of two or more parts.</summary>
    public sealed class OrExpression : SetExpression
    {
        internal OrExpression(IReadOnlyList<SetExpression> parts) => Parts = parts;

        /// <summary>The parts.</summary>
        public IReadOnlyList<SetExpression> Parts { get; }

        /// <inheritdoc />
        public override string Describe() =>
            "(" + string.Join(" or ", Parts.Select(p => p.Describe())) + ")";
    }

    /// <summary>Everything, or nothing.</summary>
    public sealed class ConstantExpression : SetExpression
    {
        internal ConstantExpression(bool value) => Value = value;

        /// <summary>True for everything, false for the empty set.</summary>
        public bool Value { get; }

        /// <inheritdoc />
        public override string Describe() => Value ? "everything" : "nothing";
    }
}
