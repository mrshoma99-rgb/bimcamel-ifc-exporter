using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace BIMCamel.Profiles
{
    /// <summary>
    /// Pane state that is not an export setting: theme, dismissed notices, which cards were open,
    /// the last folders used. Kept apart from <see cref="ExportProfile"/> because a profile is meant
    /// to be handed to a colleague, and none of this is theirs.
    /// </summary>
    [DataContract]
    public sealed class UiPrefs
    {
        [DataMember] public bool Dark;
        [DataMember] public bool WarningDismissed;
        [DataMember] public bool AdvancedOpen;
        [DataMember] public string LastExportFolder = "";
        [DataMember] public string ProfileName = "";
    }

    /// <summary>
    /// The pane's memory between Navisworks sessions, under %APPDATA%\BIMCamel next to the update
    /// check's state. Before this, every restart reset every tab, and profiles were the only way
    /// to keep a configuration.
    ///
    /// Every call swallows I/O errors: a locked or unwritable AppData must never stop an export,
    /// it only means the pane starts from defaults next time.
    /// </summary>
    public static class SettingsStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMCamel");

        private static string LastPath => Path.Combine(Dir, "last-settings.json");
        private static string UiPath => Path.Combine(Dir, "ui-prefs.json");

        public static ExportProfile? LoadLast()
        {
            try { return File.Exists(LastPath) ? ExportProfile.Load(LastPath) : null; }
            catch { return null; }
        }

        public static void SaveLast(ExportProfile p)
        {
            try { Directory.CreateDirectory(Dir); WriteAtomic(LastPath, ExportProfile.ToJson(p)); }
            catch { }
        }

        public static UiPrefs LoadUi()
        {
            try
            {
                if (!File.Exists(UiPath)) return new UiPrefs();
                var ser = new DataContractJsonSerializer(typeof(UiPrefs));
                using var fs = File.OpenRead(UiPath);
                return (UiPrefs?)ser.ReadObject(fs) ?? new UiPrefs();
            }
            catch { return new UiPrefs(); }
        }

        public static void SaveUi(UiPrefs p)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var ser = new DataContractJsonSerializer(typeof(UiPrefs));
                using var ms = new MemoryStream();
                ser.WriteObject(ms, p);
                WriteAtomic(UiPath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch { }
        }

        // Written beside, then swapped in: Navisworks can be closed mid-write, and a truncated
        // JSON file would cost the user every setting rather than the last change.
        private static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
