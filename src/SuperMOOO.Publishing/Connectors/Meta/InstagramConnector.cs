using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SuperMOOO.Publishing.Connectors.MediaHost;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.Meta;

[Service]
public sealed class InstagramConnectorOptions
{
    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Tài liệu khuyên hỏi trạng thái container khoảng 1 lần/phút, không quá 5 phút mỗi đợt.</summary>
    public TimeSpan ContainerPollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int ContainerMaxPolls { get; set; } = 10;
    /// <summary>Hạn URL công khai: chỉ cần đủ để Meta tải về khi xử lý container.</summary>
    public TimeSpan MediaUrlLifetime { get; set; } = TimeSpan.FromHours(2);
    /// <summary>Dùng khi API không trả config.quota_total (tài liệu ghi 50, chỗ khác ghi 100).</summary>
    public int FallbackPublishLimit { get; set; } = 50;
    public bool ShareToFeed { get; set; } = true;
    public TimeSpan ReconcileSlack { get; set; } = TimeSpan.FromMinutes(10);
    public const long MaxFileBytes = 300L * 1024 * 1024;
    public const int MaxCaptionChars = 2200;
    public const int MaxHashtags = 30;
}

/// <summary>
/// Instagram Reels qua Instagram API with Facebook Login (graph.facebook.com): đưa file lên <see cref="IPublicMediaHost"/> →
/// tạo container REELS với video_url → chờ status_code FINISHED → media_publish. Container hết hạn sau 24h nên tạo sát giờ đăng.
/// Token của phiên: Page token của Page liên kết tài khoản Instagram business/creator. Remote id = IG media id.
/// https://developers.facebook.com/docs/instagram-platform/content-publishing
/// </summary>
public sealed partial class InstagramConnector(InstagramConnectorOptions options, IPublicMediaHost? mediaHost = null, TimeProvider? time = null)
    : ISocialConnector
{
    public const string Name = "Instagram";
    public const string MissingHostMessage =
        "Instagram nhận media qua URL HTTPS công khai: cấu hình media host (S3 / Cloudflare R2 / MinIO) trong Cài đặt trước khi đăng.";
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov" };
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Platform => nameof(SocialPlatform.Instagram);
    public bool HasMediaHost => mediaHost is not null;

    public IReadOnlyList<CapabilityInfo> GetCapabilities(string? grantedScopes)
    {
        var scopes = PlatformHttp.ParseScopes(grantedScopes);
        var read = scopes.Contains(MetaOAuthProvider.InstagramBasic) && scopes.Contains(MetaOAuthProvider.PagesReadEngagement)
            ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission;
        var upload = read == CapabilityStatus.Supported && scopes.Contains(MetaOAuthProvider.InstagramContentPublish)
            ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission;
        string? uploadNote = "Reels 3s–15 phút, ≤ 300 MB; trước App Review chỉ tài khoản có vai trò trong app đăng được.";
        if (upload == CapabilityStatus.Supported && mediaHost is null) { upload = CapabilityStatus.RequiresManualStep; uploadNote = MissingHostMessage; }
        return
        [
            new(Capability.UploadVideo, upload, uploadNote),
            new(Capability.UploadImages, upload, "Ảnh JPEG, tối đa 8 MB/ảnh; carousel tối đa 10 ảnh. Cần media host."),
            new(Capability.ResumableUpload, CapabilityStatus.NotSupported, "Instagram tự tải video từ URL công khai."),
            new(Capability.ReadPublishStatus, read),
            new(Capability.ScheduleNative, CapabilityStatus.NotSupported, "API không hẹn giờ; app tạo container sát giờ đăng (hết hạn 24h)."),
            new(Capability.ReconcilePost, read, "Liệt kê media gần đây (khớp caption + thời gian)."),
            new(Capability.UpdateChannelInfo, CapabilityStatus.NotSupported, "Chưa triển khai."),
            new(Capability.CreateChannel, CapabilityStatus.NotSupported, "Tạo tài khoản Instagram business/creator và liên kết với Page."),
            new(Capability.ReadAnalytics, scopes.Contains(MetaOAuthProvider.InstagramManageInsights) && read == CapabilityStatus.Supported
                    ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission,
                "Views/reach/tương tác của Reels (media insights, trễ tới 48 giờ) và người theo dõi. Cần quyền instagram_manage_insights: bật thống kê Meta trong Cài đặt rồi kết nối lại.")
        ];
    }

    public IReadOnlyList<ValidationIssue> Validate(PublishVariant v, MediaFile media, DestinationInfo? destination)
    {
        if (media.IsImagePost) return ValidateImages(v, media);
        var issues = new List<ValidationIssue>();
        if (mediaHost is null) issues.Add(new("MEDIA_HOST_MISSING", MissingHostMessage));
        if (!Extensions.Contains(Path.GetExtension(media.Path))) issues.Add(new("FORMAT", "Instagram Reels nhận .mp4 hoặc .mov."));
        if (!File.Exists(media.Path)) issues.Add(new("FILE_MISSING", "Không tìm thấy file video trong kho media."));
        if (media.SizeBytes <= 0) issues.Add(new("FILE_EMPTY", "File video rỗng."));
        if (media.SizeBytes > InstagramConnectorOptions.MaxFileBytes) issues.Add(new("FILE_TOO_LARGE", "Instagram Reels tối đa 300 MB."));
        if (v.DurationMs is { } ms && (ms < 3_000 || ms > 15 * 60_000)) issues.Add(new("DURATION", "Instagram Reels phải dài 3 giây đến 15 phút."));
        var caption = PlatformText.Caption(v);
        if (caption.Length > InstagramConnectorOptions.MaxCaptionChars)
            issues.Add(new("CAPTION_TOO_LONG", $"Caption dài {caption.Length} ký tự (tối đa {InstagramConnectorOptions.MaxCaptionChars})."));
        if (PlatformText.HashtagCount(caption) > InstagramConnectorOptions.MaxHashtags) issues.Add(new("HASHTAGS_TOO_MANY", "Instagram cho tối đa 30 hashtag."));
        if (v.Privacy != PublishPrivacy.Public) issues.Add(new("PRIVACY_UNSUPPORTED", "Bài Instagram luôn công khai theo tài khoản."));
        if (v.PublishAtUtc is not null) issues.Add(new("NATIVE_SCHEDULE_UNSUPPORTED", "Instagram API không hẹn giờ: để app lên lịch."));
        return issues;
    }

    public async Task<IReadOnlyList<DestinationInfo>> ListDestinationsAsync(ConnectorSession session, CancellationToken ct)
    {
        var result = new List<DestinationInfo>();
        foreach (var (id, username) in await ListAccountsAsync(session, ct))
            result.Add(new DestinationInfo(id, username ?? id, username is null ? null : "https://www.instagram.com/" + username, null,
                new Dictionary<string, string>()));
        return result;
    }

    public async Task<UploadOutcome> UploadAsync(ConnectorSession session, PublishVariant variant, MediaFile media, UploadState state,
        IUploadCheckpoint checkpoint, CancellationToken ct)
    {
        if (mediaHost is null) throw new ConnectorException(ConnectorErrorKind.Permission, MissingHostMessage, reason: "media_host_missing");
        var igId = await ResolveAccountAsync(session, ct);
        var keys = (media.Images ?? [media]).Select(m => S3CompatibleMediaHost.StableKey(session.ConnectionId, m)).ToList();
        var containerId = state.SessionUri;
        var publishSent = state.FinalChunkSent;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (containerId is null)
            {
                await EnsurePublishingLimitAsync(session, igId, ct);
                await checkpoint.BeforeCreateSessionAsync(ct);
                containerId = await CreateMediaContainerAsync(session, igId, media, variant, ct);
                // Lưu container id ngay: crash sau đó chỉ chờ/đăng tiếp container này, không tạo container thứ hai.
                await checkpoint.SaveSessionAsync(containerId, media.SizeBytes, ct);
                publishSent = false;
            }

            var status = await WaitContainerAsync(session, containerId, ct);
            switch (status.Code)
            {
                case "PUBLISHED":
                    await CleanupMediaAsync(keys);
                    return new UploadOutcome.Ambiguous("Container đã được đăng nhưng chưa có media id; cần đối soát.");
                case "ERROR":
                    await checkpoint.ClearSessionAsync(ct);
                    await CleanupMediaAsync(keys);
                    throw new ConnectorException(ConnectorErrorKind.Validation,
                        "Instagram không xử lý được video" + (status.Detail is null ? "." : ": " + status.Detail), reason: "container_error");
                case "EXPIRED":
                    // Hết 24h mà chưa đăng: chắc chắn chưa có bài → tạo container mới.
                    await checkpoint.ClearSessionAsync(ct);
                    containerId = null;
                    continue;
            }

            await EnsurePublishingLimitAsync(session, igId, ct);
            await checkpoint.VerifyRouteAsync(ct);
            if (!publishSent) await checkpoint.MarkFinalChunkSendingAsync(ct);
            string mediaId;
            try
            {
                var json = await MetaGraphApi.PostFormAsync(session, $"{MetaGraphApi.Api}/{igId}/media_publish", options.ApiTimeout, Name, ct,
                    ("creation_id", containerId));
                mediaId = ConnectorHttp.Str(json?["id"]) ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Instagram không trả media id.");
            }
            catch (ConnectorException e) when (e.IsRetryable)
            {
                // Không biết media_publish đã chạy chưa: hỏi container. PUBLISHED → đối soát; còn FINISHED → lần chạy sau đăng lại cùng container (không tạo trùng).
                var probe = await ReadContainerAsync(session, containerId, ct);
                if (probe.Code == "PUBLISHED")
                {
                    await CleanupMediaAsync(keys);
                    return new UploadOutcome.Ambiguous("Đã gửi lệnh đăng, container báo PUBLISHED nhưng mất phản hồi media id; cần đối soát.");
                }
                throw;
            }
            await CleanupMediaAsync(keys);
            return new UploadOutcome.Completed(mediaId);
        }
        throw new ConnectorException(ConnectorErrorKind.Transient, "Container Instagram liên tục hết hạn; thử lại sau.");
    }

    public async Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, CancellationToken ct)
    {
        try
        {
            var json = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{Uri.EscapeDataString(remoteId)}?fields=id,permalink,media_product_type,timestamp",
                options.ApiTimeout, Name, ct);
            if (ConnectorHttp.Str(json?["id"]) is null) return new RemoteStatus(RemoteState.NotFound, null, "Không tìm thấy bài trên Instagram.");
            // Media id chỉ có sau media_publish: bài đã lên.
            return new RemoteStatus(RemoteState.Ready, "public", ConnectorHttp.Str(json?["permalink"]));
        }
        catch (ConnectorException e) when (MetaGraphApi.IsNotFound(e))
        {
            return new RemoteStatus(RemoteState.NotFound, null, "Không tìm thấy bài trên Instagram (có thể đã bị xóa).");
        }
    }

    public async Task<ReconcileResult> ReconcileAsync(ConnectorSession session, DestinationInfo destination, ReconcileQuery query, CancellationToken ct)
    {
        var from = query.UploadStartedUtc - options.ReconcileSlack;
        var to = query.NowUtc + options.ReconcileSlack;
        var json = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{Uri.EscapeDataString(destination.RemoteId)}/media?" + ConnectorHttp.Query(
            ("fields", "id,caption,timestamp,media_product_type"), ("since", PlatformHttp.Unix(from)), ("limit", "50")), options.ApiTimeout, Name, ct);
        var candidates = (json?["data"]?.AsArray() ?? [])
            .Select(i => (Id: ConnectorHttp.Str(i?["id"]), Caption: ConnectorHttp.Str(i?["caption"]),
                At: PlatformHttp.ParseTime(ConnectorHttp.Str(i?["timestamp"])), Type: ConnectorHttp.Str(i?["media_product_type"])))
            .Where(c => c.Id is not null && !query.ExcludeRemoteIds.Contains(c.Id)
                        && (query.IsImagePost ? c.Type is "FEED" : c.Type is null or "REELS")
                        && c.At is { } at && at >= from && at <= to && PlatformText.MatchesTitle(c.Caption, query.Title))
            .Select(c => c.Id!)
            .Distinct()
            .ToList();
        return candidates.Count switch
        {
            0 => new ReconcileResult.NotFound(),
            1 => new ReconcileResult.Found(candidates[0]),
            _ => new ReconcileResult.Ambiguous(candidates)
        };
    }

    [GeneratedRegex(@"^\d{5,25}$")]
    private static partial Regex IdPattern();

    /// <summary>Chỉ nhận media id số; URL instagram.com/reel/{shortcode} không đổi được sang media id khi offline.</summary>
    public string? ParseRemoteId(string url) => IdPattern().IsMatch(url.Trim()) ? url.Trim() : null;

    /// <summary>Media id không suy ra được permalink: dùng permalink trong <see cref="GetStatusAsync"/> (Detail) khi có.</summary>
    public string RemoteUrl(string remoteId) => "https://www.instagram.com/";

    // --- Graph ---

    public sealed record ContainerStatus(string Code, string? Detail);

    private async Task<string> CreateContainerAsync(ConnectorSession session, string igId, Uri videoUrl, PublishVariant v, CancellationToken ct)
    {
        var json = await MetaGraphApi.PostFormAsync(session, $"{MetaGraphApi.Api}/{igId}/media", options.ApiTimeout, Name, ct,
            ("media_type", "REELS"), ("video_url", videoUrl.AbsoluteUri), ("caption", PlatformText.Caption(v)),
            ("share_to_feed", options.ShareToFeed ? "true" : "false"));
        return ConnectorHttp.Str(json?["id"]) ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Instagram không trả id container.");
    }

    private async Task<ContainerStatus> WaitContainerAsync(ConnectorSession session, string containerId, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            var status = await ReadContainerAsync(session, containerId, ct);
            if (status.Code is "FINISHED" or "ERROR" or "EXPIRED" or "PUBLISHED") return status;
            if (i + 1 >= options.ContainerMaxPolls)
                throw new ConnectorException(ConnectorErrorKind.Timeout, "Instagram xử lý video lâu hơn dự kiến; sẽ kiểm tra tiếp container đã tạo.");
            await PlatformHttp.DelayAsync(options.ContainerPollInterval, _time, ct);
        }
    }

    public async Task<ContainerStatus> ReadContainerAsync(ConnectorSession session, string containerId, CancellationToken ct)
    {
        try
        {
            var json = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{Uri.EscapeDataString(containerId)}?fields=status_code,status",
                options.ApiTimeout, Name, ct);
            return new ContainerStatus(ConnectorHttp.Str(json?["status_code"]) ?? "IN_PROGRESS",
                ConnectorHttp.Str(json?["status"]) is { } s ? PlatformHttp.Truncate(s, 200) : null);
        }
        catch (ConnectorException e) when (MetaGraphApi.IsNotFound(e))
        {
            return new ContainerStatus("EXPIRED", "Container không còn tồn tại.");
        }
    }

    /// <summary>content_publishing_limit: hết suất → QuotaExceeded kèm thời điểm suất cũ nhất trong 24h được trả lại.</summary>
    private async Task EnsurePublishingLimitAsync(ConnectorSession session, string igId, CancellationToken ct)
    {
        var json = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{igId}/content_publishing_limit?fields=config,quota_usage",
            options.ApiTimeout, Name, ct);
        var item = json?["data"]?.AsArray().FirstOrDefault();
        var usage = PlatformHttp.Long(item?["quota_usage"]) ?? 0;
        var total = PlatformHttp.Long(item?["config"]?["quota_total"]) ?? options.FallbackPublishLimit;
        if (usage < total) return;

        var now = _time.GetUtcNow();
        TimeSpan wait = TimeSpan.FromHours(1);
        var media = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{igId}/media?" + ConnectorHttp.Query(
            ("fields", "id,timestamp"), ("since", PlatformHttp.Unix(now - Day)), ("limit", "100")), options.ApiTimeout, Name, ct);
        var oldest = (media?["data"]?.AsArray() ?? []).Select(i => PlatformHttp.ParseTime(ConnectorHttp.Str(i?["timestamp"])))
            .Where(t => t is not null && t > now - Day).Min();
        if (oldest is { } o && o + Day - now > TimeSpan.FromMinutes(1)) wait = o + Day - now;
        throw new ConnectorException(ConnectorErrorKind.QuotaExceeded,
            $"Tài khoản Instagram đã dùng {usage}/{total} lượt đăng qua API trong 24 giờ; thử lại sau.", wait, null, "content_publishing_limit");
    }

    private async Task<string> ResolveAccountAsync(ConnectorSession session, CancellationToken ct)
    {
        var accounts = await ListAccountsAsync(session, ct);
        return accounts.Count switch
        {
            1 => accounts[0].Id,
            0 => throw new ConnectorException(ConnectorErrorKind.Permission,
                "Page chưa liên kết tài khoản Instagram business/creator, hoặc thiếu quyền instagram_basic."),
            _ => throw new ConnectorException(ConnectorErrorKind.Validation,
                "Token thuộc nhiều tài khoản Instagram; kết nối Instagram phải dùng Page token của đúng Page liên kết.")
        };
    }

    /// <summary>Page token: /me là Page → instagram_business_account. Token người dùng: lấy từ /me/accounts.</summary>
    private async Task<List<(string Id, string? Username)>> ListAccountsAsync(ConnectorSession session, CancellationToken ct)
    {
        try
        {
            var me = await MetaGraphApi.GetAsync(session, MetaGraphApi.Api + "/me?fields=id,instagram_business_account{id,username}", options.ApiTimeout, Name, ct);
            // Lời gọi thành công nghĩa là token của Page: không có field = Page chưa liên kết Instagram.
            return me?["instagram_business_account"] is { } ig && ConnectorHttp.Str(ig["id"]) is { } id ? [(id, ConnectorHttp.Str(ig["username"]))] : [];
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Validation)
        {
            // Token người dùng: node User không có field instagram_business_account.
        }
        var result = new List<(string, string?)>();
        string? next = MetaGraphApi.Api + "/me/accounts?fields=instagram_business_account{id,username}&limit=100";
        for (var guard = 0; next is not null && guard < 20; guard++)
        {
            var json = await MetaGraphApi.GetAsync(session, next, options.ApiTimeout, Name, ct);
            foreach (var item in json?["data"]?.AsArray() ?? [])
                if (item?["instagram_business_account"] is { } ig && ConnectorHttp.Str(ig["id"]) is { } id)
                    result.Add((id, ConnectorHttp.Str(ig["username"])));
            next = MetaGraphApi.NextPage(json);
        }
        return result;
    }

    /// <summary>Xóa file tạm trên host; lỗi xóa không làm hỏng kết quả đăng (lifecycle rule của bucket dọn sau).</summary>
    private async Task CleanupAsync(string key)
    {
        try { await mediaHost!.DeleteAsync(key, CancellationToken.None); }
        catch (ConnectorException) { }
    }
}
