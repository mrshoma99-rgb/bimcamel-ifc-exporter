using Autodesk.Navisworks.Api;

namespace CamelWorks.Nav
{
    /// <summary>
    /// Model units to metres, in one place.
    ///
    /// Every threshold in this product is stated in metres — a 10 mm clash tolerance, a 5 m
    /// grouping distance, a half-metre section-box margin — and the host reports geometry in
    /// whatever unit the document was authored in. Converting at the boundary is the only place it
    /// can be got right once; leaving it to each caller is how a millimetre model ends up with a
    /// half-millimetre margin around its section box and nobody able to say why.
    /// </summary>
    public static class NavUnits
    {
        /// <summary>How many metres one model unit is.</summary>
        /// <param name="units">The document's or model's unit.</param>
        public static double MetresPerUnit(Units units)
        {
            switch (units)
            {
                case Units.Meters: return 1;
                case Units.Centimeters: return 0.01;
                case Units.Millimeters: return 0.001;
                case Units.Kilometers: return 1000;
                case Units.Micrometers: return 0.000001;
                case Units.Feet: return 0.3048;
                case Units.Inches: return 0.0254;
                case Units.Yards: return 0.9144;
                case Units.Miles: return 1609.344;
                case Units.Mils: return 0.0000254;
                case Units.Microinches: return 0.0000000254;
                default: return 1;
            }
        }

        /// <summary>How many model units one metre is. Never zero.</summary>
        /// <param name="units">The document's or model's unit.</param>
        public static double UnitsPerMetre(Units units)
        {
            var metres = MetresPerUnit(units);
            return metres > 0 ? 1 / metres : 1;
        }
    }
}
