using System.Text.RegularExpressions;
using SC2Radar.Common.Misc.Data;
using SC2Radar.SC2;
using SC2Radar.DMA.Features;
using SC2Radar.SC2.Features;
using SC2Radar.SC2.Features.MemoryWrites;

namespace SC2Radar.UI.ImGui
{
    /// <summary>
    /// Hotkeys (Settings ▸ Hotkeys): polled once per frame on the UI thread - the polling half of the EFT radar's
    /// HotkeyManager. Keys are read from this PC and, over DMA, from the gaming PC's keyboard (InputManager.IsKeyDownAny),
    /// so they work while playing. Per key: Toggle - each press flips the feature's real state (so a hotkey and its
    /// checkbox never disagree) - or OnKey - on while held, back off on release. Zoom repeats while held.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class HotkeyManager : IDisposable
    {
        const int CooldownMs = 150, RepeatMs = 60, ZoomStep = 5;

        readonly Dictionary<string, Action> _panelToggles;   // MenuManager's panel show flags, by action key
        readonly Dictionary<string, bool> _wasDown = [];      // per action / per mode: the key was down last frame
        readonly Dictionary<string, DateTime> _lastRun = [];

        /// <param name="panelToggles">Opens / closes a panel, by action key (ToggleSettings, ...).</param>
        public HotkeyManager(Dictionary<string, Action> panelToggles)
        {
            _panelToggles = panelToggles;
            UI.OnHotkeyChanged -= RebuildConflicts;
            UI.OnHotkeyChanged += RebuildConflicts;
            RebuildConflictMap();
        }

        public void Dispose() => UI.OnHotkeyChanged -= RebuildConflicts;

        // a binding just changed: the key that was pressed to bind it is still down - don't also fire its action
        DateTime _quietUntil;

        /// <summary>The key badges flag a key bound to two actions.</summary>
        void RebuildConflicts()
        {
            _quietUntil = DateTime.UtcNow.AddMilliseconds(400);
            RebuildConflictMap();
        }

        static void RebuildConflictMap() =>
            UI.RebuildHotkeyConflicts(Program.Config.HotKeys.All().Where(a => a.Entry.Enabled || a.Entry.Keys.Count > 0)
                .Select(a => (a.Entry, Regex.Replace(a.Key, "([a-z])([A-Z])", "$1 $2"))));

        public void PollHotkeys()
        {
            var now = DateTime.UtcNow;
            bool quiet = now < _quietUntil;   // still track key states, but run nothing
            foreach (var (key, entry) in Program.Config.HotKeys.All())
            {
                var keys = entry.Keys?.Where(k => k > 0).Distinct().ToList() ?? [];
                if (entry.Key > 0 && !keys.Contains(entry.Key)) keys.Add(entry.Key);
                if (keys.Count == 0 || !entry.Enabled) continue;   // binding a key enables it; its switch on the Hotkeys tab can turn it off

                // Toggle keys: act on the press edge, once per cooldown
                bool toggleDown = keys.Any(k => entry.GetKeyMode(k) == HotkeyMode.Toggle && InputManager.IsKeyDownAny(k));
                if (Edge(key + "/t", toggleDown) && !quiet && Ready(key, now, CooldownMs)) Run(key, hold: null);

                // OnKey keys: on while held (zoom: repeats while held)
                bool holdDown = keys.Any(k => entry.GetKeyMode(k) == HotkeyMode.OnKey && InputManager.IsKeyDownAny(k));
                if (key is nameof(SC2Hotkeys.ZoomIn) or nameof(SC2Hotkeys.ZoomOut))
                {
                    if ((holdDown || toggleDown) && !quiet && Ready(key + "/r", now, RepeatMs)) Run(key, hold: true);
                }
                else
                {
                    bool was = _wasDown.GetValueOrDefault(key + "/h");
                    if (holdDown != was) { _wasDown[key + "/h"] = holdDown; if (!quiet) Run(key, hold: holdDown); }
                }
            }
        }

        /// <summary>True on the frame <paramref name="down"/> goes from up to down.</summary>
        bool Edge(string id, bool down)
        {
            bool was = _wasDown.GetValueOrDefault(id);
            _wasDown[id] = down;
            return down && !was;
        }

        bool Ready(string id, DateTime now, int ms)
        {
            if (_lastRun.TryGetValue(id, out var last) && (now - last).TotalMilliseconds < ms) return false;
            _lastRun[id] = now;
            return true;
        }

        /// <param name="hold">null: a Toggle press (flip); true / false: an OnKey key went down / up (set).</param>
        void Run(string key, bool? hold)
        {
            var c = Program.Config;
            bool Flip(bool current) => hold ?? !current;
            void Save() => c.SaveDebounced();
            try
            {
                switch (key)
                {
                    case nameof(SC2Hotkeys.ZoomIn):  if (hold != false) ImGuiApp.Radar?.AdjustZoom(ZoomStep);  break;
                    case nameof(SC2Hotkeys.ZoomOut): if (hold != false) ImGuiApp.Radar?.AdjustZoom(-ZoomStep); break;
                    case nameof(SC2Hotkeys.CenterOnBase): if (hold != false) ImGuiApp.Radar?.CenterOnBase(); break;
                    case nameof(SC2Hotkeys.FitMap):       if (hold != false) ImGuiApp.Radar?.FitMapToWindow(); break;
                    case nameof(SC2Hotkeys.ToggleLabels):      c.ShowLabels      = Flip(c.ShowLabels);      Save(); break;
                    case nameof(SC2Hotkeys.ToggleCreep):       c.ShowCreep       = Flip(c.ShowCreep);       Save(); break;
                    case nameof(SC2Hotkeys.TogglePylonPower):  c.ShowPowerFields = Flip(c.ShowPowerFields); Save(); break;
                    case nameof(SC2Hotkeys.ToggleCameras):     c.ShowCameras     = Flip(c.ShowCameras);     Save(); break;
                    case nameof(SC2Hotkeys.ToggleDefenceRanges): c.RangeDefences = Flip(c.RangeDefences);   Save(); break;
                    case nameof(SC2Hotkeys.ToggleProgress):    c.ShowProgress    = Flip(c.ShowProgress);    Save(); break;
                    case nameof(SC2Hotkeys.TogglePOIs):        c.ShowPOIs        = Flip(c.ShowPOIs);        Save(); break;
                    case nameof(SC2Hotkeys.NextProfile):
                        if (hold == false) break;
                        var ps = RadarProfiles.S.Profiles;
                        int at = ps.IndexOf(RadarProfiles.Active);
                        RadarProfiles.S.Forced = ps[(at + 1) % ps.Count].Name;
                        RadarProfiles.Changed();
                        ToastManager.Show("Radar profile", RadarProfiles.S.Forced, ToastStyle.Info, 2);
                        break;
                    case nameof(SC2Hotkeys.MemWritesMaster):
                        bool on = Flip(c.MemWritesEnabled);
                        FeatureManager.SetMasterSwitch(on);
                        ToastManager.Show("Memory writes", on ? "On" : "Off", on ? ToastStyle.Warning : ToastStyle.Info, 2);
                        break;
                    case nameof(SC2Hotkeys.RevealMap):        SetFeature(RevealMap.Instance, hold);   break;
                    case nameof(SC2Hotkeys.ShareEnemyVision): SetFeature(ShareEnemyVision.Instance, hold); break;
                    case nameof(SC2Hotkeys.UnitSight):        SetFeature(UnitSight.Instance, hold);   break;
                    case nameof(SC2Hotkeys.ObserverView):     SetFeature(ObserverView.Instance, hold); break;
                    case nameof(SC2Hotkeys.CameraZoom):       SetFeature(CameraZoom.Instance, hold);        break;
                    case nameof(SC2Hotkeys.FastWin):          if (hold != false) FeatureManager.Trigger(FastWin.Instance); break;
                    default:
                        if (hold != false && _panelToggles.TryGetValue(key, out var toggle)) toggle();
                        break;
                }
            }
            catch (Exception ex) { XMLogging.WriteLine($"[Hotkeys] {key}: {ex.Message}"); }
        }

        /// <summary>
        /// Whether the feature an action controls is on right now - read from the feature itself, not from the hotkey's
        /// own press history (a checkbox may have changed it since). Null for one-shot actions (zoom, centre, Fast Win, ...).
        /// </summary>
        public static bool? GetActionEnabledState(string key)
        {
            var c = Program.Config;
            return key switch
            {
                nameof(SC2Hotkeys.ToggleLabels)        => c.ShowLabels,
                nameof(SC2Hotkeys.ToggleCreep)         => c.ShowCreep,
                nameof(SC2Hotkeys.TogglePylonPower)    => c.ShowPowerFields,
                nameof(SC2Hotkeys.ToggleCameras)       => c.ShowCameras,
                nameof(SC2Hotkeys.ToggleDefenceRanges) => c.RangeDefences,
                nameof(SC2Hotkeys.ToggleProgress)      => c.ShowProgress,
                nameof(SC2Hotkeys.TogglePOIs)          => c.ShowPOIs,
                nameof(SC2Hotkeys.MemWritesMaster)     => c.MemWritesEnabled,
                nameof(SC2Hotkeys.RevealMap)           => RevealMap.Instance.Enabled,
                nameof(SC2Hotkeys.ShareEnemyVision)    => ShareEnemyVision.Instance.Enabled,
                nameof(SC2Hotkeys.UnitSight)           => UnitSight.Instance.Enabled,
                nameof(SC2Hotkeys.ObserverView)        => ObserverView.Instance.Enabled,
                nameof(SC2Hotkeys.CameraZoom)          => CameraZoom.Instance.Enabled,
                _                                      => null,
            };
        }

        /// <summary>A memory-write toggle by hotkey: only while the master switch is on, as from its panel.</summary>
        static void SetFeature(IMemWriteFeature f, bool? hold)
        {
            if (!Program.Config.MemWritesEnabled) { if (hold != false) ToastManager.Show(f.Name, "Turn on the memory writes master switch first.", ToastStyle.Warning, 3); return; }
            bool on = hold ?? !f.Enabled;
            if (on == f.Enabled) return;
            FeatureManager.SetEnabled(f, on);
            ToastManager.Show(f.Name, on ? "On" : "Off", on ? ToastStyle.Success : ToastStyle.Info, 2);
        }
    }
}
