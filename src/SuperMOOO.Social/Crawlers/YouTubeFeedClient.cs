using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SuperMOOO.Social.Crawlers;

/// <summary>RSS công khai của kênh YouTube (~15 video gần nhất, gồm cả Shorts/livestream): không key, không tốn hạn mức nên quét dày được.</summary>
[Service]
public sealed partial class YouTubeFeedClient(CrawlHttp http)
{
    /// <summary>Feed chỉ chứa chừng này video gần nhất.</summary>
    public const int FeedSize = 15;

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    private readonly ConcurrentDictionary<string, string> _channelIds = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^/channel/(UC[\w-]{22})/?$")]
    private static partial Regex ChannelIdPath();

    // Trang kênh còn chứa channelId của kênh khác (đề xuất, kênh liên quan) nên chỉ lấy theo canonical/rssUrl của chính kênh.
    [GeneratedRegex(@"<link rel=""canonical"" href=""https://www\.youtube\.com/channel/(UC[\w-]{22})""|""rssUrl"":""https://www\.youtube\.com/feeds/videos\.xml\?channel_id=(UC[\w-]{22})""")]
    private static partial Regex ChannelIdInPage();

    public async Task<string> ResolveChannelIdAsync(string channelUrl, CancellationToken ct)
    {
        if (_channelIds.TryGetValue(channelUrl, out var cached)) return cached;
        string? id = null;
        if (Uri.TryCreate(channelUrl, UriKind.Absolute, out var uri) && ChannelIdPath().Match(uri.AbsolutePath) is { Success: true } direct)
            id = direct.Groups[1].Value;
        else
        {
            var html = await http.GetStringAsync("youtube", channelUrl, timeout: TimeSpan.FromSeconds(25), ct: ct);
            var m = ChannelIdInPage().Match(html);
            if (m.Success) id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }

        if (id is null) throw new ProviderException("youtube", ProviderErrorKind.BadResponse, "Không tìm thấy mã kênh (UC…) trong trang kênh.");
        _channelIds[channelUrl] = id;
        return id;
    }

    public async Task<IReadOnlyList<ContentItem>> ListAsync(string channelUrl, CancellationToken ct)
    {
        var channelId = await ResolveChannelIdAsync(channelUrl, ct);
        var xml = await http.GetStringAsync("youtube", $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}", ct: ct);
        return Parse(xml);
    }

    internal static IReadOnlyList<ContentItem> Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            throw new ProviderException("youtube", ProviderErrorKind.BadResponse, "RSS YouTube không hợp lệ.");
        }

        var items = new List<ContentItem>();
        foreach (var entry in doc.Descendants(Atom + "entry"))
        {
            var id = (string?)entry.Element(Yt + "videoId");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var group = entry.Element(Media + "group");
            var url = entry.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value
                ?? $"https://www.youtube.com/watch?v={id}";
            var views = group?.Element(Media + "community")?.Element(Media + "statistics")?.Attribute("views")?.Value;
            items.Add(new ContentItem("youtube", "video", id, (string?)entry.Element(Atom + "title"),
                (string?)entry.Element(Yt + "channelId"), (string?)entry.Element(Atom + "author")?.Element(Atom + "name"), url,
                group?.Element(Media + "thumbnail")?.Attribute("url")?.Value, (string?)group?.Element(Media + "description"),
                JsonX.ParseDate((string?)entry.Element(Atom + "published")), ViewCount: long.TryParse(views, out var v) ? v : null));
        }

        return items;
    }
}
