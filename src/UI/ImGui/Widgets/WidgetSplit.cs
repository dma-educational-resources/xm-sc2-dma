using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using SC2Radar.UI.Misc;

namespace SC2Radar.UI.ImGui.Widgets
{
    /// <summary>
    /// A WidgetChrome that hosts N other WidgetChromes in a side-by-side (horizontal)
    /// or stacked (vertical) split view with draggable dividers. Structural mirror of
    /// <see cref="SC2Radar.UI.ImGui.Panels.SplitPanel"/>, retargeted at WidgetChrome.
    /// Start with 2 panes; additional panes can be inserted at runtime via drag-to-pane-edge.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    internal sealed class WidgetSplit : WidgetChrome
    {
        #region Chrome Overrides

        protected override bool HasScrollWindowPadding => false;

        protected override string WidgetTitle
        {
            get
            {
                string j = string.Join(" · ", _panes.Select(p => p?.Title ?? "?"));
                return j.Length > 60 ? j[..57] + "···" : j;
            }
        }

        protected override Vector2 InitSize
        {
            get
            {
                if (_panes.Count == 0) return new Vector2(400f, 400f);
                if (Horizontal)
                {
                    // Sum pane widths side-by-side; height = tallest pane.
                    float w = _panes.Sum(p => p?.ScreenSize.X ?? 300f)
                              + DividerW * (_panes.Count - 1);
                    float h = _panes.Max(p => p?.ScreenSize.Y ?? 400f);
                    return new Vector2(w, h + TitleH + MiniTitleH);
                }
                else
                {
                    // Use the tallest pane's current screen height, NOT the sum.
                    float w = _panes.Max(p => p?.ScreenSize.X ?? 300f);
                    float h = _panes.Max(p => p?.ScreenSize.Y ?? 300f);
                    return new Vector2(w, h);
                }
            }
        }

        #endregion

        #region State

        internal bool Horizontal { get; }

        private readonly List<WidgetChrome> _panes  = new();
        private readonly List<float>        _ratios = new(); // per-pane fractions, sum = 1.0

        internal int PaneCount => _panes.Count;
        internal IReadOnlyList<WidgetChrome> Panes  => _panes;
        internal IReadOnlyList<float>        Ratios => _ratios;
        internal WidgetChrome GetPane(int i) => _panes[i];

        // Divider drag state
        private int   _activeDivider  = -1;
        private float _divDragOrigin;
        private float _divRatioAStart;
        private float _divRatioBStart;

        // Pane screen rects (updated every Draw; used for zone detection)
        private readonly List<(Vector2 Pos, Vector2 Size)> _paneRects = new();

        /// <summary>Returns the pane index (0-based) whose screen rect contains p, or -1.</summary>
        internal int GetPaneIndexAt(Vector2 p)
        {
            for (int i = 0; i < _paneRects.Count; i++)
            {
                var (pos, size) = _paneRects[i];
                if (size.X > 0 && size.Y > 0 &&
                    p.X >= pos.X && p.X < pos.X + size.X &&
                    p.Y >= pos.Y && p.Y < pos.Y + size.Y)
                    return i;
            }
            return -1;
        }

        internal (Vector2 Pos, Vector2 Size) GetPaneRect(int i)
            => i >= 0 && i < _paneRects.Count ? _paneRects[i] : default;

        // Pop-out
        private int _pendingPopIndex = -1;
        internal event Action<WidgetSplit, WidgetChrome> MemberPopped;

        // Layout constants
        private const float MiniTitleH = 24f;
        private const float DividerW   = 5f;
        private const float PopBtnS    = 16f;
        private const float MinPaneW   = 80f;

        // Identity
        private readonly string _splitKey;
        internal string SplitKey => _splitKey;
        internal bool ShowState = true;

        internal WidgetSplit(string key, WidgetChrome first, WidgetChrome second, bool horizontal)
        {
            _splitKey  = key;
            Horizontal = horizontal;
            AppendPane(first);
            AppendPane(second);
        }

        /// <summary>Appends a pane and resets all ratios to equal shares.</summary>
        private void AppendPane(WidgetChrome widget)
        {
            _panes.Add(widget);
            _paneRects.Add(default);
            widget.IsGrouped = true;
            widget.IsHosted  = true;
            ResetRatiosEqual();
        }

        /// <summary>Inserts a new pane at atIndex (or appends if -1/overflow). Redistributes ratios.</summary>
        internal void InsertPane(WidgetChrome widget, int atIndex = -1)
        {
            int idx = (atIndex < 0 || atIndex >= _panes.Count) ? _panes.Count : atIndex;
            _panes.Insert(idx, widget);
            _paneRects.Insert(idx, default);
            widget.IsGrouped = true;
            widget.IsHosted  = true;

            // Give the new pane 1/n of the space, shrinking all others proportionally.
            int n = _panes.Count;
            _ratios.Insert(idx, 0f);
            while (_ratios.Count > n) _ratios.RemoveAt(_ratios.Count - 1);
            float newShare = 1f / n;
            float scale    = 1f - newShare;
            float oldSum   = 0f;
            for (int i = 0; i < n; i++) if (i != idx) oldSum += _ratios[i];
            for (int i = 0; i < n; i++)
                _ratios[i] = i == idx
                    ? newShare
                    : (oldSum > 0f ? _ratios[i] / oldSum * scale : scale / (n - 1));
            NormalizeRatios();
        }

        /// <summary>Removes the pane at index and redistributes its ratio to remaining panes.</summary>
        internal void RemovePane(int index)
        {
            if (index < 0 || index >= _panes.Count) return;
            var widget = _panes[index];
            widget.IsGrouped = false;
            widget.IsHosted  = false;
            _panes.RemoveAt(index);
            _paneRects.RemoveAt(index);
            if (index < _ratios.Count) _ratios.RemoveAt(index);
            NormalizeRatios();
        }

        private void ResetRatiosEqual()
        {
            int n = _panes.Count;
            _ratios.Clear();
            for (int i = 0; i < n; i++) _ratios.Add(1f / n);
        }

        private void NormalizeRatios()
        {
            int n = _panes.Count;
            while (_ratios.Count < n) _ratios.Add(0f);
            while (_ratios.Count > n) _ratios.RemoveAt(_ratios.Count - 1);
            float sum = _ratios.Sum();
            if (sum <= 0f) { ResetRatiosEqual(); return; }
            for (int i = 0; i < n; i++) _ratios[i] /= sum;
        }

        /// <summary>Overrides stored ratios (e.g. from config restore).</summary>
        internal void SetRatios(IList<float> ratios)
        {
            _ratios.Clear();
            _ratios.AddRange(ratios);
            NormalizeRatios();
        }

        internal void RequestPop(int paneIndex)
        {
            if (paneIndex >= 0 && paneIndex < _panes.Count)
                _pendingPopIndex = paneIndex;
        }

        #endregion

        #region UI - Entry Point

        protected override void DrawWidgetContent()
        {
            if (_pendingPopIndex >= 0 && OnPopOutPane_Committed())
                return;

            if (_panes.Count == 0) return;

            // DrawScrollableContent positions us at ContentTopGap. Reset to flush (same as WidgetGroup).
            ImGuiNET.ImGui.SetCursorPosY(0f);

            var avail = ImGuiNET.ImGui.GetContentRegionAvail();
            if (avail.X < 1f || avail.Y < 1f) return;

            // Keep _paneRects in sync.
            while (_paneRects.Count < _panes.Count) _paneRects.Add(default);
            while (_paneRects.Count > _panes.Count) _paneRects.RemoveAt(_paneRects.Count - 1);

            if (Horizontal) DrawHSplit(avail);
            else            DrawVSplit(avail);
        }

        #endregion

        #region Events - Pop-out

        /// <summary>
        /// Commits a pending pop-out request raised by the pane's pop-out button: places the
        /// popped widget at its last-known visual position, removes it from the split, and
        /// fires <see cref="MemberPopped"/>. Returns true if the split fell below 2 panes, in
        /// which case the caller must stop drawing this frame.
        /// </summary>
        private bool OnPopOutPane_Committed()
        {
            int idx = _pendingPopIndex;
            _pendingPopIndex = -1;
            if (idx >= _panes.Count) return false;

            var popped = _panes[idx];
            var r = GetPaneRect(idx);
            if (r.Size.X > 0f)
                popped.SetPosition(r.Pos);
            RemovePane(idx);
            MemberPopped?.Invoke(this, popped);
            if (_panes.Count < 2)
            {
                ShowState = false;
                return true;
            }
            return false;
        }

        #endregion

        #region UI - Dividers

        private float[] ComputeSizes(float total)
        {
            int n = _panes.Count;
            var sizes = new float[n];
            if (n == 0 || total <= 0f) return sizes;

            for (int i = 0; i < n; i++)
                sizes[i] = MathF.Max(MinPaneW, total * _ratios[i]);

            float sum = sizes.Sum();
            if (sum > total + 0.5f)
            {
                float shrinkable = sizes.Sum(s => s - MinPaneW);
                float excess     = sum - total;
                if (shrinkable > 0f)
                {
                    float f = MathF.Min(1f, excess / shrinkable);
                    for (int i = 0; i < n; i++)
                        sizes[i] = MinPaneW + (sizes[i] - MinPaneW) * (1f - f);
                }
            }
            return sizes;
        }

        private void DrawHSplit(Vector2 avail)
        {
            float totalW = avail.X - DividerW * (_panes.Count - 1);
            float[] ws   = ComputeSizes(totalW);
            for (int i = 0; i < _panes.Count; i++)
            {
                DrawPane(i, new Vector2(ws[i], avail.Y));
                if (i < _panes.Count - 1)
                {
                    ImGuiNET.ImGui.SameLine(0f, 0f);
                    DrawDivider(avail.Y, isH: true, totalDim: totalW, divIdx: i);
                    ImGuiNET.ImGui.SameLine(0f, 0f);
                }
            }
        }

        private void DrawVSplit(Vector2 avail)
        {
            float totalH = avail.Y - DividerW * (_panes.Count - 1);
            float[] hs   = ComputeSizes(totalH);
            for (int i = 0; i < _panes.Count; i++)
            {
                DrawPane(i, new Vector2(avail.X, hs[i]));
                if (i < _panes.Count - 1)
                    DrawDivider(avail.X, isH: false, totalDim: totalH, divIdx: i);
            }
        }

        private void DrawDivider(float crossDim, bool isH, float totalDim, int divIdx)
        {
            var cursor = ImGuiNET.ImGui.GetCursorScreenPos();
            var dl     = ImGuiNET.ImGui.GetWindowDrawList();
            var io     = ImGuiNET.ImGui.GetIO();

            var btnSz = isH ? new Vector2(DividerW, crossDim) : new Vector2(crossDim, DividerW);
            ImGuiNET.ImGui.InvisibleButton($"##wsdiv_{_splitKey}_{divIdx}", btnSz);
            bool hov = ImGuiNET.ImGui.IsItemHovered();

            if (hov || _activeDivider == divIdx)
                ImGuiNET.ImGui.SetMouseCursor(isH ? ImGuiMouseCursor.ResizeEW : ImGuiMouseCursor.ResizeNS);

            if (ImGuiNET.ImGui.IsItemActivated() && _activeDivider < 0)
            {
                _activeDivider  = divIdx;
                _divDragOrigin  = isH ? io.MousePos.X : io.MousePos.Y;
                _divRatioAStart = _ratios[divIdx];
                _divRatioBStart = _ratios[divIdx + 1];
            }

            if (_activeDivider == divIdx)
            {
                if (io.MouseDown[0])
                {
                    float cur   = isH ? io.MousePos.X : io.MousePos.Y;
                    float frac  = (cur - _divDragOrigin) / MathF.Max(1f, totalDim);
                    float sumAB = _divRatioAStart + _divRatioBStart;
                    float minR  = MinPaneW / MathF.Max(1f, totalDim);
                    float newA  = Math.Clamp(_divRatioAStart + frac, minR, sumAB - minR);
                    _ratios[divIdx]     = newA;
                    _ratios[divIdx + 1] = sumAB - newA;
                }
                else _activeDivider = -1;
            }

            bool active = _activeDivider == divIdx;
            uint col    = UI.Alpha(ImGuiStyle.Border, active || hov ? (byte)200 : (byte)70);
            float thick = active || hov ? 2f : 1.5f;

            if (isH)
            {
                float cx = cursor.X + DividerW * 0.5f;
                dl.AddLine(new Vector2(cx, cursor.Y + 6f),
                           new Vector2(cx, cursor.Y + crossDim - 6f), col, thick);
            }
            else
            {
                float cy = cursor.Y + DividerW * 0.5f;
                dl.AddLine(new Vector2(cursor.X + 6f, cy),
                           new Vector2(cursor.X + crossDim - 6f, cy), col, thick);
            }
        }

        #endregion

        #region UI - Panes

        private void DrawPane(int paneIdx, Vector2 size)
        {
            var widget = _panes[paneIdx];
            bool inOuter = ImGuiNET.ImGui.BeginChild(
                $"##wsp_{paneIdx}_{_splitKey}", size, ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

            if (inOuter)
            {
                var wp = ImGuiNET.ImGui.GetWindowPos();
                var ws = ImGuiNET.ImGui.GetWindowSize();

                // Record screen rect for zone detection.
                _paneRects[paneIdx] = (wp, ws);

                // Nested WidgetSplit: fill the entire pane area directly - it manages its own chrome.
                if (widget is WidgetSplit)
                {
                    ImGuiNET.ImGui.SetCursorPos(Vector2.Zero);
                    DrawPaneContent(widget, paneIdx, ws.Y);
                }
                else
                {
                var dl = ImGuiNET.ImGui.GetWindowDrawList();

                // Mini title bar background.
                dl.AddRectFilled(wp, new Vector2(wp.X + ws.X, wp.Y + MiniTitleH),
                    widget.GetChromeTitleBg());

                // Top accent line.
                dl.AddLine(wp, new Vector2(wp.X + ws.X, wp.Y),
                    UI.Alpha(ImGuiStyle.Accent, 140), 1.5f);

                // Widget title text.
                var   font = ImGuiStyle.FontBold;
                float fs   = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                dl.AddText(font, fs,
                    new Vector2(wp.X + 8f, wp.Y + (MiniTitleH - fs) * 0.5f),
                    widget.GetChromeTitleTxt(), widget.Title);

                // Separator below mini title.
                dl.AddLine(new Vector2(wp.X,        wp.Y + MiniTitleH - 0.5f),
                           new Vector2(wp.X + ws.X, wp.Y + MiniTitleH - 0.5f),
                           UI.Alpha(ImGuiStyle.Border, 80), 1f);

                // Pop-out button.
                float bx = ws.X - PopBtnS - 4f;
                float by = (MiniTitleH - PopBtnS) * 0.5f;
                ImGuiNET.ImGui.SetCursorPos(new Vector2(bx, by));
                ImGuiNET.ImGui.InvisibleButton($"##wsppop_{paneIdx}_{_splitKey}",
                    new Vector2(PopBtnS, PopBtnS));
                bool phov = ImGuiNET.ImGui.IsItemHovered();
                if (phov) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGuiNET.ImGui.IsItemClicked()) RequestPop(paneIdx);

                // Pop-out icon.
                Vector2 pTL = new(wp.X + bx, wp.Y + by);
                uint popCol = UI.Alpha(ImGuiStyle.Accent, phov ? (byte)210 : (byte)100);
                if (phov)
                    dl.AddRectFilled(pTL, pTL + new Vector2(PopBtnS, PopBtnS),
                        UI.Alpha(ImGuiStyle.Accent, 28), 3f);
                float pp = 3.5f, sq = PopBtnS - pp * 2f;
                Vector2 sqTL = new(pTL.X + pp, pTL.Y + pp + sq * 0.42f);
                dl.AddRect(sqTL, new Vector2(sqTL.X + sq * 0.55f, sqTL.Y + sq * 0.58f),
                    popCol, 1f, ImDrawFlags.None, 1f);
                float ax = pTL.X + pp + sq * 0.45f, ay = pTL.Y + pp, aw = sq * 0.55f;
                dl.AddLine(new Vector2(ax, ay),      new Vector2(ax + aw, ay), popCol, 1.5f);
                dl.AddLine(new Vector2(ax + aw, ay), new Vector2(ax + aw, ay + aw * 0.75f), popCol, 1.5f);
                dl.AddLine(new Vector2(ax + aw * 0.2f, ay + aw * 0.45f),
                           new Vector2(ax + aw, ay), popCol, 1.5f);

                // Widget content.
                float contentH = ws.Y - MiniTitleH;
                if (contentH >= 1f)
                {
                    ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, MiniTitleH));
                    DrawPaneContent(widget, paneIdx, contentH);
                }
                } // end else (regular widget)
            }

            ImGuiNET.ImGui.EndChild();
        }

        private void DrawPaneContent(WidgetChrome widget, int paneIdx, float contentH)
        {
            DrawContentBody($"##wspc_{paneIdx}_{_splitKey}", new Vector2(0f, contentH),
                widget.GetHasContentPadding(), widget.GetHasScrollWindowPadding(),
                widget.GetChromeScrollExtraFlags(), widget.GetChromeScrollThumb(),
                widget.InvokeDrawContent);
        }

        #endregion
    }
}
