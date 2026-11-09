using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using SC2Radar.UI.ImGui;
using SC2Radar.UI.Misc;

namespace SC2Radar.UI.ImGui.Panels
{
    /// <summary>
    /// A ChromePanel that hosts multiple other ChromePanels as tabbed members.
    /// Created dynamically when the user drags one panel onto another.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    internal sealed class GroupPanel : ChromePanel
    {
        #region Chrome Overrides

        // GroupPanel delegates horizontal padding to ##grpmember_ (one layer per member).
        // HasScrollWindowPadding=false keeps ##chrscroll_GroupPanel at WindowPadding=0 so only
        // the member child contributes the 8px side padding, matching standalone panels exactly.
        // HasContentPadding stays true so the topGap=13 in DrawScrollableContent is preserved;
        // that gap makes chromeBtm sit 13px above the chrome border, identical to standalone.
        protected override bool HasScrollWindowPadding => false;

        protected override string PanelTitle
        {
            get
            {
                if (_members.Count == 0) return _groupKey;
                var name = string.Join(" · ", _members.Select(m => m.Title));
                if (name.Length > 40)
                    name = string.Join(" · ", _members.Take(2).Select(m => m.Title))
                           + " · ···";
                return name;
            }
        }

        protected override string TitleBadge
            => _members.Count > 0 && _activeTab < _members.Count
                ? _members[_activeTab].GetTitleBadge()
                : null;

        protected override (string, bool, Action<bool>)? TitleMasterSwitch
            => _members.Count > 0 && _activeTab < _members.Count
                ? _members[_activeTab].GetTitleMasterSwitch()
                : null;

        protected override Vector2 InitSize
        {
            get
            {
                if (_members.Count == 0) return new Vector2(400f, 500f);
                float w = 300f, h = 300f;
                foreach (var m in _members)
                {
                    var s = m.GetInitSize();
                    if (s.X > w) w = s.X;
                    if (s.Y > h) h = s.Y;
                }
                return new Vector2(w, h + TabBarH + 4f);
            }
        }

        // Proxy the active member's panel-menu button so the ••• button appears in the
        // GroupPanel title bar when the active tab has a menu.
        protected override bool HasPanelMenu
            => _members.Count > 0 && _activeTab < _members.Count
               && _members[_activeTab].GetHasPanelMenu();

        protected override void DrawPanelMenu()
        {
            if (_members.Count > 0 && _activeTab < _members.Count)
                _members[_activeTab].InvokeDrawPanelMenu();
        }

        #endregion

        #region State

        private readonly List<ChromePanel> _members = new();
        private int _activeTab;

        private readonly string _groupKey;

        private int _pendingPopOut = -1;

        /// <summary>Fired when a member is popped out. Args: (this group, the popped panel).</summary>
        internal event Action<GroupPanel, ChromePanel> MemberPopped;

        // Tab drag-reorder / detach state.
        private int  _tabDragSrc  = -1;
        private bool _tabDragging;
        private bool _tabDetaching;

        /// <summary>
        /// Screen position at which the user released a detach-drag.
        /// NaN when no detach pop is pending. Read by MenuManager in OnMemberPopped
        /// to position the newly-standalone panel. Reset automatically after pop fires.
        /// </summary>
        internal Vector2 PendingPopOutPos { get; private set; } = new Vector2(float.NaN, float.NaN);

        // Open state (used by MenuManager as the ref bool for Draw).
        internal bool ShowState = true;
        /// <summary>
        /// When true the group dissolved because a member was popped out (not because
        /// the user closed it). MenuManager should free remaining members as standalone
        /// panels rather than hiding them via HidePanel.
        /// </summary>
        internal bool DissolvedByPop;

        // Visual constants match UI.TabRow exactly (SegH, SegRound, PadX=14, no gap).
        private const float TabBarH       = 26f;   // SegH
        private const float TabPadX       = 14f;   // horizontal text padding
        private const float TabRound      =  5f;   // SegRound
        private const float DetachThresh  = 36f;
        private const float TabGapY       =  3f;   // only used for GetInitSize row calc

        private float _tabScrollOff = 0f;
        private int   _rClickedTab  = -1;

        #endregion

        #region Public API

        internal IReadOnlyList<ChromePanel> Members    => _members;
        internal string                     GroupKey   => _groupKey;
        internal int                        MemberCount => _members.Count;

        internal GroupPanel(string groupKey, IEnumerable<ChromePanel> initialMembers)
        {
            _groupKey = groupKey;
            foreach (var m in initialMembers)
                AddMember(m);
        }

        internal void AddMember(ChromePanel panel)
        {
            if (_members.Contains(panel)) return;
            _members.Add(panel);
            panel.IsGrouped = true;
            panel.IsHosted  = true;
        }

        /// <summary>
        /// Removes the member at <paramref name="idx"/>, clears its grouped/hosted flags,
        /// and fires <see cref="MemberPopped"/> so the coordinator can re-add it standalone.
        /// </summary>
        internal void RequestPopOut(int idx)
        {
            if (idx < 0 || idx >= _members.Count) return;
            _pendingPopOut = idx;
        }

        private ChromePanel ExecutePopOut(int idx)
        {
            var m = _members[idx];
            _members.RemoveAt(idx);
            m.IsGrouped = false;
            m.IsHosted  = false;
            if (_activeTab >= _members.Count)
                _activeTab = Math.Max(0, _members.Count - 1);
            return m;
        }

        /// <summary>
        /// Silently removes a member from the group (clears its grouped/hosted flags).
        /// Does NOT fire MemberPopped - caller decides what to do with the panel.
        /// </summary>
        internal ChromePanel? RemoveMember(ChromePanel panel)
        {
            int idx = _members.IndexOf(panel);
            return idx < 0 ? null : ExecutePopOut(idx);
        }

        #endregion

        #region UI - Entry Point

        protected override void DrawPanelContent()
        {
            // Process any pending pop-out before touching the member list mid-draw.
            ProcessPendingPopOut();

            if (_members.Count == 0) return;

            // DrawScrollableContent inserts a 13px leading dummy before us (HasContentPadding=true).
            // Cancel it so the tab bar sits flush against the title accent line.
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() - 13f);

            DrawGroupTabBar();

            // Member content in its own child so it gets its own scroll context.
            float memberH = ImGuiNET.ImGui.GetContentRegionAvail().Y;
            if (memberH < 1f) return;

            // Active tab may be invisible (its toolbar flag is off); find first visible member.
            int visibleActive = _activeTab;
            if (!_members[visibleActive].IsVisible)
            {
                visibleActive = -1;
                for (int i = 0; i < _members.Count; i++)
                    if (_members[i].IsVisible) { visibleActive = i; break; }
            }
            if (visibleActive < 0) return; // all members hidden - nothing to render

            bool hasPad = _members[visibleActive].GetHasContentPadding();
            var memberScrollFlags = _members[visibleActive].GetChromeScrollExtraFlags();
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
                new Vector2(hasPad ? 8f : 0f, 0f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize,     5f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 3f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg,             Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,         Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,
                UI.ToV4(UI.Alpha(ImGuiStyle.Col(0x3A, 0x3A, 0x50), 90)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered,
                UI.ToV4(UI.Alpha(ImGuiStyle.Col(0x3A, 0x3A, 0x50), 200)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,
                UI.ToV4(ImGuiStyle.Accent));

            bool inChild = ImGuiNET.ImGui.BeginChild(
                "##grpmember_" + _groupKey,
                new Vector2(0f, memberH),
                ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollWithMouse | memberScrollFlags);

            ImGuiNET.ImGui.PopStyleColor(5);
            ImGuiNET.ImGui.PopStyleVar(3);

            if (inChild)
            {
                if (hasPad)
                {
                    // Match DrawScrollableContent's 13px leading dummy exactly.
                    ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
                        new Vector2(ImGuiNET.ImGui.GetStyle().ItemSpacing.X, 0f));
                    ImGuiNET.ImGui.Dummy(new Vector2(1f, 13f));
                    ImGuiNET.ImGui.PopStyleVar();
                }

                UI.ClearWheelConsumed();
                _members[visibleActive].InvokeDrawContent();

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
            }
            ImGuiNET.ImGui.EndChild();
        }

        #endregion

        #region UI - Tab Bar

        private void DrawGroupTabBar()
        {
            var   io     = ImGuiNET.ImGui.GetIO();
            var   dl     = ImGuiNET.ImGui.GetWindowDrawList();
            float avail  = ImGuiNET.ImGui.GetContentRegionAvail().X;
            var   cursor = ImGuiNET.ImGui.GetCursorScreenPos();

            // Matches UI.TabRow exactly: FontNormal, SegH=26, PadX=14, no gap between tabs.
            var   font    = ImGuiStyle.FontNormal;
            float fsize   = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            int   count   = _members.Count;
            uint  rowHash = (uint)_groupKey.GetHashCode();

            // Advance active tab away from invisible members first.
            AdvanceActiveTabPastHidden(count);

            // Per-tab widths (invisible members get 0 = skipped in all passes).
            var   tabWidths = new float[count];
            var   tabX      = new float[count];
            float totalW    = 0f;
            float cx0       = 0f;
            for (int i = 0; i < count; i++)
            {
                if (!_members[i].IsVisible) continue;
                tabWidths[i] = font.CalcTextSizeA(fsize, float.MaxValue, -1f, _members[i].Title).X
                               + TabPadX * 2f;
                tabX[i] = cx0;
                cx0    += tabWidths[i]; // no gap (matches TabRow)
            }
            totalW = cx0;

            bool  overflow  = totalW > avail;
            float maxScroll = overflow ? Math.Max(0f, totalW - avail) : 0f;
            _tabScrollOff   = overflow ? Math.Clamp(_tabScrollOff, 0f, maxScroll) : 0f;

            // Single invisible button covers the full row (like TabRow).
            ImGuiNET.ImGui.SetCursorScreenPos(cursor);
            ImGuiNET.ImGui.InvisibleButton("##grpbar_" + _groupKey, new Vector2(avail, TabBarH));
            bool rowHov = ImGuiNET.ImGui.IsItemHovered();
            bool rowAct = ImGuiNET.ImGui.IsItemActive();

            if (rowHov && io.MouseWheel != 0f)
            {
                if (overflow) _tabScrollOff = Math.Clamp(_tabScrollOff - io.MouseWheel * 40f, 0f, maxScroll);
                io.MouseWheel = 0f;
            }
            if (overflow && rowAct && !_tabDragging)
                _tabScrollOff = Math.Clamp(_tabScrollOff - io.MouseDelta.X, 0f, maxScroll);

            // Local hit-test (X only - single-row, no Y check needed).
            int HitTest(float rx)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!_members[i].IsVisible || tabWidths[i] == 0f) continue;
                    if (rx >= tabX[i] && rx < tabX[i] + tabWidths[i]) return i;
                }
                return -1;
            }

            // Drag state (reorder or detach).
            int  newActive = _activeTab;
            bool mouseDown = io.MouseDown[0];
            int  dropAt    = -1;

            if (_tabDragging && _tabDragSrc >= 0)
            {
                if (!_tabDetaching && io.MousePos.Y > cursor.Y + TabBarH + DetachThresh)
                    _tabDetaching = true;

                if (_tabDetaching)
                {
                    DrawDetachGhost(font, fsize, _members[_tabDragSrc].Title, io.MousePos);
                    if (!mouseDown)
                        CommitTabDetach(io.MousePos);
                }
                else
                {
                    float rx = io.MousePos.X - cursor.X + _tabScrollOff;
                    dropAt = count - 1;
                    for (int i = 0; i < count; i++)
                        if (_members[i].IsVisible && tabWidths[i] > 0f && rx < tabX[i] + tabWidths[i] * 0.5f)
                            { dropAt = i; break; }
                    if (!mouseDown)
                        CommitTabReorder(dropAt);
                }
            }
            else if (!mouseDown) { ResetDragState(); }

            if (rowAct && !_tabDragging && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 6f))
            {
                int src = HitTest(io.MouseClickedPos[0].X - cursor.X + _tabScrollOff);
                if (src >= 0) OnTabDragStart(src);
            }
            if (rowHov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_tabDragging)
            {
                int clicked = HitTest(io.MouseClickedPos[0].X - cursor.X + _tabScrollOff);
                if (clicked >= 0) newActive = clicked;
            }
            if (rowHov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                _rClickedTab = HitTest(io.MouseClickedPos[1].X - cursor.X + _tabScrollOff);
                if (_rClickedTab >= 0) ImGuiNET.ImGui.OpenPopup("##grpctx_" + _groupKey);
            }

            // Scrollbar rendered INSIDE the bottom of the row (matches TabRow).
            if (overflow)
            {
                const float SbH   = 2f, SbRnd = 1f;
                float sbY    = cursor.Y + TabBarH - SbH - 1f;
                float ratio  = avail / totalW;
                float thumbW = Math.Max(avail * ratio, 20f);
                float thumbX = cursor.X + (_tabScrollOff / maxScroll) * (avail - thumbW);
                dl.AddRectFilled(new Vector2(cursor.X, sbY), new Vector2(cursor.X + avail, sbY + SbH),
                    UI.Alpha(ImGuiStyle.Dark3, 80), SbRnd);
                dl.AddRectFilled(new Vector2(thumbX, sbY), new Vector2(thumbX + thumbW, sbY + SbH),
                    UI.Alpha(ImGuiStyle.Border, 130), SbRnd);
            }

            if (overflow)
                dl.PushClipRect(cursor, new Vector2(cursor.X + avail, cursor.Y + TabBarH), true);

            for (int i = 0; i < count; i++)
            {
                if (!_members[i].IsVisible || tabWidths[i] == 0f) continue;

                float tx   = cursor.X + tabX[i] - _tabScrollOff;
                float ty   = cursor.Y;
                float tw   = tabWidths[i];
                Vector2 tl = new(tx, ty);
                Vector2 br = new(tx + tw, ty + TabBarH);

                bool isSel     = i == _activeTab;
                bool isDragSrc = _tabDragging && _tabDragSrc == i;
                bool tHov      = rowHov && io.MousePos.X >= tl.X && io.MousePos.X < br.X;

                // Animation IDs and speeds match UI.TabRow.
                uint  tid = rowHash * 2654435761u ^ (uint)i;
                float tv  = UI.Anim(tid,          isSel ? 1f : 0f, 14f);
                float th  = UI.Anim(tid ^ 0xCCu,  tHov  ? 1f : 0f, 12f);

                if (isDragSrc)
                {
                    dl.AddRectFilled(tl, br, UI.Alpha(ImGuiStyle.Dark4, 40), TabRound);
                    dl.AddText(font, fsize, new Vector2(tl.X + TabPadX, tl.Y + (TabBarH - fsize) * 0.5f),
                        UI.Alpha(ImGuiStyle.Text, 50), _members[i].Title);
                }
                else
                {
                    // Hover fill
                    if (th > 0.01f)
                        dl.AddRectFilled(tl, br, UI.Alpha(ImGuiStyle.Dark3, (byte)(75 * th)), TabRound);
                    // Selected: accent fill + bottom underline (matches TabRow exactly)
                    if (tv > 0.01f)
                    {
                        dl.AddRectFilled(tl, br, UI.Alpha(ImGuiStyle.Accent, (byte)(45 * tv)), TabRound);
                        dl.AddRectFilled(
                            new Vector2(tl.X + 4f, br.Y - 3f),
                            new Vector2(br.X - 4f, br.Y - 0.5f),
                            UI.Alpha(ImGuiStyle.Accent, (byte)(255 * tv)), 1.5f);
                    }
                    // Label
                    uint lc = isSel
                        ? UI.Lerp(ImGuiStyle.Text, 0xFFFFFFFF, tv)
                        : UI.Lerp(ImGuiStyle.TextDim, ImGuiStyle.Text, th);
                    dl.AddText(font, fsize, new Vector2(tl.X + TabPadX, tl.Y + (TabBarH - fsize) * 0.5f),
                        lc, _members[i].Title);
                }
            }

            // Reorder drop indicator.
            if (_tabDragging && !_tabDetaching && dropAt >= 0)
            {
                float lineX = cursor.X + tabX[dropAt] - _tabScrollOff
                            + (dropAt > _tabDragSrc ? tabWidths[dropAt] : 0f);
                dl.AddLine(new Vector2(lineX, cursor.Y + 2f),
                           new Vector2(lineX, cursor.Y + TabBarH - 3f),
                           ImGuiStyle.Accent, 2f);
            }

            if (overflow) dl.PopClipRect();

            // Separator line below the row (matches TabRow).
            dl.AddLine(
                new Vector2(cursor.X,         cursor.Y + TabBarH),
                new Vector2(cursor.X + avail, cursor.Y + TabBarH),
                UI.Alpha(ImGuiStyle.Border, 70), 1f);

            // Context menu.
            if (ImGuiNET.ImGui.BeginPopup("##grpctx_" + _groupKey))
            {
                if (_rClickedTab >= 0 && _rClickedTab < count &&
                    ImGuiNET.ImGui.MenuItem($"Pop out \"{_members[_rClickedTab].Title}\""))
                    OnPopOutMenuItem_Clicked(_rClickedTab);
                ImGuiNET.ImGui.EndPopup();
            }

            // Update active tab; auto-scroll to it on change.
            int prevActive = _activeTab;
            _activeTab = Math.Clamp(newActive, 0, Math.Max(0, count - 1));
            if (_activeTab != prevActive && overflow)
            {
                float ts = tabX[_activeTab], te = ts + tabWidths[_activeTab];
                if (ts < _tabScrollOff) _tabScrollOff = ts;
                else if (te > _tabScrollOff + avail) _tabScrollOff = te - avail;
                _tabScrollOff = Math.Clamp(_tabScrollOff, 0f, maxScroll);
            }

            // Advance cursor past tab row + 1px separator. Zero ItemSpacing so the zero-height
            // Dummy doesn't add an extra gap before the member child window.
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + TabBarH + 1f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 0f));
            ImGuiNET.ImGui.PopStyleVar();
        }

        private static void DrawDetachGhost(ImFontPtr font, float fsize, string label, Vector2 mousePos)
        {
            var    dl   = ImGuiNET.ImGui.GetForegroundDrawList();
            const float PadX = 10f, PadY = 4f;
            var    tsz  = font.CalcTextSizeA(fsize, float.MaxValue, -1f, label);
            Vector2 tl  = new(mousePos.X + 14f, mousePos.Y + 6f);
            Vector2 br  = new(tl.X + tsz.X + PadX * 2f, tl.Y + fsize + PadY * 2f);
            dl.AddRectFilled(tl, br, UI.Alpha(ImGuiStyle.InputBg, 230), 5f);
            dl.AddRect(tl, br, UI.Alpha(ImGuiStyle.Accent, 200), 5f, ImDrawFlags.None, 1.5f);
            dl.AddText(font, fsize,
                new Vector2(tl.X + PadX, tl.Y + PadY),
                ImGuiStyle.Accent, label);
        }

        #endregion

        #region Events - Tab Bar

        /// <summary>
        /// Consumes a pending pop-out request (queued by the context menu or a drag-detach),
        /// removes the member, and notifies subscribers so it can be re-added standalone.
        /// </summary>
        private void ProcessPendingPopOut()
        {
            if (_pendingPopOut < 0) return;
            int idx        = _pendingPopOut;
            _pendingPopOut = -1;
            var popped     = ExecutePopOut(idx);
            MemberPopped?.Invoke(this, popped);
            PendingPopOutPos = new Vector2(float.NaN, float.NaN);
        }

        /// <summary>
        /// If the active tab's member is currently hidden (toolbar-visibility flag off),
        /// advances to the first visible member so the tab bar never highlights a hidden tab.
        /// </summary>
        private void AdvanceActiveTabPastHidden(int count)
        {
            if (count > 0 && !_members[_activeTab].IsVisible)
                for (int i = 0; i < count; i++)
                    if (_members[i].IsVisible) { _activeTab = i; break; }
        }

        #endregion

        #region Events - Drag & Drop

        private void OnTabDragStart(int src)
        {
            _tabDragSrc   = src;
            _tabDragging  = true;
            _tabDetaching = false;
        }

        /// <summary>Mouse released past the detach threshold - queue the dragged tab for pop-out.</summary>
        private void CommitTabDetach(Vector2 releasePos)
        {
            PendingPopOutPos = releasePos;
            _pendingPopOut   = _tabDragSrc;
            ResetDragState();
        }

        /// <summary>Mouse released over the tab row - commit the reorder to the member list.</summary>
        private void CommitTabReorder(int dropAt)
        {
            if (dropAt != _tabDragSrc)
            {
                var m = _members[_tabDragSrc];
                _members.RemoveAt(_tabDragSrc);
                _members.Insert(dropAt, m);
                if      (_activeTab == _tabDragSrc)                        _activeTab = dropAt;
                else if (_activeTab > _tabDragSrc && _activeTab <= dropAt) _activeTab--;
                else if (_activeTab < _tabDragSrc && _activeTab >= dropAt) _activeTab++;
            }
            _tabDragging = false;
            _tabDragSrc  = -1;
        }

        private void ResetDragState()
        {
            _tabDetaching = false;
            _tabDragging  = false;
            _tabDragSrc   = -1;
        }

        #endregion

        #region Events - Panel Menu

        private void OnPopOutMenuItem_Clicked(int idx) => _pendingPopOut = idx;

        #endregion
    }
}
