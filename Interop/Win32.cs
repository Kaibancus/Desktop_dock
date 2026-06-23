using System;
using System.Runtime.InteropServices;

namespace Polaris.Interop;

/// <summary>
/// Shared Win32 interop for the GPU dock windows. Each GPU surface (the main dock,
/// the side dock, the Saturn notch clock and the drop shim) is a raw top-level
/// Win32 window, and they all need the same window-class registration, creation and
/// positioning plumbing. Centralising it here removes four near-identical copies of
/// the same P/Invoke declarations, structs and constants, and gives a single
/// <see cref="CreateWindow"/> helper a future GPU surface can reuse.
/// </summary>
internal static class Win32
{
    /// <summary>Window procedure delegate. Callers keep their own <c>static readonly</c>
    /// instance alive for the window's lifetime so the marshalled function pointer is
    /// never collected (a per-instance delegate would dangle after a dock rebuild).</summary>
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- Structs ---------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    // ---- Window styles / show / set-pos constants ------------------------------
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_POPUP = 0x80000000;

    public const int GWL_EXSTYLE = -20;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public static readonly IntPtr IDC_ARROW = (IntPtr)32512;
    public const int BLACK_BRUSH = 4;

    // ---- Window management P/Invokes -------------------------------------------
    [DllImport("user32.dll", SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLongW(IntPtr h, int index, int value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int index);

    /// <summary>Registers <paramref name="className"/> once (keyed by the caller's
    /// <paramref name="atom"/>) and creates a borderless <c>WS_POPUP</c> top-level window of
    /// the given extended style and size at the origin. The caller must keep
    /// <paramref name="proc"/> rooted in a static field so its function pointer survives.
    /// Used by every GPU surface: the composition docks (<c>WS_EX_NOREDIRECTIONBITMAP</c>),
    /// the notch clock and the layered drop shim.</summary>
    public static IntPtr CreateWindow(string className, int exStyle, int width, int height,
        WndProc proc, ref ushort atom, IntPtr hCursor = default, IntPtr hbrBackground = default)
    {
        if (atom == 0)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                hInstance = GetModuleHandleW(null),
                hCursor = hCursor,
                hbrBackground = hbrBackground,
                lpszClassName = className,
            };
            atom = RegisterClassExW(ref wc);
        }
        return CreateWindowExW(exStyle, className, string.Empty, WS_POPUP,
            0, 0, width, height, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }
}
