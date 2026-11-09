// UIControls.cs
// Custom draw-list widget library.
//
// Every interactive control uses ImGui's InvisibleButton / Dummy for layout and
// hit-testing so scrolling, BeginDisabled, ID-stack, and tooltips all work normally.
// All visuals are drawn manually via GetWindowDrawList.
//
// Drop-in replacements:
//   ImGui.Button(label)           ->  UI.Button(label)
//   ImGui.Checkbox(label, ref v)  ->  UI.Toggle(label, ref v)
//   ImGui.SliderFloat(...)        ->  UI.SliderFloat(...)
//   ImGui.SliderInt(...)          ->  UI.SliderInt(...)

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using SC2Radar.Common.Misc.Data;
using SC2Radar.DMA;

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static class UI
    {
        // ── Geometry ─────────────────────────────────────────────────────────
        private const float ButtonH     = 30f;
        private const float BtnRound    = 5f;
        private const float ToggleW     = 40f;
        private const float ToggleH     = 20f;
        private const float TrackH      = 4f;   // matches XAML Height="5" + slight DPI scale
        private const float TrackRound  = 2f;   // matches XAML CornerRadius="2"
        private const float ThumbR      = 7f;   // unused visually, kept for drag math

        // ── Animation ────────────────────────────────────────────────────────
        // Exponential decay - mathematically impossible to overshoot, never flickers.
        private static readonly Dictionary<uint, float> _anim = new(64);

        // Animation speed presets - pass as the `speed` argument to Anim().
        private const float SpeedHov    = 12f;   // hover fade-in/out
        private const float SpeedAct    = 20f;   // click/press response
        private const float SpeedToggle = 18f;   // toggle on/off flip
        private const float SpeedSlide  = 14f;   // default smooth lerp

        // Per-ID XOR salts - each widget uses the same base ImGui item ID but
        // XORs a different salt per animation variable so they live in separate
        // _anim slots without colliding.
        private const uint SaltHov  = 0xAAu;
        private const uint SaltAct  = 0xCCu;
        private const uint SaltAlt1 = 0xBBu;
        private const uint SaltAlt2 = 0xDDu;
        private const uint SaltAlt3 = 0xEEu;

        // Convenience wrappers used inside every interactive widget.
        private static float AnimHov(uint id, bool hov)  => Anim(id ^ SaltHov,  hov  ? 1f : 0f, SpeedHov);
        private static float AnimAct(uint id, bool act)  => Anim(id ^ SaltAct,  act  ? 1f : 0f, SpeedAct);

        /// <summary>
        /// When true, all animations snap to their target value instantly - no easing curves,
        /// no sine-wave pulses. Toggle from the UI Controls panel to reduce CPU/GPU load.
        /// </summary>
        public static bool LowPerf { get; set; }

        // ── Disabled state ────────────────────────────────────────────────────
        // Call BeginDisabled()/EndDisabled() instead of ImGui's version so our
        // custom draw-list controls respect the dimming and block interaction.
        private static float _disabledAlpha = 1f;
        private static int   _disabledDepth;
        private static readonly Stack<string> _disabledReasonStack = new();
        private static uint  _disabledTooltipLastFrame = uint.MaxValue;
        private static uint  _disabledTooltipRowKey;
        public static bool IsDisabled => _disabledAlpha < 1f;

        // Returns the innermost non-null reason from the disabled scope stack.
        private static string CurrentDisabledReason
        {
            get { foreach (var r in _disabledReasonStack) if (r != null) return r; return null; }
        }

        public static void BeginDisabled(string reason = null)
        {
            _disabledDepth++;
            _disabledAlpha = 0.35f;
            _disabledReasonStack.Push(reason);
            ImGuiNET.ImGui.BeginDisabled();
        }

        public static void EndDisabled()
        {
            if (_disabledDepth > 0) _disabledDepth--;
            if (_disabledReasonStack.Count > 0) _disabledReasonStack.Pop();
            if (_disabledDepth == 0) _disabledAlpha = 1f;
            ImGuiNET.ImGui.EndDisabled();
        }

        /// <summary>
        /// Force-clears any leaked BeginDisabled()/EndDisabled() nesting - e.g. after an
        /// exception mid-draw skipped the matching EndDisabled() call. Disabled scopes should
        /// never span more than a single synchronous draw call, so this is always safe to call
        /// as a recovery step (also balances the underlying ImGui disabled stack).
        /// </summary>
        public static void ResetDisabledState()
        {
            while (_disabledDepth > 0)
            {
                _disabledDepth--;
                ImGuiNET.ImGui.EndDisabled();
            }
            _disabledReasonStack.Clear();
            _disabledAlpha = 1f;
        }

        /// <summary>
        /// Shows a "requires" tooltip when the last drawn widget is inside a BeginDisabled scope
        /// that has a reason string. Delegates to Tooltip(Action, Vector2?) so it shares the
        /// same accent line, fade, and shell styling as every other tooltip in the app.
        /// Call immediately after every interactive widget's existing tooltip/return site.
        /// </summary>
        // localReason bypasses the IsDisabled check - used by widgets that internally disable
        // a sub-control (e.g. ToggleCombo dims its combo when the toggle is off) and need to
        // show a reason after their own EndDisabled() has already been called.
        private static void ShowDisabledReasonTooltip(string localReason = null)
        {
            string reason = !string.IsNullOrEmpty(localReason) ? localReason : CurrentDisabledReason;
            if (string.IsNullOrEmpty(reason)) return;
            if (string.IsNullOrEmpty(localReason) && !IsDisabled) return;
            // Localized once here (both the popup and non-popup branches below use the result
            // directly) - every BeginGroup/BeginDisabled disabledReason across every panel
            // becomes translation-friendly without touching each call site.
            reason = LocManager.Get(reason);

            // Only one disabled-reason tooltip per frame - prevents duplicate popups when
            // multiple widgets on the same visual row each call this (e.g. checkbox + combo).
            uint frame = (uint)ImGuiNET.ImGui.GetFrameCount();
            if (_disabledTooltipLastFrame == frame) return;

            // When rendering inside a popup (e.g. item-settings popover), Tooltip(Action)
            // suppresses itself via the IsPopupOpen guard. Use TooltipInPopup instead -
            // it bypasses the guard and uses position-based hover detection just like the
            // non-popup path, but TxtDim is passed so the text matches the standard disabled style.
            if (ImGuiNET.ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            {
                TooltipInPopup(reason, _lastRowTL, _lastRowBR, Alpha(TxtDim, 230));
                return;
            }

            float   wrapWidth = SafeTooltipWrapWidth();
            Vector2 textSize  = ImGuiNET.ImGui.CalcTextSize(reason, false, wrapWidth);
            Vector2 boxSize   = textSize + new Vector2(20f, 16f);

            if (Tooltip(() =>
            {
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, ToV4(Alpha(TxtDim, 230)));
                ImGuiNET.ImGui.PushTextWrapPos(wrapWidth);
                ImGuiNET.ImGui.TextUnformatted(reason);
                ImGuiNET.ImGui.PopTextWrapPos();
                ImGuiNET.ImGui.PopStyleColor();
            }, boxSize))
            {
                _disabledTooltipLastFrame = frame;
                _disabledTooltipRowKey    = unchecked((uint)(_lastRowTL.X.GetHashCode() * 397 ^ _lastRowTL.Y.GetHashCode()));
            }
        }

        // Applies disabled alpha to any colour - used throughout all controls.
        private static uint DA(uint col)
        {
            if (_disabledAlpha >= 1f) return col;
            byte a = (byte)(((col >> 24) & 0xFF) * _disabledAlpha);
            return (col & 0x00FF_FFFF) | ((uint)a << 24);
        }

        // Draws a pulsing accent border when this key matches the SearchRegistry highlight target.
        // Call after every control sets _lastRowTL/_lastRowBR.
        private static void DrawSearchHighlight(string key, Vector2 tl, Vector2 br, float corner = 4f)
        {
            if (SearchRegistry.HighlightKey != key) return;
            float time  = SearchRegistry.HighlightTime;
            float fade  = MathF.Min(time, 1f);
            // LowPerf: static ring at full opacity; normal: sine-wave pulse.
            float alpha = LowPerf
                ? fade
                : fade * (0.35f + 0.65f * (MathF.Sin(time * 7f) * 0.5f + 0.5f));
            var dl = DL;
            dl.AddRect(
                new Vector2(tl.X - 2f, tl.Y - 2f),
                new Vector2(br.X + 2f, br.Y + 2f),
                Alpha(Accent, (byte)(110 * alpha)), corner + 2f, ImDrawFlags.None, 1f);
            dl.AddRect(tl, br, Alpha(Accent, (byte)(255 * alpha)), corner, ImDrawFlags.None, 1.5f);
        }

        // ── Tooltip bounds ────────────────────────────────────────────────────
        // Each custom control writes its row rect here so UI.Tooltip() can
        // check whether the mouse is hovering it - avoids fighting with ImGui's
        // IsItemHovered() on Dummy / InvisibleButton edge cases.
        private static Vector2 _lastRowTL;
        private static Vector2 _lastRowBR;

        // Tooltip hover timer - accumulates per row so the tooltip only shows after a delay.
        // Keyed by a hash of the row's screen position (changes on scroll, which resets the timer).
        private static readonly Dictionary<uint, float> _tooltipHover = new(64);
        private const float TooltipDelay  = 0.1f;
        private const float TooltipFadeIn = 0.15f;

        // ─── Wheel consumption ────────────────────────────────────────────────────
        // Set by any custom widget (slider, etc.) that consumed the mouse wheel this
        // frame. ChromePanel reads this flag in DrawScrollableContent and calls
        // SetScrollY(savedScroll) before EndChild to cancel the auto-scroll that
        // ImGui.NewFrame() already applied, keeping the panel stationary.
        public  static bool WheelConsumedThisFrame   { get; private set; }
        public  static void ClearWheelConsumed()     => WheelConsumedThisFrame = false;
        public  static void MarkWheelConsumed()      => WheelConsumedThisFrame = true;

        // True while any hotkey badge is waiting for a key press.
        public static bool IsCapturingHotkey => _captureBadgeId != 0;

        // ───────────────────────────────────────────────────────────────────────
        // ── Slider zone latch ──────────────────────────────────────────────────
        // Tracks whether the current press on a slider started in a dec/inc zone.
        // Keyed by ImGui item ID so multiple sliders don't interfere.
        private static readonly Dictionary<uint, bool> _sliderZoneLatch = new(16);

        // ── Slider group ──────────────────────────────────────────────────────
        // When > 0, all sliders use this shared label column width so their
        // tracks are left-aligned. Cleared by EndSliderGroup().
        private static float _groupLabelColW = 0f;

        // ── Numeric group ─────────────────────────────────────────────────────
        // Same concept for InputInt/InputFloat spinners.
        private static float _numLabelColW = 0f;

        // Section collapse animation state - keyed by section label hash.
        private struct SectionState
        {
            public float CollapseT;     // 0 = fully open, 1 = fully closed
            public float ExpandedH;     // pixel height when fully expanded
            public float ContentStartY; // screen Y at content start (set each frame)
            public bool  InChild;       // true when a BeginChild clip is active
        }
        private static readonly Dictionary<uint, SectionState> _sections     = new();
        private static readonly Stack<uint>                     _sectionStack = new();

        public static void  SeedAnim(uint id, float value) => _anim[id] = value;

        public static float Anim(uint id, float target, float speed = SpeedSlide)
        {
            if (LowPerf)
            {
                if (MathF.Abs(target) < 1e-5f) _anim.Remove(id);
                else                            _anim[id] = target;
                return target;
            }
            _anim.TryGetValue(id, out float cur);
            float dt   = ImGuiNET.ImGui.GetIO().DeltaTime;
            float next = cur + (target - cur) * MathF.Min(1f - MathF.Exp(-speed * dt), 1f);
            // Remove entries that have fully decayed to zero - prevents the dict
            // from accumulating dead entries for controls that no longer exist.
            if (MathF.Abs(next) < 1e-5f && MathF.Abs(target) < 1e-5f)
                _anim.Remove(id);
            else
                _anim[id] = next;
            return next;
        }

        // ── Palette shortcuts ────────────────────────────────────────────────
        private static uint Accent          => ImGuiStyle.Accent;
        private static uint AccentHover     => ImGuiStyle.AccentHover;
        private static uint AccentActive    => ImGuiStyle.AccentActive;
        private static uint TxtNorm         => ImGuiStyle.Text;
        private static uint TxtDim          => ImGuiStyle.TextDim;
        private static uint Dark3           => ImGuiStyle.Dark3;
        private static uint Dark4           => ImGuiStyle.Dark4;
        private static uint Dark5           => ImGuiStyle.Dark5;
        private static uint BorderCol       => ImGuiStyle.Border;
        private static uint ControlFill     => ImGuiStyle.ControlFill;
        private static uint GroupTitleColor => ImGuiStyle.GroupTitleColor;
        private static uint InputBg         => ImGuiStyle.InputBg;
        private static uint InputBgH        => ImGuiStyle.InputBgHov;
        private static uint BrighterColor(uint col, byte delta) => ImGuiStyle.BrighterColor(col, delta);

        // ── Color helpers ─────────────────────────────────────────────────────
        public static uint Alpha(uint col, byte a)
            => (col & 0x00FF_FFFF) | ((uint)a << 24);

        public static uint Lerp(uint a, uint b, float t)
        {
            float ar = (a & 0xFF) / 255f,          ag = ((a >> 8)  & 0xFF) / 255f;
            float ab = ((a >> 16) & 0xFF) / 255f,  aa = ((a >> 24) & 0xFF) / 255f;
            float br = (b & 0xFF) / 255f,          bg = ((b >> 8)  & 0xFF) / 255f;
            float bb = ((b >> 16) & 0xFF) / 255f,  ba = ((b >> 24) & 0xFF) / 255f;
            return Pack(ar + (br - ar) * t, ag + (bg - ag) * t,
                        ab + (bb - ab) * t, aa + (ba - aa) * t);
        }

        private static uint Pack(float r, float g, float b, float a = 1f)
            => ((uint)(r * 255) & 0xFF)
             | (((uint)(g * 255) & 0xFF) << 8)
             | (((uint)(b * 255) & 0xFF) << 16)
             | (((uint)(a * 255) & 0xFF) << 24);

        private static ImDrawListPtr DL => ImGuiNET.ImGui.GetWindowDrawList();

        // ───────────────────────────────────────────────────────────────────────────
        // BUTTON  - solid rounded rectangle, lightens on hover, darkens on press
        //
        // Optional icon drawn left of the label (or centred when label is empty):
        //   UI.Button("Settings", icon: UI.Icons.Gear)
        //   UI.Button("##icon_only", icon: UI.Icons.Trash, size: new(30,30))
        //
        // Icon delegate: (ImDrawListPtr dl, Vector2 centre, float size, uint col)
        // ───────────────────────────────────────────────────────────────────────────

        public static bool Button(string label, Vector2 size = default,
            ButtonStyle style = ButtonStyle.Accent,
            Action<ImDrawListPtr, Vector2, float, uint> icon = null,
            string hint = null, bool risky = false, string riskyWarning = null)
        {
            string locRiskyWarning = risky && riskyWarning != null ? Loc(riskyWarning) : null;
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            float avail     = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            string stripped = Loc(label);
            bool   hasLabel = !string.IsNullOrEmpty(stripped);
            bool   hasIcon  = icon != null;

            Vector2 ts = hasLabel ? MeasureText(font, fsize, stripped) : Vector2.Zero;

            // Icon is drawn as a square matching fsize; gap between icon and text = 6px
            const float IconGap = 6f;
            float iconSz  = hasIcon ? fsize : 0f;
            float contentW = ts.X + (hasIcon ? iconSz + (hasLabel ? IconGap : 0f) : 0f);

            float w = size.X > 0f ? size.X : MathF.Min(contentW + 36f, avail);
            float h = size.Y > 0f ? size.Y : ButtonH;
            w = MathF.Min(w, avail);

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.InvisibleButton(label, new Vector2(w, h));
            bool clicked = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            bool hovered = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            bool active  = !IsDisabled && ImGuiNET.ImGui.IsItemActive();
            if (hovered) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            uint id      = ImGuiNET.ImGui.GetItemID();

            float t  = Anim(id, hovered ? 1f : 0f);
            float ta = Anim(id ^ 0xCCu, active ? 1f : 0f, 20f);
            Vector2 br = new(tl.X + w, tl.Y + h);
            var dl = DL;

            // Per-variant colours: base, hover, press, border-rest, border-hover, label
            (uint cBase, uint cHov, uint cPress, uint cBord, uint cBordH, uint cTxt) = style switch
            {
                // Ghost: outline-only at rest, accent-tinted fill + accent border on hover
                ButtonStyle.Ghost => (
                    0u,
                    Alpha(Accent, 22),
                    Alpha(Accent, 42),
                    Alpha(BorderCol, 180),
                    Alpha(Accent, 215),
                    TxtNorm),
                // Danger: subtle danger tint at rest, colored border, danger-text → fills/white on hover
                ButtonStyle.Danger => (
                    Alpha(ImGuiStyle.Danger, 12),
                    Alpha(ImGuiStyle.Danger, 48),
                    Alpha(ImGuiStyle.Danger, 78),
                    Alpha(ImGuiStyle.Danger, 150),
                    ImGuiStyle.Danger,
                    ImGuiStyle.Danger),
                // Success: same treatment as Danger in the success palette
                ButtonStyle.Success => (
                    Alpha(ImGuiStyle.Success, 12),
                    Alpha(ImGuiStyle.Success, 48),
                    Alpha(ImGuiStyle.Success, 78),
                    Alpha(ImGuiStyle.Success, 150),
                    ImGuiStyle.Success,
                    ImGuiStyle.Success),
                // Accent: outline-primary - accent border always visible, tinted fill on hover
                _ => (
                    Alpha(Accent, 15),
                    Alpha(Accent, 40),
                    Alpha(Accent, 68),
                    Alpha(Accent, 180),
                    Accent,
                    Accent),
            };

            // Risky buttons: swap the resting label colour for the same orange the toggle
            // uses, so a hazardous action reads at a glance without changing the button
            // chrome itself (keeps Ghost/Accent framing intact).
            if (risky)
            {
                cTxt = ImGuiStyle.Col(0xFF, 0xAA, 0x22);
            }

            // Ghost keeps its text colour steady; all others brighten to white on hover.
            uint cTxtH      = style == ButtonStyle.Ghost ? cTxt : 0xFFFFFFFF;
            uint iconTxtCol = DA(Lerp(cTxt, cTxtH, t));

            // Body fill - skip draw when fully transparent (Ghost/Danger/Success at rest)
            uint body = DA(Lerp(Lerp(cBase, cHov, t), cPress, ta));
            if ((body & 0xFF000000u) != 0) dl.AddRectFilled(tl, br, body, BtnRound);

            // Border - colour animates from rest to hover as t → 1
            uint bord = DA(Lerp(Lerp(cBord, cBordH, t), cBordH, ta));
            dl.AddRect(tl, br, bord, BtnRound, ImDrawFlags.None, 1f);

            // Click flash - set to 1 on click frame, decays to 0
            if (clicked) _anim[id ^ 0xFFu] = 1f;
            float tf = Anim(id ^ 0xFFu, 0f, 18f);
            if (tf > 0.01f)
                dl.AddRectFilled(tl, br, Alpha(0xFFFFFFFF, (byte)(48 * tf)), BtnRound);

            // ── Icon + label, horizontally centred as a unit ──────────────────
            // Truncate label when it doesn't fit within the button body; prevents text
            // from overflowing left or right when the button is narrower than its content.
            float iconAndGapW = hasIcon ? iconSz + (hasLabel ? IconGap : 0f) : 0f;
            float maxBtnLblW  = MathF.Max(0f, w - iconAndGapW - 36f);
            string dispBtnLbl = hasLabel ? TruncateText(font, fsize, stripped, maxBtnLblW) : "";
            float dispLblW    = hasLabel ? MeasureText(font, fsize, dispBtnLbl).X : 0f;
            float dispContentW = dispLblW + iconAndGapW;
            float cy = tl.Y + h * .5f;
            float startX = tl.X + MathF.Max(2f, (w - dispContentW) * .5f);

            if (hasIcon)
            {
                float icx = startX + iconSz * .5f;
                icon(dl, new Vector2(icx, cy), iconSz, iconTxtCol);
                startX += iconSz + (hasLabel ? IconGap : 0f);
            }

            if (hasLabel)
            {
                dl.AddText(font, fsize,
                    new Vector2(startX, MathF.Round(cy - fsize * .5f)),
                    iconTxtCol, dispBtnLbl);
            }

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + w, tl.Y + h);
            DrawSearchHighlight(Strip(label), _lastRowTL, _lastRowBR, BtnRound);
            if (locRiskyWarning != null) TooltipWithRiskyWarning(hint, locRiskyWarning);
            else if (hint != null)      Tooltip(hint);
            ShowDisabledReasonTooltip();
            return clicked;
        }

        // ── ICON BUTTON ────────────────────────────────────────────────────────
        // Button whose icon fills most of the face - replaces UI.Button + manual
        // dl.AddImage so the icon renders larger and stays crisp from the source PNG.
        //   UI.IconButton("##id", IconCache.Icon.Plus)                         // 30×30, Ghost
        //   UI.IconButton("##id", IconCache.Icon.Trash, w: 28f, h: 30f, style: ButtonStyle.Danger)
        //   UI.IconButton("##id", IconCache.Icon.Bell,  w: 28f, iconSz: 16f)   // fixed icon size
        //   iconSz overrides the auto size (min(w,h) - pad*2) when set > 0.

        public static bool IconButton(string id, IconCache.Icon icon,
            float w = ButtonH, float h = ButtonH, float pad = 3f,
            ButtonStyle style = ButtonStyle.Ghost, uint tint = 0xFFFFFFFF,
            float iconSz = 0f, string hint = null)
            => IconButton(id, IconCache.Get(icon), w, h, pad, style, tint, iconSz, hint);

        public static bool IconButton(string id, IntPtr tex,
            float w = ButtonH, float h = ButtonH, float pad = 3f,
            ButtonStyle style = ButtonStyle.Ghost, uint tint = 0xFFFFFFFF,
            float iconSz = 0f, string hint = null)
        {
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            float bw    = MathF.Min(w, avail);
            float bh    = h;

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.InvisibleButton(id, new Vector2(bw, bh));
            bool clicked = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            bool hovered = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            bool active  = !IsDisabled && ImGuiNET.ImGui.IsItemActive();
            if (hovered) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            uint itemId = ImGuiNET.ImGui.GetItemID();

            float t  = Anim(itemId,         hovered ? 1f : 0f);
            float ta = Anim(itemId ^ 0xCCu, active  ? 1f : 0f, 20f);
            Vector2 br = new(tl.X + bw, tl.Y + bh);
            var dl = DL;

            (uint cBase, uint cHov, uint cPress, uint cBord, uint cBordH, uint cIco) = style switch
            {
                ButtonStyle.Ghost   => (0u,                             Alpha(Accent, 22),               Alpha(Accent, 42),
                                        Alpha(BorderCol, 180),          Alpha(Accent, 215),              TxtNorm),
                ButtonStyle.Danger  => (Alpha(ImGuiStyle.Danger, 12),   Alpha(ImGuiStyle.Danger, 48),    Alpha(ImGuiStyle.Danger, 78),
                                        Alpha(ImGuiStyle.Danger, 150),  ImGuiStyle.Danger,               ImGuiStyle.Danger),
                ButtonStyle.Success => (Alpha(ImGuiStyle.Success, 12),  Alpha(ImGuiStyle.Success, 48),   Alpha(ImGuiStyle.Success, 78),
                                        Alpha(ImGuiStyle.Success, 150), ImGuiStyle.Success,              ImGuiStyle.Success),
                _                   => (Alpha(Accent, 15),              Alpha(Accent, 40),               Alpha(Accent, 68),
                                        Alpha(Accent, 180),             Accent,                          Accent),
            };

            uint cIcoH  = style == ButtonStyle.Ghost ? cIco : 0xFFFFFFFF;
            uint icoCol = DA(Lerp(cIco, cIcoH, t));

            uint body = DA(Lerp(Lerp(cBase, cHov, t), cPress, ta));
            if ((body & 0xFF000000u) != 0) dl.AddRectFilled(tl, br, body, BtnRound);
            dl.AddRect(tl, br, DA(Lerp(Lerp(cBord, cBordH, t), cBordH, ta)), BtnRound, ImDrawFlags.None, 1f);

            if (clicked) _anim[itemId ^ 0xFFu] = 1f;
            float tf = Anim(itemId ^ 0xFFu, 0f, 18f);
            if (tf > 0.01f)
                dl.AddRectFilled(tl, br, Alpha(0xFFFFFFFF, (byte)(48 * tf)), BtnRound);

            if (tex != IntPtr.Zero)
            {
                float sz = iconSz > 0f ? iconSz : MathF.Min(bw, bh) - pad * 2f;
                float cx = tl.X + bw * 0.5f, cy = tl.Y + bh * 0.5f;
                float ix = cx - sz * 0.5f,    iy = cy - sz * 0.5f;
                byte fr = (byte)((tint & 0xFF)         * (icoCol & 0xFF)         / 255);
                byte fg = (byte)(((tint >> 8)  & 0xFF) * ((icoCol >> 8)  & 0xFF) / 255);
                byte fb = (byte)(((tint >> 16) & 0xFF) * ((icoCol >> 16) & 0xFF) / 255);
                byte fa = (byte)(((tint >> 24) & 0xFF) * ((icoCol >> 24) & 0xFF) / 255);
                uint finalTint = (uint)fr | ((uint)fg << 8) | ((uint)fb << 16) | ((uint)fa << 24);
                dl.AddImage(tex, new Vector2(ix, iy), new Vector2(ix + sz, iy + sz),
                    Vector2.Zero, Vector2.One, finalTint);
            }

            _lastRowTL = tl;
            _lastRowBR = br;
            if (hint != null) Tooltip(hint);
            ShowDisabledReasonTooltip();
            return clicked;
        }

        // ── ICON TOGGLE ────────────────────────────────────────────────────────
        // Square icon-button with active/inactive state. Uses ImGui.Button() (not
        // InvisibleButton) so it clips correctly inside BeginMenuBar() contexts.
        // Active → accent background + accent icon + underline pip.
        // Inactive → transparent background + dim icon.
        //   if (UI.IconToggle("##freecam", IconCache.Icon.Compass, freeMode))
        //       freeMode = !freeMode;
        //   UI.Tooltip("...");

        public static bool IconToggle(string id, IconCache.Icon icon, bool active, float size = 0f, string hint = null)
        {
            float h = size > 0f ? size : ImGuiNET.ImGui.GetFrameHeight();

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button,
                active ? Alpha(Accent, 22) : 0u);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
                Alpha(Accent, active ? (byte)38 : (byte)22));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonActive,
                Alpha(Accent, 55));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,
                active ? Accent : TxtDim);

            bool clicked = ImGuiNET.ImGui.Button(id, new Vector2(h, h));

            var  dl  = DL;
            var  min = ImGuiNET.ImGui.GetItemRectMin();
            var  max = ImGuiNET.ImGui.GetItemRectMax();
            var  tex = IconCache.Get(icon);
            if (tex != IntPtr.Zero)
            {
                const float IconSz = 16f;
                float cx = (min.X + max.X) * 0.5f;
                float cy = (min.Y + max.Y) * 0.5f;
                dl.AddImage(tex,
                    new Vector2(cx - IconSz * 0.5f, cy - IconSz * 0.5f),
                    new Vector2(cx + IconSz * 0.5f, cy + IconSz * 0.5f),
                    Vector2.Zero, Vector2.One, active ? Accent : TxtDim);
            }

            if (active)
                dl.AddRectFilled(
                    new Vector2(min.X + 3f, max.Y - 2f),
                    new Vector2(max.X - 3f, max.Y),
                    Accent, 1f);

            _lastRowTL = min;
            _lastRowBR = max;
            ImGuiNET.ImGui.PopStyleColor(4);
            if (hint != null) Tooltip(hint);
            return clicked;
        }

        //─────────────────────────────────────────────────────────────────────────
        // TOGGLE  - checkbox style (square with animated tick)
        // ───────────────────────────────────────────────────────────────────────────

        private const float CbSize    = 16f;   // checkbox square size
        private const float CbRound   = 3f;
        private const float CbMarginR = 2f;    // mirrors the +2f text offset so left and right border gaps are equal

        // Inline hotkey label (drawn inside Toggle rows)
        private const float HkLabelGap = 6f;    // gap between label text and checkbox

        // Hotkey capture state - persists across frames
        private static uint _captureBadgeId;       // rowId currently listening for a key
        private static bool _captureWaitRelease;   // true until the activating LMB is physically released

        // Called when any HotkeyBadge modifies an entry - wire to GeneralPanel.RefreshHotkeyRegistrations.
        public static Action OnHotkeyChanged;

        // vk → all enabled HotkeyEntry objects that include that key; only kept when count > 1.
        // Self-exclusion in the badge uses reference equality - no label-matching required.
        private static IReadOnlyDictionary<int, IReadOnlyList<HotkeyEntry>> _hkConflictMap
            = new Dictionary<int, IReadOnlyList<HotkeyEntry>>();

        /// <summary>
        /// Rebuild the per-vk conflict map from all currently-enabled hotkey entries.
        /// Call from GeneralPanel.LoadHotkeysFromConfig after any hotkey change.
        /// The fallbackLabel (SplitCamelCase of the property name) seeds DisplayLabel so
        /// tooltips show a reasonable name even before that panel has been rendered.
        /// </summary>
        public static void RebuildHotkeyConflicts(
            IEnumerable<(HotkeyEntry entry, string fallbackLabel)> entries)
        {
            var tmp = new Dictionary<int, List<HotkeyEntry>>();
            foreach (var (entry, fallbackLabel) in entries)
            {
                // Only seed if Toggle hasn't yet set the real UI label this session.
                if (string.IsNullOrEmpty(entry.DisplayLabel))
                    entry.DisplayLabel = fallbackLabel;
                if (entry.Keys == null) continue;
                foreach (var vk in entry.Keys.Where(k => k > 0))
                {
                    if (!tmp.TryGetValue(vk, out var lst)) tmp[vk] = lst = new List<HotkeyEntry>();
                    if (!lst.Contains(entry)) lst.Add(entry);
                }
            }
            _hkConflictMap = tmp
                .Where(kvp => kvp.Value.Count > 1)
                .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<HotkeyEntry>)kvp.Value.AsReadOnly());
        }

        public static bool Toggle(string label, ref bool value, float width = 0f, bool risky = false, string hint = null, HotkeyEntry hotkey = null, string riskyWarning = null, string extraRiskyWarning = null)
        {
            string locRiskyWarning      = risky && riskyWarning != null ? Loc(riskyWarning) : null;
            string locExtraRiskyWarning = risky && extraRiskyWarning != null ? Loc(extraRiskyWarning) : null;
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowH  = MathF.Max(CbSize, fsize) + 4f;
            float avail = width > 0f ? width
                        : ColWidth > 0f ? ColWidth
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl  = ImGuiNET.ImGui.GetCursorScreenPos();
            float   cbX = tl.X + CbMarginR;
            float   lblX = cbX + CbSize + 6f; // text starts here

            // ── Pre-compute hotkey label geometry before InvisibleButton so we can
            //    exclude it from the row's hover/click hit-test.
            var     bfont   = ImGuiStyle.FontSmall;
            float   bfsize  = bfont.FontSize > 0f ? bfont.FontSize : 10f;
            string  keyLbl  = "";
            float   keyTW   = 0f;
            Vector2 bTL     = default, bBR = default;
            bool    preBadgeHov = false;
            const float hitPadX = 3f, hitPadY = 2f;

            if (hotkey != null)
            {
                // Cache the UI label so conflict-map self-exclusion uses the same string.
                hotkey.DisplayLabel = Strip(label);
                // Build multi-key label: "[K1 / K2 / K3]" or "[-]"
                var hkActiveKeys = hotkey.Keys != null
                    ? hotkey.Keys.Where(k => k > 0).Distinct().ToList()
                    : new List<int>();
                if (hkActiveKeys.Count == 0 && hotkey.Key > 0)
                    hkActiveKeys.Add(hotkey.Key);
                keyLbl = hkActiveKeys.Count > 0
                    ? "[" + string.Join(" / ", hkActiveKeys.Select(KeyBadgeLabel)) + "]"
                    : "[-]";
                keyTW  = MeasureText(bfont, bfsize, keyLbl).X;
            }

            // ── Truncate label to fit within the column/group boundary ────────────
            // Inside BeginColumns, ColWidth is the per-column width; outside it's the
            // full group or content-region width. Reserve space for the badge if present.
            float rowW      = ColWidth > 0f ? ColWidth : avail;
            float badgeRes  = hotkey != null ? keyTW + hitPadX * 2f + 6f + 4f : 4f;
            float maxLabelW = MathF.Max(0f, rowW - (lblX - tl.X) - badgeRes);
            string dispLabel  = TruncateText(font, fsize, Loc(label), maxLabelW);
            float  dispLabelW = MeasureText(font, fsize, dispLabel).X;

            if (hotkey != null)
            {
                // Badge sits inline, immediately right of the (possibly truncated) label text.
                float bTLx = lblX + dispLabelW + 6f;
                float bBRx = bTLx + keyTW + hitPadX * 2f;
                float bY   = MathF.Floor(tl.Y + (rowH - bfsize) * 0.5f) - hitPadY;
                bTL = new(bTLx, bY);
                bBR = new(bBRx, bY + bfsize + hitPadY * 2f);
                preBadgeHov = !IsDisabled && ImGuiNET.ImGui.IsMouseHoveringRect(bTL, bBR, true);
            }

            ImGuiNET.ImGui.InvisibleButton(label, new Vector2(avail, rowH));
            bool clicked = !IsDisabled && ImGuiNET.ImGui.IsItemClicked() && !preBadgeHov;
            bool hovered = !IsDisabled && ImGuiNET.ImGui.IsItemHovered() && !preBadgeHov;
            uint id      = ImGuiNET.ImGui.GetItemID();

            if (clicked) value = !value;

            float tv = Anim(id,         value   ? 1f : 0f, 18f);  // check fill + tick
            float th = Anim(id ^ 0xAAu, hovered ? 1f : 0f, 12f);  // hover brightness

            var dl = DL;

            // ── Hotkey badge ──────────────────────────────────────────────────────
            bool badgeChanged = false;
            if (hotkey != null)
                badgeChanged = DrawInlineHotkeyBadge(dl, id, hotkey, bTL, bBR, bfont, bfsize, keyLbl, keyTW, Strip(label));
            float cbY = MathF.Floor(tl.Y + (rowH - CbSize) * .5f);
            Vector2 cbTL = new(cbX, cbY);
            Vector2 cbBR = new(cbX + CbSize, cbY + CbSize);

            // Background: dark → fill as value animates in
            uint _cbBgDark  = Alpha(InputBg,  200);
            uint _cbBgLight = Alpha(InputBgH, 235);
            uint bgUnchecked = Lerp(_cbBgDark, _cbBgLight, th);
            uint bgChecked   = Lerp(ControlFill, BrighterColor(ControlFill, 0x33), th);
            uint bgCol       = DA(Lerp(bgUnchecked, bgChecked, tv));
            dl.AddRectFilled(cbTL, cbBR, bgCol, CbRound);

            // Border: dim → fill color on hover/checked
            uint bordCol = DA(Lerp(Alpha(BorderCol, 160), Alpha(ControlFill, 220), MathF.Max(th, tv)));
            dl.AddRect(cbTL, cbBR, bordCol, CbRound, ImDrawFlags.None, 1.5f);

            // Tick mark draws as tv animates from 0→1
            if (tv > 0.05f)
            {
                // Classic tick: short leg bottom-left, long leg bottom-right up
                float pad  = 3.5f;
                float midX = cbX + CbSize * 0.35f;
                float midY = cbY + CbSize * 0.65f;
                float p1x  = cbX + pad;
                float p1y  = cbY + CbSize * 0.52f;
                float p3x  = cbX + CbSize - pad;
                float p3y  = cbY + pad + 1f;

                uint tickCol = DA(Alpha(0xFFFFFFFFu, (byte)(255 * MathF.Min(tv * 2f, 1f))));
                dl.AddLine(new Vector2(p1x, p1y), new Vector2(midX, midY), tickCol, 1.8f);
                dl.AddLine(new Vector2(midX, midY), new Vector2(p3x, p3y), tickCol, 1.8f);
            }

            // Label - truncated to fit within the column/group boundary
            float lblY = MathF.Round(cbY + (CbSize - fsize) * .5f) + 1f; // +1 compensates for cap-height optical offset
            uint lCol = risky
                ? (locExtraRiskyWarning != null
                    ? DA(Lerp(ImGuiStyle.Danger, BrighterColor(ImGuiStyle.Danger, 0x33), th))
                    : DA(Lerp(ImGuiStyle.Col(0xFF, 0xAA, 0x22), ImGuiStyle.Col(0xFF, 0xCC, 0x55), th)))
                : DA(Lerp(TxtNorm, 0xFFFFFFFF, th));
            dl.AddText(font, fsize, new Vector2(lblX, lblY), lCol, dispLabel);

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
            DrawSearchHighlight(Strip(label), _lastRowTL, _lastRowBR);
            // Shrink the hint-hover region to the label area only so that external
            // UI.Tooltip() calls after this control don't fire when the badge is hovered.
            if (hotkey != null) _lastRowBR = new Vector2(bTL.X, tl.Y + rowH);
            if (hovered && (hint != null || locRiskyWarning != null || locExtraRiskyWarning != null))
                TooltipWithRiskyWarning(hint, locRiskyWarning, locExtraRiskyWarning);
            if (IsDisabled) _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
            ShowDisabledReasonTooltip();
            return clicked || badgeChanged;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Draws an inline hotkey badge supporting multiple bound keys per action.
        //   Left-click  → APPEND capture: press a key to add it to the list.
        //   Right-click → popup: mode (Toggle/Hold), per-key remove buttons, Clear All.
        //   ESC while capturing → cancel without clearing existing keys.
        // selfLabel is the human-readable label of the parent Toggle row, used to
        // exclude this entry from its own conflict tooltip.
        // Returns true when the entry was modified this frame.
        private static bool DrawInlineHotkeyBadge(
            ImDrawListPtr dl, uint rowId, HotkeyEntry entry,
            Vector2 bTL, Vector2 bBR, ImFontPtr bfont, float bfsize,
            string keyLbl, float keyTW, string selfLabel, bool insidePopup = false)
        {
            bool changed     = false;
            bool isCapturing = _captureBadgeId == rowId;
            string popupId   = $"##hkmode_{rowId}";
            const float hitPadX = 3f;

            // ── Hit detection ────────────────────────────────────────────────────
            bool badgeHov  = !IsDisabled && ImGuiNET.ImGui.IsMouseHoveringRect(bTL, bBR, true);
            bool popupOpen = ImGuiNET.ImGui.IsPopupOpen(popupId);

            // Left-click → start APPEND capture (any key pressed will be added to Keys).
            // Guard: do not start capture when the popup is already open - clicks inside the
            // popup are geometrically inside the badge rect and would accidentally trigger capture.
            if (!isCapturing && !popupOpen && badgeHov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _captureBadgeId     = rowId;
                _captureWaitRelease = true;
                isCapturing         = true;
            }

            // Right-click → open manage-popup. If capture is active, cancel it first so the
            // two modes never run simultaneously.
            if (badgeHov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                if (isCapturing)
                {
                    _captureBadgeId = 0;
                    isCapturing     = false;
                }
                ImGuiNET.ImGui.OpenPopup(popupId);
            }

            // ── Key capture polling (APPEND mode) ────────────────────────────────
            if (isCapturing)
            {
                if (_captureWaitRelease)
                {
                    if (!IsVkDown(0x01)) _captureWaitRelease = false;
                }
                else if (IsVkDown(0x1B))  // ESC → cancel without clearing existing keys
                {
                    _captureBadgeId = 0;
                    isCapturing     = false;
                }
                else if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !badgeHov)
                {
                    // Click outside the badge → cancel capture
                    _captureBadgeId = 0;
                    isCapturing     = false;
                }
                else
                {
                    for (int vk = 1; vk <= 0xFF; vk++)
                    {
                        // Skip modifier keys and ESC (ESC is handled separately above; LMB is
                        // gated by the wait-for-release logic so the opening click is never
                        // captured - mouse buttons including X1/X2 are otherwise valid hotkeys).
                        if (vk is 0x10 or 0x11 or 0x12 or 0x1B) continue;
                        if (!IsVkDown(vk)) continue;
                        entry.Keys ??= new List<int>();
                        entry.AddKey(vk);
                        if (entry.Key <= 0) entry.Key = vk;  // keep legacy field pointing at first key
                        entry.Enabled   = true;
                        _captureBadgeId = 0;
                        isCapturing     = false;
                        changed         = true;
                        break;
                    }
                }
            }

            // ── Mode + key-management popup ──────────────────────────────────────
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.ContextMenuBg, 252)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(BorderCol, 180)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,  new Vector2(0f, 4f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,  6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,    new Vector2(0f, 1f));
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(170f, 0f), new Vector2(260f, 320f));
            if (ImGuiNET.ImGui.BeginPopup(popupId))
            {
                float pw    = ImGuiNET.ImGui.GetContentRegionAvail().X;
                var   pdl   = DL;
                var   pfont = ImGuiStyle.FontNormal;
                float pfs   = pfont.FontSize > 0f ? pfont.FontSize : 13f;
                float rowH  = pfs + 10f;

                // Pre-measure fixed-width elements used in every key row.
                string xMark = "×";
                float  xw    = MeasureText(pfont, pfs, xMark).X;
                string tLbl  = LocManager.Get("Toggle");
                string hLbl  = LocManager.Get("On Key");
                float  tLblW = MeasureText(pfont, pfs, tLbl).X;
                float  hLblW = MeasureText(pfont, pfs, hLbl).X;
                float  modeSepW = MeasureText(pfont, pfs, " · ").X;
                // Right-to-left: ×(xw) + 8 gap | T(tLblW) + modeSepW + OK(hLblW) + 8 gap | key name
                float xBtnX0  = pw - xw - 8f;           // left edge of ×
                float tLblX   = xBtnX0 - tLblW - 8f;   // left edge of "Toggle" (right mode)
                float sepX    = tLblX - modeSepW;        // left edge of " · "
                float hLblX   = sepX - hLblW;            // left edge of "On Key" (left mode)

                // ── Per-key rows ──────────────────────────────────────────────────
                // This entry must always be driven by hold state (see HotkeyManager.
                // IsOnKeyOnlyEntry) - e.g. an engaged aimbot can't be allowed to latch
                // permanently on via Toggle - so its Toggle zone is inert here too.
                bool onKeyOnly  = false; // SC2: HotkeyManager.IsOnKeyOnlyEntry not used
                var popupKeys = entry.Keys?.Where(k => k > 0).Distinct().ToList() ?? new List<int>();
                foreach (var vk in popupKeys)
                {
                    string kLbl   = KeyBadgeLabel(vk);
                    Vector2 rTL   = ImGuiNET.ImGui.GetCursorScreenPos();
                    ImGuiNET.ImGui.InvisibleButton($"##hkr_{vk}_{rowId}", new Vector2(pw, rowH));
                    bool rClk = ImGuiNET.ImGui.IsItemClicked();
                    bool rHov = ImGuiNET.ImGui.IsItemHovered();
                    float rAn = Anim(rowId ^ (uint)(vk * 0x9E3779B9u & 0x7FFFFFFF),
                                     rHov ? 1f : 0f, 14f);

                    // Dispatch click based on mouse x-position within the row.
                    if (rClk)
                    {
                        float mx = ImGuiNET.ImGui.GetMousePos().X - rTL.X;
                        if (mx >= xBtnX0 - 4f)
                        {
                            // × zone - remove this key
                            entry.Keys.Remove(vk);
                            entry.KeyModes?.Remove(vk);
                            if (entry.Key == vk)
                                entry.Key = entry.Keys.Count > 0 ? entry.Keys[0] : -1;
                            if (entry.Keys.Count == 0) entry.Enabled = false;
                            changed = true;
                            ImGuiNET.ImGui.CloseCurrentPopup();
                        }
                        else if (mx >= hLblX - 4f && mx < sepX + modeSepW * 0.5f)
                        {
                            // On Key zone (left)
                            if (entry.KeyModes == null) entry.KeyModes = new();
                            entry.KeyModes[vk] = HotkeyMode.OnKey;
                            changed = true;
                        }
                        else if (!onKeyOnly && mx >= sepX + modeSepW * 0.5f && mx < xBtnX0 - 4f)
                        {
                            // Toggle zone (right) - disabled entirely for onKeyOnly entries
                            if (entry.KeyModes == null) entry.KeyModes = new();
                            entry.KeyModes[vk] = HotkeyMode.Toggle;
                            changed = true;
                        }
                    }

                    // Sub-region hover detection for mode labels (visual feedback only).
                    float relMx = rHov ? ImGuiNET.ImGui.GetMousePos().X - rTL.X : -1f;
                    bool hoveringH = relMx >= hLblX - 4f && relMx < sepX + modeSepW * 0.5f;
                    bool hoveringToggleZone = relMx >= sepX + modeSepW * 0.5f && relMx < xBtnX0 - 4f;
                    bool hoveringT = !onKeyOnly && hoveringToggleZone;
                    bool hoveringX = relMx >= xBtnX0 - 4f;

                    uint rowTint = hoveringX
                        ? Alpha(ImGuiStyle.Col(0xFF, 0x44, 0x44), (byte)(22 * rAn))
                        : Alpha(Accent, (byte)(15 * rAn));
                    if (rAn > 0.01f)
                        pdl.AddRectFilled(new Vector2(rTL.X - 1f, rTL.Y),
                                          new Vector2(rTL.X + pw + 1f, rTL.Y + rowH), rowTint, 0f);

                    float ty = rTL.Y + (rowH - pfs) * 0.5f;

                    // Key name (amber if conflicted).
                    bool vkIsConflicted = _hkConflictMap.TryGetValue(vk, out var vkConflictOthers)
                        && vkConflictOthers.Any(e => !ReferenceEquals(e, entry));
                    uint keyRowCol = vkIsConflicted
                        ? DA(Lerp(Alpha(ImGuiStyle.Col(0xFF, 0xAA, 0x22), 200),
                                  ImGuiStyle.Col(0xFF, 0xCC, 0x55), rAn))
                        : DA(Lerp(Alpha(TxtNorm, 160), 0xFFFFFFFF, rAn));
                    pdl.AddText(pfont, pfs, new Vector2(rTL.X + 10f, ty), keyRowCol, $"[{kLbl}]");

                    // Mode picker: On Key · Toggle
                    HotkeyMode keyMode = entry.GetKeyMode(vk);
                    bool isTgl = !onKeyOnly && keyMode == HotkeyMode.Toggle;

                    uint hCol = (!isTgl || hoveringH)
                        ? DA(Lerp(!isTgl ? Accent : Alpha(TxtNorm, 100), !isTgl ? AccentHover : Alpha(TxtNorm, 200), rAn))
                        : DA(Alpha(TxtDim, 80));
                    // Toggle label stays permanently dim (unselectable look) for onKeyOnly entries.
                    uint tCol = onKeyOnly
                        ? DA(Alpha(TxtDim, 45))
                        : (isTgl || hoveringT)
                            ? DA(Lerp(isTgl ? Accent : Alpha(TxtNorm, 100), isTgl ? AccentHover : Alpha(TxtNorm, 200), rAn))
                            : DA(Alpha(TxtDim, 80));
                    uint dotCol = DA(Alpha(TxtDim, 60));

                    pdl.AddText(pfont, pfs, new Vector2(rTL.X + hLblX, ty), hCol, hLbl);
                    pdl.AddText(pfont, pfs, new Vector2(rTL.X + sepX,   ty), dotCol, " · ");
                    pdl.AddText(pfont, pfs, new Vector2(rTL.X + tLblX, ty), tCol, tLbl);

                    // Delete ×
                    uint xCol = DA(Lerp(Alpha(ImGuiStyle.Col(0xFF, 0x55, 0x55), 160),
                                        ImGuiStyle.Col(0xFF, 0x88, 0x88), hoveringX ? rAn : 0f));
                    pdl.AddText(pfont, pfs, new Vector2(rTL.X + xBtnX0, ty), xCol, xMark);

                    // Disabled-mode tooltip - explains why Toggle can't be picked for this entry.
                    if (onKeyOnly && rHov && hoveringToggleZone)
                        ShowStyledTooltipInPopup(() =>
                            ImGuiNET.ImGui.TextDisabled(LocManager.Get("This hotkey type is disabled for this hotkey")));

                    // Conflict tooltip.
                    if (rHov && !hoveringT && !hoveringH && !hoveringX
                        && _hkConflictMap.TryGetValue(vk, out var vkShared))
                    {
                        var otherLabels = vkShared
                            .Where(e => !ReferenceEquals(e, entry))
                            .Select(e => !string.IsNullOrEmpty(e.DisplayLabel)
                                ? LocManager.Get(e.DisplayLabel) : LocManager.Get("(unknown)"))
                            .Distinct().ToList();
                        if (otherLabels.Count > 0)
                        {
                            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f));
                            ImGuiNET.ImGui.BeginTooltip();
                            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,
                                new Vector4(1f, 0.75f, 0.13f, 1f));
                            ImGuiNET.ImGui.TextUnformatted(
                                LocManager.Get("Also triggers: ") + string.Join(", ", otherLabels));
                            ImGuiNET.ImGui.PopStyleColor();
                            ImGuiNET.ImGui.EndTooltip();
                            ImGuiNET.ImGui.PopStyleVar();
                        }
                    }
                }

                // ── Empty state placeholder ───────────────────────────────────────
                if (popupKeys.Count == 0)
                {
                    Vector2 noKeyTL = ImGuiNET.ImGui.GetCursorScreenPos();
                    ImGuiNET.ImGui.InvisibleButton($"##hknone_{rowId}", new Vector2(pw, rowH));
                    pdl.AddText(pfont, pfs,
                        new Vector2(noKeyTL.X + 10f, noKeyTL.Y + (rowH - pfs) * 0.5f),
                        DA(Alpha(TxtDim, 110)), LocManager.Get("No keys assigned"));
                }

                // ── Clear All row (only shown when keys exist) ────────────────────
                if (popupKeys.Count > 0)
                {
                    // Thin separator before Clear All
                    Vector2 sepPos = ImGuiNET.ImGui.GetCursorScreenPos();
                    ImGuiNET.ImGui.Dummy(new Vector2(pw, 5f));
                    pdl.AddLine(
                        new Vector2(sepPos.X + 6f, sepPos.Y + 2f),
                        new Vector2(sepPos.X + pw - 6f, sepPos.Y + 2f),
                        DA(Alpha(BorderCol, 70)), 1f);

                    Vector2 clrTL = ImGuiNET.ImGui.GetCursorScreenPos();
                    ImGuiNET.ImGui.InvisibleButton($"##hkclr_{rowId}", new Vector2(pw, rowH));
                    bool clrClk = ImGuiNET.ImGui.IsItemClicked();
                    bool clrHov = ImGuiNET.ImGui.IsItemHovered();
                    float clrAn = Anim(rowId ^ 0xDEADBEEFu, clrHov ? 1f : 0f, 14f);

                    if (clrClk)
                    {
                        entry.Key = -1;
                        entry.Keys.Clear();
                        entry.KeyModes?.Clear();
                        entry.Enabled = false;
                        changed = true;
                        ImGuiNET.ImGui.CloseCurrentPopup();
                    }

                    if (clrAn > 0.01f)
                        pdl.AddRectFilled(new Vector2(clrTL.X - 1f, clrTL.Y),
                                          new Vector2(clrTL.X + pw + 1f, clrTL.Y + rowH),
                                          Alpha(ImGuiStyle.Col(0xFF, 0x44, 0x44), (byte)(22 * clrAn)), 0f);

                    float clrTy = clrTL.Y + (rowH - pfs) * 0.5f;
                    pdl.AddText(pfont, pfs, new Vector2(clrTL.X + 10f, clrTy),
                        DA(Lerp(Alpha(ImGuiStyle.Col(0xFF, 0x55, 0x44), 160),
                                ImGuiStyle.Col(0xFF, 0x88, 0x66), clrAn)), LocManager.Get("Clear All"));
                }

                ImGuiNET.ImGui.EndPopup();
            }
            ImGuiNET.ImGui.PopStyleVar(3);
            ImGuiNET.ImGui.PopStyleColor(2);

            // ── Text-label rendering ─────────────────────────────────────────────
            // Re-derive active keys AFTER capture polling so an assignment this frame
            // is immediately reflected without a one-frame blink.
            var activeKeys = entry.Keys != null
                ? entry.Keys.Where(k => k > 0).Distinct().ToList()
                : new List<int>();
            if (activeKeys.Count == 0 && entry.Key > 0) activeKeys.Add(entry.Key);
            bool hasKey = activeKeys.Count > 0;

            // Conflict detection: uses reference equality - no label-matching, no false positives.
            List<string> conflictsWith = null;
            foreach (var vk in activeKeys)
            {
                if (_hkConflictMap.TryGetValue(vk, out var others))
                {
                    foreach (var other in others)
                    {
                        if (ReferenceEquals(other, entry)) continue;  // skip self
                        string lbl = !string.IsNullOrEmpty(other.DisplayLabel)
                            ? LocManager.Get(other.DisplayLabel) : LocManager.Get("(unknown)");
                        conflictsWith ??= new List<string>();
                        if (!conflictsWith.Contains(lbl)) conflictsWith.Add(lbl);
                    }
                }
            }
            bool hasConflict = conflictsWith != null;

            float hAnim = Anim(rowId ^ 0xC0FFEE01u, badgeHov || isCapturing ? 1f : 0f, 14f);

            // Compute total label width for right-anchored positioning (same string as precompute).
            string displayLbl;
            if (isCapturing)
                displayLbl = hasKey
                    ? "[" + string.Join(" / ", activeKeys.Select(KeyBadgeLabel)) + " / ...]"
                    : "[...]";
            else
                displayLbl = hasKey
                    ? "[" + string.Join(" / ", activeKeys.Select(KeyBadgeLabel)) + "]"
                    : "[-]";

            float lw      = MeasureText(bfont, bfsize, displayLbl).X;
            float cx      = bBR.X - hitPadX - lw;   // left edge of first character
            float badgeTy = bTL.Y + ((bBR.Y - bTL.Y) - bfsize) * 0.5f;

            // Per-key segment colors: only keys that share a vk with another entry go amber.
            uint bracketCol  = DA(Lerp(Alpha(TxtDim, 110), Alpha(TxtNorm, 160), hAnim));
            uint sepCol      = DA(Lerp(Alpha(TxtDim, 70),  Alpha(TxtNorm, 110), hAnim));
            uint normalCol   = DA(Lerp(Alpha(TxtNorm, 160), Alpha(AccentHover, 230), hAnim));
            uint noKeyCol    = DA(Lerp(Alpha(TxtDim, 90),  Alpha(TxtNorm, 160), hAnim));
            uint capturCol   = DA(Lerp(Alpha(Accent, 190), AccentHover, hAnim));
            uint conflictCol = DA(Lerp(Alpha(ImGuiStyle.Col(0xFF, 0xAA, 0x22), 200),
                                       ImGuiStyle.Col(0xFF, 0xCC, 0x55), hAnim));

            void Seg(string s, uint color)
            {
                dl.AddText(bfont, bfsize, new Vector2(cx, badgeTy), color, s);
                cx += MeasureText(bfont, bfsize, s).X;
            }

            Seg("[", bracketCol);
            if (!hasKey)
            {
                Seg(isCapturing ? "..." : "-", isCapturing ? capturCol : noKeyCol);
            }
            else
            {
                for (int ki = 0; ki < activeKeys.Count; ki++)
                {
                    if (ki > 0) Seg(" / ", sepCol);
                    bool kConflict = _hkConflictMap.TryGetValue(activeKeys[ki], out var kOthers)
                        && kOthers.Any(e => !ReferenceEquals(e, entry));
                    Seg(KeyBadgeLabel(activeKeys[ki]), kConflict ? conflictCol : normalCol);
                }
                if (isCapturing) { Seg(" / ", sepCol); Seg("...", capturCol); }
            }
            Seg("]", bracketCol);

            if (badgeHov && !isCapturing && !popupOpen)
            {
                ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                void TooltipContent()
                {
                    if (hasConflict)
                    {
                        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.13f, 1f));
                        ImGuiNET.ImGui.TextUnformatted(
                            LocManager.Get("Also bound to: ") + string.Join(", ", conflictsWith));
                        ImGuiNET.ImGui.PopStyleColor();
                    }
                    ImGuiNET.ImGui.TextDisabled(LocManager.Get("Click to add key  |  Right-click to manage"));
                }
                if (insidePopup)
                    ShowStyledTooltipInPopup(TooltipContent);
                else
                    ShowStyledTooltip(TooltipContent);
            }
            if (isCapturing)
            {
                ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ShowStyledTooltip(() =>
                    ImGuiNET.ImGui.TextDisabled(LocManager.Get("Press any key to add  |  ESC to cancel")));
            }

            if (changed) OnHotkeyChanged?.Invoke();
            return changed;
        }

        /// <summary>
        /// Standalone hotkey badge - e.g. for binding a hotkey to a dynamic list item
        /// (a loot filter, a saved preset, ...) that isn't attached to a Toggle row.
        /// Reserves a fixed-width slot (same left-click-capture / right-click-manage /
        /// ESC-cancel behavior as every other hotkey badge in the app) and right-aligns
        /// the "[Key]" label within it, same as PopoverHotkeySection's rows.
        /// Returns true when the binding was modified this frame.
        /// </summary>
        public static bool HotkeyBadge(string id, HotkeyEntry hotkey, string displayLabel, float width = 44f)
        {
            if (hotkey == null) return false;

            var   bfont  = ImGuiStyle.FontSmall;
            float bfsize = bfont.FontSize > 0f ? bfont.FontSize : 10f;
            float rowH   = ComboH;
            const float hitPadX = 3f, hitPadY = 2f;

            hotkey.DisplayLabel = displayLabel;
            var hkActiveKeys = hotkey.Keys != null
                ? hotkey.Keys.Where(k => k > 0).Distinct().ToList()
                : new List<int>();
            if (hkActiveKeys.Count == 0 && hotkey.Key > 0)
                hkActiveKeys.Add(hotkey.Key);
            string keyLbl = hkActiveKeys.Count > 0
                ? "[" + string.Join(" / ", hkActiveKeys.Select(KeyBadgeLabel)) + "]"
                : "[-]";
            float keyTW = MeasureText(bfont, bfsize, keyLbl).X;

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.InvisibleButton(id, new Vector2(width, rowH));
            uint rowId = ImGuiNET.ImGui.GetItemID();

            float bBRx = tl.X + width;
            float bTLx = bBRx - keyTW - hitPadX * 2f;
            float bY   = MathF.Floor(tl.Y + (rowH - bfsize) * 0.5f) - hitPadY;
            Vector2 bTL = new(bTLx, bY);
            Vector2 bBR = new(bBRx, bY + bfsize + hitPadY * 2f);

            return DrawInlineHotkeyBadge(DL, rowId, hotkey, bTL, bBR, bfont, bfsize, keyLbl, keyTW, displayLabel);
        }

        private static bool IsVkDown(int vk) => InputManager.IsKeyDownAny(vk);   // this PC's keyboard, or the gaming PC's over DMA

        // ───────────────────────────────────────────────────────────────────────────
        // SLIDER GROUP  - aligns label columns across multiple sliders
        //
        //  UI.BeginSliderGroup("Label A", "Longer label B", "C");
        //  UI.SliderFloat("Label A##g1", ...);
        //  UI.SliderFloat("Longer label B##g2", ...);
        //  UI.SliderInt  ("C##g3",       ...);
        //  UI.EndSliderGroup();
        // ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Call before a set of sliders. Pass the label strings (without ##id suffix) so
        /// the widest label can be measured and used as a shared column width.
        /// </summary>
        public static void BeginSliderGroup(params string[] labels)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float maxW  = 0f;
            foreach (string l in labels)
            {
                string clean = Strip(l);
                if (string.IsNullOrEmpty(clean)) continue;
                float w = MeasureText(font, fsize, clean).X + 10f;
                if (w > maxW) maxW = w;
            }
            _groupLabelColW = maxW;
        }

        /// <summary>Clears the shared label column width set by BeginSliderGroup.</summary>
        public static void EndSliderGroup() => _groupLabelColW = 0f;

        /// <summary>
        /// Call before a set of InputInt/InputFloat controls. Pass the label strings
        /// (without ##id suffix) so the widest label sets a shared column width and
        /// all spinners left-align their input boxes.
        /// </summary>
        public static void BeginNumericGroup(params string[] labels)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float maxW  = 0f;
            foreach (string l in labels)
            {
                string clean = Strip(l);
                if (string.IsNullOrEmpty(clean)) continue;
                float w = MeasureText(font, fsize, clean).X + 12f;
                if (w > maxW) maxW = w;
            }
            _numLabelColW = maxW;
        }

        /// <summary>Clears the shared label column width set by BeginNumericGroup.</summary>
        public static void EndNumericGroup() => _numLabelColW = 0f;

        // ───────────────────────────────────────────────────────────────────────────
        // SLIDER FLOAT / INT
        //
        // Matches TextValueSlider.xaml layout:
        //   - Full-width track, no thumb circle
        //   - Value text floats ~8.5 px above the track centre, centred on the fill edge
        //   - Black drop-shadow behind value text keeps it readable over the fill
        //   - suffix   : unit appended after the number  ("-", "m", "px", "x", etc.)
        //   - minText  : shown instead of the number when value == min  ("Off", "None", -)
        //   - maxText  : shown instead of the number when value == max  ("Max", "-", -)
        // ───────────────────────────────────────────────────────────────────────────

        public static bool SliderFloat(string label, ref float value, float min, float max,
            string fmt = "%.1f", string suffix = "", string minText = "", string maxText = "",
            float width = 0f, float smallChange = 0f, string hint = null)
            => DoSlider(label, ref value, min, max, fmt, suffix, minText, maxText, width,
                smallChange > 0f ? smallChange : (max - min) * 0.01f, v => v, hint: hint);

        public static bool SliderInt(string label, ref int value, int min, int max,
            string suffix = "", string minText = "", string maxText = "",
            float width = 0f, int smallChange = 1, bool abbreviated = false, string hint = null, int snapStep = 1)
        {
            float f = value;
            Func<float, float> snap = snapStep > 1
                ? v => MathF.Round(v / snapStep) * snapStep
                : MathF.Round;
            bool  c = DoSlider(label, ref f, min, max, "%.0f", suffix, minText, maxText,
                width, smallChange, snap, abbreviated, hint);
            if (c) value = (int)(MathF.Round(f / snapStep) * snapStep);
            return c;
        }

        // Vertical slider - no label, fill-bar style matching DoSlider, bottom = min, top = max.
        // Sized explicitly; call SetCursorPos before to position it.
        public static bool VSliderFloat(string id, Vector2 size, ref float value, float min, float max,
            string fmt = "%.1f", string suffix = "", float wheelStep = 0f, string hint = null)
        {
            Vector2 tl    = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.InvisibleButton(id, size);
            uint  itemId  = ImGuiNET.ImGui.GetItemID();
            bool  hov     = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            bool  act     = !IsDisabled && ImGuiNET.ImGui.IsItemActive();
            float th      = Anim(itemId, hov || act ? 1f : 0f);
            bool  changed = false;

            if (act && size.Y > 0f)
            {
                float my   = ImGuiNET.ImGui.GetIO().MousePos.Y;
                float norm = Math.Clamp((my - tl.Y) / size.Y, 0f, 1f);
                float nv   = Math.Clamp(min + (1f - norm) * (max - min), min, max);
                if (MathF.Abs(nv - value) > 1e-6f) { value = nv; changed = true; }
            }

            if (hov)
            {
                float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
                if (wheel != 0f)
                {
                    float step = wheelStep > 0f ? wheelStep : (max - min) * 0.05f;
                    float nv   = Math.Clamp(value + wheel * step, min, max);
                    if (MathF.Abs(nv - value) > 1e-6f) { value = nv; changed = true; }
                    ImGuiNET.ImGui.GetIO().MouseWheel = 0f;
                    MarkWheelConsumed();
                }
            }

            var   dl     = DL;
            float n01    = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
            if (act) _anim[itemId ^ 0xBBu] = n01;
            float n01Vis = Anim(itemId ^ 0xBBu, n01, 10f);

            // Track centred horizontally within the allocated width, matching DoSlider's TrackH
            float trackX  = tl.X + (size.X - TrackH) * 0.5f;
            float trackY0 = tl.Y;
            float trackY1 = tl.Y + size.Y;
            float fillY   = trackY1 - n01Vis * (trackY1 - trackY0);

            // Unfilled track (dark pill)
            dl.AddRectFilled(
                new Vector2(trackX, trackY0), new Vector2(trackX + TrackH, trackY1),
                DA(Lerp(Alpha(0xFF000000, 90), Alpha(0xFF000000, 120), th)), TrackRound);

            // Accent fill (bottom to fillY)
            if (fillY < trackY1 - 0.5f)
            {
                uint  fillCol    = DA(act ? AccentActive : Lerp(Accent, AccentHover, th));
                float clampFillY = Math.Max(fillY, trackY0);
                dl.AddRectFilled(
                    new Vector2(trackX, clampFillY), new Vector2(trackX + TrackH, trackY1),
                    fillCol, TrackRound);

                // Shimmer line along the left edge of the fill (mirrors DoSlider's top shimmer)
                if (trackY1 - clampFillY > TrackRound * 2f)
                    dl.AddLine(
                        new Vector2(trackX + 1f, trackY1 - TrackRound),
                        new Vector2(trackX + 1f, clampFillY + 1f),
                        DA(Alpha(0xFFFFFFFF, (byte)(40 + (int)(30 * th)))), 1f);
            }

            // Value text - centred horizontally in size.X, vertically at the fill boundary.
            // Same font, shadow, and white foreground as DoSlider's value text.
            var    font    = ImGuiStyle.FontNormal;
            float  fsize   = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            string display = BuildDisplayString(value, min, max, fmt, suffix, "", "", false);
            Vector2 valSz  = MeasureText(font, fsize, display);
            float valX = tl.X + (size.X - valSz.X) * 0.5f;
            float valY = Math.Clamp(fillY - valSz.Y * 0.5f, trackY0, trackY1 - valSz.Y);

            uint shadow = DA(Alpha(0xFF000000, 200));
            dl.AddText(font, fsize, new Vector2(valX - 1f, valY),     shadow, display);
            dl.AddText(font, fsize, new Vector2(valX + 1f, valY),     shadow, display);
            dl.AddText(font, fsize, new Vector2(valX,     valY - 1f), shadow, display);
            dl.AddText(font, fsize, new Vector2(valX,     valY + 1f), shadow, display);
            dl.AddText(font, fsize, new Vector2(valX,     valY),      DA(0xFFFFFFFF), display);

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + size.X, tl.Y + size.Y);
            if (hint != null) Tooltip(hint);
            ShowDisabledReasonTooltip();
            return changed;
        }

        // PROGRESS BAR - read-only fill bar, same visual language as DoSlider's
        // track (dark unfilled track, Accent fill, subtle top shimmer) but with no
        // interaction at all (no drag/click/wheel - progress isn't user-settable).
        // Always pill-shaped (corner radius = height / 2) rather than TrackRound,
        // since callers pick their own height and a shallow fixed radius looks odd
        // at anything much thicker than the standard slider track.

        /// <summary>
        /// Draws a horizontal, read-only progress bar. <paramref name="fraction01"/> is
        /// clamped to 0..1. <paramref name="overlayText"/> (e.g. "42%"), if given, is centered
        /// over the whole bar with a black drop-shadow for legibility against either the fill
        /// or the bare track.
        /// </summary>
        public static void ProgressBar(float fraction01, float width = 0f, float height = 0f, string overlayText = null)
        {
            float w = width  > 0f ? width  : ImGuiNET.ImGui.GetContentRegionAvail().X;
            float h = height > 0f ? height : TrackH;
            float n01 = Math.Clamp(fraction01, 0f, 1f);
            float round = h * 0.5f;

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.Dummy(new Vector2(w, h));

            var dl = DL;
            float tx0 = tl.X, tx1 = tl.X + w;
            float fillX = tx0 + n01 * w;

            dl.AddRectFilled(new Vector2(tx0, tl.Y), new Vector2(tx1, tl.Y + h), DA(Alpha(0xFF000000, 90)), round);

            if (fillX > tx0 + 0.5f)
            {
                float clampedFillX = Math.Min(fillX, tx1);
                dl.AddRectFilled(new Vector2(tx0, tl.Y), new Vector2(clampedFillX, tl.Y + h), DA(Accent), round);

                if (clampedFillX > tx0 + round * 2f)
                    dl.AddLine(
                        new Vector2(tx0 + round, tl.Y + 1f),
                        new Vector2(clampedFillX - 1f, tl.Y + 1f),
                        DA(Alpha(0xFFFFFFFF, 50)), 1f);
            }

            if (!string.IsNullOrEmpty(overlayText))
            {
                var    font  = ImGuiStyle.FontNormal;
                float  fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                Vector2 sz   = MeasureText(font, fsize, overlayText);
                float tx = tx0 + (w - sz.X) * 0.5f;
                float ty = tl.Y + (h - fsize) * 0.5f;

                uint shadow = DA(Alpha(0xFF000000, 200));
                dl.AddText(font, fsize, new Vector2(tx - 1f, ty),      shadow, overlayText);
                dl.AddText(font, fsize, new Vector2(tx + 1f, ty),      shadow, overlayText);
                dl.AddText(font, fsize, new Vector2(tx,      ty - 1f), shadow, overlayText);
                dl.AddText(font, fsize, new Vector2(tx,      ty + 1f), shadow, overlayText);
                dl.AddText(font, fsize, new Vector2(tx, ty), DA(0xFFFFFFFF), overlayText);
            }

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tx1, tl.Y + h);
        }

        private static bool DoSlider(string label, ref float value, float min, float max,
            string fmt, string suffix, string minText, string maxText,
            float overW, float smallChange, Func<float, float> snap, bool abbreviated = false, string hint = null)
        {
            // ── Single-row layout matching the screenshot ─────────────────
            //
            //  [Label col ][ ────────────95-????????????????????????????? ]
            //               - track fills track column edge-to-edge
            //                         - value text sits just above the track
            //                           centre, inside the same row
            //
            //  Row height = max(labelFontH, valFontH + gap + TrackH) + vpad
            //  Everything shares the same vertical centre for the track.
            //  Label is in a fixed left column; track fills the rest.

            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            float w     = overW > 0f ? overW : avail;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            // Label column - use group-shared width when inside BeginSliderGroup,
            // otherwise measure per-slider so the label isn't needlessly wide.
            string cleanLabel = Loc(label);
            float  labelColW;
            if (_groupLabelColW > 0f)
            {
                labelColW = _groupLabelColW;
            }
            else if (!string.IsNullOrEmpty(cleanLabel))
            {
                labelColW = MeasureText(font, fsize, cleanLabel).X + 10f;
            }
            else
            {
                labelColW = 0f;
            }

            // Value display string
            string display = BuildDisplayString(value, min, max, fmt, suffix, minText, maxText, abbreviated);
            Vector2 valSz  = MeasureText(font, fsize, display);

            // Row height: enough for the font plus padding. Track stays thin (TrackH).
            // The value text overlaps the track visually - the track does not grow.
            const float vPad  = 6f;
            float trackHeight = TrackH;
            float rowH        = fsize + vPad;

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.InvisibleButton(label, new Vector2(w, rowH));
            uint id      = ImGuiNET.ImGui.GetItemID();
            bool hovered = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            bool active  = !IsDisabled && ImGuiNET.ImGui.IsItemActive();

            float th = Anim(id, hovered || active ? 1f : 0f);

            // Track centred vertically in the row
            // Clamp labelColW so the track always has at least some positive width
            labelColW = MathF.Min(labelColW, w - 20f);
            if (labelColW < 0f) labelColW = 0f;
            float trackTop = tl.Y + (rowH - trackHeight) * .5f;
            float tx0 = tl.X + labelColW;
            float tx1 = tl.X + w;
            float trackW = tx1 - tx0;

            // ── Click / Drag handling - single InvisibleButton, zone-aware ──
            // The left/right 15px zones act as decrement/increment (one step per click).
            // Anywhere else on the track drags to position.
            // We determine intent on the frame the button is first activated (IsItemActivated),
            // then latch it for the duration of the drag so the zone behaviour is consistent.
            const float ZoneW = 15f;
            bool changed = false;

            // Per-ID latch: true = this press is a zone click (not a drag)
            bool inZone = _sliderZoneLatch.TryGetValue(id, out bool latched) && latched;

            if (!IsDisabled && ImGuiNET.ImGui.IsItemActivated() && trackW > 0f)
            {
                float mx = ImGuiNET.ImGui.GetIO().MousePos.X;
                inZone = (mx < tx0 + ZoneW) || (mx > tx1 - ZoneW);
                _sliderZoneLatch[id] = inZone;

                if (inZone)
                {
                    // Apply the increment/decrement immediately on first press
                    float delta = (mx < tx0 + ZoneW) ? -smallChange : smallChange;
                    float nv    = snap(Math.Clamp(value + delta, min, max));
                    if (MathF.Abs(nv - value) > 1e-6f) { value = nv; changed = true; }
                }
            }

            if (!ImGuiNET.ImGui.IsItemActive())
                _sliderZoneLatch.Remove(id);

            // Mouse wheel - step by smallChange when hovered.
            // Zero io.MouseWheel immediately so nothing else in this frame
            // (drag code below, other widgets) can re-read and override the change.
            bool scrolledThisFrame = false;
            if (hovered)
            {
                var  _sio   = ImGuiNET.ImGui.GetIO();
                float wheel = _sio.MouseWheel;
                if (wheel != 0f)
                {
                    float nv = snap(Math.Clamp(value + wheel * smallChange, min, max));
                    if (MathF.Abs(nv - value) > 1e-6f) { value = nv; changed = true; }
                    _sio.MouseWheel    = 0f;  // consume - prevents drag from seeing stale wheel
                    scrolledThisFrame  = true;
                    MarkWheelConsumed();
                }
            }

            // Drag - only when not in a zone press and scroll didn't already change
            // the value this frame (drag would override the scroll with mouse-position value)
            if (active && !inZone && !scrolledThisFrame && trackW > 0f)
            {
                float norm = Math.Clamp((ImGuiNET.ImGui.GetIO().MousePos.X - tx0) / trackW, 0f, 1f);
                float nv   = snap(min + norm * (max - min));
                if (MathF.Abs(nv - value) > 1e-6f)
                {
                    value   = Math.Clamp(nv, min, max);
                    changed = true;
                }
            }

            if (changed)
            {
                display = BuildDisplayString(value, min, max, fmt, suffix, minText, maxText, abbreviated);
                valSz   = MeasureText(font, fsize, display);
            }

            var dl = DL;

            // ── Label (left column, vertically centred on track) ──────────
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                string dispLabel = TruncateText(font, fsize, cleanLabel, labelColW);
                dl.AddText(font, fsize,
                    new Vector2(tl.X + 2f, trackTop + (trackHeight - fsize) * .5f),
                    DA(Lerp(TxtNorm, 0xFFFFFFFF, th)), dispLabel);
            }

            float n01 = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;

            // Animate fill toward the actual value; snap during active drag so
            // the fill tracks the mouse precisely without visible lag.
            if (active) _anim[id ^ 0xBBu] = n01;
            float n01Vis = Anim(id ^ 0xBBu, n01, 10f);
            float fillX  = tx0 + n01Vis * trackW;

            // ── Unfilled track ────────────────────────────────────────────
            dl.AddRectFilled(
                new Vector2(tx0, trackTop), new Vector2(tx1, trackTop + trackHeight),
                DA(Lerp(Alpha(0xFF000000, 90), Alpha(0xFF000000, 120), th)), TrackRound);

            // ── Accent fill (left - fillX) ────────────────────────────────
            if (fillX > tx0 + 0.5f)
            {
                uint fillCol = DA(active ? AccentActive : Lerp(Accent, AccentHover, th));
                float clampedFillX = Math.Min(fillX, tx1);
                dl.AddRectFilled(
                    new Vector2(tx0, trackTop), new Vector2(clampedFillX, trackTop + trackHeight),
                    fillCol, TrackRound);

                if (clampedFillX > tx0 + TrackRound * 2f)
                    dl.AddLine(
                        new Vector2(tx0 + TrackRound, trackTop + 1f),
                        new Vector2(clampedFillX - 1f, trackTop + 1f),
                        DA(Alpha(0xFFFFFFFF, (byte)(40 + (int)(30 * th)))), 1f);
            }

            // ── Value text ────────────────────────────────────────────────
            float trackCY  = trackTop + trackHeight * .5f;
            float clampHi  = tx1 - valSz.X;
            float valX     = clampHi >= tx0
                ? Math.Clamp(fillX - valSz.X * .5f, tx0, clampHi)
                : tx0;
            float valY    = trackCY - fsize * .5f + 1f;

            uint shadow = DA(Alpha(0xFF000000, 200));
            dl.AddText(font, fsize, new Vector2(valX - 1f, valY),     shadow, display);
            dl.AddText(font, fsize, new Vector2(valX + 1f, valY),     shadow, display);
            dl.AddText(font, fsize, new Vector2(valX,     valY - 1f), shadow, display);
            dl.AddText(font, fsize, new Vector2(valX,     valY + 1f), shadow, display);
            dl.AddText(font, fsize, new Vector2(valX, valY), DA(0xFFFFFFFF), display);

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + w, tl.Y + rowH);
            DrawSearchHighlight(Strip(label), _lastRowTL, _lastRowBR);
            if (hint != null && hovered) Tooltip(hint);
            ShowDisabledReasonTooltip();
            return changed;
        }

        // Builds the display string: minText/maxText override, then number + suffix.
        // Compares formatted strings so a value like 0.9997 that rounds to "1.00" still
        // triggers maxText - matches the XAML CoerceValue Math.Round(value, 2) behaviour.
        private static string BuildDisplayString(float value, float min, float max,
            string fmt, string suffix, string minText, string maxText, bool abbreviated = false)
        {
            string num;
            if (abbreviated)
            {
                float abs = MathF.Abs(value);
                if (abs >= 1_000_000f)
                    num = (value / 1_000_000f).ToString("0.##") + "m";
                else if (abs >= 1_000f)
                    num = (value / 1_000f).ToString("0.##") + "k";
                else
                    num = ((int)value).ToString();
            }
            else
            {
                num = FormatValue(value, fmt);
            }

            if (!string.IsNullOrEmpty(minText) && FormatValue(value, fmt) == FormatValue(min, fmt))
                return LocManager.Get(minText);
            if (!string.IsNullOrEmpty(maxText) && FormatValue(value, fmt) == FormatValue(max, fmt))
                return LocManager.Get(maxText);

            return string.IsNullOrEmpty(suffix) ? num : num + suffix;
        }

        // ───────────────────────────────────────────────────────────────────────────
        // LABEL
        // ───────────────────────────────────────────────────────────────────────────

        public static void Label(string text, LabelStyle style = LabelStyle.Normal)
        {
            text = LocManager.Get(text);
            // colHover = base mixed 40% toward white - always visibly brighter regardless of base alpha
            static uint Brighten(uint c) => UI.Lerp(c, 0xFFFFFFFF, 0.40f);

            (ImFontPtr font, uint colBase, uint colHover) = style switch
            {
                LabelStyle.Dim     => (ImGuiStyle.FontNormal, TxtDim,             TxtNorm),
                LabelStyle.Bold    => (ImGuiStyle.FontBold,   TxtNorm,            Brighten(TxtNorm)),
                LabelStyle.Accent  => (ImGuiStyle.FontMedium, Accent,             AccentHover),
                LabelStyle.Danger  => (ImGuiStyle.FontNormal, ImGuiStyle.Danger,  Brighten(ImGuiStyle.Danger)),
                LabelStyle.Success => (ImGuiStyle.FontNormal, ImGuiStyle.Success, Brighten(ImGuiStyle.Success)),
                LabelStyle.Warning => (ImGuiStyle.FontNormal, ImGuiStyle.Warning, Brighten(ImGuiStyle.Warning)),
                _                  => (ImGuiStyle.FontNormal, TxtNorm,            Brighten(TxtNorm)),
            };

            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            Vector2 pos = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 sz  = MeasureText(font, fsize, text);

            // Wrap labels wider than the container onto multiple lines. A bare AddText
            // draws a single line that the panel's clip-rect then chops off mid-word at
            // the right edge (e.g. "…at x0m" losing ".dev"). Only long labels take this
            // path; short ones keep the original single-line width/height exactly.
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            bool  wrap  = font.FontSize > 0f && avail > 1f && sz.X > avail;

            float rowW, rowH;
            if (wrap)
            {
                rowW = avail;
                rowH = font.CalcTextSizeA(fsize, float.MaxValue, avail, text).Y;
            }
            else
            {
                rowW = sz.X;
                rowH = fsize;
            }

            ImGuiNET.ImGui.InvisibleButton("##lbl_" + text, new Vector2(rowW, rowH));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            uint id  = ImGuiNET.ImGui.GetItemID();
            float t  = Anim(id, hov ? 1f : 0f, 10f);

            uint col = DA(Lerp(colBase, colHover, t));
            if (wrap) DL.AddText(font, fsize, pos, col, text, avail);
            else      DL.AddText(font, fsize, pos, col, text);
            _lastRowTL = pos;
            _lastRowBR = new Vector2(pos.X + rowW, pos.Y + rowH);
        }

        // Property-sheet row: key right-aligned within labelWidth so colons stack,
        // value left-aligned after the colon. labelWidth=0 falls back to key-left/value-right.
        public static void KVRow(string key, string value, float width = 0f, float labelWidth = 0f, LabelStyle valueStyle = LabelStyle.Normal)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowW  = width > 0f ? width : (_groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X);
            float rowH  = fsize + 2f;
            Vector2 tl  = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.Dummy(new Vector2(rowW, rowH));

            var    dl       = DL;
            float  ty       = tl.Y + 1f;
            string keyColon = LocManager.Get(key) + ":";

            uint valCol = valueStyle switch
            {
                LabelStyle.Success => DA(Alpha(ImGuiStyle.Success, 220)),
                LabelStyle.Warning => DA(Alpha(ImGuiStyle.Warning, 220)),
                LabelStyle.Danger  => DA(Alpha(ImGuiStyle.Danger,  220)),
                LabelStyle.Dim     => DA(Alpha(TxtDim, 160)),
                _                  => DA(TxtNorm),
            };

            if (labelWidth > 0f)
            {
                Vector2 keySz = MeasureText(font, fsize, keyColon);
                dl.AddText(font, fsize, new Vector2(tl.X + labelWidth - keySz.X, ty), DA(Alpha(TxtDim, 200)), keyColon);
                dl.AddText(font, fsize, new Vector2(tl.X + labelWidth + 6f,      ty), valCol, value);
            }
            else
            {
                dl.AddText(font, fsize, new Vector2(tl.X, ty), DA(Alpha(TxtDim, 200)), keyColon);
                Vector2 valSz = MeasureText(font, fsize, value);
                dl.AddText(font, fsize, new Vector2(tl.X + rowW - valSz.X, ty), valCol, value);
            }
        }

        // Width of the current column when inside BeginColumns/EndColumns, else 0.
        public static float ColWidth => _colCount > 0 ? _colW : 0f;

        // Measures the widest "key:" string to use as labelWidth for a KVRow group.
        public static float KVLabelWidth(params string[] keys)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float max   = 0f;
            foreach (var k in keys)
            {
                float w = MeasureText(font, fsize, LocManager.Get(k) + ":").X;
                if (w > max) max = w;
            }
            return max;
        }

        //───────────────────────────────────────────────────────────────────────────
        // COLLAPSEABLE SECTION
        //
        //  bool open = true;
        //  if (UI.BeginSection("Toggles", ref open))
        //  {
        //      UI.Toggle(...);
        //  }
        //  UI.EndSection();
        //
        // The ref bool is your persistent open/closed state (field on the panel).
        // ───────────────────────────────────────────────────────────────────────────

        public static bool BeginSection(string text, ref bool open)
        {
            ImGuiNET.ImGui.Spacing();

            var     dl   = DL;
            Vector2 pos  = ImGuiNET.ImGui.GetCursorScreenPos();
            float   w    = ImGuiNET.ImGui.GetContentRegionAvail().X;

            var   font  = ImGuiStyle.FontBold;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float barH  = fsize + 12f;

            ImGuiNET.ImGui.InvisibleButton("##sec_" + text, new Vector2(w, barH));
            if (ImGuiNET.ImGui.IsItemClicked())
                open = !open;

            bool  hov = ImGuiNET.ImGui.IsItemHovered();
            uint  id  = ImGuiNET.ImGui.GetItemID();
            float th  = Anim(id,          hov  ? 1f : 0f, 12f);
            float tc  = Anim(id ^ 0xBBu, open ? 1f : 0f, 14f);

            // Background: subtle dark fill, lifts slightly on hover
            uint bgBase  = Alpha(InputBg, 180);
            uint bgHover = Alpha(InputBgH, 220);
            dl.AddRectFilled(pos, new Vector2(pos.X + w, pos.Y + barH),
                Lerp(bgBase, bgHover, th), 5f);

            // Bottom separator line - replaces the full border, cleaner look
            uint sepCol = Lerp(Alpha(BorderCol, 55), Alpha(Accent, 80), th);
            dl.AddLine(
                new Vector2(pos.X,     pos.Y + barH - 0.5f),
                new Vector2(pos.X + w, pos.Y + barH - 0.5f),
                sepCol, 1f);

            // Label - dims when collapsed, full brightness when open or hovered
            float textY = pos.Y + (barH - fsize) * .5f;
            uint  lCol  = Lerp(
                Lerp(Alpha(TxtDim, 200), Alpha(TxtNorm, 255), tc),   // collapsed-open tint
                Alpha(TxtNorm, 255), th);                              // hover always full
            // Section id/hash keys off the original (untranslated) text so collapsed/expanded
            // state and the widget id stay stable across a locale switch - only the drawn
            // glyphs change.
            dl.AddText(font, fsize, new Vector2(pos.X + 8f, textY), lCol, LocManager.Get(text));

            // Chevron: ? when collapsed (tc=0), - when expanded (tc=1)
            // In screen coords Y increases downward:
            //   right  = angle 0
            //   down   = angle -/2
            float  cx    = pos.X + w - 16f;
            float  cy    = pos.Y + barH * .5f;
            float  cr    = 5.5f;
            float  angle = tc * (MathF.PI * 0.5f);  // 0 - -/2  (right - down)
            uint   chCol = Lerp(Alpha(TxtDim, 160), Alpha(Accent, 220), MathF.Max(th, tc));
            DrawChevron(dl, cx, cy, cr, angle, chCol);

            ImGuiNET.ImGui.Spacing();

            // Animated collapse: show content while colT < 0.99 regardless of open flag.
            uint secId = (uint)text.GetHashCode() ^ 0xAB00u;
            _sectionStack.Push(secId);

            if (!_sections.TryGetValue(secId, out var state))
                state = default;

            float colT      = Anim(secId, open ? 0f : 1f, 14f);
            state.CollapseT = colT;

            bool showContent = colT < 0.99f;
            bool animating   = colT > 0.02f && colT < 0.99f;

            if (showContent)
            {
                state.ContentStartY = ImGuiNET.ImGui.GetCursorScreenPos().Y;

                if (animating && state.ExpandedH > 4f)
                {
                    // Clip to shrinking/growing height so content slides in/out.
                    float clipH = Math.Max(1f, state.ExpandedH * (1f - colT));
                    ImGuiNET.ImGui.BeginChild("##seccl_" + secId, new Vector2(-1f, clipH),
                        ImGuiChildFlags.None,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
                    state.InChild = true;
                }
                else
                {
                    state.InChild = false;
                }
            }
            else
            {
                state.InChild = false;
            }

            _sections[secId] = state;
            return showContent;
        }

        public static void EndSection()
        {
            if (_sectionStack.TryPop(out uint secId) && _sections.TryGetValue(secId, out var state))
            {
                if (state.InChild)
                {
                    ImGuiNET.ImGui.EndChild();
                }
                else if (state.CollapseT < 0.02f) // fully expanded - record content height
                {
                    float h = ImGuiNET.ImGui.GetCursorScreenPos().Y - state.ContentStartY;
                    if (h > 1f) state.ExpandedH = h;
                }
                state.InChild = false;
                _sections[secId] = state;
            }
            ImGuiNET.ImGui.Spacing();
        }

        // Equilateral triangle tip pointing in `angle` (screen coords: 0=right, -/2=down).
        // ── Inline notice banner ──────────────────────────────────────────────────
        //
        //  UI.Notice("Saving will overwrite existing data.", NoticeStyle.Warning);
        //
        public static void Notice(string message, NoticeStyle style = NoticeStyle.Info)
        {
            message = LocManager.Get(message);
            Vector2 pos   = ImGuiNET.ImGui.GetCursorScreenPos();
            float   w     = ImGuiNET.ImGui.GetContentRegionAvail().X;
            var     font  = ImGuiStyle.FontNormal;
            float   fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            const float PadX = 12f, PadY = 7f;

            float   textW    = w - (PadX + 3f) - PadX;
            Vector2 textSize = ImGuiNET.ImGui.CalcTextSize(message, false, textW);
            float   h        = textSize.Y + PadY * 2f;

            uint sc = style switch
            {
                NoticeStyle.Success => ImGuiStyle.Success,
                NoticeStyle.Warning => ImGuiStyle.Warning,
                NoticeStyle.Danger  => ImGuiStyle.Danger,
                _                   => Accent,
            };

            ImGuiNET.ImGui.Dummy(new Vector2(w, h));
            var dl = DL;

            // Background with subtle tint
            dl.AddRectFilled(pos, new Vector2(pos.X + w, pos.Y + h), DA(Alpha(sc, 20)), 4f);
            // Left accent bar
            dl.AddRectFilled(pos, new Vector2(pos.X + 3f, pos.Y + h),
                DA(Alpha(sc, 195)), 4f, ImDrawFlags.RoundCornersLeft);
            // Border
            dl.AddRect(pos, new Vector2(pos.X + w, pos.Y + h), DA(Alpha(sc, 60)), 4f, ImDrawFlags.None, 1f);
            // Text - wrap_width keeps text inside the box for multi-line messages
            dl.AddText(font, fsize,
                new Vector2(pos.X + PadX + 3f, pos.Y + PadY),
                DA(Alpha(TxtNorm, 225)), message, textW);
        }

        private static void DrawChevron(ImDrawListPtr dl, float cx, float cy, float r, float angle, uint col)
        {
            static Vector2 V(float cx, float cy, float r, float a)
                => new(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));

            dl.AddTriangleFilled(
                V(cx, cy, r,       angle),
                V(cx, cy, r * .9f, angle + 2.09f),
                V(cx, cy, r * .9f, angle - 2.09f),
                col);
        }

        // ───────────────────────────────────────────────────────────────────────────
        // BADGE  - pill with correctly centred text
        // ───────────────────────────────────────────────────────────────────────────

        public static void Badge(string text, uint bgCol = 0, uint fgCol = 0xFFFFFFFF)
        {
            text = LocManager.Get(text);
            if (bgCol == 0) bgCol = Alpha(Accent, 180);

            var   font  = ImGuiStyle.FontSmall;
            float fsize = font.FontSize > 0f ? font.FontSize : 11f;
            Vector2 ts  = MeasureText(font, fsize, text);

            const float px = 8f;
            const float py = 3f;
            float bw = ts.X + px * 2f;
            float bh = fsize + py * 2f;

            Vector2 pos = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.InvisibleButton("##badge_" + text, new Vector2(bw, bh));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            uint id  = ImGuiNET.ImGui.GetItemID();
            float t  = Anim(id, hov ? 1f : 0f, 12f);

            // Extract RGB from bgCol, animate toward a brighter version on hover
            uint bgHover = Alpha(bgCol, (byte)MathF.Min(((bgCol >> 24) & 0xFF) + 60, 255));
            uint bgAnim  = Lerp(bgCol, bgHover, t);

            var dl = DL;
            Vector2 br = new(pos.X + bw, pos.Y + bh);

            // Soft outer glow on hover
            if (t > 0.01f)
                dl.AddRectFilled(
                    new Vector2(pos.X - 3f, pos.Y - 3f),
                    new Vector2(br.X  + 3f, br.Y  + 3f),
                    Alpha(bgCol, (byte)(20 * t)), bh * .5f + 3f);

            dl.AddRectFilled(pos, br, bgAnim, bh * .5f);

            // Inner top highlight
            dl.AddLine(
                new Vector2(pos.X + bh * .4f, pos.Y + 1f),
                new Vector2(br.X  - bh * .4f, pos.Y + 1f),
                Alpha(0xFFFFFFFF, (byte)(28 + (int)(20 * t))), 1f);

            // Border - brightens on hover
            dl.AddRect(pos, br,
                Alpha(0xFFFFFFFF, (byte)(20 + (int)(35 * t))), bh * .5f, ImDrawFlags.None, 1f);

            dl.AddText(font, fsize, new Vector2(pos.X + px, pos.Y + py), fgCol, text);
        }

        // ───────────────────────────────────────────────────────────────────────────
        // SEPARATOR
        // ───────────────────────────────────────────────────────────────────────────

        public static void Separator()
        {
            Vector2 pos = ImGuiNET.ImGui.GetCursorScreenPos();
            float   w   = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            DL.AddRectFilled(pos, new Vector2(pos.X + w, pos.Y + 1f), Alpha(BorderCol, 90));
            ImGuiNET.ImGui.Dummy(new Vector2(w, 1f));
        }

        // Bold modal title + full-width separator line + bottom gap.
        // Use at the top of every BeginPopupModal content method so all modal
        // headers stay visually consistent without duplicating draw-list calls.
        public static void ModalTitle(string title)
        {
            title = LocManager.Get(title);
            var   bold   = ImGuiStyle.FontBold;
            float boldFs = bold.FontSize > 0f ? bold.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail  = ImGuiNET.ImGui.GetContentRegionAvail().X;

            Vector2 tp = ImGuiNET.ImGui.GetCursorScreenPos();
            DL.AddText(bold, boldFs, tp, DA(Alpha(TxtNorm, 240)), title);
            ImGuiNET.ImGui.Dummy(new Vector2(avail, boldFs + 2f));

            // Draw separator inline on the draw list (no extra Dummy) then a single gap dummy,
            // matching ConfirmModal exactly so the title section feels identical across all modals.
            Vector2 sep = ImGuiNET.ImGui.GetCursorScreenPos();
            DL.AddRectFilled(sep, new Vector2(sep.X + avail, sep.Y + 1f), Alpha(BorderCol, 90));
            ImGuiNET.ImGui.Dummy(new Vector2(1f, 10f));
        }

        // Styled BeginPopupModal wrapper - applies consistent Dark1 background,
        // border, rounding, and padding so every multi-step modal looks identical
        // without repeating 8 lines of Push/Pop boilerplate per panel.
        //
        // Usage:
        //   if (_open) { ImGuiNET.ImGui.OpenPopup("##mymod"); _open = false; }
        //   if (UI.BeginModal("##mymod", 420f))
        //   {
        //       UI.ModalTitle("My Modal");
        //       // ... content using UI.* controls ...
        //       UI.EndModal();
        //   }
        public static bool BeginModal(string id, float width = 420f)
        {
            var io = ImGuiNET.ImGui.GetIO();
            ImGuiNET.ImGui.SetNextWindowPos(
                new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.5f),
                ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            ImGuiNET.ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);
            // Exact same background as ConfirmModal; WindowBorderSize=0 so we draw our own border.
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.ModalBg, 250)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   8f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    new Vector2(16f, 16f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            bool open = ImGuiNET.ImGui.BeginPopupModal(id,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize);
            ImGuiNET.ImGui.PopStyleVar(3);
            ImGuiNET.ImGui.PopStyleColor(1);
            if (open)
            {
                // Dismiss by clicking outside the modal window (mirrors ConfirmModal behaviour).
                // Skip on the popup's first frame (IsWindowAppearing) - otherwise the SAME click
                // that opened it programmatically (e.g. a radar right-click menu item, whose click
                // naturally lands far from this modal's screen-centered spawn point) reads as an
                // immediate "clicked outside" and closes it before it's ever visible.
                if (!ImGuiNET.ImGui.IsWindowAppearing() &&
                    ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                    !ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
                    ImGuiNET.ImGui.CloseCurrentPopup();

                const float Round = 8f;
                var     dl = DL;
                Vector2 wp = ImGuiNET.ImGui.GetWindowPos();
                Vector2 ws = ImGuiNET.ImGui.GetWindowSize();
                Vector2 br = wp + ws;
                dl.AddRect(wp, br, Alpha(BorderCol, 200), Round, ImDrawFlags.None, 1f);
                dl.PathArcTo(new Vector2(wp.X + Round, wp.Y + Round), Round, MathF.PI,        MathF.PI * 1.5f);
                dl.PathArcTo(new Vector2(br.X - Round, wp.Y + Round), Round, MathF.PI * 1.5f, MathF.PI * 2f);
                dl.PathStroke(Alpha(Accent, 200), ImDrawFlags.None, 1.5f);
            }
            return open;
        }

        // Call inside the if (UI.BeginModal(...)) block to close the chrome.
        // Always ends with EndPopup so the caller doesn't have to.
        public static void EndModal() => ImGuiNET.ImGui.EndPopup();

        // ───────────────────────────────────────────────────────────────────────────
        // GROUP BOX  - bordered rounded container with a title in the top edge
        //
        //  UI.BeginGroup("ESP Settings");
        //      UI.Toggle(...);
        //      UI.SliderFloat(...);
        //  UI.EndGroup();
        // ───────────────────────────────────────────────────────────────────────────

        private static Vector2 _groupTL;
        private static float   _groupAvailW    = 0f;
        private static string  _groupTitle     = "";
        private static bool    _groupDisabled  = false;
        private static bool    _groupCollapsed = false;
        private static bool    _groupHovered   = false;
        private static uint    _groupFadeId;
        private const  float   GroupPad       = 10f;
        public  const  float   GroupMarginX   = 10f;  // horizontal gap between group box and window edge
        private const  float   GroupRound     = 6f;

        /// <summary>
        /// Cursor-Y headroom a panel should leave before calling BeginGroup(title) when
        /// the group is the first thing drawn inside a freshly clipped child (no
        /// inherited headroom from an outer scroll area). EndGroup draws the title text
        /// at boxTL.Y - fsize*0.5, so the +5f margin on top of that keeps the title clear
        /// of the clip rect; the resulting total (12px at the default font size) matches
        /// the gap above "Search Filter" in tabbed panels, where the tab row gives the
        /// box that much breathing room for free. Scales with font size so it stays
        /// correct at any UI scale setting.
        /// </summary>
        public static float GroupTitleClearance
        {
            get
            {
                float fsize = ImGuiStyle.FontBold.FontSize > 0f ? ImGuiStyle.FontBold.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                return MathF.Ceiling(fsize * .5f) + 6f;
            }
        }

        // Group-change indicator - value is remaining fade seconds, keyed by group title hash.
        private static readonly Dictionary<uint, float> _groupDirtyTime = new();

        // Tracks which group fade IDs have already had their entrance animation seeded.
        // Kept separate from _anim so that _anim's zero-pruning doesn't cause the fade to re-trigger.
        private static readonly HashSet<uint> _groupFadeSeen = new();

        // Inner drawable width available to controls inside BeginGroup/EndGroup.
        // Safe to call inside the draw-list channel split (unlike GetContentRegionAvail).
        public static float GroupInnerWidth => _groupAvailW - GroupPad * 2f;

        // Returns GroupInnerWidth when inside a BeginGroup, otherwise falls back to the
        // ImGui content region width.  Use this instead of ImGui.GetContentRegionAvail().X
        // whenever the call may be inside a group, so widths are computed consistently.
        public static float ContentAvailWidth =>
            _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;

        // ── COLUMNS ───────────────────────────────────────────────────────────
        // Indent-free multi-column layout. Works inside or outside BeginGroup.
        // Unlike ImGui.Columns, these never add per-column indent.
        //
        // Usage:
        //   UI.BeginGroup("Match");
        //   UI.BeginColumns(2);
        //   DrawKV("Map",   ...);  UI.NextColumn();
        //   DrawKV("Clock", ...);   UI.NextColumn();   // wraps to next row
        //   DrawKV("Players", ...); UI.NextColumn();
        //   DrawKV("Speed", ...);
        //   UI.EndColumns();
        //   DrawKV("Position", ...);  // back to full width
        //   UI.EndGroup();

        private static int     _colCount;
        private static int     _colIndex;
        private static float   _colW;
        private static Vector2 _colRowOrigin;
        private static float   _colRowMaxH;

        public static void BeginColumns(int count = 2)
        {
            _colCount     = Math.Max(1, count);
            _colIndex     = 0;
            _colW         = (_groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X) / _colCount;
            _colRowOrigin = ImGuiNET.ImGui.GetCursorScreenPos();
            _colRowMaxH   = 0f;
        }

        // Call between items (including between the last item of one row and
        // the first of the next). Do NOT call after the very last item - use EndColumns() instead.
        public static void NextColumn()
        {
            float h = ImGuiNET.ImGui.GetCursorScreenPos().Y - _colRowOrigin.Y;
            if (h > _colRowMaxH) _colRowMaxH = h;
            _colIndex++;
            if (_colIndex >= _colCount)
            {
                _colIndex = 0;
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(_colRowOrigin.X, _colRowOrigin.Y + _colRowMaxH));
                _colRowOrigin = ImGuiNET.ImGui.GetCursorScreenPos();
                _colRowMaxH   = 0f;
            }
            else
            {
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(_colRowOrigin.X + _colIndex * _colW, _colRowOrigin.Y));
            }
        }

        // Call after the last item. Advances cursor to below the tallest column of the final row.
        public static void EndColumns()
        {
            float h = ImGuiNET.ImGui.GetCursorScreenPos().Y - _colRowOrigin.Y;
            if (h > _colRowMaxH) _colRowMaxH = h;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(_colRowOrigin.X, _colRowOrigin.Y + _colRowMaxH));
            _colCount = 0;
        }

        // ── GROUP BOX ─────────────────────────────────────────────────────────

        /// <summary>
        /// True while inside a collapsed BeginGroup/EndGroup. Content that draws in its own child
        /// window (Table, VirtualList) escapes the group's clip rect, so skip it when this is set.
        /// </summary>
        public static bool IsGroupCollapsed => _groupCollapsed;

        // width: explicit column width (including margins). Pass -1 to use full content region.
        public static void BeginGroup(string title = "", bool disabled = false, float width = -1f, string disabledReason = null)
        {
            var   font  = ImGuiStyle.FontBold;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float topGap = string.IsNullOrEmpty(title) ? GroupPad : MathF.Floor(fsize * .5f) + 2f;

            _groupTitle    = string.IsNullOrEmpty(title) ? title : LocManager.Get(title);
            _groupFadeId   = (uint)_groupTitle.GetHashCode() ^ 0xFADEB0E0u;

            // _groupTL marks the top-left of the box (inset by margin from the window edge)
            Vector2 rawTL    = ImGuiNET.ImGui.GetCursorScreenPos();
            float   rawAvail = ImGuiNET.ImGui.GetContentRegionAvail().X;
            _groupTL       = new Vector2(rawTL.X + GroupMarginX, rawTL.Y);
            _groupAvailW   = (width > 0f ? width : rawAvail) - GroupMarginX * 2f;

            // Collapse toggle: double-click the title bar to collapse/expand (titled groups only).
            bool collapsed = false;
            bool hasTitle  = !string.IsNullOrEmpty(_groupTitle);
            if (hasTitle)
            {
                var cfg = Program.Config;
                collapsed = cfg?.CollapsedGroups?.Contains(_groupTitle) == true;

                // Hit zone over the title bar - covers the text (sits fsize*0.5 above boxTL)
                // plus the inner gap down to content. Save/restore cursor so layout is unaffected.
                float titleAbove = MathF.Floor(fsize * .5f);
                var savedPos = ImGuiNET.ImGui.GetCursorScreenPos();
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(_groupTL.X, _groupTL.Y - titleAbove));
                ImGuiNET.ImGui.InvisibleButton($"##grphdr_{_groupTitle}",
                    new Vector2(_groupAvailW, topGap + GroupPad * .5f + titleAbove));
                _groupHovered = ImGuiNET.ImGui.IsItemHovered();
                if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
                    cfg?.CollapsedGroups != null)
                {
                    if (collapsed) cfg.CollapsedGroups.Remove(_groupTitle);
                    else           cfg.CollapsedGroups.Add(_groupTitle);
                    collapsed = !collapsed;
                    cfg.SaveDebounced();
                }
                ImGuiNET.ImGui.SetCursorScreenPos(savedPos);
            }
            _groupCollapsed = collapsed;
            // disabled tracks whether EndGroup must call EndDisabled (collapse also disables).
            _groupDisabled  = disabled || collapsed;

            // Split into 2 channels: 0 = background (drawn last but renders behind), 1 = content
            DL.ChannelsSplit(2);
            DL.ChannelsSetCurrent(1);

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(_groupTL.X + GroupPad, MathF.Floor(_groupTL.Y + topGap + GroupPad * .5f)));
            ImGuiNET.ImGui.PushItemWidth(_groupAvailW - GroupPad * 2f);
            ImGuiNET.ImGui.BeginGroup();

            if (_groupDisabled) BeginDisabled(disabled ? disabledReason : null);

            // Clip content draws to the group's horizontal bounds.
            // When collapsed: zero-height rect so content is invisible but still "runs" in the
            // ImGui layout - EndGroup overrides brY to the compact collapsed height, so the
            // outer cursor is repositioned correctly without needing any call-site changes.
            var clipBR = collapsed
                ? _groupTL   // zero-height → nothing visible
                : new Vector2(_groupTL.X + _groupAvailW, _groupTL.Y + 9999f);
            DL.PushClipRect(_groupTL, clipBR, true);
        }

        /// <param name="overrideBrY">
        /// When &gt; 0, pins the group border's bottom Y to this screen coordinate instead
        /// of computing it from content height. Use to align the border to a fixed
        /// boundary (e.g. a parent clip rect bottom) when the group contains a
        /// fill-height child that would otherwise cause overflow.
        /// </param>
        public static void EndGroup(float overrideBrY = 0f)
        {
            bool wasCollapsed = _groupCollapsed;
            if (_groupDisabled) EndDisabled();

            // Remove the horizontal clip pushed by BeginGroup before drawing the border.
            DL.PopClipRect();

            ImGuiNET.ImGui.EndGroup();
            ImGuiNET.ImGui.PopItemWidth();

            var   font  = ImGuiStyle.FontBold;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            bool  hasTitle = !string.IsNullOrEmpty(_groupTitle);
            float topGap   = hasTitle ? fsize * .5f + 2f : GroupPad;

            Vector2 contentEnd = ImGuiNET.ImGui.GetCursorScreenPos();
            float   avail      = _groupAvailW;

            Vector2 boxTL = _groupTL;
            // When collapsed: clamp the box to just the title bar height so the group
            // appears as a compact header. EndGroup repositions the outer cursor to brY,
            // so no call-site changes are needed - content still "runs" but is invisible.
            float   brY   = wasCollapsed  ? boxTL.Y + topGap + GroupPad * .5f
                          : overrideBrY > 0f ? overrideBrY
                          : contentEnd.Y + GroupPad * .5f;
            Vector2 boxBR = new(boxTL.X + avail, brY);

            var dl = DL;

            // Draw background on channel 0 (behind content drawn on channel 1)
            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(boxTL, boxBR, Alpha(ImGuiStyle.PanelBg, 50), GroupRound);
            dl.ChannelsSetCurrent(1);

            // Border and title drawn on top (channel 1, same layer as content)
            if (hasTitle)
            {
                // Hover/collapse animation keys
                uint  chevKey = (uint)_groupTitle.GetHashCode() ^ 0xC011u;
                float chevT   = Anim(chevKey,                wasCollapsed  ? 0f : 1f, 12f);
                float hh      = Anim(_groupFadeId ^ 0xAA01u, _groupHovered ? 1f : 0f, SpeedHov);

                // Interior header zone: the strip inside the box between border and content.
                float hdrBot = boxTL.Y + topGap + GroupPad * .5f;
                float hdrCY  = boxTL.Y + (topGap + GroupPad * .5f) * .5f;

                // Hover fill - entirely inside the box, anchored to the interior header zone.
                if (hh > 0.01f)
                    dl.AddRectFilled(
                        new Vector2(boxTL.X + 1f, boxTL.Y + 1f),
                        new Vector2(boxTL.X + avail - 1f, hdrBot),
                        Alpha(InputBgH, (byte)(65 * hh)), GroupRound, ImDrawFlags.RoundCornersTop);

                // Border (drawn after fill so it renders on top)
                dl.AddRect(boxTL, boxBR, Alpha(BorderCol, 140), GroupRound, ImDrawFlags.None, 1f);

                // Measure title and punch the classic gap in the top border line
                float   tx  = boxTL.X + 10f;
                float   maxTitleW = MathF.Max(0f, avail - 28f);
                string  dispTitle = TruncateText(font, fsize, _groupTitle, maxTitleW);
                Vector2 ts  = MeasureText(font, fsize, dispTitle);
                float   gap = ts.X + 8f;
                dl.AddLine(new Vector2(tx - 4f, boxTL.Y),
                           new Vector2(tx + gap, boxTL.Y),
                           Alpha(ImGuiStyle.PanelBg, 255), 2f);

                // Chevron: centred inside the header zone, not on the border line.
                // right (collapsed, chevT=0) → down (expanded, chevT=1). Lifts to accent on hover.
                float chevAngle = chevT * (MathF.PI * 0.5f);
                uint  chevBase  = Alpha(GroupTitleColor, (byte)(100 + (int)(60 * chevT)));
                uint  chevCol   = Lerp(chevBase, Alpha(Accent, 210), hh);
                DrawChevron(dl, boxTL.X + avail - 14f, hdrCY, 4.5f, chevAngle, chevCol);

                // Title text - brightens on hover and when expanded
                byte titleA   = (byte)MathF.Min(255f, 140f + 70f * chevT + 45f * hh);
                uint titleCol = Alpha(GroupTitleColor, titleA);
                dl.AddText(font, fsize, new Vector2(tx, MathF.Floor(boxTL.Y - fsize * .5f)), titleCol, dispTitle);

                // Change-acknowledgement dot - fades out after MarkChanged() is called
                uint gKey = (uint)_groupTitle.GetHashCode();
                if (_groupDirtyTime.TryGetValue(gKey, out float dotT) && dotT > 0f)
                {
                    float dotAlpha = LowPerf ? 1f : MathF.Min(dotT / 0.3f, 1f);
                    float dotX = tx + ts.X + 5f;
                    float dotY = MathF.Floor(boxTL.Y);
                    dl.AddCircleFilled(new Vector2(dotX, dotY), 3f, Alpha(ImGuiStyle.Success, (byte)(220 * dotAlpha)));
                    float newDot = dotT - ImGuiNET.ImGui.GetIO().DeltaTime;
                    if (newDot <= 0f) _groupDirtyTime.Remove(gKey);
                    else              _groupDirtyTime[gKey] = newDot;
                }
            }
            else
            {
                dl.AddRect(boxTL, boxBR, Alpha(BorderCol, 140), GroupRound, ImDrawFlags.None, 1f);
            }

            // Entrance fade - on first render seed the anim at 1 so it decays to 0,
            // creating a "reveal" effect. Skipped entirely in LowPerf mode.
            // _groupFadeSeen guards the seed so _anim's zero-pruning can't re-trigger the fade.
            if (!LowPerf)
            {
                if (_groupFadeSeen.Add(_groupFadeId)) _anim[_groupFadeId] = 1f;
                float fadeIn = Anim(_groupFadeId, 0f, 8f);
                if (fadeIn > 0.01f)
                    dl.AddRectFilled(boxTL, boxBR,
                        Alpha(ImGuiStyle.PanelBg, (byte)(210 * fadeIn)), GroupRound);
            }

            // Merge channels - background (ch0) composites behind content (ch1)
            dl.ChannelsMerge();
            _groupAvailW = 0f; // reset so controls outside the group use GetContentRegionAvail normally

            // Advance cursor past the box (back to the full content-region left edge)
            float fullAvail = avail + GroupMarginX * 2f;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(_groupTL.X - GroupMarginX, boxBR.Y));
            ImGuiNET.ImGui.Dummy(new Vector2(fullAvail, 0f));
            // isp.Y (6px) post-gap + 8px extra = 14px gap before next group.
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 8f);
        }

        /// <summary>
        /// Draws a lightweight sub-label inside a <see cref="BeginGroup"/> block - bold
        /// colored text with a hairline rule extending to the right edge. No border, no
        /// collapse. Use to visually segment content within a group without nesting a full
        /// child group box.
        /// </summary>
        public static void GroupSubLabel(string text)
        {
            text = LocManager.Get(text);
            var   dl    = DL;
            var   font  = ImGuiStyle.FontBold;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;

            ImGuiNET.ImGui.Spacing();

            Vector2 pos = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 ts  = MeasureText(font, fsize, text);

            dl.AddText(font, fsize, pos, Alpha(GroupTitleColor, 170), text);

            float lineX0 = pos.X + ts.X + 6f;
            float lineX1 = pos.X + avail;
            float lineY  = pos.Y + MathF.Floor(fsize * 0.5f) + 1f;
            if (lineX1 > lineX0 + 4f)
                dl.AddLine(new Vector2(lineX0, lineY), new Vector2(lineX1, lineY),
                    Alpha(BorderCol, 75), 1f);

            ImGuiNET.ImGui.Dummy(new Vector2(avail, fsize + 2f));
        }

        /// <summary>
        /// Call inside any changed-value handler to show a brief accent dot on the current
        /// group's title, acknowledging that the change was saved.
        /// </summary>
        public static void MarkChanged()
        {
            if (string.IsNullOrEmpty(_groupTitle)) return;
            _groupDirtyTime[(uint)_groupTitle.GetHashCode()] = 1.6f;
        }

        // ───────────────────────────────────────────────────────────────────────────
        // TWO-COLUMN GRID  - use inside BeginGroup/EndGroup to lay controls out
        // in two equal columns. Works correctly inside the draw-list channel split.
        //
        // Usage:
        //   UI.BeginGroup("Settings");
        //   UI.DrawGrid(UI.GroupInnerWidth, new UI.GridItem[]
        //   {
        //       new(w => UI.Toggle("Option A##a", ref valA, w)),
        //       new(w => UI.Toggle("Option B##b", ref valB, w)),
        //       new(w => UI.ToggleWithPopover("Option C##c", ref valC, () => { ... }, width: w)),
        //   });
        //   UI.EndGroup();
        // ───────────────────────────────────────────────────────────────────────────

        // Gap between columns - prevents left-column checkbox touching right-column label.
        public const float ColGutter = 8f;

        /// <summary>Item entry for DrawGrid. Draw receives the allocated cell pixel width.</summary>
        public struct GridItem
        {
            public Action<float> Draw;
            public bool          FullWidth;
            public GridItem(Action<float> draw, bool full = false) { Draw = draw; FullWidth = full; }
        }

        /// <summary>
        /// Lays out items in a 2-column grid. Pass UI.GroupInnerWidth as avail when inside a group.
        /// Full-width items span both columns; all others are paired left/right.
        /// </summary>
        public static void DrawGrid(float avail, GridItem[] items)
        {
            float colW = avail / 2f;
            float rowH = ImGuiNET.ImGui.GetTextLineHeight() + 8f;

            int i = 0;
            while (i < items.Length)
            {
                float startX = ImGuiNET.ImGui.GetCursorScreenPos().X;
                float startY = ImGuiNET.ImGui.GetCursorScreenPos().Y;

                if (items[i].FullWidth)
                {
                    items[i].Draw(avail);
                    i++;
                }
                else if (i + 1 < items.Length && !items[i + 1].FullWidth)
                {
                    TwoCol(avail, colW, rowH, startX, startY, items[i].Draw, items[i + 1].Draw);
                    i += 2;
                }
                else
                {
                    TwoCol(avail, colW, rowH, startX, startY, items[i].Draw, null);
                    i++;
                }
            }
        }

        private static void TwoCol(float avail, float colW, float rowH, float startX, float startY,
            Action<float> left, Action<float>? right)
        {
            // Left cell is narrowed by the full gutter so its checkbox clears the right label.
            // Right cell starts at the midpoint and fills exactly to the inner right edge.
            float leftW  = colW - ColGutter;
            float rightX = startX + colW;
            float rightW = avail - colW;

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(startX, startY));
            left(leftW);

            if (right != null)
            {
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rightX, startY));
                right(rightW);
            }

            float rowBottom = ImGuiNET.ImGui.GetCursorScreenPos().Y;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(startX, MathF.Max(rowBottom, startY + rowH)));
        }

        // ───────────────────────────────────────────────────────────────────────────
        // RADIO BUTTON  - single option within a group; selected when value == id
        //
        //  UI.Radio("Option A##r1", ref mode, 0);
        //  UI.Radio("Option B##r2", ref mode, 1);
        //  UI.Radio("Option C##r3", ref mode, 2);
        // ───────────────────────────────────────────────────────────────────────────

        private const float RadioR  = 7f;   // outer circle radius
        private const float RadioDR = 3.5f; // inner filled dot radius

        public static bool Radio(string label, ref int value, int id, float width = 0f)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowH  = MathF.Max(RadioR * 2f, fsize) + 4f;
            float avail = width > 0f ? width
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl  = ImGuiNET.ImGui.GetCursorScreenPos();

            ImGuiNET.ImGui.InvisibleButton(label, new Vector2(avail, rowH));
            bool clicked = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            bool hovered = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            uint btnId   = ImGuiNET.ImGui.GetItemID();

            bool selected = value == id;
            if (clicked) value = id;

            float tv = Anim(btnId,         selected ? 1f : 0f, 16f);
            float th = Anim(btnId ^ 0xAAu, hovered  ? 1f : 0f, 12f);

            var dl = DL;

            // ── Circle - right-aligned, vertically centred ────────────────
            float cx = tl.X + avail - RadioR;
            float cy = tl.Y + rowH * .5f;

            // Outer ring background: dark - accent tint when selected
            uint ringBg = DA(Lerp(
                Lerp(Alpha(Dark5, 220), Alpha(Dark4, 240), th),
                Alpha(Accent, 40), tv));
            dl.AddCircleFilled(new Vector2(cx, cy), RadioR, ringBg);

            // Outer ring border: dim - accent
            uint ringBord = DA(Lerp(Alpha(BorderCol, 160), Alpha(Accent, 220), MathF.Max(th, tv)));
            dl.AddCircle(new Vector2(cx, cy), RadioR, ringBord, 0, 1.5f);

            // Inner dot - scales in as tv animates 0-1
            if (tv > 0.02f)
            {
                float dotR = RadioDR * tv;
                dl.AddCircleFilled(new Vector2(cx, cy), dotR,
                    DA(Alpha(0xFFFFFFFF, (byte)(255 * MathF.Min(tv * 1.5f, 1f)))));
            }

            // ───Label (truncated to avoid overlapping the right-aligned circle) ─────────
            float radioMaxW = MathF.Max(0f, avail - RadioR * 2f - 6f);
            uint lCol = DA(Lerp(TxtNorm, 0xFFFFFFFF, th));
            dl.AddText(font, fsize,
                new Vector2(tl.X + 2f, tl.Y + (rowH - fsize) * .5f),
                lCol, TruncateText(font, fsize, Loc(label), radioMaxW));

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
            DrawSearchHighlight(Strip(label), _lastRowTL, _lastRowBR);
            return clicked;
        }

        /// <summary>
        /// Single-select radio row: label in the left column (respects BeginSliderGroup width),
        /// options rendered inline in the right column as  ● Text  ○ Text.
        /// Returns true if the selected value changed.
        /// </summary>
        public static bool RadioRow(string label, string[] options, ref int value, string hint = null)
        {
            if (options == null || options.Length == 0) return false;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowH  = MathF.Max(RadioR * 2f, fsize) + 4f;
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;

            string cleanLabel = Loc(Strip(label));
            float  labelColW  = _groupLabelColW > 0f
                ? _groupLabelColW
                : MeasureText(font, fsize, cleanLabel).X + 10f;

            // Size each cell to exactly fit its circle + gap + text + right-pad.
            float   rightColW  = avail - labelColW;
            float[] cellWidths = new float[options.Length];
            float   totalW     = 0f;
            for (int i = 0; i < options.Length; i++)
            {
                cellWidths[i] = RadioR * 2f + 6f + MeasureText(font, fsize, Loc(options[i])).X + 10f;
                totalW += cellWidths[i];
            }
            // Scale down uniformly if they exceed the available column.
            float scale = totalW > rightColW ? rightColW / totalW : 1f;

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();

            // Group label in left column.
            DL.AddText(font, fsize,
                new Vector2(tl.X + 2f, tl.Y + (rowH - fsize) * .5f),
                DA(IsDisabled ? TxtDim : TxtNorm), cleanLabel);

            // Options inline in right column - circle LEFT, text RIGHT.
            bool changed = false;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(tl.X + labelColW, tl.Y));
            for (int i = 0; i < options.Length; i++)
            {
                if (i > 0) ImGuiNET.ImGui.SameLine(0f, 0f);

                float   cellW  = cellWidths[i] * scale;
                Vector2 cellTL = ImGuiNET.ImGui.GetCursorScreenPos();

                ImGuiNET.ImGui.InvisibleButton(options[i] + "##rrow_" + Strip(label) + "_" + i,
                    new Vector2(cellW, rowH));

                bool clicked  = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
                bool hovered  = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
                uint btnId    = ImGuiNET.ImGui.GetItemID();
                bool selected = value == i;
                if (clicked) { value = i; changed = true; }

                float tv = Anim(btnId,         selected ? 1f : 0f, 16f);
                float th = Anim(btnId ^ 0xAAu, hovered  ? 1f : 0f, 12f);

                // Circle - left edge of cell.
                float cx = cellTL.X + RadioR;
                float cy = cellTL.Y + rowH * .5f;

                uint ringBg = DA(Lerp(
                    Lerp(Alpha(Dark5, 220), Alpha(Dark4, 240), th),
                    Alpha(Accent, 40), tv));
                DL.AddCircleFilled(new Vector2(cx, cy), RadioR, ringBg);
                DL.AddCircle(new Vector2(cx, cy), RadioR,
                    DA(Lerp(Alpha(TxtDim, 80), Accent, tv)), 0, 1.2f);
                if (tv > 0.01f)
                    DL.AddCircleFilled(new Vector2(cx, cy), RadioDR * tv,
                        DA(Alpha(0xFFFFFFFF, (byte)(255 * MathF.Min(tv * 1.5f, 1f)))));

                // Label - immediately right of circle.
                DL.AddText(font, fsize,
                    new Vector2(cellTL.X + RadioR * 2f + 4f, cellTL.Y + (rowH - fsize) * .5f),
                    DA(Lerp(TxtNorm, 0xFFFFFFFF, th)), Loc(options[i]));
            }

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
            if (hint != null && ImGuiNET.ImGui.IsMouseHoveringRect(tl, _lastRowBR)) Tooltip(hint);
            return changed;
        }

        //───────────────────────────────────────────────────────────────────────────
        // MULTI-SELECT RADIO GROUP  - any number of options can be selected
        //
        // bool[] overload:
        //   bool[] sel = { true, false, false };
        //   UI.RadioGroup("Layers##rg1", new[]{"A","B","C"}, sel);
        //
        // int bitmask overload:
        //   int flags = 0b001;
        //   UI.RadioGroup("Layers##rg1", new[]{"A","B","C"}, ref flags);
        // ───────────────────────────────────────────────────────────────────────────

        /// <summary>Multi-select radio group backed by a bool array. Returns true if any item changed.</summary>
        public static bool RadioGroup(string groupId, string[] labels, bool[] selected)
        {
            if (labels == null || selected == null) return false;
            int count   = Math.Min(labels.Length, selected.Length);
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                string itemId = labels[i] + "##" + groupId + i;
                changed |= RadioMulti(itemId, ref selected[i]);
            }
            return changed;
        }

        /// <summary>Multi-select radio group backed by a bitmask int. Returns true if any bit changed.</summary>
        public static bool RadioGroup(string groupId, string[] labels, ref int flags)
        {
            if (labels == null) return false;
            bool changed = false;
            for (int i = 0; i < labels.Length && i < 32; i++)
            {
                string itemId  = labels[i] + "##" + groupId + i;
                bool   cur     = (flags & (1 << i)) != 0;
                bool   prev    = cur;
                changed |= RadioMulti(itemId, ref cur);
                if (cur != prev)
                {
                    if (cur) flags |=  (1 << i);
                    else     flags &= ~(1 << i);
                }
            }
            return changed;
        }

        // Shared renderer for a single multi-select radio item - toggle on click.
        private static bool RadioMulti(string label, ref bool value)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowH  = MathF.Max(RadioR * 2f, fsize) + 4f;
            float avail = _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl  = ImGuiNET.ImGui.GetCursorScreenPos();

            ImGuiNET.ImGui.InvisibleButton(label, new Vector2(avail, rowH));
            bool clicked = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            bool hovered = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            uint btnId   = ImGuiNET.ImGui.GetItemID();

            if (clicked) value = !value;

            float tv = Anim(btnId,         value   ? 1f : 0f, 16f);
            float th = Anim(btnId ^ 0xAAu, hovered ? 1f : 0f, 12f);

            var dl = DL;

            float cx = tl.X + avail - RadioR;
            float cy = tl.Y + rowH * .5f;

            // Outer ring - same visuals as single Radio
            uint ringBg = DA(Lerp(
                Lerp(Alpha(Dark5, 220), Alpha(Dark4, 240), th),
                Alpha(Accent, 40), tv));
            dl.AddCircleFilled(new Vector2(cx, cy), RadioR, ringBg);

            uint ringBord = DA(Lerp(Alpha(BorderCol, 160), Alpha(Accent, 220), MathF.Max(th, tv)));
            dl.AddCircle(new Vector2(cx, cy), RadioR, ringBord, 0, 1.5f);

            // Inner dot
            if (tv > 0.02f)
            {
                float dotR = RadioDR * tv;
                dl.AddCircleFilled(new Vector2(cx, cy), dotR,
                    DA(Alpha(0xFFFFFFFF, (byte)(255 * MathF.Min(tv * 1.5f, 1f)))));
            }

            // Label (truncated to avoid overlapping the right-aligned circle)
            float radioMultiMaxW = MathF.Max(0f, avail - RadioR * 2f - 6f);
            uint lCol = DA(Lerp(TxtNorm, 0xFFFFFFFF, th));
            dl.AddText(font, fsize,
                new Vector2(tl.X + 2f, tl.Y + (rowH - fsize) * .5f),
                lCol, TruncateText(font, fsize, Loc(label), radioMultiMaxW));

            return clicked;
        }

        // NUMERIC UP-DOWN  - text input with ── stepper buttons
        //
        //  int   fps = 60;
        //  UI.InputInt("FPS Cap##n1", ref fps, min: 30, max: 300, step: 1);
        //
        //  float scale = 1.0f;
        //  UI.InputFloat("Scale##n2", ref scale, min: 0.1f, max: 4f, step: 0.1f, fmt: "F2");
        // ───────────────────────────────────────────────────────────────────────────

        private const float NumH        = 28f;
        private const float NumRound    = 5f;
        private const float NumArrowW   = 24f;  // stacked +/? column width

        public static bool InputInt(string label, ref int value,
            int min = int.MinValue, int max = int.MaxValue, int step = 1,
            float width = 0f, string hint = null)
        {
            float f = value;
            bool changed = DoNumeric(label, ref f, min, max, step, "F0", width, hint);
            if (changed) value = (int)MathF.Round(f);
            return changed;
        }

        public static bool InputFloat(string label, ref float value,
            float min = float.MinValue, float max = float.MaxValue,
            float step = 1f, string fmt = "F1", float width = 0f, string hint = null)
            => DoNumeric(label, ref value, min, max, step, fmt, width, hint);

        private static bool DoNumeric(string label, ref float value,
            float min, float max, float step, string fmt, float overW, string hint = null)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            float w     = overW > 0f ? MathF.Min(overW, avail) : avail;

            string cleanLabel = Loc(label);
            float  labelColW;
            if (string.IsNullOrEmpty(cleanLabel))
                labelColW = 0f;
            else if (_numLabelColW > 0f)
                labelColW = _numLabelColW;
            else
                labelColW = MeasureText(font, fsize, cleanLabel).X + 12f;

            // Layout: [label col] [text field ............] [±]
            //                                               [∓]
            // Clamp labelColW so the widget always has a minimum usable width.
            // Without this, a narrow panel makes totalW negative and the widget inverts.
            const float MinFieldW = 40f;
            labelColW = MathF.Min(labelColW, MathF.Max(0f, w - NumArrowW - MinFieldW));
            float totalW = w - labelColW;
            float fieldW = totalW - NumArrowW;

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            float   ix = tl.X + labelColW;              // left edge of spinner widget
            float   halfH  = NumH * .5f;
            bool    changed = false;
            var     dl     = DL;

            // ── - increment (top half of arrow column) ────────────────────
            Vector2 incTL = new(ix + fieldW, tl.Y);
            Vector2 incBR = new(ix + totalW, tl.Y + halfH);
            ImGuiNET.ImGui.SetCursorScreenPos(incTL);
            ImGuiNET.ImGui.InvisibleButton("##inc_" + label, new Vector2(NumArrowW, halfH));
            bool incHov = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            bool incAct = !IsDisabled && ImGuiNET.ImGui.IsItemActive();
            bool incClk = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            uint incId  = ImGuiNET.ImGui.GetItemID();
            float tInc  = Anim(incId,         incHov ? 1f : 0f, 14f);
            float tIncA = Anim(incId ^ 0xD2u, incAct ? 1f : 0f, 20f);
            if (incClk) { float nv = Math.Clamp(value + step, min, max); if (MathF.Abs(nv - value) > 1e-9f) { value = nv; changed = true; } }

            // ── - decrement (bottom half of arrow column) ─────────────────
            Vector2 decTL = new(ix + fieldW, tl.Y + halfH);
            Vector2 decBR = new(ix + totalW, tl.Y + NumH);
            ImGuiNET.ImGui.SetCursorScreenPos(decTL);
            ImGuiNET.ImGui.InvisibleButton("##dec_" + label, new Vector2(NumArrowW, halfH));
            bool decHov = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            bool decAct = !IsDisabled && ImGuiNET.ImGui.IsItemActive();
            bool decClk = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            uint decId  = ImGuiNET.ImGui.GetItemID();
            float tDec  = Anim(decId,         decHov ? 1f : 0f, 14f);
            float tDecA = Anim(decId ^ 0xD1u, decAct ? 1f : 0f, 20f);
            if (decClk) { float nv = Math.Clamp(value - step, min, max); if (MathF.Abs(nv - value) > 1e-9f) { value = nv; changed = true; } }

            // ── Text field - placed last so it wins hit-testing ───────────
            // Pre-compute the InputText item ID to read previous-frame animation values.
            // Drawing the background BEFORE the widget ensures ImGui's selection rect
            // and text cursor render on top of it - same technique as InputText.
            uint  preId    = ImGuiNET.ImGui.GetID("##nf_" + label);
            float tf0      = _anim.TryGetValue(preId ^ 0xF1u, out var _tf0a) ? _tf0a : 0f;
            float th0      = _anim.TryGetValue(preId ^ 0xF2u, out var _th0a) ? _th0a : 0f;
            Vector2 widgetTL = new(ix, tl.Y);
            Vector2 widgetBR = new(ix + totalW, tl.Y + NumH);
            dl.AddRectFilled(widgetTL, widgetBR,
                DA(Lerp(Alpha(ImGuiStyle.WidgetBg, 200), Alpha(InputBg, 220), th0)), NumRound);

            Vector2 fieldTL = widgetTL;
            ImGuiNET.ImGui.SetCursorScreenPos(fieldTL);
            ImGuiNET.ImGui.SetNextItemWidth(fieldW);

            // Make InputText fully invisible - we draw everything ourselves via draw-list
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg,        Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,         Alpha(0xFF000000, 0));
            // Hide ImGui's own text - we draw it ourselves so we control colour/position
            ImGuiNET.ImGui.PushFont(font);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,           ToV4(DA(Lerp(TxtNorm, 0xFFFFFFFF, th0 * 0.3f))));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(8f, (NumH - fsize) * .5f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);

            string buf = value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            if (!IsDisabled)
            {
                if (ImGuiNET.ImGui.InputText("##nf_" + label, ref buf, 32,
                    ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    if (float.TryParse(buf, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                    { value = Math.Clamp(parsed, min, max); changed = true; }
                }
            }
            else
            {
                string tmp = buf;
                ImGuiNET.ImGui.InputText("##nf_" + label, ref tmp, 32, ImGuiInputTextFlags.ReadOnly);
            }

            bool  focused  = ImGuiNET.ImGui.IsItemFocused();
            bool  fieldHov = ImGuiNET.ImGui.IsItemHovered();
            uint  fieldId  = ImGuiNET.ImGui.GetItemID();
            float tf       = Anim(fieldId ^ 0xF1u, focused  ? 1f : 0f, 14f);
            float th       = Anim(fieldId ^ 0xF2u, (fieldHov || incHov || decHov) ? 1f : 0f, 12f);

            ImGuiNET.ImGui.PopStyleVar(2);
            ImGuiNET.ImGui.PopStyleColor(5);
            ImGuiNET.ImGui.PopFont();

            // ── Right-click context menu (Cut / Copy / Paste) ────────────────
            if (!IsDisabled && BeginContextMenu("##num_ctx_" + label, widgetTL, widgetBR))
            {
                string clip;
                try { clip = ImGuiNET.ImGui.GetClipboardText() ?? ""; } catch { clip = ""; }
                bool hasClip = float.TryParse(clip.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float clipVal);

                if (MenuItem("Cut"))
                { ImGuiNET.ImGui.SetClipboardText(buf); value = Math.Clamp(0f, min, max); changed = true; }
                if (MenuItem("Copy"))
                    ImGuiNET.ImGui.SetClipboardText(buf);
                MenuSeparator();
                if (MenuItem("Paste", hasClip))
                { value = Math.Clamp(clipVal, min, max); changed = true; }
                EndContextMenu();
            }

            // ── Draw: whole widget bg + border ──────────────────────────────────
            if (tf > 0.01f)
                dl.AddRect(new Vector2(widgetTL.X - 1f, widgetTL.Y - 1f),
                           new Vector2(widgetBR.X + 1f, widgetBR.Y + 1f),
                           Alpha(Accent, (byte)(35 * tf)), NumRound + 1f, ImDrawFlags.None, 1f);

            dl.AddRect(widgetTL, widgetBR,
                DA(Lerp(Lerp(Alpha(BorderCol, 120), Alpha(BorderCol, 200), th), Alpha(Accent, 200), tf)),
                NumRound, ImDrawFlags.None, focused ? 1.5f : 1f);

            // Divider between field and arrow column
            float divX = ix + fieldW;
            dl.AddLine(new Vector2(divX, tl.Y + 3f), new Vector2(divX, tl.Y + NumH - 3f),
                DA(Alpha(BorderCol, 80)), 1f);

            // + button hover fill - solid accent bg, brighter on click
            if (tInc > 0.01f || tIncA > 0.01f)
                dl.AddRectFilled(incTL, incBR,
                    DA(Alpha(Accent, (byte)(60 * tInc + 60 * tIncA))),
                    0f, ImDrawFlags.RoundCornersTopRight);

            // ? button hover fill
            if (tDec > 0.01f || tDecA > 0.01f)
                dl.AddRectFilled(decTL, decBR,
                    DA(Alpha(Accent, (byte)(60 * tDec + 60 * tDecA))),
                    0f, ImDrawFlags.RoundCornersBottomRight);

            // ── Value text - always drawn by us (ImGui's own text is hidden) ──
            // Show live buffer when typing, formatted value otherwise.

            // + / ? drawn as filled rects on the window draw list (not foreground).
            // Using the window draw list means ImGui's own clip rect for the current
            // scroll region automatically hides symbols that have scrolled out of view.
            const float SymA = 4f;   // half-arm length
            const float SymB = 1f;   // half-arm thickness
            float symCx   = MathF.Floor(incTL.X + NumArrowW * 0.5f);
            float symCyUp = MathF.Floor(incTL.Y + halfH * 0.5f);
            float symCyDn = MathF.Floor(decTL.Y + halfH * 0.5f);

            uint incCol = DA(Lerp(Alpha(TxtDim, 180), Alpha(TxtNorm, 255), MathF.Max(tInc, tIncA)));
            uint decCol = DA(Lerp(Alpha(TxtDim, 180), Alpha(TxtNorm, 255), MathF.Max(tDec, tDecA)));

            // + horizontal bar
            dl.AddRectFilled(new Vector2(symCx - SymA, symCyUp - SymB), new Vector2(symCx + SymA, symCyUp + SymB), incCol);
            // + vertical bar
            dl.AddRectFilled(new Vector2(symCx - SymB, symCyUp - SymA), new Vector2(symCx + SymB, symCyUp + SymA), incCol);

            // ? horizontal bar
            dl.AddRectFilled(new Vector2(symCx - SymA, symCyDn - SymB), new Vector2(symCx + SymA, symCyDn + SymB), decCol);

            // Label - right-aligned in label column (flush against the widget)
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                float maxLabelW = MathF.Max(0f, ix - 6f - tl.X);
                string dispLabel = TruncateText(font, fsize, cleanLabel, maxLabelW);
                float lw = MeasureText(font, fsize, dispLabel).X;
                dl.AddText(font, fsize, new Vector2(ix - lw - 6f, tl.Y + (NumH - fsize) * .5f), DA(TxtNorm), dispLabel);
            }

            // Advance cursor - register the FULL widget bounds as one normal-flow item
            // (not a zero-height dummy below it) so a subsequent SameLine() call correctly
            // continues on this same row instead of dropping to the row below (previously
            // caused X/Y/Z inputs placed via SameLine to stack vertically like a ladder).
            ImGuiNET.ImGui.SetCursorScreenPos(tl);
            ImGuiNET.ImGui.Dummy(new Vector2(w, NumH));
            _lastRowTL = new Vector2(tl.X, tl.Y);
            _lastRowBR = new Vector2(tl.X + w, tl.Y + NumH);
            if (hint != null) Tooltip(hint);
            return changed;
        }


        // ───────────────────────────────────────────────────────────────────────────
        // TEXTBOX  - single-line text input
        //
        //  string buf = "";
        //  UI.InputText("Search##t1", ref buf, 256);
        // ───────────────────────────────────────────────────────────────────────────

        private const float InputH     = 30f;
        private const float InputRound = 5f;

        public static bool InputText(string label, ref string value, uint maxLen = 256,
            string placeholder = "", float width = 0f,
            bool error = false, string errorMsg = null,
            bool numericOnly = false, bool required = false, string hint = null,
            float labelWidth = 0f)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            // Inline label reserves labelWidth on the left; the box takes the rest.
            float availForBox = labelWidth > 0f ? avail - labelWidth : avail;
            float w     = MathF.Max(InputH, width > 0f ? MathF.Min(width, availForBox) : availForBox);

            string cleanLabel = Loc(label);
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                if (labelWidth > 0f)
                {
                    // Inline: label to the left, vertically centered with the box.
                    Vector2 lpos = ImGuiNET.ImGui.GetCursorScreenPos();
                    float ly = lpos.Y + (InputH - fsize) * 0.5f;
                    DL.AddText(font, fsize, new Vector2(lpos.X, ly), DA(TxtNorm),
                        TruncateText(font, fsize, cleanLabel, labelWidth - 6f));
                    ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(lpos.X + labelWidth, lpos.Y));
                }
                else
                {
                    Vector2 lpos = ImGuiNET.ImGui.GetCursorScreenPos();
                    DL.AddText(font, fsize, lpos, DA(TxtNorm), TruncateText(font, fsize, cleanLabel, w));
                    ImGuiNET.ImGui.Dummy(new Vector2(w, fsize + 4f));
                }
            }

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 br = new(tl.X + w, tl.Y + InputH);
            var dl = DL;

            // Pre-compute the item ID that InputText will use.  ImGui IDs are
            // deterministic for a given window + ID-stack state, so GetID() called
            // with the same string right before the widget gives the same result.
            // We use this to read last-frame animation values so the background can
            // be drawn BEFORE the widget (putting it behind ImGui's selection highlight
            // and text cursor) while still using the correct hover/focus colours.
            uint preId  = ImGuiNET.ImGui.GetID("##inp_" + label);
            float tf0   = _anim.TryGetValue(preId ^ 0xF0u, out var _tf0) ? _tf0 : 0f;
            float th0   = _anim.TryGetValue(preId ^ 0xF3u, out var _th0) ? _th0 : 0f;

            // Background drawn first - sits behind ImGui's selection rect and text.
            dl.AddRectFilled(tl, br,
                DA(Lerp(Alpha(ImGuiStyle.WidgetBg, 200), Alpha(InputBg, 220), th0)),
                InputRound);

            // Render the real widget; Text = TxtNorm so ImGui draws text and the
            // selection highlight (TextSelectedBg) itself, both on top of our background.
            ImGuiNET.ImGui.SetNextItemWidth(w);
            ImGuiNET.ImGui.PushFont(font);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg,        Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,         Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,           ToV4(DA(TxtNorm)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(8f, (InputH - fsize) * .5f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, InputRound);

            bool changed = false;
            if (!IsDisabled)
                changed = ImGuiNET.ImGui.InputText("##inp_" + label, ref value, maxLen,
                    numericOnly ? ImGuiInputTextFlags.CharsDecimal : ImGuiInputTextFlags.None);
            else
            {
                string tmp = value;
                ImGuiNET.ImGui.InputText("##inp_" + label, ref tmp, maxLen, ImGuiInputTextFlags.ReadOnly);
            }

            if (numericOnly && changed)
            {
                bool hasNonDigit = false;
                foreach (char c in value) if (c < '0' || c > '9') { hasNonDigit = true; break; }
                if (hasNonDigit)
                {
                    var sb = new System.Text.StringBuilder(value.Length);
                    foreach (char c in value) if (c >= '0' && c <= '9') sb.Append(c);
                    value = sb.ToString();
                }
            }

            ImGuiNET.ImGui.PopStyleVar(2);
            ImGuiNET.ImGui.PopStyleColor(5);
            ImGuiNET.ImGui.PopFont();

            bool focused = ImGuiNET.ImGui.IsItemFocused();
            bool hovered = ImGuiNET.ImGui.IsItemHovered();
            uint inpId   = ImGuiNET.ImGui.GetItemID();  // == preId
            float tf     = Anim(inpId ^ 0xF0u, focused ? 1f : 0f, 14f);
            float th     = Anim(inpId ^ 0xF3u, hovered ? 1f : 0f, 12f);

            // Border drawn after ImGui's content so it sits on top.
            if (tf > 0.01f)
                dl.AddRect(new Vector2(tl.X - 1f, tl.Y - 1f), new Vector2(br.X + 1f, br.Y + 1f),
                    Alpha(Accent, (byte)(35 * tf)), InputRound + 1f, ImDrawFlags.None, 1f);
            dl.AddRect(tl, br,
                DA(Lerp(Lerp(Alpha(BorderCol, 120), Alpha(BorderCol, 200), th), Alpha(Accent, 200), tf)),
                InputRound, ImDrawFlags.None, focused ? 1.5f : 1f);

            // Placeholder - ImGui doesn't render this, so we handle it manually.
            float textY = tl.Y + (InputH - fsize) * .5f;
            if (!focused && string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(placeholder))
            {
                dl.PushClipRect(new Vector2(tl.X + 2f, tl.Y), new Vector2(br.X - 2f, br.Y), true);
                dl.AddText(font, fsize, new Vector2(tl.X + 8f, textY), DA(Alpha(TxtDim, 140)), LocManager.Get(placeholder));
                dl.PopClipRect();
            }

            // Error state - overrides normal border with a red glow.
            bool isError = error || (required && string.IsNullOrWhiteSpace(value));
            if (isError)
            {
                uint ec = ImGuiStyle.Danger;
                dl.AddRect(tl, br, Alpha(ec, 200), InputRound, ImDrawFlags.None, 1.5f);
                dl.AddRect(new Vector2(tl.X - 2f, tl.Y - 2f), new Vector2(br.X + 2f, br.Y + 2f),
                    Alpha(ec, 40), InputRound + 2f, ImDrawFlags.None, 1f);
                if (!string.IsNullOrEmpty(errorMsg))
                {
                    float efs = fsize * 0.88f;
                    dl.AddText(font, efs, new Vector2(tl.X + 2f, br.Y + 3f),
                        Alpha(ec, 185), LocManager.Get(errorMsg));
                    ImGuiNET.ImGui.Dummy(new Vector2(w, efs + 3f));
                }
            }

            // ── Right-click context menu (Cut / Copy / Paste) ────────────────────
            if (BeginContextMenu("##inp_ctx_" + label, tl, br))
            {
                bool  hasTxt = !string.IsNullOrEmpty(value);
                string clip;
                try { clip = ImGuiNET.ImGui.GetClipboardText() ?? ""; } catch { clip = ""; }
                bool hasClip = !string.IsNullOrEmpty(clip);

                if (MenuItem("Cut",   hasTxt && !IsDisabled))
                { ImGuiNET.ImGui.SetClipboardText(value); value = ""; changed = true; }
                if (MenuItem("Copy",  hasTxt))
                    ImGuiNET.ImGui.SetClipboardText(value);
                MenuSeparator();
                if (MenuItem("Paste", hasClip && !IsDisabled))
                {
                    string pasted = clip.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
                    if (pasted.Length > (int)maxLen) pasted = pasted[..(int)maxLen];
                    value = pasted; changed = true;
                }
                EndContextMenu();
            }

            _lastRowTL = tl;
            _lastRowBR = br;
            DrawSearchHighlight(Strip(label), _lastRowTL, _lastRowBR, InputRound);
            if (hint != null) Tooltip(hint);
            return changed;
        }

        public static bool InputTextMultiline(string label, ref string value, uint maxLen = 65536,
            float height = 120f, float width = 0f, string placeholder = "")
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
            float w     = width > 0f ? MathF.Min(width, avail) : avail;

            string cleanLabel = Loc(label);
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                Vector2 lpos = ImGuiNET.ImGui.GetCursorScreenPos();
                DL.AddText(font, fsize, lpos, DA(TxtNorm), cleanLabel);
                ImGuiNET.ImGui.Dummy(new Vector2(w, fsize + 4f));
            }

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 br = new(tl.X + w, tl.Y + height);
            var dl = DL;

            uint  preId = ImGuiNET.ImGui.GetID("##mlt_" + label);
            float tf0   = _anim.TryGetValue(preId ^ 0xF0u, out var _tf0) ? _tf0 : 0f;
            float th0   = _anim.TryGetValue(preId ^ 0xF3u, out var _th0) ? _th0 : 0f;

            dl.AddRectFilled(tl, br,
                DA(Lerp(Alpha(ImGuiStyle.WidgetBg, 200), Alpha(InputBg, 220), th0)),
                InputRound);

            ImGuiNET.ImGui.PushFont(font);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg,        Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,         Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,           ToV4(DA(TxtNorm)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(8f, 8f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, InputRound);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6f);

            bool changed = false;
            if (!IsDisabled)
                changed = ImGuiNET.ImGui.InputTextMultiline("##mlt_" + label, ref value, maxLen,
                    new Vector2(w, height));
            else
            {
                string tmp = value;
                ImGuiNET.ImGui.InputTextMultiline("##mlt_" + label, ref tmp, maxLen,
                    new Vector2(w, height), ImGuiInputTextFlags.ReadOnly);
            }

            ImGuiNET.ImGui.PopStyleVar(3);
            ImGuiNET.ImGui.PopStyleColor(5);
            ImGuiNET.ImGui.PopFont();

            bool  focused = ImGuiNET.ImGui.IsItemFocused();
            bool  hovered = ImGuiNET.ImGui.IsItemHovered();
            uint  inpId   = ImGuiNET.ImGui.GetItemID();
            float tf      = Anim(inpId ^ 0xF0u, focused ? 1f : 0f, 14f);
            float th      = Anim(inpId ^ 0xF3u, hovered ? 1f : 0f, 12f);

            if (tf > 0.01f)
                dl.AddRect(new Vector2(tl.X - 1f, tl.Y - 1f), new Vector2(br.X + 1f, br.Y + 1f),
                    Alpha(Accent, (byte)(35 * tf)), InputRound + 1f, ImDrawFlags.None, 1f);
            dl.AddRect(tl, br,
                DA(Lerp(Lerp(Alpha(BorderCol, 120), Alpha(BorderCol, 200), th), Alpha(Accent, 200), tf)),
                InputRound, ImDrawFlags.None, focused ? 1.5f : 1f);

            if (!focused && string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(placeholder))
                dl.AddText(font, fsize, new Vector2(tl.X + 8f, tl.Y + 8f),
                    DA(Alpha(TxtDim, 140)), LocManager.Get(placeholder));

            // ── Right-click context menu (Cut / Copy / Paste) ────────────────────
            // Unlike InputText's single-line paste, newlines are kept as-is here - the
            // whole point of a multiline box is to hold multi-line pasted text.
            if (BeginContextMenu("##mlt_ctx_" + label, tl, br))
            {
                bool  hasTxt = !string.IsNullOrEmpty(value);
                string clip;
                try { clip = ImGuiNET.ImGui.GetClipboardText() ?? ""; } catch { clip = ""; }
                bool hasClip = !string.IsNullOrEmpty(clip);

                if (MenuItem("Cut",   hasTxt && !IsDisabled))
                { ImGuiNET.ImGui.SetClipboardText(value); value = ""; changed = true; }
                if (MenuItem("Copy",  hasTxt))
                    ImGuiNET.ImGui.SetClipboardText(value);
                MenuSeparator();
                if (MenuItem("Paste", hasClip && !IsDisabled))
                {
                    value = clip.Length > (int)maxLen ? clip[..(int)maxLen] : clip;
                    changed = true;
                }
                EndContextMenu();
            }

            return changed;
        }

        //───────────────────────────────────────────────────────────────────────────
        // COMBOBOX  - custom header + themed dropdown
        //
        //  int sel = 0;
        //  string[] opts = { "Option A", "Option B" };
        //  UI.Combo("Mode##c1", ref sel, opts);
        // ───────────────────────────────────────────────────────────────────────────

        private const float ComboH     = 30f;
        private const float ComboRound = 5f;

        public static bool Combo(string label, ref int selectedIndex, string[] items,
            float width = 0f, string hint = null, bool inlineLabel = false, int maxVisibleItems = 0,
            bool localizeItems = true)
        {
            if (items == null || items.Length == 0) return false;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            float w     = width > 0f ? MathF.Min(width, avail) : avail;

            string cleanLabel = Loc(label);
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                if (inlineLabel)
                {
                    const float LabelGap = 6f;
                    float labelW = font.CalcTextSizeA(fsize, float.MaxValue, 0f, cleanLabel).X;
                    Vector2 cur = ImGuiNET.ImGui.GetCursorScreenPos();
                    DL.AddText(font, fsize, new Vector2(cur.X, cur.Y + (ComboH - fsize) * 0.5f), DA(TxtNorm), cleanLabel);
                    ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(cur.X + labelW + LabelGap, cur.Y));
                    w = MathF.Max(40f, w - labelW - LabelGap);
                }
                else
                {
                    Vector2 lpos = ImGuiNET.ImGui.GetCursorScreenPos();
                    DL.AddText(font, fsize, lpos, DA(TxtNorm), cleanLabel);
                    ImGuiNET.ImGui.Dummy(new Vector2(w, fsize + 4f));
                }
            }

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 br = new(tl.X + w, tl.Y + ComboH);

            // Render BeginCombo first so it owns the hit region
            ImGuiNET.ImGui.SetNextItemWidth(w);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg,              Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,       Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive,        Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,               Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button,               Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered,        Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonActive,         Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,                 Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg,              Alpha(ImGuiStyle.DropdownBg, 252));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,               Alpha(BorderCol, 120));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Alpha(InputBg, 200));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Alpha(BorderCol, 160));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Alpha(Accent, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Alpha(Accent, 220));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(8f, (ComboH - fsize) * .5f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ComboRound);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(0f, 1f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);

            bool   changed = false;
            // Localize item text so translated builds don't leave dropdown entries in English -
            // the label above already goes through Loc(); the list contents didn't.
            // Skippable via localizeItems: callers feeding dynamic data (item search results,
            // locale codes, etc.) must opt out - LocManager.Get() permanently registers every
            // string it sees as a translatable UI key, and that data isn't UI text.
            var    locItems = new string[items.Length];
            for (int li = 0; li < items.Length; li++)
                locItems[li] = localizeItems ? LocManager.Get(items[li]) : items[li];
            string preview = locItems[Math.Clamp(selectedIndex, 0, items.Length - 1)];

            // Opt-in exact-fit popup height: shows up to maxVisibleItems rows before
            // scrolling, and shrinks to exactly fit the list when there are fewer than
            // that (no dead space, no unnecessary scrollbar). Left at ImGui's own default
            // sizing when maxVisibleItems is unset (0), so other Combo() call sites are
            // unaffected.
            if (maxVisibleItems > 0)
            {
                int   visibleCount = Math.Min(items.Length, maxVisibleItems);
                float popupH       = visibleCount * ListItemH + Math.Max(0, visibleCount - 1) * 1f + 2f;
                ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(0f, popupH), new Vector2(float.MaxValue, popupH));
            }

            bool comboOpen = false;
            if (!IsDisabled)
            {
                comboOpen = ImGuiNET.ImGui.BeginCombo("##cmb_" + label, "", ImGuiComboFlags.NoArrowButton);
                if (comboOpen)
                {
                    var   popupDL  = ImGuiNET.ImGui.GetWindowDrawList();
                    var   clipMin  = ImGuiNET.ImGui.GetWindowPos();
                    var   clipMax  = clipMin + ImGuiNET.ImGui.GetWindowSize();
                    popupDL.PushClipRect(clipMin, clipMax, true);

                    ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 1f));

                    float rowW2   = ImGuiNET.ImGui.GetContentRegionAvail().X;
                    float sbW     = ImGuiNET.ImGui.GetScrollMaxY() > 0f ? 8f : 0f;
                    float fillL   = clipMin.X + 4f;
                    float fillR   = sbW > 0f ? clipMax.X - sbW : clipMax.X - 4f;
                    int   cn      = items.Length;

                    // Virtual clipping: for large lists only render the rows visible
                    // inside the popup viewport. Small lists render all items directly.
                    const int ComboVirtThreshold = 20;
                    int firstVis, lastVis;
                    if (cn > ComboVirtThreshold)
                    {
                        float scrollY = ImGuiNET.ImGui.GetScrollY();
                        float viewH   = ImGuiNET.ImGui.GetWindowSize().Y;
                        firstVis = Math.Max(0, (int)(scrollY / ListItemH) - 1);
                        lastVis  = Math.Min(cn - 1, (int)((scrollY + viewH) / ListItemH) + 1);
                        if (firstVis > 0)
                            ImGuiNET.ImGui.Dummy(new Vector2(rowW2, firstVis * ListItemH));
                    }
                    else
                    {
                        firstVis = 0;
                        lastVis  = cn - 1;
                    }

                    for (int i = firstVis; i <= lastVis; i++)
                    {
                        bool    sel   = i == selectedIndex;
                        Vector2 rowTL = ImGuiNET.ImGui.GetCursorScreenPos();

                        // Transparent Selectable so keyboard nav (arrows + Enter) works
                        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Header,        0u);
                        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0u);
                        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.HeaderActive,  0u);
                        bool activated = ImGuiNET.ImGui.Selectable("##cmbrow_" + label + i, false,
                            ImGuiSelectableFlags.None, new Vector2(0f, ListItemH));
                        ImGuiNET.ImGui.PopStyleColor(3);
                        bool  rowHov = ImGuiNET.ImGui.IsItemHovered();
                        uint  rowId  = ImGuiNET.ImGui.GetItemID();
                        float rth    = Anim(rowId, rowHov ? 1f : 0f, 14f);
                        if (sel) ImGuiNET.ImGui.SetItemDefaultFocus();

                        if (activated) { selectedIndex = i; changed = true; ImGuiNET.ImGui.CloseCurrentPopup(); }

                        Vector2 fillTL = new(fillL, rowTL.Y);
                        Vector2 fillBR = new(fillR, rowTL.Y + ListItemH);
                        if (sel)
                        {
                            popupDL.AddRectFilled(fillTL, fillBR, DA(Lerp(Alpha(Accent, 60), Alpha(Accent, 90), rth)), 4f);
                            popupDL.AddRectFilled(fillTL, new Vector2(fillTL.X + 3f, fillBR.Y), DA(Accent), 2f);
                        }
                        else if (rth > 0.01f)
                            popupDL.AddRectFilled(fillTL, fillBR, DA(Alpha(ImGuiStyle.Dark3, (byte)(80 * rth))), 4f);

                        uint txtCol = sel
                            ? Lerp(Alpha(TxtNorm, 220), 0xFFFFFFFF, rth)
                            : Lerp(TxtDim, TxtNorm, rth);
                        popupDL.AddText(font, fsize,
                            new Vector2(fillL + 10f, rowTL.Y + (ListItemH - fsize) * .5f),
                            DA(txtCol), locItems[i]);
                    }

                    if (cn > ComboVirtThreshold)
                    {
                        int below = cn - lastVis - 1;
                        if (below > 0)
                            ImGuiNET.ImGui.Dummy(new Vector2(rowW2, below * ListItemH));
                    }

                    ImGuiNET.ImGui.PopStyleVar();
                    popupDL.PopClipRect();
                    ImGuiNET.ImGui.EndCombo();
                }
            }
            else
            {
                ImGuiNET.ImGui.BeginDisabled();
                ImGuiNET.ImGui.BeginCombo("##cmb_" + label, "", ImGuiComboFlags.NoArrowButton);
                ImGuiNET.ImGui.EndDisabled();
            }

            bool  hov  = ImGuiNET.ImGui.IsItemHovered();
            bool  open = ImGuiNET.ImGui.IsPopupOpen("##cmb_" + label);
            uint  cmbId = ImGuiNET.ImGui.GetItemID();
            float th    = Anim(cmbId ^ 0xE2u, hov  ? 1f : 0f, 12f);
            float to    = Anim(cmbId ^ 0xE0u, open ? 1f : 0f, 16f);

            ImGuiNET.ImGui.PopStyleVar(6);
            ImGuiNET.ImGui.PopStyleColor(14);

            // ── Custom header chrome ──────────────────────────────────────
            var dl = DL;
            dl.AddRectFilled(tl, br,
                DA(Lerp(Lerp(Alpha(ImGuiStyle.WidgetBg, 200), Alpha(InputBg, 220), th),
                             Alpha(InputBgH, 240), to)),
                open ? ComboRound * 0.5f : ComboRound);

            dl.AddRect(tl, br,
                DA(Lerp(Lerp(Alpha(BorderCol, 120), Alpha(BorderCol, 200), th), Alpha(Accent, 200), to)),
                open ? ComboRound * 0.5f : ComboRound, ImDrawFlags.None, open ? 1.5f : 1f);

            // Our own preview text - clip to the area left of the chevron so long
            // values don't bleed outside the combo bounds.
            var comboClip = new Vector4(tl.X, tl.Y, br.X - 20f, br.Y);
            dl.AddText(font, fsize, new Vector2(tl.X + 8f, tl.Y + (ComboH - fsize) * .5f),
                DA(TxtNorm), preview, 0f, ref comboClip);

            float cx    = br.X - 16f;
            float cy    = tl.Y + ComboH * .5f;
            float angle = MathF.PI * 0.5f + to * MathF.PI * 0.5f;
            DrawChevron(dl, cx, cy, 5f, angle,
                DA(Lerp(Alpha(TxtDim, 160), Alpha(Accent, 220), MathF.Max(th, to))));

            _lastRowTL = tl;
            _lastRowBR = br;
            if (hint != null) Tooltip(hint);
            ShowDisabledReasonTooltip();
            return changed;
        }

        //───────────────────────────────────────────────────────────────────────────
        // SEGMENTED BAR  - horizontal row of mutually exclusive pill buttons.
        // Auto-detects group context so it sizes itself correctly inside
        // BeginGroup/EndGroup with zero configuration.
        //
        //  UI.SegmentedBar("Entity Type##et", new[]{"All","PMC","Scav"}, ref sel);
        // ───────────────────────────────────────────────────────────────────────────

        private const float SegH      = 26f;
        private const float SegRound  = 5f;
        private const float SegPadX   = 6f;   // horizontal padding inside each pill
        private const float SegGapX   = 3f;   // gap between pills
        private const float SegGapY   = 3f;   // gap between rows (wrap mode)

        // Per-bar scroll offset in scroll mode, keyed by label hash
        private static readonly Dictionary<uint, float> _segScroll = new(8);

        public static bool SegmentedBar(string label, string[] options, ref int selected, float width = 0f, bool autoSize = false, string hint = null, bool stretch = false, bool? scrollable = null)
        {
            if (options == null || options.Length == 0) return false;

            var   font       = ImGuiStyle.FontNormal;
            float fsize      = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail      = width > 0f ? width
                             : _groupAvailW > 0f ? GroupInnerWidth
                             : ImGuiNET.ImGui.GetContentRegionAvail().X;
            int   n          = options.Length;
            bool  isScrollable = scrollable ?? false;

            string cleanLabel = Loc(label);
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                DL.AddText(font, fsize, ImGuiNET.ImGui.GetCursorScreenPos(), DA(TxtNorm), cleanLabel);
                ImGuiNET.ImGui.Dummy(new Vector2(avail, fsize + 4f));
            }

            // Localize option labels so keys are registered under the current panel scope.
            var locOpts = new string[n];
            for (int i = 0; i < n; i++) locOpts[i] = LocManager.Get(options[i]);

            // Measure each pill's natural width
            var pillW = new float[n];
            for (int i = 0; i < n; i++)
                pillW[i] = MeasureText(font, fsize, locOpts[i]).X + SegPadX * 2f;

            // Shrink track to exactly fit pills + inter-pill gaps (no trailing empty track space)
            if (autoSize)
            {
                float natural = 0f;
                for (int i = 0; i < n; i++) natural += pillW[i];
                natural += (n - 1) * SegGapX;
                avail = natural;
            }

            // Stretch pills to fill avail (used by SegmentedRow so it behaves like a slider track).
            // Scale only the pill widths against the space remaining after fixed gaps,
            // so pills + gaps == avail exactly (no trailing gap at the right edge).
            if (stretch && !autoSize)
            {
                float totalPillNat = 0f;
                for (int i = 0; i < n; i++) totalPillNat += pillW[i];
                if (totalPillNat > 0f)
                {
                    float scale = MathF.Max(0f, (avail - (n - 1) * SegGapX) / totalPillNat);
                    for (int i = 0; i < n; i++) pillW[i] = MathF.Max(SegPadX * 2f, pillW[i] * scale);
                    for (int i = 0; i < n; i++)
                        locOpts[i] = TruncateText(font, fsize, locOpts[i], MathF.Max(0f, pillW[i] - SegPadX * 2f));
                }
            }

            // Compute bar height
            float barH;
            if (isScrollable)
            {
                barH = SegH;
            }
            else
            {
                // Epsilon guards against a stretch-scaled pill sum landing a hair over `avail`
                // purely from float rounding - without it, a bar whose pills exactly fill one row
                // could spuriously wrap to a 2nd (empty) row, doubling its drawn height.
                const float WrapEpsilon = 0.5f;
                float rx = 0f; int rows = 1;
                for (int i = 0; i < n; i++)
                {
                    if (rx + pillW[i] > avail + WrapEpsilon && rx > 0f) { rows++; rx = 0f; }
                    rx += pillW[i] + SegGapX;
                }
                barH = rows * SegH + (rows - 1) * SegGapY;
            }

            Vector2 barTL = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 barBR = new(barTL.X + avail, barTL.Y + barH);
            uint    barId = (uint)label.GetHashCode();

            // Total content width for scroll clamping / scrollbar sizing
            float totalW = 0f;
            for (int i = 0; i < n; i++) totalW += pillW[i] + SegGapX;
            float maxScroll = MathF.Max(0f, totalW - avail + SegGapX);

            // ── Scrollbar hit region (placed before bar region so it wins clicks) ──
            const float SbH = 3f, SbGap = 2f, SbRound = 1.5f;
            bool hasSb = isScrollable && maxScroll > 0f;
            float sbY  = barTL.Y + barH + SbGap;
            bool sbHov = false, sbAct = false;

            if (hasSb)
            {
                _segScroll.TryGetValue(barId, out float sx0);
                float ratio0  = avail / totalW;
                float thumbW0 = MathF.Max(avail * ratio0, 20f);
                float thumbX0 = barTL.X + (sx0 / maxScroll) * (avail - thumbW0);

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(thumbX0, sbY - 2f));
                ImGuiNET.ImGui.InvisibleButton("##seg_sb_" + label, new Vector2(thumbW0, SbH + 4f));
                sbHov = ImGuiNET.ImGui.IsItemHovered();
                sbAct = ImGuiNET.ImGui.IsItemActive();

                if (sbAct)
                {
                    float trackRange = avail - thumbW0;
                    if (trackRange > 0f)
                    {
                        float delta = ImGuiNET.ImGui.GetIO().MouseDelta.X * (maxScroll / trackRange);
                        sx0 = Math.Clamp(sx0 + delta, 0f, maxScroll);
                        _segScroll[barId] = sx0;
                    }
                }
            }

            // ── Bar-level invisible button - captures wheel/drag scroll ──────
            ImGuiNET.ImGui.SetCursorScreenPos(barTL);
            ImGuiNET.ImGui.InvisibleButton("##seg_bar_" + label, new Vector2(avail, barH));
            bool barHov = ImGuiNET.ImGui.IsItemHovered();
            bool barAct = ImGuiNET.ImGui.IsItemActive();

            if (isScrollable)
            {
                _segScroll.TryGetValue(barId, out float scrollX);
                if (barHov || sbHov)
                {
                    float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
                    if (wheel != 0f)
                    {
                        scrollX -= wheel * 30f;
                        ImGuiNET.ImGui.GetIO().MouseWheel = 0f;
                        MarkWheelConsumed();
                    }
                }
                if (barAct && !sbAct) scrollX -= ImGuiNET.ImGui.GetIO().MouseDelta.X;
                scrollX = Math.Clamp(scrollX, 0f, maxScroll);
                _segScroll[barId] = scrollX;
            }

            _segScroll.TryGetValue(barId, out float scrollOff);
            var   dl      = DL;
            bool  changed = false;

            // Draw track background
            dl.AddRectFilled(barTL, barBR, DA(ImGuiStyle.WidgetBg), SegRound);

            // Clip pills in scroll mode
            if (isScrollable)
                dl.PushClipRect(barTL, new Vector2(barTL.X + avail, barTL.Y + barH), true);

            // Draw pills
            float cx = barTL.X - (isScrollable ? scrollOff : 0f);
            float cy = barTL.Y;
            Vector2 mp = ImGuiNET.ImGui.GetIO().MousePos;

            for (int i = 0; i < n; i++)
            {
                float pw = pillW[i];

                // Wrap in non-scrollable mode - same WrapEpsilon tolerance as the barH precompute
                // above, so the two stay consistent (a pill can't wrap into a row the box wasn't
                // actually sized for, or vice versa).
                if (!isScrollable && cx + pw > barTL.X + avail + 0.5f && cx > barTL.X)
                { cx = barTL.X; cy += SegH + SegGapY; }

                Vector2 pTL = new(cx, cy);
                Vector2 pBR = new(cx + pw, cy + SegH);

                bool isSel   = i == selected;
                uint pillId  = (uint)(barId * 2654435761u ^ (uint)i);
                bool pillHov = barHov
                    && mp.X >= pTL.X && mp.X <= pBR.X
                    && mp.Y >= pTL.Y && mp.Y <= pBR.Y;

                if (!IsDisabled && pillHov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                { selected = i; changed = true; }

                float tv = Anim(pillId,         isSel    ? 1f : 0f, 16f);
                float th = Anim(pillId ^ 0xAAu, pillHov  ? 1f : 0f, 12f);

                // Hover fill
                if (th > 0.01f)
                    dl.AddRectFilled(pTL, pBR, DA(Alpha(Dark3, (byte)(90 * th))), SegRound);

                // Selected fill + bottom accent bar
                if (tv > 0.01f)
                {
                    dl.AddRectFilled(pTL, pBR, DA(Alpha(Accent, (byte)(52 * tv))), SegRound);
                    dl.AddRectFilled(
                        new Vector2(pTL.X + 4f, pBR.Y - 2.5f),
                        new Vector2(pBR.X - 4f, pBR.Y - 0.5f),
                        DA(Alpha(Accent, (byte)(220 * tv))), 1f);
                }

                // Label
                Vector2 ts = MeasureText(font, fsize, locOpts[i]);
                uint    lc = DA(Lerp(Lerp(TxtDim, TxtNorm, tv > 0.5f ? 0f : th), 0xFFFFFFFF, tv));
                dl.AddText(font, fsize,
                    new Vector2(pTL.X + (pw - ts.X) * .5f, pTL.Y + (SegH - fsize) * .5f),
                    lc, locOpts[i]);

                // Pulse the pill when search navigated to this tab/option.
                DrawSearchHighlight(options[i], pTL, pBR, SegRound);

                cx += pw + SegGapX;
            }

            if (isScrollable) dl.PopClipRect();

            // Outer border sits above fills
            dl.AddRect(barTL, barBR, DA(Alpha(BorderCol, 110)), SegRound, ImDrawFlags.None, 1f);

            // ── Scrollbar ─────────────────────────────────────────────────────
            if (hasSb)
            {
                _segScroll.TryGetValue(barId, out float sx);
                float ratio  = avail / totalW;
                float thumbW = MathF.Max(avail * ratio, 20f);
                float thumbX = barTL.X + (sx / maxScroll) * (avail - thumbW);

                // Track
                dl.AddRectFilled(
                    new Vector2(barTL.X, sbY),
                    new Vector2(barTL.X + avail, sbY + SbH),
                    DA(Alpha(ImGuiStyle.Dark3, 120)), SbRound);

                // Thumb
                uint thumbCol = sbAct ? DA(Accent)
                              : sbHov ? DA(Alpha(Accent, 180))
                              :         DA(Alpha(BorderCol, 150));
                dl.AddRectFilled(
                    new Vector2(thumbX, sbY),
                    new Vector2(thumbX + thumbW, sbY + SbH),
                    thumbCol, SbRound);
            }

            // Advance cursor
            float totalH = barH + (hasSb ? SbH + SbGap : 0f);
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(barTL.X, barTL.Y + totalH));
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 0f));

            _lastRowTL = barTL;
            _lastRowBR = barBR;
            if (hint != null) Tooltip(hint);
            return changed;
        }

        /// <summary>
        /// Multi-select variant of <see cref="SegmentedBar(string,string[],ref int,float,bool,string,bool,bool?)"/>.
        /// Each pill toggles its corresponding <paramref name="selected"/> entry independently.
        /// </summary>
        public static bool SegmentedBar(string label, string[] options, bool[] selected,
            float width = 0f, bool autoSize = false, string hint = null, bool stretch = false, bool? scrollable = null,
            float gapX = SegGapX, bool perPillBg = false)
        {
            if (options == null || selected == null || options.Length == 0) return false;

            var   font       = ImGuiStyle.FontNormal;
            float fsize      = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail      = width > 0f ? width
                             : _groupAvailW > 0f ? GroupInnerWidth
                             : ImGuiNET.ImGui.GetContentRegionAvail().X;
            int   n          = options.Length;
            bool  isScrollable = scrollable ?? false;

            string cleanLabel = Loc(label);
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                DL.AddText(font, fsize, ImGuiNET.ImGui.GetCursorScreenPos(), DA(TxtNorm), cleanLabel);
                ImGuiNET.ImGui.Dummy(new Vector2(avail, fsize + 4f));
            }

            var locOpts = new string[n];
            for (int i = 0; i < n; i++) locOpts[i] = LocManager.Get(options[i]);

            var pillW = new float[n];
            for (int i = 0; i < n; i++)
                pillW[i] = MeasureText(font, fsize, locOpts[i]).X + SegPadX * 2f;

            if (autoSize)
            {
                float natural = 0f;
                for (int i = 0; i < n; i++) natural += pillW[i];
                natural += (n - 1) * gapX;
                avail = natural;
            }

            if (stretch && !autoSize)
            {
                float totalPillNat = 0f;
                for (int i = 0; i < n; i++) totalPillNat += pillW[i];
                if (totalPillNat > 0f)
                {
                    float scale = (avail - (n - 1) * gapX) / totalPillNat;
                    for (int i = 0; i < n; i++) pillW[i] *= scale;
                }
            }

            float barH;
            if (isScrollable)
            {
                barH = SegH;
            }
            else
            {
                // Epsilon guards against a stretch-scaled pill sum landing a hair over `avail`
                // purely from float rounding - see the single-select SegmentedBar overload above.
                const float WrapEpsilon = 0.5f;
                float rx = 0f; int rows = 1;
                for (int i = 0; i < n; i++)
                {
                    if (rx + pillW[i] > avail + WrapEpsilon && rx > 0f) { rows++; rx = 0f; }
                    rx += pillW[i] + gapX;
                }
                barH = rows * SegH + (rows - 1) * SegGapY;
            }

            Vector2 barTL = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 barBR = new(barTL.X + avail, barTL.Y + barH);
            uint    barId = (uint)label.GetHashCode();

            float totalW  = 0f;
            for (int i = 0; i < n; i++) totalW += pillW[i] + gapX;
            float maxScroll = MathF.Max(0f, totalW - avail + gapX);

            const float SbH = 3f, SbGap = 2f, SbRound = 1.5f;
            bool hasSb = isScrollable && maxScroll > 0f;
            float sbY  = barTL.Y + barH + SbGap;
            bool sbHov = false, sbAct = false;

            if (hasSb)
            {
                _segScroll.TryGetValue(barId, out float sx0);
                float ratio0  = avail / totalW;
                float thumbW0 = MathF.Max(avail * ratio0, 20f);
                float thumbX0 = barTL.X + (sx0 / maxScroll) * (avail - thumbW0);

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(thumbX0, sbY - 2f));
                ImGuiNET.ImGui.InvisibleButton("##seg_sb_" + label, new Vector2(thumbW0, SbH + 4f));
                sbHov = ImGuiNET.ImGui.IsItemHovered();
                sbAct = ImGuiNET.ImGui.IsItemActive();

                if (sbAct)
                {
                    float trackRange = avail - thumbW0;
                    if (trackRange > 0f)
                    {
                        float delta = ImGuiNET.ImGui.GetIO().MouseDelta.X * (maxScroll / trackRange);
                        sx0 = Math.Clamp(sx0 + delta, 0f, maxScroll);
                        _segScroll[barId] = sx0;
                    }
                }
            }

            ImGuiNET.ImGui.SetCursorScreenPos(barTL);
            ImGuiNET.ImGui.InvisibleButton("##seg_bar_" + label, new Vector2(avail, barH));
            bool barHov = ImGuiNET.ImGui.IsItemHovered();
            bool barAct = ImGuiNET.ImGui.IsItemActive();

            if (isScrollable)
            {
                _segScroll.TryGetValue(barId, out float scrollX);
                if (barHov || sbHov)
                {
                    float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
                    if (wheel != 0f)
                    {
                        scrollX -= wheel * 30f;
                        ImGuiNET.ImGui.GetIO().MouseWheel = 0f;
                        MarkWheelConsumed();
                    }
                }
                if (barAct && !sbAct) scrollX -= ImGuiNET.ImGui.GetIO().MouseDelta.X;
                scrollX = Math.Clamp(scrollX, 0f, maxScroll);
                _segScroll[barId] = scrollX;
            }

            _segScroll.TryGetValue(barId, out float scrollOff);
            var   dl      = DL;
            bool  changed = false;

            // perPillBg = true skips the single continuous background so a widened gapX
            // reads as actual gaps between separate pills, instead of a wide gray band -
            // each pill gets its own background rect inside the loop below instead.
            if (!perPillBg)
                dl.AddRectFilled(barTL, barBR, DA(InputBg), SegRound);

            if (isScrollable)
                dl.PushClipRect(barTL, new Vector2(barTL.X + avail, barTL.Y + barH), true);

            float cx = barTL.X - (isScrollable ? scrollOff : 0f);
            float cy = barTL.Y;
            Vector2 mp = ImGuiNET.ImGui.GetIO().MousePos;

            for (int i = 0; i < n; i++)
            {
                float pw = pillW[i];

                if (!isScrollable && cx + pw > barTL.X + avail + 0.5f && cx > barTL.X)
                { cx = barTL.X; cy += SegH + SegGapY; }

                Vector2 pTL = new(cx, cy);
                Vector2 pBR = new(cx + pw, cy + SegH);

                if (perPillBg)
                    dl.AddRectFilled(pTL, pBR, DA(InputBg), SegRound);

                // ── Multi-select: each pill is independent ────────────────────
                bool isSel  = i < selected.Length && selected[i];
                uint pillId = (uint)(barId * 2654435761u ^ (uint)i);
                bool pillHov = barHov
                    && mp.X >= pTL.X && mp.X <= pBR.X
                    && mp.Y >= pTL.Y && mp.Y <= pBR.Y;

                if (!IsDisabled && pillHov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                { if (i < selected.Length) { selected[i] = !selected[i]; changed = true; } }

                float tv = Anim(pillId,         isSel   ? 1f : 0f, 16f);
                float th = Anim(pillId ^ 0xAAu, pillHov ? 1f : 0f, 12f);

                if (th > 0.01f)
                    dl.AddRectFilled(pTL, pBR, DA(Alpha(Dark3, (byte)(90 * th))), SegRound);

                if (tv > 0.01f)
                {
                    dl.AddRectFilled(pTL, pBR, DA(Alpha(Accent, (byte)(52 * tv))), SegRound);
                    dl.AddRectFilled(
                        new Vector2(pTL.X + 4f, pBR.Y - 2.5f),
                        new Vector2(pBR.X - 4f, pBR.Y - 0.5f),
                        DA(Alpha(Accent, (byte)(220 * tv))), 1f);
                }

                Vector2 ts = MeasureText(font, fsize, locOpts[i]);
                uint    lc = DA(Lerp(Lerp(TxtDim, TxtNorm, tv > 0.5f ? 0f : th), 0xFFFFFFFF, tv));
                dl.AddText(font, fsize,
                    new Vector2(pTL.X + (pw - ts.X) * .5f, pTL.Y + (SegH - fsize) * .5f),
                    lc, locOpts[i]);

                DrawSearchHighlight(options[i], pTL, pBR, SegRound);

                cx += pw + gapX;
            }

            if (isScrollable) dl.PopClipRect();

            // With perPillBg, an outer box around the whole (now-gapped) bar would defeat
            // the point of separating the pills - each pill already reads as its own button.
            if (!perPillBg)
                dl.AddRect(barTL, barBR, DA(Alpha(BorderCol, 110)), SegRound, ImDrawFlags.None, 1f);

            if (hasSb)
            {
                _segScroll.TryGetValue(barId, out float sx);
                float ratio  = avail / totalW;
                float thumbW = MathF.Max(avail * ratio, 20f);
                float thumbX = barTL.X + (sx / maxScroll) * (avail - thumbW);

                dl.AddRectFilled(
                    new Vector2(barTL.X, sbY),
                    new Vector2(barTL.X + avail, sbY + SbH),
                    DA(Alpha(ImGuiStyle.Dark3, 120)), SbRound);

                uint thumbCol = sbAct ? DA(Accent)
                              : sbHov ? DA(Alpha(Accent, 180))
                              :         DA(Alpha(BorderCol, 150));
                dl.AddRectFilled(
                    new Vector2(thumbX, sbY),
                    new Vector2(thumbX + thumbW, sbY + SbH),
                    thumbCol, SbRound);
            }

            float totalH = barH + (hasSb ? SbH + SbGap : 0f);
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(barTL.X, barTL.Y + totalH));
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 0f));

            if (hint != null && barHov) Tooltip(hint);
            return changed;
        }

        /// <summary>
        /// Label in the left column (respects BeginSliderGroup width) with a SegmentedBar
        /// inline in the right column - all on one row.
        /// Returns true if the selected value changed.
        /// </summary>
        public static bool SegmentedRow(string label, string[] options, ref int value, string hint = null)
        {
            if (options == null || options.Length == 0) return false;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;

            string cleanLabel = Loc(Strip(label));
            float  labelColW  = _groupLabelColW > 0f
                ? _groupLabelColW
                : MeasureText(font, fsize, cleanLabel).X + 10f;

            // Clamp so the bar always has a minimum width; prevents negative rightColW.
            const float SegMinBarW = 40f;
            labelColW = MathF.Min(labelColW, MathF.Max(0f, avail - SegMinBarW));
            float rightColW = avail - labelColW;

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();

            // Label vertically centred within the SegH bar height.
            string dispLabel = TruncateText(font, fsize, cleanLabel, MathF.Max(0f, labelColW - 4f));
            DL.AddText(font, fsize,
                new Vector2(tl.X + 2f, tl.Y + (SegH - fsize) * .5f),
                DA(IsDisabled ? TxtDim : TxtNorm), dispLabel);

            // Natural pill width - bar placed immediately after label, capped to rightColW.
            // When natural width exceeds the available column, scale pills down (stretch) instead of wrapping.
            float naturalW = (options.Length - 1) * SegGapX;
            for (int i = 0; i < options.Length; i++)
                naturalW += MeasureText(font, fsize, Loc(options[i])).X + SegPadX * 2f;
            bool  mustStretch = naturalW > rightColW;
            float barW        = mustStretch ? rightColW : MathF.Min(naturalW + 1f, rightColW);

            float barX = tl.X + labelColW;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(barX, tl.Y));
            bool changed = SegmentedBar("##segrow_" + Strip(label), options, ref value, barW, stretch: mustStretch);

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, tl.Y + SegH);
            if (hint != null && ImGuiNET.ImGui.IsMouseHoveringRect(_lastRowTL, _lastRowBR)) Tooltip(hint);
            return changed;
        }

        /// <summary>
        /// Multi-select variant of <see cref="SegmentedRow"/>. Label on the left, pills on the
        /// right - each pill toggles independently. Bar is naturally sized (never wider than its pills).
        /// </summary>
        public static bool SegmentedRowMulti(string label, string[] options, bool[] selected, string hint = null,
            float gapX = SegGapX, bool perPillBg = false, bool alignRight = false)
        {
            if (options == null || selected == null || options.Length == 0) return false;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;

            string cleanLabel = Loc(Strip(label));
            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();

            float naturalW = (options.Length - 1) * gapX;
            for (int i = 0; i < options.Length; i++)
                naturalW += MeasureText(font, fsize, Loc(options[i])).X + SegPadX * 2f;

            bool changed;
            if (alignRight)
            {
                // Whole "label + pills" unit hugs the right edge of the row instead of the
                // label sitting in a fixed left column - leftover space lands on the left.
                const float LabelGap = 8f;
                float labelNaturalW = MeasureText(font, fsize, cleanLabel).X + LabelGap;
                float barWR   = MathF.Min(naturalW + 1f, MathF.Max(20f, avail - labelNaturalW));
                float totalW  = labelNaturalW + barWR;
                float startX  = tl.X + MathF.Max(0f, avail - totalW);

                DL.AddText(font, fsize,
                    new Vector2(startX, tl.Y + (SegH - fsize) * .5f),
                    DA(IsDisabled ? TxtDim : TxtNorm), cleanLabel);

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(startX + labelNaturalW, tl.Y));
                changed = SegmentedBar("##segrowm_" + Strip(label), options, selected, barWR, gapX: gapX, perPillBg: perPillBg);
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(tl.X, tl.Y + SegH));
            }
            else
            {
                float labelColW = _groupLabelColW > 0f
                    ? _groupLabelColW
                    : MeasureText(font, fsize, cleanLabel).X + 10f;

                // Clamp so the bar always has a minimum width; prevents negative rightColW.
                labelColW = MathF.Min(labelColW, MathF.Max(0f, avail - 40f));
                float rightColW = avail - labelColW;

                string dispLabelM = TruncateText(font, fsize, cleanLabel, MathF.Max(0f, labelColW - 4f));
                DL.AddText(font, fsize,
                    new Vector2(tl.X + 2f, tl.Y + (SegH - fsize) * .5f),
                    DA(IsDisabled ? TxtDim : TxtNorm), dispLabelM);

                float barW = MathF.Min(naturalW + 1f, rightColW);

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(tl.X + labelColW, tl.Y));
                changed = SegmentedBar("##segrowm_" + Strip(label), options, selected, barW, gapX: gapX, perPillBg: perPillBg);
            }

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, tl.Y + SegH);
            if (hint != null && ImGuiNET.ImGui.IsMouseHoveringRect(_lastRowTL, _lastRowBR)) Tooltip(hint);
            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────
        // TAB ROW  ── underline-style panel navigation header.
        //
        // No surrounding box. Tabs render at natural width from the left.
        // Selected tab = bright text + animated accent underline.
        // Non-selected = dim text; hover = subtle fill.
        // Thin separator line drawn below the full row.
        //
        //   UI.TabRow(new[]{"General","Tiers","Containers"}, ref _tab);
        // ─────────────────────────────────────────────────────────────────────

        public static bool TabRow(string[] tabs, ref int selected, float width = 0f)
        {
            if (tabs == null || tabs.Length == 0) return false;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = width > 0f ? width
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;
            int   n     = tabs.Length;
            const float PadX = 14f;
            float rowH  = SegH;

            // Ensure avail is positive so InvisibleButton and clip rects stay valid.
            avail = MathF.Max(1f, avail);

            var locTabs = new string[n];
            var tabW    = new float[n];
            for (int i = 0; i < n; i++)
            {
                locTabs[i] = Loc(Strip(tabs[i]));
                tabW[i]    = MeasureText(font, fsize, locTabs[i]).X + PadX * 2f;
            }

            Vector2 rowTL   = ImGuiNET.ImGui.GetCursorScreenPos();
            var     dl      = DL;
            bool    changed = false;
            var     io      = ImGuiNET.ImGui.GetIO();
            Vector2 mp      = io.MousePos;

            // Stable base ID derived from tab labels, not screen position.
            // Position-based IDs reset Anim() on every drag frame, causing flicker.
            uint rowId = unchecked((uint)string.Join("|", tabs).GetHashCode());

            float totalW = 0f;
            for (int i = 0; i < n; i++) totalW += tabW[i];
            bool  overflow  = totalW > avail + 0.5f;
            float maxScroll = MathF.Max(0f, totalW - avail);

            _segScroll.TryGetValue(rowId, out float scrollOff);

            // Single invisible button spans the full row for hit-testing.
            ImGuiNET.ImGui.SetCursorScreenPos(rowTL);
            ImGuiNET.ImGui.InvisibleButton("##tabrow_" + tabs[0], new Vector2(avail, rowH));
            bool rowHov = ImGuiNET.ImGui.IsItemHovered();
            bool rowAct = ImGuiNET.ImGui.IsItemActive();

            // Wheel + drag scroll. Always consume wheel when hovering so the page below doesn't scroll.
            if (rowHov && io.MouseWheel != 0f)
            {
                if (overflow) scrollOff -= io.MouseWheel * 40f;
                io.MouseWheel = 0f;
                MarkWheelConsumed();
            }
            if (overflow && rowAct)
                scrollOff -= io.MouseDelta.X;
            if (overflow)
            {
                scrollOff = Math.Clamp(scrollOff, 0f, maxScroll);
                _segScroll[rowId] = scrollOff;
            }

            // Thin scrollbar rendered inside the bottom of the row BEFORE tab fills
            // so the accent underline paints on top and isn't dimmed by the track.
            if (overflow && maxScroll > 0f)
            {
                const float SbH = 2f, SbRound = 1f;
                float sbY    = rowTL.Y + rowH - SbH - 1f;
                float ratio  = avail / totalW;
                float thumbW = MathF.Max(avail * ratio, 20f);
                float thumbX = rowTL.X + (scrollOff / maxScroll) * (avail - thumbW);

                dl.AddRectFilled(
                    new Vector2(rowTL.X, sbY),
                    new Vector2(rowTL.X + avail, sbY + SbH),
                    DA(Alpha(Dark3, 80)), SbRound);
                dl.AddRectFilled(
                    new Vector2(thumbX, sbY),
                    new Vector2(thumbX + thumbW, sbY + SbH),
                    DA(Alpha(BorderCol, 130)), SbRound);
            }

            if (overflow)
                dl.PushClipRect(rowTL, new Vector2(rowTL.X + avail, rowTL.Y + rowH), true);

            float cx = rowTL.X - scrollOff;
            for (int i = 0; i < n; i++)
            {
                Vector2 tTL = new(cx, rowTL.Y);
                Vector2 tBR = new(cx + tabW[i], rowTL.Y + rowH);
                bool isSel  = i == selected;
                uint tid    = unchecked(rowId * 2654435761u ^ (uint)i);
                bool tHov   = rowHov && mp.X >= tTL.X && mp.X <= tBR.X;

                if (!IsDisabled && tHov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                { selected = i; changed = true; }

                float tv = Anim(tid,          isSel ? 1f : 0f, 14f);
                float th = Anim(tid ^ 0xCCu,  tHov  ? 1f : 0f, 12f);

                // Hover fill
                if (th > 0.01f)
                    dl.AddRectFilled(tTL, tBR, DA(Alpha(Dark3, (byte)(75 * th))), 4f);

                // Selected: subtle accent fill + bright underline (mirrors SegmentedBar treatment)
                if (tv > 0.01f)
                {
                    dl.AddRectFilled(tTL, tBR, DA(Alpha(Accent, (byte)(45 * tv))), 4f);
                    dl.AddRectFilled(
                        new Vector2(tTL.X + 4f, tBR.Y - 3f),
                        new Vector2(tBR.X - 4f, tBR.Y - 0.5f),
                        DA(Alpha(Accent, (byte)(255 * tv))), 1.5f);
                }

                // Label: dim → normal on hover, white on select
                uint lc = DA(isSel
                    ? Lerp(TxtNorm, 0xFFFFFFFF, tv)
                    : Lerp(TxtDim, TxtNorm, th));
                float textW = MeasureText(font, fsize, locTabs[i]).X;
                dl.AddText(font, fsize,
                    new Vector2(tTL.X + (tabW[i] - textW) * .5f, tTL.Y + (rowH - fsize) * .5f),
                    lc, locTabs[i]);

                DrawSearchHighlight(tabs[i], tTL, tBR, 4f);
                cx += tabW[i];
            }

            if (overflow) dl.PopClipRect();

            // Thin separator below the full row
            dl.AddLine(
                new Vector2(rowTL.X,         rowTL.Y + rowH),
                new Vector2(rowTL.X + avail, rowTL.Y + rowH),
                DA(Alpha(BorderCol, 70)), 1f);

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rowTL.X, rowTL.Y + rowH + 1f));
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 0f));

            _lastRowTL = rowTL;
            _lastRowBR = new Vector2(rowTL.X + avail, rowTL.Y + rowH);
            return changed;
        }

        // ───────────────────────────────────────────────────────────────────────────
        // MULTI-SELECT TAG COMBO  - dismissible tag chips in a combobox header
        //
        // Selected items appear as "Label -" chips inside the closed header.
        // Clicking the header opens a checkbox dropdown to add/remove items.
        // Clicking - on a chip deselects that item without opening the dropdown.
        //
        // bool[] overload:
        //   bool[] sel = { false, true, false, true };
        //   UI.TagCombo("Widgets##tc1", labels, sel);
        //
        // int bitmask overload:
        //   int flags = 0b1010;
        //   UI.TagCombo("Widgets##tc1", labels, ref flags);
        // ───────────────────────────────────────────────────────────────────────────

        private const float TagComboH    = 32f;
        private const float TagPadX      = 6f;
        private const float TagPadY      = 4f;
        private const float TagRound     = 4f;
        private const float TagGap       = 4f;
        private const float TagCloseSize = 10f;
        private static string _tagClearChangedFor;

        public static bool TagCombo(string label, string[] items, bool[] selected, float width = 0f,
            string hint = null, string[] itemHints = null, string placeholder = null,
            Action[] itemSettings = null, float itemPopupWidth = 220f,
            bool[] disabledItems = null, int maxVisibleItems = 0)
        {
            if (items == null || selected == null) return false;
            int count = Math.Min(items.Length, selected.Length);
            bool changed = false;
            Func<int, bool> disabled = disabledItems != null
                ? i => i < disabledItems.Length && disabledItems[i]
                : null;
            DoTagCombo(label, items, count, width,
                i => i < selected.Length && selected[i],
                (i, v) => { if (i < selected.Length) { selected[i] = v; changed = true; } },
                hint, itemHints, placeholder, itemSettings, itemPopupWidth, disabled, maxVisibleItems);
            return changed;
        }

        public static bool TagCombo(string label, string[] items, ref int flags, float width = 0f,
            string hint = null, string[] itemHints = null, string placeholder = null)
        {
            if (items == null) return false;
            int count = Math.Min(items.Length, 32);
            bool changed = false;
            int localFlags = flags;
            DoTagCombo(label, items, count, width,
                i => (localFlags & (1 << i)) != 0,
                (i, v) =>
                {
                    if (v) localFlags |=  (1 << i);
                    else   localFlags &= ~(1 << i);
                    changed = true;
                },
                hint, itemHints, placeholder);
            flags = localFlags;
            return changed;
        }

        private static void DoItemPopup(string id, float w, Action content)
        {
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(w, 40f), new Vector2(w, 600f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    new Vector2(10f, 10f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,    6f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.PopoverBg, 248)));
            if (ImGuiNET.ImGui.BeginPopup(id,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar))
            {
                var  pdl = ImGuiNET.ImGui.GetWindowDrawList();
                var  pwp = ImGuiNET.ImGui.GetWindowPos();
                var  pws = ImGuiNET.ImGui.GetWindowSize();
                pdl.AddRect(pwp, new Vector2(pwp.X + pws.X, pwp.Y + pws.Y),
                    Alpha(BorderCol, 160), 6f, ImDrawFlags.None, 1f);
                pdl.AddRectFilled(
                    new Vector2(pwp.X + 2f, pwp.Y + 1f),
                    new Vector2(pwp.X + pws.X - 2f, pwp.Y + 3f),
                    Alpha(Accent, 200), 4f, ImDrawFlags.RoundCornersTop);
                float savedAvailW = _groupAvailW;
                _groupAvailW = 0f;
                content();
                _groupAvailW = savedAvailW;
                ImGuiNET.ImGui.EndPopup();
            }
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopStyleVar(4);
        }

        private static void DoTagCombo(string label, string[] items, int count, float width,
            Func<int, bool> getSelected, Action<int, bool> setSelected,
            string hint = null, string[] itemHints = null, string placeholder = null,
            Action[] itemSettings = null, float itemPopupWidth = 220f,
            Func<int, bool> isItemDisabled = null, int maxVisibleItems = 0)
        {
            // Apply deferred clear confirmed via ConfirmModal in a previous frame
            if (_tagClearChangedFor == label)
            {
                _tagClearChangedFor = null;
                for (int i = 0; i < count; i++) setSelected(i, false);
            }

            // Localize item text once up front - every measurement, hit-test, and draw call
            // below must use the same (translated) string, or chip widths and ×/gear hit
            // zones will be sized against English text while rendering a different-length
            // translated label.
            var locItems = new string[count];
            for (int li = 0; li < count; li++) locItems[li] = LocManager.Get(items[li]);

            var   font  = ImGuiStyle.FontNormal;
            var   fontS = ImGuiStyle.FontSmall;
            float fsize  = font.FontSize  > 0f ? font.FontSize  : ImGuiNET.ImGui.GetTextLineHeight();
            float fsizeS = fontS.FontSize > 0f ? fontS.FontSize : fsize * 0.85f;
            float avail  = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            float w      = MathF.Max(TagComboH + 1f, width > 0f ? MathF.Min(width, avail) : avail);

            string cleanLabel = Loc(label);
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                Vector2 lpos = ImGuiNET.ImGui.GetCursorScreenPos();
                DL.AddText(font, fsize, lpos, DA(TxtNorm), TruncateText(font, fsize, cleanLabel, w));
                ImGuiNET.ImGui.Dummy(new Vector2(w, fsize + 4f));
            }

            // ───Measure how many tag rows we need for the header height ─────────
            float tagH   = fsize + TagPadY * 2f;
            float innerW = MathF.Max(TagCloseSize + TagPadX * 2f + 1f, w - TagComboH - TagPadX * 2f);
            float rowW   = 0f;
            int   rows   = 1;
            for (int i = 0; i < count; i++)
            {
                if (!getSelected(i)) continue;
                float gearExt0 = (itemSettings != null && i < itemSettings.Length && itemSettings[i] != null) ? 14f : 0f;
                // Clamp chip width so it never exceeds innerW (avoids single-chip infinite loops)
                float fw = MathF.Min(MeasureText(font, fsize, locItems[i]).X + TagCloseSize + TagPadX * 2f + 6f + gearExt0, innerW);
                // Mirror the renderer's wrap check exactly: gap only between tags, not after the last one
                float needed = rowW > 0f ? rowW + TagGap + fw : fw;
                if (needed > innerW && rowW > 0f) { rows++; rowW = 0f; needed = fw; }
                rowW = needed;
            }
            float headerH = MathF.Max(TagComboH, rows * tagH + MathF.Max(rows - 1, 0) * TagGap + TagPadY * 2f);

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 br = new(tl.X + w, tl.Y + headerH);

            // Check whether the mouse-down position (from last frame) landed on a tag -.
            // GetMouseClickedPos returns where the button went down, so it's stable across frames.
            // We use this to suppress BeginCombo on the frame the - is clicked.
            // Pre-detect x hit before BeginCombo so we can close the popup immediately
            // inside the combo block if a tag x was what the user actually clicked.
            // Detect x hit via IsMouseDown so BeginCombo stays disabled for the entire
            // press duration - prevents any popup flash. IsMouseClicked marks the removal target.
            bool mouseOnX    = false;
            int  suppressIdx = -1;
            int  gearIdx     = -1;
            if (!IsDisabled)
            {
                bool mDown = ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left);
                bool mClk  = ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left);
                if (mDown || mClk)
                {
                    Vector2 cp = mClk
                        ? ImGuiNET.ImGui.GetIO().MouseClickedPos[(int)ImGuiMouseButton.Left]
                        : ImGuiNET.ImGui.GetIO().MousePos;
                    float pX = tl.X + TagPadX;
                    float pY = rows > 1 ? tl.Y + TagPadY : tl.Y + (headerH - tagH) * .5f;
                    for (int i = 0; i < count; i++)
                    {
                        if (!getSelected(i)) continue;
                        bool  iHasCog2 = itemSettings != null && i < itemSettings.Length && itemSettings[i] != null;
                        float gearExt2 = iHasCog2 ? 14f : 0f;
                        float tw2    = MeasureText(font, fsize, locItems[i]).X;
                        float fullW2 = MathF.Min(tw2 + TagCloseSize + TagPadX * 2f + 6f + gearExt2, innerW);
                        if (pX + fullW2 > tl.X + innerW + TagPadX && pX > tl.X + TagPadX)
                        { pX = tl.X + TagPadX; pY += tagH + TagGap; }
                        float bRx = pX + fullW2;
                        float bRy = pY + tagH;
                        float cxP = bRx - TagCloseSize - TagPadX * 0.5f;
                        // Gear first - its zone overlaps the × hit area; priority ensures gear
                        // clicks open settings rather than accidentally removing the tag.
                        if (iHasCog2)
                        {
                            float gCx2 = cxP - TagCloseSize - 3f;
                            bool gHit = cp.X >= gCx2 - 8f && cp.X < gCx2 + 8f && cp.Y >= pY && cp.Y <= bRy;
                            if (gHit) { mouseOnX = true; if (mClk) gearIdx = i; break; }
                        }
                        // × hit
                        bool hit = cp.X >= cxP - TagCloseSize && cp.X <= bRx && cp.Y >= pY && cp.Y <= bRy;
                        if (hit) { mouseOnX = true; if (mClk) suppressIdx = i; break; }
                        pX += fullW2 + TagGap;
                    }
                }
            }

            // ── Use BeginCombo as the interactive driver (fixes hit-testing) ──
            ImGuiNET.ImGui.SetNextItemWidth(w);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg,              Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,       Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive,        Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,               Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button,               Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered,        Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonActive,         Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,                 Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg,              Alpha(ImGuiStyle.DropdownBg, 248));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,               Alpha(Accent, 120));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Alpha(InputBg, 200));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Alpha(BorderCol, 160));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Alpha(Accent, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Alpha(Accent, 220));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(8f, (headerH - fsize) * .5f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ComboRound);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, ComboRound);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(4f, 2f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);

            var dl = DL;

            // Opt-in exact-fit popup height: shows up to maxVisibleItems rows before
            // scrolling (same technique as Combo()'s maxVisibleItems), instead of
            // ImGuiComboFlags.HeightLargest letting the list grow to fit every item
            // unbounded - which for a long list (e.g. 25+ bones) pushes the popup well
            // past the screen with no scrollbar at all.
            if (maxVisibleItems > 0)
            {
                int   visibleCount = Math.Min(count, maxVisibleItems);
                float popupH       = visibleCount * ListItemH + Math.Max(0, visibleCount - 1) * 2f + 12f;
                ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(0f, popupH), new Vector2(float.MaxValue, popupH));
            }

            bool dropOpen = false;
            if (!IsDisabled)
            {
                if (mouseOnX) ImGuiNET.ImGui.BeginDisabled();
                dropOpen = ImGuiNET.ImGui.BeginCombo("##tc_" + label, "", ImGuiComboFlags.NoArrowButton | ImGuiComboFlags.HeightLargest);
                if (mouseOnX) ImGuiNET.ImGui.EndDisabled();
                if (dropOpen && suppressIdx >= 0)
                {
                    // Popup was already open from a prior frame; close it when x was hit.
                    ImGuiNET.ImGui.CloseCurrentPopup();
                    ImGuiNET.ImGui.EndCombo();
                    dropOpen = false;
                }
                else if (dropOpen)
                {
                    // Must use the popup's own draw list - parent dl is behind the popup window.
                    var popupDL = ImGuiNET.ImGui.GetWindowDrawList();
                    var clipMin = ImGuiNET.ImGui.GetWindowPos();
                    var clipMax = clipMin + ImGuiNET.ImGui.GetWindowSize();
                    popupDL.PushClipRect(clipMin, clipMax, true);

                    for (int i = 0; i < count; i++)
                    {
                        bool cur          = getSelected(i);
                        bool itemDisabled = isItemDisabled != null && isItemDisabled(i);
                        Vector2 rowTL = ImGuiNET.ImGui.GetCursorScreenPos();
                        float   rowW2 = ImGuiNET.ImGui.GetContentRegionAvail().X;

                        // Reserve space on right for a cog button when this item has settings.
                        bool  hasCog    = itemSettings != null && i < itemSettings.Length && itemSettings[i] != null;
                        float rowBtnW2  = rowW2 - (hasCog ? 20f : 0f);

                        ImGuiNET.ImGui.InvisibleButton("##tcrow_" + label + i, new Vector2(rowBtnW2, ListItemH));
                        bool rowHov   = ImGuiNET.ImGui.IsItemHovered();
                        bool rowClick = ImGuiNET.ImGui.IsItemClicked();
                        uint rowId    = ImGuiNET.ImGui.GetItemID();
                        float rth     = Anim(rowId, (rowHov && !itemDisabled) ? 1f : 0f, 14f);
                        if (rowClick && !itemDisabled) setSelected(i, !cur);

                        Vector2 rowBR2 = new(rowTL.X + rowW2, rowTL.Y + ListItemH);
                        if (rth > 0.01f)
                            popupDL.AddRectFilled(rowTL, rowBR2, Alpha(Accent, (byte)(40 * rth)), 4f);

                        float cbX2 = rowTL.X + rowBtnW2 - CbSize - 4f;
                        float cbY2 = rowTL.Y + (ListItemH - CbSize) * .5f;
                        Vector2 cbTL2 = new(cbX2, cbY2), cbBR2 = new(cbX2 + CbSize, cbY2 + CbSize);
                        float tv2 = Anim(rowId ^ 0xCBu, cur ? 1f : 0f, 18f);
                        if (itemDisabled)
                        {
                            popupDL.AddRectFilled(cbTL2, cbBR2, Alpha(Dark5, 130), CbRound);
                            popupDL.AddRect(cbTL2, cbBR2, Alpha(BorderCol, 70), CbRound, ImDrawFlags.None, 1.5f);
                        }
                        else
                        {
                            popupDL.AddRectFilled(cbTL2, cbBR2, Lerp(Lerp(Alpha(Dark5, 220), Alpha(Dark4, 240), rth), Lerp(Accent, AccentHover, rth), tv2), CbRound);
                            popupDL.AddRect(cbTL2, cbBR2, Lerp(Alpha(BorderCol, 160), Alpha(Accent, 220), MathF.Max(rth, tv2)), CbRound, ImDrawFlags.None, 1.5f);
                            if (tv2 > 0.05f)
                            {
                                float midX2 = cbX2 + CbSize * 0.35f, midY2 = cbY2 + CbSize * 0.65f;
                                uint  tick2 = Alpha(0xFFFFFFFF, (byte)(255 * MathF.Min(tv2 * 2f, 1f)));
                                popupDL.AddLine(new Vector2(cbX2 + 3.5f, cbY2 + CbSize * 0.52f), new Vector2(midX2, midY2), tick2, 1.8f);
                                popupDL.AddLine(new Vector2(midX2, midY2), new Vector2(cbX2 + CbSize - 3.5f, cbY2 + 4.5f), tick2, 1.8f);
                            }
                        }
                        float dropMaxTw = MathF.Max(0f, rowBtnW2 - CbSize - 16f);
                        popupDL.AddText(font, fsize,
                            new Vector2(rowTL.X + 8f, rowTL.Y + (ListItemH - fsize) * .5f),
                            itemDisabled
                                ? DA(Alpha(TxtDim, 110))
                                : DA(Lerp(TxtNorm, 0xFFFFFFFF, MathF.Max(rth, tv2 * 0.5f))),
                            TruncateText(font, fsize, locItems[i], dropMaxTw));

                        if (itemHints != null && i < itemHints.Length && itemHints[i] != null)
                        {
                            float hintMaxX = hasCog ? rowTL.X + rowW2 - 20f : rowTL.X + rowW2;
                            TooltipInPopup(LocManager.Get(itemHints[i]), rowTL, new Vector2(hintMaxX, rowTL.Y + ListItemH));
                        }

                        // ── Per-item cog button ───────────────────────────────────
                        if (hasCog)
                        {
                            bool itemSel = getSelected(i);
                            ImGuiNET.ImGui.SameLine(0, 0);
                            Vector2 cogTL2 = ImGuiNET.ImGui.GetCursorScreenPos();
                            Vector2 cogBR2 = new(cogTL2.X + 20f, cogTL2.Y + ListItemH);
                            ImGuiNET.ImGui.InvisibleButton("##tccog_" + label + i, new Vector2(20f, ListItemH));
                            bool cogHov2 = itemSel && ImGuiNET.ImGui.IsItemHovered();
                            bool cogClk2 = itemSel && ImGuiNET.ImGui.IsItemClicked();
                            if (cogClk2) ImGuiNET.ImGui.OpenPopup("##tcset_" + label + i);
                            {
                                var gearTex = IconCache.Get(IconCache.Icon.Settings);
                                float gsz   = fsize * 0.85f;
                                float gcx   = cogTL2.X + 10f, gcy = cogTL2.Y + ListItemH * 0.5f;
                                uint  gcol  = !itemSel
                                    ? Alpha(TxtDim, 55)
                                    : DA(cogHov2 ? Alpha(Accent, 220) : Alpha(TxtDim, 140));
                                popupDL.AddImage(gearTex,
                                    new Vector2(gcx - gsz * 0.5f, gcy - gsz * 0.5f),
                                    new Vector2(gcx + gsz * 0.5f, gcy + gsz * 0.5f),
                                    Vector2.Zero, Vector2.One, gcol);
                            }
                            if (!itemSel && ImGuiNET.ImGui.IsMouseHoveringRect(cogTL2, cogBR2, true))
                                TooltipInPopup($"Enable {locItems[i]} to configure these settings", cogTL2, cogBR2);
                            DoItemPopup("##tcset_" + label + i, itemPopupWidth, itemSettings[i]);
                        }
                    }

                    popupDL.PopClipRect();
                    ImGuiNET.ImGui.EndCombo();
                }
            }
            else
            {
                // Emit a no-op combo to keep the ImGui ID/hover state consistent.
                ImGuiNET.ImGui.BeginDisabled();
                ImGuiNET.ImGui.BeginCombo("##tc_" + label, "", ImGuiComboFlags.NoArrowButton);
                ImGuiNET.ImGui.EndDisabled();
            }

            bool  hov  = ImGuiNET.ImGui.IsItemHovered();
            bool  open = ImGuiNET.ImGui.IsPopupOpen("##tc_" + label);
            uint  cmbId = ImGuiNET.ImGui.GetItemID();
            float th    = Anim(cmbId ^ 0xE3u, hov  ? 1f : 0f, 12f);
            float to    = Anim(cmbId ^ 0xE1u, open ? 1f : 0f, 16f);

            ImGuiNET.ImGui.PopStyleVar(6);
            ImGuiNET.ImGui.PopStyleColor(14);

            // ── Header chrome ──────────────────────────────────────────────
            dl.AddRectFilled(tl, br,
                DA(Lerp(Lerp(Alpha(ImGuiStyle.WidgetBg, 200), Alpha(InputBg, 220), th),
                             Alpha(InputBgH, 240), to)),
                open ? ComboRound * 0.5f : ComboRound);
            dl.AddRect(tl, br,
                DA(Lerp(Lerp(Alpha(BorderCol, 120), Alpha(BorderCol, 200), th), Alpha(Accent, 200), to)),
                open ? ComboRound * 0.5f : ComboRound, ImDrawFlags.None, open ? 1.5f : 1f);

            // ── Placeholder - shown when nothing is selected ──────────────────
            bool anySelected = false;
            for (int i = 0; i < count; i++) if (getSelected(i)) { anySelected = true; break; }
            if (!anySelected && placeholder != null)
            {
                float phY = tl.Y + (headerH - fsize) * 0.5f;
                dl.AddText(font, fsize, new Vector2(tl.X + TagPadX, phY),
                    DA(Alpha(TxtDim, 110)), TruncateText(font, fsize, LocManager.Get(placeholder), innerW));
            }

            // ── Tags ──────────────────────────────────────────────────────────
            float curX = tl.X + TagPadX;
            float curY = tl.Y + (headerH - tagH) * .5f; // vertically centre single-row tags
            if (rows > 1) curY = tl.Y + TagPadY;        // top-align when wrapping

            Vector2 mp2 = ImGuiNET.ImGui.GetIO().MousePos;

            for (int i = 0; i < count; i++)
            {
                if (!getSelected(i)) continue;
                bool  iHasCog = itemSettings != null && i < itemSettings.Length && itemSettings[i] != null;
                float gearExt = iHasCog ? 14f : 0f;
                float chipOverhead = TagCloseSize + TagPadX * 2f + 6f + gearExt;
                float tw    = MeasureText(font, fsize, locItems[i]).X;
                float fullW = MathF.Min(tw + chipOverhead, innerW);

                if (curX + fullW > tl.X + innerW + TagPadX && curX > tl.X + TagPadX)
                { curX = tl.X + TagPadX; curY += tagH + TagGap; }

                // Truncate against what's actually left in this row, not the whole combo -
                // otherwise a chip sharing a crowded row with earlier chips (or a gear-bearing
                // neighbour) gets judged against the full combo width, which can make a SHORTER
                // label truncate while a longer one sitting alone on its own row does not.
                float rowRemaining  = MathF.Max(0f, tl.X + innerW + TagPadX - curX);
                float renderW       = MathF.Min(fullW, rowRemaining);
                // When nothing is actually being clamped, use tw directly rather than
                // renderW - chipOverhead - that add-then-subtract round trip through float
                // arithmetic can land a hair below tw's exact bit value, which flips
                // TruncateText's "does it fit" check and chops off a whole "..." worth of
                // characters for a sub-pixel, content-dependent rounding artifact.
                float maxChipTw     = renderW >= fullW ? tw : MathF.Max(0f, renderW - chipOverhead);
                string dispChipText = TruncateText(font, fsize, locItems[i], maxChipTw);

                Vector2 tagTL = new(curX, curY);
                Vector2 tagBR = new(curX + renderW, curY + tagH);

                float cxPos = tagBR.X - TagCloseSize - TagPadX * 0.5f;
                Vector2 xTL = new(cxPos - TagCloseSize, tagTL.Y);
                float gCx   = iHasCog ? cxPos - TagCloseSize - 3f : 0f;

                // Gear hover takes priority; × hover excludes the gear zone
                bool gHov   = iHasCog && mp2.X >= gCx - 8f && mp2.X < gCx + 8f && mp2.Y >= tagTL.Y && mp2.Y <= tagBR.Y;
                bool tagHov = mp2.X >= tagTL.X && mp2.X <= tagBR.X && mp2.Y >= tagTL.Y && mp2.Y <= tagBR.Y;
                bool xHov   = !gHov && mp2.X >= xTL.X && mp2.X <= tagBR.X && mp2.Y >= xTL.Y && mp2.Y <= tagBR.Y;
                uint tagId  = (uint)(label.GetHashCode() ^ (i * 2654435761));
                float txh   = Anim(tagId,          tagHov ? 1f : 0f, 16f);
                float txhX  = Anim(tagId ^ 0xF1u,  xHov   ? 1f : 0f, 16f);

                dl.AddRectFilled(tagTL, tagBR, DA(Alpha(Accent, (byte)(55 + 40 * txh))), TagRound);
                dl.AddRect(tagTL, tagBR, DA(Lerp(Alpha(Accent, 120), Alpha(Accent, 220), txh)), TagRound, ImDrawFlags.None, 1f);
                dl.AddText(font, fsize,
                    new Vector2(tagTL.X + TagPadX, tagTL.Y + (tagH - fsize) * .5f),
                    DA(TxtNorm), dispChipText);

                // Gear icon (gCx/gHov computed above for hover exclusivity)
                if (iHasCog)
                {
                    float gHovT = Anim(tagId ^ 0xF2u, gHov ? 1f : 0f, 16f);
                    uint  gCol  = DA(Lerp(Alpha(TxtNorm, 150), Alpha(Accent, 240), gHovT));
                    {
                        var gearTex = IconCache.Get(IconCache.Icon.Settings);
                        float gsz   = 8f;
                        float gcx   = gCx, gcy = tagTL.Y + tagH * 0.5f;
                        dl.AddImage(gearTex,
                            new Vector2(gcx - gsz * 0.5f, gcy - gsz * 0.5f),
                            new Vector2(gcx + gsz * 0.5f, gcy + gsz * 0.5f),
                            Vector2.Zero, Vector2.One, gCol);
                    }
                }

                float cyPos = tagTL.Y + tagH * .5f;
                float cs    = TagCloseSize * 0.28f;
                uint xCol   = DA(Lerp(Alpha(TxtNorm, 180), 0xFFFFFFFF, txhX));
                dl.AddLine(new Vector2(cxPos - cs, cyPos - cs), new Vector2(cxPos + cs, cyPos + cs), xCol, 1.5f);
                dl.AddLine(new Vector2(cxPos + cs, cyPos - cs), new Vector2(cxPos - cs, cyPos + cs), xCol, 1.5f);

                // Detect - click using the mouse-down position so it's stable
                curX += fullW + TagGap;
            }

            // Apply x removal (detected before BeginCombo to suppress dropdown opening)
            if (suppressIdx >= 0)
                setSelected(suppressIdx, false);

            // Header gear click → open settings popup
            if (gearIdx >= 0)
                ImGuiNET.ImGui.OpenPopup("##tchdr_" + label + gearIdx);
            if (itemSettings != null)
            {
                for (int i = 0; i < count; i++)
                {
                    if (i >= itemSettings.Length || itemSettings[i] == null) continue;
                    DoItemPopup("##tchdr_" + label + i, itemPopupWidth, itemSettings[i]);
                }
            }
            // Chevron
            DrawChevron(dl, br.X - 16f, tl.Y + headerH * .5f, 5f,
                MathF.PI * 0.5f + to * MathF.PI * 0.5f,
                DA(Lerp(Alpha(TxtDim, 160), Alpha(Accent, 220), MathF.Max(th, to))));

            // Advance cursor past the header
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(tl.X, br.Y));
            ImGuiNET.ImGui.Dummy(new Vector2(w, 0f));
            _lastRowTL = tl;
            _lastRowBR = br;
            if (hint != null && hov) Tooltip(hint);
            ShowDisabledReasonTooltip();

            // ── Right-click "Clear All" context menu ──────────────────────────
            if (!IsDisabled && BeginContextMenu("##tcctx_" + label, tl, br))
            {
                bool anySelectedCtx = false;
                for (int i = 0; i < count; i++) if (getSelected(i)) { anySelectedCtx = true; break; }

                if (MenuItem("Clear All", anySelectedCtx))
                    ConfirmModal.Open("Clear All", "Deselect all options in this control?",
                        () => _tagClearChangedFor = label,
                        confirmLabel: "Clear All");
                EndContextMenu();
            }
        }

        //───────────────────────────────────────────────────────────────────────────
        // TOGGLE WITH POPOVER  - toggle row with a cog button that opens a
        // small rounded popover anchored below the cog. Auto-closes on any
        // click outside. Uses ImGui's popup system for correct Z-ordering.
        //
        // Usage:
        //   UI.ToggleWithPopover("Wide Lean##wl", ref enabled, () =>
        //   {
        //       UI.SliderFloat(...);
        //   });
        // ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Draws a dim "Hotkeys" caption followed by one row per entry (label left,
        /// rebindable key badge right) - meant to sit at the top of any popover, before
        /// its main content. Must be called from inside an already-open ImGui popup/
        /// window (uses that window's current draw list and content-region width).
        /// idSalt only needs to be unique among sections rendered in the same popup this
        /// frame (e.g. the popover's own label) - it seeds each row's InvisibleButton id.
        /// </summary>
        public static bool PopoverHotkeySection(string idSalt,
            (string Label, HotkeyEntry Entry)[] hotkeys, bool dividerAfter = true)
        {
            if (hotkeys == null || hotkeys.Length == 0) return false;

            bool changed = false;
            var  pdl     = ImGuiNET.ImGui.GetWindowDrawList();
            float spw    = ImGuiNET.ImGui.GetContentRegionAvail().X;
            var   bfnt   = ImGuiStyle.FontSmall;
            float bfs    = bfnt.FontSize > 0f ? bfnt.FontSize : 10f;
            var   nfnt   = ImGuiStyle.FontNormal;
            float nfs    = nfnt.FontSize > 0f ? nfnt.FontSize : 13f;
            float sRowH  = nfs + 10f;

            // Dim "Hotkeys" caption
            Vector2 capP = ImGuiNET.ImGui.GetCursorScreenPos();
            pdl.AddText(nfnt, nfs * 0.82f, capP, Alpha(Accent, 150), LocManager.Get("Hotkeys"));
            ImGuiNET.ImGui.Dummy(new Vector2(spw, nfs * 0.82f + 4f));

            foreach (var (subLbl, subEntry) in hotkeys)
            {
                if (subEntry == null) continue;
                subEntry.DisplayLabel = subLbl;

                var   hkKeys = subEntry.Keys?.Where(k => k > 0).Distinct().ToList() ?? new List<int>();
                if (hkKeys.Count == 0 && subEntry.Key > 0) hkKeys.Add(subEntry.Key);
                string skLbl = hkKeys.Count > 0
                    ? "[" + string.Join(" / ", hkKeys.Select(KeyBadgeLabel)) + "]"
                    : "[-]";
                float  skTW  = MeasureText(bfnt, bfs, skLbl).X;
                const float hp = 3f, vp = 2f;

                Vector2 rTL  = ImGuiNET.ImGui.GetCursorScreenPos();
                float   bBRx = rTL.X + spw;
                float   bTLx = bBRx - skTW - hp * 2f;
                float   bY   = MathF.Floor(rTL.Y + (sRowH - bfs) * 0.5f) - vp;
                Vector2 sTL  = new(bTLx, bY);
                Vector2 sBR  = new(bBRx, bY + bfs + vp * 2f);

                ImGuiNET.ImGui.InvisibleButton("##sh_" + subLbl + idSalt, new Vector2(spw, sRowH));
                uint sRowId = ImGuiNET.ImGui.GetItemID();

                pdl.AddText(nfnt, nfs, new Vector2(rTL.X, rTL.Y + (sRowH - nfs) * 0.5f),
                    DA(TxtNorm), LocManager.Get(subLbl));

                changed |= DrawInlineHotkeyBadge(pdl, sRowId, subEntry,
                    sTL, sBR, bfnt, bfs, skLbl, skTW, subLbl, insidePopup: true);
            }

            if (dividerAfter)
            {
                Vector2 divP = ImGuiNET.ImGui.GetCursorScreenPos();
                pdl.AddLine(new Vector2(divP.X - 10f, divP.Y),
                            new Vector2(divP.X + spw + 10f, divP.Y),
                            Alpha(BorderCol, 60), 1f);
                ImGuiNET.ImGui.Dummy(new Vector2(spw, 5f));
            }

            return changed;
        }

        public static bool ToggleWithPopover(string label, ref bool value, Action popoverContent,
            Vector2 popoverSize = default, float width = 0f, bool risky = false, string hint = null,
            HotkeyEntry hotkey = null, (string Label, HotkeyEntry Entry)[] subHotkeys = null, string riskyWarning = null,
            string extraRiskyWarning = null)
        {
            string locRiskyWarning      = risky && riskyWarning != null ? Loc(riskyWarning) : null;
            string locExtraRiskyWarning = risky && extraRiskyWarning != null ? Loc(extraRiskyWarning) : null;
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowH  = MathF.Max(CbSize, fsize) + 4f;
            float avail = width > 0f ? width
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl  = ImGuiNET.ImGui.GetCursorScreenPos();

            // Layout: [CbMarginR][checkbox][6f][label][hotkey?][CogGap][CogW][CbMarginR]
            const float CogW = 22f, CogGap = 4f;
            float cbX0   = tl.X + CbMarginR;
            float lblX   = cbX0 + CbSize + 6f;
            float cogBRx = tl.X + avail - CbMarginR;
            float cogTLx = cogBRx - CogW;
            float cogY   = tl.Y + (rowH - CogW) * .5f;
            Vector2 cogTL = new(cogTLx, cogY);
            Vector2 cogBR = new(cogBRx, cogY + CogW);
            string popupId = "##pop_" + label;

            // 1. Main row (everything left of cog) - computed early so the badge
            // geometry below can clamp against it too.
            float mainW = cogTLx - CogGap - tl.X;

            // ── Pre-compute hotkey badge geometry ─────────────────────────────
            var     bfont       = ImGuiStyle.FontSmall;
            float   bfsize      = bfont.FontSize > 0f ? bfont.FontSize : 10f;
            string  keyLbl      = "";
            float   keyTW       = 0f;
            Vector2 bTL         = default, bBR = default;
            bool    preBadgeHov = false;
            if (hotkey != null)
            {
                hotkey.DisplayLabel = Strip(label);
                var hkKeys = hotkey.Keys != null
                    ? hotkey.Keys.Where(k => k > 0).Distinct().ToList()
                    : new List<int>();
                if (hkKeys.Count == 0 && hotkey.Key > 0) hkKeys.Add(hotkey.Key);
                keyLbl = hkKeys.Count > 0
                    ? "[" + string.Join(" / ", hkKeys.Select(KeyBadgeLabel)) + "]"
                    : "[-]";
                keyTW = MeasureText(bfont, bfsize, keyLbl).X;
                const float hitPadX = 3f, hitPadY = 2f;
                // Clamp against the same available width the label itself gets
                // truncated to (below) - a long label (e.g. "Dim Off-Level Loot")
                // would otherwise position the badge off of its own raw text width,
                // pushing it past the truncated label and behind/past the cog
                // instead of sitting right after the visible text.
                float availForLabel = MathF.Max(0f, mainW - (lblX - tl.X) - 4f);
                float lblW_pre = MathF.Min(MeasureText(font, fsize, Loc(label)).X, availForLabel);
                float bTLx     = lblX + lblW_pre + 6f;
                float bBRx     = bTLx + keyTW + hitPadX * 2f;
                // Never let the badge itself overlap the cog, regardless of label width.
                float maxBadgeRight = cogTLx - CogGap;
                if (bBRx > maxBadgeRight)
                {
                    bBRx = maxBadgeRight;
                    bTLx = bBRx - (keyTW + hitPadX * 2f);
                }
                float bY = MathF.Floor(tl.Y + (rowH - bfsize) * 0.5f) - hitPadY;
                bTL = new(bTLx, bY);
                bBR = new(bBRx, bY + bfsize + hitPadY * 2f);
                preBadgeHov = !IsDisabled && ImGuiNET.ImGui.IsMouseHoveringRect(bTL, bBR, true);
            }

            // ── Hit areas ─────────────────────────────────────────────────────
            ImGuiNET.ImGui.InvisibleButton(label, new Vector2(mainW, rowH));
            bool lClk = !IsDisabled && ImGuiNET.ImGui.IsItemClicked() && !preBadgeHov;
            bool lHov = !IsDisabled && ImGuiNET.ImGui.IsItemHovered() && !preBadgeHov;
            uint id   = ImGuiNET.ImGui.GetItemID();

            // 2. Cog
            ImGuiNET.ImGui.SetCursorScreenPos(cogTL);
            ImGuiNET.ImGui.InvisibleButton("##pcog_" + label, new Vector2(CogW, CogW));
            bool cogClk    = !IsDisabled && value && ImGuiNET.ImGui.IsItemClicked();
            bool cogHov    = !IsDisabled && value && ImGuiNET.ImGui.IsItemHovered();
            bool cogOffHov = !IsDisabled && !value && ImGuiNET.ImGui.IsMouseHoveringRect(cogTL, cogBR, true);
            uint cogId     = ImGuiNET.ImGui.GetItemID();

            // Advance cursor past the full row
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(tl.X, tl.Y + rowH));
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 0f));

            bool clicked = lClk;
            bool hovered = lHov;
            if (clicked) value = !value;

            // ── Search navigation ──────────────────────────────────────────────
            bool pendingOpen = SearchRegistry.TakeOpenPopover(Strip(label));
            if (pendingOpen && !value)
                SearchRegistry.SetHighlight(Strip(label));

            if (cogClk || (value && pendingOpen)) ImGuiNET.ImGui.OpenPopup(popupId);
            bool popOpen = ImGuiNET.ImGui.IsPopupOpen(popupId);

            // ── Animations ────────────────────────────────────────────────────
            float tv   = Anim(id,         value   ? 1f : 0f, 18f);
            float th   = Anim(id ^ 0xAAu, hovered ? 1f : 0f, 12f);
            float tco3 = Anim(cogId,         cogHov  ? 1f : 0f, 14f);
            float tca3 = Anim(cogId ^ 0xC3u, popOpen ? 1f : 0f, 16f);

            var dl = DL;

            // ── Hotkey badge ───────────────────────────────────────────────────
            bool badgeChanged = false;
            if (hotkey != null)
                badgeChanged = DrawInlineHotkeyBadge(dl, id, hotkey, bTL, bBR, bfont, bfsize, keyLbl, keyTW, Strip(label));

            // ── Checkbox (LEFT) ───────────────────────────────────────────────
            float cbY = tl.Y + (rowH - CbSize) * .5f;
            Vector2 cbTL = new(cbX0, cbY), cbBR = new(cbX0 + CbSize, cbY + CbSize);
            uint bgUnchecked = Lerp(Alpha(InputBg, 200), Alpha(InputBgH, 235), th);
            uint bgChecked   = Lerp(ControlFill, BrighterColor(ControlFill, 0x33), th);
            dl.AddRectFilled(cbTL, cbBR, DA(Lerp(bgUnchecked, bgChecked, tv)), CbRound);
            dl.AddRect(cbTL, cbBR, DA(Lerp(Alpha(BorderCol, 160), Alpha(ControlFill, 220), MathF.Max(th, tv))), CbRound, ImDrawFlags.None, 1.5f);
            if (tv > 0.05f)
            {
                float midX = cbX0 + CbSize * 0.35f, midY = cbY + CbSize * 0.65f;
                uint tick = DA(Alpha(0xFFFFFFFF, (byte)(255 * MathF.Min(tv * 2f, 1f))));
                dl.AddLine(new Vector2(cbX0 + 3.5f, cbY + CbSize * 0.52f), new Vector2(midX, midY), tick, 1.8f);
                dl.AddLine(new Vector2(midX, midY), new Vector2(cbX0 + CbSize - 3.5f, cbY + 4.5f), tick, 1.8f);
            }

            // ── Label text (truncated before cog) ────────────────────────────
            float twpMaxLblW = MathF.Max(0f, mainW - (lblX - tl.X) - 4f);
            float twpLblY = MathF.Round(tl.Y + (rowH - fsize) * .5f) + 1f; // +1 compensates for cap-height optical offset - matches Toggle()
            uint twpLCol = risky
                ? (locExtraRiskyWarning != null
                    ? DA(Lerp(ImGuiStyle.Danger, BrighterColor(ImGuiStyle.Danger, 0x33), th))
                    : DA(Lerp(ImGuiStyle.Col(0xFF, 0xAA, 0x22), ImGuiStyle.Col(0xFF, 0xCC, 0x55), th)))
                : DA(Lerp(TxtNorm, 0xFFFFFFFF, th));
            dl.AddText(font, fsize, new Vector2(lblX, twpLblY), twpLCol, TruncateText(font, fsize, Loc(label), twpMaxLblW));

            // ── Cog (RIGHT) - dims when toggle is off ─────────────────────────
            float prevAlpha = _disabledAlpha;
            if (!value) _disabledAlpha = MathF.Min(_disabledAlpha, 0.38f + 0.62f * tv);

            uint cogBg3 = DA(Lerp(Alpha(Dark4, (byte)(60 + (int)(60 * tco3))),
                                  Alpha(Accent, (byte)(60 + (int)(60 * tco3))), tca3));
            dl.AddRectFilled(cogTL, cogBR, cogBg3, 4f);
            dl.AddRect(cogTL, cogBR,
                DA(Lerp(Alpha(BorderCol, 80), Alpha(Accent, 180), MathF.Max(tco3, tca3))),
                4f, ImDrawFlags.None, 1f);
            {
                var gearTex = IconCache.Get(IconCache.Icon.Settings);
                float gsz   = CogW * 0.65f;
                float gcx   = cogTL.X + CogW * 0.5f, gcy = cogTL.Y + CogW * 0.5f;
                uint  gcol  = DA(Lerp(Alpha(TxtDim, 160), Alpha(TxtNorm, 255), MathF.Max(tco3, tca3)));
                dl.AddImage(gearTex,
                    new Vector2(gcx - gsz * 0.5f, gcy - gsz * 0.5f),
                    new Vector2(gcx + gsz * 0.5f, gcy + gsz * 0.5f),
                    Vector2.Zero, Vector2.One, gcol);
            }

            _disabledAlpha = prevAlpha;

            bool subChanged = false;

            // ── Popover ───────────────────────────────────────────────────
            // Anchor: below the cog button. Enforce a minimum width so controls
            // inside always have enough room to render without negative track widths.
            float popW = popoverSize.X > 0f ? popoverSize.X : 220f;
            float popH = popoverSize.Y > 0f ? popoverSize.Y : 0f;
            // Anchor popover below the cog, right-aligned to cog's right edge
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(cogBR.X - popW, cogBR.Y + 1f), ImGuiCond.Always);
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(popW, 40f), new Vector2(popW, 600f));
            if (popH > 0f)
                ImGuiNET.ImGui.SetNextWindowSize(new Vector2(popW, popH), ImGuiCond.Always);

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,   new Vector2(10f, 10f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,  6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,   6f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.PopoverBg, 248)));

            if (ImGuiNET.ImGui.BeginPopup(popupId,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar))
            {
                // Custom border drawn on top of the popup background
                var  pdl = ImGuiNET.ImGui.GetWindowDrawList();
                var  pwp = ImGuiNET.ImGui.GetWindowPos();
                var  pws = ImGuiNET.ImGui.GetWindowSize();
                // Border first - its rounded corners define the visual silhouette
                pdl.AddRect(pwp, new Vector2(pwp.X + pws.X, pwp.Y + pws.Y),
                    Alpha(BorderCol, 160), 6f, ImDrawFlags.None, 1f);
                // Accent strip - 2px X-inset, 4f top rounding (smaller than popup's 6f so
                // every pixel including anti-aliasing is guaranteed within the popup corner circle)
                pdl.AddRectFilled(
                    new Vector2(pwp.X + 2f, pwp.Y + 1f),
                    new Vector2(pwp.X + pws.X - 2f, pwp.Y + 3f),
                    Alpha(Accent, 200), 4f, ImDrawFlags.RoundCornersTop);

                // Reset group context so controls inside the popup measure against
                // GetContentRegionAvail rather than the outer group's width.
                float savedAvailW = _groupAvailW;
                _groupAvailW = 0f;

                // ── Sub-hotkeys section (above user content) ──────────────────────
                if (subHotkeys != null && subHotkeys.Length > 0)
                    subChanged |= PopoverHotkeySection(label, subHotkeys, dividerAfter: popoverContent != null);

                LocManager.PushPopover(Strip(label));
                popoverContent?.Invoke();
                LocManager.PopPopover();
                _groupAvailW = savedAvailW;
                ImGuiNET.ImGui.EndPopup();
            }

            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopStyleVar(4);

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
            // When redirected here because the feature is off, pulse only the cog -
            // not the whole row - so it's clear what the user needs to click.
            if (SearchRegistry.HighlightKey == Strip(label))
                DrawSearchHighlight(Strip(label), cogTL, cogBR, 4f);
            // Shrink the hint-hover region so external UI.Tooltip() calls don't fire
            // when hovering the badge or cog - each has its own tooltip already.
            _lastRowBR = hotkey != null
                ? new Vector2(bTL.X, tl.Y + rowH)
                : new Vector2(cogTLx - CogGap, tl.Y + rowH);
            if (hovered && (hint != null || locRiskyWarning != null || locExtraRiskyWarning != null))
                TooltipWithRiskyWarning(hint, locRiskyWarning, locExtraRiskyWarning);
            if (IsDisabled) _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
            ShowDisabledReasonTooltip();
            if (cogOffHov)
            {
                _lastRowTL = cogTL;
                _lastRowBR = cogBR;
                string cogMsg = $"Enable {Loc(label)} to configure these settings";
                float  ww    = SafeTooltipWrapWidth();
                Tooltip(() =>
                {
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, ToV4(Alpha(TxtDim, 230)));
                    ImGuiNET.ImGui.PushTextWrapPos(ww);
                    ImGuiNET.ImGui.TextUnformatted(cogMsg);
                    ImGuiNET.ImGui.PopTextWrapPos();
                    ImGuiNET.ImGui.PopStyleColor();
                }, ImGuiNET.ImGui.CalcTextSize(cogMsg, false, ww) + new Vector2(20f, 16f));
            }
            return clicked || badgeChanged || subChanged;
        }

        //───────────────────────────────────────────────────────────────────────────
        // TOGGLE+COMBO  - checkbox on the LEFT, inline material/option combo
        // on the RIGHT, all in one row. Combo dims automatically when the
        // toggle is off. Auto-detects group context like all other controls.
        //
        //  UI.ToggleCombo("Clothing Chams##cc", ref enabled, materials, ref sel);
        // ───────────────────────────────────────────────────────────────────────────

        public static bool ToggleCombo(string label, ref bool enabled,
            string[] items, ref int selected, float comboWidth = 150f, float width = 0f, string hint = null)
        {
            if (items == null || items.Length == 0) return false;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowH  = ComboH;  // row height matches combo so both elements align
            float avail = width > 0f ? width
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;

            Vector2 tl    = ImGuiNET.ImGui.GetCursorScreenPos();
            float   comboW = MathF.Min(comboWidth, avail * 0.6f);
            float   togW   = avail - comboW;

            // ── Toggle hit area (left portion, excludes combo zone) ───────────
            ImGuiNET.ImGui.InvisibleButton(label, new Vector2(togW, rowH));
            bool togClicked = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            bool togHovered = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            uint togId      = ImGuiNET.ImGui.GetItemID();
            if (togClicked) enabled = !enabled;

            float tv = Anim(togId,         enabled    ? 1f : 0f, 18f);
            float th = Anim(togId ^ 0xAAu, togHovered ? 1f : 0f, 12f);

            var dl = DL;

            // Checkbox - left side, vertically centred in the row
            const float CbL = 2f, CbGap = 6f;
            float cbX = tl.X + CbL;
            float cbY = tl.Y + (rowH - CbSize) * .5f;
            Vector2 cbTL = new(cbX, cbY), cbBR = new(cbX + CbSize, cbY + CbSize);

            uint bgUnchk = Lerp(Alpha(Dark5, 220), Alpha(Dark4, 240), th);
            uint bgChk   = Lerp(Accent, AccentHover, th);
            dl.AddRectFilled(cbTL, cbBR, DA(Lerp(bgUnchk, bgChk, tv)), CbRound);
            dl.AddRect(cbTL, cbBR,
                DA(Lerp(Alpha(BorderCol, 160), Alpha(Accent, 220), MathF.Max(th, tv))),
                CbRound, ImDrawFlags.None, 1.5f);

            if (tv > 0.05f)
            {
                float pad  = 3.5f;
                float midX = cbX + CbSize * 0.35f, midY = cbY + CbSize * 0.65f;
                uint  tick = DA(Alpha(0xFFFFFFFF, (byte)(255 * MathF.Min(tv * 2f, 1f))));
                dl.AddLine(new Vector2(cbX + pad, cbY + CbSize * 0.52f), new Vector2(midX, midY), tick, 1.8f);
                dl.AddLine(new Vector2(midX, midY), new Vector2(cbX + CbSize - pad, cbY + pad + 1f), tick, 1.8f);
            }

            // Label - right of checkbox, truncated before the combo zone
            float tcMaxLblW = MathF.Max(0f, togW - CbL - CbSize - CbGap - 4f);
            uint lCol = DA(Lerp(Lerp(TxtDim, TxtNorm, tv), 0xFFFFFFFF, th));
            dl.AddText(font, fsize,
                new Vector2(cbX + CbSize + CbGap, tl.Y + (rowH - fsize) * .5f),
                lCol, TruncateText(font, fsize, Loc(label), tcMaxLblW));

            // ── Combo (right portion) - dims when toggle is off ───────────────
            bool changed = togClicked;
            bool needDisable = !enabled && !IsDisabled;
            if (needDisable) BeginDisabled();

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(tl.X + togW, tl.Y));
            if (Combo("##tcmb_" + label, ref selected, items, comboW)) changed = true;

            if (needDisable) EndDisabled();

            // Advance cursor past the full row
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(tl.X, tl.Y + rowH));
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 0f));

            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
            DrawSearchHighlight(Strip(label), _lastRowTL, _lastRowBR);
            if (hint != null && togHovered) Tooltip(hint);
            if (needDisable)
            {
                // Restrict hover region to combo portion only - the toggle half is what you
                // click to enable it, so showing the reason there would be confusing.
                var savedTL = _lastRowTL;
                var savedBR = _lastRowBR;
                _lastRowTL = new Vector2(tl.X + togW, tl.Y);
                _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
                ShowDisabledReasonTooltip($"Enable {Strip(label)} above to configure");
                _lastRowTL = savedTL;
                _lastRowBR = savedBR;
            }
            else
            {
                ShowDisabledReasonTooltip();
            }
            return changed;
        }

        //───────────────────────────────────────────────────────────────────────────
        // COLOR EDIT  - themed swatch row that opens a custom HSV picker popup.
        //
        // Usage:
        //   UI.ColorEdit("NVG Tint##nvgt", ref _myColor);
        //
        // The swatch shows the current color; clicking it (or the label) opens
        // a popup with a 2D saturation/value square, a hue bar, and a hex input.
        // Returns true whenever the color changes.
        // ───────────────────────────────────────────────────────────────────────────

        private const float CeSwatchW = 40f;    // row swatch - wider landscape rectangle
        private const float CePickW   = 184f;
        private const float CePickH   = 150f;   // SV square height
        private const float CeHueBarH = 12f;
        private const float CeRowH    = 22f;    // hex-input / channel-input row height
        private const float CeGap     = 6f;

        // Per-popup HSVA working state (X=H, Y=S, Z=V, W=A). Preserves hue when S/V-0.
        private static readonly Dictionary<string, Vector4> _ceHsva = new();
        // Per-popup original color captured on open so users can revert.
        private static readonly Dictionary<string, Vector4> _cePrev = new();
        // Last-frame size cache used for off-screen clamping of positioned popups.
        private static readonly Dictionary<string, Vector2> _popupSizes = new();
        // Cross-picker color clipboard for right-click Copy/Paste Color.
        private static Vector4? _ceClipboard;

        // Vector3 overload - wraps Vector4, ignores alpha (alpha stays 1).
        public static bool ColorEdit(string label, ref Vector3 rgb, float width = 0f, string hint = null)
        {
            var rgba = new Vector4(rgb.X, rgb.Y, rgb.Z, 1f);
            bool ch  = ColorEdit(label, ref rgba, width, hint);
            if (ch) rgb = new Vector3(rgba.X, rgba.Y, rgba.Z);
            return ch;
        }

        // Vector4 overload - full RGBA support including alpha slider.
        public static bool ColorEdit(string label, ref Vector4 rgba, float width = 0f, string hint = null)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowH  = MathF.Max(CbSize, fsize) + 4f;
            float avail = width > 0f        ? width
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();

            // Swatch on LEFT, label immediately right - mirrors Toggle layout.
            float swY = MathF.Floor(tl.Y + (rowH - CbSize) * .5f);
            float swX = tl.X + CbMarginR;
            Vector2 swTL = new(swX, swY);
            Vector2 swBR = new(swX + CeSwatchW, swY + CbSize);

            // Incorporate render position so controls with identical labels (e.g. "Border"
            // used across multiple groups all mapping to BorderColor) get independent popup
            // state rather than colliding on the same ImGui popup ID.
            string popId   = $"##ce_{label}_{(int)tl.X}_{(int)tl.Y}";
            bool   popOpen = ImGuiNET.ImGui.IsPopupOpen(popId);

            // Single hit-target spanning the full row.
            ImGuiNET.ImGui.InvisibleButton(label + "##cesw", new Vector2(avail, rowH));
            bool swClk = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            bool swHov = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            uint swId  = ImGuiNET.ImGui.GetItemID();

            if (swClk && !popOpen)
                ImGuiNET.ImGui.OpenPopup(popId);

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(tl.X, tl.Y + rowH));
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 0f));

            float th = Anim(swId, swHov || popOpen ? 1f : 0f, 12f);
            var   dl = DL;

            // Swatch: solid bg (no DA - keeps swatch visible when disabled),
            // then color blended on top; dim overlay substitutes for the DA effect.
            dl.AddRectFilled(swTL, swBR, InputBgH, CbRound);
            dl.AddRectFilled(swTL, swBR, CeRgbaToImCol(rgba), CbRound);
            if (IsDisabled)
                dl.AddRectFilled(swTL, swBR, Alpha(0xFF000000, 140), CbRound);
            dl.AddRect(swTL, swBR,
                DA(Lerp(Alpha(ImGuiStyle.BorderHover, 200), Alpha(Accent, 220), th)),
                CbRound, ImDrawFlags.None, 1.5f);

            // Label immediately right of swatch (truncated to row width).
            float lblX = swX + CeSwatchW + 6f;
            float lblY = MathF.Round(swY + (CbSize - fsize) * .5f) + 1f;
            float ceMaxLblW = MathF.Max(0f, tl.X + avail - lblX - 4f);
            dl.AddText(font, fsize,
                new Vector2(lblX, lblY),
                DA(Lerp(TxtNorm, 0xFFFFFFFF, th)), TruncateText(font, fsize, Loc(label), ceMaxLblW));

            // Right-click → Copy / Paste Color context menu.
            // Reuses popId's position-based uniqueness (see comment above) - otherwise
            // swatches sharing a label (e.g. the many "Border"/"Background" rows across
            // this tab) would collide on the same ImGui popup ID and each contribute its
            // own Copy/Paste entries into one shared popup.
            bool changed = false;
            Vector2 rowBR = new(tl.X + avail, tl.Y + rowH);
            if (BeginContextMenu("##ce_ctx_" + popId, tl, rowBR))
            {
                if (MenuItem("Copy Color"))
                {
                    _ceClipboard = rgba;
                    ImGuiNET.ImGui.CloseCurrentPopup();
                }
                if (MenuItem("Paste Color", enabled: _ceClipboard.HasValue))
                {
                    rgba = _ceClipboard!.Value;
                    // Sync the picker's internal HSV state so it reflects the pasted color immediately.
                    var pr3 = new Vector3(rgba.X, rgba.Y, rgba.Z);
                    CeRgbToHsv(pr3, out float ph, out float ps, out float pv);
                    _ceHsva[popId] = new Vector4(ph, ps, pv, rgba.W);
                    changed = true;
                    ImGuiNET.ImGui.CloseCurrentPopup();
                }
                EndContextMenu();
            }

            float popW = CePickW + 20f;
            var   disp = ImGuiNET.ImGui.GetIO().DisplaySize;
            _popupSizes.TryGetValue(popId, out Vector2 ceLastSz);
            float ceEstH = ceLastSz.Y > 0f ? ceLastSz.Y : 300f;
            float belowY = swBR.Y + 4f;
            float aboveY = swTL.Y - ceEstH - 4f;
            float ceY    = belowY + ceEstH <= disp.Y ? belowY      // fits below display
                         : aboveY >= 0f              ? aboveY      // fits above display
                         : MathF.Max(0f, disp.Y - ceEstH);         // clamp to bottom of display
            Vector2 cePos = new(
                MathF.Max(0f, MathF.Min(swTL.X, disp.X - popW)),
                ceY);
            ImGuiNET.ImGui.SetNextWindowPos(cePos, ImGuiCond.Always);
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(popW, 40f), new Vector2(popW, 600f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,   new Vector2(10f, 10f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,   6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.ColorPickerBg, 252)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(BorderCol, 180)));

            if (ImGuiNET.ImGui.BeginPopup(popId,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar))
            {
                if (ImGuiNET.ImGui.IsWindowAppearing() || !_ceHsva.ContainsKey(popId))
                {
                    CeRgbToHsv(new Vector3(rgba.X, rgba.Y, rgba.Z), out float h0, out float s0, out float v0);
                    _ceHsva[popId] = new Vector4(h0, s0, v0, rgba.W);
                    _cePrev[popId] = rgba;
                }

                var   hsva = _ceHsva[popId];
                float h = hsva.X, s = hsva.Y, v = hsva.Z, a = hsva.W;
                _cePrev.TryGetValue(popId, out Vector4 prevRgba);

                changed |= CeDrawPopup(popId, ref h, ref s, ref v, ref a, ref prevRgba);
                if (changed)
                {
                    _ceHsva[popId] = new Vector4(h, s, v, a);
                    var rgb3 = CeHsvToRgb(h, s, v);
                    rgba = new Vector4(rgb3.X, rgb3.Y, rgb3.Z, a);
                }
                _popupSizes[popId] = ImGuiNET.ImGui.GetWindowSize();
                ImGuiNET.ImGui.EndPopup();
            }

            ImGuiNET.ImGui.PopStyleColor(2);
            ImGuiNET.ImGui.PopStyleVar(3);
            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, tl.Y + rowH);
            DrawSearchHighlight(Strip(label), _lastRowTL, _lastRowBR);
            if (hint != null && swHov) Tooltip(hint);
            ShowDisabledReasonTooltip();
            return changed;
        }

        private static bool CeStyledInput(string id, ref string value, uint maxLen,
            ImGuiInputTextFlags flags, float x, float y, float w, float h)
        {
            var   dl    = ImGuiNET.ImGui.GetWindowDrawList();
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            Vector2 tl  = new(x, y);
            Vector2 br  = new(x + w, y + h);

            uint  preId = ImGuiNET.ImGui.GetID(id);
            float tf0   = _anim.TryGetValue(preId ^ 0xF0u, out var _tf0) ? _tf0 : 0f;
            float th0   = _anim.TryGetValue(preId ^ 0xF3u, out var _th0) ? _th0 : 0f;

            dl.AddRectFilled(tl, br,
                DA(Lerp(Alpha(ImGuiStyle.WidgetBg, 200), Alpha(InputBg, 220), th0)),
                InputRound);

            ImGuiNET.ImGui.SetCursorScreenPos(tl);
            ImGuiNET.ImGui.SetNextItemWidth(w);
            ImGuiNET.ImGui.PushFont(font);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg,        Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,         Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,           ToV4(DA(TxtNorm)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(4f, (h - fsize) * .5f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, InputRound);
            bool changed = ImGuiNET.ImGui.InputText(id, ref value, maxLen, flags);
            ImGuiNET.ImGui.PopStyleVar(2);
            ImGuiNET.ImGui.PopStyleColor(5);
            ImGuiNET.ImGui.PopFont();

            bool  focused = ImGuiNET.ImGui.IsItemFocused();
            bool  hovered = ImGuiNET.ImGui.IsItemHovered();
            uint  inpId   = ImGuiNET.ImGui.GetItemID();
            float tf      = Anim(inpId ^ 0xF0u, focused ? 1f : 0f, 14f);
            float th      = Anim(inpId ^ 0xF3u, hovered ? 1f : 0f, 12f);

            if (tf > 0.01f)
                dl.AddRect(new Vector2(tl.X - 1f, tl.Y - 1f), new Vector2(br.X + 1f, br.Y + 1f),
                    Alpha(Accent, (byte)(35 * tf)), InputRound + 1f, ImDrawFlags.None, 1f);
            dl.AddRect(tl, br,
                DA(Lerp(Lerp(Alpha(BorderCol, 120), Alpha(BorderCol, 200), th), Alpha(Accent, 200), tf)),
                InputRound, ImDrawFlags.None, focused ? 1.5f : 1f);

            // Right-click context menu
            if (BeginContextMenu(id + "##cectx", tl, br))
            {
                bool  hasTxt = !string.IsNullOrEmpty(value);
                string clip;
                try { clip = ImGuiNET.ImGui.GetClipboardText() ?? ""; } catch { clip = ""; }
                bool hasClip = !string.IsNullOrEmpty(clip);

                if (MenuItem("Cut",   hasTxt)) { ImGuiNET.ImGui.SetClipboardText(value); value = ""; changed = true; }
                if (MenuItem("Copy",  hasTxt))   ImGuiNET.ImGui.SetClipboardText(value);
                MenuSeparator();
                if (MenuItem("Paste", hasClip)) { value = clip.Trim(); changed = true; }
                EndContextMenu();
            }

            return changed;
        }

        private static bool CeDrawPopup(string key, ref float h, ref float s, ref float v, ref float a, ref Vector4 prevRgba)
        {
            bool changed = false;
            var  dl      = ImGuiNET.ImGui.GetWindowDrawList();
            var  io      = ImGuiNET.ImGui.GetIO();

            // ── Saturation / Value square ─────────────────────────────────────
            Vector2 svTL = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 svBR = new(svTL.X + CePickW, svTL.Y + CePickH);

            ImGuiNET.ImGui.InvisibleButton(key + "##sv", new Vector2(CePickW, CePickH));
            if (ImGuiNET.ImGui.IsItemActive())
            {
                s = Math.Clamp((io.MousePos.X - svTL.X) / CePickW, 0f, 1f);
                v = 1f - Math.Clamp((io.MousePos.Y - svTL.Y) / CePickH, 0f, 1f);
                changed = true;
            }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(svTL.X, svBR.Y + CeGap));

            dl.AddRectFilled(svTL, svBR, CeHsvToImCol(h, 1f, 1f));
            dl.AddRectFilledMultiColor(svTL, svBR, 0xFFFFFFFF, 0x00FFFFFF, 0x00FFFFFF, 0xFFFFFFFF);
            dl.AddRectFilledMultiColor(svTL, svBR, 0x00000000, 0x00000000, 0xFF000000, 0xFF000000);
            dl.AddRect(svTL, svBR, Alpha(BorderCol, 40));

            float cx = svTL.X + s * CePickW;
            float cy = svTL.Y + (1f - v) * CePickH;
            dl.AddCircleFilled(new Vector2(cx, cy), 7f,  0x40000000);
            dl.AddCircleFilled(new Vector2(cx, cy), 5f,  CeHsvToImCol(h, s, v));
            dl.AddCircle(new Vector2(cx, cy),       5f,  0xFFFFFFFF, 16, 1.5f);

            // ── Hue bar ───────────────────────────────────────────────────────
            Vector2 hueTL = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 hueBR = new(hueTL.X + CePickW, hueTL.Y + CeHueBarH);

            ImGuiNET.ImGui.InvisibleButton(key + "##hue", new Vector2(CePickW, CeHueBarH));
            if (ImGuiNET.ImGui.IsItemActive())
            {
                h = Math.Clamp((io.MousePos.X - hueTL.X) / CePickW, 0f, 1f - 1e-4f);
                changed = true;
            }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(hueTL.X, hueBR.Y + CeGap));

            float seg = CePickW / 6f;
            for (int i = 0; i < 6; i++)
            {
                var segTL = new Vector2(hueTL.X + i * seg,       hueTL.Y);
                var segBR = new Vector2(hueTL.X + (i + 1) * seg, hueBR.Y);
                dl.AddRectFilledMultiColor(segTL, segBR,
                    CeHsvToImCol(i / 6f,       1f, 1f),
                    CeHsvToImCol((i + 1) / 6f, 1f, 1f),
                    CeHsvToImCol((i + 1) / 6f, 1f, 1f),
                    CeHsvToImCol(i / 6f,       1f, 1f));
            }
            dl.AddRect(hueTL, hueBR, Alpha(BorderCol, 40));

            float hcX = Math.Clamp(hueTL.X + h * CePickW, hueTL.X + 2.5f, hueBR.X - 2.5f);
            dl.AddRectFilled(new Vector2(hcX - 2.5f, hueTL.Y - 2f), new Vector2(hcX + 2.5f, hueBR.Y + 2f), 0xFFFFFFFF, 2f);
            dl.AddRect(      new Vector2(hcX - 3.5f, hueTL.Y - 3f), new Vector2(hcX + 3.5f, hueBR.Y + 3f), 0x50000000, 2f);

            // ── Alpha bar ─────────────────────────────────────────────────────
            Vector2 alphaTL = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 alphaBR = new(alphaTL.X + CePickW, alphaTL.Y + CeHueBarH);

            ImGuiNET.ImGui.InvisibleButton(key + "##alpha", new Vector2(CePickW, CeHueBarH));
            if (ImGuiNET.ImGui.IsItemActive())
            {
                a = Math.Clamp((io.MousePos.X - alphaTL.X) / CePickW, 0f, 1f);
                changed = true;
            }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(alphaTL.X, alphaBR.Y + CeGap));

            // Solid dark bg; gradient on top runs transparent - opaque current color
            dl.AddRectFilled(alphaTL, alphaBR, InputBgH, 2f);
            // Gradient: transparent - current color
            uint colOpaque = CeHsvToImCol(h, s, v);
            uint colTransp = Alpha(colOpaque, 0);
            dl.AddRectFilledMultiColor(alphaTL, alphaBR, colTransp, colOpaque, colOpaque, colTransp);
            dl.AddRect(alphaTL, alphaBR, Alpha(BorderCol, 40));

            float acX = Math.Clamp(alphaTL.X + a * CePickW, alphaTL.X + 2.5f, alphaBR.X - 2.5f);
            dl.AddRectFilled(new Vector2(acX - 2.5f, alphaTL.Y - 2f), new Vector2(acX + 2.5f, alphaBR.Y + 2f), 0xFFFFFFFF, 2f);
            dl.AddRect(      new Vector2(acX - 3.5f, alphaTL.Y - 3f), new Vector2(acX + 3.5f, alphaBR.Y + 3f), 0x50000000, 2f);

            // ── RGBA channel inputs ───────────────────────────────────────────
            Vector2 chTL = ImGuiNET.ImGui.GetCursorScreenPos();
            float   chW  = (CePickW - 9f) / 4f;   // 4 fields with 3-3px gaps

            Vector3 curRgbCh = CeHsvToRgb(h, s, v);
            int[] chVals = {
                (int)Math.Clamp(curRgbCh.X * 255f, 0f, 255f),
                (int)Math.Clamp(curRgbCh.Y * 255f, 0f, 255f),
                (int)Math.Clamp(curRgbCh.Z * 255f, 0f, 255f),
                (int)Math.Clamp(a           * 255f, 0f, 255f),
            };
            uint[] chDots = {
                ImGuiStyle.Col(0xFF, 0x55, 0x55),
                ImGuiStyle.Col(0x55, 0xCC, 0x55),
                ImGuiStyle.Col(0x55, 0x88, 0xFF),
                ImGuiStyle.Col(0xAA, 0xAA, 0xAA),
            };

            for (int ci = 0; ci < 4; ci++)
            {
                float fx  = chTL.X + ci * (chW + 3f);
                float dcy = chTL.Y + CeRowH * .5f;
                dl.AddCircleFilled(new Vector2(fx + 5f, dcy), 3f, chDots[ci]);

                string buf = chVals[ci].ToString();
                if (CeStyledInput("##ch" + ci + key, ref buf, 4,
                    ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll |
                    ImGuiInputTextFlags.EnterReturnsTrue,
                    fx + 11f, chTL.Y, chW - 11f, CeRowH))
                {
                    if (int.TryParse(buf, out int parsed))
                    {
                        float f01 = Math.Clamp(parsed, 0, 255) / 255f;
                        if (ci == 3) { a = f01; changed = true; }
                        else
                        {
                            var nr2 = new Vector3(
                                ci == 0 ? f01 : curRgbCh.X,
                                ci == 1 ? f01 : curRgbCh.Y,
                                ci == 2 ? f01 : curRgbCh.Z);
                            CeRgbToHsv(nr2, out h, out s, out v);
                            changed = true;
                        }
                    }
                }
            }

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(chTL.X, chTL.Y + CeRowH + CeGap));

            // ── Previous / current color compare + hex input ──────────────────
            Vector2 hexTL = ImGuiNET.ImGui.GetCursorScreenPos();
            float   preW  = 26f;
            float   hexW  = CePickW - preW * 2f - CeGap * 2f;
            Vector3 curRgb = CeHsvToRgb(h, s, v);

            // Previous swatch - clicking reverts
            Vector2 prevSwBR = new(hexTL.X + preW, hexTL.Y + CeRowH);
            ImGuiNET.ImGui.SetCursorScreenPos(hexTL);
            ImGuiNET.ImGui.InvisibleButton(key + "##prev", new Vector2(preW, CeRowH));
            bool prevHov = ImGuiNET.ImGui.IsItemHovered();
            bool prevClk = ImGuiNET.ImGui.IsItemClicked();
            float tprev = Anim((uint)key.GetHashCode() ^ 0xE7u, prevHov ? 1f : 0f, 14f);

            // Solid bg then prev color blended on top
            dl.AddRectFilled(hexTL, prevSwBR, InputBgH, 3f);
            dl.AddRectFilled(hexTL, prevSwBR, CeRgbaToImCol(prevRgba), 3f);
            dl.AddRect(hexTL, prevSwBR,
                Alpha((uint)0xFFFFFFFF, (byte)(60 + (int)(80 * tprev))), 3f, ImDrawFlags.None, 1f);
            if (prevHov)
            {
                dl.AddRectFilled(hexTL, prevSwBR, Alpha(0xFFFFFFFF, 30), 3f);
            }

            if (prevClk)
            {
                var pr3 = new Vector3(prevRgba.X, prevRgba.Y, prevRgba.Z);
                CeRgbToHsv(pr3, out h, out s, out v);
                a = prevRgba.W;
                changed = true;
            }

            // Current swatch
            Vector2 curSwTL = new(hexTL.X + preW + CeGap, hexTL.Y);
            Vector2 curSwBR = new(curSwTL.X + preW, hexTL.Y + CeRowH);
            dl.AddRectFilled(curSwTL, curSwBR, InputBgH, 3f);
            dl.AddRectFilled(curSwTL, curSwBR, CeRgbaToImCol(new Vector4(curRgb.X, curRgb.Y, curRgb.Z, a)), 3f);
            dl.AddRect(curSwTL, curSwBR, Alpha(BorderCol, 80), 3f, ImDrawFlags.None, 1f);

            // Hex text input
            string hex = string.Format("{0:X2}{1:X2}{2:X2}",
                (int)Math.Clamp(curRgb.X * 255f, 0f, 255f),
                (int)Math.Clamp(curRgb.Y * 255f, 0f, 255f),
                (int)Math.Clamp(curRgb.Z * 255f, 0f, 255f));

            if (CeStyledInput("##hex_" + key, ref hex, 7,
                ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase |
                ImGuiInputTextFlags.AutoSelectAll,
                curSwBR.X + CeGap, hexTL.Y, hexW, CeRowH))
            {
                if (hex.Length == 6 &&
                    uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint hv))
                {
                    var nr = new Vector3(
                        (hv >> 16 & 0xFF) / 255f,
                        (hv >> 8  & 0xFF) / 255f,
                        (hv       & 0xFF) / 255f);
                    CeRgbToHsv(nr, out h, out s, out v);
                    changed = true;
                }
            }

            float totalH = CePickH + CeGap + CeHueBarH + CeGap + CeHueBarH + CeGap + CeRowH + CeGap + CeRowH;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(svTL.X, svTL.Y + totalH));
            ImGuiNET.ImGui.Dummy(new Vector2(CePickW, 0f));

            return changed;
        }

        // ── HSV ? RGB ─────────────────────────────────────────────────────────

        private static void CeRgbToHsv(Vector3 rgb, out float h, out float s, out float v)
        {
            float r = rgb.X, g = rgb.Y, b = rgb.Z;
            float max = MathF.Max(r, MathF.Max(g, b));
            float min = MathF.Min(r, MathF.Min(g, b));
            float d   = max - min;
            v = max;
            s = max > 1e-6f ? d / max : 0f;
            if (d < 1e-6f) { h = 0f; return; }
            if      (max == r) h = (g - b) / d + (g < b ? 6f : 0f);
            else if (max == g) h = (b - r) / d + 2f;
            else               h = (r - g) / d + 4f;
            h = (h / 6f + 1f) % 1f;
        }

        private static Vector3 CeHsvToRgb(float h, float s, float v)
        {
            if (s < 1e-6f) return new Vector3(v, v, v);
            float hh = (h % 1f) * 6f;
            int   i  = (int)hh;
            float f  = hh - i;
            float p  = v * (1f - s);
            float q  = v * (1f - s * f);
            float t  = v * (1f - s * (1f - f));
            return i switch
            {
                0 => new(v, t, p),
                1 => new(q, v, p),
                2 => new(p, v, t),
                3 => new(p, q, v),
                4 => new(t, p, v),
                _ => new(v, p, q),
            };
        }

        private static uint CeHsvToImCol(float h, float s, float v) =>
            CeRgbToImCol(CeHsvToRgb(h, s, v));

        private static uint CeRgbToImCol(Vector3 c) =>
            ImGuiStyle.Col(
                (byte)Math.Clamp(c.X * 255f, 0f, 255f),
                (byte)Math.Clamp(c.Y * 255f, 0f, 255f),
                (byte)Math.Clamp(c.Z * 255f, 0f, 255f));

        private static uint CeRgbaToImCol(Vector4 c) =>
            ImGuiStyle.Col(
                (byte)Math.Clamp(c.X * 255f, 0f, 255f),
                (byte)Math.Clamp(c.Y * 255f, 0f, 255f),
                (byte)Math.Clamp(c.Z * 255f, 0f, 255f),
                (byte)Math.Clamp(c.W * 255f, 0f, 255f));

        // ───────────────────────────────────────────────────────────────────────────
        // TAB BAR  - horizontal tab strip with animated active indicator
        //
        // Default (scrollable: false) - tabs wrap onto multiple rows like a
        // tag cloud; any number of tabs, no horizontal overflow.
        //
        // Scrollable (scrollable: true) - single row; tabs that don't fit clip
        // off the right edge.  Scroll with the mouse wheel while hovering the
        // bar, or drag horizontally inside it.
        //
        // Usage:
        //   int tab = 0;
        //   if (UI.BeginTabBar("##tb1", new[]{"General","Config","Loot"}, ref tab))
        //   {
        //       UI.Toggle(...);
        //   }
        //   UI.EndTabBar();
        // ───────────────────────────────────────────────────────────────────────────

        private const float TabH       = 24f;
        private const float TabPadX    = 5f;
        private const float TabPadY    = 3f;
        private const float TabRound   = 4f;
        private const float TabGapX    = 3f;
        private const float TabGapY    = 3f;

        // Per-tab-bar scroll offset (scrollable mode) - keyed by tab-bar hash
        private static readonly Dictionary<uint, float> _tabScroll = new(8);

        public static bool BeginTabBar(string id, string[] tabs, ref int selected,
            bool scrollable = false, float width = 0f)
        {
            if (tabs == null || tabs.Length == 0) return true;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = width > 0f ? width : ImGuiNET.ImGui.GetContentRegionAvail().X;

            // Measure each tab's total width once
            var locTabs   = new string[tabs.Length];
            var tabWidths = new float[tabs.Length];
            for (int i = 0; i < tabs.Length; i++)
            {
                locTabs[i]    = LocManager.Get(tabs[i]);
                tabWidths[i]  = MeasureText(font, fsize, locTabs[i]).X + TabPadX * 2f;
            }

            // ── Compute bar height (wrapping or single-row) ───────────────
            float barH;
            if (scrollable)
            {
                barH = TabH;
            }
            else
            {
                // Count rows needed
                float rowX = 0f;
                int   rows = 1;
                for (int i = 0; i < tabs.Length; i++)
                {
                    if (rowX + tabWidths[i] > avail && rowX > 0f) { rows++; rowX = 0f; }
                    rowX += tabWidths[i] + TabGapX;
                }
                barH = rows * TabH + (rows - 1) * TabGapY;
            }

            Vector2 barTL = ImGuiNET.ImGui.GetCursorScreenPos();
            uint    barId = (uint)id.GetHashCode();

            // Pre-compute total content width - needed for scroll clamping and scrollbar sizing
            float totalTabW = 0f;
            for (int i = 0; i < tabs.Length; i++) totalTabW += tabWidths[i] + TabGapX;
            float maxScrollAll = MathF.Max(0f, totalTabW - avail + TabGapX);

            // ── Scrollbar geometry (computed before hit regions so we know total height) ──
            const float SbH     = 3f;
            const float SbGap   = 2f;
            const float SbRound = 1.5f;
            bool  hasSb   = scrollable && maxScrollAll > 0f;
            float sbY     = barTL.Y + barH + SbGap;

            // ── Invisible hit region: scrollbar thumb (placed first, drawn last) ──
            // Positioned now with a placeholder width; real thumb coords recalculated below.
            bool sbAct = false;
            bool sbHov = false;
            if (hasSb)
            {
                _tabScroll.TryGetValue(barId, out float sx0);
                float ratio0  = avail / totalTabW;
                float thumbW0 = MathF.Max(avail * ratio0, 20f);
                float thumbX0 = barTL.X + (sx0 / maxScrollAll) * (avail - thumbW0);

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(thumbX0, sbY - 2f));
                ImGuiNET.ImGui.InvisibleButton("##tb_sb_" + id, new Vector2(thumbW0, SbH + 4f));
                sbHov = ImGuiNET.ImGui.IsItemHovered();
                sbAct = ImGuiNET.ImGui.IsItemActive();

                if (sbAct)
                {
                    // Convert mouse delta on thumb - scroll delta in content space
                    float trackRange = avail - thumbW0;
                    if (trackRange > 0f)
                    {
                        float scrollDelta = ImGuiNET.ImGui.GetIO().MouseDelta.X * (maxScrollAll / trackRange);
                        sx0 = Math.Clamp(sx0 + scrollDelta, 0f, maxScrollAll);
                        _tabScroll[barId] = sx0;
                    }
                }
            }

            // ── Invisible hit region for the tab row (wheel scroll + tab-row drag) ──
            ImGuiNET.ImGui.SetCursorScreenPos(barTL);
            ImGuiNET.ImGui.InvisibleButton("##tb_bar_" + id, new Vector2(avail, barH));
            bool barHov = ImGuiNET.ImGui.IsItemHovered();
            bool barAct = ImGuiNET.ImGui.IsItemActive();

            // Horizontal scroll in scrollable mode
            if (scrollable)
            {
                _tabScroll.TryGetValue(barId, out float scrollX);

                // Mouse-wheel scroll
                if (barHov || sbHov)
                    scrollX -= ImGuiNET.ImGui.GetIO().MouseWheel * 30f;

                // Drag across the tab row itself
                if (barAct && !sbAct)
                    scrollX -= ImGuiNET.ImGui.GetIO().MouseDelta.X;

                scrollX = Math.Clamp(scrollX, 0f, maxScrollAll);
                _tabScroll[barId] = scrollX;
            }

            var dl = DL;

            // ── Draw each tab ─────────────────────────────────────────────
            _tabScroll.TryGetValue(barId, out float sx);
            float cx = barTL.X - (scrollable ? sx : 0f);
            float cy = barTL.Y;

            // Push clip rect so tabs don't overdraw outside the bar in scrollable mode
            if (scrollable)
                dl.PushClipRect(barTL, new Vector2(barTL.X + avail, barTL.Y + barH), true);

            for (int i = 0; i < tabs.Length; i++)
            {
                float tw = tabWidths[i];

                // Wrap in non-scrollable mode
                if (!scrollable && cx + tw > barTL.X + avail && cx > barTL.X)
                {
                    cx  = barTL.X;
                    cy += TabH + TabGapY;
                }

                Vector2 tabTL = new(cx, cy);
                Vector2 tabBR = new(cx + tw, cy + TabH);

                bool   isActive = i == selected;
                uint   tabItemId = (uint)(barId * 2654435761u ^ (uint)i);
                bool   tabHov    = barHov &&
                                   ImGuiNET.ImGui.GetIO().MousePos.X >= tabTL.X &&
                                   ImGuiNET.ImGui.GetIO().MousePos.X <= tabBR.X &&
                                   ImGuiNET.ImGui.GetIO().MousePos.Y >= tabTL.Y &&
                                   ImGuiNET.ImGui.GetIO().MousePos.Y <= tabBR.Y;

                float th = Anim(tabItemId,         tabHov   ? 1f : 0f, 14f);
                float ta = Anim(tabItemId ^ 0xACu, isActive ? 1f : 0f, 18f);

                // Background: ghost for inactive, accent fill for active
                uint bgInactive = DA(Alpha(Dark4, (byte)(80 + (int)(60 * th))));
                uint bgActive   = DA(Lerp(ImGuiStyle.DarkerColor(Accent, 20), Accent, th));
                uint bgCol      = DA(Lerp(bgInactive, bgActive, ta));
                dl.AddRectFilled(tabTL, tabBR, bgCol, TabRound);

                // Border: dim for inactive, bright accent for active
                uint bordInactive = DA(Alpha(BorderCol, (byte)(80 + (int)(80 * th))));
                uint bordActive   = DA(Alpha(Accent, (byte)(180 + (int)(40 * th))));
                uint bordCol      = DA(Lerp(bordInactive, bordActive, ta));
                dl.AddRect(tabTL, tabBR, bordCol, TabRound, ImDrawFlags.None, 1f);

                // Label - white on active, dimmer on inactive
                uint lblInactive = DA(Lerp(Alpha(TxtDim, 200), Alpha(TxtNorm, 255), th));
                uint lblActive   = DA(Alpha(0xFFFFFFFF, (byte)(200 + (int)(55 * ta))));
                uint lblCol      = DA(Lerp(lblInactive, lblActive, ta));
                dl.AddText(font, fsize,
                    new Vector2(tabTL.X + TabPadX, tabTL.Y + (TabH - fsize) * .5f),
                    lblCol, locTabs[i]);

                // Click detection (using the mouse-clicked pos for reliability)
                if (!IsDisabled &&
                    ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                    ImGuiNET.ImGui.GetIO().MouseClickedPos[0].X >= tabTL.X &&
                    ImGuiNET.ImGui.GetIO().MouseClickedPos[0].X <= tabBR.X &&
                    ImGuiNET.ImGui.GetIO().MouseClickedPos[0].Y >= tabTL.Y &&
                    ImGuiNET.ImGui.GetIO().MouseClickedPos[0].Y <= tabBR.Y)
                {
                    selected = i;
                }

                cx += tw + TabGapX;
            }

            if (scrollable)
                dl.PopClipRect();

            // ── Scrollbar draw (scrollable mode only, when there is overflow) ──
            if (hasSb)
            {
                _tabScroll.TryGetValue(barId, out float sx2);

                float sbX0   = barTL.X;
                float sbX1   = barTL.X + avail;
                float ratio  = avail / totalTabW;
                float thumbW = MathF.Max(avail * ratio, 20f);
                float thumbX = sbX0 + (sx2 / maxScrollAll) * (avail - thumbW);

                float tSb = Anim(barId ^ 0xBBu, (barHov || sbHov || sbAct) ? 1f : 0f, 12f);

                // Track
                dl.AddRectFilled(
                    new Vector2(sbX0, sbY),
                    new Vector2(sbX1, sbY + SbH),
                    DA(Alpha(Dark4, (byte)(80 + (int)(40 * tSb)))), SbRound);

                // Thumb - brightens further on active drag
                byte thumbAlpha = (byte)(120 + (int)(80 * tSb) + (sbAct ? 55 : 0));
                uint thumbCol   = sbAct ? DA(Alpha(Accent, thumbAlpha)) : DA(Alpha(BorderCol, thumbAlpha));
                dl.AddRectFilled(
                    new Vector2(thumbX,          sbY),
                    new Vector2(thumbX + thumbW, sbY + SbH),
                    thumbCol, SbRound);
            }

            // Advance cursor past bar + optional scrollbar
            float extraH = hasSb ? SbGap + SbH + 2f : 0f;
            float endY   = barTL.Y + barH + extraH;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(barTL.X, endY));
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 0f));
            return true;
        }

        public static void EndTabBar() { }

        // ───────────────────────────────────────────────────────────────────────────
        // LISTBOX  - scrollable themed list with selectable rows
        //
        //  int sel = 0;
        //  string[] items = { "Item A", "Item B", "Item C" };
        //  UI.ListBox("##lb1", ref sel, items, visibleRows: 5);
        // ───────────────────────────────────────────────────────────────────────────

        private const float ListItemH  = 26f;
        private const float ListRound  = 5f;

        public static bool ListBox(string label, ref int selectedIndex, string[] items,
            int visibleRows = 6, float width = 0f)
        {
            if (items == null || items.Length == 0) return false;

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
            float w     = width > 0f ? MathF.Min(width, avail) : avail;
            float h     = ListItemH * MathF.Min(items.Length, visibleRows) + 8f;

            string cleanLabel = Loc(label);
            if (!string.IsNullOrEmpty(cleanLabel))
            {
                Vector2 lpos = ImGuiNET.ImGui.GetCursorScreenPos();
                DL.AddText(font, fsize, lpos, DA(TxtNorm), cleanLabel);
                ImGuiNET.ImGui.Dummy(new Vector2(w, fsize + 4f));
            }

            Vector2 tl = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 br = new(tl.X + w, tl.Y + h);
            var dl = DL;

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg,              Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,               Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Alpha(InputBg, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Alpha(BorderCol, 160));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Alpha(Accent, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Alpha(Accent, 220));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ListRound);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);

            bool changed = false;

            if (ImGuiNET.ImGui.BeginListBox("##lb_" + label, new Vector2(w, h)))
            {
                // Capture the draw list and clip rect INSIDE the listbox child window.
                // Draw-list calls bypass ImGui's clip rect by default, so we must push it
                // manually - otherwise scrolled-out items render outside the box bounds.
                var  innerDL  = ImGuiNET.ImGui.GetWindowDrawList();
                var  clipMin  = ImGuiNET.ImGui.GetWindowPos();
                var  clipMax  = clipMin + ImGuiNET.ImGui.GetWindowSize();
                innerDL.PushClipRect(clipMin, clipMax, true);

                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 1f));

                for (int i = 0; i < items.Length; i++)
                {
                    bool    selected = i == selectedIndex;
                    Vector2 itemTL   = ImGuiNET.ImGui.GetCursorScreenPos();
                    float   rowW     = ImGuiNET.ImGui.GetContentRegionAvail().X;

                    ImGuiNET.ImGui.InvisibleButton("##li_" + label + i, new Vector2(rowW, ListItemH));
                    bool rowHov   = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
                    bool rowClick = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
                    uint rowId    = ImGuiNET.ImGui.GetItemID();
                    float rth     = Anim(rowId, rowHov ? 1f : 0f, 14f);

                    if (rowClick) { selectedIndex = i; changed = true; }

                    Vector2 rowBR = new(itemTL.X + rowW, itemTL.Y + ListItemH);
                    if (selected)
                    {
                        innerDL.AddRectFilled(itemTL, rowBR, DA(Lerp(Alpha(Accent, 60), Alpha(Accent, 90), rth)), 4f);
                        innerDL.AddRectFilled(itemTL, new Vector2(itemTL.X + 3f, rowBR.Y), DA(Accent), 2f);
                    }
                    else if (rth > 0.01f)
                        innerDL.AddRectFilled(itemTL, rowBR, DA(Alpha(ImGuiStyle.Dark3, (byte)(80 * rth))), 4f);

                    uint txtCol = DA(Lerp(TxtNorm, 0xFFFFFFFF, selected ? MathF.Max(rth, 0.3f) : rth * 0.4f));
                    innerDL.AddText(font, fsize, new Vector2(itemTL.X + 10f, itemTL.Y + (ListItemH - fsize) * .5f), txtCol, LocManager.Get(items[i]));
                }

                innerDL.PopClipRect();
                ImGuiNET.ImGui.PopStyleVar();
                ImGuiNET.ImGui.EndListBox();
            }

            ImGuiNET.ImGui.PopStyleVar(2);
            ImGuiNET.ImGui.PopStyleColor(6);

            // ─────────────────────────────────────────────────────────────────────────
            // Capture actual post-render cursor position - this is the true bottom of the listbox
            // regardless of any internal scroll state. Use this for the border and cursor advance.
            Vector2 afterPos = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 actualBR = new(tl.X + w, afterPos.Y);

            dl.AddRect(tl, actualBR, DA(Alpha(BorderCol, 120)), ListRound, ImDrawFlags.None, 1f);

            return changed;
        }

        // ───────────────────────────────────────────────────────────────────────────
        // CAPTION  - muted descriptive text, word-wrapped to available width.
        //
        // Use inside a group to add context beneath a heading or above a button.
        //   UI.Caption("Reads the game's orders twice a second.");
        // ───────────────────────────────────────────────────────────────────────────

        // ─────────────────────────────────────────────────────────────────────────
        // VIRTUAL LIST - only renders visible rows; handles thousands of items.
        //
        //   UI.VirtualList("##players", players.Count,
        //       (i, w) => { DrawRow(players[i], w); },
        //       ref _sel, visibleRows: 8);
        //
        //   UI.ScrollToItem("##players", index); // enqueue before the frame
        // ─────────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, int> _scrollToItem = new();

        public static void ScrollToItem(string listId, int index)
            => _scrollToItem[listId] = index;

        public static bool VirtualList(string id, int count,
            Action<int, float> drawItem,
            ref int selectedIndex,
            float itemHeight = ListItemH, int visibleRows = 6, float width = 0f, float? fixedHeight = null, bool border = true)
        {
            if (count == 0) { Empty(); return false; }

            float avail = width > 0f ? width
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;
            // fixedHeight overrides the usual "shrink to fit however many rows there actually
            // are" sizing - used by UI.Table so its box always fills the space it was given
            // instead of leaving dead space below a short row list.
            float h = fixedHeight ?? (itemHeight * Math.Min(count, visibleRows) + 8f);

            Vector2 vlTL = ImGuiNET.ImGui.GetCursorScreenPos();
            var vlDL = DL;

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg,              Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,               Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Alpha(InputBg, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Alpha(BorderCol, 160));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Alpha(Accent, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Alpha(Accent, 220));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ListRound);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);

            bool changed = false;

            // BeginChild must always be paired with EndChild in ImGui 1.90+ regardless of return value.
            bool childVis = ImGuiNET.ImGui.BeginChild(id, new Vector2(avail, h),
                ImGuiChildFlags.None, ImGuiWindowFlags.None);

            if (childVis)
            {
                if (_scrollToItem.TryGetValue(id, out int targetIdx))
                {
                    ImGuiNET.ImGui.SetScrollY(targetIdx * itemHeight);
                    _scrollToItem.Remove(id);
                }

                float scrollY = ImGuiNET.ImGui.GetScrollY();
                int   first   = Math.Max(0, (int)(scrollY / itemHeight) - 1);
                int   last    = Math.Min(count - 1, first + visibleRows + 2);

                if (first > 0)
                    ImGuiNET.ImGui.Dummy(new Vector2(avail, first * itemHeight));

                var  vDL  = ImGuiNET.ImGui.GetWindowDrawList();
                var  cMin = ImGuiNET.ImGui.GetWindowPos();
                var  cMax = cMin + ImGuiNET.ImGui.GetWindowSize();
                vDL.PushClipRect(cMin, cMax, true);
                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 1f));

                for (int i = first; i <= last; i++)
                {
                    bool    sel  = i == selectedIndex;
                    Vector2 rTL  = ImGuiNET.ImGui.GetCursorScreenPos();
                    float   rowW = ImGuiNET.ImGui.GetContentRegionAvail().X;

                    // Allows a real widget drawn inside drawItem (e.g. a per-row icon button)
                    // to still receive hover/click on top of this row's own selection button -
                    // without this, ImGui's active-id exclusivity silently eats input on
                    // anything submitted later in the same row once this button claims it.
                    ImGuiNET.ImGui.SetNextItemAllowOverlap();
                    ImGuiNET.ImGui.InvisibleButton("##vl_" + id + i, new Vector2(rowW, itemHeight));
                    bool vhov = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
                    bool vclk = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
                    uint vrid = ImGuiNET.ImGui.GetItemID();
                    float vt  = Anim(vrid, vhov ? 1f : 0f, 14f);

                    if (vclk) { selectedIndex = i; changed = true; }

                    Vector2 rBR = new(rTL.X + rowW, rTL.Y + itemHeight);
                    if (sel)
                    {
                        vDL.AddRectFilled(rTL, rBR, DA(Lerp(Alpha(Accent, 60), Alpha(Accent, 90), vt)), 4f);
                        vDL.AddRectFilled(rTL, new Vector2(rTL.X + 3f, rBR.Y), DA(Accent), 2f);
                    }
                    else if (vt > 0.01f)
                        vDL.AddRectFilled(rTL, rBR, DA(Alpha(ImGuiStyle.Dark3, (byte)(80 * vt))), 4f);

                    ImGuiNET.ImGui.SetCursorScreenPos(rTL);
                    drawItem(i, rowW);
                    ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rTL.X, rBR.Y));
                }

                ImGuiNET.ImGui.PopStyleVar();
                vDL.PopClipRect();

                int below = count - last - 1;
                if (below > 0)
                    ImGuiNET.ImGui.Dummy(new Vector2(avail, below * itemHeight));
            }

            ImGuiNET.ImGui.EndChild();

            ImGuiNET.ImGui.PopStyleVar(2);
            ImGuiNET.ImGui.PopStyleColor(6);

            Vector2 vlEnd = ImGuiNET.ImGui.GetCursorScreenPos();
            if (border)
                vlDL.AddRect(vlTL, new Vector2(vlTL.X + avail, vlEnd.Y), DA(Alpha(BorderCol, 170)), ListRound, ImDrawFlags.None, 1f);
            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // TABLE - header row + virtualized, selectable row list (built on VirtualList).
        // Generalizes the hand-rolled "TextDisabled header + manual column offsets +
        // VirtualList + manual click-outside-deselect + manual per-row context menu"
        // pattern that kept getting re-implemented per-widget (Player Info, Loot Info, …).
        //
        //   UI.Table("##players",
        //       new[] { new UI.TableColumn("Name"), new UI.TableColumn("Dist", 45f), new UI.TableColumn("KD", 45f) },
        //       alive.Count,
        //       (i, colX, w) => {
        //           var p = alive[i];
        //           dl.AddText(colX[0], ty, col, p.Name);
        //           dl.AddText(colX[1], ty, col, $"{dist}m");
        //           dl.AddText(colX[2], ty, col, $"{kd:F1}");
        //       },
        //       ref _selRow,
        //       rowContextMenu: i => {
        //           if (UI.MenuItem("Set Focus")) alive[i].IsFocused = true;
        //       });
        //
        // Exactly one column should have Width == 0f (flexible - takes whatever space is
        // left after the fixed-width columns); if none do, the last column absorbs it.
        // visibleRows: 0 (default) fills whatever vertical space the caller currently has
        // instead of a fixed row count, so resizing a widget/panel taller grows the table.
        // ─────────────────────────────────────────────────────────────────────────

        public readonly struct TableColumn
        {
            public readonly string Header;
            public readonly float  Width; // 0f = flexible column
            public TableColumn(string header, float width = 0f) { Header = header; Width = width; }
        }

        public static bool Table(
            string id,
            TableColumn[] columns,
            int rowCount,
            Action<int, float[], float> drawRow,
            ref int selectedIndex,
            float itemHeight = ListItemH,
            int visibleRows = 0,
            float width = 0f,
            Action<int> rowContextMenu = null,
            bool clickOutsideDeselects = true,
            int sortColumn = -1,
            bool sortDescending = false,
            Action<int> onSortColumn = null,
            bool border = true)
        {
            if (columns == null || columns.Length == 0 || drawRow == null) return false;

            float avail = width > 0f ? width
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;

            // Column X-offsets: fixed-width columns keep their width; the flexible column
            // (Width == 0f - the last one wins if more than one is marked flexible) absorbs
            // whatever's left. Computed once and handed to drawRow so callers never
            // re-derive column math the header already knows.
            int   flexIdx    = -1;
            float fixedTotal = 0f;
            for (int c = 0; c < columns.Length; c++)
            {
                if (columns[c].Width <= 0f) flexIdx = c;
                else fixedTotal += columns[c].Width;
            }
            if (flexIdx < 0)
            {
                // No explicit flexible column - the last one absorbs the remainder instead.
                flexIdx = columns.Length - 1;
                fixedTotal -= columns[flexIdx].Width;
            }

            var colW = new float[columns.Length];
            var colX = new float[columns.Length];
            float flexW = MathF.Max(20f, avail - fixedTotal);
            float cx = 0f;
            for (int c = 0; c < columns.Length; c++)
            {
                colW[c] = c == flexIdx ? flexW : columns[c].Width;
                colX[c] = cx;
                cx += colW[c];
            }

            // ── Header ───────────────────────────────────────────────────────────
            // Columns become clickable sort toggles when the caller supplies onSortColumn
            // (opt-in - other Table callers are unaffected and keep plain static headers).
            var     font    = ImGuiStyle.FontNormal;
            float   fsize   = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float   headerH = ImGuiNET.ImGui.GetTextLineHeight() + 4f;
            Vector2 hTL     = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 hdrMp   = ImGuiNET.ImGui.GetIO().MousePos;
            for (int c = 0; c < columns.Length; c++)
            {
                string hdr = LocManager.Get(columns[c].Header);
                float  cellL = hTL.X + colX[c];

                bool clickable = onSortColumn != null && !string.IsNullOrEmpty(hdr);
                bool active    = c == sortColumn;
                bool hov       = false;
                if (clickable)
                {
                    float cellR = c == columns.Length - 1 ? hTL.X + avail : hTL.X + colX[c + 1];
                    hov = hdrMp.X >= cellL && hdrMp.X < cellR && hdrMp.Y >= hTL.Y && hdrMp.Y < hTL.Y + headerH;
                    if (hov)
                    {
                        ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                            onSortColumn(c);
                    }
                }

                uint hdrCol = active ? DA(Accent) : hov ? DA(0xFFFFFFFF) : DA(TxtDim);
                DL.AddText(font, fsize, new Vector2(cellL, hTL.Y), hdrCol, hdr);

                if (active)
                {
                    float tw = font.CalcTextSizeA(fsize, float.MaxValue, -1f, hdr).X;
                    DrawChevron(DL, cellL + tw + 9f, hTL.Y + fsize * 0.5f, 3.5f,
                        sortDescending ? MathF.PI * 0.5f : -MathF.PI * 0.5f, DA(Accent));
                }
            }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(hTL.X, hTL.Y + headerH));

            // Fill whatever vertical space is left instead of a fixed row count, so the
            // table grows to fill a resized widget/panel rather than leaving dead space.
            // boxH is set to the *exact* pixel height available (matching the pattern
            // LootFilterPanel.DrawItemsList uses for its own BeginChild) rather than floored
            // down to a whole multiple of itemHeight - flooring was leaving a residual gap
            // between the table's border and the container's actual bottom edge. visibleRows
            // only bounds how many rows VirtualList's windowing loop considers; it's kept
            // generous (ceiling, +1) so the loop always has enough rows queued to cover the
            // full boxH, with the last one clipped rather than the box falling short of it.
            float boxH;
            if (visibleRows <= 0)
            {
                float availH = ImGuiNET.ImGui.GetContentRegionAvail().Y;
                visibleRows = Math.Max(1, (int)MathF.Ceiling(availH / itemHeight)) + 1;
                boxH = MathF.Max(itemHeight + 8f, availH);
            }
            else
            {
                // the list child has no inner padding: just the rows plus the border, no dead space below them
                boxH = itemHeight * visibleRows + 2f;
            }

            var absColX = new float[columns.Length]; // filled in per-row below (screen-space)

            Vector2 tblTL = ImGuiNET.ImGui.GetCursorScreenPos();
            bool changed = VirtualList(id, rowCount, (i, rowW) =>
            {
                Vector2 rowTL = ImGuiNET.ImGui.GetCursorScreenPos();
                for (int c = 0; c < columns.Length; c++)
                    absColX[c] = rowTL.X + colX[c];

                drawRow(i, absColX, rowW);

                if (rowContextMenu != null &&
                    UI.BeginContextMenu($"##{id}_ctx_{i}", rowTL, new Vector2(rowTL.X + rowW, rowTL.Y + itemHeight)))
                {
                    rowContextMenu(i);
                    UI.EndContextMenu();
                }
            }, ref selectedIndex, itemHeight, visibleRows, avail, fixedHeight: boxH, border: border);
            Vector2 tblBR = ImGuiNET.ImGui.GetCursorScreenPos();

            // Click anywhere that isn't a row - outside the table entirely, or on the dead
            // space below the last row when there are fewer rows than fit the box (no
            // scrollbar in that case) - clears the selection. `changed` tells us a row's own
            // InvisibleButton already consumed this click, so we never fight that; we only
            // need to catch the "inside the box, on nothing" case VirtualList doesn't itself
            // report. `rowsBottomY < tblBR.Y` guards against treating a scrollbar-drag click
            // (which lands "inside" but on real, overflowing row content) as a deselect.
            if (clickOutsideDeselects && selectedIndex >= 0 && !changed &&
                !ImGuiNET.ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId) &&
                ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var mp = ImGuiNET.ImGui.GetIO().MousePos;
                bool outside = mp.X < tblTL.X || mp.X > tblTL.X + avail || mp.Y < tblTL.Y || mp.Y > tblBR.Y;
                float rowsBottomY = tblTL.Y + rowCount * itemHeight;
                bool inDeadSpace = !outside && rowsBottomY < tblBR.Y && mp.Y >= rowsBottomY;
                if (outside || inDeadSpace) selectedIndex = -1;
            }

            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CHECK LIST - virtualised scrollable list where every item has a checkbox.
        //
        //   UI.CheckList("##mylist", items,
        //       isChecked: i => cfg.Selected.Contains(items[i]),
        //       onToggle:  (i, v) => { if (v) cfg.Selected.Add(items[i]); else cfg.Selected.Remove(items[i]); },
        //       filter: _search,
        //       height: 200f,
        //       contextMenu: sel => {
        //           if (UI.MenuItem("Enable selected"))  { foreach (var i in sel) onToggle(i, true);  }
        //           if (UI.MenuItem("Disable selected")) { foreach (var i in sel) onToggle(i, false); }
        //       });
        //
        // Multi-select:
        //   Click          - toggle item, set as sole UI selection
        //   Ctrl+Click     - toggle UI selection only (no checked-state change)
        //   Shift+Click    - range-select in UI selection
        //   Right-click    - open context menu (if contextMenu != null); acts on UI selection
        // ─────────────────────────────────────────────────────────────────────────

        private struct CheckListState
        {
            public HashSet<int> Selection;   // UI selection (indices into full items list)
            public int          LastClicked; // last clicked index (for shift-range)
        }
        private static readonly Dictionary<string, CheckListState> _checkLists    = new();
        private static readonly List<int>                          _checkListVis  = new(128);

        public static bool CheckList(
            string id,
            IReadOnlyList<string> items,
            Func<int, bool>       isChecked,
            Action<int, bool>     onToggle,
            string                filter      = null,
            float                 height      = 200f,
            float                 width       = 0f,
            Action<IReadOnlyList<int>> contextMenu = null)
        {
            if (!_checkLists.TryGetValue(id, out var cls))
                cls = new CheckListState { Selection = new HashSet<int>(), LastClicked = -1 };

            float avail = width > 0f ? width
                        : _groupAvailW > 0f ? GroupInnerWidth
                        : ImGuiNET.ImGui.GetContentRegionAvail().X;

            // Build filtered index list - reuse static list to avoid per-frame allocation.
            bool hasFilter = !string.IsNullOrWhiteSpace(filter);
            _checkListVis.Clear();
            if (hasFilter)
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        _checkListVis.Add(i);
            }
            else
            {
                for (int i = 0; i < items.Count; i++) _checkListVis.Add(i);
            }
            var visIdx  = _checkListVis;
            int visCount = visIdx.Count;

            // Draw border before child so the rect wraps exactly
            Vector2 clTL = ImGuiNET.ImGui.GetCursorScreenPos();
            var     clDL = DL;

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg,             Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,              Alpha(0xFF000000, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,         Alpha(InputBg, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,       Alpha(BorderCol, 160));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered,Alpha(Accent, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Alpha(Accent, 220));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ListRound);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);

            bool changed = false;
            string ctxPopupId = id + "_clctx";

            bool childVis = ImGuiNET.ImGui.BeginChild(id, new Vector2(avail, height),
                ImGuiChildFlags.None, ImGuiWindowFlags.None);

            if (childVis)
            {
                if (visCount == 0)
                {
                    // Empty state centred in the child
                    float   eh     = height;
                    Vector2 eTL    = ImGuiNET.ImGui.GetCursorScreenPos();
                    ImGuiNET.ImGui.Dummy(new Vector2(avail, eh));
                    var     eDL    = ImGuiNET.ImGui.GetWindowDrawList();
                    float   ecx    = eTL.X + avail * 0.5f;
                    float   ecy    = eTL.Y + eh * 0.5f;
                    var     efont  = ImGuiStyle.FontNormal;
                    float   efsize = efont.FontSize > 0f ? efont.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                    string  emsg   = hasFilter ? "No matches" : "No items";

                    // Dashed ring
                    float er = 13f; int esegs = 16;
                    for (int i = 0; i < esegs; i += 2)
                    {
                        float a0 = i * MathF.PI * 2f / esegs, a1 = (i + 1) * MathF.PI * 2f / esegs;
                        eDL.AddLine(new Vector2(ecx + er * MathF.Cos(a0), ecy + er * MathF.Sin(a0)),
                                    new Vector2(ecx + er * MathF.Cos(a1), ecy + er * MathF.Sin(a1)),
                                    DA(Alpha(TxtDim, 55)), 1.5f);
                    }
                    eDL.AddLine(new Vector2(ecx - er * 0.5f, ecy), new Vector2(ecx + er * 0.5f, ecy),
                        DA(Alpha(TxtDim, 55)), 1.5f);
                    Vector2 ets = MeasureText(efont, efsize * 0.88f, emsg);
                    eDL.AddText(efont, efsize * 0.88f,
                        new Vector2(ecx - ets.X * 0.5f, ecy + er + 5f),
                        DA(Alpha(TxtDim, 130)), emsg);
                }
                else
                {
                    float scrollY    = ImGuiNET.ImGui.GetScrollY();
                    int   visRows    = (int)(height / ListItemH) + 2;
                    int   firstRow   = Math.Max(0, (int)(scrollY / ListItemH) - 1);
                    int   lastRow    = Math.Min(visCount - 1, firstRow + visRows);

                    if (firstRow > 0)
                        ImGuiNET.ImGui.Dummy(new Vector2(avail, firstRow * ListItemH));

                    var  innerDL  = ImGuiNET.ImGui.GetWindowDrawList();
                    var  cMin     = ImGuiNET.ImGui.GetWindowPos();
                    var  cMax     = cMin + ImGuiNET.ImGui.GetWindowSize();
                    innerDL.PushClipRect(cMin, cMax, true);
                    ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 1f));

                    var io = ImGuiNET.ImGui.GetIO();
                    bool ctrlHeld  = io.KeyCtrl;
                    bool shiftHeld = io.KeyShift;

                    for (int vi = firstRow; vi <= lastRow; vi++)
                    {
                        int     idx    = visIdx[vi];
                        bool    chk    = isChecked(idx);
                        bool    uiSel  = cls.Selection.Contains(idx);
                        Vector2 rowTL  = ImGuiNET.ImGui.GetCursorScreenPos();
                        float   rowW   = ImGuiNET.ImGui.GetContentRegionAvail().X;

                        ImGuiNET.ImGui.InvisibleButton("##cl_" + id + vi, new Vector2(rowW, ListItemH));
                        bool rowHov  = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
                        bool rowClk  = !IsDisabled && ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Left);
                        bool rowRClk = !IsDisabled && ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right);
                        uint rowId   = ImGuiNET.ImGui.GetItemID();
                        float rth    = Anim(rowId, rowHov ? 1f : 0f, 14f);

                        if (rowClk)
                        {
                            if (ctrlHeld)
                            {
                                // Ctrl+click: toggle UI selection only
                                if (cls.Selection.Contains(idx)) cls.Selection.Remove(idx);
                                else                             cls.Selection.Add(idx);
                                cls.LastClicked = idx;
                            }
                            else if (shiftHeld && cls.LastClicked >= 0)
                            {
                                // Shift+click: range select (add range to selection)
                                int startVI = visIdx.IndexOf(cls.LastClicked);
                                if (startVI < 0) startVI = vi;
                                int lo = Math.Min(startVI, vi), hi = Math.Max(startVI, vi);
                                for (int r = lo; r <= hi; r++) cls.Selection.Add(visIdx[r]);
                            }
                            else
                            {
                                // Plain click: toggle checkbox, reset to single selection
                                cls.Selection.Clear();
                                cls.Selection.Add(idx);
                                cls.LastClicked = idx;
                                bool nv = !chk;
                                onToggle(idx, nv);
                                changed = true;
                            }
                        }

                        if (rowRClk && contextMenu != null)
                        {
                            // Ensure right-clicked item is in the selection
                            if (!cls.Selection.Contains(idx)) { cls.Selection.Clear(); cls.Selection.Add(idx); }
                            ImGuiNET.ImGui.OpenPopup(ctxPopupId);
                        }

                        Vector2 rowBR = new(rowTL.X + rowW, rowTL.Y + ListItemH);

                        // Row background: accent tint for UI-selected, hover for others
                        if (uiSel)
                            innerDL.AddRectFilled(rowTL, rowBR, DA(Alpha(Accent, (byte)(50 + (int)(30 * rth)))), 3f);
                        else if (rth > 0.01f)
                            innerDL.AddRectFilled(rowTL, rowBR, DA(Alpha(ImGuiStyle.Dark3, (byte)(70 * rth))), 3f);

                        // Checkbox - left-aligned
                        const float CbPad = 6f, CbGap = 6f;
                        float cbX  = rowTL.X + CbPad;
                        float cbY  = rowTL.Y + (ListItemH - CbSize) * 0.5f;
                        Vector2 cbTL2 = new(cbX, cbY), cbBR2 = new(cbX + CbSize, cbY + CbSize);
                        float tv  = Anim(rowId ^ 0xCBu, chk ? 1f : 0f, 18f);
                        innerDL.AddRectFilled(cbTL2, cbBR2,
                            DA(Lerp(Lerp(Alpha(Dark5, 220), Alpha(Dark4, 240), rth),
                                   Lerp(Accent, AccentHover, rth), tv)), CbRound);
                        innerDL.AddRect(cbTL2, cbBR2,
                            DA(Lerp(Alpha(BorderCol, 160), Alpha(Accent, 220), MathF.Max(rth, tv))),
                            CbRound, ImDrawFlags.None, 1.5f);
                        if (tv > 0.05f)
                        {
                            float midX2 = cbX + CbSize * 0.35f, midY2 = cbY + CbSize * 0.65f;
                            uint  tick  = DA(Alpha(0xFFFFFFFF, (byte)(255 * MathF.Min(tv * 2f, 1f))));
                            innerDL.AddLine(new Vector2(cbX + 3.5f, cbY + CbSize * 0.52f), new Vector2(midX2, midY2), tick, 1.8f);
                            innerDL.AddLine(new Vector2(midX2, midY2), new Vector2(cbX + CbSize - 3.5f, cbY + 4.5f), tick, 1.8f);
                        }

                        // Label
                        var   lFont  = ImGuiStyle.FontNormal;
                        float lFsize = lFont.FontSize > 0f ? lFont.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                        uint  lCol   = DA(Lerp(Lerp(TxtDim, TxtNorm, tv), 0xFFFFFFFF, rth));
                        innerDL.AddText(lFont, lFsize,
                            new Vector2(cbX + CbSize + CbGap, rowTL.Y + (ListItemH - lFsize) * 0.5f),
                            lCol, items[idx]);

                        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rowTL.X, rowBR.Y));
                    }

                    ImGuiNET.ImGui.PopStyleVar();
                    innerDL.PopClipRect();

                    int below = visCount - lastRow - 1;
                    if (below > 0)
                        ImGuiNET.ImGui.Dummy(new Vector2(avail, below * ListItemH));
                }
            }

            ImGuiNET.ImGui.EndChild();
            ImGuiNET.ImGui.PopStyleVar(2);
            ImGuiNET.ImGui.PopStyleColor(6);

            // Border drawn over the child region
            Vector2 clEnd = ImGuiNET.ImGui.GetCursorScreenPos();
            clDL.AddRect(clTL, new Vector2(clTL.X + avail, clEnd.Y),
                DA(Alpha(BorderCol, 120)), ListRound, ImDrawFlags.None, 1f);

            // Clear UI selection when the user left-clicks outside the list (and no popup is open)
            if (cls.Selection.Count > 0 && !ImGuiNET.ImGui.IsPopupOpen(ctxPopupId))
            {
                var  _clio = ImGuiNET.ImGui.GetIO();
                bool clickedOutside =
                    ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                    (_clio.MousePos.X < clTL.X || _clio.MousePos.X > clTL.X + avail ||
                     _clio.MousePos.Y < clTL.Y || _clio.MousePos.Y > clEnd.Y);
                if (clickedOutside) cls.Selection.Clear();
            }

            // Context menu popup
            if (contextMenu != null)
            {
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.ContextMenuBg, 252)));
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(BorderCol, 180)));
                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 4f));
                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(0f, 1f));
                ImGuiNET.ImGui.SetNextWindowSizeConstraints(
                    new Vector2(160f, 0f), new Vector2(400f, float.MaxValue));
                if (ImGuiNET.ImGui.BeginPopup(ctxPopupId))
                {
                    contextMenu(cls.Selection.ToList().AsReadOnly());
                    ImGuiNET.ImGui.EndPopup();
                }
                ImGuiNET.ImGui.PopStyleVar(3);
                ImGuiNET.ImGui.PopStyleColor(2);
            }

            _checkLists[id] = cls;
            return changed;
        }

        public static void Caption(string text, float width = 0f, LabelStyle style = LabelStyle.Dim)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = LocManager.Get(text);
            float avail    = width > 0f        ? width
                           : _groupAvailW > 0f ? GroupInnerWidth
                           : ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl     = ImGuiNET.ImGui.GetCursorScreenPos();  // screen-space, for _lastRowTL
            float   wrapX  = ImGuiNET.ImGui.GetCursorPos().X + avail; // window-local, for PushTextWrapPos

            uint col = style switch
            {
                LabelStyle.Warning => DA(ImGuiStyle.Warning),
                LabelStyle.Danger  => DA(ImGuiStyle.Danger),
                LabelStyle.Success => DA(ImGuiStyle.Success),
                LabelStyle.Accent  => DA(Accent),
                LabelStyle.Bold    => DA(TxtNorm),
                _                  => DA(TxtDim),
            };
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, ToV4(col));
            ImGuiNET.ImGui.PushTextWrapPos(wrapX);
            ImGuiNET.ImGui.TextUnformatted(text);
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.PopStyleColor();

            Vector2 br = ImGuiNET.ImGui.GetCursorScreenPos();
            _lastRowTL = tl;
            _lastRowBR = new Vector2(tl.X + avail, br.Y);
        }

        // ───────────────────────────────────────────────────────────────────────────
        // TOOLTIP  - call immediately after any custom control to show a themed
        // tooltip when the mouse is within the control's row.
        //
        //   UI.Toggle("Enable Chams", ref _chamsEnabled);
        //   UI.Tooltip("Enables the chams overlay for all entity types.");
        // ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-clamps the current tooltip window so it stays fully inside the current ImGui
        /// context's DisplaySize. Every tooltip is positioned at mouse+14,14 up front, which
        /// is fine in the (large) main app window but overflows a small popped-out widget's
        /// own OS window - there's no multi-viewport support, so anything drawn past a
        /// context's DisplaySize is simply clipped, not shown on top of neighbouring windows.
        /// Call after the tooltip's content has been drawn (so its auto-fit size is known)
        /// and before EndTooltip() - SetWindowPos still applies for this frame's render.
        /// </summary>
        private static void ClampTooltipToDisplay()
        {
            var pos = ImGuiNET.ImGui.GetWindowPos();
            var newPos = ClampPosToDisplay(pos, ImGuiNET.ImGui.GetWindowSize());
            if (newPos != pos)
                ImGuiNET.ImGui.SetWindowPos(newPos, ImGuiCond.Always);
        }

        /// <summary>Clamps a desired top-left position so a box of <paramref name="size"/> fits
        /// fully within the current ImGui context's DisplaySize.</summary>
        private static Vector2 ClampPosToDisplay(Vector2 desiredTopLeft, Vector2 size)
        {
            var   io  = ImGuiNET.ImGui.GetIO();
            const float pad = 4f;
            float x = Math.Min(desiredTopLeft.X, io.DisplaySize.X - size.X - pad);
            float y = Math.Min(desiredTopLeft.Y, io.DisplaySize.Y - size.Y - pad);
            x = Math.Max(x, pad);
            y = Math.Max(y, pad);
            return new Vector2(x, y);
        }

        public static void TooltipForBounds(Vector2 tl, Vector2 br, string text)
        {
            _lastRowTL = tl;
            _lastRowBR = br;
            Tooltip(text);
        }

        /// <summary>
        /// Wrap width for tooltip text: normally 280px, but capped to whatever actually
        /// fits the current ImGui context's DisplaySize minus tooltip padding/offset - a
        /// small popped-out widget's own OS window can be far narrower than 280px, and no
        /// amount of repositioning (ClampTooltipToDisplay) can make an already-too-wide
        /// tooltip box fit inside a window narrower than the box itself.
        /// </summary>
        private static float SafeTooltipWrapWidth()
            => Math.Max(60f, Math.Min(280f, ImGuiNET.ImGui.GetIO().DisplaySize.X - 32f));

        public static void Tooltip(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (IsDisabled) return;
            // Every UI.Tooltip(string) call site across every panel passes a raw literal -
            // localizing once here makes all of them translation-friendly for free.
            text = LocManager.Get(text);
            // Suppress hint if a disabled-reason tooltip already fired for this same row this
            // frame - prevents both tooltips from showing when UI.Tooltip() is called outside
            // the BeginDisabled/EndDisabled block (a common pattern in panels).
            uint curFrame = (uint)ImGuiNET.ImGui.GetFrameCount();
            if (_disabledTooltipLastFrame == curFrame)
            {
                uint rowKey = unchecked((uint)(_lastRowTL.X.GetHashCode() * 397 ^ _lastRowTL.Y.GetHashCode()));
                if (_disabledTooltipRowKey == rowKey) return;
            }
            if (ImGuiNET.ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            {
                // Only show tooltip when the item is actually hovered in the popup context.
                // IsItemHovered() is popup-aware and returns false for items blocked by a
                // popup sitting on top of them, preventing tooltips from bleeding through.
                if (ImGuiNET.ImGui.IsItemHovered())
                    TooltipInPopup(text, _lastRowTL, _lastRowBR);
                return;
            }
            // Compute the box size up front (text is known) so the tooltip can be
            // correctly positioned on its very first visible frame via SetNextWindowPos -
            // rather than relying on a same-frame SetWindowPos repositioning trick after
            // Begin, which is what left tooltips still clipping against a narrow popped-out
            // widget's own OS window edge even after wrap-width was capped.
            float wrapWidth = SafeTooltipWrapWidth();
            Vector2 textSize = ImGuiNET.ImGui.CalcTextSize(text, false, wrapWidth);
            Vector2 boxSize  = textSize + new Vector2(20f, 16f); // WindowPadding (10,8) each side

            Tooltip(() =>
            {
                ImGuiNET.ImGui.PushTextWrapPos(wrapWidth);
                ImGuiNET.ImGui.TextUnformatted(text);
                ImGuiNET.ImGui.PopTextWrapPos();
            }, boxSize);
        }

        /// <summary>
        /// Same themed tooltip as <see cref="Tooltip(string)"/>, but appends a second line
        /// in risky-orange below the normal hint - used for <c>risky</c> controls that pass a
        /// <c>riskyWarning</c> - and optionally a third
        /// line in danger-red for <c>extraRiskyWarning</c>.
        /// Any of the three may be null.
        /// </summary>
        // riskyWarning/extraRiskyWarning arrive already localized - callers (Toggle,
        // ToggleWithPopover) run them through Loc() themselves before this is invoked (they
        // need the translated text early, to decide layout and label color). hint does not,
        // so it's localized here - same as the plain Tooltip(string) path this delegates to
        // when there's no warning at all.
        private static void TooltipWithRiskyWarning(string hint, string riskyWarning, string extraRiskyWarning = null)
        {
            bool hasHint  = !string.IsNullOrEmpty(hint);
            bool hasWarn  = !string.IsNullOrEmpty(riskyWarning);
            bool hasExtra = !string.IsNullOrEmpty(extraRiskyWarning);
            if (!hasHint && !hasWarn && !hasExtra) return;
            if (!hasWarn && !hasExtra) { Tooltip(hint); return; }

            hint = LocManager.Get(hint);

            float   wrapWidth = SafeTooltipWrapWidth();
            Vector2 hintSize  = hasHint  ? ImGuiNET.ImGui.CalcTextSize(hint, false, wrapWidth) : Vector2.Zero;
            Vector2 warnSize  = hasWarn  ? ImGuiNET.ImGui.CalcTextSize(riskyWarning, false, wrapWidth) : Vector2.Zero;
            Vector2 extraSize = hasExtra ? ImGuiNET.ImGui.CalcTextSize(extraRiskyWarning, false, wrapWidth) : Vector2.Zero;
            float   itemGap   = ImGuiNET.ImGui.GetStyle().ItemSpacing.Y;
            float   gap1      = hasHint ? itemGap : 0f;
            float   gap2      = (hasHint || hasWarn) && hasExtra ? itemGap : 0f;
            Vector2 boxSize   = new Vector2(MathF.Max(hintSize.X, MathF.Max(warnSize.X, extraSize.X)),
                                             hintSize.Y + gap1 + warnSize.Y + gap2 + extraSize.Y)
                                + new Vector2(20f, 16f); // WindowPadding (10,8) each side

            Tooltip(() =>
            {
                ImGuiNET.ImGui.PushTextWrapPos(wrapWidth);
                if (hasHint) ImGuiNET.ImGui.TextUnformatted(hint);
                if (hasWarn)
                {
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, ToV4(ImGuiStyle.Col(0xFF, 0xAA, 0x22)));
                    ImGuiNET.ImGui.TextUnformatted(riskyWarning);
                    ImGuiNET.ImGui.PopStyleColor();
                }
                if (hasExtra)
                {
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, ToV4(ImGuiStyle.Danger));
                    ImGuiNET.ImGui.TextUnformatted(extraRiskyWarning);
                    ImGuiNET.ImGui.PopStyleColor();
                }
                ImGuiNET.ImGui.PopTextWrapPos();
            }, boxSize);
        }

        // Tooltip variant for use inside popup windows (e.g. TagCombo dropdown).
        // Tooltip() suppresses itself when any popup is open; this bypasses that check
        // while using identical styling to ShowStyledTooltip + TooltipDelay.
        // textColor: when non-zero, overrides the default TxtNorm text colour - pass
        // Alpha(TxtDim, 230) for disabled-reason tooltips shown inside popup windows.
        private static void TooltipInPopup(string text, Vector2 rowTL, Vector2 rowBR, uint textColor = 0)
        {
            if (string.IsNullOrEmpty(text)) return;
            var   io   = ImGuiNET.ImGui.GetIO();
            var   mp   = io.MousePos;
            bool  over = mp.X >= rowTL.X && mp.X <= rowBR.X && mp.Y >= rowTL.Y && mp.Y <= rowBR.Y;
            uint  key  = unchecked((uint)(rowTL.X.GetHashCode() * 397 ^ rowTL.Y.GetHashCode()));
            _tooltipHover.TryGetValue(key, out float hovered);
            hovered = over ? hovered + io.DeltaTime : 0f;
            if (hovered <= 0f) _tooltipHover.Remove(key);
            else               _tooltipHover[key] = hovered;
            if (!over || (!LowPerf && hovered < TooltipDelay)) return;
            float animT = LowPerf ? 1f : Math.Clamp((hovered - TooltipDelay) / TooltipFadeIn, 0f, 1f);

            // Compute box size up front (same rationale as Tooltip(string)) so this
            // positions correctly on its first visible frame regardless of host window size.
            float   wrapWidth = SafeTooltipWrapWidth();
            Vector2 textSize  = ImGuiNET.ImGui.CalcTextSize(text, false, wrapWidth);
            Vector2 boxSize   = textSize + new Vector2(20f, 16f);
            Vector2 pos = ClampPosToDisplay(new Vector2(mp.X + 14f, mp.Y + 14f), boxSize);

            ImGuiNET.ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.Alpha,         animT);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.TooltipBg, 250)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(ImGuiStyle.Border, 160)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,    ToV4(textColor != 0 ? textColor : Alpha(TxtNorm, 230)));
            if (ImGuiNET.ImGui.BeginTooltip())
            {
                ImGuiNET.ImGui.PushTextWrapPos(wrapWidth);
                ImGuiNET.ImGui.TextUnformatted(text);
                ImGuiNET.ImGui.PopTextWrapPos();

                if (!LowPerf)
                {
                    var  tdl = ImGuiNET.ImGui.GetWindowDrawList();
                    var  twp = ImGuiNET.ImGui.GetWindowPos();
                    var  tws = ImGuiNET.ImGui.GetWindowSize();
                    tdl.AddRectFilled(
                        new Vector2(twp.X + 2f, twp.Y + 1f),
                        new Vector2(twp.X + tws.X - 2f, twp.Y + 2.5f),
                        Alpha(Accent, 180), 4f, ImDrawFlags.RoundCornersTop);
                }
                ImGuiNET.ImGui.EndTooltip();
            }
            ImGuiNET.ImGui.PopStyleColor(3);
            ImGuiNET.ImGui.PopStyleVar(3);
        }

        /// <summary>
        /// Shows a themed tooltip with custom content after hovering the last control for
        /// <see cref="TooltipDelay"/> seconds. Call immediately after any UI control.
        /// </summary>
        public static void Tooltip(Action content) { Tooltip(content, null); }

        /// <summary>
        /// Core tooltip implementation. When <paramref name="estimatedSize"/> is known up
        /// front (the text-based <see cref="Tooltip(string)"/> overload always knows it -
        /// see <see cref="ClampTooltipToDisplay"/>'s doc comment for why that matters for
        /// popped-out widgets), the tooltip is positioned correctly via SetNextWindowPos
        /// before Begin, so it never has to visually "jump" or risk a same-frame
        /// SetWindowPos reposition not taking effect. For arbitrary custom content whose
        /// size can't be known ahead of time, falls back to positioning at the mouse and
        /// re-clamping after the content has been measured.
        /// </summary>
        private static bool Tooltip(Action content, Vector2? estimatedSize)
        {
            if (content == null) return false;
            // Suppress tooltips when any popup/modal is open - they render in the foreground
            // draw list and would otherwise bleed through on top of modal windows.
            if (ImGuiNET.ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return false;
            var   io   = ImGuiNET.ImGui.GetIO();
            var   mp   = io.MousePos;
            // IsWindowHovered() returns false when another window sits on top of this one
            // under the cursor, preventing tooltips from bleeding through overlapping panels.
            bool  over = ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) &&
                         mp.X >= _lastRowTL.X && mp.X <= _lastRowBR.X &&
                         mp.Y >= _lastRowTL.Y && mp.Y <= _lastRowBR.Y;

            // Per-row hover accumulator - keyed by screen position so it resets on scroll
            uint key = unchecked((uint)(_lastRowTL.X.GetHashCode() * 397 ^ _lastRowTL.Y.GetHashCode()));
            _tooltipHover.TryGetValue(key, out float hovered);
            hovered = over ? hovered + io.DeltaTime : 0f;
            // Remove entries when hover resets to 0 to prevent accumulation as panels scroll.
            if (hovered <= 0f) _tooltipHover.Remove(key);
            else               _tooltipHover[key] = hovered;

            if (!over || (!LowPerf && hovered < TooltipDelay)) return false;

            // Fade-in: 0→1 over TooltipFadeIn seconds. LowPerf skips animation (instant full alpha).
            float animT = LowPerf ? 1f
                : Math.Clamp((hovered - TooltipDelay) / TooltipFadeIn, 0f, 1f);

            Vector2 desiredPos = new(mp.X + 14f, mp.Y + 14f);
            if (estimatedSize.HasValue)
                desiredPos = ClampPosToDisplay(desiredPos, estimatedSize.Value);

            ImGuiNET.ImGui.SetNextWindowPos(desiredPos, ImGuiCond.Always);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.Alpha,         animT);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.TooltipBg, 250)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(ImGuiStyle.Border, 160)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,    ToV4(Alpha(TxtNorm, 230)));

            // BeginTooltip returns bool in ImGui 1.90+ - only draw content and EndTooltip when open
            if (ImGuiNET.ImGui.BeginTooltip())
            {
                content();
                // Fallback re-clamp for content whose size wasn't known up front. For the
                // text-tooltip path (estimatedSize supplied) this is a no-op - position was
                // already correct going into Begin.
                if (!estimatedSize.HasValue)
                    ClampTooltipToDisplay();

                // Accent top strip - drawn AFTER content/clamp so it always matches the
                // tooltip's FINAL position/size, not a stale pre-content/pre-clamp one.
                if (!LowPerf)
                {
                    var  tdl = ImGuiNET.ImGui.GetWindowDrawList();
                    var  twp = ImGuiNET.ImGui.GetWindowPos();
                    var  tws = ImGuiNET.ImGui.GetWindowSize();
                    tdl.AddRectFilled(
                        new Vector2(twp.X + 2f, twp.Y + 1f),
                        new Vector2(twp.X + tws.X - 2f, twp.Y + 2.5f),
                        Alpha(Accent, 180), 4f, ImDrawFlags.RoundCornersTop);
                }

                ImGuiNET.ImGui.EndTooltip();
            }

            ImGuiNET.ImGui.PopStyleColor(3);
            ImGuiNET.ImGui.PopStyleVar(3);   // Alpha + WindowPadding + PopupRounding
            return true;
        }

        // Same themed shell as Tooltip(Action) but fires immediately - no hover delay.
        // Used for transient UI-driven tooltips (e.g. hotkey badge hints).
        private static void ShowStyledTooltip(Action content)
        {
            if (ImGuiNET.ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;
            var mp = ImGuiNET.ImGui.GetIO().MousePos;
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(mp.X + 14f, mp.Y + 14f), ImGuiCond.Always);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.Alpha,         1f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.TooltipBg, 250)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(ImGuiStyle.Border, 160)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,    ToV4(Alpha(TxtNorm, 230)));
            if (ImGuiNET.ImGui.BeginTooltip())
            {
                if (!LowPerf)
                {
                    var tdl = ImGuiNET.ImGui.GetWindowDrawList();
                    var twp = ImGuiNET.ImGui.GetWindowPos();
                    var tws = ImGuiNET.ImGui.GetWindowSize();
                    tdl.AddRectFilled(
                        new Vector2(twp.X + 2f, twp.Y + 1f),
                        new Vector2(twp.X + tws.X - 2f, twp.Y + 2.5f),
                        Alpha(Accent, 180), 4f, ImDrawFlags.RoundCornersTop);
                }
                content();
                ClampTooltipToDisplay();
                ImGuiNET.ImGui.EndTooltip();
            }
            ImGuiNET.ImGui.PopStyleColor(3);
            ImGuiNET.ImGui.PopStyleVar(3);
        }

        // Same as ShowStyledTooltip but skips the IsPopupOpen guard, so it works
        // correctly when called from within a popup window (e.g. ToggleWithPopover sub-hotkeys).
        private static void ShowStyledTooltipInPopup(Action content)
        {
            var mp = ImGuiNET.ImGui.GetIO().MousePos;
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(mp.X + 14f, mp.Y + 14f), ImGuiCond.Always);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.Alpha,         1f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.TooltipBg, 250)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(ImGuiStyle.Border, 160)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,    ToV4(Alpha(TxtNorm, 230)));
            if (ImGuiNET.ImGui.BeginTooltip())
            {
                if (!LowPerf)
                {
                    var tdl = ImGuiNET.ImGui.GetWindowDrawList();
                    var twp = ImGuiNET.ImGui.GetWindowPos();
                    var tws = ImGuiNET.ImGui.GetWindowSize();
                    tdl.AddRectFilled(
                        new Vector2(twp.X + 2f, twp.Y + 1f),
                        new Vector2(twp.X + tws.X - 2f, twp.Y + 2.5f),
                        Alpha(Accent, 180), 4f, ImDrawFlags.RoundCornersTop);
                }
                content();
                ClampTooltipToDisplay();
                ImGuiNET.ImGui.EndTooltip();
            }
            ImGuiNET.ImGui.PopStyleColor(3);
            ImGuiNET.ImGui.PopStyleVar(3);
        }

        /// <summary>
        /// Registers the last ImGui item's rect as tooltip bounds so UI.Tooltip() works
        /// after raw ImGui controls (Button, Checkbox, Dummy, InvisibleButton, etc.).
        /// Call immediately after the raw control, before UI.Tooltip().
        /// </summary>
        public static void RegisterLastItemForTooltip()
        {
            _lastRowTL = ImGuiNET.ImGui.GetItemRectMin();
            _lastRowBR = ImGuiNET.ImGui.GetItemRectMax();
        }

        // ── Context Menu ──────────────────────────────────────────────────────────
        // Universal helpers for custom themed context menus - keeps style consistent
        // and fixes the row-highlight gap caused by WindowPadding + corner rounding.

        /// <summary>
        /// Push the standard themed context-menu style (dark bg, zero padding, themed border).
        /// Also calls SetNextWindowSizeConstraints. Follow with BeginPopup, then
        /// ContextMenuRow calls, EndPopup, and PopContextMenuStyle.
        /// </summary>
        public static void PushContextMenuStyle(float minWidth = 130f, float maxWidth = 280f)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.ContextMenuBg, 252)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(BorderCol, 180)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,  new Vector2(0f, 4f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,  6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,    new Vector2(0f, 1f));
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(
                new Vector2(minWidth, 0f), new Vector2(maxWidth, 400f));
        }

        /// <summary>Pop the style pushed by PushContextMenuStyle.</summary>
        public static void PopContextMenuStyle()
        {
            ImGuiNET.ImGui.PopStyleVar(3);
            ImGuiNET.ImGui.PopStyleColor(2);
        }

        /// <summary>
        /// Draw one row inside a custom context-menu popup.
        /// Returns true if clicked (caller should close the popup and act on the selection).
        /// </summary>
        public static bool ContextMenuRow(string id, string label, bool enabled = true)
        {
            label = LocManager.Get(label);
            var      fnt    = ImGuiStyle.FontNormal;
            float    fs     = fnt.FontSize > 0f ? fnt.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float    rh     = fs + 10f;
            float    textW  = fnt.CalcTextSizeA(fs, float.MaxValue, -1f, label).X;
            float    minW   = textW + 24f; // 12px padding each side
            float    w      = Math.Max(ImGuiNET.ImGui.GetContentRegionAvail().X, minW);
            Vector2  tl     = ImGuiNET.ImGui.GetCursorScreenPos();

            ImGuiNET.ImGui.InvisibleButton(id, new Vector2(w, rh));
            bool hov = ImGuiNET.ImGui.IsItemHovered() && enabled;
            bool clk = ImGuiNET.ImGui.IsItemClicked() && enabled;

            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            if (hov)
                dl.AddRectFilled(tl, new Vector2(tl.X + w, tl.Y + rh), Alpha(Accent, 28), 0f);

            uint tc = !enabled ? Alpha(TxtDim, 90) : hov ? 0xFFFFFFFF : TxtNorm;
            dl.AddText(fnt, fs, new Vector2(tl.X + 12f, tl.Y + (rh - fs) * 0.5f), tc, label);
            return clk;
        }

        // ── Context Menu ──────────────────────────────────────────────────────────

        // ───────────────────────────────────────────────────────────────────────────
        // Helpers
        // ───────────────────────────────────────────────────────────────────────────

        // ─────────────────────────────────────────────────────────────────────────
        // EMPTY STATE  - centered placeholder when a list or section has no content
        //
        //   UI.Empty("No loot matches the current filter.");
        //
        public static void Empty(string message = "No items", float height = 64f)
        {
            message = LocManager.Get(message);
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl  = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.Dummy(new Vector2(avail, height));

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            var   dl    = DL;
            float cx    = tl.X + avail * 0.5f;
            float cy    = tl.Y + height * 0.5f;

            // Dashed circle placeholder icon
            float r = 14f;
            int   segs = 16;
            for (int i = 0; i < segs; i += 2)
            {
                float a0 = i       * MathF.PI * 2f / segs;
                float a1 = (i + 1) * MathF.PI * 2f / segs;
                dl.AddLine(
                    new Vector2(cx + r * MathF.Cos(a0), cy + r * MathF.Sin(a0)),
                    new Vector2(cx + r * MathF.Cos(a1), cy + r * MathF.Sin(a1)),
                    DA(Alpha(TxtDim, 60)), 1.5f);
            }
            dl.AddLine(new Vector2(cx - r * 0.55f, cy), new Vector2(cx + r * 0.55f, cy),
                DA(Alpha(TxtDim, 60)), 1.5f);

            if (!string.IsNullOrEmpty(message))
            {
                Vector2 ts = MeasureText(font, fsize * 0.88f, message);
                dl.AddText(font, fsize * 0.88f,
                    new Vector2(cx - ts.X * 0.5f, cy + r + 6f),
                    DA(Alpha(TxtDim, 140)), message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // LOADING STATE  - animated spinner with optional label
        //
        //   UI.Loading();
        //   UI.Loading("Fetching data...");
        //
        public static void Loading(string message = "Loading...", float height = 64f)
        {
            message = LocManager.Get(message);
            float avail = _groupAvailW > 0f ? GroupInnerWidth : ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl  = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.Dummy(new Vector2(avail, height));

            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            var   dl    = DL;
            float cx    = tl.X + avail * 0.5f;
            float cy    = tl.Y + height * 0.5f;
            float r     = 10f;

            // Faint track
            dl.AddCircle(new Vector2(cx, cy), r, DA(Alpha(BorderCol, 55)), 32, 1.5f);

            // Rotating arc - static in LowPerf (no per-frame GetTime sampling)
            if (LowPerf)
            {
                dl.PathArcTo(new Vector2(cx, cy), r, 0f, MathF.PI * 1.5f, 24);
                dl.PathStroke(DA(Alpha(Accent, 160)), ImDrawFlags.None, 2f);
            }
            else
            {
                float t     = (float)(ImGuiNET.ImGui.GetTime() % 1.0);
                float start = t * MathF.PI * 2f;
                dl.PathArcTo(new Vector2(cx, cy), r, start, start + MathF.PI * 1.5f, 24);
                dl.PathStroke(DA(Alpha(Accent, 200)), ImDrawFlags.None, 2f);
            }

            if (!string.IsNullOrEmpty(message))
            {
                Vector2 ts = MeasureText(font, fsize * 0.88f, message);
                dl.AddText(font, fsize * 0.88f,
                    new Vector2(cx - ts.X * 0.5f, cy + r + 6f),
                    DA(Alpha(TxtDim, 160)), message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CONTEXT MENU  - right-click popup anchored to the last rendered control.
        //
        // Usage:
        //   UI.SomeControl(...);
        //   if (UI.BeginContextMenu("##ctx_mycontrol"))
        //   {
        //       if (UI.MenuItem("Copy"))  { ... }
        //       if (UI.MenuItem("Paste")) { ... }
        //       UI.MenuSeparator();
        //       if (UI.MenuItem("Delete", enabled: canDelete)) { ... }
        //       UI.EndContextMenu();
        //   }
        //
        private const float MenuItemH    = 24f;
        private const float MenuItemPadX = 10f;

        public static bool BeginContextMenu(string id, Vector2 tl, Vector2 br)
        {
            var mp   = ImGuiNET.ImGui.GetIO().MousePos;
            bool over = mp.X >= tl.X && mp.X <= br.X && mp.Y >= tl.Y && mp.Y <= br.Y;

            if (over && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                ImGuiNET.ImGui.OpenPopup(id);

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg,    ToV4(Alpha(ImGuiStyle.ContextMenuBg, 252)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,     ToV4(Alpha(BorderCol, 180)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,   new Vector2(0f, 4f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,   6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,     new Vector2(0f, 1f));
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(
                new Vector2(160f, 0f), new Vector2(400f, float.MaxValue));

            bool open = ImGuiNET.ImGui.BeginPopup(id);
            if (!open)
            {
                ImGuiNET.ImGui.PopStyleVar(3);
                ImGuiNET.ImGui.PopStyleColor(2);
            }
            return open;
        }

        public static bool BeginContextMenu(string id)
        {
            var mp   = ImGuiNET.ImGui.GetIO().MousePos;
            bool over = mp.X >= _lastRowTL.X && mp.X <= _lastRowBR.X &&
                        mp.Y >= _lastRowTL.Y && mp.Y <= _lastRowBR.Y;

            if (over && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                ImGuiNET.ImGui.OpenPopup(id);

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg,    ToV4(Alpha(ImGuiStyle.ContextMenuBg, 252)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,     ToV4(Alpha(BorderCol, 180)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,   new Vector2(0f, 4f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,   6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,     new Vector2(0f, 1f));

            // Enforce a minimum width so the popup never collapses to a sliver.
            // MenuItem sizes off GetContentRegionAvail().X which is circular on an
            // auto-sized popup - without a floor, the window stays at ~0px wide.
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(
                new Vector2(160f, 0f), new Vector2(400f, float.MaxValue));

            bool open = ImGuiNET.ImGui.BeginPopup(id);
            if (!open)
            {
                ImGuiNET.ImGui.PopStyleVar(3);
                ImGuiNET.ImGui.PopStyleColor(2);
            }
            return open;
        }

        public static bool MenuItem(string label, bool enabled = true, bool selected = false, string hint = null)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float rowW  = ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 tl  = ImGuiNET.ImGui.GetCursorScreenPos();

            if (!enabled) ImGuiNET.ImGui.BeginDisabled();

            ImGuiNET.ImGui.InvisibleButton("##mi_" + label, new Vector2(rowW, MenuItemH));
            bool hov = !IsDisabled && ImGuiNET.ImGui.IsItemHovered();
            bool clk = !IsDisabled && ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();
            float t  = Anim(id, hov ? 1f : 0f, 14f);

            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            Vector2 br = new(tl.X + rowW, tl.Y + MenuItemH);

            if (t > 0.01f)
                dl.AddRectFilled(tl, br, Alpha(Accent, (byte)(38 * t)), 4f);

            if (selected)
                dl.AddRectFilled(tl, new Vector2(tl.X + 3f, br.Y), Accent, 2f);

            uint lCol = enabled
                ? DA(Lerp(TxtNorm, 0xFFFFFFFF, t))
                : DA(Alpha(TxtDim, 140));
            dl.AddText(font, fsize,
                new Vector2(tl.X + MenuItemPadX + (selected ? 6f : 0f), tl.Y + (MenuItemH - fsize) * .5f),
                lCol, Loc(label));

            if (!enabled) ImGuiNET.ImGui.EndDisabled();
            if (hint != null && hov)
            {
                _lastRowTL = tl;
                _lastRowBR = br;
                Tooltip(hint);
            }
            if (clk) ImGuiNET.ImGui.CloseCurrentPopup();
            return clk;
        }

        public static void MenuSeparator()
        {
            Vector2 pos = ImGuiNET.ImGui.GetCursorScreenPos();
            float   w   = ImGuiNET.ImGui.GetContentRegionAvail().X;
            ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(pos.X + MenuItemPadX, pos.Y + 3f),
                new Vector2(pos.X + w - MenuItemPadX, pos.Y + 4f),
                Alpha(BorderCol, 90));
            ImGuiNET.ImGui.Dummy(new Vector2(w, 7f));
        }

        public static void EndContextMenu()
        {
            ImGuiNET.ImGui.EndPopup();
            ImGuiNET.ImGui.PopStyleVar(3);
            ImGuiNET.ImGui.PopStyleColor(2);
        }

        // ── Dropdown button menu ──────────────────────────────────────────────────
        // Like BeginContextMenu but triggered by left-click on a button, not
        // right-click on a row.  Caller opens the popup via ImGui.OpenPopup(id)
        // before calling this, then passes the screen-space anchor point (usually
        // the bottom-left of the trigger button) so the popup appears below it.
        //
        // Usage:
        //   if (ImGui.Button("Menu")) ImGui.OpenPopup("##myDrop");
        //   Vector2 anchor = new(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);
        //   if (UI.BeginDropdownMenu("##myDrop", anchor))
        //   {
        //       if (UI.MenuItem("Item", selected: isOn)) isOn = !isOn;
        //       UI.MenuSeparator();
        //       if (UI.MenuItem("Other")) { ... }
        //       UI.EndDropdownMenu();
        //   }

        private static int    _dropdownColumns = 1;
        private static string _dropdownId      = null;

        // Optional columns > 1 lays items out in a multi-column grid inside the
        // popup. Caller must call ImGui.NextColumn() after each MenuItem.
        public static bool BeginDropdownMenu(string id, Vector2 anchor, int columns = 1)
        {
            var   ddDisp = ImGuiNET.ImGui.GetIO().DisplaySize;
            _popupSizes.TryGetValue(id, out Vector2 ddLastSz);
            float ddEstW = ddLastSz.X > 0f ? ddLastSz.X : (columns > 1 ? columns * 100f : 200f);
            float ddEstH = ddLastSz.Y > 0f ? ddLastSz.Y : 160f;
            Vector2 ddPos = new(
                Math.Clamp(anchor.X, 0f, Math.Max(0f, ddDisp.X - ddEstW)),
                anchor.Y + ddEstH > ddDisp.Y ? anchor.Y - ddEstH : anchor.Y);
            ImGuiNET.ImGui.SetNextWindowPos(ddPos, ImGuiCond.Always);
            float minW = columns > 1 ? columns * 100f : 160f;
            float maxW = columns > 1 ? minW           : 400f;
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(
                new Vector2(minW, 0f), new Vector2(maxW, float.MaxValue));

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ToV4(Alpha(ImGuiStyle.ContextMenuBg, 252)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  ToV4(Alpha(BorderCol, 180)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(0f, 1f));

            bool open = ImGuiNET.ImGui.BeginPopup(id, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize);
            if (!open)
            {
                ImGuiNET.ImGui.PopStyleVar(3);
                ImGuiNET.ImGui.PopStyleColor(2);
                _dropdownColumns = 1;
            }
            else
            {
                _dropdownColumns = columns;
                _dropdownId      = id;
                if (columns > 1)
                    ImGuiNET.ImGui.Columns(columns, $"##dc_{id}", false);
            }
            return open;
        }

        public static void EndDropdownMenu()
        {
            if (_dropdownColumns > 1)
                ImGuiNET.ImGui.Columns(1);
            if (_dropdownId != null)
            {
                _popupSizes[_dropdownId] = ImGuiNET.ImGui.GetWindowSize();
                _dropdownId = null;
            }
            _dropdownColumns = 1;
            ImGuiNET.ImGui.EndPopup();
            ImGuiNET.ImGui.PopStyleVar(3);
            ImGuiNET.ImGui.PopStyleColor(2);
        }

        // Convert packed ABGR uint to Vector4 RGBA for ImGui PushStyleColor
        public static System.Numerics.Vector4 ToV4(uint c) =>
            new((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f,
                ((c >> 16) & 0xFF) / 255f, ((c >> 24) & 0xFF) / 255f);

        // Strip ImGui ##id suffix for display
        private static string Strip(string label)
        {
            int i = label.IndexOf("##", StringComparison.Ordinal);
            return i >= 0 ? label[..i] : label;
        }

        // Strip ##id suffix, then run through LocManager so the key is registered
        // under the current panel scope and the translated text is returned.
        private static string Loc(string label)
        {
            string clean = Strip(label);
            return string.IsNullOrEmpty(clean) ? clean : LocManager.Get(clean);
        }

        // Measure text using the font that will actually render it.
        // ImGui.CalcTextSize() always uses the current (default) font which can differ from
        // FontSmall / FontBold, causing misaligned centering and badge sizing.
        private static Vector2 MeasureText(ImFontPtr font, float fontSize, string text)
        {
            if (font.FontSize > 0f)
                return font.CalcTextSizeA(fontSize, float.MaxValue, 0f, text);
            return ImGuiNET.ImGui.CalcTextSize(text);
        }

        /// <summary>
        /// Case-insensitive search match with ranking, best (lowest) tier across every
        /// word/token in <paramref name="haystack"/> (tokens split on any non-alphanumeric
        /// character):
        ///   0 = needle exactly equals the FIRST word ("TerraGroup" search "terragroup" in
        ///       "TerraGroup Blue Folders")
        ///   1 = the FIRST word STARTS WITH needle ("TerraGroup Blue Folders" for search "terra")
        ///   2 = needle exactly equals some OTHER (non-first) word ("Order from TerraGroup..."
        ///       for search "terragroup")
        ///   3 = some OTHER (non-first) word STARTS WITH needle ("Mystery Ranch Terraframe..."
        ///       for search "terra")
        ///   4 = needle only appears as a substring not aligned to any word's start (e.g. "ox"
        ///       inside "Toxic Waste" - the "ox" starts mid-word, not at a token boundary)
        /// Returns false (rank unset) when there's no match at all. A word being an exact
        /// match beats it merely starting with the needle, and "first word" always beats
        /// "some other word" - so a short, generic query like "terra" still puts every item
        /// that actually STARTS with "Terra..." ahead of ones where "terra" only shows up
        /// buried later in the name.
        /// </summary>
        public static bool TryRankSearchMatch(string haystack, string needle, out int rank)
        {
            rank = 0;
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) return false;

            int  best        = int.MaxValue;
            bool isFirstToken = true;
            int  start        = -1;
            for (int i = 0; i <= haystack.Length; i++)
            {
                bool isWordChar = i < haystack.Length && char.IsLetterOrDigit(haystack[i]);
                if (isWordChar)
                {
                    if (start < 0) start = i;
                    continue;
                }
                if (start >= 0)
                {
                    int len = i - start;
                    if (len >= needle.Length &&
                        string.Compare(haystack, start, needle, 0, needle.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        bool exact = len == needle.Length;
                        int  tier  = isFirstToken ? (exact ? 0 : 1) : (exact ? 2 : 3);
                        if (tier < best) best = tier;
                        if (best == 0) { rank = 0; return true; } // unbeatable - stop early
                    }
                    isFirstToken = false;
                    start = -1;
                }
            }
            rank = best == int.MaxValue ? 4 : best;
            return true;
        }

        private static string TruncateText(ImFontPtr font, float fontSize, string text, float maxW)
        {
            if (MeasureText(font, fontSize, text).X <= maxW) return text;
            const string Dots = "...";
            float dotsW = MeasureText(font, fontSize, Dots).X;
            if (dotsW >= maxW) return Dots;
            float remaining = maxW - dotsW;
            int lo = 0, hi = text.Length;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (MeasureText(font, fontSize, text[..mid]).X <= remaining) lo = mid;
                else hi = mid;
            }
            return text[..lo] + Dots;
        }

        // Short label for the hotkey badge chip (abbreviates long names to fit).
        internal static string KeyBadgeLabel(int vk) => vk switch
        {
            0x01 => "M1", 0x02 => "M2", 0x04 => "M3", 0x05 => "M4", 0x06 => "M5",
            0x08 => "Bsp", 0x09 => "Tab", 0x0D => "Ent", 0x20 => "Spc",
            0x21 => "PgU", 0x22 => "PgD", 0x23 => "End", 0x24 => "Hm",
            0x25 => "←",  0x26 => "↑",  0x27 => "→",  0x28 => "↓",
            0x2D => "Ins", 0x2E => "Del",
            _ => KeyDisplayName(vk)
        };

        // Converts a Windows virtual-key code to a short display string.
        public static string KeyDisplayName(int vk)
        {
            if (vk >= 0x01 && vk <= 0x06)
            {
                return vk switch
                {
                    0x01 => "Mouse1", 0x02 => "Mouse2", 0x04 => "Mouse3",
                    0x05 => "Mouse4", 0x06 => "Mouse5",
                    _ => $"Mouse{vk}"
                };
            }
            return vk switch
            {
                0x08 => "Backspace", 0x09 => "Tab",     0x0D => "Enter",  0x1B => "Escape",
                0x20 => "Space",     0x21 => "PageUp",  0x22 => "PageDn", 0x23 => "End",
                0x24 => "Home",      0x25 => "Left",    0x26 => "Up",     0x27 => "Right",
                0x28 => "Down",      0x2D => "Ins",     0x2E => "Del",
                >= 0x30 and <= 0x39 => ((char)vk).ToString(),
                >= 0x41 and <= 0x5A => ((char)vk).ToString(),
                >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
                >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
                0xA0 => "LShift", 0xA1 => "RShift", 0xA2 => "LCtrl", 0xA3 => "RCtrl",
                0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
                0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
                _ => $"K{vk:X2}"
            };
        }

        private static string FormatValue(float v, string fmt)
        {
            try
            {
                int dot = fmt.IndexOf('.');
                if (dot >= 0 && fmt.Length > dot + 1)
                {
                    if (int.TryParse(fmt.AsSpan(dot + 1, fmt.Length - dot - 2), out int prec))
                        return v.ToString("F" + prec, System.Globalization.CultureInfo.InvariantCulture);
                }
                return v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return v.ToString(); }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CONFIG-BINDING OVERLOADS
        //
        // Value-based (non-ref) versions of the core controls. Accept an onChange
        // callback instead of a ref parameter so they work directly with config
        // properties (which cannot be passed by ref):
        //
        //   UI.Toggle("Show Creep", Config.ShowCreep, v => { Config.ShowCreep = v; Config.SaveDebounced(); });
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Value-based Toggle that calls <paramref name="onChange"/> when clicked.</summary>
        public static bool Toggle(string label, bool value, Action<bool> onChange,
            float width = 0f, bool risky = false, string hint = null, HotkeyEntry hotkey = null, string riskyWarning = null,
            string extraRiskyWarning = null)
        {
            bool v = value;
            bool changed = Toggle(label, ref v, width, risky, hint, hotkey, riskyWarning, extraRiskyWarning);
            if (changed) onChange?.Invoke(v);
            return changed;
        }

        /// <summary>Value-based SliderFloat that calls <paramref name="onChange"/> when changed.</summary>
        public static bool SliderFloat(string label, float value, float min, float max,
            Action<float> onChange,
            string fmt = "%.1f", string suffix = "", string minText = "", string maxText = "",
            float width = 0f, float smallChange = 0f)
        {
            float v = value;
            bool changed = SliderFloat(label, ref v, min, max, fmt, suffix, minText, maxText, width, smallChange);
            if (changed) onChange?.Invoke(v);
            return changed;
        }

        /// <summary>Value-based SliderInt that calls <paramref name="onChange"/> when changed.</summary>
        public static bool SliderInt(string label, int value, int min, int max,
            Action<int> onChange,
            string suffix = "", string minText = "", string maxText = "",
            float width = 0f, int smallChange = 1, bool abbreviated = false)
        {
            int v = value;
            bool changed = SliderInt(label, ref v, min, max, suffix, minText, maxText, width, smallChange, abbreviated);
            if (changed) onChange?.Invoke(v);
            return changed;
        }

        /// <summary>Value-based Combo that calls <paramref name="onChange"/> when selection changes.</summary>
        public static bool Combo(string label, int selectedIndex, string[] items,
            Action<int> onChange, float width = 0f)
        {
            int v = selectedIndex;
            bool changed = Combo(label, ref v, items, width);
            if (changed) onChange?.Invoke(v);
            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // DRAG-REORDER  - universal helpers for reorderable lists.
        //
        // Pattern per row:
        //
        //   Vector2 rowTL = ImGui.GetCursorScreenPos();
        //   bool dropped = UI.DragReorderRow("MY_PAYLOAD", ref _dragSrc, i, rowTL, rowW, rowH);
        //   if (dropped) { /* move _dragSrc to i in your list */ }
        //
        //   // ... draw row widgets ...
        //
        //   ImGui.InvisibleButton("##handle", new Vector2(handleW, rowH));
        //   UI.DragReorderSource("MY_PAYLOAD", ref _dragSrc, i,
        //       () => ImGui.TextDisabled(items[i].Name));
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Submits a full-row Dummy drop target, draws source/target highlights, and resets the cursor
        /// to <paramref name="rowTL"/> so subsequent widgets render inside the row as normal.
        /// Returns true when a valid payload was dropped onto this row - caller should reorder
        /// their list (move <paramref name="dragSrc"/> to <paramref name="rowIdx"/>).
        /// </summary>
        /// <param name="payloadType">ImGui drag-drop type string.</param>
        /// <param name="dragSrc">The caller's current drag-source index (−1 = none). Read-only here; set by <see cref="DragReorderSource"/>.</param>
        /// <param name="rowIdx">Index of this row in the list.</param>
        /// <param name="rowTL">Top-left corner of the row in screen space.</param>
        /// <param name="rowW">Row width in pixels.</param>
        /// <param name="rowH">Row height in pixels.</param>
        public static bool DragReorderRow(string payloadType, ref int dragSrc, int rowIdx,
                                           Vector2 rowTL, float rowW, float rowH)
        {
            bool dropped = false;

            // Dummy with ID=0 sets LastItemData.HoveredRect for BeginDragDropTarget without
            // claiming HoveredId - interactive widgets submitted afterward receive hover/click normally.
            ImGuiNET.ImGui.SetCursorScreenPos(rowTL);
            ImGuiNET.ImGui.Dummy(new Vector2(rowW, rowH));

            if (ImGuiNET.ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGuiNET.ImGui.AcceptDragDropPayload(payloadType);
                    if (payload.NativePtr != null && dragSrc >= 0 && dragSrc != rowIdx)
                        dropped = true;
                }
                ImGuiNET.ImGui.EndDragDropTarget();
            }

            // Reset cursor so the caller's row widgets start at rowTL
            ImGuiNET.ImGui.SetCursorScreenPos(rowTL);

            // Source row: accent fill. Hovered target row: accent outline.
            bool  dragging = ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left);
            var   mp       = ImGuiNET.ImGui.GetIO().MousePos;
            bool  overRow  = mp.Y >= rowTL.Y && mp.Y < rowTL.Y + rowH;
            var   br       = new Vector2(rowTL.X + rowW, rowTL.Y + rowH);
            var   dl       = DL;

            if (dragSrc == rowIdx && dragging)
                dl.AddRectFilled(rowTL, br, DA(Alpha(Accent, 30)));
            else if (dragSrc >= 0 && dragSrc != rowIdx && dragging && overRow)
                dl.AddRect(rowTL, br, DA(Alpha(Accent, 80)), 0f, ImDrawFlags.None, 2f);

            return dropped;
        }

        /// <summary>
        /// Call immediately after the drag-handle InvisibleButton (or any widget that initiates
        /// a drag). Sets up the ImGui drag source, records <paramref name="rowIdx"/> in
        /// <paramref name="dragSrc"/>, and invokes <paramref name="drawPreview"/> for the tooltip.
        /// </summary>
        /// <param name="payloadType">Must match the <c>payloadType</c> passed to <see cref="DragReorderRow"/>.</param>
        /// <param name="dragSrc">Set to <paramref name="rowIdx"/> while this row is being dragged.</param>
        /// <param name="rowIdx">Index of this row in the list.</param>
        /// <param name="drawPreview">Renders the drag tooltip content (icon + label, etc.).</param>
        public static void DragReorderSource(string payloadType, ref int dragSrc, int rowIdx,
                                              Action drawPreview)
        {
            if (ImGuiNET.ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                dragSrc = rowIdx;
                ImGuiNET.ImGui.SetDragDropPayload(payloadType, IntPtr.Zero, 0);
                drawPreview?.Invoke();
                ImGuiNET.ImGui.EndDragDropSource();
            }
        }

        /// <summary>
        /// Sets the mouse cursor to <see cref="ImGuiMouseCursor.Hand"/> when the last submitted
        /// widget is hovered. Call immediately after any interactive widget that should show
        /// a pointer cursor on hover.
        /// </summary>
        public static void HandCursorIfHovered()
        {
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        /// <summary>
        /// Draws an animated hover highlight over a list row. Mouse position is checked internally.
        /// Call AFTER drawing row backgrounds and BEFORE <see cref="DragReorderRow"/> so the
        /// z-order is correct: backgrounds → hover → drag highlights.
        /// Returns true while the mouse is over the row (useful for adjusting text colour etc).
        /// Automatically suppresses during drag operations so drag highlights remain visible.
        /// </summary>
        /// <param name="rowTL">Row top-left in screen space.</param>
        /// <param name="rowW">Row width in pixels.</param>
        /// <param name="rowH">Row height in pixels.</param>
        /// <param name="colour">Override tint (ABGR). Leave 0 for the default subtle white overlay.</param>
        /// <param name="rounding">Corner rounding radius.</param>
        public static bool RowHover(Vector2 rowTL, float rowW, float rowH,
                                     uint colour = 0, float rounding = 0f)
        {
            var  mp       = ImGuiNET.ImGui.GetIO().MousePos;
            bool dragging = ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left);
            bool over     = !dragging
                         && mp.X >= rowTL.X && mp.X < rowTL.X + rowW
                         && mp.Y >= rowTL.Y && mp.Y < rowTL.Y + rowH;

            uint animKey = unchecked((uint)(rowTL.X.GetHashCode() * 397 ^ rowTL.Y.GetHashCode()));
            float t      = Anim(animKey, over ? 1f : 0f, 10f);

            if (t > 0.01f)
            {
                uint c    = colour != 0 ? colour : Alpha(0xFFFFFFFF, 15);
                byte maxA = (byte)((c >> 24) & 0xFF);
                DL.AddRectFilled(rowTL, new Vector2(rowTL.X + rowW, rowTL.Y + rowH),
                    Alpha(c, (byte)(maxA * t)), rounding);
            }

            return over;
        }

        // ── Splitter ────────────────────────────────────────────────────────────────────────
        // Draggable vertical divider between two side-by-side panes.
        // sign: +1 = drag right grows the left pane; -1 = drag right shrinks it.
        public static void Splitter(string id, ref float size, float min, float max, float h, float sign = 1f)
        {
            const float W = 4f;
            var  sp  = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.InvisibleButton(id, new Vector2(W, h));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool act = ImGuiNET.ImGui.IsItemActive();
            if (hov || act) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            if (act) size = Math.Clamp(size + sign * ImGuiNET.ImGui.GetIO().MouseDelta.X, min, max);
            uint col = act ? Alpha(Accent,    180)
                     : hov ? Alpha(BorderCol,  200)
                     :       Alpha(BorderCol,   70);
            ImGuiNET.ImGui.GetWindowDrawList().AddLine(
                new Vector2(sp.X + W * 0.5f, sp.Y),
                new Vector2(sp.X + W * 0.5f, sp.Y + h),
                col, 1f);
        }
    }

    public enum ButtonStyle  { Accent, Ghost, Danger, Success }
    public enum LabelStyle   { Normal, Dim, Bold, Accent, Danger, Success, Warning }
    public enum NoticeStyle  { Info, Success, Warning, Danger }

    // ───────────────────────────────────────────────────────────────────────────
    // ICONS  - draw-list geometry icons for use with UI.Button(icon: ...)
    //
    // Each icon is: Action<ImDrawListPtr dl, Vector2 centre, float size, uint col>
    // The button passes its own draw-list, the icon centre position, the icon
    // size (matches font size), and the already-tinted+dimmed colour.
    // ───────────────────────────────────────────────────────────────────────────

    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static class Icons
    {
        // ── Gear / Settings ───────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Gear =
            (dl, c, sz, col) =>
            {
                // Dimensions
                float ro     = sz * 0.46f;  // tooth outer radius
                float ri     = sz * 0.32f;  // tooth base / ring outer radius
                float holeR  = sz * 0.18f;  // centre hole radius
                float toothW = sz * 0.10f;  // tooth half-width
                int   teeth  = 6;

                // Ring drawn as a thick circle stroke - naturally hollow, no background-
                // coloured hole overdraw needed, so the icon dims uniformly at any alpha.
                dl.AddCircle(c, (ri + holeR) * 0.5f, col, 32, ri - holeR);

                // Rectangular teeth: one convex quad per tooth
                for (int i = 0; i < teeth; i++)
                {
                    float a  = i * MathF.PI * 2f / teeth;
                    float ax = MathF.Cos(a);
                    float ay = MathF.Sin(a);
                    float px = -ay;
                    float py =  ax;

                    Vector2 p0 = new(c.X + ax * ri - px * toothW, c.Y + ay * ri - py * toothW);
                    Vector2 p1 = new(c.X + ax * ro - px * toothW, c.Y + ay * ro - py * toothW);
                    Vector2 p2 = new(c.X + ax * ro + px * toothW, c.Y + ay * ro + py * toothW);
                    Vector2 p3 = new(c.X + ax * ri + px * toothW, c.Y + ay * ri + py * toothW);
                    Vector2[] tooth = { p0, p1, p2, p3 };
                    dl.AddConvexPolyFilled(ref tooth[0], 4, col);
                }
            };

        // ── Close / X ─────────────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Close =
            (dl, c, sz, col) =>
            {
                float s = sz * 0.30f;
                float t = sz * 0.11f;
                dl.AddLine(new Vector2(c.X - s, c.Y - s), new Vector2(c.X + s, c.Y + s), col, t);
                dl.AddLine(new Vector2(c.X + s, c.Y - s), new Vector2(c.X - s, c.Y + s), col, t);
            };

        // ── Plus / Add ────────────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Plus =
            (dl, c, sz, col) =>
            {
                float s = sz * 0.32f;
                float t = sz * 0.11f;
                dl.AddLine(new Vector2(c.X - s, c.Y), new Vector2(c.X + s, c.Y), col, t);
                dl.AddLine(new Vector2(c.X, c.Y - s), new Vector2(c.X, c.Y + s), col, t);
            };

        // ── Minus / Remove ────────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Minus =
            (dl, c, sz, col) =>
            {
                float s = sz * 0.32f;
                dl.AddLine(new Vector2(c.X - s, c.Y), new Vector2(c.X + s, c.Y), col, sz * 0.11f);
            };

        // ── Trash / Delete ────────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Trash =
            (dl, c, sz, col) =>
            {
                float hw  = sz * 0.28f;   // half-width of bin body
                float top = c.Y - sz * 0.28f;
                float bot = c.Y + sz * 0.36f;
                float lw  = sz * 0.08f;

                // Bin body outline
                dl.AddLine(new Vector2(c.X - hw, top), new Vector2(c.X - hw, bot), col, lw);
                dl.AddLine(new Vector2(c.X + hw, top), new Vector2(c.X + hw, bot), col, lw);
                dl.AddLine(new Vector2(c.X - hw, bot), new Vector2(c.X + hw, bot), col, lw);

                // Lid
                float lidY = top - sz * 0.06f;
                dl.AddLine(new Vector2(c.X - hw * 1.3f, lidY), new Vector2(c.X + hw * 1.3f, lidY), col, lw);

                // Handle on lid
                float hh = sz * 0.10f;
                dl.AddLine(new Vector2(c.X - hw * 0.4f, lidY), new Vector2(c.X - hw * 0.4f, lidY - hh), col, lw);
                dl.AddLine(new Vector2(c.X + hw * 0.4f, lidY), new Vector2(c.X + hw * 0.4f, lidY - hh), col, lw);
                dl.AddLine(new Vector2(c.X - hw * 0.4f, lidY - hh), new Vector2(c.X + hw * 0.4f, lidY - hh), col, lw);

                // Vertical lines inside body
                dl.AddLine(new Vector2(c.X, top + sz * 0.06f), new Vector2(c.X, bot - sz * 0.06f), col, lw * 0.8f);
                dl.AddLine(new Vector2(c.X - hw * 0.5f, top + sz * 0.06f), new Vector2(c.X - hw * 0.5f, bot - sz * 0.06f), col, lw * 0.8f);
                dl.AddLine(new Vector2(c.X + hw * 0.5f, top + sz * 0.06f), new Vector2(c.X + hw * 0.5f, bot - sz * 0.06f), col, lw * 0.8f);
            };

        // ── Check / Confirm ───────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Check =
            (dl, c, sz, col) =>
            {
                float s  = sz * 0.28f;
                float lw = sz * 0.11f;
                Vector2 mid = new(c.X - s * 0.15f, c.Y + s * 0.35f);
                dl.AddLine(new Vector2(c.X - s, c.Y), mid, col, lw);
                dl.AddLine(mid, new Vector2(c.X + s, c.Y - s * 0.7f), col, lw);
            };

        // ── Refresh / Reload ──────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Refresh =
            (dl, c, sz, col) =>
            {
                float r  = sz * 0.32f;
                float lw = sz * 0.10f;
                // Arc - ~300 degrees
                dl.PathArcTo(c, r, MathF.PI * 0.2f, MathF.PI * 1.9f);
                dl.PathStroke(col, ImDrawFlags.None, lw);

                // Arrow tip at end of arc
                float a  = MathF.PI * 0.2f;
                float ax = c.X + r * MathF.Cos(a);
                float ay = c.Y + r * MathF.Sin(a);
                float ts2 = sz * 0.14f;
                dl.AddTriangleFilled(
                    new Vector2(ax, ay),
                    new Vector2(ax - ts2, ay - ts2 * 1.4f),
                    new Vector2(ax + ts2 * 0.6f, ay - ts2 * 1.4f),
                    col);
            };

        // ── Search / Magnifier ────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Search =
            (dl, c, sz, col) =>
            {
                float r  = sz * 0.24f;
                float lw = sz * 0.10f;
                Vector2 cc = new(c.X - sz * 0.07f, c.Y - sz * 0.07f);
                dl.AddCircle(cc, r, col, 0, lw);
                float d = r * 0.72f;
                dl.AddLine(
                    new Vector2(cc.X + d, cc.Y + d),
                    new Vector2(c.X + sz * 0.32f, c.Y + sz * 0.32f),
                    col, lw);
            };

        // ── Save / Floppy disk ────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Save =
            (dl, c, sz, col) =>
            {
                float h2  = sz * 0.40f;
                float w2  = sz * 0.36f;
                float lw  = sz * 0.09f;
                float cut = sz * 0.14f;   // top-right corner cut for the disk notch

                // Outer shape - square with top-right corner cut
                Vector2 tl = new(c.X - w2, c.Y - h2);
                Vector2 br = new(c.X + w2, c.Y + h2);
                dl.AddLine(tl,                              new Vector2(br.X - cut, tl.Y), col, lw);
                dl.AddLine(new Vector2(br.X - cut, tl.Y),  new Vector2(br.X, tl.Y + cut), col, lw);
                dl.AddLine(new Vector2(br.X, tl.Y + cut),  br,                             col, lw);
                dl.AddLine(br,                              new Vector2(tl.X, br.Y),        col, lw);
                dl.AddLine(new Vector2(tl.X, br.Y),        tl,                             col, lw);

                // Label slot (top-centre rectangle)
                float sw = w2 * 1.0f;
                float sh = h2 * 0.5f;
                dl.AddRectFilled(
                    new Vector2(c.X - sw * 0.5f, tl.Y + lw * 0.5f),
                    new Vector2(c.X + sw * 0.5f, tl.Y + sh),
                    col);

                // Bottom data area (small inset rectangle)
                dl.AddRect(
                    new Vector2(c.X - w2 * 0.6f, c.Y + h2 * 0.05f),
                    new Vector2(c.X + w2 * 0.6f, br.Y - lw),
                    col, 0f, ImDrawFlags.None, lw * 0.8f);
            };

        // ── Warning / Alert ───────────────────────────────────────────────
        public static readonly Action<ImDrawListPtr, Vector2, float, uint> Warning =
            (dl, c, sz, col) =>
            {
                float h  = sz * 0.44f;
                float hw = sz * 0.40f;
                float lw = sz * 0.09f;

                // Triangle outline
                Vector2 top   = new(c.X,      c.Y - h);
                Vector2 left  = new(c.X - hw, c.Y + h * 0.6f);
                Vector2 right = new(c.X + hw, c.Y + h * 0.6f);
                dl.AddTriangle(top, right, left, col, lw);

                // Exclamation stem
                float stemTop = top.Y + h * 0.28f;
                float stemBot = c.Y + h * 0.18f;
                dl.AddLine(new Vector2(c.X, stemTop), new Vector2(c.X, stemBot), col, lw * 1.2f);

                // Exclamation dot
                dl.AddCircleFilled(new Vector2(c.X, c.Y + h * 0.38f), lw * 0.8f, col);
            };
    }
}
