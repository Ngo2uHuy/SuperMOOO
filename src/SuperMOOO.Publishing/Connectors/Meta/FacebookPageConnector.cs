using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Publishing.Connectors.Meta;

[Service]
public sealed class FacebookPageConnectorOptions
{
    /// <summary>Trần Reels đăng qua API / Page / 24h trượt (tài liệu: 30). Đặt thấp hơn để chừa biên an toàn.</summary>
    public int MaxReelsPer24h { get; set; } = 30;
    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Thời gian tối đa cho 1 lần POST byte lên rupload (phần còn lại của file).</summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ReconcileSlack { get; set; } = TimeSpan.FromMinutes(10);
    public int MinDurationSeconds { get; set; } = 3;
    public int MaxDurationSeconds { get; set; } = 90;
}

/// <summary>Trạng thái video Reels đọc từ GET /{video-id}?fields=status.</summary>
public sealed record FacebookVideoStatus(
    string? VideoStatus, string? UploadingStatus, long BytesTransferred, string? ProcessingStatus, string? PublishingStatus,
    string? PublishStatus, string? Error)
{
    public bool IsFailed => VideoStatus is "error" or "upload_failed" || Error is not null
                            || UploadingStatus == "error" || ProcessingStatus == "error" || PublishingStatus == "error" || PublishStatus == "error";
    public bool IsExpired => VideoStatus == "expired";
    /// <summary>Lệnh finish đã được Meta nhận (đang/đã xuất bản hoặc đã hẹn giờ): KHÔNG gửi finish lần nữa.</summary>
    public bool PublishRequested => PublishingStatus is "in_progress" or "complete" or "completed" || PublishStatus is "published" or "scheduled";
    public bool IsProcessed => VideoStatus == "ready" || ProcessingStatus is "complete" or "completed";
}

/// <summary>
/// Facebook Page Reels qua Graph API: init (video_reels start) → upload nhị phân lên rupload.facebook.com → finish.
/// Token của phiên phải là Page token (lấy bằng <see cref="MetaOAuthProvider.ListPagesAsync"/>).
/// Remote id = video_id. Trạng thái lấy thật từ status.video_status/processing_phase/publishing_phase.
/// https://developers.facebook.com/docs/video-api/guides/reels-publishing
/// </summary>
public sealed partial class FacebookPageConnector(FacebookPageConnectorOptions options, TimeProvider? time = null) : ISocialConnector
{
    public const string Name = "Facebook";
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan MinScheduleLead = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxScheduleLead = TimeSpan.FromDays(29);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov" };
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Platform => nameof(SocialPlatform.Facebook);

    public IReadOnlyList<CapabilityInfo> GetCapabilities(string? grantedScopes)
    {
        var scopes = PlatformHttp.ParseScopes(grantedScopes);
        var read = scopes.Contains(MetaOAuthProvider.PagesReadEngagement) && scopes.Contains(MetaOAuthProvider.PagesShowList);
        var upload = read && scopes.Contains(MetaOAuthProvider.PagesManagePosts) ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission;
        var readStatus = read ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission;
        const string review = "Trước App Review (Advanced Access) chỉ người có vai trò trong app Meta đăng được.";
        return
        [
            new(Capability.UploadVideo, upload, $"Reels {options.MinDurationSeconds}–{options.MaxDurationSeconds}s, 9:16; tối đa {options.MaxReelsPer24h} Reels/24h. {review}"),
            new(Capability.UploadImages, upload, "Bài Page một hoặc nhiều ảnh JPG/PNG (tối đa 10 ảnh)."),
            new(Capability.ResumableUpload, upload, "rupload: tiếp tục từ bytes_transfered sau khi mất kết nối."),
            new(Capability.ReadPublishStatus, readStatus),
            new(Capability.ScheduleNative, upload, "video_state=SCHEDULED: hẹn từ 10 phút đến 29 ngày."),
            new(Capability.ReconcilePost, readStatus, "Liệt kê Reels gần đây của Page (khớp caption + thời gian)."),
            new(Capability.UpdateChannelInfo, CapabilityStatus.NotSupported, "Chưa triển khai."),
            new(Capability.CreateChannel, CapabilityStatus.NotSupported, "Tạo Page trên Facebook rồi kết nối."),
            new(Capability.ReadAnalytics, scopes.Contains(MetaOAuthProvider.ReadInsights) && read ? CapabilityStatus.Supported : CapabilityStatus.MissingPermission,
                "Lượt phát/tiếp cận/thời gian xem Reels (video_insights) và người theo dõi Page. Cần quyền read_insights: bật thống kê Meta trong Cài đặt rồi kết nối lại.")
        ];
    }

