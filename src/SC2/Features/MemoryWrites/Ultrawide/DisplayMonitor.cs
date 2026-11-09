namespace SC2Radar.SC2.Features.MemoryWrites.Ultrawide;

/// <summary>
/// The game window and bottom console, read once a second on its own worker (finding the addresses reads tens of MB
/// of game code, too slow for the memory tick): the window size (the radar's camera boxes use its aspect ratio),
/// whether a mission is loaded (the unit reader's mission gate) and the state the Ultrawide feature shows. Read-only;
/// the Ultrawide memory write uses <see cref="Game"/> once the addresses are found.
/// </summary>
static class DisplayMonitor
{
    static GameMemory? _game;
    static volatile bool _resolved;
    static volatile string? _resolveError;
    static readonly Queue<string> _log = new();

    /// <summary>The game process with its display addresses found, else null.</summary>
    public static GameMemory? Game => _resolved && _game?.Addr != null ? _game : null;
    /// <summary>The game window's size, (0, 0) until the display addresses are found.</summary>
    public static (int W, int H) GameSize { get; private set; }
    /// <summary>True while a mission is loaded (console panel present). False in menus / loading.</summary>
    public static bool InMission { get; private set; }
    /// <summary>Set when the console addresses resolved, i.e. <see cref="InMission"/> is meaningful.</summary>
    public static bool MissionDetectAvailable { get; private set; }
    /// <summary>The window line of the Ultrawide popover: size, aspect, display mode.</summary>
    public static string Status { get; private set; } = "Waiting for StarCraft II...";
    /// <summary>The bottom console line of the Ultrawide popover.</summary>
    public static string ConsoleState { get; private set; } = "-";
    /// <summary>The game is wider than its own 16:9 limit (false while unknown).</summary>
    public static bool IsWide { get; private set; }
    /// <summary>Raised on the monitor's worker once the addresses of a game process are found.</summary>
    public static event Action? Resolved;

    static DisplayMonitor()
    {
        new Thread(Worker) { Name = "DisplayMonitor", IsBackground = true }.Start();
    }

    /// <summary>Starts the worker (the first access does).</summary>
    public static void Start() { }

    /// <summary>The last few things that happened (address lookup, Ultrawide steps), newest last.</summary>
    public static string[] RecentLog()
    {
        lock (_log) return [.. _log];
    }

    public static void Log(string msg)
    {
        lock (_log)
        {
            _log.Enqueue($"{DateTime.Now:HH:mm:ss}  {msg}");
            while (_log.Count > 8) _log.Dequeue();
        }
        XMLogging.WriteLine($"[Ultrawide] {msg}");
    }

    static void Worker()
    {
        while (true)
        {
            try { Step(); }
            catch (Exception ex) { Status = $"Error: {ex.Message}"; }
            Thread.Sleep(1000);
        }
    }

    static void Step()
    {
        var mem = Memory.Instance;
        if (mem == null || !Memory.InGame || mem.ExeBase == 0)
        {
            _game = null; _resolved = false; InMission = false; MissionDetectAvailable = false; IsWide = false;
            Status = "Waiting for StarCraft II...";
            return;
        }

        if (_game == null || _game.Pid != mem.Pid || _game.Base != mem.ExeBase)
        {
            _game = new GameMemory(mem);
            _resolved = false; _resolveError = null;
        }
        var game = _game;

        if (!_resolved)
        {
            Status = "Finding addresses (reading game code)...";
            try
            {
                var a = AddressFinder.Find(game);
                Log(a.Console ? "Addresses found." : $"Addresses found; console fix unavailable ({a.ConsoleError}).");
                MissionDetectAvailable = a.Console;
            }
            catch (LookupFailed ex)
            {
                _resolveError = ex.Message;
                Log($"Address lookup failed: {ex.Message}. A game patch may have changed that code; nothing will be written.");
            }
            finally
            {
                game.ClearCache();
                _resolved = true;
            }
            if (game.Addr != null) Resolved?.Invoke();
        }
        if (game.Addr is null) { Status = $"Unavailable in this build ({_resolveError})"; return; }

        var (w, h) = WideMode.StoredSize(game);
        uint mode  = game.ReadU32(game.Addr.DisplayMode);
        GameSize = ((int)w, (int)h);
        IsWide   = WideMode.WiderThanCap(w, h);
        Status   = $"{w} x {h}  |  {(IsWide ? "ultrawide" : "16:9 limit")}  |  " +
                   (mode == WideMode.DisplayWindowed ? "Windowed (Fullscreen)" : $"display mode {mode}");
        InMission    = game.Addr.Console && BottomConsole.InMission(game);
        ConsoleState = game.Addr.Console ? BottomConsole.State(game) : "fix unavailable in this build";
    }
}
