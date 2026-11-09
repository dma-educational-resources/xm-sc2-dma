using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using SC2Radar.SC2;
using SC2Radar.UI.Misc;
using SC2Radar.UI.ImGui.Panels;

namespace SC2Radar.UI.ImGui.Widgets
{
    /// <summary>
    /// Abstract base for custom-chrome floating radar widgets (Player/Loot/Quest/KillFeed/
    /// Debug/AimView). Visually and mechanically mirrors <see cref="ChromePanel"/> (drag,
    /// collapse animation, close/collapse/pin/panel-menu buttons, scrollable content,
    /// all-edge resize, screen-edge snapping, focus ring) but is a fully independent type
    /// with its own static drag source and its own persistence
    /// (<see cref="Config.WidgetOpen"/> / <see cref="Config.WidgetLayouts"/>) so widget
    /// dragging/docking can never interact with the settings-panel docking system driven by
    /// <see cref="ChromePanel.DragSource"/> and MenuManager.
    /// Subclasses implement WidgetTitle, InitSize, MinSize, and DrawWidgetContent().
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public abstract class WidgetChrome : ImGuiPanel
    {
        // ── Chrome layout constants ───────────────────────────────────────────
        // Deliberately shorter than ChromePanel's 30px settings-panel title bar - widgets
        // are meant to read as small, compact HUD overlays (matching the old native-ImGui
        // widget title bars they replaced), not full settings panels.
        // These are `internal` rather than `protected` so WidgetManager (which renders
        // docked-slot chrome but does NOT extend WidgetChrome - a dock slot isn't a
        // WidgetChrome instance) can reference the exact same values directly instead of
        // maintaining hand-tuned duplicate constants that inevitably drift out of sync.
        internal const float TitleH    = 18f;
        // Wider than ChromePanel's 8px - widget content is mostly flat ImGui.Text/tables
        // with no per-row indentation of their own (unlike settings panels' UI.Label/
        // UI.KVRow rows), so the same 8px chrome inset read as text hugging the border.
        internal const float Pad       = 16f;
        internal const float WinRound  = 8f;
        internal const float CloseBtnS = 16f;
        // Exact vertical gap between the accent line and the first line of content.
        // Applied via SetCursorPosY (not a Dummy+ItemSpacing trick) so this value is the
        // WHOLE gap - a Dummy item would pick up the app's default 6px ItemSpacing.Y on
        // top of whatever height we gave it, silently inflating (and diluting tweaks to) the gap.
        internal const float ContentTopGap = 6f;

        // ── Chrome colours (independently themeable - see General Settings ▸ Colors ▸
        // Widgets, and InterfaceColorOptions.SetColors) ─────────────────────────
        // Subclasses override HasOpacityOverride + OpacityOverride to wire up the
        // per-widget opacity slider from General Settings ▸ Radar Widgets.
        // When HasOpacityOverride is true all chrome colours have their alpha replaced
        // by OpacityOverride (0–1); when false the colour's own alpha is used as-is.
        protected virtual bool  HasOpacityOverride => false;
        protected virtual float OpacityOverride    => 1f;
        private uint ApplyOpacity(uint col)
            => HasOpacityOverride ? UI.Alpha(col, (byte)(OpacityOverride * 255f)) : col;

        protected virtual uint ChromeBg          => ApplyOpacity(ImGuiStyle.WidgetChromeBg);
        protected virtual uint ChromeTitleBg     => ApplyOpacity(ImGuiStyle.WidgetChromeTitleBg);
        protected virtual uint ChromeBorder      => ApplyOpacity(ImGuiStyle.WidgetChromeBorder);
        protected virtual uint ChromeTitleTxt    => ApplyOpacity(ImGuiStyle.WidgetChromeTitleText);
        protected virtual uint ChromeScrollThumb => ApplyOpacity(ImGuiStyle.WidgetChromeScrollThumb);

        // ── Widget identity ───────────────────────────────────────────────────
        protected abstract string WidgetTitle { get; }

        // ── Content hooks ─────────────────────────────────────────────────────
        protected abstract void DrawWidgetContent();

        // ── Chrome scroll child extra flags ───────────────────────────────────
        /// <summary>
        /// Extra ImGuiWindowFlags merged into the chrome scroll child.
        /// Override with NoScrollbar when the widget manages its own full-bleed content
        /// (e.g. AimView's canvas) and doesn't need the chrome-level scrollbar to appear.
        /// </summary>
        protected virtual ImGuiWindowFlags ChromeScrollExtraFlags => ImGuiWindowFlags.None;

        // ── Content padding ───────────────────────────────────────────────────
        /// <summary>
        /// When true (default) the chrome adds 8 px side padding and a 13 px top/bottom
        /// gap around widget content. Set to false for widgets that own their own full-bleed
        /// layout (e.g. AimView) so content fills edge-to-edge below the title bar.
        /// </summary>
        protected virtual bool HasContentPadding    => true;
        protected virtual bool HasBottomGap         => !HasContentPadding;
        protected virtual bool HasScrollWindowPadding => HasContentPadding;

        /// <summary>
        /// Nudges the cursor up before a widget's very first content element is a UI.Toggle
        /// checkbox row. UI.Toggle's 16px checkbox is a few px taller than a plain text line,
        /// so its label ends up sitting a few px lower than ContentTopGap would put a plain
        /// ImGuiNET.Text call - which reads as inconsistent gap next to text-first widgets
        /// (Debug, Loot Info, Kill Feed). This is a widget-local compensation only: UI.Toggle
        /// itself (shared by every settings panel in the app) is left untouched.
        /// Call once, as the first line of DrawWidgetContent(), before the first UI.Toggle.
        /// </summary>
        protected static void AlignLeadingToggleRow()
            => ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() - 4f);

        // ── Auto size ─────────────────────────────────────────────────────────
        /// <summary>
        /// When true the widget can't be resized by hand: each frame its window takes the size the content reported
        /// through <see cref="ReportContentSize"/> on the previous frame (plus title bar and padding).
        /// </summary>
        protected virtual bool AutoSize => false;
        private Vector2 _autoContent;

        /// <summary>For <see cref="AutoSize"/> widgets: call at the end of DrawWidgetContent with the content's
        /// right / bottom edge relative to the content child's top-left (e.g. from GetItemRectMax - GetWindowPos).</summary>
        protected void ReportContentSize(Vector2 extent) => _autoContent = extent;

        private Vector2? AutoWindowSize(ImGuiIOPtr io)
        {
            if (!AutoSize || _autoContent.X <= 0f) return null;
            float w = _autoContent.X + (HasScrollWindowPadding ? Pad : 0f);
            float h = TitleH + _autoContent.Y + (HasBottomGap ? 6f : 0f) + 6f;
            return new Vector2(Math.Clamp(w, MinSize.X, io.DisplaySize.X), Math.Clamp(h, TitleH + 20f, io.DisplaySize.Y));
        }

        // ── Panel menu (ellipsis button) ──────────────────────────────────────
        /// <summary>
        /// Return true to show the ellipsis (…) button in the title bar.
        /// Override DrawPanelMenu() to populate the dropdown.
        /// </summary>
        protected virtual bool HasPanelMenu => false;
        /// <summary>Called inside the panel-menu dropdown popup.</summary>
        protected virtual void DrawPanelMenu() { }

        // ── Group / host support ──────────────────────────────────────────────

        /// <summary>When true this widget belongs to a WidgetGroup - Draw() is a no-op.</summary>
        internal bool IsGrouped { get; set; }

