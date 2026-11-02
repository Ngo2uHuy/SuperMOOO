using System.ComponentModel.DataAnnotations;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Library;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Workflows.Delivery;

public static class DeliveryModes
{
    public const string Review = "review";
    public const string Automatic = "automatic";
}

/// <summary>Cấu hình đưa video Auto sang lịch đăng. `Review` tạo bài nháp chờ người dùng duyệt; `Automatic` hẹn giờ đăng luôn.</summary>
public sealed class DeliveryOptions
{
    [RegularExpression("review|automatic", ErrorMessage = "Chế độ đăng phải là review hoặc automatic.")] public string Mode { get; set; } = DeliveryModes.Review;
    /// <summary>Id kênh/trang đích (pub_destinations).</summary>
    public List<string> DestinationIds { get; set; } = [];
    [StringLength(100)] public string Title { get; set; } = "";
    [StringLength(5000)] public string Caption { get; set; } = "";
    [RegularExpression("private|unlisted|public", ErrorMessage = "Quyền riêng tư phải là private, unlisted hoặc public.")] public string Privacy { get; set; } = "private";
    [Range(0, 10080)] public int DelayMinutes { get; set; }
    [Range(1, 10080)] public int SpacingMinutes { get; set; } = 10;
    [Range(1, 1000)] public int DailyLimit { get; set; } = 20;
}

/// <param name="Schedule">Khung giờ đăng theo giờ địa phương của thị trường; có thì giới hạn/ngày tính theo ngày địa phương thay vì 24 giờ trượt.</param>
public sealed record SchedulePacing(int DelayMinutes = 0, int SpacingMinutes = 10, int DailyLimit = 20, PostingSchedule? Schedule = null);

/// <summary>Nội dung/lịch riêng của một kênh trong <see cref="DeliveryPlan"/> (S08: theo hồ sơ thị trường).</summary>
public sealed record DeliveryTarget(string Title, string Caption, string Privacy, SchedulePacing Pacing);

/// <summary>Một video cần đăng lên nhiều kênh.</summary>
/// <param name="PlanKey">Khoá chống trùng của video; mỗi bài là "{PlanKey}:{kênh}" nên chạy lại không tạo bài trùng.</param>
/// <param name="DedupeAcrossRefs">Chống trùng theo mọi OriginRef của cùng Origin (Auto Reup: cùng video nguồn ở luồng khác không đăng lại).</param>
/// <param name="Targets">Ghi đè tiêu đề/mô tả/quyền riêng tư/lịch theo kênh; kênh không có thì dùng giá trị chung của plan.</param>
/// <param name="TargetFor">Dựng <paramref name="Targets"/> theo kênh khi cần (sau chống trùng); trả null thì dùng giá trị chung.</param>
public sealed record DeliveryPlan(string Origin, string OriginRef, string Path, string PlanKey, string Title, string Caption, string Privacy,
    string Mode, IReadOnlyList<string> DestinationIds, SchedulePacing Pacing, bool IsReupload = false, bool DedupeAcrossRefs = false,
    IReadOnlyDictionary<string, DeliveryTarget>? Targets = null, Func<string, CancellationToken, Task<DeliveryTarget?>>? TargetFor = null);

