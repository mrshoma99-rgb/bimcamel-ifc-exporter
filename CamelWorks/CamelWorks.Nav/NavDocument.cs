using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;
using NavColour = Autodesk.Navisworks.Api.Color;

namespace CamelWorks.Nav
{
    /// <summary>One loaded model.</summary>
    public sealed class NavModelSource : IModelSource
    {
        internal NavModelSource(Model model)
        {
            Model = model;
            SourcePath = NavKeys.SourcePathOf(model) ?? string.Empty;
            Scope = NavKeys.ScopeOf(model);
            DisplayName = System.IO.Path.GetFileName(SourcePath);
            if (string.IsNullOrEmpty(DisplayName)) DisplayName = "(unnamed model)";
        }

        /// <summary>The host model.</summary>
        public Model Model { get; }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public string SourcePath { get; }

        /// <inheritdoc />
        public string Scope { get; }
    }

    /// <summary>One element.</summary>
    public sealed class NavModelItem : IModelItem
    {
        private readonly NavDocument _document;
        private readonly int _generation;

        internal NavModelItem(NavDocument document, ModelItem item, NavModelSource model)
        {
            _document = document;
            _generation = document.Generation;
            Item = item;
            Model = model;
            Key = NavKeys.Of(item);
        }

        /// <summary>The host item.</summary>
        public ModelItem Item { get; }

        /// <inheritdoc />
        public ElementKey Key { get; }

        /// <inheritdoc />
        public IModelSource Model { get; }

        /// <inheritdoc />
        public string DisplayName => Fresh().DisplayName ?? string.Empty;

        /// <inheritdoc />
        public string? Category => Fresh().ClassDisplayName ?? Fresh().ClassName;

