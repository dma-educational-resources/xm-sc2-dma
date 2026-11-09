
namespace SC2Radar.SC2
{
    /// <summary>
    /// The fog-of-war switch map triggers flip with VisEnable(c_visTypeFog, ...) (5.0.x addresses in brackets, all by code
    /// signature). VisIsEnabled [exe+0x26FF420] asks [exe+0x67B9F0] for type 1 (fog): a pointer encoded in four exe globals
    /// (lo = u32[g1] ^ u32[k1], hi = u32[g2] ^ u32[k2] ^ imm) leads to the vision settings, whose u32 @+0x14 is a mask of
    /// the players fog is off for, encoded by one of 16 routine pairs picked by the player-slot selector (getter cases
    /// decode, setter [exe+0x67B580] cases encode). VisEnable writes 0 or ~0 there; the vision update [exe+0x697130]
    /// then treats every player in the mask as seeing the whole map. Synced simulation state: writing it on one client
    /// makes that client's game diverge from everyone else's.
    /// </summary>
    public sealed partial class SC2Memory
    {
        // VisIsEnabled: "sub rsp,28h; test ecx,ecx; je mask; cmp ecx,1; je fog; xor al,al; add rsp,28h; ret;
        //  fog: call getter; test eax,eax; sete al; ...; mask: call ...; test eax,eax; sete al"
        const string VisIsEnabledPattern = "48 83 EC 28 85 C9 74 ? 83 F9 01 74 ? 32 C0 48 83 C4 28 C3 E8 ? ? ? ? 85 C0 0F 94 C0 48 83 C4 28 C3 E8 ? ? ? ? 85 C0 0F 94 C0";
        // the fog getter: decode the settings pointer, then switch on the selector into a decoder of [ptr + field]
        const string FogGetterPattern = "48 83 EC 28 8B 05 ? ? ? ? 33 05 ? ? ? ? 89 44 24 30 8B 05 ? ? ? ? 33 05 ? ? ? ? 35 ? ? ? ? 89 44 24 34 48 8B 54 24 30 8B 05 ? ? ? ? 48 83 C2 ? 83 F8 0F 0F 87 ? ? ? ? 4C 8D 05 ? ? ? ? 41 8B 8C 80 ? ? ? ?";
        // the fog setter, same shape with the encoders: "...; mov rcx,[rbp+30h]; mov eax,[sel]; add rcx,field; ...; mov edx,[rdi+rax*4+table]"
        const string FogSetterPattern = "89 4C 24 08 55 57 48 8B EC 48 83 EC 38 8B 05 ? ? ? ? 33 05 ? ? ? ? 89 45 30 8B 05 ? ? ? ? 33 05 ? ? ? ? 35 ? ? ? ? 89 45 34 48 8B 4D 30 8B 05 ? ? ? ? 48 83 C1 ? 83 F8 0F 0F 87 ? ? ? ? 48 8D 3D ? ? ? ? 8B 94 87 ? ? ? ?";

        ulong _fogLoG, _fogLoK, _fogHiG, _fogHiK;
        uint  _fogConst, _fogField, _fogGetCases, _fogSetCases;
        ulong _fogDecoder, _fogEncoder;
        string _fogDiag = "";

        public bool HasFogSwitch => _fogGetCases != 0 && _fogSetCases != 0;

        string ResolveFog(List<(uint Rva, byte[] Data)> code)
        {
            _fogGetCases = _fogSetCases = 0; _fogDecoder = _fogEncoder = 0;
            if (code.Count > 0)
            {
                // VisIsEnabled's type-1 call is the fog getter; its setter is the one writing the same field of the same pointer
                var getters = FindPattern(code, VisIsEnabledPattern, 8)
                    .Select(h => RelTarget(code, h + 21) is { } t && CodeBytes(code, t, 81) is { } b && FindPattern([((uint)t, b)], FogGetterPattern, 1).Count == 1 ? (uint)t : 0u)
                    .Where(t => t != 0).Distinct().ToList();
                if (getters is [var g])
                {
                    (_fogLoG, _fogLoK, _fogHiG, _fogHiK) = (RelTarget(code, g + 6) ?? 0, RelTarget(code, g + 12) ?? 0, RelTarget(code, g + 22) ?? 0, RelTarget(code, g + 28) ?? 0);
                    _fogConst = CodeU32(code, g + 33) ?? 0;
                    _fogField = CodeBytes(code, g + 55, 1) is [var f] ? f : 0u;
                    uint getCases = CodeU32(code, g + 76) ?? 0;
                    var setters = FindPattern(code, FogSetterPattern, 64)
                        .Where(s => (RelTarget(code, s + 15), RelTarget(code, s + 21), RelTarget(code, s + 30), RelTarget(code, s + 36)) == (_fogLoG, _fogLoK, _fogHiG, _fogHiK)
                                    && CodeU32(code, s + 41) == _fogConst && CodeBytes(code, s + 61, 1) is [var sf] && sf == _fogField)
                        .Select(s => CodeU32(code, s + 81) ?? 0).Where(t => t != 0).Distinct().ToList();
                    _fogDiag = $"getter exe+0x{g:X}, field 0x{_fogField:X}, cases {getCases:X}, setters {setters.Count}";
                    if (_fogLoG != 0 && _fogLoK != 0 && _fogHiG != 0 && _fogHiK != 0 && _fogField != 0 && getCases != 0 && setters is [var sc])
                        (_fogGetCases, _fogSetCases) = (getCases, sc);
                }
                else _fogDiag = $"getters {getters.Count}";
            }
            if (!HasFogSwitch) XMLogging.WriteLine($"[Fog] signature 'fog switch' missed ({_fogDiag}) - map reveal unavailable");
            return $"fog {(HasFogSwitch ? "sig" : "missed")}";
        }

