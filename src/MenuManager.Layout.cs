using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Panels;
using SC2Radar.UI.Misc;

namespace SC2Radar.UI.ImGui.Menus
{
    // Panel layout: drag-to-merge groups (tabs), drag-to-edge splits, docking to the screen edges, the drop-zone
    // overlay, and saving / restoring all of it - ported unchanged from the EFT radar's MenuManager. The SC2-specific
    // parts (which panels exist, their show flags, GetVisibleMergeablePanels) live in MenuManager.cs.
    public sealed partial class MenuManager
    {
        // ── Panel group helpers ───────────────────────────────────────────────

        private GroupPanel CreateGroup(IEnumerable<ChromePanel> initialMembers)
        {
            string key = $"__group_{_nextGroupId++}";
            var group  = new GroupPanel(key, initialMembers);
            group.MemberPopped += OnMemberPopped;
            _groups.Add(group);
            return group;
        }

        private void DissolveGroup(GroupPanel group)
        {
            // Only clears member flags - caller removes from _groups.
            foreach (var m in group.Members)
            {
                m.IsGrouped = false;
                m.IsHosted  = false;
            }
        }

        /// <summary>Hides a panel by toggling its MenuManager show-flag to false.</summary>
        private void HidePanel(ChromePanel panel)
        {
            if (_panelHideActions != null && _panelHideActions.TryGetValue(panel, out var hide))
                hide();
        }

        /// <summary>
        /// Each frame: push the current toolbar show-flags into each panel's IsVisible property.
        /// GroupPanel uses IsVisible to hide/show tabs without removing members,
        /// so re-enabling a panel makes its tab reappear in the same group automatically.
        /// </summary>
        private void SyncPanelVisibility()
        {
            if (_panelVisibilityFuncs == null) return;
            foreach (var (panel, vis) in _panelVisibilityFuncs)
                panel.IsVisible = vis();
        }

        private void OnMemberPopped(GroupPanel group, ChromePanel panel)
        {
            // If this was a drag-detach, place the panel at the drop position.
            var dropPos = group.PendingPopOutPos;
            if (!float.IsNaN(dropPos.X))
                panel.SetPosition(new Vector2(dropPos.X - 20f, dropPos.Y - 15f));

            // Bring the newly standalone panel to the front via two mechanisms:
            // 1. SetNextWindowFocus() fires on the panel's first standalone Draw() (frame N+2).
            // 2. SetWindowFocus(name) fires after all draws for 2 frames - the definitive fix,
            //    since groups draw after regular panels and would otherwise win z-ordering.
            panel.RequestFocus();
            _bringToFrontWindow    = "##chrome_" + panel.Title;
            _bringToFrontWindowTtl = 2;

            if (group.MemberCount < 2)
            {
                // Free any remaining member flags and signal the draw loop
                // to dissolve this frame via ShowState (avoids removing from
                // _groups mid-iteration inside the draw loop).
                // DissolvedByPop=true tells the draw loop NOT to call HidePanel on
                // the survivors - they should stay visible as standalone windows.
                foreach (var m in group.Members)
                {
                    m.IsGrouped = false;
                    m.IsHosted  = false;
                }
                group.DissolvedByPop = true;
                group.ShowState = false;
                // SaveGroupConfig triggered by draw loop after removal.
            }
            else
            {
                SaveGroupConfig();
            }
        }

        /// <summary>Iterates mergeable panels (excludes grouped panels; includes docked panels).
        /// Groups are yielded first because they are drawn last (highest Z-order), so they
        /// should take priority over any standalone panel that happens to overlap them.</summary>
        // ── Dock system ──────────────────────────────────────────────────────
        // Header height for the dock slot chrome (matches ChromePanel.TitleH).
        private const float DockTitleH = 30f;
        // Edge zone width/height for drag-to-dock detection. Kept in sync with
        // WidgetManager's own DockEdgeZone so panels and widgets dock at the same distance.
        private const float DockEdgeZone = 32f;
        // Minimum slot dimension so tiny panels don't create unusable slots.
        private const float DockMinDim = 200f;

        /// <summary>
        /// Returns the pixel margins carved from the display by docked panels.
        /// Used by ImGuiApp to position the radar window in the remaining center rect.
        /// </summary>
        internal (float L, float R, float T, float B) GetDockMargins()
        {
            float l = _dockDim.GetValueOrDefault(DockEdge.Left,   0f);
            float r = _dockDim.GetValueOrDefault(DockEdge.Right,  0f);
            float t = _dockDim.GetValueOrDefault(DockEdge.Top,    0f);
            float b = _dockDim.GetValueOrDefault(DockEdge.Bottom, 0f);
            return (l, r, t, b);
        }

        private void DockPanel(ChromePanel panel, DockEdge edge, bool save = true)
        {
            if (panel.IsGrouped) return; // must detach from group first

            // Remove from old edge if re-docking to a different edge
            if (panel.IsDocked && panel.DockedEdge != edge)
            {
                _docked.Remove(panel.DockedEdge);
                panel.DockedEdge = DockEdge.None;
            }

            // If the edge is already occupied by something else, merge into it
            if (_docked.TryGetValue(edge, out var existing) && !ReferenceEquals(existing, panel))
            {
                MergeIntoDocked(panel, existing, edge);
                if (save) SaveDockConfig();
                return;
            }

            panel.DockedEdge = edge;
            _docked[edge] = panel;
            _dockDim[edge] = Math.Max(DockMinDim,
                edge is DockEdge.Left or DockEdge.Right ? panel.ScreenSize.X : panel.ScreenSize.Y);

            if (save) SaveDockConfig();
        }

