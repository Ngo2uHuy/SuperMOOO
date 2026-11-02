using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using SuperMOOO.Social.Crawlers;
using SuperMOOO.Workflows.Auto;

namespace SuperMOOO.Workflows.AutoReup;

public static class ReupPublishModes
{
    public const string Automatic = "automatic";
    public const string Review = "review";
    public const string Download = "download";
}

/// <summary>Các trường ghi đè theo kênh đích; null = kế thừa hồ sơ thị trường (hoặc cài đặt chung của luồng).</summary>
public sealed class ReupOverrides
{
    public string? Language { get; set; }
    public string? TimeZoneId { get; set; }
    public List<PostingWindow>? Windows { get; set; }
    public List<DayOfWeek>? Days { get; set; }
    public int? DailyLimit { get; set; }
    public bool? TranslateText { get; set; }
    public string? Hashtags { get; set; }
    public string? TitleTemplate { get; set; }
    public string? CaptionTemplate { get; set; }
    public string? Privacy { get; set; }

    public bool IsEmpty => Language is null && TimeZoneId is null && Windows is null && Days is null && DailyLimit is null && TranslateText is null
        && Hashtags is null && TitleTemplate is null && CaptionTemplate is null && Privacy is null;
}

/// <summary>Cấu hình riêng của một kênh đích trong luồng.</summary>
public sealed class ReupDestinationOptions
{
    /// <summary>Giá trị <see cref="MarketProfileId"/>: bỏ qua hồ sơ, dùng cài đặt chung của luồng.</summary>
    public const string NoProfile = "none";

    public string DestinationId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>null = theo hồ sơ của tài khoản; <see cref="NoProfile"/> = cài đặt chung của luồng; khác = id hồ sơ.</summary>
    public string? MarketProfileId { get; set; }
    public ReupOverrides Overrides { get; set; } = new();
}

/// <summary>Cấu hình một luồng Auto Reup: trang nguồn → lọc → xử lý video → đưa sang lịch đăng.</summary>
public sealed partial class ReupRuleConfig
{
    public static readonly IReadOnlyList<string> SourcePlatforms = ["tiktok", "youtube", "facebook", "instagram"];
    public static readonly IReadOnlyList<string> TemplateFields = ["title", "url", "author", "id", "platform", "hashtags"];

    private static readonly Dictionary<string, string[]> Domains = new()
    {
        ["tiktok"] = ["tiktok.com"], ["youtube"] = ["youtube.com"], ["facebook"] = ["facebook.com"], ["instagram"] = ["instagram.com"]
    };

    [GeneratedRegex(@"\{(?<field>[^{}]*)\}")]
    private static partial Regex Placeholder();

