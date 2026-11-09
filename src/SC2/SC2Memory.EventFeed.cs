using System.Buffers.Binary;
using System.Numerics;

namespace SC2Radar.SC2
{
    /// <summary>A command a player gave, from the live game event stream (SCmdEvent).</summary>
    public readonly record struct PlayerCommand(DateTime Time, int Player, string Ability, int CmdIndex, Vector2? Point, uint TargetTag, bool Queued);

    /// <summary>
    /// s2protocol's BitPackedBuffer reader (big-endian bit order: a value's top bits come from the low bits of the current
    /// byte) and the generic value decoder over S2Protocol.Types. Values: long, bool, null, List&lt;object?&gt;, bool[] (bit
    /// arrays), byte[] (blobs), KeyValuePair&lt;string, object?&gt; (choices), Dictionary&lt;string, object?&gt; (structs).
    /// </summary>
    sealed class S2Reader(byte[] data, int start, int end)
    {
        int _used = start, _next, _nextBits;
        public int Used => _used;
        public bool Done => _nextBits == 0 && _used >= end;

        public ulong Bits(int n)
        {
            ulong r = 0; int rb = 0;
            while (rb != n)
            {
                if (_nextBits == 0)
                {
                    if (_used >= end) throw new EndOfStreamException();
                    _next = data[_used++]; _nextBits = 8;
                }
                int c = Math.Min(n - rb, _nextBits);
                ulong copy = (ulong)(_next & ((1 << c) - 1));
                r |= copy << (n - rb - c);
                _next >>= c; _nextBits -= c; rb += c;
            }
            return r;
        }

        public void Align() => _nextBits = 0;

        public object? Read(int type)
        {
            var t = S2Protocol.Types[type];
            switch (t.Kind)
            {
                case 0: return t.Offset + (long)Bits(t.Bits);
                case 1: return Bits(1) != 0;
                case 2: return null;
                case 3: return Bits(1) != 0 ? Read(t.Type) : null;
                case 4:
                {
                    long n = t.Offset + (long)Bits(t.Bits);
                    if (n is < 0 or > 4096) throw new InvalidDataException("array length");
                    var l = new List<object?>((int)n);
                    for (long i = 0; i < n; i++) l.Add(Read(t.Type));
                    return l;
                }
                case 5:
                {
                    long n = t.Offset + (long)Bits(t.Bits);
                    var b = new bool[n];
                    // the whole bit array is one big-endian number: its first bit read is the highest index
                    for (long i = n - 1; i >= 0; i--) b[i] = Bits(1) != 0;
                    return b;
                }
                case 6:
                {
                    long n = t.Offset + (long)Bits(t.Bits);
                    Align();
                    if (n < 0 || _used + n > end) throw new EndOfStreamException();
                    var b = data.AsSpan(_used, (int)n).ToArray(); _used += (int)n;
                    return b;
                }
                case 7: return Bits(32);
                case 8: return BitConverter.Int32BitsToSingle((int)Bits(32));
                case 9: return BitConverter.Int64BitsToDouble((long)Bits(64));
                case 10:
                {
                    long tag = t.Offset + (long)Bits(t.Bits);
                    foreach (var f in t.Fields!) if (f.Tag == tag) return new KeyValuePair<string, object?>(f.Name, Read(f.Type));
                    throw new InvalidDataException("choice tag");
                }
                case 11:
                {
                    var d = new Dictionary<string, object?>(t.Fields!.Length);
                    foreach (var f in t.Fields) d[f.Name] = Read(f.Type);
                    return d;
                }
                default: throw new InvalidDataException("type kind");
            }
        }

