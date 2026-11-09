using System.Numerics;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// The observer panel's tabs, from the radar's own tracking (SC2.MatchStats): income per minute, the army value over
    /// the match as a graph, units lost, supply and bank, and upgrade levels - one row (or line) per player in their colour.
    /// Income and losses are estimates (see MatchStats); upgrades are the game's own counts.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class ObserverWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Observer";
        protected override Vector2 InitSize    => new(380, 220);
        protected override bool    HasOpacityOverride => Program.Config.ObserverWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.ObserverWidgetAlpha;
        protected override bool    AutoSize     => true;

        static readonly string[] Tabs = ["Income", "Army", "Lost", "Supply", "Upgrades"];
        const float TabW = 360f, GraphH = 130f;
        const uint MineralCol = 0xFFFFC870, GasCol = 0xFF70D870;   // ABGR, as the Army widget
        float _right;

        void Track() => _right = MathF.Max(_right, ImGuiNET.ImGui.GetItemRectMax().X - ImGuiNET.ImGui.GetWindowPos().X);

        /// <summary>A player's name ("Player 3" without one) and colour, shared with the Timeline and Unit Counts widgets.</summary>
        internal static (string Name, uint Color) PlayerLabel(int p)
        {
            var mem = Memory.Instance;
            string n = mem?.PlayerDisplayName(p) ?? "";
            if (n.Length == 0) n = $"Player {p}";
            int local = mem?.LocalPlayer ?? -1;
            if (p == local) n += " (you)";
            return (n, VisionOverlay.PlayerColor(p));
        }

        /// <summary>Players in the order the widgets list them: you, your allies, then the rest.</summary>
        internal static List<int> OrderedPlayers(IEnumerable<int> players)
        {
            var mem = Memory.Instance;
            int local = mem?.LocalPlayer ?? -1;
            int Rank(int p) => p == local ? 0 : local > 0 && mem != null && mem.HasAllianceTable && mem.IsAlly(p, local) ? 1 : 2;
            return [.. players.OrderBy(Rank).ThenBy(p => p)];
        }

        protected override void DrawWidgetContent()
        {
            _right = 0f;
            int tab = Program.Config.ObserverTab;
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 2f);
            ImGuiNET.ImGui.Indent(Pad);
            try
            {
                if (UI.TabRow(Tabs, ref tab, TabW)) { Program.Config.ObserverTab = tab; Program.Config.SaveDebounced(); }
                _right = Pad + TabW;
                ImGuiNET.ImGui.Dummy(new Vector2(1f, 2f));
                var players = OrderedPlayers(MatchStats.Players);
                if (!Memory.InGame || players.Count == 0)
                {
                    ImGuiNET.ImGui.TextDisabled("No match tracked yet.");
                    Track();
                }
                else switch (Math.Clamp(tab, 0, Tabs.Length - 1))
                {
                    case 0: DrawIncome(players); break;
                    case 1: DrawArmyGraph(players); break;
                    case 2: DrawLost(players); break;
                    case 3: DrawSupply(players); break;
                    default: DrawUpgrades(players); break;
                }
            }
            finally { ImGuiNET.ImGui.Unindent(Pad); }
            ReportContentSize(new Vector2(_right + Pad, ImGuiNET.ImGui.GetCursorPosY()));
        }

        // a table row: the player's name, then values at fixed columns
        void Row(int p, params (string Text, uint? Color)[] cells)
        {
            var (name, col) = PlayerLabel(p);
            ImGuiNET.ImGui.TextColored(UI.ToV4(col), name);
            Track();
            float x = Pad + 150f;
            foreach (var (text, c) in cells)
            {
                ImGuiNET.ImGui.SameLine(x);
                if (c is uint cc) ImGuiNET.ImGui.TextColored(UI.ToV4(cc), text);
                else ImGuiNET.ImGui.TextUnformatted(text);
                Track();
                x += 70f;
            }
        }

        void Header(params string[] cols)
        {
            float x = Pad + 150f;
            ImGuiNET.ImGui.TextDisabled("Player");
            foreach (var c in cols) { ImGuiNET.ImGui.SameLine(x); ImGuiNET.ImGui.TextDisabled(c); x += 70f; }
            Track();
        }

        void DrawIncome(List<int> players)
        {
            Header("Minerals", "Gas", "Workers");
            foreach (int p in players)
            {
                var t = MatchStats.Player(p)!;
                var inc = t.IncomePerMin;
                Row(p, (inc is { } i ? $"{i.Minerals}" : "-", MineralCol), (inc is { } j ? $"{j.Gas}" : "-", GasCol),
                    ($"{t.Last?.Workers ?? 0}", null));
            }
            ImGuiNET.ImGui.TextDisabled("Per minute, estimated over the last minute.");
            Track();
        }

        void DrawLost(List<int> players)
        {
            Header("Minerals", "Gas", "Army", "Workers");
            foreach (int p in players)
            {
                var t = MatchStats.Player(p)!;
                Row(p, ($"{t.LostMinerals:N0}", MineralCol), ($"{t.LostGas:N0}", GasCol), ($"{t.LostArmy}", null), ($"{t.LostWorkers}", null));
            }
            ImGuiNET.ImGui.TextDisabled("Value of units lost (approximate).");
            Track();
        }

        void DrawSupply(List<int> players)
        {
            Header("Supply", "Minerals", "Gas");
            foreach (int p in players)
            {
                var s = MatchStats.Player(p)!.Last;
                string sup = s is { SupplyUsed: >= 0f } a ? $"{a.SupplyUsed:0}/{a.SupplyCap:0}" : "-";
                Row(p, (sup, s is { SupplyUsed: >= 0f } b && b.SupplyUsed >= b.SupplyCap && b.SupplyCap < 200f ? 0xFF5050FF : null),
                    (s is { BankMinerals: >= 0 } m ? $"{m.BankMinerals:N0}" : "-", MineralCol),
                    (s is { BankGas: >= 0 } g ? $"{g.BankGas:N0}" : "-", GasCol));
            }
        }

        void DrawUpgrades(List<int> players)
        {
            var mem = Memory.Instance;
            if (mem is not { HasUpgrades: true })
            {
                ImGuiNET.ImGui.TextDisabled("Upgrade levels aren't available (signature missed).");
                Track();
                return;
            }
            foreach (int p in players)
            {
                var (name, col) = PlayerLabel(p);
                ImGuiNET.ImGui.TextColored(UI.ToV4(col), name);
                Track();
                var ups = mem.Upgrades(p);
                string text = ups.Count == 0 ? "none"
                    : string.Join(", ", ups.OrderBy(kv => kv.Key).Select(kv => PrettyNames.Get(kv.Key) + (kv.Value > 1 ? $" ×{kv.Value}" : "")));
                ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + TabW);
                ImGuiNET.ImGui.TextDisabled(text);
                ImGuiNET.ImGui.PopTextWrapPos();
                Track();
            }
        }

        /// <summary>Army value (minerals + gas of army units on the field) over the match, one line per player.</summary>
        void DrawArmyGraph(List<int> players)
        {
            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            var tl = ImGuiNET.ImGui.GetCursorScreenPos();
            var size = new Vector2(TabW, GraphH);
            ImGuiNET.ImGui.Dummy(size);
            Track();
            dl.AddRectFilled(tl, tl + size, 0x40000000, 3f);

            float t0 = float.MaxValue, t1 = 0f; int vmax = 100;
            foreach (int p in players)
                foreach (var s in MatchStats.Player(p)!.Samples)
                { t0 = MathF.Min(t0, s.Time); t1 = MathF.Max(t1, s.Time); vmax = Math.Max(vmax, s.ArmyValue); }
            if (t1 - t0 < 1f) { dl.AddText(tl + new Vector2(8f, 8f), 0x90FFFFFF, "Collecting..."); return; }

            // light gridlines at quarter heights, the peak value top-left, the clock span along the bottom
            for (int q = 1; q < 4; q++)
            {
                float y = tl.Y + size.Y * q / 4f;
                dl.AddLine(new Vector2(tl.X, y), new Vector2(tl.X + size.X, y), 0x18FFFFFF);
            }
            dl.AddText(tl + new Vector2(4f, 2f), 0x80FFFFFF, $"{vmax:N0}");
            string end = SC2Memory.FormatClock(t1);
            dl.AddText(tl + new Vector2(size.X - ImGuiNET.ImGui.CalcTextSize(end).X - 4f, size.Y - 16f), 0x80FFFFFF, end);

            Vector2 At(float t, int v) => new(tl.X + (t - t0) / (t1 - t0) * size.X, tl.Y + size.Y - (float)v / vmax * (size.Y - 18f) - 2f);
            foreach (int p in players)
            {
                var samples = MatchStats.Player(p)!.Samples;
                if (samples.Count < 2) continue;
                var pts = new Vector2[samples.Count];
                for (int i = 0; i < samples.Count; i++) pts[i] = At(samples[i].Time, samples[i].ArmyValue);
                dl.AddPolyline(ref pts[0], pts.Length, PlayerLabel(p).Color, ImGuiNET.ImDrawFlags.None, 1.6f);
            }

            // legend: current values
            foreach (int p in players)
            {
                var (name, col) = PlayerLabel(p);
                ImGuiNET.ImGui.TextColored(UI.ToV4(col), $"{name}  {MatchStats.Player(p)!.Last?.ArmyValue ?? 0:N0}");
                Track();
            }
        }
    }
}
