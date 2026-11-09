using System.Numerics;
using SC2Radar.DMA;

namespace SC2Radar.SC2
{
    /// <summary>
    /// What the observer panel would show, worked out from the radar's own snapshots (the game's score values aren't
    /// read): per player a sample every few game seconds - army value, workers, supply, bank - an income estimate, the
    /// value of units lost, and a timeline of the unit / structure types each player fields for the first time.
    /// Updated on the UI thread once per new snapshot (RadarWindow.Draw); starts over when a new match begins.
    ///
    /// Losses: a unit gone for <see cref="GoneConfirmSec"/> game seconds and not back. Not counted: one that vanished next
    /// to its owner's container (gas buildings, Bunker, Command Center, transports, Nydus) - it may be inside - and a Zerg
    /// worker that became a building. Income: change in bank + change in value on the field + value lost, per minute
    /// over the last minute. Research and units still in production aren't on the field, so it reads low while teching.
    /// </summary>
    public static class MatchStats
    {
        public const float SampleEverySec = 5f, GoneConfirmSec = 5f, IncomeWindowSec = 60f;

        public readonly record struct Sample(float Time, int ArmyValue, int Workers, int FieldMinerals, int FieldGas,
                                             int BankMinerals, int BankGas, float SupplyUsed, float SupplyCap, int LostMinerals, int LostGas);

        public readonly record struct TechEvent(float Time, byte Owner, string Type, bool Started);

        public sealed class PlayerTrack
        {
            public readonly List<Sample> Samples = [];
            public int LostMinerals, LostGas, LostArmy, LostWorkers;
            public Sample? Last => Samples.Count > 0 ? Samples[^1] : null;

            /// <summary>Minerals and gas gathered per minute over the last minute (estimate); null until there is a minute
            /// of samples with the bank known.</summary>
            public (int Minerals, int Gas)? IncomePerMin
            {
                get
                {
                    if (Samples.Count < 2 || Samples[^1].BankMinerals < 0) return null;
                    var now = Samples[^1];
                    int i = Samples.Count - 1;
                    while (i > 0 && now.Time - Samples[i - 1].Time <= IncomeWindowSec) i--;
                    var then = Samples[i];
                    float dt = now.Time - then.Time;
                    if (dt < 20f || then.BankMinerals < 0) return null;
                    float m = (now.BankMinerals - then.BankMinerals) + (now.FieldMinerals - then.FieldMinerals) + (now.LostMinerals - then.LostMinerals);
                    float g = (now.BankGas - then.BankGas) + (now.FieldGas - then.FieldGas) + (now.LostGas - then.LostGas);
                    return ((int)MathF.Max(0f, m * 60f / dt), (int)MathF.Max(0f, g * 60f / dt));
                }
            }
        }

        sealed class Tracked
        {
            public byte Owner; public int Minerals, Gas; public string Type = "", Name = "";
            public UnitCategory Category; public Vector2 Pos; public float FirstSeen, GoneAt = -1f; public bool Stored;
        }

        static readonly PlayerTrack[] _players = new PlayerTrack[16];
        static readonly Dictionary<ulong, Tracked> _units = [];
        static readonly HashSet<(byte, string)> _seenTypes = [];
        static readonly List<TechEvent> _timeline = [];
        static UnitSnapshot? _last;
        static float _lastClock = -1f, _nextSample;
        static bool _seeded;

        public static PlayerTrack? Player(int p) => p is >= 0 and < 16 ? _players[p] : null;
        public static IReadOnlyList<TechEvent> Timeline => _timeline;

        /// <summary>Players with anything tracked, in id order.</summary>
        public static IEnumerable<int> Players => Enumerable.Range(1, 15).Where(p => _players[p] is { Samples.Count: > 0 });

        static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
        {
            "Refinery", "RefineryRich", "Assimilator", "AssimilatorRich", "Extractor", "ExtractorRich", "Bunker",
            "CommandCenter", "Medivac", "WarpPrism", "WarpPrismPhasing", "OverlordTransport", "NydusNetwork", "NydusCanal",
        };
        // never a loss: hatchery spawn and morph stages, and free units
        static readonly HashSet<string> NotLosses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Larva", "Egg", "BanelingCocoon", "RavagerCocoon", "LurkerMPEgg", "BroodLordCocoon", "OverlordCocoon",
            "TransportOverlordCocoon", "Broodling", "BroodlingEscort", "Locust", "LocustMP", "LocustMPFlying", "Interceptor",
            "AutoTurret", "MULE", "CreepTumor", "CreepTumorBurrowed", "CreepTumorQueen", "AdeptPhaseShift", "Changeling",
        };

