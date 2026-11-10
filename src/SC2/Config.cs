using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using SC2Radar.UI;
using SC2Radar.UI.Misc;
using SC2Radar.Common.Misc.Config;

namespace SC2Radar.SC2
{
    public sealed class Config : SC2Radar.Common.Misc.Config.IConfig
    {
        // ── Radar display ──────────────────────────────────────────────────────
        public bool   ShowUnits     { get; set; } = true;
        public bool   ShowBuildings { get; set; } = true;
        public bool   ShowWorkers   { get; set; } = true;
        public float  UnitDotRadius { get; set; } = 3.5f;
        public bool   ZoomDetail    { get; set; } = true;   // small plain dots zoomed out, full markers / outlines / bars zoomed in
        public int    TargetFPS     { get; set; } = 60;

        // ── Unit scan ─────────────────────────────────────────────────────────
        public int UnitReadIntervalMs { get; set; } = 50; // misc worker (unit details, players, match ...): 20 Hz, each reader keeps its own cadence
        public int RealtimeIntervalMs { get; set; } = 16; // realtime worker (unit positions): ~60 Hz
        public int PositionReads      { get; set; } = SC2Memory.PositionReadFields;  // how the realtime read is batched (fields is usually the fastest layout)
        public string DeviceStr       { get; set; } = "fpga";   // the DMA device (MemProcFS -device), e.g. "fpga://algo=1" for the FPGA's tiny-read algorithm
        // performance mode (Settings ▸ Performance): for big arcade maps. 0 off, 1 auto (from PerfAutoUnits units on the map), 2 always
        public int  PerfMode           { get; set; } = 1;
        public int  PerfAutoUnits      { get; set; } = 1500;
        public bool PerfSlowerReads    { get; set; } = true;    // half the read rate
        public bool PerfSkipLayers     { get; set; } = true;    // no vision overlay, creep or pylon power
        public bool PerfSkipLabels     { get; set; } = true;    // no unit names, vitals figures or resource amounts (cluster labels stay)
        public bool PerfSimpleMarkers  { get; set; } = true;    // plain dots: no outlines, shadows or health bars
        public bool ShowPerfOverlay    { get; set; } = false;   // frame / draw / memory read times in the corner, instead of FPS only

