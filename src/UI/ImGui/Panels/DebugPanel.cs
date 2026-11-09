using System.Numerics;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI;
using SC2Radar.UI.ImGui.Panels;

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class DebugPanel : ChromePanel
    {
        protected override Vector2 InitSize => new(380f, 480f);
        protected override string PanelTitle => "Debug";

        public DebugPanel() { }

        protected override void DrawPanelContent()
        {
            DrawConnectionGroup();
            DrawMemoryGroup();
            DrawPerformanceGroup();
        }

        private static void DrawConnectionGroup()
        {
            float lw = UI.KVLabelWidth("Connected", "In Game");
            UI.BeginGroup("Connection");
            DrawKV("Connected", Memory.IsConnected ? "Yes" : "No", lw,
                Memory.IsConnected ? LabelStyle.Success : LabelStyle.Warning);
            DrawKV("In Game", Memory.InGame ? "Yes" : "No", lw,
                Memory.InGame ? LabelStyle.Success : LabelStyle.Normal);
            UI.EndGroup();
        }

        private static void DrawMemoryGroup()
        {
            var sc2 = Memory.Instance;
            if (sc2 == null)
            {
                UI.BeginGroup("Memory");
                DrawKV("Status", "Not initialized", 0f, LabelStyle.Dim);
                UI.EndGroup();
                return;
            }

            float lw = UI.KVLabelWidth("Unit Count");
            UI.BeginGroup("Memory");
            DrawKV("Unit Count", sc2.Snapshot.Units.Length.ToString(), lw);
            UI.EndGroup();
        }

        private static void DrawPerformanceGroup()
        {
            float lw = UI.KVLabelWidth("FPS", "Frame Time");
            UI.BeginGroup("Performance");
            DrawKV("FPS",        $"{ImGuiNET.ImGui.GetIO().Framerate:F0}",              lw);
            DrawKV("Frame Time", $"{ImGuiNET.ImGui.GetIO().DeltaTime * 1000f:F1} ms",   lw);
            UI.EndGroup();
        }

        private static void DrawKV(string key, string value, float lw, LabelStyle style = LabelStyle.Normal)
        {
            float colW = UI.ColWidth > 0f ? UI.ColWidth : 0f;
            UI.KVRow(key, value, colW, lw, style);
        }
    }
}
