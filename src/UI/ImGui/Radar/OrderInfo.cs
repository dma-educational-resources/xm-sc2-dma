using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Units' orders on the radar (SC2Memory.Orders): a line from each unit through its queued targets - red for attacks,
    /// green for moves, amber for everything else (patrol, build, abilities) - with a small mark at the end; and the
    /// orders in words for the hover card ("Move → (126, 75), then Attack → Zergling"). Workers gathering are left out of
    /// the lines (they'd only draw the mining paths). Settings ▸ Display ▸ Order Lines: off, enemies, everyone.
    /// </summary>
    public static class OrderInfo
    {
        static UnitSnapshot? _snap;
        static readonly Dictionary<ulong, (Vector2 Pos, string Name)> _units = [];

        static void Refresh(SC2Memory mem)
        {
            var snap = mem.Snapshot;
            if (ReferenceEquals(snap, _snap)) return;
            _snap = snap;
            _units.Clear();
            foreach (ref readonly var u in snap.Units.AsSpan())
                if (u.Record != 0) _units.TryAdd(u.Record, (new Vector2(u.X, u.Y), u.DisplayName));
        }

        /// <summary>"move" -> "Move", "DroneHarvest" -> "Gather", "ZergBuild" -> "Build", else spaced out.</summary>
        public static string AbilityName(string ability) => ability switch
        {
            _ when ability.EndsWith("Harvest", StringComparison.OrdinalIgnoreCase) => "Gather",
            _ when ability.EndsWith("Build", StringComparison.OrdinalIgnoreCase) => "Build",
            _ => ability.Length > 0 ? PrettyNames.Get(ability) : "?",
        };

        static bool IsAttack(string a) => a.Equals("attack", StringComparison.OrdinalIgnoreCase) || a.StartsWith("Attack", StringComparison.OrdinalIgnoreCase);
        static bool IsMove(string a) => a.Equals("move", StringComparison.OrdinalIgnoreCase);
        static bool IsGather(string a) => a.EndsWith("Harvest", StringComparison.OrdinalIgnoreCase);

        /// <summary>"Move → (126, 75), then Attack → Zergling", or "" when the unit has no orders.</summary>
        public static string Describe(ulong record)
        {
            var mem = Memory.Instance;
            if (record == 0 || mem == null || !mem.Orders.TryGetValue(record, out var orders)) return "";
            Refresh(mem);
            return string.Join(", then ", orders.Select(o =>
                AbilityName(o.Ability) + (o.Point is { } p ? $" → ({p.X:0}, {p.Y:0})"
                                         : o.TargetRecord != 0 && _units.TryGetValue(o.TargetRecord, out var t) ? $" → {t.Name}" : "")));
        }

        /// <summary>The lines: each unit's position, then its targets in queue order (only units drawn on the radar).</summary>
        public static void DrawLines(in RadarFrameCtx ctx, UnitEntry[] units)
        {
            var mem = Memory.Instance;
            int mode = Program.Config.OrderLines;
            if (mode == 0 || mem == null || mem.Orders.Count == 0) return;
            Refresh(mem);
            int local = mem.LocalPlayer;
            bool enemiesOnly = mode == 1 && local > 0 && mem.HasAllianceTable;
            var dl = ctx.DrawList;
            var done = new HashSet<ulong>();
            foreach (ref readonly var u in units.AsSpan())
            {
                if (u.Record == 0 || !done.Add(u.Record) || !mem.Orders.TryGetValue(u.Record, out var orders)) continue;
                if (enemiesOnly && mem.IsAlly(u.Owner, local)) continue;
                if (!UnitFilter.IsVisible(in u) || CargoInfo.IsCarried(u.Record)) continue;
                var from = ctx.WorldToScreen(u.X, u.Y);
                foreach (var o in orders)
                {
                    if (IsGather(o.Ability)) break;
                    Vector2? to = o.Point is { } p ? ctx.WorldToScreen(p.X, p.Y)
                                : o.TargetRecord != 0 && _units.TryGetValue(o.TargetRecord, out var t) ? ctx.WorldToScreen(t.Pos.X, t.Pos.Y) : null;
                    if (to is not { } end) continue;
                    uint col = IsAttack(o.Ability) ? 0xB04040FFu : IsMove(o.Ability) ? 0xA060E060u : 0xA040C0FFu;   // ABGR: red, green, amber
                    dl.AddLine(from, end, col, 1.3f);
                    if (IsAttack(o.Ability))
                    {
                        // a small X on the target
                        dl.AddLine(end + new Vector2(-3.5f, -3.5f), end + new Vector2(3.5f, 3.5f), col | 0xFF000000, 1.6f);
                        dl.AddLine(end + new Vector2(-3.5f, 3.5f), end + new Vector2(3.5f, -3.5f), col | 0xFF000000, 1.6f);
                    }
                    else dl.AddCircle(end, 3f, col | 0xFF000000, 12, 1.4f);
                    from = end;
                }
            }
        }
    }
}
