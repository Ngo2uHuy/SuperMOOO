using System.Text;

namespace SuperMOOO.Social.Crawlers;

/// <summary>Hashtag nổi bật trên TikTok Creative Center: điểm phổ biến theo ngày (0–100) để đoán xu hướng, kèm creator dùng hashtag nhiều nhất.</summary>
public sealed record CreativeCenterHashtag(string Name, int Rank, long? Posts, long? Views, IReadOnlyList<double> Curve, IReadOnlyList<ContentItem> TopCreators);

/// <summary>
/// TikTok Creative Center (ads.tiktok.com/creative/creativeCenter/trends): API web công khai, không cần chữ ký.
/// Khách chưa đăng nhập chỉ nhận vài mục đầu (3 hashtag, 4 video) nên gửi kèm cookie TikTok nếu đã lưu (cookie .tiktok.com dùng chung cho ads.tiktok.com).
/// </summary>
[Service]
public sealed class TikTokCreativeCenter(CrawlHttp http, PlatformCredentials credentials)
{
    private const string Platform = "tiktok";
    private const string Referer = "https://ads.tiktok.com/creative/creativeCenter/trends";

    // Bảng video chỉ có các nước này (cấu hình mặc định của trang); nước khác trả danh sách rỗng.
    public static readonly IReadOnlyList<string> VideoCountries = ["US", "JP", "VN", "TH", "ID"];

    public static readonly IReadOnlyList<string> HashtagCountries =
    [
        "US", "FR", "DE", "IT", "ES", "GB", "AR", "AU", "BR", "CA", "CO", "EG", "ID", "IL", "JP", "KR", "MY", "MX", "PH", "SA", "SG", "ZA", "TW", "TH", "TR", "AE", "VN"
    ];

    // Trang chọn máy chủ theo nước: Mỹ và châu Âu có cụm dữ liệu riêng.
    private static readonly HashSet<string> EuCountries =
    [
        "GB", "DE", "FR", "IT", "ES", "NL", "BE", "AT", "CH", "SE", "NO", "DK", "FI", "PL", "CZ", "HU", "RO", "BG", "HR", "SK", "SI", "LT", "LV", "EE", "IE", "PT", "GR", "CY", "MT", "LU"
    ];

    private static string HostFor(string country) => country switch
    {
        "US" => "https://ads.us.tiktok.com",
        _ when EuCountries.Contains(country) => "https://ads-useast2a.tiktok.com",
        _ => "https://ads.tiktok.com"
    };

