namespace SuperMOOO.Core.Models;

public sealed record ContentItem(string Platform, string Kind, string ExternalId, string? Title = null,
    string? AuthorId = null, string? AuthorName = null, string? Url = null, string? ThumbnailUrl = null,
    string? Content = null, DateTimeOffset? PublishedAt = null, TimeSpan? Duration = null,
    long? ViewCount = null, long? LikeCount = null, long? CommentCount = null,
    string? Language = null, string? Category = null, long Id = 0);

// Words: mốc từng từ từ ASR (karaoke \kf). Style: ghi đè kiểu riêng cho cue, xuất thành 1 Style ASS riêng.
public sealed record SubtitleCue(int Index, TimeSpan Start, TimeSpan End, string Text,
    IReadOnlyList<WordTiming>? Words = null, SubtitleStyle? Style = null);

public interface ISubtitleFormat
{
    string Extension { get; }
    bool Bom { get; }
    string Render(SubtitleDocument document);
}

public sealed record SubtitleDocument(IReadOnlyList<SubtitleCue> Cues)
{
    public async Task SaveAsync(string path, ISubtitleFormat format, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, format.Render(this), new System.Text.UTF8Encoding(format.Bom), ct);
    }
}

// PositionY: % từ mép trên (50–95), ưu tiên hơn MarginV. KaraokeColor: màu tô dần khi cue có Words.
// BoxLeft/BoxWidth: dải chữ theo % chiều rộng khung (mặc định lề 5% hai bên); chữ xuống dòng trong dải này.
public sealed record SubtitleStyle(string FontName = "Arial", int FontSize = 48, string PrimaryColor = "#FFFFFF",
    string OutlineColor = "#000000", int Outline = 2, int MarginV = 40, string Alignment = "bottom",
    string BackColor = "#000000", double BackOpacity = 0.5, bool BoxBackground = false, bool Bold = false,
    double? PositionY = null, string? KaraokeColor = null, double? BoxLeft = null, double? BoxWidth = null);

public sealed record VoiceEffect(string Id, string Name, double PitchSemitones = 0, double Tempo = 1, string? FfmpegFilter = null);

public sealed record ProxyProfile(string Id, string Name, string Scheme, string Host, int Port, string? Username = null, bool Enabled = true);
public sealed record RouteSnapshot(string Mode, string? ProxyUri, string? VpnName, DateTimeOffset At);
public sealed record IpCheckResult(bool Ok, string? Ip, string? Country, string? Message);
