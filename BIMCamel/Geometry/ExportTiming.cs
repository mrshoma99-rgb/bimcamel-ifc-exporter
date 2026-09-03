using System.Diagnostics;

namespace BIMCamel.Geometry
{
    /// <summary>
    /// Lightweight per-export profiling counters. The extractors add to these around the costly COM
    /// sections; the UI reports them so a single long run shows where the time actually goes — and,
    /// crucially, whether the per-item COM conversion (E3) or the per-triangle primitive read (which
    /// only the native shim removes) dominates. Reset() at the start of every export.
    ///
    /// THREAD OWNERSHIP (v5 E7). The export is no longer single-threaded: the extractor produces on
    /// the Navisworks UI thread while a background consumer writes. Plain static fields are still
    /// correct — but only because every counter is written by EXACTLY ONE thread, and only read
    /// after that thread has been joined (a join is a memory barrier, so the reader sees the writes).
    /// The two groups are marked below; a new counter must be added to the group whose thread
    /// touches it, or it needs Interlocked. Do not add a counter both threads increment.
    /// </summary>
    public static class ExportTiming
    {
        // ── produced on the READ thread (Navisworks UI thread) ─────────────────────────────
        public static long ComConvertTicks;   // ComApiBridge.ToInwOpSelection (per chunk since E3)
        public static long ReadTicks;         // GenerateSimplePrimitives walk (the per-triangle cost)
        public static long HarvestTicks;      // property + role harvesting (one pass since E2)
        public static long WeldTicks;         // vertex welding (and folded quantities since E4)
        public static long QtyTicks;          // quantity computation not folded into the weld
        public static long Fragments;         // fragments processed
        public static long ComConverts;       // COM selection conversions performed
        public static long UiTicks;           // Dispatcher pump cost
        public static long UiPumps;           // number of pumps

        // E1 (fast instancing): how well the pre-read identity guess held up.
        public static long GeomReadsSkipped;  // fragment reads avoided by a candidate-key hit
        public static long GeomVerifications; // sampled full reads done to check a cached candidate
        public static long GeomMismatches;    // verifications that DISAGREED — the guess was wrong
        public static long GeomEvictions;     // candidates dropped for the memory budget

        // ── produced on the WRITE thread (background consumer since E7) ────────────────────
        public static long GeomWriteTicks;    // serialising mesh entities (point lists / face sets)
        public static long PropWriteTicks;    // serialising property sets (incl. content hashing)
        public static long QtyWriteTicks;     // serialising base quantities

        // ── scan phase (UI thread, before the export proper) — v5 S4 ──────────────────────
        // One "Scan" number could not say whether the tree walk, the set searches or the property
        // sample was the slow half, so S1/S2/S3 could only ever be plausible. These make them
        // verifiable.
        public static long CollectTicks;      // the leaf tree walk
        public static long SetResolveTicks;   // Search.FindAll per mapping set + key assignment
        public static long MinCornerTicks;    // scope minimum corner
        public static long PropScanTicks;     // the sampled property/category scan
        public static long NodesVisited;      // tree nodes touched by the walk
        public static long SetsResolved;      // distinct sets actually resolved
        public static long SampledItems;      // items the property scan actually read

        // ── after the file is closed ───────────────────────────────────────────────────────
        public static long ValidateTicks;     // structural validation of the written file(s)

        public static void Reset()
        {
            ComConvertTicks = ReadTicks = HarvestTicks = WeldTicks = QtyTicks = 0;
            Fragments = ComConverts = 0;
            UiTicks = 0; UiPumps = 0;
            GeomReadsSkipped = GeomVerifications = GeomMismatches = GeomEvictions = 0;
            GeomWriteTicks = PropWriteTicks = QtyWriteTicks = 0;
            ValidateTicks = 0;
        }

        /// <summary>Scan counters live across the scan→export boundary, so they reset separately —
        /// the export's Reset() must not wipe the numbers the scan just produced.</summary>
        public static void ResetScan()
        {
            CollectTicks = SetResolveTicks = MinCornerTicks = PropScanTicks = 0;
            NodesVisited = SetsResolved = SampledItems = 0;
        }

        public static long Now => Stopwatch.GetTimestamp();
        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
