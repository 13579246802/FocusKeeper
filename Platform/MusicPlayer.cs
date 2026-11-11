using System.Media;
using System.Runtime.InteropServices;
using System.Text;

namespace FocusKeeper.Platform;

/// <summary>
/// 休息时的轻音乐与提示音。
/// 曲目用 MusicLibrary 给的 MP3 等（winmm/MCI 播放，Windows 自带解码，零依赖）；
/// 曲子放不出来就静音休息，不再合成任何背景声（看书时合成雨声听着像杂音，已废除）；
/// 双音 chime 用于「该休息了」和「休息超时了」两种提醒，音量克制。
/// WAV 生成为纯函数（可自测），SoundPlayer 只负责播放。
/// </summary>
public static class MusicPlayer
{
    private const int SampleRate = 22050;
    private const string MciAlias = "fk_music";

    private static MemoryStream? _chimeStream;
    private static SoundPlayer? _chimePlayer;
    private static readonly object Gate = new();

    public static bool Playing { get; private set; }

    /// <summary>当前是否在播 MCI 曲目。供 Tick 轮询曲子播完自动换下一首。</summary>
    public static bool TrackOpen { get; private set; }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnBuffer, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciGetErrorString(int errorCode, StringBuilder errorText, int maxLength);

    private static string MciErrorText(int code)
    {
        var sb = new StringBuilder(256);
        mciGetErrorString(code, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 发 MCI 命令：返回值是 mciSendString 的错误码（0 = 成功），
    /// out 带回的是返回缓冲区文本（open 成功时是设备 ID，status 时是状态）。
    /// 两者必须分开——早先版本把缓冲区文本当返回值判断成败，open 成功返回的
    /// 设备 ID "1" 被当成失败，钢琴曲一次都没真正放过，全掉进了（现已废除的）雨声兜底。
    /// </summary>
    private static int MciSend(string command, out string result)
    {
        var sb = new StringBuilder(64);
        var ret = mciSendString(command, sb, sb.Capacity, IntPtr.Zero);
        result = sb.ToString().Trim();
        return ret;
    }

    /// <summary>播一首曲目文件（mp3/wav 等）。声卡或解码失败时返回 false，调用方换一首或静音。</summary>
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
                if (MciSend($"open \"{path}\" type {type} alias {MciAlias}", out _) != 0) return false;
                // 音量克制：休息音乐不该盖过门铃。setaudio 失败不致命，不影响放
                MciSend($"setaudio {MciAlias} volume to 600", out _);
                if (MciSend($"play {MciAlias}", out _) != 0) { CloseTrack(); return false; }
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
            try
            {
                return MciSend($"status {MciAlias} mode", out var mode) == 0 && mode == "playing";
            }
            catch { return false; }
        }
    }

    private static void CloseTrack()
    {
        try { MciSend($"close {MciAlias}", out _); } catch { }
        TrackOpen = false;
    }

    /// <summary>
    /// 诊断用（--playtest）：走与 PlayTrack 完全相同的链路打开/播放一个文件，
    /// 把每步的 MCI 错误码、缓冲文本和错误原文写成报告，用来定位「音乐放不出来」。
    /// 用独立别名 fk_diag，不碰正常播放的 fk_music。
    /// </summary>
    public static string PlayTestReport(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"file={path}");
        sb.AppendLine($"exists={File.Exists(path)} size={(File.Exists(path) ? new FileInfo(path).Length : 0)}");

        var open = MciSend($"open \"{path}\" type mpegvideo alias fk_diag", out var devId);
        sb.AppendLine($"open type=mpegvideo errCode={open} devId={devId}");
        if (open != 0)
        {
            sb.AppendLine($"mciError={MciErrorText(open)}");
            return sb.ToString();
        }

        sb.AppendLine($"setaudio errCode={MciSend("setaudio fk_diag volume to 600", out _)}");
        sb.AppendLine($"play errCode={MciSend("play fk_diag", out _)}");
        Thread.Sleep(1200);
        MciSend("status fk_diag mode", out var mode);
        MciSend("status fk_diag length", out var length);
        sb.AppendLine($"mode after 1.2s={mode}");
        sb.AppendLine($"length={length}");
        MciSend("close fk_diag", out _);
        sb.AppendLine(mode == "playing" ? "=> 播放正常" : "=> open 成功但没在播，需进一步排查");
        return sb.ToString();
    }

    /// <summary>休息结束（人回来了 / 自动收工）：停止曲目。</summary>
    public static void StopTrack()
    {
        lock (Gate)
        {
            CloseTrack();
            Playing = false;
        }
    }

    /// <summary>提醒音：到点该休息了 / 休息超时了。叠加在曲目上播放。</summary>
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