        // ── Render-actor source (radar data; see SC2Memory.ActorReader.cs) ────
        public ulong ActorVtableRva     { get; set; } = 0x2E7D578; // actor vtable, SC2_x64.exe-relative
        public int   ActorPosOffset     { get; set; } = 0x40;      // float X, Y
        public int   ActorModelOffset   { get; set; } = 0x158;     // -> model, +0x40 -> path string
        public int   ActorColorOffset   { get; set; } = 0x5A4;     // float RGB team colour
        public int   ActorRescanSeconds { get; set; } = 10;        // pause between heap-scan rescans (fallback path)
        // static actor table: [[exe+ActorTableRva]+ActorTableSceneOffset]+ActorTablePagesOffset = {page*, 0x800} pairs
        public ulong ActorTableRva         { get; set; } = 0x4033830; // 0 = always heap-scan
        public int   ActorTableSceneOffset { get; set; } = 0xE0;
        public int   ActorTablePagesOffset { get; set; } = 0xA900;
        public int   ActorTableRefreshMs   { get; set; } = 250;       // pause between table re-reads
        public bool  ShowLabels         { get; set; } = true;
        public bool  ShowTerrain        { get; set; } = true;      // the map's height map under the units
        public float TerrainAlpha       { get; set; } = 0.85f;
        public bool  ShowClockOverlay   { get; set; } = true;      // game clock + map name in the radar's corner
        public bool  MatchInTopBar      { get; set; } = true;      // game clock, map and radar profile in the top bar
        public bool  CursorInTopBar     { get; set; } = true;      // map x, y under the mouse in the top bar
        public bool  DeclutterLabels    { get; set; } = true;      // skip labels that would overlap another
        public bool  ShowProgress       { get; set; } = true;      // morph progress rings
        public bool  ProgressDetails    { get; set; } = true;      // over the ring: what it makes and the time left (else the %)
        public bool  RangeHovered       { get; set; } = true;      // weapon range ring around the unit under the cursor
        public bool  RangeDefences      { get; set; } = true;      // weapon range of enemy static defence
        public bool  RangeEnemies       { get; set; } = false;     // weapon range of every enemy unit
        public bool  DetectionRings     { get; set; } = true;      // detection radius of enemy detectors
        public bool  ShowCameras        { get; set; } = true;      // where the other players are looking (their camera view)
        public bool  ShowOwnCamera      { get; set; } = true;      // ... and your own (live, from the client camera)
        public bool  ShowAiCameras      { get; set; } = true;      // computer players too (their camera rarely moves)
        public float CameraConsoleHeight { get; set; } = 22f;     // % of the screen the bottom console hides (camera boxes start above it)
        public bool  ShowCreep          { get; set; } = true;      // the creep spread, from the game's creep grid
        public float CreepAlpha         { get; set; } = 0.55f;
        public bool  ShowPowerFields    { get; set; } = true;      // Protoss power fields: what each Pylon / phasing Warp Prism powers
        public bool  MapFrame           { get; set; } = true;      // playable area outlined, the rest faded, coordinate ticks
        public bool  ClusterUnits       { get; set; } = true;      // zoomed out: one "40 Marine +5" label per crowd (full list on hover)
        public bool  ClusterBases       { get; set; } = false;     // ... for bases (buildings, workers) too, not only armies
        public bool  DimOutsideVision   { get; set; } = false;     // fade enemy units your own in-game vision doesn't cover
        public bool  BuildingFootprints { get; set; } = true;      // zoomed in: buildings at their real footprint
        public bool  ShowLarvaInject    { get; set; } = true;      // larva count on hatcheries, inject and chrono rings (behaviour scan)
        public bool  DropWarning        { get; set; } = true;      // toast + red pulse when enemy transports or an army reach your base
        public bool  DropWarningSound   { get; set; } = false;
        public bool  TrackCargo         { get; set; } = true;      // read transports' cargo: hide units inside, count on the transport
        public bool  TrackOrders        { get; set; } = true;      // read units' orders (ability, target unit or point)
        public bool  TrackBuffs         { get; set; } = true;      // read every unit's buffs / debuffs (behaviour ids): hover card, "buff:" patterns
        public bool  TrackScript        { get; set; } = true;      // map-script variables (Research ▸ Script Variables, "script:" patterns)
        public List<string> ScriptPins  { get; set; } = [];        // Research ▸ Script Variables: paths read all the time
        public bool  TrackEvents        { get; set; } = true;      // live game event feed (commands, selections, cameras) from the replay the client writes
        public bool  ShowSelections     { get; set; } = true;      // ring the units each player has selected (live event feed)
        public int   OrderLines         { get; set; } = 1;         // lines to order targets: 0 off, 1 enemies, 2 everyone
        public float PowerFieldAlpha    { get; set; } = 0.15f;
        // points of interest (Points of Interest panel, PoiManager): drawn on the radar for the map being played
        public bool        ShowPOIs  { get; set; } = true;
        public bool        POILabels { get; set; } = true;
        public MarkerShape POIShape  { get; set; } = MarkerShape.Diamond;
        public float       POISize   { get; set; } = 6f;           // marker radius, pixels
        public uint        POIColor  { get; set; } = 0xFFFFBF00;   // ABGR: deep sky blue, as the EFT radar
        public MapColorSettings MapColors { get; set; } = new();  // Settings ▸ Colors ▸ Map
        public bool  CamerasEnemiesOnly { get; set; } = false;     // camera boxes for enemies only (your own still follows ShowOwnCamera)
        public bool  ScoutAlerts        { get; set; } = false;     // toast when an enemy fields a new unit / structure type
        public bool  ScoutAlertsHidden  { get; set; } = false;     // toast when a cloaked / burrowed enemy unit appears
        public bool  SupplyBlockAlert   { get; set; } = true;      // toast when your own supply hits the cap

        // ── Window ────────────────────────────────────────────────────────────
        public int  WindowX         { get; set; } = 100;
        public int  WindowY         { get; set; } = 100;
        public int  WindowWidth     { get; set; } = 1280;
        public int  WindowHeight    { get; set; } = 720;
        public bool WindowMaximized { get; set; } = false;

        // ── UI / locale ───────────────────────────────────────────────────────
        public string UILocale { get; set; } = "en";
        public float  UIScale  { get; set; } = 1.0f;

        // ── Map zoom / rotation ───────────────────────────────────────────────
        public int Zoom             { get; set; } = 100;
        public int MapRotationSteps { get; set; } = 0;

