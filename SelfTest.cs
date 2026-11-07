using System.Text;
using FocusKeeper.Core;
using FocusKeeper.Storage;

namespace FocusKeeper;

/// <summary>无 UI 自检：验证状态机、存储、导入去重、文案加载。结果写 selftest.log（WinExe 没有控制台）。</summary>
internal static class SelfTest
{
    public static bool Run(string baseDir, string dataDir)
    {
        var sb = new StringBuilder();
        var pass = 0;
        var fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; sb.AppendLine($"PASS  {name}"); }
            else { fail++; sb.AppendLine($"FAIL  {name}  {detail}"); }
        }

        // ---- 1. 状态机 ----
        var clock = new FakeClock();
        var state = new AppState(clock) { GoalMinutes = 15, RestMinutes = 5 };
        var events = new List<EventRecord>();
        state.OnEvent = events.Add;

        state.Start("写 FocusKeeper", 15, "初级");
        Check("开始后进入 Focusing", state.Phase == Phase.Focusing);

        clock.Advance(300);
        state.Distract();
        clock.Advance(600);
        state.Distract();
        Check("走神计时不停（900s）", Math.Abs(state.FocusElapsed.TotalSeconds - 900) < 1,
            $"实际 {state.FocusElapsed.TotalSeconds}");

        var tick = state.Tick();
        Check("达到目标时长触发 GoalReached", tick.GoalReached);

        state.StartBreak();
        Check("休息中状态切到 Break", state.Phase == Phase.Break);
        clock.Advance(300);
        var t2 = state.Tick();
        Check("休息倒计时结束触发 BreakOver", t2.BreakOver);

        state.EndBreak();
        clock.Advance(300);

        var result = state.Stop();
        Check("休息时间不计入专注（应为 1200s）",
            result is not null && Math.Abs(result.FocusSeconds - 1200) < 1,
            $"实际 {result?.FocusSeconds}");
        Check("走神次数记 2 次", result?.Distractions == 2, $"实际 {result?.Distractions}");
        Check("结束后回到 Idle", state.Phase == Phase.Idle);
        Check("事件数量 >= 10", events.Count >= 10, $"实际 {events.Count}");

        // ---- 2. 睡眠检测 ----
        var c2 = new FakeClock();
        var s2 = new AppState(c2) { GoalMinutes = 30 };
        s2.Start("睡眠测试", 30, "中级");
        s2.Tick();
        c2.AdvanceWallOnly(7200);
        var t3 = s2.Tick();
        Check("墙钟跳 2 小时判为休眠", t3.Suspended);

        // ---- 2b. 到点未按结束：自动收工必须截到目标时长 ----
        var c3 = new FakeClock();
        var s3 = new AppState(c3) { GoalMinutes = 15 };
        var ev3 = new List<EventRecord>();
        s3.OnEvent = ev3.Add;
        s3.Start("忘了按结束", 15, "初级");
        c3.Advance(25 * 60);            // 目标 15 分钟，实际跑了 25 分钟
        s3.Tick();
        var capped = s3.Stop("auto", TimeSpan.FromMinutes(15));
        Check("自动收工截到目标时长（900s）",
            capped is not null && Math.Abs(capped.FocusSeconds - 900) < 1, $"实际 {capped?.FocusSeconds}");
        var durSum = ev3.Where(e => e.Type == "segment_end").Sum(e => e.Dur ?? 0);
        Check("截断后事件流时长 == 结果时长", Math.Abs(durSum - 900) < 1, $"事件流 {durSum}");

        var c4 = new FakeClock();
        var s4 = new AppState(c4) { GoalMinutes = 15, RestMinutes = 5 };
        s4.Start("跨段截断", 15, "初级");
        c4.Advance(600);
        s4.StartBreak();
        c4.Advance(300);                // 休息 5 分钟，不计入专注
        s4.EndBreak();
        c4.Advance(600);                // 累计专注 20 分钟
        var capped2 = s4.Stop("auto", TimeSpan.FromMinutes(15));
        Check("跨段截断只削超额部分（900s）",
            capped2 is not null && Math.Abs(capped2.FocusSeconds - 900) < 1, $"实际 {capped2?.FocusSeconds}");

        // ---- 2c. 载体（在这台电脑上 / 不在电脑上） ----
        var c5 = new FakeClock();
        var s5 = new AppState(c5) { GoalMinutes = 25 };
        var ev5 = new List<EventRecord>();
        s5.OnEvent = ev5.Add;
        s5.Start("看《心流》", 25, "中级", WorkMode.OffPc, true);

        var startEv = ev5.First(e => e.Type == "start");
        Check("开始事件记下载体与形态", startEv.Mode == "offpc" && startEv.Full == true,
            $"mode={startEv.Mode}, full={startEv.Full}");

        c5.Advance(25 * 60);
        var r5 = s5.Stop();
        Check("结果带回载体", r5 is not null && r5.Mode == WorkMode.OffPc && r5.Fullscreen);

        // 老数据没有 mode 字段，导出时留空，不能硬猜成「电脑上」
        var legacy = new List<EventRecord>
        {
            new() { T = "2026-11-06T09:00:00.000+08:00", Type = "start", Sid = "old1", Title = "老记录", Goal = 15 },
            new() { T = "2026-11-06T09:15:00.000+08:00", Type = "segment_end", Sid = "old1", Dur = 900 }
        };
        var legacySum = Stats.Sessions(legacy).Single();
        Check("老记录没有载体时留空", legacySum.ModeLabel == "" && legacySum.ShapeLabel == "",
            $"'{legacySum.ModeLabel}'/'{legacySum.ShapeLabel}'");

        // ---- 3. 存储与修复 ----
        var tmp = Path.Combine(Path.GetTempPath(), "fk-selftest-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        var storePath = Path.Combine(tmp, "events.jsonl");
        var store = new EventStore(storePath);

        foreach (var e in events) store.Append(e);
        var read = store.ReadAll(out var bad);
        Check("JSONL 写入后读回条数一致", read.Count == events.Count, $"{read.Count}/{events.Count}, bad={bad}");
        Check("无损坏行", bad == 0);

        // 悬空会话修复
        var store2 = new EventStore(Path.Combine(tmp, "dangling.jsonl"));
        store2.Append(new EventRecord { Type = "start", Sid = "abc", Title = "崩溃测试", Goal = 25 });
        store2.Append(new EventRecord { Type = "segment_end", Sid = "abc", Dur = 120, Reason = "stop" });
        var fixedCount = store2.RepairDanglingSessions();
        var after = store2.ReadAll(out _);
        Check("悬空会话被补 end", fixedCount == 1 && after.Any(x => x.Type == "end" && x.Sid == "abc"));

        // ---- 4. 导入去重 ----
        var (added, skipped) = store2.ImportFrom(store2.FilePath);
        Check("重复导入全部跳过", added == 0 && skipped > 0, $"added={added}, skipped={skipped}");

        // ---- 5. 统计 ----
        var summaries = Stats.Sessions(store.ReadAll(out _));
        var today = Stats.Day(summaries, DateTime.Today);
        Check("今日统计能算出专注分钟", today.FocusMinutes >= 20, $"实际 {today.FocusMinutes}");

        // ---- 6. CSV 导出 ----
        var csv = Stats.ToCsv(summaries);
        Check("CSV 有中文表头", csv.StartsWith("日期,开始时间,标题"), csv.Split('\n')[0]);
        Check("CSV 行数 = 会话数 + 表头", csv.TrimEnd('\r', '\n').Split("\r\n").Length == summaries.Count + 1);
        var csvBytes = Stats.ToCsvBytes(summaries);
        Check("CSV 带 UTF-8 BOM（Excel 不乱码）",
            csvBytes.Length > 3 && csvBytes[0] == 0xEF && csvBytes[1] == 0xBB && csvBytes[2] == 0xBF);

        var tricky = new List<SessionSummary>
        {
            new("x1", new DateTime(2026, 11, 7), new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(8)),
                "写代码,顺便发呆", "中级", 25, 1500, 3, 2, true)
        };
        var trickyCsv = Stats.ToCsv(tricky);
        Check("CSV 标题里的逗号被引号包裹", trickyCsv.Contains("\"写代码,顺便发呆\""), trickyCsv);

        Check("CSV 表头有载体与形态列", csv.Contains("在哪做") && csv.Contains("形态"), csv.Split("\r\n")[0]);
        Check("CSV 里「电脑上」的会话有标记", csv.Contains("电脑上"));

        var offCsv = Stats.ToCsv(new List<SessionSummary>
        {
            new("o1", new DateTime(2026, 11, 7), new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(8)),
                "看《心流》", "中级", 25, 1500, 2, 1, true, "offpc", true)
        });
        Check("CSV 写出「不在电脑上 / 全屏」",
            offCsv.Contains("不在电脑上") && offCsv.Contains("全屏"), offCsv);

        // ---- 7. 文案库 ----
        var phrasesPath = Path.Combine(dataDir, "phrases.json");
        var phrases = Phrases.Load(phrasesPath);
        var bank = File.Exists(phrasesPath);
        Check("phrases.json 存在", bank, phrasesPath);
        var p1 = phrases.Next("distract");
        Check("走神文案有内容", p1.Text.Length > 4, p1.Text);
        var repeatFree = true;
        var last = p1.Text;
        for (var i = 0; i < 30; i++)
        {
            var n = phrases.Next("distract");
            if (n.Text == last) repeatFree = false;
            last = n.Text;
        }
        Check("走神文案不连续重复", repeatFree);

        foreach (var cat in new[] { "nudge", "unclosed", "auto" })
        {
            var p = phrases.Next(cat);
            Check($"文案类别 {cat} 有内容", p.Text.Length > 6, p.Text);
        }

        var nudgeTexts = Enumerable.Range(0, 12).Select(_ => phrases.Next("nudge").Text).ToList();
        var banned = new[] { "加油", "相信自己", "你是最棒", "坚持就是胜利", "再不", "又没", "懒" };
        Check("今日提醒不含鸡汤词与贬损词",
            nudgeTexts.All(t => banned.All(b => !t.Contains(b))),
            nudgeTexts.FirstOrDefault(t => banned.Any(t.Contains)) ?? "");

        Check("到点提醒交代了自动收工规则",
            Enumerable.Range(0, 4).All(_ => phrases.Next("unclosed").Text.Contains("{wait}")));

        var summary = $"selftest: {pass} passed, {fail} failed";
        sb.AppendLine();
        sb.AppendLine(summary);
        File.WriteAllText(Path.Combine(baseDir, "selftest.log"), sb.ToString(), new UTF8Encoding(false));

        try { Directory.Delete(tmp, true); } catch { }
        return fail == 0;
    }

    private sealed class FakeClock : IClock
    {
        private DateTimeOffset _now = new(2026, 11, 7, 10, 0, 0, TimeSpan.FromHours(8));
        private TimeSpan _mono = TimeSpan.Zero;

        public DateTimeOffset Now => _now;
        public TimeSpan Monotonic => _mono;

        public void Advance(double seconds)
        {
            _now = _now.AddSeconds(seconds);
            _mono += TimeSpan.FromSeconds(seconds);
        }

        public void AdvanceWallOnly(double seconds) => _now = _now.AddSeconds(seconds);
    }
}
