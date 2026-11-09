using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Production progress (5.0.x addresses in brackets, offsets by code signature):
    ///   morphs (Lair, Hive, Greater Spire, ...): the morph ability's progress [exe+0x9187D0] takes the unit's first
    ///   order - *(record+0x60) -> *head &amp; ~1 -> node - 8 - and checks its type (vtable[0]() == 0x15) and that it
    ///   belongs to the ability; [exe+0x9188C0] then reports remaining = max(0, int @+0x138), total = int @+0x134
    ///   (only once the start delay int @+0x130 is &lt;= 0). Progress = 1 - remaining / total.
    /// </summary>
    public sealed partial class SC2Memory
    {
        const string OrderListPattern = "48 8B 41 18 48 8B F2 48 83 C0 ? 48 8B F9 48 8B 18 48 85 DB 0F 84 ? ? ? ? 48 8B 1B 48 83 E3 FE 0F 84 ? ? ? ? 48 83 C3 ? 0F 84 ? ? ? ? 48 8B 03 48 8B CB FF 10 83 F8 ? 75";
        const string MorphFieldsPattern = "40 53 48 83 EC 20 83 B9 ? ? ? ? 00 48 8B DA 4C 8B C1 7E";
        const string MorphTimesPattern = "41 8B 80 ? ? ? ? 85 C0 7E 04 89 44 24 30 48 8D 44 24 30 8B 00 89 42 10 41 8B 80 ? ? ? ?";

        // Queues (units in production, research): UnitGetProgressComplete [exe+0x9A1130] gets the unit's queue ability with
        // [exe+0x84DF20](record + 0x108, type 0x1A). Ability list: L = *(record + 0x108) &amp; ~3; the first byte (*L)[0]
        // entries are live objects encoded at L + 0x38 + 8 i (lo = ~u32 + u32[k1], hi = u32 - u32[k2]); type = vtable[0]().
        // Queue: item count int @+0x68, items @ *(+0x78); an item's progress [exe+0x8D6CC0]: remaining int @+0xC0,
        // total int @+0xBC (game loops x 4096; the game timer shows 22.4 loops a second).
        const string AbilityKeysPattern = "44 8B 0D ? ? ? ? 8D 73 38 44 8B 15 ? ? ? ?";
        const string QueueLookupPattern = "45 33 C0 48 81 C1 ? ? ? ? 41 8D 50 ? E8 ? ? ? ? 48 85 C0 74 ? 44 3B 70 ? 7D ? 45 85 F6 78 ? 48 8B 40 ?";
        // queue and morph timers count game loops in fixed 4096; the game's timer shows 22.4 loops a second
        const float LoopsFx = 22.4f * 4096f;
        const string QueueItemPattern = "8B 87 ? ? ? ? 48 8B CF 89 46 58 8B 87 ? ? ? ? 89 46 5C";
        // what an item makes: its info method [exe+0x92E7A0] copies count @+0x180 entries from *(+0x190) - link records,
        // the id (Marine, Stimpack, ...) a string from *(+8) to *(+0x10). "mov dword [rbx+148h],imm; mov eax,[rbp+count]; test; je;
        // ...; mov eax,[rbx]; mov rsi,[rbp+list]"
        const string QueueIdsPattern = "C7 83 ? ? ? ? ? 00 00 00 8B 85 ? ? ? ? 85 C0 74 ? 48 89 74 24 30 48 81 C3 ? ? ? ? 48 89 7C 24 38 33 FF 4C 89 74 24 40 44 8B F0 ? ? ? ? ? ? ? ? ? ? ? ? 8B 03 48 8B B5 ? ? ? ?";
        // research items are another class [exe+0x923640]: one link record @+0x180 ("mov rax,[rdi+180h]; mov [rsp+40h],rbp;
        // mov [rcx+rdx*8-8],rax")
        const string ResearchIdPattern = "48 8B 87 ? ? ? ? 48 89 6C 24 40 48 89 44 D1 F8";
        uint  _queueIdsCountOff, _queueIdsListOff;
        uint  _researchIdOff;
        readonly Dictionary<ulong, string> _linkIds = [];   // link record -> id string
        Dictionary<ulong, string> _itemProduct = [];             // queue item -> product (an item never changes what it makes)
        ulong _abilityK1Rva, _abilityK2Rva;
        uint  _abilityListOff, _queueItemRemainingOff, _queueItemTotalOff;
        int   _queueType = -1, _queueCountOff, _queueItemsOff;
        readonly Dictionary<ulong, ulong> _queueAbility = [];   // record -> its queue ability (0 = none)

        int  _orderListOff = -1, _orderNodeAdj, _morphOrderType = -1;
        uint _morphDelayOff, _morphRemainingOff, _morphTotalOff;
        readonly Dictionary<ulong, int> _orderVtType = [];   // order vtable -> type constant (-1 unknown)

        string ResolveProgress(List<(uint Rva, byte[] Data)> code)
        {
            _orderListOff = -1; _morphOrderType = -1;
            if (code.Count > 0 && FindPattern(code, OrderListPattern, 2) is [var ol] && CodeBytes(code, ol, 61) is { } ob)
            {
                _orderListOff   = ob[10];
                _orderNodeAdj   = (sbyte)ob[42];
                _morphOrderType = ob[59];
            }
            _morphDelayOff     = code.Count > 0 ? SigImm32Agree(code, MorphFieldsPattern, 8) ?? 0 : 0;
            _morphRemainingOff = code.Count > 0 ? SigImm32Agree(code, MorphTimesPattern, 3) ?? 0 : 0;
            _morphTotalOff     = code.Count > 0 ? SigImm32Agree(code, MorphTimesPattern, 28) ?? 0 : 0;
            bool ok = _orderListOff > 0 && _morphOrderType > 0 && _morphRemainingOff is > 0 and < 0x1000 && _morphTotalOff is > 0 and < 0x1000 && _morphDelayOff is > 0 and < 0x1000;
            if (!ok) { _morphOrderType = -1; XMLogging.WriteLine("[Progress] signature 'morph progress' missed"); }

            // queues
            _queueType = -1;
            _abilityK2Rva = code.Count > 0 && ResolveSigAgree(code, new CodeSig("ability key 2", AbilityKeysPattern, 3)) is { } a2 ? a2 : 0;
            _abilityK1Rva = code.Count > 0 && ResolveSigAgree(code, new CodeSig("ability key 1", AbilityKeysPattern, 13)) is { } a1 ? a1 : 0;
            if (code.Count > 0 && FindPattern(code, QueueLookupPattern, 2) is [var ql] && CodeBytes(code, ql, 39) is { } qb)
            {
                _abilityListOff = BinaryPrimitives.ReadUInt32LittleEndian(qb.AsSpan(6));
                _queueType      = qb[13];
                _queueCountOff  = qb[27];
                _queueItemsOff  = qb[38];
            }
            _queueItemRemainingOff = code.Count > 0 ? SigImm32Agree(code, QueueItemPattern, 2) ?? 0 : 0;
            _queueItemTotalOff     = code.Count > 0 ? SigImm32Agree(code, QueueItemPattern, 14) ?? 0 : 0;
            _linkIds.Clear(); _itemProduct = [];
            _queueIdsCountOff = code.Count > 0 ? SigImm32Agree(code, QueueIdsPattern, 12) ?? 0 : 0;
            _queueIdsListOff  = code.Count > 0 ? SigImm32Agree(code, QueueIdsPattern, 64) ?? 0 : 0;
            _researchIdOff = code.Count > 0 ? SigImm32Agree(code, ResearchIdPattern, 3) ?? 0 : 0;
            if (_researchIdOff >= 0x400) _researchIdOff = 0;
            if (_queueIdsCountOff is 0 or >= 0x400 || _queueIdsListOff is 0 or >= 0x400) { _queueIdsCountOff = _queueIdsListOff = 0; XMLogging.WriteLine("[Progress] signature 'queue item ids' missed - no product names"); }
            bool qok = _abilityK1Rva != 0 && _abilityK2Rva != 0 && _queueType > 0 && _abilityListOff is > 0 and < 0x1000 &&
                       _queueItemRemainingOff is > 0 and < 0x400 && _queueItemTotalOff is > 0 and < 0x400;
            if (!qok) { _queueType = -1; XMLogging.WriteLine("[Progress] signature 'queue progress' missed"); }
            return $"morphs {(ok ? "sig" : "missed")}, queues {(qok ? "sig" : "missed")}";
        }

        int VTableConstant(ulong obj, Dictionary<ulong, int> cache) => VTableConstantOf(ReadU64(obj), cache);

        /// <summary>The constant a class's first virtual returns ("mov eax, imm32; ret"), cached per vtable; -1 if none.</summary>
        int VTableConstantOf(ulong vt, Dictionary<ulong, int> cache)
        {
            if (cache.TryGetValue(vt, out int t)) return t;
            ulong fn = vt >= _exeBase && vt < _exeEnd ? ReadU64(vt) : 0;
            var c = fn >= _exeBase && fn < _exeEnd ? ReadBytes(fn, 6) : null;
            return cache[vt] = c is [0xB8, _, _, _, _, 0xC3] ? BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(1)) : -1;
        }

        const int AbilityListLen = 0x38 + 8 * 32;

        /// <summary>
        /// <see cref="FindQueueAbility"/> for many records in four batched reads (ability list pointers, the lists, their
        /// descriptors, the abilities' vtables). 0 where a record has none; null when a batched read failed.
        /// </summary>
        ulong[]? FindQueueAbilities(ulong[] recs)
        {
            var found = new ulong[recs.Length];
            var lists = ReadPointers([.. recs.Select(r => r + _abilityListOff)]);
            var idx = Enumerable.Range(0, recs.Length).Where(i => IsUserPtr(lists[i] & ~3UL)).ToArray();
            if (idx.Length == 0) return found;
            var lb = new byte[idx.Length * AbilityListLen]; var lok = new bool[idx.Length];
            if (!ReadValues([.. idx.Select(i => lists[i] & ~3UL)], AbilityListLen, lb, lok)) return null;
            var descs = Enumerable.Range(0, idx.Length).Select(k => lok[k] ? BinaryPrimitives.ReadUInt64LittleEndian(lb.AsSpan(k * AbilityListLen)) : 0).ToArray();
            if (!ReadStructs<byte>([.. descs], out var firsts, out var fok)) return null;
            uint k1 = U32At(_abilityK1Rva), k2 = U32At(_abilityK2Rva);
            var cands = new List<(int Rec, ulong Obj)>();
            for (int k = 0; k < idx.Length; k++)
            {
                if (!lok[k] || !IsUserPtr(descs[k]) || !fok[k]) continue;
                var s = lb.AsSpan(k * AbilityListLen, AbilityListLen);
                int count = s[0x33];
                for (int i = 0; i < Math.Min(count, firsts[k]) && i < 32; i++)
                {
                    ulong obj = ((ulong)(BinaryPrimitives.ReadUInt32LittleEndian(s[(0x3C + 8 * i)..]) - k2) << 32) | (~BinaryPrimitives.ReadUInt32LittleEndian(s[(0x38 + 8 * i)..]) + k1);
                    if (IsUserPtr(obj)) cands.Add((idx[k], obj));
                }
            }
            var vts = ReadPointers([.. cands.Select(c => c.Obj)]);
            for (int c = 0; c < cands.Count; c++)
                if (found[cands[c].Rec] == 0 && VTableConstantOf(vts[c], _orderVtType) == _queueType) found[cands[c].Rec] = cands[c].Obj;
            return found;
        }

        /// <summary>The record's queue ability, decoded from its ability list (0 = it has none).</summary>
        ulong FindQueueAbility(ulong rec)
        {
            ulong L = ReadU64(rec + _abilityListOff) & ~3UL;
            var lb = IsUserPtr(L) ? ReadBytes(L, 0x38 + 8 * 32) : null;
            if (lb == null) return 0;
            int count = lb[0x33];
            ulong desc = BinaryPrimitives.ReadUInt64LittleEndian(lb);
            int first = IsUserPtr(desc) && ReadBytes(desc, 1) is { } db ? db[0] : 0;
            uint k1 = U32At(_abilityK1Rva), k2 = U32At(_abilityK2Rva);
            for (int i = 0; i < Math.Min(count, first) && i < 32; i++)
            {
                ulong obj = ((ulong)(BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(0x3C + 8 * i)) - k2) << 32) | (~BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(0x38 + 8 * i)) + k1);
                if (IsUserPtr(obj) && VTableConstant(obj, _orderVtType) == _queueType) return obj;
            }
            return 0;
        }

        /// <summary>Per building record with a queue: progress of the first item (0..1) and the queue length; idle queues
        /// as (-1, 0), so callers can tell an idle production building from one that has no queue at all. Product: what the
        /// first item makes ("Marine", "Zergling ×2", "Stimpack"), "" when unknown. Left: seconds of the game timer until the first item is done.</summary>
        Dictionary<ulong, (float Progress, int Count, string Product, float Left)> ReadQueueProgress(ulong[] records)
        {
            var result = new Dictionary<ulong, (float, int, string, float)>();
            if (_queueType < 0 || records.Length == 0) return result;
            var fresh = records.Where(r => !_queueAbility.ContainsKey(r)).ToArray();
            if (fresh.Length > 0 && FindQueueAbilities(fresh) is { } found)
                for (int i = 0; i < fresh.Length; i++) _queueAbility[fresh[i]] = found[i];
            var withQueue = records.Where(r => _queueAbility.GetValueOrDefault(r) != 0).ToArray();
            if (withQueue.Length == 0) return result;
            int qlen = Math.Max(_queueCountOff + 4, _queueItemsOff + 8);
            var qbuf = new byte[withQueue.Length * qlen]; var qok = new bool[withQueue.Length];
            if (!ReadValues(withQueue.Select(r => _queueAbility[r]).ToArray(), qlen, qbuf, qok)) return result;
            var busy = new List<(ulong Rec, int Count, ulong Items)>();
            for (int i = 0; i < withQueue.Length; i++)
            {
                if (!qok[i]) continue;
                int count = BinaryPrimitives.ReadInt32LittleEndian(qbuf.AsSpan(i * qlen + _queueCountOff));
                ulong items = BinaryPrimitives.ReadUInt64LittleEndian(qbuf.AsSpan(i * qlen + _queueItemsOff));
                if (count == 0) result[withQueue[i]] = (-1f, 0, "", -1f);
                if (count is > 0 and <= 32 && IsUserPtr(items)) busy.Add((withQueue[i], count, items));
            }
            var products = new Dictionary<ulong, string>();
            string Product(ulong item)
            {
                string p = _itemProduct.TryGetValue(item, out var known) ? known : ItemProduct(item);
                if (p.Length > 0) products[item] = p;   // a failed read is retried next pass
                return p;
            }
            try
            {
                if (busy.Count == 0) return result;
                var ibuf = new byte[busy.Count * 8]; var iok = new bool[busy.Count];
                if (!ReadValues(busy.Select(b => b.Items).ToArray(), 8, ibuf, iok)) return result;
                uint lo = Math.Min(_queueItemRemainingOff, _queueItemTotalOff);
                int flen = (int)(Math.Max(_queueItemRemainingOff, _queueItemTotalOff) - lo + 4);
                var first = busy.Select((b, i) => iok[i] ? BinaryPrimitives.ReadUInt64LittleEndian(ibuf.AsSpan(i * 8, 8)) : 0).ToArray();
                var live = Enumerable.Range(0, busy.Count).Where(i => IsUserPtr(first[i])).ToArray();
                var fbuf = new byte[live.Length * flen]; var fok = new bool[live.Length];
                if (live.Length == 0 || !ReadValues(live.Select(i => first[i] + lo).ToArray(), flen, fbuf, fok)) return result;
                for (int k = 0; k < live.Length; k++)
                {
                    if (!fok[k]) continue;
                    int remaining = BinaryPrimitives.ReadInt32LittleEndian(fbuf.AsSpan(k * flen + (int)(_queueItemRemainingOff - lo)));
                    int total = BinaryPrimitives.ReadInt32LittleEndian(fbuf.AsSpan(k * flen + (int)(_queueItemTotalOff - lo)));
                    if (total <= 0 || remaining < 0 || remaining > total) continue;
                    var b = busy[live[k]];
                    result[b.Rec] = (1f - (float)remaining / total, b.Count, Product(first[live[k]]), remaining / LoopsFx);
                }
                return result;
            }
            finally { _itemProduct = products; }   // keep only the items still queued
        }

        /// <summary>The id a queue item makes, pretty-printed, with "×n" when it makes several (zerglings come in pairs).
        /// Link record names are cached: there are only so many unit and upgrade types.</summary>
        string ItemProduct(ulong item)
        {
            if (_queueIdsCountOff == 0) return "";
            uint lo = Math.Min(_queueIdsCountOff, _queueIdsListOff);
            if (ReadBytes(item + lo, (int)(Math.Max(_queueIdsCountOff, _queueIdsListOff) - lo + 8)) is not { } b) return "";
            int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)(_queueIdsCountOff - lo)));
            ulong list = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)(_queueIdsListOff - lo)));
            // a unit-training item lists what it makes; a research item holds the one upgrade
            ulong link = n is > 0 and <= 16 && IsUserPtr(list) ? ReadU64(list) : _researchIdOff != 0 ? ReadU64(item + _researchIdOff) : 0;
            if (n is <= 0 or > 16) n = 1;
            if (link == 0) return "";
            if (!_linkIds.TryGetValue(link, out var id))
            {
                // +8: the id's characters, +0x10: one past the last
                id = "";
                if (ReadBytes(link + 8, 16) is { } sb)
                {
                    ulong s = BinaryPrimitives.ReadUInt64LittleEndian(sb), e = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(8));
                    if (s > 0x10000 && e > s && e - s <= 64 && ReadBytes(s, (int)(e - s)) is { } cs && cs.All(c => c is >= 0x20 and < 0x7F))
                        id = System.Text.Encoding.ASCII.GetString(cs);
                }
                _linkIds[link] = id;
            }
            if (id.Length == 0) return "";
            string name = PrettyNames.Get(id);
            return n > 1 ? $"{name} ×{n}" : name;
        }

        /// <summary>Per record: morph progress 0..1 and seconds left on the game timer (absent = not morphing). Three batched hops plus the order reads.</summary>
        Dictionary<ulong, (float Progress, float Left)> ReadMorphProgress(ulong[] records)
        {
            var result = new Dictionary<ulong, (float, float)>();
            if (_morphOrderType < 0 || records.Length == 0) return result;
            ulong[] Hop(IEnumerable<ulong> from)
            {
                var src = from.ToArray();
                var buf = new byte[src.Length * 8]; var ok = new bool[src.Length];
                var res = new ulong[src.Length];
                var idx = Enumerable.Range(0, src.Length).Where(i => src[i] != 0).ToArray();
                if (idx.Length == 0) return res;
                var b2 = new byte[idx.Length * 8]; var ok2 = new bool[idx.Length];
                if (ReadValues(idx.Select(i => src[i]).ToArray(), 8, b2, ok2))
                    for (int k = 0; k < idx.Length; k++)
                        if (ok2[k]) res[idx[k]] = BinaryPrimitives.ReadUInt64LittleEndian(b2.AsSpan(k * 8, 8));
                return res;
            }
            var heads = Hop(records.Select(r => r + (ulong)_orderListOff));
            var nodes = Hop(heads.Select(h => IsUserPtr(h) ? h : 0)).Select(n => n & ~1UL).ToArray();
            var orders = nodes.Select(n => IsUserPtr(n) ? (ulong)((long)n + _orderNodeAdj) : 0).ToArray();
            var live = Enumerable.Range(0, records.Length).Where(i => orders[i] != 0).ToArray();
            if (live.Length == 0) return result;
            int len = (int)Math.Max(_morphRemainingOff, Math.Max(_morphTotalOff, _morphDelayOff)) + 4;
            var buf = new byte[live.Length * len]; var okb = new bool[live.Length];
            if (!ReadValues(live.Select(i => orders[i]).ToArray(), len, buf, okb)) return result;
            for (int k = 0; k < live.Length; k++)
            {
                if (!okb[k]) continue;
                var o = buf.AsSpan(k * len, len);
                ulong vt = BinaryPrimitives.ReadUInt64LittleEndian(o);
                if (!_orderVtType.TryGetValue(vt, out int type))
                {
                    ulong fn = vt >= _exeBase && vt < _exeEnd ? ReadU64(vt) : 0;
                    var c = fn >= _exeBase && fn < _exeEnd ? ReadBytes(fn, 6) : null;
                    _orderVtType[vt] = type = c is [0xB8, _, _, _, _, 0xC3] ? BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(1)) : -1;
                }
                if (type != _morphOrderType) continue;
                int delay = BinaryPrimitives.ReadInt32LittleEndian(o[(int)_morphDelayOff..]);
                int remaining = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(o[(int)_morphRemainingOff..]));
                int total = BinaryPrimitives.ReadInt32LittleEndian(o[(int)_morphTotalOff..]);
                if (delay > 0 || total <= 0 || remaining > total) continue;
                result[records[live[k]]] = (1f - (float)remaining / total, remaining / LoopsFx);
            }
            return result;
        }
    }
}
