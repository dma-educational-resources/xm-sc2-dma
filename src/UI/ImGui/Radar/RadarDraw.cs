// SC2 radar drawing helpers - coordinate transforms and ImGui draw-list primitives.

using System.Numerics;
using ImGuiNET;
using SC2Radar.UI.ImGui;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Per-frame context passed to all SC2 entity draw methods.
    /// </summary>
    public readonly struct RadarFrameCtx
    {
        public readonly Vector2   CanvasPos;    // top-left of radar ImGui window
        public readonly Vector2   CanvasSize;   // size of radar canvas
        public readonly Vector2   MapCenter;    // world origin projected to canvas center
        public readonly float     Scale;        // pixels per world unit
        public readonly ImDrawListPtr DrawList;

        public RadarFrameCtx(Vector2 canvasPos, Vector2 canvasSize, Vector2 mapCenter,
                              float scale, ImDrawListPtr dl)
        {
            CanvasPos  = canvasPos;
            CanvasSize = canvasSize;
            MapCenter  = mapCenter;
            Scale      = scale;
            DrawList   = dl;
        }

        /// <summary>Project a world (X, Y) position to screen pixels.</summary>
        public Vector2 WorldToScreen(float wx, float wy)
        {
            return new Vector2(
                CanvasPos.X + CanvasSize.X * 0.5f + (wx - MapCenter.X) * Scale,
                CanvasPos.Y + CanvasSize.Y * 0.5f - (wy - MapCenter.Y) * Scale);
        }
    }

    /// <summary>
    /// Static drawing helpers for the SC2 radar canvas.
    /// All methods operate on an <see cref="ImDrawListPtr"/> so they run inside the
    /// ImGui radar window without an extra SkiaSharp layer.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static class RadarDraw
    {
        // ── Circle dot ────────────────────────────────────────────────────────

        public static void DrawCircleDot(ImDrawListPtr dl, Vector2 pos, float radius, uint col)
        {
            dl.AddCircleFilled(pos, radius, col);
            dl.AddCircle(pos, radius + 0.5f, ImGuiStyle.WithAlpha(col, 120), 0, 1f);
        }

        // ── Square dot ────────────────────────────────────────────────────────

        public static void DrawSquareDot(ImDrawListPtr dl, Vector2 pos, float halfSize, uint col)
        {
            dl.AddRectFilled(
                new Vector2(pos.X - halfSize, pos.Y - halfSize),
                new Vector2(pos.X + halfSize, pos.Y + halfSize),
                col);
        }

        // ── Diamond dot ───────────────────────────────────────────────────────

        public static void DrawDiamondDot(ImDrawListPtr dl, Vector2 pos, float halfSize, uint col)
        {
            dl.AddQuadFilled(
                new Vector2(pos.X,            pos.Y - halfSize),
                new Vector2(pos.X + halfSize, pos.Y),
                new Vector2(pos.X,            pos.Y + halfSize),
                new Vector2(pos.X - halfSize, pos.Y),
                col);
        }

        // ── Text label ────────────────────────────────────────────────────────

        public static void DrawLabel(ImDrawListPtr dl, Vector2 pos, string text, uint col, float offsetY = 0f)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sz = ImGuiNET.ImGui.CalcTextSize(text);
            var tp = new Vector2(pos.X - sz.X * 0.5f, pos.Y + offsetY);
            dl.AddText(new Vector2(tp.X + 1, tp.Y + 1), 0xFF000000u, text);
            dl.AddText(tp, col, text);
        }

        // ── SC2 unit draw methods ─────────────────────────────────────────────

        /// <summary>Draw a SC2 unit as a colored dot.</summary>
        public static void DrawUnit(in RadarFrameCtx ctx, float wx, float wy,
            float radius, uint col, string? label = null)
        {
            var dl  = ctx.DrawList;
            var pos = ctx.WorldToScreen(wx, wy);

            DrawCircleDot(dl, pos, radius, col);

            if (label != null)
                DrawLabel(dl, pos, label, col, radius + 2f);
        }

        /// <summary>Draw a building as a small square with outline.</summary>
        public static void DrawBuilding(in RadarFrameCtx ctx, float wx, float wy,
            float halfSize, uint col)
        {
            var dl  = ctx.DrawList;
            var pos = ctx.WorldToScreen(wx, wy);

            dl.AddRectFilled(
                new Vector2(pos.X - halfSize, pos.Y - halfSize),
                new Vector2(pos.X + halfSize, pos.Y + halfSize),
                col & 0x88FFFFFFu);
            dl.AddRect(
                new Vector2(pos.X - halfSize, pos.Y - halfSize),
                new Vector2(pos.X + halfSize, pos.Y + halfSize),
                col, 0, ImDrawFlags.None, 1.5f);
        }

        // ── colours: the radar keeps ABGR, the colour editor RGBA floats ─────

        public static Vector4 AbgrToRgba(uint abgr) =>
            new((abgr & 0xFF) / 255f, ((abgr >> 8) & 0xFF) / 255f, ((abgr >> 16) & 0xFF) / 255f, (abgr >> 24) / 255f);

        public static uint RgbaToAbgr(Vector4 c) =>
            (uint)(Math.Clamp(c.W, 0f, 1f) * 255f) << 24 | (uint)(Math.Clamp(c.Z, 0f, 1f) * 255f) << 16 |
            (uint)(Math.Clamp(c.Y, 0f, 1f) * 255f) << 8 | (uint)(Math.Clamp(c.X, 0f, 1f) * 255f);

        // ── Styled markers (Entity Info) ─────────────────────────────────────

        static uint Darken(uint abgr, float f)
        {
            uint r = (uint)((abgr & 0xFF) * f), g = (uint)(((abgr >> 8) & 0xFF) * f), b = (uint)(((abgr >> 16) & 0xFF) * f);
            return (abgr & 0xFF000000) | (b << 16) | (g << 8) | r;
        }

        static void ShapePath(ImDrawListPtr dl, Vector2 p, float r, SC2.MarkerShape shape, uint col, bool filled, float thick = 1f)
        {
            switch (shape)
            {
                case SC2.MarkerShape.Square:
                    if (filled) dl.AddRectFilled(p - new Vector2(r, r), p + new Vector2(r, r), col, r * 0.3f);
                    else dl.AddRect(p - new Vector2(r, r), p + new Vector2(r, r), col, r * 0.3f, ImDrawFlags.None, thick);
                    break;
                case SC2.MarkerShape.Diamond:
                {
                    float d = r * 1.25f;
                    var (a, b, c, e) = (p - new Vector2(0, d), p + new Vector2(d, 0), p + new Vector2(0, d), p - new Vector2(d, 0));
                    if (filled) dl.AddQuadFilled(a, b, c, e, col); else dl.AddQuad(a, b, c, e, col, thick);
                    break;
                }
                case SC2.MarkerShape.Triangle:
                {
                    float d = r * 1.3f;
                    var (a, b, c) = (p + new Vector2(0, -d), p + new Vector2(d * 0.87f, d * 0.5f), p + new Vector2(-d * 0.87f, d * 0.5f));
                    if (filled) dl.AddTriangleFilled(a, b, c, col); else dl.AddTriangle(a, b, c, col, thick);
                    break;
                }
                case SC2.MarkerShape.Hexagon:
                    if (filled) dl.AddNgonFilled(p, r * 1.15f, col, 6); else dl.AddNgon(p, r * 1.15f, col, 6, thick);
                    break;
                default:   // circle, ring
                    if (filled) dl.AddCircleFilled(p, r, col, Math.Clamp((int)(r * 2.5f), 12, 32));
                    else dl.AddCircle(p, r, col, Math.Clamp((int)(r * 2.5f), 12, 32), thick);
                    break;
            }
        }

        /// <summary>
        /// One entity marker: a soft drop shadow, the shape filled in <paramref name="col"/> (a Ring is an outline only) and,
        /// with <paramref name="outline"/>, a thin dark edge so markers stay readable on any terrain or creep.
        /// </summary>
        public static void DrawMarker(ImDrawListPtr dl, Vector2 pos, SC2.MarkerShape shape, float r, uint col, bool outline, bool shadow = true)
        {
            uint a = col >> 24;
            if (shadow && a > 0x60) ShapePath(dl, pos + new Vector2(0.8f, 1.2f), r + 0.6f, shape, (a * 0x50 / 0xFF) << 24, true);
            if (shape == SC2.MarkerShape.Ring)
            {
                if (outline) ShapePath(dl, pos, r, shape, (a * 0xB0 / 0xFF) << 24, false, 3.6f);
                ShapePath(dl, pos, r, shape, col, false, 2f);
                return;
            }
            ShapePath(dl, pos, r, shape, col, true);
            if (outline) ShapePath(dl, pos, r, shape, (Darken(col, 0.35f) & 0x00FFFFFF) | ((a * 0xD0 / 0xFF) << 24), false, 1f);
        }

        /// <summary>A soft glow ring (two faint, wider strokes under a crisp one): game-mode markers.</summary>
        public static void DrawGlow(ImDrawListPtr dl, Vector2 pos, float r, uint col)
        {
            uint rgb = col & 0x00FFFFFF;
            dl.AddCircle(pos, r + 3f, rgb | 0x18000000, 32, 6f);
            dl.AddCircle(pos, r + 2.5f, rgb | 0x30000000, 32, 3f);
            dl.AddCircle(pos, r + 2.5f, rgb | 0xC0000000, 32, 1.2f);
        }

        /// <summary>A thin rounded vitals bar: dark track, filled part in <paramref name="col"/>.</summary>
        public static void DrawBar(ImDrawListPtr dl, Vector2 tl, float w, float h, float frac, uint col)
        {
            float rr = h * 0.5f;
            dl.AddRectFilled(tl - new Vector2(1f, 1f), tl + new Vector2(w + 1f, h + 1f), 0xB0000000, rr + 1f);
            if (frac > 0f) dl.AddRectFilled(tl, tl + new Vector2(MathF.Max(h, w * Math.Clamp(frac, 0f, 1f)), h), col, rr);
        }

        /// <summary>
        /// A label "pill": a dark rounded background behind the text (the marker beside it already carries the owner's
        /// colour). Returns its size so the caller can lay out the next one.
        /// </summary>
        public static Vector2 DrawPill(ImDrawListPtr dl, Vector2 tl, string text, uint textCol, float alpha = 1f)
        {
            var ts = ImGuiNET.ImGui.CalcTextSize(text);
            var size = new Vector2(ts.X + 8f, ts.Y + 2f);
            uint bgA = (uint)(0xB4 * alpha);
            dl.AddRectFilled(tl, tl + size, (bgA << 24) | 0x00181410, 3f);
            dl.AddText(tl + new Vector2(4f, 1f), (textCol & 0x00FFFFFF) | ((uint)((textCol >> 24) * alpha) << 24), text);
            return size;
        }

        // ── Outlined text ────────────────────────────────────────────────────

        public static void DrawOutlinedText(ImDrawListPtr dl, Vector2 pos, string text, uint col)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    dl.AddText(new Vector2(pos.X + dx, pos.Y + dy), 0xFF000000u, text);
            dl.AddText(pos, col, text);
        }
    }
}
