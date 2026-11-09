// SC2 minimal SkiaSharp paint set.
// Entity dots are drawn via ImGui draw list using ImGuiStyle colors;
// these SK paints are for canvas-layer text and overlay work.

using SkiaSharp;

namespace SC2Radar.UI.Misc
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static class SKPaints
    {
        // ── Unit dot paints (filled circles on radar canvas) ──────────────────
        public static readonly SKPaint PaintSelf    = new() { Color = SKColors.White,     IsAntialias = true, Style = SKPaintStyle.Fill };
        public static readonly SKPaint PaintAlly    = new() { Color = SKColors.LimeGreen, IsAntialias = true, Style = SKPaintStyle.Fill };
        public static readonly SKPaint PaintEnemy   = new() { Color = SKColors.Red,       IsAntialias = true, Style = SKPaintStyle.Fill };
        public static readonly SKPaint PaintNeutral = new() { Color = SKColors.Gray,      IsAntialias = true, Style = SKPaintStyle.Fill };
        public static readonly SKPaint PaintWorker  = new() { Color = SKColors.Yellow,    IsAntialias = true, Style = SKPaintStyle.Fill };
        public static readonly SKPaint PaintBuilding = new() { Color = SKColors.Cyan,     IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };

        // ── Text paints ───────────────────────────────────────────────────────
        public static readonly SKPaint TextSelf    = new() { Color = SKColors.White,     TextSize = 12, IsAntialias = true };
        public static readonly SKPaint TextAlly    = new() { Color = SKColors.LimeGreen, TextSize = 12, IsAntialias = true };
        public static readonly SKPaint TextEnemy   = new() { Color = SKColors.Red,       TextSize = 12, IsAntialias = true };
        public static readonly SKPaint TextNeutral = new() { Color = SKColors.Gray,      TextSize = 12, IsAntialias = true };

        // ── Map utility ───────────────────────────────────────────────────────
        public static readonly SKPaint PaintBitmap = new() { IsAntialias = true };

        // ── Connector / outline ───────────────────────────────────────────────
        public static readonly SKPaint PaintConnector = new()
        {
            Color = new SKColor(0, 255, 0, 100),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        };

        // ── Outline stroke (reused for highlighted entities) ──────────────────
        public static readonly SKPaint PaintOutline = new()
        {
            Color = SKColors.Black,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f
        };
    }
}
