// PNG icon texture cache for dl.AddImage() use in ImGui.
// Loads embedded PNGs from UI/Resources/{stem}.png with mipmap generation for clean downscaling.
// To add a new icon: drop {stem}.png into src/UI/Resources/, add a stem entry to Icons[] and a
// matching value to the Icon enum - the csproj wildcard embeds it automatically.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SkiaSharp;

namespace SC2Radar.UI.ImGui
{
    public static class IconCache
    {
        public enum Icon
        {
            Bell       = 0,
            Trash      = 1,
            Ban        = 2,
            Note       = 3,
            Plus       = 4,
            Minus      = 5,
            Sync       = 6,
            Settings   = 7,
            Refresh    = 8,
            DollarSign = 9,
            Filter     = 10,
            PlayerPill = 11,
            WandMagic  = 12,
            Users      = 13,
            History    = 14,
            ArrowsToCircle    = 15,
            Share      = 16,
            UpArrow    = 17,
            DownArrow  = 18,
            Send       = 19,
            Copy       = 20,
            Save       = 21,
            Orientation = 22,
        }

        // Embedded-resource stem for each icon → UI/Resources/{stem}.png.
        private static readonly string[] Icons =
        {
            "bell",        // Bell
            "bin",         // Trash
            "blacklist",   // Ban
            "note",        // Note
            "plus",        // Plus
            "minus",       // Minus
            "sync",        // Sync
            "gear",        // Settings
            "refresh",     // Refresh
            "dollar-sign", // DollarSign
            "filter",      // Filter
            "player-pill", // PlayerPill
            "wand-magic",  // WandMagic
            "users",       // Users
            "history",     // History
            "arrows-to-circle",     // Arrows To Circle
            "share",       // Share
            "up-arrow",    // Up Arrow
            "down-arrow",  // Down Arrow
            "send",        // Send
            "copy",        // Copy
            "save",        // Save
            "orientation", // Orientation
        };

        private static readonly int      _count  = Icons.Length;
        private static readonly IntPtr[] _tex    = new IntPtr[_count];
        private static readonly bool[]   _loaded = new bool[_count];
        private static readonly List<(ShaderResourceView Srv, Texture2D Tex)> _owned = new();

        private static Assembly? _asm;

        /// <summary>
        /// Wraps a PNG icon as a UI.Button(icon:)-compatible delegate, so label+icon combo
        /// buttons can use an IconCache PNG the same way they use the vector Icons.* glyphs.
        /// The texture is pre-normalised to straight-alpha white (see LoadPng), so tinting by
        /// the button's already-animated icon colour reproduces the same hover/press behaviour
        /// as a vector icon gets.
        /// </summary>
        public static Action<ImDrawListPtr, Vector2, float, uint> AsButtonIcon(Icon icon) =>
            (dl, c, sz, col) =>
            {
                var tex = Get(icon);
                if (tex == IntPtr.Zero) return;
                float half = sz * 0.5f;
                dl.AddImage(tex, new Vector2(c.X - half, c.Y - half), new Vector2(c.X + half, c.Y + half),
                    Vector2.Zero, Vector2.One, col);
            };

        public static IntPtr Get(Icon icon)
        {
            int i = (int)icon;
            if (!_loaded[i])
            {
                var ptr = LoadPng(i);
                if (ptr != IntPtr.Zero || ImGuiApp.RadarHwndDevice != null)
                {
                    _tex[i]    = ptr;
                    _loaded[i] = true;
                }
            }
            return _tex[i];
        }

        private static IntPtr LoadPng(int idx)
        {
            string stem = Icons[idx];

            var device = ImGuiApp.RadarHwndDevice;
            if (device == null) return IntPtr.Zero;

            try
            {
                _asm ??= Assembly.GetExecutingAssembly();
                using var stream = _asm.GetManifestResourceStream($"SC2Radar.UI.Resources.{stem}.png");
                if (stream == null) return IntPtr.Zero;

                var bytes = new byte[stream.Length];
                _ = stream.Read(bytes, 0, bytes.Length);

                using var bmp = SKBitmap.Decode(bytes);
                if (bmp == null) return IntPtr.Zero;

                int w = bmp.Width, h = bmp.Height;
                bool hasAlpha = bmp.AlphaType != SKAlphaType.Opaque;

                // Normalise to straight-alpha white-on-transparent in one pass.
                // Premul stores white-at-opacity-A as (A,A,A,A); ImGui expects (255,255,255,A).
                // For opaque PNGs (no alpha) derive alpha from inverted luminance (dark = opaque).
                var data = new byte[w * h * 4];
                Marshal.Copy(bmp.GetPixels(), data, 0, data.Length);

                for (int i = 0; i < w * h; i++)
                {
                    int p = i * 4;
                    byte a;
                    if (hasAlpha)
                    {
                        a = data[p + 3];
                    }
                    else
                    {
                        float luma = (0.299f * data[p] + 0.587f * data[p + 1] + 0.114f * data[p + 2]) / 255f;
                        a = (byte)MathF.Round(MathF.Max(0f, 1f - luma) * 255f);
                    }
                    data[p]     = 255;
                    data[p + 1] = 255;
                    data[p + 2] = 255;
                    data[p + 3] = a;
                }

                return CreateTexture(data, w, h);
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// An ImGui texture (shader resource view, mipmapped) from straight-alpha RGBA pixels, owned and released with the
        /// icons; zero before the radar's device exists. Also used for the unit type icons (UnitIcons).
        /// </summary>
        internal static IntPtr CreateTexture(byte[] rgba, int w, int h)
        {
            var device = ImGuiApp.RadarHwndDevice;
            if (device == null || w <= 0 || h <= 0 || rgba.Length < w * h * 4) return IntPtr.Zero;
            var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                var tex = new Texture2D(device, new Texture2DDescription
                {
                    Width             = w,
                    Height            = h,
                    MipLevels         = 0,
                    ArraySize         = 1,
                    Format            = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage             = ResourceUsage.Default,
                    BindFlags         = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CpuAccessFlags    = CpuAccessFlags.None,
                    OptionFlags       = ResourceOptionFlags.GenerateMipMaps
                });
                var srv = new ShaderResourceView(device, tex);
                device.ImmediateContext.UpdateSubresource(
                    new SharpDX.DataBox(handle.AddrOfPinnedObject(), w * 4, 0), tex, 0);
                device.ImmediateContext.GenerateMips(srv);
                _owned.Add((srv, tex));
                return srv.NativePointer;
            }
            catch { return IntPtr.Zero; }
            finally { handle.Free(); }
        }

        public static void Dispose()
        {
            foreach (var (srv, tex) in _owned) { srv.Dispose(); tex.Dispose(); }
            _owned.Clear();
            for (int i = 0; i < _count; i++) { _tex[i] = IntPtr.Zero; _loaded[i] = false; }
        }
    }
}
