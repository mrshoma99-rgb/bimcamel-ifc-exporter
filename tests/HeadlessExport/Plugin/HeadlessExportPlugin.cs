using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using BIMCamel.Collect;
using BIMCamel.Geometry;
using BIMCamel.Ifc;

namespace BIMCamel.Tests
{
    /// <summary>
    /// Runs the instanced export on the active document with the exporter's default settings,
    /// no UI. Arguments: output path, merge-parts threshold (0 = off), schema (IFC4 | IFC2X3),
    /// and optionally "Skip geometry smaller than" and the simplification tolerance, both in
    /// millimetres (0 = off).
    /// Writes "&lt;output&gt;.summary.txt" beside the IFC.
    /// </summary>
    [Plugin("HeadlessExport", "BCTST")]
    [AddInPlugin(AddInLocation.None)]
    public class HeadlessExportPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            string outPath = parameters.Length > 0 ? parameters[0] : "";
            var sb = new StringBuilder();
            try
            {
                int mergeAbove = parameters.Length > 1 ? int.Parse(parameters[1], CultureInfo.InvariantCulture) : 16;
                var schema = parameters.Length > 2 && parameters[2].Equals("IFC2X3", StringComparison.OrdinalIgnoreCase) ? IfcSchema.Ifc2x3 : IfcSchema.Ifc4;
                double minMm = parameters.Length > 3 ? double.Parse(parameters[3], CultureInfo.InvariantCulture) : 0;
                double simplifyMm = parameters.Length > 4 ? double.Parse(parameters[4], CultureInfo.InvariantCulture) : 0;
                var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
                ExportIssues.Reset(); ExportTiming.Reset();
                var (unitScale, unitName) = ModelSurvey.UnitsToMetre(doc.Units);
                var coords = new CoordOptions();
                var names = new SpatialNames { LevelElevations = ModelSurvey.LevelElevations(doc, unitScale) };
                var items = ItemCollector.GetVisibleLeafItemsWithGeometry(doc, null, true);
                var sm = ItemCollector.MinCornerValid ? ItemCollector.LastMinCorner : ItemCollector.ScopeMinCorner(items);
                var geomMin = (sm.x * unitScale, sm.y * unitScale, sm.z * unitScale);
                // The UI's defaults: Balanced quality, props + materials, quantities on.
                var opts = new ExtractOptions { Props = true, Materials = true, WeldTol = 1e-4, MinFragmentSize = minMm / 1000.0, SimplifyTol = simplifyMm / 1000.0 };
                var summary = new ExportSummary();
                var sw = Stopwatch.StartNew();
                var stream = InstancedExtractor.ExtractStream(items, unitScale, opts);
                ExportPipeline.Run(stream, s =>
                    IfcExporter.ExportInstanced(outPath, schema, s, "test", unitScale, coords, true, 4, geomMin, names, 0, summary, false, mergeAbove));
                sw.Stop();
                var issues = IfcValidator.Validate(outPath);

                sb.AppendLine($"items       : {items.Count}");
                sb.AppendLine($"units       : {unitName}");
                sb.AppendLine($"mergeAbove  : {mergeAbove}");
                sb.AppendLine($"minSize mm  : {minMm}");
                sb.AppendLine($"simplify mm : {simplifyMm}");
                sb.AppendLine($"elements    : {summary.ElementCount}");
                sb.AppendLine($"triangles   : {summary.TriangleCount}");
                sb.AppendLine($"source tris : {summary.SourceTriangleCount}");
                sb.AppendLine($"simplified  : {ExportTiming.SimplifiedMeshes} mesh(es), {ExportTiming.SimplifyTrisIn} -> {ExportTiming.SimplifyTrisOut} tris, {ExportTiming.SimplifyReused} reuse(s), {ExportTiming.Ms(ExportTiming.SimplifyTicks):0} ms");
                sb.AppendLine($"unique geom : {summary.UniqueGeometries}");
                sb.AppendLine($"instances   : {summary.InstanceCount}");
                sb.AppendLine($"merged els  : {summary.MergedElements}");
                sb.AppendLine($"merged shps : {summary.MergedShapes}");
                sb.AppendLine($"fragments   : {ExportTiming.Fragments} read, {ExportTiming.ForeignFragmentsSkipped} foreign skipped, {ExportTiming.OwnFragmentFallbacks} fallback path(s)");
                sb.AppendLine($"bytes       : {summary.FileSizeBytes}");
                sb.AppendLine($"seconds     : {sw.Elapsed.TotalSeconds:0.0}");
                sb.AppendLine($"validator   : {issues.Count} issue(s)");
                foreach (var i in issues) sb.AppendLine("  " + i);
                sb.Append(ExportIssues.Report());
            }
            catch (Exception ex) { sb.AppendLine("FAILED: " + ex); }
            File.WriteAllText(outPath + ".summary.txt", sb.ToString());
            return 0;
        }
    }
}
