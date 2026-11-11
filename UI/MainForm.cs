using System.Runtime.InteropServices;
using FocusKeeper.Core;
using FocusKeeper.Platform;
using FocusKeeper.Storage;
using Microsoft.Win32;

namespace FocusKeeper.UI;

public sealed class MainForm : Form
{
    private readonly AppState _state;
    private readonly EventStore _store;
    private readonly Phrases _phrases;
    private readonly SettingsData _settings;
    private readonly string _settingsPath;
    private readonly string _exportDir;
    private readonly IdleWatcher _idle = new();
    private readonly System.Windows.Forms.Timer _timer;
    private GlobalHotkey? _hotkey;

    private Label _lblTime = null!;
    private Label _lblTitle = null!;
    private Label _lblMeta = null!;
    private Label _lblMsg = null!;
    private Label _lblHint = null!;
    private TitleBox _txtTitle = null!;
    private Panel _pnlStats = null!;
    private Label _lblStatHead = null!;
    private Label _lblStatMain = null!;
    private Label _lblStatSub = null!;
    private Label _lblStatHint = null!;
    private Panel _pnlChart = null!;

    // 标题输入时的载体选择
    private Label _lblAsk = null!;
    private Label _optPc = null!;
    private Label _optOffWindow = null!;
    private Label _optOffFull = null!;
    private Label _lblModeNote = null!;

    // 全屏形态
    private BufferedPanel _pnlFull = null!;
    private Label _fsTitle = null!;
    private Label _fsTime = null!;
    private Label _fsMsg = null!;
    private Label _fsHint = null!;
    private bool _fullscreen;          // 用户想要的形态
    private bool _fullscreenActive;    // 当前真的铺满了屏幕
    private Rectangle _windowedBounds;
    private Size _fsLaidOutFor;
    private WorkMode _pendingMode = WorkMode.OnPc;
    private bool _pendingFullscreen;
    private bool _statsWanted;
    private string _pendingNotice = "";

    private string _message = "";
    private DateTime _messageUntil = DateTime.MinValue;
    private bool _idlePrompted;
    private double _todayFocusSec;
    private int _todayDistracts;
    private int _todaySessions;

    // 到点未结束的自动收工
    private DateTime? _autoStopAt;
    private bool _unclosedShown;

    // 今日未练提醒
    private DateTime _lastNudgeAt = DateTime.MinValue;
    private DateTime _loadedDay = DateTime.Today;
    private bool _demoMode;

    private bool _statsDirty = true;
    private DayStat _todayStat = new(0, 0, 0, 0);
    private double _medianGap;
    private int _badLines;
    private readonly List<(string Label, int Minutes, bool Today)> _chart = new();

    // 待机时显示的「今日一句」：每天换一条，一天内不闪变
    private string _dailyQuote = "";

    // 休息音乐开关（M 键 / 右键菜单）
    private bool _musicOn;
    private readonly MusicLibrary _music;
    private readonly string _userMusicDir;

    private const string HintIdle = "1 开始   ↑↓ 等级   ←→ 分钟   5 统计";
    private const string HintFocus = "1 开始  2 结束  3 走神  4 休息";
    private const string HintBreak = "1 回来   4 也回来   M 音乐";

    public MainForm(EventStore store, Phrases phrases, SettingsData settings, string settingsPath, string exportDir, int repaired)
    {
        _store = store;
        _phrases = phrases;
        _settings = settings;
        _settingsPath = settingsPath;
        _exportDir = exportDir;

        _state = new AppState(new SystemClock())
        {
            GoalMinutes = settings.GoalMinutes,
            RestMinutes = ResolveRest(settings),
            BreakGraceMinutes = Math.Clamp(settings.BreakGraceMinutes, 1, 10),
            Level = settings.Level
        };
        _state.OnEvent = _store.Append;
        _musicOn = settings.MusicOn;

        // 曲库：data\music（用户上传）优先，exe 旁 music\（自带）兜底；放不出来就换一首试，全失败静音
        _userMusicDir = Path.Combine(Path.GetDirectoryName(settingsPath) ?? ".", "music");
        _music = new MusicLibrary(_userMusicDir, Path.Combine(AppContext.BaseDirectory, "music"));

        _pendingMode = WorkModeText.Parse(settings.LastWorkMode);
        _pendingFullscreen = settings.LastFullscreen;

        BuildUi();
        LoadToday();
        _dailyQuote = PickQuote();
        ApplyWindowPos();

        // 跨重启保留「上次提醒时刻」，否则一天开关几次就会重复被念
        if (DateTime.TryParse(_settings.LastNudgeAt, out var lastNudge)) _lastNudgeAt = lastNudge;

        SystemEvents.SessionSwitch += OnSessionSwitch;

        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        if (repaired > 0)
            ShowMessage($"上次有 {repaired} 段没有正常结束，已按最后一条记录补上。", 8);
        else
            ApplyRecommendation(announce: true);   // 开机时按最近表现推荐今天的时长

        RefreshUi();
    }

    // ---------------- UI ----------------

