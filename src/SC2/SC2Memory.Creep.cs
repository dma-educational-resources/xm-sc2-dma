using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Creep, as CreepIsPresent [exe+0x26B7760] reads it (5.0.x addresses in brackets, all by code signature): a pointer
    /// encoded in four exe globals (lo = u32[g1] ^ u32[k1] ^ imm, hi = ~(u32[g2] - u32[k2])) leads to the creep object;
    /// [exe+0x6906C0] then tests byte object[0x1C + (y &lt;&lt; 8) + x] (x, y whole map cells) - creep where its low 4 bits
    /// aren't 0. Re-read once a second; Version changes when any cell does.
    /// </summary>
    public sealed partial class SC2Memory
    {
        const string CreepGlobalPattern = "8B 05 ? ? ? ? 48 8D 95 ? ? ? ? 33 05 ? ? ? ? 44 8B C3 35 ? ? ? ? 89 44 24 28 8B 05 ? ? ? ? 2B 05 ? ? ? ? F7 D0 89 44 24 2C 48 8B 4C 24 28 E8";
        const string CreepTestPattern   = "48 83 EC 48 45 8B C8 4C 8B D1 45 85 C0 75 ? 48 63 42 04 48 63 12 48 C1 F8 0C 48 C1 E0 ? 48 03 C1 48 C1 FA 0C F6 44 02 ? 0F";

        ulong _creepG1, _creepK1, _creepG2, _creepK2;
        uint  _creepConst, _creepGridOff;
        int   _creepShift;
        DateTime _nextCreep;
        byte[]? _creepRaw;
        int _creepVersion;
        string _creepDiag = "";

        /// <summary>The creep grid, null when unknown (or the signature missed).</summary>
        public CreepMap? Creep { get; private set; }
        public bool HasCreep => _creepGridOff != 0;

        string ResolveCreep(List<(uint Rva, byte[] Data)> code)
        {
            _creepGridOff = 0; Creep = null; _creepRaw = null;
            // several natives decode the same object: take the hit whose call is the creep test, the globals must agree
            var hits = code.Count > 0 ? FindPattern(code, CreepGlobalPattern, 64)
                .Where(x => RelTarget(code, x + 55) is { } t && CodeBytes(code, t, 42) is { } b && FindPattern([((uint)t, b)], CreepTestPattern, 1).Count == 1).ToList() : [];
            if (hits.Select(x => (RelTarget(code, x + 2), RelTarget(code, x + 15), RelTarget(code, x + 33), RelTarget(code, x + 39))).Distinct().Count() == 1 &&
                hits[0] is var h && RelTarget(code, h + 55) is { } test && CodeBytes(code, test, 42) is { } tb)
            {
                _creepG1 = RelTarget(code, h + 2) ?? 0; _creepK1 = RelTarget(code, h + 15) ?? 0;
                _creepG2 = RelTarget(code, h + 33) ?? 0; _creepK2 = RelTarget(code, h + 39) ?? 0;
                _creepConst = CodeU32(code, h + 23) ?? 0;
                _creepShift = tb[29]; _creepGridOff = tb[40];
                if (_creepG1 == 0 || _creepK1 == 0 || _creepG2 == 0 || _creepK2 == 0 || _creepShift is < 6 or > 10) _creepGridOff = 0;
            }
            if (!HasCreep && code.Count > 0)
                _creepDiag = $"global hits {FindPattern(code, CreepGlobalPattern, 64).Count}, test hits {FindPattern(code, CreepTestPattern, 8).Count}, sections " +
                             string.Join(" ", code.Select(c => $"{c.Rva:X}+{c.Data.Length:X}"));
            if (!HasCreep) XMLogging.WriteLine("[Creep] signature 'creep grid' missed - no creep");
            return $"creep {(HasCreep ? "sig" : "missed")}";
        }
        /// <summary>A byte grid as a false-colour PNG (north up), one colour per value.</summary>
        static void SaveGrid(byte[] buf, int o, int stride, int cols, int rows, string path)
        {
            uint[] pal = [0xFF000000, 0xFFE6194B, 0xFF3CB44B, 0xFFFFE119, 0xFF4363D8, 0xFFF58231, 0xFF911EB4, 0xFF46F0F0, 0xFFF032E6, 0xFFBCF60C,
                          0xFFFABEBE, 0xFF008080, 0xFFE6BEFF, 0xFF9A6324, 0xFFFFFAC8, 0xFF800000];
            using var bmp = new System.Drawing.Bitmap(cols, rows, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    bmp.SetPixel(x, rows - 1 - y, System.Drawing.Color.FromArgb(unchecked((int)pal[buf[o + y * stride + x] % pal.Length])));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        /// <summary>Decode a TGA (raw or RLE, BGR(A)) and save it as a PNG.</summary>
        static void SaveTga(byte[] raw, int tw, int th, int px, bool rle, bool topDown, string path)
        {
            var pixels = new byte[tw * th * 4];
            int src = 18 + raw[0], dst = 0, total = tw * th;
            void Put(int s) { pixels[dst * 4] = raw[s]; pixels[dst * 4 + 1] = raw[s + 1]; pixels[dst * 4 + 2] = raw[s + 2]; pixels[dst * 4 + 3] = px == 4 ? raw[s + 3] : (byte)255; dst++; }
            while (dst < total && src < raw.Length)
            {
                if (!rle) { Put(src); src += px; continue; }
                int hdr = raw[src++], count = (hdr & 0x7F) + 1;
                if ((hdr & 0x80) != 0) { for (int k = 0; k < count && dst < total; k++) Put(src); src += px; }
                else for (int k = 0; k < count && dst < total; k++) { Put(src); src += px; }
            }
            using var bmp = new System.Drawing.Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, tw, th), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            for (int y = 0; y < th; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, (topDown ? y : th - 1 - y) * tw * 4, data.Scan0 + y * data.Stride, tw * 4);
            bmp.UnlockBits(data);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        void ReadCreep()
        {
            if (!HasCreep || DateTime.UtcNow < _nextCreep) return;
            _nextCreep = DateTime.UtcNow.AddSeconds(1);
            ulong obj = ((ulong)~(U32At(_creepG2) - U32At(_creepK2)) << 32) | (U32At(_creepG1) ^ U32At(_creepK1) ^ _creepConst);
            int stride = 1 << _creepShift;
            int rows = Math.Min(stride, Terrain?.H ?? stride), cols = Math.Min(stride, Terrain?.W ?? stride);
            if (!IsUserPtr(obj) || ReadBytes(obj + _creepGridOff, stride * rows) is not { } raw) { Creep = null; return; }
            if (_creepRaw != null && raw.AsSpan().SequenceEqual(_creepRaw) && Creep != null) return;
            _creepRaw = raw;
            var cells = new bool[cols * rows];
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                    cells[y * cols + x] = (raw[y * stride + x] & 0x0F) != 0;
            Creep = new CreepMap(cols, rows, cells, ++_creepVersion);
        }
    }

    /// <summary>Creep per map cell, row-major from the bottom-left (map 0,0).</summary>
    public sealed record CreepMap(int W, int H, bool[] Cells, int Version)
    {
        public bool At(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && Cells[y * W + x];
    }
}
