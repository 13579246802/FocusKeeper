using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusKeeper.Storage;

public sealed class SettingsData
{
    [JsonPropertyName("goalMinutes")] public int GoalMinutes { get; set; } = 15;

    /// <summary>休息时长（分钟）。0 = 按等级自动：初级 3 / 中级 5 / 高级 10。</summary>
    [JsonPropertyName("restMinutes")] public int RestMinutes { get; set; } = 0;

    /// <summary>休息倒计时走完后的缓冲期（分钟）：人不在键盘前，给个回来的窗口。</summary>
    [JsonPropertyName("breakGraceMinutes")] public int BreakGraceMinutes { get; set; } = 2;

    /// <summary>休息时放不放雨声。M 键或右键菜单随时切。</summary>
    [JsonPropertyName("musicOn")] public bool MusicOn { get; set; } = true;
    [JsonPropertyName("level")] public string Level { get; set; } = "初级";
    [JsonPropertyName("lastTitle")] public string LastTitle { get; set; } = "";
    [JsonPropertyName("globalHotkey")] public bool GlobalHotkey { get; set; } = false;
    [JsonPropertyName("windowX")] public int WindowX { get; set; } = -1;
    [JsonPropertyName("windowY")] public int WindowY { get; set; } = -1;

    /// <summary>上次选择的载体与形态。记住它，第二次开始按 1 直接回车就行，不用再选一遍。</summary>
    [JsonPropertyName("lastWorkMode")] public string LastWorkMode { get; set; } = "onpc";
    [JsonPropertyName("lastFullscreen")] public bool LastFullscreen { get; set; }

    /// <summary>到点后过了多久才提醒收工（分钟）。</summary>
    [JsonPropertyName("autoStopGraceMinutes")] public int AutoStopGraceMinutes { get; set; } = 2;

    /// <summary>提醒弹出后，多久没按键就按目标时长自动收工（分钟）。</summary>
    [JsonPropertyName("autoStopMinutes")] public int AutoStopMinutes { get; set; } = 5;

    /// <summary>今日未练提醒：上次提示的日期、次数、时刻（跨重启保留，避免重启刷屏）。</summary>
    [JsonPropertyName("nudgeDate")] public string NudgeDate { get; set; } = "";
    [JsonPropertyName("nudgeCount")] public int NudgeCount { get; set; }
    [JsonPropertyName("lastNudgeAt")] public string LastNudgeAt { get; set; } = "";
}

public static class Settings
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static SettingsData Load(string path)
    {
        if (!File.Exists(path)) return new SettingsData();
        try
        {
            return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path), Opts) ?? new SettingsData();
        }
        catch
        {
            return new SettingsData();
        }
    }

    public static void Save(string path, SettingsData data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(data, Opts));
    }
}