        /// <summary>
        /// Set each frame by WidgetManager from the widget's show-flag.
        /// WidgetGroup hides this widget's tab when false but keeps it as a member,
        /// so re-enabling the flag makes the tab reappear without re-joining the group.
        /// </summary>
        internal bool IsVisible { get; set; } = true;

        /// <summary>
        /// When true, DrawWidgetContent() is being called from inside a WidgetGroup host.
        /// DrawTabHeader() skips the leading-dummy offset cancellation in this mode.
        /// </summary>
        internal bool IsHosted { get; set; }

        /// <summary>Screen edge this widget is anchored to. None = floating.</summary>
        internal DockEdge DockedEdge { get; set; } = DockEdge.None;

        /// <summary>True while the widget is docked to a screen edge; Draw() is a no-op.</summary>
        internal bool IsDocked => DockedEdge != DockEdge.None;

        /// <summary>Exposes WidgetTitle (localized) for WidgetGroup membership tracking.</summary>
        internal string Title => LocManager.Get(WidgetTitle);

        // ── OS-window pop-out ───────────────────────────────────────────────
        /// <summary>True while this widget is rendered inside its own independent OS window.</summary>
        internal bool IsPoppedOut { get; private set; }

        /// <summary>True if the popped-out OS window should stay above other windows.</summary>
        internal bool AlwaysOnTop { get; private set; }

        /// <summary>HWND of this widget's popout window, valid only while IsPoppedOut.</summary>
        internal IntPtr PopoutHwnd { get; private set; }

        /// <summary>Raised when the user clicks "Pop out of app" - ImGuiApp creates the OS window.</summary>
        internal event Action<WidgetChrome> PopOutRequested;
        /// <summary>Raised when the user explicitly clicks "Return to app" while popped - ImGuiApp tears down the OS window and forgets the pop-out intent (it stays docked/floating in-app from then on).</summary>
        internal event Action<WidgetChrome> RedockRequested;
        /// <summary>Raised when the widget is closed (in-chrome × or disabled) while popped - ImGuiApp tears down the OS window but keeps the pop-out intent, so re-enabling the widget re-creates it.</summary>
        internal event Action<WidgetChrome> HideRequested;
        /// <summary>Raised when the always-on-top toggle changes while popped.</summary>
        internal event Action<WidgetChrome> AlwaysOnTopToggled;

        /// <summary>Called by ImGuiApp once the popout OS window exists/is torn down (explicit redock - clears pop-out intent).</summary>
        internal void MarkPoppedOut(bool poppedOut, IntPtr hwnd = default)
        {
            if (poppedOut && !IsPoppedOut)
            {
                // Remember the in-app placement so "Return to app" can restore it instead of
                // leaving the widget pinned at (0,0) sized like the popout's OS window - see
                // the IsPoppedOut branch in Draw(), which overwrites _winPos/_lastKnownSize
                // every frame while popped.
                _prePopoutPos  = _winPos;
                _prePopoutSize = _lastKnownSize;
            }
            else if (!poppedOut && IsPoppedOut)
            {
                _winPos        = _prePopoutPos;
                _lastKnownSize = _prePopoutSize;
                _sizeRestored  = false; // force the restored size next frame (see !_sizeRestored branch)
            }

            IsPoppedOut = poppedOut;
            PopoutHwnd  = poppedOut ? hwnd : IntPtr.Zero;
            if (!poppedOut) AlwaysOnTop = false;

            // Write PoppedOut into Config synchronously rather than relying solely on the
            // deferred _pendingSave flush at the top of Draw(). WidgetManager.ReconcilePopoutIntent
            // runs at the very top of DrawAllWidgets - before this widget's own Draw() call
            // later in that same frame - and re-triggers a pop-out the instant it sees
            // IsPoppedOut == false but Config still says PoppedOut == true. Without this, an
            // explicit "Return to app" flipped IsPoppedOut here but left the stale Config value
            // in place for one frame, so ReconcilePopoutIntent immediately queued a fresh
            // pop-out and the widget re-popped itself right back out.
            if (Config.WidgetLayouts.TryGetValue(WidgetTitle, out var existing))
                existing.PoppedOut = poppedOut;
            else
                Config.WidgetLayouts[WidgetTitle] = new WidgetLayoutEntry
                {
                    X = _winPos.X,        Y = _winPos.Y,
                    W = _lastKnownSize.X, H = _lastKnownSize.Y,
                    Collapsed   = _collapsed,
                    PoppedOut   = poppedOut,
                    AlwaysOnTop = AlwaysOnTop
                };

            _pendingSave = true;
        }

        /// <summary>
        /// Updates in-memory popped-out state only - no persistence side effect. Used when
        /// the OS window is torn down because the widget was closed/disabled rather than
        /// explicitly un-popped, so Config.WidgetLayouts[title].PoppedOut (and AlwaysOnTop)
        /// keep remembering the widget's last deliberate pop-out state; re-enabling it later
        /// re-creates the popout instead of leaving it parented in the main app.
        /// </summary>
        internal void MarkPoppedOutTransient(bool poppedOut)
        {
            IsPoppedOut = poppedOut;
            PopoutHwnd  = poppedOut ? PopoutHwnd : IntPtr.Zero;
        }

        /// <summary>Current screen-space position (updated every frame after Draw).</summary>
        internal Vector2 ScreenPos => _winPos;

        /// <summary>Current screen-space size (updated every frame after Draw).</summary>
        internal Vector2 ScreenSize => _lastKnownSize.X > 0f ? _lastKnownSize : InitSize;

        /// <summary>Teleports the widget to <paramref name="pos"/> (screen-space top-left).</summary>
        /// <summary>Reads the position and size from Config.WidgetLayouts again on the next Draw (a layout preset was applied).</summary>
        internal void ReloadPlacement() { _placed = false; _pendingSave = false; }

        internal void SetPosition(Vector2 pos)
        {
            _winPos      = pos;
            _placed      = true; // prevent first-Draw centering from overriding explicit placement
            _pendingSave = true;
        }

        /// <summary>
        /// Forces a specific size on the first Draw() call, overriding InitSize.
        /// Used when creating a split so the new chrome window inherits the target widget's
        /// current size rather than expanding to fit both panes' design sizes summed together.
        /// </summary>
        internal void SetInitialSize(Vector2 size)
        {
            _lastKnownSize = size;
            _sizeRestored  = false; // triggers ImGuiCond.Always on the first frame
        }

        private bool _requestFocus;

        /// <summary>Requests <see cref="ImGui.SetNextWindowFocus"/> on the next Draw call.</summary>
        internal void RequestFocus() => _requestFocus = true;

        /// <summary>
        /// Called each frame by the dock-slot renderer so ContainsScreenPoint stays
        /// accurate even though docked widgets skip their own Draw().
        /// </summary>
        internal void UpdateScreenRect(Vector2 pos, Vector2 size)
        {
            _winPos        = pos;
            _lastKnownSize = size;
        }

        /// <summary>Returns true if <paramref name="p"/> falls inside this widget's screen bounds.</summary>
        internal bool ContainsScreenPoint(Vector2 p)
        {
            var pos = _winPos;
            var sz  = _lastKnownSize.X > 0f ? _lastKnownSize : InitSize;
            return p.X >= pos.X && p.X < pos.X + sz.X && p.Y >= pos.Y && p.Y < pos.Y + sz.Y;
        }

