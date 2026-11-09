using System.IO;
using System.Numerics;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Past matches (SC2.MatchStats writes one summary per match to match_history\): newest first on the left - date,
    /// map, length - and the selected one's players on the right: their side, peak army, what they lost and their
    /// average income (the Observer widget's estimates), plus how many tech steps the timeline recorded.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class HistoryWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "History";
        protected override Vector2 InitSize    => new(640, 260);
        protected override bool    HasOpacityOverride => Program.Config.HistoryWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.HistoryWidgetAlpha;
        protected override bool    HasPanelMenu => true;

        const float ListW = 230f;
        const uint MineralCol = 0xFFFFC870, GasCol = 0xFF70D870, YouCol = 0xFF60D060, AllyCol = 0xFFE0C060, EnemyCol = 0xFF6060E8;   // ABGR

        string[] _files = [];
        DateTime _nextList;
        int _sel;
        string? _loadedFile;
        MatchStats.MatchSummary? _summary;

        protected override void DrawPanelMenu()
        {
            if (UI.MenuItem("Open history folder"))
                try { Directory.CreateDirectory(MatchStats.HistoryFolder); System.Diagnostics.Process.Start("explorer.exe", MatchStats.HistoryFolder); } catch { }
            if (_sel < _files.Length && UI.MenuItem("Delete this match", hint: "Removes its summary file."))
                try { File.Delete(_files[_sel]); _nextList = DateTime.MinValue; _loadedFile = null; } catch { }
        }

        protected override void DrawWidgetContent()
        {
            if (DateTime.UtcNow >= _nextList)
            {
                _nextList = DateTime.UtcNow.AddSeconds(5);
                _files = Directory.Exists(MatchStats.HistoryFolder)
                    ? [.. Directory.GetFiles(MatchStats.HistoryFolder, "*.json").OrderByDescending(f => f)]
                    : [];
            }
            ImGuiNET.ImGui.Indent(ContentTopGap);
            if (_files.Length == 0)
            {
                ImGuiNET.ImGui.TextDisabled("No matches yet - each match is added when it ends.");
                ImGuiNET.ImGui.Unindent(ContentTopGap);
                return;
            }
            _sel = Math.Clamp(_sel, 0, _files.Length - 1);
            float h = ImGuiNET.ImGui.GetContentRegionAvail().Y;

            ImGuiNET.ImGui.BeginChild("##hist_list", new Vector2(ListW, h), ImGuiNET.ImGuiChildFlags.None);
            UI.ListBox("##hist_lb", ref _sel, [.. _files.Select(ListLabel)], visibleRows: Math.Max(3, (int)(h / 26f) - 1), width: ListW - 8f);
            ImGuiNET.ImGui.EndChild();
            ImGuiNET.ImGui.SameLine(0f, 10f);

            ImGuiNET.ImGui.BeginChild("##hist_detail", new Vector2(0f, h), ImGuiNET.ImGuiChildFlags.None);
            DrawDetail(_files[_sel]);
            ImGuiNET.ImGui.EndChild();
            ImGuiNET.ImGui.Unindent(ContentTopGap);
        }

        // "Oct 23 14:15  Special Forces Elite 5" from the file name (yyyyMMdd-HHmmss) and a quick look at the map
        readonly Dictionary<string, string> _labels = [];
        string ListLabel(string file)
        {
            if (_labels.TryGetValue(file, out var l)) return l;
            string stem = Path.GetFileNameWithoutExtension(file);
            string when = DateTime.TryParseExact(stem, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t) ? t.ToString("MMM d HH:mm") : stem;
            string map = "";
            try { map = JsonSerializer.Deserialize<MatchStats.MatchSummary>(File.ReadAllText(file))?.Map ?? ""; } catch { }
            return _labels[file] = map.Length > 0 ? $"{when}  {map}" : when;
        }

        void DrawDetail(string file)
        {
            if (_loadedFile != file)
            {
                _loadedFile = file;
                try { _summary = JsonSerializer.Deserialize<MatchStats.MatchSummary>(File.ReadAllText(file)); } catch { _summary = null; }
            }
            if (_summary is not { } s) { ImGuiNET.ImGui.TextDisabled("This file couldn't be read."); return; }

            ImGuiNET.ImGui.TextUnformatted(s.Map.Length > 0 ? s.Map : "Unknown map");
            ImGuiNET.ImGui.TextDisabled($"{s.StartedUtc.ToLocalTime():ddd d MMM HH:mm}  ·  {SC2Memory.FormatClock(s.GameSeconds)} game time  ·  {s.Timeline.Count} tech steps");
            ImGuiNET.ImGui.Dummy(new Vector2(1f, 4f));

            float x0 = ImGuiNET.ImGui.GetCursorPosX();
            float[] cols = [0f, 150f, 210f, 280f, 350f, 420f, 490f];
            void Cell(int c, string text, uint? col = null)
            {
                if (c > 0) ImGuiNET.ImGui.SameLine(x0 + cols[c]);
                if (col is uint cc) ImGuiNET.ImGui.TextColored(UI.ToV4(cc), text); else ImGuiNET.ImGui.TextUnformatted(text);
            }
            ImGuiNET.ImGui.TextDisabled("Player");
            string[] heads = ["Side", "Peak army", "Lost", "Lost gas", "Income", "Gas/min"];
            for (int c = 0; c < heads.Length; c++) { ImGuiNET.ImGui.SameLine(x0 + cols[c + 1]); ImGuiNET.ImGui.TextDisabled(heads[c]); }
            foreach (var p in s.Players.OrderBy(p => p.Relation switch { "you" => 0, "ally" => 1, _ => 2 }).ThenBy(p => p.Id))
            {
                uint side = p.Relation switch { "you" => YouCol, "ally" => AllyCol, "enemy" => EnemyCol, _ => 0xFFB0B0B0 };
                Cell(0, p.Name, side);
                Cell(1, p.Relation.Length > 0 ? p.Relation : "-");
                Cell(2, $"{p.PeakArmy:N0}");
                Cell(3, $"{p.LostMinerals:N0}", MineralCol);
                Cell(4, $"{p.LostGas:N0}", GasCol);
                Cell(5, p.IncomeMinerals >= 0 ? $"{p.IncomeMinerals}/min" : "-", MineralCol);
                Cell(6, p.IncomeGas >= 0 ? $"{p.IncomeGas}" : "-", GasCol);
            }
            ImGuiNET.ImGui.Dummy(new Vector2(1f, 4f));
            ImGuiNET.ImGui.TextDisabled("Army value, losses and income are the radar's estimates (see the Observer widget).");
        }
    }
}