        // ── Map overrides (saved via Override Editor) ─────────────────────────
        public Dictionary<string, MapConfigOverride> MapOverrides { get; set; } = new();

        // ── Panel layout persistence ──────────────────────────────────────────
        public Dictionary<string, PanelLayoutEntry>  PanelLayouts  { get; set; } = new();
        public Dictionary<string, WidgetLayoutEntry> WidgetLayouts { get; set; } = new();
        public bool LockPanels { get; set; } = false;
        public bool PanelMerging   { get; set; } = true;   // drag a panel onto another: tabs (centre) or a split (edges)
        public bool PanelAnchoring { get; set; } = true;   // drag a panel to a screen edge: dock it there

        // ── Color themes ─────────────────────────────────────────────────────
        public Dictionary<RadarColorOption, string>?     Colors          { get; set; }
        public Dictionary<InterfaceColorOption, string>? InterfaceColors { get; set; }

        // ── Logging ───────────────────────────────────────────────────────────
        public bool ShowDebugConsole { get; set; } = false;
        public bool LogToFile        { get; set; } = false;

        // ── Debug widget ──────────────────────────────────────────────────────
        public bool  ShowDebugWidget              { get; set; } = true;
        public float DebugWidgetAlpha             { get; set; } = 0.85f;
        public bool  DebugWidgetOverrideOpacity   { get; set; } = false;

        // ── Vision overlay (toggled per player in the Army widget) ───────────
        public List<int> VisionPlayers   { get; set; } = [];   // players whose vision (incl. shared) is drawn
        public float     VisionFillAlpha { get; set; } = 0.16f;
        public bool      AllianceColors  { get; set; } = false;   // you green, allies yellow, enemies red
        public bool      VisionHighGround { get; set; } = true;   // ground units don't see up cliffs (needs the height map)

        // ── Army widget ───────────────────────────────────────────────────────
        public bool  ShowArmyWidget               { get; set; } = true;
        public float ArmyWidgetAlpha              { get; set; } = 0.85f;
        public bool  ArmyWidgetOverrideOpacity    { get; set; } = false;
        public ArmyWidgetSettings Army            { get; set; } = new();

        // ── Match widget ──────────────────────────────────────────────────────
        public bool  ShowMatchWidget              { get; set; } = true;
        public float MatchWidgetAlpha             { get; set; } = 0.85f;
        public bool  MatchWidgetOverrideOpacity   { get; set; } = false;
        public bool  MatchShowDescription         { get; set; } = true;

        // ── Production widget ─────────────────────────────────────────────────
        public bool  ShowProductionWidget            { get; set; } = true;
        public float ProductionWidgetAlpha           { get; set; } = 0.85f;
        public bool  ProductionWidgetOverrideOpacity { get; set; } = false;
        public ProductionWidgetSettings Production   { get; set; } = new();

        // ── Hotkeys widget (active writes + bound hotkeys) ────────────────────
        public bool  ShowHotkeysWidget            { get; set; } = false;
        public float HotkeysWidgetAlpha           { get; set; } = 0.85f;
        public bool  HotkeysWidgetOverrideOpacity { get; set; } = false;

        // ── Live Events widget (commands and selections from the live event feed) ──
        public bool  ShowEventsWidget             { get; set; } = false;
        public float EventsWidgetAlpha            { get; set; } = 0.85f;
        public bool  EventsWidgetOverrideOpacity  { get; set; } = false;

        // ── Log widget (everything logged this session) ───────────────────────
        public bool  ShowLogWidget                { get; set; } = false;
        public float LogWidgetAlpha               { get; set; } = 0.85f;
        public bool  LogWidgetOverrideOpacity     { get; set; } = false;

        // ── Observer widget (income, army graph, lost, supply, upgrades) ─────
        public bool  ShowObserverWidget            { get; set; } = false;
        public float ObserverWidgetAlpha           { get; set; } = 0.85f;
        public bool  ObserverWidgetOverrideOpacity { get; set; } = false;
        public int   ObserverTab                   { get; set; } = 0;

        // ── Timeline widget (what each enemy got and when) ────────────────────
        public bool  ShowTimelineWidget            { get; set; } = false;
        public float TimelineWidgetAlpha           { get; set; } = 0.85f;
        public bool  TimelineWidgetOverrideOpacity { get; set; } = false;
        public bool  TimelineAllPlayers            { get; set; } = false;
        public bool  TimelineBuildings             { get; set; } = true;