        /// <summary>Calls DrawWidgetContent() - used by WidgetGroup to render a hosted member.</summary>
        internal void InvokeDrawContent() => DrawWidgetContent();

        /// <summary>Exposes HasContentPadding for WidgetGroup and DrawDockedSlot to proxy.</summary>
        internal bool GetHasContentPadding() => HasContentPadding;
        /// <summary>Exposes HasScrollWindowPadding for DrawDockedSlot.</summary>
        internal bool GetHasScrollWindowPadding() => HasScrollWindowPadding;

        /// <summary>Exposes ChromeScrollExtraFlags for WidgetGroup to proxy.</summary>
        internal ImGuiWindowFlags GetChromeScrollExtraFlags() => ChromeScrollExtraFlags;

        /// <summary>Exposes HasPanelMenu for WidgetGroup and WidgetSplit to proxy.</summary>
        internal bool GetHasPanelMenu() => HasPanelMenu;
        /// <summary>Exposes DrawPanelMenu for WidgetGroup and WidgetSplit to proxy.</summary>
        internal void InvokeDrawPanelMenu() => DrawPanelMenu();

        /// <summary>Exposes InitSize for WidgetGroup to compute its own initial size.</summary>
        internal Vector2 GetInitSize() => InitSize;

        // Expose chrome colours so dock slots can exactly match the widget's visual theme.
        internal uint GetChromeBg()          => ChromeBg;
        internal uint GetChromeTitleBg()     => ChromeTitleBg;
        internal uint GetChromeBorder()      => ChromeBorder;
        internal uint GetChromeTitleTxt()    => ChromeTitleTxt;
        internal uint GetChromeScrollThumb() => ChromeScrollThumb;

        // ── Static drag source (independent from ChromePanel.DragSource) ───────
        // Set when any WidgetChrome starts dragging; cleared by WidgetManager.
        // Deliberately a *separate* static field from ChromePanel.DragSource so that
        // dragging a widget can never be observed by MenuManager's snap-zone/dock-preview
        // code (which polls ChromePanel.DragSource), and vice versa.
        internal static WidgetChrome DragSource { get; private set; }
        internal static void ClearDragSource() => DragSource = null;

        // ── Focus tracking ────────────────────────────────────────────────────
        private static string _focusedTitle;
        private static uint   _anyDraggingFrame;

        // ── Per-instance chrome state ─────────────────────────────────────────
        private bool    _placed;
        private bool    _dragging;
        private Vector2 _winPos;
        private Vector2 _dragStartMouse;
        private Vector2 _dragStartWinPos;
        private Vector2 _lastKnownSize;
        private bool    _collapsed;
        private float   _expandedH;
        private bool    _wasAnimating;
        private bool    _pendingSave;
        private bool    _sizeRestored = true; // false = have a saved size that must be forced on first frame
        private Vector2 _prePopoutPos;
        private Vector2 _prePopoutSize;

        // ── Resize state ──────────────────────────────────────────────────────
        private enum ResizeEdge
        {
            None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight
        }
        private bool       _resizing;
        private ResizeEdge _resizeEdge;
        private ResizeEdge _hoveredEdge;
        private Vector2    _resizeStartMouse;
        private Vector2    _resizeStartPos;
        private Vector2    _resizeStartSize;

        private uint CollapseAnimId => (uint)WidgetTitle.GetHashCode() ^ 0xC011_0000u;

