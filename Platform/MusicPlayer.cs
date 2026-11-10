using System.Media;
using System.Runtime.InteropServices;
using System.Text;

namespace FocusKeeper.Platform;

/// <summary>
/// 休息时的轻音乐与提示音。
/// 曲目优先用 MusicLibrary 给的 MP3 等（winmm/MCI 播放，Windows 自带解码，零依赖）；
/// 曲库为空时退回程序合成的棕噪雨声（不引入外部音频文件）：
/// 棕噪听起来像远处的雨，适合盖住环境噪音又不抢注意力；
/// 双音 chime 用于「该休息了」和「休息超时了」两种提醒，音量克制。
/// WAV 生成为纯函数（可自测），SoundPlayer 只负责播放。
/// </summary>
public static class MusicPlayer
{
    private const int SampleRate = 22050;
    private const string MciAlias = "fk_music";

    private static MemoryStream? _loopStream;
    private static SoundPlayer? _loopPlayer;
    private static MemoryStream? _chimeStream;
    private static SoundPlayer? _chimePlayer;
    private static readonly object Gate = new();

    public static bool Playing { get; private set; }

    /// <summary>当前是否在播 MCI 曲目（雨声除外）。供 Tick 轮询曲子播完自动换下一首。</summary>
    public static bool TrackOpen { get; private set; }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnBuffer, int returnLength, IntPtr callback);

    private static string Mci(string command)
    {
        var sb = new StringBuilder(64);
        mciSendString(command, sb, sb.Capacity, IntPtr.Zero);
        return sb.ToString().Trim();
    }

    /// <summary>播一首曲目文件（mp3/wav 等）。声卡或解码失败时返回 false，调用方落到雨声。</summary>
    public static bool PlayTrack(string path)
    {
        lock (Gate)
        {
            try
            {
                CloseTrack();
                var type = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                    ? "waveaudio"
                    : "mpegvideo";                       // mp3 / m4a / wma 走 mpegvideo
                if (Mci($"open \"{path}\" type {type} alias {MciAlias}") != "0") return false;
                Mci($"setaudio {MciAlias} volume to 600");   // 音量克制：休息音乐不该盖过门铃
                if (Mci($"play {MciAlias}") != "0") { CloseTrack(); return false; }
                TrackOpen = true;
                Playing = true;
                return true;
            }
            catch
            {
                TrackOpen = false;
                Playing = false;
                return false;
            }
        }
    }

    /// <summary>曲子是否还在播（MCI 状态）。没开过曲目返回 false。</summary>
    public static bool IsTrackPlaying()
    {
        lock (Gate)
        {
            if (!TrackOpen) return false;
            try { return Mci($"status {MciAlias} mode") == "playing"; }
            catch { return false; }
        }
    }

    private static void CloseTrack()
    {
        try { Mci($"close {MciAlias}"); } catch { }
        TrackOpen = false;
    }

    /// <summary>休息开始：循环播放合成雨声（曲库为空时的兜底）。生成一次，之后复用。</summary>
    public static void StartLoop()
    {
        lock (Gate)
        {
            CloseTrack();
            try
            {
                _loopStream ??= new MemoryStream(BuildWav(BrownNoiseSamples(12, SampleRate)));
                _loopPlayer ??= new SoundPlayer(_loopStream);
                _loopPlayer.PlayLooping();
                Playing = true;
            }
            catch
            {
                // 声卡不可用不能影响计时主流程
                Playing = false;
            }
        }
    }

    /// <summary>休息结束（人回来了 / 自动收工）：曲目和雨声一起停。</summary>
    public static void StopLoop()
    {
        lock (Gate)
        {
            try { _loopPlayer?.Stop(); } catch { }
            CloseTrack();
            Playing = false;
        }
    }

    /// <summary>提醒音：到点该休息了 / 休息超时了。与循环音乐叠加播放。</summary>
    public static void Chime()
    {
        lock (Gate)
        {
            try
            {
                _chimeStream ??= new MemoryStream(BuildWav(ChimeSamples(1.4, SampleRate)));
                _chimePlayer ??= new SoundPlayer(_chimeStream);
                _chimePlayer.Play();
            }
            catch
            {
                // 放不出声音就算了，文字提醒还在
            }
        }
    }

    // ---------- 合成 ----------

    /// <summary>棕噪（布朗噪声）：比白噪低沉，听感接近雨声。首尾各 1 秒渐隐，循环处不炸耳。</summary>
    public static double[] BrownNoiseSamples(int seconds, int sampleRate)
    {
        var n = seconds * sampleRate;
        var samples = new double[n];
        double brown = 0;
        var fade = sampleRate;               // 1 秒渐变
        var rng = new Random(20261102);      // 固定种子：每次听起来一样，不给用户「今天雨声不一样」的错觉

        for (var i = 0; i < n; i++)
        {
            var white = rng.NextDouble() * 2.0 - 1.0;
            brown = (brown + 0.02 * white) / 1.02;
            var v = brown * 3.5;             // 补偿泄漏带来的衰减
            if (v > 1.0) v = 1.0;
            if (v < -1.0) v = -1.0;

            if (i < fade) v *= (double)i / fade;               // 渐入
            if (i > n - fade) v *= (double)(n - i) / fade;     // 渐出

            samples[i] = v * 0.45;           // 整体音量克制：休息音乐不该盖过门铃
        }

        return samples;
    }

    /// <summary>
    /// 双音 chime：C5 + E5 两个正弦叠加，指数衰减。像轻敲了一下玻璃杯，
    /// 提示到位但不吓人——休息提醒最忌讳「闹钟感」。
    /// </summary>
    public static double[] ChimeSamples(double seconds, int sampleRate)
    {
        var n = (int)(seconds * sampleRate);
        var samples = new double[n];

        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            var env = Math.Exp(-3.5 * t);                        // 指数衰减
            var v = 0.55 * Math.Sin(2 * Math.PI * 523.25 * t)    // C5
                  + 0.40 * Math.Sin(2 * Math.PI * 659.25 * t)    // E5（大三度，协和）
                  + 0.15 * Math.Sin(2 * Math.PI * 1318.5 * t);   // 泛音，让声音「亮」一点
            samples[i] = v * env * 0.38;
        }

        return samples;
    }

    /// <summary>把浮点样本打包成 16-bit 单声道 PCM WAV（标准 RIFF 头）。</summary>
    public static byte[] BuildWav(double[] samples)
    {
        var dataLen = samples.Length * 2;
        var wav = new byte[44 + dataLen];

        // RIFF 头
        "RIFF"u8.CopyTo(wav.AsSpan(0));
        WriteInt(wav, 4, 36 + dataLen);
        "WAVE"u8.CopyTo(wav.AsSpan(8));

        // fmt 块：PCM / 单声道 / 22050Hz / 16-bit
        "fmt "u8.CopyTo(wav.AsSpan(12));
        WriteInt(wav, 16, 16);
        wav[20] = 1; wav[21] = 0;            // PCM
        wav[22] = 1; wav[23] = 0;            // mono
        WriteInt(wav, 24, SampleRate);
        WriteInt(wav, 28, SampleRate * 2);   // byte rate = rate × channels × 2
        wav[32] = 2; wav[33] = 0;            // block align
        wav[34] = 16; wav[35] = 0;           // bits per sample

        // data 块
        "data"u8.CopyTo(wav.AsSpan(36));
        WriteInt(wav, 40, dataLen);

        for (var i = 0; i < samples.Length; i++)
        {
            var s = (short)Math.Round(Math.Clamp(samples[i], -1.0, 1.0) * short.MaxValue);
            wav[44 + i * 2] = (byte)(s & 0xFF);
            wav[45 + i * 2] = (byte)((s >> 8) & 0xFF);
        }

        return wav;
    }

    private static void WriteInt(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}
