using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;

namespace CamelWorks.Nav.Clash
{
    /// <summary>
    /// Reads the host's clash tests and results.
    ///
    /// <b>This type lives in its own assembly on purpose, and nothing may move it.</b>
    /// <c>Autodesk.Navisworks.Clash.dll</c> ships with Navisworks Manage and not with Simulate. A
    /// type from a missing assembly costs nothing until the JIT reaches a method that mentions it,
    /// and then it is a FileNotFoundException in the middle of whatever the user was doing, naming
    /// a DLL they have never heard of. Behind an assembly boundary, a Simulate user simply never
    /// loads this — the clash tools are shown disabled, with a reason.
    ///
    /// <b>Main thread only.</b>
    /// </summary>
    public sealed class NavClashSource : IClashSource
    {
        private readonly NavDocument _document;

        /// <summary>Wrap a document.</summary>
        public NavClashSource(NavDocument document) =>
            _document = document ?? throw new ArgumentNullException(nameof(document));

        /// <summary>
        /// Whether this document can supply clash data at all.
        ///
        /// False on Simulate, and false on Manage for a document that has never had the clash tool
        /// opened. Both are ordinary states, not errors: the caller disables the clash tools and
        /// says which one it is.
        /// </summary>
        public bool IsAvailable
        {
            get
            {
                try
                {
                    return Clash() != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<ClashTestInfo> Tests()
        {
            var tests = new List<ClashTestInfo>();

            var clash = Clash();
            if (clash == null) return tests;

            foreach (var test in clash.TestsData.Tests.OfType<ClashTest>())
            {
                var results = new List<ClashResult>();
                Collect(test.Children, results);

                tests.Add(new ClashTestInfo(
                    test.Guid.ToString(),
                    test.DisplayName ?? "(unnamed test)",
                    null,
                    results.Count,
                    LastRunTicksOf(test)));
            }

            return tests;
        }

        /// <inheritdoc />
        public IReadOnlyList<ClashResultInfo> Results(string testId)
        {
            var results = new List<ClashResultInfo>();

            var clash = Clash();
            if (clash == null || string.IsNullOrWhiteSpace(testId)) return results;

            var test = clash.TestsData.Tests.OfType<ClashTest>()
                .FirstOrDefault(t => string.Equals(t.Guid.ToString(), testId, StringComparison.OrdinalIgnoreCase));

            if (test == null) return results;

            var found = new List<ClashResult>();
            Collect(test.Children, found);

            foreach (var result in found)
            {
                var a = KeyOf(result.CompositeItem1);
                var b = KeyOf(result.CompositeItem2);

                // Both participants have to resolve or there is no clash key, and a key made from
                // one side would collide with every other result on that element. Skipped, and the
                // count difference shows up in the funnel rather than being invented here.
                if (a.IsEmpty || b.IsEmpty) continue;

                var centre = result.Center;

                results.Add(new ClashResultInfo(
                    result.Guid.ToString(),
                    a, b,
                    centre.X, centre.Y, centre.Z,
                    StatusOf(result),
                    result.AssignedTo));
            }

            return results;
        }

        private DocumentClash? Clash()
        {
            // GetClash() is an extension on Document that only exists when the Manage-only
            // assembly is present, which is the whole reason this file is in its own project.
            var clash = _document.Document.GetClash();
            return clash;
        }

        private static void Collect(SavedItemCollection items, List<ClashResult> results)
        {
            if (items == null) return;

            foreach (SavedItem item in items)
            {
                if (item is ClashResult result)
                {
                    results.Add(result);
                }
                else if (item is ClashResultGroup group)
                {
                    // The host's own grouping. Flattened here on purpose: CamelWorks re-derives
                    // groups from its own rule stack, and carrying two competing groupings through
                    // the pipeline would mean neither could be trusted.
                    Collect(group.Children, results);
                }
            }
        }

        private static ElementKey KeyOf(ModelItem? item)
        {
            if (item == null) return default;

            try
            {
                return NavKeys.Of(item);
            }
            catch (Exception)
            {
                return default;
            }
        }

        // The host's status, as text, without asserting it means what CamelWorks means by it. The
        // engine recomputes New, Active and Resolved on every run, so only Reviewed and Approved
        // are read back as human judgement — and that decision is made above the seam, on the
        // string this returns.
        private static string? StatusOf(ClashResult result)
        {
            try
            {
                return result.Status.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long LastRunTicksOf(ClashTest test)
        {
            try
            {
                return test.LastRun.Ticks;
            }
            catch (Exception)
            {
                // A test that has never been run. Zero says so, and the board shows it as never
                // run rather than as run at the epoch.
                return 0;
            }
        }
    }
}
