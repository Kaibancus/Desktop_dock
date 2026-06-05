using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DesktopPanel.Models;

namespace DesktopPanel.Services;

/// <summary>
/// Builds an <see cref="AppEntry"/> from a dropped file path. Resolves
/// Windows .lnk shortcuts via the IShellLink COM interface.
/// </summary>
public static class ShortcutResolver
{
    public static AppEntry? CreateEntry(string droppedPath)
    {
        if (string.IsNullOrWhiteSpace(droppedPath))
            return null;

        try
        {
            string ext = Path.GetExtension(droppedPath);
            if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
                return FromShortcut(droppedPath);

            // Any other file: launch directly (exe, bat, url handler, doc, etc.)
            return new AppEntry
            {
                Name = Path.GetFileNameWithoutExtension(droppedPath),
                Path = droppedPath,
                IconSource = droppedPath,
                WorkingDirectory = Path.GetDirectoryName(droppedPath) ?? string.Empty,
            };
        }
        catch
        {
            return null;
        }
    }

    private static AppEntry FromShortcut(string lnkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        var file = (IPersistFile)link;
        file.Load(lnkPath, 0);

        var sb = new StringBuilder(260);
        var data = new WIN32_FIND_DATAW();
        link.GetPath(sb, sb.Capacity, ref data, SLGP_RAWPATH);
        string target = sb.ToString();

        // A shortcut to a virtual shell object (This PC, Recycle Bin, Control
        // Panel…) has no file-system target — GetPath returns empty — but it
        // still carries an absolute PIDL we can turn into a "shell:::{CLSID}"
        // token.
        if (string.IsNullOrWhiteSpace(target))
        {
            link.GetIDList(out IntPtr pidl);
            if (pidl != IntPtr.Zero)
            {
                try
                {
                    var shellEntry = ShellNamespace.FromAbsolutePidl(pidl);
                    if (shellEntry != null)
                    {
                        shellEntry.Name = Path.GetFileNameWithoutExtension(lnkPath);
                        return shellEntry;
                    }
                }
                finally
                {
                    ShellNamespace.FreePidl(pidl);
                }
            }
        }

        sb.Clear();
        link.GetArguments(sb, sb.Capacity);
        string args = sb.ToString();

        sb.Clear();
        link.GetWorkingDirectory(sb, sb.Capacity);
        string workDir = sb.ToString();

        sb.Clear();
        link.GetIconLocation(sb, sb.Capacity, out _);
        string iconLoc = sb.ToString();

        string name = Path.GetFileNameWithoutExtension(lnkPath);
        string iconSource = string.IsNullOrWhiteSpace(iconLoc) ? target : iconLoc;

        return new AppEntry
        {
            Name = name,
            Path = target,
            Arguments = args,
            WorkingDirectory = string.IsNullOrWhiteSpace(workDir)
                ? (Path.GetDirectoryName(target) ?? string.Empty)
                : workDir,
            IconSource = iconSource,
        };
    }

    private const uint SLGP_RAWPATH = 0x4;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath, ref WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
}
