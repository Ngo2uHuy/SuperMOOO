using SuperMOOO.App.Controls;
using SuperMOOO.Core.Persistence;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.App.Features.S14;

public sealed record PostRow(
    string Id, string Platform, string Destination, string Title, string RawStatus, string Status, string Scheduled, int Attempts, string Error,
    bool CanCancel, bool CanRetry, bool IsUnknown, bool IsDraft, string? RemoteUrl)
{
    public static PostRow From(PublicationDto p) => new(
        p.Id, Labels.Term(p.Platform), p.DestinationTitle, p.Title, p.Status, PublishLabels.Status(p.Status),
        p.ScheduledUtc is { } at ? VietnamTimeText(at) : "", p.Attempts, p.LastError ?? "", p.CanCancel, p.CanRetry,
        p.Status == nameof(PublicationStatus.Unknown), p.Status == nameof(PublicationStatus.Draft), p.RemoteUrl);

    public static string VietnamTimeText(DateTimeOffset value) =>
        SuperMOOO.Publishing.Scheduling.VietnamTime.ToLocal(value).ToString("dd/MM/yyyy HH:mm");
}

public static class PublishLabels
{
    public static string Status(string status) => status switch
    {
        nameof(PublicationStatus.Draft) => "Nháp",
        nameof(PublicationStatus.Validating) => "Đang kiểm tra",
        nameof(PublicationStatus.AwaitingApproval) => "Chờ duyệt",
        nameof(PublicationStatus.Scheduled) => "Chờ đăng",
        nameof(PublicationStatus.Uploading) => "Đang tải lên",
        nameof(PublicationStatus.Processing) => "Nền tảng đang xử lý",
        nameof(PublicationStatus.Published) => "Đã đăng",
        nameof(PublicationStatus.RetryScheduled) => "Chờ thử lại",
        nameof(PublicationStatus.NeedsAction) => "Cần xử lý",
        nameof(PublicationStatus.Unknown) => "Không xác định",
        nameof(PublicationStatus.Failed) => "Lỗi",
        nameof(PublicationStatus.Cancelled) => "Đã huỷ",
        nameof(PublicationStatus.AwaitingUser) => "Chờ bạn hoàn tất trong app",
        _ => status
    };

    public static string Privacy(string privacy) => Labels.Term(privacy);
}

/// <summary>Thao tác dùng chung cho bảng "Cần xử lý" và danh sách bài đăng; lỗi người dùng hiện qua <see cref="IDialogService.Error"/>.</summary>
public sealed class PostActions(PublicationService publications, IDialogService dialogs)
{
    public async Task<bool> GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (PublishingException ex)
        {
            dialogs.Error(ex.Message);
            return false;
        }
    }

    public Task<bool> RetryAsync(string id) => GuardAsync(() => publications.RetryAsync(new PublicationIdRequest(id), default));

    public async Task<bool> CancelAsync(string id)
    {
        if (!await dialogs.ConfirmAsync("Huỷ bài", "Huỷ bài đăng này? (Không xoá video đã có trên nền tảng.)")) return false;
        return await GuardAsync(() => publications.CancelAsync(new PublicationIdRequest(id), default));
    }

    public async Task<bool> ResolveUnknownAsync(string id, string resolution, string? url)
    {
        if (resolution == UnknownResolutions.Repost
            && !await dialogs.ConfirmAsync("Đăng lại", "Có thể tạo video trùng trên nền tảng. Chỉ đăng lại khi đã kiểm tra video chưa lên. Tiếp tục?")) return false;
        if (resolution == UnknownResolutions.Cancel
            && !await dialogs.ConfirmAsync("Huỷ bài", "Bỏ bài này? Video có thể đã nằm trên nền tảng.")) return false;
        return await GuardAsync(() => publications.ResolveUnknownAsync(
            new ResolveUnknownPublicationRequest(id, resolution, url, resolution == UnknownResolutions.Repost), default));
    }

    /// <summary>Gửi lại sau khi người dùng xem cảnh báo rủi ro/đăng lại; null = không tạo được (đã báo lỗi) hoặc người dùng từ chối.</summary>
    public async Task<CreatePublicationsResultDto?> ConfirmWarningsAsync(CreatePublicationsResultDto result, Func<bool, Task<CreatePublicationsResultDto>> resend)
    {
        if (result.Created) return result;
        if (result.Blocked || result.Errors.Count > 0)
        {
            dialogs.Error(string.Join("\n", result.Errors.Count > 0 ? result.Errors : result.Warnings.Select(w => w.Message)));
            return null;
        }
        if (!result.NeedsConfirmation) return null;
        var text = string.Join("\n", result.Warnings.Select(w => "• " + w.Message));
        if (!await dialogs.ConfirmAsync("Có cảnh báo", text + "\n\nVẫn tiếp tục?", "Vẫn đăng")) return null;
        var again = await resend(true);
        if (again.Created) return again;
        dialogs.Error(string.Join("\n", again.Errors.Count > 0 ? again.Errors : again.Warnings.Select(w => w.Message)));
        return null;
    }
}

