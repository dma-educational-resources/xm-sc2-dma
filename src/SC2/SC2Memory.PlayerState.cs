using System.Buffers.Binary;
using System.Numerics;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Per-player state and other per-match lookups, from the Galaxy natives (5.0.x addresses in brackets; all by
    /// code signature):
    ///   players        PlayerGetPropertyInt -> player lookup [exe+0x746850]: an array pointer encoded in four exe globals
    ///                  (lo = g1 - k1, hi = ~g2 + k2); each 8-byte slot is decoded by one of 16 routines picked by a
    ///                  start-up selector - run with MiniEmu. Objects are stable for a match, so decoded once per mission.
    ///                  minerals / vespene: encoded pairs @+0x348 / +0x350 ([exe+0x67F970], same encoding as supply;
    ///                  the ints @+0xA68 / +0xA6C the property switch reads first are overridden), supply limit fixed @+0xA64,
    ///                  supply used / made: encoded fixed @+0x300 / +0x2E8 (two key-table lookups, see DecodeSupply)
    ///   start points   PlayerStartLocation -> [exe+0x809300]: if the enabled flag is set, int index[player * 40] into a
    ///                  list of 24-byte entries, point (fixed x, y) @+4
    ///   resources      UnitGetPropertyInt(c_unitPropResources) -> [exe+0x9B04F0]: the unit's behaviour of type 11;
    ///                  behaviours: list @ record+0x110, *(list+0x18) &amp; ~1 = first chunk {next @+0 (bit 0 = end),
    ///                  items @+8, count byte @+0x48}; type = constant returned by vtable[0xF8]; amount int @+0x94
    /// </summary>
    public sealed partial class SC2Memory
    {
        // ── players ──────────────────────────────────────────────────────────
        const string PlayerArrayPattern = "8B 05 ? ? ? ? 2B 05 ? ? ? ? 89 45 E8 8B 05 ? ? ? ? F7 D0 03 05 ? ? ? ? 89 45 EC";
        static readonly CodeSig[] PlayerArraySigs =
        [
            new("player array g1", PlayerArrayPattern, 2), new("player array k1", PlayerArrayPattern, 8),
            new("player array g2", PlayerArrayPattern, 17), new("player array k2", PlayerArrayPattern, 25),
        ];
        const string PlayerSlotPattern = "48 8B 45 E8 41 0F B6 D3 48 8B 08 8B 05 ? ? ? ? 48 8D 14 D1 83 F8 0F 0F 87 ? ? ? ? 48 8D 0D ? ? ? ? 8B 84 81 ? ? ? ?";
        static readonly CodeSig PlayerSlotSelectorSig = new("player slot decoder selector", PlayerSlotPattern, 13);
        const string PlayerSupplyPattern = "48 8D 54 24 ? 48 8B CF E8 ? ? ? ? 8B 08 89 0B EB ? 48 8D 54 24 ? 48 8B CF E8 ? ? ? ? 8B 08 89 0B EB ? 8B 87 ? ? ? ?";
        const string SupplyDecodePattern = "45 8B 0B 41 8B C1 45 8B 43 04 C1 E8 0C 41 8D 89";
        // the real bank: props 0..3 go through [exe+0x67F970](player, resource): "lea eax,[rdx+68h]; lea r8,[rcx+rax*8]",
        // then the encoded pair at r8 + 8 (same encoding as the supply fields) - minerals +0x348, vespene +0x350
        const string BankGetterPattern = "8D 42 ? 4C 8D 04 C1 8B 05 ? ? ? ? 4C 8D 15";
        // there are three such getters; the bank's is the one PlayerGetPropertyInt calls for props 0..3:
        // "cmp esi,3; ja; cmp esi,4; jae; mov edx,esi; mov rcx,rbx; call getter"
        const string BankCallPattern = "83 FE 03 77 ? 83 FE 04 0F 83 ? ? ? ? 8B D6 48 8B CB E8 ? ? ? ?";

        ulong[] _playerArrayRvas = [];
        ulong   _slotSelectorRva;
        uint    _slotCaseTableRva;
        uint    _mineralsOff, _gasOff, _supplyLimitOff, _supplyUsedOff, _supplyMadeOff, _supplyKey;
        ulong   _supplyKeysRva;
        uint[]  _supplyKeys = [];
        readonly ulong[] _playerObjs = new ulong[16];
        DateTime _nextPlayerDecode = DateTime.MinValue;
        PlayerStats?[] _playerStats = new PlayerStats?[16];

        /// <summary>Bank and supply of a player, null when unknown.</summary>
        public PlayerStats? GetPlayerStats(int player) => player is >= 0 and < 16 ? _playerStats[player] : null;

        // PlayerType [exe+0x2664850] reads byte player object + 5 (c_playerType*: 0 none, 1 user, 2 computer, 3 neutral,
        // 4 hostile, 5 referee, 6 spectator). Game-rule state: the melee win check reads it.
        const ulong PlayerTypeOff = 5;
        public const byte PlayerTypeUser = 1, PlayerTypeSpectator = 6;

        /// <summary>A player's type, null when its player object isn't known.</summary>
        public byte? PlayerType(int player) =>
            player is >= 0 and < 16 && _playerObjs[player] != 0 && ReadBytes(_playerObjs[player] + PlayerTypeOff, 1) is [var t] ? t : null;

        public bool SetPlayerType(int player, byte type, ScatterWriteHandle? writes = null) =>
            player is >= 0 and < 16 && _playerObjs[player] != 0 && Write(_playerObjs[player] + PlayerTypeOff, type, writes);
        public bool HasPlayerStats => _playerArrayRvas.Length == 4 && _mineralsOff != 0;

        // ── start locations ──────────────────────────────────────────────────
        const string StartPattern = "83 3D ? ? ? ? 00 4C 8B CA 74 ? 0F B6 C1 48 8D 0C 80 48 8D 05 ? ? ? ? 8B 04 C8 85 C0 78 ? 3B 05 ? ? ? ? 7D ? 48 8D 0C 40 48 8B 05 ? ? ? ?";
        static readonly CodeSig[] StartSigs =
        [
            new("start locations flag", StartPattern, 2, 1), new("start locations index", StartPattern, 22),
            new("start locations count", StartPattern, 35), new("start locations list", StartPattern, 48),
        ];
        ulong[] _startRvas = [];
        Vector2?[] _starts = new Vector2?[16];
        /// <summary>Where a player started, null when the map has no start location for it.</summary>
        public Vector2? StartLocation(int player) => player is >= 0 and < 16 ? _starts[player] : null;

        // ── resources ────────────────────────────────────────────────────────
        const string ResourcePattern = "8D 53 ? E8 ? ? ? ? 48 85 C0 74 ? 44 8B 88 ? ? ? ?";
        const string BehaviourTypePattern = "48 8B 07 48 8B CF FF 90 ? ? ? ? 41 3B C6 75";
        uint _behaviourListOff, _behaviourTypeSlot, _resourceAmountOff;
        int  _resourceBehaviourType = -1;
        readonly Dictionary<ulong, ulong> _resourceBehaviour = [];   // record -> its resource behaviour (0 = none)
        readonly Dictionary<ulong, int> _vtableType = [];              // behaviour vtable -> type constant (-1 unknown)

        /// <summary>Like ResolveSig, but several matches are fine when they all reference the same address
        /// (the game has near-identical copies of some functions).</summary>
        static ulong? ResolveSigAgree(List<(uint Rva, byte[] Data)> code, CodeSig sig)
        {
            var targets = FindPattern(code, sig.Pattern, 8).Select(h => RelTarget(code, h + (uint)sig.DispOffset) is { } t ? t + (ulong)sig.InstrTail : 0).Distinct().ToList();
            return targets is [var only] && only != 0 ? only : null;
        }

        /// <summary>The immediate at <paramref name="offset"/> of every match, when they all agree.</summary>
        static uint? SigImm32Agree(List<(uint Rva, byte[] Data)> code, string pattern, int offset)
        {
            var vals = FindPattern(code, pattern, 8).Select(h => CodeU32(code, h + (uint)offset)).Distinct().ToList();
            return vals is [{ } only] ? only : null;
        }

        static uint? CodeU32(List<(uint Rva, byte[] Data)> code, ulong rva)
        {
            foreach (var (r, d) in code)
                if (rva >= r && rva + 4 <= r + (ulong)d.Length) return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)(rva - r)));
            return null;
        }

        static byte[]? CodeBytes(List<(uint Rva, byte[] Data)> code, ulong rva, int len)
        {
            foreach (var (r, d) in code)
                if (rva >= r && rva + (ulong)len <= r + (ulong)d.Length) return d.AsSpan((int)(rva - r), len).ToArray();
            return null;
        }

        string ResolvePlayerState(List<(uint Rva, byte[] Data)> code)
        {
            var parts = new List<string>();
            // players
            var pr = PlayerArraySigs.Select(s => code.Count > 0 ? ResolveSigAgree(code, s) : null).ToArray();
            _playerArrayRvas = pr.All(r => r is not null) ? pr.Select(r => r!.Value).ToArray() : [];
            _slotSelectorRva  = code.Count > 0 && ResolveSigAgree(code, PlayerSlotSelectorSig) is { } sel ? sel : 0;
            // the copies have their own (equivalent) case tables: any of them will do
            _slotCaseTableRva = code.Count > 0 && FindPattern(code, PlayerSlotPattern, 8) is [var first, ..] ? CodeU32(code, first + 40) ?? 0 : 0;
            _mineralsOff = _gasOff = _supplyLimitOff = _supplyUsedOff = _supplyMadeOff = 0;
            if (code.Count > 0 && FindPattern(code, BankCallPattern, 2) is [var call] && RelTarget(code, call + 20) is { } getter &&
                FindPattern(code, BankGetterPattern, 8).Contains((uint)getter) && CodeBytes(code, getter + 2, 1) is [var idx])
            {
                _mineralsOff = (uint)idx * 8 + 8;   // resource 0, encoded pair
                _gasOff      = _mineralsOff + 8;    // resource 1
            }
            if (code.Count > 0 && FindPattern(code, PlayerSupplyPattern, 2) is [var sup])
            {
                _supplyLimitOff = CodeU32(code, sup + 40) ?? 0;
                ulong? used = RelTarget(code, sup + 9), made = RelTarget(code, sup + 28);
                // each getter: "mov [rsp+10h],rbx; mov eax,[rip+..]; lea r11,[rcx+field]; lea rbx,[rip+keys]"
                (uint Field, ulong Keys) Getter(ulong? fn) =>
                    fn is { } f && CodeBytes(code, f, 25) is { } b && b[11] == 0x4C && b[12] == 0x8D && b[13] == 0x99 && b[18] == 0x48 && b[19] == 0x8D && b[20] == 0x1D
                        ? (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(14)), (ulong)((long)f + 25 + BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(21)))) : (0, 0);
                var (uf, uk) = Getter(used);
                var (mf, _)  = Getter(made);
                _supplyUsedOff = uf; _supplyMadeOff = mf; _supplyKeysRva = uk;
                // the decode constant: "lea ecx,[r9+imm32]" a little further into the used getter
                if (used is { } u && CodeBytes(code, u, 0x200) is { } body)
                {
                    var (pb, _) = ParsePattern(SupplyDecodePattern);
                    int at = body.AsSpan().IndexOf(pb);
                    _supplyKey = at >= 0 && at + pb.Length + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + pb.Length)) : 0;
                }
            }
            bool playersOk = _playerArrayRvas.Length == 4 && _slotSelectorRva != 0 && _slotCaseTableRva != 0 && _mineralsOff != 0;
            parts.Add($"players {(playersOk ? "sig" : "missed")}{(playersOk && _supplyKey == 0 ? " (no supply)" : "")}");
            if (!playersOk) _playerArrayRvas = [];

            // start locations
            var st = StartSigs.Select(s => code.Count > 0 ? ResolveSig(code, s, out _) : null).ToArray();
            _startRvas = st.All(r => r is not null) ? st.Select(r => r!.Value).ToArray() : [];
            parts.Add($"starts {(_startRvas.Length == 4 ? "sig" : "missed")}");

            // resources
            _behaviourListOff = _resourceAmountOff = _behaviourTypeSlot = 0; _resourceBehaviourType = -1;
            if (code.Count > 0 && FindPattern(code, ResourcePattern, 4) is { Count: > 0 } rhits)
                foreach (var h in rhits)
                {
                    // "lea edx,[rbx+type]; call get; ...; mov r9d,[rax+amount]", get = "add rcx,list; jmp"
                    if (RelTarget(code, h + 4) is not { } get || CodeBytes(code, get, 8) is not [0x48, 0x81, 0xC1, _, _, _, _, 0xE9]) continue;
                    _resourceBehaviourType = CodeBytes(code, h + 2, 1)![0];
                    _behaviourListOff  = CodeU32(code, get + 3) ?? 0;
                    _resourceAmountOff = CodeU32(code, h + 16) ?? 0;
                    break;
                }
            _behaviourTypeSlot = code.Count > 0 ? SigImm32Agree(code, BehaviourTypePattern, 8) ?? 0 : 0;
            bool resOk = _behaviourListOff != 0 && _resourceAmountOff != 0 && _behaviourTypeSlot != 0;
            parts.Add($"resources {(resOk ? "sig" : "missed")}");
            if (!resOk) _resourceBehaviourType = -1;
            foreach (var p in parts.Where(p => p.Contains("missed"))) XMLogging.WriteLine($"[Players] signature '{p}'");
            return string.Join(", ", parts);
        }

        void ResetPlayerState()
        {
            _camTargets = new System.Numerics.Vector2?[16];
            _upgrades = new Dictionary<string, int>[16];
            _upgradeIds.Clear();
            Array.Clear(_playerObjs);
            _playerStats = new PlayerStats?[16];
            _starts = new Vector2?[16];
            _nextPlayerDecode = DateTime.MinValue;
            _resourceBehaviour.Clear();
            _vtableType.Clear();
            _queueAbility.Clear();
            _orderVtType.Clear();
        }

        /// <summary>Decode the 16 player objects (stable during a match; redone every 30 s and on a mission change).</summary>
        void DecodePlayers()
        {
            Array.Clear(_playerObjs);
            if (_playerArrayRvas.Length != 4) return;
            ulong P = ((ulong)(~U32At(_playerArrayRvas[2]) + U32At(_playerArrayRvas[3])) << 32) | (U32At(_playerArrayRvas[0]) - U32At(_playerArrayRvas[1]));
            ulong arr = IsUserPtr(P) ? ReadU64(P) : 0;
            if (!IsUserPtr(arr)) return;
            uint sel = U32At(_slotSelectorRva);
            if (sel > 15) return;
            uint caseRva = U32At(_slotCaseTableRva + sel * 4);
            // case: "lea rcx,[rbp+28h]; call decoder"
            var cb = caseRva != 0 ? ReadBytes(_exeBase + caseRva, 9) : null;
            if (cb is not [0x48, 0x8D, 0x4D, _, 0xE8, _, _, _, _]) return;
            ulong decoder = (ulong)((long)caseRva + 9 + BinaryPrimitives.ReadInt32LittleEndian(cb.AsSpan(5)));
            if (_supplyKeysRva != 0 && ReadBytes(_exeBase + _supplyKeysRva, 0x4000) is { Length: 0x4000 } kb)
                _supplyKeys = MemoryMarshal.Cast<byte, uint>(kb).ToArray();
            var emu = new MiniEmu(this, null);
            for (int p = 0; p < 16; p++)
            {
                try { _playerObjs[p] = emu.Call(decoder, MiniEmu.OutBuf, arr + (ulong)p * 8) && emu.ReadOut(0, 8) is var o && IsUserPtr(o) ? o : 0; }
                catch (Exception ex) { _playerObjs[p] = 0; XMLogging.WriteLine($"[Players] decode {p}: {ex.Message}"); }
            }
        }

        /// <summary>The supply fields' encoding (getters at [exe+0x69BBD0] / [exe+0x69BCD0]).</summary>
        float DecodeSupply(ReadOnlySpan<byte> s) => _supplyKeys.Length != 4096 || _supplyKey == 0 ? -1f : (int)DecodeSupplyRaw(s) / 4096f;

        uint DecodeSupplyRaw(ReadOnlySpan<byte> s)
        {
            if (_supplyKeys.Length != 4096) return 0;
            uint r9 = BinaryPrimitives.ReadUInt32LittleEndian(s), r8 = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
            r8 += _supplyKeys[((r9 >> 12) ^ (r9 + _supplyKey)) & 0xFFF];
            r9 -= _supplyKeys[~(r8 - (r8 >> 12)) & 0xFFF];
            return ((r8 ^ r9) & 0x55555555) ^ r9;
        }

        void ReadPlayerState()
        {
            if (_playerArrayRvas.Length == 4)
            {
                if (DateTime.UtcNow >= _nextPlayerDecode) { _nextPlayerDecode = DateTime.UtcNow.AddSeconds(30); DecodePlayers(); }
                var stats = new PlayerStats?[16];
                var live = Enumerable.Range(0, 16).Where(p => _playerObjs[p] != 0).ToArray();
                if (live.Length > 0)
                {
                    // per player: bank (encoded minerals + gas pairs), supply limit (fixed), supply used / made (encoded)
                    const int sz = 16, per = 4;
                    var addrs = new List<ulong>();
                    foreach (int p in live)
                    {
                        addrs.Add(_playerObjs[p] + _mineralsOff);
                        addrs.Add(_playerObjs[p] + _supplyLimitOff);
                        addrs.Add(_playerObjs[p] + _supplyUsedOff);
                        addrs.Add(_playerObjs[p] + _supplyMadeOff);
                    }
                    var buf = new byte[addrs.Count * sz]; var ok = new bool[addrs.Count];
                    if (_gasOff == _mineralsOff + 8 && ReadValues(addrs.ToArray(), sz, buf, ok))
                        for (int i = 0; i < live.Length; i++)
                        {
                            if (!ok[i * per] || !ok[i * per + 1]) continue;
                            var bank = buf.AsSpan(i * per * sz, sz);
                            int minerals = (int)DecodeSupplyRaw(bank[..8]);
                            int gas      = (int)DecodeSupplyRaw(bank[8..16]);
                            float limit  = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan((i * per + 1) * sz)) / 4096f;
                            float used = _supplyUsedOff != 0 && ok[i * per + 2] ? DecodeSupply(buf.AsSpan((i * per + 2) * sz, 8)) : -1f;
                            float made = _supplyMadeOff != 0 && ok[i * per + 3] ? DecodeSupply(buf.AsSpan((i * per + 3) * sz, 8)) : -1f;
                            if (minerals is < 0 or > 100_000_000 || gas is < 0 or > 100_000_000) continue;   // not a player object any more
                            stats[live[i]] = new PlayerStats(minerals, gas, used, made, limit);
                        }
                    if (stats.All(s => s == null)) _nextPlayerDecode = DateTime.MinValue;
                }
                _playerStats = stats;
            }
            ReadStartLocations();
        }

        void ReadStartLocations()
        {
            if (_startRvas.Length != 4) return;
            var starts = new Vector2?[16];
            if (U32At(_startRvas[0]) != 0 && ReadBytes(_exeBase + _startRvas[1], 16 * 40) is { } idx)
            {
                int count = (int)U32At(_startRvas[2]);
                ulong list = U64At(_startRvas[3]);
                if (count is > 0 and < 64 && IsUserPtr(list) && ReadBytes(list, count * 24) is { } lb)
                    for (int p = 0; p < 16; p++)
                    {
                        int i = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(p * 40));
                        if (i < 0 || i >= count) continue;
                        float x = BinaryPrimitives.ReadInt32LittleEndian(lb.AsSpan(i * 24 + 4)) / 4096f, y = BinaryPrimitives.ReadInt32LittleEndian(lb.AsSpan(i * 24 + 8)) / 4096f;
                        if (x is > 0 and < 512 && y is > 0 and < 512) starts[p] = new Vector2(x, y);
                    }
            }
            _starts = starts;
        }

        /// <summary>Remaining amount per resource record (the resource behaviour, found once per record).</summary>
        Dictionary<ulong, int> ReadResourceAmounts(IEnumerable<ulong> records)
        {
            var result = new Dictionary<ulong, int>();
            if (_resourceBehaviourType < 0) return result;
            var wanted = records.ToHashSet();
            var fresh = wanted.Where(r => !_resourceBehaviour.ContainsKey(r)).ToArray();
            if (fresh.Length > 0 && FindBehaviours(fresh, _resourceBehaviourType) is { } found)
                for (int i = 0; i < fresh.Length; i++) _resourceBehaviour[fresh[i]] = found[i];
            var have = _resourceBehaviour.Where(kv => kv.Value != 0 && wanted.Contains(kv.Key)).ToArray();
            if (have.Length == 0) return result;
            var buf = new byte[have.Length * 4]; var ok = new bool[have.Length];
            if (ReadValues(have.Select(kv => kv.Value + _resourceAmountOff).ToArray(), 4, buf, ok))
                for (int i = 0; i < have.Length; i++)
                    if (ok[i]) result[have[i].Key] = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i * 4));
            return result;
        }

        /// <summary>
        /// <see cref="FindBehaviour"/> for many records in a few batched reads: the behaviour walk for all of them, then
        /// every instance's vtable. 0 where a record has none; null when a batched read failed (tried again next time).
        /// </summary>
        ulong[]? FindBehaviours(ulong[] recs, int type)
        {
            if (ReadBehaviourInstances(recs, _behaviourListOff + 0x18, maxNodes: 32) is not { } insts) return null;
            var vts = ReadPointers([.. insts.Select(x => x.Inst)]);
            var found = new ulong[recs.Length];
            for (int k = 0; k < insts.Count; k++)
                if (found[insts[k].Unit] == 0 && vts[k] != 0 && BehaviourType(vts[k]) == type) found[insts[k].Unit] = insts[k].Inst;
            return found;
        }

        /// <summary>A behaviour class's type id: its vtable slot is "mov eax, imm32; ret" (cached per vtable), -1 if not.</summary>
        int BehaviourType(ulong vt)
        {
            if (_vtableType.TryGetValue(vt, out int t)) return t;
            ulong fn = vt >= _exeBase && vt < _exeEnd ? ReadU64(vt + _behaviourTypeSlot) : 0;
            var code = fn >= _exeBase && fn < _exeEnd ? ReadBytes(fn, 6) : null;
            return _vtableType[vt] = code is [0xB8, _, _, _, _, 0xC3] ? BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(1)) : -1;
        }

        ulong FindBehaviour(ulong rec, int type)
        {
            ulong head = ReadU64(rec + _behaviourListOff + 0x18);
            ulong c = IsUserPtr(head) ? ReadU64(head) & ~1UL : 0;
            for (int guard = 0; IsUserPtr(c) && guard < 32; guard++)
            {
                var cb = ReadBytes(c, 0x50);
                if (cb == null) break;
                for (int i = 0; i < Math.Min(8, (int)cb[0x48]); i++)
                {
                    ulong obj = BinaryPrimitives.ReadUInt64LittleEndian(cb.AsSpan(8 + i * 8));
                    if (obj is <= 0x10000 or >= 0x7FFFFFFFFFFF || (obj & 3) != 0) continue;   // behaviours are only 4-aligned
                    if (BehaviourType(ReadU64(obj)) == type) return obj;
                }
                ulong next = BinaryPrimitives.ReadUInt64LittleEndian(cb);
                c = (next & 1) != 0 ? 0 : next;
            }
            return 0;
        }
    }

    /// <summary>A player's bank and supply. Supply fields are -1 when the encoding wasn't found.</summary>
    public readonly record struct PlayerStats(int Minerals, int Gas, float SupplyUsed, float SupplyMade, float SupplyLimit)
    {
        /// <summary>The cap the game shows: supply made, limited by the player's supply limit and 200.</summary>
        public float SupplyCap => SupplyMade < 0 ? -1f : MathF.Min(MathF.Min(SupplyMade, SupplyLimit > 0 ? SupplyLimit : 200f), 200f);
    }
}
