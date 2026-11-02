using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.App.Features.S14;

public sealed record ActionRow(string Id, string Kind, string Title, string Detail, string Created);

public sealed partial class TodayViewModel(PublicationService publications, IActionRequiredRepository actions, PostActions postActions, IDialogService dialogs,
    IpCountryGuard ipGuard) : ObservableObject
{
    private static readonly string[] ProblemStatuses =
        [nameof(PublicationStatus.Failed), nameof(PublicationStatus.Unknown), nameof(PublicationStatus.NeedsAction)];

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public ObservableCollection<ActionRow> Items { get; } = [];
    public ObservableCollection<PostRow> Problems { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResolveActionCommand), nameof(AcceptCountryCommand))]
    public partial ActionRow? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(CancelCommand), nameof(MarkPublishedCommand), nameof(RepostCommand), nameof(DropUnknownCommand))]
    public partial PostRow? SelectedProblem { get; set; }

    [ObservableProperty]
    public partial string RemoteUrl { get; set; } = "";

    /// <summary>Số mục cần xử lý, hiển thị ở tiêu đề tab.</summary>
    [ObservableProperty]
    public partial int OpenCount { get; set; }

    public async Task LoadAsync()
    {
        try
        {
            var start = new DateTimeOffset(DateTime.Today);
            var summary = await publications.SummaryAsync(new GetPublishSummaryRequest(start, start.AddDays(1)), default);
            var (open, total) = await actions.ListAsync(false, 1, 100, default);
            var problems = new List<PostRow>();
            foreach (var status in ProblemStatuses)
                problems.AddRange((await publications.ListAsync(new ListPublicationsRequest(status, null, null, null, 1, 100), default)).Items.Select(PostRow.From));
            var quota = summary.QuotaLimit > 0 ? $" · Quota YouTube {summary.QuotaUsed}/{summary.QuotaLimit}" : "";
            UiPost.Run(_ui, () =>
            {
                Summary = $"Hôm nay {summary.Today} bài ({summary.TodayPublished} đã đăng) · Đang chạy {summary.InProgress} · Lỗi {summary.Failed} · "
                    + $"Cần xử lý {summary.NeedsAction} · Không xác định {summary.Unknown}{quota}";
                OpenCount = total + problems.Count;
                Items.Sync([.. open.Select(a => new ActionRow(a.Id, a.Kind, a.Title, a.Detail, PostRow.VietnamTimeText(a.CreatedUtc)))], r => r.Id, r => r);
                var keep = SelectedProblem?.Id;
                Problems.Sync([.. problems], r => r.Id, r => r);
                SelectedProblem = Problems.FirstOrDefault(r => r.Id == keep);
            });
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasItem))]
    private async Task ResolveActionAsync()
    {
        if (SelectedItem is not { } item) return;
        await postActions.GuardAsync(async () => await actions.ResolveAsync(item.Id, default));
        await LoadAsync();
    }

    /// <summary>Mục "IP đổi quốc gia": chấp nhận quốc gia mới cho tài khoản (RefId) để bài dời đăng tiếp.</summary>
    [RelayCommand(CanExecute = nameof(IsCountryChange))]
    private async Task AcceptCountryAsync()
    {
        if (SelectedItem is not { Kind: ActionKinds.IpCountryChanged } item) return;
        var row = (await actions.ListOpenByKindAsync(ActionKinds.IpCountryChanged, default)).FirstOrDefault(a => a.Id == item.Id);
        if (row is not null && await ipGuard.AcceptAsync(row.RefId, default) is null) await actions.ResolveAsync(item.Id, default);
        await LoadAsync();
    }

    private bool IsCountryChange() => SelectedItem?.Kind == ActionKinds.IpCountryChanged;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync() => await Act(p => postActions.RetryAsync(p.Id));

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync() => await Act(p => postActions.CancelAsync(p.Id));

    [RelayCommand(CanExecute = nameof(IsUnknown))]
    private async Task MarkPublishedAsync() => await Act(p => postActions.ResolveUnknownAsync(p.Id, UnknownResolutions.MarkPublished, RemoteUrl));

    [RelayCommand(CanExecute = nameof(IsUnknown))]
    private async Task RepostAsync() => await Act(p => postActions.ResolveUnknownAsync(p.Id, UnknownResolutions.Repost, null));

    [RelayCommand(CanExecute = nameof(IsUnknown))]
    private async Task DropUnknownAsync() => await Act(p => postActions.ResolveUnknownAsync(p.Id, UnknownResolutions.Cancel, null));

    private async Task Act(Func<PostRow, Task<bool>> action)
    {
        if (SelectedProblem is not { } row) return;
        if (await action(row)) await LoadAsync();
    }

    private bool HasItem() => SelectedItem is not null;
    private bool CanRetry() => SelectedProblem is { CanRetry: true };
    private bool CanCancel() => SelectedProblem is { CanCancel: true };
    private bool IsUnknown() => SelectedProblem is { IsUnknown: true };
}

