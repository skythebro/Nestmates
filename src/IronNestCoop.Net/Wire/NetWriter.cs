using System;
using System.Buffers.Binary;
using System.Text;

namespace IronNestCoop.Net.Wire
{
    /// <summary>Growable little-endian binary writer. Reuse with <see cref="Reset"/>.</summary>
    public sealed class NetWriter
    {
        byte[] _buf;
        int _pos;

        public NetWriter(int capacity = 256) { _buf = new byte[capacity]; }

        public int Length => _pos;
        public byte[] Buffer => _buf;

        public void Reset() => _pos = 0;

        public byte[] ToArray()
        {
            var a = new byte[_pos];
            System.Buffer.BlockCopy(_buf, 0, a, 0, _pos);
            return a;
        }

        void Ensure(int n)
        {
            if (_pos + n <= _buf.Length) return;
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));
        }

        public void U8(byte v) { Ensure(1); _buf[_pos++] = v; }
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
        public void U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_pos), v); _pos += 2; }
        public void I32(int v) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_pos), v); _pos += 4; }
        public void U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos), v); _pos += 4; }
        public void U64(ulong v) { Ensure(8); BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(_pos), v); _pos += 8; }
        public void F32(float v) { Ensure(4); BinaryPrimitives.WriteSingleLittleEndian(_buf.AsSpan(_pos), v); _pos += 4; }
        public void F64(double v) { Ensure(8); BinaryPrimitives.WriteDoubleLittleEndian(_buf.AsSpan(_pos), v); _pos += 8; }

        /// <summary>Unsigned LEB128.</summary>
        public void VarU(ulong v)
        {
            Ensure(10);
            while (v >= 0x80) { _buf[_pos++] = (byte)(v | 0x80); v >>= 7; }
            _buf[_pos++] = (byte)v;
        }

        /// <summary>Zig-zag signed varint.</summary>
        public void VarI(long v) => VarU((ulong)((v << 1) ^ (v >> 63)));

        /// <summary>Length-prefixed UTF-8; null and "" are distinct (prefix 0 = null).</summary>
        public void Str(string s)
        {
            if (s == null) { VarU(0); return; }
            int n = Encoding.UTF8.GetByteCount(s);
            VarU((ulong)n + 1);
            Ensure(n);
            Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, _pos);
            _pos += n;
        }

        public void Bytes(byte[] b) => Bytes(b, 0, b?.Length ?? 0);

        public void Bytes(byte[] b, int offset, int count)
        {
            if (b == null) { VarU(0); return; }
            VarU((ulong)count + 1);
            Raw(b, offset, count);
        }

        public void Raw(byte[] b, int offset, int count)
        {
            Ensure(count);
            System.Buffer.BlockCopy(b, offset, _buf, _pos, count);
            _pos += count;
        }

        public void Vec3(float x, float y, float z) { F32(x); F32(y); F32(z); }
        public void Quat(float x, float y, float z, float w) { F32(x); F32(y); F32(z); F32(w); }
    }
}
