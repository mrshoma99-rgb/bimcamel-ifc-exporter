using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMCamel.Data
{
    /// <summary>
    /// Rule-based IFC class for elements no set rule mapped — which would otherwise all export as
    /// IfcBuildingElementProxy. No model and no training: three fixed rules, tried in order, each
    /// one auditable by reading this file.
    ///
    ///   1. IFC CLASS NAME. A model that came from IFC already says what each element is: the
    ///      Navisworks item type reads "IfcWall", "IfcPipeSegment"… Taken as stated.
    ///   2. REVIT CATEGORY. Revit files carry Element › Category ("Walls", "Structural Framing",
    ///      "Pipes"…), a fixed vocabulary that maps to an IFC class almost one to one — the same
    ///      mapping Revit's own IFC exporter starts from. Taken as stated, so a category with no
    ///      clean IFC counterpart ("Generic Models", "Mechanical Equipment") is simply not listed
    ///      and falls through to the names.
    ///   3. NAME WORDS. Family, type and element names — "Basic Wall:Exterior", "M_Round Duct".
    ///      Ported from BIMCamel's ProxyClassifier, including what made it trustworthy there:
    ///        • whole words only ("Wallace" is not a wall);
    ///        • a word naming a PIECE refuses ("Curtain Wall:Glazing" is a panel, not a wall);
    ///        • a name naming two classes refuses ("Wall Foundation" is not evidence for either);
    ///        • shape can VETO a name, never promote one (a "wall" 300 mm across every axis is a
    ///          fitting with "wall" in its name).
    ///
    /// A WRONG CLASS IS WORSE THAN NO CLASS. A proxy is honest; a chair filed as a wall corrupts
    /// every schedule that counts walls. Every rule here would rather say nothing — and the
    /// exporter records which rule decided, so a reviewer can tell a guess from a mapping.
    ///
    /// Pure: no Navisworks types, so it reads (and could be tested) on its own.
    /// </summary>
    public static partial class RuleClassifier
    {
        /// <summary>What decided, and the class key (TypeMapping encoding "Friendly|PREDEF").</summary>
        public readonly struct Guess
        {
            public readonly string ClassKey;
            /// <summary>"IFC class", "Revit category" or "name".</summary>
            public readonly string Rule;
            /// <summary>The text that matched, for the audit property.</summary>
            public readonly string Evidence;
            public Guess(string classKey, string rule, string evidence) { ClassKey = classKey; Rule = rule; Evidence = evidence; }
        }

        /// <param name="ifcTypeNames">Navisworks item type names of the element and its ancestors,
        /// nearest first — "IfcWall" on IFC-sourced models.</param>
        /// <param name="category">Revit Element › Category, or null.</param>
        /// <param name="names">Type, family and element names, most specific first.</param>
        /// <param name="sizeMetres">Bounding-box size in metres, any order; null when unknown.</param>
        public static Guess? Classify(IEnumerable<string?> ifcTypeNames, string? category, IEnumerable<string?> names,
                                      (double X, double Y, double Z)? sizeMetres)
        {
            foreach (var t in ifcTypeNames)
            {
                var friendly = FromIfcEntity(t);
                if (friendly != null) return new Guess(friendly, "IFC class", t!);
            }

            if (!string.IsNullOrWhiteSpace(category) && Categories.TryGetValue(category!.Trim(), out var byCategory))
                return new Guess(byCategory, "Revit category", category.Trim());

            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                var text = Normalise(n!);
                if (ComponentWords.Any(w => ContainsWord(text, w))) continue;

                var hits = MatchesIn(text);
                if (hits.Count != 1) continue;
                if (sizeMetres is { } s && ShapeContradicts(hits[0], s)) continue;
                return new Guess(hits[0], "name", n!.Trim());
            }
            return null;
        }

        // ── audit + per-export tally ──────────────────────────────────────────────────────

        /// <summary>The PropertySet an auto-classified element records its rule in, so a reviewer
        /// can tell (and filter) a rule's guess from a set mapping.</summary>
        public const string AuditPset = "BIMCamel_Classification";

        /// <summary>Elements classified per rule in the current export. Single-threaded export
        /// (same static-tally pattern as ExportIssues); <see cref="Reset"/> runs at its start.</summary>
        public static readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public static void Reset() => Counts.Clear();

        /// <summary>Counts the guess and, when properties are being exported, records it on the
        /// element. Returns the (possibly new) property list.</summary>
        public static List<IfcProp>? Apply(Guess g, List<IfcProp>? props, bool writeAudit)
        {
            Counts[g.Rule] = Counts.TryGetValue(g.Rule, out var n) ? n + 1 : 1;
            if (!writeAudit) return props;
            props ??= new List<IfcProp>();
            props.Add(new IfcProp { Pset = AuditPset, Name = "Method", Value = "Rule: " + g.Rule });
            props.Add(new IfcProp { Pset = AuditPset, Name = "Evidence", Value = g.Evidence });
            return props;
        }

        /// <summary>One report line, or "" when the option was off or nothing matched.</summary>
        public static string Report()
        {
            if (Counts.Count == 0) return "";
            int total = Counts.Values.Sum();
            return $"Auto-classify: {total:N0} unmapped element(s) classified by rule ("
                   + string.Join(", ", Counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value:N0} by {kv.Key}")) + ")";
        }

        // ── rule 1: IFC class names ────────────────────────────────────────────────────────

        private static readonly Dictionary<string, string> FriendlyByEntity = BuildEntityLookup();

        private static Dictionary<string, string> BuildEntityLookup()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in TypeMapping.Catalog)
                if (kv.Value.Ifc4 != "IFCBUILDINGELEMENTPROXY" && !map.ContainsKey(kv.Value.Ifc4))
                    map[kv.Value.Ifc4] = kv.Key;   // first entry wins: "Wall" over "Wall (standard case)"
            return map;
        }

        private static string? FromIfcEntity(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            var t = typeName!.Trim();
            if (!t.StartsWith("Ifc", StringComparison.OrdinalIgnoreCase) || t.IndexOf(' ') >= 0) return null;
            // IfcWallStandardCase/IfcSlabStandardCase are IFC2x3-era spellings of the plain class,
            // deprecated in IFC4 — export them as the class itself.
            if (t.EndsWith("StandardCase", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - "StandardCase".Length);
            return FriendlyByEntity.TryGetValue(t, out var f) ? f : null;
        }

        // ── rule 2: Revit categories ───────────────────────────────────────────────────────

        /// <summary>
        /// Revit category → class key. Only categories whose IFC class is not in doubt; every
        /// PredefinedType here is a valid IFC4 value for its class. English category names, as
        /// Revit stores them in an English-language project.
        /// </summary>
        private static readonly Dictionary<string, string> Categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Architectural
            { "Walls", "Wall" },
            { "Floors", "Slab|FLOOR" },
            { "Roofs", "Roof" },
            { "Ceilings", "Covering|CEILING" },
            { "Doors", "Door" },
            { "Windows", "Window" },
            { "Stairs", "Stair" },
            { "Runs", "Stair flight" },
            { "Railings", "Railing" },
            { "Top Rails", "Railing|HANDRAIL" },
            { "Handrails", "Railing|HANDRAIL" },
            { "Ramps", "Ramp" },
            { "Columns", "Column" },
            { "Curtain Panels", "Plate|CURTAIN_PANEL" },
            { "Curtain Wall Mullions", "Member|MULLION" },
            { "Furniture", "Furniture" },
            { "Furniture Systems", "Furniture" },
            { "Casework", "Furniture" },
            { "Parts", "Building element part" },
            // Structural
            { "Structural Columns", "Column|COLUMN" },
            { "Structural Framing", "Beam|BEAM" },
            { "Structural Foundations", "Footing" },
            // Piping
            { "Pipes", "Pipe segment|RIGIDSEGMENT" },
            { "Flex Pipes", "Pipe segment|FLEXIBLESEGMENT" },
            { "Pipe Fittings", "Pipe fitting" },
            { "Plumbing Fixtures", "Sanitary terminal" },
            // HVAC
            { "Ducts", "Duct segment|RIGIDSEGMENT" },
            { "Flex Ducts", "Duct segment|FLEXIBLESEGMENT" },
            { "Duct Fittings", "Duct fitting" },
            { "Air Terminals", "Air terminal" },
            // Electrical
            { "Cable Trays", "Cable carrier segment|CABLETRAYSEGMENT" },
            { "Cable Tray Fittings", "Cable carrier fitting" },
            { "Conduits", "Cable carrier segment|CONDUITSEGMENT" },
            { "Conduit Fittings", "Cable carrier fitting" },
            { "Lighting Fixtures", "Light fixture" },
        };

        // ── rule 3: name words (BIMCamel ProxyClassifier) ─────────────────────────────────

        /// <summary>Whole words that name a class. Phrases are matched as phrases, so "curtain
        /// wall" is one word here.</summary>
        private static readonly (string Word, string ClassKey)[] NameWords =
        {
            ("curtainwall", "Curtain wall"), ("curtain wall", "Curtain wall"),
            ("handrail", "Railing"), ("balustrade", "Railing"), ("guardrail", "Railing"), ("railing", "Railing"),
            ("stair", "Stair"), ("stairs", "Stair"),
            ("column", "Column"), ("pilaster", "Column"),
            ("beam", "Beam"), ("joist", "Beam"), ("lintel", "Beam"), ("purlin", "Beam"),
            ("wall", "Wall"), ("partition", "Wall"),
            // "Wall Foundation:Bearing Footing" names two classes and is refused — silence beats a
            // footing in the wall schedule.
            ("footing", "Footing"), ("foundation", "Footing"),
            ("slab", "Slab"), ("floor", "Slab"), ("deck", "Slab"),
            ("ceiling", "Covering"), ("soffit", "Covering"), ("cladding", "Covering"),
            ("door", "Door"), ("window", "Window"), ("roof", "Roof"),
            ("duct", "Duct segment"), ("pipe", "Pipe segment"),
            ("diffuser", "Air terminal"), ("grille", "Air terminal"),
            ("luminaire", "Light fixture"),
            ("desk", "Furniture"), ("chair", "Furniture"), ("table", "Furniture"), ("cabinet", "Furniture"), ("furniture", "Furniture"),
        };

        /// <summary>Words that mean "a PIECE of one of those". Measured in BIMCamel: adding these
        /// took the name rule's disagreement with authored classes from 11% to 4.8%. "Panel" is
        /// deliberately absent — a precast wall panel really is a wall.</summary>
        private static readonly string[] ComponentWords =
        {
            "glazing", "mullion", "transom",
            "shoe", "bracket", "clip", "anchor", "fixing", "fastener", "cleat",
            "tread", "riser", "flight", "landing", "nosing",
            // Fittings and accessories carry their run's word ("Duct Elbow", "Pipe Tee").
            "fitting", "elbow", "bend", "tee", "reducer", "transition", "union", "coupling", "flange", "cap", "accessory",
        };

        private static List<string> MatchesIn(string normalised)
        {
            var found = new List<string>();
            foreach (var (word, key) in NameWords)
                if (!found.Contains(key) && ContainsWord(normalised, word)) found.Add(key);
            // "Curtain wall" names one thing, not two; a stair railing is a railing.
            if (found.Contains("Curtain wall")) found.Remove("Wall");
            if (found.Contains("Railing")) found.Remove("Stair");
            return found;
        }

        /// <summary>Lower case, every separator a space, padded — so a whole-word test is a plain
        /// search for " word ".</summary>
        private static string Normalise(string text)
        {
            var chars = text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray();
            return " " + new string(chars) + " ";
        }

        private static bool ContainsWord(string normalised, string word) =>
            normalised.IndexOf(" " + word + " ", StringComparison.Ordinal) >= 0;

        /// <summary>A veto, never a vote: proportions cannot identify an element, but they can
        /// refuse the obvious mistakes.</summary>
        private static bool ShapeContradicts(string classKey, (double X, double Y, double Z) size)
        {
            var d = new[] { size.X, size.Y, size.Z }.Select(Math.Abs).Where(v => v > 0).OrderBy(v => v).ToArray();
            if (d.Length < 3) return false;
            double thin = d[0], middle = d[1], longest = d[2];
            switch (TypeMapping.Friendly(classKey))
            {
                // A plate: one dimension much smaller than the other two, and building-sized.
                case "Wall": case "Curtain wall": case "Slab": case "Roof": case "Covering":
                    return longest < 0.6 || thin > middle * 0.5;
                // A run: the longest dimension dominates.
                case "Beam": case "Column": case "Duct segment": case "Pipe segment":
                    return longest < middle * 2.0;
                default:
                    return false;
            }
        }
    }
}
