using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Data
{
    /// <summary>One element, as the health checks see it.</summary>
    public sealed class HealthElement
    {
        /// <summary>Create an element.</summary>
        /// <param name="id">How it is named in the report.</param>
        /// <param name="source">Which model it came from.</param>
        public HealthElement(string id, string source)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>How it is named in the report.</summary>
        public string Id { get; }

        /// <summary>Which model it came from.</summary>
        public string Source { get; }

        /// <summary>Its display name.</summary>
        public string? Name { get; set; }

        /// <summary>Its category.</summary>
        public string? Category { get; set; }

        /// <summary>Bounding box centre.</summary>
        public double X { get; set; }

        /// <summary>Bounding box centre.</summary>
        public double Y { get; set; }

        /// <summary>Bounding box centre.</summary>
        public double Z { get; set; }

        /// <summary>Bounding box size.</summary>
        public double SizeX { get; set; }

        /// <summary>Bounding box size.</summary>
        public double SizeY { get; set; }

        /// <summary>Bounding box size.</summary>
        public double SizeZ { get; set; }

        /// <summary>How many properties it carries.</summary>
        public int PropertyCount { get; set; }

        /// <summary>The largest of its three dimensions.</summary>
        public double LargestDimension => Math.Max(SizeX, Math.Max(SizeY, SizeZ));

        /// <inheritdoc />
        public override string ToString() => Id;
    }

    /// <summary>One thing wrong with the model, and what to do about it.</summary>
    public sealed class HealthFinding
    {
        internal HealthFinding(string rule, string summary, string fix, int count,
                               IReadOnlyList<string> examples)
        {
            Rule = rule; Summary = summary; Fix = fix; Count = count; Examples = examples;
        }

        /// <summary>Which check found it.</summary>
        public string Rule { get; }

        /// <summary>What is wrong, in one line.</summary>
        public string Summary { get; }

        /// <summary>
        /// What to do about it.
        ///
        /// Carried on every finding rather than left to the reader. A health check that reports
        /// "37 elements at the origin" and stops has told somebody there is a problem and nothing
        /// about whose problem it is; the fix is usually in the authoring tool, by somebody who is
        /// not the person reading this.
        /// </summary>
        public string Fix { get; }

        /// <summary>How many elements are affected.</summary>
        public int Count { get; }

        /// <summary>A few of them, so the finding can be checked rather than believed.</summary>
        public IReadOnlyList<string> Examples { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Count.ToString("N0", CultureInfo.InvariantCulture) + " · " + Summary;
    }

    /// <summary>What the health check found.</summary>
    public sealed class HealthReport
    {
        internal HealthReport(int elementsChecked, IReadOnlyList<HealthFinding> findings)
        {
            ElementsChecked = elementsChecked; Findings = findings;
        }

        /// <summary>How many elements were looked at.</summary>
        public int ElementsChecked { get; }

        /// <summary>What was found, worst first.</summary>
        public IReadOnlyList<HealthFinding> Findings { get; }

        /// <summary>True when nothing was found.</summary>
        public bool IsClean => Findings.Count == 0;

        /// <summary>The one-line readout.</summary>
        public override string ToString() =>
            IsClean
                ? ElementsChecked.ToString("N0", CultureInfo.InvariantCulture) + " elements, nothing found"
                : Findings.Count.ToString(CultureInfo.InvariantCulture) + " issues across "
                  + ElementsChecked.ToString("N0", CultureInfo.InvariantCulture) + " elements";
    }

    /// <summary>
    /// The checks worth running on a federation before anybody trusts a clash report from it.
    ///
    /// Every one of these is a real way a federation goes wrong quietly. An element stack at the
    /// origin is a failed insertion; a model sitting a kilometre from everything else is a survey
    /// point somebody forgot; two identical elements in the same place is a model loaded twice, and
    /// it doubles every clash count in the report. None of them announce themselves — the model
    /// opens, it looks like a building, and the numbers are wrong.
    ///
    /// The thresholds are relative to the model, never absolute. A site plan and a plant room are
    /// both normal, and a fixed "anything beyond 500m is suspicious" is wrong on one of them.
    /// </summary>
    public static class ModelHealth
    {
        /// <summary>How many examples each finding carries.</summary>
        public const int ExampleCount = 5;

        /// <summary>How close to the origin counts as "at the origin", in model units.</summary>
        public const double OriginRadius = 0.001;

        /// <summary>How small a dimension has to be to count as degenerate, in model units.</summary>
        public const double DegenerateSize = 1e-6;

        /// <summary>How many times the model's own spread an element must be beyond to be an outlier.</summary>
        public const double OutlierFactor = 10;

        /// <summary>Run every check.</summary>
        public static HealthReport Check(IEnumerable<HealthElement> elements)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));

            var all = elements.Where(e => e != null).ToList();
            var findings = new List<HealthFinding>();

            AtTheOrigin(all, findings);
            FarFromEverythingElse(all, findings);
            Degenerate(all, findings);
            Duplicated(all, findings);
            Unnamed(all, findings);
            WithoutProperties(all, findings);

            return new HealthReport(all.Count, findings.OrderByDescending(f => f.Count)
                                                       .ThenBy(f => f.Rule, StringComparer.Ordinal)
                                                       .ToList());
        }

        private static void AtTheOrigin(List<HealthElement> all, List<HealthFinding> findings)
        {
            // Only interesting when the model is NOT centred on the origin. A small model authored
            // around zero is perfectly normal, and flagging it every time is how a health check
            // gets ignored.
            var atOrigin = all.Where(e => Math.Abs(e.X) < OriginRadius
                                          && Math.Abs(e.Y) < OriginRadius
                                          && Math.Abs(e.Z) < OriginRadius).ToList();

            if (atOrigin.Count == 0) return;

            if (atOrigin.Count == all.Count) return;

            // Not List.Contains: that is a linear scan inside a loop over every element, and a
            // federation is hundreds of thousands of elements. A health check that takes minutes
            // is a health check nobody runs.
            var atOriginSet = new HashSet<HealthElement>(atOrigin);
            var rest = all.Where(e => !atOriginSet.Contains(e)).Select(e => Distance(e.X, e.Y, e.Z)).ToList();

            // Whether the origin is outside the model is a question about scale, not distance. A
            // plant room authored around zero has elements a few centimetres out and is entirely
            // normal; a site model has them two hundred metres out and an element at zero is
            // clearly lost. So the origin counts as far away only when it is far compared with how
            // spread out the model is — never against a fixed number of metres, which would be
            // wrong on one of those two.
            var centre = Median(rest);
            var spread = Median(rest.Select(d => Math.Abs(d - centre)));
            if (centre <= spread * OutlierFactor) return;

            Add(findings, "origin-stack",
                atOrigin.Count.ToString("N0", CultureInfo.InvariantCulture)
                + " elements sit exactly on the project origin while the rest of the model does not",
                "This is usually a failed insertion or a family with no location. Check the source "
                + "file's shared coordinates before running clashes: everything here will clash with "
                + "everything else here.",
                atOrigin);
        }

        private static void FarFromEverythingElse(List<HealthElement> all, List<HealthFinding> findings)
        {
            if (all.Count < 8) return;   // too few to say what "normal" is

            // Median absolute deviation rather than a mean and a standard deviation. A single
            // element a kilometre away drags a mean far enough to hide itself, which is exactly
            // the case this check exists for.
            var distances = all.Select(e => Distance(e.X, e.Y, e.Z)).ToList();
            var median = Median(distances);
            var deviation = Median(distances.Select(d => Math.Abs(d - median)));

            if (deviation < OriginRadius) return;

            var limit = median + (OutlierFactor * deviation);
            var outliers = all.Where(e => Distance(e.X, e.Y, e.Z) > limit).ToList();

            if (outliers.Count == 0 || outliers.Count > all.Count / 2) return;

            Add(findings, "stray-geometry",
                outliers.Count.ToString("N0", CultureInfo.InvariantCulture)
                + " elements sit far outside the rest of the model",
                "Usually a survey point or base point that was not applied when the model was "
                + "exported. Fix it at source rather than by moving the model here, or the next "
                + "revision will arrive in the wrong place again.",
                outliers);
        }

        private static void Degenerate(List<HealthElement> all, List<HealthFinding> findings)
        {
            var flat = all.Where(e => e.LargestDimension < DegenerateSize).ToList();
            if (flat.Count == 0) return;

            Add(findings, "degenerate-geometry",
                flat.Count.ToString("N0", CultureInfo.InvariantCulture)
                + " elements have no measurable size",
                "Clash detection cannot report on these and takeoff will count them as nothing. "
                + "They are usually annotation or reference geometry that should not have been "
                + "exported at all.",
                flat);
        }

        private static void Duplicated(List<HealthElement> all, List<HealthFinding> findings)
        {
            // Same name, same category, same place. Position is quantised so that two copies whose
            // coordinates differ in the last bit still count as the same place.
            // Hashed rather than joined with a delimiter: a name containing the delimiter would
            // group two unrelated elements together, and Hash already escapes and separates its
            // parts for exactly this reason.
            var duplicates = all
                .GroupBy(e => Hash.Of(Hash.ComponentWidth, "duplicate",
                    e.Name,
                    e.Category,
                    Hash.Quantise(e.X, 0.001).ToString(CultureInfo.InvariantCulture),
                    Hash.Quantise(e.Y, 0.001).ToString(CultureInfo.InvariantCulture),
                    Hash.Quantise(e.Z, 0.001).ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicates.Count == 0) return;

            var affected = duplicates.Sum(g => g.Count());
            var acrossModels = duplicates.Count(g => g.Select(e => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

            Add(findings, "duplicate-elements",
                affected.ToString("N0", CultureInfo.InvariantCulture)
                + " elements are exact duplicates of another element in the same place"
                + (acrossModels > 0
                    ? ", and " + acrossModels.ToString("N0", CultureInfo.InvariantCulture)
                      + " of those groups span two models"
                    : string.Empty),
                acrossModels > 0
                    ? "The same model has probably been appended twice, or two disciplines have both "
                      + "exported the same linked model. Every clash on these elements is counted twice."
                    : "Usually a copy pasted in place. Every clash on these elements is counted twice.",
                duplicates.SelectMany(g => g).ToList());
        }

        private static void Unnamed(List<HealthElement> all, List<HealthFinding> findings)
        {
            var unnamed = all.Where(e => string.IsNullOrWhiteSpace(e.Name)).ToList();
            if (unnamed.Count == 0) return;

            Add(findings, "unnamed-elements",
                unnamed.Count.ToString("N0", CultureInfo.InvariantCulture) + " elements have no name",
                "Anything selected from a clash report shows as blank, and search sets cannot pick "
                + "them up by name. Fix in the authoring tool; renaming here would not survive the "
                + "next export.",
                unnamed);
        }

        private static void WithoutProperties(List<HealthElement> all, List<HealthFinding> findings)
        {
            var bare = all.Where(e => e.PropertyCount == 0).ToList();
            if (bare.Count == 0) return;

            Add(findings, "no-properties",
                bare.Count.ToString("N0", CultureInfo.InvariantCulture)
                + " elements carry no properties at all",
                "Nothing can be filtered, grouped or scheduled on these. Usually the export omitted "
                + "the property sets, or the geometry came from a format that has none.",
                bare);
        }

        private static void Add(List<HealthFinding> findings, string rule, string summary, string fix,
                                IReadOnlyList<HealthElement> affected)
        {
            var examples = affected
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .Take(ExampleCount)
                .Select(e => e.Id + " (" + e.Source + ")")
                .ToList();

            findings.Add(new HealthFinding(rule, summary, fix, affected.Count, examples));
        }

        private static double Distance(double x, double y, double z) => Math.Sqrt((x * x) + (y * y) + (z * z));

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;

            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }
    }
}
