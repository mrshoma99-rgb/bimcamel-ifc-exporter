using System;
using System.Collections.Generic;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Abstractions
{
    /// <summary>
    /// One loaded model within the document — a discipline's NWC, or a sub-NWF.
    /// </summary>
    public interface IModelSource
    {
        /// <summary>Display name as the host shows it.</summary>
        string DisplayName { get; }

        /// <summary>Full source path, used to derive <see cref="ElementKey.ScopeOf"/>.</summary>
        string SourcePath { get; }

        /// <summary>The scope hash every key from this model carries.</summary>
        string Scope { get; }
    }

    /// <summary>
    /// The host document, behind a seam.
    ///
    /// <b>The generation counter is the point of this interface.</b> A host model item is a handle
    /// into a native tree, and appending or refreshing a model invalidates every one of them. That
    /// happens mid-week with the board, the appearance stack and a takeoff all open — it is the
    /// most ordinary action there is. So no screen may hold an item; screens hold
    /// <c>(Generation, ElementKey)</c> and re-resolve when the generation moves. Cheap to build in
    /// now; a rewrite of every tab's bindings at any later point.
    /// </summary>
    public interface IModelDocument
    {
        /// <summary>
        /// Bumped whenever the tree changes shape — append, refresh, remove, or document switch.
        /// A holder comparing a stale value against this one knows to re-resolve.
        /// </summary>
        int Generation { get; }

        /// <summary>True when no document is open. Every screen must render something sensible for this.</summary>
        bool IsEmpty { get; }

        /// <summary>The loaded models, in host order.</summary>
        IReadOnlyList<IModelSource> Models { get; }

        /// <summary>
        /// Resolve a key back to a live item, or null when it cannot be found in the current
        /// generation. Null is an ordinary outcome, not an error: an element genuinely removed
        /// between revisions resolves to null and the caller reports it as unmatched.
        /// </summary>
        IModelItem? Resolve(ElementKey key);

        /// <summary>
        /// Walk a scope. Implementations stream rather than materialise — a federation is
        /// millions of items and callers are expected to stop early.
        /// </summary>
        IEnumerable<IModelItem> Traverse(TraversalScope scope);
    }

    /// <summary>What a walk covers. The zero-setup default (<see cref="WholeDocument"/>) is
    /// deliberately the cheapest thing to construct: no configuration, no prior state.</summary>
    public readonly struct TraversalScope
    {
        /// <summary>Which kind of scope this is.</summary>
        public TraversalScopeKind Kind { get; }

        /// <summary>Set name, when <see cref="Kind"/> is <see cref="TraversalScopeKind.Set"/>.</summary>
        public string? SetName { get; }

        /// <summary>Explicit keys, when <see cref="Kind"/> is <see cref="TraversalScopeKind.Keys"/>.</summary>
        public IReadOnlyList<ElementKey>? Keys { get; }

        private TraversalScope(TraversalScopeKind kind, string? setName, IReadOnlyList<ElementKey>? keys)
        {
            Kind = kind; SetName = setName; Keys = keys;
        }

        /// <summary>Everything in every loaded model. The default scope for every screen.</summary>
        public static TraversalScope WholeDocument => new TraversalScope(TraversalScopeKind.WholeDocument, null, null);

        /// <summary>Whatever the user has selected in the host right now.</summary>
        public static TraversalScope CurrentSelection => new TraversalScope(TraversalScopeKind.CurrentSelection, null, null);

        /// <summary>A saved selection or search set, by name.</summary>
        public static TraversalScope Set(string name) =>
            string.IsNullOrWhiteSpace(name)
                ? throw new ArgumentException("set name is required", nameof(name))
                : new TraversalScope(TraversalScopeKind.Set, name, null);

        /// <summary>An explicit list of keys — how a screen re-resolves after a generation bump.</summary>
        public static TraversalScope FromKeys(IReadOnlyList<ElementKey> keys) =>
            keys == null
                ? throw new ArgumentNullException(nameof(keys))
                : new TraversalScope(TraversalScopeKind.Keys, null, keys);

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            TraversalScopeKind.Set => "set:" + SetName,
            TraversalScopeKind.Keys => "keys:" + (Keys?.Count ?? 0),
            _ => Kind.ToString(),
        };
    }

    /// <summary>The kinds of <see cref="TraversalScope"/>.</summary>
    public enum TraversalScopeKind
    {
        /// <summary>Every item in every loaded model.</summary>
        WholeDocument = 0,

        /// <summary>The host's current selection.</summary>
        CurrentSelection = 1,

        /// <summary>A named selection or search set.</summary>
        Set = 2,

        /// <summary>An explicit key list.</summary>
        Keys = 3,
    }
}