        // ── Sealed Draw ───────────────────────────────────────────────────────
        public sealed override void Draw(ref bool open)
        {
            if (!open) return;
            if (IsGrouped) return; // rendered by WidgetGroup - skip standalone draw
            if (IsDocked)  return; // rendered by WidgetManager dock slot

            var io = ImGuiNET.ImGui.GetIO();

            if (!_placed)
            {
                _placed = true;
                if (Config.WidgetLayouts.TryGetValue(WidgetTitle, out var saved) &&
                    saved.W > 0f && saved.H > 0f)
                {
                    // Clamp loaded size to MinSize so a corrupted or stale config
                    // value can never produce a widget smaller than its design minimum.
                    float clampedW = Math.Max(MinSize.X, saved.W);
                    float clampedH = Math.Max(MinSize.Y, saved.H);

                    _winPos        = new Vector2(saved.X, saved.Y);
                    _lastKnownSize = new Vector2(clampedW, clampedH);
                    _expandedH     = clampedH;
                    _collapsed     = saved.Collapsed;
                    AlwaysOnTop    = saved.AlwaysOnTop;
                    // NOTE: saved.PoppedOut is intentionally not applied here - restoring the
                    // actual OS window is ImGuiApp's job at startup (it owns _device/thread
                    // affinity for creating a DX11Window), driven by the same saved flag.

                    if (_collapsed)
                    {
                        // Pre-seed the collapse animation to 1 so the widget renders
                        // fully collapsed immediately with no expand-then-shrink flash.
                        UI.SeedAnim(CollapseAnimId, 1f);
                        _sizeRestored = true; // collapse branch handles sizing; don't force saved H
                    }
                    else
                    {
                        _sizeRestored = false; // force saved size on first frame (ImGui NoSavedSettings)
                    }
                }
                else
                {
                    _winPos       = new Vector2(
                        (io.DisplaySize.X - InitSize.X) * .5f,
                        (io.DisplaySize.Y - InitSize.Y) * .5f);
                    _sizeRestored = true;
                }
            }

            // Flush deferred layout save (runs one frame after the event so
            // _winPos and _lastKnownSize reflect the final post-event values).
            if (_pendingSave)
            {
                _pendingSave = false;
                Config.WidgetLayouts[WidgetTitle] = new WidgetLayoutEntry
                {
                    X = _winPos.X,        Y = _winPos.Y,
                    W = _lastKnownSize.X, H = _lastKnownSize.Y,
                    Collapsed   = _collapsed,
                    PoppedOut   = IsPoppedOut,
                    AlwaysOnTop = AlwaysOnTop
                };
                Config.SaveDebounced();
            }

            float   colT;
            bool    resizingNow = false;
            bool    animating   = false;
            bool    snapToExpanded = false;

            if (IsPoppedOut)
            {
                // The host OS window itself now owns move (native title-bar drag, see
                // DrawDragRegion) and resize (native WS_THICKFRAME on the popout
                // DX11Window) - the widget simply fills its host window completely.
                _dragging      = false;
                _resizing      = false;
                _collapsed     = false;
                _hoveredEdge   = ResizeEdge.None;
                _winPos        = Vector2.Zero;
                _lastKnownSize = io.DisplaySize;
                _expandedH     = io.DisplaySize.Y;
                colT = 0f;

                ImGuiNET.ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
                ImGuiNET.ImGui.SetNextWindowSize(io.DisplaySize, ImGuiCond.Always);
            }
            else
            {
                // ── Drag ──────────────────────────────────────────────────────
                if (_dragging && io.MouseDown[0])
                {
                    _winPos = _dragStartWinPos + (io.MousePos - _dragStartMouse);

                    // Screen-edge snapping (8 px threshold). Top snaps to just below the main
                    // menu bar (not y=0) so widgets can never be dragged up to cover it.
                    const float SnapDist = 8f;
                    float menuH = ImGuiNET.ImGui.GetFrameHeight();
                    Vector2 sz = _lastKnownSize.X > 0f ? _lastKnownSize : InitSize;
                    if (_winPos.X < SnapDist)                                  _winPos.X = 0f;
                    else if (_winPos.X + sz.X > io.DisplaySize.X - SnapDist)  _winPos.X = io.DisplaySize.X - sz.X;
                    if (_winPos.Y < menuH + SnapDist)                          _winPos.Y = menuH;
                    else if (_winPos.Y + sz.Y > io.DisplaySize.Y - SnapDist)  _winPos.Y = io.DisplaySize.Y - sz.Y;
                }
                else
                {
                    if (_dragging) _pendingSave = true;
                    _dragging = false;
                }
                if (_dragging) _anyDraggingFrame = (uint)ImGuiNET.ImGui.GetFrameCount();

                // ── Resize (screen-space hit test - runs before Begin so child
                //    windows cannot block the edge hit regions) ───────────────
                uint  _cf             = (uint)ImGuiNET.ImGui.GetFrameCount();
                bool  _otherDragging  = !_dragging && (_anyDraggingFrame == _cf || _anyDraggingFrame == _cf - 1u);
                resizingNow = _resizing && io.MouseDown[0] && !_collapsed && !_otherDragging && !Config.WidgetLockSize && !AutoSize;
                if (!resizingNow && _resizing) { _resizing = false; _pendingSave = true; }

                Vector2 resizeTargetSize = default;
                if (resizingNow)
                {
                    Vector2 delta = io.MousePos - _resizeStartMouse;
                    resizeTargetSize = ApplyResize(delta);
                }
                else if (!_dragging && !_collapsed && !Config.WidgetLockSize && !AutoSize)
                {
                    // Probe edges in screen space and update cursor/start resize.
                    Vector2 curSize = _lastKnownSize.X > 0f ? _lastKnownSize : InitSize;
                    HandleResizeProbe(io, curSize);
                }
                else
                {
                    _hoveredEdge = ResizeEdge.None;
                }

                // ── Collapse animation ────────────────────────────────────────
                colT      = UI.Anim(CollapseAnimId, _collapsed ? 1f : 0f, 14f);
                animating = colT > 0.001f && colT < 0.999f;

                snapToExpanded = _wasAnimating && !animating && !_collapsed;
                _wasAnimating = animating || _collapsed;

                if (!_collapsed && !animating && !snapToExpanded && !_resizing && _lastKnownSize.Y > TitleH + 1f)
                    _expandedH = _lastKnownSize.Y;
                float expandH = _expandedH > TitleH + 1f ? _expandedH : InitSize.Y;
                float winH    = expandH + colT * (TitleH - expandH);

                // ── Window position / size ──────────────────────────────────
                Vector2 clampSize = new(_lastKnownSize.X > 0f ? _lastKnownSize.X : InitSize.X,
                                        _lastKnownSize.Y > 0f ? _lastKnownSize.Y : InitSize.Y);
                _winPos.X = Math.Clamp(_winPos.X, 0f, Math.Max(0f, io.DisplaySize.X - clampSize.X));
                _winPos.Y = Math.Clamp(_winPos.Y, 0f, Math.Max(0f, io.DisplaySize.Y - TitleH));

                ImGuiNET.ImGui.SetNextWindowPos(_winPos, ImGuiCond.Always);

                var autoSize = AutoWindowSize(io);
                if (autoSize is { } asz && !_collapsed && !animating)
                {
                    ImGuiNET.ImGui.SetNextWindowSize(asz, ImGuiCond.Always);
                    _expandedH    = asz.Y;
                    _sizeRestored = true;
                }
                else if (autoSize is { } csz && (_collapsed || animating))
                    ImGuiNET.ImGui.SetNextWindowSize(new Vector2(csz.X, winH), ImGuiCond.Always);
                else if (resizingNow)
                    ImGuiNET.ImGui.SetNextWindowSize(resizeTargetSize, ImGuiCond.Always);
                else if (_collapsed || animating)
                    ImGuiNET.ImGui.SetNextWindowSize(new Vector2(clampSize.X, winH), ImGuiCond.Always);
                else if (snapToExpanded)
                    ImGuiNET.ImGui.SetNextWindowSize(new Vector2(clampSize.X, expandH), ImGuiCond.Always);
                else if (!_sizeRestored)
                {
                    // First frame after loading a persisted size - force it with Always so ImGui's
                    // NoSavedSettings mode doesn't override it with InitSize via FirstUseEver.
                    ImGuiNET.ImGui.SetNextWindowSize(_lastKnownSize, ImGuiCond.Always);
                    _sizeRestored = true;
                }
                else
                    ImGuiNET.ImGui.SetNextWindowSize(InitSize, ImGuiCond.FirstUseEver);

                bool fullyCollapsed = _collapsed && !animating;
                bool atRest         = !_collapsed && !animating && !snapToExpanded && !resizingNow;
                ImGuiNET.ImGui.SetNextWindowSizeConstraints(
                    fullyCollapsed ? new Vector2(MinSize.X, TitleH) : atRest ? MinSize : new Vector2(MinSize.X, 1f),
                    fullyCollapsed ? new Vector2(MaxSize.X, TitleH) :                    MaxSize);
            }

            if (_requestFocus)
            {
                ImGuiNET.ImGui.SetNextWindowFocus();
                _requestFocus = false;
            }

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    Vector2.Zero);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   WinRound);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg,           Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ResizeGrip,         Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,  Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,   Vector4.Zero);

            bool visible = ImGuiNET.ImGui.Begin("##widgetchrome_" + WidgetTitle, ref open,
                ImGuiWindowFlags.NoTitleBar        |
                ImGuiWindowFlags.NoScrollbar       |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoResize          |
                ImGuiWindowFlags.NoMove            |
                ImGuiWindowFlags.NoNav             |
                ImGuiWindowFlags.NoSavedSettings);

            ImGuiNET.ImGui.PopStyleColor(4);
            ImGuiNET.ImGui.PopStyleVar(3);

            if (!visible || !open) { ImGuiNET.ImGui.End(); return; }

            Vector2 wp = ImGuiNET.ImGui.GetWindowPos();
            Vector2 ws = ImGuiNET.ImGui.GetWindowSize();
            _winPos        = wp; // always mirror the actual ImGui position so ContainsScreenPoint is never stale
            _lastKnownSize = ws;

            // Track focus for the focus-ring border highlight, and for cross-type
            // (widget vs panel) resize-occlusion arbitration in HandleResizeProbe.
            if (ImGuiNET.ImGui.IsWindowFocused())
            {
                _focusedTitle       = WidgetTitle;
                FocusedChromeTitle  = WidgetTitle;
                FocusedChromeMin    = wp;
                FocusedChromeMax    = wp + ws;
            }

            DrawChromeVisuals(wp, ws, colT);
            if (ws.Y > TitleH + 1f)
                DrawScrollableContent(wp, ws);
            int slot = 1;
            DrawCloseButton(wp, ws, ref open, slot++);
            if (!IsPoppedOut) DrawCollapseButton(wp, ws, colT, slot++);
            if (HasPanelMenu) DrawPanelMenuButton(wp, ws, slot++);
            // Grouped/docked widgets never reach here (Draw() early-returns above), so the
            // pop-out button is always offered on a standalone widget; hide it only while
            // popped-and-mid-close would be redundant - RedockRequested covers close instead.
            DrawPinButton(wp, ws, slot++);
            DrawPopoutButton(wp, ws, slot++);
            if (IsPoppedOut) DrawAlwaysOnTopButton(wp, ws, slot++);
            DrawDragRegion(ws, slot - 1);

            ImGuiNET.ImGui.End();
        }

        // ── Resize (screen-space) ─────────────────────────────────────────────

        // Called each frame when not already resizing/dragging.
        // Tests the mouse against the widget's screen-space edges and sets
        // cursor shape + starts a resize when the primary mouse button is clicked.
        // Running this BEFORE Begin() means child windows cannot steal the clicks.
        private void HandleResizeProbe(ImGuiIOPtr io, Vector2 size)
        {
            var mp = io.MousePos;

            // If a different chrome window (widget OR panel) currently holds global focus
            // - i.e. is visually on top - and its bounds cover this point, that window owns
            // any edge here. An occluded widget must not steal its hover/click just because
            // their rects also overlap the point.
            if (FocusedChromeTitle != null && FocusedChromeTitle != WidgetTitle &&
                mp.X >= FocusedChromeMin.X && mp.X < FocusedChromeMax.X &&
                mp.Y >= FocusedChromeMin.Y && mp.Y < FocusedChromeMax.Y)
            {
                _hoveredEdge = ResizeEdge.None;
                return;
            }

            float x = _winPos.X, y = _winPos.Y, w = size.X, h = size.Y;

            const float EdgeW    = 6f;
            const float CornerSz = 10f;

            // Corner rects (checked first - take priority over edge strips)
            bool inTL = mp.X >= x         && mp.X <= x + CornerSz && mp.Y >= y         && mp.Y <= y + CornerSz;
            bool inTR = mp.X >= x + w - CornerSz && mp.X <= x + w && mp.Y >= y         && mp.Y <= y + CornerSz;
            bool inBL = mp.X >= x         && mp.X <= x + CornerSz && mp.Y >= y + h - CornerSz && mp.Y <= y + h;
            bool inBR = mp.X >= x + w - CornerSz && mp.X <= x + w && mp.Y >= y + h - CornerSz && mp.Y <= y + h;

            // Edge strips (excluding corner squares already handled above)
            bool inLeft   = !inTL && !inBL && mp.X >= x         && mp.X <= x + EdgeW      && mp.Y > y + TitleH && mp.Y < y + h;
            bool inRight  = !inTR && !inBR && mp.X >= x + w - EdgeW && mp.X <= x + w      && mp.Y > y + TitleH && mp.Y < y + h;
            bool inTop    = !inTL && !inTR && mp.Y >= y         && mp.Y <= y + EdgeW      && mp.X > x + CornerSz && mp.X < x + w - CornerSz;
            bool inBottom = !inBL && !inBR && mp.Y >= y + h - EdgeW && mp.Y <= y + h     && mp.X > x + CornerSz && mp.X < x + w - CornerSz;

            bool onEdge = inTL || inTR || inBL || inBR || inLeft || inRight || inTop || inBottom;
            if (!onEdge) { _hoveredEdge = ResizeEdge.None; return; }

            _hoveredEdge = inTL ? ResizeEdge.TopLeft    : inTR ? ResizeEdge.TopRight   :
                           inBL ? ResizeEdge.BottomLeft : inBR ? ResizeEdge.BottomRight :
                           ResizeEdge.None;

            // Set cursor shape
            if      (inBR || inTL) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
            else if (inBL || inTR) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNESW);
            else if (inLeft || inRight) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            else                   ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);

            // Start resize on primary click
            if (!io.MouseClicked[0]) return;
            { uint _pf = (uint)ImGuiNET.ImGui.GetFrameCount(); if (_anyDraggingFrame == _pf || _anyDraggingFrame == _pf - 1u) return; }

            _resizing         = true;
            _resizeEdge       = inTL ? ResizeEdge.TopLeft    : inTR ? ResizeEdge.TopRight   :
                                inBL ? ResizeEdge.BottomLeft : inBR ? ResizeEdge.BottomRight :
                                inLeft ? ResizeEdge.Left     : inRight ? ResizeEdge.Right    :
                                inTop  ? ResizeEdge.Top      : ResizeEdge.Bottom;
            _resizeStartMouse = io.MousePos;
            _resizeStartPos   = _winPos;
            _resizeStartSize  = size;
        }

        private Vector2 ApplyResize(Vector2 delta)
        {
            float w = _resizeStartSize.X, h = _resizeStartSize.Y;
            float x = _resizeStartPos.X,  y = _resizeStartPos.Y;

            switch (_resizeEdge)
            {
                case ResizeEdge.Right:
                    w = Math.Max(MinSize.X, w + delta.X);
                    break;
                case ResizeEdge.Bottom:
                    h = Math.Max(MinSize.Y, h + delta.Y);
                    break;
                case ResizeEdge.Left:
                {
                    float nw = Math.Max(MinSize.X, w - delta.X);
                    x = _resizeStartPos.X + (w - nw);
                    w = nw;
                    break;
                }
                case ResizeEdge.Top:
                {
                    float nh = Math.Max(MinSize.Y, h - delta.Y);
                    y = _resizeStartPos.Y + (h - nh);
                    h = nh;
                    break;
                }
                case ResizeEdge.BottomRight:
                    w = Math.Max(MinSize.X, w + delta.X);
                    h = Math.Max(MinSize.Y, h + delta.Y);
                    break;
                case ResizeEdge.BottomLeft:
                {
                    float nw = Math.Max(MinSize.X, w - delta.X);
                    x = _resizeStartPos.X + (w - nw);
                    w = nw;
                    h = Math.Max(MinSize.Y, h + delta.Y);
                    break;
                }
                case ResizeEdge.TopRight:
                {
                    float nh = Math.Max(MinSize.Y, h - delta.Y);
                    y = _resizeStartPos.Y + (h - nh);
                    h = nh;
                    w = Math.Max(MinSize.X, w + delta.X);
                    break;
                }
                case ResizeEdge.TopLeft:
                {
                    float nw = Math.Max(MinSize.X, w - delta.X);
                    x = _resizeStartPos.X + (w - nw);
                    w = nw;
                    float nh = Math.Max(MinSize.Y, h - delta.Y);
                    y = _resizeStartPos.Y + (h - nh);
                    h = nh;
                    break;
                }
            }

            _winPos = new Vector2(x, y);
            return new Vector2(w, h);
        }

        // ── Chrome internals ──────────────────────────────────────────────────

        private void DrawChromeVisuals(Vector2 wp, Vector2 ws, float colT)
        {
            var     dl          = ImGuiNET.ImGui.GetWindowDrawList();
            Vector2 br          = new(wp.X + ws.X, wp.Y + ws.Y);

            dl.AddRectFilled(wp, br, ChromeBg, WinRound);

            var   fontB  = ImGuiStyle.FontBold;
            float fsizeB = fontB.FontSize > 0f ? fontB.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            // Title text - left inset matches the content Pad so title and body text align.
            string titleText = LocManager.Get(WidgetTitle);
            dl.AddText(fontB, fsizeB,
                new Vector2(wp.X + Pad, wp.Y + (TitleH - fsizeB) * .5f),
                ChromeTitleTxt, titleText);

            // Outer border
            dl.AddRect(wp, br, ChromeBorder, WinRound, ImDrawFlags.None, 1f);

            if (!AutoSize) DrawResizeHandles(wp, ws);
        }

        private void DrawResizeHandles(Vector2 wp, Vector2 ws)
        {
            if (ws.Y <= TitleH + 2f) return;

            var   dl     = ImGuiNET.ImGui.GetWindowDrawList();
            float r      = WinRound;
            const float Thick = 2f;

            ResizeEdge active = _resizing ? _resizeEdge : _hoveredEdge;

            // Arc centers match the rounded-rect corner centres used by AddRect.
            // PathArcToFast uses a 12-segment lookup (each step = 30°):
            //   0=right  3=down  6=left  9=up  (screen-space, y-axis down)
            DrawCornerHandle(dl, new Vector2(wp.X + r,          wp.Y + r),          active, ResizeEdge.TopLeft,     6,  9,  r, Thick);
            DrawCornerHandle(dl, new Vector2(wp.X + ws.X - r,   wp.Y + r),          active, ResizeEdge.TopRight,    9,  12, r, Thick);
            DrawCornerHandle(dl, new Vector2(wp.X + ws.X - r,   wp.Y + ws.Y - r),   active, ResizeEdge.BottomRight, 0,  3,  r, Thick);
            DrawCornerHandle(dl, new Vector2(wp.X + r,          wp.Y + ws.Y - r),   active, ResizeEdge.BottomLeft,  3,  6,  r, Thick);
        }

        private void DrawCornerHandle(ImGuiNET.ImDrawListPtr dl, Vector2 center,
            ResizeEdge active, ResizeEdge edge, int aMin, int aMax, float radius, float thick)
        {
            uint  animId = (uint)(WidgetTitle.GetHashCode() ^ ((int)edge * 0x9E3779B1));
            float t      = UI.Anim(animId, active == edge ? 1f : 0f, 14f);
            if (t < 0.01f) return;
            uint col = UI.Alpha(ImGuiStyle.Accent, (byte)(230 * t));
            // Draw an arc that traces the widget's rounded corner - sits just outside the border.
            dl.PathArcToFast(center, radius + 1f, aMin, aMax);
            dl.PathStroke(col, ImDrawFlags.None, thick);
        }

        // ── Title bar button slots (right-to-left: close, collapse, [panelmenu], pin) ──
        // Slot n's left edge sits at ws.X - (CloseBtnS+SlotGap)*n - 2f. Tighter gap than
        // ChromePanel's settings-panel buttons to match the smaller, more compact TitleH.
        internal const float SlotGap = 3f;
        private static float SlotX(Vector2 ws, int slot) => ws.X - (CloseBtnS + SlotGap) * slot - 2f;

        private void DrawCloseButton(Vector2 wp, Vector2 ws, ref bool open, int slot)
        {
            float btnX = SlotX(ws, slot);
            float btnY = (TitleH - CloseBtnS) * .5f;

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##wgtclose", new Vector2(CloseBtnS, CloseBtnS));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool clk = ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();
            if (clk)
            {
                open = false;
                // Closing while popped tears down the OS window too, rather than leaving
                // it orphaned - but unlike explicitly clicking "Return to app", this keeps
                // the pop-out intent (HideRequested, not RedockRequested), so re-enabling
                // the widget later re-creates the popout instead of dropping it into the
                // main app.
                if (IsPoppedOut) HideRequested?.Invoke(this);
            }

            float   t      = UI.Anim(id, hov ? 1f : 0f);
            Vector2 center = new(wp.X + btnX + CloseBtnS * .5f, wp.Y + btnY + CloseBtnS * .5f);
            float   r      = CloseBtnS * .5f - 1f;

            var dl = ImGuiNET.ImGui.GetWindowDrawList();

            dl.AddCircleFilled(center, r,
                UI.Lerp(UI.Alpha(ImGuiStyle.Col(0xFF, 0xFF, 0xFF), 10),
                         UI.Alpha(ImGuiStyle.Col(0x8A, 0x20, 0x20), 210), t));

            // AddLine at 45° antialias \ and / diagonals differently, producing an
            // asymmetric X regardless of endpoint math. Fix: draw each arm as a filled
            // parallelogram + a round cap circle at the tip. Filled triangles rasterise
            // identically in every direction - guaranteed symmetric.
            float cx  = center.X, cy = center.Y;
            float s   = 4.0f;         // arm reach from centre
            float hw  = 0.8f;         // arm half-width
            float off = hw * 0.7071f; // perpendicular offset = hw / √2
            uint  xc  = UI.Lerp(UI.Alpha(0xFFFFFFFF, 160), UI.Alpha(0xFFFFFFFF, 255), t);

            // \ arm - filled parallelogram
            dl.AddQuadFilled(
                new Vector2(cx - s + off, cy - s - off),
                new Vector2(cx + s + off, cy + s - off),
                new Vector2(cx + s - off, cy + s + off),
                new Vector2(cx - s - off, cy - s + off), xc);

            // / arm - filled parallelogram
            dl.AddQuadFilled(
                new Vector2(cx + s + off, cy - s + off),
                new Vector2(cx - s + off, cy + s + off),
                new Vector2(cx - s - off, cy + s - off),
                new Vector2(cx + s - off, cy - s - off), xc);

            // Round caps at each arm tip (makes arms look like strokes, not cut diamonds)
            dl.AddCircleFilled(new Vector2(cx - s, cy - s), hw, xc);
            dl.AddCircleFilled(new Vector2(cx + s, cy + s), hw, xc);
            dl.AddCircleFilled(new Vector2(cx + s, cy - s), hw, xc);
            dl.AddCircleFilled(new Vector2(cx - s, cy + s), hw, xc);
        }

        private void DrawCollapseButton(Vector2 wp, Vector2 ws, float colT, int slot)
        {
            var   dl   = ImGuiNET.ImGui.GetWindowDrawList();
            float btnX = SlotX(ws, slot);
            float btnY = (TitleH - CloseBtnS) * .5f;

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##wgtcollapse", new Vector2(CloseBtnS, CloseBtnS));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool clk = ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();
            if (clk)
            {
                if (!_collapsed) _expandedH = _lastKnownSize.Y;
                _collapsed = !_collapsed;
                _pendingSave = true;
            }

            float   t      = UI.Anim(id, hov ? 1f : 0f);
            Vector2 center = new(wp.X + btnX + CloseBtnS * .5f, wp.Y + btnY + CloseBtnS * .5f);
            float   r      = CloseBtnS * .5f - 1f;

            // Subtle rest bg, accent fill on hover
            dl.AddCircleFilled(center, r,
                UI.Lerp(UI.Alpha(ImGuiStyle.Col(0xFF, 0xFF, 0xFF), 10),
                         UI.Alpha(ImGuiStyle.Accent, 170), t));
            float cx = center.X, cy = center.Y;
            float s  = 4f, hs = s * 0.45f;
            float tipY  = cy + (-hs + (hs - (-hs)) * colT);
            float baseY = cy + ( hs + (-hs -   hs) * colT);
            uint  iconC = UI.Lerp(UI.Alpha(0xFFFFFFFF, 155), UI.Alpha(0xFFFFFFFF, 255), t);
            dl.AddLine(new Vector2(cx - s, baseY), new Vector2(cx,     tipY),  iconC, 1.6f);
            dl.AddLine(new Vector2(cx,     tipY),  new Vector2(cx + s, baseY), iconC, 1.6f);
        }

        private void DrawPanelMenuButton(Vector2 wp, Vector2 ws, int slot)
        {
            var   dl   = ImGuiNET.ImGui.GetWindowDrawList();
            float btnX = SlotX(ws, slot);
            float btnY = (TitleH - CloseBtnS) * .5f;

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##wgtpmenu", new Vector2(CloseBtnS, CloseBtnS));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool clk = ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();

            if (clk) ImGuiNET.ImGui.OpenPopup("##wgtpmenu_popup");

            float   t      = UI.Anim(id, hov ? 1f : 0f);
            Vector2 center = new(wp.X + btnX + CloseBtnS * .5f, wp.Y + btnY + CloseBtnS * .5f);
            float   r      = CloseBtnS * .5f - 1f;

            dl.AddCircleFilled(center, r,
                UI.Lerp(UI.Alpha(ImGuiStyle.Col(0xFF, 0xFF, 0xFF), 10),
                         UI.Alpha(ImGuiStyle.Accent, 170), t));

            uint iconC = UI.Lerp(UI.Alpha(0xFFFFFFFF, 155), UI.Alpha(0xFFFFFFFF, 255), t);
            dl.AddCircleFilled(new Vector2(center.X - 4f, center.Y), 1.5f, iconC);
            dl.AddCircleFilled(new Vector2(center.X,      center.Y), 1.5f, iconC);
            dl.AddCircleFilled(new Vector2(center.X + 4f, center.Y), 1.5f, iconC);

            Vector2 anchor = new(wp.X + btnX, wp.Y + TitleH + 2f);
            if (UI.BeginDropdownMenu("##wgtpmenu_popup", anchor))
            {
                DrawPanelMenu();
                UI.EndDropdownMenu();
            }
        }

        private void DrawPinButton(Vector2 wp, Vector2 ws, int slot)
        {
            float btnX = SlotX(ws, slot);
            float btnY = (TitleH - CloseBtnS) * .5f;

            bool pinned = Config.WidgetOpen.IsPinned(WidgetTitle);

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##wgtpin", new Vector2(CloseBtnS, CloseBtnS));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool clk = ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();
            if (clk)
            {
                Config.WidgetOpen.TogglePin(WidgetTitle);
                Config.SaveDebounced();
            }

            var     dl     = ImGuiNET.ImGui.GetWindowDrawList();
            float   t      = UI.Anim(id, hov ? 1f : 0f);
            Vector2 center = new(wp.X + btnX + CloseBtnS * .5f, wp.Y + btnY + CloseBtnS * .5f);
            float   r      = CloseBtnS * .5f - 1f;

            uint restBg   = pinned ? UI.Alpha(ImGuiStyle.Accent, 55) : UI.Alpha(ImGuiStyle.Col(0xFF, 0xFF, 0xFF), 10);
            uint hoverBg  = UI.Alpha(ImGuiStyle.Accent, 170);
            dl.AddCircleFilled(center, r, UI.Lerp(restBg, hoverBg, t));

            uint  iconC = pinned
                ? UI.Lerp(UI.Alpha(ImGuiStyle.Accent, 230), 0xFFFFFFFFu, t)
                : UI.Lerp(UI.Alpha(0xFFFFFFFF, 130), UI.Alpha(0xFFFFFFFF, 235), t);
            string icon = pinned ? "+" : "o";
            var    font = ImGuiStyle.FontBold;
            float  fs   = (font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight()) - 2f;
            var    ts   = font.CalcTextSizeA(fs, float.MaxValue, -1f, icon);
            dl.AddText(font, fs,
                new Vector2(center.X - ts.X * .5f, center.Y - ts.Y * .5f),
                iconC, icon);

            UI.TooltipForBounds(
                new Vector2(wp.X + btnX, wp.Y + btnY),
                new Vector2(wp.X + btnX + CloseBtnS, wp.Y + btnY + CloseBtnS),
                pinned ? "Pinned - always on top\nClick to unpin." : "Click to pin on top of all others.");
        }

        private void DrawPopoutButton(Vector2 wp, Vector2 ws, int slot)
        {
            float btnX = SlotX(ws, slot);
            float btnY = (TitleH - CloseBtnS) * .5f;

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##wgtpopout", new Vector2(CloseBtnS, CloseBtnS));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool clk = ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();
            if (clk)
            {
                if (IsPoppedOut) RedockRequested?.Invoke(this);
                else             PopOutRequested?.Invoke(this);
            }

            var     dl     = ImGuiNET.ImGui.GetWindowDrawList();
            float   t      = UI.Anim(id, hov ? 1f : 0f);
            Vector2 center = new(wp.X + btnX + CloseBtnS * .5f, wp.Y + btnY + CloseBtnS * .5f);
            float   r      = CloseBtnS * .5f - 1f;

            uint restBg  = IsPoppedOut ? UI.Alpha(ImGuiStyle.Accent, 55) : UI.Alpha(ImGuiStyle.Col(0xFF, 0xFF, 0xFF), 10);
            uint hoverBg = UI.Alpha(ImGuiStyle.Accent, 170);
            dl.AddCircleFilled(center, r, UI.Lerp(restBg, hoverBg, t));

            // Small window-with-arrow glyph: box + diagonal arrow through the top-right
            // corner. Same glyph either way - direction of intent is carried by the tooltip.
            uint iconC = UI.Lerp(UI.Alpha(0xFFFFFFFF, 155), UI.Alpha(0xFFFFFFFF, 235), t);
            float bx = center.X - 3.5f, by = center.Y - 2.5f, bw = 6f, bh = 5f;
            dl.AddRect(new Vector2(bx, by), new Vector2(bx + bw, by + bh), iconC, 0f, ImDrawFlags.None, 1.1f);
            Vector2 aTip = new(center.X + 4.5f, center.Y - 4.5f);
            Vector2 aTail = new(center.X + 0.5f, center.Y - 0.5f);
            dl.AddLine(aTail, aTip, iconC, 1.1f);
            dl.AddLine(aTip, new Vector2(aTip.X - 3.2f, aTip.Y), iconC, 1.1f);
            dl.AddLine(aTip, new Vector2(aTip.X, aTip.Y + 3.2f), iconC, 1.1f);

            UI.TooltipForBounds(
                new Vector2(wp.X + btnX, wp.Y + btnY),
                new Vector2(wp.X + btnX + CloseBtnS, wp.Y + btnY + CloseBtnS),
                IsPoppedOut ? "Return to app" : "Pop out of app\nMove this widget into its own window.");
        }

        private void DrawAlwaysOnTopButton(Vector2 wp, Vector2 ws, int slot)
        {
            float btnX = SlotX(ws, slot);
            float btnY = (TitleH - CloseBtnS) * .5f;

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##wgtaontop", new Vector2(CloseBtnS, CloseBtnS));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool clk = ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();
            if (clk)
            {
                AlwaysOnTop = !AlwaysOnTop;
                _pendingSave = true;
                AlwaysOnTopToggled?.Invoke(this);
            }

            var     dl     = ImGuiNET.ImGui.GetWindowDrawList();
            float   t      = UI.Anim(id, hov ? 1f : 0f);
            Vector2 center = new(wp.X + btnX + CloseBtnS * .5f, wp.Y + btnY + CloseBtnS * .5f);
            float   r      = CloseBtnS * .5f - 1f;

            uint restBg  = AlwaysOnTop ? UI.Alpha(ImGuiStyle.Accent, 55) : UI.Alpha(ImGuiStyle.Col(0xFF, 0xFF, 0xFF), 10);
            uint hoverBg = UI.Alpha(ImGuiStyle.Accent, 170);
            dl.AddCircleFilled(center, r, UI.Lerp(restBg, hoverBg, t));

            // Simple upward pin/thumbtack glyph.
            uint iconC = AlwaysOnTop
                ? UI.Lerp(UI.Alpha(ImGuiStyle.Accent, 230), 0xFFFFFFFFu, t)
                : UI.Lerp(UI.Alpha(0xFFFFFFFF, 130), UI.Alpha(0xFFFFFFFF, 235), t);
            dl.AddCircleFilled(new Vector2(center.X, center.Y - 2.5f), 2.2f, iconC);
            dl.AddLine(new Vector2(center.X, center.Y - 0.5f), new Vector2(center.X, center.Y + 4f), iconC, 1.3f);

            UI.TooltipForBounds(
                new Vector2(wp.X + btnX, wp.Y + btnY),
                new Vector2(wp.X + btnX + CloseBtnS, wp.Y + btnY + CloseBtnS),
                AlwaysOnTop ? "Always on top - enabled\nClick to allow other windows above it." : "Keep this window above all other applications.");
        }

        private void DrawDragRegion(Vector2 ws, int buttonCount)
        {
            float rightW = (CloseBtnS + SlotGap) * buttonCount + 12f;
            ImGuiNET.ImGui.SetCursorPos(Vector2.Zero);
            ImGuiNET.ImGui.InvisibleButton("##wgtdrag",
                new Vector2(ws.X - rightW, TitleH));

            if (ImGuiNET.ImGui.IsItemActivated())
            {
                if (IsPoppedOut && PopoutHwnd != IntPtr.Zero)
                {
                    // Native OS window drag - lets Windows itself own the move, including
                    // across monitors, exactly like the app's own title bar drag
                    // (MenuManager.cs) does for the main window.
                    Win32.ReleaseCapture();
                    Win32.PostMessage(PopoutHwnd, Win32.WM_NCLBUTTONDOWN, new IntPtr(Win32.HTCAPTION), IntPtr.Zero);
                }
                else
                {
                    _dragging        = true;
                    _dragStartMouse  = ImGuiNET.ImGui.GetIO().MousePos;
                    _dragStartWinPos = _winPos;
                    DragSource       = this;
                }
            }

            if (ImGuiNET.ImGui.IsItemHovered() &&
                ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) &&
                !IsPoppedOut)
            {
                _collapsed   = !_collapsed;
                _pendingSave = true;
            }

            if (!_dragging)
                _winPos = ImGuiNET.ImGui.GetWindowPos();
        }

        private void DrawScrollableContent(Vector2 wp, Vector2 ws)
        {
            bool  pad      = HasContentPadding;
            float botGap   = HasBottomGap ? 6f : 0f;
            float contentH = ws.Y - TitleH - (pad ? ContentTopGap : 0f) - botGap;
            ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, TitleH));

            DrawContentBody("##wgtscroll_" + WidgetTitle, new Vector2(ws.X, contentH),
                pad, HasScrollWindowPadding, ChromeScrollExtraFlags, ChromeScrollThumb,
                DrawWidgetContent, allowUnfocusClick: true, suppressHotkeyClicks: true);

            DrawAccentLine("##wgtaccent_" + WidgetTitle, ws, TitleH);
        }

        // ── Shared content-body / accent-line renderers ────────────────────────
        // Used by every widget "content surface" in the system: a standalone WidgetChrome's
        // own content (above), a WidgetManager dock slot, a WidgetGroup's active member, and
        // a WidgetSplit's pane. Centralising this here is what keeps the padding/gap/scrollbar
        // styling identical everywhere instead of drifting across independently hand-copied
        // implementations (which is exactly how the content-gap ended up inconsistent before).

        /// <summary>
        /// Draws the themed, wheel-scrollable content child shared by every widget content
        /// surface. <paramref name="allowUnfocusClick"/> and <paramref name="suppressHotkeyClicks"/>
        /// are only meaningful for a standalone WidgetChrome's own top-level content.
        /// </summary>
        internal static void DrawContentBody(
            string childId, Vector2 size, bool hasPad, bool hasPadX,
            ImGuiWindowFlags extraFlags, uint scrollThumb, Action drawContent,
            bool allowUnfocusClick = false, bool suppressHotkeyClicks = false)
        {
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,     new Vector2(hasPadX ? Pad : 0f, 0f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize,     5f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 3f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg,              Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        UI.ToV4(UI.Alpha(scrollThumb, 90)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, UI.ToV4(UI.Alpha(scrollThumb, 200)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  UI.ToV4(ImGuiStyle.Accent));

            // NoScrollWithMouse prevents UpdateMouseWheel from auto-scrolling this child.
            // We apply the wheel manually below so normal scrolling still works.
            bool inChild = ImGuiNET.ImGui.BeginChild(childId, size,
                ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse | extraFlags);

            ImGuiNET.ImGui.PopStyleColor(5);
            ImGuiNET.ImGui.PopStyleVar(3);

            if (inChild)
            {
                // SetCursorPosY (not Dummy) - a Dummy item would pick up the default
                // ItemSpacing.Y gap before the next item, silently adding on top of ContentTopGap.
                if (hasPad)
                    ImGuiNET.ImGui.SetCursorPosY(ContentTopGap);

                // Suppress LMB click events while a hotkey is being captured so that
                // pressing M1 (or any mouse button) to bind a key doesn't simultaneously
                // activate checkboxes, buttons, or sliders in the widget content.
                if (suppressHotkeyClicks && UI.IsCapturingHotkey)
                    ImGuiNET.ImGui.GetIO().MouseClicked[0] = false;

                UI.ClearWheelConsumed();
                drawContent();

                // Wheel not consumed by a widget: apply it manually to this content area.
                if (!UI.WheelConsumedThisFrame)
                {
                    float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
                    if (wheel != 0f && ImGuiNET.ImGui.IsWindowHovered())
                    {
                        float step = ImGuiNET.ImGui.GetTextLineHeight() * 3f;
                        ImGuiNET.ImGui.SetScrollY(
                            Math.Clamp(ImGuiNET.ImGui.GetScrollY() - wheel * step,
                                       0f, ImGuiNET.ImGui.GetScrollMaxY()));
                        ImGuiNET.ImGui.GetIO().MouseWheel = 0f;
                    }
                }

                if (allowUnfocusClick &&
                    ImGuiNET.ImGui.IsWindowHovered() &&
                    ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                    !ImGuiNET.ImGui.IsAnyItemHovered())
                {
                    Vector2 mp  = ImGuiNET.ImGui.GetMousePos();
                    Vector2 cur = ImGuiNET.ImGui.GetCursorScreenPos();
                    ImGuiNET.ImGui.SetCursorScreenPos(mp - Vector2.One);
                    ImGuiNET.ImGui.InvisibleButton("##wgt_unfocus", new Vector2(2f, 2f));
                    ImGuiNET.ImGui.SetCursorScreenPos(cur);
                }
            }
            ImGuiNET.ImGui.EndChild();
        }

        /// <summary>
        /// Draws the 1.5px accent-line highlight immediately below a title bar. A dedicated
        /// child window (rather than GetForegroundDrawList) confines it to the caller's own
        /// bounds so it can never bleed onto whatever's underneath.
        /// </summary>
        internal static void DrawAccentLine(string childId, Vector2 ws, float titleH)
        {
            ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, titleH));
            ImGuiNET.ImGui.BeginChild(childId, new Vector2(ws.X, 1f), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs);
            var dl  = ImGuiNET.ImGui.GetWindowDrawList();
            var pos = ImGuiNET.ImGui.GetWindowPos();
            dl.AddLine(pos, new Vector2(pos.X + ws.X, pos.Y), UI.Alpha(ImGuiStyle.Accent, 210), 1.5f);
            ImGuiNET.ImGui.EndChild();
        }
    }
}
