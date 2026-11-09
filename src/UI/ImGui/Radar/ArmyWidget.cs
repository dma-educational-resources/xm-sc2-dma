using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// One row per player, grouped into teams (the game's alliance table): supply and the mineral / gas value of
    /// everything on the field, summed from each unit's catalog entry (Food, CostResource, CostCategory). Values are
    /// type costs, so morphs (lair, archon, ...) count only the morph's own cost and units in production are not
    /// included. Each row also toggles that player's vision overlay. Options: title-bar menu and Settings dialog.
    /// Drawn with UI.Table; column widths are measured from the content and the widget sizes itself around it.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class ArmyWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Army";
        protected override Vector2 InitSize    => new(640, 300);
        protected override bool    HasOpacityOverride => Program.Config.ArmyWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.ArmyWidgetAlpha;
        protected override bool    HasPanelMenu => true;
        protected override bool    AutoSize     => true;

        const uint MineralCol = 0xFFFFC870;   // ABGR: light blue
        const uint GasCol     = 0xFF70D870;   // green
        const uint WarnCol    = 0xFF5050FF;   // red
        const int  SupplyCap  = 200;
        const float RowH      = 22f;
        const float CellPad   = 14f;
        const string SettingsPopup = "##armySettings";

        static Config Cfg => Program.Config;
        bool _openSettings;
        int  _sel = -1;

        sealed class Totals
        {
            public readonly Dictionary<uint, int> Colors = [];
            public float SupplyUsed, SupplyProvided;
            public int Workers, ArmyUnits;
            public readonly int[] Minerals = new int[4], Gas = new int[4];   // by CostCategory 0..3
            public readonly Dictionary<string, int> Army = [], Workers_ = [], Buildings = [];   // composition by display name
            public int BankMinerals, BankGas;   // unspent, from the player object
            public bool HasBank;

            public void Add(Totals o)
            {
                SupplyUsed += o.SupplyUsed; SupplyProvided += o.SupplyProvided;
                BankMinerals += o.BankMinerals; BankGas += o.BankGas; HasBank |= o.HasBank;
                Workers += o.Workers; ArmyUnits += o.ArmyUnits;
                for (int i = 0; i < 4; i++) { Minerals[i] += o.Minerals[i]; Gas[i] += o.Gas[i]; }
            }
        }

        enum RowKind { Team, Player, Total }
        sealed record Row(RowKind Kind, string Label, Totals T, byte Owner = 0, uint Color = 0, int TeamIndex = 0,
                          string TeamKey = "", bool Collapsed = false);

        // Inset: blank leading column so header and cells line up with the title text
        enum Col { Inset, Player, Supply, Bank, Army, Units, Economy, Workers, Tech, Upgrades, Vision }
        const float InsetW = Pad;

        // ── Title-bar menu ───────────────────────────────────────────────────

        protected override void DrawPanelMenu()
        {
            var s = Cfg.Army;
            if (UI.MenuItem("Alliance colours", selected: Cfg.AllianceColors,
                    hint: "Radar colours: you green, allies yellow, enemies red. Off: each player's own colour."))
            { Cfg.AllianceColors = !Cfg.AllianceColors; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Team totals", selected: s.TeamTotals, hint: "A totals row under each team with more than one player."))
            { s.TeamTotals = !s.TeamTotals; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Hide AI / map players", selected: s.HideUnnamed,
                    hint: "Only list players with a lobby name (hides computer and map-controlled players)."))
            { s.HideUnnamed = !s.HideUnnamed; Cfg.SaveDebounced(); }
            UI.MenuSeparator();
            if (UI.MenuItem("Settings...", hint: "Columns and display options.")) _openSettings = true;
        }

        void DrawSettings()
        {
            if (_openSettings) { ImGuiNET.ImGui.OpenPopup(SettingsPopup); _openSettings = false; }
            if (!UI.BeginModal(SettingsPopup, 380f)) return;
            UI.ModalTitle("Army widget");
            var s = Cfg.Army;
            bool changed = false;
            bool Opt(string label, bool value, string hint)
            {
                bool v = value;
                if (UI.Toggle(label, ref v, hint: hint)) changed = true;
                return v;
            }

            UI.BeginGroup("Columns");
            s.ShowSupply  = Opt("Supply##army_c_supply",   s.ShowSupply,  "Supply used / cap (cap shown up to 200).");
            s.ShowBank    = Opt("Bank##army_c_bank",       s.ShowBank,    "Unspent minerals / gas, read from the game.");
            s.ShowArmy    = Opt("Army value##army_c_army", s.ShowArmy,    "Minerals / gas of army units and defences on the field.");
            s.ShowUnits   = Opt("Army units##army_c_units",s.ShowUnits,   "Number of army units.");
            s.ShowEconomy = Opt("Economy value##army_c_eco", s.ShowEconomy, "Minerals / gas of workers and economy buildings.");
            s.ShowWorkers = Opt("Workers##army_c_workers", s.ShowWorkers, "Number of workers.");
            s.ShowTech    = Opt("Tech value##army_c_tech", s.ShowTech,    "Minerals / gas of tech buildings and everything uncategorised.");
            s.ShowUpgrades = Opt("Upgrades##army_c_upgrades", s.ShowUpgrades, "Weapon / armour levels (M = melee, R = ranged, A = armour, ...) and how many other upgrades are done.");
            s.ShowVision  = Opt("Vision##army_c_vision",   s.ShowVision,  "Per-player toggle for the vision overlay.");
            UI.EndGroup();

            UI.BeginGroup("Display");
            Cfg.AllianceColors = Opt("Alliance colours##army_d_alliance", Cfg.AllianceColors, "Radar colours: you green, allies yellow, enemies red.");
            s.TeamTotals    = Opt("Team totals##army_d_totals",   s.TeamTotals,    "A totals row under each team with more than one player.");
            s.SupplyWarning = Opt("Supply block warning##army_d_block", s.SupplyWarning, "Supply in red when a player is at their supply cap.");
            s.ClanTags      = Opt("Clan tags##army_d_clans",      s.ClanTags,      "Show names as <CLAN> name.");
            s.HideUnnamed   = Opt("Hide AI / map players##army_d_hide", s.HideUnnamed, "Only list players with a lobby name.");
            Cfg.VisionHighGround = Opt("Vision stops at high ground##army_d_highground", Cfg.VisionHighGround,
                "Vision overlay: ground units don't see onto higher ground (from the map's height map). Flyers and tall units see over it.");
            UI.EndGroup();

            if (changed) Cfg.SaveDebounced();
            if (UI.Button("Done##army_done", new Vector2(ImGuiNET.ImGui.GetContentRegionAvail().X, 28f)))
                ImGuiNET.ImGui.CloseCurrentPopup();
            UI.EndModal();
        }

        // ── Content ──────────────────────────────────────────────────────────

        protected override void DrawWidgetContent()
        {
            DrawSettings();
            var mem   = Memory.Instance;
            var units = mem?.Snapshot.Units ?? [];
            var s     = Cfg.Army;

            var players = new SortedDictionary<byte, Totals>();
            var counted = new HashSet<ulong>();   // several actors can belong to one unit (bunker + upgrade, ...)
            foreach (var u in units)
            {
                if (u.Owner is 0 or >= 16) continue;   // neutral, hostile
                if (u.Category is not (UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building)) continue;
                if (u.Record == 0 || !counted.Add(u.Record)) continue;
                if (!players.TryGetValue(u.Owner, out var t)) players[u.Owner] = t = new Totals();
                t.Colors[u.Color] = t.Colors.GetValueOrDefault(u.Color) + 1;
                if (u.Food < 0) t.SupplyUsed -= u.Food; else t.SupplyProvided += u.Food;
                if (u.Category == UnitCategory.Worker) t.Workers++;
                else if (u.Category == UnitCategory.Unit && u.CostCategory == 1) t.ArmyUnits++;
                var kinds = u.Category switch { UnitCategory.Worker => t.Workers_, UnitCategory.Building => t.Buildings, _ => t.Army };
                kinds[u.DisplayName] = kinds.GetValueOrDefault(u.DisplayName) + 1;
                int c = u.CostCategory is >= 0 and <= 3 ? u.CostCategory : 0;
                t.Minerals[c] += u.Minerals;
                t.Gas[c]      += u.Gas;
            }

            // the game's own bank and supply (player objects) over the unit sums when available
            if (mem != null)
                foreach (var (owner, t) in players)
                    if (mem.GetPlayerStats(owner) is { } ps)
                    {
                        t.BankMinerals = ps.Minerals; t.BankGas = ps.Gas; t.HasBank = true;
                        if (ps.SupplyUsed >= 0f && ps.SupplyCap >= 0f) { t.SupplyUsed = ps.SupplyUsed; t.SupplyProvided = ps.SupplyCap; }
                    }

            // AI / map-controlled players have no lobby name; hide them only when names are known at all
            if (s.HideUnnamed && mem != null && players.Keys.Any(p => mem.PlayerName(p).Length > 0))
                foreach (var p in players.Keys.Where(p => mem.PlayerName(p).Length == 0).ToList()) players.Remove(p);

            if (players.Count == 0)
            {
                ImGuiNET.ImGui.TextDisabled("No players on the field.");
                var wp = ImGuiNET.ImGui.GetWindowPos();
                ReportContentSize(new Vector2(ImGuiNET.ImGui.GetItemRectMax().X - wp.X, ImGuiNET.ImGui.GetCursorPosY()));
                return;
            }

            // rows: per team a header (click to fold), its players (unless folded), and a totals row
            int local = mem?.LocalPlayer ?? -1;
            var rows = new List<Row>();
            var teams = Teams(players.Keys);
            for (int ti = 0; ti < teams.Count; ti++)
            {
                bool own = teams[ti].Contains((byte)local);
                string key = own ? "own" : string.Join(",", teams[ti]);
                bool collapsed = s.CollapsedTeams.Contains(key);
                var teamTotal = new Totals();
                string title = own ? $"TEAM {ti + 1} (yours)" : $"TEAM {ti + 1}";
                if (collapsed) title += $"  ({teams[ti].Count} player{(teams[ti].Count == 1 ? "" : "s")})";
                rows.Add(new Row(RowKind.Team, title, teamTotal, TeamIndex: ti, TeamKey: key, Collapsed: collapsed));
                foreach (byte owner in teams[ti])
                {
                    var t = players[owner];
                    teamTotal.Add(t);
                    if (collapsed) continue;
                    string name = (s.ClanTags ? mem?.PlayerDisplayName(owner) : mem?.PlayerName(owner)) is { Length: > 0 } n ? n : $"Player {owner}";
                    rows.Add(new Row(RowKind.Player, owner == local ? $"{name} (you)" : name, t, owner,
                                     PlayerColors.ForPlayer(owner, t.Colors.MaxBy(kv => kv.Value).Key), ti));
                }
                // a folded team still shows its totals
                if (teams[ti].Count > 1 && (s.TeamTotals || collapsed))
                    rows.Add(new Row(RowKind.Total, "Team total", teamTotal, TeamIndex: ti));
            }

            // columns, each as wide as its widest cell
            var cols = new List<Col> { Col.Inset, Col.Player };
            if (s.ShowSupply)  cols.Add(Col.Supply);
            if (s.ShowBank && players.Values.Any(t => t.HasBank)) cols.Add(Col.Bank);
            if (s.ShowArmy)    cols.Add(Col.Army);
            if (s.ShowUnits)   cols.Add(Col.Units);
            if (s.ShowEconomy) cols.Add(Col.Economy);
            if (s.ShowWorkers) cols.Add(Col.Workers);
            if (s.ShowTech)    cols.Add(Col.Tech);
            if (s.ShowUpgrades && mem is { HasUpgrades: true }) cols.Add(Col.Upgrades);
            if (s.ShowVision)  cols.Add(Col.Vision);
            var font  = ImGuiStyle.FontNormal;
            float fs  = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float Tw(string t) => font.CalcTextSizeA(fs, float.MaxValue, -1f, t).X;
            float swatchW = fs * 0.6f + 6f;
            var widths = cols.Select(c =>
            {
                if (c == Col.Inset) return InsetW;
                float w = Tw(Header(c));
                foreach (var r in rows)
                    w = MathF.Max(w, c switch
                    {
                        Col.Player => r.Kind == RowKind.Team ? Tw(r.Label) + fs : Tw(r.Label) + (r.Kind == RowKind.Player ? swatchW : 0f),
                        Col.Vision => fs,
                        _ when r.Kind == RowKind.Team => 0f,
                        Col.Army or Col.Economy or Col.Tech or Col.Bank => CostWidth(c, r.T, Tw),
                        _ => Tw(CellText(c, r)),
                    });
                return MathF.Ceiling(w + CellPad);
            }).ToArray();
            // the last column ends with the same gap as the inset on the left, not the between-column spacing
            widths[^1] = widths[^1] - CellPad + InsetW;
            float tableW = widths.Sum();
            float fitW   = tableW;              // what the widget sizes to (before stretching, or it could never shrink)
            // a narrow table is still as wide as the widget's minimum width: the Player column takes the slack
            float slack = ImGuiNET.ImGui.GetContentRegionAvail().X - tableW;
            if (slack > 0f) { widths[1] += MathF.Floor(slack); tableW += MathF.Floor(slack); }
            var columns = cols.Select((c, i) => new UI.TableColumn(Header(c), widths[i])).ToArray();

            float startX = ImGuiNET.ImGui.GetCursorPosX();
            UI.Table("##army", columns, rows.Count, (i, colX, rowW) => DrawRow(rows[i], cols, colX, rowW, fs),
                     ref _sel, itemHeight: RowH, visibleRows: rows.Count, width: tableW, clickOutsideDeselects: false, border: false);

            // clicking a team header folds / unfolds it; rows aren't otherwise selectable
            if (_sel >= 0 && _sel < rows.Count && rows[_sel].Kind == RowKind.Team)
            {
                var key = rows[_sel].TeamKey;
                if (!s.CollapsedTeams.Remove(key)) s.CollapsedTeams.Add(key);
                Cfg.SaveDebounced();
            }
            _sel = -1;

            ReportContentSize(new Vector2(startX + fitW,ImGuiNET.ImGui.GetCursorPosY()));
        }

        static string Header(Col c) => c switch
        {
            Col.Inset  => "",
            Col.Player => "Player", Col.Supply => "Supply", Col.Bank => "Bank", Col.Army => "Army", Col.Units => "Units",
            Col.Economy => "Economy", Col.Workers => "Workers", Col.Tech => "Tech", Col.Upgrades => "Upgrades", _ => "Vision",
        };

        static (int M, int G) Cost(Col c, Totals t) => c switch
        {
            Col.Army    => (t.Minerals[1], t.Gas[1]),
            Col.Economy => (t.Minerals[2], t.Gas[2]),
            Col.Bank    => (t.BankMinerals, t.BankGas),
            _           => (t.Minerals[3] + t.Minerals[0], t.Gas[3] + t.Gas[0]),
        };

        const string CostSep = " / ";

        static float CostWidth(Col c, Totals t, Func<string, float> tw)
        {
            var (m, g) = Cost(c, t);
            return tw($"{m:N0}") + tw(CostSep) + tw($"{g:N0}");
        }

        static string CellText(Col c, Row r) => c switch
        {
            Col.Supply  => r.Kind == RowKind.Total ? $"{r.T.SupplyUsed:0}" : $"{r.T.SupplyUsed:0}/{(int)MathF.Min(r.T.SupplyProvided, SupplyCap)}",
            Col.Units   => r.T.ArmyUnits.ToString(),
            Col.Workers => r.T.Workers.ToString(),
            Col.Upgrades => r.Kind == RowKind.Player ? UpgradeSummary(r.Owner).Short : "",
            _           => "",
        };

        /// <summary>Paints one row with the draw list (UI.Table rows must not use ImGui item functions).</summary>
        void DrawRow(Row r, List<Col> cols, float[] colX, float rowW, float fs)
        {
            var dl   = ImGuiNET.ImGui.GetWindowDrawList();
            var font = ImGuiStyle.FontNormal;
            var top  = ImGuiNET.ImGui.GetCursorScreenPos().Y;
            float ty = top + (RowH - fs) * 0.5f;
            var mouse = ImGuiNET.ImGui.GetIO().MousePos;
            bool rowHovered = mouse.Y >= top && mouse.Y < top + RowH && ImGuiNET.ImGui.IsWindowHovered();
            uint textCol = ImGuiStyle.Text, dimCol = ImGuiStyle.TextDim;

            if (r.Kind == RowKind.Team)
            {
                float px = colX[1];   // the Player column, after the inset
                if (r.TeamIndex > 0)
                    dl.AddLine(new Vector2(px, top), new Vector2(colX[0] + rowW - InsetW, top), UI.Alpha(ImGuiStyle.Border, 140));
                // fold arrow: right when folded, down when open
                float cx = px + fs * 0.35f, cy = top + RowH * 0.5f, ar = fs * 0.22f;
                if (r.Collapsed) dl.AddTriangleFilled(new Vector2(cx - ar * 0.6f, cy - ar), new Vector2(cx - ar * 0.6f, cy + ar), new Vector2(cx + ar, cy), dimCol);
                else             dl.AddTriangleFilled(new Vector2(cx - ar, cy - ar * 0.6f), new Vector2(cx + ar, cy - ar * 0.6f), new Vector2(cx, cy + ar), dimCol);
                dl.AddText(font, fs, new Vector2(px + fs, ty), dimCol, r.Label);
                if (rowHovered) ImGuiNET.ImGui.SetTooltip(r.Collapsed ? "Show this team's players" : "Hide this team's players");
                return;
            }

            bool total = r.Kind == RowKind.Total;
            for (int ci = 0; ci < cols.Count; ci++)
            {
                float x = colX[ci];
                switch (cols[ci])
                {
                    case Col.Player:
                        if (total) { dl.AddText(font, fs, new Vector2(x + fs, ty), dimCol, r.Label); break; }
                        float sh = fs * 0.6f;
                        dl.AddRectFilled(new Vector2(x, top + (RowH - sh) * 0.5f), new Vector2(x + sh, top + (RowH + sh) * 0.5f), r.Color, 2f);
                        dl.AddText(font, fs, new Vector2(x + sh + 6f, ty), textCol, r.Label);
                        float right = ci + 1 < cols.Count ? colX[ci + 1] : colX[0] + rowW;
                        if (rowHovered && mouse.X >= x && mouse.X < right)
                            ImGuiNET.ImGui.SetTooltip(Composition(r));
                        break;
                    case Col.Supply:
                    {
                        int cap = (int)MathF.Min(r.T.SupplyProvided, SupplyCap);
                        bool blocked = !total && Cfg.Army.SupplyWarning && cap is > 0 and < SupplyCap && r.T.SupplyUsed >= cap;
                        dl.AddText(font, fs, new Vector2(x, ty), blocked ? WarnCol : total ? dimCol : textCol, CellText(Col.Supply, r));
                        break;
                    }
                    case Col.Bank when !r.T.HasBank:
                        dl.AddText(font, fs, new Vector2(x, ty), dimCol, "-");
                        break;
                    case Col.Army: case Col.Economy: case Col.Tech: case Col.Bank:
                    {
                        var (m, g) = Cost(cols[ci], r.T);
                        string ms = $"{m:N0}";
                        float mw = font.CalcTextSizeA(fs, float.MaxValue, -1f, ms).X;
                        float sw = font.CalcTextSizeA(fs, float.MaxValue, -1f, CostSep).X;
                        dl.AddText(font, fs, new Vector2(x, ty), total ? UI.Alpha(MineralCol, 170) : MineralCol, ms);
                        dl.AddText(font, fs, new Vector2(x + mw, ty), UI.Alpha(dimCol, 150), CostSep);
                        dl.AddText(font, fs, new Vector2(x + mw + sw, ty), total ? UI.Alpha(GasCol, 170) : GasCol, $"{g:N0}");
                        break;
                    }
                    case Col.Units: case Col.Workers:
                        dl.AddText(font, fs, new Vector2(x, ty), total ? dimCol : textCol, CellText(cols[ci], r));
                        break;
                    case Col.Upgrades:
                    {
                        dl.AddText(font, fs, new Vector2(x, ty), textCol, CellText(Col.Upgrades, r));
                        float uright = ci + 1 < cols.Count ? colX[ci + 1] : colX[0] + rowW;
                        if (rowHovered && mouse.X >= x && mouse.X < uright && r.Kind == RowKind.Player && UpgradeSummary(r.Owner).Full is { Length: > 0 } full)
                            ImGuiNET.ImGui.SetTooltip(full);
                        break;
                    }
                    case Col.Vision:
                        // centred under the header text
                        if (!total)
                        {
                            float hw = font.CalcTextSizeA(fs, float.MaxValue, -1f, Header(Col.Vision)).X;
                            DrawVisionBox(dl, x + (hw - MathF.Min(14f, RowH - 8f)) * 0.5f, top, fs, r.Owner, mouse);
                        }
                        break;
                }
            }
        }

        /// <summary>Player row tooltip: what the player has on the field, by unit type.</summary>
        static string Composition(Row r)
        {
            static string Line(string title, Dictionary<string, int> kinds)
            {
                if (kinds.Count == 0) return "";
                var top = kinds.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
                string list = string.Join(", ", top.Take(10).Select(kv => $"{kv.Value} {kv.Key}"));
                if (top.Count > 10) list += $", +{top.Skip(10).Sum(kv => kv.Value)} more";
                return $"\n{title}: {list}";
            }
            string up = UpgradeSummary(r.Owner).Full;
            return $"Player {r.Owner}" + Line("Army", r.T.Army) + Line("Workers", r.T.Workers_) + Line("Buildings", r.T.Buildings) +
                   (up.Length > 0 ? $"\nUpgrades: {up.Replace("\n", ", ")}" : "");
        }

        // weapon / armour lines: "ZergMeleeWeaponsLevel2" -> line "MeleeWeapons", level 2
        static readonly System.Text.RegularExpressions.Regex LevelUpgrade =
            new(@"^(?:Terran|Zerg|Protoss)(\w+?)Level(\d)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        static readonly (string Line, string Short)[] Lines =
        [
            ("MeleeWeapons", "M"), ("MissileWeapons", "R"), ("GroundArmors", "A"), ("FlyerWeapons", "FW"), ("FlyerArmors", "FA"),
            ("InfantryWeapons", "IW"), ("InfantryArmors", "IA"), ("VehicleWeapons", "VW"), ("ShipWeapons", "SW"),
            ("VehicleAndShipArmors", "VA"), ("GroundWeapons", "W"), ("Shields", "S"), ("AirWeapons", "AW"), ("AirArmors", "AA"),
        ];

        /// <summary>"M1 R2 A1 +2" (levels, then how many other upgrades) and the full list for the tooltip.</summary>
        static (string Short, string Full) UpgradeSummary(byte owner)
        {
            var ups = Memory.Instance?.Upgrades(owner);
            if (ups == null || ups.Count == 0) return ("", "");
            var levels = new Dictionary<string, int>();
            var other = new List<string>();
            foreach (var (id, n) in ups)
            {
                if (id.StartsWith("Spray", StringComparison.OrdinalIgnoreCase)) continue;   // cosmetic
                if (LevelUpgrade.Match(id) is { Success: true } m)
                    levels[m.Groups[1].Value] = Math.Max(levels.GetValueOrDefault(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                else other.Add(PrettyNames.Get(id));
            }
            var known = Lines.Where(l => levels.ContainsKey(l.Line)).ToList();
            var shortText = string.Join(" ", known.Select(l => $"{l.Short}{levels[l.Line]}")
                .Concat(levels.Keys.Where(k => known.All(l => l.Line != k)).Select(k => $"{k}{levels[k]}")));
            if (other.Count > 0) shortText += (shortText.Length > 0 ? " " : "") + $"+{other.Count}";
            var full = levels.Select(kv => $"{PrettyNames.Get(kv.Key)} {kv.Value}").Concat(other.Order()).ToList();
            return (shortText, string.Join("\n", full));
        }

        /// <summary>A checkbox in the UI library's style, painted and hit-tested by hand inside the table row.</summary>
        static void DrawVisionBox(ImDrawListPtr dl, float x, float top, float fs, byte owner, Vector2 mouse)
        {
            float sz = MathF.Min(14f, RowH - 8f);
            var tl = new Vector2(x, top + (RowH - sz) * 0.5f);
            var br = tl + new Vector2(sz, sz);
            bool on  = Cfg.VisionPlayers.Contains(owner);
            bool hov = mouse.X >= tl.X - 2 && mouse.X < br.X + 2 && mouse.Y >= tl.Y - 2 && mouse.Y < br.Y + 2 && ImGuiNET.ImGui.IsWindowHovered();
            uint bg = on ? (hov ? UI.Alpha(ImGuiStyle.ControlFill, 255) : ImGuiStyle.ControlFill)
                         : (hov ? UI.Alpha(ImGuiStyle.InputBg, 255) : UI.Alpha(ImGuiStyle.InputBg, 200));
            dl.AddRectFilled(tl, br, bg, 3f);
            dl.AddRect(tl, br, UI.Alpha(ImGuiStyle.Border, on ? (byte)0 : (byte)170), 3f);
            if (on)
            {
                // tick
                dl.AddLine(tl + new Vector2(sz * 0.22f, sz * 0.52f), tl + new Vector2(sz * 0.42f, sz * 0.72f), 0xFFFFFFFF, 1.6f);
                dl.AddLine(tl + new Vector2(sz * 0.42f, sz * 0.72f), tl + new Vector2(sz * 0.78f, sz * 0.30f), 0xFFFFFFFF, 1.6f);
            }
            if (hov)
            {
                ImGuiNET.ImGui.SetTooltip("Show what this player can see, including vision its allies share.");
                if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (on) Cfg.VisionPlayers.Remove(owner); else Cfg.VisionPlayers.Add(owner);
                    Cfg.SaveDebounced();
                }
            }
        }

        /// <summary>Teams from the game's alliance table: a player joins the first team whose every member is its ally
        /// both ways (IsAlly), so a one-off link on custom maps doesn't chain two teams together. Each player alone when
        /// the table isn't available. The local player's team first.</summary>
        internal static List<List<byte>> Teams(IEnumerable<byte> players)
        {
            var mem = Memory.Instance;
            var teams = new List<List<byte>>();
            foreach (byte p in players.Order())
            {
                var team = mem is { HasAllianceTable: true } ? teams.FirstOrDefault(t => t.All(q => mem.IsAlly(p, q))) : null;
                if (team != null) team.Add(p); else teams.Add([p]);
            }
            int local = mem?.LocalPlayer ?? -1;
            return [.. teams.OrderByDescending(t => t.Contains((byte)local)).ThenBy(t => t[0])];
        }
    }
}
