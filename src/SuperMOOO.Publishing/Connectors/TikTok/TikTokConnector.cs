using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Connectors.MediaHost;

namespace SuperMOOO.Publishing.Connectors.TikTok;

[Service]
public sealed class TikTokConnectorOptions
{
    /// <summary>Người dùng xác nhận app đã qua audit Direct Post của TikTok. Chưa audit: Direct Post chỉ được SELF_ONLY.</summary>
    public bool DirectPostAudited { get; set; }
    /// <summary>Kích thước chunk mong muốn; được ép vào 5–64 MB theo quy định TikTok.</summary>
    public long ChunkSizeBytes { get; set; } = 10L * 1024 * 1024;
    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ChunkTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Hạn người dùng hoàn tất bài trong hộp thư TikTok (khảo sát: 24h; tài liệu chính thức chưa ghi rõ).</summary>
    public TimeSpan InboxCompletionWindow { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Thời lượng tối đa khi upload vào inbox (tài liệu media transfer: tối đa 10 phút).</summary>
    public int InboxMaxDurationSeconds { get; set; } = 600;
}

public static class TikTokPrivacy
{
    public const string PublicToEveryone = "PUBLIC_TO_EVERYONE";
    public const string MutualFollowFriends = "MUTUAL_FOLLOW_FRIENDS";
    public const string FollowerOfCreator = "FOLLOWER_OF_CREATOR";
    public const string SelfOnly = "SELF_ONLY";
}

/// <summary>
/// Lựa chọn người dùng nhập trên màn hình "Post to TikTok" (bắt buộc theo content-sharing-guidelines). Không có giá trị mặc định cho privacy.
/// </summary>
/// <param name="PrivacyLevel">Một trong privacy_level_options của creator_info; người dùng tự chọn, không chọn sẵn.</param>
/// <param name="CommercialContent">Bật công tắc "Disclose video content".</param>
/// <param name="YourBrand">"Your brand" → nhãn Promotional content (brand_organic_toggle).</param>
/// <param name="BrandedContent">"Branded content" → nhãn Paid partnership (brand_content_toggle); không được SELF_ONLY.</param>
/// <param name="UserConsented">Người dùng đã bấm đăng sau khi thấy tuyên bố Music Usage Confirmation (+ Branded Content Policy nếu có).</param>
public sealed record TikTokDirectPostSettings(
    string PrivacyLevel, bool AllowComment, bool AllowDuet, bool AllowStitch, bool CommercialContent, bool YourBrand, bool BrandedContent,
    bool UserConsented, long? VideoCoverTimestampMs = null);

/// <summary>Tầng ứng dụng trả lựa chọn Direct Post của publication; null = gửi vào hộp thư (inbox) để người dùng hoàn tất trong app TikTok.</summary>
public interface ITikTokPostSettingsProvider
{
    Task<TikTokDirectPostSettings?> GetAsync(string connectionId, PublishVariant variant, CancellationToken ct);
}

public sealed record TikTokCreatorInfo(
    string? Nickname, string? Username, string? AvatarUrl, IReadOnlyList<string> PrivacyLevelOptions, bool CommentDisabled, bool DuetDisabled,
    bool StitchDisabled, int? MaxVideoPostDurationSec);

/// <summary>Chia chunk theo quy định TikTok: chunk 5–64 MB, số chunk = floor(size/chunk) (1..1000), chunk cuối gánh phần dư (≤ 128 MB); file &lt; 5 MB gửi 1 chunk.</summary>
public sealed record TikTokChunkPlan(long VideoSize, long ChunkSize, int ChunkCount)
{
    public const long MinChunk = 5L * 1024 * 1024;
    public const long MaxChunk = 64L * 1024 * 1024;
    public const long MaxVideoBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxChunks = 1000;

    public static TikTokChunkPlan For(long size, long preferredChunk)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (size < MinChunk) return new TikTokChunkPlan(size, size, 1);
        var chunk = Math.Clamp(preferredChunk, MinChunk, MaxChunk);
        if (size / chunk > MaxChunks) chunk = Math.Min(MaxChunk, (size + MaxChunks - 1) / MaxChunks);
        if (chunk > size) chunk = size;
        return new TikTokChunkPlan(size, chunk, (int)Math.Max(1, size / chunk));
    }

