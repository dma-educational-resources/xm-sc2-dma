using System.IO;

namespace SC2Radar.Misc
{
    /// <summary>
    /// Log severity level.
    /// </summary>
    public enum AppLogLevel
    {
        Debug,    // Detailed diagnostic info ?? only emitted when EnableDebugLogging is true
        Info,     // General informational messages
        Warning,  // Warnings that don't prevent operation
        Error     // Errors that may affect functionality
    }

    /// <summary>
    /// Unified application logger.
    /// Owns the console window, optional file sink, level filtering, and rate-limit helpers.
    /// Thread-safe.
    /// </summary>
    public static class XMLogging
    {
        #region Console / file infrastructure

        private static StreamWriter _fileWriter;
        private static bool _consoleAllocated;
        private static bool _consoleVisible;
        private static readonly Lock _writeLock = new();
        private static ConsoleColor _currentColor = ConsoleColor.Gray;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        static XMLogging()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args?.Contains("-logging", StringComparer.OrdinalIgnoreCase) ?? false)
            {
                string logFileName = $"log-{DateTime.UtcNow.ToFileTime()}.txt";
                var fs = new FileStream(logFileName, FileMode.Create, FileAccess.Write);
                _fileWriter = new StreamWriter(fs, Encoding.UTF8, 0x1000);
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
        }

        /// <summary>Whether the debug console window is currently visible.</summary>
        public static bool ConsoleVisible => _consoleVisible;