        /// <summary>One game event: (gameloop delta, user id, event id, event value). Throws at the end of the data.</summary>
        public (long Delta, int User, int Id, object? Value) Event()
        {
            var delta = (KeyValuePair<string, object?>)Read(S2Protocol.SVarUInt32Type)!;
            var user = (Dictionary<string, object?>)Read(S2Protocol.UserIdType)!;
            int id = (int)(long)Read(S2Protocol.GameEventIdType)!;
            if (!S2Protocol.GameEvents.TryGetValue(id, out var ev)) throw new InvalidDataException("event id");
            var value = Read(ev.Type);
            Align();
            return ((long)delta.Value!, (int)(long)user["m_userId"]!, id, value);
        }
    }

    /// <summary>
    /// Live game event feed (what an observer sees: every player's commands, selections, control groups, cameras), read
    /// from the replay the client writes while the game runs - read-only, so safe in any game. The writer is a static
    /// object in the exe's data: the same buffer pointer at +0x40 and +0x90 (0x2000 bytes), page / buffer sizes 0x1000 /
    /// 0x2000 at +0x78 / +0x80, and at +0x70 a file object whose path ends in "replay.game.events". The buffer holds the
    /// replay file's bytes [u32 +0x48, u32 +0x48 + 0x2000); events are copied in 0x100-byte pages and committed up to u32
    /// +0x58, so the feed trails by up to a page. Events are s2protocol game events (S2Protocol, generated per build).
    /// Users map to players by whose units they select.
    /// </summary>
    public sealed partial class SC2Memory
    {
        const int EvBufferLen = 0x2000;
        ulong _evWriterRva;                          // found once per process (the object is static)
        DateTime _nextEvFind, _nextEvScan;
        long _evFileOff = -1;                        // file offset of the next undecoded event, -1 = not synced
        ulong _abilCatalog;                          // the Abil catalog (CatalogNameOf), for ability names by index
        readonly Dictionary<int, string> _abilByIndex = [];
        readonly Dictionary<int, Dictionary<int, int>> _userVotes = [];   // user -> player -> times its units were selected
        readonly object _evLock = new();
        // per user: the selection as an ordered list (subgroup priority, unit link, intra priority, tag) and 10 control groups
        readonly Dictionary<int, List<(long Pri, long Link, long Intra, uint Tag)>> _selections = [];
        readonly Dictionary<int, List<(long Pri, long Link, long Intra, uint Tag)>[]> _groups = [];
        readonly LinkedList<PlayerCommand> _commands = new();
        readonly Dictionary<int, Vector2> _evCameras = [];

        /// <summary>Whether the live event feed has found the game's replay writer.</summary>
        public bool EventFeedAvailable => _evWriterRva != 0;
        public string EventFeedStatus { get; private set; } = "-";

        /// <summary>Recent commands of every player, newest last (up to 200).</summary>
        public PlayerCommand[] RecentCommands { get { lock (_evLock) return [.. _commands]; } }

        /// <summary>Player -> the unit tags they have selected right now.</summary>
        public Dictionary<int, uint[]> PlayerSelections
        {
            get
            {
                lock (_evLock)
                {
                    var r = new Dictionary<int, uint[]>();
                    foreach (var (user, sel) in _selections) if (PlayerOfUser(user) is int p and > 0) r[p] = [.. sel.Select(s => s.Tag)];
                    return r;
                }
            }
        }

        /// <summary>Player -> where their camera looks (map units), from their camera events.</summary>
        public Dictionary<int, Vector2> PlayerCameras
        {
            get { lock (_evLock) return _evCameras.Where(kv => PlayerOfUser(kv.Key) is > 0).ToDictionary(kv => PlayerOfUser(kv.Key)!.Value, kv => kv.Value); }
        }

        /// <summary>The player a user is: the owner of the units they select; before any selection, the local player when
        /// they are the only human (vs AI).</summary>
        int? PlayerOfUser(int user) =>
            _userVotes.TryGetValue(user, out var v) && v.Count > 0 ? v.MaxBy(kv => kv.Value).Key
            : _userVotes.Count == 0 && OnlyHumanInGame ? LocalPlayer : null;

