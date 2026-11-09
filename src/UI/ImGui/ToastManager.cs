// ToastManager.cs
// Lightweight transient toast notification overlay.
//
// Usage:
//   ToastManager.Show("Saved successfully!", ToastStyle.Success);
//   ToastManager.Show("Connection", "Lost connection to server.", ToastStyle.Danger, 5f);
//   ToastManager.Show(new Toast("Custom message", ToastStyle.Warning, Duration: 4f, Title: "Alert"));
//
// Call ToastManager.Draw() once per frame AFTER all panels so toasts sit on top.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using ImGuiNET;

namespace SC2Radar.UI.ImGui
{
    // ── Public types ──────────────────────────────────────────────────────────────

    public enum ToastStyle { Info, Success, Warning, Danger }

    /// <summary>Immutable data model for a single toast notification.</summary>
    public record Toast(
        string     Message,
        ToastStyle Style    = ToastStyle.Info,
        float      Duration = 3f,
        string     Title    = null
    );

    // ── Manager ───────────────────────────────────────────────────────────────────

    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static class ToastManager
    {
        // ── Layout / look constants ───────────────────────────────────────────────

        public const float ToastWidth   = 300f;
        public const float PadX         = 12f;
        public const float PadY         = 9f;
        public const float ToastGap     = 6f;
        public const float MarginRight  = 16f;
        public const float MarginBottom = 16f;
        public const float AccentBarW   = 4f;
        public const float ProgressH    = 3f;
        public const float CornerR      = 6f;
        public const int   MaxToasts    = 5;

        private const float SlideSpeed = 12f;  // exp-decay slide-in speed
        private const float FadeWindow = 0.4f; // seconds before expiry to start fading

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Show a simple notification.</summary>
        public static void Show(string message, ToastStyle style = ToastStyle.Info, float duration = 3f)
            => Enqueue(new Toast(message, style, duration));

        /// <summary>Show a notification with a bold title line above the message.</summary>
        public static void Show(string title, string message, ToastStyle style = ToastStyle.Info, float duration = 3f)
            => Enqueue(new Toast(message, style, duration, title));

        /// <summary>Show a fully-configured Toast record.</summary>
        public static void Show(Toast toast) => Enqueue(toast);

        // ── Internal state ────────────────────────────────────────────────────────

        private sealed class LiveToast
        {
            public readonly Toast Data;
            public readonly float TotalDuration;
            public float          TimeLeft;
            public float          SlideT;    // 0 → 1 slide-in (advances each frame)
            public Vector2        CloseTL;   // close-button hit rect, updated per frame
            public Vector2        CloseBR;

            public LiveToast(Toast data)
            {
                Data          = data;
                TotalDuration = Math.Max(data.Duration, 0.1f);
                TimeLeft      = TotalDuration;
            }
        }

        private static readonly List<LiveToast> _active = new(MaxToasts);
        private static readonly object          _lock   = new();

        private static void Enqueue(Toast toast)
        {
            lock (_lock)
            {
                if (_active.Count >= MaxToasts)
                    _active.RemoveAt(0);
                _active.Add(new LiveToast(toast));
            }
        }

        // ── Per-frame draw ────────────────────────────────────────────────────────
        // Call once per frame from the main render loop, AFTER all panels.

        public static void Draw()
        {
            if (_active.Count == 0) return;

            var   io  = ImGuiNET.ImGui.GetIO();
            float dt  = io.DeltaTime;
            var   dl  = ImGuiNET.ImGui.GetForegroundDrawList();

            bool    clicked = ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            Vector2 mp      = io.MousePos;

            _active.RemoveAll(t => t.TimeLeft <= 0f);
            if (_active.Count == 0) return;

            var   fontN = ImGuiStyle.FontNormal;
            var   fontB = ImGuiStyle.FontBold;
            float fsN   = fontN.FontSize > 0f ? fontN.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float fsB   = fontB.FontSize > 0f ? fontB.FontSize : fsN;

            float baseX = io.DisplaySize.X - ToastWidth - MarginRight;
            float curY  = io.DisplaySize.Y - MarginBottom;

            foreach (var lt in _active)
            {
                lt.TimeLeft -= dt;

                // Advance slide-in (snaps instantly in LowPerf mode)
                lt.SlideT = UI.LowPerf
                    ? 1f
                    : Math.Min(1f, lt.SlideT + dt * SlideSpeed);

                bool   hasTitle = !string.IsNullOrEmpty(lt.Data.Title);
                string title    = hasTitle ? LocManager.Get(lt.Data.Title) : null;
                string message  = LocManager.Get(lt.Data.Message);
                const float CbSz    = 16f;
                float wrapWidth = ToastWidth - AccentBarW - 2f * PadX - CbSz - 2f;
                float msgH      = fontN.CalcTextSizeA(fsN, float.MaxValue, wrapWidth, message).Y;
                float toastH    = PadY
                                + (hasTitle ? fsB + 3f : 0f)
                                + msgH
                                + PadY + ProgressH + 2f;

                // Cubic ease-out: starts fast, decelerates into final position
                float ease = 1f - MathF.Pow(1f - lt.SlideT, 3f);
                float tx   = baseX + (ToastWidth + MarginRight) * (1f - ease);
                float ty   = curY - toastH;

                // Fade alpha as expiry approaches
                float fadeT = lt.TimeLeft < FadeWindow
                    ? Math.Max(0f, lt.TimeLeft / FadeWindow)
                    : 1f;

                var tl = new Vector2(tx, ty);
                var br = new Vector2(tx + ToastWidth, ty + toastH);

                uint sc = StyleColor(lt.Data.Style);

                // Background
                dl.AddRectFilled(tl, br,
                    Fade(ImGuiStyle.Col(0x1A, 0x1A, 0x24, 242), fadeT), CornerR);

                // Left accent bar
                dl.AddRectFilled(
                    new Vector2(tl.X, tl.Y),
                    new Vector2(tl.X + AccentBarW, br.Y),
                    Fade(sc, fadeT), CornerR, ImDrawFlags.RoundCornersLeft);

                // Border (style-tinted)
                dl.AddRect(tl, br,
                    Fade(SetAlpha(sc, 55), fadeT),
                    CornerR, ImDrawFlags.None, 1f);

                // Close button (×)
                float cbX = br.X - PadX - CbSz;
                float cbY = tl.Y + PadY * 0.6f;
                lt.CloseTL = new Vector2(cbX, cbY);
                lt.CloseBR = new Vector2(cbX + CbSz, cbY + CbSz);
                bool closeHov = mp.X >= lt.CloseTL.X && mp.X <= lt.CloseBR.X
                             && mp.Y >= lt.CloseTL.Y && mp.Y <= lt.CloseBR.Y;
                if (clicked && closeHov) { lt.TimeLeft = 0f; continue; }

                float cx = cbX + CbSz * .5f, cy = cbY + CbSz * .5f, xs = 3.5f;
                uint  xc = Fade(closeHov ? 0xFFFFFFFF : SetAlpha(0xFFFFFFFF, 100), fadeT);
                dl.AddLine(new Vector2(cx - xs, cy - xs), new Vector2(cx + xs, cy + xs), xc, 1.5f);
                dl.AddLine(new Vector2(cx + xs, cy - xs), new Vector2(cx - xs, cy + xs), xc, 1.5f);

                // Text (title + message) - clip to the left of the close button
                float textX    = tl.X + AccentBarW + PadX;
                float textY    = tl.Y + PadY;
                float clipMaxX = cbX - 2f;
                if (hasTitle)
                {
                    var titleClip = new Vector4(textX, tl.Y, clipMaxX, textY + fsB);
                    dl.AddText(fontB, fsB,
                        new Vector2(textX, textY),
                        Fade(0xFFFFFFFF, fadeT), title, 0f, ref titleClip);
                    textY += fsB + 3f;
                }
                var msgClip = new Vector4(textX, textY, clipMaxX, textY + msgH);
                dl.AddText(fontN, fsN,
                    new Vector2(textX, textY),
                    Fade(hasTitle ? SetAlpha(0xFFFFFFFF, 195) : 0xFFFFFFFF, fadeT),
                    message, wrapWidth, ref msgClip);

                // Progress bar (style color, depletes over duration)
                float progress = Math.Max(0f, lt.TimeLeft / lt.TotalDuration);
                float barY0    = br.Y - ProgressH - 1f;
                float barX0    = tl.X + AccentBarW;
                float barX1    = barX0 + (ToastWidth - AccentBarW) * progress;
                if (barX1 > barX0 + 1f)
                    dl.AddRectFilled(
                        new Vector2(barX0, barY0),
                        new Vector2(barX1, br.Y - 1f),
                        Fade(SetAlpha(sc, 145), fadeT));

                curY = ty - ToastGap;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static uint StyleColor(ToastStyle style) => style switch
        {
            ToastStyle.Success => ImGuiStyle.Success,
            ToastStyle.Warning => ImGuiStyle.Warning,
            ToastStyle.Danger  => ImGuiStyle.Danger,
            _                  => ImGuiStyle.Accent,
        };

        // Scale the alpha channel by t (0–1), used for fade-out.
        private static uint Fade(uint col, float t)
        {
            byte a = (byte)(((col >> 24) & 0xFF) * Math.Clamp(t, 0f, 1f));
            return (col & 0x00FF_FFFF) | ((uint)a << 24);
        }

        // Replace the alpha channel with a specific byte value.
        private static uint SetAlpha(uint col, byte a)
            => (col & 0x00FF_FFFF) | ((uint)a << 24);
    }
}
