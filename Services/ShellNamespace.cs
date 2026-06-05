using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Polaris.Models;

namespace Polaris.Services;

/// <summary>
/// Supports adding Windows shell-namespace objects (This PC, Recycle Bin,
/// Control Panel, etc.) that have no file-system path. These arrive on a drop
/// as CFSTR_SHELLIDLIST ("Shell IDList Array") rather than CF_HDROP, and are
/// launched via explorer.exe with a "shell:::{CLSID}" token.
/// </summary>
public static class ShellNamespace
{
    public const string ShellIdListFormat = "Shell IDList Array";

    public static bool HasShellItems(System.Windows.IDataObject data) =>
        data.GetDataPresent(ShellIdListFormat);

    public static bool IsShellToken(string s) =>
        !string.IsNullOrEmpty(s) &&
        (s.StartsWith("::{", StringComparison.Ordinal) ||
         s.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds AppEntry items from a dropped Shell IDList Array.</summary>
    public static List<AppEntry> CreateEntries(System.Windows.IDataObject data)
    {
        var result = new List<AppEntry>();
        byte[]? bytes = data.GetData(ShellIdListFormat) switch
        {
            MemoryStream ms => ms.ToArray(),
            byte[] b => b,
            _ => null,
        };
        if (bytes == null || bytes.Length < 8)
            return result;

        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = pin.AddrOfPinnedObject();
            // CIDA: UINT cidl; UINT aoffset[cidl+1]  (aoffset[0] = parent folder)
            uint cidl = (uint)Marshal.ReadInt32(basePtr, 0);
            uint parentOff = (uint)Marshal.ReadInt32(basePtr, 4);
            IntPtr parentPidl = IntPtr.Add(basePtr, (int)parentOff);

            for (int i = 1; i <= cidl; i++)
            {
                uint childOff = (uint)Marshal.ReadInt32(basePtr, 4 * (i + 1));
                IntPtr childPidl = IntPtr.Add(basePtr, (int)childOff);
                IntPtr abs = ILCombine(parentPidl, childPidl);
                if (abs == IntPtr.Zero)
                    continue;
                try
                {
                    string token = GetName(abs, SIGDN_DESKTOPABSOLUTEPARSING);
                    string name = GetName(abs, SIGDN_NORMALDISPLAY);
                    if (string.IsNullOrWhiteSpace(token))
                        continue;
                    // Skip ordinary file-system items here; they come through CF_HDROP.
                    if (!IsShellToken(token) && (File.Exists(token) || Directory.Exists(token)))
                        continue;
                    result.Add(new AppEntry
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? token : name,
                        Path = token,
                        IconSource = token,
                    });
                }
                finally
                {
                    ILFree(abs);
                }
            }
        }
        finally
        {
            pin.Free();
        }
        return result;
    }

    /// <summary>Builds an AppEntry from an absolute shell PIDL (does not free it).</summary>
    public static AppEntry? FromAbsolutePidl(IntPtr absPidl)
    {
        if (absPidl == IntPtr.Zero)
            return null;
        string token = GetName(absPidl, SIGDN_DESKTOPABSOLUTEPARSING);
        if (string.IsNullOrWhiteSpace(token))
            return null;
        string name = GetName(absPidl, SIGDN_NORMALDISPLAY);
        return new AppEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? token : name,
            Path = token,
            IconSource = token,
        };
    }

    /// <summary>Releases a PIDL allocated by the shell (e.g. IShellLink.GetIDList).</summary>
    public static void FreePidl(IntPtr pidl)
    {
        if (pidl != IntPtr.Zero)
            Marshal.FreeCoTaskMem(pidl);
    }

    /// <summary>Launches a shell-namespace token via explorer.exe.</summary>
    public static void Launch(string token)
    {
        string arg = token.StartsWith("::", StringComparison.Ordinal) ? "shell:" + token : token;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arg,
            UseShellExecute = true,
        });
    }

    /// <summary>Extracts a high-resolution (jumbo) icon for a shell token.</summary>
    public static BitmapSource? GetIcon(string token)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(token, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return null;

            var shinfo = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(pidl, 0, ref shinfo,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_SYSICONINDEX);
            if (res == IntPtr.Zero)
                return null;

            var iid = IID_IImageList;
            if (SHGetImageList(SHIL_JUMBO, ref iid, out IImageList? list) != 0 || list == null)
                return null;

            IntPtr hicon = IntPtr.Zero;
            try
            {
                list.GetIcon(shinfo.iIcon, ILD_TRANSPARENT, ref hicon);
                if (hicon == IntPtr.Zero)
                    return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (hicon != IntPtr.Zero) DestroyIcon(hicon);
                Marshal.ReleaseComObject(list);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static string GetName(IntPtr pidl, int sigdn)
    {
        if (SHGetNameFromIDList(pidl, sigdn, out IntPtr p) != 0 || p == IntPtr.Zero)
            return string.Empty;
        try { return Marshal.PtrToStringUni(p) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    private const int SIGDN_NORMALDISPLAY = 0x00000000;
    private const int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_PIDL = 0x000000008;
    private const int SHIL_JUMBO = 0x4;
    private const int ILD_TRANSPARENT = 0x1;
    private static Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [DllImport("shell32.dll")] private static extern IntPtr ILCombine(IntPtr p1, IntPtr p2);
    [DllImport("shell32.dll")] private static extern void ILFree(IntPtr pidl);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc,
        out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("shell32.dll", EntryPoint = "SHGetImageList")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IImageList? ppv);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
