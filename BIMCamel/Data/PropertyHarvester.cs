using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Data
{
    public enum PropKind { Text, Real, Integer, Boolean }

    /// <summary>A single property destined for an IfcPropertySet, with an IFC value kind.</summary>
    public sealed class IfcProp
    {
        public string Pset = "";
        public string Name = "";
        public string Value = "";        // text; for Boolean "T"/"F"; for Real/Integer the invariant number
        public PropKind Kind = PropKind.Text;
    }

    /// <summary>Surface colour (0–1) + transparency (0 opaque … 1 transparent).</summary>
    public sealed class Material
    {
        public double R, G, B, Transparency;
    }

    /// <summary>A category-qualified property reference (Category may be blank = match any category).</summary>
    public struct PropRef
    {
        public string Category;
        public string Name;
        public bool IsSet => !string.IsNullOrWhiteSpace(Name);
        public PropRef(string category, string name) { Category = category ?? ""; Name = name ?? ""; }
    }

    /// <summary>Which source property feeds each IFC semantic role (category-qualified).</summary>
    public sealed class PropertyRoles
    {
        public PropRef Type;          // → IfcElementType grouping + occurrence ObjectType
        public PropRef Level;         // → IfcBuildingStorey
        public PropRef Material;      // → IfcMaterial
        public PropRef Classification;// → IfcClassificationReference
        public bool Any => Type.IsSet || Level.IsSet || Material.IsSet || Classification.IsSet;
    }

    /// <summary>Role values read from one element.</summary>
    public struct RoleValues { public string Type, Level, Material, Classification; }

    /// <summary>
    /// Harvests Navisworks properties and colour for an element (F4 / F8). Property loop is the
    /// proven pattern from NavisworksExporter.ElementCollector. Values are typed (real/int/bool/
    /// text) so the IFC carries proper IfcValue types. UI-thread only.
    /// </summary>
    public static class PropertyHarvester
    {
        /// <summary>Harvest props; if <paramref name="include"/> is non-null, only those Pset (category) names.</summary>
        public static List<IfcProp> Harvest(ModelItem item, HashSet<string>? include = null)
        {
            var list = new List<IfcProp>();
            try
            {
                foreach (var cat in item.PropertyCategories)
                {
                    string pset = cat.DisplayName ?? cat.Name ?? "Properties";
                    if (include != null && !include.Contains(pset)) continue;
                    foreach (var p in cat.Properties)
                    {
                        string name = p.DisplayName ?? p.Name ?? "";
                        if (string.IsNullOrEmpty(name)) continue;
                        list.Add(Typed(pset, name, p.Value));
                    }
                }
            }
            catch { /* tolerate odd nodes */ }
            return list;
        }

        /// <summary>Distinct property-category (Pset) names found across a sample of items.</summary>
        public static List<string> ScanCategories(IEnumerable<ModelItem> items, int cap = 4000, Action<int>? onProgress = null)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    foreach (var cat in item.PropertyCategories)
                    {
                        var name = cat.DisplayName ?? cat.Name;
                        if (!string.IsNullOrEmpty(name)) set.Add(name!);
                    }
                }
                catch { }
                n++; onProgress?.Invoke(n);
                if (n >= cap) break;
            }
            return set.ToList();
        }

        /// <summary>Distinct element names + property values across a sample — used to populate
        /// the mapping-rule keyword autocomplete so users pick real values instead of guessing.</summary>
        public static List<string> ScanValues(IEnumerable<ModelItem> items, int itemCap = 4000, int valueCap = 4000, Action<int>? onProgress = null)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    if (!string.IsNullOrEmpty(item.DisplayName)) set.Add(item.DisplayName);
                    if (!string.IsNullOrEmpty(item.ClassDisplayName)) set.Add(item.ClassDisplayName);
                    foreach (var cat in item.PropertyCategories)
                        foreach (var p in cat.Properties)
                        {
                            var v = Typed("", "", p.Value).Value;
                            if (!string.IsNullOrEmpty(v) && v.Length <= 60) set.Add(v);
                            if (set.Count > valueCap) { onProgress?.Invoke(n + 1); return set.ToList(); }
                        }
                }
                catch { }
                n++; onProgress?.Invoke(n);
                if (n >= itemCap) break;
            }
            return set.ToList();
        }

        /// <summary>Distinct property (DataProperty) display names across a sample — for the role dropdowns.</summary>
        public static List<string> ScanPropertyNames(IEnumerable<ModelItem> items, int cap = 1000)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    foreach (var cat in item.PropertyCategories)
                        foreach (var p in cat.Properties)
                        {
                            var name = p.DisplayName ?? p.Name;
                            if (!string.IsNullOrEmpty(name)) set.Add(name!);
                        }
                }
                catch { }
                if (++n >= cap) break;
            }
            return set.ToList();
        }

        // Best-guess role property names (first candidate that exists in the model).
        private static readonly string[] TypeCandidates = { "Type", "Family and Type", "Family Type", "Type Name", "Type Mark" };
        private static readonly string[] LevelCandidates = { "Level", "Base Level", "Reference Level", "Schedule Level", "Base Constraint" };
        private static readonly string[] MaterialCandidates = { "Material", "Structural Material", "Material Name", "Materials" };
        private static readonly string[] ClassCandidates = { "Assembly Code", "Classification", "Uniclass", "OmniClass", "Keynote", "Assembly Description" };

        /// <summary>Distinct property names grouped by category — for dependent category/parameter dropdowns.</summary>
        public static Dictionary<string, List<string>> ScanCategoryParams(IEnumerable<ModelItem> items, int cap = 1000)
        {
            var map = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    foreach (var cat in item.PropertyCategories)
                    {
                        var cn = cat.DisplayName ?? cat.Name;
                        if (string.IsNullOrEmpty(cn)) continue;
                        if (!map.TryGetValue(cn!, out var set)) { set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase); map[cn!] = set; }
                        foreach (var p in cat.Properties)
                        {
                            var pn = p.DisplayName ?? p.Name;
                            if (!string.IsNullOrEmpty(pn)) set.Add(pn!);
                        }
                    }
                }
                catch { }
                if (++n >= cap) break;
            }
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map) result[kv.Key] = kv.Value.ToList();
            return result;
        }

        public static PropertyRoles GuessRoles(Dictionary<string, List<string>> catParams)
        {
            PropRef Pick(string[] cands)
            {
                foreach (var cand in cands)
                    foreach (var kv in catParams)
                        foreach (var p in kv.Value)
                            if (string.Equals(p, cand, StringComparison.OrdinalIgnoreCase)) return new PropRef(kv.Key, p);
                return default;
            }
            return new PropertyRoles
            {
                Type = Pick(TypeCandidates),
                Level = Pick(LevelCandidates),
                Material = Pick(MaterialCandidates),
                Classification = Pick(ClassCandidates)
            };
        }

        /// <summary>One pass over an item's properties reading the configured role values (category-qualified).</summary>
        public static RoleValues ReadRoles(ModelItem item, PropertyRoles roles)
        {
            var rv = new RoleValues { Type = "", Level = "", Material = "", Classification = "" };
            if (roles == null || !roles.Any) return rv;
            try
            {
                foreach (var cat in item.PropertyCategories)
                {
                    var cn = cat.DisplayName ?? cat.Name ?? "";
                    foreach (var p in cat.Properties)
                    {
                        var name = p.DisplayName ?? p.Name;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (rv.Type == "" && Match(roles.Type, cn, name!)) rv.Type = Typed("", "", p.Value).Value;
                        else if (rv.Level == "" && Match(roles.Level, cn, name!)) rv.Level = Typed("", "", p.Value).Value;
                        else if (rv.Material == "" && Match(roles.Material, cn, name!)) rv.Material = Typed("", "", p.Value).Value;
                        else if (rv.Classification == "" && Match(roles.Classification, cn, name!)) rv.Classification = Typed("", "", p.Value).Value;
                    }
                }
            }
            catch { }
            return rv;
        }

        private static bool Match(PropRef r, string categoryName, string propName)
            => r.IsSet && string.Equals(propName, r.Name, StringComparison.OrdinalIgnoreCase)
               && (string.IsNullOrWhiteSpace(r.Category) || string.Equals(categoryName, r.Category, StringComparison.OrdinalIgnoreCase));

        public static Material? GetMaterial(ModelItem item)
        {
            try
            {
                var g = item.Geometry;
                if (g == null) return null;
                var c = g.ActiveColor;
                return new Material { R = c.R, G = c.G, B = c.B, Transparency = g.ActiveTransparency };
            }
            catch { return null; }
        }

        private static IfcProp Typed(string pset, string name, VariantData value)
        {
            var p = new IfcProp { Pset = pset, Name = name };
            try
            {
                if (value == null) { p.Value = ""; }
                else if (value.IsBoolean) { p.Kind = PropKind.Boolean; p.Value = value.ToBoolean() ? "T" : "F"; }
                else if (value.IsDouble) { p.Kind = PropKind.Real; p.Value = value.ToDouble().ToString("R", CultureInfo.InvariantCulture); }
                else if (value.IsInt32) { p.Kind = PropKind.Integer; p.Value = value.ToInt32().ToString(CultureInfo.InvariantCulture); }
                else if (value.IsDisplayString) { p.Value = value.ToDisplayString(); }
                else if (value.IsNamedConstant) { p.Value = value.ToNamedConstant().DisplayName; }
                else { p.Value = value.ToString(); }
            }
            catch
            {
                p.Kind = PropKind.Text;
                try { p.Value = value?.ToString() ?? ""; } catch { p.Value = ""; }
            }
            return p;
        }
    }
}
