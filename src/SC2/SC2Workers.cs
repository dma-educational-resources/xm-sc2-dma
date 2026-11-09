using SC2Radar.SC2.Features;

namespace SC2Radar.SC2
{
    /// <summary>
    /// The radar's worker threads, as the EFT radar's raid workers: each job on its own thread at its own rate, so the
    /// slow reads never hold up unit positions.
    /// <list type="bullet">
    /// <item>SC2-Realtime (highest priority): unit positions and the local camera - <see cref="SC2Memory.RealtimeTick"/>,
    /// every Config.RealtimeIntervalMs.</item>
    /// <item>SC2-Misc: attach, the mission gate, unit details, players, match, cameras, upgrades, creep -
    /// <see cref="SC2Memory.MiscTick"/>, every Config.UnitReadIntervalMs (each reader keeps its own cadence).</item>
    /// <item>SC2-Scans: buffs, inject / chrono, cargo, orders, script values, event feed - <see cref="SC2Memory.ScansTick"/>.</item>
    /// <item>SC2-Features: the memory-write features - <see cref="FeatureManager.Tick"/>, 20 Hz (Camera Zoom eases per tick).</item>
    /// </list>
    /// Loops are fixed-rate: a tick that took part of the interval sleeps only the rest. Performance mode's "half the read
    /// rate" doubles the realtime and misc intervals.
    /// </summary>
    internal sealed class SC2Workers
    {
        readonly SC2Memory _mem;
        readonly Config _cfg;
        readonly Thread[] _threads;
        volatile bool _stop;
        volatile bool _attached;

        public SC2Workers(SC2Memory mem, Config cfg)
        {
            _mem = mem;
            _cfg = cfg;
            _threads =
            [
                new Thread(() => Loop("Realtime", () => Slower(_cfg.RealtimeIntervalMs), () => { if (_attached) _mem.RealtimeTick(); }))
                    { Name = "SC2-Realtime", IsBackground = true, Priority = ThreadPriority.Highest },
                new Thread(() => Loop("Misc", () => Slower(_cfg.UnitReadIntervalMs), () => _attached = _mem.MiscTick()))
                    { Name = "SC2-Misc", IsBackground = true, Priority = ThreadPriority.AboveNormal },
                new Thread(() => Loop("Scans", () => 50, () => { if (_attached) _mem.ScansTick(); }))
                    { Name = "SC2-Scans", IsBackground = true, Priority = ThreadPriority.BelowNormal },
                new Thread(() => Loop("Features", () => 50, () => FeatureManager.Tick(_mem, _attached)))
                    { Name = "SC2-Features", IsBackground = true, Priority = ThreadPriority.Normal },
            ];
        }

        public void Start()
        {
            foreach (var t in _threads) t.Start();
        }

        /// <summary>
        /// Stop every worker and wait (up to <paramref name="waitMs"/> in all) for the reads they have in flight: the DMA
        /// handle must not be closed under a running read - the FPGA driver can hang the process on exit.
        /// </summary>
        public void Stop(int waitMs = 3000)
        {
            _stop = true;
            var until = Environment.TickCount64 + waitMs;
            foreach (var t in _threads)
                if (t.IsAlive && t != Thread.CurrentThread)
                    t.Join((int)Math.Max(0, until - Environment.TickCount64));
        }

        /// <summary>The interval, doubled while performance mode halves the read rate (Settings ▸ Performance).</summary>
        int Slower(int ms) => PerfStats.PerfModeActive && _cfg.PerfSlowerReads ? ms * 2 : ms;

        void Loop(string name, Func<int> intervalMs, Action tick)
        {
            long next = Environment.TickCount64;
            while (!_stop)
            {
                try { tick(); }
                catch (Exception ex) { XMLogging.WriteLine($"[{name}] tick error: {ex.Message}"); }
                int interval = Math.Max(1, intervalMs());
                next += interval;
                long now = Environment.TickCount64;
                // a tick longer than the interval: carry on from now (rather than racing to catch up), after a short yield so
                // the other workers get their share of the DMA link
                if (next <= now) { next = now; Thread.Sleep(1); }
                else Thread.Sleep((int)(next - now));
            }
        }
    }
}