        /// <summary>
        /// Merges <paramref name="incoming"/> (individual panel or GroupPanel) into
        /// <paramref name="existing"/> which already occupies <paramref name="edge"/>.
        /// If <paramref name="existing"/> is already a GroupPanel, members are appended.
        /// If it is an individual panel, a new GroupPanel wrapping both is created.
        /// The dock slot dimension is kept - only the hosted panel changes.
        /// Caller is responsible for calling SaveDockConfig() if needed.
        /// </summary>
        /// <summary>
        /// Recursively flattens <paramref name="panel"/> into its leaf ChromePanels,
        /// dissolving any GroupPanels and SplitPanels encountered along the way.
        /// Results are appended to <paramref name="result"/>.
        /// </summary>
        private void FlattenIncoming(ChromePanel panel, List<ChromePanel> result)
        {
            if (panel is GroupPanel gp)
            {
                var members = gp.Members.ToList();
                DissolveGroup(gp);
                _groups.Remove(gp);
                foreach (var m in members) FlattenIncoming(m, result);
            }
            else if (panel is SplitPanel sp)
            {
                var panes = Enumerable.Range(0, sp.PaneCount).Select(i => sp.GetPane(i)).ToList();
                foreach (var p in panes) { p.IsGrouped = false; p.IsHosted = false; }
                if (sp.IsDocked) { _docked.Remove(sp.DockedEdge); sp.DockedEdge = DockEdge.None; }
                _splits.Remove(sp);
                foreach (var p in panes) FlattenIncoming(p, result);
            }
            else
            {
                result.Add(panel);
            }
        }

        private void MergeIntoDocked(ChromePanel incoming, ChromePanel existing, DockEdge edge)
        {
            // Flatten incoming: dissolve any groups/splits and collect leaf panels.
            var toAdd = new List<ChromePanel>();
            FlattenIncoming(incoming, toAdd);

            if (existing is GroupPanel exGp)
            {
                // Append new members to the docked group - done.
                foreach (var m in toAdd)
                    exGp.AddMember(m);
            }
            else
            {
                // Existing is an individual panel. Wrap existing + incoming into a new group.
                // Clear dock state on the existing panel first.
                existing.DockedEdge = DockEdge.None;
                _docked.Remove(edge);

                var allMembers = new List<ChromePanel>(1 + toAdd.Count) { existing };
                allMembers.AddRange(toAdd);

                var newGroup = CreateGroup(allMembers);
                newGroup.DockedEdge = edge;
                _docked[edge] = newGroup;
                // _dockDim[edge] intentionally preserved - slot size stays the same.
            }

            // Keep PanelGroups in sync with the new membership so SaveDockConfig() and
            // SaveGroupConfig() agree when the app restarts and restores dock state.
            SaveGroupConfig();
        }

        private void UndockPanel(ChromePanel panel, bool save = true)
        {
            _docked.Remove(panel.DockedEdge);
            _dockDim.Remove(panel.DockedEdge);
            panel.DockedEdge = DockEdge.None;
            if (save) SaveDockConfig();
        }

        private void SaveDockConfig()
        {
            var panels = new Dictionary<string, string>();
            var groups = new Dictionary<string, List<string>>();
            var dims   = new Dictionary<string, float>();

            foreach (var kvp in _docked)
            {
                string key = kvp.Key.ToString().ToLowerInvariant();
                if (kvp.Value is GroupPanel gp)
                    groups[key] = gp.Members.Select(m => m.Title).ToList();
                else if (kvp.Value is SplitPanel)
                {
                    // A docked SplitPanel's own Title is a synthesized "PanelA · PanelB" string
                    // that never matches a real entry in _panelByTitle, so writing it into
                    // DockedPanels here silently failed to restore on the next launch. Its dock
                    // edge is now carried on the split's own PanelSplitEntry.DockedEdge instead
                    // (set in SerializeSplit/SaveSplitConfig) and re-applied by the restore loop.
                }
                else
                    panels[key] = kvp.Value.Title;

                if (_dockDim.TryGetValue(kvp.Key, out float dim))
                    dims[key] = dim;
            }

            Program.Config.PanelOpen.DockedPanels = panels;
            Program.Config.PanelOpen.DockedGroups = groups;
            Program.Config.PanelOpen.DockedDims   = dims;
            Program.Config.SaveDebounced();

            // A split's own DockedEdge only gets written to its PanelSplitEntry by
            // SaveSplitConfig(), which otherwise only runs on split-specific mutations
            // (create, pop member, etc.) - not on a plain dock/undock of an existing split.
            // Without this, dragging an already-split pair onto an edge updates _docked/
            // DockedEdge in memory but never persists it, so it reverts to floating on
            // restart. Cheap enough (handful of splits, not a per-frame call) to just always
            // resync here rather than try to precisely detect which case applies.
            SaveSplitConfig();
        }

        private void DrawDockedPanels()
        {
            if (_docked.Count == 0) return;

            var   io    = ImGuiNET.ImGui.GetIO();
            var   disp  = io.DisplaySize;
            float menuH = ImGuiNET.ImGui.GetFrameHeight();
            var (dL, dR, dT, dB) = GetDockMargins();

            // Compute slot rects (Top/Bottom span full width; Left/Right fill between them).
            var slots = new (DockEdge Edge, Vector2 Pos, Vector2 Size)[]
            {
                (DockEdge.Top,    new Vector2(0f,          menuH),       new Vector2(disp.X, dT)),
                (DockEdge.Bottom, new Vector2(0f,          disp.Y - dB), new Vector2(disp.X, dB)),
                (DockEdge.Left,   new Vector2(0f,          menuH + dT),  new Vector2(dL,     disp.Y - menuH - dT - dB)),
                (DockEdge.Right,  new Vector2(disp.X - dR, menuH + dT),  new Vector2(dR,     disp.Y - menuH - dT - dB)),
            };

            // Resize probes BEFORE Begin() so child windows can't steal the hit.
            foreach (var (edge, pos, size) in slots)
                if (_docked.ContainsKey(edge) && size.X > 0f && size.Y > 0f)
                    ProbeDockedResize(edge, pos, size, io);

            // Draw the slots.
            foreach (var (edge, pos, size) in slots)
                if (_docked.TryGetValue(edge, out var panel) && size.X > 0f && size.Y > 0f)
                    DrawDockedSlot(edge, panel, io, pos, size);
        }

