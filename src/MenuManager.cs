using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.UI.ImGui.Panels;
using SC2Radar.UI.ImGui.Widgets;
using SC2Radar.UI.Misc;
using SC2Radar.UI;

namespace SC2Radar.UI.ImGui.Menus
{
    /// <summary>
    /// Top bar, panels and their layout. Panels can be dragged onto each other (tabs in the centre, a split at an edge)
    /// or to a screen edge (docked; the radar shrinks around it), as in the EFT radar - that generic part is in
    /// MenuManager.Layout.cs; this file has the SC2 panels, their show flags and saving / restoring them.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed partial class MenuManager : IDisposable
    {
        private readonly DX11Window _host;

        // ── Panels ────────────────────────────────────────────────────────────
        private GeneralPanel   _generalPanel    = null!;
        private DebugPanel     _debugPanel      = null!;
        private MemWritesPanel _memWritesPanel  = null!;
        private UnitFilterPanel _unitFilterPanel = null!;
        private SearchPanel    _searchPanel     = null!;
        private POIPanel       _poiPanel        = null!;

        // ── Panel groups / splits / docks (MenuManager.Layout.cs) ────────────
        private readonly List<GroupPanel> _groups = new();
        private readonly List<SplitPanel> _splits = new();
        private int _nextGroupId = 1, _nextSplitId = 1;
        private Dictionary<string, ChromePanel>     _panelByTitle = null!;          // title -> panel (saved layouts)
        private Dictionary<ChromePanel, Action>     _panelHideActions = null!;      // closing a group hides its members
        private Dictionary<ChromePanel, Func<bool>> _panelVisibilityFuncs = null!;  // group tabs follow the show flags
        private readonly Dictionary<DockEdge, ChromePanel> _docked  = new();
        private readonly Dictionary<DockEdge, float>       _dockDim = new();   // slot width (left/right) or height

        // ── Visibility flags ─────────────────────────────────────────────────
        private bool _showGeneral    = true;
        private bool _showDebug      = false;
        private bool _showMemWrites  = false;
        private bool _showUnitFilter = false;
        private bool _showSearch     = false;
        private bool _showPoi        = false;

        // ── Toolbar scroll offset ─────────────────────────────────────────────
        private float _menuBarScrollX;

        // ── Pending bring-to-front after panel detach ─────────────────────────
        private string? _pendingFocus;
        private string? _bringToFront;
        private int     _bringToFrontTtl;

        public WidgetManager? Widgets { get; private set; }

        public MenuManager(DX11Window host)
        {
            _host            = host;
            _generalPanel    = new GeneralPanel();
            _debugPanel      = new DebugPanel();
            _memWritesPanel  = new MemWritesPanel();
            _unitFilterPanel = new UnitFilterPanel();
            _searchPanel     = new SearchPanel();
            _poiPanel        = new POIPanel();
            Widgets          = new WidgetManager();

            _panelByTitle = new Dictionary<string, ChromePanel>(StringComparer.OrdinalIgnoreCase)
            {
                ["Settings"] = _generalPanel, ["Debug"] = _debugPanel, ["Unit Filter"] = _unitFilterPanel,
                ["Memory Writes"] = _memWritesPanel, ["Search Features"] = _searchPanel,
                ["Points of Interest"] = _poiPanel,
            };
            _panelHideActions = new Dictionary<ChromePanel, Action>
            {
                [_generalPanel] = () => _showGeneral = false, [_debugPanel] = () => _showDebug = false,
                [_unitFilterPanel] = () => _showUnitFilter = false, [_memWritesPanel] = () => _showMemWrites = false,
                [_searchPanel] = () => _showSearch = false,
                [_poiPanel] = () => _showPoi = false,
            };
            _panelVisibilityFuncs = new Dictionary<ChromePanel, Func<bool>>
            {
                [_generalPanel] = () => _showGeneral, [_debugPanel] = () => _showDebug,
                [_unitFilterPanel] = () => _showUnitFilter, [_memWritesPanel] = () => _showMemWrites,
                [_searchPanel] = () => _showSearch,
                [_poiPanel] = () => _showPoi,
            };

            ApplySavedLayout();
            RegisterSearchPanels();
        }

        /// <summary>
        /// Open flags, groups, splits and docks from the saved config. Called at start and after a settings restore;
        /// whatever layout exists is torn down first.
        /// </summary>
        private void ApplySavedLayout()
        {
            foreach (var g in _groups) DissolveGroup(g);
            _groups.Clear();
            foreach (var s in _splits)
                for (int i = s.PaneCount - 1; i >= 0; i--) { var pane = s.GetPane(i); pane.IsGrouped = false; pane.IsHosted = false; }
            _splits.Clear();
            foreach (var kvp in _docked.ToList()) kvp.Value.DockedEdge = DockEdge.None;
            _docked.Clear();
            _dockDim.Clear();

            var ps = Program.Config.PanelOpen;
            (_showGeneral, _showDebug, _showUnitFilter, _showMemWrites, _showSearch, _showPoi) =
                (ps.General, ps.Debug, ps.UnitFilter, ps.MemWrites, ps.Search, ps.POI);

            ChromePanel? Panel(string title) => _panelByTitle.TryGetValue(title, out var p) ? p : null;
            DockEdge Edge(string key) => Enum.TryParse<DockEdge>(key, ignoreCase: true, out var e) ? e : DockEdge.None;

            // panels claimed by a docked group aren't also restored as a floating group
            var inDockedGroups = new HashSet<string>(ps.DockedGroups.Values.Where(v => v != null).SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
            foreach (var titles in ps.PanelGroups)
            {
                var members = titles.Where(t => !inDockedGroups.Contains(t)).Select(Panel).OfType<ChromePanel>().ToList();
                if (members.Count >= 2) CreateGroup(members);
            }
            foreach (var entry in ps.PanelSplits)
                if (RestoreSplitFromConfig(entry) is { } split && entry.DockedEdge is { } se && Edge(se) is var e && e != DockEdge.None)
                    DockPanel(split, e, save: false);
            foreach (var (key, title) in ps.DockedPanels)
                if (Edge(key) is var e && e != DockEdge.None && Panel(title) is { } p) DockPanel(p, e, save: false);
            foreach (var (key, titles) in ps.DockedGroups)
            {
                var e = Edge(key);
                if (e == DockEdge.None || titles == null) continue;
                var members = titles.Select(Panel).OfType<ChromePanel>().ToList();
                if (members.Count == 1) DockPanel(members[0], e, save: false);
                else if (members.Count > 1) DockPanel(CreateGroup(members), e, save: false);
            }
            foreach (var (key, dim) in ps.DockedDims)
                if (Edge(key) is var e && _dockDim.ContainsKey(e)) _dockDim[e] = Math.Max(DockMinDim, dim);
        }

        /// <summary>Settings search (Ctrl+F): which panel each result opens, and how it jumps to the right tab.</summary>
        private void RegisterSearchPanels()
        {
            SearchRegistry.RegisterPanel("Settings",      () => _showGeneral = true,    path => _generalPanel.NavigateTo(path));
            SearchRegistry.RegisterPanel("Unit Filter",   () => _showUnitFilter = true, path => _unitFilterPanel.NavigateTo(path));
            SearchRegistry.RegisterPanel("Memory Writes", () => _showMemWrites = true,  path => _memWritesPanel.NavigateTo(path));
            SearchRegistry.RegisterPanel("Debug",         () => _showDebug = true,      path => _debugPanel.NavigateTo(path));
            SearchRegistry.RegisterPanel("Points of Interest", () => _showPoi = true, path => _poiPanel.NavigateTo(path));
        }

        /// <summary>Panels that can be dropped onto (groups first: they draw last, on top).</summary>
        private IEnumerable<ChromePanel> GetVisibleMergeablePanels()
        {
            foreach (var g in _groups) if (!g.IsGrouped) yield return g;
            if (_showGeneral    && !_generalPanel.IsGrouped)    yield return _generalPanel;
            if (_showDebug      && !_debugPanel.IsGrouped)      yield return _debugPanel;
            if (_showUnitFilter && !_unitFilterPanel.IsGrouped) yield return _unitFilterPanel;
            if (_showMemWrites  && !_memWritesPanel.IsGrouped)  yield return _memWritesPanel;
            if (_showSearch     && !_searchPanel.IsGrouped)     yield return _searchPanel;
            if (_showPoi        && !_poiPanel.IsGrouped)        yield return _poiPanel;
        }

        private void SavePanelOpenStatesIfChanged()
        {
            var ps = Program.Config.PanelOpen;
            if ((ps.General, ps.Debug, ps.UnitFilter, ps.MemWrites, ps.Search, ps.POI) ==
                (_showGeneral, _showDebug, _showUnitFilter, _showMemWrites, _showSearch, _showPoi)) return;
            (ps.General, ps.Debug, ps.UnitFilter, ps.MemWrites, ps.Search, ps.POI) =
                (_showGeneral, _showDebug, _showUnitFilter, _showMemWrites, _showSearch, _showPoi);
            Program.Config.SaveDebounced();
        }

        private void FocusIfPending(string label)
        {
            if (_pendingFocus != label) return;
            ImGuiNET.ImGui.SetNextWindowFocus();
            _pendingFocus = null;
        }

        // ── Called each frame: toolbar + panels ──────────────────────────────

        public void DrawMenuBar()
        {
            if (!ImGuiNET.ImGui.BeginMenuBar()) return;

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,     new Vector2(2f, 0f));

            const float WinBtnW = 40f;
            float totalRight = WinBtnW * 3f;
            float barH       = ImGuiNET.ImGui.GetFrameHeight();
            float contentMax = ImGuiNET.ImGui.GetContentRegionMax().X;
            float cursorX    = ImGuiNET.ImGui.GetCursorPosX();
            float scrollW    = Math.Max(0f, contentMax - cursorX - totalRight);

            var     winDl   = ImGuiNET.ImGui.GetWindowDrawList();
            Vector2 stripTL = ImGuiNET.ImGui.GetCursorScreenPos();

            winDl.PushClipRect(stripTL, new Vector2(stripTL.X + scrollW, stripTL.Y + barH), true);
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(stripTL.X - _menuBarScrollX, stripTL.Y));

            // ── Status badge ─────────────────────────────────────────────────
            DrawStatusBadge();
            BarSeparator();

            // ── Panel toggles ─────────────────────────────────────────────────
            ToolbarToggle("Settings",   ref _showGeneral,   "Settings");
            ToolbarToggle("Debug",      ref _showDebug,     "Debug");
            ToolbarToggle("Units",      ref _showUnitFilter, "Unit Filter");
            ToolbarToggle("Mem Writes", ref _showMemWrites, "Memory Writes");
            ToolbarToggle("POI",        ref _showPoi,       "Points of Interest");
            UI.Tooltip("Points of interest: named spots per map, drawn on the radar.");
            ToolbarToggle("Search",     ref _showSearch,    "Search");
            UI.Tooltip("Search every setting (Ctrl+F).");

            BarSeparator();

            // ── Quick toggles ─────────────────────────────────────────────────
            if (UI.IconToggle("##zoomin", IconCache.Icon.Plus, false))
                ImGuiApp.Radar?.AdjustZoom(10);
            UI.Tooltip("Zoom In");

            ImGuiNET.ImGui.SameLine(0f, 2f);
            if (UI.IconToggle("##zoomout", IconCache.Icon.Minus, false))
                ImGuiApp.Radar?.AdjustZoom(-10);
            UI.Tooltip("Zoom Out");

            DrawMatchInfo();
            DrawCursorPos();

            // Measure true content width for scroll bookkeeping
            float usedW      = ImGuiNET.ImGui.GetCursorScreenPos().X - stripTL.X + _menuBarScrollX;
            float scrollMaxX = MathF.Max(0f, usedW - scrollW);
            _menuBarScrollX  = Math.Clamp(_menuBarScrollX, 0f, scrollMaxX);

            winDl.PopClipRect();

            // ── Drag region (double-click to toggle maximize, drag to move) ──
            if (scrollMaxX <= 0.5f)
            {
                float leftover = scrollW - usedW;
                if (leftover > 1f)
                {
                    ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(stripTL.X + usedW, stripTL.Y));
                    ImGuiNET.ImGui.InvisibleButton("##titledrag", new Vector2(leftover, barH));
                    if (ImGuiNET.ImGui.IsItemHovered() &&
                        ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        ImGuiApp.ToggleMaximizeWindow();
                    }
                    else if (ImGuiNET.ImGui.IsItemActive() &&
                             ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
                    {
                        Win32.ReleaseCapture();
                        Win32.PostMessage(_host.Hwnd, Win32.WM_NCLBUTTONDOWN,
                            new IntPtr(Win32.HTCAPTION), IntPtr.Zero);
                    }
                }
            }

            // ── Toolbar scrollbar ─────────────────────────────────────────────
            Vector2 stripBR  = new(stripTL.X + scrollW, stripTL.Y + barH);
            var     mp       = ImGuiNET.ImGui.GetIO().MousePos;
            bool    stripHov = mp.X >= stripTL.X && mp.X <= stripBR.X && mp.Y >= stripTL.Y && mp.Y <= stripBR.Y;
            if (scrollMaxX > 0.5f)
            {
                float ratio      = scrollW / usedW;
                float thumbW     = MathF.Max(scrollW * ratio, 24f);
                float trackRange = scrollW - thumbW;
                float thumbX     = stripTL.X + (trackRange > 0f ? (_menuBarScrollX / scrollMaxX) * trackRange : 0f);

                const float SbH = 3f, SbRound = 1.5f;
                float sbY = stripTL.Y + barH - SbH;

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(thumbX, sbY - 2f));
                ImGuiNET.ImGui.InvisibleButton("##menubar_sb", new Vector2(thumbW, SbH + 4f));
                bool sbHov = ImGuiNET.ImGui.IsItemHovered();
                bool sbAct = ImGuiNET.ImGui.IsItemActive();
                if (sbAct && trackRange > 0f)
                {
                    float delta = ImGuiNET.ImGui.GetIO().MouseDelta.X * (scrollMaxX / trackRange);
                    _menuBarScrollX = Math.Clamp(_menuBarScrollX + delta, 0f, scrollMaxX);
                    thumbX = stripTL.X + (_menuBarScrollX / scrollMaxX) * trackRange;
                }

                winDl.AddRectFilled(new Vector2(stripTL.X, sbY), new Vector2(stripTL.X + scrollW, sbY + SbH),
                    UI.Alpha(ImGuiStyle.Dark3, 120), SbRound);
                uint thumbCol = sbAct ? UI.Alpha(ImGuiStyle.Accent, 255)
                              : sbHov ? UI.Alpha(ImGuiStyle.Accent, 180)
                              :         UI.Alpha(ImGuiStyle.Border, 150);
                winDl.AddRectFilled(new Vector2(thumbX, sbY), new Vector2(thumbX + thumbW, sbY + SbH),
                    thumbCol, SbRound);

                if (sbHov || stripHov)
                {
                    float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
                    if (wheel != 0f)
                    {
                        _menuBarScrollX = Math.Clamp(_menuBarScrollX - wheel * 60f, 0f, scrollMaxX);
                        ImGuiNET.ImGui.GetIO().MouseWheel = 0f;
                    }
                }
            }

