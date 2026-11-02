using System.Collections.ObjectModel;
using System.Globalization;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Persistence;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Analytics;
using SuperMOOO.Publishing.Automation;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Production;
using SuperMOOO.Publishing.Scheduling;

namespace SuperMOOO.App.Features.S22;

public sealed record Option<T>(T Value, string Label);

public enum AutomationChoice { SuggestOnly, ManualApproval, Auto }

public sealed record CampaignRow(Campaign Campaign, CampaignCounts Counts)
{
    public string Id => Campaign.Id;
    public string Name => Campaign.Name;
    public string State => Campaign.Status switch
    {
        CampaignStatus.Active => "Đang chạy", CampaignStatus.Paused => "Tạm dừng", _ => "Dừng hẳn"
    };
    public string Level => CampaignsText.Level(Campaign);
    public int Total => Counts.Total;
    public int Awaiting => Counts.AwaitingApproval;
    public int Upcoming => Counts.Upcoming;
}

public static class CampaignsText
{
    public static string Level(Campaign c) => c.SuggestOnly ? "Chỉ gợi ý" : c.Automation == AutomationLevel.AutoWithinLimits ? "Tự động" : "Cần duyệt";

    public static string Day(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "Thứ 2", DayOfWeek.Tuesday => "Thứ 3", DayOfWeek.Wednesday => "Thứ 4", DayOfWeek.Thursday => "Thứ 5",
        DayOfWeek.Friday => "Thứ 6", DayOfWeek.Saturday => "Thứ 7", _ => "Chủ nhật"
    };

    public static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Outcome(string outcome) => outcome switch
    {
        PlanOutcomes.Scheduled => "Tự đăng", PlanOutcomes.AwaitingApproval => "Chờ duyệt", PlanOutcomes.Suggested => "Gợi ý", _ => outcome
    };

    public static string State(ProductionState s) => s switch
    {
        ProductionState.Draft => "Đang tạo", ProductionState.Sent => "Đã gửi", ProductionState.Accepted => "Đã nhận", ProductionState.Running => "Đang sản xuất",
        ProductionState.Completed => "Xong", ProductionState.Failed => "Lỗi", ProductionState.Rejected => "Bị từ chối", ProductionState.Expired => "Hết hạn",
        _ => "Đã huỷ"
    };
}

public sealed partial class RuleEditor : ObservableObject
{
    public string Id { get; init; } = "";
    public ObservableCollection<DestinationOption> Destinations { get; init; } = [];

    [ObservableProperty]
    public partial string SourceId { get; set; } = "";

    [ObservableProperty]
    public partial string Tags { get; set; } = "";

    [ObservableProperty]
    public partial string Language { get; set; } = "";

    [ObservableProperty]
    public partial double? MinSeconds { get; set; }

    [ObservableProperty]
    public partial double? MaxSeconds { get; set; }

    [ObservableProperty]
    public partial AspectFilter Aspect { get; set; }
}

public sealed record SlotRow(CampaignSlot Slot, string DestinationText)
{
    public string Text => $"{CampaignsText.Day(Slot.Day)} {CampaignsText.Hm(Slot.Time)}";
}

public sealed record SuggestRow(string? DestinationId, string Channel, DayOfWeek Day, TimeOnly Time, string Detail)
{
    public string Text => $"{Channel}: {CampaignsText.Day(Day)} {CampaignsText.Hm(Time)} (giờ chiến dịch) · {Detail}";
}

public sealed record PreviewRow(string When, string Channel, string Content, string Title, string Outcome, string Warnings);

