using System.Globalization;
using System.Text.Json;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Http;
using SuperMOOO.Publishing.Scheduling;

namespace SuperMOOO.Publishing.Publications;

/// <summary>
/// Use case cho publication (UI S14/S07/S08 gọi trực tiếp): tạo (atomic, có kiểm tra rủi ro), lên lịch, hủy local, thử lại, xử lý Unknown.
/// Lỗi do người dùng → <see cref="PublishingException"/>. "Đăng ngay" xếp job upload ngay; bài hẹn giờ do <see cref="PublishDispatcher"/> xếp khi tới giờ.
/// </summary>
[Service]
public sealed class PublicationService(
    IPublicationRepository publications,
    IDestinationRepository destinations,
    IConnectionRepository connections,
    IContentRepository contents,
    ConnectorRegistry registry,
    IActionRequiredRepository actions,
    RiskAssessor risk,
    QuotaGuard quota,
    IPublishSettingsStore settings,
    IAuditLog audit,
    PublishDispatcher dispatcher,
    IClock clock)
{
    public const int MaxItemsPerRequest = 200;
    public const int PublishMaxAttempts = 8;
    /// <summary>Hẹn giờ bằng YouTube cần thời gian upload + xử lý trước giờ công khai.</summary>
    public static readonly TimeSpan MinNativeLead = TimeSpan.FromMinutes(15);
    /// <summary>Hạn hoàn tất bài trong hộp thư TikTok tính từ lúc upload xong (khảo sát: 24h).</summary>
    public static readonly TimeSpan AwaitingUserWindow = TimeSpan.FromHours(24);

    private static readonly PublicationStatus[] Pending =
        [PublicationStatus.Scheduled, PublicationStatus.RetryScheduled];

    private sealed record Planned(
        string PublicationId, PublicationTargetRequest Item, FinishedContent Content, MediaAsset Video, SocialDestination Destination,
        SocialConnection Connection, DateTimeOffset At, PublishVariant Variant);

    // --- Tạo ---

    public Task<CreatePublicationsResultDto> CreateAsync(CreatePublicationsRequest r, CancellationToken ct) =>
        CreateCoreAsync(r, MaxItemsPerRequest, dryRun: false, ct);

    /// <param name="dryRun">Chỉ kiểm tra (lỗi, đăng lại, rủi ro) và trả hết cảnh báo; không ghi gì. Dùng cho xem trước CSV.</param>
    internal async Task<CreatePublicationsResultDto> CreateCoreAsync(CreatePublicationsRequest r, int maxItems, bool dryRun, CancellationToken ct)
    {
        if (r.Items is null || r.Items.Count == 0 || r.Items.Count > maxItems)
            throw Invalid($"Cần 1..{maxItems} cặp nội dung × kênh.");
        var mode = ParseMode(r.Mode);
        var now = clock.UtcNow;
        // Thời gian lưu dạng chuỗi và so sánh trong SQL: luôn chuẩn hóa về UTC ở biên.
        r = r with
        {
            ScheduledUtc = r.ScheduledUtc?.ToUniversalTime(),
            Items = r.Items.Select(i => i with { ScheduledUtc = i.ScheduledUtc?.ToUniversalTime() }).ToList()
        };
        var errors = new List<string>();
        var planned = new List<Planned>();

        foreach (var (item, index) in r.Items.Select((x, i) => (x, i + 1)))
        {
            var label = $"Mục {index}";
            var at = mode switch
            {
                PublishMode.Now => now,
                PublishMode.Schedule => item.ScheduledUtc ?? r.ScheduledUtc,
                _ => item.ScheduledUtc ?? r.ScheduledUtc
            };
            if (mode == PublishMode.Schedule && at is null) { errors.Add($"{label}: thiếu giờ đăng."); continue; }
            if (mode == PublishMode.Schedule && at < now) { errors.Add($"{label}: giờ đăng đã qua."); continue; }

            var prepared = await PrepareAsync(item, label, mode, at, r.UseNativeSchedule, errors, ct);
            if (prepared is not null) planned.Add(prepared with { PublicationId = Guid.NewGuid().ToString("N"), At = at ?? now });
        }
        if (r.Items.Any(i => i.PlanKey is not null) && !dryRun)
        {
            // Nguồn tự động (S07/S08) chạy lại không tạo trùng bài: bỏ các mục đã có plan_key.
            var existing = (await publications.ListByOriginAsync(r.Origin, r.OriginRef, 10_000, ct))
                .Select(p => p.PlanKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            planned.RemoveAll(p => p.Item.PlanKey is { } key && existing.Contains(key));
            if (planned.Count == 0 && errors.Count == 0) return Rejected(["Các bài này đã được tạo trước đó."]);
        }
        var repeated = planned.GroupBy(p => (p.Content.Id, p.Destination.Id)).Where(g => g.Count() > 1).ToList();
        foreach (var dup in repeated.Where(g => !r.AllowRepeatedSchedule || mode != PublishMode.Schedule
                     || g.Select(p => VietnamTime.ToLocal(p.At).Date).Distinct().Count() != g.Count()))
            errors.Add($"\"{dup.First().Content.Title}\" được chọn nhiều lần cho kênh \"{dup.First().Destination.Title}\".");
        if (errors.Count > 0) return Rejected(errors);

        var warnings = new List<PublishWarningDto>();
        if (dryRun)
        {
            warnings.AddRange(await RepostWarningsAsync(planned, null, ct));
            if (mode != PublishMode.Draft) warnings.AddRange(await AssessAsync(planned, ct));
            return new CreatePublicationsResultDto(false, warnings.Any(w => w.Level == RiskLevels.Block),
                warnings.Any(w => w.Level == RiskLevels.Warning), [], warnings, []);
        }
        if (await RepostWarningsAsync(planned, null, ct) is { Count: > 0 } reposts)
        {
            if (!r.AllowRepost) return new CreatePublicationsResultDto(false, false, true, [], reposts, []);
            warnings.AddRange(reposts);
        }
        if (mode != PublishMode.Draft)
        {
            var risk = await AssessAsync(planned, ct);
            if (risk.Any(w => w.Level == RiskLevels.Block)) return new CreatePublicationsResultDto(false, true, false, [], [.. warnings, .. risk], []);
            if (risk.Any(w => w.Level == RiskLevels.Warning) && !r.AcknowledgeWarnings)
                return new CreatePublicationsResultDto(false, false, true, [], [.. warnings, .. risk], []);
            warnings.AddRange(risk);
        }

        var pubs = new List<Publication>();
        var dispatch = new Dictionary<string, DateTimeOffset>();
        foreach (var p in planned)
        {
            var native = mode == PublishMode.Schedule && r.UseNativeSchedule;
            if (mode != PublishMode.Draft) dispatch[p.PublicationId] = native ? now : p.At;
            pubs.Add(new Publication
            {
                Id = p.PublicationId, ContentItemId = p.Content.Id, DestinationId = p.Destination.Id,
                Title = p.Variant.Title.Trim(), Description = p.Variant.Description, TagsJson = JsonSerializer.Serialize(p.Variant.Tags),
                Privacy = p.Variant.Privacy, MadeForKids = p.Variant.MadeForKids, ContainsSyntheticMedia = p.Variant.ContainsSyntheticMedia,
                PlatformSettingsJson = p.Variant.PlatformSettingsJson,
                // Nháp chưa hẹn giờ để null; đăng ngay ghi giờ tạo để hiện đúng ngày trên lịch.
                ScheduledUtc = mode == PublishMode.Draft ? p.Item.ScheduledUtc ?? r.ScheduledUtc : p.At,
                UseNativeSchedule = native,
                // Draft → Validating → Scheduled diễn ra đồng bộ ở trên; chỉ ghi trạng thái cuối.
                Status = mode == PublishMode.Draft ? PublicationStatus.Draft : PublicationStatus.Scheduled,
                IdempotencyKey = $"{p.Destination.Id}:{p.Content.Id}:{p.PublicationId}",
                PlanKey = p.Item.PlanKey,
                Origin = r.Origin,
                OriginRef = r.OriginRef,
                IntentionalRepost = repeated.Any(g => g.Key == (p.Content.Id, p.Destination.Id))
                    || warnings.Any(w => w.Code == "DUP_ON_DESTINATION" && w.DestinationId == p.Destination.Id && w.ContentItemId == p.Content.Id),
                CreatedUtc = now, UpdatedUtc = now
            });
        }
        await publications.CreateBatchAsync(pubs, dispatch, ct);
        await audit.WriteAsync("ui", "publication.create", string.Join(',', pubs.Select(p => p.Id)), $"mode={mode}; count={pubs.Count}", ct);
        if (mode == PublishMode.Now)
            foreach (var p in pubs) await dispatcher.DispatchAsync(p.Id, manual: true, ct);
        return new CreatePublicationsResultDto(true, false, false, pubs.Select(p => p.Id).ToList(), warnings, []);
    }

    public async Task<CreatePublicationsResultDto> ScheduleAsync(SchedulePublicationRequest r, CancellationToken ct)
    {
        var mode = ParseMode(r.Mode);
        if (mode == PublishMode.Draft) throw Invalid("Chọn đăng ngay hoặc hẹn giờ.");
        var pub = await publications.GetAsync(r.Id, ct) ?? throw NotFound();
        if (pub.Status != PublicationStatus.Draft) throw Invalid("Chỉ lên lịch được bài nháp.");
        var now = clock.UtcNow;
        var at = mode == PublishMode.Now ? now : r.ScheduledUtc?.ToUniversalTime() ?? pub.ScheduledUtc;
        if (at is null || at < now) return Rejected(["Giờ đăng không hợp lệ hoặc đã qua."]);

        var item = new PublicationTargetRequest(pub.ContentItemId, pub.DestinationId, pub.Title, pub.Description,
            PublishText.ParseTags(pub.TagsJson), pub.Privacy.ToString(), pub.MadeForKids, pub.ContainsSyntheticMedia, at,
            PlatformPostSettings.TikTokOf(pub.PlatformSettingsJson));
        var errors = new List<string>();
        var prepared = await PrepareAsync(item, "Bài", mode, at, r.UseNativeSchedule, errors, ct);
        if (prepared is null) return Rejected(errors);
        prepared = prepared with { PublicationId = pub.Id, At = at.Value };

        var warnings = new List<PublishWarningDto>();
        if (!pub.IntentionalRepost && await RepostWarningsAsync([prepared], pub.Id, ct) is { Count: > 0 } reposts)
        {
            if (!r.AllowRepost) return new CreatePublicationsResultDto(false, false, true, [], reposts, []);
            warnings.AddRange(reposts);
        }
        var risk = await AssessAsync([prepared], ct);
        if (risk.Any(w => w.Level == RiskLevels.Block)) return new CreatePublicationsResultDto(false, true, false, [], [.. warnings, .. risk], []);
        if (risk.Any(w => w.Level == RiskLevels.Warning) && !r.AcknowledgeWarnings)
            return new CreatePublicationsResultDto(false, false, true, [], [.. warnings, .. risk], []);

        var native = mode == PublishMode.Schedule && r.UseNativeSchedule;
        if (!await publications.RequeueAsync(new PublicationRequeue(pub.Id, [PublicationStatus.Draft], PublicationStatus.Scheduled, native ? now : at.Value,
                Schedule: (at, native), IntentionalRepost: pub.IntentionalRepost || r.AllowRepost), ct))
            throw Invalid("Bài đã đổi trạng thái; tải lại danh sách.");
        await audit.WriteAsync("ui", "publication.schedule", pub.Id, $"mode={mode}", ct);
        if (mode == PublishMode.Now) await dispatcher.DispatchAsync(pub.Id, manual: true, ct);
        return new CreatePublicationsResultDto(true, false, false, [pub.Id], [.. warnings, .. risk], []);
    }

    /// <summary>Kiểm tra một cặp nội dung × kênh; lỗi ghi vào <paramref name="errors"/> và trả null.</summary>
    private async Task<Planned?> PrepareAsync(PublicationTargetRequest item, string label, PublishMode mode, DateTimeOffset? at,
        bool useNative, List<string> errors, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.ContentItemId) || string.IsNullOrWhiteSpace(item.DestinationId))
        {
            errors.Add($"{label}: thiếu nội dung hoặc kênh đích.");
            return null;
        }
        var detail = await contents.GetAsync(item.ContentItemId, ct);
        var video = detail?.Assets.FirstOrDefault(a => a.Kind is MediaKind.Video or MediaKind.Image);
        var destination = await destinations.GetAsync(item.DestinationId, ct);
        var connection = destination is null ? null : await connections.GetAsync(destination.ConnectionId, ct);
        if (detail is null || video is null) { errors.Add($"{label}: không tìm thấy nội dung hoặc file media."); return null; }
        if (destination is null || connection is null) { errors.Add($"{label}: không tìm thấy kênh đích."); return null; }
        label = $"\"{Short(detail.Item.Title)}\" → \"{destination.Title}\"";
        if (destination.RemovedUtc is not null || !destination.Enabled) { errors.Add($"{label}: kênh không còn khả dụng (đồng bộ lại kênh)."); return null; }
        if (!Enum.TryParse<PublishPrivacy>(item.Privacy, true, out var privacy) || !Enum.IsDefined(privacy))
        {
            errors.Add($"{label}: chế độ hiển thị không hợp lệ.");
            return null;
        }
        var connector = registry.Find(destination.Platform);
        if (connector is null) { errors.Add($"{label}: nền tảng chưa có connector."); return null; }

        if (mode != PublishMode.Draft)
        {
            if (connection.ApiStatus != ApiStatus.Connected)
                errors.Add($"{label}: tài khoản \"{connection.DisplayName}\" chưa kết nối API hoặc cần kết nối lại.");
            else if (DestinationCapabilities.AutoPublishBlockedReason(destination.Platform, DestinationCapabilities.For(connector, connection, destination))
                     is { } blocked)
                errors.Add($"{label}: {blocked}");
        }
        var native = mode == PublishMode.Schedule && useNative;
        if (native && at < clock.UtcNow + MinNativeLead)
            errors.Add($"{label}: hẹn giờ bằng nền tảng cần giờ đăng sau ít nhất {MinNativeLead.TotalMinutes:0} phút.");

        string? platformJson = null;
        if (destination.Platform == SocialPlatform.TikTok)
        {
            var tikTok = TikTokPostRules.Normalize(item.TikTok ?? new TikTokPostSettingsDto(TikTokPostModes.Inbox));
            // Nháp được lưu form dở dang; chỉ bắt buộc đủ điều kiện khi đăng/hẹn giờ.
            if (mode != PublishMode.Draft)
                foreach (var e in TikTokPostRules.Validate(tikTok, await PlatformFlags.TikTokAuditedAsync(settings, ct), connection.Scopes))
                    errors.Add($"{label}: {e}");
            platformJson = PlatformPostSettings.Serialize(tikTok);
        }
        else if (item.TikTok is not null) errors.Add($"{label}: lựa chọn TikTok chỉ dùng cho kênh TikTok.");

        var tags = (item.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var variant = new PublishVariant((item.Title ?? "").Trim(), item.Description ?? "", tags, privacy, item.MadeForKids,
            item.ContainsSyntheticMedia, native ? at : null, detail.Item.DurationMs, platformJson);
        foreach (var issue in connector.Validate(variant, MediaFile.FromAssets(detail.Assets)!, ToInfo(destination)))
            errors.Add($"{label}: {issue.Message}");
        return new Planned("", item, detail.Item, video, destination, connection, at ?? clock.UtcNow, variant);
    }

    private async Task<List<PublishWarningDto>> RepostWarningsAsync(IReadOnlyList<Planned> planned, string? selfId, CancellationToken ct)
    {
        var warnings = new List<PublishWarningDto>();
        foreach (var p in planned)
        {
            var existing = (await publications.FindOnDestinationAsync(p.Content.Id, p.Destination.Id, ct))
                .Where(x => x.Id != selfId && x.Status is not (PublicationStatus.Cancelled or PublicationStatus.Failed)).ToList();
            if (existing.Count == 0) continue;
            var posted = existing.Any(x => x.Status == PublicationStatus.Published);
            warnings.Add(new PublishWarningDto(p.Content.Id, p.Destination.Id, RiskLevels.Warning, "DUP_ON_DESTINATION",
                $"\"{Short(p.Content.Title)}\" đã có {existing.Count} bài trên kênh \"{p.Destination.Title}\"{(posted ? " (đã đăng)" : "")}. "
                + "Chỉ đăng lại khi bạn chủ ý."));
        }
        return warnings;
    }

    private async Task<List<PublishWarningDto>> AssessAsync(IReadOnlyList<Planned> planned, CancellationToken ct)
    {
        var byId = planned.ToDictionary(p => p.PublicationId);
        var items = planned.Select(p => new RiskItem(p.PublicationId, p.Destination.Id, p.Connection.Id, p.At, p.Content, p.Variant.Description,
            p.Variant.ContainsSyntheticMedia)).ToList();
        var warnings = (await risk.AssessAsync(items, ct)).ToList();
        if (!await PlatformFlags.TikTokAuditedAsync(settings, ct))
            warnings.AddRange(planned.Where(p => TikTokPostRules.IsDirectPost(PlatformPostSettings.TikTokOf(p.Variant.PlatformSettingsJson)))
                .Select(p => new RiskWarning(p.PublicationId, RiskLevel.Warning, "TIKTOK_UNAUDITED", TikTokPostRules.UnauditedWarning)));
        return warnings
            .Select(w => new PublishWarningDto(byId[w.PublicationId].Content.Id, byId[w.PublicationId].Destination.Id, w.Level.ToString(), w.Code,
                $"\"{Short(byId[w.PublicationId].Content.Title)}\" → \"{byId[w.PublicationId].Destination.Title}\": {w.Message}"))
            .ToList();
    }

    // --- Hủy / thử lại / Unknown ---

    /// <summary>Hủy local: không bao giờ gọi API xóa video trên nền tảng.</summary>
    public async Task<object?> CancelAsync(PublicationIdRequest r, CancellationToken ct)
    {
        var pub = await publications.GetAsync(r.Id, ct) ?? throw NotFound();
        if (pub.Status == PublicationStatus.Unknown) throw Invalid("Bài ở trạng thái Không xác định: chọn cách xử lý trong mục Cần xử lý.");
        if (PublicationRules.MayExistRemotely(pub) || !PublicationRules.CanTransition(pub.Status, PublicationStatus.Cancelled))
            throw Invalid("Video có thể đã nằm trên nền tảng; hủy trong app không xóa video. Quản lý video trực tiếp trên nền tảng.");
        if (!await publications.CancelAsync(pub.Id, allowUnknown: false, ct))
            throw Invalid("Không hủy được: bài vừa bắt đầu gửi phần cuối hoặc đã đổi trạng thái.");
        await ResolveActionsAsync(pub.Id, ct);
        await audit.WriteAsync("ui", "publication.cancel", pub.Id, null, ct);
        return null;
    }

    public async Task<object?> RetryAsync(PublicationIdRequest r, CancellationToken ct)
    {
        var pub = await publications.GetAsync(r.Id, ct) ?? throw NotFound();
        if (pub.Status is not (PublicationStatus.Failed or PublicationStatus.NeedsAction))
            throw Invalid("Chỉ thử lại được bài lỗi hoặc cần xử lý.");
        if (pub.RemoteId is null && pub.FinalChunkSentUtc is not null)
            throw Invalid("Không chắc video đã lên hay chưa: không thử lại tự động để tránh đăng trùng.");
        var connection = await ConnectionOfAsync(pub, ct);
        if (connection.ApiStatus != ApiStatus.Connected) throw Invalid("Tài khoản chưa kết nối API; kết nối lại trước.");
        var to = pub.RemoteId is null ? PublicationStatus.RetryScheduled : PublicationStatus.Processing;
        if (!await publications.RequeueAsync(new PublicationRequeue(pub.Id, [pub.Status], to, clock.UtcNow), ct))
            throw Invalid("Bài đã đổi trạng thái; tải lại danh sách.");
        await ResolveActionsAsync(pub.Id, ct);
        if (to == PublicationStatus.RetryScheduled) await dispatcher.DispatchAsync(pub.Id, manual: true, ct);
        await audit.WriteAsync("ui", "publication.retry", pub.Id, null, ct);
        return null;
    }

    public async Task<object?> ResolveUnknownAsync(ResolveUnknownPublicationRequest r, CancellationToken ct)
    {
        var pub = await publications.GetAsync(r.Id, ct) ?? throw NotFound();
        if (pub.Status != PublicationStatus.Unknown) throw Invalid("Bài không ở trạng thái Không xác định.");
        var destination = await destinations.GetAsync(pub.DestinationId, ct) ?? throw NotFound();
        var connector = registry.Find(destination.Platform) ?? throw Invalid("Nền tảng chưa có connector.");
        var connection = await ConnectionOfAsync(pub, ct);
        var now = clock.UtcNow;

        switch (r.Resolution)
        {
            case UnknownResolutions.MarkPublished:
                var remoteId = connector.ParseRemoteId(r.RemoteUrl ?? "") ?? throw Invalid("URL video không hợp lệ.");
                if ((await publications.RemoteIdsOnDestinationAsync(destination.Id, ct)).Contains(remoteId))
                    throw Invalid("Video này đã gắn với một bài khác trên kênh.");
                var post = new RemotePost
                {
                    Id = Guid.NewGuid().ToString("N"), PublicationId = pub.Id, DestinationId = destination.Id, Platform = destination.Platform,
                    RemoteId = remoteId, Url = connector.RemoteUrl(remoteId), Title = pub.Title, DiscoveredBy = "Manual",
                    LastSyncedUtc = now, CreatedUtc = now
                };
                // Gắn rồi để job kiểm tra trạng thái thật qua API thay vì tin hẳn vào URL người dùng nhập.
                // next_check_at = now: Quartz kiểm tra trạng thái ở nhịp kế tiếp.
                if (!await publications.RequeueAsync(new PublicationRequeue(pub.Id, [PublicationStatus.Unknown], PublicationStatus.Processing,
                        now, Attach: post), ct))
                    throw Invalid("Bài đã đổi trạng thái; tải lại danh sách.");
                break;
            case UnknownResolutions.Repost:
                if (!r.ConfirmRepost) throw Invalid("Cần xác nhận rõ ràng việc đăng lại (có thể tạo video trùng).");
                if (connection.ApiStatus != ApiStatus.Connected) throw Invalid("Tài khoản chưa kết nối API; kết nối lại trước.");
                if (!await publications.RequeueAsync(new PublicationRequeue(pub.Id, [PublicationStatus.Unknown], PublicationStatus.RetryScheduled,
                        now, ResetUpload: true, IntentionalRepost: true), ct))
                    throw Invalid("Bài đã đổi trạng thái; tải lại danh sách.");
                await dispatcher.DispatchAsync(pub.Id, manual: true, ct);
                break;
            case UnknownResolutions.Cancel:
                if (!await publications.CancelAsync(pub.Id, allowUnknown: true, ct)) throw Invalid("Bài đã đổi trạng thái; tải lại danh sách.");
                break;
            default:
                throw Invalid("Cách xử lý không hợp lệ.");
        }
        await ResolveActionsAsync(pub.Id, ct);
        await audit.WriteAsync("ui", "publication.unknown." + r.Resolution, pub.Id, null, ct);
        return null;
    }

    /// <summary>Kết nối lại thành công: bài bị chặn vì mất quyền được xếp lại (giữ giờ hẹn nếu chưa tới).</summary>
    public async Task<int> RequeueAuthBlockedAsync(string connectionId, CancellationToken ct)
    {
        var connection = await connections.GetAsync(connectionId, ct);
        if (connection is null) return 0;
        var count = 0;
        var now = clock.UtcNow;
        foreach (var pub in await publications.ListByConnectionAsync(connectionId, [PublicationStatus.NeedsAction], ct))
        {
            if (pub.BlockedReason != Publication.BlockedByAuth) continue;
            var started = pub.UploadSessionUri is not null || pub.RemoteId is not null;
            var to = pub.RemoteId is not null ? PublicationStatus.Processing : started ? PublicationStatus.RetryScheduled : PublicationStatus.Scheduled;
            var due = pub.UseNativeSchedule || pub.ScheduledUtc is null || pub.ScheduledUtc < now ? now : pub.ScheduledUtc.Value;
            if (await publications.RequeueAsync(new PublicationRequeue(pub.Id, [PublicationStatus.NeedsAction], to, due), ct))
            {
                await ResolveActionsAsync(pub.Id, ct);
                count++;
            }
        }
        return count;
    }

    /// <summary>Ngắt kết nối: bài chờ đăng của tài khoản chuyển NeedsAction (không tự chạy khi chưa có quyền).</summary>
    public async Task BlockPendingForConnectionAsync(string connectionId, string reason, CancellationToken ct)
    {
        foreach (var pub in await publications.ListByConnectionAsync(connectionId, Pending, ct))
            await publications.TransitionAsync(pub.Id, PublicationStatus.NeedsAction, reason, Publication.BlockedByAuth, ct);
    }

    // --- Đọc ---

    public async Task<PagedDto<PublicationDto>> ListAsync(ListPublicationsRequest r, CancellationToken ct)
    {
        if (r.Page < 1 || r.PageSize < 1 || r.PageSize > PublishingJson.MaxPageSize) throw Invalid("Phân trang không hợp lệ.");
        PublicationStatus? status = r.Status is null ? null
            : Enum.TryParse<PublicationStatus>(r.Status, true, out var s) && Enum.IsDefined(s) ? s : throw Invalid("Trạng thái không hợp lệ.");
        var (items, total) = await publications.QueryAsync(
            new PublicationQuery(status, r.DestinationId, r.FromUtc?.ToUniversalTime(), r.ToUtc?.ToUniversalTime(), r.Page, r.PageSize, r.CampaignId), ct);
        return new PagedDto<PublicationDto>(items.Select(ToDto).ToList(), total, r.Page, r.PageSize);
    }

    public async Task<PublicationDetailDto> GetAsync(PublicationIdRequest r, CancellationToken ct)
    {
        var row = await publications.GetRowAsync(r.Id, ct) ?? throw NotFound();
        var p = row.Publication;
        var attempts = await publications.ListAttemptsAsync(p.Id, ct);
        return new PublicationDetailDto(ToDto(row), p.Description, PublishText.ParseTags(p.TagsJson), p.MadeForKids, p.ContainsSyntheticMedia,
            p.IntentionalRepost, attempts.Select(a => new PublishAttemptDto(a.StartedUtc, a.FinishedUtc, a.Outcome, a.Error)).ToList());
    }

    public async Task<PublishSummaryDto> SummaryAsync(GetPublishSummaryRequest r, CancellationToken ct)
    {
        if (r.DayEndUtc <= r.DayStartUtc || r.DayEndUtc - r.DayStartUtc > TimeSpan.FromDays(2)) throw Invalid("Khoảng ngày không hợp lệ.");
        var s = await publications.SummaryAsync(r.DayStartUtc.ToUniversalTime(), r.DayEndUtc.ToUniversalTime(), ct);
        var (used, limit) = await quota.GetUsageAsync(ct);
        return new PublishSummaryDto(s.Today, s.TodayPublished, s.Failed, s.NeedsAction, s.Unknown, s.InProgress, used, limit);
    }

    // --- Hỗ trợ ---

    public static PublicationDto ToDto(PublicationRow row)
    {
        var p = row.Publication;
        var tikTok = PlatformPostSettings.TikTokOf(p.PlatformSettingsJson);
        return new PublicationDto(p.Id, p.ContentItemId, row.ContentTitle, p.DestinationId, row.DestinationTitle, row.ConnectionId,
            row.ConnectionName, row.Platform.ToString(), p.Title, p.Privacy.ToString(), p.Status.ToString(), p.ScheduledUtc, p.UseNativeSchedule,
            p.UploadBytesConfirmed, p.UploadBytesTotal, row.Attempts, p.RemoteId, p.RemoteUrl, p.LastError, p.PublishedUtc,
            p.CreatedUtc, p.UpdatedUtc,
            CanCancel: p.Status != PublicationStatus.Unknown && !PublicationRules.MayExistRemotely(p)
                       && PublicationRules.CanTransition(p.Status, PublicationStatus.Cancelled),
            CanRetry: p.Status is PublicationStatus.Failed or PublicationStatus.NeedsAction && !(p.RemoteId is null && p.FinalChunkSentUtc is not null),
            CampaignId: p.CampaignId, CampaignName: row.CampaignName, CanReschedule: PublicationRules.CanReschedule(p),
            AwaitingUserUntilUtc: p.Status == PublicationStatus.AwaitingUser && p.UploadCompletedUtc is { } done ? done + AwaitingUserWindow : null,
            TikTokMode: row.Platform == SocialPlatform.TikTok ? (tikTok?.Mode ?? TikTokPostModes.Inbox) : null,
            NeedsTikTokForm: p.Status is PublicationStatus.Draft or PublicationStatus.AwaitingApproval && TikTokPostRules.IsDirectPost(tikTok)
                             && TikTokPostRules.Validate(tikTok, audited: true, TikTokPostRules.PublishScope).Count > 0);
    }

    /// <summary>Hoàn tất/sửa form TikTok của bài chưa upload; Direct Post phải đủ điều kiện (trừ nháp).</summary>
    public async Task<PublicationDto> UpdateTikTokAsync(UpdatePublicationTikTokRequest r, CancellationToken ct)
    {
        var pub = await publications.GetAsync(r.Id, ct) ?? throw NotFound();
        var destination = await destinations.GetAsync(pub.DestinationId, ct) ?? throw NotFound();
        if (destination.Platform != SocialPlatform.TikTok) throw Invalid("Bài không đăng lên TikTok.");
        PublicationStatus[] editable = [PublicationStatus.Draft, PublicationStatus.AwaitingApproval, PublicationStatus.Scheduled];
        if (!editable.Contains(pub.Status) || pub.UploadSessionUri is not null || pub.RemoteId is not null)
            throw Invalid("Chỉ sửa được lựa chọn TikTok khi bài chưa bắt đầu upload.");
        var settingsDto = TikTokPostRules.Normalize(r.Settings ?? throw Invalid("Thiếu lựa chọn TikTok."));
        var connection = await ConnectionOfAsync(pub, ct);
        if (pub.Status != PublicationStatus.Draft
            && TikTokPostRules.Validate(settingsDto, await PlatformFlags.TikTokAuditedAsync(settings, ct), connection.Scopes) is { Count: > 0 } errors)
            throw Invalid(string.Join(" ", errors));
        if (!await publications.SetPlatformSettingsAsync(pub.Id, PlatformPostSettings.Serialize(settingsDto), editable, ct))
            throw Invalid("Bài đã đổi trạng thái; tải lại danh sách.");
        await audit.WriteAsync("ui", "publication.tiktok", pub.Id, settingsDto.Mode, ct);
        return ToDto((await publications.GetRowAsync(pub.Id, ct))!);
    }

    public static DestinationInfo ToInfo(SocialDestination d)
    {
        Dictionary<string, string> attributes;
        try { attributes = JsonSerializer.Deserialize<Dictionary<string, string>>(d.AttributesJson) ?? []; }
        catch (JsonException) { attributes = []; }
        return new DestinationInfo(d.RemoteId, d.Title, d.Url, d.UploadsPlaylistId, attributes);
    }

    private async Task<SocialConnection> ConnectionOfAsync(Publication pub, CancellationToken ct)
    {
        var destination = await destinations.GetAsync(pub.DestinationId, ct) ?? throw NotFound();
        return await connections.GetAsync(destination.ConnectionId, ct) ?? throw NotFound();
    }

    private async Task ResolveActionsAsync(string publicationId, CancellationToken ct)
    {
        foreach (var kind in new[] { ActionKinds.PublishNeedsAction, ActionKinds.PublishFailed, ActionKinds.PublishUnknown })
            await actions.ResolveByRefAsync(kind, publicationId, ct);
    }



    private enum PublishMode { Draft, Now, Schedule }

    private static PublishMode ParseMode(string? mode) => mode switch
    {
        PublishModes.Draft => PublishMode.Draft,
        PublishModes.Now => PublishMode.Now,
        PublishModes.Schedule => PublishMode.Schedule,
        _ => throw Invalid("Chế độ đăng không hợp lệ.")
    };

    private static CreatePublicationsResultDto Rejected(List<string> errors) => new(false, false, false, [], [], errors);

    private static string Short(string s) => s.Length <= 60 ? s : s[..57] + "...";

    private static PublishingException Invalid(string message) => new(PublishingErrors.InvalidRequest, message);
    private static PublishingException NotFound() => new(PublishingErrors.NotFound, "Không tìm thấy bài đăng.");
}

/// <param name="Automatic">Bài theo lịch/chiến dịch: dừng theo cờ "tạm dừng tự động"; đăng tay vẫn chạy.</param>
/// <param name="ConcurrencyKey">Khoá đồng thời của JobQueue (<see cref="JobPayloadKeys.ConcurrencyKey"/>): mỗi tài khoản 1 upload.</param>
public sealed record PublishJobPayload(string PublicationId, string ConnectionId, string Platform, bool Automatic, string ConcurrencyKey);
