using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;

namespace CamelWorks.Nav
{
    /// <summary>
    /// Property writes, batched and previewed before anything touches the document.
    ///
    /// Nothing is written until <see cref="Commit"/>. That is not tidiness: writing properties goes
    /// through the COM bridge, one call per element, and a half-applied batch on a federation is
    /// not something anybody can undo by hand. So the whole batch is accumulated, previewed —
    /// including which keys no longer resolve — and then applied in one pass, or not at all.
    ///
    /// <b>Main thread only.</b>
    /// </summary>
    public sealed class NavWriteTransaction : IModelWriteTransaction
    {
        private readonly NavDocument _document;
        private readonly Dictionary<string, List<Write>> _writes = new Dictionary<string, List<Write>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ElementKey> _keys = new Dictionary<string, ElementKey>(StringComparer.Ordinal);
        private bool _finished;

        /// <summary>Begin a batch.</summary>
        /// <param name="document">The document to write to.</param>
        /// <param name="description">What the batch is for, shown in any progress or error message.</param>
        public NavWriteTransaction(NavDocument document, string description)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            Description = string.IsNullOrWhiteSpace(description) ? "CamelWorks" : description;
        }

        /// <summary>
        /// The property tab written to.
        ///
        /// One tab, named for the product, and never mixed into the host's own or the authoring
        /// tool's categories. A coordinator has to be able to tell at a glance which properties
        /// came from here — and be able to ignore all of them at once when comparing two exports.
        /// </summary>
        public const string TabName = "CamelWorks";

        /// <inheritdoc />
        public string Description { get; }

        /// <inheritdoc />
        public void SetProperty(ElementKey key, string category, string name, string? value)
        {
            if (category == null) throw new ArgumentNullException(nameof(category));
            if (name == null) throw new ArgumentNullException(nameof(name));

            Record(key, new Write(category, name, value, remove: false));
        }

        /// <inheritdoc />
        public void RemoveProperty(ElementKey key, string category, string name)
        {
            if (category == null) throw new ArgumentNullException(nameof(category));
            if (name == null) throw new ArgumentNullException(nameof(name));

            Record(key, new Write(category, name, null, remove: true));
        }

        /// <inheritdoc />
        public WritePreview Preview()
        {
            var unresolved = new List<ElementKey>();
            var resolved = 0;
            var sets = 0;
            var removals = 0;

            foreach (var pair in _writes)
            {
                var key = _keys[pair.Key];

                if (_document.Resolve(key) == null)
                {
                    // Named rather than dropped. A batch that silently skipped the elements it
                    // could not find would report success and change less than it said.
                    unresolved.Add(key);
                    continue;
                }

                resolved++;
                sets += pair.Value.Count(w => !w.Remove);
                removals += pair.Value.Count(w => w.Remove);
            }

            return new WritePreview(resolved, sets, removals, unresolved);
        }

        /// <inheritdoc />
        public int Commit()
        {
            if (_finished) throw new InvalidOperationException("this transaction has already been finished");

            var written = 0;

            using (var transaction = _document.Document.BeginTransaction(Description))
            {
                foreach (var pair in _writes)
                {
                    var key = _keys[pair.Key];
                    var item = _document.Resolve(key) as NavModelItem;
                    if (item == null) continue;

                    if (Apply(item.Item, pair.Value)) written++;
                }

                transaction.Commit();
            }

            _finished = true;
            return written;
        }

        /// <inheritdoc />
        public void Rollback()
        {
            _writes.Clear();
            _keys.Clear();
            _finished = true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Disposing without committing rolls back. A batch abandoned by an exception must not
            // be half-applied, and must not be applied later by accident.
            if (!_finished) Rollback();
        }

        private void Record(ElementKey key, Write write)
        {
            if (_finished) throw new InvalidOperationException("this transaction has already been finished");
            if (key.IsEmpty) return;

            var id = key.ToString();
            _keys[id] = key;

            if (!_writes.TryGetValue(id, out var list))
            {
                list = new List<Write>();
                _writes[id] = list;
            }

            // Last write wins on the same property, so a service that revises its mind mid-batch
            // does not produce two conflicting values on one element.
            list.RemoveAll(w => string.Equals(w.Category, write.Category, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(w.Name, write.Name, StringComparison.OrdinalIgnoreCase));
            list.Add(write);
        }

        /// <summary>
        /// The one call that actually touches the host.
        ///
        /// Isolated deliberately: everything above is ordinary logic that can be reasoned about,
        /// and this is the COM bridge, which cannot. It writes the whole tab in one go because the
        /// bridge replaces a user-defined tab wholesale rather than merging into it — so a removal
        /// is simply an omission from the rebuilt tab, and the existing values have to be read back
        /// and re-sent alongside the new ones.
        /// </summary>
        private static bool Apply(ModelItem item, List<Write> writes)
        {
            try
            {
                var state = ComApiBridge.State;
                var path = ComApiBridge.ToInwOaPath(item);

                var vector = (InwOaPropertyVec)state.ObjectFactory(
                    nwEObjectType.eObjectType_nwOaPropertyVec, null, null);

                foreach (var write in Merge(item, writes))
                {
                    var property = (InwOaProperty)state.ObjectFactory(
                        nwEObjectType.eObjectType_nwOaProperty, null, null);

                    property.name = write.Name;
                    property.UserName = write.Name;
                    property.value = write.Value;

                    vector.Properties().Add(property);
                }

                // SetUserDefined is on the GUI property node, not on the state. The node is the
                // per-item property panel, and asking for it with create:true is what makes a tab
                // exist on an element that has none.
                var node = (InwGUIPropertyNode2)state.GetGUIPropertyNode(path, true);
                node.SetUserDefined(0, TabName, TabName, vector);
                return true;
            }
            catch (Exception)
            {
                // One element the bridge refuses must not take the batch with it. The count
                // returned by Commit is what the caller reports, and it will be short by this one.
                return false;
            }
        }

        // The bridge replaces the tab rather than merging, so whatever is already in it has to be
        // carried forward — otherwise setting one property silently deletes every other one that
        // CamelWorks wrote earlier.
        private static IEnumerable<Write> Merge(ModelItem item, List<Write> writes)
        {
            var merged = new List<Write>();

            foreach (var category in item.PropertyCategories)
            {
                var categoryName = category.DisplayName ?? category.Name;
                if (!string.Equals(categoryName, TabName, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var property in category.Properties)
                {
                    var name = property.DisplayName ?? property.Name;
                    if (string.IsNullOrEmpty(name)) continue;

                    merged.Add(new Write(TabName, name!, NavValues.ToText(property.Value), remove: false));
                }
            }

            foreach (var write in writes)
            {
                merged.RemoveAll(w => string.Equals(w.Name, write.Name, StringComparison.OrdinalIgnoreCase));
                if (!write.Remove) merged.Add(write);
            }

            return merged.Where(w => w.Value != null);
        }

        private readonly struct Write
        {
            internal Write(string category, string name, string? value, bool remove)
            {
                Category = category; Name = name; Value = value; Remove = remove;
            }

            internal string Category { get; }

            internal string Name { get; }

            internal string? Value { get; }

            internal bool Remove { get; }
        }
    }
}
