// Core DX11 + ImGui.NET backend.
// Owns two windows: the main radar/menu window and the transparent ESP overlay.
// All ImGui frames are driven from a single dedicated render thread.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using ImGuiNET;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace SC2Radar.UI.ImGui
{
    /// <summary>
    /// Manages a single DX11 swap-chain window (radar OR ESP overlay).
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class DX11Window : IDisposable
    {
        // ???? Win32 ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        public IntPtr Hwnd { get; private set; }
        private string _className;
        private Win32.WNDPROC _wndProcDelegate; // keep alive

        // ???? DX11 ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        private readonly Device _device;
        private readonly DeviceContext _ctx;

        /// <summary>Shared DX11 device used by this window.</summary>
        public Device Device => _device;
        private SwapChain _swapChain;
        private RenderTargetView _rtv;
        private int _width, _height;
        private int _pendingResizeW, _pendingResizeH;

        /// <summary>Current client width/height, in pixels.</summary>
        public int Width  => _width;
        public int Height => _height;

        // ???? ImGui ??????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        private ImGuiImplDX11 _imguiImpl;
        private IntPtr _imguiCtx;

        public bool WantsClose { get; private set; }
        public bool IsOverlay { get; }
        public string Title { get; }

        /// <summary>Last known screen X of the restored (non-maximized) window (-1 = unknown).</summary>
        public int WindowX { get; private set; } = -1;
        /// <summary>Last known screen Y of the restored (non-maximized) window (-1 = unknown).</summary>
        public int WindowY { get; private set; } = -1;
        /// <summary>True when the main (non-overlay) window is maximized.</summary>
        public bool IsMaximized => !IsOverlay && Win32.IsZoomed(Hwnd);

        public DX11Window(Device device, string title, int w, int h, bool isOverlay,
                          IntPtr? parentHwnd = null, IntPtr sharedFontAtlas = default,
                          int? initialX = null, int? initialY = null)
        {
            _device = device;
            _ctx    = device.ImmediateContext;
            Title   = title;
            IsOverlay = isOverlay;
            _width  = w;
            _height = h;

            CreateWin32Window(title, w, h, isOverlay, parentHwnd, initialX, initialY);
            CreateSwapChain();
            CreateRTV();
            InitImGui(sharedFontAtlas);
        }

        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        // Win32 window creation
        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????

        private void CreateWin32Window(string title, int w, int h, bool overlay, IntPtr? parent,
                                        int? initialX = null, int? initialY = null)
        {
            _className = "DX11_" + Guid.NewGuid().ToString("N")[..8];
            _wndProcDelegate = WndProc;

            // Taskbar / title-bar icon. Popouts (parent != null) get the gear icon
            // so their taskbar entries are visually distinct from the main radar.
            bool isPopout = parent != null && parent.Value != IntPtr.Zero;
            IntPtr hIcon = isPopout ? SC2Radar.UI.ImGui.WindowIcons.GearHIcon : SC2Radar.UI.ImGui.WindowIcons.AppHIcon;

            var wc = new Win32.WNDCLASSEX
            {
                cbSize        = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                style         = Win32.CS_HREDRAW | Win32.CS_VREDRAW,
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance     = Win32.GetModuleHandle(null),
                hIcon         = hIcon,
                hIconSm       = hIcon,
                hCursor       = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW),
                lpszClassName = _className
            };
            Win32.RegisterClassEx(ref wc);

            uint exStyle, style;
            if (overlay)
            {
                // Transparent, taskbar-visible, click-through when not in menu mode
                exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_APPWINDOW | Win32.WS_EX_TRANSPARENT;
                style   = Win32.WS_POPUP;
            }
            else
            {
                // Borderless with OS resize support; custom title bar drawn in ImGui
                exStyle = Win32.WS_EX_APPWINDOW;
                style   = Win32.WS_POPUP | Win32.WS_THICKFRAME;
            }

            var cfg = Program.Config;
            int posX = initialX ?? (overlay ? 0 : (cfg.WindowX >= 0 ? cfg.WindowX : 100));
            int posY = initialY ?? (overlay ? 0 : (cfg.WindowY >= 0 ? cfg.WindowY : 100));

            Hwnd = Win32.CreateWindowEx(
                exStyle, _className, title, style,
                posX, posY,
                w, h,
                parent ?? IntPtr.Zero,
                IntPtr.Zero,
                Win32.GetModuleHandle(null),
                IntPtr.Zero);

            if (overlay)
            {
                // Full alpha channel via DWM
                Win32.SetLayeredWindowAttributes(Hwnd, 0, 255, Win32.LWA_ALPHA);
                Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
            }
            else
            {
                // Popouts (explicit initialX/Y) always spawn normally - never maximized.
                bool startMaximized = initialX is null && cfg.WindowMaximized;
                Win32.ShowWindow(Hwnd, startMaximized ? Win32.SW_SHOWMAXIMIZED : Win32.SW_SHOW);

                // Win32 quirk: the FIRST ShowWindow call the whole process makes has its
                // nCmdShow silently overridden by the process's inherited STARTUPINFO show
                // state (see the ShowWindow docs) - if this process was launched
                // hidden/minimized (e.g. by the xmLauncher), whichever window happens to make
                // that first call absorbs the override instead of the state it actually asked
                // for. Deliberately NOT gated on initialX being null: the startup splash
                // (LoadingWindow) is itself now a DX11Window with an explicit initialX/Y (it
                // centers itself on screen), so it - not the main window - is normally the
                // first one created, and needs this guard just as much. Harmless for a genuine
                // popout too: it would never legitimately land iconic from a plain
                // ShowWindow(SW_SHOW), so this just never fires for those.
                if (!startMaximized && Win32.IsIconic(Hwnd))
                {
                    Win32.ShowWindow(Hwnd, Win32.SW_RESTORE);
                    Win32.SetForegroundWindow(Hwnd);
                }

                // Enable DWM drop shadow on the borderless window
                var shadowMargins = new Win32.MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
                Win32.DwmExtendFrameIntoClientArea(Hwnd, ref shadowMargins);
            }
        }

        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        // DX11
        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????

        private void CreateSwapChain()
        {
            var sd = new SwapChainDescription
            {
                BufferCount       = 1,
                ModeDescription   = new ModeDescription(
                    _width, _height, new Rational(60, 1), Format.R8G8B8A8_UNorm),
                IsWindowed        = true,
                OutputHandle      = Hwnd,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect        = SwapEffect.Discard,
                Usage             = Usage.RenderTargetOutput,
                Flags             = SwapChainFlags.None
            };

            // Must use the factory that created the device's adapter -
            // constructing a new Factory1() independently causes DXGI_ERROR_INVALID_CALL.
            using var dxgiDev = _device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDev.GetParent<SharpDX.DXGI.Adapter>();
            using var factory = adapter.GetParent<SharpDX.DXGI.Factory>();

            _swapChain = new SwapChain(factory, _device, sd);
            factory.MakeWindowAssociation(Hwnd, SharpDX.DXGI.WindowAssociationFlags.IgnoreAltEnter);
        }

        private void CreateRTV()
        {
            using var bb = _swapChain.GetBackBuffer<Texture2D>(0);
            _rtv = new RenderTargetView(_device, bb);
        }

        private void DisposeRTV()
        {
            _rtv?.Dispose();
            _rtv = null;
        }

        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        // ImGui
        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????

        /// <summary>Native pointer to the font SRV uploaded by this window's backend.</summary>
        public IntPtr FontSrvNativePtr => _imguiImpl?.FontSrvNativePtr ?? IntPtr.Zero;

        /// <summary>
        /// Points this context at an already-uploaded font texture owned by another window.
        /// Call after the primary window has called RebuildFontTexture().
        /// </summary>
        public void UseSharedFontTexture(IntPtr srvNativePtr)
        {
            MakeCurrent();
            _imguiImpl?.UseSharedFontTexture(srvNativePtr);
        }

        private void InitImGui(IntPtr sharedFontAtlas = default)
        {
            unsafe
            {
                _imguiCtx = sharedFontAtlas != IntPtr.Zero
                    ? ImGuiNET.ImGuiNative.igCreateContext((ImGuiNET.ImFontAtlas*)sharedFontAtlas)
                    : ImGuiNET.ImGuiNative.igCreateContext(null);
            }
            ImGuiNET.ImGui.SetCurrentContext(_imguiCtx);

            var io = ImGuiNET.ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            if (!IsOverlay)
                io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

            // Our DX11 backend passes cmd.VtxOffset as the base vertex location in
            // DrawIndexed, so advertise RendererHasVtxOffset. This lets ImGui auto-split
            // a draw list into multiple draw commands at the 16-bit (64k) vertex boundary
            // instead of overflowing/wrapping - required for dense loot/ESP without
            // corrupting the overlay.
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

            // Disable automatic imgui.ini - we handle load/save manually via _iniPath in ImGuiApp
            unsafe { io.NativePtr->IniFilename = null; }

            io.DisplaySize = new Vector2(_width, _height);

            _imguiImpl = new ImGuiImplDX11(_device, _ctx, Hwnd);
        }

        /// <summary>Sets this window's ImGui context as current.</summary>
        public void MakeCurrent() => ImGuiNET.ImGui.SetCurrentContext(_imguiCtx);

        /// <summary>Rebuilds the backend font texture after ImGui fonts are changed.</summary>
        public void RebuildFontTexture()
        {
            MakeCurrent();
            _imguiImpl?.RebuildFontTexture();
        }

        public void NewFrame()
        {
            MakeCurrent();
            _imguiImpl.NewFrame();
        }

        /// <summary>
        /// Optional callback fired after the RTV is cleared but before ImGui issues any draw calls.
        /// Use for 3D content that should appear behind the 2D overlay (e.g. skinned character models).
        /// Signature: (DeviceContext ctx, RenderTargetView rtv, int width, int height)
        /// </summary>
        public Action<SharpDX.Direct3D11.DeviceContext, SharpDX.Direct3D11.RenderTargetView, int, int>? PreImGuiRender;

        public void Render(SharpDX.Mathematics.Interop.RawColor4 clearColor)
        {
            MakeCurrent();
            ImGuiNET.ImGui.Render();

            // _rtv can be null if a resize threw between DisposeRTV and CreateRTV.
            // Always end the ImGui frame (above) but skip DX11 work to avoid a
            // native AV on ClearRenderTargetView(null).
            if (_rtv == null) return;

            _ctx.OutputMerger.SetRenderTargets(_rtv);
            _ctx.ClearRenderTargetView(_rtv, new SharpDX.Mathematics.Interop.RawColor4(clearColor.R, clearColor.G, clearColor.B, clearColor.A));

            PreImGuiRender?.Invoke(_ctx, _rtv, _width, _height);

            _imguiImpl.RenderDrawData(ImGuiNET.ImGui.GetDrawData());
            _swapChain.Present(0, PresentFlags.None); // vsync=0 for max fps
        }

        /// <summary>
        /// Paints the window a solid color without ImGui.
        /// Call during startup (before the font atlas is ready) so the window
        /// shows the correct background color instead of a white OS default.
        /// </summary>
        public void ClearAndPresent(SharpDX.Mathematics.Interop.RawColor4 clearColor)
        {
            _ctx.OutputMerger.SetRenderTargets(_rtv);
            _ctx.ClearRenderTargetView(_rtv, clearColor);
            _swapChain.Present(0, PresentFlags.None);
        }

public void Resize(int w, int h)
{
    if (w <= 0 || h <= 0)
        return;

    if (w == _width && h == _height)
        return;

    // WM_SIZE can fire during CreateWindowEx/ShowWindow before swapchain exists.
    if (_swapChain == null)
    {
        _width = w;
        _height = h;
        return;
    }

    // Unbind ALL pipeline state first so the D3D11 runtime releases every
    // internal reference to the back buffer (RTV, shader resources, etc.)
    // before we dispose our own wrapper and call ResizeBuffers.
    // ClearState() is more thorough than SetRenderTargets(null) alone and
    // matches the MSDN-recommended pattern for resize.
    _ctx.ClearState();
    DisposeRTV();
    _ctx.Flush();

    _swapChain.ResizeBuffers(
        0,
        w,
        h,
        Format.Unknown,
        SwapChainFlags.None);

    // Only update tracked dimensions after a successful resize so that a
    // failed ResizeBuffers (DXGI_ERROR_INVALID_CALL etc.) doesn't poison the
    // equality check at the top and prevent a retry on the next frame.
    _width = w;
    _height = h;

    CreateRTV();
    _imguiImpl?.FlushTextureSrvCache();

    if (_imguiCtx != IntPtr.Zero)
    {
        MakeCurrent();
        ImGuiNET.ImGui.GetIO().DisplaySize = new Vector2(w, h);
    }
}

/// <summary>
/// Apply the latest pending resize (stored by WM_SIZE) exactly once per frame.
/// Call this after PumpMessages() to collapse all queued WM_SIZE events into a
/// single ResizeBuffers() call instead of one per queued message.
/// </summary>
public void ApplyPendingResize()
{
    int w = _pendingResizeW;
    int h = _pendingResizeH;
    if (w <= 0 || h <= 0)
        return;
    _pendingResizeW = _pendingResizeH = 0;
    Resize(w, h);
}

        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        // Window state control (main window only)
        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????

        public void Minimize()
        {
            if (!IsOverlay) Win32.ShowWindow(Hwnd, Win32.SW_MINIMIZE);
        }

        public void ToggleMaximize()
        {
            if (IsOverlay) return;
            Win32.ShowWindow(Hwnd, Win32.IsZoomed(Hwnd) ? Win32.SW_RESTORE : Win32.SW_SHOWMAXIMIZED);
        }

        public void RequestClose() => WantsClose = true;

        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        // Overlay click-through toggle
        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????

        private bool _clickThrough = true;

        /// <summary>
        /// When menus are open on the overlay, disable click-through so mouse reaches ImGui.
        /// When menus are hidden, re-enable so game mouse works normally.
        /// </summary>
        public void SetClickThrough(bool clickThrough)
        {
            if (!IsOverlay || _clickThrough == clickThrough) return;
            _clickThrough = clickThrough;

            var exStyle = (uint)Win32.GetWindowLong(Hwnd, Win32.GWL_EXSTYLE);
            if (clickThrough)
                exStyle |= Win32.WS_EX_TRANSPARENT;
            else
                exStyle &= ~Win32.WS_EX_TRANSPARENT;

            Win32.SetWindowLong(Hwnd, Win32.GWL_EXSTYLE, (int)exStyle);
        }

        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        // Monitor placement
        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????

        /// <summary>Moves and resizes the overlay to fill a specific monitor rectangle.</summary>
        public void MoveToMonitor(int x, int y, int w, int h)
        {
            // SetWindowPos on the owning thread delivers WM_SIZE synchronously,
            // which calls Resize() to update the swap chain and ImGui display size.
            Win32.SetWindowPos(Hwnd, IntPtr.Zero, x, y, w, h,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        /// <summary>Toggles this window between always-on-top and normal z-order.</summary>
        public void SetTopmost(bool topmost)
        {
            Win32.SetWindowPos(Hwnd, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
                0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        }

        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
        // WndProc
        // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????

private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
{
    try
    {
        // Ensure keyboard/char events are delivered to THIS window's ImGui context,
        // not whichever context happens to be current at the time of the callback.
        MakeCurrent();
        if (_imguiImpl != null && _imguiImpl.HandleWndProc(hwnd, msg, wParam, lParam))
            return IntPtr.Zero;

        // Borderless window: handle resize edges only.
        // Dragging is handled inside ImGui via an InvisibleButton + WM_NCLBUTTONDOWN
        // so that menu items and other ImGui controls in the toolbar receive clicks normally.
        if (msg == Win32.WM_NCHITTEST && !IsOverlay)
        {
            int sx = Win32.GET_X_LPARAM(lParam);
            int sy = Win32.GET_Y_LPARAM(lParam);

            if (Win32.IsZoomed(hwnd))
            {
                // When maximized the frame may extend a few pixels into the taskbar
                // row (the non-client strip between client.Bottom and frame.Bottom).
                // Return HTTRANSPARENT for any position outside the work area so the
                // taskbar receives those clicks instead of our window eating them.
                var hMon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
                if (hMon != IntPtr.Zero)
                {
                    var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                    if (Win32.GetMonitorInfo(hMon, ref mi))
                    {
                        if (sx < mi.rcWork.Left  || sx >= mi.rcWork.Right ||
                            sy < mi.rcWork.Top   || sy >= mi.rcWork.Bottom)
                            return (IntPtr)Win32.HTTRANSPARENT;
                    }
                }
                return (IntPtr)Win32.HTCLIENT;
            }

            Win32.GetWindowRect(hwnd, out var wr);
            int wx = sx - wr.Left;
            int wy = sy - wr.Top;
            int ww = wr.Right  - wr.Left;
            int wh = wr.Bottom - wr.Top;

            const int border = 5; // resize grip thickness in pixels

            bool top    = wy < border;
            bool bottom = wy > wh - border;
            bool left   = wx < border;
            bool right  = wx > ww - border;

            if (top    && left)  return (IntPtr)Win32.HTTOPLEFT;
            if (top    && right) return (IntPtr)Win32.HTTOPRIGHT;
            if (bottom && left)  return (IntPtr)Win32.HTBOTTOMLEFT;
            if (bottom && right) return (IntPtr)Win32.HTBOTTOMRIGHT;
            if (top)    return (IntPtr)Win32.HTTOP;
            if (bottom) return (IntPtr)Win32.HTBOTTOM;
            if (left)   return (IntPtr)Win32.HTLEFT;
            if (right)  return (IntPtr)Win32.HTRIGHT;

            return (IntPtr)Win32.HTCLIENT;
        }

        switch (msg)
        {
            case Win32.WM_NCCALCSIZE:
                // Return 0 when wParam=TRUE to collapse the non-client area to zero.
                // When maximized: MINMAXINFO already constrains the window frame so
                // that frame.Right = rcWork.Right and frame.Bottom = rcWork.Bottom,
                // so we just assign rcWork directly - no strip math needed.
                // MonitorFromRect on the proposed rect picks the target monitor
                // reliably even before the window physically moves there.
                if (!IsOverlay && wParam != IntPtr.Zero)
                {
                    if (Win32.IsZoomed(hwnd) && lParam != IntPtr.Zero)
                    {
                        var p = Marshal.PtrToStructure<Win32.NCCALCSIZE_PARAMS>(lParam);
                        var hMon = Win32.MonitorFromRect(ref p.rgrc0, Win32.MONITOR_DEFAULTTONEAREST);
                        if (hMon != IntPtr.Zero)
                        {
                            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                            if (Win32.GetMonitorInfo(hMon, ref mi))
                            {
                                p.rgrc0 = mi.rcWork;
                                Marshal.StructureToPtr(p, lParam, false);
                            }
                        }
                    }
                    return IntPtr.Zero;
                }
                break;

            case Win32.WM_GETMINMAXINFO:
            {
                if (!IsOverlay && lParam != IntPtr.Zero)
                {
                    var mmi = Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
                    mmi.ptMinTrackSize.X = 300;
                    mmi.ptMinTrackSize.Y = 200;
                    // MonitorFromWindow is reliable here: WM_GETMINMAXINFO fires before
                    // the window moves, so the hwnd is still physically on the monitor
                    // the user triggered maximize from.
                    //
                    // Windows only pre-fills ptMaxPosition/ptMaxSize for the PRIMARY
                    // monitor by default - a WS_POPUP window (no WS_CAPTION) never gets
                    // the multi-monitor-aware default the way a normal WS_OVERLAPPEDWINDOW
                    // does. Left untouched, that's exactly why maximize snapped back to
                    // whichever monitor the window originally spawned on instead of the
                    // one it was just dragged to. So both position AND size must be set
                    // explicitly here.
                    //
                    // ptMaxPosition/ptMaxSize are relative to the TARGET monitor's own
                    // origin, not absolute screen coordinates - the previous attempt at
                    // this used absolute rcWork coordinates directly, which happened to
                    // work on the primary monitor (origin (0,0)) but shifted the window
                    // off past the correct bounds / cut off content on any secondary
                    // monitor whose origin isn't (0,0).
                    //
                    // No frame-thickness padding here: WM_NCCALCSIZE below unconditionally
                    // forces the final client rect to mi.rcWork once the window is zoomed,
                    // so padding this outer rect doesn't change anything except create a
                    // gap between the outer window rect and the (smaller) client rect -
                    // i.e. a strip of genuine non-client area. On a monitor with a taskbar
                    // that strip lands inside the taskbar's reserved space rather than off
                    // the physical screen, so it doesn't get clipped - Windows paints it as
                    // plain white NC chrome (the bar seen at the bottom of a maximized
                    // window). Using the exact work-area numbers keeps outer rect ==
                    // client rect == rcWork, so there's no NC strip to paint at all.
                    var hMon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
                    if (hMon != IntPtr.Zero)
                    {
                        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                        if (Win32.GetMonitorInfo(hMon, ref mi))
                        {
                            mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                            mmi.ptMaxPosition.Y = mi.rcWork.Top  - mi.rcMonitor.Top;
                            mmi.ptMaxSize.X = mi.rcWork.Right  - mi.rcWork.Left;
                            mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                        }
                    }
                    Marshal.StructureToPtr(mmi, lParam, false);
                }
                break;
            }

            case Win32.WM_MOVE:
            {
                // Track restored (non-maximized) position so we can persist it
                if (!IsOverlay && !Win32.IsZoomed(hwnd))
                {
                    WindowX = Win32.GET_X_LPARAM(lParam);
                    WindowY = Win32.GET_Y_LPARAM(lParam);
                }
                break;
            }

            case Win32.WM_SIZE:
            {
                if (wParam.ToInt32() == 1) // SIZE_MINIMIZED
                    break;

                int w = Win32.LOWORD((uint)lParam);
                int h = Win32.HIWORD((uint)lParam);

                // Only store the latest pending size - ApplyPendingResize() will
                // call Resize() exactly once per frame (after all WM_SIZE events
                // in the pump batch are consumed), preventing multiple expensive
                // ResizeBuffers() calls per frame during rapid drag.
                if (w > 0 && h > 0)
                {
                    _pendingResizeW = w;
                    _pendingResizeH = h;
                }

                break;
            }

            case Win32.WM_CLOSE:
                WantsClose = true;
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                break;

            case Win32.WM_QUERYENDSESSION:
                // Windows shutdown/logoff/restart is asking if we can close. Kick off our
                // own graceful shutdown path and allow the session to end - returning FALSE
                // here would just delay/block the user's shutdown, not help us.
                WantsClose = true;
                return new IntPtr(1);

            case Win32.WM_ENDSESSION:
                // The session is actually ending - very little time left before Windows can
                // force-terminate us, so close the DMA/FPGA handle synchronously right here
                // rather than trusting the normal render-loop teardown to get there in time.
                if (wParam != IntPtr.Zero)
                {
                    try { Memory.Close(); }
                    catch (Exception ex) { XMLogging.Write(AppLogLevel.Error, $"[DX11Window] Memory.Close() failed during WM_ENDSESSION: {ex}"); }
                }
                break;
        }
    }
    catch (Exception ex)
    {
        XMLogging.Write(AppLogLevel.Error, $"[DX11Window] WndProc exception msg=0x{msg:X}: {ex}");
        return IntPtr.Zero;
    }

    return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
}

        public static void PumpMessages()
        {
            while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
        }

        public void Dispose()
        {
            _imguiImpl?.Dispose();
            _imguiImpl = null; // prevent WndProc from calling HandleWndProc on the disposed backend
            DisposeRTV();
            _swapChain?.Dispose();
            // Destroy the Win32 window first: DestroyWindow dispatches WM_DESTROY synchronously
            // to WndProc, which calls MakeCurrent(). The context must still be valid at that point.
            // Destroying the context first and then the window causes MakeCurrent + HandleWndProc
            // to dereference a freed ImGui context, which produces ExecutionEngineException.
            Win32.DestroyWindow(Hwnd);
            ImGuiNET.ImGui.DestroyContext(_imguiCtx);
            Win32.UnregisterClass(_className, Win32.GetModuleHandle(null));
        }
    }

    // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
    // Thin DX11 ImGui implementation wrapper
    // ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Wraps the ImGui DX11 backend calls (NewFrame / RenderDrawData / WndProc).
    /// Mirrors what imgui_impl_dx11.cpp does, implemented in managed C#.
    /// </summary>
    public sealed class ImGuiImplDX11 : IDisposable
    {
        private readonly Device _device;
        private readonly DeviceContext _ctx;
        private readonly IntPtr _hwnd;

        // Accumulated scroll from WM_MOUSEWHEEL (applied in NewFrame after context switch)
        private float _pendingMouseWheel;

        // Font texture
        private Texture2D _fontTex;
        private ShaderResourceView _fontSRV;

        // Vertex / Index buffers
        private Buffer _vb;
        private Buffer _ib;
        private int _vbSize;
        private int _ibSize;

        // Shaders
        private VertexShader _vs;
        private PixelShader  _ps;
        private InputLayout  _layout;
        private Buffer       _cb;       // constant buffer (MVP)

        // Blend / rasterizer / depth / sampler state
        private BlendState        _blend;
        private RasterizerState   _raster;
        private DepthStencilState _depth;
        private SamplerState      _sampler;

        // TextureId -> SRV wrapper cache for ImGui draw commands.
        // Wrappers that own exactly one AddRef'd COM reference to the underlying SRV.
        // FlushTextureSrvCache() releases those references and is safe to call at any time.
        private readonly Dictionary<IntPtr, ShaderResourceView> _textureSrvCache = new();

        // Win32 time tracking
        private long _lastTime;
        private long _tickFreq;
        private const int WIN32_INPUT_BUFFER = 256;

        public ImGuiImplDX11(Device device, DeviceContext ctx, IntPtr hwnd)
        {
            _device = device;
            _ctx    = ctx;
            _hwnd   = hwnd;

            QueryPerformanceFrequency(out _tickFreq);
            QueryPerformanceCounter(out _lastTime);

            CreateDeviceObjects();
        }

        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long freq);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long count);

        // HLSL is compiled at runtime so CreateVertexShader never receives stale/corrupt DXBC bytes.
        private const string ImGuiVertexShaderHlsl = @"
cbuffer vertexBuffer : register(b0)
{
    float4x4 ProjectionMatrix;
};

struct VS_INPUT
{
    float2 pos : POSITION;
    float2 uv  : TEXCOORD0;
    float4 col : COLOR0;
};

struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float4 col : COLOR0;
    float2 uv  : TEXCOORD0;
};

PS_INPUT main(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.0f, 1.0f));
    output.col = input.col;
    output.uv  = input.uv;
    return output;
}
";

        private const string ImGuiPixelShaderHlsl = @"
struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float4 col : COLOR0;
    float2 uv  : TEXCOORD0;
};

SamplerState sampler0 : register(s0);
Texture2D texture0 : register(t0);

float4 main(PS_INPUT input) : SV_Target
{
    return input.col * texture0.Sample(sampler0, input.uv);
}
";

        // ???? Shaders (HLSL compiled inline via FXC bytecode arrays) ????????????????????????
        // For brevity we embed minimal pre-compiled shader bytecode.
        // In production replace with proper CSO files or runtime compilation.

        // Precompiled DXBC shader bytecode from Dear ImGui dx11 backend
        private static readonly byte[] _vsBytes = new byte[]
        {
            0x44, 0x58, 0x42, 0x43, 0xf2, 0x7e, 0x14, 0x37, 0x9d, 0x64, 0xe7, 0x48, 0xda, 0x4c, 0x6c, 0x03,
            0xf6, 0x82, 0x72, 0x9e, 0x01, 0x00, 0x00, 0x00, 0x7c, 0x02, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00,
            0x34, 0x00, 0x00, 0x00, 0xac, 0x00, 0x00, 0x00, 0x14, 0x01, 0x00, 0x00, 0x88, 0x01, 0x00, 0x00,
            0x00, 0x02, 0x00, 0x00, 0x52, 0x44, 0x45, 0x46, 0x70, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x48, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x1c, 0x00, 0x00, 0x00, 0x00, 0x04, 0xfe, 0xff,
            0x00, 0x01, 0x00, 0x00, 0x47, 0x00, 0x00, 0x00, 0x3c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x76, 0x65, 0x72, 0x74, 0x65, 0x78, 0x42, 0x75,
            0x66, 0x66, 0x65, 0x72, 0x00, 0x4d, 0x69, 0x63, 0x72, 0x6f, 0x73, 0x6f, 0x66, 0x74, 0x20, 0x28,
            0x52, 0x29, 0x20, 0x48, 0x4c, 0x53, 0x4c, 0x20, 0x53, 0x68, 0x61, 0x64, 0x65, 0x72, 0x20, 0x43,
            0x6f, 0x6d, 0x70, 0x69, 0x6c, 0x65, 0x72, 0x20, 0x31, 0x30, 0x2e, 0x31, 0x00, 0x49, 0x53, 0x47,
            0x4e, 0x60, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x50, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x03, 0x00, 0x00, 0x59, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x62, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00,
            0x00, 0x0f, 0x0f, 0x00, 0x00, 0x50, 0x4f, 0x53, 0x49, 0x54, 0x49, 0x4f, 0x4e, 0x00, 0x54, 0x45,
            0x58, 0x43, 0x4f, 0x4f, 0x52, 0x44, 0x00, 0x43, 0x4f, 0x4c, 0x4f, 0x52, 0x00, 0x4f, 0x53, 0x47,
            0x4e, 0x6c, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x50, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x0f, 0x00, 0x00, 0x00, 0x5c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x00, 0x65, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00,
            0x00, 0x03, 0x0c, 0x00, 0x00, 0x53, 0x56, 0x5f, 0x50, 0x4f, 0x53, 0x49, 0x54, 0x49, 0x4f, 0x4e,
            0x00, 0x43, 0x4f, 0x4c, 0x4f, 0x52, 0x00, 0x54, 0x45, 0x58, 0x43, 0x4f, 0x4f, 0x52, 0x44, 0x00,
            0x53, 0x48, 0x44, 0x52, 0x70, 0x00, 0x00, 0x00, 0x40, 0x00, 0x01, 0x00, 0x1c, 0x00, 0x00, 0x00,
            0x59, 0x00, 0x00, 0x04, 0x46, 0x8e, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00,
            0x5f, 0x00, 0x00, 0x03, 0x32, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5f, 0x00, 0x00, 0x03,
            0x32, 0x10, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x5f, 0x00, 0x00, 0x03, 0xf2, 0x10, 0x10, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x67, 0x00, 0x00, 0x04, 0xf2, 0x20, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x65, 0x00, 0x00, 0x03, 0xf2, 0x20, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x65, 0x00, 0x00, 0x03, 0x32, 0x20, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00, 0x02,
            0x01, 0x00, 0x00, 0x00, 0x38, 0x00, 0x00, 0x08, 0xf2, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x56, 0x15, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46, 0x8e, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0a, 0xf2, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x46, 0x8e, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x10, 0x10, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x46, 0x0e, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0a,
            0xf2, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46, 0x8e, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0xa6, 0x1a, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46, 0x0e, 0x10, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0a, 0xf2, 0x20, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x46, 0x8e, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xf6, 0x1e, 0x10, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x46, 0x0e, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x05,
            0xf2, 0x20, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x46, 0x1e, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00,
            0x36, 0x00, 0x00, 0x05, 0x32, 0x20, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00, 0x46, 0x10, 0x10, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x3e, 0x00, 0x00, 0x01, 0x53, 0x54, 0x41, 0x54, 0x74, 0x00, 0x00, 0x00,
            0x07, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
        };

        // Precompiled DXBC shader bytecode from Dear ImGui dx11 backend
        private static readonly byte[] _psBytes = new byte[]
        {
            0x44, 0x58, 0x42, 0x43, 0x57, 0x5e, 0x8b, 0xa5, 0x18, 0xa2, 0xce, 0xb3, 0xb0, 0x54, 0x24, 0xb6,
            0x4d, 0x2c, 0xa9, 0xa0, 0x01, 0x00, 0x00, 0x00, 0x14, 0x02, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00,
            0x34, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0xb8, 0x00, 0x00, 0x00, 0xec, 0x00, 0x00, 0x00,
            0x98, 0x01, 0x00, 0x00, 0x52, 0x44, 0x45, 0x46, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x1c, 0x00, 0x00, 0x00, 0x00, 0x04, 0xff, 0xff,
            0x00, 0x01, 0x00, 0x00, 0x1b, 0x00, 0x00, 0x00, 0x5c, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6e, 0x75, 0x6c, 0x6c, 0x00, 0x4d, 0x69, 0x63,
            0x72, 0x6f, 0x73, 0x6f, 0x66, 0x74, 0x20, 0x28, 0x52, 0x29, 0x20, 0x48, 0x4c, 0x53, 0x4c, 0x20,
            0x53, 0x68, 0x61, 0x64, 0x65, 0x72, 0x20, 0x43, 0x6f, 0x6d, 0x70, 0x69, 0x6c, 0x65, 0x72, 0x20,
            0x31, 0x30, 0x2e, 0x31, 0x00, 0x49, 0x53, 0x47, 0x4e, 0x30, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00,
            0x00, 0x08, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0f, 0x0f, 0x00, 0x00, 0x29, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
            0x00, 0x03, 0x03, 0x00, 0x00, 0x43, 0x4f, 0x4c, 0x4f, 0x52, 0x00, 0x54, 0x45, 0x58, 0x43, 0x4f,
            0x4f, 0x52, 0x44, 0x00, 0x4f, 0x53, 0x47, 0x4e, 0x2c, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x08, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x00, 0x53, 0x56, 0x5f, 0x54,
            0x61, 0x72, 0x67, 0x65, 0x74, 0x00, 0x53, 0x48, 0x44, 0x52, 0xa4, 0x00, 0x00, 0x00, 0x40, 0x00,
            0x00, 0x00, 0x29, 0x00, 0x00, 0x00, 0x5a, 0x00, 0x00, 0x03, 0x00, 0x60, 0x10, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x58, 0x18, 0x00, 0x04, 0x00, 0x70, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x55, 0x55,
            0x00, 0x00, 0x62, 0x10, 0x00, 0x03, 0xf2, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x62, 0x10,
            0x00, 0x03, 0x32, 0x10, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x65, 0x00, 0x00, 0x03, 0xf2, 0x20,
            0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x45, 0x00,
            0x00, 0x09, 0xf2, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46, 0x10, 0x10, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x46, 0x7e, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x10, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x38, 0x00, 0x00, 0x07, 0xf2, 0x20, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46, 0x0e,
            0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46, 0x1e, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x00,
            0x00, 0x01, 0x53, 0x54, 0x41, 0x54, 0x74, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        };

        private void CreateDeviceObjects()
        {
            // Shaders
            // Do NOT use the old embedded _vsBytes/_psBytes blobs here. The uploaded log
            // showed E_INVALIDARG from CreateVertexShader, which means the bytecode passed
            // to D3D11 was invalid for this device/runtime.
            using var vsCompile = ShaderBytecode.Compile(
                ImGuiVertexShaderHlsl,
                "main",
                "vs_4_0",
                ShaderFlags.OptimizationLevel3,
                EffectFlags.None);

            if (vsCompile.HasErrors)
                throw new InvalidOperationException($"ImGui vertex shader compile failed:\n{vsCompile.Message}");

            using var psCompile = ShaderBytecode.Compile(
                ImGuiPixelShaderHlsl,
                "main",
                "ps_4_0",
                ShaderFlags.OptimizationLevel3,
                EffectFlags.None);

            if (psCompile.HasErrors)
                throw new InvalidOperationException($"ImGui pixel shader compile failed:\n{psCompile.Message}");

            _vs = new VertexShader(_device, vsCompile.Bytecode);
            _ps = new PixelShader(_device, psCompile.Bytecode);

            // Input layout must be created from the exact compiled VS bytecode.
            _layout = new InputLayout(_device, vsCompile.Bytecode, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32_Float,    0,  0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float,    8,  0),
                new InputElement("COLOR",    0, Format.R8G8B8A8_UNorm, 16,  0),
            });

            // Constant buffer
            _cb = new Buffer(_device, new BufferDescription
            {
                SizeInBytes    = 64, // 4x4 float matrix
                Usage          = ResourceUsage.Dynamic,
                BindFlags      = BindFlags.ConstantBuffer,
                CpuAccessFlags = CpuAccessFlags.Write
            });

            // Blend
            var bd = new BlendStateDescription { AlphaToCoverageEnable = false };
            bd.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled        = true,
                SourceBlend           = BlendOption.SourceAlpha,
                DestinationBlend      = BlendOption.InverseSourceAlpha,
                BlendOperation        = BlendOperation.Add,
                SourceAlphaBlend      = BlendOption.InverseDestinationAlpha,
                DestinationAlphaBlend = BlendOption.One,
                AlphaBlendOperation   = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteMaskFlags.All
            };
            _blend = new BlendState(_device, bd);

            // Rasterizer
            _raster = new RasterizerState(_device, new RasterizerStateDescription
            {
                FillMode              = FillMode.Solid,
                CullMode              = CullMode.None,
                IsDepthClipEnabled    = true,
                IsScissorEnabled      = true,
                IsMultisampleEnabled  = false,
                IsAntialiasedLineEnabled = false
            });

            // Depth
            _depth = new DepthStencilState(_device, new DepthStencilStateDescription
            {
                IsDepthEnabled   = false,
                IsStencilEnabled = false,
                DepthWriteMask   = DepthWriteMask.All
            });

            // Sampler
            _sampler = new SamplerState(_device, new SamplerStateDescription
            {
                Filter            = Filter.MinMagMipLinear,
                AddressU          = TextureAddressMode.Wrap,
                AddressV          = TextureAddressMode.Wrap,
                AddressW          = TextureAddressMode.Wrap,
                ComparisonFunction = Comparison.Always,
                MinimumLod        = 0,
                MaximumLod        = float.MaxValue
            });

            // Font texture - if the shared atlas already has a TexID the primary context
            // has already uploaded it; reuse that SRV instead of calling GetTexDataAsRGBA32()
            // (pixel data was freed by ClearTexData() after the primary upload).
            var existingTexId = ImGuiNET.ImGui.GetIO().Fonts.TexID;
            if (existingTexId != IntPtr.Zero)
                UseSharedFontTexture(existingTexId);
            else
                UploadFontTexture();
        }

        /// <summary>Native pointer to the font SRV owned by this backend instance.</summary>
        public IntPtr FontSrvNativePtr => _fontSRV?.NativePointer ?? IntPtr.Zero;

        /// <summary>
        /// Points this context at a font texture already uploaded by the primary window.
        /// No atlas build or texture upload - just registers the SRV pointer.
        /// The texture is owned by the primary; this instance must not dispose it.
        /// </summary>
        public void UseSharedFontTexture(IntPtr srvNativePtr)
        {
            ImGuiNET.ImGui.GetIO().Fonts.SetTexID(srvNativePtr);
            ImGuiNET.ImGui.GetIO().Fonts.ClearTexData();
        }

        public void RebuildFontTexture()
        {
            _fontSRV?.Dispose();
            _fontSRV = null;

            _fontTex?.Dispose();
            _fontTex = null;

            ImGuiNET.ImGui.GetIO().Fonts.SetTexID(IntPtr.Zero);
            UploadFontTexture();
        }

        private void UploadFontTexture()
        {
            ImGuiNET.ImGui.GetIO().Fonts.GetTexDataAsRGBA32(
                out IntPtr pixels, out int w, out int h, out int bpp);

            _fontTex = new Texture2D(_device, new Texture2DDescription
            {
                Width     = w, Height = h, MipLevels = 1, ArraySize = 1,
                Format    = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage     = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags  = CpuAccessFlags.None,
                OptionFlags     = ResourceOptionFlags.None
            }, new DataRectangle(pixels, w * bpp));

            _fontSRV = new ShaderResourceView(_device, _fontTex);
            ImGuiNET.ImGui.GetIO().Fonts.SetTexID(_fontSRV.NativePointer);
            ImGuiNET.ImGui.GetIO().Fonts.ClearTexData();
        }

        public void NewFrame()
        {
            // Update delta time
            QueryPerformanceCounter(out long now);
            float dt = (float)(now - _lastTime) / _tickFreq;
            _lastTime = now;

            var io = ImGuiNET.ImGui.GetIO();
            io.DeltaTime = dt > 0 ? dt : 1f / 60f;

            // Only feed mouse input to ImGui when this window is in the foreground.
            // If another window (browser, etc.) is on top, suppress all mouse state so
            // hovering/clicking through the panel cannot accidentally trigger controls.
            bool focused = Win32.GetForegroundWindow() == _hwnd;

            // Apply accumulated mouse wheel delta from WM_MOUSEWHEEL messages
            io.MouseWheel      = focused ? _pendingMouseWheel : 0f;
            _pendingMouseWheel = 0f;

            // Mouse position
            if (focused && Win32.GetCursorPos(out var pt))
            {
                Win32.ScreenToClient(_hwnd, ref pt);
                io.MousePos = new Vector2(pt.X, pt.Y);
            }
            else
            {
                io.MousePos = new Vector2(-float.MaxValue, -float.MaxValue);
            }

            // Mouse buttons
            io.MouseDown[0] = focused && (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0; // LMB
            io.MouseDown[1] = focused && (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0; // RMB
            io.MouseDown[2] = focused && (Win32.GetAsyncKeyState(0x04) & 0x8000) != 0; // MMB

            ImGuiNET.ImGui.NewFrame();
        }

        public void RenderDrawData(ImDrawDataPtr drawData)
        {
            if (drawData.CmdListsCount == 0) return;

            int totalVtx = drawData.TotalVtxCount;
            int totalIdx = drawData.TotalIdxCount;
            if (totalVtx == 0 || totalIdx == 0) return;

            // Grow vertex/index buffers if needed
            EnsureVB(totalVtx);
            EnsureIB(totalIdx);

            // Upload geometry
            var vtxDst = _ctx.MapSubresource(_vb, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            var idxDst = _ctx.MapSubresource(_ib, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            long vtxOff = vtxDst.DataPointer.ToInt64();
            long idxOff = idxDst.DataPointer.ToInt64();

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var cmdList = drawData.CmdLists[n];
                int vtxBytes = cmdList.VtxBuffer.Size * 20; // ImDrawVert = 20 bytes
                int idxBytes = cmdList.IdxBuffer.Size * 2;  // ImDrawIdx  = 2 bytes
                unsafe
                {
                    System.Buffer.MemoryCopy(
                        (void*)cmdList.VtxBuffer.Data, (void*)vtxOff, vtxBytes, vtxBytes);
                    System.Buffer.MemoryCopy(
                        (void*)cmdList.IdxBuffer.Data, (void*)idxOff, idxBytes, idxBytes);
                }
                vtxOff += vtxBytes;
                idxOff += idxBytes;
            }
            _ctx.UnmapSubresource(_vb, 0);
            _ctx.UnmapSubresource(_ib, 0);

            // Update constant buffer (orthographic projection)
            float L = drawData.DisplayPos.X;
            float R = drawData.DisplayPos.X + drawData.DisplaySize.X;
            float T = drawData.DisplayPos.Y;
            float B = drawData.DisplayPos.Y + drawData.DisplaySize.Y;

            var mvp = new Matrix4x4(
                2f/(R-L),    0,           0, 0,
                0,           2f/(T-B),    0, 0,
                0,           0,           0.5f, 0,
                (R+L)/(L-R), (T+B)/(B-T), 0.5f, 1f);

            var cbData = _ctx.MapSubresource(_cb, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(mvp, cbData.DataPointer, false);
            _ctx.UnmapSubresource(_cb, 0);

            // Set pipeline state
            _ctx.InputAssembler.InputLayout      = _layout;
            _ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            _ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vb, 20, 0));
            _ctx.InputAssembler.SetIndexBuffer(_ib, Format.R16_UInt, 0);
            _ctx.VertexShader.Set(_vs);
            _ctx.VertexShader.SetConstantBuffer(0, _cb);
            _ctx.PixelShader.Set(_ps);
            _ctx.PixelShader.SetSampler(0, _sampler);
            _ctx.OutputMerger.SetBlendState(_blend, null, 0xFFFFFFFF);
            _ctx.OutputMerger.SetDepthStencilState(_depth, 0);
            _ctx.Rasterizer.State = _raster;

            var vp = new SharpDX.Mathematics.Interop.RawViewportF
            {
                X = 0, Y = 0,
                Width  = drawData.DisplaySize.X,
                Height = drawData.DisplaySize.Y,
                MinDepth = 0, MaxDepth = 1
            };
            _ctx.Rasterizer.SetViewport(vp);

            // Draw commands
            int globalVtxOffset = 0;
            int globalIdxOffset = 0;

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var cmdList = drawData.CmdLists[n];
                for (int ci = 0; ci < cmdList.CmdBuffer.Size; ci++)
                {
                    var cmd = cmdList.CmdBuffer[ci];
                    if (cmd.UserCallback != IntPtr.Zero)
                    {
                        // User callbacks not supported in this impl
                        continue;
                    }

                    var clipRect = new SharpDX.Mathematics.Interop.RawRectangle(
                        (int)(cmd.ClipRect.X - drawData.DisplayPos.X),
                        (int)(cmd.ClipRect.Y - drawData.DisplayPos.Y),
                        (int)(cmd.ClipRect.Z - drawData.DisplayPos.X),
                        (int)(cmd.ClipRect.W - drawData.DisplayPos.Y));

                    _ctx.Rasterizer.SetScissorRectangle(
                        clipRect.Left, clipRect.Top, clipRect.Right, clipRect.Bottom);

                    var texSRV = GetShaderResourceView(cmd.GetTexID());
                    _ctx.PixelShader.SetShaderResource(0, texSRV);

                    _ctx.DrawIndexed(
                        (int)cmd.ElemCount,
                        (int)(cmd.IdxOffset + globalIdxOffset),
                        (int)(cmd.VtxOffset + globalVtxOffset));
                }
                globalVtxOffset += cmdList.VtxBuffer.Size;
                globalIdxOffset += cmdList.IdxBuffer.Size;
            }
        }

        private ShaderResourceView GetShaderResourceView(IntPtr textureId)
        {
            // When using a shared font texture (secondary window), _fontSRV is null.
            // Fall through to the cache path which wraps the shared native pointer.
            IntPtr fontPtr = _fontSRV?.NativePointer ?? IntPtr.Zero;
            if (fontPtr != IntPtr.Zero && (textureId == IntPtr.Zero || textureId == fontPtr))
                return _fontSRV;

            if (_textureSrvCache.TryGetValue(textureId, out var cached))
                return cached;

            // SharpDX's CppObject(IntPtr) constructor does NOT call AddRef - it adopts
            // the pointer as-is. We must AddRef first so that Dispose() later only releases
            // our own reference and does not free the SRV owned by MapTextureCache / IconCache.
            Marshal.AddRef(textureId);
            var srv = new ShaderResourceView(textureId);
            _textureSrvCache[textureId] = srv;
            return srv;
        }

        private void EnsureVB(int count)
        {
            if (_vbSize >= count) return;
            _vb?.Dispose();
            _vbSize = count + 5000;
            _vb = new Buffer(_device, new BufferDescription
            {
                SizeInBytes    = _vbSize * 20,
                Usage          = ResourceUsage.Dynamic,
                BindFlags      = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write
            });
        }

        private void EnsureIB(int count)
        {
            if (_ibSize >= count) return;
            _ib?.Dispose();
            _ibSize = count + 10000;
            _ib = new Buffer(_device, new BufferDescription
            {
                SizeInBytes    = _ibSize * 2,
                Usage          = ResourceUsage.Dynamic,
                BindFlags      = BindFlags.IndexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write
            });
        }

        public bool HandleWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var io = ImGuiNET.ImGui.GetIO();
            switch (msg)
            {
                case 0x0200: // WM_MOUSEMOVE
                    return false; // handled via GetCursorPos in NewFrame
                case 0x020A: // WM_MOUSEWHEEL
                    _pendingMouseWheel += (short)(wParam.ToInt64() >> 16) / 120f;
                    return true;
                case 0x0100: // WM_KEYDOWN
                case 0x0104: // WM_SYSKEYDOWN
                {
                    var key = VkToImGuiKey((int)wParam);
                    if (key != ImGuiKey.None)
                        io.AddKeyEvent(key, true);
                    // Also set modifier flags so Ctrl+A/C/V/Z/X work
                    io.AddKeyEvent(ImGuiKey.ModCtrl,  (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0);
                    io.AddKeyEvent(ImGuiKey.ModShift, (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0);
                    io.AddKeyEvent(ImGuiKey.ModAlt,   (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0);
                    return false;
                }
                case 0x0101: // WM_KEYUP
                case 0x0105: // WM_SYSKEYUP
                {
                    var key = VkToImGuiKey((int)wParam);
                    if (key != ImGuiKey.None)
                        io.AddKeyEvent(key, false);
                    io.AddKeyEvent(ImGuiKey.ModCtrl,  (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0);
                    io.AddKeyEvent(ImGuiKey.ModShift, (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0);
                    io.AddKeyEvent(ImGuiKey.ModAlt,   (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0);
                    return false;
                }
                case 0x0102: // WM_CHAR
                    io.AddInputCharacter((uint)wParam.ToInt64());
                    return false;
            }
            return false;
        }

        private static ImGuiKey VkToImGuiKey(int vk) => vk switch
        {
            0x08 => ImGuiKey.Backspace,
            0x09 => ImGuiKey.Tab,
            0x0D => ImGuiKey.Enter,
            0x1B => ImGuiKey.Escape,
            0x20 => ImGuiKey.Space,
            0x21 => ImGuiKey.PageUp,
            0x22 => ImGuiKey.PageDown,
            0x23 => ImGuiKey.End,
            0x24 => ImGuiKey.Home,
            0x25 => ImGuiKey.LeftArrow,
            0x26 => ImGuiKey.UpArrow,
            0x27 => ImGuiKey.RightArrow,
            0x28 => ImGuiKey.DownArrow,
            0x2D => ImGuiKey.Insert,
            0x2E => ImGuiKey.Delete,
            0x10 => ImGuiKey.LeftShift,
            0x11 => ImGuiKey.LeftCtrl,
            0x12 => ImGuiKey.LeftAlt,
            0xA0 => ImGuiKey.LeftShift,
            0xA1 => ImGuiKey.RightShift,
            0xA2 => ImGuiKey.LeftCtrl,
            0xA3 => ImGuiKey.RightCtrl,
            0xA4 => ImGuiKey.LeftAlt,
            0xA5 => ImGuiKey.RightAlt,
            0x30 => ImGuiKey._0,
            0x31 => ImGuiKey._1,
            0x32 => ImGuiKey._2,
            0x33 => ImGuiKey._3,
            0x34 => ImGuiKey._4,
            0x35 => ImGuiKey._5,
            0x36 => ImGuiKey._6,
            0x37 => ImGuiKey._7,
            0x38 => ImGuiKey._8,
            0x39 => ImGuiKey._9,
            0x41 => ImGuiKey.A, 0x42 => ImGuiKey.B, 0x43 => ImGuiKey.C,
            0x44 => ImGuiKey.D, 0x45 => ImGuiKey.E, 0x46 => ImGuiKey.F,
            0x47 => ImGuiKey.G, 0x48 => ImGuiKey.H, 0x49 => ImGuiKey.I,
            0x4A => ImGuiKey.J, 0x4B => ImGuiKey.K, 0x4C => ImGuiKey.L,
            0x4D => ImGuiKey.M, 0x4E => ImGuiKey.N, 0x4F => ImGuiKey.O,
            0x50 => ImGuiKey.P, 0x51 => ImGuiKey.Q, 0x52 => ImGuiKey.R,
            0x53 => ImGuiKey.S, 0x54 => ImGuiKey.T, 0x55 => ImGuiKey.U,
            0x56 => ImGuiKey.V, 0x57 => ImGuiKey.W, 0x58 => ImGuiKey.X,
            0x59 => ImGuiKey.Y, 0x5A => ImGuiKey.Z,
            0x70 => ImGuiKey.F1,  0x71 => ImGuiKey.F2,  0x72 => ImGuiKey.F3,
            0x73 => ImGuiKey.F4,  0x74 => ImGuiKey.F5,  0x75 => ImGuiKey.F6,
            0x76 => ImGuiKey.F7,  0x77 => ImGuiKey.F8,  0x78 => ImGuiKey.F9,
            0x79 => ImGuiKey.F10, 0x7A => ImGuiKey.F11, 0x7B => ImGuiKey.F12,
            _ => ImGuiKey.None
        };

        // Each wrapper owns one AddRef'd COM reference. Stale cache entries therefore
        // keep their backing D3D11 textures alive even after MapTextureCache has evicted
        // them. Call this between frames (e.g. on resize) to release those extra refs.
        // Active texture IDs are re-wrapped (and re-AddRef'd) on the next draw call.
        public void FlushTextureSrvCache()
        {
            foreach (var srv in _textureSrvCache.Values)
                srv.Dispose();
            _textureSrvCache.Clear();
        }

        public void Dispose()
        {
            // Wrappers own their COM references (each holds an explicit AddRef), so it is
            // safe to Dispose them regardless of what order the external caches are torn down.
            FlushTextureSrvCache();

            _fontSRV?.Dispose();
            _fontTex?.Dispose();
            _vb?.Dispose(); _ib?.Dispose(); _cb?.Dispose();
            _vs?.Dispose(); _ps?.Dispose();
            _layout?.Dispose();
            _blend?.Dispose(); _raster?.Dispose();
            _depth?.Dispose(); _sampler?.Dispose();
        }
    }
}
