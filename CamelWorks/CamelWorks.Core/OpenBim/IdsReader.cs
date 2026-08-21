using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace CamelWorks.Core.OpenBim
{
    /// <summary>What came back from an .ids file.</summary>
    public sealed class IdsReadResult
    {
        internal IdsReadResult(IdsDocument? document, IReadOnlyList<string> warnings)
        {
            Document = document; Warnings = warnings;
        }

        /// <summary>The document, or null when the file could not be read at all.</summary>
        public IdsDocument? Document { get; }

        /// <summary>
        /// What was wrong with it, having read what could be read.
        ///
        /// A specification that was skipped is named. An IDS is a contract, and quietly dropping
        /// one of its clauses produces a check that passes for the wrong reason — the same failure
        /// mode as a specification that matches nothing, arriving one step earlier.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>True when there is a document to check against.</summary>
        public bool IsUsable => Document != null && Document.Specifications.Count > 0;
    }

    /// <summary>
    /// Reads an .ids file.
    ///
    /// Two things in the format need care. Its values are either a literal or an <c>xs:restriction</c>
    /// borrowed wholesale from XML Schema, so the reader has to understand facets it did not
    /// design. And the file is untrusted input that contains regular expressions, so the parser
    /// resolves no external entities and the patterns run under a timeout — an IDS someone emails
    /// over should not be able to hang the host.
    ///
    /// There is no schema-validation step here, unlike BCF. The asymmetry is deliberate: BCF files
    /// are written, so another tool has to accept them and their shape must be proved against the
    /// published XSD. IDS files are only read, and validating our own reading of somebody else's
    /// file proves nothing about anything. What matters instead is that a file this reader cannot
    /// fully understand is reported rather than quietly half-read.
    /// </summary>
    public static class IdsReader
    {
        private static readonly XNamespace Ids = "http://standards.buildingsmart.org/IDS";
        private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

        /// <summary>Read an .ids file.</summary>
        /// <param name="input">The file. Left open.</param>
        public static IdsReadResult Read(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var warnings = new List<string>();
            XElement? root;

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreWhitespace = true,
                };

                using (var reader = XmlReader.Create(input, settings))
                    root = XDocument.Load(reader).Root;
            }
            catch (XmlException e)
            {
                return new IdsReadResult(null, new[] { "this is not readable XML: " + e.Message });
            }

            if (root == null || root.Name.LocalName != "ids")
                return new IdsReadResult(null, new[] { "the root element is not <ids>, so this is not an IDS file" });

            var info = Child(root, "info");
            var document = new IdsDocument(Text(info, "title") ?? "Untitled")
            {
                Version = Text(info, "version"),
                Description = Text(info, "description"),
                Author = Text(info, "author"),
            };

            var specifications = Child(root, "specifications");
            if (specifications == null)
            {
                warnings.Add("the file has no <specifications> element, so there is nothing to check");
                return new IdsReadResult(document, warnings);
            }

            foreach (var element in Children(specifications, "specification"))
            {
                var specification = ReadSpecification(element, warnings);
                if (specification != null) document.Specifications.Add(specification);
            }

            if (document.Specifications.Count == 0)
                warnings.Add("no specifications could be read, so a check against this would pass for the wrong reason");

            return new IdsReadResult(document, warnings);
        }

        private static IdsSpecification? ReadSpecification(XElement element, List<string> warnings)
        {
            var name = element.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                warnings.Add("a specification has no name and was skipped");
                return null;
            }

            var specification = new IdsSpecification(name!)
            {
                Identifier = element.Attribute("identifier")?.Value,
                Description = element.Attribute("description")?.Value,
                Instructions = element.Attribute("instructions")?.Value,
            };

            var versions = element.Attribute("ifcVersion")?.Value;
            if (!string.IsNullOrWhiteSpace(versions))
                foreach (var version in versions!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    specification.IfcVersions.Add(version);

            var applicability = Child(element, "applicability");
            if (applicability != null)
            {
                // xs:occurs on applicability, which the schema puts nowhere else. minOccurs="1"
                // means the model is expected to contain at least one matching element.
                if (int.TryParse(applicability.Attribute("minOccurs")?.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var min) && min >= 0)
                    specification.MinOccurs = min;

                var max = applicability.Attribute("maxOccurs")?.Value;
                if (max != null && !string.Equals(max, "unbounded", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    specification.MaxOccurs = parsed;

                foreach (var facet in Facets(applicability, warnings, name!, requirement: false))
                    specification.Applicability.Add(facet);
            }

            if (specification.Applicability.Count == 0)
            {
                // Without applicability there is nothing to select, and the checker would find the
                // specification applies to nothing — reported honestly, but the cause is here.
                warnings.Add("'" + name + "' has no applicability, so it selects no elements and was skipped");
                return null;
            }

            var requirements = Child(element, "requirements");
            if (requirements != null)
                foreach (var facet in Facets(requirements, warnings, name!, requirement: true))
                    specification.Requirements.Add(facet);

            return specification;
        }

        private static IEnumerable<IdsFacet> Facets(XElement parent, List<string> warnings,
                                                    string specification, bool requirement)
        {
            foreach (var element in parent.Elements())
            {
                var facet = ReadFacet(element, warnings, specification);
                if (facet == null) continue;

                if (requirement)
                {
                    facet.Cardinality = Cardinality(element.Attribute("cardinality")?.Value);
                    facet.Instructions = element.Attribute("instructions")?.Value;
                }

                yield return facet;
            }
        }

        private static IdsFacet? ReadFacet(XElement element, List<string> warnings, string specification)
        {
            switch (element.Name.LocalName)
            {
                case "entity":
                {
                    var name = Value(Child(element, "name"));
                    if (name == null) break;
                    return new IdsEntityFacet(name, Value(Child(element, "predefinedType")));
                }

                case "attribute":
                {
                    var name = Value(Child(element, "name"));
                    if (name == null) break;
                    return new IdsAttributeFacet(name, Value(Child(element, "value")));
                }

                case "property":
                {
                    var set = Value(Child(element, "propertySet"));
                    var baseName = Value(Child(element, "baseName"));
                    if (set == null || baseName == null) break;
                    return new IdsPropertyFacet(set, baseName, Value(Child(element, "value")),
                                                element.Attribute("dataType")?.Value);
                }

                case "classification":
                {
                    var system = Value(Child(element, "system"));
                    if (system == null) break;
                    return new IdsClassificationFacet(system, Value(Child(element, "value")));
                }

                case "material":
                    return new IdsMaterialFacet(Value(Child(element, "value")));

                case "partOf":
                {
                    var entity = Child(element, "entity");
                    var name = Value(Child(entity, "name"));
                    if (name == null) break;
                    return new IdsPartOfFacet(name, element.Attribute("relation")?.Value);
                }

                default:
                    warnings.Add("'" + specification + "' uses a facet this build does not understand ("
                                 + element.Name.LocalName + "); it was ignored");
                    return null;
            }

            warnings.Add("'" + specification + "' has an incomplete " + element.Name.LocalName
                         + " facet; it was ignored");
            return null;
        }

        private static IdsCardinality Cardinality(string? text) => text switch
        {
            "prohibited" => IdsCardinality.Prohibited,
            "optional" => IdsCardinality.Optional,
            _ => IdsCardinality.Required,
        };

        private static IdsValue? Value(XElement? element)
        {
            if (element == null) return null;

            var simple = Child(element, "simpleValue");
            if (simple != null) return IdsValue.Simple(simple.Value);

            var restriction = element.Element(Xsd + "restriction");
            if (restriction == null) return null;

            var enumeration = restriction.Elements(Xsd + "enumeration")
                .Select(e => e.Attribute("value")?.Value)
                .Where(v => v != null)
                .Select(v => v!)
                .ToList();

            return IdsValue.Restriction(
                pattern: Facet(restriction, "pattern"),
                enumeration: enumeration.Count > 0 ? enumeration : null,
                minInclusive: Number(restriction, "minInclusive"),
                maxInclusive: Number(restriction, "maxInclusive"),
                minExclusive: Number(restriction, "minExclusive"),
                maxExclusive: Number(restriction, "maxExclusive"),
                length: Integer(restriction, "length"),
                minLength: Integer(restriction, "minLength"),
                maxLength: Integer(restriction, "maxLength"));
        }

        private static string? Facet(XElement restriction, string name) =>
            restriction.Element(Xsd + name)?.Attribute("value")?.Value;

        private static double? Number(XElement restriction, string name) =>
            double.TryParse(Facet(restriction, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : (double?)null;

        private static int? Integer(XElement restriction, string name) =>
            int.TryParse(Facet(restriction, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v
                : (int?)null;

        // Namespace-tolerant lookup. The schema qualifies its elements, and files in the wild are
        // sometimes written without the namespace; refusing those would help nobody.
        private static XElement? Child(XElement? parent, string name) =>
            parent?.Element(Ids + name) ?? parent?.Element(name);

        private static IEnumerable<XElement> Children(XElement parent, string name)
        {
            var qualified = parent.Elements(Ids + name).ToList();
            return qualified.Count > 0 ? qualified : parent.Elements(name);
        }

        private static string? Text(XElement? parent, string name)
        {
            var value = Child(parent, name)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }
    }
}
