using System.Numerics;
using System.Runtime.InteropServices;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// The map drawn under the units, from the game's own data (SC2Memory.Terrain / Pathing):
    ///   ground the units can walk on - shaded by the height map (low ground dark, high ground light, lit from the
    ///                                  north-west so slopes and ramps read)
    ///   blocked (structures, minerals, rocks, doodads) - the ground tone, darker
    ///   no pathing (outside the playable area, holes) and cliffs - near black
    /// The navmesh triangles are rasterised on a background thread whenever the game's mesh or height map changes
    /// (a structure placed or destroyed changes the mesh); the result is uploaded as one texture.
    /// </summary>
    public static class TerrainLayer
    {
        const int Upscale = 4;   // texels per map unit
        static Config Config => Program.Config;

        static ShaderResourceView? _srv;
        static Texture2D? _tex;
        static (int Terrain, int Pathing, int Colors) _built = (-1, -1, 0), _pending;
        static Task<Pixels?>? _job;

        internal sealed record Pixels(byte[] Data, int W, int H);

        /// <summary>The current terrain picture (RGBA, row 0 = the top / north edge, Upscale texels per map unit) and the
        /// key it was built for, for the web radar. Null when there is no terrain.</summary>
        internal static (Pixels Pixels, string Key)? BuildCurrent()
        {
            var mem = Memory.Instance;
            if (mem?.Terrain is not { } t) return null;
            var pm = mem.Pathing;
            var colors = Config.MapColors;
            var px = Build(t, pm, Rgb(colors.TerrainLow), Rgb(colors.TerrainHigh), Rgb(colors.TerrainVoid));
            return px == null ? null : (px, CurrentKey()!);
        }

        /// <summary>Changes whenever the terrain picture would: a new map, pathing or map colours.</summary>
        internal static string? CurrentKey()
        {
            var mem = Memory.Instance;
            return mem?.Terrain is { } t ? $"{t.Version}-{mem.Pathing?.Version ?? 0}-{Config.MapColors.Version}" : null;
        }

        public static void Draw(in RadarFrameCtx ctx)
        {
            var mem = Memory.Instance;
            var t = mem?.Terrain;
            if (!Config.ShowTerrain || t == null) return;
            var pm = mem!.Pathing;
            var colors = Config.MapColors;
            var key = (t.Version, pm?.Version ?? 0, colors.Version);
            if (key != _built && _job == null)
            {
                _pending = key;
                var (low, high, none) = (Rgb(colors.TerrainLow), Rgb(colors.TerrainHigh), Rgb(colors.TerrainVoid));
                _job = Task.Run(() => Build(t, pm, low, high, none));
            }
            if (_job is { IsCompleted: true })
            {
                if (_job.IsCompletedSuccessfully && _job.Result is { } px) Upload(px);
                _built = _pending;
                _job = null;
            }

            var dl = ctx.DrawList;
            var tl = ctx.WorldToScreen(0, t.H);
            var br = ctx.WorldToScreen(t.W, 0);
            if (_srv != null)
            {
                uint a = (uint)(Math.Clamp(Config.TerrainAlpha, 0f, 1f) * 255f);
                dl.AddImage(_srv.NativePointer, tl, br, Vector2.Zero, Vector2.One, (a << 24) | 0x00FFFFFF);
            }
            dl.AddRect(tl, br, Config.MapColors.MapEdge, 0f, ImGuiNET.ImDrawFlags.None, 1f);   // map edge
        }

        /// <summary>An ABGR colour as 0..1 RGB.</summary>
        static Vector3 Rgb(uint abgr) => new((abgr & 0xFF) / 255f, ((abgr >> 8) & 0xFF) / 255f, ((abgr >> 16) & 0xFF) / 255f);

        static float Sample(TerrainMap t, float x, float y) => t.Sample(x, y);

        /// <summary>Pathing type per texel (255 = no mesh: treated as ground).</summary>
        static byte[] RasterisePathing(PathingMesh? pm, int w, int h, int mapH)
        {
            var grid = new byte[w * h];
            Array.Fill(grid, (byte)255);
            if (pm == null) return grid;
            Vector2 T(Vector2 v) => new(v.X * Upscale, (mapH - v.Y) * Upscale);   // texel space, row 0 at the top
            foreach (var tri in pm.Triangles)
            {
                Vector2 a = T(pm.Vertices[tri.A]), b = T(pm.Vertices[tri.B]), c = T(pm.Vertices[tri.C]);
                float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (MathF.Abs(area) < 1e-6f) continue;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                float s = MathF.Sign(area);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        // same side of all three edges (either winding)
                        if (s * ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) < 0) continue;
                        if (s * ((c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X)) < 0) continue;
                        if (s * ((a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X)) < 0) continue;
                        grid[y * w + x] = tri.Type;
                    }
            }
            return grid;
        }

        /// <param name="low">Low ground (RGB 0..1, Settings ▸ Colors ▸ Map), <paramref name="high"/> high ground,
        /// <paramref name="none"/> cliffs, holes and outside the playable area.</param>
        static Pixels? Build(TerrainMap t, PathingMesh? pm, Vector3 low, Vector3 high, Vector3 none)
        {
            int w = t.W * Upscale, h = t.H * Upscale;
            var path = RasterisePathing(pm, w, h, t.H);
            float lo = t.Heights.Min(), hi = t.Heights.Max(), span = MathF.Max(0.5f, hi - lo);
            var light = Vector3.Normalize(new Vector3(-1f, 1f, 1.6f));   // from the north-west, map +Y is north
            var data = new byte[w * h * 4];
            const float e = 1f / Upscale;
            for (int py = 0; py < h; py++)
            {
                float my = t.H - (py + 0.5f) / Upscale;
                for (int px = 0; px < w; px++)
                {
                    float mx = (px + 0.5f) / Upscale;
                    byte type = path[py * w + px];
                    Vector3 c;
                    if (type is 0 or 3) c = none;
                    else
                    {
                        float z = Sample(t, mx, my);
                        float dzx = (Sample(t, mx + e, my) - Sample(t, mx - e, my)) / (2 * e);
                        float dzy = (Sample(t, mx, my + e) - Sample(t, mx, my - e)) / (2 * e);
                        var n = Vector3.Normalize(new Vector3(-dzx, -dzy, 1f));
                        float shade = 0.62f + 0.38f * Math.Clamp(Vector3.Dot(n, light), -1f, 1f);
                        c = Vector3.Lerp(low, high, Math.Clamp((z - lo) / span, 0f, 1f)) * shade;
                        if (pm == null && MathF.Sqrt(dzx * dzx + dzy * dzy) > 1.2f) c *= 0.55f;   // no mesh: mark steep steps
                        if (type == 2) c *= 0.55f;                                                // blocked
                    }
                    // a thin dark rim where walkable ground meets anything else
                    if (pm != null && type is 1 or 255 && px > 0 && py > 0 && px < w - 1 && py < h - 1)
                    {
                        int i = py * w + px;
                        if (path[i - 1] is 0 or 3 || path[i + 1] is 0 or 3 || path[i - w] is 0 or 3 || path[i + w] is 0 or 3) c *= 0.6f;
                    }
                    int o = (py * w + px) * 4;
                    data[o]     = (byte)Math.Clamp(c.X * 255f, 0f, 255f);
                    data[o + 1] = (byte)Math.Clamp(c.Y * 255f, 0f, 255f);
                    data[o + 2] = (byte)Math.Clamp(c.Z * 255f, 0f, 255f);
                    data[o + 3] = 255;
                }
            }
            return new Pixels(data, w, h);
        }

        static void Upload(Pixels p)
        {
            var device = ImGuiApp.RadarHwndDevice;
            if (device == null) return;
            Release();
            var handle = GCHandle.Alloc(p.Data, GCHandleType.Pinned);
            try
            {
                _tex = new Texture2D(device, new Texture2DDescription
                {
                    Width = p.W, Height = p.H, MipLevels = 0, ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CpuAccessFlags = CpuAccessFlags.None, OptionFlags = ResourceOptionFlags.GenerateMipMaps,
                });
                _srv = new ShaderResourceView(device, _tex);
                device.ImmediateContext.UpdateSubresource(new SharpDX.DataBox(handle.AddrOfPinnedObject(), p.W * 4, 0), _tex, 0);
                device.ImmediateContext.GenerateMips(_srv);
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
