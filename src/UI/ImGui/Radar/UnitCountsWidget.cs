using System.Numerics;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Each player's units by type with their count ("Marine 24, Medivac 4"), most valuable first - from the snapshot,
    /// one per unit (several actors can share a unit). Text for now; icons come with the icon source (roadmap C3).
    /// Buildings and workers from the title-bar menu. Sizes itself to its content.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class UnitCountsWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Unit Counts";
        protected override Vector2 InitSize    => new(260, 200);
        protected override bool    HasOpacityOverride => Program.Config.UnitCountsWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.UnitCountsWidgetAlpha;
        protected override bool    HasPanelMenu => true;
        protected override bool    AutoSize     => true;

        const int MaxTypes = 12;
        const float NameW = 140f;
        static Config Cfg => Program.Config;
        float _right;

        void Track() => _right = MathF.Max(_right, ImGuiNET.ImGui.GetItemRectMax().X - ImGuiNET.ImGui.GetWindowPos().X);

        protected override void DrawPanelMenu()
        {
            if (UI.MenuItem("Workers", selected: Cfg.UnitCountsWorkers)) { Cfg.UnitCountsWorkers = !Cfg.UnitCountsWorkers; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Buildings", selected: Cfg.UnitCountsBuildings)) { Cfg.UnitCountsBuildings = !Cfg.UnitCountsBuildings; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Enemies only", selected: Cfg.UnitCountsEnemiesOnly)) { Cfg.UnitCountsEnemiesOnly = !Cfg.UnitCountsEnemiesOnly; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Type icons", selected: Cfg.ShowTypeBadges, hint: "A small icon per type: a race badge with its initials, or your own PNG from the icons folder (also in Production)."))
            { Cfg.ShowTypeBadges = !Cfg.ShowTypeBadges; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Open icons folder", hint: "Put PNGs named after the game's type id here (SiegeTank.png, Zergling.png) to replace the badges."))
            {
                try { System.IO.Directory.CreateDirectory(UnitIcons.Folder); System.Diagnostics.Process.Start("explorer.exe", UnitIcons.Folder); } catch { }
            }
        }

        protected override void DrawWidgetContent()
        {
            _right = 0f;
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 2f);
            ImGuiNET.ImGui.Indent(Pad);
            try { DrawRows(); }
            finally { ImGuiNET.ImGui.Unindent(Pad); }
            ReportContentSize(new Vector2(_right + Pad, ImGuiNET.ImGui.GetCursorPosY()));
        }

        void DrawRows()
        {
            var mem = Memory.Instance;
            var units = Memory.InGame ? mem?.Snapshot.Units ?? [] : [];
            int local = mem?.LocalPlayer ?? -1;
            bool enemiesKnown = local > 0 && mem is { HasAllianceTable: true };

            // owner -> type -> (count, value)
            var byPlayer = new Dictionary<int, Dictionary<string, (int N, int Value, string Type, Faction Race)>>();
            var counted = new HashSet<ulong>();
            foreach (ref readonly var u in units.AsSpan())
            {
                if (u.Owner is 0 or >= 16 || u.Record == 0 || !counted.Add(u.Record)) continue;
                bool want = u.Category switch
                {
                    UnitCategory.Unit => true, UnitCategory.Worker => Cfg.UnitCountsWorkers, UnitCategory.Building => Cfg.UnitCountsBuildings,
                    _ => false,
                };
                if (!want || Cfg.UnitCountsEnemiesOnly && enemiesKnown && mem!.IsAlly(u.Owner, local)) continue;
                if (!byPlayer.TryGetValue(u.Owner, out var types)) byPlayer[u.Owner] = types = [];
                var cur = types.GetValueOrDefault(u.DisplayName);
                types[u.DisplayName] = (cur.N + 1, cur.Value + u.Minerals + u.Gas, u.TypeName, RadarProfiles.FactionOf(in u));
            }

            if (byPlayer.Count == 0)
            {
                ImGuiNET.ImGui.TextDisabled(Memory.InGame ? "No units." : "No match.");
                Track();
                return;
            }
            bool first = true;
            foreach (int p in ObserverWidget.OrderedPlayers(byPlayer.Keys))
            {
                if (!first) ImGuiNET.ImGui.Dummy(new Vector2(1f, 4f));
                first = false;
                var (name, col) = ObserverWidget.PlayerLabel(p);
                ImGuiNET.ImGui.TextColored(UI.ToV4(col), name);
                Track();
                var sorted = byPlayer[p].OrderByDescending(kv => kv.Value.Value).ThenByDescending(kv => kv.Value.N).ThenBy(kv => kv.Key).ToList();
                foreach (var (type, (n, _, typeId, race)) in sorted.Take(MaxTypes))
                {
                    if (Cfg.ShowTypeBadges)
                    {
                        // the type's icon (UnitIcons: a PNG from the icons folder, else a race badge), then the name
                        float bs = ImGuiNET.ImGui.GetTextLineHeight() + 2f;
                        UnitIcons.Draw(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(8f, -1f), bs, typeId, type, race);
                        ImGuiNET.ImGui.Dummy(new Vector2(bs + 12f, ImGuiNET.ImGui.GetTextLineHeight()));
                        ImGuiNET.ImGui.SameLine(0f, 0f);
                        ImGuiNET.ImGui.TextUnformatted(type);
                    }
                    else ImGuiNET.ImGui.TextUnformatted("  " + type);
                    Track();
                    ImGuiNET.ImGui.SameLine(Pad + NameW + (Cfg.ShowTypeBadges ? 28f : 0f));
                    ImGuiNET.ImGui.TextColored(UI.ToV4(col), n.ToString());
                    Track();
                }
                if (sorted.Count > MaxTypes) { ImGuiNET.ImGui.TextDisabled($"  +{sorted.Count - MaxTypes} more types"); Track(); }
            }
        }
    }
}
