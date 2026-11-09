using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Drop / flank warning (roadmap D5): a toast, and a red pulse around the base on the radar, when an enemy transport
    /// (Medivac, Warp Prism, transport Overlord) or a Nydus worm comes within <see cref="BaseRadius"/> of one of your
    /// townhalls, or at least <see cref="AttackMin"/> enemy army units do. Once per base every <see cref="CooldownSec"/>
    /// seconds. The toast also says what the transports carry (SC2Memory.Cargo).
    /// </summary>
    public static class BaseAlerts
    {
        const float BaseRadius = 16f, CooldownSec = 30f, PulseSec = 8f;
        const int AttackMin = 4;

        static readonly HashSet<string> Townhalls = new(StringComparer.OrdinalIgnoreCase)
            { "CommandCenter", "OrbitalCommand", "PlanetaryFortress", "Nexus", "Hatchery", "Lair", "Hive" };
        static readonly HashSet<string> Transports = new(StringComparer.OrdinalIgnoreCase)
            { "Medivac", "WarpPrism", "WarpPrismPhasing", "OverlordTransport", "NydusCanal", "NydusCanalAttacker" };

        static UnitSnapshot? _last;
        static readonly Dictionary<ulong, DateTime> _alertAt = [];   // townhall record -> last warning
        static readonly List<(Vector2 Pos, DateTime At)> _pulses = [];
        static Config Config => Program.Config;

        public static void Update()
        {
            var mem = Memory.Instance;
            if (!Config.DropWarning || mem == null || !Memory.InGame) return;
            var snap = mem.Snapshot;
            if (ReferenceEquals(snap, _last)) return;
            _last = snap;
            int local = mem.LocalPlayer;
            if (local <= 0 || !mem.HasAllianceTable) return;

            var bases = new List<(ulong Rec, Vector2 Pos)>();
            var enemies = new List<UnitEntry>();
            var seen = new HashSet<ulong>();
            foreach (ref readonly var u in snap.Units.AsSpan())
            {
                if (u.Record == 0 || !seen.Add(u.Record) || u.Owner is 0 or >= 16) continue;
                if (u.Owner == local && Townhalls.Contains(u.TypeName)) bases.Add((u.Record, new Vector2(u.X, u.Y)));
                else if (!mem.IsAlly(u.Owner, local) && (u.Category == UnitCategory.Unit || Transports.Contains(u.TypeName))) enemies.Add(u);
            }

            var now = DateTime.UtcNow;
            bool sound = false;
            foreach (var (rec, pos) in bases)
            {
                if (_alertAt.TryGetValue(rec, out var at) && (now - at).TotalSeconds < CooldownSec) continue;
                var near = enemies.Where(e => Vector2.DistanceSquared(new Vector2(e.X, e.Y), pos) < BaseRadius * BaseRadius).ToList();
                var drops = near.Where(e => Transports.Contains(e.TypeName)).ToList();
                int army = near.Count(e => e.Category == UnitCategory.Unit && e.CostCategory == 1);
                if (drops.Count == 0 && army < AttackMin) continue;

                _alertAt[rec] = now;
                _pulses.Add((pos, now));
                var what = (drops.Count > 0 ? drops : near).GroupBy(e => e.DisplayName).OrderByDescending(g => g.Count())
                                                          .Take(3).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
                // what the transports carry, when the cargo is read (SC2Memory.Cargo)
                string cargo = string.Join(", ", drops.Select(d => CargoInfo.Describe(d.Record)).Where(c => c.Length > 0));
                ToastManager.Show(drops.Count > 0 ? "Drop at your base" : "Attack at your base",
                                  string.Join(", ", what) + (cargo.Length > 0 ? $" carrying {cargo}" : ""), ToastStyle.Danger, 6f);
                sound |= Config.DropWarningSound;
            }
            if (sound)
                try { System.Media.SystemSounds.Hand.Play(); } catch { }
        }

        /// <summary>The red pulse around each warned base for a few seconds.</summary>
        public static void Draw(in RadarFrameCtx ctx)
        {
            if (_pulses.Count == 0) return;
            var now = DateTime.UtcNow;
            _pulses.RemoveAll(p => (now - p.At).TotalSeconds > PulseSec);
            foreach (var (pos, at) in _pulses)
            {
                float t = (float)(now - at).TotalSeconds;
                float wave = t % 1f;   // one ring per second, growing out and fading
                var c = ctx.WorldToScreen(pos.X, pos.Y);
                float r = BaseRadius * ctx.Scale;
                uint a = (uint)(0xC0 * (1f - wave) * (1f - t / PulseSec));
                ctx.DrawList.AddCircle(c, r * (0.6f + 0.4f * wave), (a << 24) | 0x003030FF, 64, 2.5f);
                ctx.DrawList.AddCircle(c, r, ((uint)(0x70 * (1f - t / PulseSec)) << 24) | 0x003030FF, 64, 1.5f);
            }
        }
    }
}
