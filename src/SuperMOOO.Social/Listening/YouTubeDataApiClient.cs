using System.Text.RegularExpressions;
using System.Xml;
using SuperMOOO.Social.Comments;
using SuperMOOO.Social.Crawlers;

namespace SuperMOOO.Social.Listening;

public sealed record YouTubeChannel(string Id, string Title, string Description, string? CustomUrl, DateTimeOffset? PublishedAt,
    string? ThumbnailUrl, long Subscribers, long Views, long VideoCount, string? UploadsPlaylistId)
{
    public string Url => $"https://www.youtube.com/channel/{Id}";
}

/// <summary>YouTube Data API v3 (OAuth token ưu tiên, sau đó API key); mọi lời gọi tính vào hạn mức.</summary>
[Service]
public sealed partial class YouTubeDataApiClient(CrawlHttp http, PlatformCredentials credentials, QuotaService quota)
{
    private const string Base = "https://www.googleapis.com/youtube/v3";
    private const string Platform = "youtube";

    [GeneratedRegex("^UC[a-zA-Z0-9_-]{18,}$")] private static partial Regex ChannelIdPattern();
    [GeneratedRegex(@"^[\w.-]+$")] private static partial Regex HandlePattern();

    /// <summary>(loại tham số API, giá trị): id | forHandle | forUsername.</summary>
    public static (string Type, string Value) ParseChannelIdentifier(string input)
    {
        var clean = input.Trim().TrimEnd('/');
        if (clean.StartsWith('@')) return ("forHandle", clean);

        if (clean.Contains("://") || clean.StartsWith("youtube.com/", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("www.youtube.com/", StringComparison.OrdinalIgnoreCase)
            || clean.StartsWith("m.youtube.com/", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(clean.Contains("://") ? clean : "https://" + clean, UriKind.Absolute, out var uri)
                || !(uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
                || uri.UserInfo.Length > 0)
                throw new ArgumentException("URL kênh YouTube không hợp lệ.");
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            if (parts.Length > 0 && parts[0].StartsWith('@') && parts[0].Length > 1) return ("forHandle", parts[0]);
            if (parts.Length >= 2 && parts[0] == "channel" && ChannelIdPattern().IsMatch(parts[1])) return ("id", parts[1]);
            if (parts.Length >= 2 && parts[0] == "user") return ("forUsername", parts[1]);
            throw new ArgumentException("Dùng handle @tên, ID kênh, hoặc URL /channel/ và /user/.");
        }

        if (ChannelIdPattern().IsMatch(clean)) return ("id", clean);
        if (HandlePattern().IsMatch(clean)) return ("forHandle", "@" + clean);
        throw new ArgumentException("Handle hoặc ID kênh YouTube không hợp lệ.");
    }

    private async Task<JsonNode> CallAsync(string endpoint, Dictionary<string, string?> parameters, int cost, CancellationToken ct)
    {
        var token = await credentials.TokenAsync(Platform, ct);
        var key = token is null ? await credentials.GetAsync(Platform, "api_key", ct) : null;
        if (token is null && key is null)
            throw new ProviderException(Platform, ProviderErrorKind.KeyMissing, "Chưa cấu hình YouTube Data API key hoặc access token (Cài đặt > Nền tảng).");

        var headers = new Dictionary<string, string>();
        if (token is not null) headers["Authorization"] = $"Bearer {token}";
        else parameters["key"] = key;

        var node = await http.GetJsonAsync(Platform, $"{Base}/{endpoint}?{JsonX.Query(parameters)}", headers, TimeSpan.FromSeconds(12), ct);
        await quota.RecordAsync(Platform, endpoint, cost, "youtube_data_api", ct);
        return node;
    }

    public async Task<YouTubeChannel> GetChannelAsync(string channelOrUrl, CancellationToken ct = default)
    {
        var (type, value) = ParseChannelIdentifier(channelOrUrl);
        var data = await CallAsync("channels", new() { ["part"] = "snippet,statistics,contentDetails", [type] = value }, QuotaCost.YoutubeList, ct);
        var ch = JsonX.Objects(data, "items").FirstOrDefault()
            ?? throw new ProviderException(Platform, ProviderErrorKind.BadResponse, $"Không tìm thấy kênh YouTube: {channelOrUrl}");
        return ToChannel(ch);
    }

    /// <summary>Nhiều kênh theo ID, 50 kênh một lời gọi (1 đơn vị).</summary>
    public async Task<IReadOnlyList<YouTubeChannel>> GetChannelsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var result = new List<YouTubeChannel>();
        foreach (var chunk in ids.Chunk(50))
        {
            var data = await CallAsync("channels", new() { ["part"] = "snippet,statistics", ["id"] = string.Join(',', chunk) }, QuotaCost.YoutubeList, ct);
            result.AddRange(JsonX.Objects(data, "items").Select(ToChannel));
        }

        return result;
    }

    private static YouTubeChannel ToChannel(JsonObject ch) =>
        new(JsonX.Str(ch, "id")!, JsonX.Str(ch, "snippet", "title") ?? "", JsonX.Str(ch, "snippet", "description") ?? "",
            JsonX.Str(ch, "snippet", "customUrl"), JsonX.ParseDate(JsonX.Str(ch, "snippet", "publishedAt")),
            JsonX.Str(ch, "snippet", "thumbnails", "high", "url"), JsonX.Long(ch, "statistics", "subscriberCount") ?? 0,
            JsonX.Long(ch, "statistics", "viewCount") ?? 0, JsonX.Long(ch, "statistics", "videoCount") ?? 0,
            JsonX.Str(ch, "contentDetails", "relatedPlaylists", "uploads"));

    public async Task<IReadOnlyList<string>> GetPlaylistVideoIdsAsync(string playlistId, int max, CancellationToken ct = default)
    {
        var ids = new List<string>();
        string? page = null;
        var target = Math.Min(max, 200);
        while (ids.Count < target)
        {
            var data = await CallAsync("playlistItems", new()
            {
                ["part"] = "contentDetails", ["playlistId"] = playlistId, ["maxResults"] = Math.Min(50, target - ids.Count).ToString(), ["pageToken"] = page
            }, QuotaCost.YoutubeList, ct);
            var items = JsonX.Objects(data, "items").ToList();
            ids.AddRange(items.Select(i => JsonX.Str(i, "contentDetails", "videoId")).OfType<string>());
            page = JsonX.Str(data, "nextPageToken");
            if (page is null || items.Count == 0) break;
        }

        return ids;
    }

    public async Task<IReadOnlyList<ContentItem>> GetVideosAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var result = new List<ContentItem>();
        foreach (var chunk in ids.Chunk(50))
        {
            var data = await CallAsync("videos", new() { ["part"] = "snippet,statistics,contentDetails", ["id"] = string.Join(',', chunk) }, QuotaCost.YoutubeList, ct);
            result.AddRange(JsonX.Objects(data, "items").Select(ToVideo));
        }

        return result;
    }

