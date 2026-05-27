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
        [DataMember] public bool Validate;
        [DataMember] public string Mapping = "";

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
