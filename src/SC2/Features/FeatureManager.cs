using SC2Radar.DMA.Features;
using SC2Radar.DMA.ScatterAPI;
using SC2Radar.SC2.Features.MemoryWrites;
using SC2Radar.SC2.Features.MemoryWrites.Ultrawide;

namespace SC2Radar.SC2.Features
{
    /// <summary>
    /// Drives every memory-write feature the same way (the EFT radar's FeatureManager): once per memory tick, after the
    /// reads, each feature that <see cref="IFeature.CanRun"/> queues its writes into one shared
    /// <see cref="ScatterWriteHandle"/>, executed as a single scatter write. Also delivers the game / match lifecycle and
    /// owns the master switch and the toggles, so turning things off always puts the game back the same way.
    /// Everything runs under one lock: features need no locking of their own.
    /// </summary>
    internal static class FeatureManager
    {
        /// <summary>Every memory-write feature, in panel order.</summary>
        public static IReadOnlyList<IMemWriteFeature> All { get; } =
        [
            Ultrawide.Instance, CameraZoom.Instance,
            RevealMap.Instance, ShareEnemyVision.Instance, UnitSight.Instance, ObserverView.Instance,
            FastWin.Instance,
        ];

        static readonly object _sync = new();
        static ScatterWriteHandle? _writes;
        static uint _writesPid;
        static uint _gamePid;
        static bool _matchLive;

        /// <summary>This tick's <see cref="SC2Memory.MemWritesAllowed"/> (a live match with a human in it), worked out once
        /// per tick for every feature's <see cref="IFeature.CanRun"/>.</summary>
        public static bool MatchWritable { get; private set; }

        /// <summary>Features worker (SC2Workers), 20 Hz: lifecycle events, then one scatter write with every feature's writes.</summary>
        public static void Tick(SC2Memory mem, bool attached)
        {
            lock (_sync)
            {
                uint pid = attached ? mem.Pid : 0;
                if (pid != _gamePid)
                {
                    if (_matchLive) { _matchLive = false; Notify(f => f.OnMatchEnd()); }
                    if (_gamePid != 0) Notify(f => f.OnGameStop());
                    _gamePid = pid;
                    if (pid != 0) Notify(f => f.OnGameStart());
                }
                if (pid == 0) return;

                bool live = mem.MatchLive;
                if (live != _matchLive)
                {
                    _matchLive = live;
                    Notify(live ? f => f.OnMatchStart() : f => f.OnMatchEnd());
                }

                if (!Program.Config.MemWritesEnabled) { MatchWritable = false; return; }
                long t0 = Stopwatch.GetTimestamp();
                int rt0 = SC2Memory.RoundTrips;
                // once per tick: it reads every player's type, and every feature's CanRun asks
                MatchWritable = live && mem.MemWritesAllowed;
                var writes = Writes(mem);
                foreach (var f in All)
                {
                    if (!f.CanRun) continue;
                    try
                    {
                        f.TryApply(mem, writes);
                        f.OnApply();
                    }
                    catch (Exception ex) { XMLogging.WriteLine($"[MemWrites] {f.Name}: {ex.Message}"); }
                }
                Execute(mem, writes);
                PerfStats.RecordWrites(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, SC2Memory.RoundTrips - rt0);
            }
        }

        /// <summary>
        /// Turn one feature on or off (panel toggle, hotkey). Off takes effect on the next tick, when the feature puts
        /// back what it changed. Returns false while the master switch is off.
        /// </summary>
        public static bool SetEnabled(IMemWriteFeature f, bool on)
        {
            if (!Program.Config.MemWritesEnabled) return false;
            lock (_sync) f.Enabled = on;
            return true;
        }

        /// <summary>Press an action feature's button (Fast Win). Ignored while the master switch is off.</summary>
        public static void Trigger(IMemWriteFeature f)
        {
            if (!Program.Config.MemWritesEnabled) return;
            lock (_sync) f.Trigger();
        }

        /// <summary>
        /// The master switch. Turning it off first puts back everything the features changed, in one write while
        /// writes are still allowed; nothing is written after that.
        /// </summary>
        public static void SetMasterSwitch(bool on)
        {
            lock (_sync)
            {
                if (!on && Program.Config.MemWritesEnabled && Memory.Instance is { IsConnected: true } mem)
                {
                    var writes = Writes(mem);
                    foreach (var f in All)
                    {
                        try { f.Restore(mem, writes); }
                        catch (Exception ex) { XMLogging.WriteLine($"[MemWrites] {f.Name} restore: {ex.Message}"); }
                    }
                    Execute(mem, writes);
                }
                Program.Config.MemWritesEnabled = on;
            }
            Program.Config.SaveDebounced();
        }

        static void Notify(Action<IMemWriteFeature> e)
        {
            foreach (var f in All)
            {
                try { e(f); }
                catch (Exception ex) { XMLogging.WriteLine($"[MemWrites] {f.Name}: {ex.Message}"); }
            }
        }

        /// <summary>The shared write handle, cleared; a new one per game process (a scatter handle is bound to a pid).</summary>
        static ScatterWriteHandle Writes(SC2Memory mem)
        {
            if (_writes == null || _writesPid != mem.Pid)
            {
                _writes?.Dispose();
                _writes = new ScatterWriteHandle(mem);
                _writesPid = mem.Pid;
            }
            _writes.Clear();
            return _writes;
        }

        static void Execute(SC2Memory mem, ScatterWriteHandle writes)
        {
            try { writes.Execute(() => mem.IsConnected); }
            catch (Exception ex) { XMLogging.WriteLine($"[MemWrites] scatter write: {ex.Message}"); }
        }
    }
}
