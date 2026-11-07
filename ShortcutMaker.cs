using System.Runtime.InteropServices;
using System.Text;

namespace FocusKeeper;

/// <summary>
/// 生成桌面快捷方式。
/// 手写 .lnk 二进制只能给到 LinkInfo，Explorer 在新版 Windows 上不认（双击没反应），
/// 必须走 IShellLink 让它连 LinkTargetIDList 一起写出去。
/// 只用于打包期：`FocusKeeper.exe --mkshortcut <exe> <lnk>`。
/// </summary>
internal static class ShortcutMaker
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
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
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <returns>成功返回空字符串，失败返回原因。</returns>
    public static string Create(string exePath, string lnkPath, string description)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
            link.SetIconLocation(exePath, 0);
            link.SetDescription(description);
            link.SetShowCmd(1);   // SW_SHOWNORMAL

            ((IPersistFile)link).Save(lnkPath, true);
            Marshal.ReleaseComObject(link);
            return File.Exists(lnkPath) ? "" : "文件未生成";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
