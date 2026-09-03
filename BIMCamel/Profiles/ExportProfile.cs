using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace BIMCamel.Profiles
{
    /// <summary>
    /// Serializable snapshot of every export setting (F10). Saved/loaded as JSON via the
    /// framework's DataContractJsonSerializer — no third-party dependency (keeps the open-source
    /// footprint minimal, plan §12).
    /// </summary>
    [DataContract]
    public sealed class ExportProfile
    {
        [DataMember] public int Schema;
        [DataMember] public int Units;
        [DataMember] public int Scope;
        [DataMember] public int Quality;
        [DataMember] public int BasePoint;
        [DataMember] public double CustomE;
        [DataMember] public double CustomN;
        [DataMember] public double CustomElev;
        [DataMember] public double Rotation;
        [DataMember] public bool Georef = true;
        [DataMember] public bool Props = true;
        [DataMember] public bool Materials = true;
        [DataMember] public bool Instancing;
        /// <summary>v5 E1. Defaults to false on load of an older profile, which is the safe
        /// direction: an experimental read optimisation should never arrive switched on.</summary>
        [DataMember] public bool FastInstancing;

        // ── v6 file size. Zip and SkipEmptyProps default TRUE for a profile written before v6,
        // because they are pure wins; the two that change what a recipient sees default FALSE.
        [DataMember] public bool Zip = true;
        [DataMember] public bool SkipEmptyProps = true;
        [DataMember] public bool ShareTypeProps;
        [DataMember] public bool MinSize;
        [DataMember] public string MinSizeMm = "5";
        [DataMember] public bool Validate;
        [DataMember] public bool Quantities = true;
        [DataMember] public string Mapping = "";

        // ── real-world georeferencing (kept apart from the base point above) ──────────────
        [DataMember] public string Crs = "";
        [DataMember] public double SurveyE;
        [DataMember] public double SurveyN;
        [DataMember] public double SurveyElev;

        // ── the mapping that used to be lost on save ─────────────────────────────────────
        // Property roles, parameter rules and the spatial names drive IfcBuildingStorey,
        // IfcElementType, IfcMaterial and IfcClassificationReference — arguably the most valuable
        // configuration in the tool, and none of it survived a save/load round trip before.
        /// <summary>Roles as "cat|prop" per line: Type, Level, Material, Classification.</summary>
        [DataMember] public string Roles = "";
        /// <summary>Parameter rules, one per line: "srcCategory | srcProperty | targetPset | targetName".</summary>
        [DataMember] public string ParamRules = "";
        [DataMember] public string ProjectName = "";
        [DataMember] public string SiteName = "";
        [DataMember] public string BuildingName = "";
        [DataMember] public string StoreyName = "";
        [DataMember] public string ClassificationSystem = "";
        /// <summary>Who publishes it — "NBS". Written as IfcClassification.Source.</summary>
        [DataMember] public string ClassificationSource = "";
        /// <summary>Which edition — "2015". Mandatory in IFC2x3.</summary>
        [DataMember] public string ClassificationEdition = "";
        /// <summary>The edition's date, YYYY-MM-DD. Anything else is left out rather than guessed.</summary>
        [DataMember] public string ClassificationEditionDate = "";
        /// <summary>Where it is published, as a URL. IFC4 only.</summary>
        [DataMember] public string ClassificationLocation = "";
        [DataMember] public bool Split;
        [DataMember] public string SplitMb = "200";
        /// <summary>0 = none, 1 = IfcGroup, 2 = IfcSystem, 3 = IfcZone.</summary>
        [DataMember] public int Groups;

        public static void Save(ExportProfile p, string path)
        {
            var ser = new DataContractJsonSerializer(typeof(ExportProfile));
            using var fs = File.Create(path);
            ser.WriteObject(fs, p);
        }

        public static ExportProfile Load(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(ExportProfile));
            using var fs = File.OpenRead(path);
            return (ExportProfile)ser.ReadObject(fs)!;
        }
    }
}
