using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace CamelWorks.Core.OpenBim
{
    /// <summary>What came back from a .bcfzip.</summary>
    public sealed class BcfReadResult
    {
        internal BcfReadResult(BcfVersion version, IReadOnlyList<BcfTopic> topics, IReadOnlyList<string> warnings)
        {
            Version = version; Topics = topics; Warnings = warnings;
        }

        /// <summary>The version the file declared, or the one that best fits what was in it.</summary>
        public BcfVersion Version { get; }

        /// <summary>The topics.</summary>
        public IReadOnlyList<BcfTopic> Topics { get; }

        /// <summary>
        /// What was wrong with the file, in words, having read it anyway.
        ///
        /// A BCF that another tool exported is a file nobody here can fix. Refusing it because one
        /// topic is missing a creation author helps nobody; saying which topic, and carrying on, is
        /// the behaviour that lets somebody actually receive the issues.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>The one-line readout.</summary>
        public override string ToString() =>
            "BCF " + (Version == BcfVersion.V21 ? "2.1" : "3.0") + " · "
            + Topics.Count.ToString("N0", CultureInfo.InvariantCulture) + " topics"
            + (Warnings.Count > 0
                ? " · " + Warnings.Count.ToString(CultureInfo.InvariantCulture) + " warnings"
                : string.Empty);
    }

    /// <summary>
    /// Reads a .bcfzip of either version.
    ///
    /// <b>Strict on write, lenient on read.</b> The file being read was produced by somebody else's
    /// tool, and it is routinely a little wrong: a 3.0 file with 2.1's flat comments, a missing
    /// creation author, a topic folder whose name does not match the GUID inside it. None of that
    /// is fixable from here, and refusing the file helps nobody — so the reader accepts either
    /// shape for every element that moved between versions, and reports what it had to forgive.
    /// </summary>
    public static class BcfReader
    {
        /// <summary>Read a .bcfzip.</summary>
        /// <param name="input">The archive. Left open.</param>
        public static BcfReadResult Read(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var warnings = new List<string>();
            var topics = new List<BcfTopic>();
            var version = BcfVersion.V21;

            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true))
            {
                var versionEntry = zip.Entries.FirstOrDefault(
                    e => string.Equals(e.FullName, "bcf.version", StringComparison.OrdinalIgnoreCase));

                if (versionEntry == null)
                {
                    warnings.Add("the archive has no bcf.version; it was read as BCF 2.1");
                }
                else
                {
                    var declared = Parse(versionEntry, warnings)?.Attribute("VersionId")?.Value;
                    if (declared != null && declared.StartsWith("3", StringComparison.Ordinal)) version = BcfVersion.V30;
                    else if (declared == null) warnings.Add("bcf.version declares no VersionId; it was read as BCF 2.1");
                }

                // Ordinal by path, so two reads of one archive produce topics in the same order
                // however the zip happens to be laid out.
                var markups = zip.Entries
                    .Where(e => e.FullName.EndsWith("markup.bcf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName, StringComparer.Ordinal)
                    .ToList();

                if (markups.Count == 0) warnings.Add("the archive contains no markup.bcf files, so it has no topics");

                foreach (var entry in markups)
                {
                    var root = Parse(entry, warnings);
                    if (root == null) continue;

                    var topic = ReadTopic(root, entry.FullName, warnings);
                    if (topic != null) topics.Add(topic);
                }
            }

            return new BcfReadResult(version, topics, warnings);
        }

        private static XElement? Parse(ZipArchiveEntry entry, List<string> warnings)
        {
            try
            {
                using (var stream = entry.Open())
                {
                    // No DTD processing, no external resolution: an imported BCF is untrusted
                    // input, and an XML parser that fetches what a document tells it to is a way
                    // to make a coordination file reach across the network.
                    var settings = new System.Xml.XmlReaderSettings
                    {
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                        XmlResolver = null,
                        IgnoreWhitespace = true,
                    };

                    using (var reader = System.Xml.XmlReader.Create(stream, settings))
                        return XDocument.Load(reader).Root;
                }
            }
            catch (System.Xml.XmlException e)
            {
                warnings.Add(entry.FullName + " is not readable XML and was skipped: " + e.Message);
                return null;
            }
            catch (InvalidDataException e)
            {
                warnings.Add(entry.FullName + " could not be decompressed and was skipped: " + e.Message);
                return null;
            }
        }

        private static BcfTopic? ReadTopic(XElement markup, string path, List<string> warnings)
        {
            var element = markup.Element("Topic");
            if (element == null)
            {
                warnings.Add(path + " has no Topic element and was skipped");
                return null;
            }

            var guid = element.Attribute("Guid")?.Value;
            var title = Text(element, "Title");

            if (string.IsNullOrWhiteSpace(title))
            {
                // The schema requires a title in both versions, and a topic without one cannot be
                // shown on a board at all. Named rather than dropped silently.
                warnings.Add(path + " has a topic with no title; it was given one from its folder");
                title = "Untitled topic";
            }

            var author = Text(element, "CreationAuthor");
            if (string.IsNullOrWhiteSpace(author))
            {
                warnings.Add(path + " has a topic with no creation author");
                author = "unknown";
            }

            var created = Date(Text(element, "CreationDate"));
            if (created == null)
            {
                warnings.Add(path + " has a topic with no readable creation date");
                created = DateTimeOffset.MinValue;
            }

            var topic = new BcfTopic(guid ?? path, title!, created.Value, author!)
            {
                TopicType = element.Attribute("TopicType")?.Value ?? "Issue",
                TopicStatus = element.Attribute("TopicStatus")?.Value ?? "Open",
                Priority = Text(element, "Priority"),
                Stage = Text(element, "Stage"),
                Description = Text(element, "Description"),
                AssignedTo = Text(element, "AssignedTo"),
                ModifiedAuthor = Text(element, "ModifiedAuthor"),
                ModifiedDate = Date(Text(element, "ModifiedDate")),
                DueDate = Date(Text(element, "DueDate")),
            };

            // 2.1 repeats <Labels>; 3.0 wraps <Label> in <Labels>. Both accepted, because plenty
            // of files in the wild carry one version's shape under the other's declaration.
            foreach (var label in Either(element, "Labels", "Labels", "Label"))
                topic.Labels.Add(label);

            foreach (var link in Either(element, "ReferenceLink", "ReferenceLinks", "ReferenceLink"))
                topic.ReferenceLinks.Add(link);

            foreach (var related in RelatedGuids(element))
                topic.RelatedTopics.Add(related);

            // 2.1 puts comments under Markup; 3.0 puts them under Topic. Look in both.
            foreach (var comment in Comments(markup).Concat(Comments(element)))
                topic.Comments.Add(comment);

            foreach (var viewpoint in Viewpoints(markup).Concat(Viewpoints(element)))
                topic.Viewpoints.Add(viewpoint);

            return topic;
        }

        private static IEnumerable<BcfComment> Comments(XElement parent)
        {
            var direct = parent.Elements("Comment");
            var wrapped = parent.Element("Comments")?.Elements("Comment") ?? Enumerable.Empty<XElement>();

            foreach (var element in direct.Concat(wrapped))
            {
                var guid = element.Attribute("Guid")?.Value;
                var date = Date(Text(element, "Date")) ?? DateTimeOffset.MinValue;
                var author = Text(element, "Author") ?? "unknown";

                yield return new BcfComment(guid ?? author + date.ToString("O", CultureInfo.InvariantCulture),
                                            date, author, Text(element, "Comment"))
                {
                    ViewpointGuid = element.Element("Viewpoint")?.Attribute("Guid")?.Value,
                    ModifiedDate = Date(Text(element, "ModifiedDate")),
                    ModifiedAuthor = Text(element, "ModifiedAuthor"),
                };
            }
        }

        private static IEnumerable<BcfViewpoint> Viewpoints(XElement parent)
        {
            // 2.1 names each entry "Viewpoints"; 3.0 wraps "ViewPoint" entries in "Viewpoints".
            // The names collide, so the wrapper is recognised by having element children rather
            // than by its name.
            var candidates = parent.Elements("Viewpoints")
                .Concat(parent.Elements("Viewpoints").Elements("ViewPoint"))
                .Concat(parent.Elements("ViewPoint"));

            foreach (var element in candidates)
            {
                var guid = element.Attribute("Guid")?.Value;
                if (guid == null) continue;

                yield return new BcfViewpoint(guid);
            }
        }

        private static IEnumerable<string> RelatedGuids(XElement topic)
        {
            var direct = topic.Elements("RelatedTopic");
            var wrapped = topic.Element("RelatedTopics")?.Elements("RelatedTopic") ?? Enumerable.Empty<XElement>();

            return direct.Concat(wrapped)
                .Select(e => e.Attribute("Guid")?.Value)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g!);
        }

        private static IEnumerable<string> Either(XElement parent, string flatName, string wrapperName, string innerName)
        {
            foreach (var element in parent.Elements(flatName))
            {
                // A 2.1 flat element holds text; a 3.0 wrapper holds children under the same name.
                if (element.HasElements) continue;
                if (!string.IsNullOrWhiteSpace(element.Value)) yield return element.Value.Trim();
            }

            var wrapper = parent.Element(wrapperName);
            if (wrapper == null) yield break;

            foreach (var element in wrapper.Elements(innerName))
                if (!string.IsNullOrWhiteSpace(element.Value)) yield return element.Value.Trim();
        }

        private static string? Text(XElement parent, string name)
        {
            var value = parent.Element(name)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        private static DateTimeOffset? Date(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                ? value
                : (DateTimeOffset?)null;
        }
    }
}
