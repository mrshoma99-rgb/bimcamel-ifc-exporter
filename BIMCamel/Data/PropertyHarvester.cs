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
        /// <summary>
        /// Drop properties whose value is blank (v6 Z4). An empty property costs a whole
        /// IfcPropertySingleValue entity carrying one space (WriteNominal writes IFCTEXT(' ')), and
        /// it also participates in the F3 content hash — so blanks make property sets LESS likely
        /// to dedup as well as bigger. Static because it is a global output preference, set once
        /// per export before the extractors run.
        /// </summary>
        public static bool SkipEmptyValues = true;

        /// <summary>Harvest props; if <paramref name="include"/> is non-null, only those Pset (category) names.</summary>
        public static List<IfcProp> Harvest(ModelItem item, HashSet<string>? include = null)
            => HarvestWithRoles(item, include, null, out _);

        /// <summary>
        /// One pass over an element's properties that fills BOTH the property sets and the semantic
        /// role values (v5 E2).
        ///
        /// The extractors used to call <see cref="Harvest"/> and then <see cref="ReadRoles"/>, and
        /// each walked <c>PropertyCategories → Properties → Value</c> from scratch. Every one of
        /// those member accesses crosses into COM, so on a 674k-element model with ~40 properties
        /// each that is tens of millions of interop calls paid TWICE for the same bytes. This reads
        /// them once.
        ///
        /// The two filters stay independent, exactly as before: the pset filter narrows what lands
        /// in <c>props</c>, while the role scan looks at EVERY category — a role may legitimately
        /// point at a category the user chose not to export. A category wanted by neither is skipped
        /// before its <c>Properties</c> collection is touched, which is where the cost is.
        /// </summary>
        public static List<IfcProp> HarvestWithRoles(ModelItem item, HashSet<string>? include, PropertyRoles? roles, out RoleValues rv)
        {
            rv = new RoleValues { Type = "", Level = "", Material = "", Classification = "" };
            bool wantRoles = roles != null && roles.Any;
            var list = new List<IfcProp>();
            try
            {
                foreach (var cat in item.PropertyCategories)
                {
                    // Read each COM-backed name ONCE; the two consumers disagreed on the fallback
                    // for a category with no names at all ("Properties" for psets, "" for role
                    // matching) and that difference is preserved rather than quietly unified.
                    string? dn = cat.DisplayName, cn = cat.Name;
                    string pset = dn ?? cn ?? "Properties";
                    string roleCat = dn ?? cn ?? "";

                    bool wantProps = include == null || include.Contains(pset);
                    if (!wantProps && !wantRoles) continue;

                    foreach (var p in cat.Properties)
                    {
                        string name = p.DisplayName ?? p.Name ?? "";
                        if (string.IsNullOrEmpty(name)) continue;

                        // Format the value at most once, and only when something wants it.
                        string? text = null;
                        if (wantProps)
                        {
                            var prop = Typed(pset, name, p.Value);
                            text = prop.Value;
                            // A blank value is written as IFCTEXT(' ') — an entity that says
                            // nothing the property's absence does not (v6 Z4).
                            if (!SkipEmptyValues || !string.IsNullOrWhiteSpace(prop.Value)) list.Add(prop);
                        }

                        if (!wantRoles) continue;
                        // Same first-wins ordering as the old ReadRoles chain.
                        if (rv.Type == "" && Match(roles!.Type, roleCat, name)) rv.Type = text ??= ValueToString(p.Value);
                        else if (rv.Level == "" && Match(roles!.Level, roleCat, name)) rv.Level = text ??= ValueToString(p.Value);
                        else if (rv.Material == "" && Match(roles!.Material, roleCat, name)) rv.Material = text ??= ValueToString(p.Value);
                        else if (rv.Classification == "" && Match(roles!.Classification, roleCat, name)) rv.Classification = text ??= ValueToString(p.Value);
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
                            var v = ValueToString(p.Value);
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

        /// <summary>
        /// Role values only, for callers that do not also want the property sets. Delegates to the
        /// merged pass with an "include nothing" filter, so there is one implementation of the
        /// matching rules rather than two that can drift apart.
        /// </summary>
        public static RoleValues ReadRoles(ModelItem item, PropertyRoles roles)
        {
            if (roles == null || !roles.Any)
                return new RoleValues { Type = "", Level = "", Material = "", Classification = "" };
            HarvestWithRoles(item, NoCategories, roles, out var rv);
            return rv;
        }

        /// <summary>An include-filter that matches nothing — "roles only, no property sets".</summary>
        private static readonly HashSet<string> NoCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What the extractors actually want: the property sets if the user asked for them, the
        /// role values if any role is configured, and NOTHING read at all when neither applies.
        /// One entry point so both extractors make exactly one pass over an item's properties.
        /// </summary>
        public static List<IfcProp>? HarvestAndRoles(ModelItem item, bool wantProps, HashSet<string>? include, PropertyRoles? roles, out RoleValues rv)
        {
            bool wantRoles = roles != null && roles.Any;
            if (!wantProps && !wantRoles)
            {
                rv = new RoleValues { Type = "", Level = "", Material = "", Classification = "" };
                return null;
            }
            var props = HarvestWithRoles(item, wantProps ? include : NoCategories, roles, out rv);
            return wantProps ? props : null;
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
            p.Value = ValueToString(value, out var kind);
            p.Kind = kind;
            return p;
        }

        /// <summary>
        /// The value text alone, without building an <see cref="IfcProp"/> around it. The role scan
        /// and the mapping-keyword scan only ever wanted the string, and each allocated a throwaway
        /// IfcProp per property to reach it (v5 E2).
        /// </summary>
        public static string ValueToString(VariantData value) => ValueToString(value, out _);

        private static string ValueToString(VariantData value, out PropKind kind)
        {
            kind = PropKind.Text;
            try
            {
                if (value == null) return "";
                if (value.IsBoolean) { kind = PropKind.Boolean; return value.ToBoolean() ? "T" : "F"; }
                if (value.IsDouble) { kind = PropKind.Real; return value.ToDouble().ToString("R", CultureInfo.InvariantCulture); }
                if (value.IsInt32) { kind = PropKind.Integer; return value.ToInt32().ToString(CultureInfo.InvariantCulture); }
                if (value.IsDisplayString) return value.ToDisplayString();
                if (value.IsNamedConstant) return value.ToNamedConstant().DisplayName;
                return value.ToString();
            }
            catch
            {
                kind = PropKind.Text;
                try { return value?.ToString() ?? ""; } catch { return ""; }
            }
        }
    }
}
