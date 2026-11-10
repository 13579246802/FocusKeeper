namespace FocusKeeper.Core;

/// <summary>
/// 状态机：完全不依赖 WinForms，方便单测（注入假时钟）。
/// 一次 Session = 按 1 到按 2；休息把 Session 切成多个 Segment。专注时长 = Σ Segment。
/// </summary>
public sealed class AppState
{
    private readonly IClock _clock;

    private DateTimeOffset? _lastWall;
    private TimeSpan? _lastMono;

    private TimeSpan? _segStartMono;
    private DateTimeOffset _segStartWall;

    private TimeSpan? _breakEndMono;
    private TimeSpan? _graceEndMono;
    private bool _graceNotified;

    public AppState(IClock clock)
    {
        _clock = clock;
    }

    public Phase Phase { get; private set; } = Phase.Idle;
    public Session? Current { get; private set; }
    public string Title { get; private set; } = "";
    public string Level { get; set; } = "初级";
    public int GoalMinutes { get; set; } = 15;
    public int RestMinutes { get; set; } = 5;

    /// <summary>休息倒计时走完后的缓冲期（分钟）：人不在键盘前，给个回来的窗口。</summary>
    public int BreakGraceMinutes { get; set; } = 2;

    public bool GoalReached { get; private set; }
    public TimeSpan BreakRemaining { get; private set; } = TimeSpan.Zero;

    /// <summary>休息倒计时走完后进入缓冲期；缓冲期内按 1 回来，超时由 UI 自动收工。</summary>
    public bool InBreakGrace { get; private set; }
    public TimeSpan GraceRemaining { get; private set; } = TimeSpan.Zero;

    /// <summary>等级 → 默认休息时长。休息要和专注的长度匹配：短专注配长休息只会让人走神。</summary>
    public static int RestForLevel(string level) => level switch
    {
        "高级" => 10,
        "中级" => 5,
        _ => 3
    };

    /// <summary>每产生一条事件就回调一次，由 UI 负责落盘。</summary>
    public Action<EventRecord>? OnEvent { get; set; }

    /// <summary>当前专注时长（含正在进行的这一段，不含休息）。</summary>
    public TimeSpan FocusElapsed
    {
        get
        {
            if (Current is null) return TimeSpan.Zero;
            var acc = TimeSpan.FromSeconds(Current.TotalFocusSeconds);
            if (Phase == Phase.Focusing && _segStartMono.HasValue)
                acc += _clock.Monotonic - _segStartMono.Value;
            return acc;
        }
    }

    /// <summary>
    /// 当前这一段已专注的时长。提醒休息按「段」算而不是按「总」算：
    /// 否则休息回来后总时长早超目标，刚开始新一段就立刻提醒，循环就没法周而复始了。
    /// </summary>
    public TimeSpan SegmentElapsed =>
        Phase == Phase.Focusing && _segStartMono.HasValue
            ? _clock.Monotonic - _segStartMono.Value
            : TimeSpan.Zero;

    // ---------- 状态迁移 ----------

    public void OpenTitleInput() => Phase = Phase.TitleInput;

    public void CancelTitleInput() => Phase = Phase.Idle;

    public void Start(string? title, int goalMinutes, string level,
                      WorkMode mode = WorkMode.OnPc, bool fullscreen = false)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "未命名专注" : title.Trim();
        GoalMinutes = Math.Clamp(goalMinutes, 1, 240);
        Level = level;
        GoalReached = false;

        Current = new Session
        {
            Title = Title,
            GoalMinutes = GoalMinutes,
            Level = Level,
            StartAt = _clock.Now,
            Mode = mode,
            Fullscreen = fullscreen
        };

        Emit(new EventRecord
        {
            Type = "start",
            Sid = Current.Id,
            Title = Title,
            Goal = GoalMinutes,
            Level = Level,
            Mode = mode.Key(),
            Full = fullscreen
        });

