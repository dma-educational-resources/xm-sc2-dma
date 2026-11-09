using SC2Radar.DMA.Features;

using SC2Radar.UI.ImGui;
using Ctl = SC2Radar.UI.ImGui.UI;

namespace SC2Radar.SC2.Features.MemoryWrites.Ultrawide
{
    /// <summary>
    /// Ultrawide (port of the sc2-dma tool). Turning it on holds the engine's 16:9 clamp bypass while the user
    /// re-applies the display mode on the game PC (<see cref="WideMode"/>); turning it off puts the console back and
    /// asks the user to change the display mode, which brings the game back to 16:9. While on, "Keep bottom console
    /// framed" refits the console once a second (<see cref="BottomConsole"/>).
    /// <para>
    /// Unlike the other features it works outside matches (the display mode is changed from the menus), so it runs
    /// whenever the display addresses are found (<see cref="DisplayMonitor"/>), and a match ending changes nothing.
    /// </para>
    /// </summary>
    public sealed class Ultrawide : MemWriteFeature<Ultrawide>
    {
        public override string Name        => "Ultrawide";
        public override string Description => "Lift SC2's 16:9 limit and fit the bottom console to the window.";

        static UltrawideSettings Settings => Program.Config.MemWrites.Ultrawide;

        /// <summary>A widen in progress: the bypass is held until the game is wide (or the timeout).</summary>
        sealed record Widening(WideMode.Bypass Original, DateTime Deadline)
        {
            public (uint, uint) LastSize { get; set; }
            public DateTime WideSince { get; set; }
        }

        bool _active;           // Apply ran since the feature was turned on
        Widening? _widening;
        bool _consoleFitted;    // the console holds our fit (put back when Keep console is unticked)
        DateTime _revertUntil;  // turned off while wide: the prompt to change the display mode shows until then

        Ultrawide()
        {
            // a game that is already ultrawide (from an earlier run) starts with the toggle on
            DisplayMonitor.Resolved += () =>
            {
                if (DisplayMonitor.Game is { } g && WideMode.IsWide(g) && !Enabled)
                {
                    Enabled = true;
                    DisplayMonitor.Log("The game is already ultrawide.");
                }
            };
            DisplayMonitor.Start();
        }

        public override bool CanRun => Program.Config.MemWritesEnabled && DisplayMonitor.Game != null;

        // while widening, every tick (it waits for the size to hold for 300 ms); else the console refit, once a second
        protected override TimeSpan Delay => _widening != null ? TimeSpan.Zero : TimeSpan.FromSeconds(1);

        protected override void Apply(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (DisplayMonitor.Game is not { } game) return;
            if (!_active)
            {
                _active = true;
                _revertUntil = default;
                if (WideMode.IsWide(game)) Status = "The game is already ultrawide.";
                else StartWiden(game, writes);
                return;
            }
            if (_widening is { } w) { Widen(game, writes, w); return; }

            Status = DisplayMonitor.Status;
            if (!game.Addr!.Console) return;
            if (Settings.KeepConsole)
            {
                if (BottomConsole.Fix(game, writes) is { } fitted)
                {
                    _consoleFitted = fitted.ScaleX != 1.0;
                    DisplayMonitor.Log(_consoleFitted ? $"Bottom console fitted (middle piece widened {fitted.ScaleX:0.00}x)."
                                                      : "Bottom console put back (the game is 16:9).");
                }
            }
            else if (_consoleFitted)
            {
                // unticking puts the console back right away instead of leaving it stretched
                _consoleFitted = false;
                if (BottomConsole.Fix(game, writes, restore: true) is not null) DisplayMonitor.Log("Bottom console put back to its normal size.");
            }
        }

        void StartWiden(GameMemory game, ScatterWriteHandle writes)
        {
            if (game.ReadU32(game.Addr!.DisplayMode) != WideMode.DisplayWindowed)
                DisplayMonitor.Log("Display Mode is not Windowed (Fullscreen). Set it on the game PC for this to hold.");
            var timeout = TimeSpan.FromSeconds(Settings.TimeoutSec);
            _widening = new Widening(WideMode.QueueBypassOn(game, writes), DateTime.UtcNow + timeout);
            DisplayMonitor.Log($"16:9 limit off. Waiting up to {timeout.TotalSeconds:0} s for the display mode change on the game PC.");
            Prompt("On the game PC: Options > Graphics. Set Display Mode to Windowed and apply, then back to " +
                   $"Windowed (Fullscreen) and apply. Waiting up to {timeout.TotalSeconds:0} s.");
            Status = "Waiting for the display mode change";
        }

