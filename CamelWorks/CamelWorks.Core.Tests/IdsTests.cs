using System;
using System.IO;
using System.Linq;
using System.Text;
using CamelWorks.Core.OpenBim;
using Xunit;

namespace CamelWorks.Core.Tests
{
    internal static class IdsFixture
    {
        internal static IdsElement Wall(string id, string? fireRating = "60", string? name = "WA-001")
        {
            var element = new IdsElement(id, "IFCWALL");
            if (name != null) element.Attributes["Name"] = name;
            if (fireRating != null) element.Properties.Add(new IdsProperty("Pset_WallCommon", "FireRating", fireRating));
            return element;
        }

        internal static IdsSpecification WallsCarryAFireRating()
        {
            var specification = new IdsSpecification("Walls carry a fire rating");
            specification.Applicability.Add(new IdsEntityFacet(IdsValue.Simple("IFCWALL")));
            specification.Requirements.Add(new IdsPropertyFacet(
                IdsValue.Simple("Pset_WallCommon"),
                IdsValue.Simple("FireRating"),
                IdsValue.Restriction(enumeration: new[] { "30", "60", "120" })));
            return specification;
        }

        internal static IdsDocument Document(params IdsSpecification[] specifications)
        {
            var document = new IdsDocument("Riverside stage 4");
            foreach (var specification in specifications) document.Specifications.Add(specification);
            return document;
        }

        internal static IdsReadResult Read(string xml)
        {
            using (var buffer = new MemoryStream(new UTF8Encoding(false).GetBytes(xml)))
                return IdsReader.Read(buffer);
        }
    }

    public class IdsCheckerTests
    {
        // -----------------------------------------------------------------------------------
        // The vacuous pass — the reason to build this rather than trust a green tick
        // -----------------------------------------------------------------------------------

