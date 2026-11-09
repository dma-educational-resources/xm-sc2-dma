using System.Drawing;
using System.Reflection;

namespace SC2Radar.UI.ImGui;

// HICON handles for Win32 WNDCLASSEX.hIcon / WM_SETICON. Loaded once, kept alive
// for the life of the process (the class registration references them). This is
// separate from IconCache which produces ImGui texture handles from the same PNGs.
internal static class WindowIcons
{
    // The managed Icon owns the underlying HICON and destroys it on Dispose/finalize.
    // Keep a strong reference for the life of the process so Windows' WNDCLASSEX.hIcon
    // stays valid - otherwise the GC can reclaim it, DestroyIcon runs, and the next
    // RegisterClassEx silently fails (class is never registered, so CreateWindowEx
    // then fails with ERROR_CANNOT_FIND_WND_CLASS / 1407, and swap chain creation
    // on the resulting NULL HWND throws DXGI_ERROR_INVALID_CALL).
    private static Icon? _appIconKeepAlive;

    // Main app icon (taskbar + title bar for the primary radar window). Taken from
    // the exe's embedded ApplicationIcon (xm-icon.ico) so it matches what Explorer
    // and the shell already show for the file itself.
    public static IntPtr AppHIcon { get; } = LoadAppHIcon();

    // Gear icon for settings-style popout windows (memory writes, radar config, etc.).
    // Sourced from the embedded gear.png so popouts are visually distinct from the
    // main radar in the taskbar.
    public static IntPtr GearHIcon { get; } = LoadEmbeddedPngAsHIcon("SC2Radar.UI.Resources.gear.png");

    private static IntPtr LoadAppHIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;
            // NO 'using' - the Icon owns the HICON and disposing it calls DestroyIcon,
            // leaving WNDCLASSEX.hIcon dangling. Store the Icon so it stays rooted for
            // the process lifetime.
            _appIconKeepAlive = Icon.ExtractAssociatedIcon(exe);
            return _appIconKeepAlive?.Handle ?? IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr LoadEmbeddedPngAsHIcon(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return IntPtr.Zero;
            using var bmp = new Bitmap(stream);
            // Bitmap.GetHicon returns a freshly-created HICON whose lifetime is
            // independent of the Bitmap (per MSDN), so disposing bmp is safe.
            return bmp.GetHicon();
        }
        catch { return IntPtr.Zero; }
    }
}