    public IReadOnlyList<ValidationIssue> Validate(PublishVariant v, MediaFile media, DestinationInfo? destination)
    {
        if (media.IsImagePost) return ValidateImages(v, media);
        var issues = new List<ValidationIssue>();
        if (!Extensions.Contains(Path.GetExtension(media.Path))) issues.Add(new("FORMAT", "Facebook Reels nên dùng .mp4 (hoặc .mov)."));
        if (!File.Exists(media.Path)) issues.Add(new("FILE_MISSING", "Không tìm thấy file video trong kho media."));
        if (media.SizeBytes <= 0) issues.Add(new("FILE_EMPTY", "File video rỗng."));
        if (v.DurationMs is { } ms && (ms < options.MinDurationSeconds * 1000L || ms > options.MaxDurationSeconds * 1000L))
            issues.Add(new("DURATION", $"Reels trên Page phải dài {options.MinDurationSeconds}–{options.MaxDurationSeconds} giây."));
        if (v.Privacy == PublishPrivacy.Unlisted) issues.Add(new("PRIVACY_UNSUPPORTED", "Page không có chế độ không công khai; chọn Công khai hoặc Riêng tư (bản nháp)."));
        if (v.PublishAtUtc is { } at)
        {
            if (v.Privacy != PublishPrivacy.Public) issues.Add(new("NATIVE_SCHEDULE_PRIVACY", "Hẹn giờ bằng Facebook chỉ dùng cho bài Công khai."));
            var lead = at - _time.GetUtcNow();
            if (lead < MinScheduleLead || lead > MaxScheduleLead)
                issues.Add(new("NATIVE_SCHEDULE_WINDOW", "Facebook chỉ cho hẹn giờ từ 10 phút đến 29 ngày tới."));
        }
        if (PlatformText.Caption(v).Length == 0) issues.Add(new("CAPTION_EMPTY", "Cần tiêu đề hoặc mô tả."));
        return issues;
    }

    /// <summary>Với Page token, /me là chính Page. Token người dùng → lỗi rõ (phải tách Page token qua MetaOAuthProvider.ListPagesAsync).</summary>
    public async Task<IReadOnlyList<DestinationInfo>> ListDestinationsAsync(ConnectorSession session, CancellationToken ct)
    {
        var page = await ResolvePageAsync(session, ct);
        var attributes = new Dictionary<string, string>();
        if (ConnectorHttp.Str(page["category"]) is { } category) attributes["category"] = category;
        var id = ConnectorHttp.Str(page["id"])!;
        return [new DestinationInfo(id, ConnectorHttp.Str(page["name"]) ?? id, ConnectorHttp.Str(page["link"]) ?? "https://www.facebook.com/" + id, null, attributes)];
    }

