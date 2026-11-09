using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Radar data source: SC2 render actors.
    ///   actor vtable  SC2_x64.exe + Config.ActorVtableRva
    ///   position      float X/Y at +ActorPosOffset
    ///   model path    *(*(actor + ActorModelOffset) + 0x40)  e.g. assets\units\terran\marine\marine.m3
    ///   team colour   float RGB at +ActorColorOffset
    /// Discovery (background task) - the game's actor table:
    ///   scene  = *(SC2_x64.exe + Config.ActorTableRva)                (object, vtable exe+0x2E7C4C8)
    ///   world  = *(scene + Config.ActorTableSceneOffset)              (object, vtable exe+0x2E80F80)
    ///   world + Config.ActorTablePagesOffset = { ulong page; ulong slots = 0x800; }[]  (ends at slots != 0x800)
    ///   each page = 0x800 object pointers: actors mixed with other render objects, free slots non-pointers.
    /// Re-read every ActorTableRefreshMs; name/category/colour are resolved once per new pointer.
    /// If the table can't be read, falls back to a full heap scan for the actor vtable (~25s, every ActorRescanSeconds).
    /// The per-tick read only fetches vtable + position for units and buildings.
    /// </summary>
    public sealed partial class SC2Memory
    {
        sealed record ActorInfo(ulong Addr, string Name, string Path, UnitCategory Category, uint Color, ulong Model)
        {
            /// <summary>Never moves: mineral fields, geysers and buildings - except Terran ones (they lift off) and crawlers
            /// (they uproot). The realtime worker re-reads these only a few times a second.</summary>
            public bool CantMove { get; } = CantMoveOf(Category, Name, Path);
            /// <summary>Scenery and other decoration (shown only when the Unit Filter asks): the realtime worker reads a
            /// slice of these per frame, so a map with thousands of them doesn't slow the units down.</summary>
            public bool IsDecor { get; } = Category is UnitCategory.Scenery or UnitCategory.Other;
            /// <summary>A dead unit's death / ragdoll model (classed as Other, see NonUnitName): the Unit Filter's Remains.</summary>
            public bool IsRemains { get; } = Category == UnitCategory.Other && RemainsName.IsMatch(Name);
        }

        static readonly Regex RemainsName = new(@"death|ragdoll", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static bool CantMoveOf(UnitCategory category, string name, string path) =>
            category == UnitCategory.Resource ||
            category == UnitCategory.Building && !path.Contains(@"\terran\", StringComparison.OrdinalIgnoreCase) &&
                                                 !name.Contains("crawler", StringComparison.OrdinalIgnoreCase);

        ActorInfo[]           _actors = [];
        // model -> path. A model address can be reused for another model once the game unloads one, so each entry
        // keeps the path string pointer it was read from and is re-checked once per resolve pass (_modelChecked).
        readonly Dictionary<ulong, (ulong Str, string Path, string Name)> _modelCache = [];
        readonly HashSet<ulong> _modelChecked = [];
        readonly HashSet<ulong> _staleSlots = [];   // actors whose model changed since they were resolved (main thread -> rescan)
        Task?                 _rescanTask;
        DateTime              _nextRescan = DateTime.MinValue;

        public int    ActorsTracked  => _actors.Length;
        public int    ActorsTotal    { get; private set; }
        public double LastRescanSec  { get; private set; }
        public bool   RescanRunning  => _rescanTask is { IsCompleted: false };
        public string ActorSource    { get; private set; } = "-";
        public string MissionState   { get; private set; } = "unknown";

        bool         _wasInMission;
        DateTime     _nextIdleRefresh = DateTime.MinValue;
        volatile int _actorGen;   // bumped on mission change; a rescan only publishes if it still matches

        const int  TablePageSlots = 0x800, TableMaxPages = 64;
        // table slot pointer -> resolved actor (null = not a unit/building actor); cleared every ActorRescanSeconds
        readonly Dictionary<ulong, ActorInfo?> _slotCache = [];
        DateTime _slotCacheReset = DateTime.MinValue;

        static readonly Regex NameSuffix = new(@"(ex\d+)?(mp)?(_\d+)?$", RegexOptions.Compiled);
        // models that are not units even when filed under units / buildings: placement previews, waypoint and rally
        // markers, death / ragdoll remains, warp-in effects and build-site ghosts. Their record chain leads to some
        // other unit's record (wrong owner, vision), so they get no vitals.
        static readonly Regex NonUnitName = new(@"placement|waypoint|rally|death|ragdoll|warpin|^(terran|protoss|zerg)build",
                                                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly HashSet<string> Workers = ["scv", "drone", "probe", "mule"];

        /// <summary>A building's warp-in model ("pylonwarpin" under ...\buildings\...): the building while it warps in.</summary>
        static bool IsBuildingWarpIn(string name, string path) =>
            name.EndsWith("warpin", StringComparison.OrdinalIgnoreCase) && path.Contains(@"\buildings\", StringComparison.OrdinalIgnoreCase);

        /// <summary>The state pass and the position pass in turn.</summary>
        void ReadActors()
        {
            ReadState();
            ReadPositions();
        }

        /// <summary>
        /// The actors read this pass: scenery / effects / other only while the Unit Filter shows them (most of all actors),
        /// and not the decoration it never draws (UnitFilter.SkipReading: remains, hidden types, hide patterns). Worked out
        /// again only when the actor list, the filter or the radar profile changes (the realtime and misc workers both ask).
        /// </summary>
        ActorInfo[] VisibleActors()
        {
            var actors = _actors;
            var fs = UnitFilter.S;
            var profile = RadarProfiles.Active;
            int version = UnitFilter.Version;
            if (_visible is { } v && ReferenceEquals(v.Actors, actors) && v.Version == version && ReferenceEquals(v.Profile, profile))
                return v.Result;
            var skip = new Dictionary<(string, string, UnitCategory, bool), bool>();   // per model: the decision is the same for every actor of it
            var result = Array.FindAll(actors, a =>
                (a.Category != UnitCategory.Scenery || fs.ShowScenery) &&
                (a.Category != UnitCategory.Effect  || fs.ShowEffects) &&
                (a.Category != UnitCategory.Other   || fs.ShowOther) &&
                !(skip.TryGetValue((a.Name, a.Path, a.Category, a.IsRemains), out bool s)
                    ? s : skip[(a.Name, a.Path, a.Category, a.IsRemains)] = UnitFilter.SkipReading(a.Name, a.Path, a.Category, a.IsRemains)));
            _visible = new VisibleSet(actors, version, profile, result);
            return result;
        }

        sealed record VisibleSet(ActorInfo[] Actors, int Version, object Profile, ActorInfo[] Result);
        volatile VisibleSet? _visible;

        /// <summary>
        /// Misc worker: the mission gate, the actor rescan, and every reader with its own cadence (burrow, vision, unit
        /// details, players, match, cameras, upgrades, creep, names). The realtime worker builds the snapshot from what
        /// this leaves (_actors, _vitals, _burrowed).
        /// </summary>
        void ReadState()
        {
            // mission gate (DisplayMonitor's console check): no list walks outside a mission, and a fresh
            // list after every mission load / restart. Without detection (signature miss) behave as before.
            if (Features.MemoryWrites.Ultrawide.DisplayMonitor.MissionDetectAvailable)
            {
                bool inMission = Features.MemoryWrites.Ultrawide.DisplayMonitor.InMission;
                if (inMission != _wasInMission)
                {
                    _wasInMission = inMission;
                    RefreshTranslations(full: true);          // the game rebuilt its heap: drop stale page tables
                    _actorGen++;                              // discard results of a rescan already in flight (and the realtime worker's type names)
                    _actors = []; ActorsTotal = 0;
                    _recCache.Clear(); _vitals = [];          // unit records are per mission too
                    _unitObjOf.Clear(); _recLinks.Clear(); _typeOf.Clear();
                    lock (_modelCache) _modelCache.Clear();   // models are reloaded per mission
                    _typeNameCache.Clear();
                    ResetMatch();                             // new map, clock and terrain
                    ResetPlayerState();                       // player objects, start locations, resource behaviours
                    _scanGen++;                               // the scans worker resets its own state
                    _nextRescan = DateTime.MinValue;          // walk the new mission's list right away
                    _slotCacheReset = DateTime.MinValue;      // new heap: re-resolve every pointer
                }
                if (!inMission)
                {
                    // out of a mission (or the console check is reading through stale page tables): refresh now and then
                    if (DateTime.UtcNow >= _nextIdleRefresh)
                    {
                        RefreshTranslations(full: true);
                        _nextIdleRefresh = DateTime.UtcNow.AddSeconds(5);
                    }
                    MissionState = $"not in a mission ({Features.MemoryWrites.Ultrawide.BottomConsole.MissReason})";
                    _positionsOn = false;
                    Snapshot = UnitSnapshot.Empty;
                    return;
                }
                MissionState = "in a mission";
            }
            else { _wasInMission = false; MissionState = "unknown"; }
            _positionsOn = true;

            if (!RescanRunning && DateTime.UtcNow >= _nextRescan)
                _rescanTask = Task.Run(RescanActors);

            var actors = VisibleActors();
            if (actors.Length == 0) return;
            if (DateTime.UtcNow >= _nextBurrowRead)
            {
                _nextBurrowRead = DateTime.UtcNow.AddMilliseconds(500);
                Scan("State/Burrow", () => ReadBurrowStates(actors), rethrow: true);
            }
            List<int> watched;
            try { watched = [.. _cfg.VisionPlayers]; } catch (InvalidOperationException) { watched = []; }   // the UI thread edits it
            Scan("State/Vision", () => ReadVision(watched), rethrow: true);
            if (DateTime.UtcNow >= _nextVitalsRead)
            {
                _nextVitalsRead = DateTime.UtcNow.AddMilliseconds(250);
                Scan("State/Vitals", () => ReadVitals(actors), rethrow: true);
                Scan("State/Alliances", ReadAlliances, rethrow: true);
                Scan("State/LocalPlayer", ReadLocalPlayer, rethrow: true);
                Scan("State/Match", ReadMatch, rethrow: true);
                Scan("State/PlayerState", ReadPlayerState, rethrow: true);
                Scan("State/Upgrades", ReadUpgrades, rethrow: true);
                Scan("State/Creep", ReadCreep, rethrow: true);
                if (DateTime.UtcNow >= _nextNamesRead)
                {
                    _nextNamesRead = DateTime.UtcNow.AddSeconds(2);
                    Scan("State/PlayerNames", ReadPlayerNames, rethrow: true);
                }
            }
        }

        volatile bool _positionsOn;   // set by the misc worker's mission gate: the realtime worker reads positions
        int _stickyGen = -1;          // the _actorGen that _stickyType belongs to (realtime worker)

        /// <summary>
        /// Realtime worker: every visible actor's frame (vtable, position, model, cloak flags) in one batched read, joined
        /// with the latest unit details, published as the new <see cref="Snapshot"/>.
        /// </summary>
        void ReadPositions()
        {
            // out of a mission: empty (also undoes a snapshot this thread published while the misc worker closed the gate)
            if (!_positionsOn) { if (Snapshot.Units.Length > 0) Snapshot = UnitSnapshot.Empty; return; }
            var actors = VisibleActors();
            if (actors.Length == 0) { Snapshot = UnitSnapshot.Empty; return; }
            // a new mission: the per-actor type names belong to the last one
            if (_stickyGen != _actorGen) { _stickyGen = _actorGen; _stickyType.Clear(); _slowFrames.Clear(); _nextFixedRead = 0; _decorCursor = 0; }

            ulong vt = ActorVt;
            if (!ReadFrames(actors, out var frames, out var ok)) return;
            var vitals = _vitals;
            var burrowed = _burrowed;
            var units = new List<UnitEntry>(actors.Length);
            int readable = 0;
            for (int i = 0; i < actors.Length; i++)
            {
                if (!ok[i]) continue;
                readable++;
                ref readonly var f = ref frames[i];
                if (f.Vtable != vt) continue;   // freed / reused
                var a = actors[i];
                if (!_unitObjOf.TryGetValue(a.Addr, out ulong uo) || uo != f.UnitObj) _unitObjOf[a.Addr] = f.UnitObj;
                // model swapped (spawn / morph): re-resolve name and category on the next table refresh
                if (f.Model != a.Model)
                    lock (_staleSlots) _staleSlots.Add(a.Addr);
                float x = f.X, y = f.Y;
                if (!(x >= 0.5f && x <= 512f && y >= 0.5f && y <= 512f)) continue;
                vitals.TryGetValue(a.Addr, out var vit);
                // the type name only resolves when this pass reached the unit's record; keep the last one for this actor and
                // model, so the label doesn't flick between "Vespene Geyser High" and the model name "vespenegeyser_"
                string type = vit.TypeName ?? "";
                if (type.Length > 0) _stickyType[(a.Addr, a.Name)] = type;
                else type = _stickyType.GetValueOrDefault((a.Addr, a.Name), "");
                units.Add(new UnitEntry(x, y, a.Color, a.Name, a.Category, a.Path, a.Addr,
                                        burrowed.Contains(a.Addr),
                                        (f.Flags & CloakBit) != 0,
                                        vit.Damage, vit.Energy, vit.MaxHp, vit.ShieldDamage, vit.MaxShields, vit.MaxEnergy,
                                        vit.Owner, vit.Food, vit.Minerals, vit.Gas, vit.CostCategory, vit.Sight, vit.Record,
                                        type, vit.EyeHeight, vit.Record != 0 ? vit.Resources : -1,
                                        vit.Record != 0 ? vit.Progress : -1f,
                                        vit.Record != 0 ? vit.QueueProgress : -1f, vit.QueueCount, vit.Record != 0 && vit.CanQueue,
                                        vit.Record != 0 ? vit.QueueProduct ?? "" : "", vit.Record != 0 ? vit.TimeLeft : -1f, vit.Record != 0 ? vit.Range : -1f,
                                        vit.Record != 0 && vit.Detector, IsBuildingWarpIn(a.Name, a.Path)));
            }
            // every read failed: stale page tables, or the process went away - refresh and force a re-attach
            if (readable == 0) { RefreshTranslations(full: true); IsConnected = false; InGame = false; _actors = []; }

            Snapshot = new UnitSnapshot(OnePerUnit(units));
            _knownUnitAddrs = [.. actors.Take(20).Select(a => a.Addr)];
        }

        /// <summary>
        /// One entry per game unit. A unit can have several actors sharing its record (zerglings and roaches carry an
        /// "...Upgrade" attachment), and mineral fields and geysers have a record-less twin at the same spot; each draws its
        /// own dot and label, and when only one names the type the labels differ and the overlap filter flicks between
        /// them. Keeps the actor that names the unit best (resolved, not an attachment, a real category), with burrow /
        /// cloak from any, and drops record-less twins.
        /// </summary>
        static UnitEntry[] OnePerUnit(List<UnitEntry> units)
        {
            static int Score(in UnitEntry u) =>
                (u.TypeName.Length > 0 ? 4 : 0) - (u.TypeName.EndsWith("Upgrade", StringComparison.Ordinal) ? 2 : 0) +
                (u.Category is UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building or UnitCategory.Resource ? 1 : 0);
            var at = new Dictionary<ulong, int>();
            var keep = new List<UnitEntry>(units.Count);
            foreach (var u in units)
            {
                if (u.Record == 0) { keep.Add(u); continue; }
                if (!at.TryGetValue(u.Record, out int i)) { at[u.Record] = keep.Count; keep.Add(u); continue; }
                var k = keep[i];
                var best = Score(in u) > Score(in k) ? u : k;
                keep[i] = best with { Burrowed = k.Burrowed || u.Burrowed, Cloaked = k.Cloaked || u.Cloaked };
            }
            // mineral fields and geysers also have a twin actor with no record at all: same model, same spot
            var occupied = new HashSet<(string, int, int)>();
            foreach (var u in keep)
                if (u.Record != 0) occupied.Add((u.Name, (int)MathF.Round(u.X * 2f), (int)MathF.Round(u.Y * 2f)));
            if (occupied.Count > 0)
                keep.RemoveAll(u => u.Record == 0 && occupied.Contains((u.Name, (int)MathF.Round(u.X * 2f), (int)MathF.Round(u.Y * 2f))));
            return [.. keep];
        }

        void RescanActors()
        {
            var sw = Stopwatch.StartNew();
            bool fromTable = false;
            int gen = _actorGen;
            lock (_modelCache) _modelChecked.Clear();
            try
            {
                var st = ReadActorTableResolved(out var slots);
                if (st == TableState.Ok)
                {
                    fromTable = true;
                    ResolveTableSlots(slots, gen);
                    ActorSource = "table";
                }
                else if (st == TableState.NoScene)
                {
                    // no match loaded: keep polling the cheap table path instead of heap scanning a menu
                    fromTable = true;
                    if (gen == _actorGen) { _actors = []; ActorsTotal = 0; }
                    ActorSource = "waiting for match";
                }
                else
                {
                    var found = FindInstances(ActorVt);
                    var list  = new List<ActorInfo>(found.Count / 2);
                    foreach (var a in found)
                        if (ResolveActor(a, ReadBytes(a, ActorInfoBytes)) is { } info) list.Add(info);
                    if (gen == _actorGen) { _actors = [.. list]; ActorsTotal = found.Count; }
                    ActorSource = "heap scan";
                }
            }
            catch (Exception ex) { XMLogging.WriteLine($"[Actors] rescan failed: {ex.Message}"); }
            finally
            {
                LastRescanSec = sw.Elapsed.TotalSeconds;
                _nextRescan   = fromTable ? DateTime.UtcNow.AddMilliseconds(Math.Max(50, _cfg.ActorTableRefreshMs))
                                          : DateTime.UtcNow.AddSeconds(Math.Max(1, _cfg.ActorRescanSeconds));
                if (gen != _actorGen) _nextRescan = DateTime.MinValue;   // mission changed mid-rescan: go again now
            }
        }

        int ActorInfoBytes => Math.Max(_cfg.ActorModelOffset + 8, _cfg.ActorColorOffset + 12);

        // ── burrow state ─────────────────────────────────────────────────────
        // Each actor points at its current entry in a table of state names:
        //   *(*(actor + 0x178) + 0x48) + 0x20 = "Burrow\0"  (normal units: "::Baseline")
        public const int StatePtrOff = 0x178, StateEntryOff = 0x48, StateNameOff = 0x20;

        // ── vitals (damage taken / energy) ───────────────────────────────────
        // The render actor leads to the game's unit record (found by an exact-value search: damage one ghost
        // 100 -> 80 -> 60 HP, then the actor -> record pointer path; resolved all 174 units to distinct records):
        //   record = *(*(*(actor + 0x138) + 0x20) + 0x258)
        //   record + 0x198 = damage taken (fixed-point / 4096)   - HP lost, not HP left
        //   record + 0x19C = shield damage taken (fixed-point / 4096)
        //   record + 0x1A0 = current energy (fixed-point / 4096) - confirmed with an EMP cast
        // Max life / shields are computed like the game's getters (5.0.x: life exe+0x69CD60, shields exe+0x699790;
        // found via the CUnit catalog schema registering LifeMax @+0x54 and ShieldsMax @+0xF8). v = 0 life, 1 shields:
        //   q   = record + (byte[0x2E] & 0x20 ? 0x10 : 0x08)       compressed: catalog list << 5, index in bits 59..63
        //   cat = *(*((q << 5) + 0x18) + ((q >> 59) & 31) * 8)    the unit's (per player) CUnit catalog entry
        //   base  = cat[life 0x54 | shields 0xF8] * pct[byte rec[0x54]] / 100   pct = int table in the exe (LifePctSig)
        //   flag  = byte rec[0x34] bit 4 -> mult = cat[0x1AC + 4v], bonus = cat[0x1A0 + 4v]   (else 1.0 / 0)
        //   max   = (rec[0x1A4 + 4v] + bonus + base * (rec[0x1B0 + 4v] * mult)) * rec[0x1BC + 4v]   all fixed 4096,
        //           at least 1.0 for life, 0 for shields
        //   energy (v = 2, exe+0x878690): same with cat[0xC4] (EnergyMax) and no percent scaling
        // Owner: byte rec[0x54] is the player id (0 = neutral) - also the index into the handicap percent table.
        // Supply / cost from the catalog entry (schema names): Food @0x1F8 (fixed, negative = uses supply,
        // positive = provides), CostCategory @0x1FC (1 army, 2 economy, 3 technology), CostResource @0x200
        // (int minerals, vespene, terrazine, custom).
        public static readonly int[] RecordPath = [0x138, 0x20, 0x258];
        public const int RecordDamageOff = 0x198, RecordShieldDamageOff = 0x19C, RecordEnergyOff = 0x1A0, RecordOwnerOff = 0x54;
        // detector: bit 3 of rec[0x29], set on every unit that detects (Overseer, Spore Crawler, turrets, cannons, Science Vessel,
        // campaign Overlords and Hybrids) and per unit, not per type; rec[0x11A] bit 5 and rec[0x11D] bit 0 mirror it
        const int RecordDetectorOff = 0x29, RecordDetectorBit = 0x08;
        const int RecordReadLen = 0x1C8, CatReadLen = 0x210;
        const int CatFoodOff = 0x1F8, CatCostCategoryOff = 0x1FC, CatCostOff = 0x200, CatSightOff = 0x1E8;   // Sight: fixed
        const int CatHeightOff = 0x114, CatVisionHeightOff = 0x1F4;   // fixed: flying height, eye height above the ground

        readonly record struct Vitals(float Damage, float Energy, float MaxHp, float ShieldDamage, float MaxShields, float MaxEnergy,
                                      byte Owner, float Food, int Minerals, int Gas, int CostCategory, float Sight, ulong Record,
                                      string TypeName, float EyeHeight, int Resources, float Progress, float QueueProgress, int QueueCount, bool CanQueue, string QueueProduct, float TimeLeft, float Range, bool Detector);

        readonly Dictionary<ulong, ulong> _recCache = [];   // actor -> unit record, rebuilt every vitals pass
        readonly ConcurrentDictionary<ulong, ulong> _unitObjOf = new();   // actor -> unit object, from the realtime worker's reads
        readonly Dictionary<ulong, (ulong UnitObj, ulong Rec, long Next)> _recLinks = [];               // actor -> record, checked again at Next
        readonly Dictionary<ulong, (ulong UnitObj, ulong Model, string Name, long Next)> _typeOf = [];  // actor -> type name, read again at Next

        /// <summary>When a cached per-unit link is checked again: about a second, spread so the units don't all come due on
        /// the same pass.</summary>
        static long RecheckMs(ulong addr) => 750 + (long)((addr >> 4) % 500);
        Dictionary<ulong, Vitals> _vitals = [];
        DateTime _nextVitalsRead = DateTime.MinValue;

        void ReadVitals(ActorInfo[] actors)
        {
            var units = Array.FindAll(actors, a => a.Category is UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building or UnitCategory.Resource);
            if (units.Length == 0) { _vitals = []; return; }

            // actor -> unit record. The game recycles actor memory (a lowered depot becomes a new actor, freed actors are
            // reused for other units), and a stale link hands one unit another unit's owner, energy and sight. The first hop
            // (actor -> unit object) comes fresh with every position read (ActorFrame.UnitObj); the other two are resolved
            // again when it changes, and checked about once a second otherwise. Each hop touches a page per unit, which on a
            // big match was most of this pass.
            long now = Environment.TickCount64;
            _recCache.Clear();
            var fresh = units.Select(a => a.Addr).ToArray();
            var unitObjs = new ulong[fresh.Length];   // *(actor + 0x138): the game-side unit object, which also leads to the type name
            var noFrame = new List<int>();
            for (int i = 0; i < fresh.Length; i++)
                if (_unitObjOf.TryGetValue(fresh[i], out ulong uo)) unitObjs[i] = uo;
                else noFrame.Add(i);
            if (noFrame.Count > 0)   // not read by the realtime worker yet: the hop from the actor
            {
                var objs = ReadPointers([.. noFrame.Select(i => fresh[i] + (ulong)RecordPath[0])]);
                for (int k = 0; k < objs.Length; k++) unitObjs[noFrame[k]] = objs[k];
            }
            var due = new List<int>();
            for (int i = 0; i < fresh.Length; i++)
            {
                if (!IsUserPtr(unitObjs[i])) continue;
                if (_recLinks.TryGetValue(fresh[i], out var link) && link.UnitObj == unitObjs[i] && now < link.Next) _recCache[fresh[i]] = link.Rec;
                else due.Add(i);
            }
            if (due.Count > 0)
            {
                var cur = due.Select(i => unitObjs[i]).ToArray();
                for (int h = 1; h < RecordPath.Length; h++)
                    cur = ReadPointers([.. cur.Select(p => IsUserPtr(p) ? p + (ulong)RecordPath[h] : 0UL)]);
                for (int k = 0; k < cur.Length; k++)
                {
                    int i = due[k];
                    if (!IsUserPtr(cur[k])) { _recLinks.Remove(fresh[i]); continue; }
                    _recCache[fresh[i]] = cur[k];
                    _recLinks[fresh[i]] = (unitObjs[i], cur[k], now + RecheckMs(fresh[i]));
                }
            }

            // type names: read again when the unit object or model changes, and about once a second otherwise
            var typeNames = new string[fresh.Length];
            var nameDue = new List<int>();
            for (int i = 0; i < fresh.Length; i++)
                if (_typeOf.TryGetValue(fresh[i], out var t) && t.UnitObj == unitObjs[i] && t.Model == units[i].Model && now < t.Next) typeNames[i] = t.Name;
                else nameDue.Add(i);
            if (nameDue.Count > 0)
            {
                var names = ResolveTypeNames([.. nameDue.Select(i => unitObjs[i])]);
                for (int k = 0; k < names.Length; k++)
                {
                    int i = nameDue[k];
                    typeNames[i] = names[k];
                    _typeOf[fresh[i]] = (unitObjs[i], units[i].Model, names[k], now + RecheckMs(fresh[i]));
                }
            }
            if (_recLinks.Count > 2 * fresh.Length + 256 || _typeOf.Count > 2 * fresh.Length + 256 || _unitObjOf.Count > 4 * actors.Length + 1024)
            {
                var live = actors.Select(a => a.Addr).ToHashSet();
                foreach (ulong a in _recLinks.Keys.Where(a => !live.Contains(a)).ToList()) _recLinks.Remove(a);
                foreach (ulong a in _typeOf.Keys.Where(a => !live.Contains(a)).ToList()) _typeOf.Remove(a);
                foreach (ulong a in _unitObjOf.Keys.Where(a => !live.Contains(a)).ToList()) _unitObjOf.TryRemove(a, out _);
            }
            var typeNameOf = new Dictionary<ulong, string>(fresh.Length);
            for (int i = 0; i < fresh.Length; i++) if (typeNames[i].Length > 0) typeNameOf[fresh[i]] = typeNames[i];

            // one batched read of every resolved unit record
            var have = units.Where(a => _recCache.ContainsKey(a.Addr)).ToArray();
            var addrs = have.Select(a => _recCache[a.Addr]).ToArray();
            var buf = new byte[addrs.Length * RecordReadLen]; var okv = new bool[addrs.Length];
            var map = new Dictionary<ulong, Vitals>(have.Length);
            // remaining minerals / gas of resource units (their resource behaviour)
            var resourceRecs = have.Where(a => a.Category == UnitCategory.Resource).Select(a => _recCache[a.Addr]).ToList();
            var amounts = resourceRecs.Count > 0 ? ReadResourceAmounts(resourceRecs) : [];
            // morph progress (Lair, Hive, Greater Spire, ...) from each unit's first order
            var progress = ReadMorphProgress(have.Where(a => a.Category != UnitCategory.Resource).Select(a => _recCache[a.Addr]).Distinct().ToArray());
            // units / research in production: the first queue item of each building (and egg: larva train through a queue too)
            var queues = ReadQueueProgress(have.Where(a => a.Category is UnitCategory.Building or UnitCategory.Unit).Select(a => _recCache[a.Addr]).Distinct().ToArray());
            // the longest weapon range of everything armed (cached per unit, upgrade bonuses read each pass)
            var armedKeys = have.Where(a => a.Category is UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building)
                                .Select(a => (_recCache[a.Addr], typeNameOf.GetValueOrDefault(a.Addr, ""))).Distinct().ToArray();
            var ranges = ReadWeaponRanges(armedKeys);
            PruneWeapons([.. armedKeys]);
            if (addrs.Length > 0 && ReadValues(addrs, RecordReadLen, buf, okv))
            {
                var cats = ResolveCatalogEntries(buf, okv);
                for (int i = 0; i < have.Length; i++)
                {
                    if (!okv[i]) continue;
                    var rec = buf.AsSpan(i * RecordReadLen, RecordReadLen);
                    float dmg = BinaryPrimitives.ReadInt32LittleEndian(rec[RecordDamageOff..]) / 4096f;
                    float sdmg = BinaryPrimitives.ReadInt32LittleEndian(rec[RecordShieldDamageOff..]) / 4096f;
                    float en  = BinaryPrimitives.ReadInt32LittleEndian(rec[RecordEnergyOff..]) / 4096f;
                    // sanity: a stale record pointer reads garbage - drop it and re-resolve next pass
                    if (dmg < 0 || dmg > 100000 || en < 0 || en > 100000 || sdmg < 0 || sdmg > 100000) { _recCache.Remove(have[i].Addr); _recLinks.Remove(have[i].Addr); continue; }
                    float maxHp = 0f, maxSh = 0f, maxEn = 0f, food = 0f, sight = 0f, eye = 0f;
                    int minerals = 0, gas = 0, costCat = 0;
                    if (cats[i] is { } cat)
                    {
                        maxHp = MaxVitalFx(rec, cat, 0) / 4096f;
                        maxSh = MaxVitalFx(rec, cat, 1) / 4096f;
                        maxEn = MaxVitalFx(rec, cat, 2) / 4096f;
                        food     = BinaryPrimitives.ReadInt32LittleEndian(cat.AsSpan(CatFoodOff)) / 4096f;
                        costCat  = BinaryPrimitives.ReadInt32LittleEndian(cat.AsSpan(CatCostCategoryOff));
                        minerals = BinaryPrimitives.ReadInt32LittleEndian(cat.AsSpan(CatCostOff));
                        gas      = BinaryPrimitives.ReadInt32LittleEndian(cat.AsSpan(CatCostOff + 4));
                        sight    = BinaryPrimitives.ReadInt32LittleEndian(cat.AsSpan(CatSightOff)) / 4096f;
                        eye      = Math.Max(BinaryPrimitives.ReadInt32LittleEndian(cat.AsSpan(CatHeightOff)),
                                            BinaryPrimitives.ReadInt32LittleEndian(cat.AsSpan(CatVisionHeightOff))) / 4096f;
                    }
                    map[have[i].Addr] = new Vitals(dmg, en, maxHp > dmg ? maxHp : 0f, sdmg, maxSh, maxEn,
                                                   rec[RecordOwnerOff], food, minerals, gas, costCat, sight, addrs[i],
                                                   typeNameOf.GetValueOrDefault(have[i].Addr, ""), eye,
                                                   amounts.TryGetValue(addrs[i], out int left) ? left : -1,
                                                   progress.TryGetValue(addrs[i], out var prog) ? prog.Progress : -1f,
                                                   queues.TryGetValue(addrs[i], out var q) ? q.Progress : -1f,
                                                   queues.TryGetValue(addrs[i], out var q2) ? q2.Count : 0,
                                                   queues.ContainsKey(addrs[i]),
                                                   queues.TryGetValue(addrs[i], out var q3) ? q3.Product : "",
                                                   progress.TryGetValue(addrs[i], out var p5) ? p5.Left : queues.TryGetValue(addrs[i], out var q4) && q4.Progress >= 0f ? q4.Left : -1f,
                                                   ranges.TryGetValue(addrs[i], out float range) ? range : -1f,
                                                   (rec[RecordDetectorOff] & RecordDetectorBit) != 0);
                }
            }
            _vitals = map;
        }

        // Unit type id ("Ghost", "SupplyDepotLowered", ...): *(*(unitObj + 0x5B8) + 8) is the id string, unitObj =
        // *(actor + 0x138). The string holder is interned per type, so names are cached by holder address.
        const int UnitObjNameHolderOff = 0x5B8, NameHolderCharsOff = 0x08;
        readonly Dictionary<ulong, string> _typeNameCache = [];
        readonly Dictionary<(ulong Addr, string Model), string> _stickyType = [];   // actor -> last resolved type name
        static readonly Regex TypeIdChars = new(@"^[A-Za-z][A-Za-z0-9_]{1,63}$", RegexOptions.Compiled);

        string[] ResolveTypeNames(ulong[] unitObjs)
        {
            var names = new string[unitObjs.Length];
            Array.Fill(names, "");
            var holders = new ulong[unitObjs.Length];
            var src = Enumerable.Range(0, unitObjs.Length).Where(i => IsUserPtr(unitObjs[i])).ToArray();
            if (src.Length == 0) return names;
            var hb = new byte[src.Length * 8]; var hok = new bool[src.Length];
            if (!ReadValues(src.Select(i => unitObjs[i] + UnitObjNameHolderOff).ToArray(), 8, hb, hok)) return names;
            for (int k = 0; k < src.Length; k++)
                if (hok[k]) holders[src[k]] = BinaryPrimitives.ReadUInt64LittleEndian(hb.AsSpan(k * 8, 8));

            // new holders: holder -> char* -> string (two batched reads)
            var fresh = holders.Where(h => IsUserPtr(h) && !_typeNameCache.ContainsKey(h)).Distinct().ToArray();
            if (fresh.Length > 0)
            {
                var pb = new byte[fresh.Length * 8]; var pok = new bool[fresh.Length];
                if (ReadValues(fresh.Select(h => h + NameHolderCharsOff).ToArray(), 8, pb, pok))
                {
                    var chars = fresh.Select((_, k) => pok[k] ? BinaryPrimitives.ReadUInt64LittleEndian(pb.AsSpan(k * 8, 8)) : 0).ToArray();
                    var sb = new byte[fresh.Length * 64]; var sok = new bool[fresh.Length];
                    if (ReadValues(chars.Select(c => IsUserPtr(c) || c > 0x10000 ? c : 0x10000).ToArray(), 64, sb, sok))
                        for (int k = 0; k < fresh.Length; k++)
                        {
                            if (!sok[k] || chars[k] <= 0x10000) continue;
                            int n = Array.IndexOf(sb, (byte)0, k * 64, 64) - k * 64;
                            string s = n > 0 ? Encoding.ASCII.GetString(sb, k * 64, n) : "";
                            if (TypeIdChars.IsMatch(s)) _typeNameCache[fresh[k]] = s;
                        }
                }
            }
            for (int i = 0; i < holders.Length; i++)
                if (_typeNameCache.TryGetValue(holders[i], out var s)) names[i] = s;
            return names;
        }

        /// <summary>Per record (packed RecordReadLen apart): its CUnit catalog entry bytes, or null. Two batched hops.</summary>
        byte[]?[] ResolveCatalogEntries(byte[] recs, bool[] ok)
        {
            int n = ok.Length;
            var lists = new ulong[n]; var idx = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (!ok[i]) continue;
                var rec = recs.AsSpan(i * RecordReadLen, RecordReadLen);
                ulong q = BinaryPrimitives.ReadUInt64LittleEndian(rec[((rec[0x2E] & 0x20) != 0 ? 0x10 : 0x08)..]);
                lists[i] = q << 5;
                idx[i] = (int)((q >> 59) & 31);
            }
            ulong[] Hop(ulong[] from)
            {
                var src = from.Where(IsUserPtr).Distinct().ToArray();
                var b = new byte[src.Length * 8]; var o = new bool[src.Length];
                var res = new Dictionary<ulong, ulong>(src.Length);
                if (src.Length > 0 && ReadValues(src, 8, b, o))
                    for (int k = 0; k < src.Length; k++)
                        if (o[k]) res[src[k]] = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(k * 8, 8));
                return from.Select(a => res.GetValueOrDefault(a)).ToArray();
            }
            var tables = Hop(lists.Select(l => IsUserPtr(l) ? l + 0x18 : 0).ToArray());
            var entries = Hop(tables.Select((t, i) => IsUserPtr(t) ? t + (ulong)idx[i] * 8 : 0).ToArray());

            var distinct = entries.Where(IsUserPtr).Distinct().ToArray();
            var cb = new byte[distinct.Length * CatReadLen]; var cok = new bool[distinct.Length];
            var catBytes = new Dictionary<ulong, byte[]>(distinct.Length);
            if (distinct.Length > 0 && ReadValues(distinct, CatReadLen, cb, cok))
                for (int k = 0; k < distinct.Length; k++)
                    if (cok[k]) catBytes[distinct[k]] = cb.AsSpan(k * CatReadLen, CatReadLen).ToArray();
            return entries.Select(e => catBytes.GetValueOrDefault(e)).ToArray();
        }

        /// <summary>The game's max-life (v = 0) / max-shields (v = 1) / max-energy (v = 2) getter, in fixed 4096
        /// (see the comment at RecordPath). Energy has no percent scaling.</summary>
        int MaxVitalFx(ReadOnlySpan<byte> rec, byte[] cat, int v)
        {
            static int I32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s[o..]);
            int pct = v < 2 && _lifePct is { } t && t[rec[0x54]] is > 0 and <= 10000 and var p ? p : 100;
            int baseFx = (int)((long)I32(cat, v switch { 0 => 0x54, 1 => 0xF8, _ => 0xC4 }) * pct / 100);
            bool flag = ((rec[0x34] >> 4) & 1) != 0;
            int mult = flag ? I32(cat, 0x1AC + 4 * v) : 0x1000;
            int scaled = (int)(((long)I32(rec, 0x1B0 + 4 * v) * mult) >> 12);
            int amount = (int)(((long)scaled * baseFx) >> 12);
            int total = I32(rec, 0x1A4 + 4 * v) + (flag ? I32(cat, 0x1A0 + 4 * v) : 0) + amount;
            int max = (int)(((long)total * I32(rec, 0x1BC + 4 * v)) >> 12);
            return Math.Max(v == 0 ? 0x1000 : 0, max);
        }

        // ── cloak ────────────────────────────────────────────────────────────
        // Actor flags dword: bit 0x80000 is set while the unit is cloaked (confirmed by toggling a banshee and
        // three ghosts; always set on dark templar / observer, never on high templar). The pointer at +0x728
        // appears with the first cloak but stays after uncloaking, so it is not used.
        public const int  CloakFlagsOff = 0x390;
        public const uint CloakBit      = 0x80000;
        static readonly byte[] BurrowName = "Burrow\0"u8.ToArray();
        HashSet<ulong> _burrowed = [];
        DateTime _nextBurrowRead = DateTime.MinValue;

        /// <summary>Three batched reads for every unit / worker actor; publishes the set that is burrowed.</summary>
        void ReadBurrowStates(ActorInfo[] actors)
        {
            var units = Array.FindAll(actors, a => a.Category is UnitCategory.Unit or UnitCategory.Worker);
            if (units.Length == 0) { _burrowed = []; return; }

            ulong[] Step(ulong[] from, int off)
            {
                var src = from.Select(p => p == 0 ? 0 : p + (ulong)off).ToArray();
                var res = new ulong[src.Length];
                var idx = Enumerable.Range(0, src.Length).Where(i => src[i] != 0).ToArray();
                if (idx.Length == 0) return res;
                var buf = new byte[idx.Length * 8]; var ok = new bool[idx.Length];
                if (!ReadValues(idx.Select(i => src[i]).ToArray(), 8, buf, ok)) return res;
                for (int k = 0; k < idx.Length; k++)
                    if (ok[k]) res[idx[k]] = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(k * 8, 8));
                return res;
            }
            static ulong[] Ptrs(ulong[] v) => v.Select(p => IsUserPtr(p) ? p : 0).ToArray();

            var p1   = Ptrs(Step(units.Select(a => a.Addr).ToArray(), StatePtrOff));
            var p2   = Ptrs(Step(p1, StateEntryOff));
            var name = Step(p2, StateNameOff);   // first 8 bytes of the state name
            ulong burrow = BinaryPrimitives.ReadUInt64LittleEndian(BurrowName.Concat(new byte[8]).Take(8).ToArray());
            const ulong Mask7 = 0x00FFFFFFFFFFFFFF;   // "Burrow\0" = 7 bytes
            var set = new HashSet<ulong>();
            for (int i = 0; i < units.Length; i++)
                if (p2[i] != 0 && (name[i] & Mask7) == (burrow & Mask7)) set.Add(units[i].Addr);
            _burrowed = set;
        }

        static bool IsUserPtr(ulong v) => v > 0x10000 && v < 0x7FFFFFFFFFFF && (v & 7) == 0;
        // behaviour instances can sit on a 4-byte boundary (an Observer's Detector11 at ...DF4); the game uses them as they are
        static bool IsObjPtr(ulong v) => v > 0x10000 && v < 0x7FFFFFFFFFFF && (v & 3) == 0;

        /// <summary>Classify table pointers (new ones only) and publish the unit/building actors.</summary>
        void ResolveTableSlots(List<ulong> slots, int gen = -1)
        {
            if (DateTime.UtcNow >= _slotCacheReset)
            {
                // periodic full re-resolve: catches an address reused for a different actor between refreshes
                _slotCache.Clear();
                _slotCacheReset = DateTime.UtcNow.AddSeconds(Math.Max(1, _cfg.ActorRescanSeconds));
            }
            lock (_staleSlots)
            {
                foreach (var s in _staleSlots) _slotCache.Remove(s);
                _staleSlots.Clear();
            }
            var live = slots.ToHashSet();
            foreach (var gone in _slotCache.Keys.Where(k => !live.Contains(k)).ToList()) _slotCache.Remove(gone);

            var fresh = slots.Where(s => !_slotCache.ContainsKey(s)).Distinct().ToArray();
            if (fresh.Length > 0)
            {
                // vtable first (cheap), then the full block only for actual actors
                var vbuf = new byte[fresh.Length * 8];
                var vok  = new bool[fresh.Length];
                if (!ReadValues(fresh, 8, vbuf, vok)) return;
                ulong vt = ActorVt;
                var cand = new List<ulong>();
                for (int i = 0; i < fresh.Length; i++)
                {
                    if (!vok[i]) continue;
                    if (BinaryPrimitives.ReadUInt64LittleEndian(vbuf.AsSpan(i * 8, 8)) == vt) cand.Add(fresh[i]);
                    else _slotCache[fresh[i]] = null;   // some other render object
                }
                int need = ActorInfoBytes;
                var abuf = new byte[cand.Count * need];
                var aok  = new bool[cand.Count];
                if (cand.Count > 0 && ReadValues(cand.ToArray(), need, abuf, aok))
                    for (int i = 0; i < cand.Count; i++)
                    {
                        if (!aok[i]) continue;
                        var info = ResolveActor(cand[i], abuf.AsSpan(i * need, need).ToArray(), out bool final);
                        if (final) _slotCache[cand[i]] = info;   // model not set yet: retry next refresh
                    }
            }

            var list = new List<ActorInfo>();
            foreach (var s in live)
                if (_slotCache.TryGetValue(s, out var info) && info != null) list.Add(info);
            if (gen >= 0 && gen != _actorGen) return;   // mission changed while resolving
            _actors     = [.. list];
            ActorsTotal = live.Count;
        }

        ActorInfo? ResolveActor(ulong a, byte[]? b) => ResolveActor(a, b, out _);

        /// <summary>Name/category/colour for one actor; null until its model is set.</summary>
        ActorInfo? ResolveActor(ulong a, byte[]? b, out bool final)
        {
            final = false;
            int need = ActorInfoBytes;
            if (b == null || b.Length < need) return null;
            ulong model = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(_cfg.ActorModelOffset, 8));
            var (path, name) = ResolveModel(model);
            if (path.Length == 0) return null;
            final = true;
            // a Protoss building warping in ("pylonwarpin") is that building's only actor until it finishes: keep it as a
            // building (UnitEntry.WarpingIn); unit warp-ins and the rest stay out
            var cat = NonUnitName.IsMatch(name) && !IsBuildingWarpIn(name, path)        ? UnitCategory.Other
                    : path.Contains(@"\resources\", StringComparison.OrdinalIgnoreCase) ? UnitCategory.Resource   // mineral fields, geysers
                    : path.Contains(@"\units\", StringComparison.OrdinalIgnoreCase)     ? (Workers.Contains(name) ? UnitCategory.Worker : UnitCategory.Unit)
                    : path.Contains(@"\buildings\", StringComparison.OrdinalIgnoreCase) ? UnitCategory.Building
                    : path.Contains(@"\doodads\", StringComparison.OrdinalIgnoreCase)   ? UnitCategory.Scenery
                    : path.Contains(@"\effects\", StringComparison.OrdinalIgnoreCase)   ? UnitCategory.Effect   // impacts, missiles, blood, flashes
                    : UnitCategory.Other;   // decals, skybox, portraits, ...
            float r = BitConverter.ToSingle(b, _cfg.ActorColorOffset);
            float g = BitConverter.ToSingle(b, _cfg.ActorColorOffset + 4);
            float bl = BitConverter.ToSingle(b, _cfg.ActorColorOffset + 8);
            return new ActorInfo(a, name, path, cat, ToAbgr(r, g, bl), model);
        }

        (string Path, string Name) ResolveModel(ulong model)
        {
            (ulong Str, string Path, string Name) hit;
            bool cached;
            lock (_modelCache)
            {
                cached = _modelCache.TryGetValue(model, out hit);
                if (cached && _modelChecked.Contains(model)) return (hit.Path, hit.Name);
            }

            var mb = model > 0x10000 ? ReadBytes(model + 0x40, 8) : null;
            ulong sp = mb is { Length: 8 } ? BinaryPrimitives.ReadUInt64LittleEndian(mb) : 0;
            if (cached && sp == hit.Str)
            {
                lock (_modelCache) _modelChecked.Add(model);
                return (hit.Path, hit.Name);
            }

            string path = "";
            var sb = sp > 0x10000 ? ReadBytes(sp, 160) : null;
            if (sb != null)
            {
                int n = Array.IndexOf(sb, (byte)0);
                if (n > 0) path = Encoding.ASCII.GetString(sb, 0, n);
            }
            string name = path.Length > 0 ? NameSuffix.Replace(Path.GetFileNameWithoutExtension(path.Replace('\\', '/')), "") : "";
            // only cache successful resolves; a failed read is retried on the next rescan
            if (path.Length > 0)
                lock (_modelCache) { _modelCache[model] = (sp, path, name); _modelChecked.Add(model); }
            else
                lock (_modelCache) _modelCache.Remove(model);
            return (path, name);
        }

        /// <summary>Team-colour float RGB -> ImGui ABGR. Pure white (no team colour) maps to neutral grey.</summary>
        static uint ToAbgr(float r, float g, float b)
        {
            if (!(float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b))) return 0xFFAAAAAA;
            if (r > 0.95f && g > 0.95f && b > 0.95f) return 0xFFAAAAAA;
            static uint C(float v) => (uint)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
            return 0xFF000000u | (C(b) << 16) | (C(g) << 8) | C(r);
        }
    }
}