    [Required(ErrorMessage = "Nhập tên luồng."), StringLength(100)] public string Name { get; set; } = "";
    [RegularExpression("tiktok|youtube|facebook|instagram", ErrorMessage = "Nền tảng nguồn không hỗ trợ.")] public string SourcePlatform { get; set; } = "tiktok";
    [Required(ErrorMessage = "Nhập liên kết kênh/nguồn."), StringLength(2000)] public string SourceUrl { get; set; } = "";
    /// <summary>Id kênh/trang đích (pub_destinations).</summary>
    public List<string> DestinationIds { get; set; } = [];
    /// <summary>Cấu hình theo kênh đích (hồ sơ thị trường, ghi đè). Kênh không có ở đây dùng cài đặt chung của luồng như luồng cũ.</summary>
    public List<ReupDestinationOptions> Destinations { get; set; } = [];
    /// <summary>Tuyến mạng (S23) dùng khi quét nguồn; null = tuyến chung của app.</summary>
    public string? SourceRouteId { get; set; }
    [RegularExpression("all|new|limit", ErrorMessage = "Video cũ phải là all, new hoặc limit.")] public string History { get; set; } = "all";
    [Range(1, 10000)] public int HistoryLimit { get; set; } = 50;
    [RegularExpression("all|videos|shorts|streams", ErrorMessage = "Loại nội dung phải là all, videos, shorts hoặc streams.")] public string ContentType { get; set; } = "all";
    [Range(1, 10080)] public int IntervalMinutes { get; set; } = 10;
    /// <summary>Instagram quét bằng cookie tài khoản cá nhân nên quét dày dễ bị khoá/giới hạn.</summary>
    public const int InstagramMinIntervalMinutes = 15;
    public int EffectiveIntervalMinutes => SourcePlatform == "instagram" ? Math.Max(IntervalMinutes, InstagramMinIntervalMinutes) : IntervalMinutes;
    [RegularExpression("automatic|review|download", ErrorMessage = "Chế độ đăng phải là automatic, review hoặc download.")] public string PublishMode { get; set; } = ReupPublishModes.Review;
    public bool Enabled { get; set; } = true;
    public bool NewestFirst { get; set; } = true;
    [StringLength(500)] public string Include { get; set; } = "";
    [StringLength(500)] public string Exclude { get; set; } = "";
    [Range(0, 14400)] public double MinSeconds { get; set; }
    [Range(0, 14400)] public double MaxSeconds { get; set; }
    /// <summary>Bật xử lý video bằng hồ sơ hiệu ứng reup (cùng mô hình với S06).</summary>
    public bool Transform { get; set; }
    public string ReupPreset { get; set; } = "tiktok";
    /// <summary>Xử lý AI thay cho reup thường (lồng tiếng / review / tóm tắt). Nguồn và lịch đăng do luồng này quản lý nên Sources, Delivery bị bỏ qua.</summary>
    public AutoRequest? Processing { get; set; }
    [StringLength(500)] public string TitleTemplate { get; set; } = "{title}";
    [StringLength(5000)] public string CaptionTemplate { get; set; } = "{title}\nNguồn: {url}";
    [RegularExpression("private|unlisted|public", ErrorMessage = "Quyền riêng tư phải là private, unlisted hoặc public.")] public string Privacy { get; set; } = "private";
    [Range(0, 10080)] public int DelayMinutes { get; set; }
    [Range(1, 10080)] public int SpacingMinutes { get; set; } = 10;
    [Range(1, 1000)] public int DailyLimit { get; set; } = 20;
    [Range(0, 5)] public int Retries { get; set; } = 2;

