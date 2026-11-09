using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Panels;
using SC2Radar.UI.Misc;

namespace SC2Radar.UI.ImGui.Widgets
{
    // Widget layout: drag-to-merge groups, splits, edge docking and the drop-zone overlay - ported unchanged from the
    // EFT radar's WidgetManager (widgets merge / dock only with each other, never with settings panels). The SC2 widgets
    // and their show flags are in WidgetManager.cs.
    public sealed partial class WidgetManager
    {
        // ── Group helpers ────────────────────────────────────────────────────

        private WidgetGroup CreateGroup(IEnumerable<WidgetChrome> initialMembers)
        {
            string key   = $"__wgtgroup_{_nextGroupId++}";
            var    group = new WidgetGroup(key, initialMembers);
            group.MemberPopped += OnMemberPopped;
            _groups.Add(group);
            return group;
        }

        private void DissolveGroup(WidgetGroup group)
        {
            foreach (var m in group.Members)
            {
                m.IsGrouped = false;
                m.IsHosted  = false;
            }
        }

        /// <summary>Hides a widget by toggling its WidgetManager show-flag to false.</summary>
        private void HideWidget(WidgetChrome widget)
        {
            if (_hideActions.TryGetValue(widget, out var hide))
                hide();
        }

        private void OnMemberPopped(WidgetGroup group, WidgetChrome widget)
        {
            var dropPos = group.PendingPopOutPos;
            if (!float.IsNaN(dropPos.X))
                widget.SetPosition(new Vector2(dropPos.X - 20f, dropPos.Y - 15f));

            widget.RequestFocus();
            _bringToFront    = "##widgetchrome_" + widget.Title;
            _bringToFrontTtl = 2;

            if (group.MemberCount < 2)
            {
                foreach (var m in group.Members)
                {
                    m.IsGrouped = false;
                    m.IsHosted  = false;
                }
                group.DissolvedByPop = true;
                group.ShowState = false;
            }
            else
            {
                SaveGroupConfig();
            }
        }

        /// <summary>Iterates mergeable widgets (excludes grouped widgets; includes docked widgets).
        /// Groups are yielded first because they are drawn last (highest Z-order).</summary>
        private void SaveGroupConfig()
        {
            Config.WidgetOpen.WidgetGroups =
                _groups.Select(g => g.Members.Select(m => m.Title).ToList()).ToList();
            Config.SaveDebounced();
        }

        // ── Dock system ──────────────────────────────────────────────────────
        // How close (px) the cursor must be to a screen edge while dragging before the
        // dock-preview zone activates. Kept in sync with MenuManager's own DockEdgeZone
        // (the parallel mechanism for settings panels) so widgets and panels dock at the
        // same distance from the app's edge.
        private const float DockEdgeZone = 32f;
        private const float DockMinDim   = 200f;

        private void DockWidget(WidgetChrome widget, DockEdge edge, bool save = true)
        {
            if (widget.IsGrouped) return; // must detach from group first

            if (widget.IsDocked && widget.DockedEdge != edge)
            {
                _docked.Remove(widget.DockedEdge);
                widget.DockedEdge = DockEdge.None;
            }

            if (_docked.TryGetValue(edge, out var existing) && !ReferenceEquals(existing, widget))
            {
                MergeIntoDocked(widget, existing, edge);
                if (save) SaveDockConfig();
                return;
            }

            widget.DockedEdge = edge;
            _docked[edge] = widget;
            _dockDim[edge] = Math.Max(DockMinDim,
                edge is DockEdge.Left or DockEdge.Right ? widget.ScreenSize.X : widget.ScreenSize.Y);

            if (save) SaveDockConfig();
        }

