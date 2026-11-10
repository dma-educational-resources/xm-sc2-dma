// Exposes the radar window's DX11 device to callers that need to upload textures.

using SharpDX.Direct3D11;

namespace SC2Radar.UI.ImGui
{
    public static partial class ImGuiApp
    {
        /// <summary>Radar window DX11 Device. Available after Initialized == true.</summary>
        public static Device? RadarHwndDevice => Initialized ? _radarWindow?.Device : null;
    }
}
