using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Testing
{
    /// <summary>
    /// In-memory <see cref="IModelWriteTransaction"/>, recording every effect in order.
    ///
    /// It enforces the two rules the real implementation has to keep, so a service that breaks
    /// either one fails here rather than in the field:
    ///
    /// * committing twice throws, because a double-apply is silent corruption; and
    /// * disposing without committing rolls back, so a failed operation cannot half-apply.
    /// </summary>
    public sealed class FakeWriteTransaction : IModelWriteTransaction
    {
        private readonly FakeDocument _document;
        private readonly List<QueuedWrite> _queue = new List<QueuedWrite>();
        private bool _committed;
        private bool _rolledBack;

        internal FakeWriteTransaction(FakeDocument document, string description)
        {
            _document = document;
            Description = description;
            _document.Record("write.begin " + description);
        }

        /// <inheritdoc />
        public string Description { get; }

        /// <inheritdoc />
        public WritePreview Preview()
        {
            var unresolved = new List<ElementKey>();
            var affected = new HashSet<ElementKey>();
            var writes = 0;
            var removals = 0;

            foreach (var q in _queue)
            {
                if (_document.Find(q.Key) == null)
                {
                    if (!unresolved.Contains(q.Key)) unresolved.Add(q.Key);
                    continue;
                }

                affected.Add(q.Key);
                if (q.IsRemoval) removals++; else writes++;
            }

            _document.Record("write.preview " + affected.Count + "/" + writes + "/" + removals
                             + (unresolved.Count > 0 ? "/unresolved=" + unresolved.Count : string.Empty));

            return new WritePreview(affected.Count, writes, removals, unresolved);
        }

        /// <inheritdoc />
        public void SetProperty(ElementKey key, string category, string name, string? value)
        {
            GuardOpen();
            _queue.Add(new QueuedWrite(key, category, name, value, isRemoval: false));
        }

        /// <inheritdoc />
        public void RemoveProperty(ElementKey key, string category, string name)
        {
            GuardOpen();
            _queue.Add(new QueuedWrite(key, category, name, null, isRemoval: true));
        }

        /// <inheritdoc />
        public int Commit()
        {
            GuardOpen();

            var changed = new HashSet<ElementKey>();
            foreach (var q in _queue)
            {
                var item = _document.Find(q.Key);
                if (item == null) continue;   // resolved away between preview and commit; reported, never silent

                if (q.IsRemoval) item.ApplyRemove(q.Category, q.Name);
                else item.ApplyWrite(q.Category, q.Name, q.Value);

                changed.Add(q.Key);
            }

            _committed = true;
            _document.Record("write.commit " + Description + " n=" + changed.Count.ToString(CultureInfo.InvariantCulture));
            return changed.Count;
        }

        /// <inheritdoc />
        public void Rollback()
        {
            if (_committed || _rolledBack) return;
            _rolledBack = true;
            _queue.Clear();
            _document.Record("write.rollback " + Description);
        }

        /// <inheritdoc />
        public void Dispose() => Rollback();

        private void GuardOpen()
        {
            if (_committed)
                throw new InvalidOperationException(
                    "This transaction has already been committed. Committing twice silently doubles every write.");
            if (_rolledBack)
                throw new InvalidOperationException("This transaction has been rolled back.");
        }

        private readonly struct QueuedWrite
        {
            public QueuedWrite(ElementKey key, string category, string name, string? value, bool isRemoval)
            {
                Key = key; Category = category; Name = name; Value = value; IsRemoval = isRemoval;
            }

            public ElementKey Key { get; }
            public string Category { get; }
            public string Name { get; }
            public string? Value { get; }
            public bool IsRemoval { get; }
        }
    }

    /// <summary>
    /// In-memory <see cref="IViewSession"/>. Models the host's layer asymmetry faithfully, because
    /// that asymmetry is the reason the Appearance Manager is designed the way it is: the permanent
    /// layer clears per element and reads back; the temporary layer clears only globally and cannot
    /// be read at all. A test asserting otherwise should fail.
    /// </summary>
    public sealed class FakeViewSession : IViewSession
    {
        private readonly FakeDocument _document;
        private readonly Dictionary<ElementKey, Colour> _colour = new Dictionary<ElementKey, Colour>();
        private readonly Dictionary<ElementKey, double> _transparency = new Dictionary<ElementKey, double>();
        private readonly HashSet<ElementKey> _authored = new HashSet<ElementKey>();

        /// <summary>Create a session over a document.</summary>
        public FakeViewSession(FakeDocument document) => _document = document;

        /// <inheritdoc />
        public OverrideLayer Layer { get; set; } = OverrideLayer.Permanent;

        /// <summary>The section box currently set, if any.</summary>
        public BoundingBox? SectionBox { get; private set; }

        /// <summary>Whether sectioning is on.</summary>
        public bool SectionBoxEnabled { get; private set; }

        /// <inheritdoc />
        public void SetColour(IReadOnlyList<ElementKey> keys, Colour colour)
        {
            foreach (var k in keys) { _colour[k] = colour; _authored.Add(k); }
            _document.Record("view.colour " + Layer + " n=" + keys.Count + " " + colour);
        }

        /// <inheritdoc />
        public void SetTransparency(IReadOnlyList<ElementKey> keys, double transparency)
        {
            foreach (var k in keys) { _transparency[k] = transparency; _authored.Add(k); }
            _document.Record("view.transparency " + Layer + " n=" + keys.Count);
        }

        /// <inheritdoc />
        public void SetVisible(IReadOnlyList<ElementKey> keys, bool visible)
        {
            foreach (var k in keys)
            {
                var item = _document.Find(k);
                if (item != null) item.IsHidden = !visible;
            }
            _document.Record("view.visible " + visible + " n=" + keys.Count);
        }

        /// <inheritdoc />
        public void ClearOverrides(IReadOnlyList<ElementKey> keys)
        {
            if (Layer == OverrideLayer.Temporary)
                throw new NotSupportedException(
                    "The host offers no per-element reset on the temporary layer — only a global one. " +
                    "Call ClearAllOverrides, or put the stack on the permanent layer.");

            foreach (var k in keys) { _colour.Remove(k); _transparency.Remove(k); _authored.Remove(k); }
            _document.Record("view.clear n=" + keys.Count);
        }

        /// <inheritdoc />
        public void ClearAllOverrides()
        {
            _colour.Clear();
            _transparency.Clear();
            _authored.Clear();
            _document.Record("view.clearAll " + Layer);
        }

        /// <inheritdoc />
        public IReadOnlyList<AppearanceState> ReadAppearance(IReadOnlyList<ElementKey> keys)
        {
            if (Layer == OverrideLayer.Temporary)
                throw new NotSupportedException(
                    "The temporary layer cannot be read back. Reporting it as empty would claim a " +
                    "completeness the host does not offer.");

            _document.Record("view.read n=" + keys.Count);

            return keys.Select(k =>
            {
                var item = _document.Find(k);
                var colour = _colour.TryGetValue(k, out var c) ? (Colour?)c : null;
                var transparency = _transparency.TryGetValue(k, out var t) ? (double?)t : null;
                var hidden = item?.IsHidden ?? false;
                var foreign = (colour != null || transparency != null || hidden) && !_authored.Contains(k);
                return new AppearanceState(k, colour, transparency, hidden, foreign);
            }).ToList();
        }

        /// <summary>Seed an override CamelWorks did not author, as another tool would leave behind.</summary>
        public void SeedForeignColour(ElementKey key, Colour colour)
        {
            _colour[key] = colour;
            _authored.Remove(key);
        }

        /// <inheritdoc />
        public void ZoomTo(IReadOnlyList<ElementKey> keys, double marginMetres) =>
            _document.Record("view.zoom n=" + keys.Count + " margin=" + marginMetres.ToString(CultureInfo.InvariantCulture));

        /// <inheritdoc />
        public void SetSectionBox(BoundingBox box, double marginMetres)
        {
            SectionBox = new BoundingBox(
                box.MinX - marginMetres, box.MinY - marginMetres, box.MinZ - marginMetres,
                box.MaxX + marginMetres, box.MaxY + marginMetres, box.MaxZ + marginMetres);
            SectionBoxEnabled = true;
            _document.Record("view.sectionBox margin=" + marginMetres.ToString(CultureInfo.InvariantCulture));
        }

        /// <inheritdoc />
        public void SetSectionBoxEnabled(bool enabled)
        {
            SectionBoxEnabled = enabled;
            _document.Record("view.sectionBoxEnabled " + enabled);
        }
    }
}
