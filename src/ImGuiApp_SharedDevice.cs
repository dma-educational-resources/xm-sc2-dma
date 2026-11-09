// ImGuiApp_SharedDevice.cs
// Exposes the radar window's DX11 device for MapTextureCache.

using SharpDX.Direct3D11;

namespace SC2Radar.UI.ImGui
{
    public static partial class ImGuiApp
    {
        /// <summary>Radar window DX11 Device. Available after Initialized == true.</summary>
        public static Device? RadarHwndDevice => Initialized ? _radarWindow?.Device : null;
    }
}
