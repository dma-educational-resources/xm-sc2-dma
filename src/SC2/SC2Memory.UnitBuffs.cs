namespace SC2Radar.SC2
{
    /// <summary>
    /// Every unit's buffs / debuffs: the catalog ids of its behaviour instances ("Red", "Detector10", "ChronoBoostEnergyCost"),
    /// for the hover card and for "buff:" patterns in the Unit Filter search, highlights, markers and custom styles. The
    /// same walk as UnitBehaviors (container at unit + 0x110, nodes of up to 8 instances, skipped when [inst + 0x68] bit 0
    /// is set), done for all units at once in batched reads: queue heads, then nodes, then the instances' links. Ids are
    /// cached per link and new ones named in batches, so a scan is a handful of scatter reads. Every 1.5 s on the scans worker (Config.TrackBuffs);
    /// ScanBuffs uses the same walk for inject and chrono.
    /// </summary>
    public sealed partial class SC2Memory
    {
        volatile Dictionary<ulong, string[]> _unitBuffs = [];
        DateTime _nextUnitBuffScan;

        /// <summary>Unit record -> its behaviour ids (only units that have any).</summary>
        public IReadOnlyDictionary<ulong, string[]> UnitBuffs => _unitBuffs;

        const ulong BehaviourQueueHeadOff = 0x110 + 0x18;

        /// <summary>A node of a unit's behaviour list: the next node (bit 0 set at the end), up to 8 instances, their count.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 0x50)]
        struct BehaviourNode
        {
            [FieldOffset(0x00)] public ulong Next;
            [FieldOffset(0x08)] public Instances8 Instances;
            [FieldOffset(0x48)] public byte Count;
        }

        [InlineArray(8)]
        struct Instances8 { ulong _first; }

        /// <summary>A behaviour instance from +0x60: its packed catalog link, and flags (bit 0: inactive).</summary>
        [StructLayout(LayoutKind.Explicit, Size = 0x10)]
        struct BehaviourLink
        {
            [FieldOffset(0x00)] public ulong Link;
            [FieldOffset(0x08)] public byte Flags;
        }

        void ScanUnitBuffs()
        {
            if (DateTime.UtcNow < _nextUnitBuffScan) return;
            _nextUnitBuffScan = DateTime.UtcNow.AddMilliseconds(1500);
            var recs = Snapshot.Units.Where(u => u.Record != 0 && u.Category != UnitCategory.Resource).Select(u => u.Record).Distinct().ToArray();
            if (recs.Length == 0) { if (_unitBuffs.Count > 0) _unitBuffs = []; return; }
            if (ReadBehaviourIds(recs) is { } ids) _unitBuffs = ids;
        }

        /// <summary>
        /// The active behaviour ids of every record, in a few batched reads (queue heads, first nodes, up to 4 rounds of
        /// nodes, instance links); records without any are left out. Null when a batched read failed as a whole.
        /// </summary>
        Dictionary<ulong, string[]>? ReadBehaviourIds(ulong[] recs)
        {
            if (ReadBehaviourInstances(recs, BehaviourQueueHeadOff, maxNodes: 4) is not { } insts) return null;
            var result = new Dictionary<ulong, string[]>();

            // each instance's link and flags
            if (!ReadStructs<BehaviourLink>([.. insts.Select(x => x.Inst + 0x60)], out var links, out var lok)) return null;
            // the ids not known yet, all at once (one batched read per step); unread ones are left out this time
            ResolveBehaviourIds(Enumerable.Range(0, insts.Count).Where(k => lok[k] && (links[k].Flags & 1) == 0 && links[k].Link != 0)
                                          .Select(k => links[k].Link));
            var lists = new Dictionary<int, List<string>>();
            for (int k = 0; k < insts.Count; k++)
            {
                if (!lok[k] || (links[k].Flags & 1) != 0) continue;
                string id = links[k].Link != 0 ? _behaviourIds.GetValueOrDefault(links[k].Link << 5, "") : "";
                if (id.Length == 0) continue;
                if (!lists.TryGetValue(insts[k].Unit, out var l)) lists[insts[k].Unit] = l = [];
                if (!l.Contains(id)) l.Add(id);
            }
            foreach (var (unit, ids) in lists) result[recs[unit]] = [.. ids];
            return result;
        }

        /// <summary>
        /// Every record's behaviour instances (index into <paramref name="recs"/>, instance), in list order, walked for all
        /// records at once: queue heads, first nodes, then one batched read per node depth (at most
        /// <paramref name="maxNodes"/>). Null when a batched read failed as a whole.
        /// </summary>
        List<(int Unit, ulong Inst)>? ReadBehaviourInstances(ulong[] recs, ulong headOff, int maxNodes)
        {
            var insts = new List<(int Unit, ulong Inst)>();
            // queue heads, then the first node of each
            var heads = ReadPointers([.. recs.Select(r => r + headOff)]);
            var owner = new List<int>(); var headList = new List<ulong>();
            for (int i = 0; i < recs.Length; i++)
                if (IsUserPtr(heads[i])) { owner.Add(i); headList.Add(heads[i]); }
            if (headList.Count == 0) return insts;
            var first = ReadPointers(CollectionsMarshal.AsSpan(headList));
            var nodes = new List<(int Unit, ulong Node)>();
            for (int k = 0; k < first.Length; k++)
                if ((first[k] & ~1UL) is var n && IsUserPtr(n)) nodes.Add((owner[k], n));

            // nodes (and the nodes after them): instance pointers
            for (int round = 0; round < maxNodes && nodes.Count > 0; round++)
            {
                if (!ReadStructs<BehaviourNode>([.. nodes.Select(x => x.Node)], out var nb, out var nok)) return null;
                var next = new List<(int, ulong)>();
                for (int k = 0; k < nodes.Count; k++)
                {
                    if (!nok[k]) continue;
                    ref readonly var node = ref nb[k];
                    int cnt = Math.Min((int)node.Count, 8);
                    for (int j = 0; j < cnt; j++)
                        if (node.Instances[j] is var inst && IsObjPtr(inst)) insts.Add((nodes[k].Unit, inst));
                    if ((node.Next & 1) == 0 && IsUserPtr(node.Next) && node.Next != nodes[k].Node) next.Add((nodes[k].Unit, node.Next));
                }
                nodes = next;
            }
            // a unit's later nodes come in later rounds: back in list order per unit
            return [.. insts.Select((x, i) => (x, i)).OrderBy(t => t.x.Unit).ThenBy(t => t.i).Select(t => t.x)];
        }
    }
}
