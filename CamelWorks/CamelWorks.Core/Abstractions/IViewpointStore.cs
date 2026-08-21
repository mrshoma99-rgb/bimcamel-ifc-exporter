using System.Collections.Generic;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Abstractions
{
    /// <summary>
    /// Saved viewpoints, behind the seam.
    ///
    /// Behind this interface because saved viewpoints are a second appearance write channel: a
    /// viewpoint captures overrides, so restoring one changes what the Appearance Manager's stack
    /// says is true. Routing both through seams is what lets the arbitration between them be
    /// written once, in logic, and tested without a host.
    /// </summary>
    public interface IViewpointStore
    {
        /// <summary>Every saved viewpoint, in host order, flattened across folders.</summary>
        IReadOnlyList<SavedView> All();

        /// <summary>Save the current camera and appearance under a name, returning the new view.</summary>
        SavedView SaveCurrent(string name, string? folder = null);

        /// <summary>Apply a saved viewpoint. This restores its overrides too — see the type remarks.</summary>
        void Apply(string viewId);

        /// <summary>Rename a saved viewpoint.</summary>
        void Rename(string viewId, string newName);

        /// <summary>Delete a saved viewpoint.</summary>
        void Delete(string viewId);
    }

    /// <summary>One saved viewpoint.</summary>
    public readonly struct SavedView
    {
        /// <summary>Host identity, stable for the life of the document.</summary>
        public string Id { get; }

        /// <summary>Display name.</summary>
        public string Name { get; }

        /// <summary>Containing folder, or null at the root.</summary>
        public string? Folder { get; }

        /// <summary>True when the viewpoint carries appearance overrides of its own.</summary>
        public bool HasOverrides { get; }

        /// <summary>
        /// True when the host reports native redlines on this viewpoint. CamelWorks can read their
        /// text but cannot see their geometry, so a report carrying this view says so rather than
        /// silently omitting an instruction somebody drew.
        /// </summary>
        public bool HasRedlines { get; }

        /// <summary>Create a descriptor.</summary>
        public SavedView(string id, string name, string? folder, bool hasOverrides, bool hasRedlines)
        {
            Id = id; Name = name; Folder = folder; HasOverrides = hasOverrides; HasRedlines = hasRedlines;
        }

        /// <inheritdoc />
        public override string ToString() => Folder == null ? Name : Folder + "/" + Name;
    }

    /// <summary>
    /// Clash tests and their results.
    ///
    /// <b>Manage only.</b> The host's clash assembly ships with Manage and not with Simulate, and
    /// any type whose signature names a clash type fails to load where it is absent — including,
    /// left unguarded, the very probe whose job is to report that it is absent. So this interface
    /// lives behind a capability check and its implementation lives in its own assembly, loaded
    /// only after that check passes. On Simulate the board still opens: it hydrates from the
    /// sidecar store instead, and refuses exactly two things — running or creating a test, and
    /// projecting status back into the host.
    /// </summary>
    public interface IClashSource
    {
        /// <summary>False on a host without clash support. Every caller checks before anything else.</summary>
        bool IsAvailable { get; }

        /// <summary>The tests in the document.</summary>
        IReadOnlyList<ClashTestInfo> Tests();

        /// <summary>The results of one test.</summary>
        IReadOnlyList<ClashResultInfo> Results(string testId);
    }

    /// <summary>One clash test.</summary>
    public readonly struct ClashTestInfo
    {
        /// <summary>Host identity.</summary>
        public string Id { get; }

        /// <summary>Display name, typically naming zone, level, phase and the discipline pair.</summary>
        public string Name { get; }

        /// <summary>Containing folder, or null at the root.</summary>
        public string? Folder { get; }

        /// <summary>Number of results the test currently holds.</summary>
        public int ResultCount { get; }

        /// <summary>
        /// When the host last ran this test, as ticks. Compared against the newest sidecar
        /// snapshot to notice a run started outside CamelWorks — which is the most frequent action
        /// of the week and raises no plug-in event at all.
        /// </summary>
        public long LastRunTicks { get; }

        /// <summary>Create a descriptor.</summary>
        public ClashTestInfo(string id, string name, string? folder, int resultCount, long lastRunTicks)
        {
            Id = id; Name = name; Folder = folder; ResultCount = resultCount; LastRunTicks = lastRunTicks;
        }

        /// <inheritdoc />
        public override string ToString() => (Folder == null ? Name : Folder + "/" + Name) + " (" + ResultCount + ")";
    }

    /// <summary>One clash result, as the host reports it.</summary>
    public readonly struct ClashResultInfo
    {
        /// <summary>Host identity within its test.</summary>
        public string Id { get; }

        /// <summary>First participant.</summary>
        public ElementKey A { get; }

        /// <summary>Second participant.</summary>
        public ElementKey B { get; }

        /// <summary>Clash point, X.</summary>
        public double PointX { get; }

        /// <summary>Clash point, Y.</summary>
        public double PointY { get; }

        /// <summary>Clash point, Z.</summary>
        public double PointZ { get; }

        /// <summary>The host's own status string.</summary>
        public string? HostStatus { get; }

        /// <summary>The host's own assignee string.</summary>
        public string? HostAssignedTo { get; }

        /// <summary>Create a descriptor.</summary>
        public ClashResultInfo(string id, ElementKey a, ElementKey b, double x, double y, double z,
                               string? hostStatus, string? hostAssignedTo)
        {
            Id = id; A = a; B = b; PointX = x; PointY = y; PointZ = z;
            HostStatus = hostStatus; HostAssignedTo = hostAssignedTo;
        }

        /// <summary>The stable key for this result.</summary>
        public ClashKey Key(double cell = ClashKey.DefaultCell) =>
            ClashKey.Create(A, B, PointX, PointY, PointZ, cell);
    }
}
