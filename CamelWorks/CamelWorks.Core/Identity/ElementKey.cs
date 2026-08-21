using System;
using System.Globalization;

namespace CamelWorks.Core.Identity
{
    /// <summary>Which rung of the fallback produced an <see cref="ElementKey"/>. Lower is stronger.</summary>
    public enum KeyRung
    {
        /// <summary>The element's own instance GUID. Survives anything that preserves the element.</summary>
        InstanceGuid = 1,

        /// <summary>Source file + path through the selection tree. Survives a re-export that keeps structure.</summary>
        TreePath = 2,

        /// <summary>Category/type + a rotation-tolerant size signature. Last resort; weak by construction.</summary>
        Geometry = 3,
    }

    /// <summary>
    /// Names one model element, stably enough to be written to disk and matched against a later
    /// re-export of the same model.
    ///
    /// Three properties this type exists to guarantee:
    ///
    /// 1. <b>It is scoped to its owning model.</b> Two elements in two different loaded models can
    ///    share a tree path or a geometry signature, and an unscoped key would silently match them.
    ///    A wrong match is far worse than a miss — it shows an unresolved clash as "Approved,
    ///    signed off by J. Smith" and looks correct to everyone in the room.
    /// 2. <b>The rung is part of the key.</b> A rung-3 match is not the same claim as a rung-1
    ///    match, and callers that care (undo, carry-over) branch on it.
    /// 3. <b>It is a value, not a handle.</b> A Navisworks ModelItem is a pointer into a native
    ///    tree that a model refresh invalidates; this is a string that outlives the document.
    /// </summary>
    public readonly struct ElementKey : IEquatable<ElementKey>
    {
        /// <summary>Hash of the owning model's source path. See <see cref="ScopeOf"/>.</summary>
        public string ModelScope { get; }

        /// <summary>Which rung produced <see cref="Value"/>.</summary>
        public KeyRung Rung { get; }

        /// <summary>The rung's hashed value, <see cref="Hash.ComponentWidth"/> hex characters.</summary>
        public string Value { get; }

        /// <summary>
        /// The same rung value computed WITHOUT the model scope. Diagnostic only — it is what lets
        /// a report say "this element looks like one in another model", and it is never used as a
        /// match key. Kept off equality and hashing deliberately.
        /// </summary>
        public string UnscopedValue { get; }

        private ElementKey(string modelScope, KeyRung rung, string value, string unscopedValue)
        {
            ModelScope = modelScope;
            Rung = rung;
            Value = value;
            UnscopedValue = unscopedValue;
        }

        /// <summary>True for a key that was never assigned (<c>default(ElementKey)</c>).</summary>
        public bool IsEmpty => string.IsNullOrEmpty(Value);

        /// <summary>
        /// The scope hash for a model, derived from its source file path. Case- and
        /// separator-insensitive, because the same model reached through a mapped drive and a UNC
        /// path is the same model.
        /// </summary>
        public static string ScopeOf(string? sourcePath)
        {
            var normalised = Normalise(sourcePath);
            return Hash.Of(Hash.ScopeWidth, "model", normalised);
        }

        /// <summary>Rung 1 — the element's instance GUID.</summary>
        public static ElementKey FromInstanceGuid(string modelScope, Guid instanceGuid)
        {
            if (instanceGuid == Guid.Empty)
                throw new ArgumentException("instance GUID is empty; caller should fall through to rung 2", nameof(instanceGuid));

            var g = instanceGuid.ToString("N", CultureInfo.InvariantCulture);
            return Make(modelScope, KeyRung.InstanceGuid, "g", g);
        }

        /// <summary>
        /// Rung 2 — source file plus the element's path through the selection tree.
        /// <paramref name="treePath"/> is the ordered display names from the model root down to
        /// the element, which is what a re-export preserves when it preserves structure.
        /// </summary>
        public static ElementKey FromTreePath(string modelScope, string? sourcePath, params string?[] treePath)
        {
            if (treePath == null || treePath.Length == 0)
                throw new ArgumentException("tree path is empty", nameof(treePath));

            var parts = new string?[treePath.Length + 2];
            parts[0] = "t";
            parts[1] = Normalise(sourcePath);
            Array.Copy(treePath, 0, parts, 2, treePath.Length);
            return Make(modelScope, KeyRung.TreePath, parts);
        }

        /// <summary>
        /// Rung 3 — category/type plus a size signature, for elements carrying neither a usable
        /// GUID nor a stable tree path (most DWG- and IFC-sourced NWCs).
        ///
        /// The three extents are SORTED before hashing, so the signature is unchanged by a 90°
        /// rotation about any axis. That matters because the only bounding box Navisworks exposes
        /// is world-space and axis-aligned: a rotated element yields permuted extents, and an
        /// unsorted signature would call it a different element. Size only — never position —
        /// because position is exactly what moves between revisions.
        ///
        /// This rung is weak by construction and says so: it cannot separate two identical parts.
        /// Callers treat a rung-3 match as a proposal, not a fact.
        /// </summary>
        public static ElementKey FromGeometry(
            string modelScope,
            string? category,
            string? typeName,
            double extentX,
            double extentY,
            double extentZ,
            double grid = 0.001)
        {
            var e = new[]
            {
                Hash.Quantise(Math.Abs(extentX), grid),
                Hash.Quantise(Math.Abs(extentY), grid),
                Hash.Quantise(Math.Abs(extentZ), grid),
            };
            Array.Sort(e);

            return Make(modelScope, KeyRung.Geometry,
                "x",
                category,
                typeName,
                e[0].ToString(CultureInfo.InvariantCulture),
                e[1].ToString(CultureInfo.InvariantCulture),
                e[2].ToString(CultureInfo.InvariantCulture));
        }

        private static ElementKey Make(string modelScope, KeyRung rung, params string?[] parts)
        {
            if (string.IsNullOrEmpty(modelScope))
                throw new ArgumentException("model scope is required — an unscoped key is never a match key", nameof(modelScope));

            var unscoped = Hash.Of(Hash.ComponentWidth, parts);

            var scopedParts = new string?[parts.Length + 1];
            scopedParts[0] = modelScope;
            Array.Copy(parts, 0, scopedParts, 1, parts.Length);
            var scoped = Hash.Of(Hash.ComponentWidth, scopedParts);

            return new ElementKey(modelScope, rung, scoped, unscoped);
        }

        private static string Normalise(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            return path!.Trim().Replace('\\', '/').ToUpperInvariant();
        }

        /// <summary>
        /// Wire form: <c>rung:scope:value</c>, e.g. <c>1:a1b2c3d4:0123456789abcdef</c>.
        /// The rung leads so a file of keys sorts strongest-first.
        /// </summary>
        public override string ToString() =>
            IsEmpty ? string.Empty
                    : ((int)Rung).ToString(CultureInfo.InvariantCulture) + ":" + ModelScope + ":" + Value;

        /// <summary>Parse the wire form. Returns false rather than throwing on anything malformed.</summary>
        public static bool TryParse(string? text, out ElementKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text!.Split(':');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rung)) return false;
            if (rung < (int)KeyRung.InstanceGuid || rung > (int)KeyRung.Geometry) return false;
            if (parts[1].Length != Hash.ScopeWidth || parts[2].Length != Hash.ComponentWidth) return false;

            // The unscoped value cannot be recovered from the wire form; it is diagnostic only and
            // is recomputed when the element is next resolved.
            key = new ElementKey(parts[1], (KeyRung)rung, parts[2], string.Empty);
            return true;
        }

        /// <inheritdoc />
        public bool Equals(ElementKey other) =>
            Rung == other.Rung
            && string.Equals(ModelScope, other.ModelScope, StringComparison.Ordinal)
            && string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ElementKey k && Equals(k);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            // Deliberately hand-rolled: this is an in-memory hash only, but writing it out makes
            // it obvious that UnscopedValue is excluded on purpose.
            unchecked
            {
                var h = (int)Rung;
                h = (h * 397) ^ (ModelScope?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Value?.GetHashCode() ?? 0);
                return h;
            }
        }

        /// <summary>Value equality.</summary>
        public static bool operator ==(ElementKey a, ElementKey b) => a.Equals(b);

        /// <summary>Value inequality.</summary>
        public static bool operator !=(ElementKey a, ElementKey b) => !a.Equals(b);
    }
}
