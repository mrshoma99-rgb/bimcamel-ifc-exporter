using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Collect
{
    /// <summary>
    /// Resolved set maps kept on disk, so an export of an unchanged model skips the set searches
    /// even in a new Navisworks session.
    ///
    /// On a cold session most of a set search is Navisworks loading property data: the same 22
    /// searches have cost from 4 s (warm) to 89 s (cold). The maps those searches produce are
    /// keyed by item key strings (InstanceGuid or tree path), which mean the same thing in every
    /// session, so they can be written once and read back.
    ///
    /// A saved entry is used only when ALL of these still hold, and otherwise ignored:
    ///  • the document file and every model file it loads have the same size and write time;
    ///  • the rule list is identical (same sets, classes and codes, in the same order);
    ///  • the scope is the same, for maps resolved over one scope only;
    ///  • no set was added, removed or edited in this session (<see cref="ScanCache.SetsEdited"/>):
    ///    an edit lives in memory until the file is saved, and saving changes the write time.
    /// Nothing is written while sets are edited, for the same reason. One entry per document, in
    /// %LOCALAPPDATA%\BIMCamel\SetMaps; deleting the folder is always safe.
    /// </summary>
    public static class SetMapStore
    {
        private const int Format = 1;

        private static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMCamel", "SetMaps");

        /// <summary>When the entry just read was written, for the report.</summary>
        public static DateTime LastSavedUtc { get; private set; }

        public static ItemCollector.SetMaps? Load(Document doc, string rules, string scope)
        {
            try
            {
                string? files = Fingerprint(doc);
                if (files == null) return null;
                string path = PathFor(doc);
                if (!File.Exists(path)) return null;
                using var fs = File.OpenRead(path);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var r = new BinaryReader(gz, Encoding.UTF8);
                if (r.ReadInt32() != Format) return null;
                if (r.ReadString() != files || r.ReadString() != rules || r.ReadString() != scope) return null;
                var saved = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                var maps = new ItemCollector.SetMaps();
                ReadMap(r, maps.Class);
                ReadMap(r, maps.Classification);
                ReadMap(r, maps.Group);
                LastSavedUtc = saved;
                return maps;
            }
            catch { return null; }   // unreadable or old: resolve again
        }

        public static void Save(Document doc, string rules, string scope, ItemCollector.SetMaps maps)
        {
            try
            {
                string? files = Fingerprint(doc);
                if (files == null) return;
                Directory.CreateDirectory(Folder);
                string path = PathFor(doc), tmp = path + ".tmp";
                using (var fs = File.Create(tmp))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (var w = new BinaryWriter(gz, Encoding.UTF8))
                {
                    w.Write(Format);
                    w.Write(files); w.Write(rules); w.Write(scope);
                    w.Write(DateTime.UtcNow.Ticks);
                    WriteMap(w, maps.Class);
                    WriteMap(w, maps.Classification);
                    WriteMap(w, maps.Group);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { /* a cache that can't be written just isn't there next time */ }
        }

        /// <summary>The document file and every model file, each with its size and write time; null
        /// for a document that was never saved.</summary>
        private static string? Fingerprint(Document doc)
        {
            string main = doc.FileName ?? "";
            if (main.Length == 0) return null;
            var sb = new StringBuilder();
            AddFile(sb, main);
            sb.Append(doc.Models.Count).Append('\n');
            foreach (Model m in doc.Models)
            {
                AddFile(sb, m.FileName);
                AddFile(sb, m.SourceFileName);
            }
            return sb.ToString();
        }

        private static void AddFile(StringBuilder sb, string? path)
        {
            sb.Append(path ?? "").Append('|');
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var fi = new FileInfo(path!);
                    sb.Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks);
                }
                else sb.Append('-');
            }
            catch { sb.Append('?'); }
            sb.Append('\n');
        }

        private static string PathFor(Document doc)
        {
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes((doc.FileName ?? "").ToLowerInvariant()));
            return Path.Combine(Folder, BitConverter.ToString(hash).Replace("-", "") + ".bin");
        }

        private static void WriteMap(BinaryWriter w, Dictionary<string, string> map)
        {
            w.Write(map.Count);
            foreach (var kv in map) { w.Write(kv.Key); w.Write(kv.Value); }
        }

        private static void ReadMap(BinaryReader r, Dictionary<string, string> map)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) map[r.ReadString()] = r.ReadString();
        }
    }
}