/// <summary>
/// Pipeline (bước `deliver`, S07/S18…) và Auto Reup (S08) → lõi đăng bài (pub_publications) qua <see cref="PublicationService"/>.
/// Video được đăng ký vào kho thành phẩm qua <see cref="ContentIngestService"/> (không chép file, trùng sha256 thì dùng lại nội dung cũ).
/// </summary>
[Service]
public sealed class DeliveryService(PublicationService publications, IPublicationRepository repository, ContentIngestService ingest,
    OAuthConnectService oauth)
{
    private const int MaxVideos = 50;
    // Hẹn giờ phải ở tương lai lúc PublicationService kiểm tra; chừa khoảng nhỏ cho thời gian ghi.
    private static readonly TimeSpan MinLead = TimeSpan.FromSeconds(30);

    /// <summary>Kiểm tra kênh đích còn dùng được và các tham số lịch; trả về options đã chuẩn hoá.</summary>
    public async Task<DeliveryOptions> ValidateAsync(DeliveryOptions options, CancellationToken ct = default)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
            throw new InvalidOperationException(results[0].ErrorMessage);
        options.DestinationIds = [.. options.DestinationIds.Distinct()];
        await CheckDestinationsAsync(options.DestinationIds, ct);
        return options;
    }

    public async Task CheckDestinationsAsync(IReadOnlyList<string> destinationIds, CancellationToken ct = default)
    {
        if (destinationIds.Count is 0 or > 50) throw new InvalidOperationException("Chọn từ 1 đến 50 kênh đích.");
        var all = (await Guard(() => oauth.ListDestinationsAsync(new ListDestinationsRequest(null), ct))).ToDictionary(d => d.Id);
        foreach (var id in destinationIds)
        {
            if (!all.TryGetValue(id, out var d) || !d.Enabled || d.Removed)
                throw new InvalidOperationException("Kênh đích không còn khả dụng; đồng bộ lại kênh ở màn Tài khoản & kênh (S23).");
            if (d.ConnectionApiStatus != nameof(ApiStatus.Connected))
                throw new InvalidOperationException($"Tài khoản của kênh \"{d.Title}\" chưa kết nối API hoặc cần kết nối lại (S23).");
            if (d.AutoPublishBlockedReason is { } blocked) throw new InvalidOperationException($"Kênh \"{d.Title}\": {blocked}");
        }
    }

    /// <summary>Bước `deliver`: mỗi video × kênh một bài (origin auto_workflow, origin_ref = job), khoá `wf:{slot}:{kênh}`.</summary>
    public async Task<int> EnqueueAsync(string owner, IReadOnlyList<string> paths, DeliveryOptions options, string titleFallback, CancellationToken ct = default)
    {
        await ValidateAsync(options, ct);
        var files = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count is 0 or > MaxVideos) throw new InvalidOperationException($"Lịch đăng nhận từ 1 đến {MaxVideos} video đã xuất.");
        var pacing = new SchedulePacing(options.DelayMinutes, options.SpacingMinutes, options.DailyLimit);
        var created = 0;
        for (var slot = 0; slot < files.Count; slot++)
        {
            ct.ThrowIfCancellationRequested();
            var title = options.Title.Trim() is { Length: > 0 } t ? t : titleFallback.Trim() is { Length: > 0 } f ? f : Path.GetFileNameWithoutExtension(files[slot]);
            created += await PlanAsync(new DeliveryPlan(PublicationOrigins.AutoWorkflow, owner, files[slot], $"wf:{slot}", title, options.Caption,
                options.Privacy, options.Mode, options.DestinationIds, pacing), ct);
        }
        return created;
    }

    /// <returns>Số bài mới; 0 khi mọi kênh đã có bài của video này (chạy lại hoặc trùng).</returns>
    public async Task<int> PlanAsync(DeliveryPlan plan, CancellationToken ct = default)
    {
        var contentId = await RegisterContentAsync(plan.Path, plan.Title, plan.IsReupload, ct);
        var existing = (await repository.ListByOriginAsync(plan.Origin, plan.DedupeAcrossRefs ? null : plan.OriginRef, 10_000, ct))
            .Select(p => p.PlanKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var automatic = plan.Mode == DeliveryModes.Automatic;
        var items = new List<PublicationTargetRequest>();
        foreach (var destination in plan.DestinationIds.Distinct())
        {
            var key = $"{plan.PlanKey}:{destination}";
            if (existing.Contains(key)) continue;
            // TargetFor chỉ chạy cho kênh chưa có bài (sau bước chống trùng), vì có thể phải dịch tiêu đề.
            var target = plan.Targets?.GetValueOrDefault(destination) ?? (plan.TargetFor is null ? null : await plan.TargetFor(destination, ct));
            var due = await NextDueAsync(destination, target?.Pacing ?? plan.Pacing, automatic, ct);
            items.Add(new PublicationTargetRequest(contentId, destination, Cut((target?.Title ?? plan.Title).Trim(), 100), Cut(target?.Caption ?? plan.Caption, 5000),
                null, target?.Privacy ?? plan.Privacy, false, false, due, null, key));
        }

        // Video đã có trên kênh (cùng sha256) thì bỏ kênh đó thay vì đăng lại; các kênh còn lại vẫn tạo bài.
        while (items.Count > 0)
        {
            var result = await Guard(() => publications.CreateAsync(new CreatePublicationsRequest(items, automatic ? PublishModes.Schedule : PublishModes.Draft,
                null, false, AcknowledgeWarnings: automatic, AllowRepost: false, plan.Origin, plan.OriginRef), ct));
            if (result.Created) return result.PublicationIds.Count;
            var reposted = result.Warnings.Where(w => w.Code == "DUP_ON_DESTINATION").Select(w => w.DestinationId).ToHashSet();
            if (result.Blocked || result.Errors.Count > 0 || reposted.Count == 0)
                throw new InvalidOperationException(string.Join("\n", result.Errors.Count > 0 ? result.Errors : result.Warnings.Select(w => w.Message)));
            items.RemoveAll(i => reposted.Contains(i.DestinationId));
        }
        return 0;
    }

    public Task<IReadOnlyList<Publication>> ListAsync(string origin, string originRef, CancellationToken ct = default) =>
        repository.ListByOriginAsync(origin, originRef, 200, ct);

    /// <summary>Duyệt bài nháp: giữ giờ đăng nếu còn ở tương lai, đã quá hạn thì đăng ngay. Có cảnh báo rủi ro thì trả về để UI hỏi lại.</summary>
    public async Task<CreatePublicationsResultDto> ApproveAsync(string publicationId, bool acknowledgeWarnings, CancellationToken ct = default)
    {
        var pub = await repository.GetAsync(publicationId, ct) ?? throw new InvalidOperationException("Không tìm thấy bài đăng.");
        if (pub.Status != PublicationStatus.Draft) throw new InvalidOperationException("Chỉ duyệt được bài đang chờ duyệt.");
        var later = pub.ScheduledUtc is { } at && at > DateTimeOffset.UtcNow + MinLead;
        return await Guard(() => publications.ScheduleAsync(new SchedulePublicationRequest(pub.Id, later ? PublishModes.Schedule : PublishModes.Now,
            later ? pub.ScheduledUtc : null, false, acknowledgeWarnings), ct));
    }

    /// <summary>Huỷ trong app; không xoá video đã lên nền tảng.</summary>
    public Task CancelAsync(string publicationId, CancellationToken ct = default) =>
        Guard(() => publications.CancelAsync(new PublicationIdRequest(publicationId), ct));

    /// <summary>Thử lại bài lỗi; bài có thể đã lên nền tảng thì lõi từ chối để không đăng trùng.</summary>
    public Task RetryAsync(string publicationId, CancellationToken ct = default) =>
        Guard(() => publications.RetryAsync(new PublicationIdRequest(publicationId), ct));

    private async Task<string> RegisterContentAsync(string path, string title, bool isReupload, CancellationToken ct)
    {
        try { return (await ingest.IngestAsync(new IngestRequest(Path.GetFullPath(path), title, LibrarySources.Pipeline, IsReupload: isReupload), ct)).ContentId; }
        catch (IngestException e) { throw new InvalidOperationException(e.Message, e); }
    }

    /// <summary>Giờ đăng dự kiến nếu xếp bài mới cho kênh lúc này (dùng cho cả xem trước ở S08).</summary>
    public async Task<DateTimeOffset> NextDueAsync(string destinationId, SchedulePacing pacing, bool automatic, CancellationToken ct = default)
    {
        var due = await NextSlotAsync(destinationId, pacing, ct);
        if (automatic && due < DateTimeOffset.UtcNow + MinLead)
        {
            due = DateTimeOffset.UtcNow + MinLead;
            if (pacing.Schedule is { } schedule) due = schedule.Fit(due);
        }
        return due;
    }

    /// <summary>
    /// Giờ đăng kế tiếp của kênh: cách bài trước `SpacingMinutes`, không quá `DailyLimit` bài trong 24 giờ.
    /// Có khung giờ thị trường: dời vào khung kế tiếp và đếm giới hạn theo ngày địa phương; ngày đã đủ thì sang đầu khung của ngày sau.
    /// </summary>
    private async Task<DateTimeOffset> NextSlotAsync(string destinationId, SchedulePacing pacing, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (rows, _) = await repository.QueryAsync(new PublicationQuery(null, destinationId, now.AddDays(-1), null, 1, 1000), ct);
        var taken = rows.Select(r => r.Publication)
            .Where(p => p.ScheduledUtc is not null && p.Status is not (PublicationStatus.Cancelled or PublicationStatus.Failed))
            .Select(p => p.ScheduledUtc!.Value).Order().ToList();
        var due = now.AddMinutes(pacing.DelayMinutes);
        if (taken.Count > 0 && taken[^1].AddMinutes(pacing.SpacingMinutes) > due) due = taken[^1].AddMinutes(pacing.SpacingMinutes);
        if (pacing.Schedule is { } schedule)
        {
            // Giới hạn lượt dời để cấu hình lạ (vd mọi ngày đều đầy) không lặp vô hạn.
            for (var guard = 0; guard < 400; guard++)
            {
                due = schedule.Fit(due);
                var day = schedule.LocalDay(due);
                if (taken.Count(t => schedule.LocalDay(t) == day) < pacing.DailyLimit) return due;
                due = schedule.NextDayStart(due);
            }
            return due;
        }
        while (true)
        {
            var window = taken.Where(t => t > due.AddDays(-1) && t <= due).ToList();
            if (window.Count < pacing.DailyLimit) return due;
            due = window[0].AddDays(1).AddSeconds(1);
        }
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;

    // Lỗi nghiệp vụ của lõi đăng bài → InvalidOperationException để màn/pipeline hiện lỗi như các lỗi khác.
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (PublishingException e) { throw new InvalidOperationException(e.Message, e); }
    }
}
