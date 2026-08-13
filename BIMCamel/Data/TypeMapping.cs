using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMCamel.Data
{
    /// <summary>
    /// Object → IFC class mapping (F5), driven by Navisworks selection/search SETS. The user assigns
    /// a set to an IFC class (and optionally a PredefinedType); every element in that set is exported
    /// as that class. Unmapped elements stay IfcBuildingElementProxy.
    ///
    /// Dual-schema (v2 §6 / v4 C1): each class carries its IFC4 entity AND its IFC2x3 entity. The
    /// MEP/distribution classes diverge — IFC4 has rich types (IfcPipeSegment, IfcValve…) while IFC2x3
    /// only has the generic flow vocabulary (IfcFlowSegment, IfcFlowController…). Architectural classes
    /// keep their (same-named) IFC4 entity but export as IfcBuildingElementProxy in IFC2x3, because
    /// their 2x3 attribute signatures vary per entity and a wrong count yields invalid IFC — the
    /// generic flow classes, by contrast, have a known, uniform 8-attribute signature
    /// (IfcDistributionControlElement = 9, with an optional ControlElementId).
    /// </summary>
    public static class TypeMapping
    {
        public readonly struct IfcClass
        {
            public readonly string Ifc4;     // IFC4 occurrence entity
            public readonly string Ifc2x3;   // IFC2x3 occurrence entity ("" ⇒ export as proxy in 2x3)
            public readonly int Args2x3;     // attribute count of the IFC2x3 entity (8 or 9)

            /// <summary>
            /// IFC4 attributes AFTER the 8 shared IfcElement ones (GlobalId, OwnerHistory, Name,
            /// Description, ObjectType, ObjectPlacement, Representation, Tag). <c>{P}</c> marks
            /// where PredefinedType goes. Most elements are just "{P}" — a plain 9-attribute
            /// entity — but several are NOT, and getting the count wrong is precisely the class of
            /// bug that made Revit reject our IFC4 files. Doors and windows, for instance, carry
            /// OverallHeight and OverallWidth before PredefinedType and two more after it.
            /// </summary>
            public readonly string Tail4;

            /// <summary>
            /// IFC4 attributes after the 9 shared IfcElementType ones (GlobalId, OwnerHistory,
            /// Name, Description, ApplicableOccurrence, HasPropertySets, RepresentationMaps, Tag,
            /// ElementType). Same <c>{P}</c> convention. PredefinedType is MANDATORY on IFC4 type
            /// entities, and some types have further mandatory attributes after it.
            /// </summary>
            public readonly string TypeTail4;

            /// <summary>
            /// buildingSMART base-quantity set name for this class (e.g. Qto_WallBaseQuantities).
            /// Empty ⇒ no standard set is defined for it and the generic fallback is used.
            /// </summary>
            public readonly string Qto;

            public IfcClass(string ifc4, string ifc2x3 = "", int args2x3 = 0,
                            string tail4 = "{P}", string typeTail4 = "{P}", string qto = "")
            {
                Ifc4 = ifc4; Ifc2x3 = ifc2x3 ?? ""; Args2x3 = args2x3;
                Tail4 = tail4; TypeTail4 = typeTail4; Qto = qto ?? "";
            }
        }

        // Friendly name → dual-schema entities. Architectural: Ifc2x3 = "" (proxy). MEP: generic flow class.
        public static readonly Dictionary<string, IfcClass> Catalog = new(StringComparer.OrdinalIgnoreCase)
        {
            // Architectural / structural (IFC2x3 → proxy)
            { "Wall", new IfcClass("IFCWALL", qto: "Qto_WallBaseQuantities") },
            { "Wall (standard case)", new IfcClass("IFCWALLSTANDARDCASE", qto: "Qto_WallBaseQuantities") },
            { "Slab", new IfcClass("IFCSLAB", qto: "Qto_SlabBaseQuantities") },
            { "Beam", new IfcClass("IFCBEAM", qto: "Qto_BeamBaseQuantities") },
            { "Column", new IfcClass("IFCCOLUMN", qto: "Qto_ColumnBaseQuantities") },
            { "Member", new IfcClass("IFCMEMBER", qto: "Qto_MemberBaseQuantities") },
            { "Plate", new IfcClass("IFCPLATE", qto: "Qto_PlateBaseQuantities") },
            { "Footing", new IfcClass("IFCFOOTING", qto: "Qto_FootingBaseQuantities") },
            { "Railing", new IfcClass("IFCRAILING", qto: "Qto_RailingBaseQuantities") },
            { "Stair", new IfcClass("IFCSTAIR") },
            { "Ramp", new IfcClass("IFCRAMP") },
            { "Roof", new IfcClass("IFCROOF", qto: "Qto_RoofBaseQuantities") },
            { "Covering", new IfcClass("IFCCOVERING", qto: "Qto_CoveringBaseQuantities") },
            { "Curtain wall", new IfcClass("IFCCURTAINWALL") },
            { "Chimney", new IfcClass("IFCCHIMNEY") },
            { "Shading device", new IfcClass("IFCSHADINGDEVICE") },
            // IfcFurnitureType diverges: AssemblyPlace is MANDATORY and sits before PredefinedType.
            { "Furniture", new IfcClass("IFCFURNITURE", typeTail4: ".NOTDEFINED.,{P}") },
            { "Building element proxy", new IfcClass("IFCBUILDINGELEMENTPROXY", qto: "Qto_BuildingElementProxyQuantities") },
            // Openings-adjacent architectural classes. These do NOT have the plain 9-attribute
            // signature — see IfcClass.Tail4. IfcDoor/IfcWindow carry OverallHeight + OverallWidth
            // before PredefinedType and two operation/partitioning attributes after it (13 total);
            // their type entities add a second MANDATORY enum after PredefinedType.
            { "Door", new IfcClass("IFCDOOR", tail4: "$,$,{P},$,$", typeTail4: "{P},.NOTDEFINED.,$,$", qto: "Qto_DoorBaseQuantities") },
            { "Window", new IfcClass("IFCWINDOW", tail4: "$,$,{P},$,$", typeTail4: "{P},.NOTDEFINED.,$,$", qto: "Qto_WindowBaseQuantities") },
            // IfcStairFlight adds NumberOfRisers, NumberOfTreads, RiserHeight, TreadLength before
            // PredefinedType (13); IfcPile adds ConstructionType after it (10).
            { "Stair flight", new IfcClass("IFCSTAIRFLIGHT", tail4: "$,$,$,$,{P}", qto: "Qto_StairFlightBaseQuantities") },
            { "Ramp flight", new IfcClass("IFCRAMPFLIGHT", qto: "Qto_RampFlightBaseQuantities") },
            { "Pile", new IfcClass("IFCPILE", tail4: "{P},$", qto: "Qto_PileBaseQuantities") },
            { "Building element part", new IfcClass("IFCBUILDINGELEMENTPART") },
            // Piping
            { "Pipe segment", new IfcClass("IFCPIPESEGMENT", "IFCFLOWSEGMENT", 8, qto: "Qto_PipeSegmentBaseQuantities") },
            { "Pipe fitting", new IfcClass("IFCPIPEFITTING", "IFCFLOWFITTING", 8) },
            { "Valve", new IfcClass("IFCVALVE", "IFCFLOWCONTROLLER", 8) },
            { "Pump", new IfcClass("IFCPUMP", "IFCFLOWMOVINGDEVICE", 8) },
            { "Tank", new IfcClass("IFCTANK", "IFCFLOWSTORAGEDEVICE", 8) },
            { "Flow meter", new IfcClass("IFCFLOWMETER", "IFCDISTRIBUTIONCONTROLELEMENT", 9) },
            { "Flow instrument", new IfcClass("IFCFLOWINSTRUMENT", "IFCDISTRIBUTIONCONTROLELEMENT", 9) },
            { "Filter", new IfcClass("IFCFILTER", "IFCFLOWTREATMENTDEVICE", 8) },
            { "Strainer", new IfcClass("IFCFILTER", "IFCFLOWTREATMENTDEVICE", 8) },
            // HVAC / ducting
            { "Duct segment", new IfcClass("IFCDUCTSEGMENT", "IFCFLOWSEGMENT", 8, qto: "Qto_DuctSegmentBaseQuantities") },
            { "Duct fitting", new IfcClass("IFCDUCTFITTING", "IFCFLOWFITTING", 8) },
            { "Duct silencer", new IfcClass("IFCDUCTSILENCER", "IFCFLOWTREATMENTDEVICE", 8) },
            { "Air terminal", new IfcClass("IFCAIRTERMINAL", "IFCFLOWTERMINAL", 8) },
            { "Fan", new IfcClass("IFCFAN", "IFCFLOWMOVINGDEVICE", 8) },
            { "Coil", new IfcClass("IFCCOIL", "IFCENERGYCONVERSIONDEVICE", 8) },
            { "Boiler", new IfcClass("IFCBOILER", "IFCENERGYCONVERSIONDEVICE", 8) },
            { "Chiller", new IfcClass("IFCCHILLER", "IFCENERGYCONVERSIONDEVICE", 8) },
            { "Compressor", new IfcClass("IFCCOMPRESSOR", "IFCFLOWMOVINGDEVICE", 8) },
            { "Heat exchanger", new IfcClass("IFCHEATEXCHANGER", "IFCENERGYCONVERSIONDEVICE", 8) },
            { "Space heater", new IfcClass("IFCSPACEHEATER", "IFCFLOWTERMINAL", 8) },
            { "Unitary equipment", new IfcClass("IFCUNITARYEQUIPMENT", "IFCENERGYCONVERSIONDEVICE", 8) },
            // Plumbing
            { "Sanitary terminal", new IfcClass("IFCSANITARYTERMINAL", "IFCFLOWTERMINAL", 8) },
            // Electrical
            { "Cable carrier segment", new IfcClass("IFCCABLECARRIERSEGMENT", "IFCFLOWSEGMENT", 8) },
            { "Cable carrier fitting", new IfcClass("IFCCABLECARRIERFITTING", "IFCFLOWFITTING", 8) },
            { "Cable segment", new IfcClass("IFCCABLESEGMENT", "IFCFLOWSEGMENT", 8) },
            { "Cable fitting", new IfcClass("IFCCABLEFITTING", "IFCFLOWFITTING", 8) },
            { "Light fixture", new IfcClass("IFCLIGHTFIXTURE", "IFCFLOWTERMINAL", 8) },
            { "Outlet", new IfcClass("IFCOUTLET", "IFCFLOWTERMINAL", 8) },
            { "Switching device", new IfcClass("IFCSWITCHINGDEVICE", "IFCFLOWCONTROLLER", 8) },
            { "Protective device", new IfcClass("IFCPROTECTIVEDEVICE", "IFCFLOWCONTROLLER", 8) },
            { "Transformer", new IfcClass("IFCTRANSFORMER", "IFCENERGYCONVERSIONDEVICE", 8) },
            // NB: IfcDistributionBoard only exists from IFC4X3 — in IFC4 the entity is IfcElectricDistributionBoard.
            { "Distribution board", new IfcClass("IFCELECTRICDISTRIBUTIONBOARD", "IFCFLOWCONTROLLER", 8) },
            { "Electric appliance", new IfcClass("IFCELECTRICAPPLIANCE", "IFCFLOWTERMINAL", 8) },
            { "Electric generator", new IfcClass("IFCELECTRICGENERATOR", "IFCENERGYCONVERSIONDEVICE", 8) },
            { "Electric motor", new IfcClass("IFCELECTRICMOTOR", "IFCENERGYCONVERSIONDEVICE", 8) },
            // Controls / instrumentation (IFC2x3 IfcDistributionControlElement = 9 attrs, 9th optional)
            { "Sensor", new IfcClass("IFCSENSOR", "IFCDISTRIBUTIONCONTROLELEMENT", 9) },
            { "Actuator", new IfcClass("IFCACTUATOR", "IFCDISTRIBUTIONCONTROLELEMENT", 9) },
            { "Controller", new IfcClass("IFCCONTROLLER", "IFCDISTRIBUTIONCONTROLELEMENT", 9) },
            // Accessories (IFC2x3 → proxy)
            { "Discrete accessory", new IfcClass("IFCDISCRETEACCESSORY") },
            { "Fastener", new IfcClass("IFCFASTENER") },
        };

        // ── classKey encoding: "Friendly" or "Friendly|PREDEF" (PredefinedType for IFC4) ──────────
        public static string Friendly(string? classKey)
        {
            if (string.IsNullOrEmpty(classKey)) return "";
            int b = classKey!.IndexOf('|');
            return b < 0 ? classKey : classKey.Substring(0, b);
        }

        public static string Predef(string? classKey)
        {
            if (string.IsNullOrEmpty(classKey)) return "";
            int b = classKey!.IndexOf('|');
            return b < 0 ? "" : classKey.Substring(b + 1);
        }

        public static string Encode(string friendly, string? predef)
            => string.IsNullOrWhiteSpace(predef) ? friendly : friendly + "|" + predef!.Trim();

        // Friendly class → IFC4 *Type entity. Default = element name + "TYPE"; exceptions listed.
        private static readonly Dictionary<string, string> TypeExceptions = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Wall (standard case)", "IFCWALLTYPE" },
        };

        /// <summary>IFC4 IfcElementType entity name for a friendly class (for IfcRelDefinesByType).</summary>
        public static string TypeEntityFor(string friendlyClass)
        {
            if (string.IsNullOrEmpty(friendlyClass)) return "IFCBUILDINGELEMENTPROXYTYPE";
            if (TypeExceptions.TryGetValue(friendlyClass, out var t)) return t;
            if (Catalog.TryGetValue(friendlyClass, out var c)) return c.Ifc4 + "TYPE";
            return "IFCBUILDINGELEMENTPROXYTYPE";
        }

        /// <summary>
        /// Trailing IFC4 occurrence attributes (after the 8 shared IfcElement ones) with
        /// PredefinedType substituted. Unknown classes fall back to the plain 9-attribute shape,
        /// which is what IfcBuildingElementProxy uses.
        /// </summary>
        public static string Tail4For(string friendlyClass, string predefToken)
        {
            string tail = Catalog.TryGetValue(friendlyClass ?? "", out var c) ? c.Tail4 : "{P}";
            return tail.Replace("{P}", predefToken);
        }

        /// <summary>Trailing IFC4 type attributes (after the 9 shared IfcElementType ones).</summary>
        public static string TypeTail4For(string friendlyClass, string predefToken)
        {
            string tail = Catalog.TryGetValue(friendlyClass ?? "", out var c) ? c.TypeTail4 : "{P}";
            return tail.Replace("{P}", predefToken);
        }

        /// <summary>
        /// buildingSMART base-quantity set name for a class. Falls back to
        /// <c>Qto_BuildingElementProxyQuantities</c> for unmapped elements (which really are
        /// proxies) and to the generic <c>Qto_BaseQuantities</c> for mapped classes that have no
        /// standard set defined — the previous behaviour was to use the generic name for
        /// everything, so no consumer keying on standard names ever matched.
        /// </summary>
        public static string QtoSetFor(string? friendlyClass)
        {
            if (string.IsNullOrEmpty(friendlyClass)) return "Qto_BuildingElementProxyQuantities";
            if (Catalog.TryGetValue(friendlyClass!, out var c) && c.Qto.Length > 0) return c.Qto;
            return "Qto_BaseQuantities";
        }

        /// <summary>
        /// Keyword → friendly class, for proposing a mapping from a set's NAME. Keys are the words
        /// people actually name sets after — Revit category names, mostly plural. Matching is
        /// longest-keyword-first, so "curtain wall" wins over "wall" and "duct fitting" over "duct".
        ///
        /// This is a suggestion engine, not a source of truth: a set called "Structural Framing"
        /// really is usually beams, but the user reviews the proposal before exporting.
        /// </summary>
        private static readonly Dictionary<string, string> AutoMapKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            // Architectural
            { "curtain wall", "Curtain wall" }, { "curtain panel", "Curtain wall" },
            { "wall", "Wall" }, { "walls", "Wall" },
            { "door", "Door" }, { "doors", "Door" },
            { "window", "Window" }, { "windows", "Window" },
            { "floor", "Slab" }, { "floors", "Slab" }, { "slab", "Slab" }, { "slabs", "Slab" },
            { "roof", "Roof" }, { "roofs", "Roof" },
            { "ceiling", "Covering" }, { "ceilings", "Covering" }, { "covering", "Covering" }, { "finish", "Covering" },
            { "stair flight", "Stair flight" }, { "stair", "Stair" }, { "stairs", "Stair" },
            { "ramp flight", "Ramp flight" }, { "ramp", "Ramp" }, { "ramps", "Ramp" },
            { "railing", "Railing" }, { "railings", "Railing" }, { "handrail", "Railing" }, { "balustrade", "Railing" },
            { "furniture", "Furniture" }, { "casework", "Furniture" },
            { "chimney", "Chimney" },
            { "shading", "Shading device" }, { "louvre", "Shading device" }, { "louver", "Shading device" },
            // Structural
            { "structural framing", "Beam" }, { "framing", "Beam" }, { "beam", "Beam" }, { "beams", "Beam" },
            { "structural column", "Column" }, { "column", "Column" }, { "columns", "Column" },
            { "brace", "Member" }, { "bracing", "Member" }, { "truss", "Member" }, { "member", "Member" },
            { "plate", "Plate" }, { "plates", "Plate" },
            { "foundation", "Footing" }, { "footing", "Footing" }, { "footings", "Footing" }, { "pad", "Footing" },
            { "pile", "Pile" }, { "piles", "Pile" },
            { "fastener", "Fastener" }, { "bolt", "Fastener" }, { "bolts", "Fastener" },
            // Piping
            { "pipe fitting", "Pipe fitting" }, { "pipe accessor", "Valve" },
            { "pipe", "Pipe segment" }, { "pipes", "Pipe segment" }, { "piping", "Pipe segment" },
            { "valve", "Valve" }, { "valves", "Valve" },
            { "pump", "Pump" }, { "pumps", "Pump" },
            { "tank", "Tank" }, { "tanks", "Tank" }, { "vessel", "Tank" },
            { "strainer", "Strainer" }, { "filter", "Filter" },
            { "flow meter", "Flow meter" },
            { "sanitary", "Sanitary terminal" }, { "plumbing fixture", "Sanitary terminal" },
            // HVAC
            { "duct fitting", "Duct fitting" }, { "duct accessor", "Duct silencer" },
            { "duct", "Duct segment" }, { "ducts", "Duct segment" }, { "ductwork", "Duct segment" },
            { "air terminal", "Air terminal" }, { "diffuser", "Air terminal" }, { "grille", "Air terminal" },
            { "fan", "Fan" }, { "fans", "Fan" },
            { "coil", "Coil" }, { "boiler", "Boiler" }, { "chiller", "Chiller" },
            { "compressor", "Compressor" }, { "heat exchanger", "Heat exchanger" },
            { "radiator", "Space heater" }, { "space heater", "Space heater" },
            { "ahu", "Unitary equipment" }, { "air handling", "Unitary equipment" },
            { "mechanical equipment", "Unitary equipment" },
            // Electrical
            { "cable tray", "Cable carrier segment" }, { "conduit", "Cable carrier segment" }, { "trunking", "Cable carrier segment" },
            { "cable", "Cable segment" },
            { "lighting", "Light fixture" }, { "light fixture", "Light fixture" }, { "luminaire", "Light fixture" },
            { "socket", "Outlet" }, { "outlet", "Outlet" }, { "receptacle", "Outlet" },
            { "switch", "Switching device" },
            { "distribution board", "Distribution board" }, { "panel board", "Distribution board" }, { "switchboard", "Distribution board" },
            { "transformer", "Transformer" }, { "generator", "Electric generator" }, { "motor", "Electric motor" },
            { "electrical equipment", "Electric appliance" }, { "electrical fixture", "Electric appliance" },
            // Controls
            { "sensor", "Sensor" }, { "actuator", "Actuator" }, { "controller", "Controller" }, { "thermostat", "Sensor" },
        };

        /// <summary>
        /// Best-guess friendly class for a set name, or null when nothing matches confidently.
        /// Longest keyword wins so more specific names ("duct fitting") beat their prefixes ("duct").
        /// </summary>
        public static string? GuessClass(string? setName)
        {
            if (string.IsNullOrWhiteSpace(setName)) return null;
            string n = setName!.ToLowerInvariant();
            string? best = null; int bestLen = 0;
            foreach (var kv in AutoMapKeywords)
            {
                if (kv.Key.Length <= bestLen) continue;
                if (n.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) { best = kv.Value; bestLen = kv.Key.Length; }
            }
            return best;
        }

        /// <summary>Friendly class names for the UI dropdown (sorted, proxy first as the default).</summary>
        public static List<string> Keys()
        {
            var keys = Catalog.Keys.Where(k => !k.Equals("Building element proxy", StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            keys.Insert(0, "Building element proxy");
            return keys;
        }
    }
}
