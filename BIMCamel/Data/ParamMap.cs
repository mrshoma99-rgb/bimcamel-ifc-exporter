using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMCamel.Data
{
    /// <summary>One parameter-mapping rule: rename/relocate a source property on export.</summary>
    public sealed class ParamMapRule
    {
        public string SourceCategory = ""; // source category to match (blank = any category)
        public string Source = "";         // source property name to match (case-insensitive)
        public string TargetPset = "";     // destination Pset (blank = keep original)
        public string TargetName = "";     // destination property name (blank = keep original)
    }

    /// <summary>
    /// Parameter mapping (F: rename/relocate properties) + a prefilled catalog of common IFC
    /// standard Psets and their properties. The user can pick a standard Pset/property or type a
    /// custom one. Applied to each element's harvested properties on export.
    /// </summary>
    public static class PsetCatalog
    {
        public static readonly Dictionary<string, string[]> Common = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Pset_WallCommon", new[] { "Reference", "Status", "IsExternal", "LoadBearing", "FireRating", "AcousticRating", "ThermalTransmittance", "Combustible", "Compartmentation", "SurfaceSpreadOfFlame", "ExtendToStructure" } },
            { "Pset_SlabCommon", new[] { "Reference", "Status", "IsExternal", "LoadBearing", "FireRating", "AcousticRating", "ThermalTransmittance", "Combustible", "PitchAngle" } },
            { "Pset_BeamCommon", new[] { "Reference", "Status", "IsExternal", "LoadBearing", "FireRating", "Span", "Slope", "Roll" } },
            { "Pset_ColumnCommon", new[] { "Reference", "Status", "IsExternal", "LoadBearing", "FireRating", "Slope", "Roll" } },
            { "Pset_MemberCommon", new[] { "Reference", "Status", "IsExternal", "LoadBearing", "FireRating", "Span", "Slope", "Roll" } },
            { "Pset_PlateCommon", new[] { "Reference", "Status", "IsExternal", "LoadBearing", "FireRating", "AcousticRating", "ThermalTransmittance" } },
            { "Pset_CoveringCommon", new[] { "Reference", "Status", "IsExternal", "FireRating", "AcousticRating", "ThermalTransmittance", "Combustible", "Finish", "TotalThickness" } },
            { "Pset_RoofCommon", new[] { "Reference", "Status", "IsExternal", "FireRating", "AcousticRating", "ThermalTransmittance", "Combustible", "ProjectedArea", "TotalArea" } },
            { "Pset_DoorCommon", new[] { "Reference", "Status", "FireRating", "AcousticRating", "SecurityRating", "IsExternal", "ThermalTransmittance", "FireExit", "SelfClosing", "SmokeStop", "Infiltration" } },
            { "Pset_WindowCommon", new[] { "Reference", "Status", "FireRating", "AcousticRating", "SecurityRating", "IsExternal", "ThermalTransmittance", "GlazingAreaFraction", "Infiltration" } },
            { "Pset_SpaceCommon", new[] { "Reference", "IsExternal", "GrossPlannedArea", "NetPlannedArea", "PubliclyAccessible", "HandicapAccessible", "Category" } },
            { "Pset_BuildingElementProxyCommon", new[] { "Reference", "Status" } },
            { "Pset_PipeSegmentTypeCommon", new[] { "Reference", "Status", "NominalDiameter", "InnerDiameter", "OuterDiameter", "WallThickness", "Material" } },
            { "Pset_PipeFittingTypeCommon", new[] { "Reference", "Status", "NominalDiameter", "PressureClass", "Material" } },
            { "Pset_DuctSegmentTypeCommon", new[] { "Reference", "Status", "NominalDiameterOrWidth", "NominalHeight", "Material" } },
            { "Pset_ManufacturerTypeInformation", new[] { "GlobalTradeItemNumber", "ArticleNumber", "ModelReference", "ModelLabel", "Manufacturer", "ProductionYear", "AssemblyPlace" } },
            { "Pset_ManufacturerOccurrence", new[] { "AcquisitionDate", "BarCode", "SerialNumber", "BatchReference", "AssemblyPlace" } },
        };

        public static List<string> PsetNames() => Common.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        public static List<string> AllParamNames() =>
            Common.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Apply rename/relocate rules, matched by source name + (optional) source category.</summary>
        public static void Apply(List<IfcProp> props, List<ParamMapRule>? rules)
        {
            if (rules == null || rules.Count == 0 || props.Count == 0) return;
            foreach (var p in props)
            {
                foreach (var r in rules)
                {
                    if (string.IsNullOrWhiteSpace(r.Source)) continue;
                    if (!string.Equals(p.Name, r.Source, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(r.SourceCategory) && !string.Equals(p.Pset, r.SourceCategory, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(r.TargetPset)) p.Pset = r.TargetPset.Trim();
                    if (!string.IsNullOrWhiteSpace(r.TargetName)) p.Name = r.TargetName.Trim();
                    break; // first matching rule wins
                }
            }
        }
    }
}
