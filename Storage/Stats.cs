using System.Text;
using FocusKeeper.Core;

namespace FocusKeeper.Storage;

public sealed record SessionSummary(
    string Sid,
    DateTime Date,
    DateTimeOffset StartAt,
    string Title,
    string Level,
    int GoalMinutes,
    double FocusSeconds,
    int Distractions,
    int Segments,
    bool Achieved,
    string Mode = "",
    bool Fullscreen = false)
{
    public int FocusMinutes => (int)Math.Round(FocusSeconds / 60);

    /// <summary>老数据没有 mode 字段，导出时留空，不硬猜成「电脑上」。</summary>
    public string ModeLabel => Mode switch
    {
        "onpc" => "电脑上",
        "offpc" => "不在电脑上",
        _ => ""
    };

    public string ShapeLabel => Mode == "offpc" ? (Fullscreen ? "全屏" : "小窗") : "";
}

public static partial class Stats
{
    public static List<SessionSummary> Sessions(IEnumerable<EventRecord> events)
    {
        var result = new List<SessionSummary>();

        foreach (var g in events.GroupBy(e => e.Sid))
        {
            if (string.IsNullOrEmpty(g.Key)) continue;

            var start = g.FirstOrDefault(e => e.Type == "start");
            if (start is null) continue;

            var focus = g.Where(e => e.Type == "segment_end").Sum(e => e.Dur ?? 0);
            var segCount = g.Count(e => e.Type == "segment_end");
            var distracts = g.Count(e => e.Type == "distract");
            var goal = start.Goal ?? 0;

            DateTimeOffset startAt = DateTimeOffset.TryParse(start.T, out var dto)
                ? dto
                : new DateTimeOffset(DateTime.Today, TimeSpan.Zero);

            result.Add(new SessionSummary(
                g.Key,
                startAt.LocalDateTime.Date,
                startAt,
                start.Title ?? "未命名专注",
                start.Level ?? "初级",
                goal,
                focus,
                distracts,
                segCount,
                goal > 0 && focus >= goal * 60,
                start.Mode ?? "",
                start.Full ?? false));
        }

        return result.OrderBy(s => s.StartAt).ToList();
    }

    public static DayStat Day(IEnumerable<SessionSummary> all, DateTime date)
    {
        var list = all.Where(s => s.Date == date.Date).ToList();
        return new DayStat(
            list.Sum(s => s.FocusSeconds),
            list.Count,
            list.Sum(s => s.Distractions),
            list.Count(s => s.Achieved));
    }

    public static List<DayStat> LastDays(IEnumerable<SessionSummary> all, int days, DateTime? today = null)
    {
        var t = (today ?? DateTime.Today).Date;
        var list = new List<DayStat>();
        for (var i = days - 1; i >= 0; i--)
            list.Add(Day(all, t.AddDays(-i)));
        return list;
    }

    /// <summary>核心指标：相邻两次走神之间的间隔中位数（秒）。走神事件带 elapsed，直接算。</summary>
    public static double MedianDistractionGapSec(IEnumerable<EventRecord> events, DateTime date)
    {
        var gaps = new List<double>();

        foreach (var g in events.GroupBy(e => e.Sid))
        {
            var start = g.FirstOrDefault(e => e.Type == "start");
            if (start is null || !DateTimeOffset.TryParse(start.T, out var dto)) continue;
            if (dto.LocalDateTime.Date != date.Date) continue;

            var marks = g.Where(e => e.Type == "distract")
                         .Select(e => e.Elapsed ?? 0)
                         .OrderBy(x => x)
                         .ToList();

            double prev = 0;
            foreach (var m in marks)
            {
                gaps.Add(m - prev);
                prev = m;
            }
        }

        if (gaps.Count == 0) return 0;
        gaps.Sort();
        var mid = gaps.Count / 2;
        return gaps.Count % 2 == 1 ? gaps[mid] : (gaps[mid - 1] + gaps[mid]) / 2;
    }