        NewSegment();
        Phase = Phase.Focusing;
    }

    /// <summary>走神：只记录，计时绝不停。</summary>
    public bool Distract()
    {
        if (Phase != Phase.Focusing || Current is null) return false;

        var elapsed = FocusElapsed.TotalSeconds;
        Current.Distractions.Add(new Distraction
        {
            At = _clock.Now,
            ElapsedSeconds = elapsed
        });

        Emit(new EventRecord
        {
            Type = "distract",
            Sid = Current.Id,
            Elapsed = Math.Round(elapsed, 1)
        });

        return true;
    }

    public void StartBreak()
    {
        if (Phase != Phase.Focusing || Current is null) return;

        EndSegment("break");
        _breakEndMono = _clock.Monotonic + TimeSpan.FromMinutes(RestMinutes);
        BreakRemaining = TimeSpan.FromMinutes(RestMinutes);
        Phase = Phase.Break;

        Emit(new EventRecord
        {
            Type = "break_start",
            Sid = Current.Id,
            Dur = RestMinutes * 60
        });
    }

    /// <summary>结束休息，回到同一 Session 的专注（新起一段）。缓冲期内同样走这里。</summary>
    public void EndBreak()
    {
        if (Phase != Phase.Break || Current is null) return;

        Emit(new EventRecord { Type = "break_end", Sid = Current.Id });
        _breakEndMono = null;
        _graceEndMono = null;
        _graceNotified = false;
        InBreakGrace = false;
        BreakRemaining = TimeSpan.Zero;
        GraceRemaining = TimeSpan.Zero;

        // 新一段从头算提醒：上一轮的「已达标」不能带到这一轮
        GoalReached = false;

        NewSegment();
        Phase = Phase.Focusing;
    }

    /// <param name="capFocus">
    /// 可选上限：本次结束后总专注时长不超过它。用于「到点没人按结束」的自动收工——
    /// 否则宽限期那几分钟会被算进专注，统计就虚了。
    /// </param>
    public SessionResult? Stop(string reason = "stop", TimeSpan? capFocus = null)
    {
        if (Current is null)
        {
            Phase = Phase.Idle;
            return null;
        }

        if (Phase == Phase.Focusing) EndSegment(reason, capFocus);
        if (Phase == Phase.Break)
        {
            Emit(new EventRecord { Type = "break_end", Sid = Current.Id });
            _breakEndMono = null;
            _graceEndMono = null;
            InBreakGrace = false;
            GraceRemaining = TimeSpan.Zero;
        }

        var result = new SessionResult
        {
            Title = Current.Title,
            GoalMinutes = Current.GoalMinutes,
            FocusSeconds = Current.TotalFocusSeconds,
            Distractions = Current.Distractions.Count,
            Reason = reason,
            Mode = Current.Mode,
            Fullscreen = Current.Fullscreen
        };

        Emit(new EventRecord
        {
            Type = "end",
            Sid = Current.Id,
            Focus = Math.Round(result.FocusSeconds, 1),
            Distracts = result.Distractions,
            Reason = reason
        });

        Current = null;
        Phase = Phase.Idle;
        GoalReached = false;
        BreakRemaining = TimeSpan.Zero;
        GraceRemaining = TimeSpan.Zero;
        InBreakGrace = false;
        return result;
    }

    // ---------- 每帧检查 ----------

    public TickResult Tick()
    {
        var r = new TickResult();
        var now = _clock.Now;
        var mono = _clock.Monotonic;

        // 睡眠检测：墙钟走了很久但单调时钟几乎没动 => 系统挂起过
        if (_lastWall.HasValue && _lastMono.HasValue)
        {
            var wallDelta = (now - _lastWall.Value).TotalSeconds;
            var monoDelta = (mono - _lastMono.Value).TotalSeconds;
            if (wallDelta - monoDelta > 60) r.Suspended = true;
        }
        _lastWall = now;
        _lastMono = mono;

        // 提醒休息按「段」算：每个循环专注满目标分钟就提醒一次
        if (Phase == Phase.Focusing && !GoalReached && SegmentElapsed.TotalMinutes >= GoalMinutes)
        {
            GoalReached = true;
            r.GoalReached = true;
        }

        if (Phase == Phase.Break && !InBreakGrace && _breakEndMono.HasValue)
        {
            var remain = _breakEndMono.Value - mono;
            BreakRemaining = remain > TimeSpan.Zero ? remain : TimeSpan.Zero;

            // 休息倒计时走完 → 进入缓冲期：不结束会话，等人回来；
            // 缓冲期内按 1 继续，超时由 UI 自动收工（BreakIdle）。
            if (remain <= TimeSpan.Zero)
            {
                InBreakGrace = true;
                _graceEndMono = mono + TimeSpan.FromMinutes(Math.Max(1, BreakGraceMinutes));
                GraceRemaining = TimeSpan.FromMinutes(Math.Max(1, BreakGraceMinutes));
                _graceNotified = false;
                r.BreakOver = true;
            }
        }

        if (Phase == Phase.Break && InBreakGrace && _graceEndMono.HasValue)
        {
            var gremain = _graceEndMono.Value - mono;
            GraceRemaining = gremain > TimeSpan.Zero ? gremain : TimeSpan.Zero;
            if (gremain <= TimeSpan.Zero && !_graceNotified)
            {
                _graceNotified = true;
                r.BreakIdle = true;
            }
        }

        return r;
    }

    // ---------- 内部 ----------

    private void NewSegment()
    {
        _segStartMono = _clock.Monotonic;
        _segStartWall = _clock.Now;
        Emit(new EventRecord { Type = "segment_start", Sid = Current?.Id ?? "" });
    }

    private void EndSegment(string reason, TimeSpan? capFocus = null)
    {
        if (Current is null || !_segStartMono.HasValue) return;

        var dur = (_clock.Monotonic - _segStartMono.Value).TotalSeconds;

        // 本段只补到上限为止；之前各段的时长已定，不再回收
        if (capFocus.HasValue)
        {
            var already = Current.TotalFocusSeconds;
            dur = Math.Min(dur, Math.Max(0, capFocus.Value.TotalSeconds - already));
        }

        Current.Segments.Add(new Segment
        {
            Start = _segStartWall,
            // 用单调时长反推结束时刻，而不是直接取墙钟：
            // 截断时才不会出现「段记 900 秒、Segment.Seconds 却是 1200 秒」的自相矛盾
            End = _segStartWall + TimeSpan.FromSeconds(Math.Max(0, dur)),
            EndReason = reason
        });

        Emit(new EventRecord
        {
            Type = "segment_end",
            Sid = Current.Id,
            Dur = Math.Round(Math.Max(0, dur), 1),
            Reason = reason
        });

        _segStartMono = null;
    }

    private void Emit(EventRecord e)
    {
        e.T = _clock.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
        OnEvent?.Invoke(e);
    }
}
