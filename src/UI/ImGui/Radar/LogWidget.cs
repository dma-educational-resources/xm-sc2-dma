using System.Collections.Concurrent;
using System.Numerics;
using ImGuiNET;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Everything logged this session (XMLogging - the console's lines and the file-only ones), newest at the bottom,
    /// with a filter box. Seeded from XMLogging's backlog, then fed by its OnLine event; lines arrive on any thread and
    /// are moved into the list on the UI thread. Only the visible lines are drawn.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class LogWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Log";
        protected override Vector2 InitSize    => new(520, 260);
        protected override bool    HasOpacityOverride => Program.Config.LogWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.LogWidgetAlpha;
        protected override bool    HasPanelMenu => true;
        protected override ImGuiWindowFlags ChromeScrollExtraFlags => ImGuiWindowFlags.NoScrollbar;

        const int MaxLines = 3000;
        const uint ErrCol = 0xFF6060E8, WarnCol = 0xFF40C8E8;   // ABGR

        readonly ConcurrentQueue<string> _incoming = new();
        readonly List<string> _lines = [];
        List<string>? _filtered;   // null: no filter
        string _filter = "", _filterFor = "";
        int _linesAtFilter = -1;
        bool _autoScroll = true, _problemsOnly;

        int _incomingCount;   // lines waiting in _incoming (the widget may be closed for hours: keep it bounded)

        public LogWidget()
        {
            _lines.AddRange(XMLogging.SnapshotBuffer());
            XMLogging.OnLine += line =>
            {
                _incoming.Enqueue(line);
                if (Interlocked.Increment(ref _incomingCount) > MaxLines && _incoming.TryDequeue(out _))
                    Interlocked.Decrement(ref _incomingCount);
            };
        }

        protected override void DrawPanelMenu()
        {
            if (UI.MenuItem("Auto-scroll", selected: _autoScroll, hint: "Follow new lines while scrolled to the bottom."))
                _autoScroll = !_autoScroll;
            if (UI.MenuItem("Errors and warnings only", selected: _problemsOnly))
            { _problemsOnly = !_problemsOnly; _linesAtFilter = -1; }
            if (UI.MenuItem("Copy shown lines"))
                ImGuiNET.ImGui.SetClipboardText(string.Join(Environment.NewLine, _filtered ?? _lines));
            if (UI.MenuItem("Clear", hint: "Clears this view only - the log file keeps everything."))
            { _lines.Clear(); _linesAtFilter = -1; }
        }

        static bool IsError(string l)   => l.Contains("ERROR") || l.Contains("FAIL");
        static bool IsWarning(string l) => l.Contains("WARN");

        protected override void DrawWidgetContent()
        {
            int added = 0;
            while (_incoming.TryDequeue(out var l)) { Interlocked.Decrement(ref _incomingCount); _lines.Add(l); added++; }
            if (_lines.Count > MaxLines) { _lines.RemoveRange(0, _lines.Count - MaxLines); _linesAtFilter = -1; }

            UI.InputText("##log_filter", ref _filter, 128, placeholder: "Filter", width: ImGuiNET.ImGui.GetContentRegionAvail().X);

            // filter again only when the text, the lines or the errors-only switch changed
            bool filtering = _filter.Length > 0 || _problemsOnly;
            if (!filtering) _filtered = null;
            else if (_filter != _filterFor || _lines.Count != _linesAtFilter)
            {
                _filtered = [.. _lines.Where(l => (!_problemsOnly || IsError(l) || IsWarning(l)) &&
                                                  (_filter.Length == 0 || l.Contains(_filter, StringComparison.OrdinalIgnoreCase)))];
                (_filterFor, _linesAtFilter) = (_filter, _lines.Count);
            }
            var shown = _filtered ?? _lines;

            ImGuiNET.ImGui.BeginChild("##log_lines", new Vector2(0f, 0f), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
            bool atBottom = ImGuiNET.ImGui.GetScrollY() >= ImGuiNET.ImGui.GetScrollMaxY() - 2f;
            if (shown.Count == 0) ImGuiNET.ImGui.TextDisabled(filtering ? "No matching lines." : "Nothing logged yet.");

            // only the lines in view are drawn; the rest is spacing
            float lh = ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
            int first = Math.Clamp((int)(ImGuiNET.ImGui.GetScrollY() / lh), 0, shown.Count);
            int last  = Math.Min(shown.Count, first + (int)(ImGuiNET.ImGui.GetWindowHeight() / lh) + 2);
            if (first > 0) ImGuiNET.ImGui.Dummy(new Vector2(1f, first * lh - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y));
            for (int i = first; i < last; i++)
            {
                var l = shown[i];
                if (IsError(l)) ImGuiNET.ImGui.TextColored(UI.ToV4(ErrCol), l);
                else if (IsWarning(l)) ImGuiNET.ImGui.TextColored(UI.ToV4(WarnCol), l);
                else ImGuiNET.ImGui.TextUnformatted(l);
            }
            if (last < shown.Count) ImGuiNET.ImGui.Dummy(new Vector2(1f, (shown.Count - last) * lh));

            if (_autoScroll && added > 0 && atBottom) ImGuiNET.ImGui.SetScrollHereY(1f);
            ImGuiNET.ImGui.EndChild();
        }
    }
}
