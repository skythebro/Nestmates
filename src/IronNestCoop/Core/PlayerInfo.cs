using IronNestCoop.Net.Wire;

namespace IronNestCoop.Core
{
    /// <summary>
    /// What each player shares about themselves: avatar colour and the look of their own cat.
    /// Carried in the session roster as an opaque blob, so adding fields never breaks the session layer.
    /// </summary>
    public sealed class PlayerInfo
    {
        const byte Format = 1;

        /// <summary>The avatar colour as a hue from 0 to 255. See Theme.PlayerColor.</summary>
        public byte Color;
        public bool CatEnabled = true;
        public bool CatIsKitten;
        public int CatHat, CatBody, CatEyes;
        public string CatName = "";
        /// <summary>The cat's shape sliders, stored as 3 bytes per slider: mesh flags (eyes 1, body 2, fur 4, whiskers 8),
        /// shape index, and value 0-100. It is appended last so that older readers simply stop before it.</summary>
        public byte[] CatShape = System.Array.Empty<byte>();

        public static PlayerInfo Local = new();

        public static byte[] Encode() => Local.ToBytes();

        public byte[] ToBytes()
        {
            var w = new NetWriter(64);
            w.U8(Format); w.U8(Color);
            w.Bool(CatEnabled); w.Bool(CatIsKitten);
            w.VarI(CatHat); w.VarI(CatBody); w.VarI(CatEyes);
            w.Str(CatName);
            w.Bytes(CatShape ?? System.Array.Empty<byte>());
            return w.ToArray();
        }

        public static PlayerInfo Decode(byte[] b)
        {
            var p = new PlayerInfo();
            if (b == null || b.Length == 0) return p;
            try
            {
                var r = new NetReader(b);
                if (r.U8() != Format) return p;
                p.Color = r.U8();
                p.CatEnabled = r.Bool(); p.CatIsKitten = r.Bool();
                p.CatHat = (int)r.VarI(); p.CatBody = (int)r.VarI(); p.CatEyes = (int)r.VarI();
                p.CatName = r.Str(64) ?? "";
                if (r.Remaining > 0) p.CatShape = r.Bytes(3 * 64) ?? System.Array.Empty<byte>();
            }
            catch (MalformedMessageException) { }
            return p;
        }
    }
}
