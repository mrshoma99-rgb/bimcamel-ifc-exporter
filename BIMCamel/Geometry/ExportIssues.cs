using System;
using System.Collections.Generic;
using System.Text;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Per-export tally of everything that did NOT reach the IFC.
    ///
    /// Elements used to disappear silently: a mesh welded down to nothing, or an item whose
    /// geometry read threw, simply never appeared in the file and was not counted anywhere, so
    /// "some elements are missing" was invisible from inside the plug-in. Every drop is now
    /// recorded here and printed in the export report.
    ///
    /// Single-threaded export, so plain static fields are fine (same pattern as
    /// <see cref="ExportTiming"/>). <see cref="Reset"/> runs at the START of an export — before
    /// scope collection, which is where <see cref="BranchGeometryRecovered"/> is counted.
    /// </summary>
    public static class ExportIssues
    {
        /// <summary>Items that yielded no triangles at all. Normal for many nodes (groups,
        /// annotations); reported for completeness rather than as a fault.</summary>
        public static int NoTriangles;

        /// <summary>ELEMENTS lost entirely to vertex welding — every triangle they had became
        /// degenerate, i.e. the whole part is smaller than roughly half the weld tolerance.</summary>
        public static int CollapsedByWeld;

        /// <summary>Individual welded-away pieces of elements that DID export (instanced path:
        /// one item can hold many fragments). Nothing is missing from the model when this is
        /// non-zero — counted apart from <see cref="CollapsedByWeld"/> so the report can never
        /// imply data loss that did not happen.</summary>
        public static int CollapsedFragments;

        /// <summary>Items skipped because reading their geometry or properties threw.</summary>
        public static int Failed;

        /// <summary>Fragments dropped by the minimum-size filter (v6 Z7). Unlike a weld collapse
        /// this is a deliberate removal the user asked for — counted so the report can say exactly
        /// how much was left out rather than letting it look like the model simply had less in it.</summary>
        public static int TooSmall;

        /// <summary>Geometry-bearing nodes that also had children. Before v0.8.0 the collector
        /// only took childless nodes, so these were dropped (a SolidWorks/Inventor part whose
        /// children are reference planes vanished entirely).</summary>
        public static int BranchGeometryRecovered;

        /// <summary>First few failure messages, so the report is actionable.</summary>
        public static readonly List<string> FailureSamples = new List<string>();

        public static void Reset()
        {
            TooSmall = NoTriangles = CollapsedByWeld = CollapsedFragments = Failed = BranchGeometryRecovered = 0;
            FailureSamples.Clear();
        }

        /// <summary>Records a per-item failure; the export continues with the next item.</summary>
        public static void Fail(string? itemName, Exception ex)
        {
            Failed++;
            if (FailureSamples.Count < 5)
                FailureSamples.Add((string.IsNullOrWhiteSpace(itemName) ? "(unnamed)" : itemName) + " — " + ex.Message);
        }

        /// <summary>Report text. Empty string when nothing of note happened.</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            if (CollapsedByWeld > 0)
                sb.AppendLine($"  {CollapsedByWeld:N0} element(s) LOST to vertex welding — smaller than the weld tolerance; use a finer Geometry quality to keep them");
            if (CollapsedFragments > 0)
                sb.AppendLine($"  {CollapsedFragments:N0} sub-part(s) welded away inside elements that DID export (no element lost)");
            if (Failed > 0)
            {
                sb.AppendLine($"  {Failed:N0} element(s) failed to read and were skipped:");
                foreach (var f in FailureSamples) sb.AppendLine("      " + f);
                if (Failed > FailureSamples.Count) sb.AppendLine($"      … and {Failed - FailureSamples.Count:N0} more");
            }
            if (TooSmall > 0)
                sb.AppendLine($"  {TooSmall:N0} part(s) left out by the minimum-size filter — a deliberate removal, not a loss; untick \"Skip geometry smaller than\" to keep them");
            if (NoTriangles > 0)
                sb.AppendLine($"  {NoTriangles:N0} item(s) carried no triangles (groups / annotations — normal)");
            if (BranchGeometryRecovered > 0)
                sb.AppendLine($"  {BranchGeometryRecovered:N0} geometry node(s) that also have children were exported (dropped before v0.8.0)");
            return sb.ToString();
        }
    }
}
