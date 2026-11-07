using FocusKeeper.Storage;
using FocusKeeper.UI;

namespace FocusKeeper;

internal static class Program
{
    private const string MutexName = "FocusKeeper.SingleInstance.v1";

    [STAThread]
    private static void Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Environment.GetEnvironmentVariable("FK_DATA_DIR") ?? PickDataDir(baseDir);
        Directory.CreateDirectory(dataDir);

        if (args.Contains("--selftest"))
        {
            Environment.Exit(SelfTest.Run(baseDir, dataDir) ? 0 : 1);
            return;
        }

        // 开发用：生成桌面快捷方式（打包期跑一次）
        if (args.Contains("--mkshortcut"))
        {
            var rest = args.SkipWhile(a => a != "--mkshortcut").Skip(1).ToArray();
            var exeArg = rest.ElementAtOrDefault(0) ?? Path.Combine(baseDir, "FocusKeeper.exe");
            var lnkArg = rest.ElementAtOrDefault(1)
                         ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "专注计时器.lnk");

            var err = ShortcutMaker.Create(exeArg, lnkArg, "专注计时器 - 按 1 开始，按 3 记走神");
            File.WriteAllText(Path.Combine(baseDir, "shortcut.log"),
                err == "" ? $"OK {lnkArg}" : $"FAIL {err}", new System.Text.UTF8Encoding(false));
            return;
        }

        // 开发用：生成多尺寸 ICO（只跑一次，产物提交给 csproj 的 ApplicationIcon）
        if (args.Contains("--makeicon"))
        {
            var iconPath = args.SkipWhile(a => a != "--makeicon").Skip(1).FirstOrDefault()
                           ?? Path.Combine(baseDir, "app.ico");
            IconMaker.Write(iconPath);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var settingsPath = Path.Combine(dataDir, "settings.json");
        var phrasesPath = Path.Combine(dataDir, "phrases.json");
        var eventsPath = Path.Combine(dataDir, "events.jsonl");

        var settings = Settings.Load(settingsPath);
        var phrases = Phrases.Load(phrasesPath);
        var store = new EventStore(eventsPath);
        var repaired = store.RepairDanglingSessions();

        // 开发用：把 CSV 直接导出到程序目录，方便核对格式
        if (args.Contains("--probe"))
        {
            var lines = new List<string>();
            foreach (var c in new[] { "nudge", "unclosed", "auto", "goal" })
            {
                var p = phrases.Next(c);
                lines.Add($"{c}: len={p.Text.Length} :: {p.Text}");
            }
            File.WriteAllText(Path.Combine(baseDir, "probe.txt"), string.Join('\n', lines),
                new System.Text.UTF8Encoding(false));
            return;
        }

        if (args.Contains("--csv"))
        {
            var csvBytes = Stats.ToCsvBytes(Stats.Sessions(store.ReadAll(out _)));
            File.WriteAllBytes(Path.Combine(baseDir, "dump.csv"), csvBytes);
            return;
        }

        var form = new MainForm(store, phrases, settings, settingsPath, Path.Combine(dataDir, "export"), repaired);

        // 开发用：把窗口渲染成 PNG 自检布局，不参与正常使用
        if (args.Contains("--shot"))
        {
            form.Show();
            if (args.Contains("--stats")) form.ShowStatsForShot();
            if (args.Contains("--title")) form.ShowTitleInputForShot();
            if (args.Contains("--full"))
            {
                var i = Array.IndexOf(args, "--full");
                var gw = int.TryParse(args.ElementAtOrDefault(i + 1), out var a) ? a : 1280;
                var gh = int.TryParse(args.ElementAtOrDefault(i + 2), out var b) ? b : 720;
                form.ShowFullscreenForShot(gw, gh);
            }
            foreach (var k in new[] { "nudge", "unclosed", "countdown" })
                if (args.Contains("--" + k)) form.ShowDemoForShot(k);
            Application.DoEvents();
            form.RefreshForShot();
            Application.DoEvents();

            var target = args.SkipWhile(a => a != "--shot").Skip(1).FirstOrDefault()
                         ?? Path.Combine(baseDir, "shot.png");

            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(target, System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllText(target + ".txt", form.MessageTextForDebug, new System.Text.UTF8Encoding(false));
            form.Close();
            return;
        }

        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("FocusKeeper 已经在运行了。", "FocusKeeper",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(form);
    }

    /// <summary>
    /// 优先把数据放在 exe 旁边（绿色版，拷走即带走记录）。
    /// 但放到 Program Files 这类目录时写不进去——WinExe 没有控制台，双击会表现为「没反应」，
    /// 所以退到 %LOCALAPPDATA%\FocusKeeper，至少能跑起来。
    /// </summary>
    private static string PickDataDir(string baseDir)
    {
        var preferred = Path.Combine(baseDir, "data");
        try
        {
            Directory.CreateDirectory(preferred);
            var probe = Path.Combine(preferred, ".write-test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return preferred;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusKeeper");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