            // Advance outer cursor past scroll strip
            ImGuiNET.ImGui.SetCursorScreenPos(stripTL);
            ImGuiNET.ImGui.Dummy(new Vector2(scrollW, barH));

            // ── Window controls ───────────────────────────────────────────────
            ImGuiNET.ImGui.SameLine(0f, 0f);

            if (DrawChromeCtrlBtn(winDl, "##wmin", WinBtnW, barH,
                    ImGuiStyle.Col(0.45f, 0.45f, 0.45f), out Vector2 minCtr, out float tMin))
                ImGuiApp.MinimizeWindow();
            winDl.AddLine(
                new Vector2(minCtr.X - 5f, minCtr.Y),
                new Vector2(minCtr.X + 5f, minCtr.Y),
                UI.Lerp(UI.Alpha(0xFFFFFFFF, 140), UI.Alpha(0xFFFFFFFF, 245), tMin), 1.8f);
            ImGuiNET.ImGui.SameLine(0f, 0f);

            bool maximized = ImGuiApp.IsWindowMaximized;
            if (DrawChromeCtrlBtn(winDl, "##wmax", WinBtnW, barH,
                    ImGuiStyle.WithAlpha(ImGuiStyle.Accent, 220), out Vector2 maxCtr, out float tMax))
                ImGuiApp.ToggleMaximizeWindow();
            {
                uint col = UI.Lerp(UI.Alpha(0xFFFFFFFF, 140), UI.Alpha(0xFFFFFFFF, 245), tMax);
                if (maximized)
                {
                    winDl.AddRect(new Vector2(maxCtr.X - 3f, maxCtr.Y - 5f),
                                  new Vector2(maxCtr.X + 5f, maxCtr.Y + 3f), col, 0f, ImDrawFlags.None, 1.4f);
                    winDl.AddRectFilled(new Vector2(maxCtr.X - 5f, maxCtr.Y - 3f),
                                        new Vector2(maxCtr.X + 3f, maxCtr.Y + 5f),
                                        UI.Alpha(ImGuiStyle.Col(0x10, 0x10, 0x14), 255));
                    winDl.AddRect(new Vector2(maxCtr.X - 5f, maxCtr.Y - 3f),
                                  new Vector2(maxCtr.X + 3f, maxCtr.Y + 5f), col, 0f, ImDrawFlags.None, 1.4f);
                }
                else
                {
                    winDl.AddRect(new Vector2(maxCtr.X - 5f, maxCtr.Y - 5f),
                                  new Vector2(maxCtr.X + 5f, maxCtr.Y + 5f), col, 0f, ImDrawFlags.None, 1.5f);
                }
            }
            ImGuiNET.ImGui.SameLine(0f, 0f);