    private async Task<Dictionary<string, string>> HeadersAsync(CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["Referer"] = Referer, ["Accept"] = "application/json, text/plain, */*" };
        if (await credentials.CookieAsync(Platform, ct) is { } cookie) headers["Cookie"] = cookie;
        return headers;
    }

    /// <summary>Hashtag nổi bật 7 ngày; industryId = ngành (vd 25000000000 Games), null = tất cả.</summary>
    public async Task<List<CreativeCenterHashtag>> HashtagsAsync(string country, long? industryId, int max, CancellationToken ct)
    {
        var headers = await HeadersAsync(ct);
        var result = new List<CreativeCenterHashtag>();
        for (var page = 1; result.Count < max && page <= 10; page++)
        {
            var body = new JsonObject { ["timeRange"] = 7, ["countryCode"] = country, ["page"] = page, ["limit"] = Math.Min(50, max) };
            if (industryId is { } industry) body["industryID"] = industry;
            var reply = await http.SendAsync(Platform, HttpMethod.Post, "https://ads.tiktok.com/CreativeOne/KnowledgeAPI/GetHashtagList", headers,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct: ct);
            var data = Check(CrawlHttp.ParseJson(Platform, reply.Body));
            var items = JsonX.Objects(data, "items").ToList();
            result.AddRange(items.Select(h => new CreativeCenterHashtag(JsonX.Str(h, "hashtagName") ?? "", (int)(JsonX.Long(h, "rankIndex") ?? result.Count + 1),
                JsonX.Long(h, "publishCnt"), JsonX.Long(h, "vv"),
                JsonX.Objects(h, "popularityCurve").Select(p => JsonX.Double(p, "value") ?? 0).ToList(),
                JsonX.Objects(h, "topCreators").Select(Creator).Where(c => c.ExternalId.Length > 0).ToList())).Where(h => h.Name.Length > 0));
            if (items.Count == 0 || JsonX.At(data, "pagination", "hasMore")?.ToString() != "true") break;
        }

        return result.Take(max).ToList();
    }

    /// <summary>Video nổi bật đăng trong 7 ngày gần nhất, xếp theo lượt xem; labelId = nhãn nội dung (vd 11005 Games), null = tất cả.</summary>
    public async Task<List<(ContentItem Video, double? EngagementRate, long? Followers)>> TopVideosAsync(string country, string? labelId, int max, CancellationToken ct)
    {
        var headers = await HeadersAsync(ct);
        var host = HostFor(country);
        // periodEndTimestamp bắt buộc (thiếu thì trả rỗng): lấy mốc ngày dữ liệu mới nhất của nước đó.
        var overview = Check(await http.GetJsonAsync(Platform, $"{host}/CreativeOne/Report/GetTopContentsOverview?countryCode={country}", headers, ct: ct));
        var end = JsonX.Long(overview, "lastDailyEndTimestamp") ?? new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero).ToUnixTimeSeconds();
        var result = new List<(ContentItem, double?, long?)>();
        for (var page = 1; result.Count < max && page <= 10; page++)
        {
            var query = JsonX.Query(
            [
                new("periodDimension", "3"), new("periodEndTimestamp", end.ToString()), new("orderByMetric", "1"), new("countryCode", country),
                new("contentLabelIDs", labelId), new("limit", Math.Min(20, max).ToString()), new("page", page.ToString())
            ]);
            var data = Check(await http.GetJsonAsync(Platform, $"{host}/CreativeOne/Report/CreativeCenterGetTopContentsList?{query}", headers, ct: ct));
            var items = JsonX.Objects(data, "entityInfos").Select(Video).Where(v => v.Item1.ExternalId.Length > 0 && result.All(r => r.Item1.ExternalId != v.Item1.ExternalId)).ToList();
            result.AddRange(items);
            // Khách chưa đăng nhập: trang luôn là 1 dù xin trang khác, gọi tiếp chỉ lặp lại cùng kết quả.
            if (items.Count == 0 || JsonX.Long(data, "pagination", "page") != page || JsonX.At(data, "pagination", "hasMore")?.ToString() != "true") break;
        }

        return result.Take(max).ToList();
    }

    private static (ContentItem, double?, long?) Video(JsonObject e)
    {
        var id = JsonX.Str(e, "itemInfo", "itemID") ?? "";
        var handle = JsonX.Str(e, "itemAuthorInfo", "handlerName");
        var views = JsonX.Long(e, "itemMetrics", "videoViewsLifeTime") ?? JsonX.Long(e, "itemMetrics", "videoViews");
        var item = new ContentItem(Platform, "video", id, JsonX.Str(e, "itemInfo", "title")?.Trim() ?? $"TikTok video {id}", handle,
            JsonX.Str(e, "itemAuthorInfo", "nickName") ?? handle, handle is null ? $"https://www.tiktok.com/video/{id}" : $"https://www.tiktok.com/@{handle}/video/{id}",
            JsonX.Str(e, "itemInfo", "coverURL"), PublishedAt: JsonX.FromUnixSeconds(JsonX.Long(e, "itemInfo", "createTime")), ViewCount: views);
        return (item, JsonX.Double(e, "itemMetrics", "engagementRateLifeTime") ?? JsonX.Double(e, "itemMetrics", "engagementRate"),
            JsonX.Long(e, "itemAuthorMetrics", "followers"));
    }

    private static ContentItem Creator(JsonObject c)
    {
        var handle = JsonX.Str(c, "handleName") ?? "";
        return new ContentItem(Platform, "channel", handle, JsonX.Str(c, "nickname") ?? handle, handle, JsonX.Str(c, "nickname") ?? handle,
            $"https://www.tiktok.com/@{handle}", JsonX.Str(c, "avatarURL"), ViewCount: JsonX.Long(c, "followedCnt"));
    }

    private static JsonNode Check(JsonNode reply)
    {
        var status = JsonX.Long(reply, "BaseResp", "StatusCode");
        if (status is 0) return reply;
        var message = JsonX.Str(reply, "BaseResp", "StatusMessage") ?? JsonX.Str(reply, "message") ?? JsonX.Str(reply, "msg") ?? "";
        throw new ProviderException(Platform, ProviderErrorKind.BadResponse,
            $"TikTok Creative Center từ chối yêu cầu (mã {JsonX.Str(reply, "BaseResp", "StatusCode") ?? JsonX.Str(reply, "code") ?? "?"}{(message.Length > 0 ? $": {message}" : "")}); có thể trang đã đổi cấu trúc.");
    }
}
