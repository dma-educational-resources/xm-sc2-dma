using System.Numerics;
using System.Text.Json;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Panels;
using SC2Radar.UI.ImGui.Radar;

namespace SC2Radar.UI.ImGui.Widgets
{
    /// <summary>
    /// Owns and draws the radar widgets. As in the EFT radar, widgets can be dragged onto each other (tabs in the centre,
    /// a split at an edge) or to a screen edge (docked) - that generic part is in WidgetManager.Layout.cs; this file has
    /// the SC2 widgets (one table: widget + its Config.Show*Widget flag), the draw order, widgets the active radar profile
    /// hides (RadarProfile.HiddenWidgets) and layout presets (Config.LayoutPresets, one per profile if it names one).
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed partial class WidgetManager : IDisposable
    {
        static Config Config => Program.Config;

        /// <summary>A widget and its show flag. Show is this frame's state (the flag, unless the profile hides it).</summary>
        sealed class Entry(WidgetChrome widget, Func<bool> get, Action<bool> set)
        {
            public readonly WidgetChrome Widget = widget;
            public readonly Func<bool> Get = get;
            public readonly Action<bool> Set = set;
            public bool Show, Wanted, HiddenByProfile;
        }

        readonly List<Entry> _entries;
        readonly Dictionary<string, WidgetChrome>     _widgetByTitle;
        readonly Dictionary<WidgetChrome, Action>     _hideActions;
        readonly Dictionary<WidgetChrome, Func<bool>> _visibilityFuncs;

        // ── groups / splits / docks (WidgetManager.Layout.cs) ────────────────
        readonly List<WidgetGroup> _groups = new();
        readonly List<WidgetSplit> _splits = new();
        readonly Dictionary<DockEdge, WidgetChrome> _docked  = new();
        readonly Dictionary<DockEdge, float>        _dockDim = new();
        int _nextGroupId, _nextSplitId;
        string? _bringToFront;   // "##widgetchrome_<title>" of a widget just dragged out of a group or split
        int     _bringToFrontTtl;
        string? _lastProfile;    // the radar profile last frame: switching to one with a layout preset applies it

        public WidgetManager()
        {
            var c = Config;
            _entries =
            [
                new(new ArmyWidget(),       () => c.ShowArmyWidget,       v => c.ShowArmyWidget = v),
                new(new ProductionWidget(), () => c.ShowProductionWidget, v => c.ShowProductionWidget = v),
                new(new MatchWidget(),      () => c.ShowMatchWidget,      v => c.ShowMatchWidget = v),
                new(new ObserverWidget(),   () => c.ShowObserverWidget,   v => c.ShowObserverWidget = v),
                new(new TimelineWidget(),   () => c.ShowTimelineWidget,   v => c.ShowTimelineWidget = v),
                new(new UnitCountsWidget(), () => c.ShowUnitCountsWidget, v => c.ShowUnitCountsWidget = v),
                new(new HistoryWidget(),    () => c.ShowHistoryWidget,    v => c.ShowHistoryWidget = v),
                new(new HotkeysWidget(),    () => c.ShowHotkeysWidget,    v => c.ShowHotkeysWidget = v),
                new(new EventsWidget(),     () => c.ShowEventsWidget,     v => c.ShowEventsWidget = v),
                new(new LogWidget(),        () => c.ShowLogWidget,        v => c.ShowLogWidget = v),
                new(new DebugWidget(),      () => c.ShowDebugWidget,      v => c.ShowDebugWidget = v),
            ];
            _widgetByTitle   = new(StringComparer.OrdinalIgnoreCase);
            _hideActions     = [];
            _visibilityFuncs = [];
            foreach (var e in _entries)
            {
                _widgetByTitle[e.Widget.Title] = e.Widget;
                _hideActions[e.Widget]     = () => e.Show = false;
                _visibilityFuncs[e.Widget] = () => e.Show;
            }
            RestoreLayout();
        }

        /// <summary>Every widget's title, in draw order (Settings ▸ Widgets, Visuals ▸ Profiles).</summary>
        public IEnumerable<string> WidgetTitles => _entries.Select(e => e.Widget.Title);

        /// <summary>Groups, splits and docks saved in Config.WidgetOpen.</summary>
        void RestoreLayout()
        {
            var ws = Config.WidgetOpen;
            WidgetChrome? Widget(string title) => _widgetByTitle.TryGetValue(title, out var w) ? w : null;
            DockEdge Edge(string key) => Enum.TryParse<DockEdge>(key, ignoreCase: true, out var e) ? e : DockEdge.None;

            var inDockedGroups = new HashSet<string>(ws.DockedGroups.Values.Where(v => v != null).SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
            foreach (var titles in ws.WidgetGroups)
            {
                var members = titles.Where(t => !inDockedGroups.Contains(t)).Select(Widget).OfType<WidgetChrome>().ToList();
                if (members.Count >= 2) CreateGroup(members);
            }
            foreach (var entry in ws.WidgetSplits)
                if (RestoreSplitFromConfig(entry) is { } split && entry.DockedEdge is { } se && Edge(se) is var e && e != DockEdge.None)
                    DockWidget(split, e, save: false);
            foreach (var (key, title) in ws.DockedWidgets)
                if (Edge(key) is var e && e != DockEdge.None && Widget(title) is { } w) DockWidget(w, e, save: false);
            foreach (var (key, titles) in ws.DockedGroups)
            {
                var e = Edge(key);
                if (e == DockEdge.None || titles == null) continue;
                var members = titles.Select(Widget).OfType<WidgetChrome>().ToList();
                if (members.Count == 1) DockWidget(members[0], e, save: false);
                else if (members.Count > 1) DockWidget(CreateGroup(members), e, save: false);
            }
            foreach (var (key, dim) in ws.DockedDims)
                if (Edge(key) is var e && _dockDim.ContainsKey(e)) _dockDim[e] = Math.Max(DockMinDim, dim);
        }

        public void DrawWidgets()
        {
            // a profile with a layout preset took over (a new map): lay the widgets out its way
            var profile = RadarProfiles.Active;
            if (_lastProfile != profile.Name)
            {
                bool first = _lastProfile == null;
                _lastProfile = profile.Name;
                if (!first && profile.LayoutPreset.Length > 0 && Config.LayoutPresets.FirstOrDefault(p => p.Name == profile.LayoutPreset) is { } preset)
                    ApplyPreset(preset);
            }

            foreach (var e in _entries)
            {
                e.HiddenByProfile = profile.HiddenWidgets.Contains(e.Widget.Title);
                e.Wanted = e.Get();
                e.Show = e.Wanted && !e.HiddenByProfile;
            }
            foreach (var (widget, vis) in _visibilityFuncs) widget.IsVisible = vis();
            var ws = Config.WidgetOpen;

            // docked first (edge slots), then floating: unpinned, then pinned on top
            DrawDockedWidgets();
            foreach (bool pinnedPass in new[] { false, true })
                foreach (var e in _entries)
                    if (e.Show && ws.IsPinned(e.Widget.Title) == pinnedPass) e.Widget.Draw(ref e.Show);

            foreach (var (widget, vis) in _visibilityFuncs) widget.IsVisible = vis();

            // groups (always drawn; their members skip their own draw)
            for (int i = _groups.Count - 1; i >= 0; i--)
            {
                var g = _groups[i];
                g.Draw(ref g.ShowState);
                if (g.ShowState) continue;
                if (!g.DissolvedByPop) foreach (var m in g.Members.ToList()) HideWidget(m);
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
                WidgetSplit? parent = null; int parentPi = -1;
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
                    var edge = s.DockedEdge;
                    s.DockedEdge = DockEdge.None;
                    _docked.Remove(edge);
                    if (s.PaneCount > 0) { var survivor = s.GetPane(0); survivor.IsGrouped = false; survivor.IsHosted = false; DockWidget(survivor, edge, save: false); }
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

            DrawGroupDropZones();

            if (_bringToFront != null)
            {
                ImGuiNET.ImGui.SetWindowFocus(_bringToFront);
                if (--_bringToFrontTtl <= 0) _bringToFront = null;
            }

            // closed from a widget's own X (or a group's): write the flag back - not for one the profile hides
            bool changed = false;
            foreach (var e in _entries)
                if (e.Wanted && !e.HiddenByProfile && !e.Show) { e.Set(false); changed = true; }
            if (changed) Config.SaveDebounced();
        }

        /// <summary>Widgets that can be dropped onto (groups first: they draw last, on top).</summary>
        IEnumerable<WidgetChrome> GetVisibleMergeableWidgets()
        {
            foreach (var g in _groups) if (!g.IsGrouped) yield return g;
            foreach (var e in _entries) if (e.Show && !e.Widget.IsGrouped) yield return e.Widget;
        }

        // ── layout presets ───────────────────────────────────────────────────

        static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

        /// <summary>The current arrangement as a preset: which widgets are open, where, and how they are grouped / docked.</summary>
        public LayoutPreset CapturePreset(string name) => new()
        {
            Name    = name,
            Shown   = [.. _entries.Where(e => e.Get()).Select(e => e.Widget.Title)],
            Layouts = Clone(Config.WidgetLayouts),
            Open    = Clone(Config.WidgetOpen),
        };

        /// <summary>Puts a preset's arrangement back: show flags, positions and sizes, groups, splits and docks.</summary>
        public void ApplyPreset(LayoutPreset preset)
        {
            // tear the current groups / splits / docks down (as MenuManager.ApplySavedLayout does for panels)
            foreach (var g in _groups) DissolveGroup(g);
            _groups.Clear();
            foreach (var s in _splits)
                for (int i = s.PaneCount - 1; i >= 0; i--) { var pane = s.GetPane(i); pane.IsGrouped = false; pane.IsHosted = false; }
            _splits.Clear();
            foreach (var kvp in _docked.ToList()) kvp.Value.DockedEdge = DockEdge.None;
            _docked.Clear();
            _dockDim.Clear();

            Config.WidgetLayouts = Clone(preset.Layouts);
            Config.WidgetOpen    = Clone(preset.Open);
            foreach (var e in _entries)
            {
                e.Set(preset.Shown.Contains(e.Widget.Title));
                e.Widget.ReloadPlacement();
            }
            RestoreLayout();
            Config.SaveDebounced();
            ToastManager.Show("Layout", preset.Name, ToastStyle.Info, 2f);
        }

        public void Dispose() { }
    }
}
