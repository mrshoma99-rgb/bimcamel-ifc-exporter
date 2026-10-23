using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Data
{
    public static partial class RuleClassifier
    {
        /// <summary>How far up the tree to look. Revit's Element tab sits on the object a level or
        /// two above the geometry node Navisworks hands the exporter; past a few levels the
        /// ancestors are levels, files and layers, which say nothing about this element.</summary>
        private const int MaxAncestors = 3;

        /// <summary>
        /// Gathers the rule inputs from a Navisworks item and its nearest ancestors, then
        /// classifies. UI thread only (the property API is STA). Never throws — an unreadable node
        /// is simply not classified.
        /// </summary>
        /// <param name="unitScale">Model units → metres, for the shape veto.</param>
        /// <param name="categoryCache">Per-export memo of each node's Revit category: an element's
        /// geometry nodes share the element node, so its properties are read once, not per node.</param>
        public static Guess? ClassifyItem(ModelItem item, double unitScale, Dictionary<ModelItem, string?>? categoryCache = null)
        {
            try
            {
                var ifcTypes = new List<string?>();
                var names = new List<string?>();
                string? category = null;

                var node = item;
                for (int depth = 0; node != null && depth <= MaxAncestors; depth++)
                {
                    // The item itself is read fresh; its ancestors are shared by every sibling
                    // under them and come from the per-export memo.
                    var n = depth == 0 ? Read(node) : Ancestor(node);
                    // A layer is a level or a drawing layer ("Level 2 - Floor", "A-WALL"): its name
                    // describes many elements, not this one, and above it is only the file.
                    if (n.Stop) break;
                    ifcTypes.Add(n.ClassName);
                    ifcTypes.Add(n.ClassDisplayName);
                    if (category == null)
                    {
                        if (categoryCache == null || !categoryCache.TryGetValue(node, out category))
                        {
                            category = RevitCategory(node);
                            if (categoryCache != null) categoryCache[node] = category;
                        }
                    }
                    // Family/type names first (Revit's item type is the family name), then the
                    // node's own name.
                    names.Add(n.ClassDisplayName);
                    names.Add(n.DisplayName);
                    if (n.HasModel) break;             // the file node: nothing above it is this element's
                    node = n.Parent;
                }

                (double, double, double)? size = null;
                var bb = Geometry.ItemBox.Of(item);
                if (bb != null && !bb.IsEmpty)
                    size = ((bb.Max.X - bb.Min.X) * unitScale, (bb.Max.Y - bb.Min.Y) * unitScale, (bb.Max.Z - bb.Min.Z) * unitScale);

                return Classify(ifcTypes, category, names, size);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>What the walk above reads from one node: six COM reads, taken together.</summary>
        private sealed class NodeInfo
        {
            public bool Stop, HasModel;
            public string? ClassName, ClassDisplayName, DisplayName;
            public ModelItem? Parent;
        }

        private static NodeInfo Read(ModelItem node)
        {
            var n = new NodeInfo { ClassName = node.ClassName };
            n.Stop = node.IsLayer || IsSpatial(n.ClassName);
            if (n.Stop) return n;
            n.ClassDisplayName = node.ClassDisplayName;
            n.DisplayName = node.DisplayName;
            n.HasModel = node.HasModel;
            if (!n.HasModel) n.Parent = node.Parent;
            return n;
        }

        /// <summary>
        /// Ancestors seen this export. Auto-classify runs on every unmapped element — 128k of them
        /// on a large E3D plant model — and walked up to three ancestors for each, re-reading the same
        /// branch and pipe nodes for every primitive under them. Bounded: cleared when full, which
        /// costs only re-reads. Matched by value (ModelItem equality), cleared by
        /// <see cref="Reset"/> at the start of each export.
        /// </summary>
        private static readonly Dictionary<ModelItem, NodeInfo> _ancestors = new Dictionary<ModelItem, NodeInfo>();
        private const int AncestorCap = 200_000;

        private static NodeInfo Ancestor(ModelItem node)
        {
            if (_ancestors.TryGetValue(node, out var n)) return n;
            if (_ancestors.Count >= AncestorCap) _ancestors.Clear();
            return _ancestors[node] = Read(node);
        }

        /// <summary>IFC spatial containers ("IfcBuildingStorey" named "First Floor"): same reason
        /// as layers.</summary>
        private static bool IsSpatial(string? className) =>
            className != null && (className.StartsWith("IfcBuildingStorey", StringComparison.OrdinalIgnoreCase)
                                  || className.StartsWith("IfcBuilding", StringComparison.OrdinalIgnoreCase) && !className.StartsWith("IfcBuildingElement", StringComparison.OrdinalIgnoreCase)
                                  || className.StartsWith("IfcSite", StringComparison.OrdinalIgnoreCase)
                                  || className.StartsWith("IfcProject", StringComparison.OrdinalIgnoreCase)
                                  || className.StartsWith("IfcSpace", StringComparison.OrdinalIgnoreCase)
                                  || className.StartsWith("IfcFacility", StringComparison.OrdinalIgnoreCase));

        /// <summary>Revit's Element › Category, by internal name (language-independent) or by its
        /// English display names.</summary>
        private static string? RevitCategory(ModelItem node)
        {
            // Native lookups, not an enumeration of every category: on a model that is not from
            // Revit (E3D, IFC, DWG) there is no Element tab at all, and walking each unmapped
            // item's whole PropertyCategories collection to learn that was most of this call.
            var cats = node.PropertyCategories;
            var cat = cats.FindCategoryByName("LcRevitData_Element") ?? cats.FindCategoryByDisplayName("Element");
            if (cat == null) return null;
            var p = cat.Properties.FindPropertyByName("LcRevitPropertyElementCategory")
                    ?? cat.Properties.FindPropertyByDisplayName("Category");
            if (p == null) return null;
            var v = PropertyHarvester.ValueToString(p.Value);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }
}