    public async Task<UploadOutcome> UploadAsync(ConnectorSession session, PublishVariant variant, MediaFile media, UploadState state,
        IUploadCheckpoint checkpoint, CancellationToken ct)
    {
        if (media.IsImagePost) return await UploadImagesAsync(session, variant, media, state, checkpoint, ct);
        var total = media.SizeBytes;
        var pageId = ConnectorHttp.Str((await ResolvePageAsync(session, ct))["id"])!;
        var videoId = state.SessionUri;
        var finishSent = state.FinalChunkSent;
        var transferred = 0L;

        if (videoId is not null)
        {
            var status = await TryReadStatusAsync(session, videoId, ct);
            if (status is null || status.IsExpired || status.VideoStatus == "upload_failed")
            {
                if (finishSent) return new UploadOutcome.Ambiguous("Video Reels hết hạn/không còn sau khi đã gửi lệnh xuất bản; cần đối soát.");
                await checkpoint.ClearSessionAsync(ct);
                videoId = null;
            }
            else if (status.PublishRequested) return new UploadOutcome.Completed(videoId);
            else if (status.IsFailed)
                throw new ConnectorException(ConnectorErrorKind.Validation, "Facebook không xử lý được video: " + (status.Error ?? status.VideoStatus));
            else transferred = Math.Min(status.BytesTransferred, total);
        }

        if (videoId is null)
        {
            await EnsureReelsBudgetAsync(session, pageId, ct);
            await checkpoint.BeforeCreateSessionAsync(ct);
            var init = await MetaGraphApi.PostFormAsync(session, $"{MetaGraphApi.Api}/{pageId}/video_reels", options.ApiTimeout, Name, ct,
                ("upload_phase", "start"));
            videoId = ConnectorHttp.Str(init?["video_id"])
                      ?? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Facebook không trả video_id khi khởi tạo Reels.");
            // Lưu video_id trước byte đầu tiên: crash sau đó tiếp tục đúng video này, không tạo video mới.
            await checkpoint.SaveSessionAsync(videoId, total, ct);
            finishSent = false;
        }

        if (transferred < total)
        {
            try
            {
                await UploadBytesAsync(session, videoId, media.Path, transferred, total, ct);
            }
            catch (ConnectorException e) when (e.IsRetryable)
            {
                // Byte cuối có thể đã tới: hỏi lại trước khi kết luận.
                var probe = await TryReadStatusAsync(session, videoId, ct);
                if (probe is null || probe.BytesTransferred < total) throw;
            }
            await checkpoint.SaveProgressAsync(total, ct);
        }

        await checkpoint.VerifyRouteAsync(ct);
        await checkpoint.MarkFinalChunkSendingAsync(ct);
        try
        {
            await FinishAsync(session, pageId, videoId, variant, ct);
        }
        catch (ConnectorException e) when (e.IsRetryable)
        {
            // Finish timeout: đã có video_id nên đối soát chắc chắn qua status, không gửi finish thứ hai nếu Meta đã nhận.
            var probe = await TryReadStatusAsync(session, videoId, ct);
            if (probe is { PublishRequested: true }) return new UploadOutcome.Completed(videoId);
            throw;
        }
        return new UploadOutcome.Completed(videoId);
    }

    public async Task<RemoteStatus> GetStatusAsync(ConnectorSession session, string remoteId, CancellationToken ct)
    {
        if (remoteId.Contains('_')) return await GetImagePostStatusAsync(session, remoteId, ct);
        var s = await TryReadStatusAsync(session, remoteId, ct);
        if (s is null) return new RemoteStatus(RemoteState.NotFound, null, "Không tìm thấy video trên Facebook (có thể đã bị xóa).");
        if (s.IsExpired) return new RemoteStatus(RemoteState.Failed, null, "Video đã hết hạn trên Facebook trước khi xuất bản; cần upload lại.");
        if (s.IsFailed) return new RemoteStatus(RemoteState.Failed, s.PublishStatus, "Facebook xử lý/xuất bản thất bại: " + (s.Error ?? s.VideoStatus));
        if (s.IsProcessed && s.PublishStatus is "published" && s.PublishingStatus is null or "complete" or "completed")
            return new RemoteStatus(RemoteState.Ready, "published", null);
        if (s.IsProcessed && s.PublishStatus is "scheduled") return new RemoteStatus(RemoteState.Ready, "scheduled", null);
        if (s.IsProcessed && s.PublishStatus is "draft" && s.PublishingStatus is null or "not_started")
            return new RemoteStatus(RemoteState.Ready, "draft", "Video ở dạng bản nháp của Page.");
        return new RemoteStatus(RemoteState.Processing, s.PublishStatus,
            $"video={s.VideoStatus}; xử lý={s.ProcessingStatus}; xuất bản={s.PublishingStatus}");
    }

