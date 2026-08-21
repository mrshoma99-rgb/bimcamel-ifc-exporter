using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.Store
{
    /// <summary>How an attempt to take the project lease ended.</summary>
    public enum LeaseOutcome
    {
        /// <summary>Taken.</summary>
        Acquired = 0,

        /// <summary>Somebody else holds it and it has not expired.</summary>
        HeldByAnother = 1,

        /// <summary>Taken over from a holder whose lease had expired — a crash, or a closed laptop.</summary>
        TakenOverFromStale = 2,

        /// <summary>
        /// Refused because the substrate cannot support a lease. Not a failure to be retried —
        /// see <see cref="StorageSubstrate"/>.
        /// </summary>
        UnsupportedSubstrate = 3,
    }

    /// <summary>The result of an attempt on the lease.</summary>
    public sealed class LeaseResult
    {
        internal LeaseResult(LeaseOutcome outcome, string? holder, long expiresTicks, string? reason)
        {
            Outcome = outcome; Holder = holder; ExpiresTicks = expiresTicks; Reason = reason;
        }

        /// <summary>How it ended.</summary>
        public LeaseOutcome Outcome { get; }

        /// <summary>Who holds it now — us on success, somebody else on refusal.</summary>
        public string? Holder { get; }

        /// <summary>When the current lease lapses, as UTC ticks.</summary>
        public long ExpiresTicks { get; }

        /// <summary>A sentence for the pane when the lease was not taken.</summary>
        public string? Reason { get; }

        /// <summary>True when this build may write project settings.</summary>
        public bool MayWrite => Outcome == LeaseOutcome.Acquired || Outcome == LeaseOutcome.TakenOverFromStale;
    }

    /// <summary>
    /// The advisory lease on <c>project.cwproj</c>.
    ///
    /// Advisory on purpose. It stops the ordinary accident — two coordinators editing the clash
    /// matrix at the same time and one of them losing an afternoon — and it does not pretend to be
    /// a distributed lock. Anything stronger would need a server, and this product does not have
    /// one by design.
    ///
    /// Three properties that matter more than the mechanism:
    ///
    /// * <b>It expires.</b> A lease that outlives a crashed process locks a team out of their own
    ///   project until somebody deletes a file they have never heard of. So it carries a deadline
    ///   and a later writer takes it over, saying so.
    /// * <b>It never blocks reading.</b> Losing the lease costs the ability to SAVE settings, and
    ///   nothing else. The board, the reports and the journals keep working, because those are
    ///   per-writer and merge.
    /// * <b>It refuses rather than lies on a sync root.</b> See <see cref="StorageSubstrate"/>.
    /// </summary>
    public static class ProjectLease
    {
        /// <summary>Default lifetime: long enough to survive a slow save, short enough that a crash
        /// does not strand the team for an afternoon.</summary>
        public static readonly long DefaultTtlTicks = TimeSpan.FromMinutes(10).Ticks;

        /// <summary>Try to take the lease.</summary>
        /// <param name="existingLeaseFile">Contents of the lease file, or null when absent.</param>
        /// <param name="owner">Our stable writer id.</param>
        /// <param name="nowTicks">Current UTC ticks, supplied so this is testable.</param>
        /// <param name="substrate">What the sidecar folder is sitting on.</param>
        /// <param name="ttlTicks">Lease lifetime; defaults to <see cref="DefaultTtlTicks"/>.</param>
        public static LeaseResult TryAcquire(
            string? existingLeaseFile,
            string owner,
            long nowTicks,
            SubstrateKind substrate = SubstrateKind.LocalDisk,
            long ttlTicks = 0)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
            if (ttlTicks <= 0) ttlTicks = DefaultTtlTicks;

            if (!StorageSubstrate.SupportsLease(substrate))
                return new LeaseResult(LeaseOutcome.UnsupportedSubstrate, null, 0,
                    StorageSubstrate.LeaseRefusalReason(substrate));

            var expires = nowTicks + ttlTicks;

            if (string.IsNullOrWhiteSpace(existingLeaseFile) ||
                !JsonReader.TryParse(existingLeaseFile, out var json) ||
                json.Kind != JsonKind.Object)
            {
                // No lease, or an unreadable one. An unreadable lease is treated as absent rather
                // than as a reason to refuse: a corrupt lock file must not be able to lock a team
                // out of their own project permanently.
                return new LeaseResult(LeaseOutcome.Acquired, owner, expires, null);
            }

            var holder = json["holder"].AsString();
            var heldUntil = json["expires"].AsLong();

            if (holder == null || holder == owner)
                return new LeaseResult(LeaseOutcome.Acquired, owner, expires, null);

            if (heldUntil <= nowTicks)
                return new LeaseResult(LeaseOutcome.TakenOverFromStale, owner, expires,
                    "Taken over from " + holder + ", whose lease had expired.");

            var minutes = Math.Max(1, (int)TimeSpan.FromTicks(heldUntil - nowTicks).TotalMinutes);
            return new LeaseResult(LeaseOutcome.HeldByAnother, holder, heldUntil,
                holder + " is editing project settings (for about another "
                + minutes.ToString(CultureInfo.InvariantCulture) + " minute" + (minutes == 1 ? "" : "s")
                + "). You can still triage, report and export — only settings are locked.");
        }

        /// <summary>Serialise a held lease.</summary>
        public static string Write(string owner, long expiresTicks) =>
            JsonValue.Object()
                .Set("holder", owner)
                .Set("expires", expiresTicks)
                .ToJson(indented: false);

        /// <summary>
        /// Release a lease we hold. Releasing one we do not hold is a no-op rather than an error:
        /// after a takeover, the previous holder's release must not evict the new one.
        /// </summary>
        public static string? Release(string? existingLeaseFile, string owner)
        {
            if (string.IsNullOrWhiteSpace(existingLeaseFile)) return null;
            if (!JsonReader.TryParse(existingLeaseFile, out var json) || json.Kind != JsonKind.Object) return null;

            return json["holder"].AsString() == owner ? null : existingLeaseFile;
        }
    }

    /// <summary>
    /// Which sections of a project file two versions disagree about.
    ///
    /// Shown when a save is refused, because "somebody else changed the file" is not something a
    /// user can act on. "They changed the clash matrix and the party list; your rule edits are
    /// untouched" is — it tells them whether to wait, merge by hand, or carry on.
    /// </summary>
    public static class SectionDiff
    {
        /// <summary>Compare two project documents section by section. Order-independent.</summary>
        public static IReadOnlyList<string> ChangedSections(JsonValue? mine, JsonValue? theirs)
        {
            var changed = new List<string>();
            if (mine == null || theirs == null) return changed;

            var names = mine.Keys.Concat(theirs.Keys)
                .Where(k => k != VersionedDocument.VersionKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal);

            foreach (var name in names)
            {
                // Comparing serialised form is exact and cheap, and it is only meaningful because
                // the writer preserves key order — otherwise two identical documents would compare
                // as different and every save would report a phantom conflict.
                var a = mine.Has(name) ? mine[name].ToJson(indented: false) : null;
                var b = theirs.Has(name) ? theirs[name].ToJson(indented: false) : null;
                if (!string.Equals(a, b, StringComparison.Ordinal)) changed.Add(name);
            }

            return changed;
        }

        /// <summary>The sentence shown on a refused write.</summary>
        public static string Describe(IReadOnlyList<string> changedSections, string? otherHolder)
        {
            if (changedSections == null || changedSections.Count == 0)
                return "The settings file changed on disk, but nothing you can see is different.";

            var who = string.IsNullOrWhiteSpace(otherHolder) ? "Someone else" : otherHolder;
            return who + " changed: " + string.Join(", ", changedSections)
                 + ". Your other edits are unaffected.";
        }
    }
}
