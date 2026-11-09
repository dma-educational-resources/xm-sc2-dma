using System.IO;
using System.Numerics;
using ImGuiNET;
using SC2Radar.SC2;
using SkiaSharp;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// A small square icon per unit type for the widgets (Unit Counts, Production). By default a generated badge: the
    /// race's colour with the type's initials ("ST" Siege Tank, "Mar" Marine). A PNG in the icons folder named after the
    /// game's type id (icons\SiegeTank.png, icons\Zergling.png) replaces the badge for that type - the game's own icons
    /// aren't readable from memory, so any picture set can be dropped in. The folder is re-checked every few seconds.
    /// </summary>
    public static class UnitIcons
    {
        public static string Folder => Path.Combine(Program.DataDir, "icons");

        static readonly Dictionary<string, IntPtr> _textures = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> _initials = new(StringComparer.OrdinalIgnoreCase);
        static DateTime _folderStamp, _nextCheck;

        /// <summary>The PNG for a type, zero when there is none (or it couldn't be loaded).</summary>
        static IntPtr Texture(string type)
        {
            if (type.Length == 0) return IntPtr.Zero;
            if (DateTime.UtcNow >= _nextCheck)
            {
                // a file added or replaced: load everything again on demand
                _nextCheck = DateTime.UtcNow.AddSeconds(5);
                var stamp = Directory.Exists(Folder) ? Directory.GetLastWriteTimeUtc(Folder) : DateTime.MinValue;
                if (stamp != _folderStamp) { _folderStamp = stamp; _textures.Clear(); }
            }
            if (_textures.TryGetValue(type, out var t)) return t;
            t = Load(Path.Combine(Folder, type + ".png"));
            if (t != IntPtr.Zero || ImGuiApp.RadarHwndDevice != null) _textures[type] = t;   // no device yet: try again later
            return t;
        }

        static IntPtr Load(string path)
        {
            if (!File.Exists(path)) return IntPtr.Zero;
            try
            {
                using var bmp = SKBitmap.Decode(path);
                if (bmp == null) return IntPtr.Zero;
                // straight-alpha RGBA as ImGui expects, scaled down to at most 128 px (mipmaps handle the rest)
                int w = bmp.Width, h = bmp.Height;
                float k = MathF.Min(1f, 128f / MathF.Max(w, h));
                var info = new SKImageInfo(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)), SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using var scaled = bmp.Resize(info, SKFilterQuality.High);
                if (scaled == null) return IntPtr.Zero;
                var data = new byte[info.Width * info.Height * 4];
                System.Runtime.InteropServices.Marshal.Copy(scaled.GetPixels(), data, 0, data.Length);
                return IconCache.CreateTexture(data, info.Width, info.Height);
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>"Siege Tank" -> "ST", "Marine" -> "Mar", "Planetary Fortress" -> "PF" (at most three letters).</summary>
        public static string Initials(string displayName)
        {
            if (_initials.TryGetValue(displayName, out var s)) return s;
            var words = displayName.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToArray();
            s = words.Length switch
            {
                0 => "?",
                1 => words[0].Length <= 3 ? words[0] : words[0][..3],
                _ => string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0]))),
            };
            return _initials[displayName] = s;
        }

        // ABGR, dark enough for white text: Terran steel blue, Protoss gold, Zerg purple, anything else grey
        static uint RaceColor(Faction f) => f switch
        {
            Faction.Terran => 0xFF8A5A3A, Faction.Protoss => 0xFF2A8AB0, Faction.Zerg => 0xFF7A3A7A, _ => 0xFF5A5A5A,
        };

        /// <summary>
        /// Draws the icon for a type at <paramref name="tl"/>, <paramref name="size"/> pixels square: its PNG if there is
        /// one, else the race-coloured badge with its initials. <paramref name="ring"/> (player colour, 0 = none) outlines it.
        /// </summary>
        public static void Draw(ImDrawListPtr dl, Vector2 tl, float size, string type, string displayName, Faction faction, uint ring = 0)
        {
            var br = tl + new Vector2(size);
            float round = MathF.Max(2f, size * 0.18f);
            if (Texture(type) is var tex && tex != IntPtr.Zero)
                dl.AddImageRounded(tex, tl, br, Vector2.Zero, Vector2.One, 0xFFFFFFFF, round);
            else
            {
                uint bg = RaceColor(faction);
                dl.AddRectFilled(tl, br, bg, round);
                string text = Initials(displayName);
                var font = ImGuiStyle.FontNormal;
                float fs = size * (text.Length >= 3 ? 0.48f : 0.58f);
                var ts = font.CalcTextSizeA(fs, float.MaxValue, -1f, text);
                dl.AddText(font, fs, tl + (new Vector2(size) - ts) * 0.5f, 0xF0FFFFFF, text);
            }
            if (ring != 0) dl.AddRect(tl, br, (ring & 0x00FFFFFF) | 0xE0000000, round, ImDrawFlags.None, 1.5f);
        }
    }
}