    /// <summary>Bảng thịnh hành (chart=mostPopular) theo vùng/chuyên mục, kèm tags của từng video; 1 đơn vị mỗi trang 50 video.</summary>
    public async Task<IReadOnlyList<(ContentItem Video, IReadOnlyList<string> Tags)>> GetMostPopularAsync(string regionCode, string? categoryId, int max,
        CancellationToken ct = default)
    {
        var result = new List<(ContentItem, IReadOnlyList<string>)>();
        string? page = null;
        var target = Math.Min(max, 200);
        while (result.Count < target)
        {
            var data = await CallAsync("videos", new()
            {
                ["part"] = "snippet,statistics,contentDetails", ["chart"] = "mostPopular", ["regionCode"] = regionCode, ["videoCategoryId"] = categoryId,
                ["maxResults"] = Math.Min(50, target - result.Count).ToString(), ["pageToken"] = page
            }, QuotaCost.YoutubeList, ct);
            var items = JsonX.Objects(data, "items").ToList();
            result.AddRange(items.Select(v => (ToVideo(v), (IReadOnlyList<string>)((JsonX.At(v, "snippet", "tags") as JsonArray)?
                .Select(t => t?.ToString()).OfType<string>().ToList() ?? []))));
            page = JsonX.Str(data, "nextPageToken");
            if (page is null || items.Count == 0) break;
        }

        return result;
    }

