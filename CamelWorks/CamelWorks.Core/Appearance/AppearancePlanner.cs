using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Appearance
{
    /// <summary>Elements that all want the same colour — one host call.</summary>
    public sealed class ColourBatch
    {
        internal ColourBatch(Colour colour, IReadOnlyList<ElementKey> keys)
        {
            Colour = colour; Keys = keys;
        }

        /// <summary>The colour.</summary>
        public Colour Colour { get; }

        /// <summary>The elements.</summary>
        public IReadOnlyList<ElementKey> Keys { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Colour + " × " + Keys.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Elements that all want the same transparency — one host call.</summary>
    public sealed class TransparencyBatch
    {
        internal TransparencyBatch(double transparency, IReadOnlyList<ElementKey> keys)
        {
            Transparency = transparency; Keys = keys;
        }

        /// <summary>The transparency, 0 to 1.</summary>
        public double Transparency { get; }

        /// <summary>The elements.</summary>
        public IReadOnlyList<ElementKey> Keys { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Transparency.ToString("0.##", CultureInfo.InvariantCulture)
            + " × " + Keys.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The writes that take the document from where it is to what the stack says.
    ///
    /// A diff, not a repaint. Toggling one layer on a federation must not rewrite two hundred
    /// thousand overrides, most of them to the value they already hold, so the planner compares
    /// against what the document is actually in and emits only the difference.
    ///
    /// <b>Apply in order: Clear, then Hide and Show, then Colours, then Transparencies.</b> The
    /// order is load-bearing, not tidiness. The host has no way to remove a colour override on its
    /// own — the only eraser is a per-element clear, which takes visibility and transparency with
    /// it. So an element that is losing its transparency but keeping its colour has to be cleared
    /// and then repainted, and it appears in both lists on purpose.
    /// </summary>
    public sealed class AppearancePlan
    {
        internal AppearancePlan(IReadOnlyList<ElementKey> clear, IReadOnlyList<ElementKey> hide,
                                IReadOnlyList<ElementKey> show, IReadOnlyList<ColourBatch> colours,
                                IReadOnlyList<TransparencyBatch> transparencies, IReadOnlyList<ElementKey> foreign)
        {
            Clear = clear; Hide = hide; Show = show;
            Colours = colours; Transparencies = transparencies; Foreign = foreign;
        }

        /// <summary>
        /// Elements to reset first: CamelWorks overrides that the stack no longer wants, whether
        /// because a layer was deleted, disabled, or narrowed. Some are repainted immediately
        /// afterwards — see the type remarks for why that is not wasted work.
        /// </summary>
        public IReadOnlyList<ElementKey> Clear { get; }

        /// <summary>Elements to hide.</summary>
        public IReadOnlyList<ElementKey> Hide { get; }

        /// <summary>Elements to show.</summary>
        public IReadOnlyList<ElementKey> Show { get; }

        /// <summary>Colour writes, batched by colour.</summary>
        public IReadOnlyList<ColourBatch> Colours { get; }

        /// <summary>Transparency writes, batched by value.</summary>
        public IReadOnlyList<TransparencyBatch> Transparencies { get; }

        /// <summary>
        /// Overridden elements that no layer claims and CamelWorks did not author.
        ///
        /// <b>Reported, never cleared.</b> Somebody else — a colleague, another add-in, the model
        /// itself — put those there, and a manager that quietly resets them the first time it runs
        /// is a manager nobody can leave switched on. Clearing them stays something a person asks
        /// for by name.
        /// </summary>
        public IReadOnlyList<ElementKey> Foreign { get; }

        /// <summary>How many calls the host will take. The honest cost of pressing Apply.</summary>
        public int Writes =>
            (Clear.Count > 0 ? 1 : 0) + (Hide.Count > 0 ? 1 : 0) + (Show.Count > 0 ? 1 : 0)
            + Colours.Count + Transparencies.Count;

        /// <summary>True when the document already looks like the stack says it should.</summary>
        public bool IsEmpty => Writes == 0;

        /// <summary>The one-line readout.</summary>
        public string Explain()
        {
            var parts = new List<string>();
            if (Clear.Count > 0) parts.Add(Clear.Count.ToString("N0", CultureInfo.InvariantCulture) + " reset");
            if (Hide.Count > 0) parts.Add(Hide.Count.ToString("N0", CultureInfo.InvariantCulture) + " to hide");
            if (Show.Count > 0) parts.Add(Show.Count.ToString("N0", CultureInfo.InvariantCulture) + " to show");

            var coloured = Colours.Sum(c => c.Keys.Count);
            if (coloured > 0) parts.Add(coloured.ToString("N0", CultureInfo.InvariantCulture) + " to colour");

            var faded = Transparencies.Sum(t => t.Keys.Count);
            if (faded > 0) parts.Add(faded.ToString("N0", CultureInfo.InvariantCulture) + " to fade");

            var s = parts.Count == 0
                ? "nothing to change"
                : string.Join(" · ", parts) + " · " + Writes.ToString(CultureInfo.InvariantCulture)
                  + (Writes == 1 ? " write" : " writes");

            if (Foreign.Count > 0)
                s += " · " + Foreign.Count.ToString("N0", CultureInfo.InvariantCulture)
                   + " overridden by somebody else, left alone";

            return s;
        }

        /// <inheritdoc />
        public override string ToString() => Explain();
    }

    /// <summary>
    /// Works out the smallest set of host writes that makes the document match the stack.
    /// </summary>
    public static class AppearancePlanner
    {
        /// <summary>
        /// The resolution transparency is compared at: eight bits, the host's own.
        ///
        /// A value written as 0.5 reads back as 128/255. Comparing exactly would find a difference
        /// on every element on every fold, and every layer toggle would rewrite the whole model to
        /// the value it already held. Quantising rather than allowing a tolerance band, because a
        /// band lets values chain — 0.000, 0.003, 0.006 each within tolerance of the last — and
        /// which bucket an element lands in would then depend on the order it arrived.
        /// </summary>
        public const int TransparencySteps = 255;

        /// <summary>Plan the writes.</summary>
        /// <param name="fold">What the stack says the model should look like.</param>
        /// <param name="current">What the document is actually in, from the view session.</param>
        public static AppearancePlan Plan(AppearanceFold fold, IReadOnlyList<AppearanceState>? current)
        {
            if (fold == null) throw new ArgumentNullException(nameof(fold));

            current ??= Array.Empty<AppearanceState>();

            var actual = new Dictionary<ElementKey, AppearanceState>();
            foreach (var state in current)
                if (!state.Key.IsEmpty) actual[state.Key] = state;

            var wanted = new Dictionary<ElementKey, ElementAppearance>();
            foreach (var element in fold.Elements) wanted[element.Key] = element;

            // Every element either side of the comparison: what the stack wants, plus what the
            // document already carries. The second half is what makes a deleted layer undo itself.
            var everyKey = new List<ElementKey>();
            var seen = new HashSet<ElementKey>();
            foreach (var element in fold.Elements)
                if (seen.Add(element.Key)) everyKey.Add(element.Key);
            foreach (var state in current)
                if (!state.Key.IsEmpty && seen.Add(state.Key)) everyKey.Add(state.Key);

            var clear = new List<ElementKey>();
            var hide = new List<ElementKey>();
            var show = new List<ElementKey>();
            var foreign = new List<ElementKey>();
            var byColour = new Dictionary<int, List<ElementKey>>();
            var colourOf = new Dictionary<int, Colour>();
            var byTransparency = new Dictionary<int, List<ElementKey>>();

            foreach (var key in everyKey)
            {
                wanted.TryGetValue(key, out var want);
                var hasState = actual.TryGetValue(key, out var state);

                var wantVisible = want?.Visible ?? true;
                var wantColour = want?.Colour;
                var wantTransparency = want?.Transparency;

                var isHidden = hasState && state.IsHidden;
                var hasColour = hasState && state.Colour != null;
                var hasTransparency = hasState && state.Transparency != null;

                // Somebody else's override on an element no layer DECIDES anything about. Report
                // and move on — touching it is the one thing this manager must never do on its own.
                //
                // "Decides", not "covers": a layer that is switched off still puts its elements in
                // the fold so its report can show a count, and treating that as a claim would let
                // disabling a layer wipe an override the layer never owned.
                if (hasState && state.IsForeign && (want == null || want.IsUntouched))
                {
                    if (!state.IsPristine) foreign.Add(key);
                    continue;
                }

                // The only eraser the host offers is a per-element clear, and it takes colour,
                // transparency and visibility together. So an element losing one of them has to be
                // cleared and then given back the others.
                var needsClear = (hasColour && wantColour == null) || (hasTransparency && wantTransparency == null);
                if (needsClear) clear.Add(key);

                if (!wantVisible && (needsClear || !isHidden)) hide.Add(key);
                else if (wantVisible && isHidden && !needsClear) show.Add(key);

                if (wantColour != null && (needsClear || wantColour != state.Colour))
                {
                    var packed = wantColour.Value.Packed;
                    if (!byColour.TryGetValue(packed, out var bucket))
                    {
                        bucket = new List<ElementKey>();
                        byColour[packed] = bucket;
                        colourOf[packed] = wantColour.Value;
                    }

                    bucket.Add(key);
                }

                if (wantTransparency != null)
                {
                    var step = Quantise(wantTransparency.Value);
                    if (needsClear || state.Transparency == null || Quantise(state.Transparency.Value) != step)
                    {
                        if (!byTransparency.TryGetValue(step, out var bucket))
                        {
                            bucket = new List<ElementKey>();
                            byTransparency[step] = bucket;
                        }

                        bucket.Add(key);
                    }
                }
            }

            // Ordinal ordering throughout, so two runs of the same stack produce the same plan and
            // the preview can be compared with what Apply then does.
            return new AppearancePlan(
                Sorted(clear),
                Sorted(hide),
                Sorted(show),
                byColour.OrderBy(kv => kv.Key)
                        .Select(kv => new ColourBatch(colourOf[kv.Key], Sorted(kv.Value)))
                        .ToList(),
                byTransparency.OrderBy(kv => kv.Key)
                              .Select(kv => new TransparencyBatch((double)kv.Key / TransparencySteps, Sorted(kv.Value)))
                              .ToList(),
                Sorted(foreign));
        }

        private static int Quantise(double transparency) =>
            (int)Math.Round(Math.Max(0, Math.Min(1, transparency)) * TransparencySteps, MidpointRounding.AwayFromZero);

        private static IReadOnlyList<ElementKey> Sorted(List<ElementKey> keys) =>
            keys.OrderBy(k => k.ToString(), StringComparer.Ordinal).ToList();
    }
}
