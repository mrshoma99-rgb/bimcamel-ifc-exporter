using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.OpenBim
{
    /// <summary>Which BCF schema to read or write.</summary>
    public enum BcfVersion
    {
        /// <summary>BCF 2.1 — still the version most tools accept without argument.</summary>
        V21 = 21,

        /// <summary>BCF 3.0.</summary>
        V30 = 30,
    }

    /// <summary>
    /// BCF's GUID rules, and a deterministic way to satisfy them.
    ///
    /// The schema pattern is a real constraint, not decoration: a topic whose Guid is not
    /// <c>8-4-4-4-12</c> hex is rejected outright by strict readers. CamelWorks finding ids are
    /// content hashes, not GUIDs, so they have to be converted — and the conversion has to be
    /// deterministic, because exporting the same finding twice must produce the same topic. A
    /// random GUID would make every export look like a new issue to the receiving tool, which is
    /// precisely how BCF round-trips turn into duplicate registers.
    /// </summary>
    public static class BcfGuid
    {
        /// <summary>True when the text already satisfies the schema's GUID pattern.</summary>
        public static bool IsValid(string? text)
        {
            if (text == null || text.Length != 36) return false;

            for (var i = 0; i < 36; i++)
            {
                var c = text[i];
                if (i == 8 || i == 13 || i == 18 || i == 23)
                {
                    if (c != '-') return false;
                    continue;
                }

                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }

            return true;
        }

        /// <summary>
        /// The GUID for an id: itself when it is already one, otherwise one derived from it by
        /// hashing. Same id in, same GUID out, on any machine, in any build.
        /// </summary>
        public static string For(string? id)
        {
            if (IsValid(id)) return id!.ToLowerInvariant();

            var hex = Hash.Of(32, "bcf-topic", id);
            return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4)
                 + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
        }
    }

    /// <summary>One element referenced by a viewpoint.</summary>
    public sealed class BcfComponent
    {
        /// <summary>Create a component.</summary>
        /// <param name="ifcGuid">The IFC GUID, when the element has one. 22 characters.</param>
        /// <param name="authoringToolId">The host's own id for the element, when there is no IFC GUID.</param>
        /// <param name="originatingSystem">Which tool the id belongs to.</param>
        public BcfComponent(string? ifcGuid, string? authoringToolId = null, string? originatingSystem = null)
        {
            IfcGuid = IsIfcGuid(ifcGuid) ? ifcGuid : null;
            AuthoringToolId = authoringToolId;
            OriginatingSystem = originatingSystem;
        }

        /// <summary>The IFC GUID, or null. Never a forged one — see <see cref="IsResolvable"/>.</summary>
        public string? IfcGuid { get; }

        /// <summary>The host's own id, for elements with no IFC GUID.</summary>
        public string? AuthoringToolId { get; }

        /// <summary>Which tool <see cref="AuthoringToolId"/> belongs to.</summary>
        public string? OriginatingSystem { get; }

        /// <summary>
        /// True when a receiving tool can actually find this element.
        ///
        /// Without an IFC GUID it cannot, whatever else the component carries — an authoring-tool
        /// id means something only to the tool that issued it. Federations assembled from DWG, RVT
        /// and NWC produce plenty of these, and a BCF full of them opens without complaint and
        /// selects nothing. The writer counts them rather than letting that be a surprise.
        /// </summary>
        public bool IsResolvable => IfcGuid != null;

        private static bool IsIfcGuid(string? text)
        {
            if (text == null || text.Length != 22) return false;

            foreach (var c in text)
            {
                var ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                         || c == '_' || c == '$';
                if (!ok) return false;
            }

            return true;
        }
    }

    /// <summary>A colour applied to some components in a viewpoint.</summary>
    public sealed class BcfColouring
    {
        /// <summary>Create a colouring.</summary>
        /// <param name="colour">Six or eight uppercase hex digits, RRGGBB or RRGGBBAA.</param>
        /// <param name="components">The components it applies to.</param>
        public BcfColouring(string colour, IReadOnlyList<BcfComponent> components)
        {
            Colour = Normalise(colour);
            Components = components ?? throw new ArgumentNullException(nameof(components));
        }

        /// <summary>The colour, as the schema wants it.</summary>
        public string Colour { get; }

        /// <summary>What it applies to.</summary>
        public IReadOnlyList<BcfComponent> Components { get; }

        private static string Normalise(string colour)
        {
            if (colour == null) throw new ArgumentNullException(nameof(colour));

            var s = colour.Trim();
            if (s.Length > 0 && s[0] == '#') s = s.Substring(1);
            s = s.ToUpperInvariant();

            if (s.Length != 6 && s.Length != 8)
                throw new ArgumentException("a BCF colour is six or eight hex digits", nameof(colour));

            foreach (var c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                    throw new ArgumentException("a BCF colour is hexadecimal", nameof(colour));

            return s;
        }
    }

    /// <summary>A section plane in a viewpoint.</summary>
    public sealed class BcfClippingPlane
    {
        /// <summary>Create a plane.</summary>
        public BcfClippingPlane(double x, double y, double z, double dx, double dy, double dz)
        {
            X = x; Y = y; Z = z; DirectionX = dx; DirectionY = dy; DirectionZ = dz;
        }

        /// <summary>A point on the plane.</summary>
        public double X { get; }

        /// <summary>A point on the plane.</summary>
        public double Y { get; }

        /// <summary>A point on the plane.</summary>
        public double Z { get; }

        /// <summary>The plane normal.</summary>
        public double DirectionX { get; }

        /// <summary>The plane normal.</summary>
        public double DirectionY { get; }

        /// <summary>The plane normal.</summary>
        public double DirectionZ { get; }
    }

    /// <summary>
    /// The camera a viewpoint restores.
    ///
    /// <b>Coordinates are the model's own, Z-up.</b> BCF is Z-up and so is the host, so there is
    /// no transform here and there must not be one. A viewer that renders Y-up converts on the way
    /// in; doing it here would rotate every exported viewpoint ninety degrees, which is easy to do
    /// by copying a viewer's import code and impossible to notice until somebody opens the file.
    /// </summary>
    public sealed class BcfCamera
    {
        private BcfCamera(bool perspective, double px, double py, double pz,
                          double dx, double dy, double dz, double ux, double uy, double uz,
                          double fieldOfView, double viewToWorldScale, double aspectRatio)
        {
            IsPerspective = perspective;
            X = px; Y = py; Z = pz;
            DirectionX = dx; DirectionY = dy; DirectionZ = dz;
            UpX = ux; UpY = uy; UpZ = uz;
            FieldOfViewDegrees = fieldOfView;
            ViewToWorldScale = viewToWorldScale;
            AspectRatio = aspectRatio > 0 ? aspectRatio : DefaultAspectRatio;
        }

        /// <summary>The aspect ratio assumed when the caller does not know the viewport's.</summary>
        public const double DefaultAspectRatio = 16.0 / 9.0;

        /// <summary>True for a perspective camera, false for an orthographic one.</summary>
        public bool IsPerspective { get; }

        /// <summary>Camera position.</summary>
        public double X { get; }

        /// <summary>Camera position.</summary>
        public double Y { get; }

        /// <summary>Camera position.</summary>
        public double Z { get; }

        /// <summary>View direction.</summary>
        public double DirectionX { get; }

        /// <summary>View direction.</summary>
        public double DirectionY { get; }

        /// <summary>View direction.</summary>
        public double DirectionZ { get; }

        /// <summary>Up vector.</summary>
        public double UpX { get; }

        /// <summary>Up vector.</summary>
        public double UpY { get; }

        /// <summary>Up vector.</summary>
        public double UpZ { get; }

        /// <summary>Vertical field of view in degrees, for a perspective camera.</summary>
        public double FieldOfViewDegrees { get; }

        /// <summary>The view's visible vertical size in metres, for an orthographic camera.</summary>
        public double ViewToWorldScale { get; }

        /// <summary>
        /// The viewport's width divided by its height.
        ///
        /// BCF 3.0 requires it on both camera types and 2.1 has no concept of it at all, so a
        /// viewpoint written as 2.1 loses it and is re-framed by whatever aspect the receiving
        /// viewport happens to have. That is a real difference between the two exports, and the
        /// write result says so rather than leaving somebody to wonder why the view moved.
        /// </summary>
        public double AspectRatio { get; }

        /// <summary>A perspective camera.</summary>
        public static BcfCamera Perspective(double x, double y, double z, double dx, double dy, double dz,
                                            double ux, double uy, double uz, double fieldOfViewDegrees,
                                            double aspectRatio = DefaultAspectRatio) =>
            new BcfCamera(true, x, y, z, dx, dy, dz, ux, uy, uz, fieldOfViewDegrees, 0, aspectRatio);

        /// <summary>An orthographic camera.</summary>
        public static BcfCamera Orthographic(double x, double y, double z, double dx, double dy, double dz,
                                             double ux, double uy, double uz, double viewToWorldScale,
                                             double aspectRatio = DefaultAspectRatio) =>
            new BcfCamera(false, x, y, z, dx, dy, dz, ux, uy, uz, 0, viewToWorldScale, aspectRatio);
    }

    /// <summary>One saved view attached to a topic.</summary>
    public sealed class BcfViewpoint
    {
        /// <summary>Create a viewpoint.</summary>
        /// <param name="id">
        /// Any stable id, converted to a schema-legal GUID deterministically. Required, with no
        /// random default: a viewpoint that gets a fresh GUID on every export is a viewpoint the
        /// receiving tool files as new every time, which is the duplicate-register problem the
        /// derivation exists to avoid.
        /// </param>
        public BcfViewpoint(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a viewpoint needs a stable id", nameof(id));
            Guid = BcfGuid.For(id);
        }

        /// <summary>Its GUID.</summary>
        public string Guid { get; }

        /// <summary>The camera, or null when there is none.</summary>
        public BcfCamera? Camera { get; set; }

        /// <summary>Components the viewer should select.</summary>
        public IList<BcfComponent> Selection { get; } = new List<BcfComponent>();

        /// <summary>
        /// Whether elements are visible unless listed in <see cref="VisibilityExceptions"/>.
        ///
        /// The polarity is the trap: the exceptions list means "different from this default", so
        /// with the default true it names what is hidden, and with it false it names what is shown.
        /// Getting it backwards produces a viewpoint that hides the entire model.
        /// </summary>
        public bool DefaultVisibility { get; set; } = true;

        /// <summary>Components whose visibility differs from <see cref="DefaultVisibility"/>.</summary>
        public IList<BcfComponent> VisibilityExceptions { get; } = new List<BcfComponent>();

        /// <summary>Colour overrides.</summary>
        public IList<BcfColouring> Colouring { get; } = new List<BcfColouring>();

        /// <summary>Section planes.</summary>
        public IList<BcfClippingPlane> ClippingPlanes { get; } = new List<BcfClippingPlane>();

        /// <summary>Whether spaces are shown.</summary>
        public bool SpacesVisible { get; set; }

        /// <summary>Whether space boundaries are shown.</summary>
        public bool SpaceBoundariesVisible { get; set; }

        /// <summary>Whether openings are shown.</summary>
        public bool OpeningsVisible { get; set; }

        /// <summary>The snapshot PNG, or null.</summary>
        public byte[]? Snapshot { get; set; }
    }

    /// <summary>One comment on a topic.</summary>
    public sealed class BcfComment
    {
        /// <summary>Create a comment.</summary>
        /// <param name="id">Any stable id, converted to a GUID deterministically. See <see cref="BcfViewpoint"/>.</param>
        /// <param name="date">When it was made.</param>
        /// <param name="author">Who made it.</param>
        /// <param name="text">What it says.</param>
        public BcfComment(string id, DateTimeOffset date, string author, string? text)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a comment needs a stable id", nameof(id));

            Guid = BcfGuid.For(id);
            Date = date;
            Author = author ?? throw new ArgumentNullException(nameof(author));
            Text = text;
        }

        /// <summary>Its GUID.</summary>
        public string Guid { get; }

        /// <summary>When it was made.</summary>
        public DateTimeOffset Date { get; }

        /// <summary>Who made it.</summary>
        public string Author { get; }

        /// <summary>What it says.</summary>
        public string? Text { get; }

        /// <summary>The viewpoint it refers to, if any.</summary>
        public string? ViewpointGuid { get; set; }

        /// <summary>When it was last edited.</summary>
        public DateTimeOffset? ModifiedDate { get; set; }

        /// <summary>Who last edited it.</summary>
        public string? ModifiedAuthor { get; set; }
    }

    /// <summary>One BCF topic.</summary>
    public sealed class BcfTopic
    {
        /// <summary>Create a topic.</summary>
        /// <param name="id">Any stable id. Converted to a schema-legal GUID deterministically.</param>
        /// <param name="title">The title. Required by the schema in both versions.</param>
        /// <param name="creationDate">When it was raised.</param>
        /// <param name="creationAuthor">Who raised it.</param>
        public BcfTopic(string id, string title, DateTimeOffset creationDate, string creationAuthor)
        {
            Guid = BcfGuid.For(id);
            Title = string.IsNullOrWhiteSpace(title)
                ? throw new ArgumentException("a BCF topic must have a title", nameof(title))
                : title;
            CreationDate = creationDate;
            CreationAuthor = string.IsNullOrWhiteSpace(creationAuthor)
                ? throw new ArgumentException("a BCF topic must have a creation author", nameof(creationAuthor))
                : creationAuthor;
        }

        /// <summary>Its GUID.</summary>
        public string Guid { get; }

        /// <summary>Title.</summary>
        public string Title { get; }

        /// <summary>
        /// Topic type. Required by BCF 3.0 and optional in 2.1, so it carries a default rather
        /// than being allowed to be absent and failing validation on one version only.
        /// </summary>
        public string TopicType { get; set; } = "Issue";

        /// <summary>Topic status. Required by BCF 3.0; defaulted for the same reason.</summary>
        public string TopicStatus { get; set; } = "Open";

        /// <summary>Priority.</summary>
        public string? Priority { get; set; }

        /// <summary>Stage.</summary>
        public string? Stage { get; set; }

        /// <summary>Description.</summary>
        public string? Description { get; set; }

        /// <summary>Who it is assigned to.</summary>
        public string? AssignedTo { get; set; }

        /// <summary>When it was raised.</summary>
        public DateTimeOffset CreationDate { get; }

        /// <summary>Who raised it.</summary>
        public string CreationAuthor { get; }

        /// <summary>When it was last edited.</summary>
        public DateTimeOffset? ModifiedDate { get; set; }

        /// <summary>Who last edited it.</summary>
        public string? ModifiedAuthor { get; set; }

        /// <summary>When it is due.</summary>
        public DateTimeOffset? DueDate { get; set; }

        /// <summary>Labels.</summary>
        public IList<string> Labels { get; } = new List<string>();

        /// <summary>External links.</summary>
        public IList<string> ReferenceLinks { get; } = new List<string>();

        /// <summary>Related topics, by GUID.</summary>
        public IList<string> RelatedTopics { get; } = new List<string>();

        /// <summary>Comments.</summary>
        public IList<BcfComment> Comments { get; } = new List<BcfComment>();

        /// <summary>Viewpoints.</summary>
        public IList<BcfViewpoint> Viewpoints { get; } = new List<BcfViewpoint>();

        /// <inheritdoc />
        public override string ToString() => Title + " (" + TopicStatus + ")";
    }

    /// <summary>Project details, written to <c>project.bcfp</c>.</summary>
    public sealed class BcfProject
    {
        /// <summary>Create project details.</summary>
        public BcfProject(string projectId, string? name = null)
        {
            ProjectId = BcfGuid.For(projectId);
            Name = name;
        }

        /// <summary>The project GUID.</summary>
        public string ProjectId { get; }

        /// <summary>Its name.</summary>
        public string? Name { get; }
    }

    /// <summary>
    /// Small XML helpers shared by the BCF writers.
    ///
    /// Hand-rolled rather than pulled from a serialiser, for the same reason the JSON store is:
    /// the exact bytes matter, the schemas are fixed and small, and a round-trip through an
    /// attribute-ordering serialiser is one more thing between us and a file another tool accepts.
    /// </summary>
    internal static class Xml
    {
        /// <summary>Escape text for an element or attribute value, dropping characters XML forbids.</summary>
        internal static string Escape(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(text!.Length + 16);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        // XML 1.0 permits only tab, newline, carriage return and >= 0x20. A stray
                        // control character in a property value would make the whole file
                        // unreadable, which is a bad way to find out it was there.
                        if (c >= 0x20 || c == '\t' || c == '\n' || c == '\r') sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>An <c>xs:dateTime</c>.</summary>
        internal static string Date(DateTimeOffset value) =>
            value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        /// <summary>
        /// An <c>xs:double</c>, never in exponent form.
        ///
        /// Exponent notation is legal in the schema and rejected in practice by more than one
        /// reader, so it is avoided rather than argued with.
        /// </summary>
        internal static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "0";
            return value.ToString("0.0###########", CultureInfo.InvariantCulture);
        }

        /// <summary>An <c>xs:boolean</c>.</summary>
        internal static string Bool(bool value) => value ? "true" : "false";
    }
}