        /// <summary>Whether the local player is the only human: the others (computer and map players) have no lobby name.
        /// False when the local player or the names aren't known.</summary>
        public bool OnlyHumanInGame =>
            LocalPlayer is >= 1 and <= 15 && PlayerName(LocalPlayer).Length > 0 &&
            !Enumerable.Range(1, 15).Any(p => p != LocalPlayer && PlayerName(p).Length > 0);

        ulong FogSettings() =>
            ((ulong)(U32At(_fogHiG) ^ U32At(_fogHiK) ^ _fogConst) << 32) | (U32At(_fogLoG) ^ U32At(_fogLoK));

        /// <summary>The selector's case of a 16-way switch: the routine its "lea reg,[..]; call routine" calls.</summary>
        ulong FogCase(uint cases)
        {
            uint sel = _slotSelectorRva != 0 ? U32At(_slotSelectorRva) : 99;
            uint caseRva = sel <= 15 ? U32At(cases + sel * 4) : 0;
            // "lea rcx,[rsp+30h]" (48 8D 4C 24 30) or "lea rdx,[rbp+18h]" (48 8D 55 18), then the call
            if (caseRva == 0 || ReadBytes(_exeBase + caseRva, 10) is not { } b) return 0;
            int call = b[0] == 0x48 && b[1] == 0x8D && b[2] == 0x4C ? 5 : b[0] == 0x48 && b[1] == 0x8D && b[2] == 0x55 ? 4 : -1;
            if (call < 0 || b[call] != 0xE8 || ReadBytes(_exeBase + caseRva + (uint)call + 1, 4) is not { } rel) return 0;
            return (ulong)((long)caseRva + call + 5 + BitConverter.ToInt32(rel));
        }

        /// <summary>The players fog of war is off for (bit per player), null when it can't be read.</summary>
        public uint? FogDisabledMask()
        {
            if (!HasFogSwitch) return null;
            if (_fogDecoder == 0) _fogDecoder = FogCase(_fogGetCases);
            ulong s = FogSettings();
            if (_fogDecoder == 0 || !IsUserPtr(s)) return null;
            try
            {
                var emu = new MiniEmu(this, null);
                return emu.Call(_fogDecoder, MiniEmu.OutBuf, s + _fogField) ? (uint)emu.ReadOut(0, 4) : null;
            }
            catch { return null; }
        }

        /// <summary>Write (or queue into <paramref name="writes"/>) the mask of players fog of war is off for. False when it
        /// can't be; a direct write is read back.</summary>
        public bool SetFogDisabledMask(uint mask, ScatterWriteHandle? writes = null)
        {
            if (!HasFogSwitch) return false;
            if (_fogEncoder == 0) _fogEncoder = FogCase(_fogSetCases);
            ulong s = FogSettings();
            if (_fogEncoder == 0 || !IsUserPtr(s)) return false;
            try
            {
                // the encoder reads the plain value at rdx and writes the encoded one to rcx; run it on a copy
                var emu = new MiniEmu(this, null);
                if (!emu.Call(_fogEncoder, MiniEmu.OutBuf, MiniEmu.OutBuf + 0x100, input: (MiniEmu.OutBuf + 0x100, mask))) return false;
                uint enc = (uint)emu.ReadOut(0, 4);
                return Write(s + _fogField, enc, writes) && (writes != null || FogDisabledMask() == mask);
            }
            catch { return false; }
        }
    }
}
