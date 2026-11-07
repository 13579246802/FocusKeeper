using System.Runtime.InteropServices;

namespace FocusKeeper.Platform;

/// <summary>
/// 全局热键：Ctrl+Alt+1~4。刻意不用裸数字键——裸数字全局钩子会吞掉你所有输入框里的数字。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly List<int> _ids = new();
    private readonly bool _ok;

    public GlobalHotkey(IntPtr hwnd, bool enable)
    {
        _hwnd = hwnd;
        if (!enable) return;

        // 1 开始 / 2 结束 / 3 走神 / 4 休息
        for (var i = 0; i < 4; i++)
        {
            var id = 9000 + i;
            var vk = (uint)(0x31 + i); // VK_1..VK_4
            if (RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, vk))
            {
                _ids.Add(id);
            }
        }

        _ok = _ids.Count == 4;
    }

    public bool IsActive => _ok;

    /// <summary>返回触发的键号 1~4；不是热键消息返回 0。</summary>
    public int HandleMessage(ref Message m)
    {
        if (m.Msg != WM_HOTKEY) return 0;
        var id = m.WParam.ToInt32();
        var idx = id - 9000;
        return idx is >= 0 and <= 3 ? idx + 1 : 0;
    }

    public void Dispose()
    {
        foreach (var id in _ids)
        {
            try { UnregisterHotKey(_hwnd, id); } catch { }
        }
        _ids.Clear();
    }
}
