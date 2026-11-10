// Startup splash - a genuine ImGui window (its own DX11 device + swap chain + ImGui
// context, exactly the same DX11Window/ImGuiStyle/UIControls stack the main app uses),
// not a WinForms/GDI+ stand-in. It runs on its own dedicated thread before
// Program.Main's startup (config, DMA connect - on the main thread) and is closed before
// ImGuiApp.Init() creates the real app window.
//
// LoadingWindow.Create(visible: false) skips the window - startup progress then only goes
// to the log. When not visible, this degrades to a pure logging stub (no window/thread at all), so
// callers never need to null-check - see LoadingWindow.Safe.
//
// Cross-thread updates: unlike the old WinForms version's BeginInvoke marshaling, ImGui
// has no message-pump-based invoke mechanism, so the main thread's ReportProgress calls
// just write into a lock-protected (status, percent) pair that the render loop reads once
// per frame - no ImGui call ever happens off this class's own render thread.
//
// Layout is a fixed pixel grid via ImGui's SetCursorPos, not auto-flow - every position is
// a hand-placed constant, never derived from a measured font/control size, for the same
// reason the WinForms version settled on that after chained font-metric math repeatedly
// drifted content past the window's edge (see git history on this file for the saga).

using System.Diagnostics;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using SC2Radar.UI.ImGui;
using Device = SharpDX.Direct3D11.Device;
using ImGuiStyle = SC2Radar.UI.ImGui.ImGuiStyle;
using SC2Radar.UI.Misc;

