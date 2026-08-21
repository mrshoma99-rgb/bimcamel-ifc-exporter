using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace CamelWorks.Core.OpenBim
{
    /// <summary>What the export actually managed to say.</summary>
    public sealed class BcfWriteResult
    {
        internal BcfWriteResult(BcfVersion version, int topics, int comments, int viewpoints,
                                int snapshots, int unresolvableComponents, IReadOnlyList<string> notes)
        {
            Version = version; Topics = topics; Comments = comments; Viewpoints = viewpoints;
            Snapshots = snapshots; UnresolvableComponents = unresolvableComponents; Notes = notes;
        }

        /// <summary>Which schema was written.</summary>
        public BcfVersion Version { get; }

        /// <summary>Topics written.</summary>
        public int Topics { get; }

        /// <summary>Comments written.</summary>
        public int Comments { get; }

        /// <summary>Viewpoints written.</summary>
        public int Viewpoints { get; }

        /// <summary>Snapshots embedded.</summary>
        public int Snapshots { get; }

        /// <summary>
        /// Components written without an IFC GUID.
        ///
        /// The number that decides whether the file is any use. A BCF whose components carry only
        /// an authoring-tool id opens without complaint in the receiving tool and selects nothing,
        /// and the person who sent it finds out a week later.
        /// </summary>
        public int UnresolvableComponents { get; }

        /// <summary>
        /// Everything the chosen schema could not carry exactly, in words.
        ///
        /// BCF loses things, and which things depends on the version. Saying so at export time is
        /// the difference between a known limitation and a support call.
        /// </summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>The one-line readout.</summary>
        public override string ToString()
        {
            var s = "BCF " + (Version == BcfVersion.V21 ? "2.1" : "3.0") + " · "
                  + Topics.ToString("N0", CultureInfo.InvariantCulture) + " topics · "
                  + Viewpoints.ToString("N0", CultureInfo.InvariantCulture) + " viewpoints";

            if (Snapshots > 0) s += " · " + Snapshots.ToString("N0", CultureInfo.InvariantCulture) + " snapshots";
            if (Notes.Count > 0) s += " · " + Notes.Count.ToString(CultureInfo.InvariantCulture) + " notes";
            return s;
        }
    }

    /// <summary>
    /// Writes a .bcfzip, in either schema version.
    ///
    /// The two versions are not a formatting detail, and the differences are the kind that produce
    /// a file the other tool silently ignores half of:
    ///
    /// <list type="bullet">
    /// <item>2.1 puts comments and viewpoints directly under Markup; 3.0 moves both inside Topic
    /// and wraps every repeated element in a collection element.</item>
    /// <item>2.1 spells the attribute <c>isExternal</c>; 3.0 spells it <c>IsExternal</c>.</item>
    /// <item>2.1 makes TopicType and TopicStatus optional; 3.0 requires both.</item>
    /// <item>2.1 restricts a perspective field of view to 45–60 degrees. 3.0 allows anything under
    /// 180. Host cameras routinely sit outside 45–60, so writing 2.1 means clamping — a real loss,
    /// reported rather than hidden.</item>
    /// <item>3.0 requires a camera on every viewpoint; 2.1 does not.</item>
    /// <item>3.0's strings may not be empty, so an absent value must be an absent element rather
    /// than an empty one.</item>
    /// </list>
    ///
    /// Both are written from one model, and the model is the neutral one — which is what stops the
    /// second version from being a copy of the first with edits.
    /// </summary>
    public static class BcfWriter
    {
        /// <summary>2.1's field of view floor.</summary>
        public const double MinFieldOfView21 = 45;

        /// <summary>2.1's field of view ceiling.</summary>
        public const double MaxFieldOfView21 = 60;

        /// <summary>Write a .bcfzip.</summary>
        /// <param name="output">Where to write. Left open.</param>
        /// <param name="topics">The topics.</param>
        /// <param name="version">Which schema.</param>
        /// <param name="project">Project details, written to project.bcfp when given.</param>
        public static BcfWriteResult Write(Stream output, IReadOnlyList<BcfTopic> topics,
                                           BcfVersion version, BcfProject? project = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (topics == null) throw new ArgumentNullException(nameof(topics));

            var notes = new List<string>();
            var comments = 0;
            var viewpoints = 0;
            var snapshots = 0;
            var unresolvable = 0;
            var clamped = 0;
            var cameraless = 0;

            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, "bcf.version", Version(version));

                if (project != null) Add(zip, "project.bcfp", Project(project, version));

                foreach (var topic in topics.Where(t => t != null))
                {
                    var folder = topic.Guid + "/";
                    Add(zip, folder + "markup.bcf", Markup(topic, version));

                    comments += topic.Comments.Count;

                    foreach (var viewpoint in topic.Viewpoints.Where(v => v != null))
                    {
                        viewpoints++;

                        if (viewpoint.Camera == null && version == BcfVersion.V30) cameraless++;

                        if (version == BcfVersion.V21 && viewpoint.Camera != null
                            && viewpoint.Camera.IsPerspective
                            && OutsideRange(viewpoint.Camera.FieldOfViewDegrees)) clamped++;

                        unresolvable += viewpoint.Selection.Count(c => !c.IsResolvable)
                                      + viewpoint.VisibilityExceptions.Count(c => !c.IsResolvable)
                                      + viewpoint.Colouring.Sum(c => c.Components.Count(x => !x.IsResolvable));

                        Add(zip, folder + viewpoint.Guid + ".bcfv", Visualisation(viewpoint, version));

                        if (viewpoint.Snapshot != null && viewpoint.Snapshot.Length > 0)
                        {
                            snapshots++;
                            AddBytes(zip, folder + viewpoint.Guid + ".png", viewpoint.Snapshot);
                        }
                    }
                }
            }

            if (clamped > 0)
                notes.Add(clamped.ToString(CultureInfo.InvariantCulture)
                          + " perspective viewpoints had their field of view clamped to the 45-60 degree range BCF 2.1 permits; "
                          + "the framing will differ slightly from the model. BCF 3.0 carries them unchanged.");

            if (cameraless > 0)
                notes.Add(cameraless.ToString(CultureInfo.InvariantCulture)
                          + " viewpoints had no camera. BCF 3.0 requires one, so a default looking down the model's X axis was written.");

            if (version == BcfVersion.V21 && viewpoints > 0)
                notes.Add("BCF 2.1 has no aspect ratio, so viewpoints will be re-framed to whatever "
                          + "shape the receiving viewport has. BCF 3.0 carries it.");

            if (unresolvable > 0)
                notes.Add(unresolvable.ToString(CultureInfo.InvariantCulture)
                          + " referenced elements have no IFC GUID, so the receiving tool will not be able to select them. "
                          + "Elements from IFC sources carry one; elements from DWG and native formats generally do not.");

            return new BcfWriteResult(version, topics.Count(t => t != null), comments, viewpoints,
                                      snapshots, unresolvable, notes);
        }

        private static bool OutsideRange(double fieldOfView) =>
            fieldOfView < MinFieldOfView21 || fieldOfView > MaxFieldOfView21;

        // -------------------------------------------------------------------------------------

        private static string Version(BcfVersion version)
        {
            var s = new StringBuilder();
            s.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");

            if (version == BcfVersion.V21)
            {
                // 2.1 carries a DetailedVersion element. 3.0 removed it, and a 3.0 reader that
                // validates strictly rejects the file if it is still there.
                s.Append("<Version VersionId=\"2.1\">\n");
                s.Append("  <DetailedVersion>2.1</DetailedVersion>\n");
                s.Append("</Version>\n");
            }
            else
            {
                s.Append("<Version VersionId=\"3.0\" />\n");
            }

            return s.ToString();
        }

        private static string Project(BcfProject project, BcfVersion version)
        {
            var s = new StringBuilder();
            s.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            s.Append("<ProjectExtension>\n");
            s.Append("  <Project ProjectId=\"").Append(Xml.Escape(project.ProjectId)).Append("\">\n");

            if (!string.IsNullOrWhiteSpace(project.Name))
                s.Append("    <Name>").Append(Xml.Escape(project.Name)).Append("</Name>\n");

            s.Append("  </Project>\n");

            // 2.1's ProjectExtension carries the name of the extension schema file. 3.0 dropped it
            // in favour of extensions.xml, which is optional, so nothing is written there.
            if (version == BcfVersion.V21) s.Append("  <ExtensionSchema />\n");

            s.Append("</ProjectExtension>\n");
            return s.ToString();
        }

        private static string Markup(BcfTopic topic, BcfVersion version)
        {
            var s = new StringBuilder();
            s.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            s.Append("<Markup>\n");

            s.Append("  <Topic Guid=\"").Append(Xml.Escape(topic.Guid)).Append('"');
            s.Append(" TopicType=\"").Append(Xml.Escape(Fallback(topic.TopicType, "Issue"))).Append('"');
            s.Append(" TopicStatus=\"").Append(Xml.Escape(Fallback(topic.TopicStatus, "Open"))).Append("\">\n");

            // The element order below is an xs:sequence in both schemas. Getting it wrong produces
            // a file that reads fine in tolerant tools and fails validation in strict ones, which
            // is the worst of both.
            if (version == BcfVersion.V30)
            {
                if (topic.ReferenceLinks.Count > 0)
                {
                    s.Append("    <ReferenceLinks>\n");
                    foreach (var link in NonEmpty(topic.ReferenceLinks))
                        s.Append("      <ReferenceLink>").Append(Xml.Escape(link)).Append("</ReferenceLink>\n");
                    s.Append("    </ReferenceLinks>\n");
                }
            }
            else
            {
                foreach (var link in NonEmpty(topic.ReferenceLinks))
                    s.Append("    <ReferenceLink>").Append(Xml.Escape(link)).Append("</ReferenceLink>\n");
            }

            s.Append("    <Title>").Append(Xml.Escape(topic.Title)).Append("</Title>\n");
            Optional(s, "    ", "Priority", topic.Priority);

            if (version == BcfVersion.V30)
            {
                if (topic.Labels.Count > 0)
                {
                    s.Append("    <Labels>\n");
                    foreach (var label in NonEmpty(topic.Labels))
                        s.Append("      <Label>").Append(Xml.Escape(label)).Append("</Label>\n");
                    s.Append("    </Labels>\n");
                }
            }
            else
            {
                foreach (var label in NonEmpty(topic.Labels))
                    s.Append("    <Labels>").Append(Xml.Escape(label)).Append("</Labels>\n");
            }

            s.Append("    <CreationDate>").Append(Xml.Date(topic.CreationDate)).Append("</CreationDate>\n");
            s.Append("    <CreationAuthor>").Append(Xml.Escape(topic.CreationAuthor)).Append("</CreationAuthor>\n");

            if (topic.ModifiedDate != null)
                s.Append("    <ModifiedDate>").Append(Xml.Date(topic.ModifiedDate.Value)).Append("</ModifiedDate>\n");
            Optional(s, "    ", "ModifiedAuthor", topic.ModifiedAuthor);

            if (topic.DueDate != null)
                s.Append("    <DueDate>").Append(Xml.Date(topic.DueDate.Value)).Append("</DueDate>\n");

            Optional(s, "    ", "AssignedTo", topic.AssignedTo);
            Optional(s, "    ", "Stage", topic.Stage);
            Optional(s, "    ", "Description", topic.Description);

            if (version == BcfVersion.V30)
            {
                if (topic.RelatedTopics.Count > 0)
                {
                    s.Append("    <RelatedTopics>\n");
                    foreach (var related in NonEmpty(topic.RelatedTopics))
                        s.Append("      <RelatedTopic Guid=\"").Append(Xml.Escape(BcfGuid.For(related))).Append("\" />\n");
                    s.Append("    </RelatedTopics>\n");
                }

                // 3.0 keeps comments and viewpoints inside the topic.
                if (topic.Comments.Count > 0)
                {
                    s.Append("    <Comments>\n");
                    foreach (var comment in topic.Comments.Where(c => c != null))
                        Comment(s, "      ", comment);
                    s.Append("    </Comments>\n");
                }

                if (topic.Viewpoints.Count > 0)
                {
                    s.Append("    <Viewpoints>\n");
                    foreach (var viewpoint in topic.Viewpoints.Where(v => v != null))
                        ViewPointEntry(s, "      ", viewpoint, "ViewPoint");
                    s.Append("    </Viewpoints>\n");
                }

                s.Append("  </Topic>\n");
            }
            else
            {
                foreach (var related in NonEmpty(topic.RelatedTopics))
                    s.Append("    <RelatedTopic Guid=\"").Append(Xml.Escape(BcfGuid.For(related))).Append("\" />\n");

                s.Append("  </Topic>\n");

                // 2.1 keeps them as siblings of Topic, and names each viewpoint element
                // "Viewpoints" even though each one is a single viewpoint.
                foreach (var comment in topic.Comments.Where(c => c != null))
                    Comment(s, "  ", comment);

                foreach (var viewpoint in topic.Viewpoints.Where(v => v != null))
                    ViewPointEntry(s, "  ", viewpoint, "Viewpoints");
            }

            s.Append("</Markup>\n");
            return s.ToString();
        }

        private static void Comment(StringBuilder s, string indent, BcfComment comment)
        {
            s.Append(indent).Append("<Comment Guid=\"").Append(Xml.Escape(comment.Guid)).Append("\">\n");
            s.Append(indent).Append("  <Date>").Append(Xml.Date(comment.Date)).Append("</Date>\n");
            s.Append(indent).Append("  <Author>").Append(Xml.Escape(comment.Author)).Append("</Author>\n");

            Optional(s, indent + "  ", "Comment", comment.Text);

            if (!string.IsNullOrWhiteSpace(comment.ViewpointGuid))
                s.Append(indent).Append("  <Viewpoint Guid=\"")
                 .Append(Xml.Escape(BcfGuid.For(comment.ViewpointGuid))).Append("\" />\n");

            if (comment.ModifiedDate != null)
                s.Append(indent).Append("  <ModifiedDate>").Append(Xml.Date(comment.ModifiedDate.Value))
                 .Append("</ModifiedDate>\n");

            Optional(s, indent + "  ", "ModifiedAuthor", comment.ModifiedAuthor);

            s.Append(indent).Append("</Comment>\n");
        }

        private static void ViewPointEntry(StringBuilder s, string indent, BcfViewpoint viewpoint, string elementName)
        {
            s.Append(indent).Append('<').Append(elementName)
             .Append(" Guid=\"").Append(Xml.Escape(viewpoint.Guid)).Append("\">\n");
            s.Append(indent).Append("  <Viewpoint>").Append(Xml.Escape(viewpoint.Guid)).Append(".bcfv</Viewpoint>\n");

            if (viewpoint.Snapshot != null && viewpoint.Snapshot.Length > 0)
                s.Append(indent).Append("  <Snapshot>").Append(Xml.Escape(viewpoint.Guid)).Append(".png</Snapshot>\n");

            s.Append(indent).Append("</").Append(elementName).Append(">\n");
        }

        private static string Visualisation(BcfViewpoint viewpoint, BcfVersion version)
        {
            var s = new StringBuilder();
            s.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            s.Append("<VisualizationInfo Guid=\"").Append(Xml.Escape(viewpoint.Guid)).Append("\">\n");

            Components(s, viewpoint, version);
            Camera(s, viewpoint, version);

            if (viewpoint.ClippingPlanes.Count > 0)
            {
                s.Append("  <ClippingPlanes>\n");
                foreach (var plane in viewpoint.ClippingPlanes)
                {
                    s.Append("    <ClippingPlane>\n");
                    Vector(s, "      ", "Location", plane.X, plane.Y, plane.Z);
                    Vector(s, "      ", "Direction", plane.DirectionX, plane.DirectionY, plane.DirectionZ);
                    s.Append("    </ClippingPlane>\n");
                }

                s.Append("  </ClippingPlanes>\n");
            }

            s.Append("</VisualizationInfo>\n");
            return s.ToString();
        }

        private static void Components(StringBuilder s, BcfViewpoint viewpoint, BcfVersion version)
        {
            var hasSelection = viewpoint.Selection.Count > 0;
            var hasExceptions = viewpoint.VisibilityExceptions.Count > 0;
            var hasColouring = viewpoint.Colouring.Count > 0;
            var hints = viewpoint.SpacesVisible || viewpoint.SpaceBoundariesVisible || viewpoint.OpeningsVisible;

            if (!hasSelection && !hasExceptions && !hasColouring && !hints && viewpoint.DefaultVisibility) return;

            s.Append("  <Components>\n");

            // 2.1 puts ViewSetupHints first, inside Components. 3.0 moved it inside Visibility.
            if (version == BcfVersion.V21 && hints) Hints(s, "    ", viewpoint);

            if (hasSelection)
            {
                s.Append("    <Selection>\n");
                foreach (var component in viewpoint.Selection) Component(s, "      ", component);
                s.Append("    </Selection>\n");
            }

            // 2.1 requires Visibility whenever Components is present; 3.0 makes it optional. It is
            // always written, which satisfies both and states the polarity explicitly rather than
            // leaving a reader to assume a default.
            s.Append("    <Visibility DefaultVisibility=\"").Append(Xml.Bool(viewpoint.DefaultVisibility)).Append('"');

            if (version == BcfVersion.V30 && !hints && !hasExceptions)
            {
                s.Append(" />\n");
            }
            else
            {
                s.Append(">\n");
                if (version == BcfVersion.V30 && hints) Hints(s, "      ", viewpoint);

                if (hasExceptions)
                {
                    s.Append("      <Exceptions>\n");
                    foreach (var component in viewpoint.VisibilityExceptions) Component(s, "        ", component);
                    s.Append("      </Exceptions>\n");
                }

                s.Append("    </Visibility>\n");
            }

            if (hasColouring)
            {
                s.Append("    <Coloring>\n");
                foreach (var colouring in viewpoint.Colouring)
                {
                    s.Append("      <Color Color=\"").Append(Xml.Escape(colouring.Colour)).Append("\">\n");

                    // 3.0 wraps the components of a colour in their own element; 2.1 lists them
                    // directly.
                    if (version == BcfVersion.V30)
                    {
                        s.Append("        <Components>\n");
                        foreach (var component in colouring.Components) Component(s, "          ", component);
                        s.Append("        </Components>\n");
                    }
                    else
                    {
                        foreach (var component in colouring.Components) Component(s, "        ", component);
                    }

                    s.Append("      </Color>\n");
                }

                s.Append("    </Coloring>\n");
            }

            s.Append("  </Components>\n");
        }

        private static void Hints(StringBuilder s, string indent, BcfViewpoint viewpoint)
        {
            s.Append(indent).Append("<ViewSetupHints SpacesVisible=\"").Append(Xml.Bool(viewpoint.SpacesVisible))
             .Append("\" SpaceBoundariesVisible=\"").Append(Xml.Bool(viewpoint.SpaceBoundariesVisible))
             .Append("\" OpeningsVisible=\"").Append(Xml.Bool(viewpoint.OpeningsVisible)).Append("\" />\n");
        }

        private static void Component(StringBuilder s, string indent, BcfComponent component)
        {
            s.Append(indent).Append("<Component");
            if (component.IfcGuid != null) s.Append(" IfcGuid=\"").Append(Xml.Escape(component.IfcGuid)).Append('"');

            var hasBody = !string.IsNullOrWhiteSpace(component.OriginatingSystem)
                          || !string.IsNullOrWhiteSpace(component.AuthoringToolId);

            if (!hasBody) { s.Append(" />\n"); return; }

            s.Append(">\n");
            Optional(s, indent + "  ", "OriginatingSystem", component.OriginatingSystem);
            Optional(s, indent + "  ", "AuthoringToolId", component.AuthoringToolId);
            s.Append(indent).Append("</Component>\n");
        }

        private static void Camera(StringBuilder s, BcfViewpoint viewpoint, BcfVersion version)
        {
            var camera = viewpoint.Camera;

            // 3.0 requires exactly one camera. Rather than emit an invalid file, a plain default
            // is written and the caller is told, in the result notes, that it happened.
            if (camera == null)
            {
                if (version != BcfVersion.V30) return;
                camera = BcfCamera.Perspective(0, 0, 0, 1, 0, 0, 0, 0, 1, 60);
            }

            if (camera.IsPerspective)
            {
                var fov = camera.FieldOfViewDegrees;

                // 2.1 restricts this to 45-60. Host cameras routinely sit outside it, and a value
                // outside the range fails validation, so it is clamped — and the write result says
                // how many times, because the framing really does change.
                if (version == BcfVersion.V21)
                    fov = Math.Max(MinFieldOfView21, Math.Min(MaxFieldOfView21, fov));
                else if (fov <= 0 || fov >= 180)
                    fov = 60;

                s.Append("  <PerspectiveCamera>\n");
                Vector(s, "    ", "CameraViewPoint", camera.X, camera.Y, camera.Z);
                Vector(s, "    ", "CameraDirection", camera.DirectionX, camera.DirectionY, camera.DirectionZ);
                Vector(s, "    ", "CameraUpVector", camera.UpX, camera.UpY, camera.UpZ);
                s.Append("    <FieldOfView>").Append(Xml.Number(fov)).Append("</FieldOfView>\n");

                // 3.0 requires an aspect ratio on both camera types and 2.1 has no element for it,
                // so writing 2.1 drops it. Found by validating the output against the published
                // schema rather than by reading it, which is why the validation is in the repo.
                if (version == BcfVersion.V30) AspectRatio(s, camera);

                s.Append("  </PerspectiveCamera>\n");
            }
            else
            {
                var scale = camera.ViewToWorldScale > 0 ? camera.ViewToWorldScale : 1;

                s.Append("  <OrthogonalCamera>\n");
                Vector(s, "    ", "CameraViewPoint", camera.X, camera.Y, camera.Z);
                Vector(s, "    ", "CameraDirection", camera.DirectionX, camera.DirectionY, camera.DirectionZ);
                Vector(s, "    ", "CameraUpVector", camera.UpX, camera.UpY, camera.UpZ);
                s.Append("    <ViewToWorldScale>").Append(Xml.Number(scale)).Append("</ViewToWorldScale>\n");
                if (version == BcfVersion.V30) AspectRatio(s, camera);
                s.Append("  </OrthogonalCamera>\n");
            }
        }

        // The schema types it as a PositiveDouble, so zero is not a legal value to fall back to.
        private static void AspectRatio(StringBuilder s, BcfCamera camera)
        {
            var ratio = camera.AspectRatio > 0 ? camera.AspectRatio : BcfCamera.DefaultAspectRatio;
            s.Append("    <AspectRatio>").Append(Xml.Number(ratio)).Append("</AspectRatio>\n");
        }

        private static void Vector(StringBuilder s, string indent, string name, double x, double y, double z)
        {
            s.Append(indent).Append('<').Append(name).Append(">\n");
            s.Append(indent).Append("  <X>").Append(Xml.Number(x)).Append("</X>\n");
            s.Append(indent).Append("  <Y>").Append(Xml.Number(y)).Append("</Y>\n");
            s.Append(indent).Append("  <Z>").Append(Xml.Number(z)).Append("</Z>\n");
            s.Append(indent).Append("</").Append(name).Append(">\n");
        }

        // 3.0's strings may not be empty or blank, so an absent value is an absent element. 2.1 is
        // written the same way: an empty element says "known to be nothing", which is not what an
        // unset field means.
        private static void Optional(StringBuilder s, string indent, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            s.Append(indent).Append('<').Append(name).Append('>').Append(Xml.Escape(value))
             .Append("</").Append(name).Append(">\n");
        }

        private static IEnumerable<string> NonEmpty(IEnumerable<string> values) =>
            values.Where(v => !string.IsNullOrWhiteSpace(v));

        private static string Fallback(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value!;

        private static void Add(ZipArchive zip, string path, string content)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            {
                // No BOM: the declaration says UTF-8, and a BOM in front of it trips more than one
                // BCF reader that looks for "<?xml" at byte zero.
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static void AddBytes(ZipArchive zip, string path, byte[] content)
        {
            // Already-compressed data. Storing it saves the time deflate would spend not shrinking
            // a PNG, and a topic with fifty snapshots is a normal topic.
            var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
            using (var stream = entry.Open()) stream.Write(content, 0, content.Length);
        }
    }
}
