using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace DesktopPanel.Services;

/// <summary>
/// Extracts an application icon as a WPF <see cref="BitmapSource"/>.
/// Uses SHGetFileInfo for large/high-quality icons, falling back to
/// ExtractAssociatedIcon.
/// </summary>
public static class IconExtractor
{
    public static BitmapSource? GetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        Drawing.Icon? icon = null;
        try
        {
            icon = GetShellIcon(path) ?? GetAssociatedIcon(path);
            if (icon == null)
                return null;

            var src = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            icon?.Dispose();
        }
    }

    private static Drawing.Icon? GetAssociatedIcon(string path)
    {
        try
        {
            if (File.Exists(path))
                return Drawing.Icon.ExtractAssociatedIcon(path);
        }
        catch
        {
        }
        return null;
    }

    private static Drawing.Icon? GetShellIcon(string path)
    {
        // Prefer the jumbo (256x256) system image list for crisp icons.
        var jumbo = GetJumboIcon(path);
        if (jumbo != null)
            return jumbo;

        var shinfo = new SHFILEINFO();
        const uint flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
        uint attr = File.Exists(path) ? 0u : FILE_ATTRIBUTE_NORMAL;

        IntPtr res = SHGetFileInfo(path, attr, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
            return null;

        try
        {
            // Clone so we own a managed copy, then free the unmanaged handle.
            using var tmp = Drawing.Icon.FromHandle(shinfo.hIcon);
            return (Drawing.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    /// <summary>
    /// Retrieves the 256x256 "jumbo" icon from the system image list, giving a
    /// much sharper image than the 32x32 large icon when scaled in the UI.
    /// </summary>
    private static Drawing.Icon? GetJumboIcon(string path)
    {
        try
        {
            var shinfo = new SHFILEINFO();
            const uint flags = SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES;
            uint attr = File.Exists(path) ? 0u : FILE_ATTRIBUTE_NORMAL;

            IntPtr res = SHGetFileInfo(path, attr, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (res == IntPtr.Zero)
                return null;

            var iidImageList = IID_IImageList;
            if (SHGetImageList(SHIL_JUMBO, ref iidImageList, out IImageList? list) != 0 || list == null)
                return null;

            IntPtr hicon = IntPtr.Zero;
            try
            {
                list.GetIcon(shinfo.iIcon, ILD_TRANSPARENT, ref hicon);
                if (hicon == IntPtr.Zero)
                    return null;

                using var tmp = Drawing.Icon.FromHandle(hicon);
                return (Drawing.Icon)tmp.Clone();
            }
            finally
            {
                if (hicon != IntPtr.Zero)
                    DestroyIcon(hicon);
                Marshal.ReleaseComObject(list);
            }
        }
        catch
        {
            return null;
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    private const int SHIL_JUMBO = 0x4;     // 256x256
    private const int ILD_TRANSPARENT = 0x1;
    private static Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "SHGetImageList")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IImageList? ppv);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
    }
}
