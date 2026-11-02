using System.Text;
using System.Text.Json.Nodes;

namespace SuperMOOO.Publishing.Connectors.TikTok;

/// <summary>Open API v2 của TikTok: phản hồi bọc trong {data, error{code,message,log_id}}; error.code khác "ok" là lỗi.</summary>
public static class TikTokApi
{
    public const string Base = "https://open.tiktokapis.com/v2";
    public const string Name = "TikTok";

    /// <summary>Trần ngày (đăng/inbox chờ) là cửa sổ 24h trượt: chờ tối đa 1 ngày là chắc chắn có suất lại.</summary>
    public static TimeSpan DailyCapRetryAfter { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Giới hạn theo phút cho mỗi user token (init 6/phút, status 30/phút, creator_info 20/phút).</summary>
    public static TimeSpan MinuteRateRetryAfter { get; set; } = TimeSpan.FromMinutes(1);

    public static async Task<JsonNode> PostJsonAsync(ConnectorSession session, string path, JsonObject body, TimeSpan timeout, CancellationToken ct)
    {
        var payload = body.ToJsonString();
        HttpRequestMessage Build(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Base + path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = ConnectorHttp.Bearer(token);
            return request;
        }

        var (data, error) = await TryAsync(session.Http, Build(await session.Tokens.GetAsync(ct)), timeout, ct);
        if (error is null) return data!;
        if (error.Kind != ConnectorErrorKind.Auth) throw error;
        (data, error) = await TryAsync(session.Http, Build(await session.Tokens.RefreshAsync(ct)), timeout, ct);
        if (error is null) return data!;
        if (error.Kind == ConnectorErrorKind.Auth)
            throw new ConnectorException(ConnectorErrorKind.Auth, error.Message + " Token bị từ chối kể cả sau khi làm mới; cần kết nối lại.",
                null, error.HttpStatus, error.Reason);
        throw error;
    }

    public static async Task<JsonNode> GetAsync(ConnectorSession session, string pathAndQuery, TimeSpan timeout, CancellationToken ct)
    {
        HttpRequestMessage Build(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Base + pathAndQuery);
            request.Headers.Authorization = ConnectorHttp.Bearer(token);
            return request;
        }

        var (data, error) = await TryAsync(session.Http, Build(await session.Tokens.GetAsync(ct)), timeout, ct);
        if (error is null) return data!;
        if (error.Kind != ConnectorErrorKind.Auth) throw error;
        (data, error) = await TryAsync(session.Http, Build(await session.Tokens.RefreshAsync(ct)), timeout, ct);
        return error is null ? data! : throw error;
    }

    private static async Task<(JsonNode? Data, ConnectorException? Error)> TryAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout,
        CancellationToken ct)
    {
        using var response = await PlatformHttp.SendOnceAsync(http, request, timeout, Name, ct);
        var json = await ConnectorHttp.ReadJsonAsync(response, ct);
        var code = ConnectorHttp.Str(json?["error"]?["code"]);
        // creator_info trả lỗi spam_risk_* với HTTP 200: phải xét error.code, không chỉ status.
        if (response.IsSuccessStatusCode && code is null or "ok") return (json?["data"] ?? new JsonObject(), null);
        return (null, MapError(response, json));
    }

    /// <summary>
    /// Mã lỗi theo tài liệu Content Posting API (direct post, upload inbox, status fetch, creator_info).
    /// </summary>
    public static ConnectorException MapError(HttpResponseMessage response, JsonNode? json)
    {
        var status = (int)response.StatusCode;
        var code = ConnectorHttp.Str(json?["error"]?["code"]);
        var detail = PlatformHttp.Truncate(ConnectorHttp.Str(json?["error"]?["message"]), 200);
        var message = $"TikTok trả HTTP {status}{(code is null ? "" : " (" + code + ")")}{(detail.Length == 0 ? "" : ": " + detail)}";
        var (kind, retry) = code switch
        {
            "access_token_invalid" => (ConnectorErrorKind.Auth, (TimeSpan?)null),
            "scope_not_authorized" or "token_not_authorized_for_specified_publish_id" or "url_ownership_unverified"
                => (ConnectorErrorKind.Permission, null),
            "rate_limit_exceeded" => (ConnectorErrorKind.RateLimited, ConnectorHttp.RetryAfter(response) ?? MinuteRateRetryAfter),
            "spam_risk_too_many_posts" or "spam_risk_too_many_pending_share" or "reached_active_user_cap"
                => (ConnectorErrorKind.QuotaExceeded, DailyCapRetryAfter),
            "spam_risk_user_banned_from_posting" or "unaudited_client_can_only_post_to_private_accounts" or "spam_risk" or "spam_risk_text"
                => (ConnectorErrorKind.Policy, null),
            "privacy_level_option_mismatch" or "invalid_param" or "invalid_params" => (ConnectorErrorKind.Validation, null),
            "invalid_publish_id" => (ConnectorErrorKind.NotFound, null),
            "internal_error" => (ConnectorErrorKind.Transient, ConnectorHttp.RetryAfter(response)),
            _ => status switch
            {
                401 => (ConnectorErrorKind.Auth, null),
                403 => (ConnectorErrorKind.Permission, null),
                404 => (ConnectorErrorKind.NotFound, null),
                429 => (ConnectorErrorKind.RateLimited, ConnectorHttp.RetryAfter(response) ?? MinuteRateRetryAfter),
                >= 500 => (ConnectorErrorKind.Transient, ConnectorHttp.RetryAfter(response)),
                400 => (ConnectorErrorKind.Validation, null),
                _ => (ConnectorErrorKind.BadResponse, null)
            }
        };
        return new ConnectorException(kind, message, retry, status, code);
    }

    /// <summary>fail_reason của status/fetch → mô tả cho người dùng.</summary>
    public static string FailReason(string? reason) => reason switch
    {
        "file_format_check_failed" => "định dạng file không được hỗ trợ",
        "duration_check_failed" => "thời lượng video ngoài giới hạn",
        "frame_rate_check_failed" => "frame rate không được hỗ trợ",
        "picture_size_check_failed" => "kích thước khung hình không được hỗ trợ",
        "internal" => "lỗi máy chủ TikTok (có thể thử lại)",
        "auth_removed" => "người dùng đã thu hồi quyền trong lúc xử lý",
        "spam_risk_too_many_posts" => "vượt số bài đăng qua API trong 24 giờ",
        "spam_risk_user_banned_from_posting" => "tài khoản bị cấm đăng bài",
        "spam_risk_text" => "caption bị đánh giá rủi ro spam",
        "spam_risk" => "yêu cầu bị đánh giá rủi ro",
        "publish_cancelled" => "đã hủy",
        null or "" => "không rõ lý do",
        _ => reason
    };
}
