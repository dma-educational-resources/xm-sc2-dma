namespace SC2Radar.SC2
{
    /// <summary>
    /// Timings for the performance overlay (Settings ▸ Performance ▸ Performance Overlay) and the performance mode switch.
    /// Each worker thread records its own ticks (realtime positions, misc state, scans, memory writes); the UI thread
    /// records each frame and the radar draw. Single writer per field, read by the UI; a torn read of a double only shows a
    /// wrong number for a frame.
    /// </summary>
    public static class PerfStats
    {
        public const int History = 120;

        // worker threads (SC2Workers): the last tick of each, DMA reads = single reads and scatters
        static readonly float[] _tick = new float[History];
        static int _tickAt;
        public static volatile float RealtimeMs, MiscMs, ScansMs, WritesMs;
        public static volatile int RealtimeReads, MiscReads, ScansReads, WriteRoundTrips;
        static long _ticks;

        // UI thread
        static readonly float[] _frame = new float[History];
        static int _frameAt;
        public static float FrameMs, RadarMs;
        public static int SnapshotsPerSec, Units;
        static long _lastTicks;
        static DateTime _rateAt = DateTime.UtcNow;

        /// <summary>Performance mode is on this frame (Config.PerfMode: always, or Auto with many units).</summary>
        public static bool PerfModeActive { get; private set; }

        /// <summary>Realtime worker, after each position read: a new snapshot (the overlay's graph and snapshots per second).</summary>
        public static void RecordRealtime(double ms, int reads)
        {
            RealtimeMs = (float)ms; RealtimeReads = reads;
            _tick[_tickAt] = (float)ms;
            _tickAt = (_tickAt + 1) % History;
            Interlocked.Increment(ref _ticks);
        }

        /// <summary>Misc worker, after each tick.</summary>
        public static void RecordMisc(double ms, int reads) { MiscMs = (float)ms; MiscReads = reads; }

        /// <summary>Scans worker, after a tick that read.</summary>
        public static void RecordScans(double ms, int reads) { ScansMs = (float)ms; ScansReads = reads; }

        /// <summary>Features worker, after a tick with the master switch on.</summary>
        public static void RecordWrites(double ms, int reads) { WritesMs = (float)ms; WriteRoundTrips = reads; }

        /// <summary>UI thread, once per frame: frame time, radar draw time, units on the map; decides performance mode.</summary>
        public static void RecordFrame(float frameMs, float radarMs, int units)
        {
            FrameMs = frameMs; RadarMs = radarMs; Units = units;
            _frame[_frameAt] = frameMs;
            _frameAt = (_frameAt + 1) % History;
            if ((DateTime.UtcNow - _rateAt).TotalSeconds >= 1)
            {
                long t = Interlocked.Read(ref _ticks);
                SnapshotsPerSec = (int)((t - _lastTicks) / (DateTime.UtcNow - _rateAt).TotalSeconds);
                (_lastTicks, _rateAt) = (t, DateTime.UtcNow);
            }
            var c = Program.Config;
            PerfModeActive = c.PerfMode == 2 || c.PerfMode == 1 && units >= c.PerfAutoUnits;
        }

        /// <summary>One scan's DMA reads and time: the last run, and totals since <see cref="ResetScans"/>.</summary>
        public sealed class ScanStat
        {
            public int    LastReads;
            public double LastMs;
            public long   Runs, TotalReads;
            public double TotalMs;
            public double AvgReads => Runs > 0 ? (double)TotalReads / Runs : 0;
            public double AvgMs    => Runs > 0 ? TotalMs / Runs : 0;
        }

        static readonly ConcurrentDictionary<string, ScanStat> _scans = new();

        /// <summary>Every scan that has read since the last reset (name -> stats), for the Debug panel and --readbench.</summary>
        public static IReadOnlyDictionary<string, ScanStat> Scans => _scans;

        /// <summary>A worker, after a scan that read: how many DMA reads it took and how long.</summary>
        public static void RecordScan(string name, int reads, double ms)
        {
            var s = _scans.GetOrAdd(name, _ => new ScanStat());
            s.LastReads = reads; s.LastMs = ms;
            s.Runs++; s.TotalReads += reads; s.TotalMs += ms;
        }

        public static void ResetScans() => _scans.Clear();

        /// <summary>The last <see cref="History"/> values, oldest first.</summary>
        public static float[] TickHistory  => Ordered(_tick, _tickAt);
        public static float[] FrameHistory => Ordered(_frame, _frameAt);

        static float[] Ordered(float[] ring, int at)
        {
            var o = new float[History];
            for (int i = 0; i < History; i++) o[i] = ring[(at + i) % History];
            return o;
        }

        /// <summary>Position reads (snapshots) since start.</summary>
        public static long Snapshots => Interlocked.Read(ref _ticks);

        public static float TickMsAvg => Avg(_tick);
        public static float FrameMsAvg => Avg(_frame);
        static float Avg(float[] r) { float s = 0; int n = 0; foreach (var v in r) if (v > 0f) { s += v; n++; } return n > 0 ? s / n : 0f; }
    }
}