    public async Task<ReconcileResult> ReconcileAsync(ConnectorSession session, DestinationInfo destination, ReconcileQuery query, CancellationToken ct)
    {
        // Feed không cung cấp checksum: tránh gắn nhầm một album có caption giống nhau sau khi mất phản hồi.
        if (query.IsImagePost) return new ReconcileResult.NotFound();
        var from = query.UploadStartedUtc - options.ReconcileSlack;
        var to = query.NowUtc + options.ReconcileSlack;
        var candidates = (await ListRecentReelsAsync(session, destination.RemoteId, from, ct))
            .Where(r => !query.ExcludeRemoteIds.Contains(r.Id) && r.At >= from && r.At <= to && PlatformText.MatchesTitle(r.Description, query.Title))
            .Select(r => r.Id)
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

    public string? ParseRemoteId(string url)
    {
        var s = url.Trim();
        if (PostIdPattern().IsMatch(s)) return s;
        if (IdPattern().IsMatch(s)) return s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) return null;
        if (!(u.Host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is [var page, "posts", var post] && IdPattern().IsMatch(page) && IdPattern().IsMatch(post))
            return page + "_" + post;
        string? id = segments switch
        {
            ["reel", var r, ..] => r,
            [_, "videos", .., var last] => last,
            ["videos", var v, ..] => v,
            ["watch"] => u.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "v" && p.Length == 2)?[1],
            _ => null
        };
        return id is not null && IdPattern().IsMatch(id) ? id : null;
    }

    public string RemoteUrl(string remoteId) => remoteId.Contains('_') ? "https://www.facebook.com/" + remoteId : "https://www.facebook.com/reel/" + remoteId;

    // --- Graph ---

    private async Task<JsonNode> ResolvePageAsync(ConnectorSession session, CancellationToken ct)
    {
        JsonNode? me;
        try
        {
            me = await MetaGraphApi.GetAsync(session, MetaGraphApi.Api + "/me?fields=id,name,link,category", options.ApiTimeout, Name, ct);
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Validation)
        {
            // User token không có field category → không phải Page token.
            throw new ConnectorException(ConnectorErrorKind.Permission,
                "Kết nối Facebook phải dùng Page token (chọn Page sau khi đăng nhập), không phải token người dùng.", null, e.HttpStatus, e.Reason);
        }
        return ConnectorHttp.Str(me?["id"]) is null
            ? throw new ConnectorException(ConnectorErrorKind.BadResponse, "Facebook không trả thông tin Page.")
            : me!;
    }