    public long Offset(int index) => index * ChunkSize;
    public long Length(int index) => index == ChunkCount - 1 ? VideoSize - Offset(index) : ChunkSize;
    /// <summary>Chunk chứa byte đầu tiên chưa được xác nhận (làm tròn xuống ranh giới chunk).</summary>
    public int IndexOf(long confirmedBytes) => (int)Math.Min(ChunkCount - 1, Math.Max(0, confirmedBytes) / ChunkSize);
}

/// <summary>
/// TikTok Content Posting API. Hai chế độ:
/// Inbox (video.upload): FILE_UPLOAD theo chunk → TikTok gửi thông báo, người dùng tự hoàn tất trong app (trạng thái "chờ người dùng", hạn 24h; không phải Published).
/// Direct Post (video.publish): chỉ khi có <see cref="ITikTokPostSettingsProvider"/> trả lựa chọn; gọi creator_info trước; chưa audit → chỉ SELF_ONLY.
/// Remote id = publish_id. https://developers.tiktok.com/doc/content-posting-api-get-started
/// </summary>
public sealed partial class TikTokConnector(TikTokConnectorOptions options, ITikTokPostSettingsProvider? settings = null, TimeProvider? time = null,
    IPublicMediaHost? mediaHost = null)
    : ISocialConnector
{
    /// <summary>RemoteStatus.PrivacyStatus khi video đã vào hộp thư, chờ người dùng hoàn tất trong app TikTok.</summary>
    public const string AwaitingUser = "awaiting_user";
    public const int MaxTitleChars = 2200;
    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".webm"] = "video/webm"
    };
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Platform => nameof(SocialPlatform.TikTok);

    public IReadOnlyList<CapabilityInfo> GetCapabilities(string? grantedScopes)
    {
        var scopes = PlatformHttp.ParseScopes(grantedScopes);
        var canUpload = scopes.Contains(TikTokOAuthProvider.VideoUpload);
        var canPublish = scopes.Contains(TikTokOAuthProvider.VideoPublish) && settings is not null;
        var upload = canUpload || canPublish ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission;
        string note;
        if (canPublish && options.DirectPostAudited) note = "Direct Post (đã audit) hoặc gửi vào hộp thư TikTok.";
        else if (canPublish) note = "Chưa audit Direct Post: chỉ đăng được SELF_ONLY (riêng tư), tối đa 5 người dùng/24h; tài khoản phải để riêng tư.";
        else if (canUpload) note = "Gửi vào hộp thư TikTok: người dùng mở thông báo trong app để hoàn tất trong 24h (tối đa 5 bài chờ/24h).";
        else note = "Thiếu quyền video.upload / video.publish.";
        return
        [
            new(Capability.UploadVideo, canPublish || !canUpload ? upload : CapabilityStatus.RequiresManualStep, note),
            new(Capability.UploadImages, mediaHost is null ? CapabilityStatus.RequiresManualStep : upload,
                "Tối đa 35 ảnh JPEG/WebP; cần media host HTTPS và xác minh domain/prefix URL trong app TikTok."),
            new(Capability.ResumableUpload, upload, "Chunk 5–64 MB; tiếp tục từ uploaded_bytes trong 1 giờ hiệu lực của upload_url."),
            new(Capability.ReadPublishStatus, upload),
            new(Capability.ScheduleNative, CapabilityStatus.NotSupported, "API không hẹn giờ: app tự lên lịch."),
            new(Capability.ReconcilePost, CapabilityStatus.NotSupported, "Không liệt kê video (cần video.list); publish_id được lưu trước khi gửi byte."),
            new(Capability.UpdateChannelInfo, CapabilityStatus.NotSupported, null),
            new(Capability.CreateChannel, CapabilityStatus.NotSupported, "Tạo tài khoản trong app TikTok rồi kết nối."),
            new(Capability.ReadAnalytics, scopes.Contains(TikTokOAuthProvider.VideoList) ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission,
                "View/like/comment/share từng video (video.list) và follower (user.info.stats). Bật thống kê TikTok trong Cài đặt rồi kết nối lại để xin quyền.")
        ];
    }

    public IReadOnlyList<ValidationIssue> Validate(PublishVariant v, MediaFile media, DestinationInfo? destination)
    {
        if (media.IsImagePost) return ValidateImages(v, media);
        var issues = new List<ValidationIssue>();
        if (!Mimes.ContainsKey(Path.GetExtension(media.Path))) issues.Add(new("FORMAT", "TikTok nhận MP4 (khuyên dùng), MOV hoặc WebM."));
        if (!File.Exists(media.Path)) issues.Add(new("FILE_MISSING", "Không tìm thấy file video trong kho media."));
        if (media.SizeBytes <= 0) issues.Add(new("FILE_EMPTY", "File video rỗng."));
        if (media.SizeBytes > TikTokChunkPlan.MaxVideoBytes) issues.Add(new("FILE_TOO_LARGE", "TikTok nhận video tối đa 4 GB."));
        if (PlatformText.Caption(v).Length > MaxTitleChars) issues.Add(new("TITLE_TOO_LONG", $"Caption TikTok tối đa {MaxTitleChars} ký tự."));
        if (v.PublishAtUtc is not null) issues.Add(new("NATIVE_SCHEDULE_UNSUPPORTED", "TikTok API không hẹn giờ: để app lên lịch."));
        return issues;
    }

    public async Task<IReadOnlyList<DestinationInfo>> ListDestinationsAsync(ConnectorSession session, CancellationToken ct)
    {
        var data = await TikTokApi.GetAsync(session, "/user/info/?fields=open_id,display_name,avatar_url", options.ApiTimeout, ct);
        var user = data["user"];
        var openId = ConnectorHttp.Str(user?["open_id"]) ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "TikTok không trả open_id.");
        return [new DestinationInfo(openId, ConnectorHttp.Str(user?["display_name"]) ?? "TikTok", null, null, new Dictionary<string, string>())];
    }

    /// <summary>creator_info/query (bắt buộc gọi mỗi lần hiển thị màn hình đăng Direct Post, và ngay trước init).</summary>
    public async Task<TikTokCreatorInfo> QueryCreatorInfoAsync(ConnectorSession session, CancellationToken ct)
    {
        var data = await TikTokApi.PostJsonAsync(session, "/post/publish/creator_info/query/", new JsonObject(), options.ApiTimeout, ct);
        return new TikTokCreatorInfo(
            ConnectorHttp.Str(data["creator_nickname"]), ConnectorHttp.Str(data["creator_username"]), ConnectorHttp.Str(data["creator_avatar_url"]),
            (data["privacy_level_options"]?.AsArray() ?? []).Select(ConnectorHttp.Str).OfType<string>().ToList(),
            PlatformHttp.Bool(data["comment_disabled"]) == true, PlatformHttp.Bool(data["duet_disabled"]) == true,
            PlatformHttp.Bool(data["stitch_disabled"]) == true, (int?)PlatformHttp.Long(data["max_video_post_duration_sec"]));
    }

    public async Task<UploadOutcome> UploadAsync(ConnectorSession session, PublishVariant variant, MediaFile media, UploadState state,
        IUploadCheckpoint checkpoint, CancellationToken ct)
    {
        if (media.IsImagePost) return await UploadImagesAsync(session, variant, media, state, checkpoint, ct);
        var mime = Mimes.GetValueOrDefault(Path.GetExtension(media.Path)) ?? "video/mp4";
        var plan = TikTokChunkPlan.For(media.SizeBytes, options.ChunkSizeBytes);
        var (publishId, uploadUrl) = ParseSession(state.SessionUri);
        var finalSent = state.FinalChunkSent;
        var index = 0;

        if (publishId is not null)
        {
            var status = await FetchAsync(session, publishId, ct);
            switch (status.Status)
            {
                case "PROCESSING_UPLOAD" when status.UploadedBytes < plan.VideoSize:
                    index = plan.IndexOf(status.UploadedBytes ?? state.BytesConfirmed);
                    break;
                case "FAILED" when !finalSent:
                    await checkpoint.ClearSessionAsync(ct);
                    publishId = null;
                    break;
                default:
                    // Đã nhận đủ byte (đang xử lý / đã vào inbox / đã đăng / thất bại sau khi nhận đủ): kết quả nằm ở status, không upload lại.
                    return new UploadOutcome.Completed(publishId);
            }
            if (publishId is not null && uploadUrl is null)
            {
                // Không còn upload_url (checkpoint cũ): chưa gửi chunk cuối thì tạo phiên mới.
                if (finalSent) return new UploadOutcome.Ambiguous("Thiếu địa chỉ upload TikTok sau khi đã gửi chunk cuối; kiểm tra hộp thư TikTok.");
                await checkpoint.ClearSessionAsync(ct);
                publishId = null;
            }
        }

        for (var restart = 0; restart < 2; restart++)
        {
            if (publishId is null)
            {
                var body = await BuildInitAsync(session, variant, media, plan, ct);
                await checkpoint.BeforeCreateSessionAsync(ct);
                var data = await TikTokApi.PostJsonAsync(session, body.Path, body.Json, options.ApiTimeout, ct);
                publishId = ConnectorHttp.Str(data["publish_id"]) ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "TikTok không trả publish_id.");
                uploadUrl = ConnectorHttp.Str(data["upload_url"]);
                if (!ConnectorHttp.IsHttpsHost(uploadUrl, "tiktokapis.com"))
                    throw new ConnectorException(ConnectorErrorKind.BadResponse, "TikTok trả địa chỉ upload không hợp lệ.");
                // Lưu publish_id + upload_url trước byte đầu tiên.
                await checkpoint.SaveSessionAsync(publishId + " " + uploadUrl, plan.VideoSize, ct);
                index = 0;
                finalSent = false;
            }

            var expired = false;
            for (; index < plan.ChunkCount; index++)
            {
                var isFinal = index == plan.ChunkCount - 1;
                // Chunk cuối là bước đăng (Direct Post tự đăng khi nhận đủ byte): kiểm lại IP trước, cả khi đang gửi lại chunk cuối.
                if (isFinal) await checkpoint.VerifyRouteAsync(ct);
                if (isFinal && !finalSent)
                {
                    await checkpoint.MarkFinalChunkSendingAsync(ct);
                    finalSent = true;
                }

                int code;
                try
                {
                    code = await PutChunkAsync(session.Http, uploadUrl!, media.Path, mime, plan, index, ct);
                }
                catch (ConnectorException e) when (isFinal && e.IsRetryable)
                {
                    var probe = await FetchAsync(session, publishId, ct);
                    if (probe.Status is "PROCESSING_UPLOAD" or "FAILED" && probe.UploadedBytes < plan.VideoSize) throw;
                    return new UploadOutcome.Completed(publishId);
                }

                if (code == 201) return new UploadOutcome.Completed(publishId);
                if (code == 206)
                {
                    await checkpoint.SaveProgressAsync(plan.Offset(index) + plan.Length(index), ct);
                    continue;
                }
                if (code == 416)
                {
                    // Content-Range lệch tiến độ thật: đồng bộ lại theo uploaded_bytes.
                    var probe = await FetchAsync(session, publishId, ct);
                    if (probe.UploadedBytes is not { } uploaded || uploaded >= plan.VideoSize) return new UploadOutcome.Completed(publishId);
                    index = plan.IndexOf(uploaded) - 1;
                    continue;
                }
                // 403/404: upload_url hết hạn (1 giờ) hoặc phiên không còn.
                if (finalSent)
                {
                    var probe = await FetchAsync(session, publishId, ct);
                    if (probe.Status is not ("PROCESSING_UPLOAD" or "FAILED")) return new UploadOutcome.Completed(publishId);
                    return new UploadOutcome.Ambiguous("Phiên upload TikTok hết hạn quanh chunk cuối; kiểm tra hộp thư TikTok trước khi gửi lại.");
                }
                expired = true;
                break;
            }

            if (!expired) return new UploadOutcome.Completed(publishId!);
            await checkpoint.ClearSessionAsync(ct);
            publishId = null;
        }
        throw new ConnectorException(ConnectorErrorKind.Transient, "Phiên upload TikTok liên tục hết hạn; thử lại sau.");
    }

    public Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, CancellationToken ct) =>
        GetStatusAsync(session, remoteId, null, ct);

    /// <param name="uploadCompletedUtc">Thời điểm upload xong (tầng ứng dụng lưu): dùng tính hạn 24h của bài chờ trong hộp thư.</param>
    public async Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, DateTimeOffset? uploadCompletedUtc, CancellationToken ct)
    {
        TikTokStatus s;
        try { s = await FetchAsync(session, remoteId, ct); }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.NotFound)
        {
            return new RemoteStatus(RemoteState.NotFound, null, "TikTok không còn bản ghi publish_id này.");
        }
        switch (s.Status)
        {
            case "SEND_TO_USER_INBOX":
                var deadline = uploadCompletedUtc + options.InboxCompletionWindow;
                if (deadline is { } d && _time.GetUtcNow() >= d)
                    return new RemoteStatus(RemoteState.Failed, AwaitingUser, "Quá 24 giờ mà người dùng chưa hoàn tất bài trong hộp thư TikTok.");
                return new RemoteStatus(RemoteState.Processing, AwaitingUser,
                    "Chờ bạn mở thông báo trong app TikTok để hoàn tất bài đăng"
                    + (deadline is { } dl ? $" (hạn {dl.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC)." : "."));
            case "PUBLISH_COMPLETE":
                return new RemoteStatus(RemoteState.Ready, "published", s.PostIds.Count > 0
                    ? "post_id=" + string.Join(',', s.PostIds)
                    : "Đã đăng; TikTok chưa công khai post id (đang kiểm duyệt hoặc bài riêng tư).");
            case "FAILED":
                return new RemoteStatus(RemoteState.Failed, null, "TikTok xử lý thất bại: " + TikTokApi.FailReason(s.FailReason));
            default:
                return new RemoteStatus(RemoteState.Processing, null, s.Status);
        }
    }

    /// <remarks>
    /// Byte chỉ được gửi sau khi publish_id đã lưu bền, nên không có publish_id nghĩa là TikTok chưa nhận file (không có bài nào).
    /// Liệt kê video cần scope video.list (chưa xin) — không đoán.
    /// </remarks>
    public Task<ReconcileResult> ReconcileAsync(ConnectorSession session, DestinationInfo destination, ReconcileQuery query, CancellationToken ct) =>
        Task.FromResult<ReconcileResult>(new ReconcileResult.NotFound());

    [GeneratedRegex(@"^v_(pub|inbox)_[A-Za-z0-9_~.\-]{1,60}$")]
    private static partial Regex PublishIdPattern();

    /// <summary>Chỉ nhận publish_id; URL video tiktok.com/@user/video/{id} là post id, không dùng hỏi trạng thái được.</summary>
    public string? ParseRemoteId(string url) => PublishIdPattern().IsMatch(url.Trim()) ? url.Trim() : null;

    public string RemoteUrl(string remoteId) => "https://www.tiktok.com/";

    // --- HTTP ---

    public sealed record TikTokStatus(string? Status, string? FailReason, long? UploadedBytes, IReadOnlyList<string> PostIds);

    public async Task<TikTokStatus> FetchAsync(ConnectorSession session, string publishId, CancellationToken ct)
    {
        var data = await TikTokApi.PostJsonAsync(session, "/post/publish/status/fetch/", new JsonObject { ["publish_id"] = publishId },
            options.ApiTimeout, ct);
        // Tài liệu viết "publicaly_available_post_id" (sai chính tả); nhận cả hai.
        var ids = (data["publicaly_available_post_id"] ?? data["publicly_available_post_id"])?.AsArray()
            .Select(n => PlatformHttp.Long(n)?.ToString(CultureInfo.InvariantCulture) ?? ConnectorHttp.Str(n)).OfType<string>().ToList() ?? [];
        return new TikTokStatus(ConnectorHttp.Str(data["status"]), ConnectorHttp.Str(data["fail_reason"]), PlatformHttp.Long(data["uploaded_bytes"]), ids);
    }

    private async Task<(string Path, JsonObject Json)> BuildInitAsync(ConnectorSession session, PublishVariant v, MediaFile media, TikTokChunkPlan plan,
        CancellationToken ct)
    {
        var source = new JsonObject
        {
            ["source"] = "FILE_UPLOAD", ["video_size"] = plan.VideoSize, ["chunk_size"] = plan.ChunkSize, ["total_chunk_count"] = plan.ChunkCount
        };
        var direct = settings is null ? null : await settings.GetAsync(session.ConnectionId, v, ct);
        if (direct is null)
        {
            if (v.DurationMs is { } ms && ms > options.InboxMaxDurationSeconds * 1000L)
                throw new ConnectorException(ConnectorErrorKind.Validation, $"Video dài hơn {options.InboxMaxDurationSeconds / 60} phút: TikTok không nhận qua API.");
            return ("/post/publish/inbox/video/init/", new JsonObject { ["source_info"] = source });
        }

        CheckDirectPost(direct);
        var creator = await QueryCreatorInfoAsync(session, ct);
        if (!creator.PrivacyLevelOptions.Contains(direct.PrivacyLevel))
            throw new ConnectorException(ConnectorErrorKind.Validation,
                $"Quyền riêng tư {direct.PrivacyLevel} không có trong lựa chọn của tài khoản TikTok ({string.Join(", ", creator.PrivacyLevelOptions)}).",
                reason: "privacy_level_option_mismatch");
        if (creator.MaxVideoPostDurationSec is { } max && v.DurationMs is { } duration && duration > max * 1000L)
            throw new ConnectorException(ConnectorErrorKind.Validation, $"Video dài hơn {max} giây, vượt giới hạn của tài khoản TikTok.", reason: "duration");

        var post = new JsonObject
        {
            ["title"] = PlatformText.Caption(v),
            ["privacy_level"] = direct.PrivacyLevel,
            // Tương tác bị tắt trong cài đặt của creator thì luôn tắt, bất kể lựa chọn.
            ["disable_comment"] = !direct.AllowComment || creator.CommentDisabled,
            ["disable_duet"] = !direct.AllowDuet || creator.DuetDisabled,
            ["disable_stitch"] = !direct.AllowStitch || creator.StitchDisabled,
            ["brand_content_toggle"] = direct.CommercialContent && direct.BrandedContent,
            ["brand_organic_toggle"] = direct.CommercialContent && direct.YourBrand,
            ["is_aigc"] = v.ContainsSyntheticMedia
        };
        if (direct.VideoCoverTimestampMs is { } cover) post["video_cover_timestamp_ms"] = cover;
        return ("/post/publish/video/init/", new JsonObject { ["post_info"] = post, ["source_info"] = source });
    }

    /// <summary>Chặn trước mọi lời gọi mạng: lựa chọn vi phạm điều kiện audit/UX của TikTok.</summary>
    private void CheckDirectPost(TikTokDirectPostSettings s)
    {
        if (!options.DirectPostAudited && s.PrivacyLevel != TikTokPrivacy.SelfOnly)
            throw new ConnectorException(ConnectorErrorKind.Policy,
                "App TikTok chưa qua audit Direct Post: chỉ được đăng chế độ Chỉ mình tôi (SELF_ONLY). Đánh dấu đã audit trong Cài đặt sau khi TikTok duyệt.",
                reason: "unaudited_client_can_only_post_to_private_accounts");
        if (!s.UserConsented)
            throw new ConnectorException(ConnectorErrorKind.Validation, "Người dùng chưa xác nhận đồng ý đăng (Music Usage Confirmation).", reason: "consent");
        if (s.CommercialContent && !s.YourBrand && !s.BrandedContent)
            throw new ConnectorException(ConnectorErrorKind.Validation, "Đã bật công bố nội dung thương mại: chọn Your brand và/hoặc Branded content.",
                reason: "commercial_content");
        if (s.CommercialContent && s.BrandedContent && s.PrivacyLevel == TikTokPrivacy.SelfOnly)
            throw new ConnectorException(ConnectorErrorKind.Validation, "Branded content không được để chế độ Chỉ mình tôi.", reason: "branded_private");
    }

    /// <returns>HTTP 201 (xong), 206 (còn chunk), 416 (lệch tiến độ), 403/404 (phiên hết hạn).</returns>
    private async Task<int> PutChunkAsync(HttpClient http, string uploadUrl, string path, string mime, TikTokChunkPlan plan, int index,
        CancellationToken ct)
    {
        var offset = plan.Offset(index);
        var length = plan.Length(index);
        var content = new FileSliceContent(path, offset, length, mime);
        content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, plan.VideoSize);
        // upload_url đã có chữ ký trong query: không gửi Authorization, không đưa URL vào thông báo lỗi.
        var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
        using var response = await PlatformHttp.SendOnceAsync(http, request, options.ChunkTimeout, TikTokApi.Name, ct);
        var code = (int)response.StatusCode;
        if (code is 201 or 206 or 416 or 403 or 404) return code;
        if (code == 200) return index == plan.ChunkCount - 1 ? 201 : 206;
        throw TikTokApi.MapError(response, await ConnectorHttp.ReadJsonAsync(response, ct));
    }

    private static (string? PublishId, string? UploadUrl) ParseSession(string? session)
    {
        if (string.IsNullOrEmpty(session)) return (null, null);
        var space = session.IndexOf(' ');
        return space > 0 ? (session[..space], session[(space + 1)..]) : (session, null);
    }
}
