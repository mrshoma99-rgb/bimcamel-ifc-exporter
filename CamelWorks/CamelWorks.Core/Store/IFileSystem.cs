using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CamelWorks.Core.Store
{
    /// <summary>
    /// File access, behind a seam.
    ///
    /// Not for portability — for testability of the cases that matter. "The process died between
    /// writing the temp file and replacing the target" is the scenario atomic write exists for, and
    /// it cannot be provoked against a real disk on demand. Against a fake it is three lines.
    /// </summary>
    public interface IFileSystem
    {
        /// <summary>True when the file exists.</summary>
        bool Exists(string path);

        /// <summary>Read a file, or null when it does not exist.</summary>
        string? ReadAllText(string path);

        /// <summary>Write a file, creating or truncating it. Not atomic — see <see cref="AtomicFile"/>.</summary>
        void WriteAllText(string path, string text);

        /// <summary>
        /// Replace <paramref name="destination"/> with <paramref name="source"/>, optionally
        /// keeping the old contents at <paramref name="backup"/>. Moves when the destination does
        /// not yet exist.
        /// </summary>
        void Replace(string source, string destination, string? backup);

        /// <summary>Delete a file. Deleting something absent is not an error.</summary>
        void Delete(string path);

        /// <summary>Ensure a directory exists.</summary>
        void CreateDirectory(string directory);

        /// <summary>File names (not full paths) directly in a directory. Empty when it does not exist.</summary>
        IReadOnlyList<string> ListFiles(string directory);
    }

    /// <summary>The real file system.</summary>
    public sealed class PhysicalFileSystem : IFileSystem
    {
        /// <summary>A shared instance; the type holds no state.</summary>
        public static PhysicalFileSystem Instance { get; } = new PhysicalFileSystem();

        /// <inheritdoc />
        public bool Exists(string path) => File.Exists(path);

        /// <inheritdoc />
        public string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

        /// <inheritdoc />
        public void WriteAllText(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            // UTF-8 without a BOM: these files are read by other tools and by people, and a BOM in
            // a JSON file is a well-known way to break a naive reader.
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <inheritdoc />
        public void Replace(string source, string destination, string? backup)
        {
            if (!File.Exists(destination))
            {
                File.Move(source, destination);
                return;
            }

            if (backup != null && File.Exists(backup)) File.Delete(backup);
            File.Replace(source, destination, backup, ignoreMetadataErrors: true);
        }

        /// <inheritdoc />
        public void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        /// <inheritdoc />
        public void CreateDirectory(string directory) => Directory.CreateDirectory(directory);

        /// <inheritdoc />
        public IReadOnlyList<string> ListFiles(string directory) =>
            Directory.Exists(directory)
                ? Directory.GetFiles(directory).Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToList()
                : (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// Writes a file so that a crash cannot leave a truncated one.
    ///
    /// Write to a sibling temp file, then replace the target in one operation, keeping the previous
    /// contents as <c>.bak</c>. The deliverable here is not only a sidecar — a batch job writes the
    /// federated NWF this way too, and a half-written NWF is a week of nobody being able to open
    /// the model.
    ///
    /// The temp file is a SIBLING, not in the system temp folder, because a replace across volumes
    /// is a copy: it is neither atomic nor fast, and on a large file it reintroduces the exact
    /// window this is meant to close.
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>Suffix of the in-progress file.</summary>
        public const string TempSuffix = ".cwtmp";

        /// <summary>Suffix of the previous contents, kept after a successful replace.</summary>
        public const string BackupSuffix = ".bak";

        /// <summary>Write <paramref name="text"/> to <paramref name="path"/> atomically.</summary>
        public static void Write(IFileSystem fs, string path, string text)
        {
            if (fs == null) throw new ArgumentNullException(nameof(fs));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required", nameof(path));

            var temp = path + TempSuffix;
            var backup = path + BackupSuffix;

            fs.WriteAllText(temp, text);
            fs.Replace(temp, path, backup);
        }

        /// <summary>
        /// Read a file, falling back to its backup when the file is missing or unreadable.
        ///
        /// The fallback is the other half of atomic write and is easy to leave out. Without it the
        /// <c>.bak</c> is a file nobody ever opens, and the recovery it exists for is a manual
        /// rename the user has to be told about in a support ticket.
        /// </summary>
        public static string? ReadWithFallback(IFileSystem fs, string path, out bool usedBackup)
        {
            if (fs == null) throw new ArgumentNullException(nameof(fs));

            usedBackup = false;

            var text = fs.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(text)) return text;

            var backup = fs.ReadAllText(path + BackupSuffix);
            if (string.IsNullOrWhiteSpace(backup)) return text;

            usedBackup = true;
            return backup;
        }

        /// <summary>
        /// Remove a temp file left behind by a crash. Called on load, so a stale one cannot be
        /// mistaken for a real file by anything that lists the folder.
        /// </summary>
        public static void CleanUpTemp(IFileSystem fs, string path) => fs.Delete(path + TempSuffix);
    }
}
