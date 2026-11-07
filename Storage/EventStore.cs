using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusKeeper.Storage;

/// <summary>
/// 追加写的事件存储。每写一条立即落盘，进程被强杀最多丢最后一行。
/// 读取时逐行容错：坏行跳过并计数，不整体失败。
/// </summary>
public sealed class EventStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();

    public EventStore(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string FilePath { get; }

    public void Append(Core.EventRecord e)
    {
        var line = JsonSerializer.Serialize(e, Opts) + "\n";
        lock (_lock)
        {
            using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false));
            sw.Write(line);
        }
    }

    public List<Core.EventRecord> ReadAll(out int badLines)
    {
        badLines = 0;
        var list = new List<Core.EventRecord>();
        if (!File.Exists(FilePath)) return list;

        foreach (var line in File.ReadLines(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var rec = JsonSerializer.Deserialize<Core.EventRecord>(line, Opts);
                if (rec is not null && !string.IsNullOrEmpty(rec.Type)) list.Add(rec);
                else badLines++;
            }
            catch
            {
                badLines++;
            }
        }
        return list;
    }

    /// <summary>启动修复：有 start 没有 end 的会话，按最后一条事件时间补一个 end。</summary>
    public int RepairDanglingSessions()
    {
        var events = ReadAll(out _);
        var repaired = 0;

        var groups = events.GroupBy(e => e.Sid).ToList();
        foreach (var g in groups)
        {
            if (string.IsNullOrEmpty(g.Key)) continue;
            if (!g.Any(e => e.Type == "start")) continue;
            if (g.Any(e => e.Type == "end")) continue;

            var focus = g.Where(e => e.Type == "segment_end").Sum(e => e.Dur ?? 0);
            var distracts = g.Count(e => e.Type == "distract");

            // 未闭合的段：用「最后一条事件时间 - 段开始时间」估算，避免崩溃丢掉刚专注的时长
            var ordered = g.OrderBy(e => e.T).ToList();
            var openIdx = ordered.FindLastIndex(e => e.Type == "segment_start");
            var closedAfter = openIdx >= 0 && ordered.Skip(openIdx + 1).Any(e => e.Type == "segment_end");
            if (openIdx >= 0 && !closedAfter)
            {
                if (DateTimeOffset.TryParse(ordered[openIdx].T, out var segStart) &&
                    DateTimeOffset.TryParse(ordered[^1].T, out var lastAt))
                {
                    var dur = Math.Clamp((lastAt - segStart).TotalSeconds, 0, 4 * 3600);
                    if (dur > 0)
                    {
                        Append(new Core.EventRecord
                        {
                            T = lastAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"),
                            Type = "segment_end",
                            Sid = g.Key,
                            Dur = Math.Round(dur, 1),
                            Reason = "crash"
                        });
                        focus += dur;
                    }
                }
            }

            Append(new Core.EventRecord
            {
                T = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"),
                Type = "end",
                Sid = g.Key,
                Focus = Math.Round(focus, 1),
                Distracts = distracts,
                Reason = "crash"
            });
            repaired++;
        }

        return repaired;
    }

    public string ExportTo(string targetPath)
    {
        File.Copy(FilePath, targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>导入：按 sid+t+type 去重后追加。返回 (新增, 跳过)。</summary>
    public (int Added, int Skipped) ImportFrom(string sourcePath)
    {
        var existing = ReadAll(out _)
            .Select(Key)
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        var skipped = 0;

        foreach (var line in File.ReadLines(sourcePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Core.EventRecord? rec;
            try { rec = JsonSerializer.Deserialize<Core.EventRecord>(line, Opts); }
            catch { skipped++; continue; }

            if (rec is null || string.IsNullOrEmpty(rec.Type)) { skipped++; continue; }

            if (!existing.Add(Key(rec))) { skipped++; continue; }

            Append(rec);
            added++;
        }

        return (added, skipped);
    }

    private static string Key(Core.EventRecord e) => $"{e.Sid}|{e.T}|{e.Type}";
}
