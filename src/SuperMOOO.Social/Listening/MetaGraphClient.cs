using System.Text.Json;
using SuperMOOO.Social.Crawlers;

namespace SuperMOOO.Social.Listening;

public sealed record MetaAdsQuery(string? SearchTerms, string? SearchPageIds, string Country = "VN", string ActiveStatus = "ACTIVE", int Limit = 25);

public sealed record MetaAdsResult(string SearchKey, IReadOnlyList<ContentItem> Ads, MetaRateLimit RateLimit);

/// <summary>Meta Graph API: thư viện quảng cáo (không cần App Review), thông tin và bài đăng công khai của Trang.</summary>
[Service]
public sealed class MetaGraphClient(CrawlHttp http, PlatformCredentials credentials, QuotaService quota, IContentStore content,
    ICrawlRunStore runs, MetricService metrics, AuditLogService audit)
{
    private const string Platform = "facebook";

    private async Task<string> TokenAsync(CancellationToken ct) =>
        await credentials.GetAsync("meta", "ads_token", ct) ?? await credentials.TokenAsync("meta", ct)
        ?? throw new ProviderException(Platform, ProviderErrorKind.KeyMissing, "Chưa cấu hình Meta access token (Cài đặt > Nền tảng > Meta).");

    private async Task<(JsonNode Data, MetaRateLimit Rate)> GetAsync(string endpoint, Dictionary<string, string?> parameters, CancellationToken ct,
        string? token = null)
    {
        parameters["access_token"] = token ?? await TokenAsync(ct);
        var reply = await http.SendAsync(Platform, HttpMethod.Get, $"{MetaGraph.BaseUrl}/{endpoint}?{JsonX.Query(parameters)}",
            new Dictionary<string, string> { ["User-Agent"] = "SuperMOOO-MetaConnector/1.0" }, timeout: TimeSpan.FromSeconds(15), ct: ct);
        await quota.RecordAsync("meta", endpoint, QuotaCost.MetaCall, "meta_graph", ct);
        reply.Headers.TryGetValue("x-app-usage", out var usage);
        return (CrawlHttp.ParseJson(Platform, reply.Body), QuotaService.ParseMetaRateLimit(usage));
    }

    public async Task<MetaAdsResult> SearchAdsAsync(MetaAdsQuery q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.SearchTerms) && string.IsNullOrWhiteSpace(q.SearchPageIds))
            throw new ArgumentException("Nhập từ khoá (search_terms) hoặc ID Trang (search_page_ids).");

        var key = $"{q.Country}:{q.ActiveStatus}:{q.SearchTerms}:{q.SearchPageIds}";
        var parameters = new Dictionary<string, string?>
        {
            ["ad_reached_countries"] = JsonSerializer.Serialize(new[] { q.Country.ToUpperInvariant() }),
            ["ad_active_status"] = q.ActiveStatus.ToUpperInvariant(),
            ["limit"] = Math.Min(q.Limit, 100).ToString(),
            ["fields"] = "id,page_id,page_name,ad_creation_time,ad_delivery_start_time,ad_delivery_stop_time,ad_creative_bodies,ad_creative_link_captions,"
                         + "ad_creative_link_titles,ad_creative_link_descriptions,ad_snapshot_url,publisher_platforms,currency,spend,impressions",
            ["search_terms"] = q.SearchTerms?.Trim(),
            ["search_page_ids"] = q.SearchPageIds?.Trim()
        };

        var runId = await runs.StartAsync(Platform, "meta_ads", key, ct: ct);
        try
        {
            var (data, rate) = await GetAsync("ads_archive", parameters, ct);
            var ads = new List<ContentItem>();
            foreach (var ad in JsonX.Objects(data, "data"))
            {
                var id = JsonX.Str(ad, "id") ?? "";
                var page = JsonX.Str(ad, "page_name");
                var snapshot = JsonX.Str(ad, "ad_snapshot_url");
                var body = (JsonX.At(ad, "ad_creative_bodies") as JsonArray)?.FirstOrDefault()?.ToString();
                var headline = (JsonX.At(ad, "ad_creative_link_titles") as JsonArray)?.FirstOrDefault()?.ToString();
                var item = new ContentItem(Platform, "ad", id, $"Ad by {page} ({id})", JsonX.Str(ad, "page_id"), page,
                    snapshot ?? $"https://www.facebook.com/ads/library/?id={id}", snapshot, body ?? headline,
                    JsonX.ParseDate(JsonX.Str(ad, "ad_delivery_start_time") ?? JsonX.Str(ad, "ad_creation_time")));
                var contentId = await content.UpsertAsync(item, ct);
                await runs.AddItemAsync(runId, contentId, ads.Count + 1, ct);
                ads.Add(item with { Id = contentId });
            }

            await runs.CompleteAsync(runId, "finished", ads.Count, ct: ct);
            await audit.AddAsync("meta_ads_library", key, "search_ads", "success", ads.Count, ct);
            return new MetaAdsResult(key, ads, rate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await runs.CompleteAsync(runId, "failed", 0, ex.Message, CancellationToken.None);
            throw;
        }
    }

    public async Task<ContentItem> GetPageAsync(string pageIdOrUsername, CancellationToken ct = default)
    {
        var (d, _) = await GetAsync(pageIdOrUsername.Trim(), new()
        {
            ["fields"] = "id,name,about,category,fan_count,followers_count,link,picture.type(large){url}"
        }, ct);
        var id = JsonX.Str(d, "id") ?? throw new ProviderException(Platform, ProviderErrorKind.BadResponse, "Phản hồi Trang không có id.");
        var item = new ContentItem(Platform, "page", id, JsonX.Str(d, "name"), id, JsonX.Str(d, "name"), JsonX.Str(d, "link") ?? $"https://www.facebook.com/{id}",
            JsonX.Str(d, "picture", "data", "url"), JsonX.Str(d, "about"), null, null, JsonX.Long(d, "followers_count") ?? JsonX.Long(d, "fan_count"),
            Category: JsonX.Str(d, "category"));
        item = item with { Id = await content.UpsertAsync(item, ct) };
        await metrics.RecordItemAsync(item, ct);
        return item;
    }

    public async Task<IReadOnlyList<ContentItem>> GetPagePostsAsync(string pageId, int limit = 25, CancellationToken ct = default)
    {
        var (d, _) = await GetAsync($"{pageId.Trim()}/feed", new()
        {
            ["fields"] = "id,message,created_time,shares,reactions.summary(true){total_count},comments.summary(true){total_count},attachments{media,type,url}",
            ["limit"] = Math.Min(limit, 50).ToString()
        }, ct);

        var posts = new List<ContentItem>();
        foreach (var p in JsonX.Objects(d, "data"))
        {
            var id = JsonX.Str(p, "id") ?? "";
            var message = JsonX.Str(p, "message");
            var thumb = (JsonX.At(p, "attachments", "data") as JsonArray)?.OfType<JsonObject>().Select(a => JsonX.Str(a, "media", "image", "src")).FirstOrDefault(s => s is not null);
            var item = new ContentItem(Platform, "post", id, message is null ? $"Post {id}" : message[..Math.Min(80, message.Length)], pageId, null,
                $"https://www.facebook.com/{id}", thumb, message, JsonX.ParseDate(JsonX.Str(p, "created_time")), null, null,
                JsonX.Long(p, "reactions", "summary", "total_count"), JsonX.Long(p, "comments", "summary", "total_count"));
            item = item with { Id = await content.UpsertAsync(item, ct) };
            await metrics.RecordItemAsync(item, ct);
            if (JsonX.Long(p, "shares", "count") is > 0 and var shares) await metrics.RecordAsync(Platform, id, item.Title, item.Url, "shares", shares, ct);
            posts.Add(item);
        }

        return posts;
    }

    /// <summary>
    /// Bài nổi bật của một hashtag Instagram (ig_hashtag_search → top_media). Graph API chỉ cho tra hashtag qua tài khoản Instagram doanh nghiệp/nhà sáng tạo
    /// liên kết với Trang của token, tối đa 30 hashtag khác nhau mỗi 7 ngày; không trả tên người đăng.
    /// </summary>
    public async Task<IReadOnlyList<ContentItem>> GetInstagramHashtagMediaAsync(string hashtag, int limit, CancellationToken ct = default)
    {
        var token = await credentials.TokenAsync("instagram", ct) ?? await credentials.TokenAsync("meta", ct)
            ?? throw new ProviderException("instagram", ProviderErrorKind.KeyMissing,
                "Chưa có access token Meta/Instagram (Cài đặt > Nền tảng > Meta). Token phải thuộc Trang đã liên kết tài khoản Instagram doanh nghiệp.");
        var userId = await InstagramUserIdAsync(token, ct);
        var tag = hashtag.Trim().TrimStart('#').Replace(" ", "");
        var (search, _) = await GetAsync("ig_hashtag_search", new() { ["user_id"] = userId, ["q"] = tag }, ct, token);
        var tagId = JsonX.Objects(search, "data").Select(d => JsonX.Str(d, "id")).FirstOrDefault(id => id is not null)
            ?? throw new ProviderException("instagram", ProviderErrorKind.BadResponse, $"Instagram không có hashtag #{tag}.");
        var (media, _) = await GetAsync($"{tagId}/top_media", new()
        {
            ["user_id"] = userId, ["fields"] = "id,caption,media_type,permalink,like_count,comments_count,timestamp",
            ["limit"] = Math.Clamp(limit, 1, 50).ToString()
        }, ct, token);

        var items = JsonX.Objects(media, "data").Select(m =>
        {
            var caption = JsonX.Str(m, "caption");
            return new ContentItem("instagram", JsonX.Str(m, "media_type") == "VIDEO" ? "video" : "post", JsonX.Str(m, "id") ?? "",
                caption is null ? $"#{tag}" : caption[..Math.Min(120, caption.Length)], Url: JsonX.Str(m, "permalink"), Content: caption,
                PublishedAt: JsonX.ParseDate(JsonX.Str(m, "timestamp")), LikeCount: JsonX.Long(m, "like_count"), CommentCount: JsonX.Long(m, "comments_count"));
        }).Where(i => i.ExternalId.Length > 0).ToList();
        await audit.AddAsync("instagram_graph", "hashtag_top_media", tag, "success", items.Count, ct);
        return items;
    }

    private string? _instagramUserId;

    /// <summary>Token của Trang: /me có instagram_business_account; token người dùng: lấy Trang đầu tiên có liên kết Instagram.</summary>
    private async Task<string> InstagramUserIdAsync(string token, CancellationToken ct)
    {
        if (_instagramUserId is { } cached) return cached;
        string? id = null;
        try
        {
            var (me, _) = await GetAsync("me", new() { ["fields"] = "id,instagram_business_account" }, ct, token);
            id = JsonX.Str(me, "instagram_business_account", "id");
        }
        catch (ProviderException ex) when (ex.Kind == ProviderErrorKind.BadResponse)
        {
            // Token người dùng: node User không có field instagram_business_account (HTTP 400).
        }

        if (id is null)
        {
            var (pages, _) = await GetAsync("me/accounts", new() { ["fields"] = "instagram_business_account", ["limit"] = "100" }, ct, token);
            id = JsonX.Objects(pages, "data").Select(p => JsonX.Str(p, "instagram_business_account", "id")).FirstOrDefault(s => s is not null);
        }

        return _instagramUserId = id ?? throw new ProviderException("instagram", ProviderErrorKind.Forbidden,
            "Token Meta chưa liên kết tài khoản Instagram doanh nghiệp/nhà sáng tạo nên không tra được hashtag. Liên kết Instagram với Trang Facebook rồi tạo lại token có quyền instagram_basic.");
    }
}
