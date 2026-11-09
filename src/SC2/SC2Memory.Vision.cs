using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// What each player can see right now, as VisIsVisibleForPlayer [exe+0x26FF460] asks [exe+0x6B0D60] (5.0.x addresses in
    /// brackets, all by code signature): a pointer encoded in four exe globals (lo = u32[g1] + u32[k1], hi = u32[g2] ^
    /// u32[k2]) leads to the vision object; a player's plane index is u32 @+0x10 + 4 * player, and each plane is a
    /// 256 x 256 bitmap - 8 u32 words a row - @+0x200D0 + plane * 0x2000: bit (x &amp; 31) of word [(plane &lt;&lt; 8) + y] * 8 +
    /// (x &gt;&gt; 5). Allies sharing vision share a plane. Re-read a few times a second.
    /// </summary>
    public sealed partial class SC2Memory
    {
        const string VisionPattern = "8B 05 ? ? ? ? 33 05 ? ? ? ? 8B 0D ? ? ? ? 03 0D ? ? ? ? 89 44 24 24 89 4C 24 20 41 0F B6 C0 4C 8B 44 24 20 41 8B 4C 80 ? 48 C1 E1 08 48 63 C2 48 03 C8 49 8B C1 48 C1 F8 05 41 83 E1 1F 48 8D 0C C8 33 C0 41 8B 8C 88 ? ? ? ?";
        const int VisionPlaneBytes = 256 * 8 * 4;

        ulong _visHiG, _visHiK, _visLoG, _visLoK;
        uint  _visPlaneOff, _visGridOff;
        DateTime _nextVision;
        VisionMap?[] _vision = new VisionMap?[16];

        public bool HasVisibility => _visGridOff != 0;

        /// <summary>What a player sees right now (with whoever shares vision with it), null when unknown.</summary>
        public VisionMap? Visibility(int player) => player is >= 0 and < 16 ? _vision[player] : null;

        string ResolveVision(List<(uint Rva, byte[] Data)> code)
        {
            _visGridOff = 0;
            _vision = new VisionMap?[16];
            if (code.Count > 0 && FindPattern(code, VisionPattern, 4) is { Count: > 0 } hits &&
                hits.Select(h => (RelTarget(code, h + 2), RelTarget(code, h + 8), RelTarget(code, h + 14), RelTarget(code, h + 20))).Distinct().Count() == 1)
            {
                uint h = hits[0];
                (_visHiG, _visHiK, _visLoG, _visLoK) = (RelTarget(code, h + 2) ?? 0, RelTarget(code, h + 8) ?? 0, RelTarget(code, h + 14) ?? 0, RelTarget(code, h + 20) ?? 0);
                _visPlaneOff = CodeBytes(code, h + 45, 1) is [var p] ? p : 0u;
                _visGridOff = CodeU32(code, h + 77) ?? 0;
                if (_visHiG == 0 || _visHiK == 0 || _visLoG == 0 || _visLoK == 0 || _visPlaneOff == 0 || _visGridOff is 0 or > 0x100000) _visGridOff = 0;
            }
            if (!HasVisibility) XMLogging.WriteLine("[Vision] signature 'visibility grid' missed - vision overlay estimates from sight");
            return $"vision {(HasVisibility ? "sig" : "missed")}";
        }

        /// <summary>The planes of the players asked for (deduplicated: allies share one).</summary>
        void ReadVision(IEnumerable<int> players)
        {
            if (!HasVisibility || DateTime.UtcNow < _nextVision) return;
            _nextVision = DateTime.UtcNow.AddMilliseconds(200);
            ulong obj = ((ulong)(U32At(_visHiG) ^ U32At(_visHiK)) << 32) | (U32At(_visLoG) + U32At(_visLoK));
            var result = new VisionMap?[16];
            if (!IsUserPtr(obj) || ReadBytes(obj + _visPlaneOff, 64) is not { } pt) { _vision = result; return; }
            var planes = new Dictionary<uint, VisionMap?>();
            foreach (int p in players.Where(p => p is >= 0 and < 16).Distinct())
            {
                uint plane = BinaryPrimitives.ReadUInt32LittleEndian(pt.AsSpan(p * 4));
                if (plane > 64) continue;
                if (!planes.TryGetValue(plane, out var map))
                    planes[plane] = map = ReadBytes(obj + _visGridOff + plane * (ulong)VisionPlaneBytes, VisionPlaneBytes) is { } b ? new VisionMap(b) : null;
                result[p] = map;
            }
            _vision = result;
        }
    }

    /// <summary>A 256 x 256 visibility bitmap: bit (x &amp; 31) of u32 word y * 8 + (x &gt;&gt; 5), map cells from the bottom-left.</summary>
    public sealed class VisionMap(byte[] bits)
    {
        public bool At(int x, int y) =>
            (uint)x < 256 && (uint)y < 256 && (bits[(y * 8 + (x >> 5)) * 4 + ((x & 31) >> 3)] >> (x & 7) & 1) != 0;
    }
}
