using System.Numerics;
using SkiaSharp;
using SC2Radar.SC2;
using SC2Radar.Common.Misc.Data;
using SC2Radar.UI;
using SC2Radar.UI.ImGui.Panels;
using SC2Radar.UI.Misc;
using static SC2Radar.UI.ImGui.Panels.SettingsLayout;

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class GeneralPanel : ChromePanel
    {
        protected override Vector2 InitSize => new(460f, 560f);
        protected override string PanelTitle => "Settings";

        private int _tab;
        private static readonly string[] _tabs = { "Display", "Visuals", "Map", "Performance", "Colors", "Widgets", "Hotkeys", "Web Radar" };

        public GeneralPanel() { }

        /// <summary>Settings search (Ctrl+F): draw every tab (and Visuals' sub-tabs) once off-screen so all their
        /// settings are indexed, not just the open tab's.</summary>
        public override void PreRegisterKeys()
        {
            int savedTab = _tab, savedSub = VisualsSettings.SubTab;
            for (int i = 0; i < _tabs.Length; i++)
            {
                _tab = i;
                if (_tabs[i] == "Visuals")
                    for (int j = 0; j < VisualsSettings.SubTabCount; j++) { VisualsSettings.SubTab = j; PreRegisterCurrentState(); }
                else
                    PreRegisterCurrentState();
            }
            (_tab, VisualsSettings.SubTab) = (savedTab, savedSub);
        }

        /// <summary>A search result: open its tab.</summary>
        public override void NavigateTo(IReadOnlyList<string> tabPath)
        {
            if (tabPath == null || tabPath.Count == 0) return;
            int idx = Array.IndexOf(_tabs, tabPath[0]);
            if (idx >= 0) _tab = idx;
        }

        protected override void DrawPanelContent()
        {
            DrawTabHeader(_tabs, ref _tab);
            switch (_tab)
            {
                case 0: DrawDisplayTab();       break;
                case 1: VisualsSettings.Draw(); break;
                case 2: DrawMapTab();           break;
                case 3: DrawPerformanceTab();   break;
                case 4: DrawColorsTab();        break;
                case 5: DrawWidgetsTab();       break;
                case 6: DrawHotkeysTab();       break;
                case 7: DrawWebRadarTab();      break;
            }
        }

        // ── Tab 0: Display ────────────────────────────────────────────────────

        private static void DrawDisplayTab()
        {
            UI.BeginGroup("Unit Display");
            Grid(
                Toggle("Show Units", () => Config.ShowUnits, v => Config.ShowUnits = v, "Draw all unit dots on the radar canvas."),
                Toggle("Show Buildings", () => Config.ShowBuildings, v => Config.ShowBuildings = v),
                Toggle("Show Workers", () => Config.ShowWorkers, v => Config.ShowWorkers = v),
                Toggle("Detail By Zoom", () => Config.ZoomDetail, v => Config.ZoomDetail = v,
                    "Zoomed far out, units are small plain dots; zooming in brings back full size, outlines, shadows and health bars. Game-mode markers always stay full size."),
                Toggle("Building Footprints", () => Config.BuildingFootprints, v => Config.BuildingFootprints = v,
                    "Zoomed in, melee buildings are drawn at their real size (5×5 Command Center, 3×3 Barracks, 2×2 Pylon) under their marker."),
                Toggle("Larva, Inject & Chrono", () => Config.ShowLarvaInject, v => Config.ShowLarvaInject = v,
                    "On each Hatchery / Lair / Hive its larva count, and a green ring while a Queen's inject runs; a cyan ring on buildings under Chrono Boost. Inject and chrono are read from the buildings' behaviours once a second."),
                Toggle("Transport Cargo", () => Config.TrackCargo, v => Config.TrackCargo = v,
                    "Read what's inside Medivacs, Warp Prisms, Overlords, Bunkers, Nydus and other transports: a count on the transport, the list in its hover card and in drop warnings. Units inside aren't drawn where they boarded."),
                Toggle("Dim Outside My Vision", () => Config.DimOutsideVision, v => Config.DimOutsideVision = v,
                    "Fade enemy units the game itself isn't showing you right now (outside your and your allies' vision), so you can tell what you'd see on your own screen. Needs the game's visibility map (Debug ▸ offsets: vision sig)."),
                Toggle("Production & Research Rings", () => Config.ShowProgress, v => Config.ShowProgress = v,
                    "A ring filling up on anything training, researching or morphing."),
                Toggle("Ring Labels: Name & Time Left", () => Config.ProgressDetails, v => Config.ProgressDetails = v,
                    "Over each ring, once zoomed in: what it makes and the time left (\"+1 Weapons 0:42\"). Off: the percentage."));

            // stored as the base dot radius (3.5 = 100%); each type's own size is set in Visuals ▸ Entity Info
            UI.BeginSliderGroup("Marker Scale", "Order Lines", "Crowd Labels");
            float markerScale = Config.UnitDotRadius / 3.5f * 100f;
            if (UI.SliderFloat("Marker Scale", ref markerScale, 30f, 230f, "%.0f", suffix: "%", width: UI.GroupInnerWidth,
                    hint: "Scales every marker on the radar. Each type's own shape and size: Visuals ▸ Entity Info."))
            { Config.UnitDotRadius = markerScale / 100f * 3.5f; Config.SaveDebounced(); }
            int lines = Config.OrderLines;
            if (UI.SegmentedRow("Order Lines##orderlines", ["Off", "Enemies", "Everyone"], ref lines,
                    hint: "A line from each unit to what it was ordered to do - red attack, green move, amber anything else - through its queued orders. Gathering workers are left out. Hover a unit for its orders in words."))
            { Config.OrderLines = lines; Config.TrackOrders = true; Config.SaveDebounced(); }
            int crowds = !Config.ClusterUnits ? 0 : Config.ClusterBases ? 2 : 1;
            if (UI.RadioRow("Crowd Labels##crowds", ["Off", "Armies", "Armies & Bases"], ref crowds,
                    hint: "Zoomed out past where unit names show, each crowd of a player's units gets one short label - the biggest type and how many others " +
                          "(\"40 Marine +5\"); hover it for the full list. Armies leaves out buildings and workers. Needs Labels on."))
            { Config.ClusterUnits = crowds > 0; Config.ClusterBases = crowds == 2; Config.SaveDebounced(); }
            UI.EndSliderGroup();
            UI.EndGroup();

            UI.BeginGroup("Alerts");
            Grid(
                Toggle("Enemy Tech Alerts", () => Config.ScoutAlerts, v => Config.ScoutAlerts = v,
                    "A notification when an enemy fields a unit or structure type for the first time this match (first Spire, first Banshee, ...)."),
                Toggle("Cloaked / Burrowed Alerts", () => Config.ScoutAlertsHidden, v => Config.ScoutAlertsHidden = v,
                    "A notification when a cloaked or burrowed enemy unit appears."),
                Toggle("Supply Block Alert", () => Config.SupplyBlockAlert, v => Config.SupplyBlockAlert = v,
                    "A notification when your own supply hits its cap (below 200)."),
                Toggle("Drop / Attack Warning", () => Config.DropWarning, v => Config.DropWarning = v,
                    "A notification and a red pulse on the radar when an enemy transport (Medivac, Warp Prism, transport Overlord) or Nydus worm, or 4+ enemy army units, reach one of your bases."),
                Toggle("Warning Sound", () => Config.DropWarningSound, v => Config.DropWarningSound = v,
                    "With the drop / attack warning, also play the system's warning sound."));
            UI.EndGroup();

            DrawBackups();
        }

        // ── settings backups (SettingsBackup): pick a copy, put it back, restart ──
        private static int _backupFile, _backupCopy;
        private static string _backupStatus = "";

        private static void DrawBackups()
        {
            UI.BeginGroup("Settings Backups");
            UI.Caption("A copy of each settings file is kept (hourly, the last 15) before it's overwritten.");
            (string Label, string Path)[] files =
            [
                ("Settings", SC2.Config.DefaultFile), ("Radar profiles", RadarProfiles.FilePath),
                ("Unit filter", System.IO.Path.Combine(Program.DataDir, "unit_filter.json")),
            ];
            UI.Combo("File##bk_file", ref _backupFile, [.. files.Select(f => f.Label)], width: UI.GroupInnerWidth, localizeItems: false);
            var copies = SettingsBackup.List(files[_backupFile].Path);
            if (copies.Count == 0) UI.Caption("No copies yet.");
            else
            {
                _backupCopy = Math.Clamp(_backupCopy, 0, copies.Count - 1);
                UI.Combo("Copy##bk_copy", ref _backupCopy, [.. copies.Select(c => System.IO.File.GetLastWriteTime(c).ToString("ddd d MMM HH:mm"))],
                    width: UI.GroupInnerWidth, localizeItems: false);
                if (UI.Button("Restore this copy##bk_restore", new Vector2(UI.GroupInnerWidth, 0f), ButtonStyle.Danger,
                        hint: "Puts the copy back over the current file (the current one is kept too). Restart the radar to load it."))
                    _backupStatus = SettingsBackup.Restore(files[_backupFile].Path, copies[_backupCopy])
                        ? "Restored - restart the radar to load it." : "Restore failed.";
            }
            if (_backupStatus.Length > 0) UI.Caption(_backupStatus);
            UI.EndGroup();

            DrawShareSettings();
        }

        // ── share every settings file as one zip (SettingsBackup.ExportAll / ImportAll) ──
        private static int _shareIdx;
        private static string _shareStatus = "";

        private static void DrawShareSettings()
        {
            UI.BeginGroup("Share All Settings");
            UI.Caption("Every settings file - settings, radar profiles, unit filter, points of interest, memory write settings - in one zip, to move to another PC or share.");
            if (UI.Button("Export all settings##share_exp", new Vector2(UI.GroupInnerWidth, 0f)))
                _shareStatus = SettingsBackup.ExportAll() is { } zip ? $"Saved {System.IO.Path.GetFileName(zip)}" : "Export failed.";
            var zips = SettingsBackup.ListExports();
            if (zips.Count > 0)
            {
                _shareIdx = Math.Clamp(_shareIdx, 0, zips.Count - 1);
                UI.Combo("Zip##share_pick", ref _shareIdx, [.. zips.Select(System.IO.Path.GetFileNameWithoutExtension)], width: UI.GroupInnerWidth, localizeItems: false);
                if (UI.Button("Import this zip##share_imp", new Vector2(UI.GroupInnerWidth, 0f), ButtonStyle.Danger,
                        hint: "Puts its files back over the current ones (those are backed up first). Restart the radar to load them."))
                {
                    var (n, err) = SettingsBackup.ImportAll(zips[_shareIdx]);
                    _shareStatus = err ?? $"Restored {n} file{(n == 1 ? "" : "s")} - restart the radar to load them.";
                }
            }
            if (UI.Button("Open exports folder##share_dir", new Vector2(UI.GroupInnerWidth, 0f), ButtonStyle.Ghost,
                    hint: "Put a settings zip someone sent you here to import it."))
                try { System.IO.Directory.CreateDirectory(SettingsBackup.ShareFolder); System.Diagnostics.Process.Start("explorer.exe", SettingsBackup.ShareFolder); } catch { }
            if (_shareStatus.Length > 0) UI.Caption(_shareStatus);
            UI.EndGroup();
        }

        // ── Tab 1: Map ────────────────────────────────────────────────────────

        private static void DrawMapTab()
        {
            UI.BeginGroup("Map View");
            UI.BeginSliderGroup("Zoom %", "Terrain Opacity");
            int zoom = Config.Zoom;
            if (UI.SliderInt("Zoom %", ref zoom, 5, 400, width: UI.GroupInnerWidth))
            { Config.Zoom = zoom; Config.SaveDebounced(); }
            float terrainAlpha = Config.TerrainAlpha;
            if (UI.SliderFloat("Terrain Opacity", ref terrainAlpha, 0.1f, 1f, width: UI.GroupInnerWidth))
            { Config.TerrainAlpha = terrainAlpha; Config.SaveDebounced(); }
            UI.EndSliderGroup();
            Grid(
                Toggle("Show Terrain", () => Config.ShowTerrain, v => Config.ShowTerrain = v,
                    "Draw the map under the units from the game's height map: high ground lighter, cliffs outlined."),
                Toggle("Map Frame", () => Config.MapFrame, v => Config.MapFrame = v,
                    "Outline the playable area like the minimap does, fade what's outside it, and mark map coordinates along the edges."),
                Toggle("Match Info In Top Bar", () => Config.MatchInTopBar, v => Config.MatchInTopBar = v,
                    "Game clock, map name and the radar profile in use, in the top bar next to the zoom buttons."),
                Toggle("Cursor Position In Top Bar", () => Config.CursorInTopBar, v => Config.CursorInTopBar = v,
                    "Map x, y under the mouse in the top bar; it stays (dimmed) when the mouse leaves the radar."),
                Toggle("Game Clock On Radar", () => Config.ShowClockOverlay, v => Config.ShowClockOverlay = v,
                    "In-game time and map name in the radar's top-left corner."));
            UI.EndGroup();

            UI.BeginGroup("Map Layers");
            Grid(
                Toggle("Show Creep", () => Config.ShowCreep, v => Config.ShowCreep = v, "The creep spread, in the minimap's purple."),
                Toggle("Show Pylon Power", () => Config.ShowPowerFields, v => Config.ShowPowerFields = v,
                    "The area each Pylon (and phasing Warp Prism) powers - where buildings work and units can warp in - in its owner's colour."));
            UI.BeginColumns(2);
            float creepAlpha = Config.CreepAlpha;
            if (UI.SliderFloat("Creep Opacity", ref creepAlpha, 0.1f, 1f, width: UI.ColWidth))
            { Config.CreepAlpha = creepAlpha; Config.SaveDebounced(); }
            UI.NextColumn();
            float powerAlpha = Config.PowerFieldAlpha;
            if (UI.SliderFloat("Pylon Power Opacity", ref powerAlpha, 0.05f, 0.6f, width: UI.ColWidth))
            { Config.PowerFieldAlpha = powerAlpha; Config.SaveDebounced(); }
            UI.EndColumns();
            UI.EndGroup();

            UI.BeginGroup("Player Cameras");
            Grid(
                Toggle("Show Player Cameras", () => Config.ShowCameras, v => Config.ShowCameras = v,
                    "Outline what each player's screen shows, like the box on the in-game minimap."),
                Toggle("Include My Camera", () => Config.ShowOwnCamera, v => Config.ShowOwnCamera = v),
                Toggle("Enemies Only", () => Config.CamerasEnemiesOnly, v => Config.CamerasEnemiesOnly = v,
                    "Hide your allies' camera boxes (your own follows Include My Camera)."),
                Toggle("Include Computer Players", () => Config.ShowAiCameras, v => Config.ShowAiCameras = v,
                    "AI players rarely move their camera, so theirs usually sits on their base."));
            float console = Config.CameraConsoleHeight;
            if (UI.SliderFloat("Console Height", ref console, 0f, 40f, "%.0f", suffix: "%", width: UI.GroupInnerWidth,
                    hint: "How much of the bottom of the game screen the console covers. The box starts above it, as the minimap's does - adjust until they match."))
            { Config.CameraConsoleHeight = console; Config.SaveDebounced(); }
            UI.EndGroup();
        }

        // ── Tab 2: Performance ────────────────────────────────────────────────

        private static void DrawPerformanceTab()
        {
            UI.BeginGroup("Rendering");
            // Target FPS and UI Scale side by side on one row
            UI.BeginColumns(2);
            int fps = Config.TargetFPS;
            if (UI.SliderInt("Target FPS", ref fps, 10, 240, width: UI.ColWidth, hint: "Cap the radar render rate. Lower values reduce CPU/GPU load."))
            { Config.TargetFPS = fps; ImGuiApp.TargetFPS = fps; Config.SaveDebounced(); }
            UI.NextColumn();
            float scale = Config.UIScale;
            if (UI.SliderFloat("UI Scale", ref scale, 0.5f, 2.5f, width: UI.ColWidth,
                    hint: "Global scale multiplier applied to all ImGui elements. Changes apply immediately (fonts rebuild)."))
            { Config.UIScale = scale; ImGuiApp.RequestFontReload(); Config.SaveDebounced(); }
            UI.EndColumns();
            Grid(
                Toggle("Performance Overlay", () => Config.ShowPerfOverlay, v => Config.ShowPerfOverlay = v,
                    "In the radar's top-right corner, instead of FPS only: frame time, the radar's draw time, the memory read and write times, DMA reads per tick, snapshots per second and the unit count, with a graph."));
            UI.EndGroup();

            UI.BeginGroup("Performance Mode");
            UI.Caption("For big arcade maps: lighter drawing and slower reads while it is on. The FPS corner says when it is.");
            UI.BeginSliderGroup("Mode", "Auto From (units)");
            int mode = Config.PerfMode;
            if (UI.SegmentedRow("Mode##perf_mode", ["Off", "Auto", "Always"], ref mode))
            { Config.PerfMode = mode; Config.SaveDebounced(); }
            if (Config.PerfMode == 1)
            {
                int n = Config.PerfAutoUnits;
                if (UI.SliderInt("Auto From (units)", ref n, 300, 6000, width: UI.GroupInnerWidth,
                        hint: $"Turns on while at least this many units are on the map (now {PerfStats.Units:N0})."))
                { Config.PerfAutoUnits = n; Config.SaveDebounced(); }
            }
            UI.EndSliderGroup();
            Grid(
                Toggle("Half The Read Rate##perf_r", () => Config.PerfSlowerReads, v => Config.PerfSlowerReads = v, "Read positions and details half as often (both intervals × 2)."),
                Toggle("Skip Heavy Layers##perf_l", () => Config.PerfSkipLayers, v => Config.PerfSkipLayers = v, "No vision overlay, creep or pylon power."),
                Toggle("Skip Labels##perf_t", () => Config.PerfSkipLabels, v => Config.PerfSkipLabels = v, "No unit names, vitals figures or resource amounts (crowd labels stay)."),
                Toggle("Simple Markers##perf_m", () => Config.PerfSimpleMarkers, v => Config.PerfSimpleMarkers = v, "Plain dots: no outlines, shadows, footprints or health bars."));
            UI.EndGroup();

            UI.BeginGroup("Memory Scan");
            UI.BeginSliderGroup("Positions (ms)", "Details (ms)", "Position Reads");
            int rtMs = Config.RealtimeIntervalMs;
            if (UI.SliderInt("Positions (ms)", ref rtMs, 5, 100, width: UI.GroupInnerWidth,
                    hint: "How often unit positions are read (their own thread). Lower = smoother movement, more DMA traffic. Default: 16 ms (about 60 per second)."))
            { Config.RealtimeIntervalMs = rtMs; Config.SaveDebounced(); }
            int readMs = Config.UnitReadIntervalMs;
            if (UI.SliderInt("Details (ms)", ref readMs, 10, 500, width: UI.GroupInnerWidth,
                    hint: "How often the details thread runs: health, players, match, cameras, upgrades, creep (each also has its own pace). Default: 50 ms."))
            { Config.UnitReadIntervalMs = readMs; Config.SaveDebounced(); }
            int layout = Config.PositionReads;
            if (UI.SegmentedRow("Position Reads##pos_reads", ["Pages", "Ranges", "Fields"], ref layout,
                    hint: "How the position read is batched: whole memory pages, one exact range per unit, or only the fields used (vtable, position, model, flags). " +
                          "Which is fastest depends on the DMA device - try each in a match to compare."))
            { Config.PositionReads = layout; Config.SaveDebounced(); }
            UI.EndSliderGroup();
            UI.EndGroup();

            UI.BeginGroup("Debug & Logging");
            Grid(
                Toggle("Show Debug Console", () => XMLogging.ConsoleVisible, v => OnShowDebugConsole_Changed(v),
                    "Show or hide the debug console window. With it visible, the loading splash is skipped on the next launch and startup logs appear there instead."),
                Toggle("Log to File", () => XMLogging.FileLoggingEnabled, v => OnLogToFile_Changed(v),
                    "Write debug output to a log file in the logs folder - send the file when reporting issues."));
            if (UI.Button("Open Logs Folder", new Vector2(UI.GroupInnerWidth, 0f), ButtonStyle.Ghost,
                    hint: "Open the logs folder in File Explorer"))
                try { System.Diagnostics.Process.Start("explorer.exe", Program.LogsPath.FullName); } catch { }
            UI.EndGroup();
        }

        // ── Tab 3: Colors ─────────────────────────────────────────────────────

        private void DrawColorsTab()
        {
            UI.BeginGroup("Accent & Text");
            InterfaceColors(("Accent", InterfaceColorOption.AccentColor), ("Text", InterfaceColorOption.TextColor),
                            ("Border", InterfaceColorOption.BorderColor));
            UI.EndGroup();

            UI.BeginGroup("Backgrounds");
            InterfaceColors(("Radar Background", InterfaceColorOption.RadarBackground), ("Panel Background", InterfaceColorOption.PanelBackground),
                            ("Input Background", InterfaceColorOption.InputBackground));
            UI.EndGroup();

            UI.BeginGroup("Panel Chrome");
            InterfaceColors(("Panel BG", InterfaceColorOption.MainPanelBackground), ("Panel Title BG", InterfaceColorOption.MainPanelTitleBackground),
                            ("Panel Border", InterfaceColorOption.MainPanelBorder), ("Panel Title Text", InterfaceColorOption.MainPanelTitleText),
                            ("Panel Scrollbar", InterfaceColorOption.MainPanelScrollThumb));
            if (UI.Button("Reset Panel Chrome", new Vector2(UI.GroupInnerWidth, 0f), ButtonStyle.Ghost))
                InterfaceColorOptions.ResetMainPanelColorsToDefaults(Config);
            UI.EndGroup();

            UI.BeginGroup("Widget Chrome");
            InterfaceColors(("Widget BG", InterfaceColorOption.WidgetChromeBackground), ("Widget Title BG", InterfaceColorOption.WidgetChromeTitleBackground),
                            ("Widget Border", InterfaceColorOption.WidgetChromeBorder), ("Widget Title Text", InterfaceColorOption.WidgetChromeTitleText),
                            ("Widget Scrollbar", InterfaceColorOption.WidgetChromeScrollThumb));
            if (UI.Button("Reset Widget Chrome", new Vector2(UI.GroupInnerWidth, 0f), ButtonStyle.Ghost))
                InterfaceColorOptions.ResetWidgetColorsToDefaults(Config);
            UI.EndGroup();

            UI.BeginGroup("Map");
            var mc = Config.MapColors;
            UI.GridItem MapColor(string label, Func<uint> get, Action<uint> set, string hint) => new(w =>
            {
                var c = Radar.RadarDraw.AbgrToRgba(get());
                if (UI.ColorEdit(label, ref c, w, hint)) { set(Radar.RadarDraw.RgbaToAbgr(c)); Config.SaveDebounced(); }
            });
            Grid(
                MapColor("Low Ground##mc_low",     () => mc.TerrainLow,  v => mc.TerrainLow = v,  "Terrain at its lowest; higher ground blends towards High Ground."),
                MapColor("High Ground##mc_high",   () => mc.TerrainHigh, v => mc.TerrainHigh = v, "Terrain at its highest."),
                MapColor("Cliffs & Void##mc_void", () => mc.TerrainVoid, v => mc.TerrainVoid = v, "Cliffs, holes and everything outside the playable area."),
                MapColor("Creep##mc_creep",        () => mc.Creep,       v => mc.Creep = v,       "The creep spread (its opacity: Map ▸ Map Layers)."),
                MapColor("Map Edge##mc_edge",      () => mc.MapEdge,     v => mc.MapEdge = v,     "The outline around the whole map."));
            if (UI.Button("Reset Map Colours##mc_reset", new Vector2(UI.GroupInnerWidth, 0f), ButtonStyle.Ghost))
            { Config.MapColors = new(); Config.SaveDebounced(); }
            UI.EndGroup();

            UI.BeginGroup();
            if (UI.Button("Reset All Colors##colors_reset_all", new Vector2(UI.GroupInnerWidth, 0f), ButtonStyle.Danger))
            {
                InterfaceColorOptions.ResetToDefaults(Config);
                Config.MapColors = new();
            }
            UI.Tooltip("Restore every theme color to its factory default.");
            UI.EndGroup();
        }

        // ── Tab 4: Widgets ────────────────────────────────────────────────────

        private static void DrawWidgetsTab()
        {
            UI.BeginGroup("Widgets");
            UI.DrawGrid(UI.GroupInnerWidth,
            [
                new(w => WidgetToggle("Army", w, "Supply, bank and the value of each player's army, economy and tech.",
                    () => Config.ShowArmyWidget, v => Config.ShowArmyWidget = v,
                    () => Config.ArmyWidgetOverrideOpacity, v => Config.ArmyWidgetOverrideOpacity = v,
                    () => Config.ArmyWidgetAlpha, v => Config.ArmyWidgetAlpha = v)),
                new(w => WidgetToggle("Production", w, "Everything being trained, researched or morphed, with progress and time left.",
                    () => Config.ShowProductionWidget, v => Config.ShowProductionWidget = v,
                    () => Config.ProductionWidgetOverrideOpacity, v => Config.ProductionWidgetOverrideOpacity = v,
                    () => Config.ProductionWidgetAlpha, v => Config.ProductionWidgetAlpha = v)),
                new(w => WidgetToggle("Match", w, "Map name, game clock and speed, match format and who you are.",
                    () => Config.ShowMatchWidget, v => Config.ShowMatchWidget = v,
                    () => Config.MatchWidgetOverrideOpacity, v => Config.MatchWidgetOverrideOpacity = v,
                    () => Config.MatchWidgetAlpha, v => Config.MatchWidgetAlpha = v)),
                new(w => WidgetToggle("Observer", w, "The observer panel's tabs: income per minute, army value over time, units lost, supply and bank, upgrades.",
                    () => Config.ShowObserverWidget, v => Config.ShowObserverWidget = v,
                    () => Config.ObserverWidgetOverrideOpacity, v => Config.ObserverWidgetOverrideOpacity = v,
                    () => Config.ObserverWidgetAlpha, v => Config.ObserverWidgetAlpha = v)),
                new(w => WidgetToggle("Timeline", w, "What each enemy got and when (\"4:10 Spire\"), newest first.",
                    () => Config.ShowTimelineWidget, v => Config.ShowTimelineWidget = v,
                    () => Config.TimelineWidgetOverrideOpacity, v => Config.TimelineWidgetOverrideOpacity = v,
                    () => Config.TimelineWidgetAlpha, v => Config.TimelineWidgetAlpha = v)),
                new(w => WidgetToggle("Unit Counts", w, "Each player's units by type with counts, most valuable first.",
                    () => Config.ShowUnitCountsWidget, v => Config.ShowUnitCountsWidget = v,
                    () => Config.UnitCountsWidgetOverrideOpacity, v => Config.UnitCountsWidgetOverrideOpacity = v,
                    () => Config.UnitCountsWidgetAlpha, v => Config.UnitCountsWidgetAlpha = v)),
                new(w => WidgetToggle("History", w, "Past matches: map, length, and each player's peak army, losses and income (saved when a match ends).",
                    () => Config.ShowHistoryWidget, v => Config.ShowHistoryWidget = v,
                    () => Config.HistoryWidgetOverrideOpacity, v => Config.HistoryWidgetOverrideOpacity = v,
                    () => Config.HistoryWidgetAlpha, v => Config.HistoryWidgetAlpha = v)),
                new(w => WidgetToggle("Hotkeys", w, "The memory writes that are on, and every bound hotkey with its key - green while its feature is on.",
                    () => Config.ShowHotkeysWidget, v => Config.ShowHotkeysWidget = v,
                    () => Config.HotkeysWidgetOverrideOpacity, v => Config.HotkeysWidgetOverrideOpacity = v,
                    () => Config.HotkeysWidgetAlpha, v => Config.HotkeysWidgetAlpha = v)),
                new(w => WidgetToggle("Live Events", w, "What every player does, as an observer sees it: their selection and each command they give (from the game's live replay recording, about a second behind).",
                    () => Config.ShowEventsWidget, v => Config.ShowEventsWidget = v,
                    () => Config.EventsWidgetOverrideOpacity, v => Config.EventsWidgetOverrideOpacity = v,
                    () => Config.EventsWidgetAlpha, v => Config.EventsWidgetAlpha = v)),
                new(w => WidgetToggle("Log", w, "Everything logged this session, with a filter - errors in red, warnings in yellow.",
                    () => Config.ShowLogWidget, v => Config.ShowLogWidget = v,
                    () => Config.LogWidgetOverrideOpacity, v => Config.LogWidgetOverrideOpacity = v,
                    () => Config.LogWidgetAlpha, v => Config.LogWidgetAlpha = v)),
                new(w => WidgetToggle("Debug", w, "Connection, actor counts, resolved offsets and FPS.",
                    () => Config.ShowDebugWidget, v => Config.ShowDebugWidget = v,
                    () => Config.DebugWidgetOverrideOpacity, v => Config.DebugWidgetOverrideOpacity = v,
                    () => Config.DebugWidgetAlpha, v => Config.DebugWidgetAlpha = v)),
            ]);
            UI.EndGroup();

            DrawLayoutPresets();

            UI.BeginGroup("Panel Management");
            // drag-to-merge / drag-to-dock, as in the EFT radar (MenuManager.Layout.cs, WidgetManager.Layout.cs)
            Grid(
                Toggle("Lock All Panels", () => Config.LockPanels, v => Config.LockPanels = v,
                    "Prevents all panels and widgets from being moved or resized."),
                Toggle("Lock Widget Sizes", () => Config.WidgetLockSize, v => Config.WidgetLockSize = v,
                    "Prevents all floating widgets from being resized."),
                Toggle("Panel Merging##pm_merge", () => Config.PanelMerging, v => Config.PanelMerging = v,
                    "Drag a panel onto another: centre to make tabs, an edge to show them side by side."),
                Toggle("Panel Anchoring##pm_anchor", () => Config.PanelAnchoring, v => Config.PanelAnchoring = v,
                    "Drag a panel to a screen edge to dock it there; the radar makes room."),
                Toggle("Widget Merging##pm_wmerge", () => Config.WidgetMerging, v => Config.WidgetMerging = v,
                    "Drag a widget onto another: centre to make tabs, an edge to show them side by side."),
                Toggle("Widget Anchoring##pm_wanchor", () => Config.WidgetAnchoring, v => Config.WidgetAnchoring = v,
                    "Drag a widget to a screen edge to dock it there."));
            UI.EndGroup();
        }

        // ── Tab 7: Web Radar ──────────────────────────────────────────────────
        // The EFT radar's local hosting: UPnP, port and Start / Stop on one row, the update rate, and what each tick sends.
        // While running, the links to open (this PC, the local network, and the internet when UPnP is on).

        private static void DrawWebRadarTab()
        {
            var  wr      = Config.WebRadar;
            bool running = Web.WebRadarServer.IsRunning;

            UI.BeginGroup("Web Radar");
            {
                float gw = UI.GroupInnerWidth;
                const float PortNat = 120f, BtnNat = 130f, PortMinW = 80f, BtnMinW = 80f, Gap = 8f;

                // UPnP gets whatever Port + Button leave; those two shrink when the group is too narrow
                float toggleW      = MathF.Max(0f, gw - PortNat - BtnNat - Gap * 2f);
                float portBtnAvail = gw - (toggleW > 1f ? toggleW + Gap : 0f);
                float portW        = MathF.Min(PortNat, MathF.Max(PortMinW, portBtnAvail - BtnMinW - Gap));
                float btnW         = MathF.Max(BtnMinW, portBtnAvail - portW - Gap);

                Vector2 rowTL = ImGuiNET.ImGui.GetCursorScreenPos();
                bool upnp = wr.UPnP;
                if (toggleW > 1f && UI.Toggle("UPnP##wrUpnp", ref upnp, toggleW, hint: "Automatically forwards the web radar port on your router for remote access over the internet"))
                { wr.UPnP = upnp; Config.SaveDebounced(); }

                float portX = rowTL.X + (toggleW > 1f ? toggleW + Gap : 0f);
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(portX, rowTL.Y));
                int port = wr.Port;
                if (UI.InputInt("Port##wrport", ref port, 1024, 65535, 1, portW, hint: "TCP port the web radar server listens on (1024–65535)"))
                { wr.Port = port; Config.SaveDebounced(); }

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(portX + portW + Gap, rowTL.Y));
                if (UI.Button(running ? "Stop Server##wrStop" : "Start Server##wrStart", new Vector2(btnW, 0f), running ? ButtonStyle.Danger : ButtonStyle.Accent))
                {
                    if (running) Web.WebRadarServer.Stop();
                    else Web.WebRadarServer.Start(openBrowser: true);
                }
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rowTL.X, rowTL.Y + 30f));

                bool auto = wr.AutoStart;
                if (UI.Toggle("Start With The Radar##wrAuto", ref auto, gw, hint: "Start the web radar server when the radar starts."))
                { wr.AutoStart = auto; Config.SaveDebounced(); }

                if (running)
                {
                    int p = Web.WebRadarServer.Port;
                    WebRadarLink("This PC##wrLinkLocal", $"http://localhost:{p}/", "Open in a browser on this PC.");
                    if (Web.WebRadarServer.LocalIP is { } lan)
                        WebRadarLink("Network##wrLinkLan", $"http://{lan}:{p}/", "Open on a phone, tablet or another PC on the same network.");
                    if (wr.UPnP)
                        WebRadarLink("Internet##wrLinkWan", Web.WebRadarServer.ExternalIP is { } wan ? $"http://{wan}:{p}/" : "finding your public address...",
                                     "Open from outside your network (needs the port forwarded: UPnP, or by hand on the router). Anyone with this link sees your radar.");
                }
                else if (Web.WebRadarServer.LastError is { } err)
                    UI.Label(err, LabelStyle.Danger);
            }
            UI.EndGroup();

            UI.BeginGroup("Performance", disabled: !running, disabledReason: "Start the web radar server to configure performance settings");
            {
                float gw = UI.GroupInnerWidth;
                int tickMs = wr.TickRateMs;
                string perfLabel = tickMs <= 100 ? "Fast" : tickMs <= 300 ? "Balanced" : "Bandwidth Saver";
                const float LabelColW = 110f;
                if (UI.SliderInt("Update Rate (ms)##wrTick", ref tickMs, 25, 1000, width: gw - LabelColW - 8f,
                        hint: "How often the server rebuilds the radar data for connected browsers. Lower values are more responsive but use more bandwidth."))
                { wr.TickRateMs = tickMs; Config.SaveDebounced(); }
                ImGuiNET.ImGui.SameLine(0f, 8f);
                // the speed in words, right-aligned in its own column
                var lf    = ImGuiStyle.FontNormal;
                float lfs = lf.FontSize > 0f ? lf.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                float rowH = lfs + 6f;
                float tw  = lf.CalcTextSizeA(lfs, float.MaxValue, 0f, perfLabel).X;
                var lTL   = ImGuiNET.ImGui.GetCursorScreenPos();
                ImGuiNET.ImGui.GetWindowDrawList().AddText(lf, lfs, new Vector2(lTL.X + LabelColW - tw, lTL.Y + (rowH - lfs) * 0.5f),
                    UI.Alpha(ImGuiStyle.Accent, running ? (byte)200 : (byte)70), perfLabel);
                ImGuiNET.ImGui.Dummy(new Vector2(LabelColW, 0f));
            }
            UI.EndGroup();

            UI.BeginGroup("Data Sent to Web Radar", disabled: !running, disabledReason: "Start the web radar server to choose what data is broadcast");
            {
                var labels = new[] { "Player Units", "Neutral", "Cameras", "Player Stats" };
                var sels   = new[] { wr.SendUnits, wr.SendNeutral, wr.SendCameras, wr.SendStats };
                if (UI.TagCombo("##wrData", labels, sels, UI.GroupInnerWidth, placeholder: "Units, Cameras…",
                        hint: "Select which data types are broadcast to connected web clients",
                        itemHints: [
                            "Every player's units and buildings (the largest part of each update on big maps)",
                            "Mineral fields, geysers, critters, scenery and other neutral actors",
                            "Where each player is looking: their camera box",
                            "Minerals, gas and supply per player",
                        ]))
                {
                    (wr.SendUnits, wr.SendNeutral, wr.SendCameras, wr.SendStats) = (sels[0], sels[1], sels[2], sels[3]);
                    Config.SaveDebounced();
                }
            }
            UI.EndGroup();
        }

        /// <summary>A read-only link row with a copy button, as EFT's hosted share links.</summary>
        private static void WebRadarLink(string label, string url, string hint)
        {
            float gw = UI.GroupInnerWidth;
            const float LblW = 90f, CopyW = 30f, RowH = 30f, RowGap = 6f, Gap = 8f;
            float inputW = MathF.Max(60f, gw - LblW - CopyW - Gap);
            Vector2 r = ImGuiNET.ImGui.GetCursorScreenPos();
            string shown = url;   // edits never stick: re-assigned every frame
            UI.InputText(label, ref shown, 256, width: inputW, labelWidth: LblW, hint: hint);
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(r.X + LblW + inputW + Gap, r.Y));
            if (UI.IconButton($"##copy{label}", IconCache.Icon.Copy, w: CopyW, h: RowH, iconSz: 16f, hint: "Copy link"))
                ImGuiNET.ImGui.SetClipboardText(url);
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(r.X, r.Y + RowH + RowGap));
            ImGuiNET.ImGui.Dummy(new Vector2(gw, 0f));
        }

        // ── Tab 6: Hotkeys ────────────────────────────────────────────────────
        // The EFT radar's Hotkeys tab: Input Manager, Add Hotkey (action, captured key, trigger) and Active Hotkeys (one
        // row per bound key; select a row to change its trigger or remove it). The actions are SC2Hotkeys'.

        private sealed record HotkeyRow(string Action, string ActionKey, string Key, int KeyCode, string Type);

        private static int    _hotkeyActionIdx;
        private static int    _hotkeyModeIdx;
        private static bool   _capturingKey;
        private static bool   _captureWaitMouse1Release;
        private static int    _capturedKeyCode = -1;
        private static string _capturedKeyName = "";
        private static int    _selectedHotkeyIdx = -1;
        private static int    _editHotkeyModeIdx;
        private static int    _lastHotkeySelIdx = -1;
        private static bool   _hotkeyChangedWired;
        private static readonly List<HotkeyRow> _hotkeyList = [];

        private static readonly string[] _hotkeyActionKeys = [.. SC2Hotkeys.Actions.Select(a => a.Key)];
        private static readonly string[] _hotkeyActionLabels = [.. SC2Hotkeys.Actions.Select(a => ActionLabel(a.Name, a.Group))];

        /// <summary>An action's name as the flat list shows it: the panel toggles say they're panels, the master switch
        /// whose it is.</summary>
        private static string ActionLabel(string name, string group) => group switch
        {
            "Panels" when !name.EndsWith("Panel") => $"{name} Panel",
            "Memory Writes" when name == "Master Switch" => "Mem Writes Master Switch",
            _ => name,
        };

        private static void DrawHotkeysTab()
        {
            if (!_hotkeyChangedWired)
            {
                UI.OnHotkeyChanged += LoadHotkeysFromConfig;
                _hotkeyChangedWired = true;
            }
            if (_hotkeyList.Count == 0) LoadHotkeysFromConfig();

            UI.BeginGroup("Input Manager");
            {
                UI.Caption(InputManager.Status, style: InputStatusStyle(InputManager.IsReady, InputManager.Status));
                UI.Caption("Hotkeys work from the gaming PC's keyboard (read over DMA, once Ready) and from this PC's.");
            }
            UI.EndGroup();

            UI.BeginGroup("Add Hotkey");
            {
                float gw    = UI.GroupInnerWidth;
                var   font  = ImGuiStyle.FontNormal;
                float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

                const float ActionLabelW = 54f;
                const float AddBtnW      = 28f;
                const float KeyBtnW      = 150f;
                float comboW = gw - ActionLabelW - 8f - KeyBtnW - 4f - AddBtnW;

                // Row 1: Action label + combo + Select Key button + + icon button
                bool canAdd = _capturedKeyCode > 0 && _hotkeyActionIdx >= 0 && _hotkeyActionIdx < _hotkeyActionKeys.Length;
                var  dl     = ImGuiNET.ImGui.GetWindowDrawList();
                var  rowTL  = ImGuiNET.ImGui.GetCursorScreenPos();
                dl.AddText(font, fsize,
                    new Vector2(rowTL.X + 2f, rowTL.Y + (30f - fsize) * 0.5f),
                    UI.Alpha(ImGuiStyle.Text, 210), LocManager.Get("Action"));
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rowTL.X + ActionLabelW, rowTL.Y));
                UI.Combo("##hkAction", ref _hotkeyActionIdx, _hotkeyActionLabels, comboW, hint: "The radar feature this hotkey will trigger");

                float keyX = rowTL.X + ActionLabelW + comboW + 8f;
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(keyX, rowTL.Y));
                if (_capturingKey)
                {
                    UI.Button("Press a key...##hkCap", new Vector2(KeyBtnW, 0f), ButtonStyle.Ghost);
                    if (_captureWaitMouse1Release)
                    {
                        if (!InputManager.IsKeyDownAny(0x01))
                            _captureWaitMouse1Release = false;
                    }
                    else if (InputManager.IsKeyDownAny(0x1B))  // ESC → cancel capture
                    {
                        _capturingKey = false;
                    }
                    else
                    {
                        for (int vk = 1; vk <= 0xFF; vk++)
                        {
                            if (vk is 0x10 or 0x11 or 0x12 or 0x1B) continue;
                            if (InputManager.IsKeyDownAny(vk))
                            {
                                _capturedKeyCode = vk;
                                _capturedKeyName = GetKeyDisplayName(vk);
                                _capturingKey    = false;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    string btnLabel = _capturedKeyCode > 0 ? _capturedKeyName : "Select Key";
                    if (UI.Button(btnLabel + "##hkKeyBtn", new Vector2(KeyBtnW, 0f), ButtonStyle.Ghost,
                            hint: "Click then press any key to capture it for this hotkey"))
                    {
                        _capturingKey             = true;
                        _captureWaitMouse1Release = true;
                    }
                }

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(keyX + KeyBtnW + 4f, rowTL.Y));
                if (!canAdd) UI.BeginDisabled("Select an action and press a key above to add");
                if (UI.IconButton("##hkAdd", IconCache.Icon.Plus, w: AddBtnW, h: 30f, iconSz: 16f,
                        style: ButtonStyle.Accent, hint: "Add this hotkey") && canAdd)
                    AddHotkey(_hotkeyActionKeys[_hotkeyActionIdx], _capturedKeyCode, _hotkeyModeIdx == 0 ? HotkeyMode.OnKey : HotkeyMode.Toggle);
                if (!canAdd) UI.EndDisabled();

                // Row 2: Trigger label + On Key/Toggle bar
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rowTL.X, rowTL.Y + 30f + 4f));
                Vector2 r2TL = ImGuiNET.ImGui.GetCursorScreenPos();
                dl.AddText(font, fsize,
                    new Vector2(r2TL.X + 2f, r2TL.Y + (30f - fsize) * 0.5f),
                    UI.Alpha(ImGuiStyle.Text, 210), LocManager.Get("Trigger"));
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(r2TL.X + ActionLabelW, r2TL.Y));
                UI.SegmentedBar("##hkMode", new[] { "On Key", "Toggle" }, ref _hotkeyModeIdx, autoSize: true,
                    hint: "On Key switches the feature on while the key is held. Toggle switches it on or off with each press.");

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(r2TL.X, r2TL.Y + 30f));
                ImGuiNET.ImGui.Dummy(new Vector2(gw, 0f));
            }
            UI.EndGroup();

            UI.BeginGroup("Active Hotkeys");
            {
                float gw     = UI.GroupInnerWidth;
                bool  hasSel = _selectedHotkeyIdx >= 0 && _selectedHotkeyIdx < _hotkeyList.Count;

                // Sync edit mode index when selection changes
                if (_selectedHotkeyIdx != _lastHotkeySelIdx)
                {
                    _lastHotkeySelIdx  = _selectedHotkeyIdx;
                    _editHotkeyModeIdx = hasSel && _hotkeyList[_selectedHotkeyIdx].Type == "Toggle" ? 1 : 0;
                }

                // Header row: Mode bar (left) + Trash icon button (right)
                const float TrashW     = 28f;
                const float ModeLabelW = 44f;
                const float HdrRowH    = 28f;
                var         hdrTL      = ImGuiNET.ImGui.GetCursorScreenPos();
                var         hfont      = ImGuiStyle.FontNormal;
                float       hfsize     = hfont.FontSize > 0f ? hfont.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

                ImGuiNET.ImGui.GetWindowDrawList().AddText(hfont, hfsize,
                    new Vector2(hdrTL.X + 2f, hdrTL.Y + (HdrRowH - hfsize) * 0.5f),
                    UI.Alpha(ImGuiStyle.Text, hasSel ? (byte)210 : (byte)100), "Mode");

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(hdrTL.X + ModeLabelW, hdrTL.Y));
                if (!hasSel) UI.BeginDisabled("Select a hotkey from the list below");
                float modeBarW = MathF.Max(1f, gw - ModeLabelW - TrashW - 4f);
                if (UI.SegmentedBar("##hkEditMode", new[] { "On Key", "Toggle" }, ref _editHotkeyModeIdx, width: modeBarW, stretch: true,
                        hint: "On Key switches the feature on while the key is held. Toggle switches it on or off with each press.") && hasSel)
                    UpdateSelectedHotkeyMode();
                if (!hasSel) UI.EndDisabled();

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(hdrTL.X + gw - TrashW, hdrTL.Y));
                if (!hasSel) UI.BeginDisabled("Select a hotkey from the list below");
                if (UI.IconButton("##hkTrash", IconCache.Icon.Trash, w: TrashW, h: HdrRowH, style: ButtonStyle.Danger,
                        hint: "Remove selected hotkey") && hasSel)
                {
                    int    idxToRemove = _selectedHotkeyIdx;
                    string dispName    = _hotkeyList[_selectedHotkeyIdx].Action;
                    string keyName     = _hotkeyList[_selectedHotkeyIdx].Key;
                    ConfirmModal.Open("Remove Hotkey", $"Remove \"{dispName}\" ({keyName})?",
                        () => RemoveHotkey(idxToRemove), confirmLabel: "Remove");
                }
                if (!hasSel) UI.EndDisabled();

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(hdrTL.X, hdrTL.Y + HdrRowH));
                ImGuiNET.ImGui.Dummy(new Vector2(gw, 0f));

                ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 6f);

                if (_hotkeyList.Count == 0)
                    UI.Empty("No hotkeys configured.");
                else
                    DrawHotkeyList(gw);
            }
            UI.EndGroup();
        }

        private static void DrawHotkeyList(float gw)
        {
            const float RowH      = 26f;
            const float ColAction = 160f;
            const float ColKey    = 80f;

            var   dl  = ImGuiNET.ImGui.GetWindowDrawList();
            var   lf  = ImGuiStyle.FontNormal;
            float lfs = lf.FontSize > 0f ? lf.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            // Column header (drawn before child so it sits outside the scrollable area)
            {
                var hdrPos = ImGuiNET.ImGui.GetCursorScreenPos();
                uint hc = UI.Alpha(0xFFFFFFFF, 110);
                float cx = hdrPos.X + 4f, cy = hdrPos.Y + 2f;
                // Clip each header label to its column width
                dl.PushClipRect(new Vector2(cx, cy - 2f), new Vector2(cx + ColAction, cy + lfs + 2f), true);
                dl.AddText(lf, lfs, new Vector2(cx, cy), hc, LocManager.Get("Action"));
                dl.PopClipRect();
                cx += ColAction;
                dl.PushClipRect(new Vector2(cx, cy - 2f), new Vector2(cx + ColKey, cy + lfs + 2f), true);
                dl.AddText(lf, lfs, new Vector2(cx, cy), hc, LocManager.Get("Key"));
                dl.PopClipRect();
                cx += ColKey;
                dl.PushClipRect(new Vector2(cx, cy - 2f), new Vector2(hdrPos.X + gw - 4f, cy + lfs + 2f), true);
                dl.AddText(lf, lfs, new Vector2(cx, cy), hc, LocManager.Get("Mode"));
                dl.PopClipRect();
                ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + lfs + 6f);
                dl.AddLine(
                    new Vector2(hdrPos.X, ImGuiNET.ImGui.GetCursorScreenPos().Y),
                    new Vector2(hdrPos.X + gw, ImGuiNET.ImGui.GetCursorScreenPos().Y),
                    UI.Alpha(ImGuiStyle.Border, 60));
                ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 2f);
            }

            float listH = Math.Max(60f, Math.Min(_hotkeyList.Count * RowH + 4f, 300f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiNET.ImGuiStyleVar.WindowPadding, new Vector2(0f, 2f));
            ImGuiNET.ImGui.BeginChild("##hklist", new Vector2(gw, listH), ImGuiNET.ImGuiChildFlags.None);
            ImGuiNET.ImGui.PopStyleVar();
            float rw = ImGuiNET.ImGui.GetContentRegionAvail().X;

            ImGuiNET.ImGui.PushStyleVar(ImGuiNET.ImGuiStyleVar.ItemSpacing, new Vector2(0f, 0f));
            for (int i = 0; i < _hotkeyList.Count; i++)
            {
                var  hk  = _hotkeyList[i];
                bool sel = _selectedHotkeyIdx == i;
                ImGuiNET.ImGui.PushID(i);

                var rowTL = ImGuiNET.ImGui.GetCursorScreenPos();
                if (i % 2 == 0)
                    dl.AddRectFilled(rowTL, new Vector2(rowTL.X + rw, rowTL.Y + RowH), UI.Alpha(ImGuiStyle.Dark4, 30));
                if (sel)
                    dl.AddRectFilled(rowTL, new Vector2(rowTL.X + rw, rowTL.Y + RowH), UI.Alpha(ImGuiStyle.Accent, 50));

                UI.RowHover(rowTL, rw, RowH);
                ImGuiNET.ImGui.InvisibleButton("##hkrow", new Vector2(rw, RowH));
                if (ImGuiNET.ImGui.IsItemClicked()) _selectedHotkeyIdx = i;

                float ty = rowTL.Y + (RowH - lfs) * 0.5f;
                uint  tc = sel ? UI.Alpha(ImGuiStyle.Accent, 220) : UI.Alpha(0xFFFFFFFF, 190);
                float cx = rowTL.X + 4f;
                // Action - clip to column width so long names don't bleed into Key/Mode
                dl.PushClipRect(new Vector2(cx, rowTL.Y), new Vector2(cx + ColAction - 4f, rowTL.Y + RowH), true);
                dl.AddText(lf, lfs, new Vector2(cx, ty), tc, LocManager.Get(hk.Action));
                dl.PopClipRect();
                cx += ColAction;
                // Key is a physical key/button name (e.g. "Mouse4", "F1") - left untranslated
                dl.PushClipRect(new Vector2(cx, rowTL.Y), new Vector2(cx + ColKey - 4f, rowTL.Y + RowH), true);
                dl.AddText(lf, lfs, new Vector2(cx, ty), UI.Alpha(0xFFFFFFFF, 180), hk.Key);
                dl.PopClipRect();
                cx += ColKey;
                dl.PushClipRect(new Vector2(cx, rowTL.Y), new Vector2(rowTL.X + rw - 4f, rowTL.Y + RowH), true);
                dl.AddText(lf, lfs, new Vector2(cx, ty), UI.Alpha(0xFFFFFFFF, 150), LocManager.Get(hk.Type));
                dl.PopClipRect();
                ImGuiNET.ImGui.PopID();
            }
            ImGuiNET.ImGui.PopStyleVar();
            ImGuiNET.ImGui.EndChild();
        }

        /// <summary>One row per key of every enabled action, in the actions' order.</summary>
        private static void LoadHotkeysFromConfig()
        {
            _hotkeyList.Clear();
            var conflictEntries = new List<(HotkeyEntry, string)>();
            foreach (var (actionKey, name, group) in SC2Hotkeys.Actions)
            {
                if (Config.HotKeys.Get(actionKey) is not { Enabled: true } entry) continue;
                string displayName = ActionLabel(name, group);
                conflictEntries.Add((entry, displayName));
                var keys = new List<int>();
                if (entry.Keys?.Count > 0) keys.AddRange(entry.Keys);
                if (entry.Key != -1 && !keys.Contains(entry.Key)) keys.Add(entry.Key);
                foreach (var k in keys.Distinct())
                    _hotkeyList.Add(new HotkeyRow(displayName, actionKey, GetKeyDisplayName(k), k,
                                                  entry.GetKeyMode(k) == HotkeyMode.Toggle ? "Toggle" : "On Key"));
            }
            UI.RebuildHotkeyConflicts(conflictEntries);
        }

        private static void AddHotkey(string actionKey, int keyCode, HotkeyMode mode)
        {
            if (Config.HotKeys.Get(actionKey) is not { } entry) return;
            entry.Enabled = true;
            entry.Mode    = mode;
            entry.AddKey(keyCode);
            Config.SaveDebounced();
            LoadHotkeysFromConfig();
            UI.OnHotkeyChanged?.Invoke();
            _capturedKeyCode = -1;
            _capturedKeyName = "";
        }

        private static void RemoveHotkey(int listIndex)
        {
            if (listIndex < 0 || listIndex >= _hotkeyList.Count) return;
            var item = _hotkeyList[listIndex];
            if (Config.HotKeys.Get(item.ActionKey) is not { } entry) return;
            entry.RemoveKey(item.KeyCode);
            if ((entry.Keys == null || entry.Keys.Count == 0) && entry.Key == item.KeyCode)
                entry.Key = -1;
            if ((entry.Keys == null || entry.Keys.Count == 0) && entry.Key == -1)
                entry.Enabled = false;
            Config.SaveDebounced();
            _selectedHotkeyIdx = -1;
            LoadHotkeysFromConfig();
            UI.OnHotkeyChanged?.Invoke();
        }

        private static void UpdateSelectedHotkeyMode()
        {
            if (_selectedHotkeyIdx < 0 || _selectedHotkeyIdx >= _hotkeyList.Count) return;
            var item = _hotkeyList[_selectedHotkeyIdx];
            if (Config.HotKeys.Get(item.ActionKey) is not { } entry) return;
            entry.KeyModes ??= new Dictionary<int, HotkeyMode>();
            entry.KeyModes[item.KeyCode] = _editHotkeyModeIdx == 0 ? HotkeyMode.OnKey : HotkeyMode.Toggle;
            Config.SaveDebounced();
            int savedIdx = _selectedHotkeyIdx;
            LoadHotkeysFromConfig();
            _selectedHotkeyIdx = savedIdx < _hotkeyList.Count ? savedIdx : -1;
            UI.OnHotkeyChanged?.Invoke();
        }

        private static string GetKeyDisplayName(int vk)
        {
            if (vk >= 0x01 && vk <= 0x06)
                return vk switch { 0x01 => "Mouse1", 0x02 => "Mouse2", 0x04 => "Mouse3",
                                   0x05 => "Mouse4",  0x06 => "Mouse5",  _ => $"Mouse{vk}" };
            return vk switch
            {
                0x08 => "Backspace",    0x09 => "Tab",         0x0D => "Enter",      0x13 => "Pause",
                0x14 => "CapsLock",     0x1B => "Escape",
                0x20 => "Space",        0x21 => "PageUp",      0x22 => "PageDown",   0x23 => "End",
                0x24 => "Home",         0x25 => "Left",        0x26 => "Up",         0x27 => "Right",
                0x28 => "Down",         0x2C => "PrintScreen", 0x2D => "Insert",     0x2E => "Delete",
                >= 0x30 and <= 0x39 => ((char)vk).ToString(),
                >= 0x41 and <= 0x5A => ((char)vk).ToString(),
                0x5B => "LWin",         0x5C => "RWin",        0x5D => "Menu",
                >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
                0x6A => "Num*",         0x6B => "Num+",        0x6D => "Num-",       0x6E => "Num.",   0x6F => "Num/",
                >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
                0x90 => "NumLock",      0x91 => "ScrollLock",
                0xA0 => "LShift",       0xA1 => "RShift",      0xA2 => "LCtrl",      0xA3 => "RCtrl",
                0xA4 => "LAlt",         0xA5 => "RAlt",
                0xAD => "Mute",         0xAE => "VolDown",     0xAF => "VolUp",
                0xB0 => "MediaNext",    0xB1 => "MediaPrev",   0xB2 => "MediaStop",  0xB3 => "MediaPlay",
                0xBA => ";",            0xBB => "=",           0xBC => ",",          0xBD => "-",
                0xBE => ".",            0xBF => "/",           0xC0 => "`",          0xDB => "[",
                0xDC => "\\",           0xDD => "]",           0xDE => "'",
                _ => $"Key{vk}"
            };
        }

        /// <summary>
        /// Picks a caption color for the input status string: green once ready, amber neutral while a scan/retry is
        /// actively in progress, red once it's given up or reported a hard failure.
        /// </summary>
        private static LabelStyle InputStatusStyle(bool ready, string status)
        {
            if (ready) return LabelStyle.Success;
            if (status.Contains("Restarting",  StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Checking",    StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Scanning",    StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Connecting",  StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Trying",      StringComparison.OrdinalIgnoreCase) ||
                status.Contains("next attempt", StringComparison.OrdinalIgnoreCase))
                return LabelStyle.Accent;
            return LabelStyle.Warning;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>A widget's show toggle, its opacity override behind the cog.</summary>
        // ── layout presets (A4): save the widget arrangement, put it back, tie it to a radar profile ──
        private static string _presetName = "";
        private static int _presetIdx;

        private static void DrawLayoutPresets()
        {
            var widgets = ImGuiApp.Menus?.Widgets;
            var presets = Config.LayoutPresets;
            UI.BeginGroup("Layout Presets");
            UI.Caption("Which widgets are open, where, and how they're grouped or docked. A radar profile can bring one with it (Visuals ▸ Profiles).");

            UI.InputText("Name##lp_name", ref _presetName, 48, placeholder: "Laddering, Arcade, Minimal", width: UI.GroupInnerWidth);
            string name = _presetName.Trim();
            bool exists = presets.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (widgets != null && name.Length > 0 &&
                UI.Button(exists ? $"Overwrite \"{name}\" with this layout##lp_save" : "Save this layout##lp_save", new Vector2(UI.GroupInnerWidth, 0f)))
            {
                presets.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                presets.Add(widgets.CapturePreset(name));
                _presetIdx = presets.Count - 1;
                Config.SaveDebounced();
                ToastManager.Show("Layout saved", name, ToastStyle.Success, 2f);
            }

            if (presets.Count > 0)
            {
                _presetIdx = Math.Clamp(_presetIdx, 0, presets.Count - 1);
                UI.Combo("Preset##lp_pick", ref _presetIdx, [.. presets.Select(x => x.Name)], width: UI.GroupInnerWidth, localizeItems: false);
                var sel = presets[_presetIdx];
                float half = (UI.GroupInnerWidth - 6f) * 0.5f;
                if (widgets != null && UI.Button("Apply##lp_apply", new Vector2(half, 0f))) widgets.ApplyPreset(sel);
                ImGuiNET.ImGui.SameLine(0f, 6f);
                if (UI.Button("Delete##lp_del", new Vector2(half, 0f), ButtonStyle.Danger))
                {
                    presets.Remove(sel);
                    foreach (var p in RadarProfiles.S.Profiles.Where(p => p.LayoutPreset == sel.Name)) p.LayoutPreset = "";
                    RadarProfiles.Changed();
                    Config.SaveDebounced();
                }
            }
            else UI.Caption("No presets yet.");
            UI.EndGroup();
        }

        private static void WidgetToggle(string name, float width, string hint,
            Func<bool> getShow, Action<bool> setShow, Func<bool> getOverride, Action<bool> setOverride,
            Func<float> getAlpha, Action<float> setAlpha)
        {
            bool show = getShow();
            if (UI.ToggleWithPopover($"{name}##widget_{name}", ref show, () =>
                {
                    bool ov = getOverride();
                    if (UI.Toggle($"Override Opacity##wo_{name}", ref ov, hint: "Use its own opacity instead of the shared widget opacity."))
                    { setOverride(ov); Config.SaveDebounced(); }
                    if (getOverride())
                    {
                        UI.BeginSliderGroup("Opacity");
                        float a = getAlpha();
                        if (UI.SliderFloat($"Opacity##wa_{name}", ref a, 0f, 1f, "%.2f"))
                        { setAlpha(a); Config.SaveDebounced(); }
                        UI.EndSliderGroup();
                    }
                }, width: width, hint: hint))
            { setShow(show); Config.SaveDebounced(); }
        }

        private static void OnShowDebugConsole_Changed(bool value)
        {
            XMLogging.SetConsoleVisible(value);
            Config.ShowDebugConsole = value;
            Config.SaveDebounced();
        }

        private static void OnLogToFile_Changed(bool value)
        {
            XMLogging.SetFileLoggingEnabled(value);
            Config.LogToFile = value;
            Config.SaveDebounced();
        }

        /// <summary>Theme colours in the group's two-column grid.</summary>
        private static void InterfaceColors(params (string Label, InterfaceColorOption Option)[] colors) =>
            Grid([.. colors.Select(c => new UI.GridItem(w => DrawInterfaceColorEdit(c.Label, c.Option, w)))]);

        private static bool DrawInterfaceColorEdit(string label, InterfaceColorOption opt, float width)
        {
            var colors = Config.InterfaceColors;
            var rgba   = Vector4.One;
            if (colors != null && colors.TryGetValue(opt, out var hex) && SKColor.TryParse(hex, out var c))
                rgba = new Vector4(c.Red / 255f, c.Green / 255f, c.Blue / 255f, c.Alpha / 255f);

            bool changed = UI.ColorEdit(label + "##ic_" + opt, ref rgba, width);
            if (changed)
            {
                var skColor = new SKColor(
                    (byte)(rgba.X * 255), (byte)(rgba.Y * 255),
                    (byte)(rgba.Z * 255), (byte)(rgba.W * 255));
                InterfaceColorOptions.UpdateColor(Config, opt, skColor);
            }
            return changed;
        }
    }
}