    /// <summary>Kiểm tra và chuẩn hoá (URL nguồn, kênh đích, mẫu chữ); ném <see cref="ValidationException"/>/<see cref="ArgumentException"/> kèm lý do tiếng Việt.</summary>
    public void Normalize()
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(this, new ValidationContext(this), results, validateAllProperties: true))
            throw new ArgumentException(results[0].ErrorMessage);

        SourceUrl = NormalizeProfile(SourcePlatform, SourceUrl);
        Destinations = [.. Destinations.Where(d => d.DestinationId.Length > 0).DistinctBy(d => d.DestinationId)];
        var disabled = Destinations.Where(d => !d.Enabled).Select(d => d.DestinationId).ToHashSet();
        DestinationIds = [.. DestinationIds.Distinct().Where(id => !disabled.Contains(id))];
        SourceRouteId = string.IsNullOrWhiteSpace(SourceRouteId) ? null : SourceRouteId;
        if (PublishMode != ReupPublishModes.Download && DestinationIds.Count == 0) throw new ArgumentException("Chọn ít nhất một kênh đích, hoặc chọn Chỉ tải/xử lý.");
        if (MaxSeconds > 0 && MaxSeconds < MinSeconds) throw new ArgumentException("Thời lượng tối đa phải lớn hơn tối thiểu.");
        if (Processing is not null)
        {
            if (Processing.Mode is not (AutoModes.Localize or AutoModes.Review or AutoModes.Summary or AutoModes.Reup))
                throw new ArgumentException("Reup chỉ nhận luồng xuất video: lồng tiếng, review, tóm tắt hoặc reup.");
            Processing.Sources = [];
            Processing.Delivery = null;
        }

        CheckTemplates(TitleTemplate, CaptionTemplate);
        foreach (var d in Destinations)
        {
            var o = d.Overrides;
            CheckTemplates(o.TitleTemplate ?? "", o.CaptionTemplate ?? "");
            if (o.TitleTemplate?.Length > 500 || o.CaptionTemplate?.Length > 5000) throw new ArgumentException("Mẫu tiêu đề/mô tả của kênh quá dài.");
            if (o.Windows is not null) o.Windows = MarketProfile.NormalizeWindows(o.Windows);
            if (o.Days is not null) o.Days = [.. o.Days.Distinct().Order()];
            if (o.DailyLimit is < 1 or > MarketProfile.MaxDailyLimit) throw new ArgumentException($"Giới hạn bài/ngày của kênh phải từ 1 đến {MarketProfile.MaxDailyLimit}.");
            if (o.Privacy is not (null or "private" or "unlisted" or "public")) throw new ArgumentException("Quyền riêng tư phải là private, unlisted hoặc public.");
            if (o.Hashtags is not null) o.Hashtags = MarketProfile.NormalizeHashtags(o.Hashtags);
            if (o.TimeZoneId is { Length: > 0 } zone)
            {
                try { TimeZoneInfo.FindSystemTimeZoneById(zone); }
                catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Múi giờ của kênh không có trên máy này."); }
            }
        }
    }

    public static void CheckTemplates(params string[] templates)
    {
        foreach (var template in templates)
            if (Placeholder().Matches(template).Any(m => !TemplateFields.Contains(m.Groups["field"].Value)) || Placeholder().Replace(template, "").AsSpan().IndexOfAny('{', '}') >= 0)
                throw new ArgumentException("Mẫu tiêu đề/mô tả chỉ dùng {title}, {url}, {author}, {id}, {platform}, {hashtags}.");
    }

    /// <summary>Điền mẫu `{title} {url} {author} {id} {platform} {hashtags}`; <paramref name="title"/> thay tiêu đề gốc (vd bản đã dịch).</summary>
    public static string Fill(string template, ReupVideo video, string platform, string hashtags = "", string? title = null) =>
        Placeholder().Replace(template, m => m.Groups["field"].Value switch
        {
            "title" => title ?? video.Title, "url" => video.Url, "author" => video.Author, "id" => video.Key, "platform" => platform, "hashtags" => hashtags,
            _ => m.Value
        }).Trim();

    public bool Matches(ReupVideo video)
    {
        var text = video.Title.ToLowerInvariant();
        var include = Words(Include);
        if (include.Count > 0 && !include.Any(text.Contains)) return false;
        if (Words(Exclude).Any(text.Contains)) return false;
        return video.Duration is not { } seconds || seconds >= MinSeconds && (MaxSeconds == 0 || seconds <= MaxSeconds);
    }

    private static List<string> Words(string csv) => [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant())];

    /// <summary>Chuẩn hoá URL trang cá nhân/kênh theo nền tảng; từ chối URL video lẻ, có user:pass hoặc sai tên miền.</summary>
    public static string NormalizeProfile(string platform, string value)
    {
        value = value.Trim();
        if (platform == "tiktok" && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) value = "https://www.tiktok.com/@" + value.TrimStart('@');
        if (!Domains.TryGetValue(platform, out var domains) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            throw new ArgumentException("Nhập URL trang cá nhân/kênh hợp lệ cho nền tảng đã chọn.");
        var host = uri.Host.ToLowerInvariant();
        if (!domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal))) throw new ArgumentException("URL không khớp nền tảng nguồn.");

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0 || (platform == "tiktok" && !Regex.IsMatch(path, @"^/@[\w.-]+$")))
            throw new ArgumentException("Dùng URL trang cá nhân, ví dụ https://www.tiktok.com/@abc.");
        if ((platform == "youtube" && path is "/watch" or "/playlist") || (platform == "instagram" && (path.StartsWith("/p/") || path.StartsWith("/reel/"))))
            throw new ArgumentException("Dùng URL kênh/trang cá nhân thay vì một video.");
        return $"https://{host}{path}{(platform == "facebook" ? uri.Query : "")}";
    }
}

