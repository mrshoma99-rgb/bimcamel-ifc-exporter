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
            public readonly string Ifc4;     // IFC4 occurrence entity (9 attrs incl. PredefinedType)
            public readonly string Ifc2x3;   // IFC2x3 occurrence entity ("" ⇒ export as proxy in 2x3)
            public readonly int Args2x3;     // attribute count of the IFC2x3 entity (8 or 9)
            public IfcClass(string ifc4, string ifc2x3 = "", int args2x3 = 0)
            { Ifc4 = ifc4; Ifc2x3 = ifc2x3 ?? ""; Args2x3 = args2x3; }
        }

        // Friendly name → dual-schema entities. Architectural: Ifc2x3 = "" (proxy). MEP: generic flow class.
        public static readonly Dictionary<string, IfcClass> Catalog = new(StringComparer.OrdinalIgnoreCase)
        {
            // Architectural / structural (IFC2x3 → proxy)
            { "Wall", new IfcClass("IFCWALL") },
            { "Wall (standard case)", new IfcClass("IFCWALLSTANDARDCASE") },
            { "Slab", new IfcClass("IFCSLAB") },
            { "Beam", new IfcClass("IFCBEAM") },
            { "Column", new IfcClass("IFCCOLUMN") },
            { "Member", new IfcClass("IFCMEMBER") },
            { "Plate", new IfcClass("IFCPLATE") },
            { "Footing", new IfcClass("IFCFOOTING") },
            { "Railing", new IfcClass("IFCRAILING") },
            { "Stair", new IfcClass("IFCSTAIR") },
            { "Ramp", new IfcClass("IFCRAMP") },
            { "Roof", new IfcClass("IFCROOF") },
            { "Covering", new IfcClass("IFCCOVERING") },
            { "Curtain wall", new IfcClass("IFCCURTAINWALL") },
            { "Chimney", new IfcClass("IFCCHIMNEY") },
            { "Shading device", new IfcClass("IFCSHADINGDEVICE") },
            { "Furniture", new IfcClass("IFCFURNITURE") },
            { "Building element proxy", new IfcClass("IFCBUILDINGELEMENTPROXY") },
            // Piping
            { "Pipe segment", new IfcClass("IFCPIPESEGMENT", "IFCFLOWSEGMENT", 8) },
            { "Pipe fitting", new IfcClass("IFCPIPEFITTING", "IFCFLOWFITTING", 8) },
            { "Valve", new IfcClass("IFCVALVE", "IFCFLOWCONTROLLER", 8) },
            { "Pump", new IfcClass("IFCPUMP", "IFCFLOWMOVINGDEVICE", 8) },
            { "Tank", new IfcClass("IFCTANK", "IFCFLOWSTORAGEDEVICE", 8) },
            { "Flow meter", new IfcClass("IFCFLOWMETER", "IFCDISTRIBUTIONCONTROLELEMENT", 9) },
            { "Flow instrument", new IfcClass("IFCFLOWINSTRUMENT", "IFCDISTRIBUTIONCONTROLELEMENT", 9) },
            { "Filter", new IfcClass("IFCFILTER", "IFCFLOWTREATMENTDEVICE", 8) },
            { "Strainer", new IfcClass("IFCFILTER", "IFCFLOWTREATMENTDEVICE", 8) },
            // HVAC / ducting
            { "Duct segment", new IfcClass("IFCDUCTSEGMENT", "IFCFLOWSEGMENT", 8) },
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
            { "Distribution board", new IfcClass("IFCDISTRIBUTIONBOARD", "IFCFLOWCONTROLLER", 8) },
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
