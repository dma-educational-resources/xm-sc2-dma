using SkiaSharp;
using System.Collections.Generic;
using SC2Radar.UI.Misc;
using SC2Radar.UI.ImGui;
using SC2Radar.SC2;

namespace SC2Radar.UI
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public enum RadarColorOption
    {
        // SC2 unit colors
        LocalUnit,
        AllyUnit,
        EnemyUnit,
        NeutralUnit,
        WorkerUnit,
        Building,
        // Future expansion
        Unknown,
    }

    internal static class RadarColorOptions
    {
        internal static void LoadColors(Config config)
        {
            config.Colors ??= new Dictionary<RadarColorOption, string>();

            foreach (var def in GetDefaultColors())
                config.Colors.TryAdd(def.Key, def.Value);

            SetColors(config.Colors);
        }

        internal static Dictionary<RadarColorOption, string> GetDefaultColors() =>
            new()
            {
                [RadarColorOption.LocalUnit]    = "#FFFFFF",
                [RadarColorOption.AllyUnit]     = "#00FF00",
                [RadarColorOption.EnemyUnit]    = "#FF4444",
                [RadarColorOption.NeutralUnit]  = "#AAAAAA",
                [RadarColorOption.WorkerUnit]   = "#FFE000",
                [RadarColorOption.Building]     = "#4488FF",
                [RadarColorOption.Unknown]      = "#888888",
            };

        internal static void SetColors(Dictionary<RadarColorOption, string> colors)
        {
            SetPaint(RadarColorOption.LocalUnit,   colors, SKPaints.PaintSelf,     SKPaints.TextSelf);
            SetPaint(RadarColorOption.AllyUnit,    colors, SKPaints.PaintAlly,     SKPaints.TextAlly);
            SetPaint(RadarColorOption.EnemyUnit,   colors, SKPaints.PaintEnemy,    SKPaints.TextEnemy);
            SetPaint(RadarColorOption.NeutralUnit, colors, SKPaints.PaintNeutral,  SKPaints.TextNeutral);
            SetPaint(RadarColorOption.WorkerUnit,  colors, SKPaints.PaintWorker,   null);
            SetPaint(RadarColorOption.Building,    colors, SKPaints.PaintBuilding, null);
        }

        internal static void ResetToDefaults(Config config)
        {
            config.Colors = GetDefaultColors();
            SetColors(config.Colors);
            config.SaveDebounced();
        }

        private static void SetPaint(RadarColorOption opt,
            Dictionary<RadarColorOption, string> colors,
            SKPaint? fill, SKPaint? text)
        {
            if (!colors.TryGetValue(opt, out var hex)) return;
            if (!TryParseHex(hex, out var color)) return;
            if (fill != null) fill.Color = color;
            if (text != null) text.Color = color;
        }

        private static bool TryParseHex(string hex, out SKColor color)
        {
            color = SKColors.White;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            hex = hex.TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            if (hex.Length != 8) return false;
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
            byte a = (byte)(v >> 24), r = (byte)(v >> 16), g = (byte)(v >> 8), b = (byte)v;
            color = new SKColor(r, g, b, a);
            return true;
        }
    }
}
