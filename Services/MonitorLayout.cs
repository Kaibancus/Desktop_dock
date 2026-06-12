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

    /// <summary>Targets the PRIMARY monitor (the single-monitor default).</summary>
    public static void UsePrimary()
    {
        ActiveBounds = new Rect(0, 0,
            SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        ActiveWorkArea = SystemParameters.WorkArea;
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
}
