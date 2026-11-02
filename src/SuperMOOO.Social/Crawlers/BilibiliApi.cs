using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SuperMOOO.Social.Crawlers;

/// <summary>Một video (hoặc một phần p=N của video nhiều phần) đã tra qua API chi tiết.</summary>
public sealed record BilibiliVideo(string Bvid, long Cid, int Page, string Title, string? Author, TimeSpan? Duration, string? Thumbnail, long? Views, string PageUrl);

/// <summary>API web Bilibili dùng chung cho crawler và tải: header (cookie khách buvid / SESSDATA), ký WBI, chi tiết video, playurl.</summary>
[Service]
public sealed partial class BilibiliApi(CrawlHttp http, PlatformCredentials credentials)
{
    public const string Referer = "https://www.bilibili.com/";

    // Bảng hoán vị khoá WBI cố định của web Bilibili (lấy từ JS trang chủ).
    private static readonly int[] MixinTable =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
    ];

    private sealed record Mixin(string Key, DateTime At);
    private volatile Mixin? _mixin;

    [GeneratedRegex(@"BV[0-9A-Za-z]{10}")] private static partial Regex BvPattern();
    [GeneratedRegex(@"(?:/|^)av(\d+)", RegexOptions.IgnoreCase)] private static partial Regex AvPattern();
    [GeneratedRegex(@"[?&]p=(\d+)")] private static partial Regex PagePattern();
    [GeneratedRegex(@"[!'()*]")] private static partial Regex WbiStrip();

    public async Task<Dictionary<string, string>> HeadersAsync(CancellationToken ct)
    {
        var headers = await credentials.HeadersAsync("bilibili", new Dictionary<string, string>
        {
            ["Referer"] = Referer, ["Accept"] = "application/json, text/plain, */*"
        }, ct);
        if (!headers.ContainsKey("Cookie"))
        {
            var sessdata = await credentials.GetAsync("bilibili", "sessdata", ct);
            headers["Cookie"] = (sessdata is null ? "" : $"SESSDATA={sessdata}; ") + await BuvidCookieAsync(ct);
        }

        return headers;
    }

    /// <summary>buvid3/buvid4 khách để vượt kiểm tra chống crawler (HTTP 412); lỗi thì dùng giá trị mặc định.</summary>
    private async Task<string> BuvidCookieAsync(CancellationToken ct)
    {
        try
        {
            var data = await http.GetJsonAsync("bilibili", "https://api.bilibili.com/x/frontend/finger/spi", timeout: TimeSpan.FromSeconds(5), ct: ct);
            return $"buvid3={JsonX.Str(data, "data", "b_3")}; buvid4={JsonX.Str(data, "data", "b_4")}";
        }
        catch (ProviderException)
        {
            return "buvid3=INFOC_BILIBILI_DEFAULT_V3; buvid4=INFOC_BILIBILI_DEFAULT_V4";
        }
    }

    /// <summary>Query đã ký WBI (wts + w_rid) cho các endpoint `/wbi/`; khoá trộn đổi hằng ngày nên giữ tối đa 1 giờ.</summary>
    public async Task<string> SignAsync(IEnumerable<KeyValuePair<string, string>> parameters, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var mixin = _mixin;
        if (mixin is null || DateTime.UtcNow - mixin.At > TimeSpan.FromHours(1))
        {
            var nav = await http.GetJsonAsync("bilibili", "https://api.bilibili.com/x/web-interface/nav", headers, ct: ct);
            static string Stem(string? url) => Path.GetFileNameWithoutExtension(new Uri(url ?? "https://x/").AbsolutePath);
            var raw = Stem(JsonX.Str(nav, "data", "wbi_img", "img_url")) + Stem(JsonX.Str(nav, "data", "wbi_img", "sub_url"));
            if (raw.Length < 64) throw new ProviderException("bilibili", ProviderErrorKind.BadResponse, "Không lấy được khoá ký WBI của Bilibili.");
            _mixin = mixin = new Mixin(new string(MixinTable.Select(i => raw[i]).ToArray())[..32], DateTime.UtcNow);
        }

        var query = JsonX.Query(parameters
            .Append(new("wts", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, string?>(p.Key, WbiStrip().Replace(p.Value, ""))));
        return $"{query}&w_rid={Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + mixin.Key)))}";
    }

    /// <summary>Liên kết video (bilibili.com/video/BV…|av…, ?p=N, b23.tv) → BV, cid của phần cần lấy, tiêu đề, thời lượng.</summary>
    public async Task<BilibiliVideo> VideoAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.EndsWith("b23.tv", StringComparison.OrdinalIgnoreCase))
            url = (await http.SendAsync("bilibili", HttpMethod.Get, url, headers, ct: ct)).FinalUri.ToString();

        var bv = BvPattern().Match(url);
        var av = AvPattern().Match(url);
        if (!bv.Success && !av.Success) throw new ProviderException("bilibili", ProviderErrorKind.BadResponse, "Liên kết Bilibili không có mã video (BV…/av…).");
        var id = bv.Success ? new KeyValuePair<string, string>("bvid", bv.Value) : new("aid", av.Groups[1].Value);

        var reply = await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/web-interface/wbi/view?{await SignAsync([id], headers, ct)}", headers, ct: ct);
        var data = Check(reply, "chi tiết video");
        var bvid = JsonX.Str(data, "bvid") ?? id.Value;
        var title = JsonX.Str(data, "title") ?? bvid;
        var pages = JsonX.Objects(data, "pages").ToList();
        var page = PagePattern().Match(url) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? Math.Clamp(n, 1, Math.Max(1, pages.Count)) : 1;
        var part = pages.FirstOrDefault(p => JsonX.Long(p, "page") == page) ?? pages.FirstOrDefault();
        var cid = JsonX.Long(part, "cid") ?? JsonX.Long(data, "cid")
            ?? throw new ProviderException("bilibili", ProviderErrorKind.BadResponse, "API Bilibili không trả mã phần video (cid).");
        if (pages.Count > 1) title = $"{title} - P{page} {JsonX.Str(part, "part")}".TrimEnd();
        var seconds = pages.Count > 1 ? JsonX.Long(part, "duration") : JsonX.Long(data, "duration");
        return new BilibiliVideo(bvid, cid, page, title, JsonX.Str(data, "owner", "name"), seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null,
            JsonX.Str(data, "pic"), JsonX.Long(data, "stat", "view"), $"https://www.bilibili.com/video/{bvid}/" + (pages.Count > 1 ? $"?p={page}" : ""));
    }

    /// <summary>Luồng phát của một phần video: fnval 1 = một file mp4 (durl), 4048 = DASH đủ codec/HDR/4K; qn là chất lượng tối đa muốn lấy.</summary>
    public async Task<JsonNode> PlayUrlAsync(BilibiliVideo video, int fnval, int qn, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var query = await SignAsync(
        [
            new("bvid", video.Bvid), new("cid", video.Cid.ToString(CultureInfo.InvariantCulture)), new("qn", qn.ToString(CultureInfo.InvariantCulture)),
            new("fnval", fnval.ToString(CultureInfo.InvariantCulture)), new("fnver", "0"), new("fourk", "1")
        ], headers, ct);
        return Check(await http.GetJsonAsync("bilibili", $"https://api.bilibili.com/x/player/wbi/playurl?{query}", headers, ct: ct), "luồng phát");
    }

    // Mã lỗi API: -404/62002/62012 không tồn tại/ẩn, 62004 đang duyệt, -403/-10403/87xxx cần đăng nhập/khu vực/trả phí.
    private static JsonNode Check(JsonNode reply, string what)
    {
        var code = JsonX.Long(reply, "code") ?? 0;
        if (code == 0 && reply["data"] is { } data) return data;
        var message = JsonX.Str(reply, "message") ?? "";
        var (kind, text) = code switch
        {
            -404 or 62002 or 62012 => (ProviderErrorKind.BadResponse, "video không tồn tại hoặc đã bị xoá/ẩn"),
            62004 => (ProviderErrorKind.BadResponse, "video đang chờ kiểm duyệt"),
            -101 or -403 or -10403 or (>= 87000 and < 88000) => (ProviderErrorKind.Forbidden, "video cần đăng nhập, trả phí hoặc bị chặn theo khu vực"),
            -412 or -352 => (ProviderErrorKind.RateLimited, "Bilibili tạm chặn chống bot, thử lại sau"),
            _ => (ProviderErrorKind.BadResponse, $"lỗi khi lấy {what}")
        };
        throw new ProviderException("bilibili", kind, $"Bilibili: {text} (mã {code}{(message.Length > 0 ? $", {message}" : "")}).");
    }
}
