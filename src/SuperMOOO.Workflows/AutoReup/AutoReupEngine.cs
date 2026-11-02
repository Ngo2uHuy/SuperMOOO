using SuperMOOO.Core.Publishing;
using SuperMOOO.Infrastructure.Network;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Workflows.Auto;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Reup;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Workflows.AutoReup;

public sealed record ReupRuleView(ReupRuleRecord Record, ReupRuleConfig Config, IReadOnlyDictionary<string, int> Counts);

public sealed record ReupItemView(ReupItemRecord Record, ReupVideo Video, IReadOnlyList<Publication> Posts);

/// <summary>
/// S08: luồng (rule) theo dõi trang nguồn → item mỗi video mới → job xử lý (qua JobQueue, owner "screen:S08" nên có tiến độ/huỷ ngay ở S08 và ở S15) → bài đăng `pub_publications`
/// (origin auto_reup, origin_ref = item; plan_key theo nền tảng + video nguồn + kênh, dùng chung giữa các luồng).
/// </summary>
[Service]
public sealed class AutoReupEngine(IReupStore store, ReupSourceLister lister, IJobQueue jobs, ReupStudioService reup, AutoWorkflowService auto,
    DeliveryService delivery, ProjectIndex projects, ReupMarketService markets, NetworkRouteBroker routes, ILogger<AutoReupEngine> log)
{
    private const int MaxActive = 2;
    private const int FirstScanLimit = 1000;
    private const int RescanLimit = 60;
    private const int RouteRetryMinutes = 5;
    // Tuyến cả máy đang do bài đăng khác giữ: không chờ lâu để nhịp nền 20 giây không bị chặn.
    private static readonly TimeSpan RouteLockWait = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private int _recovered;

    // ---- Luồng ----

    public async Task<string> SaveRuleAsync(ReupRuleConfig config, string? ruleId = null, CancellationToken ct = default)
    {
        config.Normalize();
        if (config.PublishMode != ReupPublishModes.Download) await delivery.CheckDestinationsAsync(config.DestinationIds, ct);

        var now = DateTimeOffset.UtcNow;
        if (ruleId is not null)
        {
            var old = await store.GetRuleAsync(ruleId, ct) ?? throw new InvalidOperationException("Không tìm thấy luồng reup.");
            var before = Parse(old);
            if (before.SourcePlatform != config.SourcePlatform || before.SourceUrl != config.SourceUrl || before.History != config.History
                || before.HistoryLimit != config.HistoryLimit || before.ContentType != config.ContentType)
                throw new InvalidOperationException("Tạo luồng mới khi đổi nguồn hoặc phạm vi lấy video cũ; lịch sử hiện tại được giữ riêng.");
            await store.UpsertRuleAsync(old with { Name = config.Name, Enabled = config.Enabled, ConfigJson = JsonSerializer.Serialize(config, Json), NextCheckAt = now }, ct);
            return ruleId;
        }

        var id = Guid.NewGuid().ToString("N");
        await store.UpsertRuleAsync(new ReupRuleRecord(id, config.Name, config.Enabled, JsonSerializer.Serialize(config, Json), false, null, now, "Chờ quét", now), ct);
        return id;
    }

    public async Task<IReadOnlyList<ReupRuleView>> ListRulesAsync(CancellationToken ct = default)
    {
        var counts = await store.CountByStatusAsync(ct);
        return [.. (await store.ListRulesAsync(ct)).Select(r => new ReupRuleView(r, Parse(r), counts.GetValueOrDefault(r.Id) ?? new Dictionary<string, int>()))];
    }

    public Task SetEnabledAsync(string ruleId, bool enabled, CancellationToken ct = default) =>
        store.SetRuleEnabledAsync(ruleId, enabled, enabled ? DateTimeOffset.UtcNow : null, ct);

    public Task DeleteRuleAsync(string ruleId, CancellationToken ct = default) => store.DeleteRuleAsync(ruleId, ct);

    /// <summary>Xem trước tiêu đề/mô tả/giờ đăng từng kênh của cấu hình đang sửa, với video gần nhất của luồng (hoặc video mẫu).</summary>
    public async Task<IReadOnlyList<ReupPreview>> PreviewAsync(ReupRuleConfig config, string? ruleId, CancellationToken ct = default)
    {
        config.Normalize();
        var latest = ruleId is null ? null : (await store.ListItemsAsync(ruleId, null, 1, 0, ct)).FirstOrDefault();
        var video = latest is null ? new ReupVideo("mau", "https://www.tiktok.com/@kenh_nguon/video/1234567890", "Tiêu đề video mẫu của kênh nguồn", "kenh_nguon")
            : ParseVideo(latest);
        return await markets.PreviewAsync(config, video, ct);
    }

    /// <summary>Quét ngay ở lượt kế tiếp thay vì chờ hết chu kỳ.</summary>
    public async Task CheckNowAsync(string ruleId, CancellationToken ct = default)
    {
        var rule = await store.GetRuleAsync(ruleId, ct) ?? throw new InvalidOperationException("Không tìm thấy luồng reup.");
        await store.SetRuleEnabledAsync(ruleId, rule.Enabled, DateTimeOffset.UtcNow, ct);
    }

    // ---- Item ----

    public async Task<IReadOnlyList<ReupItemView>> ListItemsAsync(string? ruleId, int limit = 200, int offset = 0, CancellationToken ct = default)
    {
        var views = new List<ReupItemView>();
        foreach (var item in await store.ListItemsAsync(ruleId, null, limit, offset, ct))
            views.Add(new ReupItemView(item, ParseVideo(item), await delivery.ListAsync(PublicationOrigins.AutoReup, item.Id, ct)));
        return views;
    }

    /// <summary>Thử lại item lỗi từ đầu (lỗi cloud tính phí cũng phải do người dùng chủ động bấm).</summary>
    public async Task RetryItemAsync(string itemId, CancellationToken ct = default)
    {
        var item = await store.GetItemAsync(itemId, ct);
        if (item is not { Status: ReupItemStatus.Failed }) throw new InvalidOperationException("Chỉ thử lại được video đang lỗi.");
        await store.UpdateItemAsync(itemId, ReupItemStatus.Pending, 0, null, null, JsonSerializer.Serialize(ParseVideo(item) with { JobId = null }, Json), ct: ct);
    }

    /// <summary>Duyệt mọi bài chờ duyệt của video này; bài có cảnh báo rủi ro (vd. reup) chỉ đăng khi <paramref name="acknowledgeWarnings"/>.</summary>
    /// <returns>Kết quả từng bài chưa duyệt được (cảnh báo cần xác nhận hoặc bị chặn).</returns>
    public async Task<IReadOnlyList<CreatePublicationsResultDto>> ApproveItemAsync(string itemId, bool acknowledgeWarnings, CancellationToken ct = default)
    {
        var pending = new List<CreatePublicationsResultDto>();
        foreach (var post in await delivery.ListAsync(PublicationOrigins.AutoReup, itemId, ct))
            if (post.Status == PublicationStatus.Draft && await delivery.ApproveAsync(post.Id, acknowledgeWarnings, ct) is { Created: false } result)
                pending.Add(result);
        return pending;
    }

    /// <summary>"Gửi lại" bài lỗi; bài có thể đã lên nền tảng thì lõi từ chối để không đăng trùng.</summary>
    public Task ResendAsync(string postId, CancellationToken ct = default) => delivery.RetryAsync(postId, ct);

    public Task CancelPostAsync(string postId, CancellationToken ct = default) => delivery.CancelAsync(postId, ct);

    // ---- Vòng lặp nền (ReupPollJob) ----

    /// <summary>Một lượt: theo dõi job đang chạy → xếp thêm item chờ → quét một luồng đến hạn. Lỗi của một item/luồng không chặn phần còn lại.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _recovered, 1) == 0) await store.RecoverProcessingAsync(ct);
        await PollProcessingAsync(ct);
        await StartPendingAsync(ct);
        if (await store.NextDueRuleAsync(DateTimeOffset.UtcNow, ct) is { } rule) await ScanAsync(rule, ct);
    }

    internal async Task ScanAsync(ReupRuleRecord record, CancellationToken ct)
    {
        var rule = Parse(record);
        var now = DateTimeOffset.UtcNow;
        var next = now.AddMinutes(rule.EffectiveIntervalMinutes);
        RouteLease? lease = null;
        if (rule.SourceRouteId is { } routeId)
        {
            try
            {
                var route = routes.ListRoutes().FirstOrDefault(r => r.Id == routeId)
                    ?? throw new RoutePolicyViolationException("Tuyến mạng quét nguồn đã bị xoá ở Tài khoản & kênh (S23).", routeId);
                lease = await routes.AcquireAsync(route, null, ct, RouteLockWait);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Tuyến chưa sẵn sàng (VPN rớt, sai quốc gia, đang bận): bỏ lần quét này, không quét bằng mạng khác.
                log.LogWarning("Bỏ lần quét {Rule}: tuyến {Route} chưa sẵn sàng: {Reason}", record.Name, routeId, e.Message);
                await store.UpdateRuleScanAsync(record.Id, record.Initialized, now, now.AddMinutes(Math.Min(RouteRetryMinutes, rule.EffectiveIntervalMinutes)),
                    "Bỏ lần quét này, tuyến mạng chưa sẵn sàng: " + e.Message, ct);
                return;
            }
        }

        // Tuyến cả máy giữ lease suốt lần quét rồi nhả; request của crawler trong phạm vi này đi đúng tuyến.
        await using var held = lease;
        using var scope = lease is null ? null : CrawlRoute.Begin(lease);
        try
        {
            var first = !record.Initialized;
            var videos = await lister.ListAsync(rule, first ? FirstScanLimit : RescanLimit, ct);
            var added = 0;
            for (var i = 0; i < videos.Count; i++)
            {
                var video = videos[i];
                var skip = !rule.Matches(video)
                    || (first && rule.History == "new" && !(video.PublishedAtMs is { } at && at >= record.CreatedAt.ToUnixTimeMilliseconds()))
                    || (first && rule.History == "limit" && i >= rule.HistoryLimit);
                var item = new ReupItemRecord(Guid.NewGuid().ToString("N"), record.Id, rule.SourcePlatform, video.Key, JsonSerializer.Serialize(video, Json),
                    skip ? ReupItemStatus.Ignored : ReupItemStatus.Pending, null, 0, null, null, now);
                if (await store.InsertItemAsync(item, ct) && !skip) added++;
            }

            await store.UpdateRuleScanAsync(record.Id, true, now, next, $"Quét xong {videos.Count} video ({added} video mới cần xử lý); đang theo dõi video mới.", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Giữ các video đã lưu và thử lại ở chu kỳ sau; chỉ báo lý do lên luồng.
            log.LogWarning(e, "Quét nguồn {Rule} lỗi", record.Name);
            await store.UpdateRuleScanAsync(record.Id, record.Initialized, now, next, "Quét chưa hoàn tất: " + e.Message, ct);
        }
    }

    private async Task StartPendingAsync(CancellationToken ct)
    {
        while (await store.CountProcessingAsync(ct) < MaxActive && await store.ClaimPendingAsync(DateTimeOffset.UtcNow, ct) is { } item)
        {
            try
            {
                var rule = Parse((await store.GetRuleAsync(item.RuleId, ct))!);
                var video = ParseVideo(item);
                string jobId;
                // Vòng lặp nền không mang ngữ cảnh màn, nên tự gắn owner để job hiện ở khung tác vụ của S08.
                using (JobOwner.Begin(JobOwner.Screen("S08"))) jobId = await EnqueueAsync(rule, item, video, ct);
                await store.UpdateItemAsync(item.Id, ReupItemStatus.Processing, null, null, null, JsonSerializer.Serialize(video with { JobId = jobId }, Json), ct: ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await FailAsync(item, e.Message, ct);
            }
        }
    }

    private async Task<string> EnqueueAsync(ReupRuleConfig rule, ReupItemRecord item, ReupVideo video, CancellationToken ct)
    {
        var folder = Path.Combine(projects.OutputRoot(), "AutoReup", item.RuleId[..8], item.Id[..8]);
        if (rule.Processing is { } processing)
        {
            processing.Sources = [video.Url];
            processing.OutputFolder = folder;
            return (await auto.StartAsync(processing, ct))[0].JobId;
        }

        if (rule.Transform)
            return await reup.StartExportAsync(new ReupExportRequest(video.Url, reup.DefaultProfile(rule.ReupPreset), folder), ct);

        var input = new JsonObject { ["source"] = video.Url, ["outputDir"] = folder, ["name"] = SafeName(video.Title, video.Key) };
        return await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Tải: {video.Title}", PipelineResult.Pipeline("download", input), JobLane.Network), ct);
    }

    private async Task PollProcessingAsync(CancellationToken ct)
    {
        foreach (var item in await store.ListProcessingAsync(ct))
        {
            var video = ParseVideo(item);
            if (video.JobId is null || await jobs.GetAsync(video.JobId, ct) is not { } job)
            {
                // Job không còn (app tắt giữa chừng): xếp lại từ đầu.
                await store.UpdateItemAsync(item.Id, ReupItemStatus.Pending, ct: ct);
                continue;
            }

            try
            {
                switch (job.Status)
                {
                    case JobStatus.Succeeded: await FinishAsync(item, video, job, ct); break;
                    case JobStatus.WaitingManualRetry:
                        await FailAsync(item, job.Error ?? "Bước cloud tính phí lỗi; kiểm tra rồi bấm Thử lại.", ct);
                        break;
                    case JobStatus.Failed or JobStatus.Cancelled: await RetryOrFailAsync(item, job.Error ?? "Tác vụ bị huỷ.", ct); break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await FailAsync(item, e.Message, ct);
            }
        }
    }

    private async Task FinishAsync(ReupItemRecord item, ReupVideo video, JobSnapshot job, CancellationToken ct)
    {
        var rule = Parse((await store.GetRuleAsync(item.RuleId, ct))!);
        var path = PipelineResult.Value(job.Result, "video") ?? PipelineResult.Value(job.Result, "source");
        if (path is null || !File.Exists(path)) throw new InvalidOperationException("Video đã xử lý không còn trên máy.");
        video = video with { OutputPath = path };

        if (rule.PublishMode == ReupPublishModes.Download)
        {
            await store.UpdateItemAsync(item.Id, ReupItemStatus.Ready, null, null, null, JsonSerializer.Serialize(video, Json), ct: ct);
            return;
        }

        var title = ReupRuleConfig.Fill(rule.TitleTemplate, video, rule.SourcePlatform);
        var caption = ReupRuleConfig.Fill(rule.CaptionTemplate, video, rule.SourcePlatform);
        var (targetFor, warnings) = await markets.PrepareAsync(rule, video, ct);
        // Cùng video nguồn + kênh đã có ở luồng khác (hoặc video đã có trên kênh) thì không đăng lại.
        var created = await delivery.PlanAsync(new DeliveryPlan(PublicationOrigins.AutoReup, item.Id, path, $"reup:{rule.SourcePlatform}:{item.SourceKey}",
            title, caption, rule.Privacy, rule.PublishMode == ReupPublishModes.Review ? DeliveryModes.Review : DeliveryModes.Automatic, rule.DestinationIds,
            new SchedulePacing(rule.DelayMinutes, rule.SpacingMinutes, rule.DailyLimit), IsReupload: true, DedupeAcrossRefs: true, TargetFor: targetFor), ct);

        // Cảnh báo dịch (đã dùng bản gốc) hoặc hồ sơ đã bị xoá ghi vào cột ghi chú của video, không làm hỏng việc đăng.
        await store.UpdateItemAsync(item.Id, created == 0 ? ReupItemStatus.Duplicate : ReupItemStatus.Ready, null, null,
            created == 0 ? "Video và kênh đích đã có trong một luồng khác hoặc đã đăng trên kênh."
                : warnings.Count > 0 ? Trim(string.Join(" ", warnings)) : null, JsonSerializer.Serialize(video, Json), ct: ct);
    }

    private async Task RetryOrFailAsync(ReupItemRecord item, string error, CancellationToken ct)
    {
        var rule = Parse((await store.GetRuleAsync(item.RuleId, ct))!);
        var attempts = item.Attempts + 1;
        if (attempts > rule.Retries) await FailAsync(item, error, ct, attempts);
        else await store.UpdateItemAsync(item.Id, ReupItemStatus.Pending, attempts, DateTimeOffset.UtcNow.AddSeconds(Math.Min(3600, 60 * Math.Pow(2, attempts))),
            Trim(error), JsonSerializer.Serialize(ParseVideo(item) with { JobId = null }, Json), ct: ct);
    }

    private Task FailAsync(ReupItemRecord item, string error, CancellationToken ct, int? attempts = null) =>
        store.UpdateItemAsync(item.Id, ReupItemStatus.Failed, attempts ?? item.Attempts + 1, null, Trim(error), ct: ct);

    private static string Trim(string text) => text.Length > 1000 ? text[..1000] : text;

    private static string SafeName(string title, string key)
    {
        var clean = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? key : clean.Length > 60 ? clean[..60] : clean;
    }

    private static ReupRuleConfig Parse(ReupRuleRecord r) => JsonSerializer.Deserialize<ReupRuleConfig>(r.ConfigJson, Json)
        ?? throw new InvalidDataException($"Cấu hình luồng '{r.Name}' hỏng.");

    private static ReupVideo ParseVideo(ReupItemRecord i) => JsonSerializer.Deserialize<ReupVideo>(i.VideoJson, Json)
        ?? throw new InvalidDataException("Dữ liệu video của luồng hỏng.");
}
