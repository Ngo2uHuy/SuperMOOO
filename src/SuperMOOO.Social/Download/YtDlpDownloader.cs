using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuperMOOO.Infrastructure.Network;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Social.Crawlers;
using SuperMOOO.Social.Platforms;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Social.Download;

public sealed class DownloadException(string message) : Exception(message);

/// <summary>Luồng phát trực tiếp: URL hình (kèm URL tiếng khi nền tảng tách riêng như YouTube) và header phải gửi kèm (User-Agent, Referer, Cookie).</summary>
public sealed record StreamSource(string? Title, string VideoUrl, string? AudioUrl, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>CDN đòi cookie (TikTok) mà LibVLC không gửi được cookie tuỳ ý, nên phải tải về rồi phát file cục bộ.</summary>
    public bool NeedsCookie => AudioUrl is null && Headers.ContainsKey("Cookie");
}

/// <summary>
/// Tải qua yt-dlp (tiến trình ngoài, đi qua proxy của NetworkRouter). Tải vào cache\dl-tmp rồi mới chuyển sang thư mục đích,
/// nên huỷ/lỗi không để lại file dở.
/// </summary>
[Service(As = [typeof(IDownloader)])]
public sealed partial class YtDlpDownloader(IToolLocator tools, IProcessRunner runner, INetworkRouter router,
    PlatformSessionService sessions, IHttpClientProvider http, AppPaths paths, BilibiliFallback bilibili, ILogger<YtDlpDownloader> log,
    MachineRouteLock routeLock) : IDownloader
{
    internal const string FilePrefix = "AIVS_FILE:";
    internal const string InfoPrefix = "AIVS_INFO:";
    private const string InfoFields = "%(.{id,title,uploader,channel,duration,thumbnail,webpage_url,view_count})j";

    [GeneratedRegex(@"\[download\]\s+(?<p>\d+(?:\.\d+)?)%")]
    private static partial Regex ProgressLine();

    public async Task<RemoteMediaInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        url = RequireUrl(url);
        var args = new List<string> { "--dump-single-json", "--no-playlist", "--playlist-items", "1", "--no-warnings", "--skip-download" };
        var headers = await HeadersFor(url, ct);
        args.AddRange(CommonArguments(router.ProxyUriForTools(), FfmpegDirectory(), headers));
        args.Add("--");
        args.Add(url);

        var sink = new LineSink();
        ProcessResult result;
        await using (await SharedAsync(headers, ct)) result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.YtDlp), args), sink, ct);
        if (result.ExitCode != 0)
        {
            if (!FallBackToBilibili(url, result)) throw new DownloadException(MapError(result.OutputTail));
            return await bilibili.GetInfoAsync(url, ct);
        }

        var json = sink.Json ?? throw new DownloadException("Không đọc được thông tin video từ yt-dlp.");
        return ParseInfo(url, json);
    }

    /// <summary>Lấy URL luồng để xem trước, không tải: cùng proxy/cookie như khi tải.</summary>
    public async Task<StreamSource> ResolveStreamAsync(string url, CancellationToken ct)
    {
        url = RequireUrl(url);
        var args = new List<string> { "--dump-json", "--no-playlist", "--playlist-items", "1", "--no-warnings", "--skip-download" };
        var headers = await HeadersFor(url, ct);
        args.AddRange(CommonArguments(router.ProxyUriForTools(), FfmpegDirectory(), headers));
        args.AddRange(StreamFormat);
        args.Add("--");
        args.Add(url);

        var sink = new LineSink();
        ProcessResult result;
        await using (await SharedAsync(headers, ct)) result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.YtDlp), args), sink, ct);
        if (result.ExitCode != 0)
        {
            if (!FallBackToBilibili(url, result)) throw new DownloadException(MapError(result.OutputTail));
            return await bilibili.ResolveStreamAsync(url, ct);
        }

        return ParseStream(sink.Json ?? throw new DownloadException("Không đọc được luồng video từ yt-dlp."));
    }

    /// <summary>Tải luồng cần cookie vào cache\preview để trình phát mở như file cục bộ; huỷ/lỗi thì xoá file dở.</summary>
    public async Task<string> CacheStreamAsync(StreamSource stream, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var folder = Directory.CreateDirectory(paths.CacheDir("preview")).FullName;
        var target = Path.Combine(folder, $"stream-{Guid.NewGuid():N}.mp4");
        var done = false;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, stream.VideoUrl);
            foreach (var (name, value) in stream.Headers) request.Headers.TryAddWithoutValidation(name, value);
            using var response = await http.Get("download").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, link.Token);
            if (!response.IsSuccessStatusCode)
                throw new DownloadException($"Máy chủ video từ chối luồng xem trước (HTTP {(int)response.StatusCode}); thử lại hoặc thêm Cookie ở Cài đặt > Nền tảng.");

            var total = response.Content.Headers.ContentLength ?? 0;
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(link.Token))
            await using (var file = File.Create(target))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, link.Token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), link.Token);
                    written += read;
                    if (total > 0) progress?.Report(new ProgressInfo((double)written / total, "Đang tải bản xem trước"));
                }
            }

            if (written == 0) throw new DownloadException("Luồng video rỗng.");
            done = true;
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException($"Không tải được bản xem trước: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DownloadException("Tải bản xem trước quá 5 phút; mở trên trình duyệt hoặc đưa vào tải.");
        }
        finally
        {
            if (!done) TryDelete(target);
        }
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var url = RequireUrl(request.Url);
        Directory.CreateDirectory(request.OutputFolder);

        if (request.NoWatermark && request.Format == DownloadFormat.Best && request.SectionStart is null && request.SectionEnd is null && PlatformDetector.Detect(url) == "tiktok"
            && await TryTikTokCleanAsync(url, request.OutputFolder, progress, ct) is { } clean)
            return clean;

        var work = Directory.CreateDirectory(Path.Combine(paths.CacheDir("dl-tmp"), Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var headers = await HeadersFor(url, ct, request.CookiePlatform);
            var args = BuildDownloadArguments(request with { Url = url }, work, router.ProxyUriForTools(), FfmpegDirectory(), headers);
            var sink = new LineSink(progress);
            ProcessResult result;
            await using (await SharedAsync(headers, ct)) result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.YtDlp), args), sink, ct);
            if (result.ExitCode != 0)
            {
                if (!FallBackToBilibili(url, result)) throw new DownloadException(MapError(result.OutputTail));
                var (file, fallbackInfo) = await bilibili.DownloadAsync(request with { Url = url }, work, progress, ct);
                var moved = MoveUnique(file, request.OutputFolder);
                progress?.Report(new ProgressInfo(1, "Hoàn tất"));
                return new DownloadResult(moved, new FileInfo(moved).Length, fallbackInfo);
            }

            var produced = sink.FilePath;
            if (produced is null || !File.Exists(produced) || new FileInfo(produced).Length == 0)
                throw new DownloadException("Không tìm thấy file tải hoàn tất. Kiểm tra lại liên kết và định dạng đã chọn.");

            var final = MoveUnique(produced, request.OutputFolder);
            progress?.Report(new ProgressInfo(1, "Hoàn tất"));
            var info = sink.Json is { } json ? ParseInfo(url, json) : new RemoteMediaInfo(url, PlatformDetector.Detect(url) ?? "other",
                null, Path.GetFileNameWithoutExtension(final), null, null, null, new Dictionary<string, string>());
            return new DownloadResult(final, new FileInfo(final).Length, info);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Tham số tải; công khai để test kiểm tra từng cờ mà không cần chạy yt-dlp.</summary>
    public static IReadOnlyList<string> BuildDownloadArguments(DownloadRequest request, string workFolder, string? proxy,
        string? ffmpegDirectory, IReadOnlyDictionary<string, string> headers)
    {
        var args = new List<string> { "--newline", "--no-playlist", "--playlist-items", "1", "--windows-filenames" };
        args.AddRange(CommonArguments(proxy, ffmpegDirectory, headers));
        var formatArgs = FormatArguments(request.Format).ToList();
        if (request.NoWatermark && request.Format != DownloadFormat.AudioBest)
        {
            // Lọc cả các nhánh dự phòng; metadata thiếu không có nghĩa video đã được xác nhận sạch.
            var selector = formatArgs.IndexOf("-f") + 1;
            formatArgs[selector] = $"({formatArgs[selector]})[has_watermark!=?1][is_watermarked!=?1][format_note!~=?'(?i)\\bwatermarked\\b']";
        }
        args.AddRange(formatArgs);

        if (request.SectionStart is not null || request.SectionEnd is not null)
        {
            args.Add("--download-sections");
            args.Add($"*{Seconds(request.SectionStart ?? TimeSpan.Zero)}-{(request.SectionEnd is { } end ? Seconds(end) : "inf")}");
        }

        args.AddRange(["-o", Path.Combine(workFolder, "%(title).120s [%(id)s].%(ext)s"),   // không dùng --trim-filenames: yt-dlp tính cả đường dẫn thư mục nên cắt luôn tên thư mục tạm khi data root dài
            "--no-simulate", "--no-quiet", "--print", $"after_move:{FilePrefix}%(filepath)s", "--print", $"after_move:{InfoPrefix}{InfoFields}",
            "--", request.Url]);
        return args;
    }

    // Mặc định yt-dlp chọn AV1/VP9 + Opus ở cùng độ phân giải: trình phát Windows không có AV1 extension chỉ phát tiếng.
    // Giữ ưu tiên độ phân giải, cùng độ phân giải thì lấy H.264 + AAC để file mp4 mở được ở mọi nơi.
    private const string CompatibleSort = "res,vcodec:h264,acodec:m4a";

    public static IReadOnlyList<string> FormatArguments(DownloadFormat format) => format switch
    {
        DownloadFormat.Best4K => ["-f", "bv*[height<=2160]+ba/bv*+ba/b", "-S", CompatibleSort, "--merge-output-format", "mp4"],
        DownloadFormat.P1080 => ["-f", "bv*[height<=1080]+ba/b[height<=1080]/b", "-S", CompatibleSort, "--merge-output-format", "mp4"],
        DownloadFormat.P720 => ["-f", "bv*[height<=720]+ba/b[height<=720]/b", "-S", CompatibleSort, "--merge-output-format", "mp4"],
        DownloadFormat.P480 => ["-f", "bv*[height<=480]+ba/b[height<=480]/b", "-S", CompatibleSort, "--merge-output-format", "mp4"],
        DownloadFormat.AudioBest => ["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "320K"],
        DownloadFormat.VideoMuted => ["-f", "bv*/b", "-S", CompatibleSort, "--merge-output-format", "mp4"],
        _ => ["-f", "bv*+ba/b", "-S", CompatibleSort, "--merge-output-format", "mp4"]
    };

    // Xem trước ưu tiên một file mp4 có sẵn cả hình lẫn tiếng (TikTok…); YouTube giờ chỉ còn luồng tách nên lấy hình ≤720p + tiếng m4a
    // để trình phát ghép. ≤720p cho nhanh, H.264 để LibVLC giải mã phần cứng được trên mọi máy.
    private static readonly string[] StreamFormat = ["-f", "b[ext=mp4]/bv*[height<=720]+ba/b", "-S", "res:720,vcodec:h264,acodec:m4a"];

    private static readonly HashSet<string> CookieAttributes = new(StringComparer.OrdinalIgnoreCase)
        { "Domain", "Path", "Secure", "HttpOnly", "Expires", "Max-Age", "SameSite" };

    /// <summary>JSON `-j` sau khi chọn định dạng → URL hình/tiếng + header; công khai để test không cần chạy yt-dlp.</summary>
    public static StreamSource ParseStream(JsonElement json)
    {
        string? video = null, audio = null;
        var part = json;
        if (json.TryGetProperty("requested_formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            foreach (var format in formats.EnumerateArray())
            {
                if (Text(format, "vcodec") == "none") audio ??= Text(format, "url");
                else if (video is null)
                {
                    video = Text(format, "url");
                    part = format;
                }
            }

        video ??= Text(json, "url");
        if (video is null) throw new DownloadException("yt-dlp không trả về luồng phát trực tiếp cho liên kết này.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var source = part.TryGetProperty("http_headers", out var h) && h.ValueKind == JsonValueKind.Object ? h
            : json.TryGetProperty("http_headers", out h) && h.ValueKind == JsonValueKind.Object ? h : default;
        if (source.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "User-Agent", "Referer" })
                if (Text(source, name) is { Length: > 0 } value) headers[name] = value;

        // yt-dlp đã lọc sẵn cookie áp cho đúng URL luồng, dạng Set-Cookie nối nhau: bỏ thuộc tính, bỏ ngoặc kép quanh giá trị (CDN TikTok trả 403 nếu giữ).
        var cookies = Text(part, "cookies") ?? Text(json, "cookies");
        if (cookies is not null)
        {
            var pairs = cookies.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2 && p[0].Length > 0 && !CookieAttributes.Contains(p[0]))
                .Select(p => $"{p[0]}={p[1].Trim('"')}")
                .ToList();
            if (pairs.Count > 0) headers["Cookie"] = string.Join("; ", pairs);
        }

        return new StreamSource(Text(json, "title"), video, audio, headers);
    }

    /// <summary>Phần trăm tiến độ của dòng `[download]  45.3% of …`; null nếu không phải dòng tiến độ.</summary>
    public static ProgressInfo? ParseProgress(string line)
    {
        var m = ProgressLine().Match(line);
        if (!m.Success) return null;
        var percent = double.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
        return new ProgressInfo(Math.Clamp(percent / 100, 0, 1), line.Replace("[download]", "").Trim());
    }

    public static string MapError(IReadOnlyList<string> tail)
    {
        var line = tail.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            ?? tail.LastOrDefault(l => Regex.IsMatch(l, @"^\w*(?:Error|Exception):"))
            ?? tail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "yt-dlp thất bại không rõ lý do.";
        var lower = line.ToLowerInvariant();

        if (lower.Contains("unsupported url") || lower.Contains("404") || lower.Contains("not available")
            || lower.Contains("video unavailable") || lower.Contains("does not exist") || lower.Contains("error_code=300031"))
            return "Liên kết không tồn tại hoặc nội dung đã bị xóa/chặn trên nền tảng (HTTP 404).";
        if (lower.Contains("sign in") || lower.Contains("login") || lower.Contains("cookie") || lower.Contains("private")
            || lower.Contains("403") || lower.Contains("not a robot") || lower.Contains("captcha"))
            return "Nền tảng yêu cầu đăng nhập hoặc chặn truy cập; thêm Cookie ở Cài đặt > Nền tảng rồi thử lại.";
        return line.Length > 300 ? line[..300] : line;
    }

    internal static List<string> CommonArguments(string? proxy, string? ffmpegDirectory, IReadOnlyDictionary<string, string> headers)
    {
        var args = new List<string>
        {
            "--retries", "15", "--fragment-retries", "15", "--concurrent-fragments", "8", "--socket-timeout", "30",
            "--proxy", proxy ?? "",     // chuỗi rỗng chặn yt-dlp tự suy ra proxy từ biến môi trường
            "--encoding", "utf-8"       // stdout bị pipe trên Windows mặc định là cp1252, làm hỏng đường dẫn có dấu tiếng Việt
        };
        if (ffmpegDirectory is not null) args.AddRange(["--ffmpeg-location", ffmpegDirectory]);
        foreach (var (name, value) in headers) args.AddRange(["--add-header", $"{name}:{value}"]);
        return args;
    }

    /// <summary>yt-dlp lỗi với liên kết Bilibili (vd. "Unable to extract initial state" khi trang đổi cấu trúc) → thử API Bilibili.</summary>
    private bool FallBackToBilibili(string url, ProcessResult result)
    {
        if (!BilibiliFallback.Handles(url)) return false;
        log.LogInformation("yt-dlp không đọc được Bilibili ({Error}); chuyển sang API Bilibili.", MapError(result.OutputTail));
        return true;
    }

    private string? FfmpegDirectory() => tools.TryResolve(ToolKind.Ffmpeg, out var ffmpeg) ? Path.GetDirectoryName(ffmpeg) : null;

    /// <summary>Cookie tài khoản đi tuyến chung: chờ VPN cả máy của tài khoản khác tắt rồi mới chạy yt-dlp (phiên bị thấy đổi sang IP nước khác).</summary>
    private async Task<MachineRouteLock.Hold?> SharedAsync(IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (!CrawlHttp.HasIdentity(headers)) return null;
        try { return await routeLock.EnterReadAsync(null, MachineRouteLock.IdentityWait, ct); }
        catch (RouteBusyException ex) { throw new DownloadException(ex.Message); }
    }

    private async Task<IReadOnlyDictionary<string, string>> HeadersFor(string url, CancellationToken ct, string? platformOverride = null)
    {
        var platform = PlatformRegistry.NormalizeId(platformOverride) ?? PlatformDetector.Detect(url);
        return platform is null ? new Dictionary<string, string>() : await sessions.BuildHeadersAsync(platform, ct);
    }

    private async Task<DownloadResult?> TryTikTokCleanAsync(string url, string folder, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var partial = Path.Combine(folder, $".tiktok_{Guid.NewGuid():N}.partial");
        try
        {
            var client = http.Get("download");
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lookup.CancelAfter(TimeSpan.FromSeconds(8));
            var api = await client.GetStringAsync("https://www.tikwm.com/api/?url=" + Uri.EscapeDataString(url), lookup.Token);
            using var doc = JsonDocument.Parse(api);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

            var play = Text(data, "play") ?? Text(data, "hdplay");
            if (play is null) return null;
            var id = Text(data, "id") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var title = Text(data, "title") ?? $"tiktok_{id}";

            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            using var response = await client.GetAsync(play, HttpCompletionOption.ResponseHeadersRead, link.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(link.Token))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, link.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), link.Token);
                    written += read;
                    if (total > 0) progress?.Report(new ProgressInfo((double)written / total, "Đang tải (không watermark)"));
                }
            }

            if (written == 0 || total > 0 && written != total) throw new InvalidDataException("Luồng video tải chưa đầy đủ.");

            var name = SafePath.SanitizeFileName(title.Length > 80 ? title[..80] : title, "tiktok_video") + $" [{id}].mp4";
            var final = MoveUnique(partial, folder, name);
            progress?.Report(new ProgressInfo(1, "Hoàn tất"));
            var info = new RemoteMediaInfo(url, "tiktok", id, title, null, null, null, new Dictionary<string, string>());
            return new DownloadResult(final, new FileInfo(final).Length, info);
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or IOException or InvalidDataException or OperationCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            log.LogInformation("Không tải được bản không watermark ({Reason}); chuyển sang yt-dlp.", ex.GetType().Name);
            return null;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private static RemoteMediaInfo ParseInfo(string url, JsonElement json)
    {
        var extra = new Dictionary<string, string>();
        if (json.TryGetProperty("view_count", out var views) && views.ValueKind == JsonValueKind.Number)
            extra["view_count"] = views.GetRawText();

        TimeSpan? duration = json.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(d.GetDouble()) : null;
        var pageUrl = Text(json, "webpage_url") ?? url;
        return new RemoteMediaInfo(pageUrl, PlatformDetector.Detect(url) ?? PlatformDetector.Detect(pageUrl) ?? "other", Text(json, "id"),
            Text(json, "title"), Text(json, "uploader") ?? Text(json, "channel"), duration, Text(json, "thumbnail"), extra);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    private static string RequireUrl(string url)
    {
        url = (url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Liên kết phải bắt đầu bằng http:// hoặc https://.", nameof(url));
        return url;
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string MoveUnique(string source, string folder, string? name = null)
    {
        name ??= Path.GetFileName(source);
        var target = Path.Combine(folder, name);
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        File.Move(source, target);
        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nằm trong cache, janitor sẽ dọn */ }
    }

    /// <summary>Gom các dòng đầu ra của yt-dlp: tiến độ, đường dẫn file cuối và JSON thông tin; gọi từ nhiều luồng nên có khoá.</summary>
    private sealed class LineSink(IProgress<ProgressInfo>? progress = null) : IProgress<string>
    {
        private readonly object _gate = new();
        private string? _file;
        private JsonElement? _json;

        public string? FilePath { get { lock (_gate) return _file; } }
        public JsonElement? Json { get { lock (_gate) return _json; } }

        public void Report(string line)
        {
            if (ParseProgress(line) is { } p) progress?.Report(p);
            else if (line.StartsWith(FilePrefix, StringComparison.Ordinal)) lock (_gate) _file = line[FilePrefix.Length..].Trim();
            else if (line.StartsWith(InfoPrefix, StringComparison.Ordinal)) lock (_gate) _json = TryParse(line[InfoPrefix.Length..]);
            else if (line.StartsWith('{')) lock (_gate) _json = TryParse(line);
        }

        private static JsonElement? TryParse(string text)
        {
            try { return JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
    }
}
