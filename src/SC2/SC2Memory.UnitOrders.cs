using System.Numerics;

namespace SC2Radar.SC2
{
    /// <summary>One order of a unit: its ability ("move", "attack", "DroneHarvest", ...) and its target - a unit
    /// (TargetRecord) or a map point (Point).</summary>
    public readonly record struct UnitOrder(string Ability, Vector2? Point, ulong TargetRecord);

    /// <summary>
    /// Units' orders (roadmap B4). A unit's order queue is a
    /// circular list from [record + 0x60] (next order = [order] &amp; ~7). Per order: the ability's packed catalog link at
    /// +0x30 (CatalogNameOf: "Abil/move"); the flag byte at +0x57 - bit 7 set for a point target; a unit target's tag (u32)
    /// at +0x48; a point target encoded at +0x58 (x, y, z fixed 4096, 12 bytes), decoded by the game's point getter
    /// (signature below: it zeroes the 12-byte output, takes the field at rcx + 8 and switches on the player-slot selector
    /// into one of 16 decoders; every point getter of that shape picks the same decoder). Read twice a second on the
    /// scans worker; a decoded point is kept per (order, encoded bytes) so the emulator only runs for new orders.
    /// </summary>
    public sealed partial class SC2Memory
    {
        const int OrderHeadOff = 0x60, OrderAbilOff = 0x30, OrderTagOff = 0x48, OrderFlagsOff = 0x57, OrderPointOff = 0x58;
        // "push rbx; sub rsp,20h; xor eax,eax; mov rbx,rdx; mov [rdx],rax; mov [rdx+8],eax; lea rdx,[rcx+8];
        //  mov eax,[selector]; cmp eax,0Fh; ja ..; lea r8,[exe base]; mov ecx,[r8+rax*4+table]"
        const string PointGetterPattern = "40 53 48 83 EC 20 33 C0 48 8B DA 48 89 02 89 42 08 48 8D 51 08 8B 05 ? ? ? ? 83 F8 0F 0F 87 ? ? ? ? 4C 8D 05 ? ? ? ? 41 8B 8C 80 ? ? ? ?";

        readonly List<uint> _pointTables = [];
        ulong _pointDecoder; uint _pointDecoderSel = 99;
        volatile Dictionary<ulong, UnitOrder[]> _orders = [];
        DateTime _nextOrderScan;
        readonly Dictionary<ulong, string> _abilNames = [];                                   // ability link -> "move"
        readonly Dictionary<ulong, long> _abilMisses = [];                                     // ability link -> when to try naming it again
        readonly Dictionary<(ulong Order, ulong Enc), Vector2?> _pointCache = [];

        /// <summary>One order of a unit's order queue (fields at the offsets above).</summary>
        [StructLayout(LayoutKind.Explicit, Size = OrderPointOff + 0x10)]
        struct OrderNode
        {
            [FieldOffset(0)]              public ulong Next;        // & ~7
            [FieldOffset(OrderAbilOff)]   public ulong AbilLink;    // packed catalog link, & ~7
            [FieldOffset(OrderTagOff)]    public uint  TargetTag;   // a unit target (index = tag >> 18)
            [FieldOffset(OrderFlagsOff)]  public byte  Flags;       // bit 7: a point target
            [FieldOffset(OrderPointOff)]  public ulong Point;       // the encoded point (first 8 of its 12 bytes)
        }

        public bool HasOrders => _pointTables.Count > 0;

        /// <summary>Unit record -> its orders, current first (only units that have any).</summary>
        public IReadOnlyDictionary<ulong, UnitOrder[]> Orders => _orders;

        string ResolveOrders(List<(uint Rva, byte[] Data)> code)
        {
            _pointTables.Clear(); _pointDecoder = 0; _pointDecoderSel = 99;
            if (code.Count > 0)
                foreach (uint g in FindPattern(code, PointGetterPattern, 16))
                    if (CodeU32(code, g + 47) is uint t && t != 0) _pointTables.Add(t);
            if (!HasOrders) XMLogging.WriteLine("[Orders] signature 'point getter' missed - order targets unavailable");
            return $"orders {(HasOrders ? "sig" : "missed")}";
        }

        /// <summary>The point decoder for the current selector: the one most of the getters' tables agree on.</summary>
        ulong PointDecoder()
        {
            uint sel = _slotSelectorRva != 0 ? U32At(_slotSelectorRva) : 99;
            if (sel == _pointDecoderSel && _pointDecoder != 0) return _pointDecoder;
            _pointDecoderSel = sel;
            _pointDecoder = _pointTables.Select(SwitchDecoder).Where(d => d != 0).GroupBy(d => d).MaxBy(g => g.Count())?.Key ?? 0;
            _pointCache.Clear();
            return _pointDecoder;
        }

