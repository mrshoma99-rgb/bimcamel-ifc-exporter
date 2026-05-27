using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BIMCamel.Ifc
{
    public enum IfcSchema { Ifc4, Ifc2x3 }

    /// <summary>
    /// Low-allocation STEP (ISO-10303-21) text emitter — the core of the performance pitch
    /// (IMPLEMENTATION_PLAN.md §3/§5; v3 Part A1). Two ways to emit an entity:
    ///
    ///  • <see cref="Write(string)"/> — convenience for the small, fixed skeleton/relationship
    ///    entities. Builds one string per entity; fine when the count is bounded.
    ///
    ///  • <see cref="Begin"/> + <see cref="Tok"/>/<see cref="Sep"/>/<see cref="WriteReal"/>/
    ///    <see cref="RefTok"/>/<see cref="WriteIntRaw"/> + <see cref="End"/> — the HOT path. Writes
    ///    "#id=TYPE(" then streams arguments straight to the buffered stream and finishes with ");".
    ///    A mesh therefore never becomes a multi-megabyte transient string and individual
    ///    coordinates never allocate (the v2 "huge files / slow" pain is in large part *our own*
    ///    string churn — this removes it).
    ///
    /// Callers write referenced entities first and pass the returned ids back in, so single-pass
    /// output needs no forward-reference reservation (STEP itself permits any reference order).
    /// </summary>
    public sealed class StreamingStepWriter : IDisposable
    {
        private readonly StreamWriter _w;
        private int _id;
        private long _bytes;
        private readonly char[] _num = new char[32]; // scratch for zero-alloc number formatting

        /// <summary>Approximate bytes written so far (exact char count; ASCII STEP ⇒ ≈ bytes). The
        /// exporter polls this at element boundaries to decide when to roll to a new split file.</summary>
        public long BytesWritten => _bytes;

        private void Emit(char c) { _w.Write(c); _bytes++; }
        private void Emit(string s) { _w.Write(s); _bytes += s.Length; }
        private void Emit(char[] buf, int n) { _w.Write(buf, 0, n); _bytes += n; }

        // Geometry-coordinate fractional digits. Tied to the weld tolerance by the caller so we never
        // write more precision than welding preserved (v4 file-size): 0.1 mm weld → 4, 1 mm → 3, etc.
        private readonly int _frac;
        private readonly long _fracPowL;
        private readonly double _fracPowD;

        public StreamingStepWriter(string path, int coordDecimals = 6)
        {
            _frac = coordDecimals < 1 ? 1 : (coordDecimals > 9 ? 9 : coordDecimals);
            _fracPowL = 1; for (int i = 0; i < _frac; i++) _fracPowL *= 10;
            _fracPowD = _fracPowL;
            // UTF-8 without BOM: accepted by modern IFC readers (Solibri, Navisworks, xBim).
            // 4 MB buffer keeps the hot path away from per-call flushing.
            _w = new StreamWriter(path, false, new UTF8Encoding(false), 4 << 20);
        }

        /// <summary>Write one fully-formed entity line and return its #id (convenience path).</summary>
        public int Write(string typeAndArgs)
        {
            int id = ++_id;
            Emit('#');
            WriteIntRaw(id);
            Emit('=');
            Emit(typeAndArgs);
            Emit(";\n");
            return id;
        }

        // ── streaming entity API (hot path) ────────────────────────────────────────

        /// <summary>Begin an entity: emits "#id=TYPENAME(" and returns the reserved id.</summary>
        public int Begin(string typeName)
        {
            int id = ++_id;
            Emit('#');
            WriteIntRaw(id);
            Emit('=');
            Emit(typeName);
            Emit('(');
            return id;
        }

        /// <summary>Append a raw, already-formed token fragment (e.g. "(", ")", "$").</summary>
        public void Tok(string s) => Emit(s);

        /// <summary>Append a single raw character.</summary>
        public void Tok(char c) => Emit(c);

        /// <summary>Append an argument separator ','.</summary>
        public void Sep() => Emit(',');

        /// <summary>Append a reference token "#id" without allocating.</summary>
        public void RefTok(int id) { Emit('#'); WriteIntRaw(id); }

        /// <summary>
        /// Stream a STEP string literal (quote, double embedded quotes, strip newlines) straight to
        /// the buffered writer — no StringBuilder/string allocation, unlike <see cref="Str"/>. Empty
        /// → "$". Used on the property hot path where there are millions of literals.
        /// </summary>
        public void WriteStr(string? s)
        {
            if (string.IsNullOrEmpty(s)) { Emit('$'); return; }
            Emit('\'');
            foreach (char ch in s!)
            {
                if (ch == '\'') Emit("''");
                else if (ch == '\r' || ch == '\n') Emit(' ');
                else Emit(ch);
            }
            Emit('\'');
        }

        /// <summary>Close the current entity: emits ");\n".</summary>
        public void End() => Emit(");\n");

        /// <summary>Write a non-negative (or signed) integer straight to the stream, no allocation.</summary>
        public void WriteIntRaw(long val)
        {
            if (val == 0) { Emit('0'); return; }
            int p = 0;
            bool neg = val < 0;
            ulong v = neg ? (ulong)(-val) : (ulong)val;
            while (v > 0) { _num[p++] = (char)('0' + (int)(v % 10)); v /= 10; }
            if (neg) Emit('-');
            for (int i = p - 1; i >= 0; i--) Emit(_num[i]);
        }

        /// <summary>
        /// Write a STEP real (always a '.', never exponent) straight to the stream with no string
        /// allocation, matching the "0.0##########" shape (≤10 fractional digits, trailing zeros
        /// trimmed but at least one kept). Geometry coordinates are near the origin after the
        /// base-point offset, so the fast path covers them; rare large magnitudes fall back to the
        /// allocating formatter.
        /// </summary>
        public void WriteReal(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { Emit("0.0"); return; }

            // Fast path where the split formatter stays exact (intPart fits, f*1e6 < 1e6 ≪ 2^53).
            // Geometry sits near the origin after the base-point offset, so it lands here.
            if (v < 1.0e7 && v > -1.0e7)
            {
                WriteRealFast(v);
                return;
            }
            Emit(R(v));
        }

        private void WriteRealFast(double v)
        {
            bool neg = v < 0;
            double av = neg ? -v : v;

            // Split integer/fraction so the integer part is never scaled (which would overflow
            // double's 2^53 exact-integer range). f < 1, so f*1e6 < 1e6 and stays exact.
            long intPart = (long)av;
            double f = av - intPart;
            long frac = (long)(f * _fracPowD + 0.5);                 // round half up; 0 .. 10^frac
            if (frac >= _fracPowL) { frac -= _fracPowL; intPart++; }  // rounding carry

            var buf = _num;
            int p = 0;
            if (neg && (intPart != 0 || frac != 0)) buf[p++] = '-';

            if (intPart == 0) buf[p++] = '0';
            else
            {
                int start = p;
                long t = intPart;
                while (t > 0) { buf[p++] = (char)('0' + (int)(t % 10)); t /= 10; }
                for (int a = start, b = p - 1; a < b; a++, b--) { var tmp = buf[a]; buf[a] = buf[b]; buf[b] = tmp; }
            }

            buf[p++] = '.';

            // _frac fractional digits, most-significant first
            long div = _fracPowL / 10;
            int fracStart = p;
            for (int i = 0; i < _frac; i++) { buf[p++] = (char)('0' + (int)(frac / div)); frac %= div; div /= 10; }

            // trim trailing zeros, keep at least one fractional digit
            int end = p - 1;
            while (end > fracStart && buf[end] == '0') end--;
            p = end + 1;

            Emit(buf, p);
        }

        // ── static helpers (used by the convenience path) ──────────────────────────

        public static string Ref(int id) => "#" + id.ToString(CultureInfo.InvariantCulture);

        /// <summary>STEP real: always contains a '.', never uses exponent notation (full precision —
        /// used for survey-scale values like the IfcSite offset / IfcMapConversion).</summary>
        public static string R(double v) =>
            v.ToString("0.0##########", CultureInfo.InvariantCulture);

        /// <summary>STEP real capped at 6 fractional digits (micron) — for near-origin values such as
        /// instance translations, mirroring the streamed geometry precision (v4).</summary>
        public static string R6(double v) =>
            v.ToString("0.0#####", CultureInfo.InvariantCulture);

        /// <summary>STEP string literal: wrap in quotes, double embedded quotes.</summary>
        public static string Str(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "$";
            var sb = new StringBuilder(s!.Length + 2);
            sb.Append('\'');
            foreach (var ch in s!)
            {
                if (ch == '\'') sb.Append("''");
                else if (ch == '\r' || ch == '\n') sb.Append(' ');
                else sb.Append(ch);
            }
            sb.Append('\'');
            return sb.ToString();
        }

        public void WriteHeader(IfcSchema schema, string fileName, string author)
        {
            string schemaId = schema == IfcSchema.Ifc4 ? "IFC4" : "IFC2X3";
            string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

            Emit("ISO-10303-21;\n");
            Emit("HEADER;\n");
            Emit("FILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\n");
            Emit($"FILE_NAME({Str(fileName)},'{ts}',({Str(author)}),(''),'BIMCamel IFC Exporter','BIMCamel','');\n");
            Emit($"FILE_SCHEMA(('{schemaId}'));\n");
            Emit("ENDSEC;\n");
            Emit("DATA;\n");
        }

        public void WriteFooter()
        {
            Emit("ENDSEC;\n");
            Emit("END-ISO-10303-21;\n");
            _w.Flush();
        }

        public void Dispose() => _w.Dispose();
    }
}
