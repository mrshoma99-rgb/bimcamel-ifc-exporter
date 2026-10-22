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
                    // A layer is a level or a drawing layer ("Level 2 - Floor", "A-WALL"): its name
                    // describes many elements, not this one, and above it is only the file.
                    if (node.IsLayer || IsSpatial(node.ClassName)) break;
                    ifcTypes.Add(node.ClassName);
                    ifcTypes.Add(node.ClassDisplayName);
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
                    names.Add(node.ClassDisplayName);
                    names.Add(node.DisplayName);
                    if (node.HasModel) break;          // the file node: nothing above it is this element's
                    node = node.Parent;
                }

                (double, double, double)? size = null;
                var bb = item.BoundingBox();
                if (bb != null && !bb.IsEmpty)
                    size = ((bb.Max.X - bb.Min.X) * unitScale, (bb.Max.Y - bb.Min.Y) * unitScale, (bb.Max.Z - bb.Min.Z) * unitScale);

                return Classify(ifcTypes, category, names, size);
            }
            catch
            {
                return null;
            }
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
            foreach (var cat in node.PropertyCategories)
            {
                bool element = string.Equals(cat.Name, "LcRevitData_Element", StringComparison.Ordinal)
                               || string.Equals(cat.DisplayName, "Element", StringComparison.OrdinalIgnoreCase);
                if (!element) continue;
                foreach (var p in cat.Properties)
                {
                    if (string.Equals(p.Name, "LcRevitPropertyElementCategory", StringComparison.Ordinal)
                        || string.Equals(p.DisplayName, "Category", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = PropertyHarvester.ValueToString(p.Value);
                        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                    }
                }
            }
            return null;
        }
    }
}
