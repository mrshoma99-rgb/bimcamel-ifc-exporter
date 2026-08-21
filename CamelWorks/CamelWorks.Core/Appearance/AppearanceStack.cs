using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Appearance
{
    /// <summary>One layer's decision about one property of one element, and whether it survived.</summary>
    public sealed class AppearanceDecision
    {
        internal AppearanceDecision(string layerId, string layerName, AppearanceProperty property,
                                    string value, bool survived, string? overruledBy)
        {
            LayerId = layerId; LayerName = layerName; Property = property;
            Value = value; Survived = survived; OverruledBy = overruledBy;
        }

        /// <summary>The layer that decided.</summary>
        public string LayerId { get; }

        /// <summary>Its name, for display.</summary>
        public string LayerName { get; }

        /// <summary>What it decided about.</summary>
        public AppearanceProperty Property { get; }

        /// <summary>What it decided, as display text.</summary>
        public string Value { get; }

        /// <summary>True when this is the decision the element is actually in.</summary>
        public bool Survived { get; }

        /// <summary>The layer that overruled it, when it did not survive.</summary>
        public string? OverruledBy { get; }

        /// <inheritdoc />
        public override string ToString() =>
            LayerName + ": " + Property.ToString().ToLowerInvariant() + " = " + Value
            + (Survived ? string.Empty : " (overruled by " + OverruledBy + ")");
    }

    /// <summary>Where one element ended up, and every layer that had a view on it.</summary>
    public sealed class ElementAppearance
    {
        internal ElementAppearance(ElementKey key, bool visible, Colour? colour, double? transparency,
                                   IReadOnlyList<AppearanceDecision> decisions)
        {
            Key = key; Visible = visible; Colour = colour; Transparency = transparency; Decisions = decisions;
        }

        /// <summary>The element.</summary>
        public ElementKey Key { get; }

        /// <summary>Shown or hidden. True unless a layer decided otherwise.</summary>
        public bool Visible { get; }

        /// <summary>Colour, or null when no layer sets one.</summary>
        public Colour? Colour { get; }

        /// <summary>Transparency, or null when no layer sets one.</summary>
        public double? Transparency { get; }

        /// <summary>
        /// Every layer that had a view on this element, winners and losers alike, top of the stack
        /// first.
        ///
        /// The losers are the point. "Why is this thing hidden" is answerable in the host only by
        /// undoing things until it reappears; here the answer is a row, and so is the fact that
        /// three other layers wanted it shown.
        /// </summary>
        public IReadOnlyList<AppearanceDecision> Decisions { get; }

        /// <summary>True when no layer decides anything about this element.</summary>
        public bool IsUntouched => Decisions.Count == 0;

        /// <summary>The one-line answer to "why does this look like that".</summary>
        public string Explain()
        {
            var winners = Decisions.Where(d => d.Survived).ToList();
            if (winners.Count == 0) return "no layer affects this element";

            var s = string.Join(", ", winners.Select(d => d.Property.ToString().ToLowerInvariant()
                                                          + " " + d.Value + " from '" + d.LayerName + "'"));

            var overruled = Decisions.Count - winners.Count;
            if (overruled > 0)
                s += " · " + overruled.ToString(CultureInfo.InvariantCulture)
                   + (overruled == 1 ? " other layer overruled" : " other layers overruled");

            return s;
        }
    }

    /// <summary>What one layer is actually doing, once the stack above it has had its say.</summary>
    public sealed class LayerReport
    {
        internal LayerReport(AppearanceLayer layer, int covers, int effective)
        {
            Layer = layer; Covers = covers; Effective = effective;
        }

        /// <summary>The layer.</summary>
        public AppearanceLayer Layer { get; }

        /// <summary>How many elements it currently covers.</summary>
        public int Covers { get; }

        /// <summary>On how many of those at least one of its decisions survived.</summary>
        public int Effective { get; }

        /// <summary>On how many it was completely overruled.</summary>
        public int Overruled => Covers - Effective;

        /// <summary>
        /// True when the layer covers elements but changes nothing about any of them, because
        /// layers above it decide every property it decides.
        ///
        /// Worth surfacing on its own row. A stack that has been worked on for a month accumulates
        /// these, and a coordinator deleting layers needs to know which ones are already doing
        /// nothing — otherwise the only safe move is to delete none of them.
        /// </summary>
        public bool IsDead => Layer.IsEnabled && Covers > 0 && Effective == 0;

        /// <inheritdoc />
        public override string ToString()
        {
            if (!Layer.IsEnabled) return Layer.Name + " — off";
            if (Layer.IsEmpty) return Layer.Name + " — decides nothing";
            if (Covers == 0) return Layer.Name + " — matches nothing right now";
            if (IsDead) return Layer.Name + " — fully overruled by layers above it";

            var s = Layer.Name + " — " + Effective.ToString("N0", CultureInfo.InvariantCulture) + " elements";
            if (Overruled > 0)
                s += " · " + Overruled.ToString("N0", CultureInfo.InvariantCulture) + " overruled above";
            return s;
        }
    }

    /// <summary>The whole stack, folded.</summary>
    public sealed class AppearanceFold
    {
        internal AppearanceFold(IReadOnlyList<ElementAppearance> elements, IReadOnlyList<LayerReport> layers)
        {
            Elements = elements; Layers = layers;
        }

        /// <summary>Every element any layer has a view on.</summary>
        public IReadOnlyList<ElementAppearance> Elements { get; }

        /// <summary>What each layer is doing, bottom of the stack first.</summary>
        public IReadOnlyList<LayerReport> Layers { get; }

        /// <summary>Elements the stack hides.</summary>
        public int Hidden => Elements.Count(e => !e.Visible);

        /// <summary>Elements the stack colours.</summary>
        public int Coloured => Elements.Count(e => e.Colour != null);

        /// <summary>Look one element up.</summary>
        public ElementAppearance? For(ElementKey key) => Elements.FirstOrDefault(e => e.Key.Equals(key));

        /// <summary>The one-line readout for the panel header.</summary>
        public override string ToString()
        {
            var live = Layers.Count(l => l.Layer.IsEnabled);
            var s = live.ToString(CultureInfo.InvariantCulture) + " of "
                  + Layers.Count.ToString(CultureInfo.InvariantCulture) + " layers on · "
                  + Hidden.ToString("N0", CultureInfo.InvariantCulture) + " hidden · "
                  + Coloured.ToString("N0", CultureInfo.InvariantCulture) + " coloured";

            var dead = Layers.Count(l => l.IsDead);
            if (dead > 0) s += " · " + dead.ToString(CultureInfo.InvariantCulture) + " doing nothing";
            return s;
        }
    }

    /// <summary>
    /// The layer stack, and the fold that decides what each element actually looks like.
    ///
    /// The host has no stack. It has a pile: you hide something, you override a colour, and there
    /// is no record of what you did, in what order, or why. The only way back is "reset all", which
    /// discards everything including the work you wanted to keep — so in practice people stop
    /// hiding things, or they hide things and never trust the model again.
    ///
    /// <b>Precedence is per property, top of the stack wins.</b> A layer that sets only a colour
    /// leaves the visibility beneath it alone. That is what makes an isolate expressible as two
    /// ordinary layers — hide everything, then show these — rather than as a special mode, and it
    /// is why the second layer somebody adds does not silently undo the first.
    /// </summary>
    public static class AppearanceStack
    {
        /// <summary>
        /// Fold the stack.
        /// </summary>
        /// <param name="layers">
        /// The stack, <b>bottom first</b>: later entries override earlier ones. The panel shows it
        /// the other way up, the way every layers panel does, but the list itself reads in the
        /// order the fold applies it.
        /// </param>
        /// <param name="resolver">Turns each layer's target into the elements it covers.</param>
        public static AppearanceFold Fold(IReadOnlyList<AppearanceLayer> layers, ILayerResolver resolver)
        {
            if (layers == null) throw new ArgumentNullException(nameof(layers));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));

            var covered = new List<HashSet<ElementKey>>(layers.Count);
            var order = new List<ElementKey>();
            var known = new HashSet<ElementKey>();

            foreach (var layer in layers)
            {
                // A disabled or empty layer is still resolved, so its report can say how many
                // elements it WOULD cover. A layers panel where turning something off makes its
                // count vanish is a panel you cannot plan with.
                var keys = new HashSet<ElementKey>(resolver.Resolve(layer.Target).Where(k => !k.IsEmpty));
                covered.Add(keys);

                foreach (var key in keys)
                    if (known.Add(key)) order.Add(key);
            }

            var effective = new int[layers.Count];
            var elements = new List<ElementAppearance>(order.Count);

            foreach (var key in order)
            {
                var visible = true;
                Colour? colour = null;
                double? transparency = null;

                var winner = new Dictionary<AppearanceProperty, int>();
                var claims = new List<(int Layer, AppearanceProperty Property, string Value)>();

                for (var i = 0; i < layers.Count; i++)
                {
                    var layer = layers[i];
                    if (!layer.IsEnabled || !covered[i].Contains(key)) continue;

                    // Bottom to top, so a later layer simply overwrites — the last write on each
                    // property is the surviving one, recorded per property rather than per layer.
                    if (layer.Visible != null)
                    {
                        visible = layer.Visible.Value;
                        winner[AppearanceProperty.Visibility] = i;
                        claims.Add((i, AppearanceProperty.Visibility, layer.ValueOf(AppearanceProperty.Visibility)!));
                    }

                    if (layer.Colour != null)
                    {
                        colour = layer.Colour;
                        winner[AppearanceProperty.Colour] = i;
                        claims.Add((i, AppearanceProperty.Colour, layer.ValueOf(AppearanceProperty.Colour)!));
                    }

                    if (layer.Transparency != null)
                    {
                        transparency = layer.Transparency;
                        winner[AppearanceProperty.Transparency] = i;
                        claims.Add((i, AppearanceProperty.Transparency, layer.ValueOf(AppearanceProperty.Transparency)!));
                    }
                }

                var decisions = new List<AppearanceDecision>(claims.Count);
                var creditedThisElement = new HashSet<int>();

                // Top of the stack first, which is the order the question gets asked in: the
                // winning row is the one the eye lands on, and the overruled rows follow it.
                foreach (var claim in Enumerable.Reverse(claims))
                {
                    var survived = winner[claim.Property] == claim.Layer;

                    decisions.Add(new AppearanceDecision(
                        layers[claim.Layer].Id,
                        layers[claim.Layer].Name,
                        claim.Property,
                        claim.Value,
                        survived,
                        survived ? null : layers[winner[claim.Property]].Name));

                    if (survived && creditedThisElement.Add(claim.Layer)) effective[claim.Layer]++;
                }

                elements.Add(new ElementAppearance(key, visible, colour, transparency, decisions));
            }

            var reports = layers
                .Select((layer, i) => new LayerReport(layer, covered[i].Count, effective[i]))
                .ToList();

            return new AppearanceFold(elements, reports);
        }
    }
}
