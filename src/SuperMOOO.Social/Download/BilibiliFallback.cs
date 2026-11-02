using System.Globalization;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Social.Crawlers;
using SuperMOOO.Social.Platforms;

namespace SuperMOOO.Social.Download;

/// <summary>
/// Dự phòng khi yt-dlp không đọc được trang video Bilibili (trang giờ render phía client, không còn __INITIAL_STATE__):
/// lấy luồng thẳng từ API (view → playurl ký WBI), tải hình/tiếng kèm Referer rồi ghép bằng ffmpeg.
/// </summary>
[Service]
public sealed class BilibiliFallback(BilibiliApi api, IHttpClientProvider http, IToolLocator tools, IProcessRunner runner)
{
    private const int DashAll = 4048;      // DASH + HDR + 4K + Dolby + 8K + AV1
    private const int Mp4 = 1;             // một file mp4 có sẵn tiếng (durl)
    private const int PreviewQuality = 64; // 720p

    // CDN chỉ kiểm Referer/User-Agent; không gửi cookie đăng nhập ra CDN.
    private static readonly IReadOnlyDictionary<string, string> CdnHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = CrawlHttp.DesktopAgent, ["Referer"] = BilibiliApi.Referer
    };

    private sealed record Track(int Quality, int Codec, long Bandwidth, IReadOnlyList<string> Urls);

    public static bool Handles(string url) => PlatformDetector.Detect(url) == "bilibili";

    public async Task<RemoteMediaInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        var video = await Guard(async () => await api.VideoAsync(url, await api.HeadersAsync(ct), ct));
        return ToInfo(video);
    }

    /// <summary>Xem trước: ưu tiên một file mp4 720p có tiếng (LibVLC gửi được Referer/User-Agent); không có thì hình DASH ≤720p + tiếng.</summary>
    public Task<StreamSource> ResolveStreamAsync(string url, CancellationToken ct) => Guard(async () =>
    {
        var headers = await api.HeadersAsync(ct);
        var video = await api.VideoAsync(url, headers, ct);
        var mp4 = await api.PlayUrlAsync(video, Mp4, PreviewQuality, headers, ct);
        if (JsonX.Objects(mp4, "durl").ToList() is [var single] && Urls(single, "url", "backup_url").FirstOrDefault() is { } file)
            return new StreamSource(video.Title, file, null, CdnHeaders);

        var dash = await api.PlayUrlAsync(video, DashAll, PreviewQuality, headers, ct);
        var picked = PickVideo(dash, PreviewQuality) ?? throw new DownloadException("Bilibili không trả luồng hình để xem trước.");
        return new StreamSource(video.Title, picked.Urls[0], PickAudio(dash)?.Urls[0], CdnHeaders);
    });

    /// <summary>Tải vào thư mục tạm, trả đường dẫn file hoàn chỉnh (chưa chuyển sang thư mục đích) cùng thông tin video.</summary>
    public Task<(string File, RemoteMediaInfo Info)> DownloadAsync(DownloadRequest request, string work, IProgress<ProgressInfo>? progress,
        CancellationToken ct) => Guard(async () =>
    {
        var headers = await api.HeadersAsync(ct);
        var video = await api.VideoAsync(request.Url, headers, ct);
        var maxQuality = MaxQuality(request.Format);
        var dash = await api.PlayUrlAsync(video, DashAll, maxQuality, headers, ct);
        var picture = request.Format == DownloadFormat.AudioBest ? null : PickVideo(dash, maxQuality);
        var sound = request.Format == DownloadFormat.VideoMuted ? null : PickAudio(dash);
        var name = SafePath.SanitizeFileName(video.Title.Length > 120 ? video.Title[..120] : video.Title, "bilibili_video")
                   + $" [{video.Bvid}{(video.Page > 1 ? $"_p{video.Page}" : "")}]";

        List<string> inputs = [];
        string? merged = null;
        // Khách (chưa đăng nhập) chỉ được DASH 480p nhưng durl vẫn cho mp4 720p có tiếng: chọn bản nét hơn.
        var granted = Math.Min((int)(JsonX.Long(dash, "quality") ?? 0), maxQuality);
        if (request.Format != DownloadFormat.AudioBest && (picture is null || picture.Quality < granted))
        {
            var mp4 = await api.PlayUrlAsync(video, Mp4, granted > 0 ? granted : maxQuality, headers, ct);
            if (JsonX.Objects(mp4, "durl").ToList() is [var single] && (picture is null || (JsonX.Long(mp4, "quality") ?? 0) > picture.Quality))
            {
                merged = Path.Combine(work, "durl.mp4");
                await FetchAsync(Urls(single, "url", "backup_url"), merged, progress, 0, 0.95, ct);
                inputs.Add(merged);
            }
        }

        if (merged is null)
        {
            if (picture is null && sound is null) throw new DownloadException("Bilibili không trả luồng phát nào cho video này.");
            if (picture is null && request.Format != DownloadFormat.AudioBest) throw new DownloadException("Bilibili không trả luồng hình cho video này.");
            var share = picture is not null && sound is not null ? 0.8 : 0.95;
            if (picture is not null)
            {
                inputs.Add(Path.Combine(work, "video.m4s"));
                await FetchAsync(picture.Urls, inputs[^1], progress, 0, share, ct);
            }

            if (sound is not null)
            {
                inputs.Add(Path.Combine(work, "audio.m4s"));
                await FetchAsync(sound.Urls, inputs[^1], progress, picture is null ? 0 : share, 0.95, ct);
            }
        }

        progress?.Report(new ProgressInfo(0.96, "Đang ghép hình và tiếng"));
        var audioOnly = request.Format == DownloadFormat.AudioBest;
        var output = Path.Combine(work, name + (audioOnly ? ".mp3" : ".mp4"));
        await MuxAsync(inputs, output, request, audioOnly, ct);
        return (output, ToInfo(video));
    });

    private static RemoteMediaInfo ToInfo(BilibiliVideo video)
    {
        var extra = new Dictionary<string, string>();
        if (video.Views is { } views) extra["view_count"] = views.ToString(CultureInfo.InvariantCulture);
        return new RemoteMediaInfo(video.PageUrl, "bilibili", video.Bvid, video.Title, video.Author, video.Duration, video.Thumbnail, extra);
    }

    // Mã chất lượng Bilibili: 127 8K, 120 4K, 116 1080p60, 112 1080p+, 80 1080p, 74 720p60, 64 720p, 32 480p, 16 360p.
    private static int MaxQuality(DownloadFormat format) => format switch
    {
        DownloadFormat.Best4K => 120,
        DownloadFormat.P1080 => 116,
        DownloadFormat.P720 => 74,
        DownloadFormat.P480 => 32,
        _ => 127
    };

    // Cùng chất lượng thì ưu tiên H.264 (codecid 7) rồi HEVC (12), AV1 (13) để mp4 mở được ở mọi trình phát; bỏ HDR/Dolby Vision (125/126) vì màu bạc trên màn SDR.
    private static Track? PickVideo(JsonNode data, int maxQuality) =>
        JsonX.Objects(data, "dash", "video").Select(v => ToTrack(v, (int)(JsonX.Long(v, "codecid") ?? 0)))
            .Where(t => t.Urls.Count > 0 && t.Quality <= maxQuality && t.Quality is not (125 or 126))
            .OrderByDescending(t => t.Quality).ThenBy(t => t.Codec switch { 7 => 0, 12 => 1, _ => 2 }).FirstOrDefault();

    private static Track? PickAudio(JsonNode data) =>
        JsonX.Objects(data, "dash", "audio").Select(a => ToTrack(a, 0)).Where(t => t.Urls.Count > 0)
            .OrderByDescending(t => t.Bandwidth).FirstOrDefault();

    private static Track ToTrack(JsonObject node, int codec) =>
        new((int)(JsonX.Long(node, "id") ?? 0), codec, JsonX.Long(node, "bandwidth") ?? 0, Urls(node, "baseUrl", "backupUrl"));

    private static List<string> Urls(JsonObject node, string main, string backups) =>
        new[] { JsonX.Str(node, main) }.Concat(JsonX.At(node, backups) is JsonArray list ? list.Select(u => JsonX.Str(u)) : [])
            .OfType<string>().Distinct().ToList();

    /// <summary>Tải một luồng, máy chủ chính lỗi thì thử lần lượt máy chủ dự phòng.</summary>
    private async Task FetchAsync(IReadOnlyList<string> urls, string target, IProgress<ProgressInfo>? progress, double from, double to, CancellationToken ct)
    {
        var client = http.Get("download");
        string? failure = null;
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var (name, value) in CdnHeaders) request.Headers.TryAddWithoutValidation(name, value);
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (!response.IsSuccessStatusCode)
                {
                    failure = $"HTTP {(int)response.StatusCode}";
                    continue;
                }

                var total = response.Content.Headers.ContentLength ?? 0;
                long written = 0;
                await using (var source = await response.Content.ReadAsStreamAsync(stall.Token))
                await using (var file = File.Create(target))
                {
                    var buffer = new byte[256 * 1024];
                    int read;
                    // Chỉ huỷ khi luồng đứng yên 30 s, không giới hạn tổng thời gian vì video dài có thể vài GB.
                    while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
                    {
                        stall.CancelAfter(TimeSpan.FromSeconds(30));
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        written += read;
                        if (total > 0) progress?.Report(new ProgressInfo(from + (to - from) * written / total, $"Đang tải từ Bilibili {written / 1048576.0:0.#}/{total / 1048576.0:0.#} MB"));
                    }
                }

                if (written > 0 && (total == 0 || written == total)) return;
                failure = "luồng tải chưa đầy đủ";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                failure = ex is OperationCanceledException ? "máy chủ không phản hồi 30 s" : ex.Message;
            }
        }

        throw new DownloadException($"Không tải được luồng video từ máy chủ Bilibili ({failure}).");
    }

    private async Task MuxAsync(IReadOnlyList<string> inputs, string output, DownloadRequest request, bool audioOnly, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        foreach (var input in inputs)
        {
            if (request.SectionStart is { } start) args.AddRange(["-ss", Seconds(start)]);
            args.AddRange(["-i", input]);
        }

        if (request.SectionEnd is { } end) args.AddRange(["-t", Seconds(end - (request.SectionStart ?? TimeSpan.Zero))]);
        for (var i = 0; i < inputs.Count; i++) args.AddRange(["-map", $"{i}"]);
        if (audioOnly) args.AddRange(["-vn", "-c:a", "libmp3lame", "-b:a", "320k"]);
        else
        {
            args.AddRange(["-c", "copy", "-movflags", "+faststart"]);
            if (request.Format == DownloadFormat.VideoMuted) args.Add("-an");
        }

        args.Add(output);
        var result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.Ffmpeg), args), null, ct);
        if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            throw new DownloadException("Ghép hình và tiếng bằng ffmpeg thất bại: " + (result.OutputTail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? $"mã thoát {result.ExitCode}"));
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Lỗi API (cần đăng nhập, khu vực, video bị xoá…) → thông báo tiếng Việt kèm gợi ý thêm cookie.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (ProviderException ex) when (ex.Kind is ProviderErrorKind.Forbidden or ProviderErrorKind.KeyInvalid)
        {
            throw new DownloadException($"{ex.Message} Thêm Cookie Bilibili đã đăng nhập (SESSDATA) ở Cài đặt > Nền tảng rồi thử lại.");
        }
        catch (ProviderException ex)
        {
            throw new DownloadException($"Không lấy được video Bilibili (yt-dlp và API Bilibili đều lỗi): {ex.Message}");
        }
    }
}