        [Fact]
        public void A_specification_that_matches_nothing_is_never_shown_as_a_pass()
        {
            // "Every wall must carry a fire rating" over a model with no walls is vacuously true.
            // A checker that shows that green has told the reader the opposite of what happened.
            var report = IdsChecker.Check(
                IdsFixture.Document(IdsFixture.WallsCarryAFireRating()),
                new[] { new IdsElement("d1", "IFCDUCTSEGMENT") });

            var result = Assert.Single(report.Results);
            Assert.True(result.NothingApplied);
            Assert.False(result.IsSatisfied);
            Assert.False(report.IsPass);
            Assert.Equal(1, report.NotApplicable);
            Assert.Contains("nothing was checked", result.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_specification_that_demands_the_elements_exist_fails_when_they_do_not()
        {
            // IDS's own answer, and the schema puts the occurs attributes on applicability and
            // nowhere else. minOccurs="1" turns "nothing to check" into a failure.
            var specification = IdsFixture.WallsCarryAFireRating();
            specification.MinOccurs = 1;

            var report = IdsChecker.Check(IdsFixture.Document(specification),
                new[] { new IdsElement("d1", "IFCDUCTSEGMENT") });

            var result = Assert.Single(report.Results);
            Assert.True(result.CountOutOfRange);
            Assert.False(result.IsSatisfied);
            Assert.Equal(1, report.Unsatisfied);
            Assert.Equal(0, report.NotApplicable);
            Assert.Contains("requires at least 1", result.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_specification_can_also_cap_how_many_it_expects()
        {
            var specification = IdsFixture.WallsCarryAFireRating();
            specification.MaxOccurs = 1;

            var report = IdsChecker.Check(IdsFixture.Document(specification),
                new[] { IdsFixture.Wall("w1"), IdsFixture.Wall("w2") });

            Assert.True(Assert.Single(report.Results).CountOutOfRange);
            Assert.Contains("at most 1", report.Results[0].ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_ids_is_not_a_pass()
        {
            var report = IdsChecker.Check(IdsFixture.Document(), new[] { IdsFixture.Wall("w1") });

            Assert.False(report.IsPass);
            Assert.Empty(report.Results);
        }

        // -----------------------------------------------------------------------------------
        // Missing and wrong are different failures
        // -----------------------------------------------------------------------------------

        [Fact]
        public void A_missing_property_and_a_wrong_one_are_distinguished()
        {
            // They are fixed by different people — one is a modelling omission, the other a data
            // error — and a report that calls them both "fail" sends everybody to the wrong place.
            var report = IdsChecker.Check(
                IdsFixture.Document(IdsFixture.WallsCarryAFireRating()),
                new[]
                {
                    IdsFixture.Wall("w-missing", fireRating: null),
                    IdsFixture.Wall("w-wrong", fireRating: "45"),
                    IdsFixture.Wall("w-ok"),
                });

            var result = Assert.Single(report.Results);
            Assert.Equal(3, result.Applicable);
            Assert.Equal(1, result.Passed);

            Assert.Equal(IdsFailure.Missing, result.Violations.Single(v => v.ElementId == "w-missing").Failure);
            Assert.Equal(IdsFailure.WrongValue, result.Violations.Single(v => v.ElementId == "w-wrong").Failure);
        }

        [Fact]
        public void A_violation_reads_as_a_sentence_somebody_can_act_on()
        {
            var report = IdsChecker.Check(
                IdsFixture.Document(IdsFixture.WallsCarryAFireRating()),
                new[] { IdsFixture.Wall("w-1", fireRating: null) });

            var text = report.Results[0].Violations[0].ToString();

            Assert.Contains("w-1 is missing", text, StringComparison.Ordinal);
            Assert.Contains("Pset_WallCommon", text, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------
        // Cardinality
        // -----------------------------------------------------------------------------------

        [Fact]
        public void A_prohibited_requirement_fails_when_the_element_has_it()
        {
            var specification = new IdsSpecification("Ducts carry no material");
            specification.Applicability.Add(new IdsEntityFacet(IdsValue.Simple("IFCDUCTSEGMENT")));
            specification.Requirements.Add(new IdsMaterialFacet { Cardinality = IdsCardinality.Prohibited });

            var bare = new IdsElement("d1", "IFCDUCTSEGMENT");
            var withMaterial = new IdsElement("d2", "IFCDUCTSEGMENT");
            withMaterial.Materials.Add("Galvanised steel");

            var report = IdsChecker.Check(IdsFixture.Document(specification), new[] { bare, withMaterial });

            var result = Assert.Single(report.Results);
            Assert.Equal(1, result.Passed);
            Assert.Equal(IdsFailure.Prohibited, Assert.Single(result.Violations).Failure);
        }

        [Fact]
        public void An_optional_requirement_forgives_absence_but_not_a_wrong_value()
        {
            // An element with no fire rating passes; one whose fire rating is "yes" does not.
            var specification = IdsFixture.WallsCarryAFireRating();
            specification.Requirements[0].Cardinality = IdsCardinality.Optional;

            var report = IdsChecker.Check(IdsFixture.Document(specification),
                new[] { IdsFixture.Wall("absent", fireRating: null), IdsFixture.Wall("wrong", fireRating: "yes") });

            var result = Assert.Single(report.Results);
            Assert.Equal(1, result.Passed);
            Assert.Equal("wrong", Assert.Single(result.Violations).ElementId);
        }

        // -----------------------------------------------------------------------------------
        // Values
        // -----------------------------------------------------------------------------------

        [Fact]
        public void An_absent_value_never_satisfies_a_restriction()
        {
            // Treating "there is nothing here" as satisfying "at most 60" would pass every element
            // nobody had filled in, which is the exact failure an IDS exists to catch.
            var bounded = IdsValue.Restriction(maxInclusive: 60);

            Assert.False(bounded.Matches(null));
            Assert.True(bounded.Matches("30"));
            Assert.False(bounded.Matches("90"));
        }

        [Fact]
        public void A_bound_on_something_that_is_not_a_number_fails_rather_than_being_ignored()
        {
            // "Fire rating >= 60" against "sixty minutes" is a data problem somebody has to fix.
            Assert.False(IdsValue.Restriction(minInclusive: 60).Matches("sixty minutes"));
        }

        [Fact]
        public void A_pattern_is_anchored_the_way_XML_Schema_anchors_it()
        {
            // XSD patterns are anchored at both ends implicitly and .NET's are not. Without the
            // anchors "WA-[0-9]{3}" would match "BAD-WA-001-XX" and quietly widen every rule.
            var pattern = IdsValue.Restriction(pattern: "WA-[0-9]{3}");

            Assert.True(pattern.Matches("WA-001"));
            Assert.False(pattern.Matches("BAD-WA-001-XX"));
            Assert.False(pattern.Matches("WA-0011"));
        }

        [Fact]
        public void A_restriction_combines_its_facets()
        {
            var value = IdsValue.Restriction(minInclusive: 30, maxInclusive: 120, minLength: 2);

            Assert.True(value.Matches("60"));
            Assert.False(value.Matches("6"));      // too short before it is ever a number
            Assert.False(value.Matches("240"));
        }

        [Fact]
        public void A_value_reads_as_the_rule_the_author_wrote()
        {
            Assert.Contains("one of '30', '60'",
                IdsValue.Restriction(enumeration: new[] { "30", "60" }).Description, StringComparison.Ordinal);
            Assert.Contains(">= 30", IdsValue.Restriction(minInclusive: 30).Description, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------
        // Facets
        // -----------------------------------------------------------------------------------

        [Fact]
        public void An_element_with_no_ifc_class_is_not_applicable_rather_than_failing()
        {
            // A DWG block or a native solid has no IFC class. That is not an error and it is not a
            // failure; it means the specification has nothing to say about it.
            var report = IdsChecker.Check(
                IdsFixture.Document(IdsFixture.WallsCarryAFireRating()),
                new[] { IdsFixture.Wall("w1"), new IdsElement("block-3") });

            Assert.Equal(1, Assert.Single(report.Results).Applicable);
            Assert.Equal(2, report.ElementsChecked);
        }

        [Fact]
        public void Applicability_narrows_by_every_facet_at_once()
        {
            var specification = new IdsSpecification("External walls");
            specification.Applicability.Add(new IdsEntityFacet(IdsValue.Simple("IFCWALL")));
            specification.Applicability.Add(new IdsPropertyFacet(
                IdsValue.Simple("Pset_WallCommon"), IdsValue.Simple("IsExternal"), IdsValue.Simple("true")));
            specification.Requirements.Add(new IdsAttributeFacet(IdsValue.Simple("Name")));

            var external = IdsFixture.Wall("external");
            external.Properties.Add(new IdsProperty("Pset_WallCommon", "IsExternal", "true"));

            var report = IdsChecker.Check(IdsFixture.Document(specification),
                new[] { external, IdsFixture.Wall("internal") });

            Assert.Equal(1, Assert.Single(report.Results).Applicable);
        }

        [Fact]
        public void A_classification_facet_matches_the_system_then_the_code()
        {
            var specification = new IdsSpecification("Ducts are classified");
            specification.Applicability.Add(new IdsEntityFacet(IdsValue.Simple("IFCDUCTSEGMENT")));
            specification.Requirements.Add(new IdsClassificationFacet(
                IdsValue.Simple("Uniclass 2015"), IdsValue.Restriction(pattern: "Ss_[0-9_]+")));

            var right = new IdsElement("d1", "IFCDUCTSEGMENT");
            right.Classifications.Add(new IdsClassification("Uniclass 2015", "Ss_65_40_33"));

            var wrongSystem = new IdsElement("d2", "IFCDUCTSEGMENT");
            wrongSystem.Classifications.Add(new IdsClassification("Omniclass", "Ss_65_40_33"));

            var report = IdsChecker.Check(IdsFixture.Document(specification), new[] { right, wrongSystem });

            var result = Assert.Single(report.Results);
            Assert.Equal(1, result.Passed);

            // The system is absent, not merely wrong — which points at a different fix.
            Assert.Equal(IdsFailure.Missing, Assert.Single(result.Violations).Failure);
        }

        [Fact]
        public void A_part_of_facet_looks_at_what_contains_the_element()
        {
            var specification = new IdsSpecification("Ducts sit in a storey");
            specification.Applicability.Add(new IdsEntityFacet(IdsValue.Simple("IFCDUCTSEGMENT")));
            specification.Requirements.Add(new IdsPartOfFacet(IdsValue.Simple("IFCBUILDINGSTOREY")));

            var placed = new IdsElement("d1", "IFCDUCTSEGMENT");
            placed.PartOf.Add("IFCBUILDINGSTOREY");

            var report = IdsChecker.Check(IdsFixture.Document(specification),
                new[] { placed, new IdsElement("d2", "IFCDUCTSEGMENT") });

            Assert.Equal(1, Assert.Single(report.Results).Passed);
        }

        [Fact]
        public void Failures_are_capped_and_the_cap_is_declared()
        {
            var walls = Enumerable.Range(0, 20).Select(i => IdsFixture.Wall("w" + i, fireRating: null)).ToList();

            var report = IdsChecker.Check(IdsFixture.Document(IdsFixture.WallsCarryAFireRating()), walls,
                violationCap: 5);

            var result = Assert.Single(report.Results);
            Assert.Equal(20, result.Failed);          // the count is complete
            Assert.Equal(5, result.Violations.Count); // the list is not
            Assert.True(result.ViolationsTruncated);
        }
    }

    public class IdsReaderTests
    {
        private const string Sample = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<ids xmlns=""http://standards.buildingsmart.org/IDS"" xmlns:xs=""http://www.w3.org/2001/XMLSchema"">
  <info>
    <title>Riverside stage 4</title>
    <version>1.2</version>
  </info>
  <specifications>
    <specification name=""Walls carry a fire rating"" ifcVersion=""IFC4 IFC4X3"" identifier=""FR-01""
                   instructions=""Set Pset_WallCommon.FireRating on every wall."">
      <applicability minOccurs=""1"" maxOccurs=""unbounded"">
        <entity><name><simpleValue>IFCWALL</simpleValue></name></entity>
      </applicability>
      <requirements>
        <property dataType=""IFCLABEL"" cardinality=""required"">
          <propertySet><simpleValue>Pset_WallCommon</simpleValue></propertySet>
          <baseName><simpleValue>FireRating</simpleValue></baseName>
          <value>
            <xs:restriction base=""xs:string"">
              <xs:enumeration value=""30""/>
              <xs:enumeration value=""60""/>
            </xs:restriction>
          </value>
        </property>
        <attribute cardinality=""required"">
          <name><simpleValue>Name</simpleValue></name>
          <value>
            <xs:restriction base=""xs:string"">
              <xs:pattern value=""WA-[0-9]{3}""/>
            </xs:restriction>
          </value>
        </attribute>
        <material cardinality=""prohibited""/>
      </requirements>
    </specification>
  </specifications>
</ids>";

        [Fact]
        public void A_specification_is_read_whole()
        {
            var result = IdsFixture.Read(Sample);

            Assert.True(result.IsUsable);
            Assert.Equal("Riverside stage 4", result.Document!.Title);
            Assert.Equal("1.2", result.Document.Version);

            var specification = Assert.Single(result.Document.Specifications);
            Assert.Equal("FR-01", specification.Identifier);
            Assert.Equal(new[] { "IFC4", "IFC4X3" }, specification.IfcVersions);
            Assert.Single(specification.Applicability);
            Assert.Equal(3, specification.Requirements.Count);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void The_occurs_attributes_on_applicability_are_read()
        {
            var specification = IdsFixture.Read(Sample).Document!.Specifications[0];

            Assert.Equal(1, specification.MinOccurs);
            Assert.Null(specification.MaxOccurs);   // "unbounded"
        }

        [Fact]
        public void Restrictions_are_read_as_the_rules_they_are()
        {
            var requirements = IdsFixture.Read(Sample).Document!.Specifications[0].Requirements;

            var property = Assert.IsType<IdsPropertyFacet>(requirements[0]);
            Assert.Equal(new[] { "30", "60" }, property.Value!.Enumeration);
            Assert.Equal("IFCLABEL", property.DataType);

            var attribute = Assert.IsType<IdsAttributeFacet>(requirements[1]);
            Assert.True(attribute.Value!.Matches("WA-001"));
            Assert.False(attribute.Value.Matches("WA-1"));

            Assert.Equal(IdsCardinality.Prohibited, requirements[2].Cardinality);
        }

        [Fact]
        public void A_read_file_checks_the_same_way_a_hand_built_one_does()
        {
            var document = IdsFixture.Read(Sample).Document!;

            var good = IdsFixture.Wall("w1", fireRating: "60", name: "WA-001");
            var bad = IdsFixture.Wall("w2", fireRating: "90", name: "wall 2");

            var report = IdsChecker.Check(document, new[] { good, bad });

            Assert.Equal(1, Assert.Single(report.Results).Passed);
            Assert.Equal(2, report.Results[0].Violations.Count);   // rating and name
        }

        [Fact]
        public void A_file_without_the_namespace_is_still_read()
        {
            // Files in the wild are sometimes written unqualified. Refusing those helps nobody.
            var result = IdsFixture.Read(Sample.Replace(
                @"xmlns=""http://standards.buildingsmart.org/IDS"" ", string.Empty));

            Assert.True(result.IsUsable);
            Assert.Single(result.Document!.Specifications);
        }

        [Fact]
        public void A_specification_with_no_applicability_is_skipped_and_named()
        {
            // It would select nothing, and an IDS is a contract — quietly dropping a clause
            // produces a check that passes for the wrong reason.
            var result = IdsFixture.Read(@"<?xml version=""1.0""?>
<ids xmlns=""http://standards.buildingsmart.org/IDS"">
  <info><title>t</title></info>
  <specifications>
    <specification name=""Empty"" ifcVersion=""IFC4"">
      <applicability/>
    </specification>
  </specifications>
</ids>");

            Assert.False(result.IsUsable);
            Assert.Contains(result.Warnings, w => w.Contains("no applicability"));
            Assert.Contains(result.Warnings, w => w.Contains("pass for the wrong reason"));
        }

        [Fact]
        public void An_unknown_facet_is_named_rather_than_silently_ignored()
        {
            var result = IdsFixture.Read(@"<?xml version=""1.0""?>
<ids xmlns=""http://standards.buildingsmart.org/IDS"">
  <info><title>t</title></info>
  <specifications>
    <specification name=""Odd"" ifcVersion=""IFC4"">
      <applicability><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>
      <requirements><quantity><name><simpleValue>Volume</simpleValue></name></quantity></requirements>
    </specification>
  </specifications>
</ids>");

            Assert.True(result.IsUsable);
            Assert.Contains(result.Warnings, w => w.Contains("does not understand"));
        }

        [Fact]
        public void Something_that_is_not_an_ids_file_is_refused_clearly()
        {
            Assert.Contains(IdsFixture.Read("<?xml version=\"1.0\"?><nope/>").Warnings,
                w => w.Contains("not an IDS file"));

            Assert.Contains(IdsFixture.Read("not xml at all <<<").Warnings,
                w => w.Contains("not readable XML"));
        }

        [Fact]
        public void An_external_entity_is_not_fetched()
        {
            // An IDS arrives by email. A parser that fetches what a document tells it to is a way
            // to make a coordination file reach across the network.
            var result = IdsFixture.Read(@"<?xml version=""1.0""?>
<!DOCTYPE ids [<!ENTITY x SYSTEM ""file:///etc/passwd"">]>
<ids xmlns=""http://standards.buildingsmart.org/IDS"">
  <info><title>&x;</title></info>
</ids>");

            Assert.Null(result.Document);
            Assert.Contains(result.Warnings, w => w.Contains("not readable XML"));
        }
    }
}
