namespace SuperMOOO.Core.Abstractions;

public enum DownloadFormat { Best, Best4K, P1080, P720, P480, AudioBest, VideoMuted }
public sealed record DownloadRequest(string Url, string OutputFolder, DownloadFormat Format = DownloadFormat.Best,
    bool NoWatermark = true, string? CookiePlatform = null, TimeSpan? SectionStart = null, TimeSpan? SectionEnd = null);
public sealed record RemoteMediaInfo(string Url, string Platform, string? Id, string? Title, string? Author,
    TimeSpan? Duration, string? ThumbnailUrl, IReadOnlyDictionary<string, string> Extra);
public sealed record DownloadResult(string FilePath, long SizeBytes, RemoteMediaInfo Info);

public interface IDownloader
{
    Task<RemoteMediaInfo> GetInfoAsync(string url, CancellationToken ct);
    Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct);
}
