using System.Text.Json;
using System.Text.Json.Nodes;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using Microsoft.Extensions.Logging;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;

namespace SuperMOOO.Publishing.Publications;

[Service]
public sealed class PublishOptions
{
    /// <summary>Nhịp poll trạng thái xử lý (tăng dần, giữ mức cuối).</summary>
    public TimeSpan[] PollBackoff { get; set; } =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    public int MaxProcessingPolls { get; set; } = 300;
    /// <summary>Ngay sau upload, videos.list có thể chưa thấy video: chưa kết luận "không tồn tại" trước số lần này.</summary>
    public int NotFoundGracePolls { get; set; } = 3;
    /// <summary>Nhịp hỏi trạng thái khi bài nằm trong hộp thư chờ người dùng (TikTok inbox, hạn 24h).</summary>
    public TimeSpan AwaitingUserPoll { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Job upload một publication (lane Publish, khoá đồng thời theo tài khoản): Scheduled/RetryScheduled → Uploading (resumable, checkpoint
/// từng chunk) → Processing. Kiểm tra trạng thái sau upload không chiếm lane: <see cref="CheckStatusAsync"/> do Quartz gọi theo next_check_at.
/// Timeout/crash sau byte cuối mà không có video id → đối soát; không chắc chắn → Unknown, KHÔNG upload lại.
/// </summary>
[Service]
public sealed class PublishJobHandler(
    IPublicationRepository publications,
    IDestinationRepository destinations,
    IConnectionRepository connections,
    IContentRepository contents,
    ConnectorRegistry registry,
    OAuthTokenService tokens,
    PublishHttpClients http,
    QuotaGuard quota,
    IActionRequiredRepository actions,
    ICircuitBreaker breaker,
    IClock clock,
    PublishOptions options,
    ILogger<PublishJobHandler> logger,
    IpCountryGuard ipGuard,
    ICampaignPublishGate? campaignGate = null) : IJobHandler
{
    private static readonly PublicationStatus[] Runnable =
        [PublicationStatus.Scheduled, PublicationStatus.RetryScheduled, PublicationStatus.Uploading];

    private static readonly PublicationStatus[] Checkable = [PublicationStatus.Processing, PublicationStatus.AwaitingUser];

    public string Type => PublishJobTypes.Upload;

    public static PublishJobPayload? Payload(JsonNode? json)
    {
        try { return json?.Deserialize<PublishJobPayload>(PublishingJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Kết quả một lượt chạy: ghi vào publish_attempts, rồi (nếu cần) báo JobQueue dời/thử lại.</summary>
    private sealed record Step(string Outcome, string? Error = null, Exception? Then = null);

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = Payload(context.Payload) ?? throw new InvalidOperationException("Payload job đăng bài không hợp lệ.");
        var pub = await publications.GetAsync(payload.PublicationId, ct);
        // Job thừa (bài đã có job khác giữ) hoặc publication không còn chờ upload: bỏ qua, không đụng gì.
        if (pub is null || !Runnable.Contains(pub.Status) || !await publications.TryBindJobAsync(pub.Id, context.JobId, ct)) return null;

        // Bài chiến dịch chưa bắt đầu upload: chiến dịch tạm dừng thì chờ; trễ lịch thì áp chính sách trễ (không dồn đăng bù).
        if (payload.Automatic && campaignGate is not null && pub.CampaignId is not null && pub.Status == PublicationStatus.Scheduled
            && pub.UploadSessionUri is null && pub.RemoteId is null)
        {
            var gate = await campaignGate.CheckAsync(pub, context.JobId, ct);
            if (!gate.Proceed)
            {
                if (gate.DeferUntil is { } until) throw new JobDeferredException(until, gate.Message ?? "Chờ chiến dịch.");
                return null;
            }
            pub = await publications.GetAsync(pub.Id, ct);
            if (pub is null || pub.JobId != context.JobId || !Runnable.Contains(pub.Status)) return null;
        }

        // Đã có video (crash ngay sau khi gắn id): chỉ còn việc kiểm tra trạng thái, không upload lại.
        if (pub.RemoteId is not null)
        {
            await publications.TransitionAsync(pub.Id, PublicationStatus.Processing, null, null, ct);
            await publications.SetNextCheckAsync(pub.Id, clock.UtcNow, ct);
            return null;
        }

        var connection = await connections.GetAsync(payload.ConnectionId, ct);
        var destination = await destinations.GetAsync(pub.DestinationId, ct);
        var connector = destination is null ? null : registry.Find(destination.Platform);
        if (connection is null || destination is null || connector is null)
        {
            await FailAsync(pub, "Tài khoản, kênh đích hoặc connector không còn tồn tại.", ct);
            return null;
        }
        if (connection.ApiStatus != ApiStatus.Connected)
        {
            await BlockByAuthAsync(pub, $"Tài khoản \"{connection.DisplayName}\" chưa kết nối API hoặc cần kết nối lại.", ct);
            return null;
        }

        var attemptId = await publications.StartAttemptAsync(pub.Id, context.JobId, clock.UtcNow, ct);
        var step = new Step("Interrupted");
        try
        {
            using var lease = await http.AcquireAsync(connection.Id, ct);
            await publications.SetAttemptRouteAsync(attemptId, lease.RouteKind, lease.RouteRef, lease.EgressIp, lease.EgressCountry, lease.IpCheckedAt, ct);
            // IP khác lần đăng thành công trước: khác quốc gia thì dời chờ người dùng chấp nhận (ném lỗi), cùng quốc gia chỉ ghi chú vào lần thử.
            var ipNote = await ipGuard.CheckAsync(connection, lease.EgressIp, lease.EgressCountry, ct);
            // Meta: mỗi Page/Instagram dùng Page token riêng; nền tảng khác dùng token của kết nối.
            var session = new ConnectorSession(connection.Id, lease.Client, tokens.SourceFor(connection, destination));
            context.Progress.Report(new ProgressInfo(-1, $"Đang upload lên {destination.Title}"));
            step = await UploadAsync(pub, connection, destination, connector, session, c => http.ReverifyAsync(lease, c), ct);
            if (ipNote is not null) step = step with { Error = string.IsNullOrEmpty(step.Error) ? ipNote : $"{ipNote} {step.Error}" };
        }
        catch (Exception e)
        {
            step = await StepForAsync(pub, connection, destination, e, ct);
        }
        finally
        {
            await publications.FinishAttemptAsync(attemptId, step.Outcome, step.Error, clock.UtcNow, CancellationToken.None);
        }
        if (step.Then is not null) throw step.Then;
        return new JsonObject { ["publicationId"] = pub.Id, ["outcome"] = step.Outcome };
    }

    /// <summary>Quy lỗi của một lượt chạy về Step; ngoại lệ "Then" là tín hiệu cho JobQueue (dời/thử lại/huỷ).</summary>
    private async Task<Step> StepForAsync(Publication pub, SocialConnection connection, SocialDestination destination, Exception e, CancellationToken ct)
    {
        switch (e)
        {
            case ConnectorException c:
                return await HandleConnectorErrorAsync(pub, connection, destination, c, ct);
            case NetworkRequestBlockedException b:
                await MarkRetryAsync(pub, b.Message);
                return new Step("Deferred", b.Message, new JobDeferredException(b.RetryAt, b.Message));
            case AccountRouteUnavailableException r:
                await MarkRetryAsync(pub, r.Message);
                return new Step("Deferred", r.Message, new JobDeferredException(r.RetryAt, r.Message));
            case PublicationCancelledException:
                return new Step("Cancelled");
            case OperationCanceledException:
                // Tắt app: để nguyên, khởi động lại JobQueue xếp lại job và upload tiếp từ checkpoint.
                // Người dùng huỷ job: bộ điều phối thấy job Cancelled và quyết định theo việc video có thể đã lên hay chưa.
                return new Step("Interrupted", null, e);
            default:
                var message = e is IOException ? e.Message : $"Lỗi không xác định ({e.GetType().Name}).";
                logger.LogWarning(e, "Upload publication {Id} lỗi", pub.Id);
                await OnJobEndedAsync(pub.Id, null, message, ct);
                return new Step("Failed", message);
        }
    }

    /// <summary>
    /// Kiểm tra trạng thái bài đã upload (Processing/AwaitingUser), do Quartz gọi khi tới next_check_at; đặt mốc kiểm kế tiếp theo nhịp giãn dần.
    /// </summary>
    public async Task CheckStatusAsync(Publication pub, CancellationToken ct)
    {
        if (!Checkable.Contains(pub.Status) || pub.RemoteId is null)
        {
            await publications.SetNextCheckAsync(pub.Id, null, ct);
            return;
        }
        var destination = await destinations.GetAsync(pub.DestinationId, ct);
        var connection = destination is null ? null : await connections.GetAsync(destination.ConnectionId, ct);
        var connector = destination is null ? null : registry.Find(destination.Platform);
        if (connection is null || destination is null || connector is null)
        {
            await NeedsActionAsync(pub, "Tài khoản, kênh đích hoặc connector không còn tồn tại.", null, ct);
            await publications.SetNextCheckAsync(pub.Id, null, ct);
            return;
        }
        if (connection.ApiStatus != ApiStatus.Connected)
        {
            // Chờ kết nối lại; kiểm thưa để không gọi API bằng token hỏng.
            await publications.SetNextCheckAsync(pub.Id, clock.UtcNow + TimeSpan.FromHours(1), ct);
            return;
        }

        Step step;
        try
        {
            using var lease = await http.AcquireAsync(connection.Id, ct);
            var session = new ConnectorSession(connection.Id, lease.Client, tokens.SourceFor(connection, destination));
            step = await PollAsync(pub, connection, destination, connector, session, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            step = e switch
            {
                ConnectorException c => await HandleConnectorErrorAsync(pub, connection, destination, c, ct),
                NetworkRequestBlockedException b => new Step("Deferred", b.Message, new JobDeferredException(b.RetryAt, b.Message)),
                AccountRouteUnavailableException r => new Step("Deferred", r.Message, new JobDeferredException(r.RetryAt, r.Message)),
                _ => new Step("Retry", e.Message, new TransientJobException(e.Message, e))
            };
            if (e is not (ConnectorException or NetworkRequestBlockedException or AccountRouteUnavailableException))
                logger.LogWarning(e, "Kiểm tra trạng thái publication {Id} lỗi", pub.Id);
        }

        DateTimeOffset? next = step.Then switch
        {
            JobDeferredException d => d.Due,
            TransientJobException t => t.RetryAt ?? clock.UtcNow + PollDelay(pub.ProcessingPolls + 1),
            _ => null
        };
        await publications.SetNextCheckAsync(pub.Id, next, ct);
    }

    private async Task<Step> UploadAsync(Publication pub, SocialConnection connection, SocialDestination destination,
        ISocialConnector connector, ConnectorSession session, Func<CancellationToken, Task> verifyRoute, CancellationToken ct)
    {
        var detail = await contents.GetAsync(pub.ContentItemId, ct);
        var media = detail is null ? null : MediaFile.FromAssets(detail.Assets);
        if (detail is null || media is null) return await FailAsync(pub, "Không tìm thấy nội dung hoặc file media.", ct);

        var size = media.SizeBytes;
        var variant = new PublishVariant(pub.Title, pub.Description, PublishText.ParseTags(pub.TagsJson), pub.Privacy, pub.MadeForKids,
            pub.ContainsSyntheticMedia, pub.UseNativeSchedule ? pub.ScheduledUtc : null, detail.Item.DurationMs, pub.PlatformSettingsJson);
        var info = PublicationService.ToInfo(destination);

        // Validate lại lúc chạy: file có thể đã bị xóa/đổi sau khi lên lịch. Đã có session thì file đang là nguồn upload, không đổi được nữa.
        if (pub.UploadSessionUri is null)
        {
            if (connector.Validate(variant, media, info) is { Count: > 0 } issues)
                return await FailAsync(pub, string.Join(" ", issues.Select(i => i.Message)), ct);
        }
        else if (pub.UploadBytesTotal is { } total && total != size)
        {
            return await FailAsync(pub, "File video đã thay đổi kích thước giữa các lần upload.", ct);
        }

        if (pub.Status != PublicationStatus.Uploading
            && !await publications.TransitionAsync(pub.Id, PublicationStatus.Uploading, null, null, ct))
            return new Step("Skipped", "Publication đã đổi trạng thái.");

        var state = new UploadState(pub.UploadSessionUri, pub.UploadBytesConfirmed, pub.FinalChunkSentUtc is not null);
        var outcome = await connector.UploadAsync(session, variant, media, state, new Checkpoint(publications, quota, clock, pub, connection, verifyRoute), ct);
        switch (outcome)
        {
            case UploadOutcome.Completed done:
                await AttachAsync(pub, destination, connector, done.RemoteId, "Upload", ct);
                await publications.SetNextCheckAsync(pub.Id, clock.UtcNow + PollDelay(1), CancellationToken.None);
                return new Step("Uploaded", $"Đã upload xong; chờ {destination.Platform} xử lý.");
            case UploadOutcome.Ambiguous ambiguous:
                return await ReconcileAsync(pub, connection, destination, connector, session, info, media, ambiguous.Reason, ct);
            default:
                throw new InvalidOperationException("Kết quả upload không xác định.");
        }
    }

    private async Task<Step> ReconcileAsync(Publication pub, SocialConnection connection, SocialDestination destination,
        ISocialConnector connector, ConnectorSession session, DestinationInfo info, MediaFile media, string reason, CancellationToken ct)
    {
        await quota.ConsumeAsync(connection, 2, ct);
        var exclude = await publications.RemoteIdsOnDestinationAsync(destination.Id, ct);
        var current = await publications.GetAsync(pub.Id, ct) ?? pub;
        var query = new ReconcileQuery(pub.Title, current.UploadStartedUtc ?? pub.CreatedUtc, clock.UtcNow, media.SizeBytes, exclude, media.IsImagePost);
        var result = await connector.ReconcileAsync(session, info, query, ct);
        if (result is ReconcileResult.Found found)
        {
            await AttachAsync(pub, destination, connector, found.RemoteId, "Reconcile", ct);
            await publications.SetNextCheckAsync(pub.Id, clock.UtcNow + PollDelay(1), CancellationToken.None);
            return new Step("Reconciled", reason);
        }

        var detail = result is ReconcileResult.Ambiguous a
            ? $"{reason} Đối soát thấy {a.Candidates.Count} video có thể khớp ({string.Join(", ", a.Candidates)}) nên không tự gắn."
            : $"{reason} Đối soát không tìm thấy video khớp trong danh sách tải lên gần đây.";
        await publications.TransitionAsync(pub.Id, PublicationStatus.Unknown, detail, null, ct);
        await actions.RaiseAsync(ActionKinds.PublishUnknown, pub.Id, $"Không rõ \"{Short(pub.Title)}\" đã lên kênh chưa",
            detail + " Chọn: Đã đăng (nhập URL) / Đăng lại có chủ ý / Hủy.", ct);
        return new Step("Unknown", detail);
    }

    private async Task AttachAsync(Publication pub, SocialDestination destination, ISocialConnector connector, string remoteId,
        string discoveredBy, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var post = new RemotePost
        {
            Id = Guid.NewGuid().ToString("N"), PublicationId = pub.Id, DestinationId = destination.Id, Platform = destination.Platform,
            RemoteId = remoteId, Url = connector.RemoteUrl(remoteId), Title = pub.Title, DiscoveredBy = discoveredBy,
            LastSyncedUtc = now, CreatedUtc = now
        };
        // Lưu video id ngay khi có: từ đây mọi lỗi chỉ còn là kiểm tra trạng thái, không bao giờ upload lại.
        if (!await publications.AttachRemoteAsync(pub.Id, post, CancellationToken.None))
            logger.LogWarning("Không gắn được video {RemoteId} vào publication {Id} (trạng thái đã đổi)", remoteId, pub.Id);
    }

    private async Task<Step> PollAsync(Publication pub, SocialConnection connection, SocialDestination destination, ISocialConnector connector,
        ConnectorSession session, CancellationToken ct)
    {
        if (pub.Status is not (PublicationStatus.Processing or PublicationStatus.AwaitingUser)
            && !await publications.TransitionAsync(pub.Id, PublicationStatus.Processing, null, null, ct))
            return new Step("Skipped", "Publication đã đổi trạng thái.");

        await quota.ConsumeAsync(connection, 1, ct);
        var status = await connector.GetStatusAsync(session, pub.RemoteId!, pub.UploadCompletedUtc, ct);
        var polls = pub.ProcessingPolls + 1;
        var awaitingUser = status.PrivacyStatus == RemotePrivacy.AwaitingUser;
        switch (status.State)
        {
            case RemoteState.Ready:
                await publications.MarkPublishedAsync(pub.Id, clock.UtcNow, status.PrivacyStatus, RemoteUrlFrom(status.Detail), ct);
                await ResolveAllAsync(pub.Id, ct);
                return new Step("Published");
            case RemoteState.Failed when awaitingUser:
                // Hết hạn hộp thư: KHÔNG tự upload lại (người dùng có thể đã đăng tay hoặc bài nháp còn trong app).
                await actions.ResolveByRefAsync(ActionKinds.PublishAwaitingUser, pub.Id, ct);
                return await FailAsync(pub, $"{status.Detail} Mở hộp thư TikTok kiểm tra: nếu đã tự đăng thì bỏ qua mục này; "
                    + "muốn đăng lại thì tạo bài mới từ màn Tạo bài (video sẽ được gửi lại vào hộp thư).", ct);
            case RemoteState.Failed:
                return await FailAsync(pub, status.Detail ?? "Nền tảng báo xử lý video thất bại.", ct);
            case RemoteState.NotFound when polls > options.NotFoundGracePolls:
                if (connector.GetCapabilities(connection.Scopes).Any(c => c.Capability == Capability.ReconcilePost && c.Status == CapabilityStatus.NotSupported))
                    return await UnknownAsync(pub, (status.Detail ?? "Nền tảng không còn bản ghi của bài.")
                        + " Không liệt kê được bài trên kênh để đối soát nên không kết luận đã đăng hay chưa.", ct);
                return await FailAsync(pub, status.Detail ?? "Không tìm thấy video trên nền tảng.", ct);
            case RemoteState.Processing when awaitingUser:
                return await AwaitUserAsync(pub, destination, status.Detail, ct);
        }
        await publications.SetProcessingPollsAsync(pub.Id, polls, ct);
        if (polls > options.MaxProcessingPolls)
            return await NeedsActionAsync(pub, $"Nền tảng xử lý video quá lâu; kiểm tra trực tiếp trên {destination.Platform}.", null, ct);
        return new Step("Processing", status.Detail, Defer(polls, "Video đang được xử lý."));
    }

    /// <summary>Video đã vào hộp thư: chờ người dùng hoàn tất trong app (không phải Published), hỏi lại thưa để tôn trọng rate limit.</summary>
    private async Task<Step> AwaitUserAsync(Publication pub, SocialDestination destination, string? detail, CancellationToken ct)
    {
        if (pub.Status != PublicationStatus.AwaitingUser)
        {
            if (!await publications.TransitionAsync(pub.Id, PublicationStatus.AwaitingUser, null, null, ct))
                return new Step("Skipped", "Publication đã đổi trạng thái.");
            var deadline = (pub.UploadCompletedUtc ?? clock.UtcNow) + PublicationService.AwaitingUserWindow;
            await actions.RaiseAsync(ActionKinds.PublishAwaitingUser, pub.Id, $"Hoàn tất \"{Short(pub.Title)}\" trong app TikTok",
                $"Video đã vào hộp thư TikTok của \"{destination.Title}\". Mở thông báo trong app TikTok, nhập caption và đăng trước "
                + $"{deadline:yyyy-MM-dd HH:mm}Z (24 giờ từ lúc upload xong). SuperMOOO tự cập nhật khi TikTok báo đã đăng.", ct);
        }
        return new Step("AwaitingUser", detail, new JobDeferredException(clock.UtcNow + options.AwaitingUserPoll, detail ?? "Chờ người dùng hoàn tất trên TikTok."));
    }

    /// <summary>URL thật của bài từ kết quả kiểm tra trạng thái: permalink (Instagram) hoặc post id (TikTok).</summary>
    internal static string? RemoteUrlFrom(string? detail)
    {
        if (detail is null) return null;
        if (Uri.TryCreate(detail, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) return uri.AbsoluteUri;
        const string postId = "post_id=";
        if (!detail.StartsWith(postId, StringComparison.Ordinal)) return null;
        var id = detail[postId.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        // TikTok chỉ trả post id (không có username): đường dẫn @/video/{id} được TikTok chuyển tới đúng bài.
        return id is not null && id.All(char.IsAsciiDigit) ? "https://www.tiktok.com/@/video/" + id : null;
    }

    private async Task<Step> UnknownAsync(Publication pub, string detail, CancellationToken ct)
    {
        if (!await publications.TransitionAsync(pub.Id, PublicationStatus.Unknown, detail, null, ct)) return new Step("Skipped");
        await actions.ResolveByRefAsync(ActionKinds.PublishAwaitingUser, pub.Id, ct);
        await actions.RaiseAsync(ActionKinds.PublishUnknown, pub.Id, $"Không rõ \"{Short(pub.Title)}\" đã lên kênh chưa",
            detail + " Kiểm tra trên nền tảng rồi chọn: Đã đăng (nhập URL) / Đăng lại có chủ ý / Hủy.", ct);
        return new Step("Unknown", detail);
    }

    private JobDeferredException Defer(int polls, string message) => new(clock.UtcNow + PollDelay(polls), message);

    private TimeSpan PollDelay(int polls)
    {
        var delays = options.PollBackoff;
        return delays.Length == 0 ? TimeSpan.FromMinutes(1) : delays[Math.Min(Math.Max(polls - 1, 0), delays.Length - 1)];
    }

    private async Task<Step> HandleConnectorErrorAsync(Publication pub, SocialConnection connection, SocialDestination destination,
        ConnectorException e, CancellationToken ct)
    {
        switch (e.Kind)
        {
            case ConnectorErrorKind.VerificationRequired:
                return await NeedsActionAsync(pub,
                    $"{connection.Platform} · {connection.DisplayName}: {e.Message} "
                    + "Mở trình duyệt tài khoản để tự xác minh, sau đó chọn Tiếp tục. Bài sẽ giữ trạng thái chờ, không tự thử lại.",
                    Publication.BlockedByVerification, ct);
            case ConnectorErrorKind.Auth:
                // 401 sau khi đã làm mới 1 lần, hoặc refresh bị từ chối: dừng tài khoản, chờ người dùng kết nối lại.
                await tokens.MarkNeedsReauthAsync(connection, e.Message, ct);
                await BlockByAuthAsync(pub, e.Message, ct);
                return new Step("NeedsAction", e.Message);
            case ConnectorErrorKind.QuotaExceeded when destination.Platform != SocialPlatform.YouTube:
            {
                // Trần bài/24h của Page/Instagram/TikTok: connector báo lúc suất cũ nhất được trả lại.
                var until = clock.UtcNow + (e.RetryAfter ?? TimeSpan.FromHours(1));
                var message = $"{e.Message} Tự thử lại lúc {until:yyyy-MM-dd HH:mm}Z.";
                await MarkRetryAsync(pub, message);
                return new Step("Deferred", message, new NetworkRequestBlockedException(BlockReason.RateLimited, until, message, connection.Id));
            }
            case ConnectorErrorKind.QuotaExceeded:
            {
                await quota.MarkExhaustedAsync(connection, ct);
                var until = PacificTime.NextMidnight(clock.UtcNow);
                var message = $"Hết quota YouTube API trong ngày; tự thử lại sau khi quota reset ({until:yyyy-MM-dd HH:mm}Z).";
                await MarkRetryAsync(pub, message);
                // Dời job tới lúc reset, không tính lượt thử.
                return new Step("Deferred", message, new NetworkRequestBlockedException(BlockReason.RateLimited, until, message, connection.Id));
            }
            case ConnectorErrorKind.RateLimited or ConnectorErrorKind.Transient or ConnectorErrorKind.Timeout or ConnectorErrorKind.BadResponse:
            {
                await MarkRetryAsync(pub, e.Message);
                DateTimeOffset? notBefore = e.RetryAfter is { } wait ? clock.UtcNow + wait : null;
                return new Step("Retry", e.Message, new TransientJobException(e.Message, e, notBefore));
            }
            case ConnectorErrorKind.Permission or ConnectorErrorKind.Policy:
                // Lỗi quyền/chính sách lặp lại dễ khiến tài khoản bị hạn chế: ngắt mạch tài khoản, không retry mù.
                breaker.RecordFailure(connection.Platform.ToString(), connection.Id, FailureKind.Auth, e.Message);
                return await NeedsActionAsync(pub, e.Message, null, ct);
            default:
                return await FailAsync(pub, e.Message, ct);
        }
    }

    /// <summary>
    /// Job upload kết thúc mà bài vẫn chờ (lỗi không retry, hết lượt thử, bị huỷ): quyết định theo việc video có thể đã lên hay chưa.
    /// <paramref name="jobId"/> khác null: chỉ xử lý khi job đó còn đang giữ bài (bộ điều phối gọi khi thấy job đã dừng).
    /// </summary>
    public async Task OnJobEndedAsync(string publicationId, string? jobId, string message, CancellationToken ct)
    {
        var pub = await publications.GetAsync(publicationId, ct);
        if (pub is null || !Runnable.Contains(pub.Status) || jobId is not null && pub.JobId != jobId) return;
        if (pub.RemoteId is not null)
        {
            await publications.TransitionAsync(pub.Id, PublicationStatus.Processing, null, null, ct);
            await publications.SetNextCheckAsync(pub.Id, clock.UtcNow, ct);
        }
        else if (pub.FinalChunkSentUtc is not null)
        {
            var detail = $"Đã gửi byte cuối nhưng không xác nhận được kết quả: {message}";
            await publications.TransitionAsync(pub.Id, PublicationStatus.Unknown, detail, null, ct);
            await actions.RaiseAsync(ActionKinds.PublishUnknown, pub.Id, $"Không rõ \"{Short(pub.Title)}\" đã lên kênh chưa",
                detail + " Chọn: Đã đăng (nhập URL) / Đăng lại có chủ ý / Hủy.", ct);
        }
        else await FailAsync(pub, message, ct);
    }

    private async Task<Step> FailAsync(Publication pub, string message, CancellationToken ct)
    {
        await publications.TransitionAsync(pub.Id, PublicationStatus.Failed, message, null, ct);
        await actions.RaiseAsync(ActionKinds.PublishFailed, pub.Id, $"Đăng \"{Short(pub.Title)}\" thất bại", message, ct);
        return new Step("Failed", message);
    }

    private async Task<Step> NeedsActionAsync(Publication pub, string message, string? blockedReason, CancellationToken ct)
    {
        // Đọc checkpoint mới nhất: chunk cuối có thể được gửi trong chính lượt chạy này.
        var current = await publications.GetAsync(pub.Id, ct) ?? pub;
        if (current.RemoteId is null && current.FinalChunkSentUtc is not null)
        {
            if (await publications.TransitionAsync(pub.Id, PublicationStatus.Unknown, message, blockedReason, ct))
                await actions.RaiseAsync(ActionKinds.PublishUnknown, pub.Id, $"Không rõ \"{Short(pub.Title)}\" đã lên kênh chưa",
                    message + " Kiểm tra bài trên nền tảng trước khi quyết định đăng lại.", ct);
            return new Step("Unknown", message);
        }
        if (!await publications.TransitionAsync(pub.Id, PublicationStatus.NeedsAction, message, blockedReason, ct))
            return new Step("Skipped");
        await actions.RaiseAsync(ActionKinds.PublishNeedsAction, pub.Id, $"\"{Short(pub.Title)}\" cần xử lý", message, ct);
        return new Step("NeedsAction", message);
    }

    /// <summary>Mất quyền là việc của tài khoản (đã có 1 mục ConnectionNeedsReauth): không tạo thêm mục cho từng bài; kết nối lại sẽ tự xếp lại.</summary>
    private Task BlockByAuthAsync(Publication pub, string message, CancellationToken ct) =>
        publications.TransitionAsync(pub.Id, PublicationStatus.NeedsAction, message, Publication.BlockedByAuth, ct);

    /// <summary>Chờ thử lại: bài đã có video giữ Processing; còn lại về RetryScheduled (dữ liệu upload giữ nguyên để resume).</summary>
    private async Task MarkRetryAsync(Publication pub, string message)
    {
        var current = await publications.GetAsync(pub.Id, CancellationToken.None);
        if (current is null || current.RemoteId is not null || current.Status == PublicationStatus.RetryScheduled) return;
        await publications.TransitionAsync(pub.Id, PublicationStatus.RetryScheduled, message, null, CancellationToken.None);
    }

    private async Task ResolveAllAsync(string publicationId, CancellationToken ct)
    {
        foreach (var kind in new[] { ActionKinds.PublishNeedsAction, ActionKinds.PublishFailed, ActionKinds.PublishUnknown, ActionKinds.PublishAwaitingUser })
            await actions.ResolveByRefAsync(kind, publicationId, ct);
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..57] + "...";

    /// <summary>Mốc bền của upload; mọi ghi đều có điều kiện "còn Uploading" để hủy giữa chừng dừng được việc gửi byte.</summary>
    private sealed class Checkpoint(
        IPublicationRepository publications, QuotaGuard quota, IClock clock, Publication pub, SocialConnection connection,
        Func<CancellationToken, Task> verifyRoute) : IUploadCheckpoint
    {
        public async Task BeforeCreateSessionAsync(CancellationToken ct) =>
            await quota.ConsumeAsync(connection, await quota.InsertCostAsync(ct), ct);

        public async Task SaveSessionAsync(string sessionUri, long totalBytes, CancellationToken ct)
        {
            if (!await publications.SaveUploadSessionAsync(pub.Id, sessionUri, totalBytes, clock.UtcNow, ct))
                throw new PublicationCancelledException();
        }

        public async Task SaveProgressAsync(long bytesConfirmed, CancellationToken ct)
        {
            if (!await publications.SaveUploadProgressAsync(pub.Id, bytesConfirmed, ct)) throw new PublicationCancelledException();
        }

        public async Task MarkFinalChunkSendingAsync(CancellationToken ct)
        {
            if (!await publications.MarkFinalChunkSentAsync(pub.Id, clock.UtcNow, ct)) throw new PublicationCancelledException();
        }

        public Task VerifyRouteAsync(CancellationToken ct) => verifyRoute(ct);

        public Task ClearSessionAsync(CancellationToken ct) => publications.ClearUploadSessionAsync(pub.Id, ct);
    }
}
