using System.Collections.Generic;

namespace BIMCamel.Ifc
{
    /// <summary>How the IFC project/local origin relates to the model's world coordinates.</summary>
    public enum BasePointMode
    {
        /// <summary>Lift the geometry's own min corner to the origin (default; precision-safe).</summary>
        GeometryOrigin,
        /// <summary>No offset — geometry keeps its raw world coordinates.</summary>
        ModelOrigin,
        /// <summary>User-supplied base point (Eastings/Northings/Elevation, metres).</summary>
        Custom
    }

    /// <summary>
    /// Coordinate &amp; georeferencing options (IMPLEMENTATION_PLAN.md §7).
    ///
    /// TWO INDEPENDENT CONCEPTS, deliberately kept apart:
    ///
    ///   1. BASE POINT (<see cref="Mode"/> + Custom*) — where the IFC local origin sits relative to
    ///      the model's world coordinates. Vertices are stored as (world − offset) and the same
    ///      offset is lifted into the IfcSite placement, so a consumer that walks the placement
    ///      chain lands back on the original world coordinates. This is purely about keeping stored
    ///      ordinates small and precise; it does NOT move the model.
    ///
    ///   2. SURVEY POINT (<see cref="SurveyEastings"/> …) — where the model's world origin sits in a
    ///      real-world projected CRS. This is the only thing IfcMapConversion should carry.
    ///
    /// Before v0.8.4 the base-point offset was written into BOTH the IfcSite placement and
    /// IfcMapConversion's Eastings/Northings/Height. Because the placement chain already puts the
    /// geometry at its world position, a consumer honouring both applied the real-world shift a
    /// second time. IfcMapConversion now carries only the survey offset, and is written only when
    /// the user has actually supplied georeferencing (a CRS name or a non-zero survey point) —
    /// an absent map conversion is far better than a confidently wrong one.
    /// </summary>
    public sealed class CoordOptions
    {
        public BasePointMode Mode = BasePointMode.GeometryOrigin;

        // Used when Mode == Custom (metres). Where to put the local origin.
        public double CustomEastings;
        public double CustomNorthings;
        public double CustomElevation;

        // ── Real-world georeferencing (IFC4 IfcMapConversion + IfcProjectedCRS) ──────────────
        /// <summary>Position of the model's world origin in the map CRS (metres). Default 0.</summary>
        public double SurveyEastings;
        public double SurveyNorthings;
        public double SurveyElevation;

        /// <summary>Grid/true-north rotation in degrees, clockwise from map north.</summary>
        public double RotationDeg;

        /// <summary>
        /// Projected CRS identifier, conventionally an EPSG code such as <c>EPSG:27700</c>.
        /// Written as IfcProjectedCRS.Name. Empty means the user gave us no CRS.
        /// </summary>
        public string CrsName = "";

        /// <summary>Optional human description of the CRS (IfcProjectedCRS.Description).</summary>
        public string CrsDescription = "";

        /// <summary>Write IfcMapConversion/IfcProjectedCRS (IFC4 only).</summary>
        public bool WriteGeoref = true;

        /// <summary>
        /// True when the user has supplied something real to georeference with. Without this the
        /// georef block would just restate "the origin is the origin in an unnamed CRS", which is
        /// noise at best and misleading at worst.
        /// </summary>
        public bool HasGeorefData =>
            CrsName.Trim().Length > 0
            || SurveyEastings != 0 || SurveyNorthings != 0 || SurveyElevation != 0
            || RotationDeg != 0;
    }

    /// <summary>
    /// The IFC spatial skeleton: names for each level of the fixed Project→Site→Building→Storey
    /// chain (F9), plus real storey elevations when the source model can supply them.
    /// </summary>
    public sealed class SpatialNames
    {
        public string Project = "BIMCamel Export";
        public string Site = "Site";
        public string Building = "Building";
        public string Storey = "Storey";

        /// <summary>
        /// Level name → elevation in METRES (world/model datum), read from the Navisworks grid
        /// levels. Matched case-insensitively against the Level property value that drives storey
        /// creation. Absent or unmatched levels keep elevation 0, as before.
        /// </summary>
        public IReadOnlyDictionary<string, double>? LevelElevations;

        /// <summary>
        /// Name of the classification system the codes belong to — "Uniclass 2015", "OmniClass".
        /// Written as IfcClassification.Name. Blank keeps the old generic label, which collapsed
        /// codes from every system under one anonymous source.
        /// </summary>
        public string ClassificationSystem = "";

        /// <summary>
        /// Who publishes the classification — "NBS", "OmniClass", "buildingSMART".
        ///
        /// Written as IfcClassification.Source. IFC2x3 makes Source and Edition MANDATORY, and
        /// with nothing to put there this exporter used to write its own name: every 2x3 file it
        /// produced claimed BIMCamel published Uniclass. That is a false statement about somebody
        /// else's standard sitting in a contractual deliverable, and a checker that reads Source to
        /// decide which classification it is looking at gets the wrong answer.
        ///
        /// Blank falls back to the system's own name, which is at least not a claim about anybody.
        /// </summary>
        public string ClassificationSource = "";

        /// <summary>
        /// Which edition — "2015", "v1.2", "Table Ss". Written as IfcClassification.Edition.
        ///
        /// Uniclass 2015 and Uniclass 1997 are different tables with overlapping codes, so an
        /// edition is not decoration: it is what tells the other end whether "Ss_25_10_30" means
        /// what they think it means.
        /// </summary>
        public string ClassificationEdition = "";

        /// <summary>
        /// The edition's publication date, as IFC wants it: YYYY-MM-DD.
        ///
        /// Written as IfcClassification.EditionDate. Anything that is not a plain calendar date is
        /// left out rather than guessed at — a malformed date in a deliverable is worse than an
        /// absent one, because a reader that parses it strictly rejects the file.
        /// </summary>
        public string ClassificationEditionDate = "";

        /// <summary>
        /// Where the classification is published, as a URL. IFC4 only — IfcClassification gained
        /// Location in IFC4 and 2x3 has nowhere to put it, so on 2x3 it is silently not written
        /// rather than crammed into a field that means something else.
        /// </summary>
        public string ClassificationLocation = "";

        /// <summary>
        /// IFC entity to emit for each mapped Navisworks set — "IFCGROUP", "IFCSYSTEM" or
        /// "IFCZONE". Empty means don't. Navisworks sets map onto these cleanly, and
        /// IfcRelAssignsToGroup was the one commonly-expected relationship we could actually
        /// reconstruct from data we already read.
        /// </summary>
        public string GroupEntity = "";
    }
}