        /// <summary>Done once the game is back in Windowed (Fullscreen) at a wide size and has stayed there for 300 ms:
        /// the brief Windowed step in between is not the size to keep.</summary>
        void Widen(GameMemory game, ScatterWriteHandle writes, Widening w)
        {
            bool wide = WideMode.IsWideWindowed(game, out var size);
            if (!wide || size != w.LastSize) { w.LastSize = wide ? size : default; w.WideSince = DateTime.UtcNow; }
            else if (DateTime.UtcNow - w.WideSince >= TimeSpan.FromMilliseconds(300))
            {
                EndWiden(game, writes);
                DisplayMonitor.Log($"Ultrawide on: the game is {size.Width} x {size.Height}.");
                return;
            }
            if (DateTime.UtcNow < w.Deadline) return;
            EndWiden(game, writes);
            var (sw, sh) = WideMode.StoredSize(game);
            DisplayMonitor.Log($"Timed out. The game stays {sw} x {sh}; nothing changed.");
            Enabled = false;
        }

        void EndWiden(GameMemory game, ScatterWriteHandle writes)
        {
            if (_widening is not { } w) return;
            WideMode.QueueBypassOff(game, writes, w.Original);
            _widening = null;
            _prompt = null;
            DisplayMonitor.Log("Client-API flags restored.");
        }

        /// <summary>Turned off (or the master switch): a widen in progress gives the flags back, the console goes back
        /// to normal. Turned off while wide, the user is asked to change the display mode (the game re-applies 16:9).</summary>
        protected override void Revert(SC2Memory mem, ScatterWriteHandle writes)
        {
            _active = false;
            if (DisplayMonitor.Game is not { } game) { Forget(); return; }
            bool wasWidening = _widening != null;
            EndWiden(game, writes);
            if (wasWidening) DisplayMonitor.Log("Cancelled.");
            if (game.Addr!.Console && BottomConsole.Fix(game, writes, restore: true) is not null) DisplayMonitor.Log("Bottom console put back.");
            _consoleFitted = false;
            if (!Enabled && !wasWidening && WideMode.IsWide(game))
            {
                // without the bypass, any display mode change makes the game apply its 16:9 limit again
                var timeout = TimeSpan.FromSeconds(Settings.TimeoutSec);
                _revertUntil = DateTime.UtcNow + timeout;
                Prompt("On the game PC: Options > Graphics, switch Display Mode to Windowed and back to " +
                       $"Windowed (Fullscreen), or restart StarCraft II. Waiting up to {timeout.TotalSeconds:0} s.");
            }
        }

        protected override void Forget()
        {
            _active = false;
            _widening = null;
            _consoleFitted = false;
            _prompt = null;
        }

        // not tied to a match: the display mode outlives it
        public override void OnMatchStart() { }
        public override void OnMatchEnd() { }

        // ── UI ───────────────────────────────────────────────────────────────

        volatile string? _prompt;

        /// <summary>An instruction for the user while the feature waits on them; also a toast, since the cog may be closed.</summary>
        void Prompt(string text)
        {
            _prompt = text;
            ToastManager.Show("Ultrawide", text + " Untick Ultrawide to cancel.", ToastStyle.Warning, Settings.TimeoutSec);
        }

        /// <summary>The current prompt; the revert prompt goes once the game is back to 16:9 (or its time is up).</summary>
        string? CurrentPrompt()
        {
            if (_prompt != null && _widening == null && !Enabled && (!DisplayMonitor.IsWide || DateTime.UtcNow > _revertUntil))
            {
                DisplayMonitor.Log(DisplayMonitor.IsWide
                    ? "The game is still ultrawide. Change the display mode on the game PC, or restart the game."
                    : "Back to 16:9.");
                _prompt = null;
            }
            return _prompt;
        }

        public override Action? DrawSettings => () =>
        {
            bool keep = Settings.KeepConsole;
            if (Ctl.Toggle("Keep bottom console framed", ref keep,
                    hint: "Refit the bottom console once a second, so it stays framed after every mission load."))
            { Settings.KeepConsole = keep; Program.Config.SaveDebounced(); ApplySoon(); }
            int timeout = Settings.TimeoutSec;
            if (Ctl.SliderInt("Wait for display change (s)", ref timeout, 15, 180))
            { Settings.TimeoutSec = timeout; Program.Config.SaveDebounced(); }

            Ctl.KVRow("Window",  DisplayMonitor.Status);
            Ctl.KVRow("Console", DisplayMonitor.ConsoleState);
            Ctl.KVRow("Mission", !DisplayMonitor.MissionDetectAvailable ? "unknown" : DisplayMonitor.InMission ? "in a mission" : "not in a mission");

            if (CurrentPrompt() is { } prompt)
            {
                Ctl.Notice(prompt, NoticeStyle.Warning);
                if (Ctl.Button("Cancel##uw_cancel", style: ButtonStyle.Danger))
                {
                    if (_widening != null) FeatureManager.SetEnabled(this, false);   // the next tick gives the flags back
                    else _prompt = null;
                }
            }
            foreach (var l in DisplayMonitor.RecentLog().Reverse().Take(4))
                Ctl.Caption(l);
        };
    }
}
