using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// What's inside a transport, by name (SC2Memory.Cargo gives records; the snapshot names them): "Marauder ×2, Reaper,
    /// Ghost". Used by the hover card, the count on the transport's marker and the drop warning. The name lookup is rebuilt
    /// once per snapshot.
    /// </summary>
    public static class CargoInfo
    {
        static UnitSnapshot? _snap;
        static readonly Dictionary<ulong, string> _names = [];

        static void Refresh(SC2Memory mem)
        {
            var snap = mem.Snapshot;
            if (ReferenceEquals(snap, _snap)) return;
            _snap = snap;
            _names.Clear();
            foreach (ref readonly var u in snap.Units.AsSpan())
                if (u.Record != 0) _names.TryAdd(u.Record, u.DisplayName);
        }

        /// <summary>How many units are inside this record (0 for none or not a transport).</summary>
        public static int Count(ulong record) =>
            record != 0 && Memory.Instance?.Cargo.TryGetValue(record, out var c) == true ? c.Length : 0;

        /// <summary>"Marauder ×2, Reaper, Ghost" - the most first - or "" when empty.</summary>
        public static string Describe(ulong record)
        {
            var mem = Memory.Instance;
            if (record == 0 || mem == null || !mem.Cargo.TryGetValue(record, out var inside) || inside.Length == 0) return "";
            Refresh(mem);
            return string.Join(", ", inside.Select(r => _names.GetValueOrDefault(r, "unit"))
                                           .GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                                           .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));
        }

        /// <summary>The unit is inside a transport, bunker or Nydus (the radar doesn't draw it on the map).</summary>
        public static bool IsCarried(ulong record) => record != 0 && Memory.Instance?.Carried.Contains(record) == true;
    }
}
