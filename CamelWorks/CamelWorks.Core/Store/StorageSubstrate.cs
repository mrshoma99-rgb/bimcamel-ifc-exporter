using System;
using System.Collections.Generic;
using System.Linq;

namespace CamelWorks.Core.Store
{
    /// <summary>What kind of storage a sidecar has landed on.</summary>
    public enum SubstrateKind
    {
        /// <summary>An ordinary local disk. Everything works.</summary>
        LocalDisk = 0,

        /// <summary>A UNC or mapped network share. Leases work; latency is the only cost.</summary>
        NetworkShare = 1,

        /// <summary>
        /// A folder managed by a consumer sync client — OneDrive, SharePoint, Dropbox, Google Drive,
        /// Box, Nextcloud. Leases do NOT work here, and files reappear as "conflicted copies".
        /// </summary>
        SyncRoot = 2,

        /// <summary>Could not be determined. Treated as a network share — the conservative middle.</summary>
        Unknown = 3,
    }

    /// <summary>
    /// Detects what a sidecar folder is sitting on, and what that means for concurrency.
    ///
    /// This exists because of a failure that looks like a CamelWorks bug and is not one. A sync
    /// client does not merge; when two people write the same file it keeps both, renaming one to
    /// something like <c>project (Anna's conflicted copy 2026-08-21).cwproj</c>. A lease held in a
    /// file on such a folder is therefore not a lease at all — both writers acquire it, both
    /// believe they hold it, and the loser's edits vanish into a file nobody opens.
    ///
    /// So CamelWorks refuses the lease on a sync root and says why, rather than appearing to work.
    /// The journals are unaffected and keep working: they are per-writer and append-only, which is
    /// exactly the shape a sync client cannot damage — the reason the store was designed that way.
    /// </summary>
    public static class StorageSubstrate
    {
        // Matched against path segments, case-insensitively. Deliberately conservative: a false
        // positive costs a shared lease the user could have had; a false negative costs silent
        // data loss.
        private static readonly string[] SyncMarkers =
        {
            "onedrive", "sharepoint", "dropbox", "google drive", "googledrive",
            "box sync", "boxdrive", "nextcloud", "owncloud", "icloud drive", "icloud",
            "creative cloud files", "sync.com", "pcloud", "mega",
        };

        /// <summary>
        /// Classify a path. Pure string inspection — no file system access — so it is testable and
        /// cannot itself block on an unreachable share.
        /// </summary>
        public static SubstrateKind Classify(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return SubstrateKind.Unknown;

            var path = fullPath!.Replace('\\', '/');
            var lower = path.ToLowerInvariant();

            // A sync marker anywhere in the path wins, including on a share: a synced folder
            // reached over UNC is still synced.
            var segments = lower.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(seg => SyncMarkers.Any(m => seg.Contains(m))))
                return SubstrateKind.SyncRoot;

            if (path.StartsWith("//", StringComparison.Ordinal)) return SubstrateKind.NetworkShare;

            // A drive letter with a colon is local as far as we can tell from the path alone. A
            // mapped network drive looks identical, which is why Unknown maps to the same
            // conservative behaviour as a share.
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])) return SubstrateKind.LocalDisk;
            if (path.StartsWith("/", StringComparison.Ordinal)) return SubstrateKind.LocalDisk;

            return SubstrateKind.Unknown;
        }

        /// <summary>True when a file lease can be trusted on this substrate.</summary>
        public static bool SupportsLease(SubstrateKind kind) => kind != SubstrateKind.SyncRoot;

        /// <summary>
        /// The sentence shown in the pane header when leases are unavailable. Stated in Core so
        /// every surface says the same thing, and phrased so the user knows what still works.
        /// </summary>
        public static string? LeaseRefusalReason(SubstrateKind kind) => kind == SubstrateKind.SyncRoot
            ? "This project is in a synced folder, so CamelWorks cannot lock the settings file — "
              + "a sync client keeps both versions as conflicted copies instead of merging them. "
              + "Your board edits are safe (they are written per-person and merged), but two people "
              + "editing project settings at the same time may lose one set of changes."
            : null;

        /// <summary>
        /// Whether snapshots may be retained here. Retention writes and deletes repeatedly, which
        /// on a sync root produces a stream of upload churn and, worse, resurrected deletions.
        /// </summary>
        public static bool SupportsRetention(SubstrateKind kind) => kind != SubstrateKind.SyncRoot;

        /// <summary>
        /// Recognise a sync client's conflicted copy of one of our files.
        ///
        /// These are folded rather than ignored: a conflicted copy of a journal is somebody's real
        /// edits, and the fold is designed to absorb them. Silently leaving them on disk is how a
        /// day of triage disappears.
        /// </summary>
        public static bool IsConflictedCopy(string? fileName, string expectedStem)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(expectedStem)) return false;

            var name = fileName!.ToLowerInvariant();
            if (!name.Contains(expectedStem.ToLowerInvariant())) return false;

            // OneDrive/Dropbox: "name (Anna's conflicted copy 2026-08-21).ext"
            // SharePoint:       "name-ANNA.ext"  |  Google Drive: "name (1).ext"
            return name.Contains("conflicted copy")
                || name.Contains("conflict")
                || name.Contains("(case conflict")
                || name.Contains("'s copy");
        }

        /// <summary>
        /// Every file that should be folded for a given journal stem — the file itself plus any
        /// conflicted copies of it.
        /// </summary>
        public static IReadOnlyList<string> FilesToFold(IEnumerable<string> fileNames, string expectedStem)
        {
            if (fileNames == null) throw new ArgumentNullException(nameof(fileNames));

            return fileNames
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Where(f => f.IndexOf(expectedStem, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
