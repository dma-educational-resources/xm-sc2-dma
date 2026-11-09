using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Upgrades each player has finished, as TechTreeUpgradeCount [exe+0xA676F0] counts them (5.0.x addresses in brackets,
    /// offsets by signature):
    ///   tech tree = *(*(exe+0x3B62568) + 0x6F8); + 0xD0 holds an encoded pointer, decoded by [exe+0x6859A0] - one of 16
    ///   routines picked by the player-slot selector (case table exe+0x685B38, each "mov rdx,rcx; lea rcx,[rsp+30h]; call").
    ///   The lookup [exe+0x82C230] keeps one hash map per kind; upgrades (kind 1): bucket count @+0xA8, buckets @ *(+0xB8);
    ///   a bucket points at the node before its chain, nodes link through *(node) (bit 0 = end), entry = node -
    ///   [exe+0x38F8118]; the id @ entry+0x148 is a string (len &lt;&lt; 2 @+0x18, flags @+0x1C - bit 1 = heap pointer - and the
    ///   characters @+0x20). Counts [exe+0x829700]: per player u16 ^ 0xA23B; completed @+0xCC + 2 * player.
    /// An entry appears once anyone starts the upgrade, so the map stays small.
    /// </summary>
    public sealed partial class SC2Memory
    {
        const string UpTreePattern     = "48 8B 05 ? ? ? ? 48 8D 4C 24 ? 48 8B 98 ? ? ? ? E8";
        const string UpObjPattern      = "48 8D 8B ? ? ? ? E8 ? ? ? ? 48 8B C8 48 8D 54 24 ? 48 8D 85";
        const string UpDispatchPattern = "48 83 EC 28 8B 05 ? ? ? ? 83 F8 0F 0F 87 ? ? ? ? 4C 8D 05 ? ? ? ? 41 8B 94 80 ? ? ? ?";
        const string UpLookupPattern   = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8B 1A 41 8B F8 48 8B F1 48 85 DB 74 ? 48 8B 03 48 8B CB FF 50 10 85 FF 0F 84 ? ? ? ? 83 EF 01 0F 84 ? ? ? ?";
        const string UpCountPattern    = "C0 E8 02 A8 01 74 ? 41 0F B6 C2 41 0F B7 8C 43 ? ? ? ? 81 F1 ? ? ? ?";

        ulong _upTreeRva, _upCasesRva, _upNodeRva;
        uint  _upTreeOff, _upObjOff, _upMaskOff, _upBucketsOff, _upKeyOff, _upCountOff;
        ushort _upXor;
        ulong _upDecoder;
        DateTime _nextUpgrades;
        Dictionary<string, int>[] _upgrades = new Dictionary<string, int>[16];
        public bool HasUpgrades => _upCountOff != 0;

        /// <summary>A player's finished upgrades: id ("ZergMeleeWeaponsLevel2", "zerglingmovementspeed") -> count.</summary>
        public IReadOnlyDictionary<string, int> Upgrades(int player) =>
            player is >= 0 and < 16 && _upgrades[player] is { } d ? d : EmptyUpgrades;
        static readonly Dictionary<string, int> EmptyUpgrades = [];

        string ResolveUpgrades(List<(uint Rva, byte[] Data)> code)
        {
            _upCountOff = 0; _upDecoder = 0;
            _upgrades = new Dictionary<string, int>[16];
            _upgradeIds.Clear();
            if (code.Count > 0)
                try { ResolveUpgradesCore(code); }
                catch (Exception ex) { _upCountOff = 0; XMLogging.WriteLine($"[Upgrades] resolve: {ex.Message}"); }
            if (!HasUpgrades) XMLogging.WriteLine("[Upgrades] signature 'upgrade counts' missed - no upgrade levels");
            return $"upgrades {(HasUpgrades ? "sig" : "missed")}";
        }

        void ResolveUpgradesCore(List<(uint Rva, byte[] Data)> code)
        {
            // the tree global and the object slot: "mov rax,[rip+g]; lea rcx,[rsp+x]; mov rbx,[rax+tree]; call ...",
            // a little later "lea rcx,[rbx+obj]; call dispatch"
            var tree = FindPattern(code, UpTreePattern, 32)
                .Select(h => (G: RelTarget(code, h + 3) ?? 0, Off: CodeU32(code, h + 15) ?? 0, At: h))
                .Where(t => t.G != 0 && FindPattern(code, UpObjPattern, 64).Any(o => o > t.At && o - t.At < 0x80)).Distinct().ToList();
            if (tree is not [var tr]) return;
            uint objHit = FindPattern(code, UpObjPattern, 64).First(o => o > tr.At && o - tr.At < 0x80);
            ulong dispatch = RelTarget(code, objHit + 8) ?? 0;
            if (CodeBytes(code, dispatch, 34) is not { } db || FindPattern([((uint)dispatch, db)], UpDispatchPattern, 1).Count == 0) return;
            (_upTreeRva, _upTreeOff, _upObjOff) = (tr.G, tr.Off, CodeU32(code, objHit + 3) ?? 0);
            _upCasesRva = BinaryPrimitives.ReadUInt32LittleEndian(db.AsSpan(30));

            // the lookup's upgrade branch ("sub edi,1; je kind1")
            if (FindPattern(code, UpLookupPattern, 2) is not [var lk] || RelTarget(code, lk + 51) is not { } block || CodeBytes(code, block, 0x70) is not { } bb) return;
            var blk = new List<(uint Rva, byte[] Data)> { ((uint)block, bb) };
            uint? I32At(string pattern, int off) => FindPattern(blk, pattern, 1) is [var h] ? CodeU32(blk, h + (uint)off) : null;
            _upKeyOff     = I32At("48 39 98 ? ? ? ?", 3) ?? 0;
            _upMaskOff    = I32At("8B 86 ? ? ? ? 85 C0 74", 2) ?? 0;
            _upBucketsOff = I32At("48 8B 86 ? ? ? ? 48 8B 14 C8", 3) ?? 0;
            _upNodeRva    = FindPattern(blk, "4C 8B 0D ? ? ? ?", 1) is [var n] ? RelTarget(blk, n + 3) ?? 0 : 0;
            if (_upKeyOff is 0 or > 0x400 || _upMaskOff is 0 or > 0x400 || _upBucketsOff is 0 or > 0x400 || _upNodeRva == 0) return;

            // the finished-count getter: "shr al,2; test al,1; je; ...; movzx ecx,word [r11+rax*2+count]; xor ecx,key"
            if (FindPattern(code, UpCountPattern, 2) is not [var c] || CodeU32(code, c + 16) is not (> 0 and < 0x400) || CodeU32(code, c + 22) is not { } x) return;
            _upXor = (ushort)x;
            _upCountOff = CodeU32(code, c + 16)!.Value;
        }

        /// <summary>Walk the upgrade map: every couple of seconds is plenty (upgrades take minutes).</summary>
        void ReadUpgrades()
        {
            if (!HasUpgrades || DateTime.UtcNow < _nextUpgrades) return;
            _nextUpgrades = DateTime.UtcNow.AddSeconds(2);
            if (_upDecoder == 0)
            {
                uint sel = _slotSelectorRva != 0 ? U32At(_slotSelectorRva) : 99;
                uint caseRva = sel <= 15 ? U32At(_upCasesRva + sel * 4) : 0;
                var cb = caseRva != 0 ? ReadBytes(_exeBase + caseRva, 16) : null;
                int call = cb == null ? -1 : Array.IndexOf(cb, (byte)0xE8);
                if (call < 0 || call + 5 > cb!.Length) return;
                _upDecoder = (ulong)((long)caseRva + call + 5 + BinaryPrimitives.ReadInt32LittleEndian(cb.AsSpan(call + 1)));
            }
            ulong tree = ReadU64(U64At(_upTreeRva) + _upTreeOff);
            if (!IsUserPtr(tree)) return;
            var emu = new MiniEmu(this, null);
            ulong obj;
            try { obj = emu.Call(_upDecoder, MiniEmu.OutBuf, tree + _upObjOff) ? emu.ReadOut(0, 8) : 0; } catch { return; }
            if (!IsUserPtr(obj) || ReadBytes(obj + _upMaskOff, 4) is not { } mb) return;
            uint buckets = BinaryPrimitives.ReadUInt32LittleEndian(mb);
            ulong arr = ReadU64(obj + _upBucketsOff), nodeOff = U64At(_upNodeRva);
            if (buckets is 0 or > 4096 || !IsUserPtr(arr) || ReadBytes(arr, (int)buckets * 8) is not { } ab) return;

            // every bucket's chain, walked for all buckets at once: one batched read per chain depth
            var entries = new HashSet<ulong>();
            var heads = new List<ulong>();
            for (int b = 0; b < buckets; b++)
                if (BinaryPrimitives.ReadUInt64LittleEndian(ab.AsSpan(b * 8)) is var p && IsUserPtr(p)) heads.Add(p);
            var nodes = ReadPointers(CollectionsMarshal.AsSpan(heads)).Select(n => n & ~1UL).Where(IsUserPtr).ToList();
            for (int depth = 0; depth < 256 && nodes.Count > 0; depth++)
            {
                nodes = [.. nodes.Where(n => entries.Add(n - nodeOff))];
                var next = ReadPointers(CollectionsMarshal.AsSpan(nodes));
                nodes = [.. next.Where(n => (n & 1) == 0 && IsUserPtr(n))];
            }

            // every entry's key and per-player counts in one batched read; ids are cached per key
            var list = entries.ToArray();
            var reads = ReadRanges([.. list.SelectMany(e => new[] { (e + _upKeyOff, 8), (e + _upCountOff, 32) })]);
            var result = new Dictionary<string, int>[16];
            for (int i = 0; i < list.Length; i++)
            {
                if (reads[2 * i] is not { } kb || reads[2 * i + 1] is not { } cb) continue;
                ulong key = BinaryPrimitives.ReadUInt64LittleEndian(kb);
                if (!_upgradeIds.TryGetValue(key, out var id) && (id = UpgradeId(key)) != null) _upgradeIds[key] = id;
                if (id == null) continue;
                for (int p = 0; p < 16; p++)
                {
                    int n = BinaryPrimitives.ReadUInt16LittleEndian(cb.AsSpan(p * 2)) ^ _upXor;
                    if (n is > 0 and < 100) (result[p] ??= [])[id] = n;
                }
            }
            _upgrades = result;
        }

        readonly Dictionary<ulong, string> _upgradeIds = [];   // upgrade key -> id; keys are catalog objects, per match

        /// <summary>An upgrade id string: len &lt;&lt; 2 @+0x18, flags @+0x1C (bit 1: the characters are on the heap), chars @+0x20.</summary>
        string? UpgradeId(ulong key)
        {
            if (!IsUserPtr(key) || ReadBytes(key + 0x18, 0x48) is not { } kb) return null;
            int len = (int)(BinaryPrimitives.ReadUInt32LittleEndian(kb) >> 2);
            if (len is <= 0 or > 64) return null;
            bool heap = (kb[4] & 2) != 0;
            byte[]? chars = heap ? ReadBytes(BinaryPrimitives.ReadUInt64LittleEndian(kb.AsSpan(8)), len) : len <= 0x40 ? kb.AsSpan(8, len).ToArray() : null;
            return chars != null && chars.All(c => c is >= 0x20 and < 0x7F) ? System.Text.Encoding.ASCII.GetString(chars) : null;
        }
    }
}