        /// <summary>
        /// Recursively flattens <paramref name="widget"/> into its leaf WidgetChromes,
        /// dissolving any WidgetGroups and WidgetSplits encountered along the way.
        /// </summary>
        private void FlattenIncoming(WidgetChrome widget, List<WidgetChrome> result)
        {
            if (widget is WidgetGroup wg)
            {
                var members = wg.Members.ToList();
                DissolveGroup(wg);
                _groups.Remove(wg);
                foreach (var m in members) FlattenIncoming(m, result);
            }
            else if (widget is WidgetSplit ws)
            {
                var panes = Enumerable.Range(0, ws.PaneCount).Select(i => ws.GetPane(i)).ToList();
                foreach (var p in panes) { p.IsGrouped = false; p.IsHosted = false; }
                if (ws.IsDocked) { _docked.Remove(ws.DockedEdge); ws.DockedEdge = DockEdge.None; }
                _splits.Remove(ws);
                foreach (var p in panes) FlattenIncoming(p, result);
            }
            else
            {
                result.Add(widget);
            }
        }

        private void MergeIntoDocked(WidgetChrome incoming, WidgetChrome existing, DockEdge edge)
        {
            var toAdd = new List<WidgetChrome>();
            FlattenIncoming(incoming, toAdd);

            if (existing is WidgetGroup exGp)
            {
                foreach (var m in toAdd)
                    exGp.AddMember(m);
            }
            else
            {
                existing.DockedEdge = DockEdge.None;
                _docked.Remove(edge);

                var allMembers = new List<WidgetChrome>(1 + toAdd.Count) { existing };
                allMembers.AddRange(toAdd);

                var newGroup = CreateGroup(allMembers);
                newGroup.DockedEdge = edge;
                _docked[edge] = newGroup;
                // _dockDim[edge] intentionally preserved - slot size stays the same.
            }

            SaveGroupConfig();
        }

        private void UndockWidget(WidgetChrome widget, bool save = true)
        {
            _docked.Remove(widget.DockedEdge);
            _dockDim.Remove(widget.DockedEdge);
            widget.DockedEdge = DockEdge.None;
            if (save) SaveDockConfig();
        }

        private void SaveDockConfig()
        {
            var widgets = new Dictionary<string, string>();
            var groups  = new Dictionary<string, List<string>>();
            var dims    = new Dictionary<string, float>();

            foreach (var kvp in _docked)
            {
                string key = kvp.Key.ToString().ToLowerInvariant();
                if (kvp.Value is WidgetGroup wg)
                    groups[key] = wg.Members.Select(m => m.Title).ToList();
                else if (kvp.Value is WidgetSplit)
                {
                    // A docked WidgetSplit's Title is a synthesized "WidgetA · WidgetB" string
                    // that never matches an entry in _widgetByTitle, so writing it into
                    // DockedWidgets here silently failed to restore on the next launch. Its dock
                    // edge is carried on the split's own WidgetSplitEntry.DockedEdge instead (set
                    // in SerializeSplit/SaveSplitConfig) and re-applied by the restore loop.
                }
                else
                    widgets[key] = kvp.Value.Title;

                if (_dockDim.TryGetValue(kvp.Key, out float dim))
                    dims[key] = dim;
            }

            Config.WidgetOpen.DockedWidgets = widgets;
            Config.WidgetOpen.DockedGroups  = groups;
            Config.WidgetOpen.DockedDims    = dims;
            Config.SaveDebounced();

            // A split's own DockedEdge only gets written to its WidgetSplitEntry by
            // SaveSplitConfig(), which otherwise only runs on split-specific mutations -
            // not on a plain dock/undock of an already-existing split. Without this, docking
            // an existing WidgetSplit to an edge updates _docked/DockedEdge in memory but
            // never persists it, so it reverts to floating on restart.
            SaveSplitConfig();
        }

