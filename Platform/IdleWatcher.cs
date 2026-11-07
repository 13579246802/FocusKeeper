using System.Runtime.InteropServices;

namespace FocusKeeper.Platform;

/// <summary>空闲检测：多久没有键盘/鼠标输入。用于发现"忘按结束就跑了"。</summary>
public sealed class IdleWatcher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public TimeSpan IdleTime
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

            // TickCount 会溢出回绕，用无符号减法算间隔
            var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(idleMs);
        }
    }
}
