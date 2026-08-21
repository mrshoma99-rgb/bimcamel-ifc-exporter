using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CamelWorks.Core.Report
{
    /// <summary>
    /// A PNG turned into the two streams a PDF image needs: the colour data, and the alpha mask.
    ///
    /// PDF and PNG are closer than they look. PDF's FlateDecode filter understands PNG's own
    /// per-row predictors, so an opaque PNG's compressed data can go straight into a PDF with no
    /// decoding at all — which is both far faster and far less to get wrong than decoding and
    /// re-encoding several megabytes of screenshot.
    ///
    /// Transparency is where that stops. PDF keeps alpha in a separate soft-mask image, so an RGBA
    /// PNG has to be decompressed, unfiltered, split into colour and alpha, and re-compressed. That
    /// path exists because Navisworks snapshots routinely have an alpha channel, and the choice
    /// otherwise would be between a wrong picture and no picture.
    /// </summary>
    public sealed class PngImage
    {
        private PngImage(int width, int height, byte[] data, string colourSpace, int colours,
                         byte[]? palette, byte[]? alpha, bool passedThrough)
        {
            Width = width; Height = height; Data = data; ColourSpace = colourSpace;
            Colours = colours; Palette = palette; Alpha = alpha; PassedThrough = passedThrough;
        }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>The colour data, zlib-compressed, ready for a FlateDecode stream.</summary>
        public byte[] Data { get; }

        /// <summary>The PDF colour space name: DeviceRGB, DeviceGray, or Indexed.</summary>
        public string ColourSpace { get; }

        /// <summary>Components per sample — 3 for RGB, 1 for grey or palette.</summary>
        public int Colours { get; }

        /// <summary>The palette, for an indexed image.</summary>
        public byte[]? Palette { get; }

        /// <summary>The alpha channel, zlib-compressed, or null when the image is opaque.</summary>
        public byte[]? Alpha { get; }

        /// <summary>
        /// True when the PNG's own compressed data was used unchanged.
        ///
        /// Worth knowing: it means the row predictors are still in the stream and the PDF image
        /// dictionary has to declare them. When false the data was decoded and re-compressed flat,
        /// and declaring a predictor would corrupt it.
        /// </summary>
        public bool PassedThrough { get; }

        /// <summary>
        /// Read a PNG.
        /// </summary>
        /// <param name="bytes">The file.</param>
        /// <param name="image">The result.</param>
        /// <returns>
        /// False for anything this does not handle — 16-bit samples, interlaced files, a truncated
        /// stream. The caller draws a labelled placeholder rather than a broken image, because a
        /// report with an obviously missing picture is fixable and a report with a corrupt one is
        /// not even noticed.
        /// </returns>
        public static bool TryRead(byte[]? bytes, out PngImage? image)
        {
            image = null;
            if (bytes == null || bytes.Length < 8) return false;

            var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (var i = 0; i < signature.Length; i++)
                if (bytes[i] != signature[i]) return false;

            int width = 0, height = 0, bitDepth = 0, colourType = 0, interlace = 0;
            byte[]? palette = null;
            var idat = new MemoryStream();
            var sawHeader = false;

            var offset = 8;
            while (offset + 8 <= bytes.Length)
            {
                var length = ReadInt(bytes, offset);
                if (length < 0 || offset + 12 + length > bytes.Length) break;

                var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
                var start = offset + 8;

                switch (type)
                {
                    case "IHDR":
                        if (length < 13) return false;
                        width = ReadInt(bytes, start);
                        height = ReadInt(bytes, start + 4);
                        bitDepth = bytes[start + 8];
                        colourType = bytes[start + 9];
                        interlace = bytes[start + 12];
                        sawHeader = true;
                        break;

                    case "PLTE":
                        palette = new byte[length];
                        Array.Copy(bytes, start, palette, 0, length);
                        break;

                    case "IDAT":
                        // Split across chunks in any real file, and the split can fall mid-symbol,
                        // so the pieces are concatenated before anything looks at them.
                        idat.Write(bytes, start, length);
                        break;
                }

                if (type == "IEND") break;
                offset = start + length + 4;
            }

            if (!sawHeader || width <= 0 || height <= 0 || idat.Length == 0) return false;
            if (bitDepth != 8 || interlace != 0) return false;

            var compressed = idat.ToArray();

            switch (colourType)
            {
                case 0:   // grey
                    image = new PngImage(width, height, compressed, "DeviceGray", 1, null, null, true);
                    return true;

                case 2:   // rgb
                    image = new PngImage(width, height, compressed, "DeviceRGB", 3, null, null, true);
                    return true;

                case 3:   // palette
                    if (palette == null) return false;
                    image = new PngImage(width, height, compressed, "Indexed", 1, palette, null, true);
                    return true;

                case 4:   // grey + alpha
                case 6:   // rgb + alpha
                    return TrySplitAlpha(compressed, width, height, colourType, out image);

                default:
                    return false;
            }
        }

        // The slow path, taken only when there is an alpha channel to separate. Decompress,
        // undo the row filters, pull the channels apart, and re-compress each side flat.
        private static bool TrySplitAlpha(byte[] compressed, int width, int height, int colourType,
                                          out PngImage? image)
        {
            image = null;

            var colours = colourType == 6 ? 3 : 1;
            var samples = colours + 1;

            byte[] raw;
            try
            {
                raw = Inflate(compressed, height * ((width * samples) + 1));
            }
            catch (InvalidDataException)
            {
                return false;
            }

            var stride = width * samples;
            if (raw.Length < height * (stride + 1)) return false;

            var colour = new byte[width * height * colours];
            var alpha = new byte[width * height];
            var previous = new byte[stride];
            var current = new byte[stride];

            for (var y = 0; y < height; y++)
            {
                var rowStart = y * (stride + 1);
                var filter = raw[rowStart];
                Array.Copy(raw, rowStart + 1, current, 0, stride);

                Unfilter(filter, current, previous, samples);

                for (var x = 0; x < width; x++)
                {
                    for (var c = 0; c < colours; c++)
                        colour[((y * width) + x) * colours + c] = current[(x * samples) + c];

                    alpha[(y * width) + x] = current[(x * samples) + colours];
                }

                var swap = previous;
                previous = current;
                current = swap;
            }

            image = new PngImage(width, height, Deflate(colour),
                                 colours == 3 ? "DeviceRGB" : "DeviceGray", colours,
                                 null, Deflate(alpha), false);
            return true;
        }

        // PDF's PNG predictors and PNG's own row filters are the same five, which is why the
        // opaque path can hand the stream over untouched. Here they have to be undone by hand.
        private static void Unfilter(byte filter, byte[] row, byte[] previous, int step)
        {
            switch (filter)
            {
                case 0:
                    break;

                case 1:   // Sub
                    for (var i = step; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - step]);
                    break;

                case 2:   // Up
                    for (var i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + previous[i]);
                    break;

                case 3:   // Average
                    for (var i = 0; i < row.Length; i++)
                    {
                        int left = i >= step ? row[i - step] : 0;
                        row[i] = (byte)(row[i] + ((left + previous[i]) / 2));
                    }

                    break;

                case 4:   // Paeth
                    for (var i = 0; i < row.Length; i++)
                    {
                        // Typed as bytes rather than left to inference: a ternary of a byte and
                        // the literal 0 has type int, which does not match Paeth's parameters.
                        var left = i >= step ? row[i - step] : (byte)0;
                        var upLeft = i >= step ? previous[i - step] : (byte)0;
                        row[i] = (byte)(row[i] + Paeth(left, previous[i], upLeft));
                    }

                    break;
            }
        }

        private static byte Paeth(byte a, byte b, byte c)
        {
            var p = a + b - c;
            var pa = Math.Abs(p - a);
            var pb = Math.Abs(p - b);
            var pc = Math.Abs(p - c);

            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        /// <summary>
        /// Decompress a zlib stream.
        ///
        /// The two-byte zlib header is skipped rather than parsed: the framework's DeflateStream
        /// speaks raw deflate and would reject it. PNG only ever uses deflate, so there is nothing
        /// in that header worth reading.
        /// </summary>
        private static byte[] Inflate(byte[] zlib, int expected)
        {
            using (var input = new MemoryStream(zlib, 2, zlib.Length - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(expected > 0 ? expected : 0))
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        /// <summary>
        /// Compress to a zlib stream.
        ///
        /// The header and the Adler-32 are added by hand for the same reason: DeflateStream
        /// produces raw deflate, and a PDF FlateDecode stream is specified as zlib. Some readers
        /// tolerate the raw form; a file that only works in some readers is not a deliverable.
        /// </summary>
        private static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);

                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(data, 0, data.Length);

                var adler = Adler32(data);
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);

                return output.ToArray();
            }
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;

            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        private static int ReadInt(byte[] bytes, int offset) =>
            (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}
