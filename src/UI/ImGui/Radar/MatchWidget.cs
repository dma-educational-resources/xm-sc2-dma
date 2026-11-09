using System.Numerics;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// The match at a glance: map name (and description), the in-game clock and speed, the format worked out from the
    /// teams (alliance table) and who you are. Read from memory - see SC2Memory.Match. Sizes itself to its content.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class MatchWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Match";
        protected override Vector2 InitSize    => new(280, 160);
        protected override bool    HasOpacityOverride => Program.Config.MatchWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.MatchWidgetAlpha;
        protected override bool    HasPanelMenu => true;
        protected override bool    AutoSize     => true;

        const float LabelW = 62f, DescWrap = 300f;
        static Config Cfg => Program.Config;
        float _right;   // right edge of this frame's content (window coordinates)

        protected override void DrawPanelMenu()
        {
            if (UI.MenuItem("Map description", selected: Cfg.MatchShowDescription))
            { Cfg.MatchShowDescription = !Cfg.MatchShowDescription; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Game clock on radar", selected: Cfg.ShowClockOverlay, hint: "In-game time and map name in the radar's top-left corner."))
            { Cfg.ShowClockOverlay = !Cfg.ShowClockOverlay; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Terrain on radar", selected: Cfg.ShowTerrain, hint: "The map's height map under the units."))
            { Cfg.ShowTerrain = !Cfg.ShowTerrain; Cfg.SaveDebounced(); }
        }

        void Track() => _right = MathF.Max(_right, ImGuiNET.ImGui.GetItemRectMax().X - ImGuiNET.ImGui.GetWindowPos().X);

        void Row(string label, string value, uint? color = null)
        {
            ImGuiNET.ImGui.TextDisabled(label);
            Track();
            ImGuiNET.ImGui.SameLine(Pad + LabelW);
            if (color is uint c) ImGuiNET.ImGui.TextColored(UI.ToV4(c), value);
            else ImGuiNET.ImGui.TextUnformatted(value);
            Track();
        }

        protected override void DrawWidgetContent()
        {
            _right = 0f;
            var mem = Memory.Instance;
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 2f);
            ImGuiNET.ImGui.Indent(Pad);   // line up with the title
            try { DrawRows(mem); }
            finally { ImGuiNET.ImGui.Unindent(Pad); }
            ReportContentSize(new Vector2(_right + Pad, ImGuiNET.ImGui.GetCursorPosY()));
        }

        void DrawRows(SC2Memory? mem)
        {
            if (mem == null || !Memory.InGame || mem.GameTime < 0f && mem.MapName.Length == 0)
            {
                ImGuiNET.ImGui.TextDisabled("No match.");
                Track();
                return;
            }

            // map
            ImGuiNET.ImGui.TextUnformatted(mem.MapName.Length > 0 ? mem.MapName : "Unknown map");
            Track();
            if (Cfg.MatchShowDescription && mem.MapDescription.Length > 0)
            {
                float x = ImGuiNET.ImGui.GetCursorPosX();
                float wrap = MathF.Min(DescWrap, ImGuiNET.ImGui.CalcTextSize(mem.MapDescription).X + 1f);
                ImGuiNET.ImGui.PushTextWrapPos(x + wrap);
                ImGuiNET.ImGui.TextDisabled(mem.MapDescription.Replace("<n/>", "\n"));
                ImGuiNET.ImGui.PopTextWrapPos();
                Track();
            }
            ImGuiNET.ImGui.Dummy(new Vector2(1f, 4f));

            // clock
            if (mem.GameTime >= 0f)
                Row("Time", SC2Memory.FormatClock(mem.GameTime) + (mem.GamePaused ? "   paused" : ""), mem.GamePaused ? ImGuiStyle.Warning : null);
            if (mem.GameSpeedName.Length > 0)
                Row("Speed", mem.GameSpeedName);
            if (mem.MapSource.Length > 0)
                Row("Map", mem.MapSource);

            // format from the teams of players that have units, lobby-named players only when names are known
            var owners = (mem.Snapshot.Units)
                .Where(u => u.Record != 0 && u.Owner is > 0 and < 16 && u.Category is UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building)
                .Select(u => u.Owner).Distinct().ToList();
            var named = owners.Where(p => mem.PlayerName(p).Length > 0).ToList();
            var players = named.Count > 0 ? named : owners;
            if (players.Count > 0)
            {
                var teams = ArmyWidget.Teams(players);
                string format = teams.Count > 1 ? string.Join("v", teams.Select(t => t.Count)) : $"{players.Count} player{(players.Count == 1 ? "" : "s")}";
                int others = owners.Count - players.Count;
                Row("Format", format + (others > 0 ? $"  (+{others} AI / map)" : ""));
            }
            int local = mem.LocalPlayer;
            if (local > 0)
            {
                string name = mem.PlayerDisplayName((byte)local);
                Row("You", $"{(name.Length > 0 ? name : $"Player {local}")}", VisionOverlay.PlayerColor(local));
            }
        }
    }
}
