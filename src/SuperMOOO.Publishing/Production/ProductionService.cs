using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Http;
using SuperMOOO.Publishing.OAuth;

namespace SuperMOOO.Publishing.Production;

public sealed record NewProductionRequest(
    string Topic, string Language, string Aspect, int? TargetDurationSec, string? Producer, string? CampaignId, string? Notes, bool Auto = false, string? PreferredProducer = null);

/// <param name="Blocking">true khi không đặt được vì cấu hình/producer (người dùng phải xử lý), false khi chỉ chạm hạn mức tạm thời.</param>
public sealed record CreateProductionResult(ProductionRequest? Request, string? Error, bool Blocking);

/// <summary>
/// Đặt sản xuất cho pipeline nội bộ (<see cref="IContentProducer"/>: Talkshow 16:9, Whiteboard 16:9/9:16). Mỗi yêu cầu có RequestId ổn định:
/// producer chống trùng theo RequestId nên đặt lại sau crash không tạo job thứ hai; video xong tự vào kho (PipelineLibraryObserver) kèm request_id.
/// Trạng thái yêu cầu theo job: Running → Completed / Failed / Cancelled.
/// </summary>
[Service]
public sealed class ProductionService(
    IProductionRepository requests,
    IProductionConfigRepository configs,
    ICampaignRepository campaigns,
    IEnumerable<IContentProducer> producers,
    IJobQueue jobs,
    IActionRequiredRepository actions,
    IAuditLog audit,
    IClock clock)
{
    public const int MaxTopic = 500;
    public const int MaxLanguage = 100;
    public const int MaxNotes = 2000;
    /// <summary>Yêu cầu Draft quá lâu mà chưa có job: producer đã lỗi giữa chừng (vd crash), coi là thất bại.</summary>
    private static readonly TimeSpan DraftStale = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IReadOnlyList<IContentProducer> _producers = [.. producers];

    public async Task<Dictionary<string, ProducerConfig>> LoadConfigsAsync(CancellationToken ct)
    {
        var saved = (await configs.ListProducersAsync(ct)).ToDictionary(c => c.Producer);
        return Producers.All.ToDictionary(p => p, p => saved.GetValueOrDefault(p) ?? ProducerConfig.Default(p));
    }

    public async Task<CreateProductionResult> CreateAsync(NewProductionRequest r, CancellationToken ct)
    {
        var topic = r.Topic?.Trim() ?? "";
        var language = r.Language?.Trim() ?? "";
        var notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
        if (topic.Length == 0 || topic.Length > MaxTopic) return Fail($"Chủ đề phải có 1..{MaxTopic} ký tự.");
        if (language.Length == 0 || language.Length > MaxLanguage) return Fail($"Ngôn ngữ phải có 1..{MaxLanguage} ký tự.");
        if (!ProductionAspects.IsValid(r.Aspect)) return Fail("Tỉ lệ phải là 16:9 hoặc 9:16.");
        if (r.TargetDurationSec is <= 0) return Fail("Thời lượng mục tiêu phải > 0.");
        if (notes is { Length: > MaxNotes }) return Fail($"Ghi chú tối đa {MaxNotes} ký tự.");
        if (r.Producer is not null && !Producers.IsKnown(r.Producer)) return Fail("Producer không hợp lệ.");
        if (r.CampaignId is not null && await campaigns.GetAsync(r.CampaignId, ct) is null) return Fail("Không tìm thấy chiến dịch.");

        await _gate.WaitAsync(ct);
        try
        {
            if (r.Auto && r.CampaignId is not null && await requests.CountOpenAsync(null, r.CampaignId, ct) > 0)
                return Fail("Chiến dịch đã có yêu cầu sản xuất đang mở.");

            var now = clock.UtcNow;
            var all = await LoadConfigsAsync(ct);
            var order = r.Producer is not null ? [r.Producer] : OrderCandidates(r.PreferredProducer);
            var reasons = new List<(string Text, bool Limit)>();
            ProducerConfig? chosen = null;
            IContentProducer? impl = null;
            foreach (var p in order)
            {
                var c = all[p];
                var name = Producers.DisplayName(p);
                impl = _producers.FirstOrDefault(x => x.Id == p);
                if (!c.Enabled) reasons.Add(($"{name} đang tắt.", false));
                else if (impl is null) reasons.Add(($"{name} chưa sẵn sàng trong app.", false));
                else if (!c.SupportedAspects.Contains(r.Aspect) || !impl.SupportedAspects.Contains(r.Aspect)) reasons.Add(($"{name} không hỗ trợ tỉ lệ {r.Aspect}.", false));
                else if (await requests.CountOpenAsync(p, null, ct) >= c.MaxOpenRequests) reasons.Add(($"{name} đã đủ {c.MaxOpenRequests} yêu cầu đang mở.", true));
                else if (await requests.CountCreatedSinceAsync(p, DayStart(now), ct) >= c.DailyRequestBudget)
                    reasons.Add(($"{name} đã hết ngân sách {c.DailyRequestBudget} yêu cầu hôm nay.", true));
                else
                {
                    chosen = c;
                    break;
                }
            }
            if (chosen is null || impl is null) return new CreateProductionResult(null, string.Join(" ", reasons.Select(x => x.Text)), reasons.All(x => !x.Limit));

            var request = new ProductionRequest
            {
                Id = Guid.NewGuid().ToString("N"), Producer = chosen.Producer, CampaignId = r.CampaignId, Topic = topic, Language = language,
                Aspect = r.Aspect, TargetDurationSec = r.TargetDurationSec, Notes = notes, State = ProductionState.Draft,
                AutoGenerated = r.Auto, CreatedUtc = now, UpdatedUtc = now, ExpiresUtc = now + TimeSpan.FromHours(chosen.RequestTtlHours)
            };
            await requests.AddAsync(request, ct);
            try
            {
                request.ProducerJobId = await impl.EnqueueAsync(new ProductionOrder(request.Id, topic, language, r.Aspect, r.TargetDurationSec, notes, r.CampaignId), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Lỗi producer (LLM viết kịch bản, pipeline không khởi tạo được): ghi vào yêu cầu để người dùng thấy và đặt lại.
                request.State = ProductionState.Failed;
                request.Message = $"Không xếp được job sản xuất: {e.Message}";
                request.UpdatedUtc = clock.UtcNow;
                await requests.UpdateAsync(request, [ProductionState.Draft], ct);
                await actions.RaiseAsync(ActionKinds.ProductionRequestFailed, request.Id, $"Đặt sản xuất \"{Short(topic)}\" thất bại", request.Message, ct);
                return Fail(request.Message);
            }
            request.State = ProductionState.Running;
            request.SentUtc = clock.UtcNow;
            request.UpdatedUtc = request.SentUtc.Value;
            await requests.UpdateAsync(request, [ProductionState.Draft], ct);
            await audit.WriteAsync(r.Auto ? "production" : "ui", "production.create", request.Id, $"{chosen.Producer}; {request.Aspect}; {Short(topic)}", ct);
            return new CreateProductionResult(request, null, false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hủy yêu cầu còn mở: hủy luôn job sản xuất đang xếp/chạy.</summary>
    public async Task CancelAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var r = await requests.GetAsync(id, ct) ?? throw new PublishingException(PublishingErrors.NotFound, "Không tìm thấy yêu cầu sản xuất.");
            if (!ProductionStates.IsOpen(r.State)) throw new PublishingException(PublishingErrors.InvalidRequest, $"Yêu cầu đã kết thúc ({r.State}).");
            if (r.ProducerJobId is { Length: > 0 } jobId) await jobs.CancelAsync(jobId);
            var from = ProductionStates.Open;
            r.State = ProductionState.Cancelled;
            r.Message = "Người dùng đã hủy.";
            r.UpdatedUtc = clock.UtcNow;
            if (!await requests.UpdateAsync(r, from, ct)) throw new PublishingException(PublishingErrors.InvalidRequest, "Yêu cầu vừa đổi trạng thái; tải lại.");
            await actions.ResolveByRefAsync(ActionKinds.ProductionRequestFailed, r.Id, ct);
            await audit.WriteAsync("ui", "production.cancel", r.Id, null, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Đồng bộ trạng thái yêu cầu còn mở theo job sản xuất tương ứng. Trả số yêu cầu đã đổi trạng thái.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.UtcNow;
            var changed = 0;
            foreach (var r in await requests.ListByStatesAsync(ProductionStates.Open, null, null, ct))
            {
                var (state, progress, message) = await ResolveAsync(r, now, ct);
                var progressPct = (int)Math.Round(progress * 100);
                if (state == r.State && progressPct == r.Progress) continue;
                var from = r.State;
                r.State = state;
                r.Progress = progressPct;
                r.Message = message ?? r.Message;
                r.UpdatedUtc = now;
                if (!await requests.UpdateAsync(r, [from], ct)) continue;
                if (state != from) changed++;
                if (state is ProductionState.Failed or ProductionState.Expired)
                    await actions.RaiseAsync(state == ProductionState.Failed ? ActionKinds.ProductionRequestFailed : ActionKinds.ProductionRequestExpired, r.Id,
                        $"Yêu cầu sản xuất \"{Short(r.Topic)}\" {(state == ProductionState.Failed ? "thất bại" : "hết hạn")}", r.Message ?? "", ct);
            }
            return changed;
        }
        finally { _gate.Release(); }
    }

    private async Task<(ProductionState State, double Progress, string? Message)> ResolveAsync(ProductionRequest r, DateTimeOffset now, CancellationToken ct)
    {
        if (r.ProducerJobId is not { Length: > 0 } jobId)
            return now - r.CreatedUtc > DraftStale ? (ProductionState.Failed, 0, "Không xếp được job sản xuất (app đóng giữa chừng).") : (r.State, r.Progress / 100.0, null);
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null)
            return r.ExpiresUtc is { } exp && now > exp ? (ProductionState.Expired, 0, "Không còn job sản xuất và đã quá hạn.") : (r.State, r.Progress / 100.0, null);
        return job.Status switch
        {
            JobStatus.Succeeded => (ProductionState.Completed, 1, "Đã xong; video nằm trong Kho thành phẩm."),
            JobStatus.Failed or JobStatus.WaitingManualRetry => (ProductionState.Failed, job.Progress, job.Error ?? "Job sản xuất lỗi."),
            JobStatus.Cancelled => (ProductionState.Cancelled, job.Progress, "Job sản xuất đã bị hủy."),
            _ => (ProductionState.Running, job.Progress, job.Message)
        };
    }

    private static List<string> OrderCandidates(string? preferred) =>
        (preferred is not null && Producers.IsKnown(preferred) ? new[] { preferred } : [])
            .Concat(Producers.All).Distinct().ToList();

    private static DateTimeOffset DayStart(DateTimeOffset now) => new(now.UtcDateTime.Date, TimeSpan.Zero);

    private static CreateProductionResult Fail(string error) => new(null, error, false);

    private static string Short(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
