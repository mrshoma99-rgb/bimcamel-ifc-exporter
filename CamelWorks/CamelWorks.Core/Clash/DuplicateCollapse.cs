using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Clash
{
    /// <summary>What the collapse folded away, and where each one went.</summary>
    public sealed class CollapseResult
    {
        internal CollapseResult(IReadOnlyList<ClashItem> kept, IReadOnlyList<ClashItem> collapsed,
                                IReadOnlyDictionary<ClashItem, ClashItem> foldedInto)
        {
            Kept = kept; Collapsed = collapsed; FoldedInto = foldedInto;
        }

        /// <summary>What goes on to the pipeline.</summary>
        public IReadOnlyList<ClashItem> Kept { get; }

        /// <summary>
        /// What was folded away — kept in full, never discarded. A coordinator asked why the
        /// Clearance test reports 900 and the board shows 40 needs to be able to open the 860.
        /// </summary>
        public IReadOnlyList<ClashItem> Collapsed { get; }

        /// <summary>Each collapsed result mapped to the one it was folded into.</summary>
        public IReadOnlyDictionary<ClashItem, ClashItem> FoldedInto { get; }

        /// <summary>How many were folded away.</summary>
        public int Count => Collapsed.Count;
    }

    /// <summary>
    /// Folds the same physical conflict reported by two different tests into one row.
    ///
    /// A federation typically runs a hard-clash test and a clearance test over the same two models.
    /// One duct through one beam is then reported twice, and a coordinator resolves it twice, in
    /// two different places, with two different statuses — the second of which nobody updates.
    ///
    /// <b>The match is a proximity band, never exact key equality.</b> Two tests with different
    /// tolerances compute different intersection points on the same pair, so the same conflict
    /// quantises into different cells and an equality test finds nothing. The band is deliberately
    /// coarse relative to the clash key's cell.
    ///
    /// <b>It never folds two results from the same test.</b> Within one test, two results on one
    /// pair a metre apart are two genuine penetrations — the case the whole positional key exists
    /// to keep separate. Only results the same test would not have produced twice are candidates.
    /// </summary>
    public static class DuplicateCollapse
    {
        /// <summary>The default band, in metres. Four clash-key cells wide.</summary>
        public const double DefaultBand = 1.0;

        /// <summary>Fold cross-test duplicates.</summary>
        /// <param name="items">Every result, from every test.</param>
        /// <param name="bandMetres">How close two tests' points must be to be the same conflict.</param>
        public static CollapseResult Across(IEnumerable<ClashItem> items, double bandMetres = DefaultBand)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (bandMetres <= 0) throw new ArgumentOutOfRangeException(nameof(bandMetres), "band must be positive");

            var all = items.Where(i => i != null).ToList();
            var kept = new List<ClashItem>();
            var collapsed = new List<ClashItem>();
            var foldedInto = new Dictionary<ClashItem, ClashItem>();

            foreach (var bucket in Bucket(all, bandMetres))
            {
                var tests = bucket
                    .GroupBy(i => i.TestName, StringComparer.Ordinal)
                    .ToList();

                if (tests.Count < 2)
                {
                    // One test's results are never duplicates of each other.
                    kept.AddRange(bucket);
                    continue;
                }

                var winner = PickWinner(tests);
                var keepers = winner.OrderBy(i => i.Key.ToString(), StringComparer.Ordinal).ToList();
                kept.AddRange(keepers);

                foreach (var test in tests)
                {
                    if (ReferenceEquals(test, winner)) continue;

                    foreach (var item in test.OrderBy(i => i.Key.ToString(), StringComparer.Ordinal))
                    {
                        collapsed.Add(item);
                        foldedInto[item] = Nearest(keepers, item);
                    }
                }
            }

            return new CollapseResult(kept, collapsed, foldedInto);
        }

        // Same pair, same band cell. The pair comes first because a conflict between two other
        // elements at the same place is a different conflict, however close it sits.
        private static IEnumerable<List<ClashItem>> Bucket(List<ClashItem> all, double band)
        {
            var buckets = new Dictionary<string, List<ClashItem>>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var item in all)
            {
                var key = item.Key.IsEmpty ? "?" + item.TestName : item.Key.PairId;
                key += "|" + Hash.Quantise(item.X, band).ToString(CultureInfo.InvariantCulture)
                     + "," + Hash.Quantise(item.Y, band).ToString(CultureInfo.InvariantCulture)
                     + "," + Hash.Quantise(item.Z, band).ToString(CultureInfo.InvariantCulture);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<ClashItem>();
                    buckets[key] = bucket;
                    order.Add(key);
                }

                bucket.Add(item);
            }

            foreach (var key in order) yield return buckets[key];
        }

        // The test reporting the most severe result wins, so the board keeps the hard clash and
        // folds the clearance miss into it rather than the other way round. Ordinal test name
        // breaks the tie, because two runs of identical data must fold identically.
        private static IGrouping<string, ClashItem> PickWinner(List<IGrouping<string, ClashItem>> tests) =>
            tests
                .OrderByDescending(t => t.Max(i => i.OverlapVolume ?? double.NegativeInfinity))
                .ThenBy(t => t.Key, StringComparer.Ordinal)
                .First();

        private static ClashItem Nearest(List<ClashItem> keepers, ClashItem item)
        {
            var best = keepers[0];
            var bestDistance = double.MaxValue;

            foreach (var keeper in keepers)
            {
                double dx = keeper.X - item.X, dy = keeper.Y - item.Y, dz = keeper.Z - item.Z;
                var d = (dx * dx) + (dy * dy) + (dz * dz);

                if (d < bestDistance)
                {
                    best = keeper;
                    bestDistance = d;
                }
            }

            return best;
        }
    }
}
