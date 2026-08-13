using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Collect
{
    /// <summary>
    /// Reads the coordinate/spatial facts Navisworks already knows about the loaded models:
    /// per-model origin, rotation and units for federated coordination, and the grid levels that
    /// give storeys their real elevations.
    ///
    /// Both were previously ignored — every IfcBuildingStorey was written at elevation 0, and there
    /// was no way to see whether a discipline model had come in on a different origin before
    /// discovering it in the exported IFC.
    /// </summary>
    public static class ModelSurvey
    {
        /// <summary>Model-unit → metre factor, plus a short display name.</summary>
        public static (double scale, string name) UnitsToMetre(Units u)
        {
            switch (u)
            {
                case Units.Meters: return (1.0, "m");
                case Units.Centimeters: return (0.01, "cm");
                case Units.Millimeters: return (0.001, "mm");
                case Units.Kilometers: return (1000.0, "km");
                case Units.Feet: return (0.3048, "ft");
                case Units.Inches: return (0.0254, "in");
                case Units.Yards: return (0.9144, "yd");
                case Units.Miles: return (1609.344, "mi");
                default: return (1.0, u.ToString());
            }
        }

        /// <summary>
        /// Level name → elevation in METRES, gathered from every grid system in the document.
        /// Names are matched case-insensitively against the Level property value that drives
        /// storey creation, so a "Level 02" grid level lines up with a "Level 02" property.
        /// Returns null when the document has no grids, leaving storeys at 0 as before.
        /// </summary>
        public static Dictionary<string, double>? LevelElevations(Document doc, double unitScale)
        {
            if (doc == null) return null;
            Dictionary<string, double>? map = null;
            try
            {
                foreach (GridSystem sys in doc.Grids.Systems)
                {
                    foreach (GridLevel lv in sys.Levels)
                    {
                        var name = (lv.DisplayName ?? "").Trim();
                        if (name.Length == 0) continue;
                        map ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        // Several grid systems can name the same level; first one wins.
                        if (!map.ContainsKey(name)) map[name] = lv.Elevation * unitScale;
                    }
                }
            }
            catch { return map; } // grids are optional — never let this break an export
            return map;
        }

        /// <summary>One loaded model's coordinate facts, for the federation report.</summary>
        public sealed class ModelInfo
        {
            public string Name = "";
            public string Units = "";
            public double Ox, Oy, Oz;        // model transform translation, metres
            public bool Rotated;             // linear part is not identity
            public double NorthDeg;          // clockwise from +Y, when the model declares north
            public bool HasNorth;
            public double MinX, MinY, MinZ;  // bounding box min, metres
            public bool HasBounds;
        }

        public static List<ModelInfo> Survey(Document doc)
        {
            var list = new List<ModelInfo>();
            if (doc == null) return list;
            foreach (Model m in doc.Models)
            {
                var info = new ModelInfo();
                try
                {
                    string file = m.SourceFileName;
                    if (string.IsNullOrEmpty(file)) file = m.FileName;
                    info.Name = string.IsNullOrEmpty(file) ? "(model)" : System.IO.Path.GetFileName(file);
                }
                catch { info.Name = "(model)"; }

                double scale = 1.0;
                try { var u = UnitsToMetre(m.Units); scale = u.scale; info.Units = u.name; } catch { info.Units = "?"; }

                try
                {
                    var t = m.Transform;
                    info.Ox = t.Translation.X * scale;
                    info.Oy = t.Translation.Y * scale;
                    info.Oz = t.Translation.Z * scale;
                    info.Rotated = !t.Linear.IsIdentity();
                }
                catch { }

                try
                {
                    if (m.HasNorthVector)
                    {
                        var n = m.NorthVector;
                        // Clockwise from +Y (project north), which is how surveyors read it.
                        info.NorthDeg = Math.Atan2(n.X, n.Y) * 180.0 / Math.PI;
                        info.HasNorth = true;
                    }
                }
                catch { }

                try
                {
                    var bb = m.RootItem?.BoundingBox();
                    if (bb != null) { info.MinX = bb.Min.X * scale; info.MinY = bb.Min.Y * scale; info.MinZ = bb.Min.Z * scale; info.HasBounds = true; }
                }
                catch { }

                list.Add(info);
            }
            return list;
        }

        /// <summary>
        /// Human-readable federation report — the per-discipline origin/rotation table you want to
        /// see BEFORE exporting a federated model, not after discovering the models disagree.
        /// </summary>
        public static string FederationReport(Document doc)
        {
            var models = Survey(doc);
            if (models.Count == 0) return "No models loaded.";

            var sb = new StringBuilder();
            sb.AppendLine($"Loaded models: {models.Count}");
            sb.AppendLine();
            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                sb.AppendLine($"[{i + 1}] {m.Name}");
                sb.AppendLine($"    Units    : {m.Units}");
                sb.AppendLine($"    Origin   : [{F(m.Ox)}, {F(m.Oy)}, {F(m.Oz)}] m" + (m.Rotated ? "   (model transform is rotated/scaled)" : ""));
                sb.AppendLine(m.HasNorth ? $"    North    : {m.NorthDeg:0.###}° from +Y" : "    North    : not declared");
                if (m.HasBounds) sb.AppendLine($"    Min corner: [{F(m.MinX)}, {F(m.MinY)}, {F(m.MinZ)}] m");
            }

            // The point of the table: say plainly whether the disciplines agree.
            if (models.Count > 1)
            {
                sb.AppendLine();
                bool sameOrigin = true, sameUnits = true;
                for (int i = 1; i < models.Count; i++)
                {
                    if (Math.Abs(models[i].Ox - models[0].Ox) > 1e-6 ||
                        Math.Abs(models[i].Oy - models[0].Oy) > 1e-6 ||
                        Math.Abs(models[i].Oz - models[0].Oz) > 1e-6) sameOrigin = false;
                    if (!string.Equals(models[i].Units, models[0].Units, StringComparison.Ordinal)) sameUnits = false;
                }
                sb.AppendLine(sameOrigin ? "All models share one origin." : "WARNING: models do not share an origin — check federation before export.");
                if (!sameUnits) sb.AppendLine("WARNING: models declare different units.");
            }
            return sb.ToString().TrimEnd();
        }

        private static string F(double v) => v.ToString("N3", CultureInfo.InvariantCulture);
    }
}
