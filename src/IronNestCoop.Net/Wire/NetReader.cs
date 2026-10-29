using System;
using System.Buffers.Binary;
using System.Text;

namespace IronNestCoop.Net.Wire
{
    /// <summary>Thrown when a message is shorter than its fields claim. Callers drop the message.</summary>
    public sealed class MalformedMessageException : Exception
    {
        public MalformedMessageException(string m) : base(m) { }
    }

    /// <summary>Bounds-checked little-endian reader over a byte segment.</summary>
    public sealed class NetReader
    {
        readonly byte[] _buf;
        readonly int _end;
        int _pos;

        public NetReader(byte[] buf) : this(buf, 0, buf.Length) { }

        public NetReader(byte[] buf, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > buf.Length) throw new ArgumentOutOfRangeException();
            _buf = buf; _pos = offset; _end = offset + count;
        }

        public int Remaining => _end - _pos;
        public int Position => _pos;

        void Need(int n)
        {
            if (n < 0 || _pos + n > _end) throw new MalformedMessageException($"need {n} bytes, have {_end - _pos}");
        }

        public byte U8() { Need(1); return _buf[_pos++]; }
        public bool Bool() => U8() != 0;
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(_pos)); _pos += 2; return v; }
        public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(_pos)); _pos += 4; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_pos)); _pos += 4; return v; }
        public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(_buf.AsSpan(_pos)); _pos += 8; return v; }
        public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(_buf.AsSpan(_pos)); _pos += 4; return v; }
        public double F64() { Need(8); var v = BinaryPrimitives.ReadDoubleLittleEndian(_buf.AsSpan(_pos)); _pos += 8; return v; }

        public ulong VarU()
        {
            ulong v = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                byte b = U8();
                v |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return v;
            }
            throw new MalformedMessageException("varint too long");
        }

        public long VarI() { ulong u = VarU(); return (long)(u >> 1) ^ -(long)(u & 1); }

        /// <summary>A count of things that follow, at most <paramref name="max"/> (else the message is malformed).
        /// Read as unsigned: a cast of a huge varint to int went negative and slipped past Math.Min caps.</summary>
        public int Count(int max, string what)
        {
            ulong n = VarU();
            if (n > (ulong)max) throw new MalformedMessageException($"{n} {what}, at most {max}");
            return (int)n;
        }

        int Len(int max, string what)
        {
            ulong n = VarU();
            if (n == 0) return -1;
            if (n - 1 > (ulong)max) throw new MalformedMessageException($"{what} of {n - 1} bytes exceeds {max}");
            int len = (int)(n - 1);
            Need(len);
            return len;
        }

        public string Str(int maxBytes = 1 << 20)
        {
            int len = Len(maxBytes, "string");
            if (len < 0) return null;
            var s = Encoding.UTF8.GetString(_buf, _pos, len);
            _pos += len;
            return s;
        }

        public byte[] Bytes(int maxBytes = 64 << 20)
        {
            int len = Len(maxBytes, "blob");
            if (len < 0) return null;
            var a = new byte[len];
            Buffer.BlockCopy(_buf, _pos, a, 0, len);
            _pos += len;
            return a;
        }

        /// <summary>The next <paramref name="n"/> bytes as a copy (no length prefix).</summary>
        public byte[] Raw(int n)
        {
            Need(n);
            var a = new byte[n];
            Buffer.BlockCopy(_buf, _pos, a, 0, n);
            _pos += n;
            return a;
        }

        public void Skip(int n) { Need(n); _pos += n; }
    }
}