            if (DrawChromeCtrlBtn(winDl, "##wclose", WinBtnW, barH,
                    ImGuiStyle.Danger, out Vector2 clsCtr, out float tClose))
                ImGuiApp.CloseMainWindow();
            {
                if (tClose > 0.01f)
                {
                    float padX = ImGuiNET.ImGui.GetStyle().WindowPadding.X;
                    Vector2 rMin = ImGuiNET.ImGui.GetItemRectMin();
                    Vector2 rMax = ImGuiNET.ImGui.GetItemRectMax();
                    winDl.AddRectFilled(new Vector2(rMax.X, rMin.Y),
                                        new Vector2(rMax.X + padX, rMax.Y),
                                        UI.Alpha(ImGuiStyle.Danger, (byte)(tClose * 220)));
                }
                float cx  = clsCtr.X, cy = clsCtr.Y;
                const float s = 4.5f, hw = 0.9f;
                float off = hw * 0.7071f;
                uint  col = UI.Lerp(UI.Alpha(0xFFFFFFFF, 140), UI.Alpha(0xFFFFFFFF, 245), tClose);
                winDl.AddQuadFilled(
                    new Vector2(cx - s + off, cy - s - off), new Vector2(cx + s + off, cy + s - off),
                    new Vector2(cx + s - off, cy + s + off), new Vector2(cx - s - off, cy - s + off), col);
                winDl.AddQuadFilled(
                    new Vector2(cx + s + off, cy - s + off), new Vector2(cx - s + off, cy + s + off),
                    new Vector2(cx - s - off, cy + s - off), new Vector2(cx + s - off, cy - s - off), col);
                winDl.AddCircleFilled(new Vector2(cx - s, cy - s), hw, col);
                winDl.AddCircleFilled(new Vector2(cx + s, cy + s), hw, col);
                winDl.AddCircleFilled(new Vector2(cx + s, cy - s), hw, col);
                winDl.AddCircleFilled(new Vector2(cx - s, cy + s), hw, col);
            }