        /// <summary>Scans worker, twice a second: every player unit's order queue (at most 4 orders each).</summary>
        void ScanOrders()
        {
            if (DateTime.UtcNow < _nextOrderScan || !HasOrders) return;
            _nextOrderScan = DateTime.UtcNow.AddMilliseconds(500);
            var recs = Snapshot.Units.Where(u => u.Record != 0 && u.Owner is > 0 and < 16 &&
                                                 u.Category is UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Hero)
                                     .Select(u => u.Record).Distinct().ToArray();
            if (recs.Length == 0) { if (_orders.Count > 0) _orders = []; return; }

            // queue heads, then every unit's next order in one batched read per hop (at most 4 orders each)
            var heads = ReadPointers([.. recs.Select(r => r + OrderHeadOff)]);
            ulong decoder = PointDecoder();
            var byIndex = RecordsByIndex();
            var lists = new List<UnitOrder>?[recs.Length];
            var live = new HashSet<(ulong, ulong)>();
            var walk = new List<(int Unit, ulong Order)>();
            for (int i = 0; i < recs.Length; i++)
                if ((heads[i] & ~7UL) is var h && IsUserPtr(h)) walk.Add((i, h));
            for (int hop = 0; hop < 4 && walk.Count > 0; hop++)
            {
                if (!ReadStructs<OrderNode>([.. walk.Select(x => x.Order)], out var nodes, out var ok)) return;
                // the abilities not named yet, all at once (a link that named nothing is tried again only after a while)
                long now = Environment.TickCount64;
                var unnamed = Enumerable.Range(0, walk.Count).Where(k => ok[k]).Select(k => nodes[k].AbilLink & ~7UL)
                                        .Where(l => IsUserPtr(l) && !_abilNames.ContainsKey(l) && (!_abilMisses.TryGetValue(l, out long r) || now >= r))
                                        .Distinct().ToArray();
                var named = CatalogNamesOf(unnamed);
                for (int u = 0; u < unnamed.Length; u++)
                {
                    string n = named[u];
                    if (n.StartsWith("Abil/", StringComparison.Ordinal)) { _abilNames[unnamed[u]] = n[5..n.IndexOf(' ')]; _abilMisses.Remove(unnamed[u]); }
                    else _abilMisses[unnamed[u]] = now + 5000;
                }
                var next = new List<(int, ulong)>();
                for (int k = 0; k < walk.Count; k++)
                {
                    if (!ok[k]) continue;
                    var (unit, o) = walk[k];
                    ref readonly var node = ref nodes[k];
                    ulong link = node.AbilLink & ~7UL;
                    string name = _abilNames.GetValueOrDefault(link, "");
                    Vector2? point = null; ulong target = 0;
                    if ((node.Flags & 0x80) != 0)
                    {
                        // a point: decode once per (order, encoded value)
                        live.Add((o, node.Point));
                        if (!_pointCache.TryGetValue((o, node.Point), out point))
                        {
                            point = decoder != 0 && Decode8(decoder, o + OrderPointOff, (o, MemoryMarshal.AsBytes(new ReadOnlySpan<OrderNode>(in node)).ToArray())) is { } v &&
                                    (int)(uint)v is > 0 and < 512 * 4096 && (int)(uint)(v >> 32) is > 0 and < 512 * 4096
                                ? new Vector2((int)(uint)v / 4096f, (int)(uint)(v >> 32) / 4096f) : null;
                            _pointCache[(o, node.Point)] = point;
                        }
                    }
                    else if (node.TargetTag != 0) byIndex.TryGetValue(node.TargetTag >> 18, out target);
                    if (name.Length > 0) (lists[unit] ??= new List<UnitOrder>(2)).Add(new UnitOrder(name, point, target));
                    // the queue is circular: stop where it comes back round
                    ulong nx = node.Next & ~7UL;
                    if (nx != (heads[unit] & ~7UL) && nx != o && IsUserPtr(nx)) next.Add((unit, nx));
                }
                walk = next;
            }
            var result = new Dictionary<ulong, UnitOrder[]>();
            for (int i = 0; i < recs.Length; i++)
                if (lists[i] is { Count: > 0 } l) result[recs[i]] = [.. l];
            // forget decodes of orders that are gone
            if (_pointCache.Count > 4 * live.Count + 256)
                foreach (var k in _pointCache.Keys.Where(k => !live.Contains(k)).ToList()) _pointCache.Remove(k);
            if (_abilNames.Count > 4096) _abilNames.Clear();
            if (_abilMisses.Count > 4096) _abilMisses.Clear();
            _orders = result;
        }
        /// <summary>Unit index (tag &gt;&gt; 18) -> record, from the tag at +0 of every record on the map (one batched read).</summary>
        Dictionary<uint, ulong> RecordsByIndex()
        {
            var recs = Snapshot.Units.Where(u => u.Record != 0).Select(u => u.Record).Distinct().ToArray();
            var map = new Dictionary<uint, ulong>(recs.Length);
            var tb = new byte[recs.Length * 4]; var tok = new bool[recs.Length];
            if (recs.Length > 0 && ReadValues(recs, 4, tb, tok))
                for (int i = 0; i < recs.Length; i++)
                    if (tok[i]) map[BinaryPrimitives.ReadUInt32LittleEndian(tb.AsSpan(i * 4)) >> 18] = recs[i];
            return map;
        }
    }
}