        // ── match history (History widget) ───────────────────────────────────

        const double EndAfterSec = 15;   // no live clock for this long: the match is over
        public static string HistoryFolder => System.IO.Path.Combine(Program.DataDir, "match_history");

        static readonly string[] _names = new string[16], _relations = new string[16];
        static string _map = "";
        static DateTime _startedUtc = DateTime.UtcNow, _lastLive = DateTime.MinValue;
        static bool _saved = true;   // nothing new since the last summary (or nothing at all)

        public sealed record PlayerSummary(int Id, string Name, string Relation, int PeakArmy, int FinalArmy, int FinalWorkers,
                                           float FinalSupply, int LostMinerals, int LostGas, int LostArmy, int LostWorkers,
                                           int IncomeMinerals, int IncomeGas);
        public sealed record MatchSummary(string Map, DateTime StartedUtc, DateTime EndedUtc, float GameSeconds,
                                          List<PlayerSummary> Players, List<TechEvent> Timeline);

        /// <summary>
        /// Writes this match's summary to match_history\yyyyMMdd-HHmmss.json (local start time): per player the peak and
        /// final army value, workers and supply, what they lost, and their average income over the match (estimates, as
        /// the Observer widget's); plus the tech timeline. Matches shorter than 30 game seconds aren't kept.
        /// </summary>
        static void SaveHistory()
        {
            _saved = true;
            try
            {
                var players = new List<PlayerSummary>();
                float end = 0f;
                for (int o = 1; o < 16; o++)
                {
                    if (_players[o] is not { Samples.Count: > 0 } t) continue;
                    var first = t.Samples[0]; var last = t.Samples[^1];
                    end = MathF.Max(end, last.Time);
                    float min = MathF.Max(1f, (last.Time - first.Time) / 60f);
                    bool bank = first.BankMinerals >= 0 && last.BankMinerals >= 0;
                    int Gathered(int b0, int b1, int f0, int f1, int l0, int l1) => bank ? (int)MathF.Max(0f, ((b1 - b0) + (f1 - f0) + (l1 - l0)) / min) : -1;
                    players.Add(new PlayerSummary(o, _names[o] ?? $"Player {o}", _relations[o] ?? "",
                        t.Samples.Max(s => s.ArmyValue), last.ArmyValue, last.Workers, last.SupplyUsed,
                        t.LostMinerals, t.LostGas, t.LostArmy, t.LostWorkers,
                        Gathered(first.BankMinerals, last.BankMinerals, first.FieldMinerals, last.FieldMinerals, first.LostMinerals, last.LostMinerals),
                        Gathered(first.BankGas, last.BankGas, first.FieldGas, last.FieldGas, first.LostGas, last.LostGas)));
                }
                if (players.Count == 0 || end < 30f) return;
                var summary = new MatchSummary(_map, _startedUtc, DateTime.UtcNow, end, players, [.. _timeline.Where(e => e.Time > 0f)]);
                System.IO.Directory.CreateDirectory(HistoryFolder);
                string file = System.IO.Path.Combine(HistoryFolder, $"{_startedUtc.ToLocalTime():yyyyMMdd-HHmmss}.json");
                System.IO.File.WriteAllText(file, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                XMLogging.WriteLine($"[History] saved {System.IO.Path.GetFileName(file)}: {_map}, {SC2Memory.FormatClock(end)}");
            }
            catch (Exception ex) { XMLogging.WriteLine($"[History] save failed: {ex.Message}"); }
        }

        static void Reset()
        {
            if (!_saved) SaveHistory();
            Array.Clear(_players);
            Array.Clear(_names); Array.Clear(_relations);
            _saved = true; _map = ""; _startedUtc = DateTime.UtcNow;
            _units.Clear(); _seenTypes.Clear(); _timeline.Clear();
            _nextSample = 0f; _seeded = false;
        }

        public static void Update()
        {
            var mem = Memory.Instance;
            // the match ended (left, closed, or the clock stopped coming): put it in the history once
            if (!_saved && (DateTime.UtcNow - _lastLive).TotalSeconds > EndAfterSec) SaveHistory();
            if (mem == null || !Memory.InGame) return;
            var snap = mem.Snapshot;
            if (ReferenceEquals(snap, _last)) return;
            _last = snap;

            float clock = mem.GameTime;
            if (clock < 0f || snap.Units.Length == 0) return;
            if (_lastClock >= 0f && clock < _lastClock - 5f) Reset();   // a new match
            if (_map.Length > 0 && mem.MapName.Length > 0 && mem.MapName != _map) Reset();   // another map: another match
            if (_map.Length == 0) { _map = mem.MapName; _startedUtc = DateTime.UtcNow; }
            _lastLive = DateTime.UtcNow;
            _saved = false;
            _lastClock = clock;

            // present units (one record per unit), first-seen types
            var present = new HashSet<ulong>();
            var field = new (int M, int G, int Army, int Workers)[16];
            foreach (ref readonly var u in snap.Units.AsSpan())
            {
                if (u.Record == 0 || u.Owner is 0 or >= 16) continue;
                if (u.Category is not (UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building)) continue;
                if (!present.Add(u.Record)) continue;
                ref var f = ref field[u.Owner];
                f.M += u.Minerals; f.G += u.Gas;
                if (u.Category == UnitCategory.Worker) f.Workers++;
                else if (u.Category == UnitCategory.Unit && u.CostCategory == 1) f.Army += u.Minerals + u.Gas;

                string type = u.TypeName.Length > 0 ? u.TypeName : u.Name;
                if (!_units.TryGetValue(u.Record, out var t))
                    _units[u.Record] = t = new Tracked { FirstSeen = clock };
                (t.Owner, t.Minerals, t.Gas, t.Type, t.Name, t.Category, t.Pos, t.GoneAt, t.Stored) =
                    (u.Owner, u.Minerals, u.Gas, type, u.DisplayName, u.Category, new Vector2(u.X, u.Y), -1f, false);

                if (!NotLosses.Contains(type) && _seenTypes.Add((u.Owner, u.DisplayName)))
                    _timeline.Add(new TechEvent(_seeded ? clock : 0f, u.Owner, u.DisplayName, false));
                if (u.Progress >= 0f && u.IsBuilding && UI.ImGui.Radar.ProductionWidget.MorphTarget(in u) is { } to && _seenTypes.Add((u.Owner, to)))
                    _timeline.Add(new TechEvent(_seeded ? clock : 0f, u.Owner, to, true));
            }
            _seeded = true;

            // gone: start the timer (noting a container next to it), confirm after a few seconds
            List<ulong>? done = null;
            foreach (var (rec, t) in _units)
            {
                if (present.Contains(rec)) continue;
                if (t.GoneAt < 0f)
                {
                    t.GoneAt = clock;
                    t.Stored = _units.Values.Any(o => o.GoneAt < 0f && o.Owner == t.Owner && Containers.Contains(o.Type) &&
                                                      Vector2.DistanceSquared(o.Pos, t.Pos) < 3.5f * 3.5f);
                    continue;
                }
                if (clock - t.GoneAt < GoneConfirmSec) continue;
                (done ??= []).Add(rec);
                if (t.Stored || NotLosses.Contains(t.Type) || t.Minerals + t.Gas == 0) continue;
                // a Zerg worker that turned into a building: a new building of the same player where it stood
                if (t.Category == UnitCategory.Worker && _units.Values.Any(o => o.Owner == t.Owner && o.Category == UnitCategory.Building &&
                        MathF.Abs(o.FirstSeen - t.GoneAt) < 2f && Vector2.DistanceSquared(o.Pos, t.Pos) < 2.5f * 2.5f)) continue;
                var p = _players[t.Owner] ??= new PlayerTrack();
                p.LostMinerals += t.Minerals; p.LostGas += t.Gas;
                if (t.Category == UnitCategory.Worker) p.LostWorkers++;
                else if (t.Category == UnitCategory.Unit) p.LostArmy++;
            }
            if (done != null) foreach (var rec in done) _units.Remove(rec);

            // a sample every few game seconds
            if (clock < _nextSample) return;
            _nextSample = clock + SampleEverySec;
            for (int o = 1; o < 16; o++)
            {
                var f = field[o];
                if (f.M + f.G == 0 && _players[o] == null) continue;
                var p = _players[o] ??= new PlayerTrack();
                var ps = mem.GetPlayerStats(o);
                p.Samples.Add(new Sample(clock, f.Army, f.Workers, f.M, f.G, ps?.Minerals ?? -1, ps?.Gas ?? -1,
                                         ps?.SupplyUsed ?? -1f, ps?.SupplyCap ?? -1f, p.LostMinerals, p.LostGas));
                if (p.Samples.Count > 2000) p.Samples.RemoveRange(0, 500);   // long arcade games: keep the last few hours
                // names and sides now, while the game can tell: the history is written after it ended
                string n = mem.PlayerDisplayName(o);
                if (n.Length > 0) _names[o] = n;
                int local = mem.LocalPlayer;
                _relations[o] = o == local ? "you" : local > 0 && mem.HasAllianceTable ? (mem.IsAlly(o, local) ? "ally" : "enemy") : "";
            }
        }
    }
}
