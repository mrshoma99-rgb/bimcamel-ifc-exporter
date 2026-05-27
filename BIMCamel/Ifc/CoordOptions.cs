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
    /// Coordinate &amp; georeferencing options (IMPLEMENTATION_PLAN.md §7). The origin offset is
    /// always lifted into the IfcSite placement so geometry sits near the origin. For IFC4 the
    /// real-world location is additionally recorded as IfcMapConversion + IfcProjectedCRS; IFC2x3
    /// has no IfcMapConversion, so the offset is only baked into the placement (reported explicitly).
    /// </summary>
    public sealed class CoordOptions
    {
        public BasePointMode Mode = BasePointMode.GeometryOrigin;

        // Used when Mode == Custom (metres).
        public double CustomEastings;
        public double CustomNorthings;
        public double CustomElevation;

        /// <summary>Grid/true-north rotation in degrees (recorded in IFC4 georeferencing).</summary>
        public double RotationDeg;

        /// <summary>Write IfcMapConversion/IfcProjectedCRS (IFC4 only).</summary>
        public bool WriteGeoref = true;
    }

    /// <summary>Names for the IFC spatial skeleton (F9). All have sensible defaults.</summary>
    public sealed class SpatialNames
    {
        public string Project = "BIMCamel Export";
        public string Site = "Site";
        public string Building = "Building";
        public string Storey = "Storey";
    }
}
