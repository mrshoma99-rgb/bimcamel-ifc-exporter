using System;
using System.Collections.Generic;
using System.Globalization;
using CamelWorks.Core.Findings;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Clash
{
    /// <summary>
    /// One clash result as the rules see it.
    ///
    /// Flattened on purpose. A rule needs a level, a grid reference, a system name and a model
    /// name; making it walk back to the host for each would put every rule on the STA thread and
    /// make the pipeline untestable. The adapter resolves these once, and the rules are then pure
    /// functions over values — which is why the whole pipeline runs on a Linux CI job.
    /// </summary>
    public sealed class ClashItem
    {
        /// <summary>Create an item.</summary>
        public ClashItem(ClashKey key, string testName)
        {
            Key = key;
            TestName = testName ?? throw new ArgumentNullException(nameof(testName));
        }

        /// <summary>Stable identity.</summary>
        public ClashKey Key { get; }

        /// <summary>The clash test that produced it.</summary>
        public string TestName { get; }

        /// <summary>Test folder, when the matrix nests tests.</summary>
        public string? TestFolder { get; set; }

        /// <summary>
        /// Where this result stands, after carry-over has run.
        ///
        /// Defaults to <see cref="FindingStatus.New"/>, which is the truth for a result no previous
        /// run knew about. <see cref="ClashCarryOver"/> overwrites it for the ones a previous run
        /// did know about — that, and nothing else, is what stops every re-export presenting last
        /// month's reviewed clashes as brand new.
        /// </summary>
        public FindingStatus Status { get; set; } = FindingStatus.New;

        /// <summary>
        /// The hand-made group this result was in last run, or null.
        ///
        /// <b>Only hand-made groups carry.</b> A derived group must re-derive, because the stack is
        /// the source of truth for it — carrying a derived name would freeze the board against its
        /// own rules, so adding a Grid rule would change nothing. A hand-made group has no other
        /// memory, so it carries or it is lost.
        /// </summary>
        public string? CarriedGroup { get; set; }

        /// <summary>Level name, from the zoning pass. Null when the model has no usable levels.</summary>
        public string? Level { get; set; }

        /// <summary>Nearest grid intersection, e.g. "C4". Null when the model has no grids.</summary>
        public string? Grid { get; set; }

        /// <summary>Zone name, when zones were derived.</summary>
        public string? Zone { get; set; }

        /// <summary>Display name of the model owning item A.</summary>
        public string? ModelA { get; set; }

        /// <summary>Display name of the model owning item B.</summary>
        public string? ModelB { get; set; }

        /// <summary>Discipline of item A, when derivable.</summary>
        public string? DisciplineA { get; set; }

        /// <summary>Discipline of item B, when derivable.</summary>
        public string? DisciplineB { get; set; }

        /// <summary>System name of item A — the MEP system, where there is one.</summary>
        public string? SystemA { get; set; }

        /// <summary>System name of item B.</summary>
        public string? SystemB { get; set; }

        /// <summary>Category of item A.</summary>
        public string? CategoryA { get; set; }

        /// <summary>Category of item B.</summary>
        public string? CategoryB { get; set; }

        /// <summary>Clash point, model units.</summary>
        public double X { get; set; }

        /// <summary>Clash point, model units.</summary>
        public double Y { get; set; }

        /// <summary>Clash point, model units.</summary>
        public double Z { get; set; }

        /// <summary>Overlap volume in cubic model units, where the engine reported one.</summary>
        public double? OverlapVolume { get; set; }

        /// <summary>Signed distance the engine reported. Negative is penetration; positive is a clearance miss.</summary>
        public double? Distance { get; set; }

        /// <summary>
        /// Angle between the two elements' dominant axes, in degrees, 0 to 90. Null when either
        /// element has no dominant axis — a slab has none, and a rule about crossing angle should
        /// not silently treat that as zero.
        /// </summary>
        public double? CrossingAngleDegrees { get; set; }

        /// <summary>Named sets item A belongs to.</summary>
        public ISet<string> SetsA { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Named sets item B belongs to.</summary>
        public ISet<string> SetsB { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Arbitrary properties a rule can match on, keyed "Category/Name".</summary>
        public IDictionary<string, string?> Properties { get; } =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when both participants are in the same model — usually a modelling artefact.</summary>
        public bool IsSameModel =>
            ModelA != null && string.Equals(ModelA, ModelB, StringComparison.OrdinalIgnoreCase);

        /// <summary>The two model names in a stable order, for pair grouping.</summary>
        public string ModelPair
        {
            get
            {
                var a = ModelA ?? "?";
                var b = ModelB ?? "?";
                return string.CompareOrdinal(a, b) <= 0 ? a + " v " + b : b + " v " + a;
            }
        }

        /// <summary>The two disciplines in a stable order.</summary>
        public string DisciplinePair
        {
            get
            {
                var a = DisciplineA ?? "?";
                var b = DisciplineB ?? "?";
                return string.CompareOrdinal(a, b) <= 0 ? a + " v " + b : b + " v " + a;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            TestName + " " + (Level ?? "?") + "/" + (Grid ?? "?") + " @"
            + X.ToString("0.##", CultureInfo.InvariantCulture) + ","
            + Y.ToString("0.##", CultureInfo.InvariantCulture) + ","
            + Z.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
