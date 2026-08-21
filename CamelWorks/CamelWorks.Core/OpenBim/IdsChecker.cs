using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CamelWorks.Core.OpenBim
{
    /// <summary>Why one element failed one requirement.</summary>
    public enum IdsFailure
    {
        /// <summary>The data the requirement asks about is not on the element at all.</summary>
        Missing = 0,

        /// <summary>The data is there and its value is not acceptable.</summary>
        WrongValue = 1,

        /// <summary>The requirement forbids this and the element has it.</summary>
        Prohibited = 2,
    }

    /// <summary>One element failing one requirement.</summary>
    public sealed class IdsViolation
    {
        internal IdsViolation(string elementId, IdsFacet requirement, IdsFailure failure)
        {
            ElementId = elementId; Requirement = requirement; Failure = failure;
        }

        /// <summary>Which element.</summary>
        public string ElementId { get; }

        /// <summary>Which requirement.</summary>
        public IdsFacet Requirement { get; }

        /// <summary>How it failed.</summary>
        public IdsFailure Failure { get; }

        /// <summary>The row as it reads in a report.</summary>
        public override string ToString()
        {
            var reason = Failure switch
            {
                IdsFailure.Missing => "is missing",
                IdsFailure.WrongValue => "has an unacceptable value for",
                IdsFailure.Prohibited => "must not have",
                _ => "fails",
            };

            return ElementId + " " + reason + " " + Requirement.Describe();
        }
    }

    /// <summary>How one specification fared.</summary>
    public sealed class IdsSpecificationResult
    {
        internal IdsSpecificationResult(IdsSpecification specification, int applicable, int passed,
                                        IReadOnlyList<IdsViolation> violations, bool truncated)
        {
            Specification = specification; Applicable = applicable; Passed = passed;
            Violations = violations; ViolationsTruncated = truncated;
        }

        /// <summary>
        /// True when the number of matching elements falls outside what the specification declared
        /// on its applicability.
        ///
        /// This is IDS's own answer to the vacuous pass. "At least one wall must exist, and every
        /// wall must carry a fire rating" is one specification with <c>minOccurs="1"</c>, and a
        /// model with no walls in it fails that — it does not pass it for lack of anything to
        /// check. The schema puts the occurs attributes on applicability and nowhere else, which
        /// is a strong hint this is exactly what they are for.
        /// </summary>
        public bool CountOutOfRange =>
            Applicable < Specification.MinOccurs
            || (Specification.MaxOccurs != null && Applicable > Specification.MaxOccurs.Value);

        /// <summary>The specification.</summary>
        public IdsSpecification Specification { get; }

        /// <summary>How many elements it applied to.</summary>
        public int Applicable { get; }

        /// <summary>How many of those met every requirement.</summary>
        public int Passed { get; }

        /// <summary>How many did not.</summary>
        public int Failed => Applicable - Passed;

        /// <summary>The failures, capped.</summary>
        public IReadOnlyList<IdsViolation> Violations { get; }

        /// <summary>True when there were more failures than the cap allowed.</summary>
        public bool ViolationsTruncated { get; }

        /// <summary>
        /// True when nothing in the model matched the applicability at all.
        ///
        /// <b>Reported on its own, never as a pass.</b> "Every wall must carry a fire rating" over
        /// a model with no walls in it is vacuously true, and a checker that shows it green has
        /// told the reader the opposite of what happened: nothing was checked. This is the single
        /// most useful thing an IDS report can say, because it is how an audit gets passed by a
        /// model that was never loaded properly.
        ///
        /// When the author wrote <c>minOccurs="1"</c>, it is not merely reported — see
        /// <see cref="CountOutOfRange"/>, which makes it a failure.
        /// </summary>
        public bool NothingApplied => Applicable == 0;

        /// <summary>True when the specification applied to something, in the expected numbers, and everything met it.</summary>
        public bool IsSatisfied => Applicable > 0 && Passed == Applicable && !CountOutOfRange;

        /// <summary>The row as it reads in a report.</summary>
        public override string ToString()
        {
            if (CountOutOfRange)
                return Specification.Name + " — " + Applicable.ToString("N0", CultureInfo.InvariantCulture)
                     + " matching elements, and this specification requires "
                     + (Applicable < Specification.MinOccurs
                        ? "at least " + Specification.MinOccurs.ToString(CultureInfo.InvariantCulture)
                        : "at most " + (Specification.MaxOccurs ?? 0).ToString(CultureInfo.InvariantCulture));

            if (NothingApplied)
                return Specification.Name + " — nothing in the model matched this, so nothing was checked";

            var s = Specification.Name + " — "
                  + Passed.ToString("N0", CultureInfo.InvariantCulture) + " of "
                  + Applicable.ToString("N0", CultureInfo.InvariantCulture);

            return Failed == 0 ? s + " pass" : s + " pass, " + Failed.ToString("N0", CultureInfo.InvariantCulture) + " fail";
        }
    }

    /// <summary>The whole check.</summary>
    public sealed class IdsReport
    {
        internal IdsReport(IdsDocument document, int elementsChecked, IReadOnlyList<IdsSpecificationResult> results)
        {
            Document = document; ElementsChecked = elementsChecked; Results = results;
        }

        /// <summary>The IDS that was checked against.</summary>
        public IdsDocument Document { get; }

        /// <summary>How many elements were offered to the check.</summary>
        public int ElementsChecked { get; }

        /// <summary>One result per specification.</summary>
        public IReadOnlyList<IdsSpecificationResult> Results { get; }

        /// <summary>Specifications that applied to something and were met in full.</summary>
        public int Satisfied => Results.Count(r => r.IsSatisfied);

        /// <summary>Specifications that were not met, including any whose match count was wrong.</summary>
        public int Unsatisfied => Results.Count(r => !r.IsSatisfied && (!r.NothingApplied || r.CountOutOfRange));

        /// <summary>Specifications nothing in the model matched, and which did not demand that it did.</summary>
        public int NotApplicable => Results.Count(r => r.NothingApplied && !r.CountOutOfRange);

        /// <summary>
        /// True only when every specification applied to something and was met.
        ///
        /// A specification that matched nothing does not count towards a pass, for the reason
        /// given on <see cref="IdsSpecificationResult.NothingApplied"/>.
        /// </summary>
        public bool IsPass => Results.Count > 0 && Results.All(r => r.IsSatisfied);

        /// <summary>The one-line readout.</summary>
        public override string ToString()
        {
            var s = Satisfied.ToString(CultureInfo.InvariantCulture) + " of "
                  + Results.Count.ToString(CultureInfo.InvariantCulture) + " specifications met";

            if (Unsatisfied > 0) s += " · " + Unsatisfied.ToString(CultureInfo.InvariantCulture) + " failed";
            if (NotApplicable > 0)
                s += " · " + NotApplicable.ToString(CultureInfo.InvariantCulture) + " matched nothing in the model";

            return s;
        }
    }

    /// <summary>
    /// Checks a model against an IDS.
    ///
    /// Two rules do most of the work, and both are about not flattering the model:
    ///
    /// A specification that matches nothing is reported as matching nothing, never as a pass. A
    /// federation where one discipline failed to load will otherwise sail through an audit, every
    /// row green, because a requirement about ductwork is vacuously true when there is no ductwork.
    /// Where the author wrote <c>minOccurs</c> on the applicability, that is not merely reported —
    /// it is a failure, which is what the schema put those attributes there for.
    ///
    /// A missing property and a wrong one are different failures. They are fixed by different
    /// people — one is a modelling omission, the other a data error — and a report that calls them
    /// both "fail" sends everybody to the wrong place.
    /// </summary>
    public static class IdsChecker
    {
        /// <summary>How many failures are kept per specification before the rest are counted only.</summary>
        public const int DefaultViolationCap = 500;

        /// <summary>Run the check.</summary>
        /// <param name="document">The IDS.</param>
        /// <param name="elements">The model, flattened.</param>
        /// <param name="violationCap">How many failures to keep per specification.</param>
        public static IdsReport Check(IdsDocument document, IEnumerable<IdsElement> elements,
                                      int violationCap = DefaultViolationCap)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            if (violationCap < 1) throw new ArgumentOutOfRangeException(nameof(violationCap), "the cap must be at least one");

            var all = elements.Where(e => e != null).ToList();
            var results = new List<IdsSpecificationResult>();

            foreach (var specification in document.Specifications.Where(s => s != null))
            {
                var applicable = all.Where(e => Applies(specification, e)).ToList();
                var violations = new List<IdsViolation>();
                var truncated = false;
                var passed = 0;

                foreach (var element in applicable)
                {
                    var failures = Check(specification, element).ToList();

                    if (failures.Count == 0) { passed++; continue; }

                    foreach (var failure in failures)
                    {
                        if (violations.Count >= violationCap) { truncated = true; break; }
                        violations.Add(failure);
                    }
                }

                results.Add(new IdsSpecificationResult(specification, applicable.Count, passed, violations, truncated));
            }

            return new IdsReport(document, all.Count, results);
        }

        // Applicability facets are filters, and every one must hold. Cardinality does not apply to
        // them: "walls that are prohibited from being walls" is not a thing the schema can express.
        private static bool Applies(IdsSpecification specification, IdsElement element) =>
            specification.Applicability.Count > 0
            && specification.Applicability.All(f => f.Matches(element));

        private static IEnumerable<IdsViolation> Check(IdsSpecification specification, IdsElement element)
        {
            foreach (var requirement in specification.Requirements.Where(r => r != null))
            {
                var matches = requirement.Matches(element);

                switch (requirement.Cardinality)
                {
                    case IdsCardinality.Required when !matches:
                        // Missing and wrong are separated here, and this is the only place that
                        // can tell them apart: by the time a caller sees a boolean it is gone.
                        yield return new IdsViolation(
                            element.Id, requirement,
                            requirement.DataIsPresent(element) ? IdsFailure.WrongValue : IdsFailure.Missing);
                        break;

                    case IdsCardinality.Prohibited when matches:
                        yield return new IdsViolation(element.Id, requirement, IdsFailure.Prohibited);
                        break;

                    case IdsCardinality.Optional when !matches && requirement.DataIsPresent(element):
                        // Optional means absence is fine and a wrong value is not. An element with
                        // no fire rating passes; one whose fire rating is "yes" does not.
                        yield return new IdsViolation(element.Id, requirement, IdsFailure.WrongValue);
                        break;
                }
            }
        }
    }
}
