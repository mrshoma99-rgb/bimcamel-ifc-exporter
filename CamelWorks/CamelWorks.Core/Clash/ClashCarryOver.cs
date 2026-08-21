using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Findings;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Clash
{
    /// <summary>One result as a previous run left it.</summary>
    public sealed class ClashRecord
    {
        /// <summary>Create a record.</summary>
        public ClashRecord(ClashKey key, FindingStatus status, string? groupName = null,
                           bool groupWasPinned = false, string? testName = null)
        {
            Key = key;
            Status = status;
            GroupName = groupName;
            GroupWasPinned = groupWasPinned;
            TestName = testName;
        }

        /// <summary>Identity.</summary>
        public ClashKey Key { get; }

        /// <summary>Where it stood.</summary>
        public FindingStatus Status { get; }

        /// <summary>The group it was in.</summary>
        public string? GroupName { get; }

        /// <summary>True when that group was hand-made rather than derived by the stack.</summary>
        public bool GroupWasPinned { get; }

        /// <summary>The test that produced it.</summary>
        public string? TestName { get; }

        /// <inheritdoc />
        public override string ToString() =>
            FindingStatusLattice.ToName(Status) + " " + Key;
    }

    /// <summary>
    /// A board as it stood at the end of a run — the thing this week is compared against.
    ///
    /// Held separately from the live board because a delta needs a fixed reference. Comparing
    /// against "whatever the sidecar says right now" would make the delta change under a colleague
    /// who saved while you were reading it.
    /// </summary>
    public sealed class ClashSnapshot
    {
        /// <summary>Create a snapshot.</summary>
        public ClashSnapshot(IReadOnlyList<ClashRecord> records,
                             IReadOnlyDictionary<string, GroupDecision>? decisions = null,
                             string? label = null)
        {
            Records = records ?? throw new ArgumentNullException(nameof(records));
            Decisions = decisions ?? new Dictionary<string, GroupDecision>(StringComparer.Ordinal);
            Label = label;
        }

        /// <summary>Every result the run ended with.</summary>
        public IReadOnlyList<ClashRecord> Records { get; }

        /// <summary>The decisions people had made, keyed by group name.</summary>
        public IReadOnlyDictionary<string, GroupDecision> Decisions { get; }

        /// <summary>How the snapshot reads on screen — a date, a milestone, an issue number.</summary>
        public string? Label { get; }

        /// <summary>An empty snapshot — what the first run compares against.</summary>
        public static ClashSnapshot Empty { get; } = new ClashSnapshot(Array.Empty<ClashRecord>());

        /// <summary>
        /// Take a snapshot of a finished run, so the next one has something to compare against.
        /// </summary>
        public static ClashSnapshot Of(ClashPipelineResult result, string? label = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var records = new List<ClashRecord>();
            var decisions = new Dictionary<string, GroupDecision>(StringComparer.Ordinal);

            foreach (var group in result.Groups)
            {
                if (group.AssignedByHand)
                    decisions[group.Name] = new GroupDecision(group.AssignedTo, group.Priority);

                foreach (var item in group.Items)
                    records.Add(new ClashRecord(item.Key, item.Status, group.Name, group.IsPinned, item.TestName));
            }

            // Suppressed results are recorded too. Dropping them would make every one of them
            // arrive as New the moment somebody turns the rule off.
            foreach (var item in result.Suppressed)
                records.Add(new ClashRecord(item.Key, item.Status, null, false, item.TestName));

            return new ClashSnapshot(records, decisions, label);
        }
    }

    /// <summary>What changed since the snapshot.</summary>
    public sealed class ClashDelta
    {
        internal ClashDelta(IReadOnlyList<ClashItem> added, IReadOnlyList<ClashItem> persisting,
                            IReadOnlyList<ClashItem> regressed, IReadOnlyList<ClashRecord> resolved,
                            int matchedOnNeighbourCell, int weakMatches)
        {
            New = added; Persisting = persisting; Regressed = regressed; Resolved = resolved;
            MatchedOnNeighbourCell = matchedOnNeighbourCell; WeakMatches = weakMatches;
        }

        /// <summary>Results no previous run knew about.</summary>
        public IReadOnlyList<ClashItem> New { get; }

        /// <summary>Results that were there before and still are.</summary>
        public IReadOnlyList<ClashItem> Persisting { get; }

        /// <summary>
        /// Results somebody had marked Resolved that are back.
        ///
        /// The single number worth opening the report for: a fix that got undone. Native clash
        /// detection has no memory of it at all — the result simply reappears as New or Active, and
        /// nothing distinguishes it from a conflict nobody has ever looked at.
        ///
        /// Approved is deliberately not treated as a regression. Approved means "accepted, no
        /// change needed", so the result is expected to still be there; it carries its status and
        /// counts as persisting.
        /// </summary>
        public IReadOnlyList<ClashItem> Regressed { get; }

        /// <summary>Results the previous run had that this one does not — gone from the model.</summary>
        public IReadOnlyList<ClashRecord> Resolved { get; }

        /// <summary>
        /// How many matches needed the neighbour-cell tolerance rather than an exact hit.
        ///
        /// Shown because a large number means the model is shifting under the keys, and every
        /// carried status is then a slightly weaker claim than it looks.
        /// </summary>
        public int MatchedOnNeighbourCell { get; }

        /// <summary>
        /// How many matches rest on a geometry-derived element key — rung 3, the weakest.
        ///
        /// A rung-3 match carrying somebody's "Approved" onto what might be a different element is
        /// the one way carry-over can quietly do harm, so it is counted rather than assumed away.
        /// </summary>
        public int WeakMatches { get; }

        /// <summary>The one-line readout.</summary>
        public override string ToString()
        {
            var s = New.Count.ToString("N0", CultureInfo.InvariantCulture) + " new · "
                  + Persisting.Count.ToString("N0", CultureInfo.InvariantCulture) + " persisting · "
                  + Resolved.Count.ToString("N0", CultureInfo.InvariantCulture) + " resolved";
            if (Regressed.Count > 0)
                s += " · " + Regressed.Count.ToString("N0", CultureInfo.InvariantCulture) + " regressed";
            return s;
        }
    }

    /// <summary>
    /// Matches this run's results against the previous run's, and carries their history forward.
    ///
    /// Runs BEFORE the grouping stack, not after. A hand-made group is restored by carry-over and
    /// then honoured by the stack as a pin; if the stack ran first it would already have scattered
    /// those results across derived groups, and no later pass could put them back without undoing
    /// the derivation for everything else.
    /// </summary>
    public static class ClashCarryOver
    {
        /// <summary>
        /// Carry the snapshot's history onto this run's results, and report what changed.
        ///
        /// <b>Mutates the items</b>, setting <see cref="ClashItem.Status"/> and
        /// <see cref="ClashItem.CarriedGroup"/>. That is the point: everything downstream reads
        /// those two fields, so carry-over is the one place that writes them.
        /// </summary>
        public static ClashDelta Apply(IEnumerable<ClashItem> items, ClashSnapshot? snapshot)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            snapshot ??= ClashSnapshot.Empty;

            // Ordinal by key, so two results contending for one previous record resolve the same
            // way every time. Without it the board would depend on the order the engine happened
            // to enumerate results in, which is the one thing carry-over must never do.
            var current = items.Where(i => i != null).OrderBy(i => i.Key.ToString(), StringComparer.Ordinal).ToList();

            var byKey = new Dictionary<ClashKey, ClashRecord>();
            foreach (var record in snapshot.Records)
                if (!record.Key.IsEmpty) byKey[record.Key] = record;

            var claimed = new HashSet<ClashKey>();
            var matched = new Dictionary<ClashItem, ClashRecord>();
            var onNeighbour = 0;

            // Pass 1: every exact match, before any tolerance is spent. Doing it per-item would let
            // a drifted result claim the record belonging to one sitting exactly on it.
            foreach (var item in current)
            {
                if (item.Key.IsEmpty) continue;

                // Two results in one run can share a key exactly — the same pair, the same cell,
                // reported by two different tests. The first claims the record; the second is a
                // new result, not a second inheritor of one history.
                if (claimed.Contains(item.Key)) continue;
                if (!byKey.TryGetValue(item.Key, out var record)) continue;

                matched[item] = record;
                claimed.Add(record.Key);
            }

            // Pass 2: the neighbour block, for results whose clash point drifted across a cell
            // boundary. One record can only be claimed once — otherwise two results near a seam
            // would both inherit the same history.
            foreach (var item in current)
            {
                if (item.Key.IsEmpty || matched.ContainsKey(item)) continue;

                foreach (var candidate in item.Key.NeighbourCells())
                {
                    if (claimed.Contains(candidate)) continue;
                    if (!byKey.TryGetValue(candidate, out var record)) continue;

                    matched[item] = record;
                    claimed.Add(record.Key);
                    onNeighbour++;
                    break;
                }
            }

            var added = new List<ClashItem>();
            var persisting = new List<ClashItem>();
            var regressed = new List<ClashItem>();
            var weak = 0;

            foreach (var item in current)
            {
                if (!matched.TryGetValue(item, out var record))
                {
                    item.Status = FindingStatus.New;
                    added.Add(item);
                    continue;
                }

                if (item.Key.WeakestRung == KeyRung.Geometry) weak++;

                // Only hand-made groups carry. A derived group re-derives, because the stack is
                // the source of truth for it — carrying a derived name would freeze the board
                // against its own rules, so adding a Grid rule would change nothing.
                if (record.GroupWasPinned && !string.IsNullOrWhiteSpace(record.GroupName))
                    item.CarriedGroup = record.GroupName;

                if (record.Status == FindingStatus.Resolved)
                {
                    // Somebody signed this off and it is back. Active, not New: it has a history,
                    // and the report needs to say so.
                    item.Status = FindingStatus.Active;
                    regressed.Add(item);
                }
                else if (FindingStatusLattice.IsHumanJudgement(record.Status))
                {
                    // Reviewed and Approved are the only two the engine does not recompute, so
                    // they are the only two safe to carry as truth.
                    item.Status = record.Status;
                    persisting.Add(item);
                }
                else
                {
                    item.Status = FindingStatus.Active;
                    persisting.Add(item);
                }
            }

            // From the index, not the list: a snapshot may carry two records for one key, and
            // reporting the same vanished result twice would overstate the week's progress.
            var resolved = byKey.Values
                .Where(r => !claimed.Contains(r.Key))
                .OrderBy(r => r.Key.ToString(), StringComparer.Ordinal)
                .ToList();

            return new ClashDelta(added, persisting, regressed, resolved, onNeighbour, weak);
        }
    }
}
