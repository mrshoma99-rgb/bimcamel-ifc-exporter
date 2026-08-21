using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CamelWorks.Core.OpenBim;
using Xunit;

namespace CamelWorks.Core.Tests
{
    internal static class BcfFixture
    {
        internal static readonly DateTimeOffset Raised =
            new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero);

        internal static BcfTopic Topic(string id = "finding-1")
        {
            var topic = new BcfTopic(id, "Duct clashes beam", Raised, "jg")
            {
                TopicType = "Clash",
                TopicStatus = "Open",
                Priority = "High",
                Description = "Riser 3 penetration",
                AssignedTo = "Structures",
                DueDate = Raised.AddDays(7),
            };

            topic.Labels.Add("L03");
            topic.Labels.Add("MEP");
            topic.ReferenceLinks.Add("https://example.test/x");

            var viewpoint = new BcfViewpoint(id + "-vp")
            {
                Camera = BcfCamera.Perspective(1, 2, 3, 1, 0, 0, 0, 0, 1, 82.5),
                DefaultVisibility = true,
                Snapshot = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 },
            };

            viewpoint.Selection.Add(new BcfComponent("1hqDDeUZjEUeu9SHfd2mQ0"));
            viewpoint.Selection.Add(new BcfComponent(null, "2:14872", "CamelWorks"));
            viewpoint.VisibilityExceptions.Add(new BcfComponent("0hqDDeUZjEUeu9SHfd2mQ1"));
            viewpoint.Colouring.Add(new BcfColouring("#ff0000",
                new[] { new BcfComponent("1hqDDeUZjEUeu9SHfd2mQ0") }));
            viewpoint.ClippingPlanes.Add(new BcfClippingPlane(0, 0, 3.5, 0, 0, 1));
            viewpoint.OpeningsVisible = true;

            topic.Viewpoints.Add(viewpoint);
            topic.Comments.Add(new BcfComment(id + "-c1", Raised.AddDays(1), "jg", "Agreed with structures")
            {
                ViewpointGuid = viewpoint.Guid,
            });

