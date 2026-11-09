using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Startup resolution of the actor-table path and actor vtable, so a game patch keeps working:
    ///   1. code signatures in SC2_x64.exe (.text, ~0.3 s)   -> table global RVA, actor vtable RVA
    ///   2. else Config.ActorTableRva / Config.ActorVtableRva
    ///   3. the scene/pages field offsets come from Config; if the table doesn't validate with them they are
    ///      re-discovered by searching scene/world for the {page*, 0x800} list
    ///   4. if nothing validates, ActorReader falls back to the heap scan
    /// Resolved once per SC2 process.
    /// </summary>
    public sealed partial class SC2Memory
    {
        // global holding the scene pointer: "mov [rip+X], rcx" in its setter, "mov rcx, [rip+X]" in getters
        static readonly CodeSig[] TableGlobalSigs =
        [
            new("table global (setter)", "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 49 8B F8 48 89 0D ? ? ? ? 48 8B F2 E8 ? ? ? ? 48 8B D8 48 85 C0 74 ? 48 8B 08 48 8B D7 E8", 21),
            new("table global (getter)", "48 89 5C 24 08 57 48 83 EC 20 48 8B F9 48 8B 0D ? ? ? ? E8 ? ? ? ? 48 8B D8 48 85 C0 74 ? 48 8B 08 48 8B D7 E8", 16),
        ];
        // actor vtable: "lea rax, [rip+vt]; mov [rbx], rax" in the constructor / destructor
        static readonly CodeSig[] ActorVtableSigs =
        [
            new("actor vtable (ctor)", "48 89 5C 24 08 57 48 83 EC 20 48 8B D9 E8 ? ? ? ? 33 FF 48 8D 05 ? ? ? ? 48 89 03 48 8D 8B B8 03 00 00 48 8D 05 ? ? ? ? 48 89 83 A0 00 00 00", 23),
            new("actor vtable (dtor)", "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 83 EC 30 48 8D 05 ? ? ? ? 48 8B D9 48 89 01 48 8D 05 ? ? ? ? 48 89 81 A0 00 00 00", 27),
        ];

        // percent table scaling catalog LifeMax, indexed by a unit-record byte: "lea rdx, [rip+tbl]" in the max-life getter
        static readonly CodeSig LifePctSig =
            new("life percent table", "48 8B 09 48 8D 15 ? ? ? ? 41 0F B6 41 54 41 BB 00 10 00 00 41 0F B6 71 34", 6);
        int[]? _lifePct;   // null: signature missed, use 100%

        // alliance table read by the PlayerGetAlliance native (5.0.x exe+0x265AA00):
        //   uint mask[11 alliance ids][16 source players]; bit t set = source has that alliance toward player t
        static readonly CodeSig AllianceSig =
            new("alliance table", "40 0F B6 C6 44 8B CF 49 C1 E1 04 4C 03 C8 0F B6 D3 48 8D 05 ? ? ? ? 42 8B 0C 88", 20);
        public const int AlliancePassive = 0, AllianceVision = 1, AllianceCount = 11;
        ulong _allianceRva;
        volatile uint[] _alliances = [];

        /// <summary>Bit t set when player `source` has `alliance` toward player t (0 if the table wasn't found).</summary>
        public uint AllianceMask(int alliance, int source) =>
            _alliances is { Length: AllianceCount * 16 } a && alliance is >= 0 and < AllianceCount && source is >= 0 and < 16 ? a[alliance * 16 + source] : 0;

        public bool HasAllianceTable => _alliances.Length == AllianceCount * 16;

        /// <summary>Player `from` shares its vision with player `to` (a player always sees its own units).</summary>
        public bool SharesVision(int from, int to) => from == to || ((AllianceMask(AllianceVision, from) >> to) & 1) != 0;

        /// <summary>Allies as the game's team colours show them: the same player, or passive toward each other both
        /// ways (neutral 0 and hostile 16 are never allies).</summary>
        public bool IsAlly(int a, int b) =>
            a == b || a is > 0 and < 16 && b is > 0 and < 16 &&
            ((AllianceMask(AlliancePassive, a) >> b) & 1) != 0 && ((AllianceMask(AlliancePassive, b) >> a) & 1) != 0;

        /// <summary>A live row of the alliance table (bit t: `source` has `alliance` toward player t), null when unknown.</summary>
        public uint? ReadAllianceRow(int alliance, int source) =>
            _allianceRva != 0 && alliance is >= 0 and < AllianceCount && source is >= 0 and < 16 &&
            ReadBytes(_exeBase + _allianceRva + (ulong)(alliance * 16 + source) * 4, 4) is { Length: 4 } b ? BitConverter.ToUInt32(b) : null;

        /// <summary>Overwrite a row of the alliance table. The game rebuilds shared vision from it every update.</summary>
        public bool WriteAllianceRow(int alliance, int source, uint mask) =>
            _allianceRva != 0 && alliance is >= 0 and < AllianceCount && source is >= 0 and < 16 &&
            MemWrite(_exeBase + _allianceRva + (ulong)(alliance * 16 + source) * 4, BitConverter.GetBytes(mask));

        // The game keeps the table twice: right after it (+ AllianceCount * 16 * 4) the transpose, uint mask[alliance][target]
        // with bit s = source s has that alliance toward target - "who shares vision with me". Its setter (5.0.x exe+0x6A8350,
        // behind PlayerSetAlliance) updates both, and the vision update reads the transpose (exe+0x69EB7C), so a write to
        // the first table alone changes nothing on screen.
        const int AllianceTableBytes = AllianceCount * 16 * 4;

        /// <summary>
        /// Whether the second table really is the transpose of the first (checked before writing through it). Mismatches
        /// only at "s shares vision with <paramref name="local"/>" set in the first table alone are what radars before this
        /// fix left behind: those bits are cleared and the tables count as fine.
        /// </summary>
        public bool AllianceTransposeOk(int local)
        {
            if (_allianceRva == 0 || ReadBytes(_exeBase + _allianceRva, AllianceTableBytes * 2) is not { Length: AllianceTableBytes * 2 } b) return false;
            var t = MemoryMarshal.Cast<byte, uint>(b).ToArray();
            var stale = new List<int>();
            for (int a = 0; a < AllianceCount; a++)
                for (int s = 0; s < 16; s++)
                    for (int d = 0; d < 16; d++)
                    {
                        bool first = ((t[a * 16 + s] >> d) & 1) != 0, second = ((t[AllianceCount * 16 + a * 16 + d] >> s) & 1) != 0;
                        if (first == second) continue;
                        if (a == AllianceVision && d == local && first) stale.Add(s);
                        else return false;
                    }
            foreach (int s in stale)
                MemWrite(_exeBase + _allianceRva + (ulong)(AllianceVision * 16 + s) * 4, BitConverter.GetBytes(t[AllianceVision * 16 + s] & ~(1u << local)));
            return true;
        }

        /// <summary>Set or clear "source has `alliance` toward target" in both tables, as the game's setter does.</summary>
        public bool SetAllianceBit(int alliance, int source, int target, bool on, ScatterWriteHandle? writes = null) =>
            SetAllianceBits(alliance, [source], target, on, writes);

        /// <summary>
        /// <see cref="SetAllianceBit"/> for several sources toward one target, from one read of both tables. The target's
        /// transposed word holds every source's bit, so it is written once with all of them (queued writes can't read
        /// each other back).
        /// </summary>
        public bool SetAllianceBits(int alliance, IReadOnlyCollection<int> sources, int target, bool on, ScatterWriteHandle? writes = null)
        {
            if (_allianceRva == 0 || alliance is < 0 or >= AllianceCount || target is < 0 or >= 16 || sources.Any(s => s is < 0 or >= 16)) return false;
            if (sources.Count == 0) return true;
            ulong table = _exeBase + _allianceRva;
            if (ReadBytes(table, AllianceTableBytes * 2) is not { Length: AllianceTableBytes * 2 } b) return false;
            var words = MemoryMarshal.Cast<byte, uint>(b);
            int ti = AllianceCount * 16 + alliance * 16 + target;
            uint tv = words[ti], tn = tv;
            bool ok = true;
            foreach (int s in sources)
            {
                int ai = alliance * 16 + s;
                uint av = words[ai], an = on ? av | 1u << target : av & ~(1u << target);
                if (an != av) ok &= Write(table + (ulong)ai * 4, an, writes);
                tn = on ? tn | 1u << s : tn & ~(1u << s);
            }
            if (tn != tv) ok &= Write(table + (ulong)ti * 4, tn, writes);
            return ok;
        }
        /// <summary>Whether "source has `alliance` toward target" holds in both tables.</summary>
        public bool AllianceBitBoth(int alliance, int source, int target) =>
            ReadAllianceRow(alliance, source) is { } r && ((r >> target) & 1) != 0 &&
            ReadBytes(_exeBase + _allianceRva + AllianceTableBytes + (ulong)(alliance * 16 + target) * 4, 4) is { Length: 4 } b &&
            ((BitConverter.ToUInt32(b) >> source) & 1) != 0;

        void ReadAlliances()
        {
            if (_allianceRva == 0) return;
            if (ReadBytes(_exeBase + _allianceRva, AllianceCount * 16 * 4) is { Length: AllianceCount * 16 * 4 } b)
                _alliances = MemoryMarshal.Cast<byte, uint>(b).ToArray();
        }

        bool   _offsetsResolved;
        ulong  _actorVtRva, _tableRva;
        int    _sceneOff, _pagesOff;
        bool   _tableOffsetsChecked;   // field offsets validated (or re-discovered) for this process

        /// <summary>How the offsets were found, for the Debug widget / log.</summary>
        public string ResolveInfo { get; private set; } = "-";

        ulong ActorVt => _exeBase + (_offsetsResolved ? _actorVtRva : _cfg.ActorVtableRva);

        void ResolveOffsets()
        {
            var sw = Stopwatch.StartNew();
            _sceneOff = _cfg.ActorTableSceneOffset;
            _pagesOff = _cfg.ActorTablePagesOffset;
            _tableOffsetsChecked = false;
            var code = ReadExeCode();

            (ulong Rva, string Src) Pick(CodeSig[] sigs, ulong cfgRva)
            {
                foreach (var s in sigs)
                {
                    var r = code.Count > 0 ? ResolveSig(code, s, out int n) : null;
                    if (r is { } rva && rva < _exeEnd - _exeBase) return (rva, "sig");
                    XMLogging.WriteLine($"[Actors] signature '{s.Name}' missed");
                }
                return (cfgRva, "config");
            }
            var (vt, vtSrc) = Pick(ActorVtableSigs, _cfg.ActorVtableRva);
            var (tb, tbSrc) = _cfg.ActorTableRva != 0 ? Pick(TableGlobalSigs, _cfg.ActorTableRva) : (0ul, "off");
            _actorVtRva = vt;
            _tableRva   = tb;
            _lifePct    = code.Count > 0 && ResolveSig(code, LifePctSig, out _) is { } prva && ReadBytes(_exeBase + prva, 256 * 4) is { Length: 256 * 4 } pb
                        ? MemoryMarshal.Cast<byte, int>(pb).ToArray() : null;
            if (_lifePct == null) XMLogging.WriteLine("[Actors] signature 'life percent table' missed - max HP unscaled");
            ResolveLocalPlayer(code);
            ResolveLobby(code);
            _allianceRva = code.Count > 0 && ResolveSig(code, AllianceSig, out _) is { } arva ? arva : 0;
            _alliances   = [];
            if (_allianceRva == 0) XMLogging.WriteLine("[Actors] signature 'alliance table' missed - teams unknown");
            string match = ResolveMatch(code) + ", " + ResolvePlayerState(code) + ", " + ResolveProgress(code) + ", " + ResolveCamera(code) + ", " + ResolveWeapons(code) + ", " + ResolveUpgrades(code) + ", " + ResolveCreep(code) + ", " + ResolveVision(code) + ", " + ResolveFog(code) + ", " + ResolveOrders(code) + ", " + ResolveScript(code);
            _offsetsResolved = true;
            ResolveInfo = $"vt {vtSrc} +0x{vt:X}, table {tbSrc} +0x{tb:X}, life pct {(_lifePct != null ? "sig" : "missed")}, alliances {(_allianceRva != 0 ? "sig" : "missed")}, {match} ({sw.ElapsedMilliseconds} ms)";
            XMLogging.WriteLine($"[Actors] resolved: {ResolveInfo}");
        }

        enum TableState { Ok, NoScene, Broken }

        public string LastTableProblem { get; private set; } = "";
        TableState Bad(string why) { LastTableProblem = why; return TableState.Broken; }

        /// <summary>Page slot counts seen: 0x800 early in a match, 0x2000 once it fills up. Powers of two only.</summary>
        static bool IsPageSize(ulong n) => n >= 0x100 && n <= 0x100000 && (n & (n - 1)) == 0;

        /// <summary>
        /// Walk scene -> world -> page list and validate: at least one page, and the sampled slot objects
        /// all have vtables inside the exe image.
        /// </summary>
        TableState TryReadTable(ulong tableRva, int sceneOff, int pagesOff, out List<ulong> slots)
        {
            slots = [];
            if (tableRva == 0) return Bad("no table rva");
            ulong scene = U64At(tableRva);
            if (scene == 0) return TableState.NoScene;            // no match loaded yet
            if (!IsUserPtr(scene)) return Bad($"scene 0x{scene:X}");
            ulong world = ReadU64(scene + (ulong)sceneOff);
            if (!IsUserPtr(world)) return Bad($"world 0x{world:X} (scene+0x{sceneOff:X})");
            var pl = ReadBytes(world + (ulong)pagesOff, TableMaxPages * 16);
            if (pl is not { Length: TableMaxPages * 16 }) return Bad("page list unreadable");

            int pages = 0, pagesRead = 0;
            for (; pages < TableMaxPages; pages++)
            {
                ulong page = BinaryPrimitives.ReadUInt64LittleEndian(pl.AsSpan(pages * 16, 8));
                ulong n    = BinaryPrimitives.ReadUInt64LittleEndian(pl.AsSpan(pages * 16 + 8, 8));
                if (!IsPageSize(n) || !IsUserPtr(page)) break;   // page size grows with the match (0x800, 0x2000, ...)
                // a single page can fail to read while the game reallocates it - skip it rather than
                // throwing the whole table away (the vtable sample below still guards against a wrong table)
                var b = ReadBytes(page, (int)n * 8);
                if (b == null || b.Length != (int)n * 8) continue;
                pagesRead++;
                foreach (ulong v in MemoryMarshal.Cast<byte, ulong>(b))
                    if (IsUserPtr(v)) slots.Add(v);
            }
            if (pages == 0 || pagesRead == 0) return Bad($"pages {pages}, read {pagesRead}");
            if (slots.Count == 0) return TableState.Ok;

            // vtable check on a spread of entries
            var all = slots;
            int ns = Math.Min(64, all.Count);
            var sample = Enumerable.Range(0, ns).Select(i => all[i * all.Count / ns]).ToArray();
            var buf = new byte[sample.Length * 8];
            var ok  = new bool[sample.Length];
            if (!ReadValues(sample, 8, buf, ok)) return Bad("sample read failed");
            int good = 0, read = 0;
            for (int i = 0; i < sample.Length; i++)
            {
                if (!ok[i]) continue;
                read++;
                ulong vt = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i * 8, 8));
                if (vt >= _exeBase && vt < _exeEnd) good++;
            }
            return read > 0 && good * 2 >= read ? TableState.Ok : Bad($"vtable sample {good}/{read} (slots {slots.Count}, pages {pagesRead})");
        }

        /// <summary>
        /// Field offsets changed? Search the scene object for the world pointer and the world object for
        /// the {page*, 0x800} list, accepting the first combination that validates.
        /// </summary>
        bool DiscoverTableOffsets(ulong tableRva)
        {
            ulong scene = U64At(tableRva);
            if (!IsUserPtr(scene)) return false;
            var sb = ReadBytes(scene, 0x400);
            if (sb == null) return false;
            for (int so = 0; so + 8 <= sb.Length; so += 8)
            {
                ulong world = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(so, 8));
                if (!IsUserPtr(world)) continue;
                ulong wvt = ReadU64(world);
                if (wvt < _exeBase || wvt >= _exeEnd) continue;      // objects only
                var wb = ReadBytes(world, 0x10000);
                if (wb == null) continue;
                for (int po = 0; po + 16 <= wb.Length; po += 8)
                {
                    if (!IsPageSize(BinaryPrimitives.ReadUInt64LittleEndian(wb.AsSpan(po + 8, 8)))) continue;
                    if (!IsUserPtr(BinaryPrimitives.ReadUInt64LittleEndian(wb.AsSpan(po, 8)))) continue;
                    // first page of the list: the previous pair must not be a page too
                    if (po >= 16 && IsPageSize(BinaryPrimitives.ReadUInt64LittleEndian(wb.AsSpan(po - 8, 8))) && IsUserPtr(BinaryPrimitives.ReadUInt64LittleEndian(wb.AsSpan(po - 16, 8)))) continue;
                    if (TryReadTable(tableRva, so, po, out var slots) == TableState.Ok && slots.Count > 0)
                    {
                        _sceneOff = so; _pagesOff = po;
                        XMLogging.WriteLine($"[Actors] table offsets re-discovered: scene +0x{so:X}, pages +0x{po:X}");
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Table read with the resolved path; the first successful read per process also validates the field
        /// offsets (re-discovering them, then trying the config RVA, if needed).
        /// </summary>
        TableState ReadActorTableResolved(out List<ulong> slots)
        {
            if (!_offsetsResolved) ResolveOffsets();
            var st = TryReadTable(_tableRva, _sceneOff, _pagesOff, out slots);
            if (st == TableState.Broken)
            {
                // transient (mid-reallocation, stale page tables, DMA busy): refresh and retry once before treating it as wrong
                RefreshTranslations(full: true);
                Thread.Sleep(200);
                st = TryReadTable(_tableRva, _sceneOff, _pagesOff, out slots);
            }
            if (st == TableState.Ok || _tableOffsetsChecked) return st;

            // first problem this process: try the alternatives once the scene exists
            if (st == TableState.NoScene && (_cfg.ActorTableRva == _tableRva || U64At(_cfg.ActorTableRva) == 0))
                return st;
            _tableOffsetsChecked = true;
            foreach (ulong rva in new[] { _tableRva, _cfg.ActorTableRva }.Where(r => r != 0).Distinct())
            {
                if (TryReadTable(rva, _cfg.ActorTableSceneOffset, _cfg.ActorTablePagesOffset, out slots) == TableState.Ok)
                {
                    (_tableRva, _sceneOff, _pagesOff) = (rva, _cfg.ActorTableSceneOffset, _cfg.ActorTablePagesOffset);
                    ResolveInfo += rva == _cfg.ActorTableRva && rva != _tableRva ? ", table fell back to config" : "";
                    return TableState.Ok;
                }
                if (DiscoverTableOffsets(rva))
                {
                    _tableRva = rva;
                    ResolveInfo += $", offsets re-discovered (+0x{_sceneOff:X}/+0x{_pagesOff:X})";
                    return TryReadTable(_tableRva, _sceneOff, _pagesOff, out slots);
                }
            }
            XMLogging.WriteLine($"[Actors] actor table did not validate ({LastTableProblem}) - using heap scan");
            return TableState.Broken;
        }
    }
}
