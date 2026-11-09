using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Everything in production right now, one row each: buildings training units or researching (their queue), and
    /// anything morphing (Lair, Orbital Command, eggs, cocoons, ...), plus idle production buildings. Progress, what a
    /// queue makes and the time left come from SC2Memory.Progress (game time, shown like the clock). Clicking a row centres
    /// the radar on it. Drawn with UI.Table like the Army widget and sized around its content.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class ProductionWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Production";
        protected override Vector2 InitSize    => new(420, 200);
        protected override bool    HasOpacityOverride => Program.Config.ProductionWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.ProductionWidgetAlpha;
        protected override bool    HasPanelMenu => true;
        protected override bool    AutoSize     => true;

        const float RowH = 22f, CellPad = 14f, BarW = 96f;
        const uint MorphCol = 0xFF40E0FF;   // ABGR: amber, as the radar's morph rings
        const uint QueueCol = 0xFFFFD040;   // cyan, as the radar's production rings
        static Config Cfg => Program.Config;
        int _sel = -1;

        enum Col { Inset, Name, Player, Progress, Left, Queued }

        sealed record Entry(UnitEntry U, bool Morph, float Progress, int Queued, float? Eta, int Count, uint Color, string Player, bool Idle = false);

        // what a morph turns into, when the source says it (a Command Center could become either fortress, so it has no entry)
        static readonly Dictionary<string, string> MorphTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Hatchery"] = "Lair", ["Lair"] = "Hive", ["Spire"] = "Greater Spire", ["Gateway"] = "Warp Gate",
            ["BanelingCocoon"] = "Baneling", ["BroodLordCocoon"] = "Brood Lord", ["OverlordCocoon"] = "Overseer",
            ["RavagerCocoon"] = "Ravager", ["LurkerMPEgg"] = "Lurker", ["TransportOverlordCocoon"] = "Overlord (transport)",
        };

        // buildings whose idle queue is worth a row: they make units (research-only buildings sit idle most of the game,
        // and hatcheries make larva, not queue items)
        /// <summary>What a morphing unit turns into, null when the source doesn't say.</summary>
        internal static string? MorphTarget(in UnitEntry u) => MorphTargets.GetValueOrDefault(BaseType(u.TypeName));

        /// <summary>The type without a skin suffix: "Hatchery_Mecha" -> "Hatchery".</summary>
        static string BaseType(string type) => type.IndexOf('_') is > 0 and var i ? type[..i] : type;

        static readonly HashSet<string> Producers = new(StringComparer.OrdinalIgnoreCase)
        {
            "CommandCenter", "OrbitalCommand", "PlanetaryFortress", "Barracks", "Factory", "Starport",
            "Nexus", "Gateway", "RoboticsFacility", "Stargate",
        };

        // ── title-bar menu ───────────────────────────────────────────────────

        protected override void DrawPanelMenu()
        {
            var s = Cfg.Production;
            void Item(string label, bool value, Action<bool> set, string hint)
            {
                if (UI.MenuItem(label, selected: value, hint: hint)) { set(!value); Cfg.SaveDebounced(); }
            }
            Item("Yours",   s.ShowOwn,     v => s.ShowOwn = v,     "Your own production.");
            Item("Allies",  s.ShowAllies,  v => s.ShowAllies = v,  "Your allies' production.");
            Item("Enemies", s.ShowEnemies, v => s.ShowEnemies = v, "Your enemies' production.");
            UI.MenuSeparator();
            Item("Units and research", s.ShowQueues, v => s.ShowQueues = v, "Buildings training units or researching.");
            Item("Morphs",  s.ShowMorphs,  v => s.ShowMorphs = v,  "Anything morphing: Lair, Hive, Orbital Command, eggs, cocoons, ...");
            Item("Idle production", s.ShowIdle, v => s.ShowIdle = v, "Command Centers, Nexuses, Barracks, Gateways, ... with nothing queued.");
            Item("Idle: yours only", s.IdleOwnOnly, v => s.IdleOwnOnly = v, "Only list your own idle buildings.");
            UI.MenuSeparator();
            Item("Combine identical", s.Combine, v => s.Combine = v, "One row \"×n\" for the same thing at about the same progress (a batch of eggs, ...).");
            Item("Player column", s.ShowPlayer, v => s.ShowPlayer = v, "The owner's name.");
        }

        // ── content ──────────────────────────────────────────────────────────

        protected override void DrawWidgetContent()
        {
            var mem = Memory.Instance;
            var entries = Collect(mem);
            if (entries.Count == 0)
            {
                ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + Pad);
                ImGuiNET.ImGui.TextDisabled("Nothing in production.");
                var wp = ImGuiNET.ImGui.GetWindowPos();
                ReportContentSize(new Vector2(ImGuiNET.ImGui.GetItemRectMax().X - wp.X + Pad, ImGuiNET.ImGui.GetCursorPosY()));
                return;
            }

            var cols = new List<Col> { Col.Inset, Col.Name };
            if (Cfg.Production.ShowPlayer) cols.Add(Col.Player);
            cols.AddRange([Col.Progress, Col.Left, Col.Queued]);
            var font = ImGuiStyle.FontNormal;
            float fs = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float Tw(string t) => font.CalcTextSizeA(fs, float.MaxValue, -1f, t).X;
            float swatchW = (Program.Config.ShowTypeBadges ? fs * 1.15f : fs * 0.6f) + 6f;   // type icon, or the player-colour square
            var widths = cols.Select(c =>
            {
                if (c == Col.Inset) return Pad;
                float w = Tw(Header(c));
                foreach (var e in entries)
                    w = MathF.Max(w, c switch
                    {
                        Col.Name => swatchW + Tw(NameText(e)),
                        Col.Progress => BarW,
                        _ => Tw(CellText(c, e)),
                    });
                return MathF.Ceiling(w + CellPad);
            }).ToArray();
            widths[^1] = widths[^1] - CellPad + Pad;
            float tableW = widths.Sum(), fitW = tableW;
            float slack = ImGuiNET.ImGui.GetContentRegionAvail().X - tableW;
            if (slack > 0f) { widths[1] += MathF.Floor(slack); tableW += MathF.Floor(slack); }
            var columns = cols.Select((c, i) => new UI.TableColumn(Header(c), widths[i])).ToArray();

            float startX = ImGuiNET.ImGui.GetCursorPosX();
            UI.Table("##production", columns, entries.Count, (i, colX, rowW) => DrawRow(entries[i], cols, colX, fs),
                     ref _sel, itemHeight: RowH, visibleRows: entries.Count, width: tableW, clickOutsideDeselects: false, border: false);
            if (_sel >= 0 && _sel < entries.Count)
                UnitFilter.CenterRequest = new Vector2(entries[_sel].U.X, entries[_sel].U.Y);
            _sel = -1;
            ReportContentSize(new Vector2(startX + fitW, ImGuiNET.ImGui.GetCursorPosY()));
        }

        List<Entry> Collect(SC2Memory? mem)
        {
            var units = mem?.Snapshot.Units ?? [];
            var s = Cfg.Production;
            int local = mem?.LocalPlayer ?? -1;
            var seen = new HashSet<ulong>();
            var raw = new List<Entry>();
            foreach (var u in units)
            {
                if (u.Record == 0 || u.Owner is 0 or >= 16 || !seen.Add(u.Record)) continue;
                bool own = u.Owner == local, ally = !own && local >= 0 && mem is { HasAllianceTable: true } && mem.IsAlly(u.Owner, local);
                if (own ? !s.ShowOwn : ally ? !s.ShowAllies : !s.ShowEnemies) continue;
                bool morph = u.Progress >= 0f;
                string player = mem?.PlayerName(u.Owner) is { Length: > 0 } n ? n : $"Player {u.Owner}";
                if (!morph && u.QueueProgress < 0f)
                {
                    if (s.ShowIdle && u.CanQueue && (own || !s.IdleOwnOnly) && Producers.Contains(BaseType(u.TypeName)))
                        raw.Add(new Entry(u, false, 0f, 0, null, 1, PlayerColors.ForPlayer(u.Owner, u.Color), player, Idle: true));
                    continue;
                }
                if (morph ? !s.ShowMorphs : !s.ShowQueues) continue;
                float p = morph ? u.Progress : u.QueueProgress;
                raw.Add(new Entry(u, morph, Math.Clamp(p, 0f, 1f), morph ? 0 : Math.Max(0, u.QueueCount - 1), u.TimeLeft >= 0f ? u.TimeLeft : null, 1,
                                  PlayerColors.ForPlayer(u.Owner, u.Color), player));
            }
            if (s.Combine)
                raw = [.. raw.GroupBy(e => (e.U.Owner, e.U.DisplayName, e.U.QueueProduct, e.Morph, e.Idle, Bucket: (int)(e.Progress * 20f)))
                             .Select(g => g.First() with { Count = g.Count(), Queued = g.Sum(e => e.Queued) })];

            // the local player's team first (Army widget order), then the furthest along
            var order = ArmyWidget.Teams(raw.Select(e => e.U.Owner).Distinct()).SelectMany(t => t).Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
            return [.. raw.OrderBy(e => order.GetValueOrDefault(e.U.Owner)).ThenBy(e => e.Idle).ThenByDescending(e => e.Progress).Take(Math.Max(1, s.MaxRows))];
        }

        static string Header(Col c) => c switch
        {
            Col.Name => "Producing", Col.Player => "Player", Col.Progress => "Progress", Col.Left => "Left", Col.Queued => "Queued", _ => "",
        };

        static string? _arrow;
        /// <summary>"→" when the UI font has the glyph, else "->".</summary>
        static unsafe string Arrow =>
            _arrow ??= ImGuiNET.ImGui.GetFont().NativePtr != null && ImGuiNET.ImGui.GetFont().FindGlyphNoFallback('→').NativePtr != null ? "→" : "->";

        static string NameText(Entry e)
        {
            string name = e.Count > 1 ? $"{e.U.DisplayName} ×{e.Count}" : e.U.DisplayName;
            string? to = e.Idle ? null : e.Morph ? MorphTarget(e.U) : e.U.QueueProduct;
            return to is { Length: > 0 } ? $"{name} {Arrow} {to}" : name;
        }

        static string CellText(Col c, Entry e) => c switch
        {
            Col.Player => e.Player,
            Col.Left   => e.Idle ? "" : e.Eta is { } t ? SC2Memory.FormatClock(t) : "...",
            Col.Queued => e.Queued > 0 ? $"+{e.Queued}" : "",
            _          => "",
        };

        /// <summary>Paints one row with the draw list (UI.Table rows must not use ImGui item functions).</summary>
        void DrawRow(Entry e, List<Col> cols, float[] colX, float fs)
        {
            var dl   = ImGuiNET.ImGui.GetWindowDrawList();
            var font = ImGuiStyle.FontNormal;
            float top = ImGuiNET.ImGui.GetCursorScreenPos().Y, ty = top + (RowH - fs) * 0.5f;
            uint kindCol = e.Morph ? MorphCol : QueueCol;
            for (int ci = 0; ci < cols.Count; ci++)
            {
                float x = colX[ci];
                switch (cols[ci])
                {
                    case Col.Name:
                        float sh = Program.Config.ShowTypeBadges ? fs * 1.15f : fs * 0.6f;
                        if (Program.Config.ShowTypeBadges)   // the building's icon, ringed in the player's colour
                            UnitIcons.Draw(dl, new Vector2(x, top + (RowH - sh) * 0.5f), sh, e.U.TypeName, e.U.DisplayName, RadarProfiles.FactionOf(e.U), e.Color);
                        else
                            dl.AddRectFilled(new Vector2(x, top + (RowH - sh) * 0.5f), new Vector2(x + sh, top + (RowH + sh) * 0.5f), e.Color, 2f);
                        dl.AddText(font, fs, new Vector2(x + sh + 6f, ty), ImGuiStyle.Text, NameText(e));
                        break;
                    case Col.Progress:
                    {
                        if (e.Idle) { dl.AddText(font, fs, new Vector2(x, ty), ImGuiStyle.Warning, "idle"); break; }
                        float bh = MathF.Round(fs * 0.85f), by = top + (RowH - bh) * 0.5f;
                        dl.AddRectFilled(new Vector2(x, by), new Vector2(x + BarW, by + bh), UI.Alpha(ImGuiStyle.InputBg, 230), 3f);
                        if (e.Progress > 0f)
                            dl.AddRectFilled(new Vector2(x, by), new Vector2(x + BarW * e.Progress, by + bh), UI.Alpha(kindCol, 150), 3f);
                        string pct = $"{e.Progress * 100f:0}%";
                        float pw = font.CalcTextSizeA(fs, float.MaxValue, -1f, pct).X;
                        dl.AddText(font, fs, new Vector2(x + (BarW - pw) * 0.5f, ty), ImGuiStyle.Text, pct);
                        break;
                    }
                    case Col.Player:
                        dl.AddText(font, fs, new Vector2(x, ty), ImGuiStyle.TextDim, CellText(Col.Player, e));
                        break;
                    case Col.Left:
                        dl.AddText(font, fs, new Vector2(x, ty), e.Eta is null ? ImGuiStyle.TextDim : ImGuiStyle.Text, CellText(Col.Left, e));
                        break;
                    case Col.Queued:
                        dl.AddText(font, fs, new Vector2(x, ty), ImGuiStyle.TextDim, CellText(Col.Queued, e));
                        break;
                }
            }
            var mouse = ImGuiNET.ImGui.GetIO().MousePos;
            if (mouse.Y >= top && mouse.Y < top + RowH && ImGuiNET.ImGui.IsWindowHovered())
                ImGuiNET.ImGui.SetTooltip($"{NameText(e)}  ·  {e.Player}\n" +
                                          (e.Idle ? "Nothing queued" :
                                           $"{(e.Morph ? "Morphing" : "Producing")} {e.Progress * 100f:0}%" +
                                           (e.Eta is { } t ? $", about {SC2Memory.FormatClock(t)} left" : "") +
                                           (e.Queued > 0 ? $"\n{e.Queued} more queued" : "")) +
                                          "\n\nClick to centre the radar on it");
        }
    }
}
