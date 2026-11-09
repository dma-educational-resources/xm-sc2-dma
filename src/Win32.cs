// Win32.cs
// P/Invoke declarations needed for the ImGui DX11 windowing layer.

using System;
using System.Runtime.InteropServices;

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    internal static class Win32
    {
        // ── Window styles ──────────────────────────────────────────────────────
        public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        public const uint WS_POPUP            = 0x80000000;
        public const uint WS_THICKFRAME       = 0x00040000;
        public const uint WS_EX_APPWINDOW     = 0x00040000;
        public const uint WS_EX_LAYERED       = 0x00080000;
        public const uint WS_EX_TOPMOST       = 0x00000008;
        public const uint WS_EX_TOOLWINDOW    = 0x00000080;
        public const uint WS_EX_TRANSPARENT   = 0x00000020;
        public const uint WS_EX_NOACTIVATE    = 0x08000000;

        // WM_NCHITTEST and hit-test return values
        public const uint WM_NCHITTEST   = 0x0084;
        public const int  HTNOWHERE      = 0;
        public const int  HTCLIENT       = 1;
        public const int  HTTRANSPARENT  = -1;
        public const int  HTCAPTION      = 2;
        public const int  HTLEFT         = 10;
        public const int  HTRIGHT        = 11;
        public const int  HTTOP          = 12;
        public const int  HTTOPLEFT      = 13;
        public const int  HTTOPRIGHT     = 14;
        public const int  HTBOTTOM       = 15;
        public const int  HTBOTTOMLEFT   = 16;
        public const int  HTBOTTOMRIGHT  = 17;

        public const uint LWA_ALPHA           = 0x00000002;
        public const uint LWA_COLORKEY        = 0x00000001;

        // ── Class styles ───────────────────────────────────────────────────────
        public const uint CS_HREDRAW = 0x0002;
        public const uint CS_VREDRAW = 0x0001;

        // ── ShowWindow ────────────────────────────────────────────────────────
        public const int SW_SHOW           = 5;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_HIDE           = 0;
        public const int SW_SHOWMAXIMIZED  = 3;
        public const int SW_MINIMIZE       = 6;
        public const int SW_RESTORE        = 9;

        // ── Messages ──────────────────────────────────────────────────────────
        public const uint WM_MOVE          = 0x0003;
        public const uint WM_SIZE          = 0x0005;
        public const uint WM_CLOSE         = 0x0010;
        public const uint WM_DESTROY       = 0x0002;
        public const uint WM_QUIT          = 0x0012;
        public const uint WM_QUERYENDSESSION = 0x0011;
        public const uint WM_ENDSESSION      = 0x0016;
        public const uint WM_NCLBUTTONDOWN  = 0x00A1;
        public const uint WM_NCCALCSIZE     = 0x0083;
        public const uint WM_GETMINMAXINFO  = 0x0024;

        // ── PeekMessage ───────────────────────────────────────────────────────
        public const uint PM_REMOVE = 0x0001;

        // ── GetSystemMetrics ─────────────────────────────────────────────
        public const int SM_CXSIZEFRAME    = 32; // resize border width
        public const int SM_CYSIZEFRAME    = 33; // resize border height
        public const int SM_CXPADDEDBORDER = 92; // extra DWM padding added to border

        // ── GetWindowLong / SetWindowLong ──────────────────────────────────────
        public const int GWL_EXSTYLE = -20;
        public const int GWL_STYLE   = -16;

        // ── SetWindowPos flags ───────────────────────────────────────────
        public const uint SWP_NOZORDER   = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOMOVE     = 0x0002;
        public const uint SWP_NOSIZE     = 0x0001;

        // SetWindowPos z-order insert-after handles, for always-on-top toggling.
        public static readonly IntPtr HWND_TOPMOST   = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        // ── Cursor ────────────────────────────────────────────────────────────
        public static readonly IntPtr IDC_ARROW = new IntPtr(32512);

        // ── Structs ───────────────────────────────────────────────────────────


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint    cbSize;
            public uint    style;
            public IntPtr  lpfnWndProc;
            public int     cbClsExtra;
            public int     cbWndExtra;
            public IntPtr  hInstance;
            public IntPtr  hIcon;
            public IntPtr  hCursor;
            public IntPtr  hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr  hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint   message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint   time;
            public POINT  pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        // Passed in lParam of WM_NCCALCSIZE when wParam=TRUE.
        // Only rgrc[0] matters - it holds the proposed window rect on entry
        // and must hold the desired client rect on return.
        [StructLayout(LayoutKind.Sequential)]
        public struct NCCALCSIZE_PARAMS
        {
            public RECT rgrc0;
            public RECT rgrc1;
            public RECT rgrc2;
            public IntPtr lppos; // pointer to WINDOWPOS, unused here
        }

        public delegate IntPtr WNDPROC(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        // ── Helpers ───────────────────────────────────────────────────────────
        public static int  LOWORD(uint v) => (int)(v & 0xFFFF);
        public static int  HIWORD(uint v) => (int)((v >> 16) & 0xFFFF);

        // Signed-short extraction from lParam (for WM_NCHITTEST screen coordinates)
        public static int GET_X_LPARAM(IntPtr lp) => (short)(lp.ToInt64() & 0xFFFF);
        public static int GET_Y_LPARAM(IntPtr lp) => (short)((lp.ToInt64() >> 16) & 0xFFFF);

        // ── P/Invokes ─────────────────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(
            uint   dwExStyle, string lpClassName, string lpWindowName,
            uint   dwStyle,
            int    x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd,
                                              uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        // Real OS-level rounded corners for a standalone window (e.g. the startup splash) -
        // distinct from how the main app's docked panels fake rounding by drawing their own
        // rounded rects inside a square host window (see ImGuiApp.BuildRadarFrame's
        // WindowRounding=0 push). After a successful SetWindowRgn the system owns the HRGN;
        // do not delete it.
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
            int nWidthEllipse, int nHeightEllipse);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint uPeriod);

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFO
        {
            public int  cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>Makes the ESP overlay window fully transparent via DWM composition.</summary>
        public static void MakeWindowTransparent(IntPtr hwnd)
        {
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
    }
}