    /// <summary>Đếm Reels của Page trong 24h trượt trước khi init; đủ trần → QuotaExceeded kèm thời điểm suất cũ nhất hết hạn.</summary>
    private async Task EnsureReelsBudgetAsync(ConnectorSession session, string pageId, CancellationToken ct)
    {
        if (options.MaxReelsPer24h <= 0) return;
        var now = _time.GetUtcNow();
        var recent = (await ListRecentReelsAsync(session, pageId, now - Day, ct)).Where(r => r.At > now - Day).OrderBy(r => r.At).ToList();
        if (recent.Count < options.MaxReelsPer24h) return;
        var freeAt = recent[recent.Count - options.MaxReelsPer24h].At + Day;
        var wait = freeAt - now;
        throw new ConnectorException(ConnectorErrorKind.QuotaExceeded,
            $"Page đã đăng {recent.Count} Reels trong 24 giờ (trần {options.MaxReelsPer24h}); thử lại sau.",
            wait > TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), null, "reels_per_24h");
    }

    private sealed record ReelItem(string Id, string? Description, DateTimeOffset At);

    private async Task<List<ReelItem>> ListRecentReelsAsync(ConnectorSession session, string pageId, DateTimeOffset since, CancellationToken ct)
    {
        var result = new List<ReelItem>();
        string? next = $"{MetaGraphApi.Api}/{Uri.EscapeDataString(pageId)}/video_reels?" + ConnectorHttp.Query(
            ("fields", "id,description,created_time,updated_time"), ("since", PlatformHttp.Unix(since)), ("limit", "100"));
        for (var guard = 0; next is not null && guard < 20; guard++)
        {
            var json = await MetaGraphApi.GetAsync(session, next, options.ApiTimeout, Name, ct);
            var page = (json?["data"]?.AsArray() ?? [])
                .Select(i => (Id: ConnectorHttp.Str(i?["id"]), Description: ConnectorHttp.Str(i?["description"]),
                    At: PlatformHttp.ParseTime(ConnectorHttp.Str(i?["created_time"]) ?? ConnectorHttp.Str(i?["updated_time"]))))
                .Where(x => x.Id is not null && x.At is not null)
                .Select(x => new ReelItem(x.Id!, x.Description, x.At!.Value))
                .ToList();
            result.AddRange(page);
            // Danh sách mới → cũ: trang đã vượt mốc since thì dừng.
            if (page.Count == 0 || page.Min(r => r.At) < since) break;
            next = MetaGraphApi.NextPage(json);
        }
        return result;
    }

    private async Task UploadBytesAsync(ConnectorSession session, string videoId, string path, long offset, long total, CancellationToken ct)
    {
        var url = $"{MetaGraphApi.RuploadVideo}/{Uri.EscapeDataString(videoId)}";
        var json = await MetaGraphApi.CallAsync(session, token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FileSliceContent(path, offset, total - offset, "application/octet-stream")
            };
            // rupload dùng scheme "OAuth" (không phải Bearer) theo tài liệu Reels.
            request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
            request.Headers.TryAddWithoutValidation("offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("file_size", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return request;
        }, options.UploadTimeout, Name, ct);
        if (PlatformHttp.Bool(json?["success"]) == false)
            throw new ConnectorException(ConnectorErrorKind.Transient, "Facebook chưa xác nhận đã nhận đủ byte video.");
    }

    private async Task FinishAsync(ConnectorSession session, string pageId, string videoId, PublishVariant v, CancellationToken ct)
    {
        var form = new List<(string, string)>
        {
            ("upload_phase", "finish"), ("video_id", videoId), ("description", PlatformText.Caption(v)), ("title", v.Title.Trim()),
            ("is_ai_generated", v.ContainsSyntheticMedia ? "true" : "false")
        };
        if (v.Privacy == PublishPrivacy.Private) form.Add(("video_state", "DRAFT"));
        else if (v.PublishAtUtc is { } at) { form.Add(("video_state", "SCHEDULED")); form.Add(("scheduled_publish_time", PlatformHttp.Unix(at))); }
        else form.Add(("video_state", "PUBLISHED"));

        var json = await MetaGraphApi.PostFormAsync(session, $"{MetaGraphApi.Api}/{pageId}/video_reels", options.ApiTimeout, Name, ct, [.. form]);
        if (PlatformHttp.Bool(json?["success"]) != true)
            throw new ConnectorException(ConnectorErrorKind.BadResponse, "Facebook không xác nhận lệnh xuất bản Reels.");
    }

    /// <summary>null = video không tồn tại.</summary>
    public async Task<FacebookVideoStatus?> TryReadStatusAsync(ConnectorSession session, string videoId, CancellationToken ct)
    {
        JsonNode? json;
        try
        {
            json = await MetaGraphApi.GetAsync(session, $"{MetaGraphApi.Api}/{Uri.EscapeDataString(videoId)}?fields=status", options.ApiTimeout, Name, ct);
        }
        catch (ConnectorException e) when (MetaGraphApi.IsNotFound(e))
        {
            return null;
        }
        var status = json?["status"];
        if (status is null) return null;
        var uploading = status["uploading_phase"];
        var processing = status["processing_phase"];
        var publishing = status["publishing_phase"];
        var error = ErrorText(processing?["error"]) ?? ErrorText(publishing?["error"])
                    ?? ErrorText((uploading?["errors"] as JsonArray)?.FirstOrDefault());
        return new FacebookVideoStatus(
            ConnectorHttp.Str(status["video_status"]),
            ConnectorHttp.Str(uploading?["status"]),
            // Tài liệu viết "bytes_transfered" (sai chính tả); nhận cả hai.
            PlatformHttp.Long(uploading?["bytes_transfered"]) ?? PlatformHttp.Long(uploading?["bytes_transferred"]) ?? 0,
            ConnectorHttp.Str(processing?["status"]),
            ConnectorHttp.Str(publishing?["status"]),
            ConnectorHttp.Str(publishing?["publish_status"]),
            error);
    }

    private static string? ErrorText(JsonNode? error) =>
        error is null ? null : PlatformHttp.Truncate(ConnectorHttp.Str(error["message"]) ?? ConnectorHttp.Str(error) ?? "lỗi không rõ", 200);
}
