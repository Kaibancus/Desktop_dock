using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace Polaris.Services;

/// <summary>
/// Shared "active monitor" target used to position the docks. By default it
/// reflects the PRIMARY monitor, so single-monitor behaviour is unchanged.
/// When the user enables "show on all monitors", the host updates the target to
/// whichever monitor the cursor is on just before a summon, and both docks read
/// <see cref="ActiveBounds"/> / <see cref="ActiveWorkArea"/> to lay themselves
/// out on that monitor instead of always the primary one.
///
/// All rectangles are in WPF device-independent units (DIPs). This assumes a
/// UNIFORM display scale across monitors (one global physical-pixel→DIP factor),
/// which keeps the virtual-desktop DIP coordinate space consistent so a window's
/// Left/Top in DIPs lands on the intended monitor. Mixed per-monitor DPI is not
/// handled here.
/// </summary>
public static class MonitorLayout
{
    /// <summary>Full bounds (DIP) of the active monitor, in virtual-desktop
    /// coordinates (origin may be non-zero on a secondary monitor).</summary>
    public static Rect ActiveBounds { get; private set; }

    /// <summary>Work area (DIP) of the active monitor — its full bounds minus
    /// any docked taskbar on that monitor.</summary>
    public static Rect ActiveWorkArea { get; private set; }

    static MonitorLayout() => UsePrimary();

    /// <summary>Targets the PRIMARY monitor (the single-monitor default). Reads LIVE Win32
    /// metrics (physical screen size, work area and per-monitor effective DPI) rather than WPF
    /// <see cref="SystemParameters"/>: the docks are bare composition windows, not WPF windows,
    /// so when no WPF window exists (the normal at-rest state) WPF never receives the
    /// WM_DISPLAYCHANGE / WM_SETTINGCHANGE that invalidates its cached SystemParameters — at
    /// login auto-start those cache the pre-settle (wrong) resolution/DPI/work-area and freeze
    /// there, which made the docks lay out at the wrong size/position after a reboot.</summary>
    public static void UsePrimary()
    {
        double scale = PrimaryDpiScale;
        int cx = GetSystemMetrics(SM_CXSCREEN);
        int cy = GetSystemMetrics(SM_CYSCREEN);
        if (cx <= 0 || cy <= 0)
        {
            // Win32 not ready — fall back to WPF's value so we never produce a zero-size dock.
            ActiveBounds = new Rect(0, 0,
                SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            ActiveWorkArea = SystemParameters.WorkArea;
            return;
        }
        ActiveBounds = new Rect(0, 0, cx / scale, cy / scale);

        var wr = new RECT();
        ActiveWorkArea = SystemParametersInfo(SPI_GETWORKAREA, 0, ref wr, 0) && wr.right > wr.left
            ? new Rect(wr.left / scale, wr.top / scale,
                       (wr.right - wr.left) / scale, (wr.bottom - wr.top) / scale)
            : ActiveBounds;
    }

    /// <summary>Device pixels per DIP for the PRIMARY monitor, read LIVE from the OS (effective
    /// per-monitor DPI, with a screen-DC fallback) so it reflects the current display even when
    /// WPF's cached <see cref="SystemParameters"/> are stale (no WPF window to refresh them).</summary>
    public static double PrimaryDpiScale
    {
        get
        {
            try
            {
                IntPtr mon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
                {
                    double s = dx / 96.0;
                    if (s >= 0.5 && s <= 4.0) return s;
                }
            }
            catch { /* shcore unavailable — fall through */ }
            try
            {
                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    try
                    {
                        int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
                        if (dpi > 0) { double s = dpi / 96.0; if (s >= 0.5 && s <= 4.0) return s; }
                    }
                    finally { ReleaseDC(IntPtr.Zero, hdc); }
                }
            }
            catch { /* fall through */ }
            return 1.0;
        }
    }

    /// <summary>Targets the monitor under the given PHYSICAL-pixel point (e.g. a
    /// raw cursor position from <c>GetCursorPos</c>). <paramref name="dipScale"/>
    /// is device pixels per DIP (uniform across monitors). Falls back to the
    /// primary monitor if the query fails.</summary>
    public static void SetTargetForPhysicalPoint(int physicalX, int physicalY, double dipScale)
    {
        if (dipScale <= 0)
            dipScale = 1.0;

        IntPtr hMon = MonitorFromPoint(new POINT { X = physicalX, Y = physicalY }, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMon == IntPtr.Zero || !GetMonitorInfo(hMon, ref mi))
        {
            UsePrimary();
            return;
        }

        ActiveBounds = RectFromPhysical(mi.rcMonitor, dipScale);
        ActiveWorkArea = RectFromPhysical(mi.rcWork, dipScale);
    }

    private static Rect RectFromPhysical(RECT r, double scale) =>
        new(r.left / scale, r.top / scale,
            (r.right - r.left) / scale, (r.bottom - r.top) / scale);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const uint SPI_GETWORKAREA = 0x0030;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int LOGPIXELSX = 88;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
}
