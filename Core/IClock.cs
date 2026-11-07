using System.Diagnostics;

namespace FocusKeeper.Core;

/// <summary>
/// 时间来源。墙钟用于打时间戳，单调时钟用于算时长（不受系统改时间影响）。
/// </summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    TimeSpan Monotonic { get; }
}

public sealed class SystemClock : IClock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();

    public DateTimeOffset Now => DateTimeOffset.Now;

    /// <summary>QPC 单调时钟：系统休眠期间不前进，正好用来检测睡眠。</summary>
    public TimeSpan Monotonic => Sw.Elapsed;
}