        /// <summary>
        /// Shows or hides the debug console window. On first show the console is allocated.
        /// Safe to call at any time after process startup.
        /// </summary>
        public static void SetConsoleVisible(bool visible)
        {
            try
            {
                if (visible)
                {
                    if (!_consoleAllocated)
                    {
                        if (GetConsoleWindow() == IntPtr.Zero)
                        {
                            if (AllocConsole())
                            {
                                Console.OutputEncoding = Encoding.UTF8;
                                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true });
                                Console.SetError(new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true });
                                Console.Title = "eft-dma-radar - Debug Console";
                                Console.ForegroundColor = ConsoleColor.Cyan;
                                Console.WriteLine("================================================================");
                                Console.WriteLine("                 eft-dma-radar Debug Console                  ");
                                Console.WriteLine("================================================================");
                                Console.ResetColor();
                                Console.WriteLine();
                                _consoleAllocated = true;
                            }
                        }
                        else
                        {
                            _consoleAllocated = true;
                        }
                    }
                    var hwnd = GetConsoleWindow();
                    if (hwnd != IntPtr.Zero)
                        ShowWindow(hwnd, SW_SHOW);
                    _consoleVisible = true;
                }
                else
                {
                    var hwnd = GetConsoleWindow();
                    if (hwnd != IntPtr.Zero)
                        ShowWindow(hwnd, SW_HIDE);
                    _consoleVisible = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set console visibility: {ex.Message}");
            }
        }

        private static void AllocateConsole()
        {
            if (_consoleAllocated)
                return;
            try
            {
                if (GetConsoleWindow() == IntPtr.Zero)
                {
                    if (AllocConsole())
                    {
                        Console.OutputEncoding = Encoding.UTF8;
                        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true });
                        Console.SetError(new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true });
                        Console.Title = "eft-dma-radar - Debug Console";
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("================================================================");
                        Console.WriteLine("                 eft-dma-radar Debug Console                  ");
                        Console.WriteLine("================================================================");
                        Console.ResetColor();
                        Console.WriteLine();
                        _consoleAllocated = true;
                    }
                }
                else
                {
                    _consoleAllocated = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to allocate console: {ex.Message}");
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            var writer = Interlocked.Exchange(ref _fileWriter, null);
            writer?.Dispose();
        }

        /// <summary>Whether debug console output is currently also being written to a log file.</summary>
        public static bool FileLoggingEnabled { get; private set; }

        /// <summary>Full path of the currently-open log file, or null if file logging is off.</summary>
        public static string CurrentLogFilePath { get; private set; }

        /// <summary>
        /// Enables or disables writing debug console output to a file under
        /// <see cref="Program.LogsPath"/>\dd-MM-yyyy\HH-mm-ss.log. Safe to call at any time.
        /// </summary>
        public static void SetFileLoggingEnabled(bool enabled)
        {
            lock (_writeLock)
            {
                if (enabled == FileLoggingEnabled)
                    return;

                if (enabled)
                {
                    try
                    {
                        var now = DateTime.Now;
                        var dir = Path.Combine(Program.LogsPath.FullName, now.ToString("dd-MM-yyyy"));
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, $"{now:HH-mm-ss}.log");
                        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                        var writer = new StreamWriter(fs, Encoding.UTF8, 0x1000) { AutoFlush = true };

                        var old = Interlocked.Exchange(ref _fileWriter, writer);
                        old?.Dispose();

                        CurrentLogFilePath = path;
                        FileLoggingEnabled = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to start file logging: {ex.Message}");
                    }
                }
                else
                {
                    var old = Interlocked.Exchange(ref _fileWriter, null);
                    old?.Dispose();
                    CurrentLogFilePath = null;
                    FileLoggingEnabled = false;
                }
            }
        }

        #endregion

        #region Level filtering

        public static AppLogLevel MinimumLogLevel { get; set; } = AppLogLevel.Info;
        public static bool EnableDebugLogging { get; set; } = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsEnabled(AppLogLevel level) =>
            level >= MinimumLogLevel && (level != AppLogLevel.Debug || EnableDebugLogging);

        #endregion

        #region Core write

        /// <summary>
        /// Write a plain message with no level prefix or category. Goes to console (when
        /// allocated) AND file -- this is the default for ordinary status/narrative messages
        /// ("Resolving GameWorld", "GameWorld found", subsystem init succeeded/failed, a feature
        /// engaged/disengaged, etc.). Use <see cref="WriteLineFileOnly"/> instead for messages
        /// that are routine per-tick/per-poll chatter, contain raw addresses/offsets/hex dumps, or
        /// otherwise aren't something a user watching the console needs to see scroll by.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteLine(object data)
        {
            lock (_writeLock)
                WriteCore(data?.ToString() ?? string.Empty, toConsole: true);
        }

        /// <summary>
        /// Same as <see cref="WriteLine(object)"/> but file-only (plus the in-memory backlog/
        /// <see cref="OnLine"/> subscribers) -- never printed to the console window. Use this for
        /// routine per-tick/per-poll diagnostic chatter (a scan that repeats every ~500ms while
        /// waiting for something), raw technical detail (addresses, offsets, hex, per-candidate
        /// reasoning), or periodic background-worker housekeeping -- anything that would just be
        /// noise to a user watching the console live, even though it's genuinely useful in the
        /// log file for troubleshooting. Nothing is lost; this only affects console visibility.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteLineFileOnly(object data)
        {
            lock (_writeLock)
                WriteCore(data?.ToString() ?? string.Empty, toConsole: false);
        }

        /// <summary>
        /// Same as <see cref="WriteLineFileOnly"/>, but the call (and every argument expression,
        /// including string interpolations) is compiled OUT ENTIRELY in a Release build --
        /// [Conditional] strips the call site itself, not just a runtime check, so technical
        /// detail (addresses, offsets, RVAs, per-call/per-renderer internals) never exists in a
        /// shipped binary regardless of any user-facing "debug logging" toggle. Use this instead
        /// of <see cref="EnableDebugLogging"/>-gated <see cref="Write"/> calls for anything that
        /// shouldn't be recoverable by flipping a setting.
        /// </summary>
        [Conditional("DEBUG")]
        public static void WriteLineDebug(object data) => WriteLineFileOnly(data);

        /// <summary>
        /// Write multiple lines atomically (no interleaving from other threads). File-only --
        /// these are typically bulk diagnostic dumps, not console-appropriate.
        /// </summary>
        public static void WriteBlock(List<string> lines)
        {
            lock (_writeLock)
                foreach (var line in lines)
                    WriteCore(line, toConsole: false);
        }

        // Bounded backlog of formatted lines, kept independent of the console/file
        // sinks (both user-toggled, default off) so a remote log request always has
        // recent history to hand over regardless of what the user has enabled.
        private const int MaxBufferLines = 3000;
        private static readonly ConcurrentQueue<string> _buffer = new();
        private static int _bufferCount;

        /// <summary>
        /// Raised for every formatted line, regardless of sink state. Subscribe to
        /// live-tail log output (e.g. a remote log-streaming client).
        /// </summary>
        public static event Action<string> OnLine;

        /// <summary>Snapshot of the current backlog buffer, oldest first.</summary>
        public static string[] SnapshotBuffer() => _buffer.ToArray();

        private static void WriteCore(string message, bool toConsole)
        {
            // Single choke point for every WriteLine/Write/WriteLineFileOnly call site (see
            // this class's own header comment), so hooking sensitive-tag encryption here
            // covers the file, the in-memory buffer, and OnLine (the session-logs relay's
            // live-tail source) uniformly - LogStreamClient itself needs no changes at all,
            // it just faithfully relays whatever WriteCore already produced.
            // SC2 build: LogEncryption not available - message passed as-is

            var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var formatted = $"[{timestamp}] {message}";

            Debug.WriteLine(formatted);

            if (toConsole && _consoleAllocated)
            {
                var color = message.Contains("ERROR") || message.Contains("FAIL")
                    ? ConsoleColor.Red
                    : message.Contains("[GOM]") || message.Contains("[Signature]")
                    ? ConsoleColor.Yellow
                    : message.Contains("OK") || message.Contains("success")
                    ? ConsoleColor.Cyan
                    : ConsoleColor.Gray;

                if (color != _currentColor)
                {
                    Console.ForegroundColor = color;
                    _currentColor = color;
                }
                Console.WriteLine(formatted);
            }

            _fileWriter?.WriteLine(formatted);

            _buffer.Enqueue(formatted);
            if (++_bufferCount > MaxBufferLines && _buffer.TryDequeue(out _))
                _bufferCount--;

            try { OnLine?.Invoke(formatted); } catch { /* subscriber errors must never break logging */ }
        }

        #endregion

        #region Level-aware write

        /// <summary>
        /// Write a message with an optional severity level and category tag. Info/Warning/Error
        /// print to the console; Debug is file-only (verbose internals, same reasoning as
        /// <see cref="WriteLineFileOnly"/>).
        /// </summary>
        /// <param name="level">Severity level (filtered against <see cref="MinimumLogLevel"/>).</param>
        /// <param name="message">Message body.</param>
        /// <param name="category">Optional category shown as <c>[Category]</c> prefix.</param>
        public static void Write(AppLogLevel level, string message, string category = "")
        {
            if (!IsEnabled(level))
                return;

            var prefix = level switch
            {
                AppLogLevel.Error   => "ERROR ",
                AppLogLevel.Warning => "WARNING ",
                AppLogLevel.Debug   => "DEBUG ",
                _                   => ""
            };

            var line = (prefix.Length, string.IsNullOrEmpty(category)) switch
            {
                (0, true)  => message,
                (0, false) => $"[{category}] {message}",
                (_, true)  => $"{prefix}{message}",
                _          => $"{prefix}[{category}] {message}"
            };

            lock (_writeLock)
                WriteCore(line, toConsole: level > AppLogLevel.Debug);
        }

        #endregion

        #region Rate-limit helpers

        private static readonly ConcurrentDictionary<string, DateTime> _rateLimitCache = new();
        private static readonly ConcurrentDictionary<string, (int count, DateTime firstOccurrence)> _repeatedMessages = new();
        private static readonly Lock _consolidationLock = new();

        /// <summary>
        /// Returns <see langword="true"/> and stamps <paramref name="key"/> if it has not been
        /// seen within <paramref name="interval"/>; otherwise returns <see langword="false"/>.
        /// Use to gate a block of manual <see cref="Write"/> calls at a fixed frequency.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryThrottle(string key, TimeSpan interval)
        {
            var now = DateTime.UtcNow;
            if (_rateLimitCache.TryGetValue(key, out var last) && now - last < interval)
                return false;
            _rateLimitCache[key] = now;
            return true;
        }

        /// <summary>
        /// Logs a message only if <paramref name="key"/> has not been seen within <paramref name="interval"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRateLimited(AppLogLevel level, string key, TimeSpan interval, string message, string category = "")
        {
            if (!IsEnabled(level))
                return;
            var now = DateTime.UtcNow;
            if (_rateLimitCache.TryGetValue(key, out var last) && now - last < interval)
                return;
            _rateLimitCache[key] = now;
            Write(level, message, category);
        }

        /// <summary>
        /// Logs the first occurrence of <paramref name="key"/> immediately; subsequent calls within
        /// the same session are silently counted. Call <see cref="FlushRepeatedMessages"/> to emit counts.
        /// </summary>
        public static void WriteRepeated(AppLogLevel level, string key, string message, string category = "")
        {
            lock (_consolidationLock)
            {
                if (_repeatedMessages.TryGetValue(key, out var existing))
                {
                    _repeatedMessages[key] = (existing.count + 1, existing.firstOccurrence);
                }
                else
                {
                    _repeatedMessages[key] = (1, DateTime.UtcNow);
                    Write(level, message, category);
                }
            }
        }

        /// <summary>
        /// Flushes accumulated repeat-counts to the log for any key older than <paramref name="maxAge"/>.
        /// </summary>
        public static void FlushRepeatedMessages(TimeSpan? maxAge = null)
        {
            lock (_consolidationLock)
            {
                var now = DateTime.UtcNow;
                var threshold = maxAge ?? TimeSpan.FromSeconds(5);
                foreach (var kvp in _repeatedMessages.ToArray())
                {
                    var (count, firstTime) = kvp.Value;
                    if (count > 1 && now - firstTime >= threshold)
                    {
                        WriteLine($"  ???? (repeated {count}x in {(now - firstTime).TotalSeconds:F1}s)");
                        _repeatedMessages.TryRemove(kvp.Key, out _);
                    }
                    else if (now - firstTime >= TimeSpan.FromMinutes(1))
                    {
                        _repeatedMessages.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }

        /// <summary>
        /// Logs exactly once per session for <paramref name="key"/>; all subsequent calls are suppressed.
        /// </summary>
        public static void WriteOnce(AppLogLevel level, string key, string message, string category = "")
        {
            if (!_rateLimitCache.TryAdd(key, DateTime.UtcNow))
                return;
            Write(level, message, category);
        }

        /// <summary>
        /// Clears all rate-limit and repeat-count caches.
        /// </summary>
        public static void ClearCaches()
        {
            _rateLimitCache.Clear();
            lock (_consolidationLock)
                _repeatedMessages.Clear();
        }

        #endregion
    }
}
