using SC2Radar.Common.Misc.Data;
using SC2Radar.DMA.Features;
using MW = SC2Radar.SC2.Features.MemoryWrites;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Every action a hotkey can trigger (Settings ▸ Hotkeys; the memory writes also show their key on the Mem Writes
    /// panel's toggles). One <see cref="HotkeyEntry"/> each: its keys, and per key Toggle (press flips the feature) or
    /// OnKey (on while held). Dispatched by UI.ImGui.HotkeyManager.
    /// </summary>
    public sealed class SC2Hotkeys
    {
        // ── radar ────────────────────────────────────────────────────────────
        public HotkeyEntry ZoomIn          { get; set; } = new();
        public HotkeyEntry ZoomOut         { get; set; } = new();
        public HotkeyEntry CenterOnBase    { get; set; } = new();
        public HotkeyEntry FitMap          { get; set; } = new();
        public HotkeyEntry ToggleLabels    { get; set; } = new();
        public HotkeyEntry ToggleCreep     { get; set; } = new();
        public HotkeyEntry TogglePylonPower { get; set; } = new();
        public HotkeyEntry ToggleCameras   { get; set; } = new();
        public HotkeyEntry ToggleDefenceRanges { get; set; } = new();
        public HotkeyEntry ToggleProgress  { get; set; } = new();
        public HotkeyEntry TogglePOIs      { get; set; } = new();
        public HotkeyEntry NextProfile     { get; set; } = new();

        // ── panels ───────────────────────────────────────────────────────────
        public HotkeyEntry ToggleSettings   { get; set; } = new();
        public HotkeyEntry ToggleUnitFilter { get; set; } = new();
        public HotkeyEntry ToggleMemWritesPanel { get; set; } = new();
        public HotkeyEntry ToggleSearch     { get; set; } = new();
        public HotkeyEntry TogglePOIPanel   { get; set; } = new();

        // ── memory writes ────────────────────────────────────────────────────
        public HotkeyEntry MemWritesMaster  { get; set; } = new();
        public HotkeyEntry RevealMap        { get; set; } = new();
        public HotkeyEntry ShareEnemyVision { get; set; } = new();
        public HotkeyEntry UnitSight        { get; set; } = new();
        public HotkeyEntry ObserverView     { get; set; } = new();
        public HotkeyEntry CameraZoom       { get; set; } = new();
        public HotkeyEntry FastWin          { get; set; } = new();

        /// <summary>(property name, display name, group) for the Hotkeys tab, in display order.</summary>
        public static readonly (string Key, string Name, string Group)[] Actions =
        [
            (nameof(ZoomIn), "Zoom In", "Radar"), (nameof(ZoomOut), "Zoom Out", "Radar"),
            (nameof(CenterOnBase), "Centre On My Base", "Radar"), (nameof(FitMap), "Fit Map To Window", "Radar"),
            (nameof(ToggleLabels), "Labels", "Radar"), (nameof(ToggleCreep), "Creep", "Radar"),
            (nameof(TogglePylonPower), "Pylon Power", "Radar"), (nameof(ToggleCameras), "Player Cameras", "Radar"),
            (nameof(ToggleDefenceRanges), "Enemy Defence Ranges", "Radar"), (nameof(ToggleProgress), "Production Rings", "Radar"),
            (nameof(TogglePOIs), "Points Of Interest", "Radar"), (nameof(NextProfile), "Next Radar Profile", "Radar"),
            (nameof(ToggleSettings), "Settings", "Panels"), (nameof(ToggleUnitFilter), "Unit Filter", "Panels"),
            (nameof(ToggleMemWritesPanel), "Memory Writes", "Panels"), (nameof(ToggleSearch), "Search", "Panels"),
            (nameof(TogglePOIPanel), "Points Of Interest Panel", "Panels"),
            (nameof(MemWritesMaster), "Master Switch", "Memory Writes"), (nameof(RevealMap), "Reveal Map", "Memory Writes"),
            (nameof(ShareEnemyVision), "Share Enemy Vision", "Memory Writes"), (nameof(UnitSight), "Unit Sight", "Memory Writes"),
            (nameof(CameraZoom), "Camera Zoom", "Memory Writes"), (nameof(FastWin), "Fast Win", "Memory Writes"),
            (nameof(ObserverView), "Observer View", "Memory Writes"),
        ];

        static readonly System.Reflection.PropertyInfo[] Props =
            typeof(SC2Hotkeys).GetProperties().Where(p => p.PropertyType == typeof(HotkeyEntry)).ToArray();

        /// <summary>Every action with its entry (property name -> entry).</summary>
        public IEnumerable<(string Key, HotkeyEntry Entry)> All() =>
            Props.Select(p => (p.Name, p.GetValue(this) as HotkeyEntry ?? new HotkeyEntry()));

        public HotkeyEntry? Get(string key) => Props.FirstOrDefault(p => p.Name == key)?.GetValue(this) as HotkeyEntry;

        /// <summary>The hotkey of a memory-write feature (shown on its toggle), null when it has none.</summary>
        public HotkeyEntry? ForFeature(IMemWriteFeature f) => f switch
        {
            MW.RevealMap           => RevealMap,
            MW.ShareEnemyVision    => ShareEnemyVision,
            MW.UnitSight           => UnitSight,
            MW.ObserverView        => ObserverView,
            MW.CameraZoom          => CameraZoom,
            MW.FastWin             => FastWin,
            _                      => null,
        };
    }
}