    private void BuildUi()
    {
        SuspendLayout();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(18, 20, 23);
        ForeColor = Color.FromArgb(232, 234, 237);
        ClientSize = new Size(360, 228);
        Text = "FocusKeeper";
        KeyPreview = true;
        DoubleBuffered = true;

        _lblTitle = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 14, 320, 20),
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(140, 146, 155),
            AutoEllipsis = true
        };

        _lblTime = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(16, 36, 330, 64),
            Font = new Font("Consolas", 33f, FontStyle.Bold),
            ForeColor = Color.FromArgb(232, 234, 237),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "00:00"
        };

        _lblMeta = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 104, 324, 20),
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(120, 126, 136)
        };

        _lblMsg = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 130, 324, 60),
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(168, 176, 186)
        };

        _lblHint = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 200, 324, 18),
            Font = new Font("Microsoft YaHei UI", 7.5f),
            ForeColor = Color.FromArgb(92, 98, 108),
            Text = HintIdle
        };

        _txtTitle = new TitleBox
        {
            Bounds = new Rectangle(18, 46, 324, 30),
            Font = new Font("Microsoft YaHei UI", 13f),
            BackColor = Color.FromArgb(30, 33, 38),
            ForeColor = Color.FromArgb(232, 234, 237),
            BorderStyle = BorderStyle.FixedSingle,
            Visible = false
        };
        _txtTitle.KeyDown += TxtTitleOnKeyDown;
        _txtTitle.CycleMode += CycleWorkMode;

        _lblAsk = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 82, 324, 18),
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(120, 126, 136),
            Text = "这件事在哪做？（Tab 切换）",
            Visible = false
        };

        _optPc = NewModeOption(18, 102, 0);
        _optOffWindow = NewModeOption(18, 124, 1);
        _optOffFull = NewModeOption(18, 146, 2);

        _lblModeNote = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 172, 324, 20),
            Font = new Font("Microsoft YaHei UI", 8f),
            ForeColor = Color.FromArgb(96, 102, 112),
            AutoEllipsis = true,
            Visible = false
        };

        _pnlStats = new Panel
        {
            Bounds = new Rectangle(0, 0, 360, 228),
            BackColor = Color.FromArgb(18, 20, 23),
            Visible = false
        };

        _lblStatHead = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 12, 324, 18),
            Font = new Font("Microsoft YaHei UI", 7.5f),
            ForeColor = Color.FromArgb(100, 106, 116),
            Text = "统计 · 近 7 天"
        };

        _lblStatMain = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(16, 30, 324, 38),
            Font = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold),
            ForeColor = Color.FromArgb(126, 214, 160)
        };

        _lblStatSub = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 72, 324, 20),
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(140, 146, 155)
        };

        _pnlChart = new Panel
        {
            Bounds = new Rectangle(18, 98, 324, 80),
            BackColor = Color.FromArgb(18, 20, 23)
        };
        _pnlChart.Paint += (_, e) => DrawChart(e.Graphics);

        _lblStatHint = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(18, 200, 324, 18),
            Font = new Font("Microsoft YaHei UI", 7.5f),
            ForeColor = Color.FromArgb(92, 98, 108),
            Text = "Ctrl+E 导出 CSV    5 返回"
        };

        _pnlStats.Controls.AddRange(new Control[] { _lblStatHead, _lblStatMain, _lblStatSub, _pnlChart, _lblStatHint });

        // 全屏形态：一张只放「标题 / 大字时间 / 一句话」的底板
        _pnlFull = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(11, 12, 14),
            Visible = false
        };

        _fsTitle = new Label
        {
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 22f),
            ForeColor = Color.FromArgb(150, 157, 167),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true
        };

        _fsTime = new Label
        {
            AutoSize = false,
            Font = new Font("Consolas", 140f, FontStyle.Bold),
            ForeColor = Color.FromArgb(126, 214, 160),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "00:00"
        };

        _fsMsg = new Label
        {
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 26f),
            ForeColor = Color.FromArgb(168, 176, 186),
            TextAlign = ContentAlignment.MiddleCenter
        };

        _fsHint = new Label
        {
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 10f),
            ForeColor = Color.FromArgb(72, 78, 88),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "2 结束    3 走神    4 休息    Esc 回小窗"
        };

        _pnlFull.Controls.AddRange(new Control[] { _fsTitle, _fsTime, _fsMsg, _fsHint });
        _pnlFull.Paint += (_, e) => PaintFullBar(e.Graphics);

        Controls.AddRange(new Control[]
        {
            _lblTitle, _lblTime, _lblMeta, _lblMsg, _lblHint,
            _lblAsk, _optPc, _optOffWindow, _optOffFull, _lblModeNote,
            _txtTitle, _pnlStats, _pnlFull
        });

        foreach (Control c in new Control[] { this, _lblTime, _lblTitle, _lblMeta, _lblMsg, _lblHint })
            c.MouseDown += DragWindowOnMouseDown;

        var menu = new ContextMenuStrip();
        menu.Items.Add("导出 CSV（全部记录）", null, (_, _) => ExportCsv());
        menu.Items.Add("导出今天（JSON）", null, (_, _) => ExportToday());
        menu.Items.Add("导入事件文件…", null, (_, _) => ImportFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("休息音乐 开/关（M）", null, (_, _) => ToggleMusic());
        menu.Items.Add("自动休息 开/关（到点直接进休息 / 按 4 确认）", null, (_, _) => ToggleAutoBreak());
        menu.Items.Add("上传我自己的音乐…", null, (_, _) => UploadMusic());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开数据文件夹", null, (_, _) => OpenDataFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        ContextMenuStrip = menu;

        ResumeLayout(false);
    }

    /// <summary>
    /// 三个载体选项做成整行可点的文字，不做成小按钮：
    /// 360px 宽放三个按钮会把文字挤到看不清，而这一行本来就要用键盘选。
    /// </summary>
    private Label NewModeOption(int x, int y, int index)
    {
        var lbl = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(x, y, 324, 20),
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(120, 126, 136),
            Text = "   " + ModeTexts[index],
            AutoEllipsis = true,
            Cursor = Cursors.Hand,
            Tag = index
        };
        lbl.Click += (_, _) => SetWorkMode(index);
        return lbl;
    }

    /// <summary>0 = 电脑上小窗，1 = 不在电脑上小窗，2 = 不在电脑上全屏。</summary>
    private int ModeIndex => _pendingMode == WorkMode.OnPc ? 0 : (_pendingFullscreen ? 2 : 1);

    private void CycleWorkMode() => SetWorkMode((ModeIndex + 1) % 3);

    private void SetWorkMode(int index)
    {
        _pendingMode = index == 0 ? WorkMode.OnPc : WorkMode.OffPc;
        _pendingFullscreen = index == 2;
        RefreshModeOptions();
    }

    private static readonly string[] ModeTexts =
    {
        "在这台电脑上 —— 小窗",
        "不在电脑上 —— 小窗",
        "不在电脑上 —— 全屏"
    };

    private static readonly string[] ModeNotes =
    {
        "小窗留在角落，你照常做事。",
        "小窗在角落；不碰电脑也不算离开。",
        "铺满屏幕，远处能看清，挡住别处。"
    };

    private void RefreshModeOptions()
    {
        var idx = ModeIndex;
        foreach (var (lbl, i) in new[] { (_optPc, 0), (_optOffWindow, 1), (_optOffFull, 2) })
        {
            var on = i == idx;
            lbl.Text = (on ? "▸ " : "   ") + ModeTexts[i];
            lbl.ForeColor = on ? Color.FromArgb(126, 214, 160) : Color.FromArgb(110, 116, 126);
        }
        _lblModeNote.Text = ModeNotes[idx];
    }

    private void ApplyWindowPos()
    {
        if (_settings.WindowX >= 0 && _settings.WindowY >= 0 &&
            Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(_settings.WindowX, _settings.WindowY))))
        {
            Location = new Point(_settings.WindowX, _settings.WindowY);
        }
        else
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Right - Width - 32, wa.Top + 32);
        }
    }

    // ---------------- 输入 ----------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_state.Phase == Phase.TitleInput)
        {
            base.OnKeyDown(e);
            return;
        }

        // 有人按键就说明人还在，撤掉「到点自动收工」
        CancelAutoStop();

        if (e.Control && e.KeyCode == Keys.E)
        {
            ExportCsv();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        var key = e.KeyCode;
        if (key is Keys.D1 or Keys.NumPad1) { OnKey1(); e.Handled = true; }
        else if (key is Keys.D2 or Keys.NumPad2) { OnKey2(); e.Handled = true; }
        else if (key is Keys.D3 or Keys.NumPad3) { OnKey3(); e.Handled = true; }
        else if (key is Keys.D4 or Keys.NumPad4) { OnKey4(); e.Handled = true; }
        else if (key is Keys.D5 or Keys.NumPad5) { ToggleStats(); e.Handled = true; }
        else if (key == Keys.M) { ToggleMusic(); e.Handled = true; }
        else if (key == Keys.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (key == Keys.Up) { CycleLevel(1); e.Handled = true; }
        else if (key == Keys.Down) { CycleLevel(-1); e.Handled = true; }
        else if (key == Keys.Left) { ShiftGoal(-5); e.Handled = true; }
        else if (key == Keys.Right) { ShiftGoal(5); e.Handled = true; }
        else if (key == Keys.Escape && _fullscreenActive) { _fullscreen = false; RefreshUi(); e.Handled = true; }
        else if (key == Keys.Escape && _statsWanted) { ToggleStats(); e.Handled = true; }

        base.OnKeyDown(e);
    }

    private void OnKey1()
    {
        switch (_state.Phase)
        {
            case Phase.Idle:
                _messageUntil = DateTime.MinValue;
                _state.OpenTitleInput();
                _txtTitle.Text = _settings.LastTitle;
                SetStatsVisible(false);
                RefreshUi();
                _txtTitle.Focus();
                _txtTitle.SelectAll();
                break;
            case Phase.Break:
                MusicPlayer.StopTrack();
                _unclosedShown = false;   // 新一段的超时提醒从头算
                _autoStopAt = null;
                _state.EndBreak();
                ShowMessage("回来了。音乐停了，这一段从头计时。", 3);
                break;
            case Phase.Focusing:
                ShowMessage("已经在计时了。", 2);
                break;
        }
        RefreshUi();
    }

    private void OnKey2()
    {
        if (_state.Phase is not (Phase.Focusing or Phase.Break)) return;

        var result = _state.Stop("stop");
        if (result is null) return;

        MusicPlayer.StopTrack();   // 休息中按 2 收工，音乐必须跟着停
        _autoStopAt = null;
        _unclosedShown = false;
        _fullscreen = false;
        LoadToday();
        _statsDirty = true;
        ApplyRecommendation(announce: false);   // 收工后重算：下一段该练多久
        var p = _phrases.Next("stop");
        ShowMessage(Fill(p), 7);
        RefreshUi();
    }

    private void OnKey3()
    {
        if (!_state.Distract()) return;

        _todayDistracts++;
        _statsDirty = true;
        var p = _phrases.Next("distract");
        ShowMessage(Fill(p), 4);
        RefreshUi();
    }

    private void OnKey4()
    {
        if (_state.Phase == Phase.Focusing)
        {
            _state.RestMinutes = ResolveRest(_settings);
            _state.StartBreak();
            var src = StartBreakMusic();
            ShowMessage($"休息 {_state.RestMinutes} 分钟，{src}（M 关音乐）。回来按 1。", 5);
        }
        else if (_state.Phase == Phase.Break)
        {
            MusicPlayer.StopTrack();
            _unclosedShown = false;   // 新一段的超时提醒从头算
            _autoStopAt = null;
            _state.EndBreak();
            ShowMessage("接上了。音乐停了，这一段从头计时。", 4);
        }
        RefreshUi();
    }

    /// <summary>
    /// 进休息放音乐：曲库（用户优先、随机挑曲）有货就放曲目；
    /// 一首放不出来就换下一首再试（最多把曲库试一遍），全失败就静音休息。
    /// 音乐被关掉就什么都不放。返回用于提示的来源描述。
    /// </summary>
    private string StartBreakMusic()
    {
        if (!_musicOn) return "音乐已关";
        for (var i = 0; i < _music.EffectiveTracks.Count; i++)
        {
            var track = _music.PickNext();
            if (track is not null && MusicPlayer.PlayTrack(track)) return "轻音乐放着";
        }
        MusicPlayer.StopTrack();   // 一首都放不出来：静音，不再有合成雨声
        return "曲子没放出来，静音休息";
    }

    private void ShiftGoal(int delta)
    {
        if (_state.Phase != Phase.Idle) return;
        SetGoal(_state.GoalMinutes + delta);
    }

    /// <summary>↑/↓ 直接换等级，同时跳到该档默认时长。←/→ 仍可微调分钟。</summary>
    private void CycleLevel(int dir)
    {
        if (_state.Phase != Phase.Idle) return;
        var cur = Array.FindIndex(LevelPresets, l => l.Name == LevelFor(_state.GoalMinutes));
        if (cur < 0) cur = 0;
        var next = LevelPresets[(cur + dir + LevelPresets.Length) % LevelPresets.Length];
        SetGoal(next.DefaultGoal);
        ShowMessage($"等级：{next.Name}（默认 {next.DefaultGoal} 分钟）。← → 可微调。", 3);
    }

    private void SetGoal(int minutes)
    {
        var g = Math.Clamp(minutes, 5, 120);
        _state.GoalMinutes = g;
        _settings.GoalMinutes = g;
        _settings.Level = LevelFor(g);
        _state.Level = _settings.Level;
        Settings.Save(_settingsPath, _settings);
        RefreshUi();
    }

    private static readonly (string Name, int DefaultGoal)[] LevelPresets =
    {
        ("初级", 15),
        ("中级", 25),
        ("高级", 45),
    };

    /// <summary>待机时的「今日一句」：一天取一条，来源可查证。</summary>
    private string PickQuote()
    {
        var p = _phrases.Next("recommend");
        if (p.Text.Length == 0) return "";
        return string.IsNullOrEmpty(p.Source) || p.Source == "自写"
            ? p.Text
            : $"「{p.Text}」 ——{p.Source}";
    }

    /// <summary>
    /// 休息时长：settings 里给了正数就用用户的；0 表示按等级自动——
    /// 短专注配长休息只会让人走神，所以 初级 3 / 中级 5 / 高级 10。
    /// </summary>
    private int ResolveRest(SettingsData s) =>
        s.RestMinutes > 0 ? s.RestMinutes : AppState.RestForLevel(s.Level);

    /// <summary>休息音乐的开关。休息中实时生效；开关状态写进 settings，重启保留。</summary>
    private void ToggleMusic()
    {
        _musicOn = !_musicOn;
        _settings.MusicOn = _musicOn;
        Settings.Save(_settingsPath, _settings);

        if (_musicOn && _state.Phase == Phase.Break) StartBreakMusic();
        else if (!_musicOn) MusicPlayer.StopTrack();

        ShowMessage(_musicOn ? "休息音乐：开（仅休息时播放）。" : "休息音乐：关。", 3);
        RefreshUi();
    }

    /// <summary>到点是否自动进休息。开 = 音乐直接响起；关 = 只提醒，按 4 才进。写进 settings，重启保留。</summary>
    private void ToggleAutoBreak()
    {
        _settings.AutoBreak = !_settings.AutoBreak;
        Settings.Save(_settingsPath, _settings);
        ShowMessage(_settings.AutoBreak
            ? "自动休息：开。到目标分钟直接进休息、放音乐。"
            : "自动休息：关。到目标分钟只提醒，按 4 才进休息。", 4);
        RefreshUi();
    }

    /// <summary>
    /// 弹文件选择框让用户挑自己的音乐，复制进 data\music（复制而非引用：
    /// 整个文件夹拷走就是搬家，曲子要跟着记录走）。用户目录一旦有文件，
    /// 曲库整体切到用户曲子，自带的不再播放。
    /// </summary>
    private void UploadMusic()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择休息时播放的音乐（可按住 Ctrl 多选）",
            Multiselect = true,
            Filter = "音频文件 (*.mp3;*.wav;*.m4a;*.flac;*.wma)|*.mp3;*.wav;*.m4a;*.flac;*.wma|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_userMusicDir) ? _userMusicDir : ""
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Directory.CreateDirectory(_userMusicDir);
            foreach (var src in dlg.FileNames)
                File.Copy(src, Path.Combine(_userMusicDir, Path.GetFileName(src)), overwrite: true);
            ShowMessage($"已放入 {dlg.FileNames.Length} 个文件。你的曲子优先生效，本段音乐播完就会换你的。", 6);
        }
        catch (Exception ex)
        {
            ShowMessage($"放入失败：{ex.Message}", 6);
        }
        RefreshUi();
    }

    /// <summary>
    /// 按最近表现推荐并设置目标时长。
    /// announce=true（开机）：弹消息说明；false（收工后）：存成待机说明，回到待机再讲。
    /// </summary>
    private void ApplyRecommendation(bool announce)
    {
        if (_state.Phase != Phase.Idle) return;

        var rec = Stats.Recommend(Stats.Sessions(_store.ReadAll(out _)), _state.GoalMinutes);

        if (rec.GoalMinutes != _state.GoalMinutes)
        {
            SetGoal(rec.GoalMinutes);
            var why = $"建议 {rec.GoalMinutes} 分钟：{rec.Reason}。←→ 可改。";
            if (announce) ShowMessage(why, 10);
            else _pendingNotice = why;
        }
        else if (announce && rec.Reason.Length > 0)
        {
            ShowMessage($"今天 {rec.GoalMinutes} 分钟。{rec.Reason}。", 8);
        }
    }

    private static string LevelFor(int goal) => goal switch
    {
        <= 20 => "初级",
        <= 35 => "中级",
        _ => "高级"
    };

    private void TxtTitleOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            var text = _txtTitle.Text.Trim();
            _settings.LastTitle = text;
            _settings.LastWorkMode = _pendingMode.Key();
            _settings.LastFullscreen = _pendingFullscreen;
            Settings.Save(_settingsPath, _settings);

            _state.Start(text, _state.GoalMinutes, LevelFor(_state.GoalMinutes),
                         _pendingMode, _pendingFullscreen);
            _fullscreen = _pendingFullscreen;
            _idlePrompted = false;
            _unclosedShown = false;
            _autoStopAt = null;
            _statsDirty = true;
            _pendingNotice = "";

            ShowMessage(_pendingMode == WorkMode.OffPc
                ? "开始。不碰电脑也不算离开，按 2 收工。"
                : "开始。走神了按 3，不用停表。", 4);
            RefreshUi();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            _state.CancelTitleInput();
            RefreshUi();
        }
    }

    // ---------------- 每帧 ----------------

    private void OnTick()
    {
        RolloverIfNewDay();
        var r = _state.Tick();

        if (r.Suspended && _state.Current is not null)
        {
            MusicPlayer.StopTrack();   // 休眠发生在休息中：醒来音乐不能再响
            _state.Stop("suspend", GoalCap());
            _fullscreen = false;
            LoadToday();
            _pendingNotice = "检测到休眠，上一段已结束。";
            ShowMessage("检测到系统休眠或时间被改动，这一段已自动结束。", 8);
        }

        if (r.GoalReached)
        {
            // 到点提醒要「直接明显」：响一声。自动模式直接进休息；手动模式把该不该休息、怎么休息一次说清
            MusicPlayer.Chime();
            if (_settings.AutoBreak)
            {
                _state.RestMinutes = ResolveRest(_settings);
                _state.StartBreak();
                var src = StartBreakMusic();
                ShowMessage($"到 {_state.GoalMinutes} 分钟了，休息 {_state.RestMinutes} 分钟，{src}（M 关）。想接着练就按 1。", 10);
            }
            else
            {
                ShowMessage($"到 {_state.GoalMinutes} 分钟了。按 4 休息 {ResolveRest(_settings)} 分钟；不歇也行，按任意键继续。", 10);
            }
        }

        if (r.BreakOver)
        {
            // 休息倒计时走完 → 缓冲期：音乐继续放，把回来要按的键和超时规则交代清楚
            MusicPlayer.Chime();
            ShowMessage($"休息超时了。{_state.BreakGraceMinutes} 分钟内按 1 回来继续；还没回来就按 {_state.GoalMinutes} 分钟收工。", 10);
        }

        if (r.BreakIdle && _state.Current is not null)
        {
            MusicPlayer.StopTrack();
            _state.Stop("break_idle", GoalCap());
            _fullscreen = false;
            LoadToday();
            _statsDirty = true;
            _pendingNotice = "休息超时没回来，已自动收工。想接着练按 1。";
            ShowMessage(_pendingNotice, 8);
        }

        AdvanceTrackIfEnded();
        CheckIdle();
        CheckUnclosed();
        CheckNudge();
        RefreshUi();
    }

    /// <summary>休息中曲目播完了：换下一首（随机不重复）。放不出来就换着试，全失败静音。</summary>
    private void AdvanceTrackIfEnded()
    {
        if (_state.Phase != Phase.Break || !_musicOn) return;
        if (!MusicPlayer.TrackOpen || MusicPlayer.IsTrackPlaying()) return;
        for (var i = 0; i < _music.EffectiveTracks.Count; i++)
        {
            var next = _music.PickNext();
            if (next is not null && MusicPlayer.PlayTrack(next)) return;
        }
        MusicPlayer.StopTrack();   // 换遍曲库都放不出来就安静
    }

    /// <summary>
    /// 总专注已达目标时，自动收工把时长截到目标分钟。
    /// 按总时长判断而不是按段：缓冲期自动收工发生在 Break 阶段，此时 GoalReached 已不参考。
    /// </summary>
    private TimeSpan? GoalCap() =>
        _state.Current is not null && _state.FocusElapsed >= TimeSpan.FromMinutes(_state.GoalMinutes)
            ? TimeSpan.FromMinutes(_state.GoalMinutes)
            : null;

    /// <summary>跨零点时刷新「今日」口径，否则第二天第一条记录会算到昨天头上。</summary>
    private void RolloverIfNewDay()
    {
        if (_loadedDay == DateTime.Today) return;
        LoadToday();
        _dailyQuote = PickQuote();
        _messageUntil = DateTime.MinValue;
    }

    private void CheckIdle()
    {
        if (_state.Phase != Phase.Focusing)
        {
            _idlePrompted = false;
            return;
        }

        // 不在电脑上做的事（看书、写字），人不碰键鼠本来就是常态。
        // 这里如果照旧判「离开」，非电脑类的专注会被整段误杀，比不检测更糟。
        if (_state.Current?.Mode == WorkMode.OffPc)
        {
            _idlePrompted = false;
            return;
        }

        var idle = _idle.IdleTime;

        if (idle >= TimeSpan.FromMinutes(6))
        {
            var capped = _state.GoalReached;
            var goal = _state.GoalMinutes;
            _state.Stop("idle", GoalCap());
            _fullscreen = false;
            LoadToday();
            _idlePrompted = false;
            CancelAutoStop();
            _pendingNotice = capped
                ? $"上一段按目标 {goal} 分钟收工了。"
                : "上一段按『离开』结束了，没算进专注。";
            ShowMessage(capped
                ? $"超过 6 分钟没有键鼠输入，这一段按『离开』结束，记到目标 {goal} 分钟。"
                : "超过 6 分钟没有键鼠输入，这一段按『离开』结束，没算进专注。", 8);
            return;
        }

        if (idle >= TimeSpan.FromMinutes(5) && !_idlePrompted)
        {
            _idlePrompted = true;
            ShowMessage("还在吗？按任意键继续，否则 1 分钟后按离开处理。", 60);
        }
        else if (_idlePrompted && idle < TimeSpan.FromSeconds(10))
        {
            _idlePrompted = false;
            _messageUntil = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 到点没按结束：先给一句提醒（说明规则），提醒后设定的分钟内无人按键，
    /// 就按目标时长收工。时长必须截到目标——否则宽限期那几分钟会虚增专注。
    /// </summary>
    private void CheckUnclosed()
    {
        if (_demoMode) return;
        if (_state.Phase != Phase.Focusing || !_state.GoalReached)
        {
            _autoStopAt = null;
            return;
        }

        if (_autoStopAt.HasValue)
        {
            if (DateTime.Now < _autoStopAt.Value) return;

            _autoStopAt = null;
            var result = _state.Stop("auto", TimeSpan.FromMinutes(_state.GoalMinutes));
            if (result is null) return;

            _fullscreen = false;
            LoadToday();
            _pendingNotice = $"已按 {_state.GoalMinutes} 分钟收工。要继续按 1。";
            ShowMessage(Fill(_phrases.Next("auto")), 10);
            RefreshUi();
            return;
        }

        if (_unclosedShown) return;

        // 超出时长按「段」算：休息回来开新一段，超时计时从头开始
        var over = _state.SegmentElapsed - TimeSpan.FromMinutes(_state.GoalMinutes);
        if (over.TotalMinutes < Math.Max(0, _settings.AutoStopGraceMinutes)) return;

        _unclosedShown = true;
        _autoStopAt = DateTime.Now.AddMinutes(Math.Max(1, _settings.AutoStopMinutes));
        ShowMessage(Fill(_phrases.Next("unclosed")), 10);
    }

    /// <summary>取消自动收工：人还在，就别替他做决定。</summary>
    private void CancelAutoStop()
    {
        if (!_autoStopAt.HasValue) return;
        _autoStopAt = null;
        _messageUntil = DateTime.MinValue;
    }

    /// <summary>
    /// 今天一条记录都没有时，隔一段时间轻推一次。
    /// 每天最多 3 次、间隔 ≥2 小时，且不打断正在显示的消息——提醒不该变成催债。
    /// </summary>
    private void CheckNudge()
    {
        if (_demoMode) return;
        if (_state.Phase != Phase.Idle || _todaySessions > 0) return;

        var now = DateTime.Now;
        if (now < _messageUntil) return;
        if (now - _lastNudgeAt < TimeSpan.FromHours(2)) return;

        var today = now.ToString("yyyy-MM-dd");
        if (_settings.NudgeDate != today)
        {
            _settings.NudgeDate = today;
            _settings.NudgeCount = 0;
        }
        if (_settings.NudgeCount >= 3) return;

        _settings.NudgeCount++;
        _lastNudgeAt = now;
        _settings.LastNudgeAt = now.ToString("o");
        Settings.Save(_settingsPath, _settings);

        ShowMessage(Fill(_phrases.Next("nudge")), 12);
    }

    private void RefreshUi()
    {
        var phase = _state.Phase;

        ApplyDisplay();

        var off = _state.Current?.Mode == WorkMode.OffPc;
        _lblTitle.Text = phase switch
        {
            Phase.Focusing or Phase.Break => (off ? "离线 · " : "") + _state.Title,
            Phase.TitleInput => "现在要做什么？",
            _ => "待机"
        };

        _lblTime.Text = phase switch
        {
            Phase.Focusing => Fmt(_state.FocusElapsed),
            Phase.Break => _state.InBreakGrace
                ? $"超时 {Fmt(_state.GraceRemaining)}"
                : Fmt(_state.BreakRemaining),
            _ => "00:00"
        };

        _lblTime.ForeColor = phase switch
        {
            Phase.Focusing => Color.FromArgb(126, 214, 160),
            Phase.Break => _state.InBreakGrace
                ? Color.FromArgb(224, 110, 110)
                : Color.FromArgb(230, 190, 110),
            _ => Color.FromArgb(120, 126, 136)
        };

        var goal = _state.GoalMinutes;
        _lblMeta.Text = $"{LevelFor(goal)} · 目标 {goal} 分 · 今日 {_todayFocusSec / 60:0} 分 · 走神 {_todayDistracts}";

        _lblHint.Text = phase switch
        {
            Phase.Focusing => _fullscreenActive ? "2 结束  3 走神  4 休息  Esc 回小窗" : HintFocus,
            Phase.Break => HintBreak,
            Phase.TitleInput => "Tab 换地方 · Enter 开始 · Esc 取消",
            _ => HintIdle
        };

        if (phase == Phase.TitleInput) RefreshModeOptions();

        _lblMsg.Text = ComposeMessage(phase);

        if (_fullscreenActive) RefreshFullscreen();
        if (_pnlStats.Visible) RenderStats();
    }

    /// <summary>小窗和全屏共用同一段提示逻辑，避免两处文案走偏。</summary>
    private string ComposeMessage(Phase phase)
    {
        if (DateTime.Now < _messageUntil && _message.Length > 0) return _message;

        if (_autoStopAt.HasValue)
        {
            var left = _autoStopAt.Value - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"到 {_state.GoalMinutes} 分钟了。按 2 收工，或按任意键继续；{(int)left.TotalMinutes}:{left.Seconds:00} 后按目标时长收工。";
        }

        if (phase == Phase.Focusing && _state.GoalReached) return "已达标，继续算超额。按 2 收工。";

        if (phase == Phase.Break)
            return _state.InBreakGrace
                ? $"休息超时，{Fmt(_state.GraceRemaining)} 内按 1 回来；超时按 {_state.GoalMinutes} 分钟自动收工。"
                : $"休息中，{Fmt(_state.BreakRemaining)} 后进缓冲。按 1 回来，M 切音乐。";

        if (phase == Phase.Idle)
        {
            if (_pendingNotice.Length > 0) return _pendingNotice;
            return _dailyQuote.Length > 0 ? _dailyQuote : "按 1 开始。↑↓ 换等级，←→ 调分钟。";
        }

        return "";
    }

    private static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    // ---------------- 文案 ----------------

    private void ShowMessage(string text, int seconds)
    {
        _message = text;
        _messageUntil = DateTime.Now.AddSeconds(seconds);
    }

    private string Fill(Phrase p)
    {
        var min = (int)Math.Round(_state.FocusElapsed.TotalMinutes);
        if (_state.Current is null && _state.Phase == Phase.Idle) min = (int)Math.Round(_todayFocusSec / 60);

        return p.Text
            .Replace("{min}", min.ToString())
            .Replace("{n}", _todayDistracts.ToString())
            .Replace("{goal}", _state.GoalMinutes.ToString())
            .Replace("{over}", Math.Max(0, min - _state.GoalMinutes).ToString())
            .Replace("{today}", ((int)Math.Round(_todayFocusSec / 60)).ToString())
            .Replace("{wait}", Math.Max(1, _settings.AutoStopMinutes).ToString());
    }

    // ---------------- 数据 ----------------

    private void LoadToday()
    {
        var events = _store.ReadAll(out _badLines);
        var all = Stats.Sessions(events);
        var today = DateTime.Today;
        _loadedDay = today;

        _todayFocusSec = all.Where(s => s.Date == today).Sum(s => s.FocusSeconds);
        _todayDistracts = all.Where(s => s.Date == today).Sum(s => s.Distractions);
        _todaySessions = all.Count(s => s.Date == today);
        _statsDirty = true;
    }

    /// <summary>仅开发期渲染截图用。</summary>
    public void ShowStatsForShot() => SetStatsVisible(true);

    /// <summary>仅开发期渲染截图用：把某类提示摆到界面上。</summary>
    public string MessageTextForDebug => _lblMsg.Text;

    /// <summary>仅开发期：不依赖 200ms 定时器，立刻按当前状态重画一次。</summary>
    public void RefreshForShot() => RefreshUi();

    public void ShowDemoForShot(string kind)
    {
        _demoMode = true;
        if (kind == "nudge") ShowMessage(Fill(_phrases.Next("nudge")), 300);
        else if (kind == "unclosed") ShowMessage(Fill(_phrases.Next("unclosed")), 300);
        else if (kind == "countdown") _autoStopAt = DateTime.Now.AddMinutes(4).AddSeconds(37);
    }

    /// <summary>仅开发期：摆出标题输入 + 载体选择的界面。</summary>
    public void ShowTitleInputForShot()
    {
        _demoMode = true;
        _state.OpenTitleInput();
        _txtTitle.Text = "看《心流》第三章";
        SetWorkMode(2);
        RefreshUi();
    }

    /// <summary>仅开发期：按给定的画布尺寸渲染全屏形态（真正全屏会按屏幕物理尺寸铺满，截不下）。</summary>
    public void ShowFullscreenForShot(int w, int h)
    {
        _demoMode = true;
        _state.Start("看《心流》第三章", 25, "中级", WorkMode.OffPc, true);
        _state.Distract();
        ShowMessage(Fill(_phrases.Next("distract")), 600);
        _fullscreen = true;
        ApplyDisplay();
        Bounds = new Rectangle(0, 0, w, h);
        _fsLaidOutFor = Size.Empty;
        LayoutFullscreen();
        RefreshUi();
    }

    private void ToggleStats() => SetStatsVisible(!_statsWanted);

    private void SetStatsVisible(bool visible)
    {
        _statsWanted = visible;
        RefreshUi();
    }

    /// <summary>
    /// 主界面 / 统计页 / 全屏 三者互斥显示。WinForms 的控件叠层不可靠（AddRange 的 z-order 是反的），
    /// 所以一律用显隐切换，不靠 BringToFront 抢位置。
    /// </summary>
    private void ApplyDisplay()
    {
        var phase = _state.Phase;
        var wantFull = _fullscreen && phase is Phase.Focusing or Phase.Break;
        var titleInput = phase == Phase.TitleInput;

        if (wantFull)
        {
            if (!_fullscreenActive)
            {
                _windowedBounds = Bounds;
                _fullscreenActive = true;
            }
            _statsWanted = false;

            var scr = Screen.FromRectangle(_windowedBounds).Bounds;
            if (Bounds != scr) Bounds = scr;
        }
        else if (_fullscreenActive)
        {
            _fullscreenActive = false;
            if (!_windowedBounds.IsEmpty) Bounds = _windowedBounds;
        }

        var showStats = !wantFull && _statsWanted;
        var showMain = !wantFull && !showStats;

        _lblTitle.Visible = showMain;
        _lblHint.Visible = showMain;
        _lblTime.Visible = showMain && !titleInput;
        _lblMeta.Visible = showMain && !titleInput;
        _lblMsg.Visible = showMain && !titleInput;

        _lblAsk.Visible = showMain && titleInput;
        _optPc.Visible = showMain && titleInput;
        _optOffWindow.Visible = showMain && titleInput;
        _optOffFull.Visible = showMain && titleInput;
        _lblModeNote.Visible = showMain && titleInput;
        _txtTitle.Visible = showMain && titleInput;
        if (showMain && titleInput) _txtTitle.BringToFront();

        _pnlStats.Visible = showStats;
        if (showStats) _statsDirty = true;

        _pnlFull.Visible = wantFull;
        if (wantFull)
        {
            _pnlFull.BringToFront();
            LayoutFullscreen();
        }
    }

    private void ToggleFullscreen()
    {
        if (_state.Phase is not (Phase.Focusing or Phase.Break))
        {
            ShowMessage("先开始一段，再按 F11 切全屏。", 3);
            return;
        }
        _fullscreen = !_fullscreen;
        RefreshUi();
    }

    /// <summary>
    /// 全屏排版按屏幕尺寸算，字号直接给像素单位（GraphicsUnit.Pixel），
    /// 这样不管系统 DPI 是 100% 还是 150%，占屏比例都一致。
    /// </summary>
    private void LayoutFullscreen()
    {
        var w = _pnlFull.ClientSize.Width;
        var h = _pnlFull.ClientSize.Height;
        if (w < 200 || h < 150) return;

        if (_fsLaidOutFor == new Size(w, h)) return;
        _fsLaidOutFor = new Size(w, h);

        ReplaceFont(_fsTitle, new Font("Microsoft YaHei UI", h * 0.032f, FontStyle.Regular, GraphicsUnit.Pixel));
        ReplaceFont(_fsTime, new Font("Consolas", h * 0.26f, FontStyle.Bold, GraphicsUnit.Pixel));
        ReplaceFont(_fsMsg, new Font("Microsoft YaHei UI", h * 0.030f, FontStyle.Regular, GraphicsUnit.Pixel));
        ReplaceFont(_fsHint, new Font("Microsoft YaHei UI", h * 0.016f, FontStyle.Regular, GraphicsUnit.Pixel));

        _fsTitle.Bounds = new Rectangle(0, (int)(h * 0.13f), w, (int)(h * 0.07f));
        _fsTime.Bounds = new Rectangle(0, (int)(h * 0.21f), w, (int)(h * 0.36f));
        _fsMsg.Bounds = new Rectangle((int)(w * 0.06f), (int)(h * 0.60f), (int)(w * 0.88f), (int)(h * 0.16f));
        _fsHint.Bounds = new Rectangle(0, (int)(h * 0.88f), w, (int)(h * 0.05f));
    }

    private static void ReplaceFont(Control c, Font next)
    {
        var old = c.Font;
        c.Font = next;
        if (!ReferenceEquals(old, next)) old?.Dispose();
    }

    private void RefreshFullscreen()
    {
        var phase = _state.Phase;
        var off = _state.Current?.Mode == WorkMode.OffPc;

        _fsTitle.Text = (off ? "离线 · " : "") + _state.Title;
        _fsTime.Text = phase == Phase.Break ? Fmt(_state.BreakRemaining) : Fmt(_state.FocusElapsed);
        _fsTime.ForeColor = phase == Phase.Break
            ? Color.FromArgb(230, 190, 110)
            : Color.FromArgb(126, 214, 160);
        _fsMsg.Text = ComposeMessage(phase);

        var h = _pnlFull.Height;
        _pnlFull.Invalidate(new Rectangle(0, Math.Max(0, h - 8), _pnlFull.Width, 8));
    }

    /// <summary>底部一条细进度：全屏时一眼看到「走到哪了」，不用读数字。</summary>
    private void PaintFullBar(Graphics g)
    {
        var w = _pnlFull.Width;
        var h = _pnlFull.Height;
        if (w <= 0 || h <= 8) return;

        var total = Math.Max(1, _state.GoalMinutes * 60.0);
        var frac = Math.Clamp(_state.FocusElapsed.TotalSeconds / total, 0, 1);

        using var bg = new SolidBrush(Color.FromArgb(28, 31, 36));
        g.FillRectangle(bg, 0, h - 6, w, 6);
        using var fg = new SolidBrush(Color.FromArgb(126, 214, 160));
        g.FillRectangle(fg, 0, h - 6, (int)(w * frac), 6);
    }

    private void RenderStats()
    {
        if (_statsDirty) RecomputeStats();

        _lblStatMain.Text = $"{_todayStat.FocusMinutes} 分钟 · {_todayStat.Sessions} 段";

        var gap = _medianGap > 0 ? $"{_medianGap / 60:0.0} 分" : "—";
        _lblStatSub.Text = $"走神 {_todayStat.Distractions} 次 · 达标 {_todayStat.Achieved} · 间隔 {gap}";

        _pnlChart.Invalidate();
    }

    private void RecomputeStats()
    {
        var events = _store.ReadAll(out _badLines);
        var all = Stats.Sessions(events);
        var today = DateTime.Today;

        _todayStat = Stats.Day(all, today);
        _medianGap = Stats.MedianDistractionGapSec(events, today);

        var days = Stats.LastDays(all, 7, today);
        _chart.Clear();
        const string wd = "日一二三四五六";
        for (var i = 0; i < days.Count; i++)
        {
            var d = today.AddDays(-(days.Count - 1 - i));
            _chart.Add((wd[(int)d.DayOfWeek].ToString(), days[i].FocusMinutes, i == days.Count - 1));
        }

        _statsDirty = false;
    }

    private void DrawChart(Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(18, 20, 23));

        if (_chart.Count == 0) return;

        var max = Math.Max(1, _chart.Max(c => c.Minutes));
        var slot = _pnlChart.Width / (float)_chart.Count;
        var baseY = _pnlChart.Height - 14f;
        const float topLimit = 14f;
        const float barW = 20f;

        using var penBase = new Pen(Color.FromArgb(38, 42, 48));
        g.DrawLine(penBase, 0, baseY, _pnlChart.Width, baseY);

        using var fontVal = new Font("Microsoft YaHei UI", 6.5f);
        using var fontLbl = new Font("Microsoft YaHei UI", 7f);

        for (var i = 0; i < _chart.Count; i++)
        {
            var (label, minutes, isToday) = _chart[i];
            var x = i * slot + (slot - barW) / 2f;

            if (minutes > 0)
            {
                var h = Math.Max(2f, (float)((baseY - topLimit) * (minutes / (double)max)));
                using var brush = new SolidBrush(isToday ? Color.FromArgb(126, 214, 160) : Color.FromArgb(58, 100, 78));
                g.FillRectangle(brush, x, baseY - h, barW, h);

                var txt = minutes.ToString();
                var size = g.MeasureString(txt, fontVal);
                using var vb = new SolidBrush(isToday ? Color.FromArgb(200, 230, 210) : Color.FromArgb(110, 150, 128));
                g.DrawString(txt, fontVal, vb, x + barW / 2 - size.Width / 2, baseY - h - size.Height + 1);
            }

            var ls = g.MeasureString(label, fontLbl);
            using var lb = new SolidBrush(isToday ? Color.FromArgb(200, 206, 214) : Color.FromArgb(96, 102, 112));
            g.DrawString(label, fontLbl, lb, x + barW / 2 - ls.Width / 2, baseY + 2);
        }
    }

    private void ExportCsv()
    {
        var all = Stats.Sessions(_store.ReadAll(out _));
        if (all.Count == 0)
        {
            ShowMessage("还没有记录。按 1 开始第一段。", 5);
            return;
        }

        try
        {
            Directory.CreateDirectory(_exportDir);
            var path = Path.Combine(_exportDir, $"focus-sessions-{DateTime.Now:yyyyMMdd-HHmm}.csv");
            File.WriteAllBytes(path, Stats.ToCsvBytes(all));
            ShowMessage($"已导出 {all.Count} 段到 {path}", 8);
        }
        catch (Exception ex)
        {
            ShowMessage("导出失败：" + ex.Message, 8);
        }
    }

    private void ExportToday()
    {
        try
        {
            Directory.CreateDirectory(_exportDir);
            var path = Path.Combine(_exportDir, $"focus-{DateTime.Now:yyyyMMdd-HHmm}.jsonl");
            _store.ExportTo(path);
            ShowMessage($"已导出到 {path}", 8);
        }
        catch (Exception ex)
        {
            ShowMessage("导出失败：" + ex.Message, 8);
        }
    }

    /// <summary>给不懂路径的人一个入口：右键就能看到记录文件在哪。</summary>
    private void OpenDataFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(_store.FilePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            ShowMessage("打不开文件夹：" + ex.Message, 6);
        }
    }

    private void ImportFile()    {
        using var dlg = new OpenFileDialog { Filter = "事件文件|*.jsonl;*.json|所有文件|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var bak = _store.FilePath + ".bak";
            if (File.Exists(_store.FilePath)) File.Copy(_store.FilePath, bak, true);

            var (added, skipped) = _store.ImportFrom(dlg.FileName);
            LoadToday();
            ShowMessage($"导入 {added} 条，跳过重复 {skipped} 条。", 8);
        }
        catch (Exception ex)
        {
            ShowMessage("导入失败：" + ex.Message, 8);
        }
    }

    // ---------------- 杂项 ----------------

    /// <summary>
    /// 锁屏 = 人离开。在「不在电脑上做事」的模式下这是唯一可靠的人不在信号，
    /// 因为那种模式没法靠键鼠空闲判断（不按键才是正常）。
    /// </summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() => HandleSessionSwitch(e.Reason)));
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁，忽略即可
        }
    }

    private void HandleSessionSwitch(SessionSwitchReason reason)
    {
        if (reason != SessionSwitchReason.SessionLock) return;
        if (_state.Phase is not (Phase.Focusing or Phase.Break)) return;

        var capped = _state.FocusElapsed >= TimeSpan.FromMinutes(_state.GoalMinutes);
        var goal = _state.GoalMinutes;

        MusicPlayer.StopTrack();   // 锁屏可能发生在休息中：走人音乐停
        _state.Stop("lock", GoalCap());
        _fullscreen = false;
        LoadToday();
        CancelAutoStop();

        _pendingNotice = capped
            ? $"锁屏前那一段按目标 {goal} 分钟记下了。要继续按 1。"
            : "锁屏前那一段结束了，记到锁屏那一刻。要继续按 1。";
        ShowMessage(_pendingNotice, 12);
        RefreshUi();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_fullscreenActive) LayoutFullscreen();
    }

    protected override void WndProc(ref Message m)
    {
        if (_hotkey is not null)
        {
            var key = _hotkey.HandleMessage(ref m);
            if (key > 0)
            {
                CancelAutoStop();
                switch (key)
                {
                    case 1: OnKey1(); break;
                    case 2: OnKey2(); break;
                    case 3: OnKey3(); break;
                    case 4: OnKey4(); break;
                }
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkey = new GlobalHotkey(Handle, _settings.GlobalHotkey);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_state.Current is not null && _state.Phase is Phase.Focusing or Phase.Break)
            _state.Stop("exit");
        MusicPlayer.StopTrack();

        // 全屏时的 Location 是屏幕左上角，存下去下次小窗会跑到角落
        var at = _fullscreenActive && !_windowedBounds.IsEmpty ? _windowedBounds.Location : Location;
        _settings.WindowX = at.X;
        _settings.WindowY = at.Y;
        Settings.Save(_settingsPath, _settings);
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hotkey?.Dispose();
        base.OnFormClosing(e);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private void DragWindowOnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }
}