[Service(ServiceLifetime.Transient)]
public sealed partial class PublishingViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _watch;

    public PublishingViewModel(PublicationService publications, SuperMOOO.Publishing.OAuth.OAuthConnectService oauth, PlatformSettingsService platform,
        IActionRequiredRepository actions, IContentRepository contents, SuperMOOO.AI.Llm.LlmProviderRegistry llm, IDialogService dialogs,
        SuperMOOO.Publishing.Analytics.AnalyticsService analytics, SuperMOOO.Publishing.Library.ContentIngestService ingest, S21.ContentHandoff handoff,
        ICampaignRepository campaignRepository, ISourceRepository sourceRepository, ISettingsStore settings, PublishMediaPreparer mediaPreparer,
        IpCountryGuard ipGuard, IPublishSettingsStore publishSettings)
    {
        var postActions = new PostActions(publications, dialogs);
        Today = new TodayViewModel(publications, actions, postActions, dialogs, ipGuard);
        Posts = new PostsViewModel(publications, postActions, dialogs);
        Calendar = new CalendarViewModel(publications);
        Analytics = new AnalyticsViewModel(analytics, dialogs, campaignRepository, sourceRepository).Init();
        Compose = new ComposeViewModel(publications, oauth, platform, contents, llm, postActions, dialogs, ingest, handoff, settings, mediaPreparer, publishSettings);
        Compose.Created += () => _ = Post(async () => { await Posts.LoadAsync(); await Today.LoadAsync(); });
    }

    /// <summary>Tab đang chọn của S14: 0 Hôm nay, 1 Soạn bài, 2 Lịch, 3 Bài đăng, 4 Hiệu quả.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; } = 1;

    public TodayViewModel Today { get; }
    public PostsViewModel Posts { get; }
    public CalendarViewModel Calendar { get; }
    public AnalyticsViewModel Analytics { get; }
    public ComposeViewModel Compose { get; }

    public async Task LoadAsync()
    {
        await Task.WhenAll(Today.LoadAsync(), Posts.LoadAsync(), Calendar.LoadAsync(), Compose.LoadAsync(), Analytics.LoadAsync());
        // "Soạn bài" từ S21: mở thẳng tab Soạn bài thay vì tab đang chọn lần trước.
        if (Compose.TookHandoff)
        {
            Compose.TookHandoff = false;
            UiPost.Run(_ui, () => SelectedTab = 1);
        }
    }

    public void StartWatching()
    {
        StopWatching();
        _watch = new CancellationTokenSource();
        _ = WatchAsync(_watch.Token);
    }

    public void StopWatching()
    {
        _watch?.Cancel();
        _watch = null;
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(RefreshEvery);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await Today.LoadAsync();
                await Posts.LoadAsync();
                await Compose.AutoSaveAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // dừng khi rời màn
        }
    }

    private Task Post(Func<Task> action)
    {
        if (_ui is null) return action();
        _ui.Post(async _ => await action(), null);
        return Task.CompletedTask;
    }
}

public static class UiPost
{
    public static void Run(SynchronizationContext? ui, Action action)
    {
        if (ui is null) action();
        else ui.Post(_ => action(), null);
    }
}
