using System;
using System.Collections.Generic;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Abstractions
{
    /// <summary>
    /// The only way CamelWorks changes the document.
    ///
    /// Two things this seam exists to force:
    ///
    /// 1. <b>Preview before write, always.</b> Every mutating command shows an affected-element
    ///    count first. There is no API on the far side that lets a caller skip it.
    /// 2. <b>Its own undo.</b> The host's transaction API does not cover us — COM user-defined
    ///    property writes sit outside the host's undo entirely, so Ctrl+Z will not reverse them.
    ///    Rather than pretend otherwise, every write captures a pre-edit snapshot into the sidecar
    ///    journal, and the product says plainly in three places that Ctrl+Z is not the way back.
    /// </summary>
    public interface IModelWriteTransaction : IDisposable
    {
        /// <summary>Short label for the operation, shown in the undo list and the journal.</summary>
        string Description { get; }

        /// <summary>
        /// What this transaction would change, without changing it. Safe to call repeatedly, and
        /// callers do — a preview pane recomputes it as the user edits the rule that drives it.
        /// </summary>
        WritePreview Preview();

        /// <summary>Queue a custom property write.</summary>
        void SetProperty(ElementKey key, string category, string name, string? value);

        /// <summary>Queue removal of a custom property.</summary>
        void RemoveProperty(ElementKey key, string category, string name);

        /// <summary>
        /// Apply everything queued. Returns the number of elements actually changed, which can be
        /// lower than the preview when an element stopped resolving between preview and commit.
        /// </summary>
        int Commit();

        /// <summary>Abandon everything queued. Also what <see cref="IDisposable.Dispose"/> does if
        /// <see cref="Commit"/> was never called, so a failed operation cannot half-apply.</summary>
        void Rollback();
    }

    /// <summary>What a transaction would do if committed.</summary>
    public readonly struct WritePreview
    {
        /// <summary>Number of distinct elements that would change.</summary>
        public int AffectedElements { get; }

        /// <summary>Number of individual property writes queued.</summary>
        public int PropertyWrites { get; }

        /// <summary>Number of property removals queued.</summary>
        public int PropertyRemovals { get; }

        /// <summary>
        /// Keys that no longer resolve in the current generation. Surfaced rather than dropped:
        /// silently skipping them is how a write quietly does less than the user was shown.
        /// </summary>
        public IReadOnlyList<ElementKey> Unresolved { get; }

        /// <summary>Create a preview.</summary>
        public WritePreview(int affectedElements, int propertyWrites, int propertyRemovals, IReadOnlyList<ElementKey> unresolved)
        {
            AffectedElements = affectedElements;
            PropertyWrites = propertyWrites;
            PropertyRemovals = propertyRemovals;
            Unresolved = unresolved ?? Array.Empty<ElementKey>();
        }

        /// <summary>True when committing would do nothing.</summary>
        public bool IsEmpty => AffectedElements == 0 && PropertyWrites == 0 && PropertyRemovals == 0;

        /// <summary>One line, as a confirm dialog would show it.</summary>
        public override string ToString()
        {
            var s = AffectedElements + " element" + (AffectedElements == 1 ? "" : "s")
                  + ", " + PropertyWrites + " write" + (PropertyWrites == 1 ? "" : "s")
                  + ", " + PropertyRemovals + " removal" + (PropertyRemovals == 1 ? "" : "s");
            if (Unresolved.Count > 0) s += ", " + Unresolved.Count + " no longer found";
            return s;
        }
    }
}