namespace SC2Radar.Common.UI
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class LoadingWindow : IDisposable
    {
        /// <summary>Current progress percentage (0–100).</summary>
        public int PercentComplete { get; private set; }

        /// <summary>Current status message.</summary>
        public string StatusMessage { get; private set; } = "";

        private readonly Splash? _splash;
        private readonly Thread? _thread;

        private LoadingWindow(Splash? splash, Thread? thread = null)
        {
            _splash = splash;
            _thread = thread;
        }

        /// <summary>
        /// Update status and optionally the progress percentage.
        /// A negative percent leaves the bar where it is.
        /// </summary>
        public void UpdateStatus(string message, int percent = -1)
        {
            StatusMessage = message ?? "";
            if (percent >= 0) PercentComplete = percent;
            XMLogging.WriteLine($"[Loading] {StatusMessage}");
            _splash?.UpdateStatus(StatusMessage, PercentComplete);
        }

        /// <summary>Update progress percentage (0–100).</summary>
        public void SetProgress(int percent)
        {
            PercentComplete = Math.Clamp(percent, 0, 100);
            XMLogging.WriteLine($"[Loading] {PercentComplete}%");
            _splash?.UpdateStatus(StatusMessage, PercentComplete);
        }

        public void Show() { }
        public void Hide() { }

        /// <summary>
        /// Requests the splash to stop and blocks until its render thread has actually
        /// exited - which means its DX11Window and ImGui context are fully torn down.
        /// Dear ImGui's current-context pointer is a single process-wide global, not
        /// thread-local, so returning before the splash thread finishes disposing its
        /// context would let it race the main render thread's own SetCurrentContext /
        /// GetIO calls in ImGuiApp.Init() - the splash's teardown could null out the
        /// global context (or steal it back mid-frame) right as the main thread reads
        /// it, producing an intermittent NullReferenceException in ImGuiIOPtr.Fonts.
        /// </summary>
        public void Close()
        {
            _splash?.RequestClose();
            _thread?.Join();
        }
        public void Dispose() => Close();

        // ── Null-safety helper ──────────────────────────────────────────────
        private static readonly LoadingWindow _noop = new(null);

        /// <summary>
        /// Returns <paramref name="instance"/> if non-null, otherwise a shared no-op instance.
        /// Eliminates null-reference crashes when callers pass null.
        /// </summary>
        public static LoadingWindow Safe(LoadingWindow instance) => instance ?? _noop;

        /// <summary>
        /// Creates the splash on its own dedicated thread and blocks until its window/ImGui
        /// context/fonts are ready. When <paramref name="visible"/> is false, no window/thread
        /// is created at all - the returned instance just logs (see UpdateStatus).
        /// </summary>
        public static LoadingWindow Create(bool visible = true)
        {
            if (!visible)
                return new LoadingWindow(null);

            Splash? splash = null;
            var thread = new Thread(() =>
            {
                splash = new Splash();
                splash.RunLoop();
            })
            { IsBackground = true, Name = "LoadingWindowRenderThread" };
            thread.Start();

            while (splash is null || !splash.IsReady)
                Thread.Sleep(1);

            return new LoadingWindow(splash, thread);
        }

        // ──────────────────────────────────────────────────────────────────────
        // The actual splash - its own DX11 device, window, and ImGui context
        // ──────────────────────────────────────────────────────────────────────

        private sealed class Splash
        {
            // ── Fixed layout grid ────────────────────────────────────────────
            private const int   WinW         = 500;
            private const int   WinH         = 172;
            private const float CornerRadius = 10f;
            private const int   HeaderH      = 26;
            private const int   ContentPadX  = 50;   // → 400px content column
            private const int   TopGap       = 30;   // header bottom -> status top
            private const int   MidGap       = 16;   // status bottom -> bar top
            private const int   StatusH      = 26;
            private const int   BarH         = 16;
            private const int   FooterH      = 26;
            private const int   BottomPad    = 14;

            private const string LinkText = "xm-dma";
            private const string LinkUrl  = "https://x0m.dev";

            private static readonly RawColor4 BgClear = new(0x10 / 255f, 0x10 / 255f, 0x14 / 255f, 1f);

            private Device? _device;
            private DX11Window? _window;
            private volatile bool _stopRequested;
            public volatile bool IsReady;

            private readonly object _lock = new();
            private string _status = "Starting...";
            private int _percent;

            public void UpdateStatus(string status, int percent)
            {
                lock (_lock) { _status = status; _percent = percent; }
            }

            public void RequestClose() => _stopRequested = true;

            public void RunLoop()
            {
                try
                {
                    _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);

                    var mon = MonitorInfo.GetPrimaryMonitor();
                    int x = mon.Left + (mon.Width  - WinW) / 2;
                    int y = mon.Top  + (mon.Height - WinH) / 2;

                    _window = new DX11Window(_device, Program.Name, WinW, WinH, isOverlay: false,
                        sharedFontAtlas: default, initialX: x, initialY: y);

                    // Real OS-level rounded corners - this is a standalone window, unlike the
                    // main app's panels which fake rounding by drawing their own rounded rects
                    // inside a deliberately-square host window (see BuildRadarFrame's
                    // WindowRounding=0 push). After SetWindowRgn succeeds the OS owns the HRGN.
                    int d = (int)CornerRadius * 2;
                    IntPtr rgn = Win32.CreateRoundRectRgn(0, 0, WinW, WinH, d, d);
                    Win32.SetWindowRgn(_window.Hwnd, rgn, true);

                    // Paint the correct background immediately - otherwise the window shows
                    // white/default until the first ImGui frame, which can take a moment while
                    // fonts build (same reasoning as ImGuiApp.Init()'s own ClearAndPresent call).
                    _window.ClearAndPresent(BgClear);

                    _window.MakeCurrent();
                    var extraFonts = ImGuiApp.GatherExtraFontPaths();
                    ImGuiStyle.LoadFonts(1f, null, extraFonts);
                    ImGuiStyle.Apply();
                    _window.RebuildFontTexture();

                    IsReady = true;

                    while (!_stopRequested && !_window.WantsClose)
                    {
                        DX11Window.PumpMessages();
                        _window.ApplyPendingResize();
                        _window.NewFrame();
                        BuildFrame();
                        _window.Render(BgClear);
                        Thread.Sleep(16); // ~60fps - plenty for a static splash
                    }
                }
                catch (Exception ex)
                {
                    XMLogging.WriteLine($"[Loading] Splash render loop error: {ex}");
                }
                finally
                {
                    _window?.Dispose();
                    _device?.Dispose();
                }
            }

            private void BuildFrame()
            {
                string status; int percent;
                lock (_lock) { status = _status; percent = _percent; }

                var io = ImGuiNET.ImGui.GetIO();
                ImGuiNET.ImGui.SetNextWindowPos(Vector2.Zero);
                ImGuiNET.ImGui.SetNextWindowSize(io.DisplaySize);

                const ImGuiWindowFlags flags =
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.NoSavedSettings;

                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                // 0, not ImGuiStyle's usual panel rounding - the real corners are already
                // handled at the OS level via SetWindowRgn; rounding the ImGui fill on top of
                // that would just double up and risk a 1px mismatch against the OS clip.
                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, ColF(ImGuiStyle.Dark2));
                ImGuiNET.ImGui.Begin("##Splash", flags);

                var dl = ImGuiNET.ImGui.GetWindowDrawList();
                Vector2 wp = ImGuiNET.ImGui.GetWindowPos();

                // Header strip
                dl.AddRectFilled(wp, new Vector2(wp.X + WinW, wp.Y + HeaderH), ImGuiStyle.Dark3);

                var titleFont = ImGuiStyle.FontBold;
                float titleFs = titleFont.FontSize > 0f ? titleFont.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                ImGuiNET.ImGui.PushFont(titleFont);
                ImGuiNET.ImGui.SetCursorPos(new Vector2(20f, (HeaderH - titleFs) * 0.5f));
                ImGuiNET.ImGui.TextColored(ColF(ImGuiStyle.Text), Program.Name);
                ImGuiNET.ImGui.PopFont();

                // Status - centered, bold, truncated to the content width rather than wrapped
                // (a long status would otherwise run off the window).
                int statusY = HeaderH + TopGap;
                var statusFont = ImGuiStyle.FontBold;
                float statusFs = statusFont.FontSize > 0f ? statusFont.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
                float maxStatusW = WinW - ContentPadX * 2;
                string statusDisp = TruncateToWidth(statusFont, statusFs, status, maxStatusW);
                Vector2 statusSz = statusFont.CalcTextSizeA(statusFs, float.MaxValue, -1f, statusDisp);
                ImGuiNET.ImGui.PushFont(statusFont);
                ImGuiNET.ImGui.SetCursorPos(new Vector2((WinW - statusSz.X) * 0.5f, statusY));
                ImGuiNET.ImGui.TextColored(ColF(ImGuiStyle.Text), statusDisp);
                ImGuiNET.ImGui.PopFont();

                // Progress bar - real UI.ProgressBar from UIControls.cs, same track/fill/shimmer
                // language as every slider elsewhere in the app.
                int barY = statusY + StatusH + MidGap;
                ImGuiNET.ImGui.SetCursorPos(new Vector2(ContentPadX, barY));
                SC2Radar.UI.ImGui.UI.ProgressBar(percent / 100f, WinW - ContentPadX * 2, BarH, $"{percent}%");

                // Footer - "Powered by xm-dma", xm-dma is a live link
                int footerY = WinH - FooterH - BottomPad;
                DrawFooter(dl, footerY);

                ImGuiNET.ImGui.End();
                ImGuiNET.ImGui.PopStyleColor();
                ImGuiNET.ImGui.PopStyleVar(2);
            }

            private void DrawFooter(ImDrawListPtr dl, int footerY)
            {
                var font  = ImGuiStyle.FontNormal;
                var bold  = ImGuiStyle.FontBold;
                float fs  = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

                const string poweredBy = "Powered by ";
                Vector2 poweredSz = font.CalcTextSizeA(fs, float.MaxValue, -1f, poweredBy);
                Vector2 linkSz    = bold.CalcTextSizeA(fs, float.MaxValue, -1f, LinkText);
                float totalW = poweredSz.X + linkSz.X;
                float startX = (WinW - totalW) / 2f;

                ImGuiNET.ImGui.SetCursorPos(new Vector2(startX, footerY));
                ImGuiNET.ImGui.PushFont(font);
                ImGuiNET.ImGui.TextColored(ColF(ImGuiStyle.TextDim), poweredBy);
                ImGuiNET.ImGui.PopFont();

                ImGuiNET.ImGui.SameLine(0, 0);
                Vector2 linkTL = ImGuiNET.ImGui.GetCursorScreenPos();
                ImGuiNET.ImGui.PushFont(bold);
                ImGuiNET.ImGui.TextColored(ColF(ImGuiStyle.Accent), LinkText);
                ImGuiNET.ImGui.PopFont();

                bool linkHovered = ImGuiNET.ImGui.IsItemHovered();
                if (linkHovered)
                {
                    ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    dl.AddLine(
                        new Vector2(linkTL.X, linkTL.Y + fs),
                        new Vector2(linkTL.X + linkSz.X, linkTL.Y + fs),
                        ImGuiStyle.Accent, 1f);

                    if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = LinkUrl, UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            XMLogging.WriteLine($"[Loading] Failed to open link: {ex.Message}");
                        }
                    }
                }
            }

            private static string TruncateToWidth(ImFontPtr font, float fontSize, string text, float maxWidth)
            {
                if (string.IsNullOrEmpty(text)) return text;
                if (font.CalcTextSizeA(fontSize, float.MaxValue, -1f, text).X <= maxWidth) return text;

                const string ellipsis = "…";
                float ellipsisW = font.CalcTextSizeA(fontSize, float.MaxValue, -1f, ellipsis).X;
                int lo = 0, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    float w = font.CalcTextSizeA(fontSize, float.MaxValue, -1f, text[..mid]).X + ellipsisW;
                    if (w <= maxWidth) lo = mid; else hi = mid - 1;
                }
                return lo <= 0 ? ellipsis : text[..lo] + ellipsis;
            }

            private static Vector4 ColF(uint col)
            {
                float r = (col & 0xFF) / 255f;
                float g = ((col >> 8) & 0xFF) / 255f;
                float b = ((col >> 16) & 0xFF) / 255f;
                float a = ((col >> 24) & 0xFF) / 255f;
                return new Vector4(r, g, b, a);
            }
        }
    }
}