        private void DrawDockedWidgets()
        {
            if (_docked.Count == 0) return;

            var   io    = ImGuiNET.ImGui.GetIO();
            var   disp  = io.DisplaySize;
            float menuH = ImGuiNET.ImGui.GetFrameHeight();

            var slots = new (DockEdge Edge, Vector2 Pos, Vector2 Size)[]
            {
                (DockEdge.Top,    new Vector2(0f, menuH), new Vector2(disp.X, _dockDim.GetValueOrDefault(DockEdge.Top, 0f))),
                (DockEdge.Bottom, new Vector2(0f, disp.Y - _dockDim.GetValueOrDefault(DockEdge.Bottom, 0f)), new Vector2(disp.X, _dockDim.GetValueOrDefault(DockEdge.Bottom, 0f))),
                (DockEdge.Left,   new Vector2(0f, menuH), new Vector2(_dockDim.GetValueOrDefault(DockEdge.Left, 0f), disp.Y - menuH)),
                (DockEdge.Right,  new Vector2(disp.X - _dockDim.GetValueOrDefault(DockEdge.Right, 0f), menuH), new Vector2(_dockDim.GetValueOrDefault(DockEdge.Right, 0f), disp.Y - menuH)),
            };

            // Resize probes BEFORE Begin() so child windows can't steal the hit.
            foreach (var (edge, pos, size) in slots)
                if (_docked.ContainsKey(edge) && size.X > 0f && size.Y > 0f)
                    ProbeDockedResize(edge, pos, size, io);

            foreach (var (edge, pos, size) in slots)
                if (_docked.TryGetValue(edge, out var widget) && size.X > 0f && size.Y > 0f &&
                    !(_visibilityFuncs.TryGetValue(widget, out var vis) && !vis()))   // a docked widget switched off (or hidden by the profile)
                    DrawDockedSlot(edge, widget, io, pos, size);
        }

        // ── Dock slot resize ─────────────────────────────────────────────────
        private bool     _dockResizing;
        private DockEdge _dockResizeEdge;
        private float    _dockResizeStartMouse;
        private float    _dockResizeStartDim;
        private DockEdge _dockResizeHoverEdge = DockEdge.None;

