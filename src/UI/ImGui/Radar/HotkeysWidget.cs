using System.Numerics;
using SC2Radar.Common.Misc.Data;
using SC2Radar.SC2;
using SC2Radar.SC2.Features;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// What's live at a glance: the memory-writes master switch and every write that is on, then each bound hotkey with
    /// its key - in green while the feature it controls is on (HotkeyManager.GetActionEnabledState), as the EFT radar's
    /// hotkey info. Rows like the Match widget's; sized around its content.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class HotkeysWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Hotkeys";
        protected override Vector2 InitSize    => new(260, 180);
        protected override bool    HasOpacityOverride => Program.Config.HotkeysWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.HotkeysWidgetAlpha;
        protected override bool    AutoSize     => true;

        const uint OnCol = 0xFF60D060, OffCol = 0xFF9A9A9A;   // ABGR
        float _right, _keyX;

        void Track() => _right = MathF.Max(_right, ImGuiNET.ImGui.GetItemRectMax().X - ImGuiNET.ImGui.GetWindowPos().X);

        protected override void DrawWidgetContent()
        {
            _right = 0f;
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 2f);
            ImGuiNET.ImGui.Indent(Pad);
            try { DrawRows(); }
            finally { ImGuiNET.ImGui.Unindent(Pad); }
            ReportContentSize(new Vector2(_right + Pad, ImGuiNET.ImGui.GetCursorPosY()));
        }

        void Row(string name, string key, bool? on)
        {
            if (on is bool b) ImGuiNET.ImGui.TextColored(UI.ToV4(b ? OnCol : OffCol), name);
            else ImGuiNET.ImGui.TextUnformatted(name);
            Track();
            if (key.Length == 0) return;
            ImGuiNET.ImGui.SameLine(_keyX);
            ImGuiNET.ImGui.TextDisabled(key);
            Track();
        }

        // the keys list plus the older single Key, as HotkeyManager reads them
        static IEnumerable<int> Keys(HotkeyEntry e) => (e.Keys ?? []).Append(e.Key).Where(k => k > 0).Distinct();

        void DrawRows()
        {
            var c = Program.Config;
            var bound = SC2Hotkeys.Actions
                .Select(a => (a.Key, a.Name, Entry: c.HotKeys.Get(a.Key)))
                .Where(a => a.Entry is { Enabled: true } e && Keys(e).Any())
                .ToList();
            var writes = c.MemWritesEnabled ? FeatureManager.All.Where(f => f.Enabled).ToList() : [];
            float nameW = bound.Select(a => ImGuiNET.ImGui.CalcTextSize(a.Name).X).DefaultIfEmpty(0f).Max();
            _keyX = Pad + MathF.Max(110f, nameW + 16f);

            // memory writes: the master switch, then whatever is on
            Row(c.MemWritesEnabled ? "Memory writes on" : "Memory writes off", "", c.MemWritesEnabled ? (bool?)true : false);
            foreach (var f in writes) Row("  " + f.Name, "", true);

            ImGuiNET.ImGui.Dummy(new Vector2(1f, 4f));
            if (bound.Count == 0)
            {
                ImGuiNET.ImGui.TextDisabled("No hotkeys bound (Settings ▸ Hotkeys).");
                Track();
                return;
            }
            foreach (var (key, name, entry) in bound)
                Row(name, string.Join(" / ", Keys(entry!).Select(UI.KeyBadgeLabel)),
                    HotkeyManager.GetActionEnabledState(key));
        }
    }
}
