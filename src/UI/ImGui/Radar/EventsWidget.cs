using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Live Events: what each player is doing, the way an observer sees it - their current selection and every command
    /// they give (ability, queued or not, target unit or point), newest first. From SC2Memory's live event feed (the
    /// replay the game client writes as it plays), so it trails the game by about a second.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class EventsWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Live Events";
        protected override Vector2 InitSize    => new(440, 320);
        protected override bool    HasOpacityOverride => Program.Config.EventsWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.EventsWidgetAlpha;
        protected override bool    HasPanelMenu => true;
        protected override ImGuiWindowFlags ChromeScrollExtraFlags => ImGuiWindowFlags.NoScrollbar;

        bool _hideSmart = true, _showSelections = true;
        int _player;   // 0 = everyone

        protected override void DrawPanelMenu()
        {
            if (UI.MenuItem("Hide right-clicks", selected: _hideSmart, hint: "Plain right-click moves and attacks (\"smart\") - most of what players do.")) _hideSmart = !_hideSmart;
            if (UI.MenuItem("Show selections", selected: _showSelections)) _showSelections = !_showSelections;
            if (UI.MenuItem("Ring selected units on the radar", selected: Program.Config.ShowSelections))
            { Program.Config.ShowSelections = !Program.Config.ShowSelections; Program.Config.SaveDebounced(); }
        }

        protected override void DrawWidgetContent()
        {
            var mem = Memory.Instance;
            if (mem == null || !Memory.InGame) { ImGuiNET.ImGui.TextDisabled("Shown during a match."); return; }
            if (!Program.Config.TrackEvents) { ImGuiNET.ImGui.TextDisabled("The live event feed is off (Track Events in the config)."); return; }
            if (!mem.EventFeedStatus.StartsWith("Live")) ImGuiNET.ImGui.TextDisabled(mem.EventFeedStatus);

            var units = mem.Snapshot.Units;
            var byRecord = new Dictionary<ulong, UnitEntry>();
            foreach (var u in units) if (u.Record != 0) byRecord.TryAdd(u.Record, u);
            var tags = mem.TagRecords;
            string UnitName(uint tag) => tags.TryGetValue(tag, out var r) && byRecord.TryGetValue(r, out var u) ? u.DisplayName : "a unit";
            string Name(int p) => p > 0 ? (mem.PlayerName(p) is { Length: > 0 } n ? n : $"Player {p}") : "?";
            uint Col(int p) => PlayerColors.ForPlayer(p, 0xFFFFFFFF) | 0xFF000000;

            // everyone in the match: the human players (they have lobby names) and whoever the feed has seen - a player
            // shows up even before the feed has seen them select anything (it follows the game from when the radar joined)
            var selections = mem.PlayerSelections;
            var players = Enumerable.Range(1, 15).Where(p => mem.PlayerName(p).Length > 0)
                                    .Concat(mem.RecentCommands.Select(c => c.Player)).Concat(selections.Keys)
                                    .Where(p => p > 0).Distinct().Order().ToArray();
            string[] items = ["Everyone", .. players.Select(Name)];
            int sel = _player == 0 ? 0 : Array.IndexOf(players, _player) + 1;
            if (sel < 0) sel = 0;
            if (UI.Combo("##ev_player", ref sel, items, ImGuiNET.ImGui.GetContentRegionAvail().X, localizeItems: false)) _player = sel == 0 ? 0 : players[sel - 1];

            ImGuiNET.ImGui.BeginChild("##ev_lines", new Vector2(0f, 0f));
            if (_showSelections)
            {
                foreach (int p in players)
                {
                    if (_player != 0 && p != _player) continue;
                    if (!selections.TryGetValue(p, out var sTags))
                    {
                        ImGuiNET.ImGui.TextColored(UI.ToV4(Col(p)), Name(p));
                        ImGuiNET.ImGui.SameLine();
                        ImGuiNET.ImGui.TextDisabled("no selection seen yet");
                        continue;
                    }
                    var kinds = sTags.Select(UnitName).GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
                    ImGuiNET.ImGui.TextColored(UI.ToV4(Col(p)), Name(p));
                    ImGuiNET.ImGui.SameLine();
                    ImGuiNET.ImGui.TextWrapped(sTags.Length == 0 ? "nothing selected" : "selected: " + string.Join(", ", kinds));
                }
                ImGuiNET.ImGui.Separator();
            }

            var now = DateTime.UtcNow;
            var cmds = mem.RecentCommands.Reverse().Where(c => (_player == 0 || c.Player == _player) && !(_hideSmart && c.Ability == "smart")).Take(100).ToList();
            if (cmds.Count == 0) ImGuiNET.ImGui.TextDisabled("No commands yet.");
            foreach (var c in cmds)
            {
                double ago = (now - c.Time).TotalSeconds;
                ImGuiNET.ImGui.TextDisabled(ago < 60 ? $"{ago,3:0}s" : $"{ago / 60,3:0}m");
                ImGuiNET.ImGui.SameLine();
                ImGuiNET.ImGui.TextColored(UI.ToV4(Col(c.Player)), Name(c.Player));
                ImGuiNET.ImGui.SameLine();
                string what = c.Ability == "smart" ? "Right-click" : PrettyAbility(c.Ability, c.CmdIndex);
                string target = c.TargetTag != 0 ? $" on {UnitName(c.TargetTag)}" : c.Point is { } pt ? $" at ({pt.X:0}, {pt.Y:0})" : "";
                ImGuiNET.ImGui.TextUnformatted(what + target + (c.Queued ? "  (queued)" : ""));
            }
            ImGuiNET.ImGui.EndChild();
        }

        /// <summary>"WarpGateTrain" -> "Warp Gate Train"; "move" -> "Move"; a command index other than the first in brackets.</summary>
        internal static string PrettyAbility(string id, int cmd) =>
            (id.Length > 0 ? char.ToUpperInvariant(id[0]) + System.Text.RegularExpressions.Regex.Replace(id[1..], "(?<=[a-z0-9])(?=[A-Z])", " ") : id) + (cmd > 0 && id is not ("move" or "attack" or "stop" or "HoldPosition" or "Patrol") ? $" [{cmd}]" : "");
    }
}
