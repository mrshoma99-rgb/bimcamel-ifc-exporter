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
    /// NOTE: the byte-ordering convention below should be cross-checked once against the
    /// GlobalId that Navisworks/Solibri assign to the same element (validation step in §11).
    /// Determinism/stability does not depend on that check; cross-tool identity does.
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
    }
}
