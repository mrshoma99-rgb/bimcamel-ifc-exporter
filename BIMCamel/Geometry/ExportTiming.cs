using System.Diagnostics;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Lightweight per-export profiling counters (single-threaded export, so plain static fields are
    /// fine). The extractors add to these around the costly COM sections; the UI reports them so a
    /// single long run shows where the time actually goes — and, crucially, whether the per-item COM
    /// conversion (which S2 would remove) or the per-triangle primitive read (which only the S3 native
    /// shim removes) dominates. Reset() at the start of every export.
    /// </summary>
    public static class ExportTiming
    {
        public static long ComConvertTicks;   // ComApiBridge.ToInwOpSelection per item
        public static long ReadTicks;         // GenerateSimplePrimitives walk (the per-triangle cost)
        public static long HarvestTicks;      // property + role harvesting
        public static long WeldTicks;         // vertex welding
        public static long Fragments;         // fragments processed
        public static long ComConverts;       // items converted to COM selections

        // Write-side decomposition (exporter), to split the big "Write+other" bucket.
        public static long GeomWriteTicks;    // serialising mesh entities (point lists / face sets)
        public static long PropWriteTicks;    // serialising property sets (incl. content hashing)
        public static long QtyTicks;          // computing + serialising base quantities
        public static long UiTicks;           // Application.DoEvents() message-pump cost
        public static long UiPumps;           // number of DoEvents calls

        public static void Reset()
        {
            ComConvertTicks = ReadTicks = HarvestTicks = WeldTicks = 0;
            Fragments = ComConverts = 0;
            GeomWriteTicks = PropWriteTicks = QtyTicks = 0;
            UiTicks = 0; UiPumps = 0;
        }

        public static long Now => Stopwatch.GetTimestamp();
        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
