using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Testing
{
    /// <summary>
    /// Thrown when code touches a model item it resolved in an earlier generation.
    ///
    /// This is the fake being deliberately harsher than the host. Navisworks does not reliably
    /// throw on a stale handle — it may return nonsense, or crash later somewhere unrelated, which
    /// is precisely why the bug is hard to find in the field. Making it loud and immediate here is
    /// the whole reason the generation counter is testable at all.
    /// </summary>
    public sealed class StaleItemException : InvalidOperationException
    {
        /// <summary>Create the exception.</summary>
        public StaleItemException(string message) : base(message) { }
    }

    /// <summary>
    /// An in-memory <see cref="IModelDocument"/> for tests, with an ordered effect log.
    ///
    /// The log is the point. Asserting final state proves a service reached the right answer;
    /// asserting the ordered effects proves it got there the right way — that it previewed before
    /// writing, that it did not write twice, that it re-resolved after a generation bump instead of
    /// reusing a handle. Those are the failures that survive state-only tests.
    /// </summary>
    public sealed class FakeDocument : IModelDocument
    {
        private readonly List<FakeItem> _items = new List<FakeItem>();
        private readonly List<FakeSource> _models = new List<FakeSource>();
        private readonly List<string> _log = new List<string>();
        private readonly Dictionary<string, List<ElementKey>> _sets =
            new Dictionary<string, List<ElementKey>>(StringComparer.OrdinalIgnoreCase);
        private List<ElementKey> _selection = new List<ElementKey>();

        /// <inheritdoc />
        public int Generation { get; private set; } = 1;

        /// <inheritdoc />
        public bool IsEmpty => _items.Count == 0;

        /// <inheritdoc />
        public IReadOnlyList<IModelSource> Models => _models;

        /// <summary>The ordered effect log. Cleared by <see cref="ClearLog"/>.</summary>
        public IReadOnlyList<string> Log => _log;

        internal void Record(string effect) => _log.Add(effect);

        /// <summary>Drop everything recorded so far, so a test can assert only what follows.</summary>
        public void ClearLog() => _log.Clear();

        /// <summary>Add a model and return it, for use as an item's owner.</summary>
        public FakeSource AddModel(string displayName, string sourcePath)
        {
            var source = new FakeSource(displayName, sourcePath);
            _models.Add(source);
            Record("model.add " + displayName);
            return source;
        }

        /// <summary>Add an element to a model.</summary>
        public FakeItem AddItem(
            FakeSource model,
            string displayName,
            string? category = null,
            string? typeName = null,
            BoundingBox bounds = default,
            bool hasGeometry = true,
            params string[] treePathAbove)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            var path = new List<string>(treePathAbove) { displayName };
            var key = ElementKey.FromTreePath(model.Scope, model.SourcePath, path.Select(p => (string?)p).ToArray());
            var item = new FakeItem(this, key, model, displayName, category, typeName, path, bounds, hasGeometry);
            _items.Add(item);
            return item;
        }

        /// <summary>Define a named set.</summary>
        public void DefineSet(string name, params ElementKey[] keys)
        {
            _sets[name] = new List<ElementKey>(keys);
            Record("set.define " + name + " n=" + keys.Length);
        }

        /// <summary>Set the host selection.</summary>
        public void Select(params ElementKey[] keys)
        {
            _selection = new List<ElementKey>(keys);
            Record("selection.set n=" + keys.Length);
        }

        /// <summary>
        /// Bump the generation, as an append or refresh would. Every item handed out before this
        /// call becomes stale and throws on next touch.
        /// </summary>
        public void BumpGeneration(string reason = "refresh")
        {
            Generation++;
            Record("document.generation " + Generation + " (" + reason + ")");
        }

        /// <summary>Remove an element, as a revision that deleted it would.</summary>
        public void RemoveItem(ElementKey key)
        {
            _items.RemoveAll(i => i.Key == key);
            Record("item.remove " + key);
        }

        /// <inheritdoc />
        public IModelItem? Resolve(ElementKey key)
        {
            var item = _items.FirstOrDefault(i => i.Key == key);
            Record("resolve " + key + (item == null ? " -> null" : " -> ok"));
            if (item == null) return null;
            item.RefreshGeneration(Generation);
            return item;
        }

        /// <inheritdoc />
        public IEnumerable<IModelItem> Traverse(TraversalScope scope)
        {
            Record("traverse " + scope);

            IEnumerable<FakeItem> chosen = scope.Kind switch
            {
                TraversalScopeKind.WholeDocument => _items,
                TraversalScopeKind.CurrentSelection => _items.Where(i => _selection.Contains(i.Key)),
                TraversalScopeKind.Set => _sets.TryGetValue(scope.SetName ?? string.Empty, out var keys)
                    ? _items.Where(i => keys.Contains(i.Key))
                    : Enumerable.Empty<FakeItem>(),
                TraversalScopeKind.Keys => _items.Where(i => scope.Keys!.Contains(i.Key)),
                _ => Enumerable.Empty<FakeItem>(),
            };

            foreach (var item in chosen)
            {
                item.RefreshGeneration(Generation);
                yield return item;
            }
        }

        /// <summary>Open a write transaction against this document.</summary>
        public IModelWriteTransaction BeginWrite(string description) => new FakeWriteTransaction(this, description);

        internal FakeItem? Find(ElementKey key) => _items.FirstOrDefault(i => i.Key == key);
    }

    /// <summary>A model in a <see cref="FakeDocument"/>.</summary>
    public sealed class FakeSource : IModelSource
    {
        internal FakeSource(string displayName, string sourcePath)
        {
            DisplayName = displayName;
            SourcePath = sourcePath;
            Scope = ElementKey.ScopeOf(sourcePath);
        }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public string SourcePath { get; }

        /// <inheritdoc />
        public string Scope { get; }
    }

    /// <summary>An element in a <see cref="FakeDocument"/>.</summary>
    public sealed class FakeItem : IModelItem
    {
        private readonly FakeDocument _document;
        private readonly Dictionary<string, string?> _properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        private int _resolvedAtGeneration;

        internal FakeItem(FakeDocument document, ElementKey key, FakeSource model, string displayName,
                          string? category, string? typeName, IReadOnlyList<string> treePath,
                          BoundingBox bounds, bool hasGeometry)
        {
            _document = document;
            Key = key;
            Model = model;
            DisplayName = displayName;
            Category = category;
            TypeName = typeName;
            TreePath = treePath;
            Bounds = bounds;
            HasGeometry = hasGeometry;
            _resolvedAtGeneration = document.Generation;
        }

        internal void RefreshGeneration(int generation) => _resolvedAtGeneration = generation;

        private void GuardStale()
        {
            if (_resolvedAtGeneration != _document.Generation)
                throw new StaleItemException(
                    "This item was resolved in generation " + _resolvedAtGeneration.ToString(CultureInfo.InvariantCulture) +
                    " but the document is now at " + _document.Generation.ToString(CultureInfo.InvariantCulture) +
                    ". Hold (generation, ElementKey) and re-resolve; never hold the item.");
        }

        /// <inheritdoc />
        public ElementKey Key { get; }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public string? Category { get; }

        /// <inheritdoc />
        public string? TypeName { get; }

        /// <inheritdoc />
        public IModelSource Model { get; }

        /// <inheritdoc />
        public IReadOnlyList<string> TreePath { get; }

        /// <inheritdoc />
        public bool HasGeometry { get; }

        /// <inheritdoc />
        public BoundingBox Bounds { get; }

        /// <inheritdoc />
        public bool IsHidden { get; internal set; }

        /// <summary>Seed a property, as the source model would carry it.</summary>
        public FakeItem WithProperty(string category, string name, string? value)
        {
            _properties[category + "/" + name] = value;
            return this;
        }

        /// <inheritdoc />
        public string? Property(string category, string name)
        {
            GuardStale();
            return _properties.TryGetValue(category + "/" + name, out var v) ? v : null;
        }

        /// <inheritdoc />
        public IEnumerable<PropertyValue> Properties()
        {
            GuardStale();
            foreach (var kv in _properties)
            {
                var slash = kv.Key.IndexOf('/');
                yield return new PropertyValue(kv.Key.Substring(0, slash), kv.Key.Substring(slash + 1), kv.Value);
            }
        }

        internal void ApplyWrite(string category, string name, string? value) =>
            _properties[category + "/" + name] = value;

        internal void ApplyRemove(string category, string name) =>
            _properties.Remove(category + "/" + name);

        internal bool Has(string category, string name) => _properties.ContainsKey(category + "/" + name);
    }
}
