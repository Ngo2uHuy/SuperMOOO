using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S07;
using SuperMOOO.Media.Effects;
using SuperMOOO.App.Features.S14;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Infrastructure.Network;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;
using SuperMOOO.Workflows.Auto;
using SuperMOOO.Workflows.AutoReup;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Reup;

namespace SuperMOOO.App.Features.S08;

public sealed record ReupChoice(string Value, string Name);

public sealed record RuleRow(ReupRuleView View)
{
    public string Id => View.Record.Id;
    public string Name => View.Record.Name;
    public string Source => View.Config.SourceUrl;
    public string State => View.Record.Enabled ? "Đang theo dõi" : "Tạm dừng";
    public string Counts => $"{View.Counts.GetValueOrDefault("pending")} chờ · {View.Counts.GetValueOrDefault("ready")} xong · {View.Counts.GetValueOrDefault("failed")} lỗi";
    public string Message => View.Record.Message ?? "";
}

public sealed record ReupItemRow(ReupItemView View)
{
    public string Id => View.Record.Id;
    public string Title => View.Video.Title;
    public string Status => Labels.ReupItem(View.Record.Status);
    public string Detail => View.Record.Error ?? "";
    public string Posts => View.Posts.Count == 0 ? "" : $"{View.Posts.Count(p => p.Status == PublicationStatus.Published)}/{View.Posts.Count} đã đăng";
}

