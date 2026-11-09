using System.Numerics;
using System.Runtime.InteropServices;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// The creep spread (SC2Memory.Creep) over the terrain, in the minimap's purple: one texel per map cell, uploaded
    /// whenever the grid changes and drawn with linear filtering so its edges come out soft like the game's.
    /// </summary>
    public static class CreepLayer
    {
        static Config Config => Program.Config;

        static ShaderResourceView? _srv;
        static Texture2D? _tex;
        static int _built = -1;

        public static void Draw(in RadarFrameCtx ctx)
        {
            var creep = Memory.Instance?.Creep;
            if (!Config.ShowCreep || creep == null) return;
            if (creep.Version != _built) { Upload(creep); _built = creep.Version; }
            if (_srv == null) return;
            // the texture is a white mask: the colour (Settings ▸ Colors ▸ Map) is the tint, so changing it costs nothing
            uint a = (uint)(Math.Clamp(Config.CreepAlpha, 0f, 1f) * 255f);
            ctx.DrawList.AddImage(_srv.NativePointer, ctx.WorldToScreen(0, creep.H), ctx.WorldToScreen(creep.W, 0),
                                  Vector2.Zero, Vector2.One, (a << 24) | (Config.MapColors.Creep & 0x00FFFFFF));
        }

        static void Upload(CreepMap c)
        {
            var device = ImGuiApp.RadarHwndDevice;
            if (device == null) return;
            // texture row 0 is the top of the map (y = H - 1)
            var data = new byte[c.W * c.H * 4];
            for (int y = 0; y < c.H; y++)
                for (int x = 0; x < c.W; x++)
                {
                    if (!c.Cells[y * c.W + x]) continue;
                    int o = ((c.H - 1 - y) * c.W + x) * 4;
                    (data[o], data[o + 1], data[o + 2], data[o + 3]) = (255, 255, 255, 255);
                }
            Release();
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _tex = new Texture2D(device, new Texture2DDescription
                {
                    Width = c.W, Height = c.H, MipLevels = 1, ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource, CpuAccessFlags = CpuAccessFlags.None,
                }, new SharpDX.DataRectangle(handle.AddrOfPinnedObject(), c.W * 4));
                _srv = new ShaderResourceView(device, _tex);
            }
            catch { Release(); }
            finally { handle.Free(); }
        }

        static void Release()
        {
            _srv?.Dispose(); _srv = null;
            _tex?.Dispose(); _tex = null;
        }
    }
}
