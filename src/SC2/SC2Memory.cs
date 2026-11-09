using System.Buffers.Binary;
using System.IO;
using VmmSharpEx;
using VmmSharpEx.Options;
using VmmSharpEx.Scatter;
using SC2Radar.SC2.Features;
using SC2Radar.SC2.Features.MemoryWrites;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Manages the DMA connection and SC2 unit reading.
    /// Runs on a dedicated background thread; publishes snapshots the render thread reads.
    /// </summary>
    public sealed partial class SC2Memory : IDisposable
    {
        // ── VMM connection ─────────────────────────────────────────────────────
        readonly Vmm   _vmm;
        /// <summary>The DMA handle - also used by InputManager to read the gaming PC's keyboard (hotkeys).</summary>
        internal Vmm Vmm => _vmm;
        uint           _pid;
        ulong          _exeBase, _exeEnd;

        // ── Per-session state (set after successful attach) ────────────────────
        ulong[]        _knownUnitAddrs = [];

        // ── Published snapshot (replaced atomically by the worker) ────────────
        public UnitSnapshot Snapshot { get; private set; } = UnitSnapshot.Empty;

        public bool  IsConnected { get; private set; }
        public bool  InGame      { get; private set; }

        // Sampled unit addresses from the last tick - used by the Research panel.
        public IReadOnlyList<ulong> KnownUnitAddresses => _knownUnitAddrs;

        // Raw vtable-match count before XY filtering (diagnostic: compare to Snapshot.Units.Length).
        public volatile int VtableSlotCount;

        // Diagnostic output paths (null = not yet generated).
        public string? LastSweepFile { get; private set; }

        // Prevent repeated auto-sweep fires per session.
        private volatile bool _autoSweptThisSession = false;

        readonly Config _cfg;

        const int PAGE    = 0x1000;
        const int STRIDE  = 0xB60;
        const int XY_OFF  = 0x100; // unit position, for validating candidates of the vtable heap scan

        // ── Ctor / Dispose ────────────────────────────────────────────────────

        public SC2Memory(Config cfg)
        {
            _cfg = cfg;
            _vmm = new Vmm("-norefresh", "-device", string.IsNullOrWhiteSpace(cfg.DeviceStr) ? "fpga" : cfg.DeviceStr);
        }

        public void Dispose() => _vmm.Dispose();

        // ── Public API ────────────────────────────────────────────────────────

        // The VMM runs with -norefresh, so its page-table (TLB) cache is only refreshed when asked. NOCACHE reads
        // skip the data cache but still translate through those page tables: once the game rebuilds its heap (a new
        // match) stale translations make reads fail or return another page. Partial refresh every couple of seconds,
        // full refresh on a mission change or when reads look stale.
        DateTime _nextTlbRefresh = DateTime.MinValue;

        void RefreshTranslations(bool full)
        {
            try
            {
                if (full) _vmm.ForceFullRefresh();
                else _vmm.ConfigSet(VmmSharpEx.Options.VmmOption.REFRESH_FREQ_TLB_PARTIAL, 1);
            }
            catch { }
            _nextTlbRefresh = DateTime.UtcNow.AddSeconds(2);
        }

        // ── Worker ticks ──────────────────────────────────────────────────────
        // The radar reads on several threads, as the EFT radar's raid workers do (SC2Workers): Realtime (unit positions
        // and the local camera, as fast as the link allows), Misc (attach, the mission gate, unit details, players, match,
        // cameras, ... at their own cadences), Scans (buffs, cargo, orders, script values, event feed) and the memory-write
        // features. Each piece of state has one writer thread; what other threads read is swapped in whole.

        /// <summary>Misc worker: attach, the mission gate and every per-cadence reader except positions. False when not
        /// attached to SC2.</summary>
        public bool MiscTick()
        {
            if (DateTime.UtcNow >= _nextTlbRefresh) RefreshTranslations(full: false);
            if (!TryAttach()) return false;
            long t0 = Stopwatch.GetTimestamp();
            int rt0 = _roundTrips;
            try
            {
                Scan("Globals", PrefetchGlobals, rethrow: true);
                Scan("State", ReadState, rethrow: true);
            }
            finally { t_globals = null; }   // other threads and the next tick read globals fresh
            PerfStats.RecordMisc(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, _roundTrips - rt0);
            return true;
        }

        /// <summary>Realtime worker: every unit's position (one batched read) and the local camera, published as a new
        /// <see cref="Snapshot"/>.</summary>
        public void RealtimeTick()
        {
            t_realtime = true;
            if (!IsConnected || !InGame) return;
            long t0 = Stopwatch.GetTimestamp();
            int rt0 = _roundTrips;
            Scan("Cameras", QueueCameras, rethrow: true);   // read along with the positions
            Scan("Positions", ReadPositions, rethrow: true);
            Scan("Cameras", ReadCameras, rethrow: true);
            PerfStats.RecordRealtime(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, _roundTrips - rt0);
        }

        /// <summary>Scans worker: the slower per-unit scans, each at its own cadence.</summary>
        public void ScansTick()
        {
            if (!IsConnected || !InGame) return;
            long t0 = Stopwatch.GetTimestamp();
            int rt0 = _roundTrips;
            // a new mission (seen by the misc worker): this thread's own state starts over
            if (_scanGen != _scanGenSeen) { _scanGenSeen = _scanGen; ResetScript(); ResetEventFeed(); }
            try
            {
                Scan("Scans/Globals", PrefetchGlobals, rethrow: true);
                if (_cfg.ShowLarvaInject) Scan("Buffs", ScanBuffs);
                if (_cfg.TrackCargo)      Scan("Cargo", ScanCargo);
                if (_cfg.TrackOrders)     Scan("Orders", ScanOrders);
                if (_cfg.TrackBuffs)      Scan("UnitBuffs", ScanUnitBuffs);
                if (_cfg.TrackScript)     Scan("Script", ScanScript);
                if (_cfg.TrackEvents)     Scan("Events", ScanEventFeed);
            }
            finally { t_globals = null; }
            if (_roundTrips != rt0) PerfStats.RecordScans(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, _roundTrips - rt0);
        }

        volatile int _scanGen;   // bumped by the misc worker on a mission change
        int _scanGenSeen;

        /// <summary>One pass of every worker's tick in turn, on the calling thread.</summary>
        public void Tick()
        {
            bool attached = MiscTick();
            if (attached) { RealtimeTick(); ScansTick(); }
            FeatureManager.Tick(this, attached);
        }

        /// <summary>DMA reads issued on this thread: one per single read, one per scatter (PerfStats counts them per scan).</summary>
        [ThreadStatic] static int _roundTrips;

        /// <summary>DMA reads issued so far on the calling thread (the features worker times its own ticks with it).</summary>
        internal static int RoundTrips => _roundTrips;

        /// <summary>Run one of the tick's scans, recording its time and DMA reads; a failing scan is logged, not fatal.</summary>
        void Scan(string name, Action scan, bool rethrow = false)
        {
            int rt = _roundTrips;
            long t = Stopwatch.GetTimestamp();
            try { scan(); }
            catch (Exception ex) when (!rethrow) { XMLogging.WriteLineFileOnly($"[{name}] {ex.Message}"); }
            finally
            {
                // most scans run every few ticks and return straight away in between: only count the runs that read
                if (_roundTrips != rt) PerfStats.RecordScan(name, _roundTrips - rt, Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
        }

        /// <summary>A scatter handle on the game process (scatter reads, and the memory-write features' batched writes).</summary>
        internal VmmScatter CreateScatter(VmmFlags flags) => new(_vmm, _pid, flags);

        /// <summary>Write bytes to SC2 process memory. Returns true on success.</summary>
        public bool MemWrite(ulong addr, byte[] data)
        {
            try { return _vmm.MemWriteArray<byte>(_pid, addr, data); }
            catch { return false; }
        }

        /// <summary>Write a value now, or queue it into <paramref name="writes"/> (the memory-write features' batch).</summary>
        bool Write<T>(ulong addr, T value, ScatterWriteHandle? writes) where T : unmanaged
        {
            if (writes == null) return MemWrite(addr, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray());
            writes.AddValueEntry(addr, value);
            return true;
        }

        /// <summary>Write bytes now, or queue them into <paramref name="writes"/>.</summary>
        bool Write(ulong addr, byte[] data, ScatterWriteHandle? writes)
        {
            if (writes == null) return MemWrite(addr, data);
            writes.AddBufferEntry<byte>(addr, data);
            return true;
        }

        /// <summary>Read raw bytes from SC2 process memory. Returns null on failure.</summary>
        public byte[]? ReadBytes(ulong addr, int count)
        {
            try { _roundTrips++; return _vmm.MemRead(_pid, addr, (uint)count, out _, VmmFlags.NOCACHE); }
            catch { return null; }
        }

        // ── Attach ────────────────────────────────────────────────────────────

        bool TryAttach()
        {
            if (IsConnected && InGame) return true;

            // Find SC2 process
            var procs = _vmm.ProcessGetInformationAll();
            var sc2   = procs?.FirstOrDefault(p =>
                string.Equals(p.sNameLong, "SC2_x64.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.sNameLong, "SC2.exe",     StringComparison.OrdinalIgnoreCase));
            var sc2v = sc2.HasValue && sc2.Value.fValid ? sc2 : null;
            if (sc2v is null) { IsConnected = false; InGame = false; return false; }

            if (_pid != sc2v.Value.dwPID) { _actors = []; _codePages.Clear(); lock (_modelCache) _modelCache.Clear(); _nextRescan = DateTime.MinValue; _slotCacheReset = DateTime.MinValue; _offsetsResolved = false; }
            _pid = sc2v.Value.dwPID;
            IsConnected = true;

            // Get exe base
            var mods = _vmm.Map_GetModule(_pid);
            var mod  = mods?.FirstOrDefault(m =>
                m.sText.Contains("SC2_x64", StringComparison.OrdinalIgnoreCase) ||
                m.sText.Contains("SC2.exe",  StringComparison.OrdinalIgnoreCase));
            if (mod is null) { InGame = false; return false; }

            _exeBase = mod.Value.vaBase;
            _exeEnd  = mod.Value.vaBase + mod.Value.cbImageSize;
            InGame   = true;

            return true;
        }

        /// <summary>In a running match: the clock has started, units are loaded and the local player is known.</summary>
        public bool MatchLive => InGame && GameTime > 0f && Snapshot.Units.Length > 0 && LocalPlayer is >= 1 and <= 15;

        /// <summary>Players 1..15 whose type is user (a person, not an AI or the map's own players).</summary>
        public int HumanPlayers
        {
            get
            {
                // every player's type in one batched read
                ReadStructs<byte>([.. Enumerable.Range(1, 15).Where(p => _playerObjs[p] != 0).Select(p => _playerObjs[p] + PlayerTypeOff)],
                                  out var types, out var ok);
                int n = 0;
                for (int i = 0; i < types.Length; i++) if (ok[i] && types[i] == PlayerTypeUser) n++;
                return n;
            }
        }

        /// <summary>Memory writes only run in a live match with at least one human in it (Fast Win turns the local player
        /// into a spectator while it ends the match, so that run counts too) - never on a menu or loading screen.</summary>
        public bool MemWritesAllowed => MatchLive && (HumanPlayers >= 1 || FastWin.Instance.Holding);

        // ── Diagnostic / offset sweep ─────────────────────────────────────────

        /// <summary>
        /// Called on background thread when VtableSlotCount >> Coords.
        /// Reads up to 20 sampled unit structs and sweeps every 4-byte offset 0..511,
        /// reporting how many units have plausible float or int32 coordinates at each offset.
        /// Output is written to <paramref name="filePath"/> (typically %TEMP%\sc2_xy_sweep.txt).
        /// </summary>
        void RunOffsetSweep(IReadOnlyList<ulong> addrs, string filePath)
        {
            try
            {
                const int ScanBytes = 512;
                int n = Math.Min(addrs.Count, 20);
                var loaded = new List<(ulong Addr, byte[] Data)>(n);
                for (int i = 0; i < n; i++)
                {
                    var b = ReadBytes(addrs[i], ScanBytes);
                    if (b != null && b.Length >= ScanBytes)
                        loaded.Add((addrs[i], b));
                }
                if (loaded.Count < 2) { LastSweepFile = "sweep: not enough readable unit addresses"; return; }

                int offCount = ScanBytes / 4;
                using var sw = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
                sw.WriteLine("=== SC2 XY Offset Sweep ===");
                sw.WriteLine($"Generated   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine($"VtableHits  : {VtableSlotCount}  Coords (last tick): {Snapshot.Units.Length}");
                sw.WriteLine($"Units read  : {loaded.Count} (first {ScanBytes} bytes each)");
                sw.WriteLine();

                // ── Offset sweep summary ──────────────────────────────────────
                sw.WriteLine("Offsets where >= 2 units have plausible values:");
                sw.WriteLine($"{"Off",-6} | {"FltHits",-7} | {"Float range",-15} | {"I32Hits",-7} | Int32 range");
                sw.WriteLine(new string('-', 68));

                var fTop = new List<(int Off, int Hits, float Min, float Max)>();
                var iTop = new List<(int Off, int Hits, int Min, int Max)>();

                for (int ri = 0; ri < offCount; ri++)
                {
                    int off = ri * 4;
                    int fH = 0; float fMin = float.MaxValue, fMax = float.MinValue;
                    int iH = 0; int   iMin = int.MaxValue,   iMax = int.MinValue;

                    foreach (var (_, data) in loaded)
                    {
                        float f = BitConverter.ToSingle(data, off);
                        if (float.IsFinite(f) && f > 0f && f < 512f)
                        { fH++; fMin = Math.Min(fMin, f); fMax = Math.Max(fMax, f); }

                        int iv = BitConverter.ToInt32(data, off);
                        if (iv > 0 && iv < 0x40000)
                        { iH++; iMin = Math.Min(iMin, iv); iMax = Math.Max(iMax, iv); }
                    }

                    if (fH >= 2 || iH >= 2)
                    {
                        string fRng = fH > 0 ? $"{fMin:F1}-{fMax:F1}" : "-";
                        string iRng = iH > 0 ? $"{iMin}-{iMax}" : "-";
                        sw.WriteLine($"+0x{off:X3}   | {fH,7} | {fRng,-15} | {iH,7} | {iRng}");
                        if (fH >= loaded.Count / 2) fTop.Add((off, fH, fMin, fMax));
                        if (iH >= loaded.Count / 2) iTop.Add((off, iH, iMin, iMax));
                    }
                }

                sw.WriteLine();
                sw.WriteLine("=== Best Float Candidates (>= half of units) ===");
                if (fTop.Count == 0) sw.WriteLine("  (none)");
                foreach (var c in fTop.OrderByDescending(x => x.Hits))
                    sw.WriteLine($"  +0x{c.Off:X3}  hits={c.Hits}/{loaded.Count}  range=[{c.Min:F2}, {c.Max:F2}]");

                sw.WriteLine();
                sw.WriteLine("=== Best Int32 Candidates (>= half of units) ===");
                if (iTop.Count == 0) sw.WriteLine("  (none)");
                foreach (var c in iTop.OrderByDescending(x => x.Hits))
                    sw.WriteLine($"  +0x{c.Off:X3}  hits={c.Hits}/{loaded.Count}  range=[{c.Min}, {c.Max}]");

                sw.WriteLine();
                sw.WriteLine("=== Raw Hex Dump (all units, all offsets) ===");
                for (int u = 0; u < loaded.Count; u++)
                {
                    var (addr, data) = loaded[u];
                    sw.WriteLine($"--- Unit {u} @ 0x{addr:X16} ---");
                    sw.WriteLine($"{"Off",-6}  {"Hex bytes",-11}  {"Float",13}  {"UInt32",11}  {"Int32",11}");
                    for (int ri = 0; ri < offCount; ri++)
                    {
                        int   off = ri * 4;
                        float f   = BitConverter.ToSingle(data, off);
                        uint  u32 = BitConverter.ToUInt32(data, off);
                        int   i32 = BitConverter.ToInt32(data, off);
                        string hex = $"{data[off]:X2}{data[off+1]:X2}{data[off+2]:X2}{data[off+3]:X2}";
                        string fStr = float.IsFinite(f) ? $"{f,13:F4}" : $"{(float.IsNaN(f) ? "NaN" : "Inf"),13}";
                        sw.WriteLine($"+0x{off:X3}  {hex}  {fStr}  {u32,11}  {i32,11}");
                    }
                    sw.WriteLine();
                }

                LastSweepFile = filePath;
            }
            catch (Exception ex)
            {
                LastSweepFile = $"sweep error: {ex.Message}";
            }
        }

        /// <summary>
        /// Manually dump raw bytes from all currently-known unit addresses to a file.
        /// Called from the Research panel "Dump Raw" button.
        /// Returns the output file path on success, or an error message.
        /// </summary>
        public string DumpRawUnitsToFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "sc2_unit_raw_dump.txt");
            RunOffsetSweep(_knownUnitAddrs, path);
            return LastSweepFile ?? path;
        }

        // ── Low-level VMM helpers ─────────────────────────────────────────────

        // Exe globals, one scatter per tick. The readers decode many pointers from values in the exe's data section (a
        // value and its key, a selector, a case table entry), a read each. On the misc and scans workers every global
        // read during the last few seconds is read again, all together, at the start of that worker's next tick: a
        // tick's globals cost one round trip, and the values that belong together are read at the same moment.
        [ThreadStatic] static Dictionary<ulong, ulong>? t_globals;   // exe rva -> the 8 bytes there, during one tick
        // per thread (the misc and scans workers each prefetch their own): rva -> the tick it was last read in
        [ThreadStatic] static Dictionary<ulong, long>? t_globalRvas;
        [ThreadStatic] static long t_tickNo;
        [ThreadStatic] static uint t_globalsPid;
        Dictionary<ulong, long> _globalRvas => t_globalRvas ??= [];
        long _tickNo { get => t_tickNo; set => t_tickNo = value; }

        void PrefetchGlobals()
        {
            if (t_globalsPid != _pid) { _globalRvas.Clear(); t_globalsPid = _pid; }   // another game process: other rvas
            var cache = new Dictionary<ulong, ulong>(_globalRvas.Count);
            if (_globalRvas.Count > 0)
            {
                // forget globals no reader has wanted for a few seconds (40 ticks at 20 Hz)
                foreach (var (rva, last) in _globalRvas) if (_tickNo - last > 40) _globalRvas.Remove(rva);
                var rvas = _globalRvas.Keys.ToArray();
                if (ReadStructs<ulong>([.. rvas.Select(r => _exeBase + r)], out var values, out var ok))
                    for (int i = 0; i < rvas.Length; i++) if (ok[i]) cache[rvas[i]] = values[i];
            }
            t_globals = cache;
            _tickNo++;
        }

        /// <summary>The 8 bytes at exe + <paramref name="rva"/>: this tick's prefetched value on the misc and scans workers, else
        /// read now. Null when unreadable.</summary>
        ulong? GlobalAt(ulong rva)
        {
            var cache = t_globals;
            if (cache != null)
            {
                _globalRvas[rva] = _tickNo;
                if (cache.TryGetValue(rva, out ulong v)) return v;
            }
            ulong? read = ReadBytes(_exeBase + rva, 8) is { Length: 8 } b ? BinaryPrimitives.ReadUInt64LittleEndian(b)
                        : ReadBytes(_exeBase + rva, 4) is { Length: 4 } b4 ? BinaryPrimitives.ReadUInt32LittleEndian(b4) : null;   // the last bytes of a section
            if (cache != null && read is { } r) cache[rva] = r;
            return read;
        }

        uint  U32At(ulong rva) => GlobalAt(rva) is { } v ? (uint)v : 0;
        ulong U64At(ulong rva) => GlobalAt(rva) ?? 0;

        /// <summary>Up to 8 bytes for MiniEmu: a global of the exe through <see cref="GlobalAt"/>, anything else read now.</summary>
        byte[]? ReadData(ulong addr, int size)
        {
            if (t_globals != null && size is > 0 and <= 8 && addr >= _exeBase && addr + (ulong)size <= _exeEnd && GlobalAt(addr - _exeBase) is { } v)
                return BitConverter.GetBytes(v)[..size];
            return ReadBytes(addr, size);
        }

        // Code pages of the exe, for MiniEmu: read once per game process (the image's code doesn't change).
        readonly ConcurrentDictionary<ulong, byte[]> _codePages = new();

        /// <summary>The 0x2000 bytes from a page of exe code (the page and the next, for instructions crossing it), or null.</summary>
        byte[]? CodePage(ulong page)
        {
            if (_codePages.TryGetValue(page, out var bytes)) return bytes;
            bytes = ReadBytes(page, 0x2000);
            if (bytes != null && page >= _exeBase && page < _exeEnd) _codePages[page] = bytes;
            return bytes;
        }

        ulong ReadU64(ulong va)
        {
            try
            {
                _roundTrips++;
                var buf = _vmm.MemRead(_pid, va, 8, out _, VmmFlags.NOCACHE);
                return buf is { Length: >= 8 } ? BinaryPrimitives.ReadUInt64LittleEndian(buf) : 0;
            }
            catch { return 0; }
        }

        void ReadRange(ulong va, Span<byte> buf, Span<bool> ok)
        {
            int n = buf.Length / PAGE;
            var pages = new ulong[n];
            for (int i = 0; i < n; i++) pages[i] = va + (ulong)i * PAGE;
            ReadPages(pages, buf, ok);
        }

        // The device does one scatter at a time, so a big batch from a background worker would hold the realtime read up
        // for as long as it takes: background batches go in chunks the realtime read can slip in between.
        [ThreadStatic] static bool t_realtime;
        const int RealtimeBatchPages = 1024, BackgroundBatchPages = 128;
        int BatchPages => t_realtime ? RealtimeBatchPages : BackgroundBatchPages;

        /// <summary>
        /// One scatter read of whole pages. A batch that comes back with no page read at all failed as a whole (the device
        /// or a page-table rebuild, during loading and mission changes) and is read once more, as the EFT radar does;
        /// pages that are simply unmapped fail on their own and aren't retried.
        /// </summary>
        VmmScatterRawResult ReadRaw(ReadOnlySpan<ulong> pages)
        {
            for (int attempt = 0; ; attempt++)
            {
                _roundTrips++;
                var r = _vmm.MemReadScatterRaw(_pid, VmmFlags.NOCACHE, pages);
                if (attempt == 1 || pages.Length == 0) return r;
                for (int i = 0; i < r.Count; i++)
                    if (r.TryGet(i, out _, out bool success, out _, out _) && success) return r;
                r.Dispose();
            }
        }

        /// <summary>Execute a prepared VmmScatter, again once if it fails as a whole.</summary>
        void Execute(VmmScatter scatter)
        {
            _roundTrips++;
            try { scatter.Execute(); }
            catch (VmmException) { _roundTrips++; scatter.Execute(); }
        }

        unsafe void ReadPages(ReadOnlySpan<ulong> pages, Span<byte> buf, Span<bool> ok)
        {
            int batch = BatchPages;
            for (int s = 0; s < pages.Length; s += batch)
            {
                int n = Math.Min(batch, pages.Length - s);
                using var r = ReadRaw(pages.Slice(s, n));
                for (int i = 0; i < n; i++)
                {
                    var dst = buf.Slice((s + i) * PAGE, PAGE);
                    if (r.TryGet(i, out _, out bool success, out IntPtr pb, out uint cb)
                        && success && cb >= PAGE)
                    {
                        new ReadOnlySpan<byte>((void*)pb, PAGE).CopyTo(dst);
                        ok[s + i] = true;
                    }
                    else { dst.Clear(); ok[s + i] = false; }
                }
            }
        }

        /// <summary>Addresses that can be game memory. The batched reads skip the others (zero placeholders, half-read
        /// pointers) - their entries fail without adding a page to the read.</summary>
        static bool IsReadable(ulong a) => a >= 0x10000 && a < 0x7FFFFFFFFFFF;

        /// <summary>
        /// The pages of one batched read, as the driver returned them: values are copied straight out of the driver's
        /// buffers (the EFT radar's ScatterPageResults), with no managed copy of the pages - a big batch used to allocate
        /// one large-object-heap array per read, and the radar ran a full garbage collection every few reads. One per
        /// thread, reused; valid until disposed.
        /// </summary>
        sealed class PageSet : IDisposable
        {
            [ThreadStatic] static PageSet? t_set;

            readonly List<ulong> _wanted = [];
            readonly HashSet<ulong> _seen = [];
            readonly Dictionary<ulong, (IntPtr Pb, uint Cb)> _pages = [];
            readonly List<VmmScatterRawResult> _results = [];
            bool _inUse;

            public static PageSet Rent()
            {
                var s = t_set ??= new PageSet();
                if (s._inUse) s = new PageSet();   // nested on this thread: a set of its own
                s._inUse = true;
                return s;
            }

            /// <summary>Read the pages <paramref name="size"/> bytes at <paramref name="addr"/> span (unreadable addresses: none).</summary>
            public void Add(ulong addr, int size)
            {
                if (size <= 0 || !IsReadable(addr)) return;
                for (ulong p = addr & ~(ulong)(PAGE - 1); p <= ((addr + (ulong)size - 1) & ~(ulong)(PAGE - 1)); p += PAGE)
                    if (_seen.Add(p)) _wanted.Add(p);
            }

            /// <summary>Read every page added, in batches the size the calling thread uses.</summary>
            public void Fetch(SC2Memory mem)
            {
                var wanted = CollectionsMarshal.AsSpan(_wanted);
                int batch = mem.BatchPages;
                for (int s = 0; s < wanted.Length; s += batch)
                {
                    var r = mem.ReadRaw(wanted.Slice(s, Math.Min(batch, wanted.Length - s)));
                    _results.Add(r);
                    for (int i = 0; i < r.Count; i++)
                        if (r.TryGet(i, out ulong va, out bool success, out IntPtr pb, out uint cb) && success)
                            _pages[va] = (pb, cb);
                }
            }

            /// <summary>Copy dst.Length bytes at <paramref name="addr"/> out of the pages read; false (dst cleared) when a
            /// page of it wasn't read.</summary>
            public unsafe bool Copy(ulong addr, Span<byte> dst)
            {
                if (!IsReadable(addr)) { dst.Clear(); return false; }
                for (int done = 0; done < dst.Length;)
                {
                    ulong va = addr + (ulong)done;
                    int off = (int)(va & (PAGE - 1)), n = Math.Min(dst.Length - done, PAGE - off);
                    if (!_pages.TryGetValue(va & ~(ulong)(PAGE - 1), out var page) || off + n > (int)page.Cb) { dst.Clear(); return false; }
                    new ReadOnlySpan<byte>((byte*)page.Pb + off, n).CopyTo(dst[done..]);
                    done += n;
                }
                return true;
            }

            public void Dispose()
            {
                foreach (var r in _results) r.Dispose();
                _results.Clear();
                _pages.Clear();
                _wanted.Clear();
                _seen.Clear();
                _inUse = false;
            }
        }

        /// <summary>
        /// One batched read of a <typeparamref name="T"/> at every address: a struct laid out like the game's
        /// (explicit field offsets), so callers read fields instead of parsing offsets. False when the read failed as a
        /// whole; <paramref name="ok"/> says which entries were readable (the others are zero).
        /// </summary>
        unsafe bool ReadStructs<T>(ReadOnlySpan<ulong> addrs, out T[] values, out bool[] ok) where T : unmanaged
        {
            values = new T[addrs.Length];
            ok = new bool[addrs.Length];
            return addrs.Length == 0 || ReadValues(addrs, sizeof(T), MemoryMarshal.AsBytes(values.AsSpan()), ok);
        }

        /// <summary>One batched read of a pointer at every address; 0 where unreadable. The values aren't checked: some
        /// callers read packed links this way (Weapons uses their high bits).</summary>
        ulong[] ReadPointers(ReadOnlySpan<ulong> addrs)
        {
            ReadStructs<ulong>(addrs, out var values, out var ok);
            for (int i = 0; i < values.Length; i++) if (!ok[i]) values[i] = 0;
            return values;
        }

        /// <summary>One batched read of ranges of different sizes; null where a page of the range was unreadable.</summary>
        byte[]?[] ReadRanges(ReadOnlySpan<(ulong Addr, int Size)> ranges)
        {
            var result = new byte[]?[ranges.Length];
            using var set = PageSet.Rent();
            foreach (var (a, size) in ranges) set.Add(a, size);
            try { set.Fetch(this); }
            catch { return result; }
            for (int i = 0; i < ranges.Length; i++)
            {
                var (a, size) = ranges[i];
                if (size <= 0) { result[i] = size == 0 ? [] : null; continue; }
                var dst = new byte[size];
                result[i] = set.Copy(a, dst) ? dst : null;
            }
            return result;
        }

        /// <summary>
        /// One batched (scatter) read of <paramref name="size"/> bytes at every address: each page involved is read once,
        /// in one DMA round trip per batch of pages.
        /// </summary>
        bool ReadValues(ReadOnlySpan<ulong> addrs, int size, Span<byte> values, Span<bool> ok)
        {
            try
            {
                using var set = PageSet.Rent();
                foreach (ulong a in addrs) set.Add(a, size);
                set.Fetch(this);
                for (int i = 0; i < addrs.Length; i++)
                    ok[i] = set.Copy(addrs[i], values.Slice(i * size, size));
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>One radar object (a render actor).</summary>
    /// <param name="Color">ImGui ABGR team colour (grey = neutral).</param>
    /// <param name="Name">Model file name without suffixes, e.g. "marine".</param>
    /// <param name="Path">Full model path, e.g. assets\units\terran\marine\marine.m3.</param>
    /// <param name="Addr">Actor address in the game process.</param>
    /// <param name="Burrowed">Actor state is "Burrow" (see SC2Memory.ActorReader ReadBurrowStates).</param>
    /// <param name="Cloaked">Actor flags (+0x390) have the cloak bit 0x80000 set.</param>
    /// <param name="Damage">Damage taken (HP lost), from the unit record (see SC2Memory.ActorReader ReadVitals).</param>
    /// <param name="Energy">Current energy, from the unit record.</param>
    /// <param name="MaxHp">Max HP from the game's max-life formula (0 = unknown). HpFrac = (MaxHp - Damage) / MaxHp.</param>
    /// <param name="ShieldDamage">Shield points lost, from the unit record.</param>
    /// <param name="MaxShields">Max shields (0 = no shields).</param>
    /// <param name="MaxEnergy">Max energy (0 = no energy).</param>
    /// <param name="Owner">Owning player id (0 = neutral).</param>
    /// <param name="Food">Supply: negative = uses, positive = provides.</param>
    /// <param name="Minerals">Mineral cost of the unit type (catalog CostResource).</param>
    /// <param name="Gas">Vespene cost of the unit type.</param>
    /// <param name="CostCategory">1 army, 2 economy, 3 technology (0 = unknown).</param>
    /// <param name="Sight">Vision radius in map units (catalog Sight).</param>
    /// <param name="Record">Game unit record address (0 = unresolved). Several actors can share one unit.</param>
    /// <param name="TypeName">The game's unit type id ("SupplyDepotLowered"), "" when unknown.</param>
    /// <param name="EyeHeight">How far above its ground the unit sees from (catalog Height for flyers, VisionHeight), map units.</param>
    /// <param name="Resources">Minerals / gas left in a mineral field or geyser, -1 when not a resource or unknown.</param>
    /// <param name="Progress">0..1 of a morph in progress (Lair, Hive, ...), -1 when not morphing.</param>
    /// <param name="QueueProgress">0..1 of the unit / research a building is producing, -1 when idle.</param>
    /// <param name="QueueCount">How many items are in the building's queue (including the one in progress).</param>
    /// <param name="CanQueue">The unit has a production queue (trains or researches), busy or idle.</param>
    /// <param name="QueueProduct">What the first queue item makes ("Marine", "Zergling ×2", "Stimpack"), "" when unknown.</param>
    /// <param name="TimeLeft">Seconds of the game timer until the morph (else the first queue item) is done, -1 when unknown.</param>
    /// <param name="Range">The longest weapon range (map units, edge to edge, with upgrades), -1 when unarmed or unknown.</param>
    /// <param name="Detector">The unit detects cloaked and burrowed units (the game's own flag, so map-specific detectors count).</param>
    /// <param name="WarpingIn">A Protoss building still warping in (its warp-in model): not working yet - a Pylon powers nothing.</param>
    public readonly record struct UnitEntry(float X, float Y, uint Color, string Name, UnitCategory Category, string Path, ulong Addr,
                                            bool Burrowed = false, bool Cloaked = false, float Damage = 0f, float Energy = 0f, float MaxHp = 0f,
                                            float ShieldDamage = 0f, float MaxShields = 0f, float MaxEnergy = 0f,
                                            byte Owner = 0, float Food = 0f, int Minerals = 0, int Gas = 0, int CostCategory = 0,
                                            float Sight = 0f, ulong Record = 0, string TypeName = "", float EyeHeight = 0f,
                                            int Resources = -1, float Progress = -1f, float QueueProgress = -1f, int QueueCount = 0,
                                            bool CanQueue = false, string QueueProduct = "", float TimeLeft = -1f, float Range = -1f,
                                            bool Detector = false, bool WarpingIn = false)
    {
        /// <summary>The game's type name spaced out ("Supply Depot Lowered"), else the model name.</summary>
        public string DisplayName => TypeName.Length > 0 ? PrettyNames.Get(TypeName) : Name;
        public bool IsBuilding => Category == UnitCategory.Building;
        public bool IsWorker   => Category == UnitCategory.Worker;
        /// <summary>0..1 HP fraction when MaxHp is known and unit is alive, else -1.</summary>
        public float HpFrac    => MaxHp > 0f ? Math.Clamp((MaxHp - Damage) / MaxHp, 0f, 1f) : (Damage > 0f ? -1f : 1f);
        /// <summary>0..1 shield fraction, or -1 for units without shields.</summary>
        public float ShieldFrac => MaxShields > 0f ? Math.Clamp((MaxShields - ShieldDamage) / MaxShields, 0f, 1f) : -1f;
        public bool  IsDamaged  => Damage >= 0.5f || (MaxShields > 0f && ShieldDamage >= 0.5f);
    }

    /// <summary>"SupplyDepotLowered" -> "Supply Depot Lowered", "SCV" stays, "Zergling_2" -> "Zergling 2"; cached.</summary>
    static class PrettyNames
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();
        static readonly System.Text.RegularExpressions.Regex Split =
            new(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_+", System.Text.RegularExpressions.RegexOptions.Compiled);
        // upgrade ids that don't read as their in-game names
        static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            ["zerglingmovementspeed"] = "Metabolic Boost", ["zerglingattackspeed"] = "Adrenal Glands", ["overlordspeed"] = "Pneumatized Carapace",
            ["CentrificalHooks"] = "Centrifugal Hooks", ["EvolveGroovedSpines"] = "Grooved Spines", ["EvolveMuscularAugments"] = "Muscular Augments",
            ["ShieldWall"] = "Combat Shield", ["PunisherGrenades"] = "Concussive Shells", ["BlinkTech"] = "Blink", ["WarpGateResearch"] = "Warp Gate",
            ["ObverseIncubation"] = "Gravitic Boosters", ["ExtendedThermalLance"] = "Extended Thermal Lance", ["PsiStormTech"] = "Psionic Storm",
            ["HiSecAutoTracking"] = "Hi-Sec Auto Tracking", ["TerranBuildingArmor"] = "Neosteel Armor", ["DrillClaws"] = "Drilling Claws",
            ["HighCapacityBarrels"] = "Infernal Pre-Igniter", ["BansheeCloak"] = "Cloaking Field", ["PersonalCloaking"] = "Personal Cloaking",
            ["DiggingClaws"] = "Adaptive Talons", ["LurkerRange"] = "Seismic Spines", ["InfestorEnergyUpgrade"] = "Pathogen Glands",
        };

        /// <summary>"SupplyDepotLowered" -> "Supply Depot Lowered"; known upgrade ids by their in-game name; first letter capital.</summary>
        public static string Get(string id) => Cache.GetOrAdd(id, s =>
        {
            if (Known.TryGetValue(s, out var name)) return name;
            string spaced = Split.Replace(s, " ").Trim();
            return spaced.Length > 0 && char.IsLower(spaced[0]) ? char.ToUpperInvariant(spaced[0]) + spaced[1..] : spaced;
        });
    }

    public sealed class UnitSnapshot
    {
        public static readonly UnitSnapshot Empty = new([]);
        public readonly UnitEntry[] Units;
        public UnitSnapshot(UnitEntry[] units) => Units = units;
    }
}
