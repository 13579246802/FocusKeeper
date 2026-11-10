using System.Text.Json.Serialization;

namespace FocusKeeper.Core;

public enum Phase
{
    Idle,
    TitleInput,
    Focusing,
    Break
}

/// <summary>
/// 这件事在哪做。决定两件完全不同的事：窗口形态，以及「键鼠没动静」还算不算人走了。
/// </summary>
public enum WorkMode
{
    /// <summary>在这台电脑上（写代码、写文档……）：小窗，键鼠空闲可判离开。</summary>
    OnPc = 0,

    /// <summary>不在这台电脑上（看书、写字、动手）：不按键是常态，空闲检测失效。</summary>
    OffPc = 1
}

public static class WorkModeText
{
    public static string Key(this WorkMode m) => m == WorkMode.OffPc ? "offpc" : "onpc";

    public static string Label(this WorkMode m) => m == WorkMode.OffPc ? "不在电脑上" : "在这台电脑上";

    public static WorkMode Parse(string? key) => key == "offpc" ? WorkMode.OffPc : WorkMode.OnPc;
}

public sealed class Segment
{
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; set; }
    public string EndReason { get; set; } = "stop";
    public double Seconds => (End - Start).TotalSeconds;
}

public sealed class Distraction
{
    public DateTimeOffset At { get; init; }
    public double ElapsedSeconds { get; init; }
}

public sealed class Session
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Level { get; set; } = "初级";
    public int GoalMinutes { get; set; }
    public DateTimeOffset StartAt { get; init; }
    public WorkMode Mode { get; init; } = WorkMode.OnPc;

    /// <summary>全屏形态只在 OffPc 时可选；记下来是为了以后能对比「全屏是否更不容易走神」。</summary>
    public bool Fullscreen { get; init; }

    public List<Segment> Segments { get; } = new();
    public List<Distraction> Distractions { get; } = new();

    public double TotalFocusSeconds => Segments.Sum(s => s.Seconds);
}

/// <summary>事件流落盘的一条。字段缩写是为了文件紧凑，也是给未来的自己看的。</summary>
public sealed class EventRecord
{
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("sid")] public string Sid { get; set; } = "";

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("goal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Goal { get; set; }

    [JsonPropertyName("level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Level { get; set; }

    [JsonPropertyName("dur")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Dur { get; set; }

    [JsonPropertyName("elapsed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Elapsed { get; set; }

    [JsonPropertyName("focus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Focus { get; set; }

    [JsonPropertyName("distracts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Distracts { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>onpc / offpc。老数据没有这个字段，读出来是 null，按未知处理。</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("full")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Full { get; set; }
}

public sealed class TickResult
{
    public bool GoalReached { get; set; }

    /// <summary>休息倒计时走完，进入缓冲期（缓冲期内按 1 回来，超时自动收工）。</summary>
    public bool BreakOver { get; set; }

    /// <summary>缓冲期也没等到人 —— 由 UI 按目标时长自动收工。</summary>
    public bool BreakIdle { get; set; }

    public bool Suspended { get; set; }
}

public sealed class SessionResult
{
    public required string Title { get; init; }
    public required int GoalMinutes { get; init; }
    public required double FocusSeconds { get; init; }
    public required int Distractions { get; init; }
    public required string Reason { get; init; }
    public WorkMode Mode { get; init; } = WorkMode.OnPc;
    public bool Fullscreen { get; init; }
}