        private void ProbeDockedResize(DockEdge edge, Vector2 pos, Vector2 size, ImGuiIOPtr io)
        {
            const float HalfW = 6f;
            Vector2 hTL, hBR;
            switch (edge)
            {
                case DockEdge.Left:   hTL = new(pos.X + size.X - HalfW, pos.Y); hBR = new(pos.X + size.X + HalfW, pos.Y + size.Y); break;
                case DockEdge.Right:  hTL = new(pos.X - HalfW,          pos.Y); hBR = new(pos.X + HalfW,          pos.Y + size.Y); break;
                case DockEdge.Top:    hTL = new(pos.X, pos.Y + size.Y - HalfW); hBR = new(pos.X + size.X,         pos.Y + size.Y + HalfW); break;
                default:              hTL = new(pos.X, pos.Y - HalfW);           hBR = new(pos.X + size.X,         pos.Y + HalfW);           break;
            }

            var  mp  = io.MousePos;
            bool hov = mp.X >= hTL.X && mp.X <= hBR.X && mp.Y >= hTL.Y && mp.Y <= hBR.Y
                       && WidgetChrome.DragSource == null; // don't start resize during widget drag

            bool isResizingThis = _dockResizing && _dockResizeEdge == edge;

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

        private void DrawDockedSlot(DockEdge edge, WidgetChrome widget, ImGuiIOPtr io, Vector2 pos, Vector2 size)
        {
            // Unlike MenuManager's panel dock slots, this is NOT flagged NoBringToDisplayFront:
            // panel docks reserve screen margins so the radar canvas never overlaps them, but
            // widget docks deliberately float on top of the full-screen radar (no margins) -
            // so the slot needs to be able to win the z-order fight every time the radar window
            // gets focused (panning/clicking), or it sinks behind the radar and disappears.
            var slotFlags =
                ImGuiWindowFlags.NoTitleBar         |
                ImGuiWindowFlags.NoResize           |
                ImGuiWindowFlags.NoMove             |
                ImGuiWindowFlags.NoCollapse         |
                ImGuiWindowFlags.NoScrollbar        |
                ImGuiWindowFlags.NoScrollWithMouse  |
                ImGuiWindowFlags.NoSavedSettings    |
                ImGuiWindowFlags.NoFocusOnAppearing;

            ImGuiNET.ImGui.SetNextWindowPos(pos,  ImGuiCond.Always);
            ImGuiNET.ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    Vector2.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);

            bool visible = ImGuiNET.ImGui.Begin("##wgtdockslot_" + edge, slotFlags);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopStyleVar(3);

            if (visible)
            {
                var wp = ImGuiNET.ImGui.GetWindowPos();
                var ws = ImGuiNET.ImGui.GetWindowSize();
                var dl = ImGuiNET.ImGui.GetWindowDrawList();

                widget.UpdateScreenRect(wp, ws);

                uint chromeBg     = widget.GetChromeBg();
                uint chromeTitleBg= widget.GetChromeTitleBg();
                uint chromeBorder = widget.GetChromeBorder();
                uint chromeTxt    = widget.GetChromeTitleTxt();
                uint chromeScroll = widget.GetChromeScrollThumb();

                dl.AddRectFilled(wp, wp + ws, chromeBg);
                dl.AddRectFilled(wp, new Vector2(wp.X + ws.X, wp.Y + WidgetChrome.TitleH), chromeTitleBg);

                var   font  = ImGuiStyle.FontBold;
                float fs    = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                dl.AddText(font, fs,
                    new Vector2(wp.X + WidgetChrome.Pad, wp.Y + (WidgetChrome.TitleH - fs) * 0.5f),
                    chromeTxt, widget.Title);

                dl.AddRect(wp, wp + ws, chromeBorder, 0f, ImDrawFlags.None, 1f);

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

                const float BtnS = WidgetChrome.CloseBtnS, BtnPad = WidgetChrome.SlotGap;
                float by = (WidgetChrome.TitleH - BtnS) * 0.5f;

                // Close (×) button - hides the widget and undocks it.
                float closeBx = ws.X - 2f * BtnS - 2f * BtnPad;
                ImGuiNET.ImGui.SetCursorPos(new Vector2(closeBx, by));
                ImGuiNET.ImGui.InvisibleButton("##wgtdockclosebtn_" + edge, new Vector2(BtnS, BtnS));
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
                ImGuiNET.ImGui.InvisibleButton("##wgtdockbtn_" + edge, new Vector2(BtnS, BtnS));
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
                    if (widget is WidgetGroup wg)
                    {
                        var members = wg.Members.ToList();
                        UndockWidget(widget);
                        DissolveGroup(wg);
                        _groups.Remove(wg);
                        foreach (var m in members) HideWidget(m);
                        SaveGroupConfig();
                    }
                    else
                    {
                        UndockWidget(widget);
                        HideWidget(widget);
                    }
                }

                if (btnClk)
                    UndockWidget(widget);

                // ── Content child (shared with every other widget content surface -
                //    see WidgetChrome.DrawContentBody / DrawAccentLine) ─────────
                ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, WidgetChrome.TitleH));
                bool  hasPad   = widget.GetHasContentPadding();
                float contentH = ws.Y - WidgetChrome.TitleH - (hasPad ? WidgetChrome.ContentTopGap : 0f);
                if (contentH > 1f)
                {
                    WidgetChrome.DrawContentBody("##wgtdockcontent_" + edge, new Vector2(0f, contentH),
                        hasPad, widget.GetHasScrollWindowPadding(),
                        widget.GetChromeScrollExtraFlags(), chromeScroll, widget.InvokeDrawContent);

                    WidgetChrome.DrawAccentLine("##wgtdockaccent_" + edge, ws, WidgetChrome.TitleH);
                }
            }
            ImGuiNET.ImGui.End();
        }

        // ── Snap / merge zone system ─────────────────────────────────────────
        private enum SnapZone { None, Left, Right, Top, Bottom, Merge }

        private static SnapZone GetSnapZone(WidgetChrome target, Vector2 mouse)
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
            var src = WidgetChrome.DragSource;
            if (src == null) return;

            var   io    = ImGuiNET.ImGui.GetIO();
            var   disp  = io.DisplaySize;
            float menuH = ImGuiNET.ImGui.GetFrameHeight();

            // ── Edge dock zones (take priority over widget merge zones) ────────
            DockEdge hoverEdge = DockEdge.None;
            var mp = io.MousePos;
            if      (mp.X < DockEdgeZone)              hoverEdge = DockEdge.Left;
            else if (mp.X > disp.X - DockEdgeZone)     hoverEdge = DockEdge.Right;
            else if (mp.Y < menuH + DockEdgeZone)       hoverEdge = DockEdge.Top;
            else if (mp.Y > disp.Y - DockEdgeZone)      hoverEdge = DockEdge.Bottom;

            if (Program.Config.WidgetAnchoring && hoverEdge != DockEdge.None && !src.IsGrouped)
            {
                DrawEdgeDockPreview(hoverEdge, src, menuH, disp);
                if (!io.MouseDown[0])
                {
                    DockWidget(src, hoverEdge);
                    WidgetChrome.ClearDragSource();
                }
                return; // skip widget merge zone handling
            }

            // ── Split-pane drop zones (checked first - splits draw last = highest Z) ──
            WidgetSplit splitTarget  = null;
            int         splitPaneIdx = -1;
            if (Program.Config.WidgetMerging && !(src is WidgetSplit))
            {
                var found = FindDeepestSplitPane(io.MousePos);
                if (found.HasValue) { splitTarget = found.Value.Split; splitPaneIdx = found.Value.PaneIdx; }
            }

            var zone           = SnapZone.None;
            WidgetChrome target = null;

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
                foreach (var widget in GetVisibleMergeableWidgets())
                {
                    if (ReferenceEquals(widget, src)) continue;
                    if (widget.ContainsScreenPoint(io.MousePos)) { target = widget; break; }
                }

                if (target != null)
                {
                    // Docked and floating targets share the same zone detection.
                    // For docked targets, directional zones create a split inside the slot;
                    // centre (Merge) groups them as tabs - both results stay anchored.
                    zone = GetSnapZone(target, io.MousePos);
                    if (zone == SnapZone.Merge && !Program.Config.WidgetMerging)
                        zone = SnapZone.None;
                    if ((zone == SnapZone.Left || zone == SnapZone.Right ||
                         zone == SnapZone.Top  || zone == SnapZone.Bottom) &&
                        (!Program.Config.WidgetMerging || (src is WidgetSplit && !target.IsDocked)))
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
                        var dockedWidget = _docked[target.DockedEdge];
                        if (zone == SnapZone.Merge)
                        {
                            MergeIntoDocked(src, dockedWidget, target.DockedEdge);
                            SaveDockConfig();
                        }
                        else
                        {
                            SplitWithinDock(src, dockedWidget, target.DockedEdge, zone);
                        }
                    }
                    else
                    {
                        switch (zone)
                        {
                            case SnapZone.Merge:                         MergeWidgets(src, target); break;
                            case SnapZone.Left: case SnapZone.Right:
                            case SnapZone.Top:  case SnapZone.Bottom:   SplitWidgets(src, target, zone); break;
                        }
                    }
                }
                WidgetChrome.ClearDragSource();
            }
        }

        private static void DrawSnapOverlay(WidgetChrome target, SnapZone activeZone)
            => DrawSnapOverlay(target.ScreenPos, target.ScreenSize, activeZone);

        private static void DrawSnapOverlay(Vector2 pos, Vector2 sz, SnapZone activeZone)
        {
            var   dl   = ImGuiNET.ImGui.GetForegroundDrawList();
            var   br   = pos + sz;
            var   ctr  = pos + sz * 0.5f;
            var   font = ImGuiStyle.FontBold;
            float fs   = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            dl.AddRect(pos, br, UI.Alpha(ImGuiStyle.Accent, 180), 8f, ImDrawFlags.None, 2.5f);

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

            DrawZoneIndicator(dl, font, fs, new Vector2(pos.X + sz.X * 0.13f, ctr.Y),   "Left",   activeZone == SnapZone.Left);
            DrawZoneIndicator(dl, font, fs, new Vector2(pos.X + sz.X * 0.87f, ctr.Y),   "Right",  activeZone == SnapZone.Right);
            DrawZoneIndicator(dl, font, fs, new Vector2(ctr.X, pos.Y + sz.Y * 0.13f),   "Top",    activeZone == SnapZone.Top);
            DrawZoneIndicator(dl, font, fs, new Vector2(ctr.X, pos.Y + sz.Y * 0.87f),   "Bottom", activeZone == SnapZone.Bottom);
            DrawZoneIndicator(dl, font, fs, ctr,                                          "Merge",  activeZone == SnapZone.Merge);
        }

        private static void DrawZoneIndicator(ImDrawListPtr dl, ImFontPtr font, float fs,
            Vector2 center, string label, bool active)
        {
            label = LocManager.Get(label);
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

        private static void DrawEdgeDockPreview(DockEdge edge, WidgetChrome src, float menuH, Vector2 disp)
        {
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
            label = LocManager.Get(label);

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

        private (WidgetSplit Split, int PaneIdx)? FindDeepestSplitPane(Vector2 p)
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

        private (WidgetSplit Split, int PaneIdx)? FindDeepestPaneInSplit(WidgetSplit split, Vector2 p)
        {
            int pi = split.GetPaneIndexAt(p);
            if (pi < 0) return null;
            if (split.GetPane(pi) is WidgetSplit nested)
            {
                var deeper = FindDeepestPaneInSplit(nested, p);
                if (deeper.HasValue) return deeper;
            }
            return (split, pi);
        }

        private void NestedAddToSplit(WidgetSplit outer, int outerPaneIdx, WidgetChrome src, SnapZone zone)
        {
            if (src.IsGrouped) return;
            if (_splits.Any(s => s.Panes.Contains(src))) return;

            var targetPane = outer.GetPane(outerPaneIdx);
            outer.RemovePane(outerPaneIdx); // temporarily un-groups targetPane

            bool nestedH = zone == SnapZone.Left || zone == SnapZone.Right;
            WidgetChrome first, second;
            if (zone == SnapZone.Left || zone == SnapZone.Top) (first, second) = (src, targetPane);
            else                                               (first, second) = (targetPane, src);

            string key    = $"__wgtsplit_{_nextSplitId++}";
            var nested    = new WidgetSplit(key, first, second, nestedH);
            nested.MemberPopped += OnSplitMemberPopped;
            _splits.Add(nested);

            outer.InsertPane(nested, outerPaneIdx);
            SaveSplitConfig();
        }

        private void AddPaneToSplit(WidgetSplit split, int paneIdx, WidgetChrome src, SnapZone zone)
        {
            if (src.IsGrouped) return;
            if (_splits.Exists(s => s.Panes.Contains(src))) return;
            bool insertBefore = zone == SnapZone.Left || zone == SnapZone.Top;
            split.InsertPane(src, insertBefore ? paneIdx : paneIdx + 1);
            SaveSplitConfig();
        }

        private void SplitWidgets(WidgetChrome src, WidgetChrome target, SnapZone zone)
        {
            if (src.IsGrouped || target.IsGrouped) return;
            if (_splits.Exists(s => s.Panes.Contains(src))) return;
            if (_splits.Exists(s => s.Panes.Contains(target))) return;

            bool horizontal = zone == SnapZone.Left || zone == SnapZone.Right;

            WidgetChrome first, second;
            if (zone == SnapZone.Left || zone == SnapZone.Top)
                (first, second) = (src, target);
            else
                (first, second) = (target, src);

            // Pre-seed the layout entry so the new WidgetSplit's first Draw() loads
            // the target's exact position/size via the normal _placed=false path.
            var tPos  = target.ScreenPos;
            var tSize = target.ScreenSize;
            var split = CreateSplit(first, second, horizontal);
            Config.WidgetLayouts[split.Title] = new WidgetLayoutEntry
            {
                X = tPos.X, Y = tPos.Y,
                W = tSize.X, H = tSize.Y
            };
            SaveSplitConfig();
        }

        private void MergeWidgets(WidgetChrome src, WidgetChrome target)
        {
            WidgetGroup tgtGroup = target as WidgetGroup
                ?? _groups.Find(g => g.Members.Contains(target));

            WidgetGroup dest = tgtGroup;
            if (dest == null)
            {
                dest = CreateGroup(new[] { target });
                dest.SetPosition(target.ScreenPos);
            }

            var toAdd = new List<WidgetChrome>();
            FlattenIncoming(src, toAdd);
            foreach (var m in toAdd)
                dest.AddMember(m);

            SaveGroupConfig();
        }

        // ── Split helpers ─────────────────────────────────────────────────────

        private void SplitWithinDock(WidgetChrome src, WidgetChrome existing, DockEdge edge, SnapZone zone)
        {
            bool horizontal = zone == SnapZone.Left || zone == SnapZone.Right;
            WidgetChrome first, second;
            if (zone == SnapZone.Left || zone == SnapZone.Top) (first, second) = (src, existing);
            else                                               (first, second) = (existing, src);

            existing.DockedEdge = DockEdge.None;
            _docked.Remove(edge);

            var split = CreateSplit(first, second, horizontal);

            split.DockedEdge = edge;
            _docked[edge]    = split;

            SaveDockConfig();
            SaveSplitConfig();
        }

        private WidgetSplit CreateSplit(WidgetChrome first, WidgetChrome second, bool horizontal)
        {
            string key   = $"__wgtsplit_{_nextSplitId++}";
            var    split = new WidgetSplit(key, first, second, horizontal);
            split.MemberPopped += OnSplitMemberPopped;
            _splits.Add(split);
            return split;
        }

        private void OnSplitMemberPopped(WidgetSplit split, WidgetChrome widget)
        {
            widget.RequestFocus();
            _bringToFront    = "##widgetchrome_" + widget.Title;
            _bringToFrontTtl = 2;
            SaveSplitConfig();
        }

        private void SaveSplitConfig()
        {
            Config.WidgetOpen.WidgetSplits = _splits
                .Where(s => !s.IsGrouped && s.PaneCount >= 2)
                .Select(SerializeSplit)
                .Where(e => e != null)
                .ToList();
            Config.SaveDebounced();
        }

        private WidgetSplitEntry SerializeSplit(WidgetSplit split)
        {
            if (split.PaneCount < 2) return null;
            var slots = split.Panes.Select(p =>
            {
                if (p is WidgetSplit nested) return new WidgetSplitPaneSlot { Split = SerializeSplit(nested) };
                return string.IsNullOrEmpty(p?.Title) ? null : new WidgetSplitPaneSlot { Widget = p.Title };
            }).Where(s => s != null).ToList();
            if (slots.Count < 2) return null;
            return new WidgetSplitEntry
            {
                Slots      = slots,
                Ratios     = split.Ratios.ToList(),
                Horizontal = split.Horizontal,
                DockedEdge = split.IsDocked ? split.DockedEdge.ToString().ToLowerInvariant() : null
            };
        }

        private WidgetSplit RestoreSplitFromConfig(WidgetSplitEntry entry)
        {
            if (entry == null || entry.Slots == null || entry.Slots.Count < 2) return null;

            List<WidgetChrome> panes = new();
            foreach (var slot in entry.Slots)
            {
                WidgetChrome p = null;
                if (slot.Split != null)
                {
                    p = RestoreSplitFromConfig(slot.Split);
                }
                else if (!string.IsNullOrEmpty(slot.Widget) &&
                         _widgetByTitle.TryGetValue(slot.Widget, out var found) &&
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
