using SC2Radar.Common.Misc;
using SC2Radar.UI.ImGui;
using System.Text;
using VmmSharpEx;
using VmmSharpEx.Extensions;
using VmmSharpEx.Options;

namespace SC2Radar.DMA
{
    public static class InputManager
    {
        private static volatile bool _initialized = false;
        private static bool _safeMode = false;

        // Initialize() is called once per successful game (re)detection, not once per process
        // lifetime -- an in-session game crash/relaunch would otherwise spawn an additional,
        // permanently-running Worker thread each time (it only exits on Memory.IsDisposed, a
        // full VMM shutdown, never on an ordinary game restart). Duplicate threads would each
        // poll key state independently and could double-fire the same hotkey handlers. One
        // Worker thread is sufficient for the whole process lifetime; it already re-polls game
        // readiness continuously, so it needs no reset on game restart.
        private static volatile bool _workerThreadStarted = false;
        private static Thread? _worker;

        /// <summary>Wait for the key reader to leave its loop (it checks Memory.IsDisposed between reads), so the DMA handle
        /// isn't closed under one of its reads.</summary>
        public static void WaitStopped(int ms) { if (_worker is { IsAlive: true } t) t.Join(ms); }

        private static ulong _gafAsyncKeyStateExport;

        private static byte[] _currentStateBitmap = new byte[64];
        private static byte[] _previousStateBitmap = new byte[64];
        // bool[] element reads/writes are atomic on x64 - safe to read from the UI thread
        // while the worker thread writes, no lock required.
        private static readonly bool[] _pressedKeys = new bool[256];

        private static Vmm _hVMM;
        private static uint _winLogonPid;

        private const int KEY_CHECK_DELAY = 100;

        private static int _currentBuild;
        private static readonly Dictionary<int, DateTime> _lastKeyTapTime = new();
        private static readonly Dictionary<int, bool> _heldStates = new();
        private const int DoubleTapThresholdMs = 300;

        public static bool IsReady => _initialized;

        /// <summary>Human-readable current state of DMA-kernel input init, for UI display -
        /// updated by the Worker retry loop below.</summary>
        private static volatile string _status = "Not initialized";
        public static string Status => _status;

#nullable enable
        public static event EventHandler? ReadyChanged;
#nullable restore

        private static readonly Dictionary<int, List<KeyActionHandler>> _keyActionHandlers = new();
        private static readonly object _eventLock = new();
        private static int _nextActionId = 1;

        /// <summary>
        /// Attempts to load Input Manager.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                if (Memory.VmmHandle == null)
                {
                    _safeMode = true;
                    _status   = "Safe Mode - DMA input unavailable";
                    XMLogging.WriteLine("[InputManager] Starting in Safe Mode - Input functionality disabled");
                    ToastManager.Show("[InputManager] Safe Mode - Input functionality disabled", ToastStyle.Warning);
                    return;
                }

                _hVMM  = Memory.VmmHandle;
                _status = "Not initialized";

