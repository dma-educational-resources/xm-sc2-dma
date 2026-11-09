namespace SC2Radar.SC2
{
    /// <summary>
    /// Inject and chrono on buildings (roadmap D2), from each building's behaviour instances (UnitBehaviors): a behaviour
    /// whose catalog id is Queen inject ("QueenSpawnLarvaTimer") or Chrono Boost ("ChronoBoostEnergyCost", earlier
    /// "TimeWarpProduction"); see BehaviourId for how the id is read. Scanned once a second on the scans worker for
    /// hatcheries and Protoss production / tech buildings. The id reading is checked in a match (SpawnLarva,
    /// PowerUserQueue, makeCreep8x6 read right); an actual inject / chrono hasn't been seen yet. The first scan logs
    /// the ids it found per building type.
    /// </summary>
    public sealed partial class SC2Memory
    {
        static readonly HashSet<string> Hatcheries = new(StringComparer.OrdinalIgnoreCase) { "Hatchery", "Lair", "Hive" };
        static readonly HashSet<string> ChronoTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            "Nexus", "Gateway", "WarpGate", "Forge", "CyberneticsCore", "TwilightCouncil", "RoboticsFacility", "RoboticsBay",
            "Stargate", "TemplarArchives", "TemplarArchive", "FleetBeacon", "DarkShrine", "SuperWarpGate",
        };

        // the buffs themselves, by id (the hatchery's own SpawnLarva behaviour is the larva spawner, not an inject)
        static readonly HashSet<string> InjectIds = new(StringComparer.OrdinalIgnoreCase) { "QueenSpawnLarvaTimer" };
        static readonly HashSet<string> ChronoIds = new(StringComparer.OrdinalIgnoreCase) { "ChronoBoostEnergyCost", "ChronoBoost", "TimeWarpProduction" };

        DateTime _nextBuffScan;
        bool _buffIdsLogged;
        volatile HashSet<ulong> _injected = [], _chronoed = [];

        /// <summary>Hatchery / Lair / Hive records with a Queen inject in progress.</summary>
        public IReadOnlySet<ulong> Injected => _injected;

        /// <summary>Building records under Chrono Boost.</summary>
        public IReadOnlySet<ulong> Chronoed => _chronoed;

        /// <summary>Scans worker: once a second, the behaviour ids of every hatchery and chrono-able building.</summary>
        void ScanBuffs()
        {
            if (DateTime.UtcNow < _nextBuffScan) return;
            _nextBuffScan = DateTime.UtcNow.AddSeconds(1);
            var injected = new HashSet<ulong>();
            var chronoed = new HashSet<ulong>();
            var seenIds = _buffIdsLogged ? null : new Dictionary<string, HashSet<string>>();
            var buildings = Snapshot.Units.Where(u => u.Record != 0 && u.IsBuilding &&
                                                      (Hatcheries.Contains(u.TypeName) || ChronoTargets.Contains(u.TypeName)))
                                          .DistinctBy(u => u.Record).ToArray();
            // every building's behaviour ids in one batched walk (ScanUnitBuffs' reads)
            if (buildings.Length == 0) { (_injected, _chronoed) = (injected, chronoed); return; }
            if (ReadBehaviourIds([.. buildings.Select(u => u.Record)]) is not { } idsOf) return;   // keep the last result
            foreach (var u in buildings)
            {
                if (!idsOf.TryGetValue(u.Record, out var ids)) continue;
                bool hatch = Hatcheries.Contains(u.TypeName);
                foreach (string id in ids)
                {
                    if (seenIds != null) { if (!seenIds.TryGetValue(u.TypeName, out var s)) seenIds[u.TypeName] = s = []; s.Add(id); }
                    if (hatch && InjectIds.Contains(id)) injected.Add(u.Record);
                    if (ChronoIds.Contains(id)) chronoed.Add(u.Record);
                }
            }
            if (seenIds is { Count: > 0 })
            {
                _buffIdsLogged = true;
                foreach (var (type, ids) in seenIds)
                    XMLogging.WriteLineFileOnly($"[Buffs] {type} behaviours: {string.Join(", ", ids.Order())}");
            }
            (_injected, _chronoed) = (injected, chronoed);
        }
        readonly Dictionary<ulong, string> _behaviourIds = [];   // link -> id; links are per behaviour type and live all match

        /// <summary>
        /// A behaviour instance's catalog id ("QueenSpawnLarvaTimer"), "" when it can't be read. The packed link at inst +
        /// 0x60 (q) gives link = q &lt;&lt; 5: [link] is the Behavior catalog, the u16 at link + 0xA the behaviour's index in it,
        /// and [[catalog + 0xA0] + index * 8] its id record, whose name runs from [record + 8] to [record + 0x10]
        /// (observed ids: SpawnLarva 0xAB, PowerUserQueue 0xA4, QueenSpawnLarvaTimer 0x61, TimeWarpProduction 0x7A).
        /// </summary>
        /// <summary>
        /// <see cref="BehaviourIdOfLink"/> for many packed links at once: the ones not known yet are named together
        /// (<see cref="ResolveCatalogLinks"/>, one batched read per step) and cached; unread ones are tried again next time.
        /// </summary>
        void ResolveBehaviourIds(IEnumerable<ulong> qs)
        {
            var links = qs.Select(q => q << 5).Where(l => IsUserPtr(l) && !_behaviourIds.ContainsKey(l)).Distinct().ToArray();
            if (links.Length == 0) return;
            var named = ResolveCatalogLinks([.. links.Where(l => !_catalogLinkNames.ContainsKey(l))]);
            foreach (ulong l in links)
            {
                if (!_catalogLinkNames.TryGetValue(l, out var n) && !named.TryGetValue(l, out n)) continue;
                int slash = n.IndexOf('/');
                string id = slash >= 0 ? n[(slash + 1)..] : "";
                if (id.Length > 0 && id != "?" && id.All(c => c is >= ' ' and <= '~')) _behaviourIds[l] = id;
            }
        }

        string BehaviourId(ulong inst) =>ReadU64(inst + 0x60) is var q && q != 0 ? BehaviourIdOfLink(q) : "";

        /// <summary>BehaviourId from the packed link itself (cached per link).</summary>
        string BehaviourIdOfLink(ulong q)
        {
            ulong link = q << 5;
            if (!IsUserPtr(link)) return "";
            if (_behaviourIds.TryGetValue(link, out var cached)) return cached;
            string id = "";
            ulong catalog = ReadU64(link);
            if (IsUserPtr(catalog) && ReadBytes(link + 0xA, 2) is { } ib)
            {
                int index = BinaryPrimitives.ReadUInt16LittleEndian(ib);
                ulong arr = ReadU64(catalog + 0xA0), rec = IsUserPtr(arr) ? ReadU64(arr + (ulong)index * 8) : 0;
                ulong s = rec > 0x10000 ? ReadU64(rec + 8) : 0, e = rec > 0x10000 ? ReadU64(rec + 0x10) : 0;
                if (s > 0x10000 && e > s && e - s < 80 && ReadBytes(s, (int)(e - s)) is { } b && b.All(c => c is >= 0x20 and <= 0x7E))
                    id = Encoding.ASCII.GetString(b);
            }
            if (id.Length > 0) _behaviourIds[link] = id;   // unread ones are retried next time
            return id;
        }
    }
}
