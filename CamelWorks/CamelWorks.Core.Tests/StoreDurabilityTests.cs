using System;
using System.Linq;
using CamelWorks.Core.Store;
using CamelWorks.Core.Testing;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class AtomicFileTests
    {
        private const string Path = "/proj/.camelworks/project.cwproj";

        [Fact]
        public void A_crash_before_the_replace_leaves_the_old_file_intact()
        {
            // The scenario the whole mechanism exists for, and the one that cannot be provoked
            // against a real disk on demand.
            var fs = new InMemoryFileSystem().With(Path, "{\"schemaVersion\":1,\"good\":true}");
            fs.FailOnce = "replace";

            Assert.Throws<SimulatedIoException>(() => AtomicFile.Write(fs, Path, "{\"half-writ"));

            Assert.Equal("{\"schemaVersion\":1,\"good\":true}", fs.ReadAllText(Path));
        }

        [Fact]
        public void A_crash_during_the_temp_write_never_touches_the_target()
        {
            var fs = new InMemoryFileSystem().With(Path, "original");
            fs.FailOnce = "write";

            Assert.Throws<SimulatedIoException>(() => AtomicFile.Write(fs, Path, "new"));

            Assert.Equal("original", fs.ReadAllText(Path));
        }

        [Fact]
        public void A_successful_write_replaces_the_target_and_keeps_the_previous_contents()
        {
            var fs = new InMemoryFileSystem().With(Path, "v1");

            AtomicFile.Write(fs, Path, "v2");

            Assert.Equal("v2", fs.ReadAllText(Path));
            Assert.Equal("v1", fs.ReadAllText(Path + AtomicFile.BackupSuffix));
        }

        [Fact]
        public void The_temp_file_does_not_survive_a_successful_write()
        {
            var fs = new InMemoryFileSystem().With(Path, "v1");

            AtomicFile.Write(fs, Path, "v2");

            Assert.False(fs.Exists(Path + AtomicFile.TempSuffix));
        }

        [Fact]
        public void Writing_a_file_that_does_not_exist_yet_works()
        {
            // First run. Replace-onto-nothing is a move, and getting this wrong means CamelWorks
            // cannot create its own store.
            var fs = new InMemoryFileSystem();

            AtomicFile.Write(fs, Path, "first");

            Assert.Equal("first", fs.ReadAllText(Path));
        }

        [Fact]
        public void The_temp_file_is_a_sibling_of_the_target()
        {
            // A replace across volumes is a copy: not atomic, not fast, and it reopens the exact
            // window this closes. So the temp file must live beside the target.
            var fs = new InMemoryFileSystem();
            AtomicFile.Write(fs, Path, "x");

            var tempWrite = fs.Log.First(l => l.StartsWith("write ", StringComparison.Ordinal));
            Assert.StartsWith("write /proj/.camelworks/", tempWrite, StringComparison.Ordinal);
        }

        [Fact]
        public void A_missing_file_falls_back_to_its_backup()
        {
            // Without this the .bak is a file nobody ever opens, and the recovery it exists for is
            // a manual rename somebody has to be told about in a support ticket.
            var fs = new InMemoryFileSystem().With(Path + AtomicFile.BackupSuffix, "recovered");

            var text = AtomicFile.ReadWithFallback(fs, Path, out var usedBackup);

            Assert.Equal("recovered", text);
            Assert.True(usedBackup);
        }

        [Fact]
        public void An_empty_file_falls_back_to_its_backup()
        {
            // An empty file is what a truncating write leaves behind, and it parses as "missing"
            // rather than as damage — so it has to trigger the same recovery.
            var fs = new InMemoryFileSystem().With(Path, "").With(Path + AtomicFile.BackupSuffix, "recovered");

            Assert.Equal("recovered", AtomicFile.ReadWithFallback(fs, Path, out var used));
            Assert.True(used);
        }

        [Fact]
        public void A_healthy_file_does_not_consult_the_backup()
        {
            var fs = new InMemoryFileSystem().With(Path, "current").With(Path + AtomicFile.BackupSuffix, "stale");

            Assert.Equal("current", AtomicFile.ReadWithFallback(fs, Path, out var used));
            Assert.False(used);
        }

        [Fact]
        public void A_temp_file_left_by_a_crash_is_cleaned_up()
        {
            var fs = new InMemoryFileSystem().With(Path + AtomicFile.TempSuffix, "junk");

            AtomicFile.CleanUpTemp(fs, Path);

            Assert.False(fs.Exists(Path + AtomicFile.TempSuffix));
        }
    }

    public class StorageSubstrateTests
    {
        [Theory]
        [InlineData(@"C:\Users\anna\OneDrive - Acme\Project\.camelworks")]
        [InlineData(@"C:\Users\ben\Dropbox\Job\.camelworks")]
        [InlineData("/home/anna/Nextcloud/job/.camelworks")]
        [InlineData(@"\\server\share\SharePoint\job\.camelworks")]
        public void A_synced_folder_is_recognised_wherever_it_sits(string path)
        {
            // A synced folder reached over UNC is still synced.
            Assert.Equal(SubstrateKind.SyncRoot, StorageSubstrate.Classify(path));
        }

        [Theory]
        [InlineData(@"C:\Projects\Job\.camelworks", SubstrateKind.LocalDisk)]
        [InlineData("/mnt/data/job/.camelworks", SubstrateKind.LocalDisk)]
        [InlineData(@"\\fileserver\projects\job\.camelworks", SubstrateKind.NetworkShare)]
        [InlineData("relative/path", SubstrateKind.Unknown)]
        [InlineData(null, SubstrateKind.Unknown)]
        public void Other_substrates_classify_as_expected(string? path, SubstrateKind expected)
        {
            Assert.Equal(expected, StorageSubstrate.Classify(path));
        }

        [Fact]
        public void A_sync_root_supports_neither_leases_nor_retention()
        {
            Assert.False(StorageSubstrate.SupportsLease(SubstrateKind.SyncRoot));
            Assert.False(StorageSubstrate.SupportsRetention(SubstrateKind.SyncRoot));

            Assert.True(StorageSubstrate.SupportsLease(SubstrateKind.NetworkShare));
            Assert.True(StorageSubstrate.SupportsLease(SubstrateKind.Unknown));
        }

        [Fact]
        public void The_refusal_says_what_still_works()
        {
            // "Cannot lock" on its own reads as "CamelWorks is broken here". The sentence has to
            // say that triage and reports are unaffected, because they genuinely are.
            var reason = StorageSubstrate.LeaseRefusalReason(SubstrateKind.SyncRoot)!;

            Assert.Contains("synced folder", reason, StringComparison.Ordinal);
            Assert.Contains("board edits are safe", reason, StringComparison.Ordinal);
            Assert.Null(StorageSubstrate.LeaseRefusalReason(SubstrateKind.LocalDisk));
        }

        [Theory]
        [InlineData("triage-anna (Anna's conflicted copy 2026-08-21).jsonl", true)]
        [InlineData("triage-anna-CONFLICT.jsonl", true)]
        [InlineData("triage-anna.jsonl", false)]
        [InlineData("something-else.jsonl", false)]
        public void A_conflicted_copy_is_recognised(string fileName, bool expected)
        {
            Assert.Equal(expected, StorageSubstrate.IsConflictedCopy(fileName, "triage-anna"));
        }

        [Fact]
        public void Conflicted_copies_are_folded_rather_than_ignored()
        {
            // A conflicted copy of a journal is somebody's real edits. Leaving it on disk is how a
            // day of triage disappears.
            var files = new[]
            {
                "triage-anna.jsonl",
                "triage-anna (Anna's conflicted copy 2026-08-21).jsonl",
                "triage-ben.jsonl",
                "unrelated.txt",
            };

            var toFold = StorageSubstrate.FilesToFold(files, "triage-anna");

            Assert.Equal(2, toFold.Count);
            Assert.DoesNotContain("triage-ben.jsonl", toFold);
        }
    }

    public class ProjectLeaseTests
    {
        private const long Now = 1_000_000;

        [Fact]
        public void An_absent_lease_is_taken()
        {
            var result = ProjectLease.TryAcquire(null, "anna", Now);

            Assert.Equal(LeaseOutcome.Acquired, result.Outcome);
            Assert.True(result.MayWrite);
        }

        [Fact]
        public void A_live_lease_held_by_somebody_else_is_refused_with_a_useful_sentence()
        {
            var held = ProjectLease.Write("ben", Now + TimeSpan.FromMinutes(5).Ticks);

            var result = ProjectLease.TryAcquire(held, "anna", Now);

            Assert.Equal(LeaseOutcome.HeldByAnother, result.Outcome);
            Assert.False(result.MayWrite);
            Assert.Contains("ben", result.Reason!, StringComparison.Ordinal);
            // Losing the lease costs settings, and nothing else — the sentence has to say so.
            Assert.Contains("triage, report and export", result.Reason!, StringComparison.Ordinal);
        }

        [Fact]
        public void An_expired_lease_is_taken_over_rather_than_stranding_the_team()
        {
            // A lease that outlives a crashed process would lock a team out of their own project
            // until somebody deletes a file they have never heard of.
            var stale = ProjectLease.Write("ben", Now - 1);

            var result = ProjectLease.TryAcquire(stale, "anna", Now);

            Assert.Equal(LeaseOutcome.TakenOverFromStale, result.Outcome);
            Assert.True(result.MayWrite);
            Assert.Contains("ben", result.Reason!, StringComparison.Ordinal);
        }

        [Fact]
        public void Reacquiring_your_own_lease_is_allowed()
        {
            var mine = ProjectLease.Write("anna", Now + TimeSpan.FromMinutes(5).Ticks);

            Assert.Equal(LeaseOutcome.Acquired, ProjectLease.TryAcquire(mine, "anna", Now).Outcome);
        }

        [Fact]
        public void A_corrupt_lease_file_cannot_lock_a_team_out_permanently()
        {
            Assert.Equal(LeaseOutcome.Acquired, ProjectLease.TryAcquire("{not json", "anna", Now).Outcome);
            Assert.Equal(LeaseOutcome.Acquired, ProjectLease.TryAcquire("[]", "anna", Now).Outcome);
        }

        [Fact]
        public void On_a_sync_root_the_lease_is_refused_rather_than_faked()
        {
            // Both writers would acquire it, both would believe they held it, and the loser's
            // edits would vanish into a conflicted copy nobody opens.
            var result = ProjectLease.TryAcquire(null, "anna", Now, SubstrateKind.SyncRoot);

            Assert.Equal(LeaseOutcome.UnsupportedSubstrate, result.Outcome);
            Assert.False(result.MayWrite);
            Assert.Contains("synced folder", result.Reason!, StringComparison.Ordinal);
        }

        [Fact]
        public void Releasing_a_lease_you_hold_clears_it()
        {
            Assert.Null(ProjectLease.Release(ProjectLease.Write("anna", Now + 1), "anna"));
        }

        [Fact]
        public void Releasing_a_lease_you_no_longer_hold_does_not_evict_the_new_holder()
        {
            // After a takeover, the previous holder's release must not evict whoever took it.
            var bensLease = ProjectLease.Write("ben", Now + 1);

            Assert.Equal(bensLease, ProjectLease.Release(bensLease, "anna"));
        }

        [Fact]
        public void A_lease_round_trips_through_its_file()
        {
            var written = ProjectLease.Write("anna", 12345);
            var result = ProjectLease.TryAcquire(written, "ben", 0);

            Assert.Equal(LeaseOutcome.HeldByAnother, result.Outcome);
            Assert.Equal("anna", result.Holder);
            Assert.Equal(12345, result.ExpiresTicks);
        }
    }

    public class SectionDiffTests
    {
        [Fact]
        public void Only_the_sections_that_actually_differ_are_named()
        {
            // "Somebody else changed the file" is not something a user can act on. Naming the
            // sections tells them whether to wait, merge by hand, or carry on.
            var mine = JsonReader.Parse("{\"schemaVersion\":1,\"rules\":{\"a\":1},\"parties\":{\"n\":2}}");
            var theirs = JsonReader.Parse("{\"schemaVersion\":1,\"rules\":{\"a\":1},\"parties\":{\"n\":3}}");

            Assert.Equal(new[] { "parties" }, SectionDiff.ChangedSections(mine, theirs).ToArray());
        }

        [Fact]
        public void The_version_field_is_never_reported_as_a_conflict()
        {
            var mine = JsonReader.Parse("{\"schemaVersion\":1,\"rules\":{}}");
            var theirs = JsonReader.Parse("{\"schemaVersion\":2,\"rules\":{}}");

            Assert.Empty(SectionDiff.ChangedSections(mine, theirs));
        }

        [Fact]
        public void A_section_added_on_one_side_counts_as_changed()
        {
            var mine = JsonReader.Parse("{\"rules\":{}}");
            var theirs = JsonReader.Parse("{\"rules\":{},\"matrix\":{\"x\":1}}");

            Assert.Equal(new[] { "matrix" }, SectionDiff.ChangedSections(mine, theirs).ToArray());
        }

        [Fact]
        public void Identical_documents_report_no_phantom_conflict()
        {
            // This only holds because the writer preserves key order — otherwise every save would
            // report a conflict against itself.
            const string text = "{\"schemaVersion\":1,\"z\":{\"b\":1,\"a\":2},\"m\":[1,2,3]}";

            Assert.Empty(SectionDiff.ChangedSections(JsonReader.Parse(text), JsonReader.Parse(text)));
        }

        [Fact]
        public void The_description_names_who_and_what()
        {
            var text = SectionDiff.Describe(new[] { "matrix", "parties" }, "ben");

            Assert.Contains("ben", text, StringComparison.Ordinal);
            Assert.Contains("matrix, parties", text, StringComparison.Ordinal);
            Assert.Contains("unaffected", text, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_diff_says_so_rather_than_naming_nothing()
        {
            Assert.Contains("nothing you can see", SectionDiff.Describe(Array.Empty<string>(), "ben"),
                StringComparison.Ordinal);
        }
    }
}