        // ── Unit Counts widget ────────────────────────────────────────────────
        public bool  ShowUnitCountsWidget            { get; set; } = false;
        public float UnitCountsWidgetAlpha           { get; set; } = 0.85f;
        public bool  UnitCountsWidgetOverrideOpacity { get; set; } = false;
        public bool  UnitCountsWorkers               { get; set; } = true;
        public bool  UnitCountsBuildings             { get; set; } = false;
        public bool  UnitCountsEnemiesOnly           { get; set; } = false;
        public bool  ShowTypeBadges                  { get; set; } = true;    // type icons in Unit Counts and Production (UnitIcons)

        // ── History widget (past matches) ─────────────────────────────────────
        public bool  ShowHistoryWidget            { get; set; } = false;
        public float HistoryWidgetAlpha           { get; set; } = 0.85f;
        public bool  HistoryWidgetOverrideOpacity { get; set; } = false;

        // ── Widget management ─────────────────────────────────────────────────
        public bool WidgetLockSize { get; set; } = false;
        public bool WidgetMerging   { get; set; } = true;   // drag a widget onto another: tabs (centre) or a split (edges)
        public bool WidgetAnchoring { get; set; } = true;   // drag a widget to a screen edge: dock it there
        /// <summary>Pinned, merged, split and docked widgets (WidgetManager), saved across restarts.</summary>
        public WidgetOpenState WidgetOpen { get; set; } = new();
        /// <summary>Saved widget arrangements (Settings ▸ Widgets ▸ Layout Presets); a radar profile can name one to use.</summary>
        public List<LayoutPreset> LayoutPresets { get; set; } = [];
        [JsonIgnore]
        public ExpanderStatesData ExpanderStates { get; } = new();

        // ── Debug / logging ───────────────────────────────────────────────────

        // ── Collapsed UI groups ───────────────────────────────────────────────
        public HashSet<string> CollapsedGroups { get; set; } = new();

        // ── Memory writes ─────────────────────────────────────────────────────────────
        public bool MemWritesEnabled { get; set; } = false;
        public Features.MemWriteSettings MemWrites { get; set; } = new();

        // ── Hotkeys (Settings ▸ Hotkeys) ──────────────────────────────────────
        public SC2Hotkeys HotKeys { get; set; } = new();

        // ── Web radar (Settings ▸ Web Radar) ──────────────────────────────────
        public WebRadarConfig WebRadar { get; set; } = new();

        // ── Persistence ───────────────────────────────────────────────────────
        [JsonIgnore]
        static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            Converters = { SafeEnumKeyDictionaryConverterFactory.Instance },
        };

        // Lives alongside the logs/profiles/other data under Program.DataDir, which uses
        // Program.SoftwareId ("sc2_dma_radar") - previously hardcoded a separate "sc2_dma"
        // folder, splitting the settings across two directories.
        [JsonIgnore]
        public static readonly string DefaultFile = Path.Combine(Program.DataDir, "config.json");

