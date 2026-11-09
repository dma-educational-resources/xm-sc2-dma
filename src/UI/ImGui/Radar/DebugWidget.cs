using System.Numerics;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui;
using SC2Radar.UI.ImGui.Widgets;
using SC2Radar.UI.Misc;

namespace SC2Radar.UI.ImGui.Radar
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class DebugWidget : WidgetChrome
    {
        protected override string WidgetTitle => "Debug";
        protected override Vector2 InitSize    => new(300, 190);
        protected override bool   HasOpacityOverride => Program.Config.DebugWidgetOverrideOpacity;
        protected override float  OpacityOverride    => Program.Config.DebugWidgetAlpha;

        protected override void DrawWidgetContent()
        {
            ImGuiNET.ImGui.Indent(ContentTopGap);

            var sc2 = Memory.Instance;

            DrawKV("Connected", Memory.IsConnected ? "Yes" : "No",
                Memory.IsConnected ? LabelStyle.Success : LabelStyle.Warning);
            DrawKV("In Game", Memory.InGame ? "Yes" : "No",
                Memory.InGame ? LabelStyle.Success : LabelStyle.Normal);

            if (sc2 != null)
            {
                int shown = sc2.Snapshot.Units.Length;
                DrawKV("Actors",  $"{sc2.ActorsTracked} units/buildings of {sc2.ActorsTotal} ({sc2.ActorSource})");
                DrawKV("Mission", sc2.MissionState,
                    sc2.MissionState == "in a mission" ? LabelStyle.Success : sc2.MissionState == "unknown" ? LabelStyle.Dim : LabelStyle.Warning);
                DrawKV("Offsets", sc2.ResolveInfo, LabelStyle.Dim);
                var t = sc2.Terrain; var pm = sc2.Pathing;
                DrawKV("Match", $"{(sc2.MapName.Length > 0 ? sc2.MapName : "-")}, clock {SC2Memory.FormatClock(sc2.GameTime)}" +
                                $"{(sc2.GamePaused ? " (paused)" : "")}, terrain {(t != null ? $"{t.W}x{t.H}" : "-")}, mesh {(pm != null ? $"{pm.Triangles.Length} tris" : "-")}",
                       LabelStyle.Dim);
                DrawKV("Shown",   shown.ToString(), shown > 0 ? LabelStyle.Success : LabelStyle.Warning);
                DrawKV("Rescan",  sc2.RescanRunning ? "running..." : $"{sc2.LastRescanSec:F1}s");
            }
            else
            {
                DrawKV("Units",  "N/A", LabelStyle.Dim);
                DrawKV("Vtable", "N/A", LabelStyle.Dim);
                DrawKV("Pools",  "N/A", LabelStyle.Dim);
            }

            DrawKV("FPS", $"{ImGuiNET.ImGui.GetIO().Framerate:F0}");

            ImGuiNET.ImGui.Unindent(ContentTopGap);
        }

        private static void DrawKV(string label, string value, LabelStyle style = LabelStyle.Normal)
        {
            ImGuiNET.ImGui.TextDisabled($"{label}:");
            ImGuiNET.ImGui.SameLine();
            switch (style)
            {
                case LabelStyle.Success: ImGuiNET.ImGui.TextColored(ToV4(ImGuiStyle.Success), value); break;
                case LabelStyle.Warning: ImGuiNET.ImGui.TextColored(ToV4(ImGuiStyle.Warning), value); break;
                case LabelStyle.Dim:     ImGuiNET.ImGui.TextDisabled(value); break;
                default:                 ImGuiNET.ImGui.TextUnformatted(value); break;
            }
        }

        private static Vector4 ToV4(uint col) => new(
            (col & 0xFF) / 255f,
            ((col >> 8)  & 0xFF) / 255f,
            ((col >> 16) & 0xFF) / 255f,
            ((col >> 24) & 0xFF) / 255f);
    }
}
