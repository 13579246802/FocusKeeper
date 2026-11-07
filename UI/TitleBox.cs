namespace FocusKeeper.UI;

/// <summary>
/// 标题输入框。默认 TextBox 会把 Tab 当作「切换焦点」吞掉，到不了 Form 的 KeyDown，
/// 所以这里直接截住 Tab，改成切换载体。Esc / Enter 仍走原来的路径。
/// </summary>
public sealed class TitleBox : TextBox
{
    public event Action? CycleMode;

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Tab)
        {
            CycleMode?.Invoke();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }
}

/// <summary>双缓冲 Panel：全屏大字号每秒刷新 5 次，不开双缓冲会看到闪烁。</summary>
public sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}
