// Generic confirmation dialog for destructive / irreversible actions.
//
// Usage (call from any panel's DrawPanelContent):
//   if (UI.Button("Enable Mem-Writes", ...))
//       ConfirmModal.Open(
//           "Enable Memory Writes",
//           "Memory writes can crash the game or trigger anti-cheat. Only enable if you know what you are doing.",
//           () => { Config.MemWriteEnabled = true; Config.SaveAsync(); });
//
// Add  ConfirmModal.Draw();  in MenuManager.DrawAllPanels(), after ToastManager.Draw().

using System;
using System.Numerics;
using System.Reflection;
using ImGuiNET;

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static class ConfirmModal
    {
        private const float ModalMinW = 320f;
        private const float ModalMaxW = 520f;
        private const float Pad       = 16f;
        private const float Round     = 8f;

        private static bool        _open;
        private static string      _title;
        private static string      _body;
        private static Action      _onConfirm;
        private static string      _confirmLabel;
        private static ButtonStyle _confirmStyle;
        private static string      _cancelLabel;
        private static float       _openT;
        private static bool        _requireExplicitChoice;
        private static bool        _singleButton;

        public static bool IsOpen => _open;

        /// <summary>Queue a confirmation dialog. Safe to call from DrawPanelContent.</summary>
        /// <param name="requireExplicitChoice">
        /// When true, clicking the backdrop and pressing Escape are disabled - the user
        /// must click one of the buttons. Use for decisions that cannot be silently dismissed.
        /// </param>
        /// <param name="singleButton">
        /// When true, renders only the confirm button (full width) - no cancel button. Use for
        /// a plain acknowledgement/alert that isn't actually a yes/no choice (e.g. both options
        /// would do the same thing), instead of passing the same label as both confirmLabel and
        /// cancelLabel, which renders as two identical buttons.
        /// </param>
        public static void Open(
            string title, string body, Action onConfirm,
            string confirmLabel = "Confirm",
            ButtonStyle confirmStyle = ButtonStyle.Danger,
            string cancelLabel = "Cancel",
            bool requireExplicitChoice = false,
            bool singleButton = false)
        {
            _title                = title ?? "Confirm";
            _body                 = body  ?? "";
            _onConfirm            = onConfirm;
            _confirmLabel         = confirmLabel;
            _confirmStyle         = confirmStyle;
            _cancelLabel          = string.IsNullOrEmpty(cancelLabel) ? "Cancel" : cancelLabel;
            _requireExplicitChoice = requireExplicitChoice;
            _singleButton         = singleButton;
            if (!_open) _openT   = 0f;
            _open                 = true;
        }

        /// <summary>
        /// Render the modal. Call once per frame from MenuManager.DrawAllPanels(),
        /// after ToastManager.Draw(), so it sits above all panels.
        /// </summary>
        public static void Draw()
        {
            if (!_open) return;

            var io = ImGuiNET.ImGui.GetIO();

            // Advance open animation (snap in LowPerf mode).
            _openT = UI.LowPerf ? 1f : MathF.Min(1f, _openT + io.DeltaTime * 14f);
            float ease = 1f - MathF.Pow(1f - _openT, 3f); // cubic ease-out

            // ── Backdrop: full-screen dim overlay + "click outside = cancel" ──
            ImGuiNET.ImGui.SetNextWindowFocus();
            ImGuiNET.ImGui.SetNextWindowPos(Vector2.Zero);
            ImGuiNET.ImGui.SetNextWindowSize(io.DisplaySize);
            ImGuiNET.ImGui.SetNextWindowBgAlpha(0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    Vector2.Zero);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.Begin("##xmcm_bd",
                ImGuiWindowFlags.NoTitleBar      |
                ImGuiWindowFlags.NoResize        |
                ImGuiWindowFlags.NoMove          |
                ImGuiWindowFlags.NoScrollbar     |
                ImGuiWindowFlags.NoNav           |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing);
            ImGuiNET.ImGui.PopStyleVar(2);

            ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(
                Vector2.Zero, io.DisplaySize,
                ImGuiStyle.WithAlpha(ImGuiStyle.Dark0, (byte)(155 * ease)));

            ImGuiNET.ImGui.InvisibleButton("##xmcm_bd_hit", io.DisplaySize);
            if (ImGuiNET.ImGui.IsItemClicked() && !_requireExplicitChoice) _open = false;

            ImGuiNET.ImGui.End();

            // ── Modal window ───────────────────────────────────────────────────
            Vector2 center = io.DisplaySize * 0.5f;
            float   riseY  = 10f * (1f - ease); // subtle rise as it appears

            ImGuiNET.ImGui.SetNextWindowFocus();
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(
                new Vector2(ModalMinW, 0f), new Vector2(ModalMaxW, 800f));
            ImGuiNET.ImGui.SetNextWindowPos(
                new Vector2(center.X, center.Y + riseY),
                ImGuiCond.Always, new Vector2(0.5f, 0.5f));

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    new Vector2(Pad, Pad));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   Round);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg,
                new Vector4(0x10 / 255f, 0x10 / 255f, 0x18 / 255f, 0.98f));

            ImGuiNET.ImGui.Begin("##xmcm_win",
                ImGuiWindowFlags.NoTitleBar          |
                ImGuiWindowFlags.NoResize            |
                ImGuiWindowFlags.NoMove              |
                ImGuiWindowFlags.NoScrollbar         |
                ImGuiWindowFlags.NoScrollWithMouse   |
                ImGuiWindowFlags.AlwaysAutoResize    |
                ImGuiWindowFlags.NoSavedSettings);

            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopStyleVar(3);

            // Chrome drawn on top of ImGui's window background.
            var     dl = ImGuiNET.ImGui.GetWindowDrawList();
            Vector2 wp = ImGuiNET.ImGui.GetWindowPos();
            Vector2 ws = ImGuiNET.ImGui.GetWindowSize(); // last-frame size - fine for chrome
            Vector2 br = wp + ws;

            // Outer border
            dl.AddRect(wp, br, ImGuiStyle.WithAlpha(ImGuiStyle.Border, 200),
                Round, ImDrawFlags.None, 1f);

            // Accent traces the full rounded top edge - left arc → top straight → right arc
            dl.PathArcTo(new Vector2(wp.X + Round, wp.Y + Round), Round, MathF.PI,       MathF.PI * 1.5f);
            dl.PathArcTo(new Vector2(br.X - Round, wp.Y + Round), Round, MathF.PI * 1.5f, MathF.PI * 2f);
            dl.PathStroke(ImGuiStyle.WithAlpha(ImGuiStyle.Accent, 200), ImDrawFlags.None, 1.5f);

            // ── Title ──────────────────────────────────────────────────────────
            var   fontB = ImGuiStyle.FontBold;
            float fsB   = fontB.FontSize > 0f ? fontB.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;

            string locTitle = LocManager.Get(_title);
            Vector2 titlePos = ImGuiNET.ImGui.GetCursorScreenPos();
            dl.AddText(fontB, fsB, titlePos, ImGuiStyle.Text, locTitle);
            ImGuiNET.ImGui.Dummy(new Vector2(avail, fsB + 2f));

            // Separator
            Vector2 sep = ImGuiNET.ImGui.GetCursorScreenPos();
            dl.AddRectFilled(sep, new Vector2(sep.X + avail, sep.Y + 1f),
                ImGuiStyle.WithAlpha(ImGuiStyle.Border, 100));
            ImGuiNET.ImGui.Dummy(new Vector2(1f, 10f));

            // ── Warning body ───────────────────────────────────────────────────
            UI.Notice(_body, NoticeStyle.Warning);
            ImGuiNET.ImGui.Dummy(new Vector2(1f, 3f));

            // ── Buttons ────────────────────────────────────────────────────────
            if (_singleButton)
            {
                if (UI.Button(_confirmLabel, new Vector2(avail, 0f), _confirmStyle))
                {
                    _open = false;
                    _onConfirm?.Invoke();
                }
            }
            else
            {
                float bw = (avail - 8f) * 0.5f;

                if (UI.Button(_cancelLabel, new Vector2(bw, 0f), ButtonStyle.Ghost))
                    _open = false;

                ImGuiNET.ImGui.SameLine(0f, 8f);

                if (UI.Button(_confirmLabel, new Vector2(bw, 0f), _confirmStyle))
                {
                    _open = false;
                    _onConfirm?.Invoke();
                }
            }

            // Escape = cancel (blocked when an explicit choice is required)
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape) && !_requireExplicitChoice) _open = false;

            ImGuiNET.ImGui.End();
        }
    }
}
