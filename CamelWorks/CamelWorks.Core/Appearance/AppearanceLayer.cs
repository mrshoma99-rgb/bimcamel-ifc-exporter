using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;
using CamelWorks.Core.Sets;

namespace CamelWorks.Core.Appearance
{
    /// <summary>What a layer points at.</summary>
    public sealed class LayerTarget
    {
        private LayerTarget(IReadOnlyList<ElementKey> keys, SetExpression? expression, string description)
        {
            Keys = keys;
            Expression = expression;
            Description = description;
        }

        /// <summary>An explicit list of elements, when the layer names them.</summary>
        public IReadOnlyList<ElementKey> Keys { get; }

        /// <summary>A set expression, when the layer is defined by a rule instead.</summary>
        public SetExpression? Expression { get; }

        /// <summary>How the target reads on the layer row.</summary>
        public string Description { get; }

        /// <summary>
        /// True when the target is a rule rather than a list, and therefore re-resolves.
        ///
        /// This is the difference a coordinator actually feels. A layer built from a selection is
        /// a photograph: it keeps pointing at the elements that were there when it was made. A
        /// layer built from a search re-runs, so new ductwork appears in it without anybody having
        /// to remember to rebuild it — which is why the host's own "override by selection" quietly
        /// stops being true a week after it is set.
        /// </summary>
        public bool ReResolves => Expression != null;

        /// <summary>Target a fixed list of elements.</summary>
        public static LayerTarget Elements(IEnumerable<ElementKey> keys, string? description = null)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));

            var list = keys.Where(k => !k.IsEmpty).Distinct().ToList();
            return new LayerTarget(
                list,
                null,
                description ?? (list.Count.ToString(CultureInfo.InvariantCulture)
                                + (list.Count == 1 ? " element" : " elements")));
        }

        /// <summary>Target whatever a set expression matches, re-resolved each time.</summary>
        public static LayerTarget Set(SetExpression expression, string? description = null)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            return new LayerTarget(Array.Empty<ElementKey>(), expression, description ?? expression.Describe());
        }

        /// <summary>Target every element in the model — the base of an isolate.</summary>
        public static LayerTarget Everything() => Set(SetExpression.Everything, "everything");

        /// <inheritdoc />
        public override string ToString() => Description;
    }

    /// <summary>One appearance property a layer can decide.</summary>
    public enum AppearanceProperty
    {
        /// <summary>Shown or hidden.</summary>
        Visibility = 0,

        /// <summary>Colour override.</summary>
        Colour = 1,

        /// <summary>Transparency override.</summary>
        Transparency = 2,
    }

    /// <summary>
    /// One layer: what it points at, and what it decides about it.
    ///
    /// <b>Each property is decided independently, and null means "does not decide".</b> A layer
    /// that only sets a colour leaves visibility to whatever is underneath it. Without that, a
    /// stack is not a stack — every layer would be a full replacement of the one below, and the
    /// second layer you added would undo the first.
    /// </summary>
    public sealed class AppearanceLayer
    {
        /// <summary>Create a layer.</summary>
        /// <param name="id">Stable id. Referenced by explanations and by the saved stack.</param>
        /// <param name="name">How it reads in the layers panel.</param>
        /// <param name="target">What it points at.</param>
        public AppearanceLayer(string id, string name, LayerTarget target)
        {
            Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("id is required", nameof(id)) : id;
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("name is required", nameof(name)) : name;
            Target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>Stable id.</summary>
        public string Id { get; }

        /// <summary>Display name.</summary>
        public string Name { get; }

        /// <summary>What the layer points at.</summary>
        public LayerTarget Target { get; }

        /// <summary>
        /// Why this layer exists, in the author's words.
        ///
        /// The single thing the host loses the instant you hide something. A federation handed over
        /// with four hundred hidden elements and no note is a federation nobody dares un-hide.
        /// </summary>
        public string? Note { get; set; }

        /// <summary>Off layers stay in the stack and decide nothing.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Shown or hidden, or null to leave it to the layers below.</summary>
        public bool? Visible { get; set; }

        /// <summary>Colour, or null to leave it to the layers below.</summary>
        public Colour? Colour { get; set; }

        /// <summary>Transparency from 0 to 1, or null to leave it to the layers below.</summary>
        public double? Transparency { get; set; }

        /// <summary>True when the layer decides nothing at all, and so can only ever be noise.</summary>
        public bool IsEmpty => Visible == null && Colour == null && Transparency == null;

        /// <summary>What this layer decides about the given property, as display text, or null.</summary>
        public string? ValueOf(AppearanceProperty property) => property switch
        {
            AppearanceProperty.Visibility => Visible == null ? null : (Visible.Value ? "shown" : "hidden"),
            AppearanceProperty.Colour => Colour?.ToString(),
            AppearanceProperty.Transparency => Transparency?.ToString("0.##", CultureInfo.InvariantCulture),
            _ => null,
        };

        /// <summary>How the layer reads on one line.</summary>
        public override string ToString()
        {
            var effects = new List<string>();
            if (Visible != null) effects.Add(Visible.Value ? "show" : "hide");
            if (Colour != null) effects.Add(Colour.Value.ToString());
            if (Transparency != null) effects.Add(Transparency.Value.ToString("0%", CultureInfo.InvariantCulture) + " transparent");

            var s = Name + " — " + Target.Description;
            if (effects.Count > 0) s += " · " + string.Join(", ", effects);
            if (!IsEnabled) s += " (off)";
            return s;
        }
    }

    /// <summary>
    /// Turns a layer's target into the elements it currently covers.
    ///
    /// A seam rather than a method on the layer, because resolving a set expression means running
    /// searches against the host, and the fold below has to stay a pure function to be worth
    /// trusting. The fake implementation in the test project is what lets the whole precedence
    /// model be proved without opening a model.
    /// </summary>
    public interface ILayerResolver
    {
        /// <summary>The elements a target currently covers.</summary>
        IReadOnlyCollection<ElementKey> Resolve(LayerTarget target);
    }
}
