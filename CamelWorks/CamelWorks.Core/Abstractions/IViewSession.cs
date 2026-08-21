using System.Collections.Generic;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Abstractions
{
    /// <summary>
    /// Everything that changes what the user sees: colour, transparency, visibility, camera and
    /// the section box.
    ///
    /// This one is behind the seam for a reason that only showed up when the panel looked at it.
    /// The host's two override layers are NOT symmetric: the permanent layer can be reset for a
    /// given collection of elements, while the temporary layer can only be reset globally — and
    /// the temporary layer cannot be read back at all. That asymmetry is what forces the Appearance
    /// Manager's stack onto the permanent layer, and it is a host fact that logic above this seam
    /// must never have to know or re-derive.
    /// </summary>
    public interface IViewSession
    {
        /// <summary>Which layer a write targets. See the type remarks for why this is not free choice.</summary>
        OverrideLayer Layer { get; set; }

        /// <summary>Override colour on the given elements.</summary>
        void SetColour(IReadOnlyList<ElementKey> keys, Colour colour);

        /// <summary>Override transparency, 0 (opaque) to 1 (invisible).</summary>
        void SetTransparency(IReadOnlyList<ElementKey> keys, double transparency);

        /// <summary>Show or hide the given elements.</summary>
        void SetVisible(IReadOnlyList<ElementKey> keys, bool visible);

        /// <summary>
        /// Clear CamelWorks overrides on the given elements. On the permanent layer this is
        /// per-element; on the temporary layer the host only offers a global reset, so an
        /// implementation must either clear everything or refuse — never pretend it was selective.
        /// </summary>
        void ClearOverrides(IReadOnlyList<ElementKey> keys);

        /// <summary>Clear every override on the current layer, including ones CamelWorks did not author.</summary>
        void ClearAllOverrides();

        /// <summary>
        /// Read the appearance the document is actually in, for the elements given.
        ///
        /// Only the permanent layer can be reported. The Appearance Manager's foreign-state row
        /// says so explicitly rather than implying completeness — claiming to show everything while
        /// silently omitting temporary overrides would be worse than the host's own blindness.
        /// </summary>
        IReadOnlyList<AppearanceState> ReadAppearance(IReadOnlyList<ElementKey> keys);

        /// <summary>Fit the camera to the given elements, with a context margin in model units.</summary>
        void ZoomTo(IReadOnlyList<ElementKey> keys, double marginMetres);

        /// <summary>Set a section box around the given elements, with a context margin.</summary>
        void SetSectionBox(BoundingBox box, double marginMetres);

        /// <summary>Turn sectioning off without discarding the box, so it can be toggled back.</summary>
        void SetSectionBoxEnabled(bool enabled);
    }

    /// <summary>Which override layer a <see cref="IViewSession"/> write targets.</summary>
    public enum OverrideLayer
    {
        /// <summary>
        /// Survives into the saved document, resettable per element, and readable back. The
        /// Appearance Manager's stack lives here — not by preference, but because the alternative
        /// supports neither selective reset nor read-back.
        /// </summary>
        Permanent = 0,

        /// <summary>
        /// Scoped to the session and to saved viewpoints. Cannot be read back and can only be
        /// reset globally, so it is used for transient effects that nothing needs to inspect.
        /// </summary>
        Temporary = 1,
    }

    /// <summary>The appearance one element is actually in, as read from the document.</summary>
    public readonly struct AppearanceState
    {
        /// <summary>The element.</summary>
        public ElementKey Key { get; }

        /// <summary>Current colour, or null when the element carries no colour override.</summary>
        public Colour? Colour { get; }

        /// <summary>Current transparency, or null when the element carries no transparency override.</summary>
        public double? Transparency { get; }

        /// <summary>Whether the host currently hides the element.</summary>
        public bool IsHidden { get; }

        /// <summary>
        /// True when the override differs from the element's original appearance but no CamelWorks
        /// layer accounts for it — i.e. somebody else set it. This is the signal behind the
        /// Appearance Manager's foreign-state row, and the answer to "why is this thing pink".
        /// </summary>
        public bool IsForeign { get; }

        /// <summary>Create a reading.</summary>
        public AppearanceState(ElementKey key, Colour? colour, double? transparency, bool isHidden, bool isForeign)
        {
            Key = key; Colour = colour; Transparency = transparency; IsHidden = isHidden; IsForeign = isForeign;
        }

        /// <summary>True when the element is in its untouched state.</summary>
        public bool IsPristine => Colour == null && Transparency == null && !IsHidden;
    }

    /// <summary>An 8-bit-per-channel colour.</summary>
    public readonly struct Colour : System.IEquatable<Colour>
    {
        /// <summary>Red, 0-255.</summary>
        public byte R { get; }

        /// <summary>Green, 0-255.</summary>
        public byte G { get; }

        /// <summary>Blue, 0-255.</summary>
        public byte B { get; }

        /// <summary>Create a colour.</summary>
        public Colour(byte r, byte g, byte b) { R = r; G = g; B = b; }

        /// <summary>Parse <c>#rrggbb</c> or <c>rrggbb</c>. Returns false rather than throwing.</summary>
        public static bool TryParse(string? hex, out Colour colour)
        {
            colour = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var s = hex!.Trim();
            if (s.Length > 0 && s[0] == '#') s = s.Substring(1);
            if (s.Length != 6) return false;

            if (!byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var r)) return false;
            if (!byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var g)) return false;
            if (!byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var b)) return false;

            colour = new Colour(r, g, b);
            return true;
        }

        /// <summary>Packed as <c>0xRRGGBB</c> — a cheap dictionary key when batching writes by colour.</summary>
        public int Packed => (R << 16) | (G << 8) | B;

        /// <inheritdoc />
        public bool Equals(Colour other) => Packed == other.Packed;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Colour c && Equals(c);

        /// <inheritdoc />
        public override int GetHashCode() => Packed;

        /// <summary>Value equality.</summary>
        public static bool operator ==(Colour a, Colour b) => a.Equals(b);

        /// <summary>Value inequality.</summary>
        public static bool operator !=(Colour a, Colour b) => !a.Equals(b);

        /// <summary><c>#rrggbb</c>.</summary>
        public override string ToString() =>
            "#" + R.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)
                + G.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)
                + B.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
    }
}