/// <summary>Video trong danh sách nguồn (không giữ URL tải đã ký hay thông tin đăng nhập của nền tảng).</summary>
public sealed record ReupVideo(string Key, string Url, string Title, string Author = "", double? Duration = null, long? PublishedAtMs = null,
    string? Thumbnail = null, string? JobId = null, string? OutputPath = null);

/// <summary>Liệt kê video của trang nguồn qua crawler của S10 (YouTube/TikTok/Facebook/Instagram).</summary>
[Service]
public sealed class ReupSourceLister(CrawlService crawl, YouTubeFeedClient feed, ILogger<ReupSourceLister> log)
{
    public async Task<IReadOnlyList<ReupVideo>> ListAsync(ReupRuleConfig rule, int maxItems, CancellationToken ct)
    {
        // RSS không có thời lượng và không phân biệt Shorts/Live, nên chỉ dùng khi luồng không lọc theo hai thứ đó.
        if (rule.SourcePlatform == "youtube" && maxItems <= YouTubeFeedClient.FeedSize && rule.ContentType == "all" && rule.MinSeconds == 0 && rule.MaxSeconds == 0)
        {
            try
            {
                var items = await feed.ListAsync(rule.SourceUrl, ct);
                if (items.Count > 0)
                    return [.. items.Select(x => new ReupVideo(x.ExternalId, x.Url!, x.Title ?? x.ExternalId, x.AuthorName ?? "", null,
                        x.PublishedAt?.ToUnixTimeMilliseconds(), x.ThumbnailUrl)).OrderByDescending(v => v.PublishedAtMs ?? 0)];
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "RSS YouTube của {Url} lỗi, chuyển sang yt-dlp", rule.SourceUrl);
            }
        }

        var tabs = rule.SourcePlatform == "youtube" && rule.ContentType == "all" ? new[] { "videos", "shorts", "streams" } : [rule.ContentType == "all" ? "videos" : rule.ContentType];
        var seen = new HashSet<string>();
        var videos = new List<ReupVideo>();
        for (var i = 0; i < tabs.Length; i++)
        {
            CrawlResult result;
            try
            {
                result = await crawl.CrawlAsync(new CrawlRequest(CrawlKind.Channel, rule.SourceUrl, maxItems, CrawlSource.Auto,
                    new Dictionary<string, string> { ["content_type"] = tabs[i] }), new CrawlOptions(rule.SourcePlatform, "scan_channel"), null, ct);
            }
            // Kênh YouTube có thể thiếu một tab (không có Shorts/Live): bỏ qua tab đó, lỗi ở tab cuối mới báo.
            catch (Exception e) when (tabs.Length > 1 && i < tabs.Length - 1 && e is not OperationCanceledException)
            {
                continue;
            }

            foreach (var item in result.Items.Where(x => !string.IsNullOrWhiteSpace(x.ExternalId) && !string.IsNullOrWhiteSpace(x.Url) && seen.Add(x.ExternalId)))
                videos.Add(new ReupVideo(item.ExternalId, item.Url!, item.Title ?? item.Content ?? item.ExternalId, item.AuthorName ?? "",
                    item.Duration?.TotalSeconds, item.PublishedAt?.ToUnixTimeMilliseconds(), item.ThumbnailUrl));
        }

        // Mới nhất trước khi biết ngày đăng; nguồn không có ngày giữ nguyên thứ tự trả về.
        return [.. videos.OrderByDescending(v => v.PublishedAtMs ?? 0)];
    }
}
