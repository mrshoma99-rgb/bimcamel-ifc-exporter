using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using CamelWorks.Core.Abstractions;

namespace CamelWorks.Nav
{
    /// <summary>
    /// The document's saved viewpoints.
    ///
    /// Identified by their folder path rather than by index. The host's own collection renumbers
    /// whenever anything is added or removed, so an index captured a moment ago points somewhere
    /// else by the time it is used — which is how a "go to viewpoint" button ends up showing the
    /// wrong view.
    ///
    /// <b>Main thread only.</b>
    /// </summary>
    public sealed class NavViewpointStore : IViewpointStore
    {
        private readonly NavDocument _document;

        /// <summary>Wrap a document.</summary>
        public NavViewpointStore(NavDocument document) =>
            _document = document ?? throw new ArgumentNullException(nameof(document));

        private Document Host => _document.Document;

        /// <inheritdoc />
        public IReadOnlyList<SavedView> All()
        {
            var views = new List<SavedView>();

            try
            {
                Collect(Host.SavedViewpoints.ToSavedItemCollection(), null, views);
            }
            catch (Exception)
            {
                // A document with no saved viewpoints. Not an error.
            }

            return views;
        }

        /// <inheritdoc />
        public SavedView SaveCurrent(string name, string? folder = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a viewpoint needs a name", nameof(name));

            var copy = (SavedViewpoint)Host.CurrentViewpoint.CreateCopy();
            copy.DisplayName = name;

            Host.SavedViewpoints.AddCopy(copy);

            return new SavedView(PathOf(folder, name), name, folder, false, false);
        }

        /// <inheritdoc />
        public void Apply(string viewId)
        {
            var found = Find(viewId);
            if (found == null) return;

            Host.SavedViewpoints.CurrentSavedViewpoint = found;
        }

        /// <inheritdoc />
        public void Rename(string viewId, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("a viewpoint needs a name", nameof(newName));

            var found = Find(viewId);
            if (found == null) return;

            // The host has no rename: a SavedItem's name is fixed once it is in the collection, and
            // the only editable form is a copy. Remove-then-add is therefore the whole operation,
            // and it moves the viewpoint to the end of its folder — a real cost, worth stating
            // rather than hiding, since the alternative is not offering rename at all.
            var copy = (SavedViewpoint)found.CreateCopy();
            copy.DisplayName = newName;

            Host.SavedViewpoints.Remove(found);
            Host.SavedViewpoints.AddCopy(copy);
        }

        /// <inheritdoc />
        public void Delete(string viewId)
        {
            var found = Find(viewId);
            if (found == null) return;

            Host.SavedViewpoints.Remove(found);
        }

        private SavedViewpoint? Find(string viewId)
        {
            if (string.IsNullOrWhiteSpace(viewId)) return null;

            SavedViewpoint? match = null;
            Walk(Host.SavedViewpoints.ToSavedItemCollection(), null, (item, path) =>
            {
                if (match == null && string.Equals(path, viewId, StringComparison.OrdinalIgnoreCase)) match = item;
            });

            return match;
        }

        private static void Collect(SavedItemCollection items, string? folder, List<SavedView> views)
        {
            Walk(items, folder, (item, path) =>
                views.Add(new SavedView(path, item.DisplayName ?? string.Empty, FolderOf(path), false, false)));
        }

        private static void Walk(SavedItemCollection items, string? folder, Action<SavedViewpoint, string> visit)
        {
            foreach (SavedItem item in items)
            {
                if (item is SavedViewpoint viewpoint)
                {
                    visit(viewpoint, PathOf(folder, viewpoint.DisplayName ?? string.Empty));
                }
                else if (item is FolderItem nested && nested.Children != null)
                {
                    Walk(nested.Children, PathOf(folder, nested.DisplayName ?? string.Empty), visit);
                }
            }
        }

        private static string PathOf(string? folder, string name) =>
            string.IsNullOrEmpty(folder) ? name : folder + "/" + name;

        private static string? FolderOf(string path)
        {
            var cut = path.LastIndexOf('/');
            return cut < 0 ? null : path.Substring(0, cut);
        }
    }
}