        // ── Dock slot resize ─────────────────────────────────────────────────
        private bool     _dockResizing;
        private DockEdge _dockResizeEdge;
        private float    _dockResizeStartMouse;
        private float    _dockResizeStartDim;
        // Set each frame so DrawDockedSlot can tint the handle strip correctly.
        private DockEdge _dockResizeHoverEdge = DockEdge.None;

        private void ProbeDockedResize(DockEdge edge, Vector2 pos, Vector2 size, ImGuiIOPtr io)
        {
            // Hit area: 6 px each side of the inner edge - wide enough to find easily.
            const float HalfW = 6f;
            Vector2 hTL, hBR;
            switch (edge)
            {
                case DockEdge.Left:   hTL = new(pos.X + size.X - HalfW, pos.Y); hBR = new(pos.X + size.X + HalfW, pos.Y + size.Y); break;
                case DockEdge.Right:  hTL = new(pos.X - HalfW,          pos.Y); hBR = new(pos.X + HalfW,          pos.Y + size.Y); break;
                case DockEdge.Top:    hTL = new(pos.X, pos.Y + size.Y - HalfW); hBR = new(pos.X + size.X,         pos.Y + size.Y + HalfW); break;
                default:              hTL = new(pos.X, pos.Y - HalfW);           hBR = new(pos.X + size.X,         pos.Y + HalfW);           break;
            }

            // Direct bounds check - IsMouseHoveringRect uses the current window's clip
            // rect which is undefined here (between Begin/End) and silently returns false.
            var  mp  = io.MousePos;
            bool hov = mp.X >= hTL.X && mp.X <= hBR.X && mp.Y >= hTL.Y && mp.Y <= hBR.Y
                       && ChromePanel.DragSource == null; // don't start resize during panel drag

            bool isResizingThis = _dockResizing && _dockResizeEdge == edge;

            // Track hover edge for the visual strip drawn inside DrawDockedSlot.
            if (hov || isResizingThis) _dockResizeHoverEdge = edge;
            else if (_dockResizeHoverEdge == edge) _dockResizeHoverEdge = DockEdge.None;

            if (hov || isResizingThis)
            {
                ImGuiNET.ImGui.SetMouseCursor(
                    edge is DockEdge.Left or DockEdge.Right
                        ? ImGuiMouseCursor.ResizeEW
                        : ImGuiMouseCursor.ResizeNS);

                if (hov && io.MouseClicked[0] && !_dockResizing)
                {
                    _dockResizing         = true;
                    _dockResizeEdge       = edge;
                    _dockResizeStartMouse = edge is DockEdge.Left or DockEdge.Right
                        ? io.MousePos.X : io.MousePos.Y;
                    _dockResizeStartDim   = _dockDim.GetValueOrDefault(edge, DockMinDim);
                }
            }

            if (isResizingThis)
            {
                if (io.MouseDown[0])
                {
                    float cur   = edge is DockEdge.Left or DockEdge.Right ? io.MousePos.X : io.MousePos.Y;
                    float delta = edge switch
                    {
                        DockEdge.Left   =>  cur - _dockResizeStartMouse,
                        DockEdge.Right  => _dockResizeStartMouse - cur,
                        DockEdge.Top    =>  cur - _dockResizeStartMouse,
                        DockEdge.Bottom => _dockResizeStartMouse - cur,
                        _ => 0f
                    };
                    _dockDim[edge] = Math.Max(DockMinDim, _dockResizeStartDim + delta);
                }
                else
                {
                    _dockResizing = false;
                    SaveDockConfig();
                }
            }
        }

        private void DrawDockedSlot(DockEdge edge, ChromePanel panel, ImGuiIOPtr io, Vector2 pos, Vector2 size)
        {
            var slotFlags =
                ImGuiWindowFlags.NoTitleBar         |
                ImGuiWindowFlags.NoResize           |
                ImGuiWindowFlags.NoMove             |
                ImGuiWindowFlags.NoCollapse         |
                ImGuiWindowFlags.NoScrollbar        |
                ImGuiWindowFlags.NoScrollWithMouse  |
                ImGuiWindowFlags.NoSavedSettings    |
                ImGuiWindowFlags.NoFocusOnAppearing |
                (ImGuiWindowFlags)(1 << 13);        // NoBringToDisplayFront

            ImGuiNET.ImGui.SetNextWindowPos(pos,  ImGuiCond.Always);
            ImGuiNET.ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    Vector2.Zero);
            // Match ChromePanel: transparent window bg, everything drawn via draw list.
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);

            bool visible = ImGuiNET.ImGui.Begin("##dockslot_" + edge, slotFlags);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopStyleVar(3);

