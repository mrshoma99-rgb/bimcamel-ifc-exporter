using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using CamelWorks.Core.Identity;

namespace CamelWorks.Nav
{
    /// <summary>
    /// Turns a host item into an <see cref="ElementKey"/>, walking down the three rungs until one
    /// of them works.
    ///
    /// The rungs are tried in order of strength, never mixed: an instance GUID is a fact, a tree
    /// path is usually right, and a geometry signature is a proposal. Which rung a key rests on
    /// travels with it, so everything downstream can weigh a match rather than assume one.
    ///
    /// <b>Main thread only.</b> Every read here goes through the host's single-threaded API.
    /// </summary>
    public static class NavKeys
    {
        /// <summary>The scope for a model — the hash of its source path.</summary>
        public static string ScopeOf(Model model) =>
            ElementKey.ScopeOf(model == null ? null : SourcePathOf(model));

        /// <summary>
        /// Where a model was loaded from.
        ///
        /// <c>SourceFileName</c> in preference to <c>FileName</c>: an appended NWD keeps the
        /// original CAD path in the former and the cache path in the latter, and a key scoped to a
        /// cache path changes the moment somebody re-caches.
        /// </summary>
        public static string? SourcePathOf(Model model)
        {
            if (model == null) return null;

            var source = model.SourceFileName;
            return string.IsNullOrWhiteSpace(source) ? model.FileName : source;
        }

        /// <summary>Build the strongest key this item supports.</summary>
        public static ElementKey Of(ModelItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var model = item.Model;
            var scope = ScopeOf(model);
            var path = SourcePathOf(model);

            // Rung 1. An instance GUID survives anything that preserves the element, so it is worth
            // asking for even though plenty of formats never supply one.
            var guid = InstanceGuidOf(item);
            if (guid != Guid.Empty) return ElementKey.FromInstanceGuid(scope, guid);

            // Rung 2. The tree path is stable across a re-export that did not restructure the
            // model, which is the common case.
            var treePath = TreePathOf(item);
            if (treePath.Count > 0) return ElementKey.FromTreePath(scope, path, ToArray(treePath));

            // Rung 3. Weak by construction and says so: this cannot separate two identical parts,
            // and a match on it is a proposal rather than a fact.
            var bounds = BoundsOf(item);
            return ElementKey.FromGeometry(
                scope,
                item.ClassDisplayName ?? item.ClassName,
                item.DisplayName,
                bounds.SizeX, bounds.SizeY, bounds.SizeZ);
        }

        /// <summary>
        /// The item's instance GUID, or <see cref="Guid.Empty"/> when it has none.
        ///
        /// Guarded because the host throws on some item kinds rather than returning empty, and one
        /// unusual node in a federation should not stop the whole document being keyed.
        /// </summary>
        public static Guid InstanceGuidOf(ModelItem item)
        {
            try
            {
                return item.InstanceGuid;
            }
            catch (Exception)
            {
                return Guid.Empty;
            }
        }

        /// <summary>Display names from the model root down to this item.</summary>
        public static IReadOnlyList<string> TreePathOf(ModelItem item)
        {
            var path = new List<string>();

            for (var node = item; node != null; node = node.Parent)
            {
                var name = node.DisplayName;
                path.Add(string.IsNullOrEmpty(name) ? node.ClassDisplayName ?? string.Empty : name);
            }

            path.Reverse();
            return path;
        }

        /// <summary>
        /// The item's bounding box, or an empty one when it has no geometry.
        ///
        /// Guarded for the same reason as the GUID: asking a node with nothing under it for a box
        /// throws in some host versions, and the answer we want in that case is "no size" rather
        /// than a failed traversal.
        /// </summary>
        public static CamelWorks.Core.Abstractions.BoundingBox BoundsOf(ModelItem item)
        {
            try
            {
                var box = item.BoundingBox();
                if (box == null) return default;

                return new CamelWorks.Core.Abstractions.BoundingBox(
                    box.Min.X, box.Min.Y, box.Min.Z,
                    box.Max.X, box.Max.Y, box.Max.Z);
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static string?[] ToArray(IReadOnlyList<string> path)
        {
            var array = new string?[path.Count];
            for (var i = 0; i < path.Count; i++) array[i] = path[i];
            return array;
        }
    }
}
