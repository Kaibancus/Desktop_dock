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

    private const string AppsFolderPrefix = "shell:AppsFolder\\";

    /// <summary>If <paramref name="path"/> is a bare packaged-app AUMID
    /// (UWP / Microsoft Store), returns the launchable, icon-resolvable
    /// "shell:AppsFolder\&lt;AUMID&gt;" form; otherwise returns it unchanged.
    /// Used both on drop and to migrate configs saved before this fix.</summary>
    public static string NormalizeAppsFolderPath(string path)
    {
        if (!IsShellToken(path) && IsAppsFolderItem(path))
            return AppsFolderPrefix + path;
        return path;
    }

    /// <summary>True when <paramref name="token"/> is an AppsFolder launcher
    /// item — a packaged AUMID ("PackageFamilyName!AppId", e.g. Settings) or a
    /// registered app-id ("MSEdge") — confirmed by resolving
    /// "shell:AppsFolder\&lt;token&gt;". We pre-filter out anything path-like
    /// (containing a separator or drive/scheme colon) so ordinary file drops skip
    /// the shell lookup; the SHParseDisplayName call is the real authority.</summary>
    private static bool IsAppsFolderItem(string token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.IndexOf('\\') >= 0 || token.IndexOf('/') >= 0 ||
            token.IndexOf(':') >= 0)
            return false;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            return SHParseDisplayName(AppsFolderPrefix + token, IntPtr.Zero,
                       out pidl, 0, out _) == 0 && pidl != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

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
                    string token = GetParsingToken(abs);
                    string name = GetName(abs, SIGDN_NORMALDISPLAY);
                    if (string.IsNullOrWhiteSpace(token))
                        continue;
                    // Packaged (UWP / Microsoft Store) apps live under
                    // shell:AppsFolder and their desktop-absolute parsing name is
                    // a bare AUMID (e.g. "Microsoft.WindowsStore_8wekyb3d8bbwe!App")
                    // that neither SHParseDisplayName (icon) nor Process.Start
                    // (launch) can consume. Normalize to the launchable,
                    // icon-resolvable "shell:AppsFolder\<AUMID>" form.
                    token = NormalizeAppsFolderPath(token);
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
        string token = GetParsingToken(absPidl);
        if (string.IsNullOrWhiteSpace(token))
            return null;
        string name = GetName(absPidl, SIGDN_NORMALDISPLAY);
        // Packaged-app shortcuts dragged from the Start menu resolve their PIDL to
        // a bare AUMID here; rewrite to the launchable "shell:AppsFolder\<AUMID>"
        // form so the icon renders and the app actually starts.
        token = NormalizeAppsFolderPath(token);
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

    /// <summary>Expands a known-folder-GUID-relative parsing name
    /// (e.g. <c>{6D809377-…}\App\app.exe</c>) into a real file-system path. These
    /// were stored by earlier builds that saved the desktop-absolute parsing name
    /// verbatim; neither Process.Start nor SHParseDisplayName can launch them.
    /// The leading <c>{GUID}</c> is a KNOWNFOLDERID resolved via
    /// <c>SHGetKnownFolderPath</c>; the remainder is appended. Returns the
    /// original token unchanged when it does not start with a GUID or cannot be
    /// resolved to an existing file-system path.</summary>
    public static string ExpandKnownFolderPath(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length == 0 || token[0] != '{')
            return token;
        int close = token.IndexOf('}');
        if (close <= 0)
            return token;
        string guidStr = token.Substring(1, close - 1);
        if (!Guid.TryParse(guidStr, out Guid folderId))
            return token;
        if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out IntPtr pPath) != 0 || pPath == IntPtr.Zero)
            return token;
        try
        {
            string basePath = Marshal.PtrToStringUni(pPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(basePath))
                return token;
            string rest = token.Substring(close + 1).TrimStart('\\', '/');
            string full = string.IsNullOrEmpty(rest) ? basePath : Path.Combine(basePath, rest);
            return (File.Exists(full) || Directory.Exists(full)) ? full : token;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pPath);
        }
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

    /// <summary>Returns the best launchable token for an absolute PIDL. Prefers
    /// the real file-system path (<see cref="SIGDN_FILESYSPATH"/>) so ordinary
    /// program shortcuts resolve to e.g. <c>C:\Program Files\App\app.exe</c>
    /// rather than the known-folder-GUID-relative parsing name
    /// (e.g. <c>{6D809377-…}\App\app.exe</c>, which neither Process.Start nor
    /// SHParseDisplayName can consume). Falls back to the desktop-absolute
    /// parsing name for virtual / packaged items that have no file-system path.</summary>
    private static string GetParsingToken(IntPtr abs)
    {
        string fsPath = GetName(abs, SIGDN_FILESYSPATH);
        if (!string.IsNullOrWhiteSpace(fsPath))
            return fsPath;
        return GetName(abs, SIGDN_DESKTOPABSOLUTEPARSING);
    }

    private const int SIGDN_NORMALDISPLAY = 0x00000000;
    private const int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);
    private const int SIGDN_FILESYSPATH = unchecked((int)0x80058000);
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
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
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
