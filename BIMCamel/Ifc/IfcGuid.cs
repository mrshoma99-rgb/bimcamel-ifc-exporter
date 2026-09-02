using System;
using System.Numerics;

namespace BIMCamel.Ifc
{
    /// <summary>
    /// Converts a .NET <see cref="Guid"/> (e.g. ModelItem.InstanceGuid) to the 22-character
    /// IFC "compressed" GlobalId encoding (base-64 over the IFC alphabet).
    ///
    /// Phase-0 acceptance criterion (IMPLEMENTATION_PLAN.md §10): the mapping MUST be
    /// deterministic — the same input Guid always yields the same 22-char string — so that
    /// re-exports keep stable GlobalIds and IFC diffing/coordination stays usable.
    ///
    /// CROSS-CHECKED. The note that used to sit here said the byte ordering below still had to be
    /// checked against the GlobalId other tools assign to the same element, and that cross-tool
    /// identity depended on that check. It has now been done, against the standard IFC compression
    /// as implemented in CamelWorks.Core (one byte to two characters, then five groups of three
    /// bytes to four characters each): twenty thousand random GUIDs, no disagreement, and the two
    /// vectors in <see cref="MatchesTheStandardCompression"/> pin it.
    ///
    /// The two arrive the same way for a reason worth writing down, because it looks like a
    /// coincidence: the standard's first chunk spends two characters - twelve bits of room - on one
    /// byte, and 22 characters hold 132 bits against a GUID's 128. The four bits of slack are the
    /// same four bits, so the chunked form and the plain big-endian number below agree digit for
    /// digit, and every chunk after the first is 24 bits into 24 bits with nothing left over.
    ///
    /// This matters beyond IFC diffing. The CamelWorks clash manager writes BCF whose components
    /// name elements by GlobalId, and a federation of Revit-sourced NWCs carries no GlobalId
    /// property at all - so it computes the id this exporter would give the element, from the same
    /// instance GUID. That link holds only while these two encodings agree.
    /// </summary>
    public static class IfcGuid
    {
        // The 64-character IFC base-64 alphabet (note: this is NOT standard base64).
        private const string Alphabet =
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

        public static string ToIfcGuid(Guid guid)
        {
            // .NET Guid.ToByteArray() stores Data1(4)/Data2(2)/Data3(2) little-endian and
            // Data4(8) big-endian. Reorder to a canonical big-endian 16-byte sequence so the
            // GUID reads as a single 128-bit number, most-significant byte first.
            var b = guid.ToByteArray();
            var be = new byte[16]
            {
                b[3], b[2], b[1], b[0],   // Data1
                b[5], b[4],               // Data2
                b[7], b[6],               // Data3
                b[8], b[9], b[10], b[11], // Data4 (already in order)
                b[12], b[13], b[14], b[15]
            };

            // Interpret as an unsigned 128-bit big integer. BigInteger consumes little-endian,
            // so reverse; append a 0x00 to force a positive value.
            var le = new byte[17];
            for (int i = 0; i < 16; i++) le[i] = be[15 - i];
            le[16] = 0;
            var value = new BigInteger(le);

            // Emit 22 base-64 digits, big-endian. 22*6 = 132 bits; the leading digit holds the
            // top 2 bits (range 0..3), the remaining 21 digits hold 6 bits each = 128 bits.
            var chars = new char[22];
            for (int i = 21; i >= 0; i--)
            {
                value = BigInteger.DivRem(value, 64, out var rem);
                chars[i] = Alphabet[(int)rem];
            }
            return new string(chars);
        }

        /// <summary>Self-test used by the Phase-0 spike: proves determinism.</summary>
        public static bool VerifyStable(Guid guid) =>
            ToIfcGuid(guid) == ToIfcGuid(guid);

        /// <summary>
        /// Self-test: the encoding still agrees with the standard IFC compression.
        ///
        /// These two vectors were produced by both this method and the chunked implementation in
        /// CamelWorks.Core and found identical. A change to the byte ordering or the alphabet
        /// above breaks this, which is the point: the clash manager computes element ids by the
        /// other implementation and expects to get the same answer this one writes into the file.
        /// </summary>
        public static bool MatchesTheStandardCompression() =>
            ToIfcGuid(Guid.Parse("11111111-2222-3333-4444-555555555555")) == "0H4H4H8Y8pCqH4LLLLLLLL"
            && ToIfcGuid(Guid.Empty) == "0000000000000000000000";
    }
}
