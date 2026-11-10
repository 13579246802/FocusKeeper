namespace FocusKeeper.Platform;

/// <summary>
/// 休息音乐的曲库。
/// 优先级是铁律：用户在 data\music 里放了任何支持的音频文件，就只放用户的；
/// 用户目录空着才用软件自带的 music\ 目录；两处都没有返回空，由调用方落到合成雨声。
/// 随机挑曲，但不会和上一首重复（除非曲库里只有一首）。
/// </summary>
public sealed class MusicLibrary
{
    public static readonly string[] SupportedExtensions =
        { ".mp3", ".wav", ".m4a", ".flac", ".wma" };

    private readonly string _userDir;
    private readonly string _defaultDir;
    private readonly Random _rng = new();
    private string? _lastTrack;

    public MusicLibrary(string userDir, string defaultDir)
    {
        _userDir = userDir;
        _defaultDir = defaultDir;
    }

    /// <summary>当前生效的曲库来源："用户上传" / "自带" / null（两处都没有，走雨声兜底）。</summary>
    public string? Source
    {
        get
        {
            var tracks = UserTracks;
            if (tracks.Count > 0) return "用户上传";
            if (DefaultTracks.Count > 0) return "自带";
            return null;
        }
    }

    /// <summary>用户目录（data\music）。有文件时它是唯一生效的曲库。</summary>
    public List<string> UserTracks => List(_userDir);

    /// <summary>软件自带曲库（exe 旁的 music\）。</summary>
    public List<string> DefaultTracks => List(_defaultDir);

    /// <summary>当前生效的全部曲目：用户有就用用户的，否则用自带的。</summary>
    public List<string> EffectiveTracks =>
        UserTracks.Count > 0 ? UserTracks : DefaultTracks;

    /// <summary>随机挑一首，不与上一首重复。曲库为空返回 null。</summary>
    public string? PickNext()
    {
        var tracks = EffectiveTracks;
        if (tracks.Count == 0)
        {
            _lastTrack = null;
            return null;
        }

        // 多于一首时避开上一首；只有一首时重复它（总比没音乐强）
        if (tracks.Count > 1 && _lastTrack is not null)
            tracks = tracks.Where(t => !string.Equals(t, _lastTrack, StringComparison.OrdinalIgnoreCase)).ToList();

        var pick = tracks[_rng.Next(tracks.Count)];
        _lastTrack = pick;
        return pick;
    }

    private static List<string> List(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory
                .EnumerateFiles(dir)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