public sealed partial class PostsViewModel(PublicationService publications, PostActions postActions, IDialogService dialogs) : ObservableObject
{
    private const int PageSize = 50;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public IReadOnlyList<TikTokOption> StatusFilters { get; } =
        [new("", "Tất cả trạng thái"), .. Enum.GetNames<PublicationStatus>().Select(n => new TikTokOption(n, PublishLabels.Status(n)))];
    public ObservableCollection<PostRow> Rows { get; } = [];
    public ObservableCollection<string> Attempts { get; } = [];

    [ObservableProperty]
    public partial string StatusFilter { get; set; } = "";

    [ObservableProperty]
    public partial int Page { get; set; } = 1;

    [ObservableProperty]
    public partial int Total { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(CancelCommand), nameof(MarkPublishedCommand), nameof(RepostCommand), nameof(DropUnknownCommand),
        nameof(PublishDraftCommand))]
    public partial PostRow? Selected { get; set; }

    [ObservableProperty]
    public partial string RemoteUrl { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public string PageText => $"Trang {Page}/{PageCount} · {Total} bài";

    partial void OnStatusFilterChanged(string value)
    {
        Page = 1;
        _ = LoadAsync();
    }

    partial void OnSelectedChanged(PostRow? value) => _ = LoadDetailAsync(value);

    public async Task LoadAsync()
    {
        try
        {
            var result = await publications.ListAsync(new ListPublicationsRequest(StatusFilter.Length == 0 ? null : StatusFilter, null, null, null, Page, PageSize), default);
            UiPost.Run(_ui, () =>
            {
                Total = result.Total;
                OnPropertyChanged(nameof(PageCount));
                OnPropertyChanged(nameof(PageText));
                var keep = Selected?.Id;
                Rows.Sync([.. result.Items.Select(PostRow.From)], r => r.Id, r => r);
                Selected = Rows.FirstOrDefault(r => r.Id == keep);
            });
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    private async Task LoadDetailAsync(PostRow? row)
    {
        if (row is null)
        {
            Detail = "";
            Attempts.Clear();
            return;
        }
        try
        {
            var detail = await publications.GetAsync(new PublicationIdRequest(row.Id), default);
            UiPost.Run(_ui, () =>
            {
                Detail = $"{detail.Summary.ContentTitle} → {detail.Summary.DestinationTitle} ({detail.Summary.ConnectionName})"
                    + (detail.Summary.RemoteUrl is { } url ? $"\n{url}" : "") + (detail.Summary.LastError is { } err ? $"\nLỗi: {err}" : "");
                Attempts.Clear();
                foreach (var a in detail.Attempts)
                    Attempts.Add($"{PostRow.VietnamTimeText(a.StartedUtc)} · {a.Outcome ?? "đang chạy"}{(a.Error is null ? "" : " · " + a.Error)}");
            });
        }
        catch (PublishingException)
        {
            // bài vừa bị xoá/đổi: bỏ qua, lần làm mới sau sẽ đồng bộ
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (Page >= PageCount) return;
        Page++;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (Page <= 1) return;
        Page--;
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync() => await Act(p => postActions.RetryAsync(p.Id));

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync() => await Act(p => postActions.CancelAsync(p.Id));

    [RelayCommand(CanExecute = nameof(IsUnknown))]
    private async Task MarkPublishedAsync() => await Act(p => postActions.ResolveUnknownAsync(p.Id, UnknownResolutions.MarkPublished, RemoteUrl));

    [RelayCommand(CanExecute = nameof(IsUnknown))]
    private async Task RepostAsync() => await Act(p => postActions.ResolveUnknownAsync(p.Id, UnknownResolutions.Repost, null));

    [RelayCommand(CanExecute = nameof(IsUnknown))]
    private async Task DropUnknownAsync() => await Act(p => postActions.ResolveUnknownAsync(p.Id, UnknownResolutions.Cancel, null));

    [RelayCommand(CanExecute = nameof(IsDraft))]
    private async Task PublishDraftAsync()
    {
        if (Selected is not { } row) return;
        Task<CreatePublicationsResultDto> Send(bool ack) =>
            publications.ScheduleAsync(new SchedulePublicationRequest(row.Id, PublishModes.Now, null, false, ack, ack), default);
        try
        {
            var result = await postActions.ConfirmWarningsAsync(await Send(false), Send);
            if (result is not null) dialogs.Info("Đăng bài", "Đã đưa bài vào hàng đợi đăng.");
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
        }
        await LoadAsync();
    }

    private async Task Act(Func<PostRow, Task<bool>> action)
    {
        if (Selected is not { } row) return;
        if (await action(row)) await LoadAsync();
    }

    private bool CanRetry() => Selected is { CanRetry: true };
    private bool CanCancel() => Selected is { CanCancel: true };
    private bool IsUnknown() => Selected is { IsUnknown: true };
    private bool IsDraft() => Selected is { IsDraft: true };
}

public sealed record CalendarCell(DateTime Date, string Header, bool InRange, IReadOnlyList<string> Lines);

public sealed partial class CalendarViewModel(PublicationService publications) : ObservableObject
{
    public IReadOnlyList<string> Modes { get; } = ["Ngày", "Tuần", "Tháng"];
    public ObservableCollection<CalendarCell> Cells { get; } = [];

    [ObservableProperty]
    public partial string Mode { get; set; } = "Tuần";

    [ObservableProperty]
    public partial DateTime Anchor { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial int Columns { get; set; } = 7;

    partial void OnModeChanged(string value) => _ = LoadAsync();
    partial void OnAnchorChanged(DateTime value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        var (from, to, inRange) = Range();
        var posts = new List<PublicationDto>();
        try
        {
            for (var page = 1; page <= 5; page++)
            {
                var result = await publications.ListAsync(new ListPublicationsRequest(null, null, from, to, page, 200), default);
                posts.AddRange(result.Items);
                if (posts.Count >= result.Total) break;
            }
        }
        catch (PublishingException)
        {
            return;
        }
        var byDay = posts.Where(p => p.ScheduledUtc is not null).GroupBy(p => SuperMOOO.Publishing.Scheduling.VietnamTime.ToLocal(p.ScheduledUtc!.Value).Date)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.ScheduledUtc).Select(p =>
                $"{SuperMOOO.Publishing.Scheduling.VietnamTime.ToLocal(p.ScheduledUtc!.Value):HH:mm} {Labels.Term(p.Platform)} · {p.Title} [{PublishLabels.Status(p.Status)}]").ToList());
        var cells = new List<CalendarCell>();
        for (var day = from.Date; day < to.Date; day = day.AddDays(1))
            cells.Add(new CalendarCell(day, $"{day:ddd dd/MM}", inRange(day), byDay.TryGetValue(day, out var lines) ? lines : []));
        Cells.Clear();
        foreach (var cell in cells) Cells.Add(cell);
        Columns = Mode == "Ngày" ? 1 : 7;
        Title = Mode switch
        {
            "Ngày" => $"{Anchor:dddd dd/MM/yyyy}",
            "Tuần" => $"{from:dd/MM} – {to.AddDays(-1):dd/MM/yyyy}",
            _ => $"Tháng {Anchor:MM/yyyy}"
        };
    }

    /// <summary>Khoảng hiển thị theo giờ Việt Nam; truy vấn đổi sang UTC để khớp giờ hẹn lưu ở DB.</summary>
    private (DateTimeOffset From, DateTimeOffset To, Func<DateTime, bool> InRange) Range()
    {
        var anchor = Anchor.Date;
        DateTime start, end;
        switch (Mode)
        {
            case "Ngày":
                start = anchor;
                end = anchor.AddDays(1);
                break;
            case "Tuần":
                start = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));
                end = start.AddDays(7);
                break;
            default:
                var first = new DateTime(anchor.Year, anchor.Month, 1);
                start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
                end = start.AddDays(((first.AddMonths(1) - start).Days + 6) / 7 * 7);
                break;
        }
        return (SuperMOOO.Publishing.Scheduling.VietnamTime.FromLocal(start), SuperMOOO.Publishing.Scheduling.VietnamTime.FromLocal(end),
            d => Mode != "Tháng" || d.Month == anchor.Month);
    }

    [RelayCommand]
    private void Previous() => Anchor = Step(-1);

    [RelayCommand]
    private void Next() => Anchor = Step(1);

    [RelayCommand]
    private void GoToday() => Anchor = DateTime.Today;

    private DateTime Step(int sign) => Mode switch
    {
        "Ngày" => Anchor.AddDays(sign),
        "Tuần" => Anchor.AddDays(7 * sign),
        _ => Anchor.AddMonths(sign)
    };
}
