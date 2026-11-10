// Creates the DX11 device + radar window and runs the render loop on a
// dedicated high-priority thread.

using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SC2Radar.UI.ImGui.Menus;
using SC2Radar.UI.ImGui.Radar;
using SC2Radar.UI.ImGui.Widgets;
using SC2Radar.UI.Misc;
using SC2Radar.UI;

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static partial class ImGuiApp
    {
        // ── Public surface ────────────────────────────────────────────────────
        public static bool IsRunning    { get; private set; }
        public static bool Initialized  { get; private set; }

        public static int TargetFPS
        {
            get => _targetFps;
            set { _targetFps = value; _frameNs = value > 0 ? 1_000_000_000L / value : 0L; }
        }

        // ── Internal ──────────────────────────────────────────────────────────
        private static int  _targetFps = 60;
        private static long _frameNs   = 1_000_000_000L / 60;

        private static SharpDX.Direct3D11.Device? _device;
        private static IntPtr _sharedFontAtlas;

        private static DX11Window _radarWindow = null!;

        private static volatile bool _pendingToggleMaximize = false;
        private static volatile bool _pendingMinimize       = false;

        private static Thread        _renderThread = null!;
        private static volatile bool _stopRequested;
        private static volatile bool _pendingFontReload;
        private static volatile bool _pendingLayoutReinit;
        private static volatile bool _pendingWindowApply;

        // ── Sub-systems initialized on render thread ───────────────────────
        private static RadarWindow  _radar = null!;
        private static MenuManager  _menus = null!;

        private static readonly string _iniPath =
            Path.Combine(Program.DataDir, "imgui_layout.ini");

        // ── Entry point ───────────────────────────────────────────────────────

        public static void Run()
        {
            IsRunning = true;

            _renderThread = new Thread(RenderLoop)
            {
                Name         = "ImGuiRenderThread",
                Priority     = ThreadPriority.Highest,
                IsBackground = false
            };
            _renderThread.Start();

            _renderThread.Join();
            IsRunning = false;
        }

        public static void RequestStop() => _stopRequested = true;

        // ── Render thread ─────────────────────────────────────────────────────

        private static void RenderLoop()
        {
            Win32.timeBeginPeriod(1);
            try
            {
                Init();
                Initialized = true;

                long freq       = Stopwatch.Frequency;
                var  sw         = Stopwatch.StartNew();
                long nextTick   = sw.ElapsedTicks;

                while (!_stopRequested && !_radarWindow.WantsClose)
                {
                    try
                    {
                        long nowTicks = sw.ElapsedTicks;

                        if (nowTicks < nextTick)
                        {
                            long waitNs  = (nextTick - nowTicks) * 1_000_000_000L / freq;
                            long sleepMs = (waitNs - 2_000_000L) / 1_000_000L;
                            if (sleepMs > 1) Thread.Sleep((int)sleepMs);
                            while (sw.ElapsedTicks < nextTick) Thread.SpinWait(8);
                            continue;
                        }

                        long interval = _frameNs > 0 ? _frameNs * freq / 1_000_000_000L : 0L;
                        if (interval > 0)
                        {
                            nextTick += interval;
                            if (nowTicks - nextTick > interval * 3L) nextTick = nowTicks;
                        }
                        else
                        {
                            nextTick = nowTicks;
                        }

                        DX11Window.PumpMessages();
                        _radarWindow.ApplyPendingResize();

                        _radarWindow.NewFrame();
                        BuildRadarFrame();
                        _radarWindow.Render(new SharpDX.Mathematics.Interop.RawColor4(0.07f, 0.07f, 0.07f, 1f));

                        ApplyPendingWindowActions();
                    }
                    catch (Exception frameEx)
                    {
                        XMLogging.Write(AppLogLevel.Error, $"[ImGuiApp] Frame error (skipped): {frameEx}");
                    }
                }
            }
            catch (Exception ex)
            {
                XMLogging.Write(AppLogLevel.Error, $"[ImGuiApp] Fatal render loop error: {ex}");
            }
            finally
            {
                Win32.timeEndPeriod(1);
                Shutdown();
            }
        }

        // ── Init / Shutdown ───────────────────────────────────────────────────

        private static void Init()
        {
            var cfg = Program.Config;
            TargetFPS = cfg.TargetFPS;

            // Restore debug console visibility from last session
            XMLogging.SetConsoleVisible(cfg.ShowDebugConsole);

            int rW = Math.Max(cfg.WindowWidth,  300);
            int rH = Math.Max(cfg.WindowHeight, 200);

            _device = new SharpDX.Direct3D11.Device(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport);

            unsafe { _sharedFontAtlas = (IntPtr)ImGuiNET.ImGuiNative.ImFontAtlas_ImFontAtlas(); }

            _radarWindow = new DX11Window(_device, "SC2 DMA Radar", rW, rH, isOverlay: false,
                sharedFontAtlas: _sharedFontAtlas);

            _radarWindow.ClearAndPresent(new SharpDX.Mathematics.Interop.RawColor4(0.07f, 0.07f, 0.07f, 1f));

            // Clamp UIScale before building font atlas
            if (cfg.UIScale < 0.5f || cfg.UIScale > 2.0f)
            {
                cfg.UIScale = 1f;
                cfg.SaveDebounced();
            }

            var extraFontPaths = GatherExtraFontPaths();

            _radarWindow.MakeCurrent();
            ImGuiStyle.LoadFonts(cfg.UIScale, null, extraFontPaths);
            ImGuiStyle.Apply();

            if (File.Exists(_iniPath))
                ImGuiNET.ImGui.LoadIniSettingsFromDisk(_iniPath);

            try
            {
                _radarWindow.RebuildFontTexture();
            }
            catch
            {
                cfg.UIScale = 1f;
                cfg.SaveDebounced();
                ImGuiStyle.LoadFonts(1f, null, extraFontPaths);
                _radarWindow.RebuildFontTexture();
            }

            _radar = new RadarWindow(_radarWindow);
            _menus = new MenuManager(_radarWindow);

            RadarColorOptions.LoadColors(Program.Config);
            InterfaceColorOptions.LoadColors(Program.Config);

            // Restore window state from config
            RequestWindowApply();
        }

        private static void Shutdown()
        {
            try
            {
                _radarWindow?.MakeCurrent();
                ImGuiNET.ImGui.SaveIniSettingsToDisk(_iniPath);
            }
            catch (Exception ex)
            {
                XMLogging.Write(AppLogLevel.Warning, $"[ImGuiApp] Failed to save layout ini: {ex.Message}");
            }

            _radar?.Dispose();
            _menus?.Dispose();
            _radarWindow?.Dispose();

            if (_sharedFontAtlas != IntPtr.Zero)
            {
                unsafe { ImGuiNET.ImGuiNative.ImFontAtlas_destroy((ImGuiNET.ImFontAtlas*)_sharedFontAtlas); }
                _sharedFontAtlas = IntPtr.Zero;
            }

            _device?.Dispose();
            _device = null;

            Program.Config.Save();
        }

        // ── Per-frame build ───────────────────────────────────────────────────

        private static void BuildRadarFrame()
        {
            _menus.PollHotkeys();

            var io = ImGuiNET.ImGui.GetIO();

            ImGuiNET.ImGui.SetNextWindowPos(Vector2.Zero);
            ImGuiNET.ImGui.SetNextWindowSize(io.DisplaySize);
            ImGuiNET.ImGui.SetNextWindowBgAlpha(0f);

            var hostFlags =
                ImGuiWindowFlags.NoTitleBar  |
                ImGuiWindowFlags.NoCollapse  |
                ImGuiWindowFlags.NoResize    |
                ImGuiWindowFlags.NoMove      |
                ImGuiWindowFlags.NoNavFocus  |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.MenuBar     |
                (ImGuiWindowFlags)(1 << 13);  // NoBringToDisplayFront

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));

            ImGuiNET.ImGui.Begin("##DockHost", hostFlags);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopStyleVar(2);

            _menus.DrawMenuBar();

            uint dockId = ImGuiNET.ImGui.GetID("MainDockSpace");
            ImGuiNET.ImGui.DockSpace(dockId, new Vector2(0, 0),
                ImGuiDockNodeFlags.PassthruCentralNode);

            ImGuiNET.ImGui.End();

            // Radar canvas - below menu bar
            float menuH = ImGuiNET.ImGui.GetFrameHeight();
            var (dL, dR, dT, dB) = _menus.GetDockMargins();
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(dL, menuH + dT), ImGuiCond.Always);
            ImGuiNET.ImGui.SetNextWindowSize(
                new Vector2(io.DisplaySize.X - dL - dR, io.DisplaySize.Y - menuH - dT - dB),
                ImGuiCond.Always);
            _radar.Draw();

            _menus.DrawAllPanels();

            _menus.Widgets?.DrawWidgets();

            // overlays on top of everything (both were never drawn, so toasts and confirm dialogs didn't show)
            ToastManager.Draw();
            Program.PollDmaInitFailureModal();
            ConfirmModal.Draw();
        }

        // ── Pending actions ───────────────────────────────────────────────────

        private static void ApplyPendingWindowActions()
        {
            if (_pendingMinimize)
            {
                _pendingMinimize = false;
                _radarWindow?.Minimize();
            }
            if (_pendingToggleMaximize)
            {
                _pendingToggleMaximize = false;
                _radarWindow?.ToggleMaximize();
            }
            if (_pendingFontReload)
            {
                _pendingFontReload = false;
                ReloadFonts();
            }
            if (_pendingLayoutReinit)
            {
                _pendingLayoutReinit = false;
                _menus?.ReapplyLayout();
            }
            if (_pendingWindowApply)
            {
                _pendingWindowApply = false;
                var cfg = Program.Config;
                if (cfg.WindowMaximized)
                {
                    if (!Win32.IsZoomed(_radarWindow.Hwnd))
                        Win32.ShowWindow(_radarWindow.Hwnd, Win32.SW_SHOWMAXIMIZED);
                }
                else
                {
                    if (Win32.IsZoomed(_radarWindow.Hwnd))
                        Win32.ShowWindow(_radarWindow.Hwnd, Win32.SW_RESTORE);
                    int x = cfg.WindowX >= 0 ? cfg.WindowX : 100;
                    int y = cfg.WindowY >= 0 ? cfg.WindowY : 100;
                    _radarWindow.MoveToMonitor(x, y, cfg.WindowWidth, cfg.WindowHeight);
                }
            }
        }

        private static void ReloadFonts()
        {
            var cfg = Program.Config;
            var extraPaths = GatherExtraFontPaths();

            _radarWindow.MakeCurrent();
            ImGuiStyle.LoadFonts(cfg.UIScale, null, extraPaths);
            try
            {
                _radarWindow.RebuildFontTexture();
            }
            catch
            {
                cfg.UIScale = 1f;
                cfg.SaveDebounced();
                ImGuiStyle.LoadFonts(1f, null, extraPaths);
                _radarWindow.RebuildFontTexture();
            }
        }

        internal static List<string> GatherExtraFontPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string[] sysFiles = {
                "arial.ttf", "arialbd.ttf", "ariali.ttf",
                "calibri.ttf", "calibrib.ttf",
                "consolas.ttf",
                "segoeui.ttf", "segoeuib.ttf",
                "tahoma.ttf", "tahomabd.ttf",
                "verdana.ttf", "verdanab.ttf",
            };
            foreach (var f in sysFiles)
            {
                var p = Path.Combine(sysDir, f);
                if (File.Exists(p)) paths.Add(p);
            }

            string userDir = Path.Combine(Program.DataDir, "Fonts");
            if (Directory.Exists(userDir))
                foreach (var f in Directory.GetFiles(userDir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".ttf" || ext == ".otf") paths.Add(f);
                }

            return [.. paths];
        }

        // ── Public helpers ────────────────────────────────────────────────────

        public static void RequestFontReload()      => _pendingFontReload    = true;
        public static void RequestLayoutReinit()    => _pendingLayoutReinit  = true;
        public static void RequestWindowApply()     => _pendingWindowApply   = true;
        public static void MinimizeWindow()         => _pendingMinimize      = true;
        public static void ToggleMaximizeWindow()   => _pendingToggleMaximize = true;
        public static void CloseMainWindow()        => _radarWindow?.RequestClose();

        public static bool IsWindowMaximized => _radarWindow?.IsMaximized ?? false;

        public static MenuManager Menus => _menus;
        public static RadarWindow Radar => _radar;
    }
}