    /// <summary>导出 CSV：一行一个 session，Excel 直接透视。带 BOM，中文不乱码。</summary>
    public static string ToCsv(IEnumerable<SessionSummary> sessions)
    {
        var sb = new StringBuilder();
        sb.Append("日期,开始时间,标题,在哪做,形态,级别,目标分钟,专注分钟,专注秒,段数,走神次数,达标\r\n");

        foreach (var s in sessions.OrderBy(x => x.StartAt))
        {
            sb.Append(s.Date.ToString("yyyy-MM-dd")).Append(',');
            sb.Append(s.StartAt.ToString("HH:mm")).Append(',');
            sb.Append(Escape(s.Title)).Append(',');
            sb.Append(Escape(s.ModeLabel)).Append(',');
            sb.Append(Escape(s.ShapeLabel)).Append(',');
            sb.Append(Escape(s.Level)).Append(',');
            sb.Append(s.GoalMinutes).Append(',');
            sb.Append(s.FocusMinutes).Append(',');
            sb.Append(Math.Round(s.FocusSeconds, 0)).Append(',');
            sb.Append(s.Segments).Append(',');
            sb.Append(s.Distractions).Append(',');
            sb.Append(s.Achieved ? "是" : "否").Append("\r\n");
        }

        return sb.ToString();
    }

    private static string Escape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    /// <summary>注意：Encoding.GetBytes 不会自动带 BOM，必须手动拼 preamble，否则 Excel 打开中文乱码。</summary>
    public static byte[] ToCsvBytes(IEnumerable<SessionSummary> sessions)
    {
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = enc.GetPreamble();
        var body = enc.GetBytes(ToCsv(sessions));

        var buffer = new byte[preamble.Length + body.Length];
        preamble.CopyTo(buffer, 0);
        body.CopyTo(buffer, preamble.Length);
        return buffer;
    }
}

public sealed record DayStat(double FocusSeconds, int Sessions, int Distractions, int Achieved)
{
    public int FocusMinutes => (int)Math.Round(FocusSeconds / 60);
}

public sealed record Recommendation(int GoalMinutes, string Reason);

public static partial class Stats
{
    /// <summary>
    /// 按最近表现推荐时长。规则必须能一句话讲清，不玩黑箱：
    /// 近 3 次（14 天内）都达标且走神不密 → +10（上限 60）；都没到 → -5（下限 10）；其余维持。
    /// 刚练 1 次不加不减——单次表现说明不了趋势。
    /// </summary>
    public static Recommendation Recommend(List<SessionSummary> all, int currentGoal, DateTime? today = null)
    {
        var t = (today ?? DateTime.Today).Date;
        var recent = all.Where(s => (t - s.Date).TotalDays <= 14)
                        .OrderBy(s => s.StartAt)
                        .TakeLast(3)
                        .ToList();

        if (recent.Count == 0)
        {
            return all.Count > 0
                ? new Recommendation(Math.Clamp(currentGoal, 10, 60), "两周没练了，从原来的档位重新起步就够")
                : new Recommendation(15, "还没开始记录，先从 15 分钟练起");
        }

        if (recent.Count < 2)
        {
            return new Recommendation(Math.Clamp(currentGoal, 10, 60),
                recent[0].Achieved ? "刚回来练，先在这一档坐稳" : "上次没到目标，同一档再来一次");
        }

        var allDone = recent.All(s => s.Achieved);
        var noneDone = recent.All(s => !s.Achieved);

        if (allDone)
        {
            // 走神密度：每 30 分钟专注里走神超过 3 次，说明这一档还没坐稳，先不加
            var dense = recent.Any(s => s.FocusSeconds > 0
                                        && s.Distractions / (s.FocusSeconds / 1800.0) > 3);
            if (!dense && currentGoal < 60)
                return new Recommendation(currentGoal + 10, "最近几次都到了目标，加 10 分钟试试");

            return new Recommendation(Math.Clamp(currentGoal, 10, 60),
                dense ? "达标了，但走神还密，先在这一档坐稳" : "已经在最高档，稳住就是赢");
        }

        if (noneDone)
            return new Recommendation(Math.Max(10, currentGoal - 5), "最近几次都没到目标，减 5 分钟更稳");

        return new Recommendation(Math.Clamp(currentGoal, 10, 60), "按现在的节奏来，不用急");
    }
}