    private static ContentItem ToVideo(JsonObject v)
    {
        var id = JsonX.Str(v, "id")!;
        var thumbs = JsonX.At(v, "snippet", "thumbnails");
        return new ContentItem(Platform, "video", id, JsonX.Str(v, "snippet", "title"), JsonX.Str(v, "snippet", "channelId"),
            JsonX.Str(v, "snippet", "channelTitle"), $"https://www.youtube.com/watch?v={id}",
            JsonX.Str(thumbs, "maxres", "url") ?? JsonX.Str(thumbs, "high", "url") ?? JsonX.Str(thumbs, "default", "url"),
            JsonX.Str(v, "snippet", "description"), JsonX.ParseDate(JsonX.Str(v, "snippet", "publishedAt")),
            ParseDuration(JsonX.Str(v, "contentDetails", "duration")), JsonX.Long(v, "statistics", "viewCount"),
            JsonX.Long(v, "statistics", "likeCount"), JsonX.Long(v, "statistics", "commentCount"),
            JsonX.Str(v, "snippet", "defaultAudioLanguage"), JsonX.Str(v, "snippet", "categoryId"));
    }

    public async Task<IReadOnlyList<string>> SearchVideoIdsAsync(string keyword, int max, string order, CancellationToken ct = default)
    {
        var data = await CallAsync("search", new()
        {
            ["part"] = "snippet", ["q"] = keyword, ["type"] = "video", ["maxResults"] = Math.Min(max, 50).ToString(), ["order"] = order
        }, QuotaCost.YoutubeSearch, ct);
        return JsonX.Objects(data, "items").Select(i => JsonX.Str(i, "id", "videoId")).OfType<string>().ToList();
    }

    public async Task<IReadOnlyList<RawComment>> GetCommentsAsync(string videoId, int max = 100, CancellationToken ct = default)
    {
        var comments = new List<RawComment>();
        string? page = null;
        var target = Math.Min(max, 500);
        while (comments.Count < target)
        {
            var data = await CallAsync("commentThreads", new()
            {
                ["part"] = "snippet,replies", ["videoId"] = videoId, ["maxResults"] = Math.Min(100, target - comments.Count).ToString(),
                ["textFormat"] = "plainText", ["pageToken"] = page
            }, QuotaCost.YoutubeList, ct);
            var items = JsonX.Objects(data, "items").ToList();
            foreach (var item in items)
            {
                var top = JsonX.At(item, "snippet", "topLevelComment", "snippet");
                comments.Add(new RawComment(JsonX.Str(top, "authorDisplayName") ?? "Unknown", JsonX.Str(top, "authorChannelUrl"),
                    JsonX.Str(top, "textDisplay") ?? "", (int)(JsonX.Long(top, "likeCount") ?? 0), JsonX.Str(top, "publishedAt")));
                if (comments.Count >= target) break;
            }

            page = JsonX.Str(data, "nextPageToken");
            if (page is null || items.Count == 0) break;
        }

        return comments;
    }

    public static ContentItem ToItem(YouTubeChannel c) => new(Platform, "channel", c.Id, c.Title, c.Id, c.Title, c.Url, c.ThumbnailUrl,
        c.Description, c.PublishedAt, null, c.Subscribers, null, c.VideoCount);

    private static TimeSpan? ParseDuration(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try
        {
            return XmlConvert.ToTimeSpan(iso);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