                // The Worker thread's retry loop calls InitKeyboard() itself, starting
                // immediately on its first iteration - it (and only it) owns init attempts
                // and success/failure reporting. A synchronous InitKeyboard() call here used
                // to race that first Worker attempt, redoing the same signature scan
                // concurrently and double-logging/double-toasting on every successful init.
                if (_hVMM != null && !_workerThreadStarted)
                {
                    _workerThreadStarted = true;
                    _worker = new Thread(Worker)
                    {
                        IsBackground = true
                    };
                    _worker.Start();
                }
            }
            catch (Exception ex)
            {
                XMLogging.WriteLine($"[InputManager] Error during initialization: {ex.Message}");
                _safeMode = true;
                _status   = "Initialization failed - Safe Mode";
                ToastManager.Show("[InputManager] Initialization failed - Safe Mode active", ToastStyle.Warning);
            }
        }

        private static bool InitKeyboard()
        {
            if (_initialized)
                return true;

            if (_safeMode || _hVMM == null)
            {
                XMLogging.WriteLineFileOnly("[InputManager] Skipping keyboard initialization - Safe Mode");
                return false;
            }

            try { _hVMM.ForceFullRefresh(); } catch { }

            try
            {
                var currentBuildBytes = _hVMM.WinReg_QueryValue(
                    "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\CurrentBuild", out _);
                _currentBuild = int.Parse(Encoding.Unicode.GetString(currentBuildBytes).TrimEnd('\0'));

                if (!_hVMM.PidGetFromName("winlogon.exe", out _winLogonPid))
                {
                    XMLogging.WriteLineFileOnly("[InputManager] winlogon.exe not found");
                    return false;
                }

                XMLogging.WriteLineFileOnly($"[InputManager] Build {_currentBuild}, winlogon PID: {_winLogonPid}");

                bool success = _currentBuild > 22000
                    ? InitKeyboardForNewWindows()
                    : InitKeyboardForOldWindows();

                // If the Win11 csrss/signature path failed, try EAT/PDB via winlogon as a fallback.
                // On some Win11 setups win32ksgd is inaccessible via csrss but EAT still resolves.
                if (!success && _currentBuild > 22000)
                {
                    XMLogging.WriteLineFileOnly("[InputManager] Csrss path failed - trying EAT/PDB fallback");
                    success = InitKeyboardForOldWindows();
                }

                return success;
            }
            catch (Exception ex)
            {
                XMLogging.WriteLineFileOnly($"[InputManager] InitKeyboard failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // Gets the base address and size for win32ksgd.sys or win32k.sys from a csrss PID.
        // On build 26100+, Map_GetModuleFromName fails for these modules even though
        // ProcessGetModuleBase succeeds, so we fall back to reading SizeOfImage from the PE header.
        private static bool TryGetWin32kInfo(uint pid, out ulong baseAddress, out ulong moduleSize)
        {
            baseAddress = 0;
            moduleSize = 0;

            foreach (var name in new[] { "win32ksgd.sys", "win32k.sys" })
            {
                var ba = _hVMM.ProcessGetModuleBase(pid, name);
                if (ba == 0)
                    continue;

                if (_hVMM.Map_GetModuleFromName(pid, name, out var info) && info.cbImageSize > 0)
                {
                    XMLogging.WriteLineFileOnly($"[InputManager] Found {name} @ 0x{ba:X}");
                    baseAddress = ba;
                    moduleSize = info.cbImageSize;
                    return true;
                }

                // Map_GetModuleFromName fails on 26100/26200 for these modules.
                // Read SizeOfImage from the PE header instead.
                var peSize = ReadPESizeOfImage(pid, ba);
                if (peSize > 0)
                {
                    XMLogging.WriteLineFileOnly($"[InputManager] Found {name} @ 0x{ba:X} (PE size: 0x{peSize:X})");
                    baseAddress = ba;
                    moduleSize = peSize;
                    return true;
                }

                // Last resort: scan a generous 32 MB range.
                XMLogging.WriteLineFileOnly($"[InputManager] Found {name} @ 0x{ba:X} (using 32MB fallback range)");
                baseAddress = ba;
                moduleSize = 0x2000000u;
                return true;
            }

            XMLogging.WriteLineFileOnly("[InputManager] Failed to locate win32k module");
            return false;
        }

        // Reads SizeOfImage from a loaded PE image header in memory.
        // Offsets: e_lfanew at DOS+0x3C; SizeOfImage at NT+0x18(COFF)+0x38(optional) = NT+0x50.
        private static uint ReadPESizeOfImage(uint pid, ulong baseAddress)
        {
            try
            {
                if (!_hVMM.MemReadValue<uint>(pid, baseAddress + 0x3C, out uint e_lfanew) || e_lfanew is 0 or > 0x1000)
                    return 0;
                if (!_hVMM.MemReadValue<uint>(pid, baseAddress + e_lfanew + 0x50, out uint sizeOfImage) || sizeOfImage == 0)
                    return 0;
                return sizeOfImage;
            }
            catch { return 0; }
        }

        private static bool TryFindSessionPointer(uint pid, ulong baseAddr, ulong size, out ulong sessionPtr)
        {
            sessionPtr = 0;

            // Use ManualFindSig directly - VMMDLL_MemSearch (native FindSignature) fails silently
            // on some DMA firmware versions when searching kernel module address ranges via a user PID.
            // WPF version never uses native MemSearch; it always does a direct memory read + manual scan.
            var gSessionPtr = ManualFindSig(pid, "48 8B 05 ? ? ? ? 48 8B 04 C8", baseAddr, baseAddr + size);
            if (gSessionPtr == 0)
                gSessionPtr = ManualFindSig(pid, "48 8B 05 ? ? ? ? FF C9", baseAddr, baseAddr + size);

            if (gSessionPtr == 0)
            {
                XMLogging.WriteLineFileOnly("[InputManager] Failed to find gSessionGlobalSlots signature");
                return false;
            }

            // No NOCACHE for init reads - these addresses are fixed at boot time and MemProcFS
            // cache is reliable here. NOCACHE during init fails on slow/unstable DMA setups.
            if (!_hVMM.MemReadValue<int>(pid, gSessionPtr + 3, out int relativeOffset) || relativeOffset == 0)
            {
                XMLogging.WriteLineFileOnly("[InputManager] Failed to read session pointer relative offset");
                return false;
            }

            sessionPtr = gSessionPtr + 7 + (ulong)relativeOffset;
            return true;
        }

        private static bool TryResolveUserSessionState(uint pid, ulong sessionPtr, out ulong sessionState)
        {
            sessionState = 0;
            for (int i = 0; i < 16; i++)
            {
                if (!_hVMM.MemReadValue<ulong>(pid, sessionPtr, out ulong t1) || t1 == 0)
                    continue;
                if (!_hVMM.MemReadValue<ulong>(pid, t1 + (ulong)(8 * i), out ulong t2) || t2 == 0)
                    continue;
                if (!_hVMM.MemReadValue<ulong>(pid, t2, out ulong t3) || t3 == 0)
                    continue;
                sessionState = t3;
                if (sessionState > 0x7FFFFFFFFFFF)
                    return true;
            }
            return sessionState != 0;
        }

        private static bool TryGetAsyncKeyStateOffset(uint pid, ulong sessionState, out ulong keyStateAddr)
        {
            keyStateAddr = 0;

            var win32kbaseBase = _hVMM.ProcessGetModuleBase(pid, "win32kbase.sys");
            if (win32kbaseBase == 0)
            {
                XMLogging.WriteLineFileOnly("[InputManager] Failed to get win32kbase.sys base");
                return false;
            }

            ulong win32kbaseSize;
            if (_hVMM.Map_GetModuleFromName(pid, "win32kbase.sys", out var win32kbaseInfo) && win32kbaseInfo.cbImageSize > 0)
            {
                win32kbaseSize = win32kbaseInfo.cbImageSize;
            }
            else
            {
                var peSize = ReadPESizeOfImage(pid, win32kbaseBase);
                win32kbaseSize = peSize > 0 ? peSize : 0x2000000u;
            }

            var ptr = ManualFindSig(
                pid,
                "48 8D 90 ? ? ? ? E8 ? ? ? ? 0F 57 C0",
                win32kbaseBase,
                win32kbaseBase + win32kbaseSize);

            if (ptr == 0)
            {
                XMLogging.WriteLineFileOnly("[InputManager] Failed to find gafAsyncKeyState offset signature");
                return false;
            }

            if (!_hVMM.MemReadValue<uint>(pid, ptr + 3, out uint sessionOffset) || sessionOffset == 0)
            {
                XMLogging.WriteLineFileOnly("[InputManager] Failed to read session offset");
                return false;
            }

            keyStateAddr = sessionState + sessionOffset;
            return keyStateAddr > 0x7FFFFFFFFFFF;
        }

        private static bool InitKeyboardForNewWindows()
        {
            XMLogging.WriteLineFileOnly("[InputManager] Windows build > 22000, attempting signature-based approach");

            var csrssPids = _hVMM.PidGetAllFromName("csrss.exe");
            if (csrssPids == null || csrssPids.Length == 0)
            {
                XMLogging.WriteLineFileOnly("[InputManager] No csrss.exe processes found");
                return false;
            }

            foreach (var csrssPid in csrssPids)
            {
                try
                {
                    if (!TryGetWin32kInfo(csrssPid, out ulong win32kBase, out ulong win32kSize))
                        continue;

                    if (!TryFindSessionPointer(csrssPid, win32kBase, win32kSize, out ulong gSessionGlobalSlots))
                        continue;

                    if (!TryResolveUserSessionState(csrssPid, gSessionGlobalSlots, out ulong userSessionState))
                        continue;

                    if (!TryGetAsyncKeyStateOffset(csrssPid, userSessionState, out ulong keyStateAddress))
                        continue;

                    _gafAsyncKeyStateExport = keyStateAddress;
                    _initialized = true;
                    ReadyChanged?.Invoke(null, EventArgs.Empty);
                    XMLogging.WriteLineFileOnly($"[InputManager] gafAsyncKeyState @ 0x{keyStateAddress:X} (csrss PID {csrssPid})");
                    return true;
                }
                catch (Exception ex)
                {
                    XMLogging.WriteLineFileOnly($"[InputManager] csrss PID {csrssPid} error: {ex.Message}");
                }
            }

            XMLogging.WriteLineFileOnly("[InputManager] Win11 path exhausted");
            return false;
        }

        private static bool InitKeyboardForOldWindows()
        {
            XMLogging.WriteLineFileOnly("[InputManager] Older Windows, attempting EAT resolution");

            uint winLogonKernelPid = _winLogonPid | Vmm.PID_PROCESS_WITH_KERNELMEMORY;

            var exports = _hVMM.Map_GetEAT(winLogonKernelPid, "win32kbase.sys", out _);
            if (exports != null)
            {
                var gafExport = exports.FirstOrDefault(e => e.sFunction == "gafAsyncKeyState");
                if (!string.IsNullOrEmpty(gafExport.sFunction) && gafExport.vaFunction >= 0x7FFFFFFFFFFF)
                {
                    _gafAsyncKeyStateExport = gafExport.vaFunction;
                    _initialized = true;
                    ReadyChanged?.Invoke(null, EventArgs.Empty);
                    XMLogging.WriteLineFileOnly($"[InputManager] Resolved via EAT @ 0x{gafExport.vaFunction:X}");
                    return true;
                }
            }

            XMLogging.WriteLineFileOnly("[InputManager] EAT failed, trying PDB");

            var win32kbaseBase = _hVMM.ProcessGetModuleBase(winLogonKernelPid, "win32kbase.sys");
            if (win32kbaseBase != 0 && _hVMM.PdbLoad(winLogonKernelPid, win32kbaseBase, out string? pdbName))
            {
                string modName = string.IsNullOrEmpty(pdbName) ? "win32kbase" : pdbName;
                if (_hVMM.PdbSymbolAddress(modName, "gafAsyncKeyState", out ulong pdbAddr) && pdbAddr >= 0x7FFFFFFFFFFF)
                {
                    _gafAsyncKeyStateExport = pdbAddr;
                    _initialized = true;
                    ReadyChanged?.Invoke(null, EventArgs.Empty);
                    XMLogging.WriteLineFileOnly($"[InputManager] Resolved via PDB @ 0x{pdbAddr:X}");
                    return true;
                }
                if (modName != "win32kbase" && _hVMM.PdbSymbolAddress("win32kbase", "gafAsyncKeyState", out pdbAddr) && pdbAddr >= 0x7FFFFFFFFFFF)
                {
                    _gafAsyncKeyStateExport = pdbAddr;
                    _initialized = true;
                    ReadyChanged?.Invoke(null, EventArgs.Empty);
                    XMLogging.WriteLineFileOnly($"[InputManager] Resolved via PDB (fallback name) @ 0x{pdbAddr:X}");
                    return true;
                }
            }

            XMLogging.WriteLineFileOnly("[InputManager] Failed to find gafAsyncKeyState export");
            return false;
        }

        public static unsafe void UpdateKeys()
        {
            if (!_initialized || _safeMode || _hVMM == null)
                return;

            Array.Copy(_currentStateBitmap, _previousStateBitmap, 64);

            uint winLogonKernelPid = _winLogonPid | Vmm.PID_PROCESS_WITH_KERNELMEMORY;

            fixed (byte* pb = _currentStateBitmap)
            {
                if (!_hVMM.MemRead(winLogonKernelPid, _gafAsyncKeyStateExport, pb, 64, out _, VmmFlags.NOCACHE))
                    return;

                for (int vk = 0; vk < 256; ++vk)
                    _pressedKeys[vk] = (_currentStateBitmap[(vk * 2 / 8)] & (1 << (vk % 4 * 2))) != 0;

                for (int vk = 0; vk < 256; ++vk)
                {
                    var wasDown = (_previousStateBitmap[(vk * 2 / 8)] & (1 << (vk % 4 * 2))) != 0;
                    var isDown = _pressedKeys[vk];

                    if (wasDown == isDown)
                        continue;

                    KeyActionHandler[] snapshot;
                    lock (_eventLock)
                    {
                        if (!_keyActionHandlers.TryGetValue(vk, out var handlers))
                            continue;
                        snapshot = handlers.ToArray();
                    }

                    foreach (var handler in snapshot)
                    {
                        try
                        {
                            handler.Handler?.Invoke(null, new KeyEventArgs(vk, isDown));
                        }
                        catch (Exception ex)
                        {
                            XMLogging.WriteLine($"Error executing key handler for action '{handler.ActionName}': {ex.Message}");
                        }
                    }
                }
            }
        }

        #region Signature Scanning

        private static ulong FindSig(uint pid, string pattern, ulong addrMin, ulong addrMax)
        {
            try
            {
                ulong native = _hVMM.FindSignature(pid, pattern, addrMin, addrMax);
                if (native != 0) return native;
            }
            catch { }
            return ManualFindSig(pid, pattern, addrMin, addrMax);
        }

        private static ulong ManualFindSig(uint pid, string pattern, ulong addrMin, ulong addrMax)
        {
            var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pat = new byte?[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                pat[i] = parts[i].StartsWith('?')
                    ? (byte?)null
                    : byte.Parse(parts[i], System.Globalization.NumberStyles.HexNumber);

            const uint CHUNK = 0x200000u;
            int overlap = Math.Max(pat.Length - 1, 0);
            // win32ksgd/win32kbase are kernel modules - if the csrss user-mode PID can't
            // read a page, retry with the kernel memory flag (gives access to all pages).
            uint kernelPid = pid | Vmm.PID_PROCESS_WITH_KERNELMEMORY;

            for (ulong addr = addrMin; addr < addrMax;)
            {
                ulong chunkEnd = Math.Min(addr + CHUNK, addrMax);
                uint size = (uint)(chunkEnd - addr);

                // Use cached reads during init - raw NOCACHE reads fail on slow/cold DMA hardware.
                byte[]? buf = _hVMM.MemRead(pid, addr, size, out _);
                if (buf == null || buf.Length < pat.Length)
                    buf = _hVMM.MemRead(kernelPid, addr, size, out _);
                if (buf != null && buf.Length >= pat.Length)
                {
                    int last = buf.Length - pat.Length;
                    for (int i = 0; i <= last; i++)
                    {
                        bool ok = true;
                        for (int j = 0; j < pat.Length; j++)
                        {
                            if (pat[j].HasValue && buf[i + j] != pat[j].Value) { ok = false; break; }
                        }
                        if (ok) return addr + (ulong)i;
                    }
                }

                ulong next = chunkEnd > (ulong)overlap ? chunkEnd - (ulong)overlap : chunkEnd;
                if (next <= addr) break;
                addr = next;
            }
            return 0;
        }

        #endregion

        #region Key Action Registration

        public static int RegisterKeyAction(int keyCode, string actionName, KeyStateChangedHandler handler)
        {
            if (!IsReady || _safeMode || handler == null || string.IsNullOrEmpty(actionName))
            {
                XMLogging.WriteLine($"[InputManager] RegisterKeyAction skipped - Safe Mode or not ready");
                return -1;
            }

            lock (_eventLock)
            {
                if (!_keyActionHandlers.ContainsKey(keyCode))
                    _keyActionHandlers[keyCode] = new List<KeyActionHandler>();

                var existingAction = _keyActionHandlers[keyCode].FirstOrDefault(h => h.ActionName == actionName);
                if (existingAction != null)
                {
                    existingAction.Handler = handler;
                    return existingAction.ActionId;
                }

                var actionId = _nextActionId++;
                _keyActionHandlers[keyCode].Add(new KeyActionHandler
                {
                    ActionId = actionId,
                    ActionName = actionName,
                    Handler = handler
                });

                return actionId;
            }
        }

        public static bool UnregisterKeyAction(int keyCode, string actionName)
        {
            if (_safeMode)
                return false;

            lock (_eventLock)
            {
                if (_keyActionHandlers.TryGetValue(keyCode, out var handlers))
                {
                    var removed = handlers.RemoveAll(h => h.ActionName == actionName) > 0;
                    if (handlers.Count == 0)
                        _keyActionHandlers.Remove(keyCode);
                    return removed;
                }
                return false;
            }
        }

        public static bool UnregisterKeyAction(int actionId)
        {
            if (_safeMode)
                return false;

            lock (_eventLock)
            {
                foreach (var kvp in _keyActionHandlers.ToList())
                {
                    var removed = kvp.Value.RemoveAll(h => h.ActionId == actionId) > 0;
                    if (kvp.Value.Count == 0)
                        _keyActionHandlers.Remove(kvp.Key);
                    if (removed)
                        return true;
                }
                return false;
            }
        }

        public static void ClearKeyActions(int keyCode)
        {
            if (_safeMode)
                return;

            lock (_eventLock)
                _keyActionHandlers.Remove(keyCode);
        }

        public static List<string> GetKeyActions(int keyCode)
        {
            if (_safeMode)
                return new List<string>();

            lock (_eventLock)
            {
                if (_keyActionHandlers.TryGetValue(keyCode, out var handlers))
                    return handlers.Select(h => h.ActionName).ToList();
                return new List<string>();
            }
        }

        public static Dictionary<int, List<string>> GetAllKeyActions()
        {
            if (_safeMode)
                return new Dictionary<int, List<string>>();

            lock (_eventLock)
            {
                var result = new Dictionary<int, List<string>>();
                foreach (var kvp in _keyActionHandlers)
                    result[kvp.Key] = kvp.Value.Select(h => h.ActionName).ToList();
                return result;
            }
        }

        #endregion

        #region Key State Queries

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// Physical key state on this machine (local Win32 input), independent of the
        /// gaming PC's keyboard read over DMA. Always checked first - a key wired directly to
        /// this PC (e.g. a locally-connected mouse) should register regardless of which
        /// hotkey backend is selected/ready.
        /// </summary>
        public static bool IsPhysicalKeyDown(int vk) =>
            (uint)vk < 256 && (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>
        /// Combined key-down check: this PC's keyboard and mouse, then the gaming PC's keyboard
        /// over DMA once it is ready - so hotkey capture and dispatch see a key press from either.
        /// </summary>
        public static bool IsKeyDownAny(int vk)
        {
            if (IsPhysicalKeyDown(vk)) return true;
            // the gaming PC's keyboard over DMA (SC2 has no Unity input mode)
            return IsReady && IsKeyDown(vk);
        }

        public static bool IsKeyDown(int key)
        {
            if (!_initialized || _safeMode || _gafAsyncKeyStateExport < 0x7FFFFFFFFFFF)
                return false;
            if ((uint)key >= 256) return false;
            return _pressedKeys[key];
        }

        public static bool IsKeyPressed(int key)
        {
            if (!_initialized || _safeMode || _gafAsyncKeyStateExport < 0x7FFFFFFFFFFF)
                return false;
            if ((uint)key >= 256) return false;
            return _pressedKeys[key] &&
                   (_previousStateBitmap[(key * 2 / 8)] & (1 << (key % 4 * 2))) == 0;
        }

        public static bool IsKeyHeldToggle(int key)
        {
            if (!_initialized || _safeMode || _gafAsyncKeyStateExport < 0x7FFFFFFFFFFF)
                return false;

            if (!IsKeyPressed(key))
                return _heldStates.TryGetValue(key, out var held) && held;

            var now = DateTime.UtcNow;

            lock (_eventLock)
            {
                if (_lastKeyTapTime.TryGetValue(key, out var lastTap))
                {
                    var delta = (now - lastTap).TotalMilliseconds;
                    if (delta < DoubleTapThresholdMs)
                    {
                        _heldStates[key] = !_heldStates.GetValueOrDefault(key, false);
                        _lastKeyTapTime.Remove(key);
                    }
                    else
                    {
                        _lastKeyTapTime[key] = now;
                    }
                }
                else
                {
                    _lastKeyTapTime[key] = now;
                }
            }

            return _heldStates.TryGetValue(key, out var isHeld) && isHeld;
        }

        #endregion

        #region Worker

        private static void Worker()
        {
            XMLogging.WriteLine("InputManager thread starting...");
            int retryCount = 0;
            long nextRetryTick = 0;
            while (true)
            {
                try
                {
                    if (Memory.IsDisposed)
                        break;

                    if (_safeMode)
                    {
                        Thread.Sleep(KEY_CHECK_DELAY);
                        continue;
                    }

                    // the gaming PC's keyboard through the kernel's key table (SC2 has no Unity input mode)
                    {
                        // retry kernel init with progressive backoff.
                        if (!_initialized && retryCount < 10 && Environment.TickCount64 >= nextRetryTick)
                        {
                            retryCount++;
                            int delaySec = Math.Min(retryCount * 2, 30);
                            _status = $"Connecting to DMA keyboard (attempt {retryCount}/10)...";
                            XMLogging.WriteLine($"[InputManager] Retrying init (attempt {retryCount}/10)...");
                            if (InitKeyboard())
                            {
                                _status = "Ready";
                                XMLogging.WriteLine($"[InputManager] Init succeeded on retry {retryCount}");
                                ToastManager.Show("[InputManager] Initialized successfully!", ToastStyle.Success);
                            }
                            else if (retryCount >= 10)
                            {
                                _status = "Failed to initialize - restart your gaming PC for hotkeys to work";
                                XMLogging.WriteLine("[InputManager] Failed to initialize after 10 retries.");
                                ToastManager.Show("[InputManager] Failed to initialize, you may need to restart your gaming pc for hotkeys to work.", ToastStyle.Danger);
                            }
                            else
                            {
                                nextRetryTick = Environment.TickCount64 + delaySec * 1000L;
                                _status = $"Retry {retryCount}/10 failed - next attempt in {delaySec}s";
                                XMLogging.WriteLine($"[InputManager] Retry {retryCount} failed, next in {delaySec}s");
                            }
                        }
                    }

                    // the key table needs only the DMA handle, not the game (SC2's WaitForProcess is a stub)
                    if (_initialized)
                        UpdateKeys();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    XMLogging.WriteLine($"[InputManager] Worker thread error: {ex.Message}");
                }
                finally
                {
                    Thread.Sleep(KEY_CHECK_DELAY);
                }
            }

            XMLogging.WriteLine("[InputManager] Worker thread exiting.");
        }

        #endregion

        #region Types

        private class KeyActionHandler
        {
            public int ActionId { get; set; }
            public string ActionName { get; set; }
            public KeyStateChangedHandler Handler { get; set; }
        }

        public class KeyEventArgs : EventArgs
        {
            public int KeyCode { get; }
            public bool IsPressed { get; }

            public KeyEventArgs(int keyCode, bool isPressed)
            {
                KeyCode = keyCode;
                IsPressed = isPressed;
            }
        }

        public delegate void KeyStateChangedHandler(object sender, KeyEventArgs e);

        #endregion
    }
}