            if (visible)
            {
                var wp = ImGuiNET.ImGui.GetWindowPos();
                var ws = ImGuiNET.ImGui.GetWindowSize();
                var dl = ImGuiNET.ImGui.GetWindowDrawList();

                // Keep panel's screen rect in sync so ContainsScreenPoint works
                // for drag-and-drop targeting (panel.Draw() is skipped while docked).
                panel.UpdateScreenRect(wp, ws);

                // Fetch this panel's exact chrome colours (subclass may override them).
                uint chromeBg     = panel.GetChromeBg();
                uint chromeTitleBg= panel.GetChromeTitleBg();
                uint chromeBorder = panel.GetChromeBorder();
                uint chromeTxt    = panel.GetChromeTitleTxt();
                uint chromeScroll = panel.GetChromeScrollThumb();

                // ── Background ────────────────────────────────────────────────
                dl.AddRectFilled(wp, wp + ws, chromeBg);

                // ── Title bar ────────────────────────────────────────────────
                dl.AddRectFilled(wp, new Vector2(wp.X + ws.X, wp.Y + DockTitleH), chromeTitleBg);

                var   font  = ImGuiStyle.FontBold;
                float fs    = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                dl.AddText(font, fs,
                    new Vector2(wp.X + 10f, wp.Y + (DockTitleH - fs) * 0.5f),
                    chromeTxt, panel.Title);

                // ── Outer border ──────────────────────────────────────────────
                dl.AddRect(wp, wp + ws, chromeBorder, 0f, ImDrawFlags.None, 1f);

                // ── Resize handle strip at inner edge ─────────────────────────
                // Drawn as a tinted line so users can see where to grab.
                bool handleActive = _dockResizeHoverEdge == edge || (_dockResizing && _dockResizeEdge == edge);
                uint handleCol    = UI.Alpha(ImGuiStyle.Accent, handleActive ? (byte)200 : (byte)55);
                float handleThick = handleActive ? 3f : 1.5f;
                switch (edge)
                {
                    case DockEdge.Left:
                        dl.AddLine(new Vector2(wp.X + ws.X - 1f, wp.Y), new Vector2(wp.X + ws.X - 1f, wp.Y + ws.Y), handleCol, handleThick); break;
                    case DockEdge.Right:
                        dl.AddLine(new Vector2(wp.X,             wp.Y), new Vector2(wp.X,             wp.Y + ws.Y), handleCol, handleThick); break;
                    case DockEdge.Top:
                        dl.AddLine(new Vector2(wp.X, wp.Y + ws.Y - 1f), new Vector2(wp.X + ws.X, wp.Y + ws.Y - 1f), handleCol, handleThick); break;
                    case DockEdge.Bottom:
                        dl.AddLine(new Vector2(wp.X, wp.Y),             new Vector2(wp.X + ws.X, wp.Y),             handleCol, handleThick); break;
                }

                // ── Title bar buttons (close + undock) ───────────────────────
                const float BtnS = 18f, BtnPad = 6f;
                float by = (DockTitleH - BtnS) * 0.5f;

                // Close (×) button - hides the panel and undocks it.
                float closeBx = ws.X - 2f * BtnS - 2f * BtnPad;
                ImGuiNET.ImGui.SetCursorPos(new Vector2(closeBx, by));
                ImGuiNET.ImGui.InvisibleButton("##dockclosebtn_" + edge, new Vector2(BtnS, BtnS));
                bool closeHov = ImGuiNET.ImGui.IsItemHovered();
                bool closeClk = ImGuiNET.ImGui.IsItemClicked();
                if (closeHov) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                Vector2 cTL = new(wp.X + closeBx, wp.Y + by);
                Vector2 cBR = new(cTL.X + BtnS, cTL.Y + BtnS);
                dl.AddRectFilled(cTL, cBR, UI.Alpha(ImGuiStyle.Accent, closeHov ? (byte)55 : (byte)20), 4f);
                dl.AddRect(cTL, cBR, UI.Alpha(ImGuiStyle.Accent, closeHov ? (byte)200 : (byte)80), 4f, ImDrawFlags.None, 1f);
                float xPad = 4.5f;
                uint  xCol = UI.Alpha(ImGuiStyle.Accent, closeHov ? (byte)255 : (byte)140);
                dl.AddLine(new Vector2(cTL.X + xPad, cTL.Y + xPad), new Vector2(cBR.X - xPad, cBR.Y - xPad), xCol, 1.5f);
                dl.AddLine(new Vector2(cBR.X - xPad, cTL.Y + xPad), new Vector2(cTL.X + xPad, cBR.Y - xPad), xCol, 1.5f);

                // Undock (float) button.
                float bx = ws.X - BtnS - BtnPad;
                ImGuiNET.ImGui.SetCursorPos(new Vector2(bx, by));
                ImGuiNET.ImGui.InvisibleButton("##dockbtn_" + edge, new Vector2(BtnS, BtnS));
                bool btnHov = ImGuiNET.ImGui.IsItemHovered();
                bool btnClk = ImGuiNET.ImGui.IsItemClicked();
                if (btnHov) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                Vector2 bTL = new(wp.X + bx, wp.Y + by);
                Vector2 bBR = new(bTL.X + BtnS, bTL.Y + BtnS);
                dl.AddRectFilled(bTL, bBR, UI.Alpha(ImGuiStyle.Accent, btnHov ? (byte)55 : (byte)20), 4f);
                dl.AddRect(bTL, bBR, UI.Alpha(ImGuiStyle.Accent, btnHov ? (byte)200 : (byte)80), 4f, ImDrawFlags.None, 1f);
                uint btnIco = UI.Alpha(ImGuiStyle.Accent, btnHov ? (byte)255 : (byte)140);
                float p = 4f, sq = BtnS - p * 2f;
                Vector2 sq1 = new(bTL.X + p, bTL.Y + p + sq * 0.45f);
                dl.AddRect(sq1, new Vector2(sq1.X + sq * 0.55f, sq1.Y + sq * 0.55f), btnIco, 1f, ImDrawFlags.None, 1f);
                float ax = bTL.X + p + sq * 0.5f, ay = bTL.Y + p, aw = sq * 0.45f;
                dl.AddLine(new Vector2(ax, ay),      new Vector2(ax + aw, ay),      btnIco, 1.5f);
                dl.AddLine(new Vector2(ax + aw, ay), new Vector2(ax + aw, ay + aw), btnIco, 1.5f);
                dl.AddLine(new Vector2(ax, ay + aw), new Vector2(ax + aw, ay),      btnIco, 1.5f);

                if (closeClk)
                {
                    if (panel is GroupPanel gp)
                    {
                        var members = gp.Members.ToList();
                        UndockPanel(panel);
                        DissolveGroup(gp);
                        _groups.Remove(gp);
                        foreach (var m in members) HidePanel(m);
                        SaveGroupConfig();
                    }
                    else
                    {
                        UndockPanel(panel);
                        HidePanel(panel);
                    }
                }

                if (btnClk)
                    UndockPanel(panel);

                // ── Content child (mirrors ChromePanel.DrawScrollableContent) ─
                ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, DockTitleH));
                bool hasPad  = panel.GetHasContentPadding();
                bool hasPadX = panel.GetHasScrollWindowPadding();
                // Subtract the same topGap (13px) that DrawScrollableContent applies for HasContentPadding
                // panels, so chromeBtm inside the content child sits 13px above the slot border -
                // matching the bottom gap visible in standalone floating panels.
                float contentH = ws.Y - DockTitleH - (hasPad ? 13f : 0f);
                if (contentH > 1f)
                {
                    var  scrollFl = panel.GetChromeScrollExtraFlags();

                    ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,     new Vector2(hasPadX ? 8f : 0f, 0f));
                    ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize,     5f);
                    ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 3f);
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg,              Vector4.Zero);
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Vector4.Zero);
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        UI.ToV4(UI.Alpha(chromeScroll, 90)));
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, UI.ToV4(UI.Alpha(chromeScroll, 200)));
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  UI.ToV4(ImGuiStyle.Accent));

                    bool inChild = ImGuiNET.ImGui.BeginChild(
                        "##dockcontent_" + edge,
                        new Vector2(0f, contentH),
                        ImGuiChildFlags.None,
                        ImGuiWindowFlags.NoScrollWithMouse | scrollFl);

                    // Intentionally NOT popped here - mirrors DrawScrollableContent which keeps
                    // ChildBg=Zero active through content execution so nested child windows
                    // (e.g. ##phrows in PlayerHistoryPanel) also get ChildBg=Zero, matching
                    // the standalone floating path exactly.

                    if (inChild)
                    {
                        if (hasPad)
                        {
                            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
                                new Vector2(ImGuiNET.ImGui.GetStyle().ItemSpacing.X, 0f));
                            ImGuiNET.ImGui.Dummy(new Vector2(1f, 13f));
                            ImGuiNET.ImGui.PopStyleVar();
                        }
                        UI.ClearWheelConsumed();
                        panel.InvokeDrawContent();

                        if (!UI.WheelConsumedThisFrame)
                        {
                            float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
                            if (wheel != 0f && ImGuiNET.ImGui.IsWindowHovered())
                            {
                                float step = ImGuiNET.ImGui.GetTextLineHeight() * 3f;
                                ImGuiNET.ImGui.SetScrollY(
                                    Math.Clamp(ImGuiNET.ImGui.GetScrollY() - wheel * step,
                                               0f, ImGuiNET.ImGui.GetScrollMaxY()));
                            }
                        }
                    }
                    ImGuiNET.ImGui.EndChild();

                    // Accent line - matches ChromePanel's ##chraccent_ overlay.
                    ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, DockTitleH));
                    ImGuiNET.ImGui.BeginChild("##dockaccent_" + edge, new Vector2(ws.X, 1f),
                        ImGuiChildFlags.None,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                        ImGuiWindowFlags.NoNav       | ImGuiWindowFlags.NoInputs);
                    var adl  = ImGuiNET.ImGui.GetWindowDrawList();
                    var apos = ImGuiNET.ImGui.GetWindowPos();
                    adl.AddLine(apos, new Vector2(apos.X + ws.X, apos.Y),
                        UI.Alpha(ImGuiStyle.Accent, 210), 1.5f);
                    ImGuiNET.ImGui.EndChild();

                    // Pop after both children - mirrors DrawScrollableContent's pop timing.
                    ImGuiNET.ImGui.PopStyleColor(5);
                    ImGuiNET.ImGui.PopStyleVar(3);
                }
            }
            ImGuiNET.ImGui.End();
        }

        // ── Snap / merge zone system ─────────────────────────────────────────
        private enum SnapZone { None, Left, Right, Top, Bottom, Merge }

        private static SnapZone GetSnapZone(ChromePanel target, Vector2 mouse)
            => GetSnapZoneForRect(mouse, target.ScreenPos, target.ScreenSize);

        private static SnapZone GetSnapZoneForRect(Vector2 mouse, Vector2 pos, Vector2 sz)
        {
            if (sz.X <= 0f || sz.Y <= 0f) return SnapZone.Merge;
            float rx = (mouse.X - pos.X) / sz.X;
            float ry = (mouse.Y - pos.Y) / sz.Y;
            float dx = MathF.Abs(rx - 0.5f);
            float dy = MathF.Abs(ry - 0.5f);
            if (dx < 0.25f && dy < 0.25f) return SnapZone.Merge;
            if (dx > dy) return rx < 0.5f ? SnapZone.Left  : SnapZone.Right;
            else         return ry < 0.5f ? SnapZone.Top   : SnapZone.Bottom;
        }

        private void DrawGroupDropZones()
        {
            var src = ChromePanel.DragSource;
            if (src == null) return;

            var   io    = ImGuiNET.ImGui.GetIO();
            var   disp  = io.DisplaySize;
            float menuH = ImGuiNET.ImGui.GetFrameHeight();

            // ── Edge dock zones (take priority over panel merge zones) ────────
            DockEdge hoverEdge = DockEdge.None;
            var mp = io.MousePos;
            if      (mp.X < DockEdgeZone)              hoverEdge = DockEdge.Left;
            else if (mp.X > disp.X - DockEdgeZone)     hoverEdge = DockEdge.Right;
            else if (mp.Y < menuH + DockEdgeZone)       hoverEdge = DockEdge.Top;
            else if (mp.Y > disp.Y - DockEdgeZone)      hoverEdge = DockEdge.Bottom;

            if (Program.Config.PanelAnchoring && hoverEdge != DockEdge.None && !src.IsGrouped)
            {
                DrawEdgeDockPreview(hoverEdge, src, menuH, disp);
                if (!io.MouseDown[0])
                {
                    DockPanel(src, hoverEdge);
                    ChromePanel.ClearDragSource();
                }
                return; // skip panel merge zone handling
            }

            // ── Split-pane drop zones (checked first - splits draw last = highest Z) ──
            SplitPanel splitTarget  = null;
            int        splitPaneIdx = -1;
            if (Program.Config.PanelMerging && !(src is SplitPanel))
            {
                var found = FindDeepestSplitPane(io.MousePos);
                if (found.HasValue) { splitTarget = found.Value.Split; splitPaneIdx = found.Value.PaneIdx; }
            }

            var zone           = SnapZone.None;
            ChromePanel target = null;

            if (splitTarget != null)
            {
                var (pPos, pSz) = splitTarget.GetPaneRect(splitPaneIdx);
                zone = GetSnapZoneForRect(io.MousePos, pPos, pSz);
                if (zone == SnapZone.Merge) zone = SnapZone.None; // merge-into-pane not supported
                if (zone != SnapZone.None)
                    DrawSnapOverlay(pPos, pSz, zone);
            }
            else
            {
                foreach (var panel in GetVisibleMergeablePanels())
                {
                    if (ReferenceEquals(panel, src)) continue;
                    if (panel.ContainsScreenPoint(io.MousePos)) { target = panel; break; }
                }

                if (target != null)
                {
                    // Docked and floating targets share the same zone detection.
                    // For docked targets, directional zones create a split inside the slot;
                    // centre (Merge) groups them as tabs - both results stay anchored.
                    zone = GetSnapZone(target, io.MousePos);
                    if (zone == SnapZone.Merge && !Program.Config.PanelMerging)
                        zone = SnapZone.None;
                    if ((zone == SnapZone.Left || zone == SnapZone.Right ||
                         zone == SnapZone.Top  || zone == SnapZone.Bottom) &&
                        (!Program.Config.PanelMerging || (src is SplitPanel && !target.IsDocked)))
                        zone = SnapZone.None;
                    if (zone != SnapZone.None)
                        DrawSnapOverlay(target, zone);
                }
            }

            if (!io.MouseDown[0])
            {
                if (splitTarget != null && zone != SnapZone.None)
                {
                    bool sameDir = splitTarget.Horizontal
                        ? (zone == SnapZone.Left || zone == SnapZone.Right)
                        : (zone == SnapZone.Top  || zone == SnapZone.Bottom);
                    if (sameDir) AddPaneToSplit(splitTarget, splitPaneIdx, src, zone);
                    else         NestedAddToSplit(splitTarget, splitPaneIdx, src, zone);
                }
                else if (target != null && zone != SnapZone.None)
                {
                    if (target.IsDocked)
                    {
                        var dockedPanel = _docked[target.DockedEdge];
                        if (zone == SnapZone.Merge)
                        {
                            MergeIntoDocked(src, dockedPanel, target.DockedEdge);
                            SaveDockConfig();
                        }
                        else
                        {
                            SplitWithinDock(src, dockedPanel, target.DockedEdge, zone);
                        }
                    }
                    else
                    {
                        switch (zone)
                        {
                            case SnapZone.Merge:                         MergePanels(src, target); break;
                            case SnapZone.Left: case SnapZone.Right:
                            case SnapZone.Top:  case SnapZone.Bottom:   SplitPanels(src, target, zone); break;
                        }
                    }
                }
                ChromePanel.ClearDragSource();
            }
        }

        private static void DrawSnapOverlay(ChromePanel target, SnapZone activeZone)
            => DrawSnapOverlay(target.ScreenPos, target.ScreenSize, activeZone);

        private static void DrawSnapOverlay(Vector2 pos, Vector2 sz, SnapZone activeZone)
        {
            var   dl   = ImGuiNET.ImGui.GetForegroundDrawList();
            var   br   = pos + sz;
            var   ctr  = pos + sz * 0.5f;
            var   font = ImGuiStyle.FontBold;
            float fs   = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            // Full panel border - always visible.
            dl.AddRect(pos, br, UI.Alpha(ImGuiStyle.Accent, 180), 8f, ImDrawFlags.None, 2.5f);

            // Active zone fill covers the relevant half (or all for Merge).
            switch (activeZone)
            {
                case SnapZone.Left:
                    dl.AddRectFilled(pos, new Vector2(pos.X + sz.X * 0.5f, br.Y),
                        UI.Alpha(ImGuiStyle.Accent, 30)); break;
                case SnapZone.Right:
                    dl.AddRectFilled(new Vector2(pos.X + sz.X * 0.5f, pos.Y), br,
                        UI.Alpha(ImGuiStyle.Accent, 30)); break;
                case SnapZone.Top:
                    dl.AddRectFilled(pos, new Vector2(br.X, pos.Y + sz.Y * 0.5f),
                        UI.Alpha(ImGuiStyle.Accent, 30)); break;
                case SnapZone.Bottom:
                    dl.AddRectFilled(new Vector2(pos.X, pos.Y + sz.Y * 0.5f), br,
                        UI.Alpha(ImGuiStyle.Accent, 30)); break;
                case SnapZone.Merge:
                    dl.AddRectFilled(pos, br, UI.Alpha(ImGuiStyle.Accent, 22)); break;
            }

            // Five indicator pills: one per zone.  Active pill is fully lit; others dim.
            DrawZoneIndicator(dl, font, fs, new Vector2(pos.X + sz.X * 0.13f, ctr.Y),   "Left",   activeZone == SnapZone.Left);
            DrawZoneIndicator(dl, font, fs, new Vector2(pos.X + sz.X * 0.87f, ctr.Y),   "Right",  activeZone == SnapZone.Right);
            DrawZoneIndicator(dl, font, fs, new Vector2(ctr.X, pos.Y + sz.Y * 0.13f),   "Top",    activeZone == SnapZone.Top);
            DrawZoneIndicator(dl, font, fs, new Vector2(ctr.X, pos.Y + sz.Y * 0.87f),   "Bottom", activeZone == SnapZone.Bottom);
            DrawZoneIndicator(dl, font, fs, ctr,                                          "Merge",  activeZone == SnapZone.Merge);
        }

        private static void DrawZoneIndicator(ImDrawListPtr dl, ImFontPtr font, float fs,
            Vector2 center, string label, bool active)
        {
            const float PadX = 8f, PadY = 3f;
            var  tsz = font.CalcTextSizeA(fs, float.MaxValue, -1f, label);
            var  tl  = new Vector2(center.X - tsz.X * 0.5f - PadX, center.Y - fs * 0.5f - PadY);
            var  tbr = new Vector2(center.X + tsz.X * 0.5f + PadX, center.Y + fs * 0.5f + PadY);
            dl.AddRectFilled(tl, tbr, UI.Alpha(ImGuiStyle.Dark1, active ? (byte)225 : (byte)170), 5f);
            dl.AddRect(tl, tbr, UI.Alpha(ImGuiStyle.Accent, active ? (byte)220 : (byte)80), 5f, ImDrawFlags.None, 1.5f);
            dl.AddText(font, fs,
                new Vector2(center.X - tsz.X * 0.5f, center.Y - fs * 0.5f),
                UI.Alpha(ImGuiStyle.Accent, active ? (byte)255 : (byte)110), label);
        }

        private static void DrawEdgeDockPreview(DockEdge edge, ChromePanel src, float menuH, Vector2 disp)
        {
            // Preview strip: shows where the panel would dock.
            float dim = Math.Max(DockMinDim,
                edge is DockEdge.Left or DockEdge.Right ? src.ScreenSize.X : src.ScreenSize.Y);

            Vector2 prevTL, prevBR;
            string  label;
            switch (edge)
            {
                case DockEdge.Left:
                    prevTL = new Vector2(0f,         menuH);
                    prevBR = new Vector2(dim,         disp.Y);
                    label  = "Dock Left";
                    break;
                case DockEdge.Right:
                    prevTL = new Vector2(disp.X - dim, menuH);
                    prevBR = new Vector2(disp.X,       disp.Y);
                    label  = "Dock Right";
                    break;
                case DockEdge.Top:
                    prevTL = new Vector2(0f,  menuH);
                    prevBR = new Vector2(disp.X, menuH + dim);
                    label  = "Dock Top";
                    break;
                default: // Bottom
                    prevTL = new Vector2(0f,        disp.Y - dim);
                    prevBR = new Vector2(disp.X,    disp.Y);
                    label  = "Dock Bottom";
                    break;
            }

            var dl = ImGuiNET.ImGui.GetForegroundDrawList();
            dl.AddRectFilled(prevTL, prevBR, UI.Alpha(ImGuiStyle.Accent, 28));
            dl.AddRect(prevTL, prevBR, UI.Alpha(ImGuiStyle.Accent, 200), 0f, ImDrawFlags.None, 2f);

            var   font = ImGuiStyle.FontBold;
            float fs   = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            var   tsz  = font.CalcTextSizeA(fs, float.MaxValue, -1f, label);
            var   ctr  = (prevTL + prevBR) * 0.5f;
            var   pTL  = new Vector2(ctr.X - tsz.X * 0.5f - 10f, ctr.Y - fs * 0.5f - 4f);
            var   pBR  = new Vector2(ctr.X + tsz.X * 0.5f + 10f, ctr.Y + fs * 0.5f + 4f);
            dl.AddRectFilled(pTL, pBR, UI.Alpha(ImGuiStyle.Dark1, 210), 6f);
            dl.AddRect(pTL, pBR, UI.Alpha(ImGuiStyle.Accent, 180), 6f, ImDrawFlags.None, 1f);
            dl.AddText(font, fs,
                new Vector2(ctr.X - tsz.X * 0.5f, ctr.Y - fs * 0.5f),
                ImGuiStyle.Accent, label);
        }

        private (SplitPanel Split, int PaneIdx)? FindDeepestSplitPane(Vector2 p)
        {
            for (int si = _splits.Count - 1; si >= 0; si--)
            {
                var s = _splits[si];
                if (s.IsGrouped || s.IsDocked) continue;
                var result = FindDeepestPaneInSplit(s, p);
                if (result.HasValue) return result;
            }
            return null;
        }

        private (SplitPanel Split, int PaneIdx)? FindDeepestPaneInSplit(SplitPanel split, Vector2 p)
        {
            int pi = split.GetPaneIndexAt(p);
            if (pi < 0) return null;
            // If the matched pane is itself a SplitPanel, recurse into it.
            if (split.GetPane(pi) is SplitPanel nested)
            {
                var deeper = FindDeepestPaneInSplit(nested, p);
                if (deeper.HasValue) return deeper;
            }
            return (split, pi);
        }

        private void NestedAddToSplit(SplitPanel outer, int outerPaneIdx, ChromePanel src, SnapZone zone)
        {
            if (src.IsGrouped) return;
            if (_splits.Any(s => s.Panes.Contains(src))) return;

            var targetPane = outer.GetPane(outerPaneIdx);
            outer.RemovePane(outerPaneIdx); // temporarily un-groups targetPane

            bool nestedH = zone == SnapZone.Left || zone == SnapZone.Right;
            ChromePanel first, second;
            if (zone == SnapZone.Left || zone == SnapZone.Top) (first, second) = (src, targetPane);
            else                                               (first, second) = (targetPane, src);

            string key    = $"__split_{_nextSplitId++}";
            var nested    = new SplitPanel(key, first, second, nestedH);
            nested.MemberPopped += OnSplitMemberPopped;
            _splits.Add(nested);

            outer.InsertPane(nested, outerPaneIdx); // nested.IsGrouped now true (inside outer)
            SaveSplitConfig();
        }

        private void AddPaneToSplit(SplitPanel split, int paneIdx, ChromePanel src, SnapZone zone)
        {
            if (src.IsGrouped) return;
            if (_splits.Exists(s => s.Panes.Contains(src))) return;
            bool insertBefore = zone == SnapZone.Left || zone == SnapZone.Top;
            split.InsertPane(src, insertBefore ? paneIdx : paneIdx + 1);
            SaveSplitConfig();
        }

        private void SplitPanels(ChromePanel src, ChromePanel target, SnapZone zone)
        {
            // Only standalone panels can form a new split.
            if (src.IsGrouped || target.IsGrouped) return;
            // Don't split panels already inside a split.
            if (_splits.Exists(s => s.Panes.Contains(src))) return;
            if (_splits.Exists(s => s.Panes.Contains(target))) return;

            bool horizontal = zone == SnapZone.Left || zone == SnapZone.Right;

            // Determine which panel is "first" (left/top) vs "second" (right/bottom).
            ChromePanel first, second;
            if (zone == SnapZone.Left || zone == SnapZone.Top)
                (first, second) = (src, target);
            else
                (first, second) = (target, src);

            // Pre-seed the layout entry so the new SplitPanel's first Draw() loads
            // the target's exact position/size via the normal _placed=false path.
            // This avoids SetPosition (which sets _placed=true, bypassing config loading)
            // and avoids relying on InitSize (which would sum vertical pane heights).
            var tPos  = target.ScreenPos;
            var tSize = target.ScreenSize;
            var split = CreateSplit(first, second, horizontal);
            Program.Config.PanelLayouts[split.Title] = new PanelLayoutEntry
            {
                X = tPos.X, Y = tPos.Y,
                W = tSize.X, H = tSize.Y
            };
            SaveSplitConfig();
        }

        private void MergePanels(ChromePanel src, ChromePanel target)
        {
            GroupPanel tgtGroup = target as GroupPanel
                ?? _groups.Find(g => g.Members.Contains(target));

            // Determine (or create) the destination group.
            GroupPanel dest = tgtGroup;
            if (dest == null)
            {
                dest = CreateGroup(new[] { target });
                dest.SetPosition(target.ScreenPos);
            }

            // Flatten src: dissolve any groups/splits and add their leaf panels.
            var toAdd = new List<ChromePanel>();
            FlattenIncoming(src, toAdd);
            foreach (var m in toAdd)
                dest.AddMember(m);

            SaveGroupConfig();
        }

        private void SaveGroupConfig()
        {
            Program.Config.PanelOpen.PanelGroups =
                _groups.Select(g => g.Members.Select(m => m.Title).ToList()).ToList();
            Program.Config.SaveDebounced();
        }

        // ── Split helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Creates a SplitPanel from <paramref name="src"/> and the current occupant of
        /// <paramref name="edge"/>, then docks the resulting split at that same edge.
        /// The split remains visually inside the dock slot; the slot dimension is preserved.
        /// </summary>
        private void SplitWithinDock(ChromePanel src, ChromePanel existing, DockEdge edge, SnapZone zone)
        {
            bool horizontal = zone == SnapZone.Left || zone == SnapZone.Right;
            ChromePanel first, second;
            if (zone == SnapZone.Left || zone == SnapZone.Top) (first, second) = (src, existing);
            else                                               (first, second) = (existing, src);

            // Remove the existing panel from the dock slot without floating it.
            existing.DockedEdge = DockEdge.None;
            _docked.Remove(edge);

            var split = CreateSplit(first, second, horizontal);

            // Dock the new split in the vacated slot - keep the existing slot dimension.
            split.DockedEdge = edge;
            _docked[edge]    = split;

            SaveDockConfig();
            SaveSplitConfig();
        }

        private SplitPanel CreateSplit(ChromePanel first, ChromePanel second, bool horizontal)
        {
            string key = $"__split_{_nextSplitId++}";
            var split  = new SplitPanel(key, first, second, horizontal);
            split.MemberPopped += OnSplitMemberPopped;
            _splits.Add(split);
            return split;
        }

        private void OnSplitMemberPopped(SplitPanel split, ChromePanel panel)
        {
            panel.RequestFocus();
            _bringToFrontWindow    = "##chrome_" + panel.Title;
            _bringToFrontWindowTtl = 2;
            // SplitPanel.DrawPanelContent sets ShowState=false when panes < 2 - don't override here.
            SaveSplitConfig();
        }

        private void SaveSplitConfig()
        {
            Program.Config.PanelOpen.PanelSplits = _splits
                .Where(s => !s.IsGrouped && s.PaneCount >= 2)
                .Select(SerializeSplit)
                .Where(e => e != null)
                .ToList();
            Program.Config.SaveDebounced();
        }

        private PanelSplitEntry SerializeSplit(SplitPanel split)
        {
            if (split.PaneCount < 2) return null;
            var slots = split.Panes.Select(p =>
            {
                if (p is SplitPanel nested) return new SplitPaneSlot { Split = SerializeSplit(nested) };
                return string.IsNullOrEmpty(p?.Title) ? null : new SplitPaneSlot { Panel = p.Title };
            }).Where(s => s != null).ToList();
            if (slots.Count < 2) return null;
            return new PanelSplitEntry
            {
                Slots      = slots,
                Ratios     = split.Ratios.ToList(),
                Horizontal = split.Horizontal,
                DockedEdge = split.IsDocked ? split.DockedEdge.ToString().ToLowerInvariant() : null
            };
        }

        private SplitPanel RestoreSplitFromConfig(PanelSplitEntry entry)
        {
            if (entry == null) return null;

            // Build the ordered pane list (a slot is a named panel or a nested split).
            List<ChromePanel> panes = new();
            foreach (var slot in entry.Slots ?? [])
            {
                ChromePanel p = null;
                if (slot.Split != null)
                {
                    p = RestoreSplitFromConfig(slot.Split);
                }
                else if (!string.IsNullOrEmpty(slot.Panel) &&
                         _panelByTitle.TryGetValue(slot.Panel, out var found) &&
                         found != null && !found.IsGrouped && !found.IsDocked)
                {
                    p = found;
                }
                if (p != null) panes.Add(p);
            }

            if (panes.Count < 2) return null;

            var split = CreateSplit(panes[0], panes[1], entry.Horizontal);
            for (int i = 2; i < panes.Count; i++) split.InsertPane(panes[i]);
            if (entry.Ratios?.Count == panes.Count) split.SetRatios(entry.Ratios);
            return split;
        }
    }
}