public sealed partial class ApprovalRow(PublicationRow row) : ObservableObject
{
    public string Id => row.Publication.Id;
    public string Title => row.Publication.Title;
    public string Channel => row.DestinationTitle;
    public string When => row.Publication.ScheduledUtc is { } at ? VietnamTime.ToLocal(at).ToString("dd/MM/yyyy HH:mm") : "";
    public string Campaign => row.CampaignName ?? "";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

public sealed record RequestRow(ProductionRequest Request)
{
    public string Id => Request.Id;
    public string Topic => Request.Topic;
    public string Producer => Producers.DisplayName(Request.Producer);
    public string State => CampaignsText.State(Request.State);
    public string Progress => ProductionStates.IsOpen(Request.State) ? Request.Progress + "%" : "";
    public string Message => Request.Message ?? "";
    public string Created => Request.CreatedUtc.ToLocalTime().ToString("dd/MM HH:mm");
    public bool IsOpen => ProductionStates.IsOpen(Request.State);
}

[Service(ServiceLifetime.Transient)]
public sealed partial class CampaignsViewModel(CampaignService service, ProductionOrderPlanner production, ProductionService productionService,
    IProductionRepository requests, ISourceRepository sources, OAuthConnectService oauth, AnalyticsService analytics, IDialogService dialogs) : ObservableObject
{
    private List<DestinationDto> _destinations = [];
    private bool _loading;

    public ObservableCollection<CampaignRow> Campaigns { get; } = [];
    public ObservableCollection<RuleEditor> Rules { get; } = [];
    public ObservableCollection<SlotRow> Slots { get; } = [];
    public ObservableCollection<SuggestRow> Suggestions { get; } = [];
    public ObservableCollection<PreviewRow> PreviewRows { get; } = [];
    public ObservableCollection<string> PreviewNotes { get; } = [];
    public ObservableCollection<ApprovalRow> Approvals { get; } = [];
    public ObservableCollection<RequestRow> Requests { get; } = [];
    public ObservableCollection<Option<string>> SourceOptions { get; } = [];
    public ObservableCollection<Option<string>> SlotDestinations { get; } = [];

    public IReadOnlyList<Option<AutomationChoice>> LevelOptions { get; } =
    [
        new(AutomationChoice.SuggestOnly, "Chỉ gợi ý (không tạo bài, bạn xem trước và tự đăng)"),
        new(AutomationChoice.ManualApproval, "Cần duyệt (tạo bài chờ bạn duyệt từng bài)"),
        new(AutomationChoice.Auto, "Tự động (tự đăng trong giới hạn, bài có cảnh báo vẫn chờ duyệt)")
    ];

    public IReadOnlyList<Option<PublishPrivacy>> PrivacyOptions { get; } =
        [new(PublishPrivacy.Private, "Riêng tư"), new(PublishPrivacy.Unlisted, "Không công khai"), new(PublishPrivacy.Public, "Công khai")];

    public IReadOnlyList<Option<LateMode>> LateOptions { get; } =
    [
        new(LateMode.Skip, "Bỏ bài trễ"), new(LateMode.Shift, "Dời sang slot kế tiếp"), new(LateMode.CatchUp, "Đăng bù (có trần)")
    ];

    public IReadOnlyList<Option<AspectFilter>> AspectOptions { get; } =
    [
        new(AspectFilter.Any, "Mọi tỉ lệ"), new(AspectFilter.Landscape, "Ngang (16:9)"), new(AspectFilter.Portrait, "Dọc (9:16)"), new(AspectFilter.Square, "Vuông")
    ];

    public IReadOnlyList<Option<DayOfWeek>> DayOptions { get; } =
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new Option<DayOfWeek>(d, CampaignsText.Day(d)))];

    public IReadOnlyList<Option<string>> ProdAspectOptions { get; } =
        [new("", "Theo quy tắc chiến dịch"), new(ProductionAspects.Landscape, "16:9"), new(ProductionAspects.Portrait, "9:16")];

    public IReadOnlyList<Option<string>> ProducerOptions { get; } =
        [new("", "Tự chọn"), new(Producers.AIVideoStudio, Producers.DisplayName(Producers.AIVideoStudio)), new(Producers.VideoC, Producers.DisplayName(Producers.VideoC))];

    public IReadOnlyList<string> TimeZoneOptions { get; } =
        ["Asia/Ho_Chi_Minh", "Asia/Bangkok", "Asia/Tokyo", "Asia/Seoul", "Europe/London", "America/New_York", "America/Los_Angeles", "UTC"];

    [ObservableProperty]
    public partial CampaignRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoEditor))]
    public partial bool HasEditor { get; set; }

    public bool NoEditor => !HasEditor;

    [ObservableProperty]
    public partial string Banner { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    // --- Chiến dịch ---
    [ObservableProperty]
    public partial string EditId { get; set; } = "";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string TimeZoneId { get; set; } = "Asia/Ho_Chi_Minh";

    [ObservableProperty]
    public partial double Weight { get; set; } = 1;

    [ObservableProperty]
    public partial AutomationChoice Level { get; set; } = AutomationChoice.ManualApproval;

    [ObservableProperty]
    public partial double? PostBudget { get; set; }

    [ObservableProperty]
    public partial double? ContentMaxAgeDays { get; set; }

    [ObservableProperty]
    public partial int MaxConsecutiveFailures { get; set; } = 3;

    [ObservableProperty]
    public partial string TitleTemplate { get; set; } = "{title}";

    [ObservableProperty]
    public partial string DescriptionTemplate { get; set; } = "{caption}";

    [ObservableProperty]
    public partial PublishPrivacy Privacy { get; set; } = PublishPrivacy.Private;

    [ObservableProperty]
    public partial bool Evergreen { get; set; }

    [ObservableProperty]
    public partial int EvergreenMinDays { get; set; } = 30;

    // --- Lịch ---
    [ObservableProperty]
    public partial int MaxPerDay { get; set; } = 3;

    [ObservableProperty]
    public partial int MinGapMinutes { get; set; } = 120;

    [ObservableProperty]
    public partial int JitterMinutes { get; set; }

    [ObservableProperty]
    public partial string ForbiddenText { get; set; } = "";

    [ObservableProperty]
    public partial LateMode LateMode { get; set; } = LateMode.Skip;

    [ObservableProperty]
    public partial int LateGraceMinutes { get; set; } = 15;

    [ObservableProperty]
    public partial int LateMaxCatchUp { get; set; } = 1;

    [ObservableProperty]
    public partial int LateMaxLatenessMinutes { get; set; } = 360;

    [ObservableProperty]
    public partial int HorizonDays { get; set; } = 7;

    [ObservableProperty]
    public partial DayOfWeek NewSlotDay { get; set; } = DayOfWeek.Monday;

    [ObservableProperty]
    public partial string NewSlotTime { get; set; } = "20:00";

    [ObservableProperty]
    public partial string NewSlotDestinationId { get; set; } = "";

    // --- Sản xuất ---
    [ObservableProperty]
    public partial bool ProdEnabled { get; set; }

    [ObservableProperty]
    public partial string ProdTopics { get; set; } = "";

    [ObservableProperty]
    public partial string ProdLanguage { get; set; } = "";

    [ObservableProperty]
    public partial string ProdAspect { get; set; } = "";

    [ObservableProperty]
    public partial string ProdProducer { get; set; } = "";

    [ObservableProperty]
    public partial double ProdBufferDays { get; set; } = 3;

    [ObservableProperty]
    public partial double ProdTargetDays { get; set; } = 7;

    [ObservableProperty]
    public partial int ProdMaxOrder { get; set; } = 5;

    [ObservableProperty]
    public partial string ProdMeasure { get; set; } = "";

    [ObservableProperty]
    public partial string OrderTopic { get; set; } = "";

    [ObservableProperty]
    public partial RequestRow? SelectedRequest { get; set; }

    partial void OnSelectedChanged(CampaignRow? value)
    {
        if (!_loading) _ = LoadEditorAsync(value);
    }

    public async Task LoadAsync()
    {
        await Guard(async () =>
        {
            _destinations = [.. (await oauth.ListDestinationsAsync(new ListDestinationsRequest(null), default)).Where(d => d.Enabled && !d.Removed)];
            var all = await sources.ListAsync(default);
            SourceOptions.Sync([new Option<string>("", "Mọi nguồn"), .. all.Select(s => new Option<string>(s.Id, s.Name))], o => o.Value, o => o.Label);
            SlotDestinations.Sync([new Option<string>("", "Mọi kênh của chiến dịch"), .. _destinations.Select(d => new Option<string>(d.Id, DestinationLabel(d)))],
                o => o.Value, o => o.Label);
            await ReloadListAsync(Selected?.Id);
        });
    }

    private async Task ReloadListAsync(string? keep)
    {
        var rows = (await service.ListAsync(default)).Select(s => new CampaignRow(s.Campaign, s.Counts)).ToList();
        _loading = true;
        try
        {
            Campaigns.Sync(rows, r => r.Id, r => (r.Name, r.State, r.Level, r.Total, r.Awaiting, r.Upcoming));
            Selected = Campaigns.FirstOrDefault(r => r.Id == keep);
        }
        finally { _loading = false; }
        var active = rows.Count(r => r.Campaign.Status == CampaignStatus.Active);
        Summary = $"{rows.Count} chiến dịch · {active} đang chạy · {rows.Sum(r => r.Awaiting)} bài chờ duyệt";
        if (Selected is not null) Banner = BannerOf(Selected.Campaign);
    }

    private static string BannerOf(Campaign c) => c.Status == CampaignStatus.Active || string.IsNullOrWhiteSpace(c.StatusReason) ? ""
        : $"{(c.StatusReason.StartsWith("Người dùng", StringComparison.Ordinal) ? "Đã dừng" : "Đã tự dừng")}: {c.StatusReason}";

    private static string DestinationLabel(DestinationDto d) => $"{Labels.Term(d.Platform)} · {d.Title} ({d.ConnectionName})";

    private IEnumerable<DestinationOption> NewDestinationOptions(IEnumerable<string> selected)
    {
        var set = selected.ToHashSet();
        return _destinations.Select(d => new DestinationOption(d) { IsSelected = set.Contains(d.Id) });
    }

    // --- Nạp / tạo bản nháp ---

    [RelayCommand]
    private void New()
    {
        _loading = true;
        Selected = null;
        _loading = false;
        Banner = "";
        EditId = Guid.NewGuid().ToString("N");
        Name = "";
        TimeZoneId = "Asia/Ho_Chi_Minh";
        Weight = 1;
        Level = AutomationChoice.ManualApproval;
        PostBudget = null;
        ContentMaxAgeDays = null;
        MaxConsecutiveFailures = 3;
        TitleTemplate = "{title}";
        DescriptionTemplate = "{caption}";
        Privacy = PublishPrivacy.Private;
        Evergreen = false;
        EvergreenMinDays = 30;
        _platformSettings = null;
        ApplyPolicy(new SchedulePolicy());
        Rules.Clear();
        AddRule();
        ApplyProduction(new CampaignProductionView(new CampaignProduction(), new StockMeasure(0, 0), 0));
        Requests.Clear();
        Approvals.Clear();
        PreviewRows.Clear();
        PreviewNotes.Clear();
        Suggestions.Clear();
        HasEditor = true;
    }

    private string? _platformSettings;

    private async Task LoadEditorAsync(CampaignRow? row)
    {
        if (row is null)
        {
            if (!_loading) HasEditor = false;
            return;
        }
        await Guard(async () =>
        {
            var plan = await service.GetAsync(row.Id, default);
            var c = plan.Campaign;
            EditId = c.Id;
            Name = c.Name;
            TimeZoneId = c.TimeZoneId;
            Weight = c.Weight;
            Level = c.SuggestOnly ? AutomationChoice.SuggestOnly : c.Automation == AutomationLevel.AutoWithinLimits ? AutomationChoice.Auto : AutomationChoice.ManualApproval;
            PostBudget = c.PostBudget;
            ContentMaxAgeDays = c.ContentMaxAgeDays;
            MaxConsecutiveFailures = c.MaxConsecutiveFailures;
            TitleTemplate = c.TitleTemplate;
            DescriptionTemplate = c.DescriptionTemplate;
            Privacy = c.Privacy;
            Evergreen = c.Evergreen;
            EvergreenMinDays = c.EvergreenMinDays;
            _platformSettings = c.PlatformSettingsJson;
            ApplyPolicy(plan.Policy);
            Rules.Clear();
            foreach (var r in plan.Rules.OrderBy(r => r.Position))
                Rules.Add(new RuleEditor
                {
                    Id = r.Id, SourceId = r.SourceId ?? "", Tags = string.Join(", ", r.Tags), Language = r.Language ?? "", MinSeconds = r.MinDurationSec,
                    MaxSeconds = r.MaxDurationSec, Aspect = r.Aspect, Destinations = [.. NewDestinationOptions(r.DestinationIds)]
                });
            Banner = BannerOf(c);
            PreviewRows.Clear();
            PreviewNotes.Clear();
            Suggestions.Clear();
            HasEditor = true;
            ApplyProduction(await production.GetAsync(c.Id, default));
            await RefreshRequestsAsync();
            await RefreshApprovalsAsync();
        });
    }

    private void ApplyPolicy(SchedulePolicy p)
    {
        MaxPerDay = p.MaxPerDay;
        MinGapMinutes = p.MinGapMinutes;
        JitterMinutes = p.JitterMinutes;
        ForbiddenText = string.Join(Environment.NewLine, p.Forbidden.Select(w => $"{CampaignsText.Hm(w.Start)}-{CampaignsText.Hm(w.End)}"));
        LateMode = p.LateMode;
        LateGraceMinutes = p.LateGraceMinutes;
        LateMaxCatchUp = p.LateMaxCatchUp;
        LateMaxLatenessMinutes = p.LateMaxLatenessMinutes;
        HorizonDays = p.HorizonDays;
        Slots.Clear();
        foreach (var s in p.Slots.OrderBy(s => ((int)s.Day + 6) % 7).ThenBy(s => s.Time)) Slots.Add(new SlotRow(s, SlotDestinationText(s.DestinationId)));
    }

    private string SlotDestinationText(string? id) =>
        id is null ? "Mọi kênh" : _destinations.FirstOrDefault(d => d.Id == id) is { } d ? DestinationLabel(d) : id;

    private void ApplyProduction(CampaignProductionView view)
    {
        var s = view.Settings;
        ProdEnabled = s.Enabled;
        ProdTopics = string.Join(Environment.NewLine, s.Topics);
        ProdLanguage = s.Language ?? "";
        ProdAspect = s.Aspect ?? "";
        ProdProducer = s.PreferredProducer ?? "";
        ProdBufferDays = s.BufferDays;
        ProdTargetDays = s.TargetDays;
        ProdMaxOrder = s.MaxOrder;
        ProdMeasure = $"Tồn kho: {view.Measure.Stock} video chưa đăng · tiêu thụ {view.Measure.PostsPerDay:0.##} bài/ngày · {view.OpenRequests} yêu cầu đang mở"
            + (s.Replenishing ? " · đang bổ sung tồn" : "");
    }

    // --- Lưu / trạng thái ---

    private CampaignPlan BuildPlan()
    {
        var now = DateTimeOffset.UtcNow;
        var campaign = new Campaign
        {
            Id = EditId, Name = Name, TimeZoneId = TimeZoneId, Weight = Weight,
            Automation = Level == AutomationChoice.Auto ? AutomationLevel.AutoWithinLimits : AutomationLevel.ManualApproval,
            SuggestOnly = Level == AutomationChoice.SuggestOnly,
            ContentMaxAgeDays = ContentMaxAgeDays is { } d ? (int)d : null, PostBudget = PostBudget is { } b ? (int)b : null,
            MaxConsecutiveFailures = MaxConsecutiveFailures, TitleTemplate = TitleTemplate, DescriptionTemplate = DescriptionTemplate, Privacy = Privacy,
            Evergreen = Evergreen, EvergreenMinDays = EvergreenMinDays, PlatformSettingsJson = _platformSettings, CreatedUtc = now, UpdatedUtc = now
        };
        var rules = Rules.Select((r, i) => new CampaignRule
        {
            Id = r.Id, CampaignId = EditId, Position = i + 1, SourceId = r.SourceId.Length == 0 ? null : r.SourceId,
            Tags = [.. r.Tags.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
            Language = r.Language, MinDurationSec = r.MinSeconds is { } mn ? (int)mn : null, MaxDurationSec = r.MaxSeconds is { } mx ? (int)mx : null,
            Aspect = r.Aspect, DestinationIds = [.. r.Destinations.Where(x => x.IsSelected).Select(x => x.Id)]
        }).ToList();
        var policy = new SchedulePolicy
        {
            CampaignId = EditId, Slots = [.. Slots.Select(s => s.Slot)], MaxPerDay = MaxPerDay, MinGapMinutes = MinGapMinutes, Forbidden = ParseForbidden(),
            JitterMinutes = JitterMinutes, LateMode = LateMode, LateGraceMinutes = LateGraceMinutes, LateMaxCatchUp = LateMaxCatchUp,
            LateMaxLatenessMinutes = LateMaxLatenessMinutes, HorizonDays = HorizonDays
        };
        return new CampaignPlan(campaign, rules, policy);
    }

    private List<TimeWindow> ParseForbidden()
    {
        var list = new List<TimeWindow>();
        foreach (var line in ForbiddenText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TryTime(parts[0], out var start) || !TryTime(parts[1], out var end))
                throw new PublishingException(PublishingErrors.InvalidRequest, $"Khung giờ cấm \"{line}\" phải dạng HH:mm-HH:mm.");
            list.Add(new TimeWindow(start, end));
        }
        return list;
    }

    private static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    [RelayCommand]
    private async Task SaveAsync()
    {
        await Guard(async () =>
        {
            var plan = await service.SaveAsync(BuildPlan(), default);
            await ReloadListAsync(plan.Campaign.Id);
            dialogs.Info("Chiến dịch", "Đã lưu. Chiến dịch mới ở trạng thái tạm dừng: bấm Bật khi sẵn sàng.");
        });
    }

    [RelayCommand]
    private Task ActivateAsync() => SetStatusAsync(CampaignStatus.Active);

    [RelayCommand]
    private Task PauseAsync() => SetStatusAsync(CampaignStatus.Paused);

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Selected is null || !await dialogs.ConfirmAsync("Dừng hẳn", "Dừng hẳn chiến dịch? Các bài chưa đăng của chiến dịch sẽ bị huỷ.")) return;
        await SetStatusAsync(CampaignStatus.Stopped);
    }

    private async Task SetStatusAsync(CampaignStatus status)
    {
        if (Selected is not { } row) return;
        await Guard(async () =>
        {
            await service.SetStatusAsync(row.Id, status, default);
            await ReloadListAsync(row.Id);
            dialogs.Info("Chiến dịch", status switch { CampaignStatus.Active => "Đã bật chiến dịch.", CampaignStatus.Paused => "Đã tạm dừng.", _ => "Đã dừng hẳn." });
        });
    }

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        if (!await dialogs.ConfirmAsync("Dừng tất cả", "Tạm dừng mọi chiến dịch đang chạy? Bài đã hẹn giữ nguyên và chờ tới khi bạn bật lại.", "Dừng tất cả")) return;
        await Guard(async () =>
        {
            var count = await service.PauseAllAsync(default);
            await ReloadListAsync(Selected?.Id);
            dialogs.Info("Chiến dịch", count == 0 ? "Không có chiến dịch nào đang chạy." : $"Đã tạm dừng {count} chiến dịch.");
        });
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is not { } row || !await dialogs.ConfirmAsync("Xoá chiến dịch", $"Xoá \"{row.Name}\"? Bài chưa đăng của chiến dịch sẽ bị huỷ; bài đã đăng được giữ.")) return;
        await Guard(async () =>
        {
            await service.RemoveAsync(row.Id, default);
            HasEditor = false;
            await ReloadListAsync(null);
        });
    }

    [RelayCommand]
    private async Task RunPlannerAsync()
    {
        await Guard(async () =>
        {
            var result = await service.RunPlannerAsync(default);
            await ReloadListAsync(Selected?.Id);
            dialogs.Info("Lập lịch", result.Created > 0 ? $"Đã tạo {result.Created} bài." : result.Notes.FirstOrDefault() ?? "Không có bài mới để tạo.");
        });
    }

    // --- Quy tắc / slot ---

    [RelayCommand]
    private void AddRule() => Rules.Add(new RuleEditor { Id = Guid.NewGuid().ToString("N"), Destinations = [.. NewDestinationOptions([])] });

    [RelayCommand]
    private void RemoveRule(RuleEditor? rule)
    {
        if (rule is not null) Rules.Remove(rule);
    }

    [RelayCommand]
    private void AddSlot()
    {
        if (!TryTime(NewSlotTime.Trim(), out var time))
        {
            dialogs.Error("Giờ slot phải dạng HH:mm (vd 20:00).");
            return;
        }
        AddSlotCore(new CampaignSlot(NewSlotDay, time, NewSlotDestinationId.Length == 0 ? null : NewSlotDestinationId));
    }

    private void AddSlotCore(CampaignSlot slot)
    {
        if (Slots.Any(s => s.Slot == slot)) return;
        Slots.Add(new SlotRow(slot, SlotDestinationText(slot.DestinationId)));
    }

    [RelayCommand]
    private void RemoveSlot(SlotRow? slot)
    {
        if (slot is not null) Slots.Remove(slot);
    }

    /// <summary>Gợi ý giờ từ BestTimeService (giờ Việt Nam) cho các kênh của chiến dịch, đổi sang múi giờ chiến dịch.</summary>
    [RelayCommand]
    private async Task SuggestSlotsAsync()
    {
        await Guard(async () =>
        {
            var destIds = Rules.SelectMany(r => r.Destinations.Where(d => d.IsSelected).Select(d => d.Id)).ToHashSet();
            if (destIds.Count == 0)
            {
                dialogs.Error("Chọn kênh đích ở tab Quy tắc trước.");
                return;
            }
            var tz = TimeZones.Find(TimeZoneId) ?? TimeZoneInfo.Utc;
            var best = await analytics.GetBestTimesAsync(new GetBestTimesRequest(), default);
            Suggestions.Clear();
            foreach (var ch in best.Channels.Where(c => destIds.Contains(c.DestinationId)))
            {
                if (ch.Status != BestTimeStatuses.Ok)
                {
                    Suggestions.Add(new SuggestRow(null, ch.Title, DayOfWeek.Monday, TimeOnly.MinValue, ch.Message));
                    continue;
                }
                foreach (var b in ch.Buckets)
                {
                    var (day, time) = ToCampaignTime(Enum.Parse<DayOfWeek>(b.Day), b.Hour, tz);
                    Suggestions.Add(new SuggestRow(ch.DestinationId, ch.Title, day, time, $"{b.Score:0.##}/1000 follower, {b.Samples} bài"));
                }
            }
            if (Suggestions.Count == 0) dialogs.Info("Gợi ý giờ", "Chưa có dữ liệu để gợi ý.");
        });
    }

    private static (DayOfWeek Day, TimeOnly Time) ToCampaignTime(DayOfWeek vnDay, int vnHour, TimeZoneInfo tz)
    {
        // Lấy một tuần mẫu cố định để quy đổi (DST của múi chiến dịch lệch tối đa 1 giờ, chấp nhận vì chỉ là gợi ý).
        var ref0 = new DateTime(2026, 1, 5).AddDays(((int)vnDay + 6) % 7).AddHours(vnHour);
        var utc = new DateTimeOffset(ref0, VietnamTime.Offset).UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        return (local.DayOfWeek, TimeOnly.FromDateTime(local));
    }

    [RelayCommand]
    private void AddSuggestion(SuggestRow? row)
    {
        if (row?.DestinationId is null) return;
        AddSlotCore(new CampaignSlot(row.Day, row.Time, row.DestinationId));
    }

    // --- Xem trước ---

    [RelayCommand]
    private async Task PreviewAsync()
    {
        await Guard(async () =>
        {
            var plan = BuildPlan();
            var result = await service.PreviewAsync(plan, 7, default);
            PreviewRows.Clear();
            foreach (var e in result.Entries.OrderBy(e => e.Post.ScheduledUtc))
                PreviewRows.Add(new PreviewRow(CampaignSchedules.LocalText(plan.Campaign, e.Post.ScheduledUtc), e.Post.Destination.Title, e.Post.Content.Title, e.Post.Title,
                    CampaignsText.Outcome(e.Outcome), string.Join(" ", e.Warnings.Select(w => w.Message))));
            PreviewNotes.Clear();
            foreach (var n in result.Notes) PreviewNotes.Add(n);
            if (PreviewRows.Count == 0 && PreviewNotes.Count == 0) PreviewNotes.Add("Không có bài nào trong 7 ngày tới (thiếu slot, kênh hoặc nội dung khớp).");
        });
    }

    // --- Sản xuất ---

    [RelayCommand]
    private async Task SaveProductionAsync()
    {
        await Guard(async () =>
        {
            var topics = ProdTopics.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var view = await production.SetAsync(new CampaignProduction
            {
                CampaignId = EditId, Enabled = ProdEnabled, Topics = topics, Language = ProdLanguage, Aspect = ProdAspect.Length == 0 ? null : ProdAspect,
                BufferDays = ProdBufferDays, TargetDays = ProdTargetDays, MaxOrder = ProdMaxOrder, PreferredProducer = ProdProducer.Length == 0 ? null : ProdProducer
            }, default);
            ApplyProduction(view);
            dialogs.Info("Sản xuất", "Đã lưu cài đặt sản xuất.");
        });
    }

    [RelayCommand]
    private async Task OrderNowAsync()
    {
        if (Selected is not { } row)
        {
            dialogs.Error("Lưu chiến dịch trước khi đặt sản xuất.");
            return;
        }
        await Guard(async () =>
        {
            var topic = OrderTopic.Trim();
            if (topic.Length == 0) topic = ProdTopics.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var plan = await service.GetAsync(row.Id, default);
            var aspect = ProdAspect.Length > 0 ? ProdAspect
                : plan.Rules.Any(r => r.Aspect == AspectFilter.Portrait) ? ProductionAspects.Portrait : ProductionAspects.Landscape;
            var language = ProdLanguage.Trim().Length > 0 ? ProdLanguage.Trim() : plan.Rules.Select(r => r.Language).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "vi";
            var result = await productionService.CreateAsync(new NewProductionRequest(topic, language, aspect, null, null, row.Id, "Đặt tay từ S22.",
                PreferredProducer: ProdProducer.Length == 0 ? null : ProdProducer), default);
            if (result.Request is null) dialogs.Error(result.Error ?? "Không đặt được sản xuất.");
            else
            {
                dialogs.Info("Sản xuất", "Đã đặt sản xuất; theo dõi ở khung tác vụ bên phải. Video xong tự vào Kho thành phẩm.");
                OrderTopic = "";
            }
            await RefreshRequestsAsync();
        });
    }

    [RelayCommand]
    private async Task CancelRequestAsync()
    {
        if (SelectedRequest is not { IsOpen: true } r) return;
        await Guard(async () =>
        {
            await productionService.CancelAsync(r.Id, default);
            await RefreshRequestsAsync();
        });
    }

    [RelayCommand]
    private Task ReloadRequestsAsync() => Guard(RefreshRequestsAsync);

    private async Task RefreshRequestsAsync()
    {
        if (Selected is not { } row) return;
        await productionService.SyncAsync(default);
        Requests.Sync([.. (await requests.ListRecentForCampaignAsync(row.Id, 30, default)).Select(r => new RequestRow(r))], r => r.Id, r => (r.State, r.Progress, r.Message));
        ApplyProduction(await production.GetAsync(row.Id, default));
    }

    // --- Duyệt ---

    [RelayCommand]
    private Task ReloadApprovalsAsync() => Guard(RefreshApprovalsAsync);

    private async Task RefreshApprovalsAsync()
    {
        var rows = await service.ListAwaitingApprovalAsync(Selected?.Id, default);
        Approvals.Sync([.. rows.Select(r => new ApprovalRow(r))], r => r.Id, r => r.When);
    }

    [RelayCommand]
    private void SelectAllApprovals()
    {
        var all = Approvals.Any(a => !a.IsChecked);
        foreach (var a in Approvals) a.IsChecked = all;
    }

    [RelayCommand]
    private Task ApproveAsync() => ResolveApprovalsAsync(approve: true);

    [RelayCommand]
    private Task RejectAsync() => ResolveApprovalsAsync(approve: false);

    private async Task ResolveApprovalsAsync(bool approve)
    {
        var ids = Approvals.Where(a => a.IsChecked).Select(a => a.Id).ToList();
        if (ids.Count == 0)
        {
            dialogs.Error("Tick chọn bài cần " + (approve ? "duyệt." : "từ chối."));
            return;
        }
        await Guard(async () =>
        {
            var result = approve ? await service.ApproveAsync(ids, default) : await service.RejectAsync(ids, default);
            await RefreshApprovalsAsync();
            await ReloadListAsync(Selected?.Id);
            if (result.Errors.Count > 0) dialogs.Error(string.Join("\n", result.Errors));
            if (result.Succeeded.Count > 0) dialogs.Info("Chiến dịch", (approve ? "Đã duyệt " : "Đã từ chối ") + result.Succeeded.Count + " bài.");
        });
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (PublishingException ex) { dialogs.Error(ex.Message); }
    }
}
