using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Marker rule alerts (Visuals ▸ Markers ▸ a marker's Alert): a toast - and with "Toast + sound" the system sound -
    /// when a unit starts matching a marker rule: it appears, or its health drops under the rule's limit. What already
    /// matches when a match (or the profile) starts is taken as known. One toast per rule every few seconds, listing
    /// how many started matching.
    /// </summary>
    public static class MarkerAlerts
    {
        const double PerRuleSec = 4;

        static UnitSnapshot? _last;
        static HashSet<(ulong, MarkerRule)> _matching = [], _next = [];
        static readonly Dictionary<MarkerRule, DateTime> _toastAt = [];
        static string? _profile;
        static float _lastClock = -1f;
        static bool _seeded;

        public static void Update()
        {
            var mem = Memory.Instance;
            if (mem == null || !Memory.InGame) return;
            var snap = mem.Snapshot;
            if (ReferenceEquals(snap, _last)) return;
            _last = snap;

            // a new match or another profile: start over silently
            var profile = RadarProfiles.Active;
            float clock = mem.GameTime;
            if (_profile != profile.Name || clock >= 0f && _lastClock >= 0f && clock < _lastClock - 5f)
            {
                _profile = profile.Name;
                _matching.Clear(); _toastAt.Clear(); _seeded = false;
            }
            if (clock >= 0f) _lastClock = clock;
            if (!profile.Markers.Any(m => m.Enabled && m.Alert > 0)) { _matching.Clear(); return; }

            _next.Clear();
            Dictionary<MarkerRule, int>? started = null;
            foreach (ref readonly var u in snap.Units.AsSpan())
            {
                if (RadarProfiles.MarkerFor(in u) is not { Alert: > 0 } rule) continue;
                var key = (u.Record != 0 ? u.Record : u.Addr, rule);
                if (!_next.Add(key)) continue;   // several actors of one unit
                if (_seeded && !_matching.Contains(key))
                    (started ??= [])[rule] = started.GetValueOrDefault(rule) + 1;
            }
            (_matching, _next) = (_next, _matching);
            _seeded = true;

            if (started == null) return;
            bool sound = false;
            foreach (var (rule, n) in started)
            {
                if (_toastAt.TryGetValue(rule, out var at) && (DateTime.UtcNow - at).TotalSeconds < PerRuleSec) continue;
                _toastAt[rule] = DateTime.UtcNow;
                string name = rule.Label.Length > 0 ? rule.Label : rule.Pattern.TrimStart('=');
                string what = rule.HealthBelow > 0 ? $"{name} under {rule.HealthBelow}% health" : name;
                ToastManager.Show("Marker", n > 1 ? $"{what} ×{n}" : what, ToastStyle.Warning, 5f);
                sound |= rule.Alert >= 2;
            }
            if (sound)
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
    }
}
