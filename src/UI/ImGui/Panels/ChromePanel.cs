using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using SC2Radar.UI.Misc;
using SC2Radar.Misc;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Panels
{
    /// <summary>
    /// Abstract base for custom-chrome floating panels.
    /// Manages drag, collapse animation, close/collapse buttons, scrollable content,
    /// all-edge resize, screen-edge snapping, focus ring, and title bar badges.
    /// Subclasses implement PanelTitle, InitSize, MinSize, and DrawPanelContent().
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public abstract class ChromePanel : ImGuiPanel
    {
        // ── Chrome layout constants ───────────────────────────────────────────
        protected const float TitleH    = 30f;
        protected const float Pad       = 8f;
        protected const float WinRound  = 8f;
        protected const float CloseBtnS = 20f;

        // ── Chrome colours (independently themeable - see General Settings ▸ Colors ▸
        // Panels, and InterfaceColorOptions.SetColors) ─────────────────────────
        protected virtual uint ChromeBg          => ImGuiStyle.MainPanelBg;
        protected virtual uint ChromeTitleBg     => ImGuiStyle.MainPanelTitleBg;
        protected virtual uint ChromeBorder      => ImGuiStyle.MainPanelBorder;
        protected virtual uint ChromeTitleTxt    => ImGuiStyle.MainPanelTitleText;
        protected virtual uint ChromeScrollThumb => ImGuiStyle.MainPanelScrollThumb;

        // ── Panel identity ────────────────────────────────────────────────────
        protected abstract string PanelTitle { get; }

        // ── Content hooks ─────────────────────────────────────────────────────
        protected abstract void DrawPanelContent();

        public virtual void NavigateTo(IReadOnlyList<string> tabPath) { }

        // ── Key pre-registration ──────────────────────────────────────────────

        /// <summary>
        /// Renders the panel's content once in a hidden off-screen window so every
        /// LocManager.Get() call fires and registers the key + panel/tab location.
        /// Panels with multiple tabs should override this and call
        /// PreRegisterCurrentState() once per tab after setting the active tab index.
        /// </summary>
        public virtual void PreRegisterKeys() => PreRegisterCurrentState();

        /// <summary>
        /// Renders DrawPanelContent() inside a minimal off-screen ImGui window.
        /// Safe to call during any ImGui frame - the window is invisible and inert.
        /// </summary>
        protected void PreRegisterCurrentState()
        {
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(-99999f, -99999f), ImGuiCond.Always);
            ImGuiNET.ImGui.SetNextWindowSize(InitSize, ImGuiCond.Always);
            ImGuiNET.ImGui.SetNextWindowBgAlpha(0f);
            LocManager.PushPanel(PanelTitle);
            if (ImGuiNET.ImGui.Begin("##prereg_" + PanelTitle,
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
                ImGuiWindowFlags.NoNav        | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing))
            {
                try { DrawPanelContent(); }
                catch (Exception ex)
                {
                    // A throw here would otherwise be swallowed silently, leaving any
                    // UI.BeginDisabled()/EndDisabled() pair straddling the failure point
                    // unbalanced - which permanently disables every control in every
                    // panel for the rest of the session. Log it and self-heal.
                    XMLogging.Write(AppLogLevel.Error, $"[ChromePanel] PreRegisterKeys threw in '{PanelTitle}': {ex}");
                    UI.ResetDisabledState();
                }
            }
            ImGuiNET.ImGui.End();
            LocManager.PopPanel();
        }

        // ── Title bar badge ───────────────────────────────────────────────────
        /// <summary>
        /// Override to show a small pill badge in the title bar next to the panel name.
        /// Return null (default) to show no badge.
        /// </summary>
        protected virtual string TitleBadge      => null;
        protected virtual uint   TitleBadgeColor => ImGuiStyle.Accent;

        // ── Title bar master switch ───────────────────────────────────────────
        /// <summary>
        /// Override to place a clickable enabled/disabled pill on the right side of
        /// the title bar. Return null (default) to show nothing.
        /// The pill replaces the per-panel master-toggle group box.
        /// </summary>
        protected virtual (string Label, bool Value, Action<bool> OnChange)? TitleMasterSwitch => null;
        protected virtual string TitleMasterSwitchHint => null;

        /// <summary>
        /// Optional second title-bar pill switch, drawn immediately to the left of
        /// <see cref="TitleMasterSwitch"/>. Null (default) draws nothing -- opt-in, no effect on
        /// panels that don't use it.
        /// </summary>
        protected virtual (string Label, bool Value, Action<bool> OnChange)? TitleSecondarySwitch => null;
        protected virtual string TitleSecondarySwitchHint => null;

        private const float MasterSwitchPillPadX = 8f;
        private const float MasterSwitchPillH    = 20f;
        private const float MasterSwitchGap      = 8f;

        private float SwitchPillWidth((string Label, bool Value, Action<bool> OnChange) sw)
        {
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float textW = font.CalcTextSizeA(fsize, float.MaxValue, -1f, LocManager.Get(sw.Label)).X;
            return textW + MasterSwitchPillPadX * 2f;
        }

        private float MasterSwitchReservedW()
        {
            float w = 0f;
            var ms = TitleMasterSwitch;
            if (ms.HasValue) w += SwitchPillWidth(ms.Value) + MasterSwitchGap;
            var ss = TitleSecondarySwitch;
            if (ss.HasValue) w += SwitchPillWidth(ss.Value) + MasterSwitchGap;
            return w;
        }

        private float DrawSwitchPill(Vector2 wp, float rightEdgeX, float by,
            (string Label, bool Value, Action<bool> OnChange) sw, string hint, string idSuffix)
        {
            var (label, value, onChange) = sw;
            var   dl    = ImGuiNET.ImGui.GetWindowDrawList();
            var   font  = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            string text  = LocManager.Get(label);
            float  pillW = SwitchPillWidth(sw);
            float  pillH = MasterSwitchPillH;

            float bx = rightEdgeX - pillW;

            Vector2 pTL = new(wp.X + bx, wp.Y + by);
            Vector2 pBR = new(wp.X + bx + pillW, wp.Y + by + pillH);

            ImGuiNET.ImGui.SetCursorPos(new Vector2(bx, by));
            ImGuiNET.ImGui.InvisibleButton("##titleswitch" + idSuffix, new Vector2(pillW, pillH));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            uint sid = ImGuiNET.ImGui.GetItemID();
            if (ImGuiNET.ImGui.IsItemClicked()) onChange?.Invoke(!value);
            if (hov) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (!string.IsNullOrEmpty(hint)) UI.TooltipForBounds(pTL, pBR, hint);

            float th = UI.Anim(sid,          hov   ? 1f : 0f, 12f);
            float tv = UI.Anim(sid ^ 0xEEu,  value ? 1f : 0f, 10f);

            uint bgCol = UI.Lerp(
                UI.Alpha(ImGuiStyle.InputBg, 180),
                UI.Alpha(ImGuiStyle.Accent, (byte)(55 + 25 * tv)),
                tv);
            bgCol = UI.Lerp(bgCol, UI.Alpha(ImGuiStyle.AccentHover, 70), th * 0.35f);

            uint borderCol = UI.Lerp(
                UI.Alpha(ChromeBorder, (byte)(160 + 60 * th)),
                UI.Alpha(ImGuiStyle.Accent, (byte)(130 + 80 * th)),
                tv);

            dl.AddRectFilled(pTL, pBR, bgCol, pillH * .5f);
            dl.AddRect(pTL, pBR, borderCol, pillH * .5f, ImDrawFlags.None, 1f);

            uint textCol = UI.Lerp(
                UI.Alpha(ChromeTitleTxt, 110),
                ChromeTitleTxt,
                tv);
            dl.AddText(font, fsize,
                new Vector2(pTL.X + MasterSwitchPillPadX, pTL.Y + (pillH - fsize) * .5f),
                textCol, text);

            return bx;
        }

        private void DrawTitleSwitch(Vector2 wp, Vector2 ws)
        {
            var ms = TitleMasterSwitch;
            var ss = TitleSecondarySwitch;
            if (!ms.HasValue && !ss.HasValue) return;

            float baseRightW = HasPanelMenu ? CloseBtnS * 3f + 18f : CloseBtnS * 2f + 16f;
            float by = (TitleH - MasterSwitchPillH) * .5f;
            float rightEdgeX = ws.X - baseRightW - MasterSwitchGap;

            if (ms.HasValue)
                rightEdgeX = DrawSwitchPill(wp, rightEdgeX, by, ms.Value, TitleMasterSwitchHint, "") - MasterSwitchGap;

            if (ss.HasValue)
                DrawSwitchPill(wp, rightEdgeX, by, ss.Value, TitleSecondarySwitchHint, "2");
        }

        // ── Chrome scroll child extra flags ───────────────────────────────────
        /// <summary>
        /// Extra ImGuiWindowFlags merged into the chrome scroll child.
        /// Override with NoScrollbar when the panel manages its own inner scroll
        /// child and doesn't need the chrome-level scrollbar to appear.
        /// </summary>
        protected virtual ImGuiWindowFlags ChromeScrollExtraFlags => ImGuiWindowFlags.None;

        // ── Content padding ───────────────────────────────────────────────────
        /// <summary>
        /// When true (default) the chrome adds 8 px side padding and a 13 px top/bottom
        /// gap around panel content. Set to false for panels that own their own full-bleed
        /// layout (e.g. IRC chat) so content fills edge-to-edge below the title bar.
        /// </summary>
        protected virtual bool HasContentPadding    => true;
        protected virtual bool HasBottomGap         => !HasContentPadding;
        // Controls only the WindowPadding (8px sides) of the scroll container child.
        // Override to false when the panel manages its own horizontal padding internally
        // (e.g. GroupPanel, which delegates padding to its per-member ##grpmember_ child).
        protected virtual bool HasScrollWindowPadding => HasContentPadding;

        // ── Panel menu (ellipsis button) ──────────────────────────────────────
        /// <summary>
        /// Return true to show the ellipsis (…) button in the title bar.
        /// Override DrawPanelMenu() to populate the dropdown.
        /// </summary>
        protected virtual bool HasPanelMenu => false;
        /// <summary>Called inside the panel-menu dropdown popup.</summary>
        protected virtual void DrawPanelMenu() { }

        // ── Close interception ──────────────────────────────────────────────────
        /// <summary>
        /// Called once, the frame the close button is clicked, before the panel is
        /// actually closed. Returning false vetoes the close for that frame - the
        /// panel is responsible for closing itself later (e.g. after showing its own
        /// confirmation modal) via whatever mechanism its caller uses to flip the
        /// backing "show" flag off.
        /// </summary>
        protected virtual bool OnCloseRequested() => true;

        // ── Title bar decoration hook ─────────────────────────────────────────
        /// <summary>
        /// Override to draw additional visual elements in the title bar (e.g. a sync-status dot).
        /// Called after the badge and before the outer border is drawn.
        /// <paramref name="dl"/> is the window draw list. <paramref name="wp"/> is the window's
        /// screen-space top-left; <paramref name="ws"/> is its size.
        /// You may call ImGui widget functions here (e.g. IsMouseHoveringRect, SetTooltip).
        /// </summary>
        protected virtual void DrawTitleDecoration(ImDrawListPtr dl, Vector2 wp, Vector2 ws) { }

        /// <summary>
        /// Override to reserve pixels on the right side of the drag region for custom
        /// title bar decorations drawn via <see cref="DrawTitleDecoration"/>.
        /// The decoration should be placed left of the close/collapse buttons by at least
        /// this many pixels so it doesn't overlap the drag area.
        /// </summary>
        protected virtual float TitleDecorationReservedW => 0f;

        // ── Group / host support ──────────────────────────────────────────────

        /// <summary>When true this panel belongs to a GroupPanel - Draw() is a no-op.</summary>
        internal bool IsGrouped { get; set; }

        /// <summary>
        /// Set each frame by MenuManager from the panel's show-flag.
        /// GroupPanel hides this panel's tab when false but keeps it as a member,
        /// so re-enabling the flag makes the tab reappear without re-joining the group.
        /// </summary>
        internal bool IsVisible { get; set; } = true;

        /// <summary>
        /// When true, DrawPanelContent() is being called from inside a GroupPanel host.
        /// DrawTabHeader() skips the leading-dummy offset cancellation in this mode.
        /// </summary>
        internal bool IsHosted { get; set; }

        /// <summary>Screen edge this panel is anchored to. None = floating.</summary>
        internal DockEdge DockedEdge { get; set; } = DockEdge.None;

        /// <summary>True while the panel is docked to a screen edge; Draw() is a no-op.</summary>
        internal bool IsDocked => DockedEdge != DockEdge.None;

        /// <summary>Exposes PanelTitle (localized) for GroupPanel membership tracking.</summary>
        internal string Title => LocManager.Get(PanelTitle);

        /// <summary>
        /// True while this panel is minimized to its title bar only - DrawPanelContent()
        /// does not run in this state, so subclasses can treat it as "not being looked at."
        /// </summary>
        internal bool IsCollapsed => _collapsed;

        /// <summary>
        /// Absolute screen-space X of the right edge of the TitleBadge pill (or the title
        /// text itself, if TitleBadge is null), set just before DrawTitleDecoration runs so
        /// subclasses can position their own title-bar decorations immediately after it
        /// without re-deriving the badge's font/padding geometry.
        /// </summary>
        protected float TitleBadgeRightX { get; private set; }

        /// <summary>Current screen-space position (updated every frame after Draw).</summary>
        internal Vector2 ScreenPos => _winPos;

        /// <summary>Current screen-space size (updated every frame after Draw).</summary>
        internal Vector2 ScreenSize => _lastKnownSize.X > 0f ? _lastKnownSize : InitSize;

        /// <summary>Teleports the panel to <paramref name="pos"/> (screen-space top-left).</summary>
        internal void SetPosition(Vector2 pos)
        {
            _winPos      = pos;
            _placed      = true; // prevent first-Draw centering from overriding explicit placement
            _pendingSave = true;
        }

        /// <summary>
        /// Forces a specific size on the first Draw() call, overriding InitSize.
        /// Used when creating a split so the new chrome window inherits the target panel's
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
        /// Forces this panel out of a collapsed state and requests focus. Needed when opening
        /// the panel programmatically (e.g. a radar right-click edit request): if the panel was
        /// left collapsed, Draw() only renders the title bar - DrawScrollableContent (and thus
        /// DrawPanelContent, where pending edit requests are consumed) never runs while
        /// ws.Y &lt;= TitleH, so a collapsed panel would silently swallow the request instead of
        /// showing the pre-filled modal.
        /// </summary>
        internal void ForceExpand()
        {
            if (_collapsed)
            {
                _collapsed = false;
                UI.SeedAnim(CollapseAnimId, 0f);
                _pendingSave = true;
            }
            _requestFocus = true;
        }

        /// <summary>
        /// Called each frame by the dock-slot renderer so ContainsScreenPoint stays
        /// accurate even though docked panels skip their own Draw().
        /// </summary>
        internal void UpdateScreenRect(Vector2 pos, Vector2 size)
        {
            _winPos        = pos;
            _lastKnownSize = size;
        }

        /// <summary>Returns true if <paramref name="p"/> falls inside this panel's screen bounds.</summary>
        internal bool ContainsScreenPoint(Vector2 p)
        {
            var pos = _winPos;
            var sz  = _lastKnownSize.X > 0f ? _lastKnownSize : InitSize;
            return p.X >= pos.X && p.X < pos.X + sz.X && p.Y >= pos.Y && p.Y < pos.Y + sz.Y;
        }

        /// <summary>Calls DrawPanelContent() - used by GroupPanel to render a hosted member.</summary>
        internal void InvokeDrawContent() => DrawPanelContent();

        /// <summary>Exposes TitleBadge for GroupPanel to proxy.</summary>
        internal string GetTitleBadge() => TitleBadge;

        /// <summary>Exposes TitleMasterSwitch for GroupPanel to proxy.</summary>
        internal (string, bool, Action<bool>)? GetTitleMasterSwitch() => TitleMasterSwitch;

        /// <summary>Exposes HasContentPadding for GroupPanel and DrawDockedSlot to proxy.</summary>
        internal bool GetHasContentPadding() => HasContentPadding;
        /// <summary>Exposes HasScrollWindowPadding for DrawDockedSlot.</summary>
        internal bool GetHasScrollWindowPadding() => HasScrollWindowPadding;

        /// <summary>Exposes ChromeScrollExtraFlags for GroupPanel to proxy.</summary>
        internal ImGuiWindowFlags GetChromeScrollExtraFlags() => ChromeScrollExtraFlags;

        /// <summary>Exposes HasPanelMenu for GroupPanel and SplitPanel to proxy.</summary>
        internal bool GetHasPanelMenu() => HasPanelMenu;
        /// <summary>Exposes DrawPanelMenu for GroupPanel and SplitPanel to proxy.</summary>
        internal void InvokeDrawPanelMenu() => DrawPanelMenu();

        /// <summary>Exposes InitSize for GroupPanel to compute its own initial size.</summary>
        internal Vector2 GetInitSize() => InitSize;

        // Expose chrome colours so dock slots can exactly match the panel's visual theme.
        internal uint GetChromeBg()          => ChromeBg;
        internal uint GetChromeTitleBg()     => ChromeTitleBg;
        internal uint GetChromeBorder()      => ChromeBorder;
        internal uint GetChromeTitleTxt()    => ChromeTitleTxt;
        internal uint GetChromeScrollThumb() => ChromeScrollThumb;

        // ── Static drag source (set when any panel starts dragging; cleared by MenuManager) ──
        internal static ChromePanel DragSource { get; private set; }
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

        private uint CollapseAnimId => (uint)PanelTitle.GetHashCode() ^ 0xC011_0000u;

        // ── Sealed Draw ───────────────────────────────────────────────────────
        public sealed override void Draw(ref bool open)
        {
            if (!open) return;
            if (IsGrouped) return; // rendered by GroupPanel - skip standalone draw
            if (IsDocked)  return; // rendered by MenuManager dock slot

            var io = ImGuiNET.ImGui.GetIO();

            if (!_placed)
            {
                _placed = true;
                if (Config.PanelLayouts.TryGetValue(PanelTitle, out var saved) &&
                    saved.W > 0f && saved.H > 0f)
                {
                    // Clamp loaded size to MinSize so a corrupted or stale config
                    // value can never produce a panel smaller than its design minimum.
                    float clampedW = Math.Max(MinSize.X, saved.W);
                    float clampedH = Math.Max(MinSize.Y, saved.H);

                    _winPos        = new Vector2(saved.X, saved.Y);
                    _lastKnownSize = new Vector2(clampedW, clampedH);
                    _expandedH     = clampedH;
                    _collapsed     = saved.Collapsed;

                    if (_collapsed)
                    {
                        // Pre-seed the collapse animation to 1 so the panel renders
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
                Config.PanelLayouts[PanelTitle] = new PanelLayoutEntry
                {
                    X = _winPos.X,        Y = _winPos.Y,
                    W = _lastKnownSize.X, H = _lastKnownSize.Y,
                    Collapsed = _collapsed
                };
                Config.SaveDebounced();
            }

            // ── Drag ──────────────────────────────────────────────────────────
            if (_dragging && io.MouseDown[0])
            {
                _winPos = _dragStartWinPos + (io.MousePos - _dragStartMouse);

                // Screen-edge snapping (8 px threshold). Top snaps to just below the main
                // menu bar (not y=0) so panels can never be dragged up to cover it.
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
            //    windows cannot block the edge hit regions) ───────────────────
            uint  _cf             = (uint)ImGuiNET.ImGui.GetFrameCount();
            bool  _otherDragging  = !_dragging && (_anyDraggingFrame == _cf || _anyDraggingFrame == _cf - 1u);
            bool resizingNow = _resizing && io.MouseDown[0] && !_collapsed && !_otherDragging && !Config.LockPanels;
            if (!resizingNow && _resizing) { _resizing = false; _pendingSave = true; }

            Vector2 resizeTargetSize = default;
            if (resizingNow)
            {
                Vector2 delta = io.MousePos - _resizeStartMouse;
                resizeTargetSize = ApplyResize(delta);
            }
            else if (!_dragging && !_collapsed && !Config.LockPanels)
            {
                // Probe edges in screen space and update cursor/start resize.
                Vector2 curSize = _lastKnownSize.X > 0f ? _lastKnownSize : InitSize;
                HandleResizeProbe(io, curSize);
            }
            else
            {
                _hoveredEdge = ResizeEdge.None;
            }

            // ── Collapse animation ────────────────────────────────────────────
            float colT      = UI.Anim(CollapseAnimId, _collapsed ? 1f : 0f, 14f);
            bool  animating = colT > 0.001f && colT < 0.999f;

            bool snapToExpanded = _wasAnimating && !animating && !_collapsed;
            _wasAnimating = animating || _collapsed;

            if (!_collapsed && !animating && !snapToExpanded && !_resizing && _lastKnownSize.Y > TitleH + 1f)
                _expandedH = _lastKnownSize.Y;
            float expandH = _expandedH > TitleH + 1f ? _expandedH : InitSize.Y;
            float winH    = expandH + colT * (TitleH - expandH);

            // ── Window position / size ────────────────────────────────────────
            Vector2 clampSize = new(_lastKnownSize.X > 0f ? _lastKnownSize.X : InitSize.X,
                                    _lastKnownSize.Y > 0f ? _lastKnownSize.Y : InitSize.Y);
            _winPos.X = Math.Clamp(_winPos.X, 0f, Math.Max(0f, io.DisplaySize.X - clampSize.X));
            _winPos.Y = Math.Clamp(_winPos.Y, 0f, Math.Max(0f, io.DisplaySize.Y - TitleH));

            ImGuiNET.ImGui.SetNextWindowPos(_winPos, ImGuiCond.Always);

            if (resizingNow)
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

            bool visible = ImGuiNET.ImGui.Begin("##chrome_" + PanelTitle, ref open,
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

            LocManager.PushPanel(PanelTitle);

            Vector2 wp = ImGuiNET.ImGui.GetWindowPos();
            Vector2 ws = ImGuiNET.ImGui.GetWindowSize();
            _winPos        = wp; // always mirror the actual ImGui position so ContainsScreenPoint is never stale
            _lastKnownSize = ws;

            // Track focus for the focus-ring border highlight, and for cross-type
            // (panel vs widget) resize-occlusion arbitration in HandleResizeProbe.
            if (ImGuiNET.ImGui.IsWindowFocused())
            {
                _focusedTitle       = PanelTitle;
                FocusedChromeTitle  = PanelTitle;
                FocusedChromeMin    = wp;
                FocusedChromeMax    = wp + ws;
            }

            DrawChromeVisuals(wp, ws, colT);
            if (ws.Y > TitleH + 1f)
                DrawScrollableContent(wp, ws);
            bool openBeforeClose = open;
            DrawCloseButton(wp, ws, ref open);
            if (openBeforeClose && !open && !OnCloseRequested())
                open = true; // vetoed - subclass shows its own confirm UI and closes itself later
            DrawCollapseButton(wp, ws, colT);
            if (HasPanelMenu) DrawPanelMenuButton(wp, ws);
            DrawTitleSwitch(wp, ws);
            DrawDragRegion(ws);

            ImGuiNET.ImGui.End();
            LocManager.PopPanel();
        }

        // ── Resize (screen-space) ─────────────────────────────────────────────

        // Called each frame when not already resizing/dragging.
        // Tests the mouse against the panel's screen-space edges and sets
        // cursor shape + starts a resize when the primary mouse button is clicked.
        // Running this BEFORE Begin() means child windows cannot steal the clicks.
        private void HandleResizeProbe(ImGuiIOPtr io, Vector2 size)
        {
            var mp = io.MousePos;

            // If a different chrome window (panel OR widget) currently holds global focus
            // - i.e. is visually on top - and its bounds cover this point, that window owns
            // any edge here. An occluded panel must not steal its hover/click just because
            // their rects also overlap the point.
            if (FocusedChromeTitle != null && FocusedChromeTitle != PanelTitle &&
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
            bool    isCollapsed = ws.Y <= TitleH + 2f;

            dl.AddRectFilled(wp, br, ChromeBg, WinRound);

            var   fontB  = ImGuiStyle.FontBold;
            float fsizeB = fontB.FontSize > 0f ? fontB.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            // Title text
            string titleText = LocManager.Get(PanelTitle);
            dl.AddText(fontB, fsizeB,
                new Vector2(wp.X + 14f, wp.Y + (TitleH - fsizeB) * .5f),
                ChromeTitleTxt, titleText);

            // Title badge pill (optional - subclass overrides TitleBadge)
            string badge = TitleBadge != null ? LocManager.Get(TitleBadge) : null;
            var titleSzForRight = fontB.CalcTextSizeA(fsizeB, float.MaxValue, -1f, titleText);
            TitleBadgeRightX = wp.X + 14f + titleSzForRight.X;
            if (badge != null)
            {
                var   titleSz = titleSzForRight;
                const float BPadX = 5f, BPadY = 2f;
                float bFs = fsizeB - 2f;
                var   bSz = fontB.CalcTextSizeA(bFs, float.MaxValue, -1f, badge);
                float bW  = bSz.X + BPadX * 2f;
                float bH  = bFs + BPadY * 2f;
                float bX  = wp.X + 14f + titleSz.X + 8f;
                float bY  = wp.Y + (TitleH - bH) * .5f;
                var   bTL = new Vector2(bX, bY);
                var   bBR = new Vector2(bX + bW, bY + bH);
                uint bCol = TitleBadgeColor;
                dl.AddRectFilled(bTL, bBR, UI.Alpha(bCol, 40), bH * .5f);
                dl.AddRect(bTL, bBR, UI.Alpha(bCol, 100), bH * .5f, ImDrawFlags.None, 1f);
                dl.AddText(fontB, bFs, new Vector2(bX + BPadX, bY + BPadY),
                    UI.Alpha(ChromeTitleTxt, 200), badge);
                TitleBadgeRightX = bBR.X;
            }

            DrawTitleDecoration(dl, wp, ws);

            // Outer border
            dl.AddRect(wp, br, ChromeBorder, WinRound, ImDrawFlags.None, 1f);

            DrawResizeHandles(wp, ws);
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
            uint  animId = (uint)(PanelTitle.GetHashCode() ^ ((int)edge * 0x9E3779B1));
            float t      = UI.Anim(animId, active == edge ? 1f : 0f, 14f);
            if (t < 0.01f) return;
            uint col = UI.Alpha(ImGuiStyle.Accent, (byte)(230 * t));
            // Draw an arc that traces the panel's rounded corner - sits just outside the border.
            dl.PathArcToFast(center, radius + 1f, aMin, aMax);
            dl.PathStroke(col, ImDrawFlags.None, thick);
        }

        private void DrawCloseButton(Vector2 wp, Vector2 ws, ref bool open)
        {
            float btnX = ws.X - CloseBtnS - 6f;
            float btnY = (TitleH - CloseBtnS) * .5f;

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##chrclose", new Vector2(CloseBtnS, CloseBtnS));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool clk = ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();
            if (clk) open = false;

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

        private void DrawCollapseButton(Vector2 wp, Vector2 ws, float colT)
        {
            var   dl   = ImGuiNET.ImGui.GetWindowDrawList();
            float btnX = ws.X - CloseBtnS * 2f - 10f;
            float btnY = (TitleH - CloseBtnS) * .5f;

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##chrcollapse", new Vector2(CloseBtnS, CloseBtnS));
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

        private void DrawPanelMenuButton(Vector2 wp, Vector2 ws)
        {
            var   dl   = ImGuiNET.ImGui.GetWindowDrawList();
            float btnX = ws.X - CloseBtnS * 3f - 14f;
            float btnY = (TitleH - CloseBtnS) * .5f;

            ImGuiNET.ImGui.SetCursorPos(new Vector2(btnX, btnY));
            ImGuiNET.ImGui.InvisibleButton("##chrpmenu", new Vector2(CloseBtnS, CloseBtnS));
            bool hov = ImGuiNET.ImGui.IsItemHovered();
            bool clk = ImGuiNET.ImGui.IsItemClicked();
            uint id  = ImGuiNET.ImGui.GetItemID();

            if (clk) ImGuiNET.ImGui.OpenPopup("##chrpmenu_popup");

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
            if (UI.BeginDropdownMenu("##chrpmenu_popup", anchor))
            {
                DrawPanelMenu();
                UI.EndDropdownMenu();
            }
        }

        private void DrawDragRegion(Vector2 ws)
        {
            float rightW = HasPanelMenu ? CloseBtnS * 3f + 18f : CloseBtnS * 2f + 16f;
            rightW += MasterSwitchReservedW();
            rightW += TitleDecorationReservedW;
            ImGuiNET.ImGui.SetCursorPos(Vector2.Zero);
            ImGuiNET.ImGui.InvisibleButton("##chrdrag",
                new Vector2(ws.X - rightW, TitleH));

            if (ImGuiNET.ImGui.IsItemActivated())
            {
                _dragging        = true;
                _dragStartMouse  = ImGuiNET.ImGui.GetIO().MousePos;
                _dragStartWinPos = _winPos;
                DragSource       = this;
            }

            if (ImGuiNET.ImGui.IsItemHovered() &&
                ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _collapsed   = !_collapsed;
                _pendingSave = true;
            }

            if (!_dragging)
                _winPos = ImGuiNET.ImGui.GetWindowPos();
        }

        // ── Panel header helpers ───────────────────────────────────────────────
        //
        // Call at the very top of DrawPanelContent() to render the standard bordered
        // header group that every tabbed ChromePanel subclass shares.
        //
        // Tab-only (no master toggle):
        //   protected override void DrawPanelContent()
        //   {
        //       DrawTabHeader(_tabs, ref _tab);
        //       switch (_tab) { ... }
        //   }
        //
        // With master toggle + config binding:
        //   DrawMasterTabHeader(
        //       "Enable Memory Writing", Config.MemWritesEnabled, FeatureManager.SetMasterSwitch,
        //       _tabs, ref _tab);
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Draws the standard bordered header group containing only the tab bar.
        /// Use when the panel has tabs but no single master enable/disable toggle.
        /// </summary>
        protected void DrawTabHeader(string[] tabs, ref int tab)
        {
            // Cancel the 13 px leading dummy that precedes DrawPanelContent in both
            // standalone mode (inserted by DrawScrollableContent) and hosted mode
            // (inserted by GroupPanel's inner child setup) so the tab row sits flush.
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() - 13f);
            UI.TabRow(tabs, ref tab);
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 6f);
        }

        /// <summary>
        /// Draws the standard bordered header group: master toggle → separator → tab bar.
        /// The <paramref name="onChange"/> callback is invoked when the toggle changes;
        /// wire it to your config property and <c>Config.SaveAsync()</c>.
        /// Returns true if the master toggle was clicked this frame.
        /// </summary>
        protected bool DrawMasterTabHeader(
            string masterLabel, bool masterValue, Action<bool> onChange,
            string[] tabs, ref int tab)
        {
            // Toggle has moved to the title bar via TitleMasterSwitch.
            // This method now just renders the tab navigation, same as DrawTabHeader.
            DrawTabHeader(tabs, ref tab);
            return false;
        }


        private void DrawScrollableContent(Vector2 wp, Vector2 ws)
        {
            bool  pad      = HasContentPadding;
            float topGap   = pad ? 13f : 0f;
            float botGap   = HasBottomGap ? 6f : 0f;
            float contentH = ws.Y - TitleH - topGap - botGap;
            ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, TitleH));

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,     new Vector2(HasScrollWindowPadding ? Pad : 0f, 0f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize,     5f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 3f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg,              Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        UI.ToV4(UI.Alpha(ChromeScrollThumb, 90)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, UI.ToV4(UI.Alpha(ChromeScrollThumb, 200)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  UI.ToV4(ImGuiStyle.Accent));

            UI.ClearWheelConsumed();

            // NoScrollWithMouse prevents UpdateMouseWheel from auto-scrolling this child.
            // We apply the wheel manually below so normal scrolling still works.
            ImGuiNET.ImGui.BeginChild("##chrscroll_" + PanelTitle,
                new Vector2(ws.X, contentH), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollWithMouse | ChromeScrollExtraFlags);

            if (pad)
            {
                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGuiNET.ImGui.GetStyle().ItemSpacing.X, 0f));
                ImGuiNET.ImGui.Dummy(new Vector2(1f, 13f));
                ImGuiNET.ImGui.PopStyleVar();
            }

            // Suppress LMB click events while a hotkey is being captured so that
            // pressing M1 (or any mouse button) to bind a key doesn't simultaneously
            // activate checkboxes, buttons, or sliders in the panel content.
            if (UI.IsCapturingHotkey)
                ImGuiNET.ImGui.GetIO().MouseClicked[0] = false;

            DrawPanelContent();

            // Wheel not consumed by a widget: apply it manually to this panel.
            // DoSlider zeros io.MouseWheel when it consumes the wheel, so if it's
            // still non-zero here no slider intercepted it and normal scrolling applies.
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

            if (ImGuiNET.ImGui.IsWindowHovered() &&
                ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                !ImGuiNET.ImGui.IsAnyItemHovered())
            {
                Vector2 mp  = ImGuiNET.ImGui.GetMousePos();
                Vector2 cur = ImGuiNET.ImGui.GetCursorScreenPos();
                ImGuiNET.ImGui.SetCursorScreenPos(mp - Vector2.One);
                ImGuiNET.ImGui.InvisibleButton("##chr_unfocus", new Vector2(2f, 2f));
                ImGuiNET.ImGui.SetCursorScreenPos(cur);
            }

            ImGuiNET.ImGui.EndChild();

            // Accent line overlay: submitted after ##chrscroll_ so it renders above all content children.
            // A child window (vs GetForegroundDrawList) confines it to this panel, preventing bleed-through.
            ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, TitleH));
            ImGuiNET.ImGui.BeginChild("##chraccent_" + PanelTitle, new Vector2(ws.X, 1f), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs);
            var adl  = ImGuiNET.ImGui.GetWindowDrawList();
            var aPos = ImGuiNET.ImGui.GetWindowPos();
            adl.AddLine(aPos, new Vector2(aPos.X + ws.X, aPos.Y), UI.Alpha(ImGuiStyle.Accent, 210), 1.5f);
            ImGuiNET.ImGui.EndChild();

            ImGuiNET.ImGui.PopStyleColor(5);
            ImGuiNET.ImGui.PopStyleVar(3);
        }
    }
}
