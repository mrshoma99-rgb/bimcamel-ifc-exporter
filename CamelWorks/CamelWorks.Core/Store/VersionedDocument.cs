using System;
using System.Globalization;

namespace CamelWorks.Core.Store
{
    /// <summary>How a load ended.</summary>
    public enum LoadOutcome
    {
        /// <summary>Read, and this build understands its version.</summary>
        Ok = 0,

        /// <summary>Nothing there. An ordinary first-run state, never an error.</summary>
        Missing = 1,

        /// <summary>Present but not valid JSON — a half-written file, or a sync conflict.</summary>
        Malformed = 2,

        /// <summary>Written by a newer CamelWorks. Readable, but this build must not save over it.</summary>
        TooNew = 3,
    }

    /// <summary>
    /// The envelope every CamelWorks-authored file carries.
    ///
    /// Two rules, both of which exist because these files outlive the build that wrote them:
    ///
    /// 1. <b>Unknown keys survive a round trip.</b> A newer CamelWorks writes a key this build has
    ///    never heard of; loading and re-saving must not delete it. Without this, one colleague on
    ///    last month's build silently strips everyone else's newer settings every time they open
    ///    the project — a data-loss bug that looks like nothing at all until somebody notices their
    ///    rules are gone.
    /// 2. <b>A file from the future is read-only.</b> This build can display it, but saving would
    ///    write a lower <c>schemaVersion</c> over a higher one and claim a downgrade it did not
    ///    perform. So it refuses, and the pane says why.
    ///
    /// Versioning starts at the first commit rather than at the first breaking change: the
    /// compatibility burden that does not exist today begins on release day, and adding the field
    /// afterwards means guessing at what unversioned files meant.
    /// </summary>
    public sealed class VersionedDocument
    {
        /// <summary>The member every CamelWorks file leads with.</summary>
        public const string VersionKey = "schemaVersion";

        private VersionedDocument(JsonValue root, int schemaVersion, int buildSupports, LoadOutcome outcome)
        {
            Root = root;
            SchemaVersion = schemaVersion;
            BuildSupports = buildSupports;
            Outcome = outcome;
        }

        /// <summary>The whole document, unknown keys intact.</summary>
        public JsonValue Root { get; }

        /// <summary>The version the file declares.</summary>
        public int SchemaVersion { get; }

        /// <summary>The highest version this build understands.</summary>
        public int BuildSupports { get; }

        /// <summary>How the load ended.</summary>
        public LoadOutcome Outcome { get; }

        /// <summary>True when this build must not save over the file.</summary>
        public bool IsReadOnly => Outcome == LoadOutcome.TooNew;

        /// <summary>
        /// The sentence the pane shows when the file is read-only. Named here rather than in the UI
        /// so every surface says the same thing.
        /// </summary>
        public string? ReadOnlyReason => IsReadOnly
            ? "This file was written by a newer CamelWorks (format " +
              SchemaVersion.ToString(CultureInfo.InvariantCulture) + "; this build reads up to " +
              BuildSupports.ToString(CultureInfo.InvariantCulture) +
              "). It is shown read-only so your changes cannot overwrite settings this build does not understand."
            : null;

        /// <summary>A new, empty document at the given version.</summary>
        public static VersionedDocument Create(int schemaVersion)
        {
            if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
            var root = JsonValue.Object().Set(VersionKey, (long)schemaVersion);
            return new VersionedDocument(root, schemaVersion, schemaVersion, LoadOutcome.Ok);
        }

        /// <summary>
        /// Load a document. Never throws: a missing or damaged file is an outcome the caller
        /// reports, not an exception it has to catch around every read.
        /// </summary>
        /// <param name="text">File contents, or null when the file does not exist.</param>
        /// <param name="buildSupports">The highest schema version this build understands.</param>
        public static VersionedDocument Load(string? text, int buildSupports)
        {
            if (buildSupports < 1) throw new ArgumentOutOfRangeException(nameof(buildSupports));

            if (string.IsNullOrWhiteSpace(text))
                return new VersionedDocument(JsonValue.Object().Set(VersionKey, (long)buildSupports),
                    buildSupports, buildSupports, LoadOutcome.Missing);

            if (!JsonReader.TryParse(text, out var root) || root.Kind != JsonKind.Object)
                return new VersionedDocument(JsonValue.Object().Set(VersionKey, (long)buildSupports),
                    0, buildSupports, LoadOutcome.Malformed);

            // A file with no version is treated as version 1 rather than rejected: it can only have
            // come from a build that predates versioning, and refusing to read it would be a worse
            // outcome than reading it conservatively.
            var declared = root.Has(VersionKey) ? (int)root[VersionKey].AsLong(1) : 1;
            if (declared < 1) declared = 1;

            var outcome = declared > buildSupports ? LoadOutcome.TooNew : LoadOutcome.Ok;
            return new VersionedDocument(root, declared, buildSupports, outcome);
        }

        /// <summary>
        /// Serialise. Refuses when the file came from a newer build, because writing a lower
        /// version over a higher one silently discards what this build could not represent.
        /// </summary>
        public string Save()
        {
            if (IsReadOnly)
                throw new InvalidOperationException(ReadOnlyReason);

            // Stamp this build's version, but never lower an existing one.
            var version = SchemaVersion > BuildSupports ? SchemaVersion : BuildSupports;
            Root.Set(VersionKey, (long)version);
            return Root.ToJson(indented: true);
        }

        /// <summary>
        /// A named section, created on first use. Sections are how the store stays additive: a
        /// build that knows nothing about a section still round-trips it untouched.
        /// </summary>
        public JsonValue Section(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("section name is required", nameof(name));

            if (Root[name].Kind == JsonKind.Object) return Root[name];

            var section = JsonValue.Object();
            Root.Set(name, section);
            return section;
        }
    }
}
