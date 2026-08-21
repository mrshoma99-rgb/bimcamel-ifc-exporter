using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CamelWorks.Core.OpenBim
{
    /// <summary>Whether a requirement must hold, must not, or only applies when present.</summary>
    public enum IdsCardinality
    {
        /// <summary>The facet must match.</summary>
        Required = 0,

        /// <summary>The facet must not match.</summary>
        Prohibited = 1,

        /// <summary>
        /// The facet must match if the data is there at all, and absence is acceptable.
        ///
        /// Only requirements carry this; an applicability facet is always a filter.
        /// </summary>
        Optional = 2,
    }

    /// <summary>
    /// A value in an IDS file: either a literal, or a restriction on what is acceptable.
    ///
    /// The restriction form is XSD's, which is where the care is needed. Its <c>pattern</c> is an
    /// XML Schema regular expression, implicitly anchored at both ends, and it arrives from a file
    /// somebody else wrote — so it is anchored explicitly here and run under a timeout. An IDS with
    /// a catastrophic pattern in it would otherwise hang the host, and the person who sent it would
    /// have no idea they had done it.
    /// </summary>
    public sealed class IdsValue
    {
        /// <summary>How long a single pattern match may take before it is abandoned.</summary>
        public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

        private readonly Regex? _pattern;

        private IdsValue(string? literal, Regex? pattern, IReadOnlyList<string>? enumeration,
                         double? minInclusive, double? maxInclusive,
                         double? minExclusive, double? maxExclusive,
                         int? length, int? minLength, int? maxLength, string description)
        {
            Literal = literal;
            _pattern = pattern;
            Enumeration = enumeration;
            MinInclusive = minInclusive; MaxInclusive = maxInclusive;
            MinExclusive = minExclusive; MaxExclusive = maxExclusive;
            Length = length; MinLength = minLength; MaxLength = maxLength;
            Description = description;
        }

        /// <summary>The literal, when the value is a simple one.</summary>
        public string? Literal { get; }

        /// <summary>The permitted values, when the restriction enumerates them.</summary>
        public IReadOnlyList<string>? Enumeration { get; }

        /// <summary>Lower bound, inclusive.</summary>
        public double? MinInclusive { get; }

        /// <summary>Upper bound, inclusive.</summary>
        public double? MaxInclusive { get; }

        /// <summary>Lower bound, exclusive.</summary>
        public double? MinExclusive { get; }

        /// <summary>Upper bound, exclusive.</summary>
        public double? MaxExclusive { get; }

        /// <summary>Exact length.</summary>
        public int? Length { get; }

        /// <summary>Minimum length.</summary>
        public int? MinLength { get; }

        /// <summary>Maximum length.</summary>
        public int? MaxLength { get; }

        /// <summary>How the value reads in a report.</summary>
        public string Description { get; }

        /// <summary>A literal value.</summary>
        public static IdsValue Simple(string literal) =>
            new IdsValue(literal ?? throw new ArgumentNullException(nameof(literal)),
                         null, null, null, null, null, null, null, null, null, "'" + literal + "'");

        /// <summary>A restriction.</summary>
        /// <param name="pattern">An XML Schema regular expression, or null.</param>
        /// <param name="enumeration">Permitted values, or null.</param>
        /// <param name="minInclusive">Lower bound, inclusive.</param>
        /// <param name="maxInclusive">Upper bound, inclusive.</param>
        /// <param name="minExclusive">Lower bound, exclusive.</param>
        /// <param name="maxExclusive">Upper bound, exclusive.</param>
        /// <param name="length">Exact length.</param>
        /// <param name="minLength">Minimum length.</param>
        /// <param name="maxLength">Maximum length.</param>
        public static IdsValue Restriction(string? pattern = null, IReadOnlyList<string>? enumeration = null,
                                           double? minInclusive = null, double? maxInclusive = null,
                                           double? minExclusive = null, double? maxExclusive = null,
                                           int? length = null, int? minLength = null, int? maxLength = null)
        {
            Regex? compiled = null;
            var parts = new List<string>();

            if (pattern != null)
            {
                // XSD patterns are anchored at both ends implicitly; .NET's are not. Without the
                // anchors, "A.*" would match "BAD-A1" and quietly widen every rule in the file.
                compiled = new Regex(@"\A(?:" + pattern + @")\z", RegexOptions.CultureInvariant, PatternTimeout);
                parts.Add("matching /" + pattern + "/");
            }

            if (enumeration != null && enumeration.Count > 0)
                parts.Add("one of " + string.Join(", ", enumeration.Select(v => "'" + v + "'")));

            if (minInclusive != null) parts.Add(">= " + Format(minInclusive.Value));
            if (maxInclusive != null) parts.Add("<= " + Format(maxInclusive.Value));
            if (minExclusive != null) parts.Add("> " + Format(minExclusive.Value));
            if (maxExclusive != null) parts.Add("< " + Format(maxExclusive.Value));
            if (length != null) parts.Add(length.Value.ToString(CultureInfo.InvariantCulture) + " characters");
            if (minLength != null) parts.Add("at least " + minLength.Value.ToString(CultureInfo.InvariantCulture) + " characters");
            if (maxLength != null) parts.Add("at most " + maxLength.Value.ToString(CultureInfo.InvariantCulture) + " characters");

            return new IdsValue(null, compiled, enumeration, minInclusive, maxInclusive,
                                minExclusive, maxExclusive, length, minLength, maxLength,
                                parts.Count == 0 ? "any value" : string.Join(" and ", parts));
        }

        /// <summary>
        /// Whether a value satisfies this.
        ///
        /// Null never matches, whatever the restriction says. An absent property has no value to
        /// test, and treating "there is nothing here" as satisfying "at most 60" would pass every
        /// element nobody had filled in — which is the exact failure an IDS exists to catch.
        /// </summary>
        public bool Matches(string? value)
        {
            if (value == null) return false;
            if (Literal != null) return string.Equals(Literal, value, StringComparison.OrdinalIgnoreCase);

            if (_pattern != null)
            {
                try
                {
                    if (!_pattern.IsMatch(value)) return false;
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pattern from somebody else's file that cannot be evaluated in a quarter of
                    // a second does not get to decide anything, and does not get to hang the host.
                    return false;
                }
            }

            if (Enumeration != null && Enumeration.Count > 0
                && !Enumeration.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (Length != null && value.Length != Length.Value) return false;
            if (MinLength != null && value.Length < MinLength.Value) return false;
            if (MaxLength != null && value.Length > MaxLength.Value) return false;

            var needsNumber = MinInclusive != null || MaxInclusive != null
                              || MinExclusive != null || MaxExclusive != null;

            if (!needsNumber) return true;

            // A bound on a value that is not a number fails rather than being ignored. "Fire
            // rating >= 60" against "sixty minutes" is a data problem somebody has to fix, and
            // passing it silently is how it stays unfixed.
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return false;

            if (MinInclusive != null && number < MinInclusive.Value) return false;
            if (MaxInclusive != null && number > MaxInclusive.Value) return false;
            if (MinExclusive != null && number <= MinExclusive.Value) return false;
            if (MaxExclusive != null && number >= MaxExclusive.Value) return false;

            return true;
        }

        /// <inheritdoc />
        public override string ToString() => Description;

        private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>One test an element is put to.</summary>
    public abstract class IdsFacet
    {
        internal IdsFacet()
        {
        }

        /// <summary>Whether this must hold, must not, or applies only when the data is present.</summary>
        public IdsCardinality Cardinality { get; set; } = IdsCardinality.Required;

        /// <summary>What the author wants done about a failure, in their words.</summary>
        public string? Instructions { get; set; }

        /// <summary>How the facet reads in a report.</summary>
        public abstract string Describe();

        /// <summary>Whether the element satisfies the facet, ignoring cardinality.</summary>
        public abstract bool Matches(IdsElement element);

        /// <summary>
        /// Whether the data this facet needs is present at all, however wrong its value.
        ///
        /// The distinction the report turns on: a property that is missing and a property that is
        /// present with the wrong value are different problems, fixed by different people. Lumping
        /// them together sends everybody to the wrong place.
        /// </summary>
        public abstract bool DataIsPresent(IdsElement element);

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>Matches on IFC class.</summary>
    public sealed class IdsEntityFacet : IdsFacet
    {
        /// <summary>Create the facet.</summary>
        public IdsEntityFacet(IdsValue name, IdsValue? predefinedType = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            PredefinedType = predefinedType;
        }

        /// <summary>The IFC class.</summary>
        public IdsValue Name { get; }

        /// <summary>The predefined type, when the facet narrows to one.</summary>
        public IdsValue? PredefinedType { get; }

        /// <inheritdoc />
        public override string Describe() =>
            "entity " + Name + (PredefinedType != null ? " of type " + PredefinedType : string.Empty);

        /// <inheritdoc />
        public override bool Matches(IdsElement element) =>
            Name.Matches(element.IfcType)
            && (PredefinedType == null || PredefinedType.Matches(element.PredefinedType));

        /// <inheritdoc />
        public override bool DataIsPresent(IdsElement element) => element.IfcType != null;
    }

    /// <summary>Matches on an IFC attribute.</summary>
    public sealed class IdsAttributeFacet : IdsFacet
    {
        /// <summary>Create the facet.</summary>
        public IdsAttributeFacet(IdsValue name, IdsValue? value = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value;
        }

        /// <summary>The attribute name.</summary>
        public IdsValue Name { get; }

        /// <summary>The value it must hold, when the facet specifies one.</summary>
        public IdsValue? Value { get; }

        /// <inheritdoc />
        public override string Describe() =>
            "attribute " + Name + (Value != null ? " = " + Value : " present");

        /// <inheritdoc />
        public override bool Matches(IdsElement element) =>
            Named(element).Any(kv => Value == null ? !string.IsNullOrEmpty(kv.Value) : Value.Matches(kv.Value));

        /// <inheritdoc />
        public override bool DataIsPresent(IdsElement element) => Named(element).Any();

        private IEnumerable<KeyValuePair<string, string?>> Named(IdsElement element) =>
            element.Attributes.Where(kv => Name.Matches(kv.Key));
    }

    /// <summary>Matches on a property in a property set.</summary>
    public sealed class IdsPropertyFacet : IdsFacet
    {
        /// <summary>Create the facet.</summary>
        public IdsPropertyFacet(IdsValue propertySet, IdsValue baseName, IdsValue? value = null, string? dataType = null)
        {
            PropertySet = propertySet ?? throw new ArgumentNullException(nameof(propertySet));
            BaseName = baseName ?? throw new ArgumentNullException(nameof(baseName));
            Value = value;
            DataType = dataType;
        }

        /// <summary>The property set.</summary>
        public IdsValue PropertySet { get; }

        /// <summary>The property name.</summary>
        public IdsValue BaseName { get; }

        /// <summary>The value it must hold, when the facet specifies one.</summary>
        public IdsValue? Value { get; }

        /// <summary>The IFC data type the value should have, when the facet names one.</summary>
        public string? DataType { get; }

        /// <inheritdoc />
        public override string Describe() =>
            "property " + PropertySet + "." + BaseName + (Value != null ? " = " + Value : " present");

        /// <inheritdoc />
        public override bool Matches(IdsElement element) =>
            Named(element).Any(p => Value == null ? !string.IsNullOrEmpty(p.Value) : Value.Matches(p.Value));

        /// <inheritdoc />
        public override bool DataIsPresent(IdsElement element) => Named(element).Any();

        private IEnumerable<IdsProperty> Named(IdsElement element) =>
            element.Properties.Where(p => PropertySet.Matches(p.PropertySet) && BaseName.Matches(p.Name));
    }

    /// <summary>Matches on a classification reference.</summary>
    public sealed class IdsClassificationFacet : IdsFacet
    {
        /// <summary>Create the facet.</summary>
        public IdsClassificationFacet(IdsValue system, IdsValue? value = null)
        {
            System = system ?? throw new ArgumentNullException(nameof(system));
            Value = value;
        }

        /// <summary>The classification system, e.g. Uniclass 2015.</summary>
        public IdsValue System { get; }

        /// <summary>The code, when the facet specifies one.</summary>
        public IdsValue? Value { get; }

        /// <inheritdoc />
        public override string Describe() =>
            "classification " + System + (Value != null ? " = " + Value : " present");

        /// <inheritdoc />
        public override bool Matches(IdsElement element) =>
            InSystem(element).Any(c => Value == null ? !string.IsNullOrEmpty(c.Value) : Value.Matches(c.Value));

        /// <inheritdoc />
        public override bool DataIsPresent(IdsElement element) => InSystem(element).Any();

        private IEnumerable<IdsClassification> InSystem(IdsElement element) =>
            element.Classifications.Where(c => System.Matches(c.System));
    }

    /// <summary>Matches on material.</summary>
    public sealed class IdsMaterialFacet : IdsFacet
    {
        /// <summary>Create the facet.</summary>
        public IdsMaterialFacet(IdsValue? value = null) => Value = value;

        /// <summary>The material name, when the facet specifies one.</summary>
        public IdsValue? Value { get; }

        /// <inheritdoc />
        public override string Describe() =>
            "material" + (Value != null ? " " + Value : " present");

        /// <inheritdoc />
        public override bool Matches(IdsElement element) =>
            Value == null ? element.Materials.Any(m => !string.IsNullOrEmpty(m)) : element.Materials.Any(Value.Matches);

        /// <inheritdoc />
        public override bool DataIsPresent(IdsElement element) => element.Materials.Count > 0;
    }

    /// <summary>Matches on what the element belongs to.</summary>
    public sealed class IdsPartOfFacet : IdsFacet
    {
        /// <summary>Create the facet.</summary>
        public IdsPartOfFacet(IdsValue entityName, string? relation = null)
        {
            EntityName = entityName ?? throw new ArgumentNullException(nameof(entityName));
            Relation = relation;
        }

        /// <summary>The IFC class of the thing it must be part of.</summary>
        public IdsValue EntityName { get; }

        /// <summary>Which relationship, when the facet names one.</summary>
        public string? Relation { get; }

        /// <inheritdoc />
        public override string Describe() =>
            "part of " + EntityName + (Relation != null ? " via " + Relation : string.Empty);

        /// <inheritdoc />
        public override bool Matches(IdsElement element) => element.PartOf.Any(EntityName.Matches);

        /// <inheritdoc />
        public override bool DataIsPresent(IdsElement element) => element.PartOf.Count > 0;
    }

    /// <summary>One property on an element.</summary>
    public sealed class IdsProperty
    {
        /// <summary>Create a property.</summary>
        public IdsProperty(string propertySet, string name, string? value)
        {
            PropertySet = propertySet ?? throw new ArgumentNullException(nameof(propertySet));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value;
        }

        /// <summary>Which set it is in.</summary>
        public string PropertySet { get; }

        /// <summary>Its name.</summary>
        public string Name { get; }

        /// <summary>Its value.</summary>
        public string? Value { get; }
    }

    /// <summary>One classification reference on an element.</summary>
    public sealed class IdsClassification
    {
        /// <summary>Create a reference.</summary>
        public IdsClassification(string system, string? value)
        {
            System = system ?? throw new ArgumentNullException(nameof(system));
            Value = value;
        }

        /// <summary>The system.</summary>
        public string System { get; }

        /// <summary>The code.</summary>
        public string? Value { get; }
    }

    /// <summary>
    /// One element, as the checker sees it.
    ///
    /// Flattened for the same reason <c>ClashItem</c> is: the rules become pure functions over
    /// values, the adapter resolves the host data once, and the whole checker runs on a Linux CI
    /// job without a model in sight.
    /// </summary>
    public sealed class IdsElement
    {
        /// <summary>Create an element.</summary>
        /// <param name="id">How it is named in the report.</param>
        /// <param name="ifcType">Its IFC class, upper case, or null when it has none.</param>
        public IdsElement(string id, string? ifcType = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            IfcType = ifcType;
        }

        /// <summary>How it is named in the report.</summary>
        public string Id { get; }

        /// <summary>
        /// Its IFC class. Null for an element from a source that has none — a DWG block, a native
        /// solid — which is not an error and does not make it fail: it makes it not applicable.
        /// </summary>
        public string? IfcType { get; }

        /// <summary>Its predefined type.</summary>
        public string? PredefinedType { get; set; }

        /// <summary>IFC attributes.</summary>
        public IDictionary<string, string?> Attributes { get; } =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Properties.</summary>
        public IList<IdsProperty> Properties { get; } = new List<IdsProperty>();

        /// <summary>Classification references.</summary>
        public IList<IdsClassification> Classifications { get; } = new List<IdsClassification>();

        /// <summary>Materials.</summary>
        public IList<string> Materials { get; } = new List<string>();

        /// <summary>IFC classes of the things this belongs to.</summary>
        public IList<string> PartOf { get; } = new List<string>();

        /// <inheritdoc />
        public override string ToString() => Id + (IfcType != null ? " (" + IfcType + ")" : string.Empty);
    }

    /// <summary>One requirement in an IDS file: who it applies to, and what they must have.</summary>
    public sealed class IdsSpecification
    {
        /// <summary>Create a specification.</summary>
        public IdsSpecification(string name)
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? throw new ArgumentException("a specification must be named", nameof(name))
                : name;
        }

        /// <summary>Its name.</summary>
        public string Name { get; }

        /// <summary>The author's identifier for it.</summary>
        public string? Identifier { get; set; }

        /// <summary>What it is for.</summary>
        public string? Description { get; set; }

        /// <summary>What the author wants done about failures.</summary>
        public string? Instructions { get; set; }

        /// <summary>Which IFC versions it targets.</summary>
        public IList<string> IfcVersions { get; } = new List<string>();

        /// <summary>
        /// How many elements the author expects the applicability to select, at least.
        ///
        /// The schema puts <c>xs:occurs</c> on applicability and nowhere else, which is IDS's own
        /// answer to the vacuous pass: "at least one wall must exist, and every wall must carry a
        /// fire rating" is one specification, not two. With a minimum of one, a model containing no
        /// walls fails this rather than sailing through it.
        /// </summary>
        public int MinOccurs { get; set; }

        /// <summary>How many it expects at most, or null for no limit.</summary>
        public int? MaxOccurs { get; set; }

        /// <summary>Which elements it applies to. An element must match all of them.</summary>
        public IList<IdsFacet> Applicability { get; } = new List<IdsFacet>();

        /// <summary>What those elements must have.</summary>
        public IList<IdsFacet> Requirements { get; } = new List<IdsFacet>();

        /// <inheritdoc />
        public override string ToString() => Name;
    }

    /// <summary>A whole IDS file.</summary>
    public sealed class IdsDocument
    {
        /// <summary>Create a document.</summary>
        public IdsDocument(string title)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title;
        }

        /// <summary>Its title.</summary>
        public string Title { get; }

        /// <summary>Its version.</summary>
        public string? Version { get; set; }

        /// <summary>Who wrote it.</summary>
        public string? Author { get; set; }

        /// <summary>What it is for.</summary>
        public string? Description { get; set; }

        /// <summary>The specifications.</summary>
        public IList<IdsSpecification> Specifications { get; } = new List<IdsSpecification>();

        /// <inheritdoc />
        public override string ToString() =>
            Title + " (" + Specifications.Count.ToString(CultureInfo.InvariantCulture) + " specifications)";
    }
}
