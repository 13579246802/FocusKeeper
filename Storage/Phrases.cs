using System.Text.Json;

namespace FocusKeeper.Storage;

public sealed class Phrase
{
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class PhraseBank
{
    public List<Phrase> Distract { get; set; } = new();
    public List<Phrase> Goal { get; set; } = new();
    public List<Phrase> BreakOver { get; set; } = new();
    public List<Phrase> Stop { get; set; } = new();

    /// <summary>今天还没开始时的提示。不评价、不催。</summary>
    public List<Phrase> Nudge { get; set; } = new();

    /// <summary>到点了还没按结束时的提醒（要说明自动收工规则）。</summary>
    public List<Phrase> Unclosed { get; set; } = new();

    /// <summary>到点没人按、按目标时长自动收工后的说明。</summary>
    public List<Phrase> Auto { get; set; } = new();
}

/// <summary>
/// 文案池：随机取，且不连续重复。占位符 {min} {n} {goal} {over} {today} 由调用方替换。
/// </summary>
public sealed class Phrases
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly PhraseBank _bank;
    private readonly Dictionary<string, int> _lastIndex = new();
    private readonly Random _rng = new();

    private Phrases(PhraseBank bank) => _bank = bank;

    public string FilePath { get; private set; } = "";

    public static Phrases Load(string path)
    {
        var bank = new PhraseBank();
        if (File.Exists(path))
        {
            try
            {
                bank = JsonSerializer.Deserialize<PhraseBank>(File.ReadAllText(path), Opts) ?? new PhraseBank();
            }
            catch
            {
                // 文案库坏了不能阻塞主流程，退回内置兜底
            }
        }

        if (bank.Distract.Count == 0) bank.Distract.Add(new Phrase { Text = "回来了就继续。" });
        if (bank.Goal.Count == 0) bank.Goal.Add(new Phrase { Text = "{min} 分钟到手。" });
        if (bank.BreakOver.Count == 0) bank.BreakOver.Add(new Phrase { Text = "歇够了，接着来。" });
        if (bank.Stop.Count == 0) bank.Stop.Add(new Phrase { Text = "今天累计 {today} 分钟。" });
        if (bank.Nudge.Count == 0) bank.Nudge.Add(new Phrase { Text = "今天还没有记录。想开始时按 1。" });
        if (bank.Unclosed.Count == 0) bank.Unclosed.Add(new Phrase { Text = "已到 {goal} 分钟。按 2 收工；{wait} 分钟内没有按键，就按 {goal} 分钟记录。" });
        if (bank.Auto.Count == 0) bank.Auto.Add(new Phrase { Text = "按 {goal} 分钟收工了。要继续就按 1。" });

        return new Phrases(bank) { FilePath = path };
    }

    public Phrase Next(string category)
    {
        var list = category switch
        {
            "distract" => _bank.Distract,
            "goal" => _bank.Goal,
            "break" => _bank.BreakOver,
            "nudge" => _bank.Nudge,
            "unclosed" => _bank.Unclosed,
            "auto" => _bank.Auto,
            _ => _bank.Stop
        };

        if (list.Count == 0) return new Phrase { Text = "" };

        var idx = _rng.Next(list.Count);
        if (list.Count > 1 && _lastIndex.TryGetValue(category, out var last) && idx == last)
            idx = (idx + 1) % list.Count;

        _lastIndex[category] = idx;
        return list[idx];
    }
}
