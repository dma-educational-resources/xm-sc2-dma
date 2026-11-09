global using SC2Radar.DMA;
global using SC2Radar.DMA.ScatterAPI;

using SC2Radar.SC2;
using SC2Radar.Common.Misc;
using VmmSharpEx.Options;
using VmmSharpEx.Scatter;
using SC2Radar.DMA.ScatterAPI;
using SC2Radar.UI.ImGui;

namespace SC2Radar.DMA
{
    /// <summary>
    /// The DMA connection and its worker threads (SC2Workers: realtime positions, misc state, scans, memory writes), and
    /// the radar window's view of the attach state.
    /// </summary>
    internal static class Memory
    {
        static SC2Memory? _mem;
        static Config?    _cfg;
        static SC2Workers? _workers;
        static volatile bool _stop;

        public static SC2Memory?  Instance   => _mem;
        public static bool        IsConnected => _mem?.IsConnected ?? false;
        public static bool        InGame      => _mem?.InGame      ?? false;
        public static VmmSharpEx.Vmm? VmmHandle => _mem?.Vmm;   // InputManager reads the gaming PC's keyboard through it
        public static bool        IsDisposed  => _stop;

        /// <summary>Stop the worker threads (session end, shutdown); the DMA handle is left to the process exit.</summary>
        public static void Close() { _stop = true; _workers?.Stop(); }

        public static void DisconnectOtherVmmConnections()
        {
            // Kill any leftover LeechCore/VMM handles from a prior crash so ours can open cleanly.
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("LeechAgent")) p.Kill();
            }
            catch { }
        }

        /// <summary>
        /// Safe Mode: running without a DMA connection (DMA initialization failed at startup, or another startup error
        /// and the user chose Safe Mode). The whole UI works - settings, panels, widgets, profiles - but nothing is read
        /// from the game. The status badge retries.
        /// </summary>
        public static bool        SafeMode    => _mem == null;
        /// <summary>The full "DMA Initialization Failed!" report from the last failed connect (null once connected).</summary>
        public static string?     ConnectError { get; private set; }
        /// <summary><see cref="ConnectError"/> without its troubleshooting checklist, for toasts and tooltips.</summary>
        public static string?     ConnectErrorSummary =>
            ConnectError is { } e && e.IndexOf("===TROUBLESHOOTING===", StringComparison.Ordinal) is > 0 and var i
                ? e[..i].TrimEnd() : ConnectError;
        /// <summary>A retry from the UI is opening the FPGA (takes a few seconds).</summary>
        public static bool        Connecting  => _connecting;
        static volatile bool      _connecting;
        static readonly object    _connectLock = new();

        public static void Start(Config cfg)
        {
            _cfg  = cfg;
            _stop = false;
            if (TryConnect(out _)) XMLogging.WriteLine("DMA Memory Interface initialized - Normal Mode");
            else XMLogging.WriteLine("Safe Memory Interface initialized - Safe Mode (DMA disabled)");
        }

        /// <summary>Safe Mode after a startup error that wasn't the DMA's: no connect attempt, but the badge can still retry.</summary>
        public static void StartSafeMode(Config cfg)
        {
            _cfg  = cfg;
            _stop = false;
            XMLogging.WriteLine("Safe Memory Interface initialized - Safe Mode (DMA disabled)");
        }

        /// <summary>Opens the FPGA and starts the worker threads and the hotkey reader. False (Safe Mode stays on) when the
        /// device can't be opened - vmm.dll throws, or is missing.</summary>
        public static bool TryConnect(out string? error)
        {
            lock (_connectLock)
            {
                if (_mem != null) { error = null; return true; }
                XMLogging.WriteLine("Initializing DMA...");
                SC2Memory mem;
                try { mem = new SC2Memory(_cfg!); }
                catch (Exception ex)
                {
                    ConnectError = error = InitFailedMessage(ex);
                    XMLogging.WriteLine(error);
                    return false;
                }
                _mem = mem;
                ConnectError = error = null;
                _workers = new SC2Workers(mem, _cfg!);
                _workers.Start();

                // hotkeys: the gaming PC's keyboard, read over DMA (its own worker thread; retries until it finds the key table)
                try { InputManager.Initialize(); }
                catch (Exception ex) { XMLogging.WriteLine($"[Memory] InputManager init failed: {ex.Message}"); }
                XMLogging.WriteLine("DMA Initialized!");
                return true;
            }
        }

        /// <summary>The EFT radar's startup report: reason, the native DLL versions and the troubleshooting checklist (less
        /// its mmap.txt step - the SC2 radar doesn't use a memory map).</summary>
        static string InitFailedMessage(Exception ex)
        {
            static string Ver(string dll)
            {
                try { return FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "unknown"; }
                catch { return "not found"; }
            }
            return "DMA Initialization Failed!\n" +
                   $"Reason: {ex.Message}\n" +
                   $"Vmm Version: {Ver("vmm.dll")}\n" +
                   $"Leechcore Version: {Ver("leechcore.dll")}\n\n" +
                   "===TROUBLESHOOTING===\n" +
                   "1. Reboot both your Game PC / Radar PC (This USUALLY fixes it).\n" +
                   "2. Reseat all cables/connections and make sure they are secure.\n" +
                   "3. Make sure all Setup Steps are completed (See DMA Setup Guide/FAQ for additional troubleshooting).";
        }

        /// <summary>Retry from the UI, off the render thread (opening the FPGA blocks for a few seconds); toasts the result.</summary>
        public static void RetryConnect()
        {
            if (_connecting || !SafeMode) return;
            _connecting = true;
            Task.Run(() =>
            {
                try
                {
                    DisconnectOtherVmmConnections();
                    if (TryConnect(out _))
                    {
                        XMLogging.WriteLine("[Program] DMA reconnected - now running in Normal Mode.");
                        ToastManager.Show("DMA Connected", "Normal Mode is now active.", ToastStyle.Success);
                    }
                    else ToastManager.Show("DMA Reconnect Failed", ConnectErrorSummary ?? "Unknown error", ToastStyle.Danger, duration: 6f);
                }
                finally { _connecting = false; }
            });
        }

        public static void Stop()
        {
            _stop = true;
            // every reader out of its loop before the DMA handle closes: closing it under a read can hang the process
            _workers?.Stop();
            InputManager.WaitStopped(500);
            _mem?.Dispose();
        }
    }
}