        void ResetEventFeed()
        {
            lock (_evLock)
            {
                _evFileOff = -1; _selections.Clear(); _groups.Clear(); _commands.Clear(); _evCameras.Clear(); _userVotes.Clear();
            }
            _abilByIndex.Clear();
        }

        // ── finding the writer ───────────────────────────────────────────────

        /// <summary>Scans the exe's writable sections for the replay event writer (see the class summary).</summary>
        ulong FindEventWriter()
        {
            if (ReadBytes(_exeBase + 0x3C, 4) is not { } lf || ReadBytes(_exeBase + BitConverter.ToUInt32(lf) + 0x50, 4) is not { } sb) return 0;
            uint image = BitConverter.ToUInt32(sb);
            var regions = (_vmm.Map_GetPTE(_pid, fIdentifyModules: true) ?? [])
                .Where(e => e.fW && e.vaBase >= _exeBase && e.vaBase < _exeBase + image && e.cbSize <= 256ul << 20).ToArray();
            foreach (var r in regions)
            {
                var buf = new byte[r.cbSize]; var ok = new bool[(int)((r.cbSize + PAGE - 1) / PAGE)];
                ReadRange(r.vaBase, buf, ok);
                for (int i = 0; i + 0xC0 <= buf.Length; i += 8)
                {
                    ulong b40 = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i + 0x40));
                    if (!IsUserPtr(b40) || BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i + 0x90)) != b40) continue;
                    if (BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i + 0x78)) != 0x1000 || BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i + 0x80)) != EvBufferLen) continue;
                    ulong file = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i + 0x70));
                    if (!IsUserPtr(file) || ReadBytes(file + 0x18, 0x60) is not { } pb) continue;
                    string path = Encoding.ASCII.GetString(pb);
                    if (!path.Contains("replay.game.events", StringComparison.OrdinalIgnoreCase)) continue;
                    return r.vaBase + (ulong)i - _exeBase;
                }
            }
            return 0;
        }

        // ── following the stream ─────────────────────────────────────────────

        /// <summary>Worker tick (Config.TrackEvents): decode the newly committed events, 4 times a second.</summary>
        void ScanEventFeed()
        {
            if (!InGame) return;
            var now = DateTime.UtcNow;
            if (_evWriterRva == 0)
            {
                if (now < _nextEvFind) return;
                _nextEvFind = now.AddSeconds(15);
                _evWriterRva = FindEventWriter();
                EventFeedStatus = _evWriterRva != 0 ? "Found" : "Replay writer not found";
                if (_evWriterRva == 0) return;
                XMLogging.WriteLine($"[Events] replay writer at exe+0x{_evWriterRva:X}");
            }
            if (now < _nextEvScan) return;
            _nextEvScan = now.AddMilliseconds(250);
            _tagOwners = null;

            if (ReadBytes(_exeBase + _evWriterRva, 0xC0) is not { } w) return;
            long fileBase = BinaryPrimitives.ReadUInt32LittleEndian(w.AsSpan(0x48));
            long committed = BinaryPrimitives.ReadUInt32LittleEndian(w.AsSpan(0x58));
            ulong bufPtr = BinaryPrimitives.ReadUInt64LittleEndian(w.AsSpan(0x90));
            int len = (int)(committed - fileBase);
            if (!IsUserPtr(bufPtr) || len is <= 0 or > EvBufferLen) { EventFeedStatus = "Waiting for events"; return; }
            if (ReadBytes(bufPtr, len) is not { } data) return;

            int start;
            if (_evFileOff >= fileBase && _evFileOff <= committed) start = (int)(_evFileOff - fileBase);
            else
            {
                // first read, a new match, or we fell behind a flush: find where whole events start in what's there
                start = -1; int best = 0;
                for (int s = 0; s < Math.Min(0x200, len); s++)
                {
                    int n = 0, endAt = s;
                    try { var r = new S2Reader(data, s, len); while (!r.Done && n < 2000) { r.Event(); n++; endAt = r.Used; } } catch { }
                    if (n >= 8 && endAt >= len - 0x60 && n > best) { best = n; start = s; }
                }
                if (start < 0) { EventFeedStatus = "Syncing..."; return; }
            }
            var rd = new S2Reader(data, start, len);
            int done = start;
            lock (_evLock)
            {
                while (!rd.Done)
                {
                    (long Delta, int User, int Id, object? Value) ev;
                    try { ev = rd.Event(); } catch { break; }   // the rest is a part of an event in the next page
                    done = rd.Used;
                    try { ApplyEvent(ev.User, ev.Id, ev.Value); } catch { }
                }
            }
            _evFileOff = fileBase + done;
            lock (_evLock)
                for (var node = _commands.First; node != null; node = node.Next)
                    if (node.Value.Ability.StartsWith("ability ", StringComparison.Ordinal) && int.TryParse(node.Value.Ability.AsSpan(8), out int ai) &&
                        AbilNameByIndex(ai) is { Length: > 0 } an) node.Value = node.Value with { Ability = an };   // named once the catalog is known
            PublishSelections();
            EventFeedStatus = $"Live ({_commands.Count} recent commands, {_selections.Count} players' selections)";
        }

        volatile Dictionary<ulong, int> _selectedBy = [];

        /// <summary>Unit record -> the player who has it selected right now (the live event feed); the radar rings these.</summary>
        public IReadOnlyDictionary<ulong, int> SelectedBy => _selectedBy;

        volatile Dictionary<uint, ulong> _tagRecords = [];

        /// <summary>Unit tag -> record for the units on the radar (refreshed with the event feed), for command targets.</summary>
        public IReadOnlyDictionary<uint, ulong> TagRecords => _tagRecords;

        /// <summary>Selections (unit tags) to unit records, one batched read of the snapshot's tags (record + 0).</summary>
        void PublishSelections()
        {
            Dictionary<uint, int> byTag = [];
            lock (_evLock)
                foreach (var (user, sel) in _selections)
                    if (PlayerOfUser(user) is int p and > 0) foreach (var x in sel) byTag[x.Tag] = p;
            var recs = Snapshot.Units.Where(u => u.Record != 0).Select(u => u.Record).Distinct().ToArray();
            var tb = new byte[recs.Length * 4]; var ok = new bool[recs.Length];
            var res = new Dictionary<ulong, int>(); var tags = new Dictionary<uint, ulong>();
            if (recs.Length > 0 && ReadValues(recs, 4, tb, ok))
                for (int i = 0; i < recs.Length; i++)
                {
                    if (!ok[i]) continue;
                    uint tag = BinaryPrimitives.ReadUInt32LittleEndian(tb.AsSpan(i * 4));
                    tags.TryAdd(tag, recs[i]);
                    if (byTag.TryGetValue(tag, out int p)) res[recs[i]] = p;
                }
            _selectedBy = res; _tagRecords = tags;
        }

        static long L(object? o) => o is long l ? l : 0;
        static Dictionary<string, object?>? D(object? o) => o as Dictionary<string, object?>;

        void ApplyEvent(int user, int id, object? value)
        {
            var e = D(value);
            if (e == null) return;
            switch (S2Protocol.GameEvents[id].Name)
            {
                case "SCmdEvent":
                {
                    var abil = D(e["m_abil"]);
                    int link = (int)L(abil?["m_abilLink"]), cmd = (int)L(abil?["m_abilCmdIndex"]);
                    Vector2? point = null; uint tag = 0;
                    if (e["m_data"] is KeyValuePair<string, object?> data)
                    {
                        if (data.Key == "TargetPoint" && D(data.Value) is { } tp) point = new Vector2(L(tp["x"]) / 4096f, L(tp["y"]) / 4096f);
                        else if (data.Key == "TargetUnit" && D(data.Value) is { } tu)
                        {
                            tag = (uint)L(tu["m_tag"]);
                            if (D(tu["m_snapshotPoint"]) is { } sp) point = new Vector2(L(sp["x"]) / 4096f, L(sp["y"]) / 4096f);
                        }
                    }
                    bool queued = (L(e["m_cmdFlags"]) & 2) != 0;   // shift-queued
                    string name = abil == null ? "smart" : AbilNameByIndex(link) is { Length: > 0 } n ? n : $"ability {link}";
                    _commands.AddLast(new PlayerCommand(DateTime.UtcNow, PlayerOfUser(user) ?? -user - 1, name, cmd, point, tag, queued));
                    while (_commands.Count > 200) _commands.RemoveFirst();
                    break;
                }
                case "SSelectionDeltaEvent":
                {
                    int group = (int)L(e["m_controlGroupId"]);
                    var delta = D(e["m_delta"]);
                    if (delta == null) return;
                    var list = group == 10 ? Selection(user) : Group(user, group);
                    ApplyDelta(list, delta);
                    if (group == 10) VoteOwner(user, list.Select(s => s.Tag));
                    break;
                }
                case "SControlGroupUpdateEvent":
                {
                    int index = (int)L(e["m_controlGroupIndex"]), update = (int)L(e["m_controlGroupUpdate"]);
                    if (index is < 0 or > 9) return;
                    var g = Group(user, index); var sel = Selection(user);
                    switch (update)
                    {
                        case 0: g.Clear(); g.AddRange(sel); break;                                     // set
                        case 1: foreach (var s in sel) if (!g.Any(x => x.Tag == s.Tag)) g.Add(s); Sort(g); break;   // append
                        case 2: sel.Clear(); sel.AddRange(g); break;                                    // recall
                        case 3: g.Clear(); break;                                                       // clear
                        case 4: g.Clear(); g.AddRange(sel); break;                                      // set and steal
                        case 5: foreach (var s in sel) if (!g.Any(x => x.Tag == s.Tag)) g.Add(s); Sort(g); break;   // append and steal
                    }
                    if (e["m_mask"] is KeyValuePair<string, object?> m && update == 2) ApplyRemove(sel, m);
                    break;
                }
                case "SCameraUpdateEvent":
                    if (D(e["m_target"]) is { } t) _evCameras[user] = new Vector2(L(t["x"]) / 256f, L(t["y"]) / 256f);
                    break;
            }
        }

        List<(long Pri, long Link, long Intra, uint Tag)> Selection(int user) => _selections.TryGetValue(user, out var s) ? s : _selections[user] = [];

        List<(long Pri, long Link, long Intra, uint Tag)> Group(int user, int g)
        {
            if (!_groups.TryGetValue(user, out var gs)) _groups[user] = gs = [.. Enumerable.Range(0, 10).Select(_ => new List<(long Pri, long Link, long Intra, uint Tag)>())];
            return gs[Math.Clamp(g, 0, 9)];
        }

        /// <summary>Selections are ordered by subgroup priority (high first), then unit type, then intra-subgroup priority:
        /// remove masks index into that order.</summary>
        static void Sort(List<(long Pri, long Link, long Intra, uint Tag)> l) =>
            l.Sort((a, b) => b.Pri != a.Pri ? b.Pri.CompareTo(a.Pri) : a.Link != b.Link ? a.Link.CompareTo(b.Link) : b.Intra != a.Intra ? b.Intra.CompareTo(a.Intra) : a.Tag.CompareTo(b.Tag));

        static void ApplyRemove(List<(long Pri, long Link, long Intra, uint Tag)> list, KeyValuePair<string, object?> mask)
        {
            switch (mask.Key)
            {
                case "Mask" when mask.Value is bool[] bits:
                    for (int i = Math.Min(bits.Length, list.Count) - 1; i >= 0; i--) if (bits[i]) list.RemoveAt(i);
                    break;
                case "OneIndices" when mask.Value is List<object?> one:
                    foreach (int i in one.Select(o => (int)L(o)).OrderByDescending(i => i)) if (i < list.Count) list.RemoveAt(i);
                    break;
                case "ZeroIndices" when mask.Value is List<object?> zero:
                    var keep = zero.Select(o => (int)L(o)).Where(i => i < list.Count).Select(i => list[i]).ToList();
                    list.Clear(); list.AddRange(keep);
                    break;
            }
        }

        static void ApplyDelta(List<(long Pri, long Link, long Intra, uint Tag)> list, Dictionary<string, object?> delta)
        {
            if (delta["m_removeMask"] is KeyValuePair<string, object?> rm) ApplyRemove(list, rm);
            var tags = (delta["m_addUnitTags"] as List<object?> ?? []).Select(o => (uint)L(o)).ToList();
            int k = 0;
            foreach (var sg in (delta["m_addSubgroups"] as List<object?> ?? []).Select(D))
            {
                if (sg == null) continue;
                long pri = L(sg["m_subgroupPriority"]), link = L(sg["m_unitLink"]), intra = L(sg["m_intraSubgroupPriority"]), n = L(sg["m_count"]);
                for (long i = 0; i < n && k < tags.Count; i++, k++) list.Add((pri, link, intra, tags[k]));
            }
            Sort(list);
        }

        /// <summary>Learns which player a user is: the owner of the units they select.</summary>
        void VoteOwner(int user, IEnumerable<uint> tags)
        {
            var want = tags.Take(12).ToHashSet();
            if (want.Count == 0) return;
            foreach (uint tag in want)
            {
                if (!TagOwners().TryGetValue(tag, out byte owner)) continue;
                if (!_userVotes.TryGetValue(user, out var v)) _userVotes[user] = v = [];
                v[owner] = v.GetValueOrDefault(owner) + 1;
                break;   // one unit is enough per selection
            }
        }

        Dictionary<uint, byte>? _tagOwners;   // unit tag -> owner, read once per scan (when a selection needs it)

        /// <summary>Every player unit's tag (u32 at record + 0) and owner, in one batched read.</summary>
        Dictionary<uint, byte> TagOwners()
        {
            if (_tagOwners != null) return _tagOwners;
            var units = Snapshot.Units.Where(u => u.Record != 0 && u.Owner is >= 1 and <= 15).DistinctBy(u => u.Record).ToArray();
            ReadStructs<uint>([.. units.Select(u => u.Record)], out var tags, out var ok);
            _tagOwners = [];
            for (int i = 0; i < units.Length; i++) if (ok[i]) _tagOwners.TryAdd(tags[i], units[i].Owner);
            return _tagOwners;
        }

        /// <summary>An ability's id by its index in the Abil catalog ("move", "attack", "StimpackMarauder"), "" when unknown.</summary>
        string AbilNameByIndex(int index)
        {
            if (_abilByIndex.TryGetValue(index, out var n)) return n;
            n = "";
            // the Abil catalog: from any ability link the unit orders have named (CatalogNameOf notes it)
            if (_abilCatalog == 0) foreach (ulong link in _abilNames.Keys.Take(4)) { CatalogNameOf(link); if (_abilCatalog != 0) break; }
            if (_abilCatalog != 0 && index is >= 0 and < 65536)
            {
                ulong arr = ReadU64(_abilCatalog + 0xA0), rec = IsUserPtr(arr) ? ReadU64(arr + (ulong)index * 8) : 0;
                ulong s = rec > 0x10000 ? ReadU64(rec + 8) : 0, e = rec > 0x10000 ? ReadU64(rec + 0x10) : 0;
                if (s > 0x10000 && e > s && e - s < 80 && ReadBytes(s, (int)(e - s)) is { } nb) n = Encoding.ASCII.GetString(nb);
            }
            if (n.Length > 0 || _abilCatalog != 0) _abilByIndex[index] = n;
            return n;
        }
    }
}
