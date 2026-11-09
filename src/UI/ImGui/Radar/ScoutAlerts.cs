using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Toasts for scouting information: an enemy fielding a unit or structure type for the first time this match
    /// ("Enemy tech: Spire"), and cloaked / burrowed enemy units appearing. Whatever is on the map when the radar first
    /// sees a match (or when the option is turned on) is taken as known, so only changes are announced.
    /// Needs the local player and the alliance table to tell enemies apart.
    /// Also: your own supply block (used at the cap, below 200), once each time it happens.
    /// </summary>
    public static class ScoutAlerts
    {
        static Config Config => Program.Config;

        static UnitSnapshot? _last;
        static readonly HashSet<(byte Owner, string Type)> _seenTypes = [];
        static readonly HashSet<ulong> _seenHidden = [];
        static readonly Dictionary<string, DateTime> _hiddenToastAt = [];
        static bool _seeded;
        static float _lastClock = -1f;
        static bool _wasOn;
        static bool _blocked;

        public static void Update()
        {
            var mem = Memory.Instance;
            SupplyBlock(mem);
            bool on = Config.ScoutAlerts || Config.ScoutAlertsHidden;
            if (mem == null || !on) { _wasOn = false; return; }
            var snap = mem.Snapshot;
            if (ReferenceEquals(snap, _last)) return;
            _last = snap;

            // a new match (the clock went back) or the option just turned on: start over silently
            float clock = mem.GameTime;
            if (!_wasOn || clock >= 0f && _lastClock >= 0f && clock < _lastClock - 5f) Reset();
            _wasOn = true;
            if (clock >= 0f) _lastClock = clock;

            int local = mem.LocalPlayer;
            if (local < 0 || !mem.HasAllianceTable || snap.Units.Length == 0) return;

            var newTypes = new List<string>();
            var newHidden = new List<string>();
            foreach (ref readonly var u in snap.Units.AsSpan())
            {
                if (u.Record == 0 || u.Owner is 0 or >= 16 || u.Owner == local || mem.IsAlly(u.Owner, local)) continue;
                if (u.Category is not (UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building)) continue;
                string type = BaseType(in u);
                if (_seenTypes.Add((u.Owner, type)) && _seeded) newTypes.Add(type);
                // a tech morph starting (Lair, Hive, Greater Spire): announced now, not when it finishes
                if (u.Progress >= 0f && u.IsBuilding && ProductionWidget.MorphTarget(in u) is { } to && _seenTypes.Add((u.Owner, to)) && _seeded)
                    newTypes.Add($"{to} (started)");
                if ((u.Cloaked || u.Burrowed) && _seenHidden.Add(u.Addr) && _seeded)
                    newHidden.Add($"{(u.Burrowed ? "Burrowed" : "Cloaked")} {type}");
            }

            if (_seeded)
            {
                if (Config.ScoutAlerts && newTypes.Count > 0)
                {
                    var distinct = newTypes.Distinct().ToList();
                    string list = string.Join(", ", distinct.Take(4)) + (distinct.Count > 4 ? $" +{distinct.Count - 4}" : "");
                    ToastManager.Show("Enemy tech", list, ToastStyle.Warning, 6f);
                }
                if (Config.ScoutAlertsHidden)
                    foreach (var h in newHidden.Distinct())
                    {
                        // one toast per kind every 20 s, so a cloaked army doesn't flood the corner
                        if (_hiddenToastAt.TryGetValue(h, out var at) && (DateTime.UtcNow - at).TotalSeconds < 20) continue;
                        _hiddenToastAt[h] = DateTime.UtcNow;
                        ToastManager.Show("Hidden enemy", h, ToastStyle.Danger, 6f);
                    }
            }
            _seeded = true;
        }

        /// <summary>A toast when your supply runs into the cap; again only after it has been lifted.</summary>
        static void SupplyBlock(SC2Memory? mem)
        {
            if (!Config.SupplyBlockAlert || mem is not { LocalPlayer: > 0 } || mem.GetPlayerStats(mem.LocalPlayer) is not { } ps
                || ps.SupplyUsed < 0f || ps.SupplyCap <= 0f) { _blocked = false; return; }
            bool blocked = ps.SupplyCap < 200f && ps.SupplyUsed >= ps.SupplyCap;
            if (blocked && !_blocked)
                ToastManager.Show("Supply blocked", $"{ps.SupplyUsed:0}/{ps.SupplyCap:0} - build supply", ToastStyle.Warning, 5f);
            _blocked = blocked;
        }

        // states of one unit type that aren't new tech: SupplyDepotLowered, SiegeTankSieged, VikingAssault, ...
        static readonly System.Text.RegularExpressions.Regex StateSuffix =
            new("(Lowered|Burrowed|Uprooted|Flying|Sieged|Phasing|Fighter|Assault|Cocoon|Egg)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        static string BaseType(in UnitEntry u) =>
            u.TypeName.Length > 0 ? new UnitEntry(0, 0, 0, u.Name, u.Category, "", 0, TypeName: StateSuffix.Replace(u.TypeName, "")).DisplayName : u.Name;

        static void Reset()
        {
            _seenTypes.Clear();
            _seenHidden.Clear();
            _hiddenToastAt.Clear();
            _seeded = false;
            _lastClock = -1f;
        }
    }
}