        public static Config Load()
        {
            try
            {
                if (File.Exists(DefaultFile))
                {
                    var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(DefaultFile), _json) ?? new();
                    if (cfg.MemWrites.MigrateLegacyFiles()) cfg.SaveDebounced();
                    return cfg;
                }
            }
            catch { }
            return new();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DefaultFile)!);
                if (!SettingsBackup.BeforeSave(DefaultFile)) return;
                File.WriteAllText(DefaultFile, JsonSerializer.Serialize(this, _json));
            }
            catch (InvalidOperationException) when (_saveTimer != null)
            {
                // the UI changed a list or dictionary while it was being written (the timer runs on another thread): again shortly
                SaveDebounced();
            }
            catch { }
        }

        public Task SaveAsync() => Task.Run(Save);

        private System.Threading.Timer? _saveTimer;
        public void SaveDebounced()
        {
            _saveTimer?.Dispose();
            _saveTimer = new System.Threading.Timer(_ => Save(), null, 1500, -1);
        }

        /// <summary>Which panels are open, pinned, merged, split or docked (MenuManager), saved across restarts.</summary>
        public PanelOpenState PanelOpen { get; set; } = new();
    }

    /// <summary>The local web radar server (SC2Radar.Web.WebRadarServer): a page any browser on the network can open.</summary>
    public sealed class WebRadarConfig
    {
        public int  Port        { get; set; } = Random.Shared.Next(50000, 60000);
        public bool UPnP        { get; set; } = false;   // forward the port on the router, for viewers outside the network
        public int  TickRateMs  { get; set; } = 100;     // how often the radar data is rebuilt for the browsers
        public bool AutoStart   { get; set; } = false;   // start the server with the radar
        // what each tick carries
        public bool SendUnits   { get; set; } = true;    // the players' units and buildings
        public bool SendNeutral { get; set; } = true;    // resources, critters, scenery and the other neutral actors
        public bool SendCameras { get; set; } = true;    // where every player is looking
        public bool SendStats   { get; set; } = true;    // minerals, gas and supply per player
    }

    /// <summary>
    /// Panel layout state, as the EFT radar keeps it: open flags, pinned panels, drag-merged groups (member titles in tab
    /// order), panels and groups docked to a screen edge ("left" / "right" / "top" / "bottom"), dock slot sizes and splits.
    /// </summary>
    public sealed class PanelOpenState
    {
        public bool General    { get; set; } = true;
        public bool Debug      { get; set; } = false;
        public bool UnitFilter { get; set; } = false;
        public bool MemWrites  { get; set; } = false;
        public bool Search     { get; set; } = false;
        public bool POI        { get; set; } = false;

        /// <summary>Panel keys drawn on top of all unpinned panels.</summary>
        public HashSet<string> Pinned
        {
            get => _pinned;
            set => _pinned = new(value ?? [], StringComparer.OrdinalIgnoreCase);   // JSON may hand back null / lose the comparer
        }
        private HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);

        public bool IsPinned(string key) => _pinned.Contains(key);
        public void TogglePin(string key)
        {
            if (!_pinned.Remove(key)) _pinned.Add(key);
        }

        public List<List<string>> PanelGroups { get => _groups; set => _groups = value ?? []; }
        private List<List<string>> _groups = [];

        public Dictionary<string, string> DockedPanels { get => _dockedPanels; set => _dockedPanels = value ?? []; }
        private Dictionary<string, string> _dockedPanels = [];

        public Dictionary<string, List<string>> DockedGroups { get => _dockedGroups; set => _dockedGroups = value ?? []; }
        private Dictionary<string, List<string>> _dockedGroups = [];

        public Dictionary<string, float> DockedDims { get => _dockedDims; set => _dockedDims = value ?? []; }
        private Dictionary<string, float> _dockedDims = [];

        public List<PanelSplitEntry> PanelSplits { get => _splits; set => _splits = value ?? []; }
        private List<PanelSplitEntry> _splits = [];
    }

    /// <summary>One slot in a SplitPanel: a named panel or a nested split.</summary>
    public sealed class SplitPaneSlot
    {
        public string?          Panel { get; set; }
        public PanelSplitEntry? Split { get; set; }
    }

    /// <summary>A saved SplitPanel (hierarchical: nested splits inline). DockedEdge: the edge a top-level split is docked to.</summary>
    public sealed class PanelSplitEntry
    {
        public List<SplitPaneSlot>? Slots      { get; set; }
        public List<float>?         Ratios     { get; set; }
        public bool                 Horizontal { get; set; }
        public string?              DockedEdge { get; set; }
    }

    public sealed class PanelLayoutEntry
    {
        public float X         { get; set; }
        public float Y         { get; set; }
        public float W         { get; set; }
        public float H         { get; set; }
        public bool  Collapsed { get; set; }
    }

    public sealed class WidgetLayoutEntry
    {
        public float X          { get; set; }
        public float Y          { get; set; }
        public float W          { get; set; }
        public float H          { get; set; }
        public bool  PoppedOut  { get; set; }
        public bool  Collapsed  { get; set; }
        public bool  AlwaysOnTop { get; set; }
    }

    /// <summary>Widget layout state, as the EFT radar keeps it (widgets merge / split / dock only with each other).</summary>
    /// <summary>A saved widget arrangement: the widgets open, their positions and sizes, and how they are grouped / docked.</summary>
    public sealed class LayoutPreset
    {
        public string Name { get; set; } = "Layout";
        public List<string> Shown { get; set; } = [];
        public Dictionary<string, WidgetLayoutEntry> Layouts { get; set; } = new();
        public WidgetOpenState Open { get; set; } = new();
    }

    public sealed class WidgetOpenState
    {
        public HashSet<string> Pinned
        {
            get => _pinned;
            set => _pinned = new(value ?? [], StringComparer.OrdinalIgnoreCase);
        }
        private HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
        public bool IsPinned(string key) => _pinned.Contains(key);
        public void TogglePin(string key)
        {
            if (!_pinned.Remove(key)) _pinned.Add(key);
        }

        public List<List<string>> WidgetGroups { get => _groups; set => _groups = value ?? []; }
        private List<List<string>> _groups = [];

        public Dictionary<string, string> DockedWidgets { get => _dockedWidgets; set => _dockedWidgets = value ?? []; }
        private Dictionary<string, string> _dockedWidgets = [];

        public Dictionary<string, List<string>> DockedGroups { get => _dockedGroups; set => _dockedGroups = value ?? []; }
        private Dictionary<string, List<string>> _dockedGroups = [];

        public Dictionary<string, float> DockedDims { get => _dockedDims; set => _dockedDims = value ?? []; }
        private Dictionary<string, float> _dockedDims = [];

        public List<WidgetSplitEntry> WidgetSplits { get => _splits; set => _splits = value ?? []; }
        private List<WidgetSplitEntry> _splits = [];
    }

    public sealed class WidgetSplitPaneSlot
    {
        public string?           Widget { get; set; }
        public WidgetSplitEntry? Split  { get; set; }
    }

    public sealed class WidgetSplitEntry
    {
        public List<WidgetSplitPaneSlot>? Slots      { get; set; }
        public List<float>?               Ratios     { get; set; }
        public bool                       Horizontal { get; set; }
        public string?                    DockedEdge { get; set; }
    }

    public sealed class ExpanderStatesData
    {
        public Dictionary<string, bool> ExpanderStates { get; set; } = new();
    }

    /// <summary>Army widget options (title-bar menu and its settings dialog).</summary>
    public sealed class ArmyWidgetSettings
    {
        public bool ShowSupply      { get; set; } = true;
        public bool ShowBank        { get; set; } = true;    // unspent minerals / gas (player object)
        public bool ShowArmy        { get; set; } = true;
        public bool ShowUnits       { get; set; } = true;
        public bool ShowEconomy     { get; set; } = true;
        public bool ShowWorkers     { get; set; } = true;
        public bool ShowTech        { get; set; } = true;
        public bool ShowVision      { get; set; } = true;
        public bool ShowUpgrades    { get; set; } = true;    // weapon / armour levels and other research
        public bool TeamTotals      { get; set; } = true;    // a totals row under each team with more than one player
        public bool SupplyWarning   { get; set; } = true;    // supply in red when used >= cap (cap below 200)
        public bool HideUnnamed     { get; set; } = false;   // hide AI / map-controlled players (no lobby name)
        public bool ClanTags        { get; set; } = true;    // "<VAPR> mason"
        public HashSet<string> CollapsedTeams { get; set; } = [];   // "own" = the local player's team, else "1,3,5"
    }

    /// <summary>The map's own colours (ABGR): terrain shading, creep and the map edge (Settings ▸ Colors ▸ Map).</summary>
    public sealed class MapColorSettings
    {
        public uint TerrainLow  { get; set; } = 0xFF2B211C;   // low ground
        public uint TerrainHigh { get; set; } = 0xFF6E7366;   // high ground
        public uint TerrainVoid { get; set; } = 0xFF0D0A09;   // cliffs, holes, outside the playable area
        public uint Creep       { get; set; } = 0xFF82346A;   // the minimap's creep purple
        public uint MapEdge     { get; set; } = 0x50FFFFFF;

        /// <summary>Changes whenever a colour does: the layers rebuild their textures on it.</summary>
        [JsonIgnore] public int Version => HashCode.Combine(TerrainLow, TerrainHigh, TerrainVoid);

    }

    public sealed class ProductionWidgetSettings
    {
        public bool ShowOwn      { get; set; } = true;    // your own production
        public bool ShowAllies   { get; set; } = true;
        public bool ShowEnemies  { get; set; } = true;
        public bool ShowMorphs   { get; set; } = true;    // Lair, Orbital, eggs, cocoons, ...
        public bool ShowQueues   { get; set; } = true;    // units and research in production buildings
        public bool ShowIdle     { get; set; } = true;    // production buildings with nothing queued
        public bool IdleOwnOnly  { get; set; } = true;
        public bool Combine      { get; set; } = true;    // identical entries (eggs, ...) as one row "×n"
        public bool ShowPlayer   { get; set; } = true;    // the owner's name column
        public int  MaxRows      { get; set; } = 20;
    }

}
