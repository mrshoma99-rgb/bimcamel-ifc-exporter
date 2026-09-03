using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace BIMCamel.Ifc
{
    /// <summary>
    /// The two shapes an exported IFC can take on disk, behind one door (v6 Z1).
    ///
    /// IFC's standard zipped form is an ordinary ZIP containing a single `.ifc`, conventionally
    /// named `.ifczip`. Revit, Solibri, ArchiCAD, Navisworks, xBim and IfcOpenShell all read it
    /// directly with no unpacking step. STEP text is close to the most compressible payload there
    /// is — ASCII, hugely repetitive, the same twenty keywords over and over.
    ///
    /// Everything that reads an export back — the validator, the byte profiler — goes through
    /// <see cref="OpenText"/> so neither of them has to learn what a ZIP is; and everything that
    /// writes one goes through <see cref="Create"/>, which streams STRAIGHT INTO the archive entry.
    /// The uncompressed file is therefore never written, so a zipped export does less disk I/O than
    /// a plain one rather than more.
    /// </summary>
    public static class IfcSource
    {
        public const string ZipExtension = ".ifczip";

        /// <summary>True when this path names the zipped form.</summary>
        public static bool IsZip(string path) =>
            path.EndsWith(ZipExtension, StringComparison.OrdinalIgnoreCase);

        /// <summary>Swap an output path between the plain and zipped forms.</summary>
        public static string PathFor(string basePath, bool zip)
        {
            if (zip) return IsZip(basePath) ? basePath : Path.ChangeExtension(basePath, "ifczip");
            return IsZip(basePath) ? Path.ChangeExtension(basePath, "ifc") : basePath;
        }

        /// <summary>The name the .ifc entry takes inside the archive: the archive's own name with
        /// an .ifc extension, which is what readers expect to find.</summary>
        private static string EntryName(string zipPath) =>
            Path.GetFileNameWithoutExtension(zipPath) + ".ifc";

        /// <summary>
        /// A writer over <paramref name="path"/>, plain or zipped by extension. Disposing the
        /// returned handle finalises the archive — so callers must dispose it, not the writer.
        /// </summary>
        public static Handle Create(string path, int coordDecimals)
        {
            if (!IsZip(path))
                return new Handle(new StreamingStepWriter(path, coordDecimals), null, null);

            var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            ZipArchive archive;
            try
            {
                // Create mode streams forward with no seeking — exactly the access pattern the
                // STEP writer already has, so an arbitrarily large model still never rewinds.
                archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
            }
            catch { file.Dispose(); throw; }

            try
            {
                var entry = archive.CreateEntry(EntryName(path), CompressionLevel.Optimal);
                var stream = entry.Open();
                return new Handle(new StreamingStepWriter(stream, coordDecimals), archive, null);
            }
            catch { archive.Dispose(); throw; }
        }

        /// <summary>Owns the writer and, for the zipped form, the archive behind it. Dispose order
        /// matters: the entry stream (inside the writer) must close before the archive.</summary>
        public sealed class Handle : IDisposable
        {
            public StreamingStepWriter Writer { get; }
            private readonly ZipArchive? _archive;
            private readonly IDisposable? _extra;
            private bool _done;

            internal Handle(StreamingStepWriter w, ZipArchive? archive, IDisposable? extra)
            { Writer = w; _archive = archive; _extra = extra; }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                Writer.Dispose();      // flushes and closes the entry stream
                _archive?.Dispose();   // writes the central directory
                _extra?.Dispose();
            }
        }

        /// <summary>
        /// Read an exported IFC back as text, plain or zipped. Streams either way — an export can be
        /// gigabytes, so nothing is ever read whole.
        /// </summary>
        public static TextReader OpenText(string path)
        {
            if (!IsZip(path)) return new StreamReader(path);

            var file = File.OpenRead(path);
            ZipArchive archive;
            try { archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false); }
            catch { file.Dispose(); throw; }

            try
            {
                // The .ifc entry, or failing that whatever single entry the archive holds — a
                // reader that is strict about the name would reject files other tools produce.
                var entry = archive.Entries.FirstOrDefault(e =>
                                e.Name.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                            ?? archive.Entries.FirstOrDefault();
                if (entry == null) throw new InvalidDataException("The IFC archive is empty.");
                return new ArchiveReader(new StreamReader(entry.Open(), Encoding.UTF8), archive);
            }
            catch { archive.Dispose(); throw; }
        }

        /// <summary>A StreamReader that also closes the archive it was opened from.</summary>
        private sealed class ArchiveReader : TextReader
        {
            private readonly StreamReader _inner;
            private readonly ZipArchive _archive;
            public ArchiveReader(StreamReader inner, ZipArchive archive) { _inner = inner; _archive = archive; }

            public override string? ReadLine() => _inner.ReadLine();
            public override int Read() => _inner.Read();
            public override int Peek() => _inner.Peek();
            public override int Read(char[] buffer, int index, int count) => _inner.Read(buffer, index, count);

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _inner.Dispose(); _archive.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