/// <summary>S08: luồng theo dõi trang nguồn, bộ lọc, cách xử lý video, lịch đăng và theo dõi từng video (đã đăng / chưa đăng, gửi lại).</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class AutoReupViewModel(AutoReupEngine engine, ReupStudioService reup, OAuthConnectService oauth, PublicationService publications,
    ReupMarketService markets, NetworkRouteBroker routes, IDialogService dialogs) : ObservableObject
{
    public static IReadOnlyList<string> Platforms { get; } = ReupRuleConfig.SourcePlatforms;
    public static IReadOnlyList<ReupChoice> Histories { get; } = [new("all", "Lấy cả video cũ"), new("new", "Chỉ video mới từ bây giờ"), new("limit", "Lấy N video gần nhất")];
    public static IReadOnlyList<ReupChoice> ContentTypes { get; } = [new("all", "Tất cả"), new("videos", "Video"), new("shorts", "Shorts"), new("streams", "Phát trực tiếp")];
    public static IReadOnlyList<ReupChoice> PublishModes { get; } =
        [new(ReupPublishModes.Review, "Chờ tôi duyệt rồi mới đăng"), new(ReupPublishModes.Automatic, "Tự đăng đúng lịch"), new(ReupPublishModes.Download, "Chỉ tải/xử lý, không đăng")];
    public static IReadOnlyList<ReupChoice> Processings { get; } =
        [new("none", "Giữ nguyên video"), new("reup", "Biến đổi bằng preset reup"), new(AutoModes.Localize, "Lồng tiếng"), new(AutoModes.Review, "Review"), new(AutoModes.Summary, "Tóm tắt")];
    public static IReadOnlyList<string> Privacies { get; } = ["private", "unlisted", "public"];
    private static readonly IReadOnlyList<ReupChoice> PrivacyChoices = [new("private", "Riêng tư"), new("unlisted", "Không công khai"), new("public", "Công khai")];
    private static readonly IReadOnlyList<ReupChoice> YesNo = [new("true", "Có"), new("false", "Không")];
    private static readonly IReadOnlyList<ReupChoice> LanguageChoices =
        [new("", "(Không đặt)"), .. SuperMOOO.AI.Catalog.LanguageCatalog.All.Select(l => new ReupChoice(l.Code, $"{l.Name} ({l.Code})"))];
    private static readonly IReadOnlyList<ReupChoice> ZoneChoices =
        [new("", "Giờ của máy này"), .. TimeZoneInfo.GetSystemTimeZones().Select(z => new ReupChoice(z.Id, z.DisplayName))];

    public IReadOnlyList<ReupPreset> Presets => reup.Presets;
    public ObservableCollection<RuleRow> Rules { get; } = [];
    public DestinationPickerViewModel Destinations { get; } = new(oauth);
    public ObservableCollection<ReupItemRow> Items { get; } = [];
    public ObservableCollection<DeliveryRow> Posts { get; } = [];
    public ObservableCollection<ReupDestinationRow> DestinationRows { get; } = [];
    /// <summary>ComboBox hồ sơ của từng kênh: theo tài khoản / các hồ sơ / cài đặt chung.</summary>
    public ObservableCollection<ReupChoice> ProfileChoices { get; } = [];
    public ObservableCollection<ReupChoice> SourceRoutes { get; } = [];
    public ObservableCollection<ReupPreviewRow> Previews { get; } = [];
    public IReadOnlyList<OverrideField> OverrideFields { get; } =
    [
        new("language", "Ngôn ngữ", LanguageChoices), new("translate", "Dịch tiêu đề video gốc", YesNo), new("zone", "Múi giờ", ZoneChoices),
        new("windows", "Khung giờ đăng (HH:mm-HH:mm, cách nhau dấu phẩy; trống = cả ngày)"), new("limit", "Tối đa bài/ngày"),
        new("hashtags", "Hashtag"), new("title", "Mẫu tiêu đề"), new("caption", "Mẫu mô tả", multiLine: true), new("privacy", "Quyền riêng tư", PrivacyChoices),
        new("days", "Ngày được đăng (bỏ chọn hết = mọi ngày)", days: true)
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomizing))]
    public partial ReupDestinationRow? CustomRow { get; set; }
    public bool IsCustomizing => CustomRow is not null;

    [ObservableProperty] public partial string SourceRouteId { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand), nameof(CheckNowCommand), nameof(DeleteRuleCommand))]
    public partial RuleRow? SelectedRule { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(RetryItemCommand))]
    public partial ReupItemRow? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResendCommand), nameof(CancelPostCommand))]
    public partial DeliveryRow? SelectedPost { get; set; }

    // --- Biên tập luồng ---
    [ObservableProperty] public partial string? EditingId { get; set; }
    [ObservableProperty] public partial string RuleName { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IntervalHint))] public partial string Platform { get; set; } = "tiktok";
    [ObservableProperty] public partial string SourceUrl { get; set; } = "";
    [ObservableProperty] public partial ReupChoice History { get; set; } = Histories[0];
    [ObservableProperty] public partial int HistoryLimit { get; set; } = 50;
    [ObservableProperty] public partial ReupChoice ContentType { get; set; } = ContentTypes[0];
    [ObservableProperty] public partial int IntervalMinutes { get; set; } = 10;
    public string IntervalHint => Platform == "instagram"
        ? $"Instagram quét tối thiểu {ReupRuleConfig.InstagramMinIntervalMinutes} phút một lần để tránh bị giới hạn tài khoản."
        : "";
    [ObservableProperty] public partial bool NewestFirst { get; set; } = true;
    [ObservableProperty] public partial string Include { get; set; } = "";
    [ObservableProperty] public partial string Exclude { get; set; } = "";
    [ObservableProperty] public partial double MinSeconds { get; set; }
    [ObservableProperty] public partial double MaxSeconds { get; set; }
    [ObservableProperty] public partial ReupChoice Processing { get; set; } = Processings[0];
    [ObservableProperty] public partial ReupPreset? Preset { get; set; }
    [ObservableProperty] public partial ReupChoice PublishMode { get; set; } = PublishModes[0];
    [ObservableProperty] public partial string TitleTemplate { get; set; } = "{title}";
    [ObservableProperty] public partial string CaptionTemplate { get; set; } = "{title}\nNguồn: {url}";
    [ObservableProperty] public partial string Privacy { get; set; } = "private";
    [ObservableProperty] public partial int DelayMinutes { get; set; }
    [ObservableProperty] public partial int SpacingMinutes { get; set; } = 10;
    [ObservableProperty] public partial int DailyLimit { get; set; } = 20;
    [ObservableProperty] public partial int Retries { get; set; } = 2;
    [ObservableProperty] public partial string Status { get; set; } = "";

    public async Task LoadAsync()
    {
        Preset ??= Presets.FirstOrDefault();
        await Guard(async () =>
        {
            await Destinations.LoadAsync();
            ReloadChoices();
            RebuildDestinationRows(null);
            await RefreshAsync();
        });
    }

    // ===== Kênh đích theo hồ sơ thị trường =====

    private List<ReupDestinationOptions>? _pendingOptions;

    private void ReloadChoices()
    {
        var keepRoute = SourceRouteId;
        SourceRoutes.Clear();
        SourceRoutes.Add(new ReupChoice("", "Trực tiếp (mạng chung của app)"));
        foreach (var r in routes.ListRoutes().Where(r => r.Enabled))
            SourceRoutes.Add(new ReupChoice(r.Id, r.ExpectedCountry is { Length: > 0 } c ? $"{r.Name} ({c})" : r.Name));
        SourceRouteId = keepRoute;

        ProfileChoices.Clear();
        ProfileChoices.Add(new ReupChoice(ReupDestinationRow.ByAccount, "Theo tài khoản"));
        foreach (var p in markets.Markets().Profiles) ProfileChoices.Add(new ReupChoice(p.Id, string.IsNullOrEmpty(p.Country) ? p.Name : $"{p.Name} ({p.Country})"));
        ProfileChoices.Add(new ReupChoice(ReupDestinationOptions.NoProfile, "Cài đặt chung"));
    }

    /// <summary>Dựng bảng kênh từ danh sách kênh đã nạp; <paramref name="options"/> null = giữ cấu hình đang sửa.</summary>
    private void RebuildDestinationRows(List<ReupDestinationOptions>? options)
    {
        // Danh sách kênh chưa nạp xong khi mở luồng: giữ cấu hình chờ tới lần dựng bảng sau.
        options ??= DestinationRows.Count > 0 ? [.. DestinationRows.Where(KeepOptions).Select(r => r.ToOptions())] : _pendingOptions ?? [];
        _pendingOptions = options;
        var hasProfiles = markets.Markets().Profiles.Count > 0;
        CustomRow = null;
        DestinationRows.Clear();
        foreach (var o in Destinations.Items)
        {
            var saved = options.FirstOrDefault(x => x.DestinationId == o.Id);
            var row = new ReupDestinationRow(o, _ => RefreshSchedules())
            {
                // Kênh mới (chưa bật) mặc định theo hồ sơ của tài khoản khi đã có hồ sơ; kênh đang bật mà luồng cũ chưa lưu cấu hình thì giữ cài đặt chung.
                ProfileId = saved is null ? (hasProfiles && !o.IsSelected ? ReupDestinationRow.ByAccount : ReupDestinationOptions.NoProfile)
                    : saved.MarketProfileId ?? ReupDestinationRow.ByAccount,
                Overrides = saved?.Overrides ?? new ReupOverrides()
            };
            // Hồ sơ đã bị xoá: giữ id để báo cảnh báo, thêm lựa chọn tạm cho ComboBox.
            if (row.ProfileId != ReupDestinationRow.ByAccount && ProfileChoices.All(c => c.Value != row.ProfileId))
                ProfileChoices.Add(new ReupChoice(row.ProfileId, "(Hồ sơ đã bị xoá)"));
            DestinationRows.Add(row);
        }
        RefreshSchedules();
    }

    /// <summary>Lưu kênh đang bật, có ghi đè, hoặc đã chọn hồ sơ khác mặc định (kể cả kênh đang tắt) để bật lại không mất cấu hình.</summary>
    private bool KeepOptions(ReupDestinationRow r) =>
        r.Option.IsSelected || !r.Overrides.IsEmpty
        || r.ProfileId != (markets.Markets().Profiles.Count > 0 ? ReupDestinationRow.ByAccount : ReupDestinationOptions.NoProfile);

    private void RefreshSchedules()
    {
        var config = BuildConfig();
        var settings = markets.Markets();
        foreach (var row in DestinationRows)
        {
            row.HasOverrides = !row.Overrides.IsEmpty;
            row.ProfileWarning = ReupMarketService.Resolve(config, row.Id, row.ConnectionId, settings).Warning ?? "";
            row.Schedule = ReupMarketService.Summary(ReupMarketService.Resolve(config, row.Id, row.ConnectionId, settings), config);
        }
    }

    partial void OnDailyLimitChanged(int value) => RefreshSchedules();
    partial void OnSpacingMinutesChanged(int value) => RefreshSchedules();

    [RelayCommand]
    private Task ReloadDestinationsAsync() => Guard(async () =>
    {
        // Lấy cấu hình trước khi nạp lại lựa chọn: xoá danh sách ComboBox làm binding ghi đè giá trị đang chọn.
        List<ReupDestinationOptions> keep = [.. DestinationRows.Select(r => r.ToOptions())];
        await Destinations.LoadAsync();
        ReloadChoices();
        RebuildDestinationRows(keep);
    });

    [RelayCommand]
    private void Customize(ReupDestinationRow? row)
    {
        if (row is null) return;
        CustomRow = row;
        var o = row.Overrides;
        // Giá trị kế thừa = hồ sơ của dòng (hoặc cài đặt chung), chưa tính ghi đè.
        var config = BuildConfig();
        config.Destinations = [.. config.Destinations.Where(d => d.DestinationId != row.Id),
            new ReupDestinationOptions { DestinationId = row.Id, MarketProfileId = row.ProfileId == ReupDestinationRow.ByAccount ? null : row.ProfileId }];
        var s = ReupMarketService.Resolve(config, row.Id, row.ConnectionId, markets.Markets());
        foreach (var f in OverrideFields)
            switch (f.Key)
            {
                case "language": f.Load(o.Language, s.Language); break;
                case "translate": f.Load(o.TranslateText?.ToString().ToLowerInvariant(), s.Translate ? "true" : "false"); break;
                case "zone": f.Load(o.TimeZoneId, s.Profile?.TimeZoneId ?? ""); break;
                case "windows": f.Load(o.Windows is null ? null : MarketProfile.FormatWindows(o.Windows), MarketProfile.FormatWindows(s.Windows)); break;
                case "limit": f.Load(o.DailyLimit?.ToString(), s.DailyLimit.ToString()); break;
                case "hashtags": f.Load(o.Hashtags, s.Hashtags); break;
                case "title": f.Load(o.TitleTemplate, s.TitleTemplate); break;
                case "caption": f.Load(o.CaptionTemplate, s.CaptionTemplate); break;
                case "privacy": f.Load(o.Privacy, s.Privacy); break;
                case "days": f.Load(o.Days is null ? null : DaysText(o.Days), DaysText(s.Days)); break;
            }
    }

    private static string DaysText(IEnumerable<DayOfWeek> days) => string.Join(",", days.Select(d => (int)d));

    [RelayCommand]
    private void ApplyCustom()
    {
        if (CustomRow is not { } row) return;
        try
        {
            string? Own(string key) => OverrideFields.First(f => f.Key == key).Own;
            var o = new ReupOverrides
            {
                Language = Own("language"), TimeZoneId = Own("zone"), Hashtags = Own("hashtags"), TitleTemplate = Own("title"), CaptionTemplate = Own("caption"),
                Privacy = Own("privacy"), TranslateText = Own("translate") is { } t ? t == "true" : null,
                Windows = Own("windows") is { } w ? MarketProfile.ParseWindows(w) : null,
                // Bỏ chọn hết lưu thành danh sách rỗng = mọi ngày (cùng nghĩa với hồ sơ), không chặn đăng.
                Days = Own("days") is { } d ? [.. d.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (DayOfWeek)int.Parse(x))] : null,
                DailyLimit = Own("limit") is { } l ? int.TryParse(l, out var n) && n is >= 1 and <= MarketProfile.MaxDailyLimit ? n
                    : throw new ArgumentException($"Tối đa bài/ngày phải từ 1 đến {MarketProfile.MaxDailyLimit}.") : null
            };
            ReupRuleConfig.CheckTemplates(o.TitleTemplate ?? "", o.CaptionTemplate ?? "");
            row.Overrides = o;
            RefreshSchedules();
            CustomRow = null;
            Status = "Đã áp dụng tuỳ chỉnh cho kênh; bấm Lưu luồng để lưu.";
        }
        catch (ArgumentException e)
        {
            Status = "Lỗi: " + e.Message;
            dialogs.Error(e.Message);
        }
    }

    [RelayCommand]
    private void CloseCustom() => CustomRow = null;

    [RelayCommand]
    private Task PreviewAsync() => Guard(async () =>
    {
        Status = "Đang xem trước (có thể dịch tiêu đề)…";
        var result = await engine.PreviewAsync(BuildConfig(), EditingId);
        Previews.Clear();
        foreach (var p in result)
            Previews.Add(new ReupPreviewRow(Destinations.NameOf(p.DestinationId), p.Profile, p.Title, p.Caption, p.DueLocal, p.DueMachine, p.Warning ?? ""));
        Status = result.Count == 0 ? "Chưa bật kênh đích nào để xem trước." : "Xem trước theo video gần nhất của luồng (hoặc video mẫu).";
    });

    /// <summary>Gọi định kỳ khi màn đang hiện.</summary>
    public async Task RefreshAsync()
    {
        var keepRule = SelectedRule?.Id;
        var keepItem = SelectedItem?.Id;
        var rules = await engine.ListRulesAsync();
        // Làm mới định kỳ chỉ cập nhật phần hiển thị; không nạp lại form đang sửa.
        _syncing = true;
        try
        {
            Rules.Sync([.. rules.Select(r => new RuleRow(r))], r => r.Id, r => (r.Name, r.Source, r.State, r.Counts, r.Message));
            SelectedRule = Rules.FirstOrDefault(r => r.Id == keepRule);
        }
        finally { _syncing = false; }
        await RefreshItemsAsync(keepItem);
    }

    private bool _syncing;

    partial void OnSelectedRuleChanged(RuleRow? value)
    {
        if (_syncing) return;
        if (value is not null) LoadIntoEditor(value);
        _ = RefreshItemsAsync(null);
    }

    partial void OnSelectedItemChanged(ReupItemRow? value)
    {
        var keep = SelectedPost?.Id;
        Posts.Sync([.. (value?.View.Posts ?? []).Select(p => new DeliveryRow(p, Destinations.NameOf(p.DestinationId)))],
            p => p.Id, p => (p.Status, p.Due, p.Error, p.Account));
        SelectedPost = Posts.FirstOrDefault(p => p.Id == keep);
    }

    private async Task RefreshItemsAsync(string? keepItem)
    {
        var items = await engine.ListItemsAsync(SelectedRule?.Id);
        var rows = items.Select(i => new ReupItemRow(i)).ToList();
        _syncing = true;
        try
        {
            Items.Sync(rows, i => i.Id, i => (i.Title, i.Status, i.Detail, i.Posts, string.Join(',', i.View.Posts.Select(p => p.Status))));
            SelectedItem = Items.FirstOrDefault(i => i.Id == keepItem);
        }
        finally { _syncing = false; }
        OnSelectedItemChanged(SelectedItem);
    }

    private void LoadIntoEditor(RuleRow row)
    {
        var c = row.View.Config;
        EditingId = row.Id;
        (RuleName, Platform, SourceUrl) = (c.Name, c.SourcePlatform, c.SourceUrl);
        History = Histories.First(h => h.Value == c.History);
        HistoryLimit = c.HistoryLimit;
        ContentType = ContentTypes.First(h => h.Value == c.ContentType);
        (IntervalMinutes, NewestFirst, Include, Exclude, MinSeconds, MaxSeconds) = (c.IntervalMinutes, c.NewestFirst, c.Include, c.Exclude, c.MinSeconds, c.MaxSeconds);
        Processing = Processings.First(p => p.Value == (c.Processing?.Mode ?? (c.Transform ? "reup" : "none")));
        Preset = Presets.FirstOrDefault(p => p.Id == c.ReupPreset) ?? Presets.FirstOrDefault();
        PublishMode = PublishModes.First(p => p.Value == c.PublishMode);
        (TitleTemplate, CaptionTemplate, Privacy) = (c.TitleTemplate, c.CaptionTemplate, c.Privacy);
        (DelayMinutes, SpacingMinutes, DailyLimit, Retries) = (c.DelayMinutes, c.SpacingMinutes, c.DailyLimit, c.Retries);
        Destinations.Select(c.DestinationIds);
        SourceRouteId = c.SourceRouteId ?? "";
        Previews.Clear();
        RebuildDestinationRows(c.Destinations);
    }

    [RelayCommand]
    private void NewRule()
    {
        SelectedRule = null;
        EditingId = null;
        (RuleName, SourceUrl, Include, Exclude) = ("", "", "", "");
        (History, ContentType, Processing, PublishMode) = (Histories[0], ContentTypes[0], Processings[0], PublishModes[0]);
        Destinations.Select([]);
        SourceRouteId = "";
        Previews.Clear();
        RebuildDestinationRows([]);
        Status = "Nhập thông tin luồng mới rồi bấm Lưu luồng.";
    }

    private ReupRuleConfig BuildConfig() =>
        new()
        {
            Name = RuleName.Trim(), SourcePlatform = Platform, SourceUrl = SourceUrl, DestinationIds = Destinations.SelectedIds,
            History = History.Value, HistoryLimit = HistoryLimit, ContentType = ContentType.Value, IntervalMinutes = IntervalMinutes,
            PublishMode = PublishMode.Value, Enabled = true, NewestFirst = NewestFirst, Include = Include, Exclude = Exclude,
            MinSeconds = MinSeconds, MaxSeconds = MaxSeconds, Transform = Processing.Value == "reup", ReupPreset = Preset?.Id ?? "tiktok",
            Processing = Processing.Value is AutoModes.Localize or AutoModes.Review or AutoModes.Summary ? new AutoRequest { Mode = Processing.Value } : null,
            TitleTemplate = TitleTemplate, CaptionTemplate = CaptionTemplate, Privacy = Privacy, DelayMinutes = DelayMinutes,
            SpacingMinutes = SpacingMinutes, DailyLimit = DailyLimit, Retries = Retries,
            Destinations = [.. DestinationRows.Where(KeepOptions).Select(r => r.ToOptions())],
            SourceRouteId = string.IsNullOrEmpty(SourceRouteId) ? null : SourceRouteId
        };

    [RelayCommand]
    private Task SaveAsync() => Guard(async () =>
    {
        EditingId = await engine.SaveRuleAsync(BuildConfig(), EditingId);
        var id = EditingId;
        await RefreshAsync();
        SelectedRule = Rules.FirstOrDefault(r => r.Id == id);
        Status = "Đã lưu luồng; app sẽ quét nguồn trong ít phút tới.";
        dialogs.Info("Reup tự động", "Đã lưu luồng.");
    });

    [RelayCommand(CanExecute = nameof(HasRule))]
    private Task ToggleEnabledAsync() => Guard(async () =>
    {
        await engine.SetEnabledAsync(SelectedRule!.Id, !SelectedRule.View.Record.Enabled);
        await RefreshAsync();
    });

    [RelayCommand(CanExecute = nameof(HasRule))]
    private Task CheckNowAsync() => Guard(async () =>
    {
        await engine.CheckNowAsync(SelectedRule!.Id);
        Status = "Sẽ quét nguồn ở lượt kiểm tra kế tiếp (tối đa 20 giây).";
    });

    [RelayCommand(CanExecute = nameof(HasRule))]
    private Task DeleteRuleAsync() => Guard(async () =>
    {
        if (!await dialogs.ConfirmAsync("Xoá luồng", $"Xoá luồng \"{SelectedRule!.Name}\" và danh sách video của nó? Bài đã xếp lịch đăng vẫn giữ nguyên.")) return;
        await engine.DeleteRuleAsync(SelectedRule.Id);
        SelectedRule = null;
        await RefreshAsync();
    });

    [RelayCommand(CanExecute = nameof(HasItem))]
    private Task ApproveAsync() => Guard(async () =>
    {
        var id = SelectedItem!.Id;
        // Bài reup luôn có cảnh báo bản quyền: hỏi một lần cho cả video rồi duyệt lại các bài còn chờ.
        var pending = await engine.ApproveItemAsync(id, false);
        if (pending.FirstOrDefault(r => r.Blocked || r.Errors.Count > 0) is { } failed)
            dialogs.Error(string.Join("\n", failed.Errors.Count > 0 ? failed.Errors : failed.Warnings.Select(w => w.Message)));
        else if (pending.Count > 0)
        {
            var merged = pending[0] with { Warnings = [.. pending.SelectMany(r => r.Warnings).DistinctBy(w => w.Message)] };
            await new PostActions(publications, dialogs).ConfirmWarningsAsync(merged, async ack =>
                (await engine.ApproveItemAsync(id, ack)).FirstOrDefault() ?? merged with { Created = true });
        }
        await RefreshAsync();
    });

    [RelayCommand(CanExecute = nameof(HasItem))]
    private Task RetryItemAsync() => Guard(async () =>
    {
        await engine.RetryItemAsync(SelectedItem!.Id);
        await RefreshAsync();
    });

    [RelayCommand(CanExecute = nameof(HasPost))]
    private Task ResendAsync() => Guard(async () =>
    {
        await engine.ResendAsync(SelectedPost!.Id);
        await RefreshAsync();
    });

    [RelayCommand(CanExecute = nameof(HasPost))]
    private Task CancelPostAsync() => Guard(async () =>
    {
        await engine.CancelPostAsync(SelectedPost!.Id);
        await RefreshAsync();
    });

    private bool HasRule() => SelectedRule is not null;
    private bool HasItem() => SelectedItem is not null;
    private bool HasPost() => SelectedPost is not null;

    private async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException or InvalidDataException)
        {
            Status = "Lỗi: " + ex.Message;
            dialogs.Error(ex.Message);
        }
    }
}