        /// <inheritdoc />
        public string? TypeName
        {
            get
            {
                // The host has no single "type" concept, so the two places a type name actually
                // turns up are tried in the order they are usually right in.
                var type = Property("Item", "Type");
                if (!string.IsNullOrWhiteSpace(type)) return type;

                return Fresh().ClassName;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> TreePath => NavKeys.TreePathOf(Fresh());

        /// <inheritdoc />
        public bool HasGeometry => Fresh().HasGeometry;

        /// <inheritdoc />
        public BoundingBox Bounds => NavKeys.BoundsOf(Fresh());

        /// <inheritdoc />
        public bool IsHidden => Fresh().IsHidden;

        /// <inheritdoc />
        public string? Property(string category, string name)
        {
            if (category == null || name == null) return null;

            foreach (var value in Properties())
                if (string.Equals(value.Category, category, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
                    return value.Value;

            return null;
        }

        /// <inheritdoc />
        public IEnumerable<PropertyValue> Properties()
        {
            foreach (var category in Fresh().PropertyCategories)
            {
                var categoryName = category.DisplayName ?? category.Name ?? "Properties";

                foreach (var property in category.Properties)
                {
                    var propertyName = property.DisplayName ?? property.Name;
                    if (string.IsNullOrEmpty(propertyName)) continue;

                    yield return new PropertyValue(categoryName, propertyName!, NavValues.ToText(property.Value));
                }
            }
        }

        /// <inheritdoc />
        public override string ToString() => DisplayName;

        // Every read checks the generation first. A ModelItem held across a document change is a
        // handle into a tree that no longer exists, and the host's behaviour then ranges from
        // wrong answers to a hard crash — so this throws instead, and the caller re-resolves by
        // key, which is exactly what keys are for.
        private ModelItem Fresh()
        {
            if (_document.Generation != _generation)
                throw new StaleItemException(
                    "this element was read before the document changed; resolve it again by key");

            return Item;
        }
    }

    /// <summary>
    /// The active document, behind the seam.
    ///
    /// <b>Main thread only.</b> Everything here goes through the host's single-threaded read API,
    /// and the seam exists precisely so that nothing above it has to know that.
    /// </summary>
    public sealed class NavDocument : IModelDocument
    {
        private readonly Dictionary<string, ModelItem> _index = new Dictionary<string, ModelItem>(StringComparer.Ordinal);
        private readonly List<NavModelSource> _models = new List<NavModelSource>();
        private bool _indexed;

        /// <summary>Wrap a host document.</summary>
        public NavDocument(Document document)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Rescan();
        }

        /// <summary>The host document.</summary>
        public Document Document { get; }

        /// <inheritdoc />
        public int Generation { get; private set; }

        /// <inheritdoc />
        public bool IsEmpty => _models.Count == 0;

        /// <inheritdoc />
        public IReadOnlyList<IModelSource> Models => _models;

        /// <summary>
        /// Note that the document has changed: a model appended, removed, or refreshed.
        ///
        /// Bumps the generation, which invalidates every item handed out so far. The plug-in calls
        /// this from the host's document-changed events; nothing detects it automatically, because
        /// a stale index that looks fresh is worse than one that says it is stale.
        /// </summary>
        public void Invalidate()
        {
            Generation++;
            _indexed = false;
            _index.Clear();
            Rescan();
        }

        /// <inheritdoc />
        public IModelItem? Resolve(ElementKey key)
        {
            if (key.IsEmpty) return null;

            EnsureIndexed();

            if (!_index.TryGetValue(key.ToString(), out var item)) return null;

            var model = ModelFor(item);
            return model == null ? null : new NavModelItem(this, item, model);
        }

        /// <inheritdoc />
        public IEnumerable<IModelItem> Traverse(TraversalScope scope)
        {
            switch (scope.Kind)
            {
                case TraversalScopeKind.CurrentSelection:
                    return Wrap(Leaves(Document.CurrentSelection.SelectedItems));

                case TraversalScopeKind.Set:
                    return Wrap(Leaves(ItemsOfSet(scope.SetName)));

                case TraversalScopeKind.Keys:
                    return ResolveMany(scope.Keys);

                default:
                    return Wrap(Leaves(Document.Models.RootItems));
            }
        }

        /// <summary>Every saved selection and search set in the document, folders included.</summary>
        public IReadOnlyList<SelectionSet> Sets()
        {
            var sets = new List<SelectionSet>();

            try
            {
                Collect(Document.SelectionSets.ToSavedItemCollection(), sets);
            }
            catch (Exception)
            {
                // A document with no sets at all. Not an error, and not worth a message.
            }

            return sets;
        }

        /// <summary>The host items behind a key list, in the order given, skipping what no longer resolves.</summary>
        public ModelItemCollection ItemsFor(IReadOnlyList<ElementKey> keys)
        {
            var collection = new ModelItemCollection();
            if (keys == null) return collection;

            EnsureIndexed();

            foreach (var key in keys)
                if (!key.IsEmpty && _index.TryGetValue(key.ToString(), out var item))
                    collection.Add(item);

            return collection;
        }

        private IEnumerable<IModelItem> ResolveMany(IReadOnlyList<ElementKey>? keys)
        {
            if (keys == null) yield break;

            foreach (var key in keys)
            {
                var item = Resolve(key);
                if (item != null) yield return item;
            }
        }

        private IEnumerable<IModelItem> Wrap(IEnumerable<ModelItem> items)
        {
            foreach (var item in items)
            {
                var model = ModelFor(item);
                if (model != null) yield return new NavModelItem(this, item, model);
            }
        }

        private IEnumerable<ModelItem> ItemsOfSet(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Enumerable.Empty<ModelItem>();

            var set = Sets().FirstOrDefault(s => string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            if (set == null) return Enumerable.Empty<ModelItem>();

            try
            {
                // A search set has to be re-run; a selection set already holds its items. Both live
                // in the same collection and only this distinguishes them.
                return set.Search != null
                    ? set.Search.FindAll(Document, false).Cast<ModelItem>()
                    : set.GetSelectedItems().Cast<ModelItem>();
            }
            catch (Exception)
            {
                return Enumerable.Empty<ModelItem>();
            }
        }

        private NavModelSource? ModelFor(ModelItem item)
        {
            var model = item.Model;
            if (model == null) return null;

            foreach (var source in _models)
                if (ReferenceEquals(source.Model, model)) return source;

            // A model that appeared without the generation being bumped. Rescanning here rather
            // than dropping the item, since losing elements silently is the worse failure.
            Rescan();

            foreach (var source in _models)
                if (ReferenceEquals(source.Model, model)) return source;

            return null;
        }

        private void Rescan()
        {
            _models.Clear();

            foreach (Model model in Document.Models)
                _models.Add(new NavModelSource(model));
        }

        private void EnsureIndexed()
        {
            if (_indexed) return;

            _index.Clear();

            // Indexed once and kept: keying an item is not free, and resolving a thousand-row clash
            // board one key at a time against an unindexed tree is the difference between a board
            // that opens and one that does not.
            foreach (var item in Leaves(Document.Models.RootItems))
            {
                var key = NavKeys.Of(item).ToString();
                if (key.Length > 0 && !_index.ContainsKey(key)) _index[key] = item;
            }

            _indexed = true;
        }

        private static void Collect(SavedItemCollection items, List<SelectionSet> result)
        {
            foreach (SavedItem item in items)
            {
                if (item is SelectionSet set) result.Add(set);
                else if (item is FolderItem folder && folder.Children != null) Collect(folder.Children, result);
            }
        }

        // Geometry-bearing nodes, taking a node's own geometry when nothing beneath it produced
        // any. Some formats hang reference planes and annotations under a part that carries the
        // mesh itself, and the naive "only childless nodes" rule loses the entire part.
        private static IEnumerable<ModelItem> Leaves(IEnumerable<ModelItem> roots)
        {
            foreach (var root in roots)
                foreach (var leaf in LeavesOf(root))
                    yield return leaf;
        }

        private static IEnumerable<ModelItem> LeavesOf(ModelItem item)
        {
            var any = false;

            foreach (var child in item.Children)
                foreach (var leaf in LeavesOf(child))
                {
                    any = true;
                    yield return leaf;
                }

            if (!any && item.HasGeometry) yield return item;
        }
    }

    /// <summary>Turns the host's variant values into text without throwing.</summary>
    public static class NavValues
    {
        /// <summary>
        /// A property value as text.
        ///
        /// Every branch is guarded. A federation contains property values the host itself cannot
        /// render, and one of them must not stop a traversal of two hundred thousand elements.
        /// </summary>
        public static string? ToText(VariantData value)
        {
            if (value == null) return null;

            try
            {
                if (value.IsDisplayString) return value.ToDisplayString();
                if (value.IsNamedConstant) return value.ToNamedConstant().DisplayName;
                if (value.IsBoolean) return value.ToBoolean() ? "Yes" : "No";
                if (value.IsInt32) return value.ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (value.IsDouble) return value.ToDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                if (value.IsDateTime) return value.ToDateTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

                return value.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A host colour as a CamelWorks one.</summary>
        public static Colour ToColour(NavColour colour) =>
            new Colour(Channel(colour.R), Channel(colour.G), Channel(colour.B));

        /// <summary>A CamelWorks colour as a host one.</summary>
        public static NavColour FromColour(Colour colour) =>
            NavColour.FromByteRGB(colour.R, colour.G, colour.B);

        private static byte Channel(double value)
        {
            var scaled = Math.Round(value * 255, MidpointRounding.AwayFromZero);
            if (scaled < 0) return 0;
            if (scaled > 255) return 255;
            return (byte)scaled;
        }
    }
}