            ImGuiNET.ImGui.PopStyleVar(2);
            ImGuiNET.ImGui.EndMenuBar();
        }

        public void DrawAllPanels()
        {
            // Bring-to-front deferred by one frame after panel opens/detaches. Focus the panel that was
            // opened (a bare SetNextWindowFocus here focused whichever panel began next, usually Settings,
            // leaving the opened one behind the full-screen radar window).
            if (_bringToFront != null)
            {
                if (--_bringToFrontTtl <= 0)
                {
                    ChromePanel? target = _bringToFront switch
                    {
                        "Settings"      => _generalPanel,
                        "Debug"         => _debugPanel,
                        "Unit Filter"   => _unitFilterPanel,
                        "Memory Writes" => _memWritesPanel,
                        "Points of Interest" => _poiPanel,
                        _               => null,
                    };
                    target?.RequestFocus();
                    _bringToFront = null;
                }
            }

            SearchRegistry.Tick();
            if (!_preRegDone)
            {
                // panels register their searchable settings once, before anyone searches
                _preRegDone = true;
                foreach (var p in _panelByTitle.Values) p.PreRegisterKeys();
            }

            // Ctrl+F: the settings search
            var io = ImGuiNET.ImGui.GetIO();
            if (io.KeyCtrl && ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.F)) { _showSearch = true; _pendingFocus = "Search"; }

            // the radar asked for the POI panel (a POI added or picked there)
            if (POIPanel.OpenRequested) { POIPanel.OpenRequested = false; _showPoi = true; _pendingFocus = "Points of Interest"; }

            var ps = Program.Config.PanelOpen;

            // docked panels first (edge slots), then floating: unpinned, then pinned on top
            DrawDockedPanels();
            foreach (bool pinnedPass in new[] { false, true })
            {
                if (_showGeneral    && ps.IsPinned("Settings") == pinnedPass)      { FocusIfPending("Settings");    _generalPanel.Draw(ref _showGeneral); }
                if (_showDebug      && ps.IsPinned("Debug") == pinnedPass)         { FocusIfPending("Debug");       _debugPanel.Draw(ref _showDebug); }
                if (_showUnitFilter && ps.IsPinned("Unit Filter") == pinnedPass)   { FocusIfPending("Unit Filter"); _unitFilterPanel.Draw(ref _showUnitFilter); }
                if (_showMemWrites  && ps.IsPinned("Memory Writes") == pinnedPass) { FocusIfPending("Memory Writes"); _memWritesPanel.Draw(ref _showMemWrites); }
                if (_showSearch     && ps.IsPinned("Search") == pinnedPass)        { FocusIfPending("Search");      _searchPanel.Draw(ref _showSearch); }
                if (_showPoi        && ps.IsPinned("Points of Interest") == pinnedPass) { FocusIfPending("Points of Interest"); _poiPanel.Draw(ref _showPoi); }
            }

            // group tabs follow the show flags
            foreach (var (panel, vis) in _panelVisibilityFuncs) panel.IsVisible = vis();

            // groups (always drawn; their members skip their own draw)
            for (int i = _groups.Count - 1; i >= 0; i--)
            {
                var g = _groups[i];
                g.Draw(ref g.ShowState);
                if (g.ShowState) continue;
                if (!g.DissolvedByPop) foreach (var m in g.Members.ToList()) HidePanel(m);   // closed with X: hide its members too
                DissolveGroup(g);
                _groups.RemoveAt(i);
                SaveGroupConfig();
            }

            // splits (nested ones are drawn by their parent)
            for (int i = _splits.Count - 1; i >= 0; i--)
            {
                var s = _splits[i];
                if (!s.IsGrouped) s.Draw(ref s.ShowState);
                if (s.ShowState) continue;
                SplitPanel? parent = null; int parentPi = -1;
                foreach (var outer in _splits)
                {
                    for (int pi = 0; pi < outer.PaneCount; pi++)
                        if (ReferenceEquals(outer.GetPane(pi), s)) { parent = outer; parentPi = pi; break; }
                    if (parent != null) break;
                }
                if (parent != null)
                {
                    parent.RemovePane(parentPi);
                    if (s.PaneCount > 0) { var survivor = s.GetPane(0); survivor.IsGrouped = false; survivor.IsHosted = false; parent.InsertPane(survivor, parentPi); }
                }
                else if (s.IsDocked)
                {
                    // a docked split that broke up: the pane left over stays docked at that edge
                    var edge = s.DockedEdge;
                    s.DockedEdge = DockEdge.None;
                    _docked.Remove(edge);
                    if (s.PaneCount > 0) { var survivor = s.GetPane(0); survivor.IsGrouped = false; survivor.IsHosted = false; DockPanel(survivor, edge, save: false); }
                    SaveDockConfig();
                }
                else
                    for (int pi = s.PaneCount - 1; pi >= 0; pi--)
                    {
                        var pane = s.GetPane(pi);
                        var rect = s.GetPaneRect(pi);
                        if (rect.Size.X > 0f) pane.SetPosition(rect.Pos);
                        if (pane.IsGrouped) { pane.IsGrouped = false; pane.IsHosted = false; }
                    }
                _splits.RemoveAt(i);
                SaveSplitConfig();
            }

            // the drop-zone overlay while a panel is dragged, and the merge / split / dock on release
            DrawGroupDropZones();

            // after all windows: bring a newly detached panel to the top (twice: it only exists on the second frame)
            if (_bringToFrontWindow != null)
            {
                ImGuiNET.ImGui.SetWindowFocus(_bringToFrontWindow);
                if (--_bringToFrontWindowTtl <= 0) _bringToFrontWindow = null;
            }

            SavePanelOpenStatesIfChanged();
        }

        private bool _preRegDone;
        private string? _bringToFrontWindow;   // "##chrome_<title>" of a panel just dragged out of a group or split
        private int     _bringToFrontWindowTtl;

        private HotkeyManager? _hotkeys;

        /// <summary>Per-frame hotkey poll (Settings ▸ Hotkeys); paused while typing in one of the radar's text fields.</summary>
        public void PollHotkeys()
        {
            if (ImGuiNET.ImGui.GetIO().WantTextInput) return;
            _hotkeys ??= new HotkeyManager(new Dictionary<string, Action>
            {
                [nameof(SC2.SC2Hotkeys.ToggleSettings)]       = () => _showGeneral    = !_showGeneral,
                [nameof(SC2.SC2Hotkeys.ToggleUnitFilter)]     = () => _showUnitFilter = !_showUnitFilter,
                [nameof(SC2.SC2Hotkeys.ToggleMemWritesPanel)] = () => _showMemWrites  = !_showMemWrites,
                [nameof(SC2.SC2Hotkeys.ToggleSearch)]         = () => _showSearch     = !_showSearch,
                [nameof(SC2.SC2Hotkeys.TogglePOIPanel)]       = () => _showPoi        = !_showPoi,
            });
            _hotkeys.PollHotkeys();
        }

        /// <summary>Re-applies the saved layout (after a settings restore) without recreating the panels.</summary>
        public void ReapplyLayout() => ApplySavedLayout();

        public void Dispose()
        {
            _hotkeys?.Dispose();
            _generalPanel?.Dispose();
            _debugPanel?.Dispose();
            _memWritesPanel?.Dispose();
            _unitFilterPanel?.Dispose();
            _poiPanel?.Dispose();
            Widgets?.Dispose();
        }

        // ── Toolbar helpers ───────────────────────────────────────────────────

        private static void DrawStatusBadge()
        {
            if (Memory.SafeMode)
            {
                // no FPGA: the badge is the retry button
                bool busy = Memory.Connecting;
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, busy ? ImGuiStyle.Col(0.9f, 0.8f, 0.2f) : ImGuiStyle.Warning);
                ImGuiNET.ImGui.Text(busy ? "Starting..." : "Safe Mode");
                ImGuiNET.ImGui.PopStyleColor();
                if (!busy && ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (!busy && ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Left)) Memory.RetryConnect();
                UI.Tooltip(Memory.ConnectErrorSummary is { } err
                    ? $"Safe Mode - DMA functionality disabled.\n\n{err}\n\nClick to retry."
                    : "Safe Mode - DMA functionality disabled.\n\nClick to connect.");
                return;
            }

            bool connected = Memory.IsConnected;
            bool inGame    = Memory.InGame;

            uint color;
            string text;
            if (inGame)
            {
                color = ImGuiStyle.Col(0.4f, 0.75f, 0.4f);
                text  = "In Game";
            }
            else if (connected)
            {
                color = ImGuiStyle.Col(0.9f, 0.8f, 0.2f);
                text  = "Connected";
            }
            else
            {
                color = ImGuiStyle.TextDim;
                text  = "Connecting...";
            }

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGuiNET.ImGui.Text(text);
            ImGuiNET.ImGui.PopStyleColor();
        }

        /// <summary>
        /// "12:34 · Map name · Profile" after the toolbar (Display ▸ Match Info In Top Bar): the match at a glance without
        /// the Match widget. The clock turns orange while the game is paused; hovering shows the match format.
        /// </summary>
        private static void DrawMatchInfo()
        {
            var mem = Memory.Instance;
            if (!Program.Config.MatchInTopBar || mem == null || !Memory.InGame || mem.GameTime < 0f) return;
            BarSeparator();
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, mem.GamePaused ? ImGuiStyle.Warning : ImGuiStyle.Text);
            ImGuiNET.ImGui.Text(SC2.SC2Memory.FormatClock(mem.GameTime) + (mem.GamePaused ? " (paused)" : ""));
            ImGuiNET.ImGui.PopStyleColor();
            if (mem.MapName.Length > 0)
            {
                ImGuiNET.ImGui.SameLine(0f, 8f);
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, ImGuiStyle.TextDim);
                ImGuiNET.ImGui.Text(mem.MapName);
                ImGuiNET.ImGui.PopStyleColor();
            }
            ImGuiNET.ImGui.SameLine(0f, 8f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, ImGuiStyle.Accent);
            ImGuiNET.ImGui.Text(SC2.RadarProfiles.Active.Name);
            ImGuiNET.ImGui.PopStyleColor();
            // Native per-item tooltip here - UI.Tooltip(string) uses _lastRowTL/BR from the
            // most recent interactive item (the Zoom Out IconToggle just above), so it would
            // anchor to that button and bleed into its tooltip when the user hovers it.
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("The radar profile in use for this map (Settings > Visuals > Profiles).");
        }

        /// <summary>"x 145.3  y 72.7": the map point under the mouse (Display ▸ Cursor Position In Top Bar); dim once the mouse
        /// leaves the radar, so it can still be read off after moving up here.</summary>
        private static void DrawCursorPos()
        {
            if (!Program.Config.CursorInTopBar || !Memory.InGame || ImGuiApp.Radar?.CursorWorld is not { } c) return;
            BarSeparator();
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, c.Stale ? ImGuiStyle.TextDim : ImGuiStyle.Text);
            ImGuiNET.ImGui.Text($"x {c.Pos.X:0.0}  y {c.Pos.Y:0.0}");
            ImGuiNET.ImGui.PopStyleColor();
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("Map coordinates under the mouse.");
        }

        private static void BarSeparator()
        {
            ImGuiNET.ImGui.SameLine(0f, 6f);
            ImGuiNET.ImGui.Separator();
            ImGuiNET.ImGui.SameLine(0f, 6f);
        }

        private void ToolbarToggle(string label, ref bool open, string focusKey)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button,
                open ? ImGuiStyle.WithAlpha(ImGuiStyle.Accent, 35) : 0u);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
                ImGuiStyle.WithAlpha(ImGuiStyle.Dark5, 180));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonActive,
                ImGuiStyle.WithAlpha(ImGuiStyle.Accent, 70));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text,
                open ? ImGuiStyle.Accent : ImGuiStyle.Text);

            if (ImGuiNET.ImGui.Button(label))
            {
                bool wasOpen = open;
                open = !open;
                if (!wasOpen)
                {
                    _bringToFront    = focusKey;
                    _bringToFrontTtl = 2;
                }
            }

            if (open)
            {
                var dl  = ImGuiNET.ImGui.GetWindowDrawList();
                var min = ImGuiNET.ImGui.GetItemRectMin();
                var max = ImGuiNET.ImGui.GetItemRectMax();
                dl.AddRectFilled(
                    new Vector2(min.X + 3f, max.Y - 2f),
                    new Vector2(max.X - 3f, max.Y),
                    ImGuiStyle.Accent, 1f);
            }

            ImGuiNET.ImGui.PopStyleColor(4);
            ImGuiNET.ImGui.SameLine(0f, 2f);
        }

        private static bool DrawChromeCtrlBtn(ImDrawListPtr dl, string id,
            float width, float height, uint hoverBg, out Vector2 center, out float animT)
        {
            ImGuiNET.ImGui.InvisibleButton(id, new Vector2(width, height));
            bool hov   = ImGuiNET.ImGui.IsItemHovered();
            bool clk   = ImGuiNET.ImGui.IsItemClicked();
            uint btnId = ImGuiNET.ImGui.GetItemID();
            animT = UI.Anim(btnId, hov ? 1f : 0f, 14f);

            Vector2 min = ImGuiNET.ImGui.GetItemRectMin();
            Vector2 max = ImGuiNET.ImGui.GetItemRectMax();
            center = new Vector2((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f);

            if (animT > 0.01f)
                dl.AddRectFilled(min, max, UI.Alpha(hoverBg, (byte)(animT * 220)));

            return clk;
        }
    }
}
