using System.Globalization;
using System.Text.Json;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Social.Platforms;

namespace SuperMOOO.Social.Download;

public sealed record CatalogEntry(string Url, string Title, TimeSpan? Duration, string? ThumbnailUrl, string? Author,
    DateTime? PublishedAt = null, long? ViewCount = null);

/// <summary>Liệt kê mục của playlist/series/danh sách tập và kết quả tìm kiếm bằng yt-dlp (không tải gì); cùng proxy/cookie như khi tải.</summary>
[Service]
public sealed class VideoCatalogService(IToolLocator tools, IProcessRunner runner, INetworkRouter router, PlatformSessionService sessions)
{
    public const int MaxEntries = 200;

    public static IReadOnlyList<CatalogEntry> Filter(IReadOnlyList<CatalogEntry> entries, DateTime? from, DateTime? to, string sortBy)
    {
        if (from is not null && to is not null && from.Value.Date > to.Value.Date)
            throw new ArgumentException("Ngày bắt đầu phải trước hoặc bằng ngày kết thúc.");
        var filtered = entries.Where(e => (from is null || e.PublishedAt is { } publishedFrom && publishedFrom.Date >= from.Value.Date)
            && (to is null || e.PublishedAt is { } publishedTo && publishedTo.Date <= to.Value.Date));
        return sortBy switch
        {
            "oldest" => [.. filtered.OrderBy(e => e.PublishedAt is null).ThenBy(e => e.PublishedAt)],
            "popular" => [.. filtered.OrderBy(e => e.ViewCount is null).ThenByDescending(e => e.ViewCount)],
            "latest" => [.. filtered.OrderBy(e => e.PublishedAt is null).ThenByDescending(e => e.PublishedAt)],
            _ => [.. filtered]
        };
    }

    // Chỉ các nền tảng mà yt-dlp có extractor tìm kiếm công khai (kiểm tra bằng --list-extractors).
    private static readonly Dictionary<string, string> SearchPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = "ytsearch", ["bilibili"] = "bilisearch"
    };

    public static bool CanSearch(string platform) => SearchPlatforms.ContainsKey(platform);

    public Task<IReadOnlyList<CatalogEntry>> SearchAsync(string platform, string keyword, int count, CancellationToken ct, bool detailed = false)
    {
        if (!SearchPlatforms.TryGetValue(platform, out var prefix)) throw new NotSupportedException("Nền tảng này chưa hỗ trợ tìm kiếm theo từ khoá; dán liên kết trực tiếp.");
        keyword = keyword.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (keyword.Length == 0) throw new ArgumentException("Nhập từ khoá để tìm.");
        count = Math.Clamp(count, 1, 50);
        return ListAsync($"{prefix}{count}:{keyword}", count, platform, ct, detailed);
    }

    public Task<IReadOnlyList<CatalogEntry>> ListAsync(string url, int limit, CancellationToken ct, bool detailed = false)
    {
        url = (url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Liên kết phải bắt đầu bằng http:// hoặc https://.", nameof(url));
        return ListAsync(url, limit, PlatformDetector.Detect(url), ct, detailed);
    }

    private async Task<IReadOnlyList<CatalogEntry>> ListAsync(string target, int limit, string? platform, CancellationToken ct, bool detailed)
    {
        limit = Math.Clamp(limit, 1, MaxEntries);
        var headers = platform is null ? new Dictionary<string, string>() : await sessions.BuildHeadersAsync(platform, ct);
        var ffmpeg = tools.TryResolve(ToolKind.Ffmpeg, out var path) ? Path.GetDirectoryName(path) : null;
        var args = new List<string> { "--dump-json", "--skip-download", "--no-warnings", "--playlist-end", limit.ToString(CultureInfo.InvariantCulture) };
        if (!detailed) args.Insert(0, "--flat-playlist");
        args.AddRange(YtDlpDownloader.CommonArguments(router.ProxyUriForTools(), ffmpeg, headers));
        args.Add("--");
        args.Add(target);

        var sink = new Collector();
        var result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.YtDlp), args), sink, ct);
        var entries = sink.Entries;
        if (entries.Count == 0)
            throw new DownloadException(result.ExitCode != 0 ? YtDlpDownloader.MapError(result.OutputTail) : "Không tìm thấy mục nào.");
        return entries;
    }

    private sealed class Collector : IProgress<string>
    {
        private readonly object _gate = new();
        private readonly List<CatalogEntry> _entries = [];

        public IReadOnlyList<CatalogEntry> Entries { get { lock (_gate) return [.. _entries]; } }

        public void Report(string line)
        {
            if (!line.StartsWith('{')) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (Parse(doc.RootElement) is { } entry) lock (_gate) _entries.Add(entry);
            }
            catch (JsonException) { /* dòng log không phải JSON */ }
        }

        private static CatalogEntry? Parse(JsonElement e)
        {
            var url = Text(e, "webpage_url") ?? Text(e, "url");
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                // Mục YouTube dạng phẳng đôi khi chỉ có id.
                if (Text(e, "ie_key") == "Youtube" && Text(e, "id") is { } id) url = "https://www.youtube.com/watch?v=" + id;
                else return null;
            }

            TimeSpan? duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(d.GetDouble()) : null;
            var thumb = Text(e, "thumbnail");
            if (thumb is null && e.TryGetProperty("thumbnails", out var list) && list.ValueKind == JsonValueKind.Array)
                thumb = list.EnumerateArray().Select(t => Text(t, "url")).LastOrDefault(u => u is not null);
            DateTime? published = null;
            var date = Text(e, "upload_date") ?? Text(e, "release_date");
            if (date is not null && DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                published = parsed;
            else if (e.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var seconds))
                published = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
            long? views = e.TryGetProperty("view_count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var value)
                ? value : null;
            return new CatalogEntry(url, Text(e, "title") ?? url, duration, thumb, Text(e, "uploader") ?? Text(e, "channel"), published, views);
        }

        private static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
