// All panels have been split into individual files under src/UI/ImGui/Panels/.
// This file only retains the ImGuiStyle partial extension that panels depend on.

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static partial class ImGuiStyle
    {
        public static readonly System.Numerics.Vector4 V4Accent =
            new((Accent & 0xFF) / 255f,
                ((Accent >> 8) & 0xFF) / 255f,
                ((Accent >> 16) & 0xFF) / 255f, 1f);
    }
}