            return topic;
        }

        internal static byte[] Write(BcfVersion version, params BcfTopic[] topics)
        {
            using (var buffer = new MemoryStream())
            {
                BcfWriter.Write(buffer, topics, version, new BcfProject("project-1", "Riverside"));
                return buffer.ToArray();
            }
        }

        internal static Dictionary<string, string> Entries(byte[] zipBytes)
        {
            var text = new Dictionary<string, string>(StringComparer.Ordinal);

            using (var buffer = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;

                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        text[entry.FullName] = reader.ReadToEnd();
                }

            return text;
        }

        internal static string Markup(byte[] zipBytes) =>
            Entries(zipBytes).First(e => e.Key.EndsWith("markup.bcf", StringComparison.Ordinal)).Value;

        internal static string Viewpoint(byte[] zipBytes) =>
            Entries(zipBytes).First(e => e.Key.EndsWith(".bcfv", StringComparison.Ordinal)).Value;
    }

    public class BcfWriterTests
    {
        // -----------------------------------------------------------------------------------
        // The version differences that actually break files
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Comments_sit_beside_the_topic_in_two_one_and_inside_it_in_three_oh()
        {
            var v21 = BcfFixture.Markup(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()));
            var v30 = BcfFixture.Markup(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic()));

            Assert.DoesNotContain("<Comments>", v21, StringComparison.Ordinal);
            Assert.Contains("</Topic>", v21, StringComparison.Ordinal);
            Assert.True(v21.IndexOf("</Topic>", StringComparison.Ordinal)
                        < v21.IndexOf("<Comment ", StringComparison.Ordinal));

            Assert.Contains("<Comments>", v30, StringComparison.Ordinal);
            Assert.True(v30.IndexOf("<Comment ", StringComparison.Ordinal)
                        < v30.IndexOf("</Topic>", StringComparison.Ordinal));
        }

        [Fact]
        public void Repeated_elements_are_wrapped_in_three_oh_and_bare_in_two_one()
        {
            var v21 = BcfFixture.Markup(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()));
            var v30 = BcfFixture.Markup(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic()));

            Assert.Contains("<Labels>L03</Labels>", v21, StringComparison.Ordinal);
            Assert.Contains("<Label>L03</Label>", v30, StringComparison.Ordinal);
            Assert.Contains("<ReferenceLinks>", v30, StringComparison.Ordinal);
            Assert.DoesNotContain("<ReferenceLinks>", v21, StringComparison.Ordinal);
        }

        [Fact]
        public void Two_one_clamps_a_field_of_view_it_cannot_express_and_says_so()
        {
            // 2.1 restricts a perspective field of view to 45-60 degrees, and host cameras
            // routinely sit outside it. Writing the real value would fail validation; writing the
            // clamped one silently would change the framing with no explanation.
            using (var buffer = new MemoryStream())
            {
                var result = BcfWriter.Write(buffer, new[] { BcfFixture.Topic() }, BcfVersion.V21);

                Assert.Contains(result.Notes, n => n.Contains("clamped"));
                Assert.Contains("<FieldOfView>60.0</FieldOfView>",
                    BcfFixture.Viewpoint(buffer.ToArray()), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Three_oh_carries_the_field_of_view_unchanged()
        {
            using (var buffer = new MemoryStream())
            {
                var result = BcfWriter.Write(buffer, new[] { BcfFixture.Topic() }, BcfVersion.V30);

                Assert.DoesNotContain(result.Notes, n => n.Contains("clamped"));
                Assert.Contains("<FieldOfView>82.5</FieldOfView>",
                    BcfFixture.Viewpoint(buffer.ToArray()), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Three_oh_writes_the_aspect_ratio_it_requires_and_two_one_has_no_element_for_it()
        {
            // Found by validating against the published schema, not by reading it. A unit test
            // could not have caught this: the file was well-formed, round-tripped through our own
            // reader, and would have been rejected by every strict BCF 3.0 tool.
            var v21 = BcfFixture.Viewpoint(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()));
            var v30 = BcfFixture.Viewpoint(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic()));

            Assert.DoesNotContain("<AspectRatio>", v21, StringComparison.Ordinal);
            Assert.Contains("<AspectRatio>", v30, StringComparison.Ordinal);
        }

        [Fact]
        public void The_view_setup_hints_move_inside_visibility_in_three_oh()
        {
            var v21 = BcfFixture.Viewpoint(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()));
            var v30 = BcfFixture.Viewpoint(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic()));

            Assert.True(v21.IndexOf("<ViewSetupHints", StringComparison.Ordinal)
                        < v21.IndexOf("<Selection>", StringComparison.Ordinal));
            Assert.True(v30.IndexOf("<Visibility", StringComparison.Ordinal)
                        < v30.IndexOf("<ViewSetupHints", StringComparison.Ordinal));
        }

        [Fact]
        public void A_colour_wraps_its_components_in_three_oh_only()
        {
            var v21 = BcfFixture.Viewpoint(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()));
            var v30 = BcfFixture.Viewpoint(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic()));

            Assert.Contains("<Color Color=\"FF0000\">", v21, StringComparison.Ordinal);
            Assert.Contains("<Color Color=\"FF0000\">\n        <Components>", v30, StringComparison.Ordinal);
        }

        [Fact]
        public void The_version_file_says_what_each_schema_expects()
        {
            var v21 = BcfFixture.Entries(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()))["bcf.version"];
            var v30 = BcfFixture.Entries(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic()))["bcf.version"];

            Assert.Contains("VersionId=\"2.1\"", v21, StringComparison.Ordinal);
            Assert.Contains("<DetailedVersion>", v21, StringComparison.Ordinal);

            Assert.Contains("VersionId=\"3.0\"", v30, StringComparison.Ordinal);
            Assert.DoesNotContain("<DetailedVersion>", v30, StringComparison.Ordinal);   // removed in 3.0
        }

        // -----------------------------------------------------------------------------------
        // Identity
        // -----------------------------------------------------------------------------------

        [Fact]
        public void The_same_finding_exported_twice_is_the_same_topic()
        {
            // A fresh GUID each export makes every re-export look like a new issue to the
            // receiving tool, which is exactly how BCF round-trips turn into duplicate registers.
            var first = BcfFixture.Entries(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic("finding-7")));
            var second = BcfFixture.Entries(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic("finding-7")));

            Assert.Equal(first.Keys.OrderBy(k => k, StringComparer.Ordinal),
                         second.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void A_hash_id_becomes_a_schema_legal_guid()
        {
            // CamelWorks ids are content hashes; the schema pattern rejects them outright.
            var guid = BcfGuid.For("a3f19c22e4b18d70");

            Assert.True(BcfGuid.IsValid(guid));
            Assert.Equal(guid, BcfGuid.For("a3f19c22e4b18d70"));
            Assert.NotEqual(guid, BcfGuid.For("a3f19c22e4b18d71"));
        }

        [Fact]
        public void A_guid_is_lowercased_because_three_oh_permits_nothing_else()
        {
            // BCF 3.0's shared Guid type is [a-f0-9] only, where 2.1 allowed [a-fA-F0-9].
            var guid = BcfGuid.For("12345678-ABCD-1234-1234-123456789ABC");

            Assert.Equal("12345678-abcd-1234-1234-123456789abc", guid);
        }

        [Fact]
        public void A_viewpoint_and_a_comment_need_a_stable_id()
        {
            Assert.Throws<ArgumentException>(() => new BcfViewpoint(" "));
            Assert.Throws<ArgumentException>(() => new BcfComment(" ", BcfFixture.Raised, "jg", "x"));
        }

        // -----------------------------------------------------------------------------------
        // Honesty about what the file can and cannot do
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Elements_with_no_ifc_guid_are_counted_because_the_file_looks_fine_without_them()
        {
            // A BCF whose components carry only an authoring-tool id opens without complaint and
            // selects nothing.
            using (var buffer = new MemoryStream())
            {
                var result = BcfWriter.Write(buffer, new[] { BcfFixture.Topic() }, BcfVersion.V30);

                Assert.Equal(1, result.UnresolvableComponents);
                Assert.Contains(result.Notes, n => n.Contains("no IFC GUID"));
            }
        }

        [Fact]
        public void An_id_that_is_not_an_ifc_guid_is_never_written_as_one()
        {
            // Forging one produces a file that points at nothing, which is worse than admitting
            // there is no id.
            var component = new BcfComponent("2:14872", "2:14872", "CamelWorks");

            Assert.Null(component.IfcGuid);
            Assert.False(component.IsResolvable);
            Assert.DoesNotContain("IfcGuid=\"2:14872\"",
                BcfFixture.Viewpoint(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic())),
                StringComparison.Ordinal);
        }

        [Fact]
        public void A_control_character_in_a_property_does_not_make_the_file_unreadable()
        {
            var topic = new BcfTopic("x", "Duct\u0007clash", BcfFixture.Raised, "jg")
            {
                Description = "<script>alert(1)</script> & more",
            };

            var markup = BcfFixture.Markup(BcfFixture.Write(BcfVersion.V21, topic));

            Assert.DoesNotContain("\u0007", markup, StringComparison.Ordinal);
            Assert.Contains("Ductclash", markup, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", markup, StringComparison.Ordinal);
            Assert.Contains("&amp; more", markup, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unset_field_is_an_absent_element_not_an_empty_one()
        {
            // 3.0's strings may not be empty or blank, so an empty element fails validation — and
            // in either version an empty element says "known to be nothing", which is not what an
            // unset field means.
            var topic = new BcfTopic("x", "Bare", BcfFixture.Raised, "jg");
            var markup = BcfFixture.Markup(BcfFixture.Write(BcfVersion.V30, topic));

            Assert.DoesNotContain("<Description>", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("<Priority>", markup, StringComparison.Ordinal);
        }

        [Fact]
        public void A_topic_must_have_a_title_and_an_author()
        {
            Assert.Throws<ArgumentException>(() => new BcfTopic("x", " ", BcfFixture.Raised, "jg"));
            Assert.Throws<ArgumentException>(() => new BcfTopic("x", "t", BcfFixture.Raised, " "));
        }

        [Fact]
        public void A_colour_is_normalised_to_what_the_schema_pattern_accepts()
        {
            Assert.Equal("FF0000", new BcfColouring("#ff0000", Array.Empty<BcfComponent>()).Colour);
            Assert.Equal("FF0000AA", new BcfColouring("ff0000aa", Array.Empty<BcfComponent>()).Colour);
            Assert.Throws<ArgumentException>(() => new BcfColouring("red", Array.Empty<BcfComponent>()));
            Assert.Throws<ArgumentException>(() => new BcfColouring("#fff", Array.Empty<BcfComponent>()));
        }

        // -----------------------------------------------------------------------------------
        // The dump the schema validator checks. See CamelWorks/schemas/bcf/validate.sh.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Writes_the_files_the_schema_validator_checks()
        {
            // Unit tests cannot prove a BCF file is valid: it can be well-formed, round-trip
            // through our own reader, pass every assertion here, and still be rejected by the
            // receiving tool. So the real output is written out and xmllint checks it against the
            // published XSDs in CI. That is how the missing AspectRatio was found.
            var root = Path.Combine(AppContext.BaseDirectory, "bcf-validation");

            foreach (var version in new[] { BcfVersion.V21, BcfVersion.V30 })
            {
                var folder = Path.Combine(root, version == BcfVersion.V21 ? "v21" : "v30");
                Directory.CreateDirectory(folder);

                // An orthographic camera too: 3.0 requires an aspect ratio on both camera types,
                // and only one of them was wrong the first time.
                var ortho = BcfFixture.Topic("ortho");
                ortho.Viewpoints[0].Camera = BcfCamera.Orthographic(1, 2, 3, 1, 0, 0, 0, 0, 1, 25);

                // And a bare topic, so the minimal shape is checked as well as the full one.
                var bare = new BcfTopic("bare", "Bare", BcfFixture.Raised, "jg");
                bare.Viewpoints.Add(new BcfViewpoint("bare-vp"));

                var bytes = BcfFixture.Write(version, BcfFixture.Topic(), ortho, bare);

                var seen = 0;
                foreach (var entry in BcfFixture.Entries(bytes))
                {
                    var suffix =
                        entry.Key.EndsWith("markup.bcf", StringComparison.Ordinal) ? ".markup.xml"
                        : entry.Key.EndsWith(".bcfv", StringComparison.Ordinal) ? ".bcfv.xml"
                        : entry.Key == "bcf.version" ? ".version.xml"
                        : null;

                    if (suffix == null) continue;

                    var name = entry.Key.Replace('/', '_') + suffix;
                    File.WriteAllText(Path.Combine(folder, name), entry.Value, new UTF8Encoding(false));
                    seen++;
                }

                Assert.True(seen >= 4, "the dump should contain a version file and three topics' markup");
            }

            Assert.True(Directory.Exists(Path.Combine(root, "v21")));
            Assert.True(Directory.Exists(Path.Combine(root, "v30")));
        }
    }

    public class BcfReaderTests
    {
        private static BcfReadResult Read(byte[] bytes)
        {
            using (var buffer = new MemoryStream(bytes)) return BcfReader.Read(buffer);
        }

        [Fact]
        public void A_two_one_file_round_trips()
        {
            var result = Read(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()));

            Assert.Equal(BcfVersion.V21, result.Version);
            var topic = Assert.Single(result.Topics);
            Assert.Equal("Duct clashes beam", topic.Title);
            Assert.Equal("Clash", topic.TopicType);
            Assert.Equal("Structures", topic.AssignedTo);
            Assert.Equal(new[] { "L03", "MEP" }, topic.Labels);
            Assert.Single(topic.Comments);
            Assert.Single(topic.Viewpoints);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void A_three_oh_file_round_trips()
        {
            var result = Read(BcfFixture.Write(BcfVersion.V30, BcfFixture.Topic()));

            Assert.Equal(BcfVersion.V30, result.Version);
            var topic = Assert.Single(result.Topics);
            Assert.Equal(new[] { "L03", "MEP" }, topic.Labels);
            Assert.Equal("Agreed with structures", Assert.Single(topic.Comments).Text);
            Assert.Single(topic.Viewpoints);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void A_three_oh_file_carrying_two_ones_shape_is_still_read()
        {
            // Strict on write, lenient on read. The file came from somebody else's tool and is
            // routinely a little wrong; refusing it helps nobody.
            var bytes = Rezip(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()),
                              "bcf.version",
                              "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Version VersionId=\"3.0\" />\n");

            var result = Read(bytes);

            Assert.Equal(BcfVersion.V30, result.Version);
            var topic = Assert.Single(result.Topics);
            Assert.Single(topic.Comments);          // found under Markup, where 3.0 does not put them
            Assert.Equal(new[] { "L03", "MEP" }, topic.Labels);
        }

        [Fact]
        public void A_topic_missing_required_fields_is_reported_rather_than_dropped()
        {
            var bytes = Rezip(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()),
                              null,
                              "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
                              + "<Markup><Topic Guid=\"12345678-1234-1234-1234-123456789abc\">"
                              + "</Topic></Markup>\n");

            var result = Read(bytes);

            var topic = Assert.Single(result.Topics);
            Assert.Equal("Untitled topic", topic.Title);
            Assert.Contains(result.Warnings, w => w.Contains("no title"));
            Assert.Contains(result.Warnings, w => w.Contains("no creation author"));
        }

        [Fact]
        public void An_archive_with_no_topics_is_an_empty_result_not_an_error()
        {
            using (var buffer = new MemoryStream())
            {
                BcfWriter.Write(buffer, Array.Empty<BcfTopic>(), BcfVersion.V21);

                var result = Read(buffer.ToArray());

                Assert.Empty(result.Topics);
                Assert.Contains(result.Warnings, w => w.Contains("no markup.bcf"));
            }
        }

        [Fact]
        public void A_markup_file_that_is_not_XML_is_skipped_with_a_warning()
        {
            var bytes = Rezip(BcfFixture.Write(BcfVersion.V21, BcfFixture.Topic()), null, "this is not xml <<<");

            var result = Read(bytes);

            Assert.Empty(result.Topics);
            Assert.Contains(result.Warnings, w => w.Contains("not readable XML"));
        }

        // Rewrites one entry of an archive: "bcf.version" when named, otherwise the markup.
        private static byte[] Rezip(byte[] original, string? entryName, string content)
        {
            using (var source = new MemoryStream(original))
            using (var zip = new ZipArchive(source, ZipArchiveMode.Read))
            using (var target = new MemoryStream())
            {
                using (var output = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var replace = entryName != null
                            ? string.Equals(entry.FullName, entryName, StringComparison.Ordinal)
                            : entry.FullName.EndsWith("markup.bcf", StringComparison.Ordinal);

                        var created = output.CreateEntry(entry.FullName);

                        using (var to = created.Open())
                        {
                            if (replace)
                            {
                                var bytes = new UTF8Encoding(false).GetBytes(content);
                                to.Write(bytes, 0, bytes.Length);
                            }
                            else
                            {
                                using (var from = entry.Open()) from.CopyTo(to);
                            }
                        }
                    }
                }

                return target.ToArray();
            }
        }
    }
}
