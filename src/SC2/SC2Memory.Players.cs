using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Player names, the way the PlayerHandle native (5.0.x exe+0x265C480) finds them:
    ///   O     = ((u32[g2] ^ u32[k2] ^ imm2) &lt;&lt; 32) | (u32[g1] + u32[k1] + imm1)    encoded pointer to a lobby object
    ///   slot  = first i with byte[O + SlotMapOff + i] == player                         (its vtable +0x120, exe+0x1542BA0)
    ///   lobby tables in the exe's data section, indexed by slot (exe+0x7210A0):
    ///     handles  H + slot * 0x20            { u32 len*4+1; u32; char[24] }   "1-S2-1-13035780"
    ///     clans    H + 0x200 + slot * 0x28    { u32 len*4+1; u32; char[..] }   "VAPR"
    ///     names    H + 0x480 + slot * 0x28    { u32 len*4+1; u32; char[32] }   "mason"
    /// Everything is located by code signature, so a patch that only moves addresses keeps working.
    /// </summary>
    public sealed partial class SC2Memory
    {
        // mov eax,[rcx+13Ch]; lea rcx,[rip+handles]; shl rax,5; add rax,rcx; ret
        const string LobbyHandlesPattern = "8B 81 3C 01 00 00 48 8D 0D ? ? ? ? 48 C1 E0 05 48 03 C1 C3";
        // mov rcx,rax; call handleHelper; mov ecx,[rax+4]; shr ecx,1; test cl,1   (PlayerHandle reading the handle string)
        const string LobbyHandleCallPattern = "48 8B C8 E8 ? ? ? ? 8B 48 04 D1 E9 F6 C1 01";
        // mov eax,[k1]; xor r9d,r9d; mov edx,[g1]; add eax,imm1; add edx,eax; movzx r8d,bl; mov eax,[g2]; xor eax,[k2]; xor eax,imm2
        const string LobbyObjectPattern  = "8B 05 ? ? ? ? 45 33 C9 8B 15 ? ? ? ? 05 ? ? ? ? 03 D0 44 0F B6 C3 8B 05 ? ? ? ? 33 05 ? ? ? ? 35 ? ? ? ?";
        // cmp [rcx+r10+slotMap],r8b
        const string SlotMapPattern      = "4C 8B D1 41 80 F8 10 74 ? 33 C9 0F 1F 44 00 00 46 38 84 11 ? ? ? ? 75";
        const int LobbyClansOff = 0x200, LobbyNamesOff = 0x480;

        ulong _lobbyHandlesRva;                                       // 0 = signatures missed
        ulong _lobbyK1, _lobbyG1, _lobbyG2, _lobbyK2; uint _lobbyImm1, _lobbyImm2;
        int _slotMapOff;
        string _lobbySigInfo = "";

        string[] _playerNames = new string[16];
        DateTime _nextNamesRead = DateTime.MinValue;

        string[] _playerClans = new string[16];

        /// <summary>Lobby name of a player ("" for AI / map-controlled players, or when unknown).</summary>
        public string PlayerName(int player) => player is >= 0 and < 16 ? _playerNames[player] ?? "" : "";

        /// <summary>Clan tag of a player ("" when none).</summary>
        public string PlayerClan(int player) => player is >= 0 and < 16 ? _playerClans[player] ?? "" : "";

        /// <summary>"&lt;VAPR&gt; mason", "mason" without a clan, "" when the name is unknown.</summary>
        public string PlayerDisplayName(int player) =>
            PlayerName(player) is { Length: > 0 } n ? (PlayerClan(player) is { Length: > 0 } c ? $"<{c}> {n}" : n) : "";

        static byte[]? CodeBytes(List<(uint Rva, byte[] Data)> code, uint rva, int len)
        {
            foreach (var (r, d) in code)
                if (rva >= r && rva + len <= r + (uint)d.Length) return d.AsSpan((int)(rva - r), len).ToArray();
            return null;
        }

        void ResolveLobby(List<(uint Rva, byte[] Data)> code)
        {
            _lobbyHandlesRva = 0;
            if (code.Count == 0) return;
            uint Rel(uint at) => (uint)((long)at + 4 + BinaryPrimitives.ReadInt32LittleEndian(CodeBytes(code, at, 4)));
            // the handle helper exists twice (the lobby tables have a second copy): take the one PlayerHandle calls
            var helpers = FindPattern(code, LobbyHandlesPattern, 4).ToHashSet();
            var o = FindPattern(code, LobbyObjectPattern, 2);
            var s = FindPattern(code, SlotMapPattern, 2);
            var h = o.Count != 1 ? [] : FindPattern(code, LobbyHandleCallPattern, 4096)
                        .Where(c => c > o[0] && c < o[0] + 0x400)                  // the call inside PlayerHandle
                        .Select(c => Rel(c + 4)).Where(helpers.Contains).Distinct().ToList();
            _lobbySigInfo = $"lobby sigs {h.Count}/{o.Count}/{s.Count}";
            if (h.Count != 1 || o.Count != 1 || s.Count != 1)
            {
                XMLogging.WriteLine($"[Actors] {_lobbySigInfo} - no player names");
                return;
            }
            uint b = o[0];
            _lobbyK1 = Rel(b + 2);  _lobbyG1 = Rel(b + 11); _lobbyImm1 = BinaryPrimitives.ReadUInt32LittleEndian(CodeBytes(code, b + 16, 4));
            _lobbyG2 = Rel(b + 28); _lobbyK2 = Rel(b + 34); _lobbyImm2 = BinaryPrimitives.ReadUInt32LittleEndian(CodeBytes(code, b + 39, 4));
            _slotMapOff = BinaryPrimitives.ReadInt32LittleEndian(CodeBytes(code, s[0] + 20, 4));
            _lobbyHandlesRva = Rel(h[0] + 9);
        }

        string LobbyString(int tableOff, int stride, int slot, int max)
        {
            var b = ReadBytes(_exeBase + _lobbyHandlesRva + (ulong)(tableOff + slot * stride), 8 + max);
            if (b == null) return "";
            int len = (int)(BinaryPrimitives.ReadUInt32LittleEndian(b) >> 2);
            return len is > 0 and <= 64 && len <= max ? System.Text.Encoding.UTF8.GetString(b, 8, len) : "";
        }

        /// <summary>Lobby slot -> player id (16 = empty slot), or null without the signatures.</summary>
        byte[]? ReadSlotMap()
        {
            if (_lobbyHandlesRva == 0) return null;
            ulong obj = ((ulong)(U32At(_lobbyG2) ^ U32At(_lobbyK2) ^ _lobbyImm2) << 32) | (U32At(_lobbyG1) + U32At(_lobbyK1) + _lobbyImm1);
            return obj > 0x10000 && ReadBytes(obj + (ulong)_slotMapOff, 16) is { Length: 16 } m ? m : null;
        }

        void ReadPlayerNames()
        {
            var names = new string[16];
            var clans = new string[16];
            if (ReadSlotMap() is { } map)
                for (int slot = 0; slot < 16; slot++)
                    if (map[slot] < 16 && LobbyString(LobbyNamesOff, 0x28, slot, 32) is { Length: > 0 } n)
                    {
                        names[map[slot]] = n;
                        clans[map[slot]] = LobbyString(LobbyClansOff, 0x28, slot, 16);
                    }
            _playerNames = names;
            _playerClans = clans;
        }
    }
}
