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
    /// Which tree nodes an element's properties are read from. Navisworks exports geometry, so
    /// every element the exporter writes is a geometry LEAF — but most authoring tools hang the
    /// meaningful data on the object ABOVE it: Revit's "Element" tab sits on the element node
    /// whose children are the solids, SmartPlant 3D / E3D put theirs on the occurrence several
    /// levels above the primitives. Reading the leaf alone exported almost none of it.
    /// The numeric values are stored in profiles, so only ever append.
    /// </summary>
    public enum PropSource
    {
        /// <summary>The geometry node only — the behaviour before this option existed.</summary>
        Geometry = 0,
        /// <summary>The geometry node plus its parent object: the nearest composite object above it
        /// (Navisworks' "Last Object"), or the immediate parent when there is none.</summary>
        ParentObject = 1,
        /// <summary>The geometry node plus every ancestor below the file node.</summary>
        AllParents = 2
    }

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

        /// <summary>
        /// Which nodes to read properties from. Static for the same reason as
        /// <see cref="SkipEmptyValues"/>: one global reading preference, and the scans, the
        /// pre-flight and both extractors must all see the same tree, or a category the scan offered
        /// would silently export empty. On a name clash (same pset + property) the node NEAREST the
        /// geometry wins, so the leaf's own values are never overwritten by a parent's.
        /// </summary>
        public static PropSource Source = PropSource.Geometry;

        /// <summary>How far above the geometry to look for its composite object before giving up
        /// and using the immediate parent. Deep enough for SP3D's Item → Aspects → primitive.</summary>
        private const int MaxObjectDepth = 8;

        /// <summary>
        /// Ancestor reads, shared by every geometry leaf under them: a Revit element with six
        /// solids, or an SP3D occurrence with forty primitives, is read from COM once instead of once
        /// per child. Deliberately SMALL — items arrive in tree order, so siblings are consecutive and
        /// a few dozen entries catch nearly every repeat, while an unbounded map would hold every
        /// parent of a 674k-element model in memory for the whole export. Cleared when full.
        /// </summary>
        private const int AncestorCacheCap = 64;
        private static readonly Dictionary<ModelItem, List<NodeProp>> _ancestorProps = new Dictionary<ModelItem, List<NodeProp>>();
        /// <summary>immediate parent → the ancestors to read for any leaf under it.</summary>
        private static readonly Dictionary<ModelItem, List<ModelItem>> _ancestorChains = new Dictionary<ModelItem, List<ModelItem>>();

        /// <summary>Forget cached ancestor reads. Call at the start of each export or scan: user
        /// data tabs can change between runs, and a reloaded document has new items anyway.</summary>
        public static void ResetCache() { _ancestorProps.Clear(); _ancestorChains.Clear(); }

        /// <summary>One cached ancestor property, unfiltered — the filters are applied on read so
        /// one entry serves the export, the scans and the pre-flight alike.</summary>
        private sealed class NodeProp
        {
            public string Pset = "", RoleCat = "";
            public IfcProp Prop = new IfcProp();
        }

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
            var ancestors = Ancestors(item);
            // Only needed to resolve leaf-vs-parent clashes; the leaf's own list is written exactly
            // as before, duplicates and all, so Geometry mode is byte-for-byte unchanged.
            var seen = ancestors.Count > 0 ? new HashSet<(string, string)>() : null;
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
                            // nothing the property's absence does not (v6 Z4). A skipped blank is
                            // not "seen", so a parent's real value for the same property fills it.
                            if (!SkipEmptyValues || !string.IsNullOrWhiteSpace(prop.Value)) { list.Add(prop); seen?.Add((pset, name)); }
                        }

                        if (!wantRoles) continue;
                        TakeRole(roles!, roleCat, name, ref rv, ref text, p.Value);
                    }
                }
            }
            catch { /* tolerate odd nodes */ }

            // Then the parents, nearest first, filling only what the nodes below did not have.
            foreach (var a in ancestors)
            {
                bool rolesDone = !wantRoles || RolesComplete(roles!, rv);
                if (include != null && include.Count == 0 && rolesDone) break;   // roles-only and nothing left to find
                foreach (var np in AncestorProps(a))
                {
                    if (include == null || include.Contains(np.Pset))
                    {
                        var src = np.Prop;
                        if ((!SkipEmptyValues || !string.IsNullOrWhiteSpace(src.Value)) && seen!.Add((src.Pset, src.Name)))
                            // A copy: parameter mapping renames props in place, and this one is
                            // shared with every sibling through the cache.
                            list.Add(new IfcProp { Pset = src.Pset, Name = src.Name, Value = src.Value, Kind = src.Kind });
                    }
                    if (wantRoles && !rolesDone)
                    {
                        string? text = np.Prop.Value;
                        TakeRole(roles!, np.RoleCat, np.Prop.Name, ref rv, ref text, null);
                    }
                }
            }
            return list;
        }

        /// <summary>Same first-wins ordering as the old ReadRoles chain: one property feeds at most
        /// one role, and a role already filled (by this node or one nearer the geometry) stays.</summary>
        private static void TakeRole(PropertyRoles roles, string roleCat, string name, ref RoleValues rv, ref string? text, VariantData? value)
        {
            if (rv.Type == "" && Match(roles.Type, roleCat, name)) rv.Type = text ??= ValueToString(value!);
            else if (rv.Level == "" && Match(roles.Level, roleCat, name)) rv.Level = text ??= ValueToString(value!);
            else if (rv.Material == "" && Match(roles.Material, roleCat, name)) rv.Material = text ??= ValueToString(value!);
            else if (rv.Classification == "" && Match(roles.Classification, roleCat, name)) rv.Classification = text ??= ValueToString(value!);
        }

        private static bool RolesComplete(PropertyRoles r, RoleValues rv)
            => (!r.Type.IsSet || rv.Type != "") && (!r.Level.IsSet || rv.Level != "")
               && (!r.Material.IsSet || rv.Material != "") && (!r.Classification.IsSet || rv.Classification != "");

        /// <summary>
        /// The ancestors whose properties belong to <paramref name="item"/> under the current
        /// <see cref="Source"/>, nearest first. Never the file node: it describes the NWC, not a
        /// building element. Empty in Geometry mode, so that mode costs nothing extra.
        /// </summary>
        private static List<ModelItem> Ancestors(ModelItem item)
        {
            if (Source == PropSource.Geometry) return NoItems;
            ModelItem? parent;
            try { parent = item.Parent; } catch { return NoItems; }
            if (parent == null) return NoItems;
            if (_ancestorChains.TryGetValue(parent, out var cached)) return cached;

            var ups = new List<ModelItem>();
            try
            {
                bool found = false;
                var a = parent;
                for (int guard = 0; a != null && guard < 256; guard++)
                {
                    var up = a.Parent;
                    if (up == null) break;                       // a is the file node
                    if (Source == PropSource.ParentObject)
                    {
                        if (a.IsLayer) break;                    // a level/layer groups elements; it is never one
                        ups.Add(a);
                        if (a.IsComposite) { found = true; break; }
                        if (ups.Count >= MaxObjectDepth) break;
                    }
                    else ups.Add(a);
                    a = up;
                }
                // No composite object above the geometry: the immediate parent is the best guess at
                // "the element", and is what Revit's tree has there whatever its flags say.
                if (Source == PropSource.ParentObject && !found && ups.Count > 1) ups.RemoveRange(1, ups.Count - 1);
            }
            catch { /* tolerate odd nodes: keep what was reached */ }

            if (_ancestorChains.Count >= AncestorCacheCap) _ancestorChains.Clear();
            _ancestorChains[parent] = ups;
            return ups;
        }

        private static readonly List<ModelItem> NoItems = new List<ModelItem>();

        /// <summary>Every property of one ancestor, typed, read from COM at most once per run of siblings.</summary>
        private static List<NodeProp> AncestorProps(ModelItem node)
        {
            if (_ancestorProps.TryGetValue(node, out var cached)) return cached;
            var all = new List<NodeProp>();
            try
            {
                foreach (var cat in node.PropertyCategories)
                {
                    string? dn = cat.DisplayName, cn = cat.Name;
                    string pset = dn ?? cn ?? "Properties";
                    string roleCat = dn ?? cn ?? "";
                    foreach (var p in cat.Properties)
                    {
                        string name = p.DisplayName ?? p.Name ?? "";
                        if (string.IsNullOrEmpty(name)) continue;
                        all.Add(new NodeProp { Pset = pset, RoleCat = roleCat, Prop = Typed(pset, name, p.Value) });
                    }
                }
            }
            catch { /* tolerate odd nodes */ }
            if (_ancestorProps.Count >= AncestorCacheCap) _ancestorProps.Clear();
            _ancestorProps[node] = all;
            return all;
        }

        /// <summary>
        /// The nodes a scan should read for one item: the item, then its <see cref="Ancestors"/>
        /// not already read by this scan. A parent shared by forty sampled leaves is read once.
        /// </summary>
        private static IEnumerable<ModelItem> ScanNodes(ModelItem item, HashSet<ModelItem> visited)
        {
            yield return item;
            foreach (var a in Ancestors(item))
                if (visited.Add(a)) yield return a;
        }

        /// <summary>Distinct property-category (Pset) names found across a sample of items.</summary>
        public static List<string> ScanCategories(IEnumerable<ModelItem> items, int cap = 4000, Action<int>? onProgress = null)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<ModelItem>();
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    foreach (var node in ScanNodes(item, visited))
                        foreach (var cat in node.PropertyCategories)
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
            var visited = new HashSet<ModelItem>();
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    if (!string.IsNullOrEmpty(item.DisplayName)) set.Add(item.DisplayName);
                    if (!string.IsNullOrEmpty(item.ClassDisplayName)) set.Add(item.ClassDisplayName);
                    foreach (var node in ScanNodes(item, visited))
                    foreach (var cat in node.PropertyCategories)
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
            var visited = new HashSet<ModelItem>();
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    foreach (var node in ScanNodes(item, visited))
                    foreach (var cat in node.PropertyCategories)
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
            var visited = new HashSet<ModelItem>();
            int n = 0;
            foreach (var item in items)
            {
                try
                {
                    foreach (var node in ScanNodes(item, visited))
                    foreach (var cat in node.PropertyCategories)
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
