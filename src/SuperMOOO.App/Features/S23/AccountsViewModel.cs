using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Infrastructure.Network;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Infrastructure.Tabular;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.OAuth;
using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.App.Features.S23;

public sealed record Opt(string Value, string Display);

public sealed partial class AccountRow(string id, string platform, string name, string status, string token, string limits, string route) : ObservableObject
{
    public string Id { get; } = id;
    public string Platform { get; } = platform;
    public string Name { get; } = name;
    public string Status { get; } = status;
    public string Token { get; } = token;
    public string Limits { get; } = limits;
    public string Route { get; } = route;
    public string Display => $"{Platform} · {Name}";
    public int? DailyLimit { get; init; }
    public bool IsConnected { get; init; }
    [ObservableProperty] public partial string TodayCount { get; set; } = "0";
    [ObservableProperty] public partial string NextRun { get; set; } = "—";
    [ObservableProperty] public partial bool IsUploading { get; set; }
    [ObservableProperty] public partial double UploadProgress { get; set; }
    [ObservableProperty] public partial bool UploadIndeterminate { get; set; }
    /// <summary>Cảnh báo IP/tuyến (màu cam); rỗng = không có.</summary>
    public string? Warning { get; init; }
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
    [ObservableProperty]
    public partial string Activity { get; set; } = "";
    [ObservableProperty]
    public partial string RunState { get; set; } = "";
    [ObservableProperty]
    public partial bool BrowserChecked { get; set; }
    [ObservableProperty]
    public partial string BrowserState { get; set; } = "Chưa mở trong phiên này";
    [ObservableProperty]
    public partial int BrowserWindowCount { get; set; }
    [ObservableProperty]
    public partial string BrowserVerification { get; set; } = "";
}

public sealed record DestinationRow(string Title, string Platform, string State, string Capabilities, string? Blocked);
public sealed record PublicationEntry(string Id, string Title, string Destination, string State, string At, double Progress, bool Indeterminate);
public sealed record AttemptEntry(string Title, string At, string Outcome, string Detail);

[Service(ServiceLifetime.Transient)]
public sealed partial class AccountsViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> HomeUrls = new()
    {
        ["YouTube"] = "https://studio.youtube.com", ["Facebook"] = "https://www.facebook.com",
        ["Instagram"] = "https://www.instagram.com", ["TikTok"] = "https://www.tiktok.com"
    };

    private readonly IConnectionRepository _connections;
    private readonly IPublicationRepository _publications;
    private readonly IPublishSettingsStore _publishSettings;
    private readonly IActionRequiredRepository _actions;
    private readonly OAuthConnectService _oauth;
    private readonly NetworkRouteBroker _broker;
    private readonly ProxyProfileService _proxies;
    private readonly WindowsVpnService _vpn;
    private readonly AccountBrowser _browser;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly ShellService _shell;
    private readonly IClock _clock;
    private readonly IpCountryGuard _ipGuard;
    private static readonly TimeSpan TestLockWait = TimeSpan.FromSeconds(30);
    private bool _loading;
    private int _detailLoadVersion;
    private IReadOnlyList<PublicationRow> _publicationRows = [];
    private (string RouteId, string Ip)? _testedIp;

    public AccountsViewModel(IConnectionRepository connections, IPublicationRepository publications, IPublishSettingsStore publishSettings,
        IActionRequiredRepository actions, OAuthConnectService oauth, NetworkRouteBroker broker,
        ProxyProfileService proxies, WindowsVpnService vpn, AccountBrowser browser, ISettingsStore settings, IDialogService dialogs, ShellService shell,
        IClock clock, IpCountryGuard ipGuard)
    {
        _ipGuard = ipGuard;
        _connections = connections;
        _publications = publications;
        _publishSettings = publishSettings;
        _actions = actions;
        _oauth = oauth;
        _broker = broker;
        _proxies = proxies;
        _vpn = vpn;
        _browser = browser;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        _clock = clock;
    }

    public IReadOnlyList<string> Platforms { get; } = ["YouTube", "Facebook", "Instagram", "TikTok"];

    public IReadOnlyList<Opt> KindOptions { get; } =
    [
        new(NetworkRoute.Direct, "Trực tiếp (không proxy/VPN)"), new(NetworkRoute.Proxy, "Proxy (hồ sơ proxy)"),
        new(NetworkRoute.WindowsVpn, "VPN có sẵn trong Windows"), new(NetworkRoute.WireGuard, "WireGuard (file .conf)"),
        new(NetworkRoute.ManualVpn, "VPN bật tay (tên app VPN)")
    ];

    public IReadOnlyList<Opt> IpModeOptions { get; } =
        [new(IpModes.StableCountry, "Giữ đúng quốc gia"), new(IpModes.Pinned, "IP cố định")];

    public IReadOnlyList<Opt> FailoverOptions { get; } =
    [
        new(FailoverModes.Strict, "Nghiêm ngặt: sai tuyến/IP thì dời bài"), new(FailoverModes.Ask, "Hỏi tôi duyệt tuyến dự phòng"),
        new(FailoverModes.AutoSameCountry, "Tự dùng dự phòng nếu cùng quốc gia")
    ];

    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<AccountRow> VisibleAccounts { get; } = [];
    public ObservableCollection<DestinationRow> Destinations { get; } = [];
    public ObservableCollection<PublicationEntry> Queue { get; } = [];
    public ObservableCollection<AttemptEntry> History { get; } = [];
    public ObservableCollection<NetworkRoute> Routes { get; } = [];
    public ObservableCollection<Opt> RouteOptions { get; } = [];
    public ObservableCollection<Opt> RefOptions { get; } = [];

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // --- Tài khoản ---
    [ObservableProperty]
    public partial AccountRow? SelectedAccount { get; set; }

    [ObservableProperty]
    public partial string AccountSearch { get; set; } = "";

    [ObservableProperty]
    public partial string AccountPlatformFilter { get; set; } = "Tất cả";

    public IReadOnlyList<string> AccountPlatformFilters { get; } = ["Tất cả", "YouTube", "Facebook", "Instagram", "TikTok"];

    [ObservableProperty]
    public partial string AccountSummary { get; set; } = "";

    [ObservableProperty]
    public partial string ConnectedSummary { get; set; } = "";

    [ObservableProperty]
    public partial string WaitingSummary { get; set; } = "";

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    public partial string TodaySummary { get; set; } = "";

    [ObservableProperty]
    public partial string NextSummary { get; set; } = "";

    [ObservableProperty]
    public partial string BulkRouteId { get; set; } = "";

    partial void OnAccountSearchChanged(string value) => FilterAccounts();
    partial void OnAccountPlatformFilterChanged(string value) => FilterAccounts();

    [ObservableProperty]
    public partial string NewPlatform { get; set; } = "YouTube";

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial string? PendingApproval { get; set; }

    // --- Tuyến mạng ---
    [ObservableProperty]
    public partial NetworkRoute? SelectedRoute { get; set; }

    [ObservableProperty]
    public partial string RouteName { get; set; } = "";

    [ObservableProperty]
    public partial string RouteKind { get; set; } = NetworkRoute.Proxy;

    [ObservableProperty]
    public partial string RouteRef { get; set; } = "";

    [ObservableProperty]
    public partial string RouteCountry { get; set; } = "";

    [ObservableProperty]
    public partial bool RouteEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string? RouteCheck { get; set; }

    /// <summary>Cảnh báo/gợi ý kill-switch của tuyến cả máy (chỉ hiển thị, không chặn lưu).</summary>
    [ObservableProperty]
    public partial string? RouteKillSwitch { get; set; }

    // --- Chính sách IP của tài khoản ---
    [ObservableProperty]
    public partial string PolicyRouteId { get; set; } = "";

    [ObservableProperty]
    public partial string PolicyFallbackId { get; set; } = "";

    [ObservableProperty]
    public partial string PolicyCountry { get; set; } = "";

    [ObservableProperty]
    public partial string PolicyIpMode { get; set; } = IpModes.StableCountry;

    [ObservableProperty]
    public partial string PolicyPinnedIp { get; set; } = "";

    [ObservableProperty]
    public partial string PolicyFailover { get; set; } = FailoverModes.Strict;

    public bool RefNeedsPicker => RouteKind is NetworkRoute.Proxy or NetworkRoute.WindowsVpn;
    public bool RefNeedsFile => RouteKind == NetworkRoute.WireGuard;
    public bool RefNeedsText => !RefNeedsPicker && RouteKind != NetworkRoute.Direct;
    public bool HasRouteReference => RouteKind != NetworkRoute.Direct;

    partial void OnRouteKindChanged(string value)
    {
        OnPropertyChanged(nameof(RefNeedsPicker));
        OnPropertyChanged(nameof(RefNeedsFile));
        OnPropertyChanged(nameof(RefNeedsText));
        OnPropertyChanged(nameof(HasRouteReference));
        RouteKillSwitch = KillSwitchCheck.Warning(value, RouteRef);
        if (!_loading) _ = Guard(RefreshRefOptionsAsync);
    }

    partial void OnRouteRefChanged(string value) => RouteKillSwitch = KillSwitchCheck.Warning(RouteKind, value);

    partial void OnSelectedRouteChanged(NetworkRoute? value)
    {
        if (value is null) return;
        _loading = true;
        RouteName = value.Name;
        RouteKind = value.Kind;
        RouteRef = value.Ref ?? "";
        RouteCountry = value.ExpectedCountry ?? "";
        RouteEnabled = value.Enabled;
        RouteCheck = null;
        _loading = false;
        _ = Guard(RefreshRefOptionsAsync);
    }

    partial void OnSelectedAccountChanged(AccountRow? value) => _ = Guard(() => LoadAccountDetailAsync(value));

    public async Task LoadAsync()
    {
        await Guard(async () =>
        {
            ReloadRoutes();
            ReloadMarkets();
            await ReloadAccountsAsync(SelectedAccount?.Id);
            await RefreshRefOptionsAsync();
        });
    }

    [RelayCommand]
    private void OpenAddAccount() => SelectedTabIndex = 1;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    // ===== Tài khoản =====

    [RelayCommand]
    private Task AddAccountAsync() => Guard(async () =>
    {
        var name = NewName.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Nhập tên tài khoản (để nhận ra, vd 'Kênh review 1').");
        var platform = Enum.Parse<SocialPlatform>(NewPlatform);
        var now = _clock.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        await _connections.UpsertAsync(new SocialConnection
        {
            Id = id, Platform = platform, DisplayName = name, HomeUrl = HomeUrls.GetValueOrDefault(NewPlatform, ""), CreatedUtc = now, UpdatedUtc = now
        }, CancellationToken.None);
        NewName = "";
        await ReloadAccountsAsync(id);
        Status = "Đã thêm tài khoản. Bấm \"Kết nối\" để đăng nhập.";
    });

    [RelayCommand]
    private Task DeleteAccountAsync() => Guard(async () =>
    {
        if (SelectedAccount is not { } a || !await _dialogs.ConfirmAsync("Xoá tài khoản", $"Xoá tài khoản \"{a.Name}\"? Nên ngắt kết nối trước.")) return;
        if (!await _connections.RemoveAsync(a.Id, CancellationToken.None))
            throw new InvalidOperationException("Không xoá được: tài khoản còn bài đăng hoặc đã bị xoá.");
        await ReloadAccountsAsync(null);
    });

    [RelayCommand]
    private Task ConnectAsync() => Guard(async () =>
    {
        if (IsBusy) return;
        var a = Require();
        IsBusy = true;
        try
        {
            var start = await _oauth.StartAsync(new StartOAuthConnectRequest(a.Id, OAuthFlows.Loopback), CancellationToken.None);
            var conn = await _connections.GetAsync(a.Id, CancellationToken.None);
            var launch = _browser.Open(a.Id, AccountIpPolicy.FromJson(conn?.IpPolicyJson), start.AuthorizeUrl);
            if (launch.Blocked) throw new InvalidOperationException(launch.Message);
            if (!launch.Ok) _shell.OpenUrl(start.AuthorizeUrl);
            Status = "Hoàn tất đăng nhập trong trình duyệt…";
            OAuthSessionDto session;
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                session = _oauth.GetSession(new OAuthSessionRequest(start.SessionId));
            } while (!OAuthSessionStates.IsFinal(session.State));

            if (session.State == OAuthSessionStates.Connected)
            {
                await _oauth.SyncDestinationsAsync(new SyncDestinationsRequest(a.Id), CancellationToken.None);
                _dialogs.Info("Tài khoản", "Đã kết nối và đồng bộ kênh.");
            }
            else _dialogs.Error(session.Message ?? "Kết nối không thành công.");
            Status = session.Message;
        }
        finally { IsBusy = false; }
        await ReloadAccountsAsync(a.Id);
    });

    [RelayCommand]
    private Task DisconnectAsync() => DisconnectCoreAsync(revoke: false);

    [RelayCommand]
    private Task RevokeAsync() => DisconnectCoreAsync(revoke: true);

    private Task DisconnectCoreAsync(bool revoke) => Guard(async () =>
    {
        var a = Require();
        var text = revoke ? $"Thu hồi quyền của \"{a.Name}\" tại nền tảng và xoá token trên máy?" : $"Xoá token của \"{a.Name}\" trên máy? Bài đang chờ sẽ tạm dừng.";
        if (!await _dialogs.ConfirmAsync(revoke ? "Thu hồi quyền" : "Ngắt kết nối", text)) return;
        await _oauth.DisconnectAsync(new DisconnectConnectionRequest(a.Id, revoke), CancellationToken.None);
        await ReloadAccountsAsync(a.Id);
        Status = revoke ? "Đã thu hồi quyền." : "Đã ngắt kết nối.";
    });

    [RelayCommand]
    private Task SyncAsync() => Guard(async () =>
    {
        var a = Require();
        IsBusy = true;
        try
        {
            var list = await _oauth.SyncDestinationsAsync(new SyncDestinationsRequest(a.Id), CancellationToken.None);
            Status = $"Đã đồng bộ {list.Count(d => !d.Removed)} kênh.";
        }
        finally { IsBusy = false; }
        await LoadAccountDetailAsync(a);
    });

    [RelayCommand]
    private Task OpenBrowserAsync() => Guard(() => OpenAccountBrowserAsync(Require()));

    private async Task OpenAccountBrowserAsync(AccountRow a)
    {
        var conn = await _connections.GetAsync(a.Id, CancellationToken.None) ?? throw new InvalidOperationException("Không tìm thấy tài khoản.");
        var url = string.IsNullOrWhiteSpace(conn.HomeUrl) ? "https://www.google.com" : conn.HomeUrl;
        var r = _browser.Open(a.Id, AccountIpPolicy.FromJson(conn.IpPolicyJson), url);
        if (r.Ok) Status = r.Message;
        else _dialogs.Error(r.Message);
    }

    private AccountRow Require() => SelectedAccount ?? throw new InvalidOperationException("Chọn một tài khoản.");

    private async Task ReloadAccountsAsync(string? select)
    {
        var checkedIds = Accounts.Where(x => x.IsChecked).Select(x => x.Id).ToHashSet();
        var browserCheckedIds = Accounts.Where(x => x.BrowserChecked).Select(x => x.Id).ToHashSet();
        var browserSelectedId = SelectedBrowserAccount?.Id;
        var routes = _broker.ListRoutes();
        var rows = new List<AccountRow>();
        var connections = await _connections.ListAsync(CancellationToken.None);
        var warnings = await AccountWarningsAsync(connections, routes);
        foreach (var c in connections)
        {
            var policy = AccountIpPolicy.FromJson(c.IpPolicyJson);
            var route = policy is null ? "Tuyến chung của app" : routes.FirstOrDefault(r => r.Id == policy.RouteId)?.Name ?? "(tuyến đã xoá)";
            var limits = $"Tối đa {(c.MaxPostsPerDay is { } m ? m.ToString() : "mặc định nền tảng")} bài/ngày · cách nhau tối thiểu {(c.MinGapMinutes is { } g ? g + " phút" : "mặc định")}";
            rows.Add(new AccountRow(c.Id, c.Platform.ToString(), c.DisplayName, StatusText(c), TokenText(c), limits, route)
            {
                IsChecked = checkedIds.Contains(c.Id),
                BrowserChecked = browserCheckedIds.Contains(c.Id),
                DailyLimit = c.MaxPostsPerDay,
                IsConnected = c.ApiStatus == ApiStatus.Connected,
                Warning = warnings.GetValueOrDefault(c.Id)
            });
        }
        Accounts.Clear();
        foreach (var r in rows)
        {
            r.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AccountRow.IsChecked)) OnPropertyChanged(nameof(AccountSelectionSummary));
            };
            Accounts.Add(r);
        }
        foreach (var r in Accounts)
            r.RunState = await _publishSettings.GetAsync("account.paused:" + r.Id, CancellationToken.None) == "true" ? "Tạm dừng" : "Đang chạy";
        FilterAccounts();
        FilterBrowserAccounts();
        SelectedBrowserAccount = Accounts.FirstOrDefault(r => r.Id == browserSelectedId);
        RefreshBrowserStates();
        await ReloadPublicationsAsync();
        SelectedAccount = Accounts.FirstOrDefault(r => r.Id == select) ?? Accounts.FirstOrDefault();
    }

    private void FilterAccounts()
    {
        VisibleAccounts.Clear();
        foreach (var row in Accounts.Where(x =>
                     (AccountPlatformFilter == "Tất cả" || x.Platform == AccountPlatformFilter)
                     && (AccountSearch.Length == 0 || x.Name.Contains(AccountSearch, StringComparison.CurrentCultureIgnoreCase))))
            VisibleAccounts.Add(row);
        OnPropertyChanged(nameof(AccountSelectionSummary));
    }

    private async Task ReloadPublicationsAsync()
    {
        var rows = new List<PublicationRow>();
        for (var page = 1; ; page++)
        {
            var result = await _publications.QueryAsync(new PublicationQuery(null, null, null, null, page, 500), CancellationToken.None);
            rows.AddRange(result.Items);
            if (rows.Count >= result.Total || result.Items.Count == 0) break;
        }
        _publicationRows = rows;
        var localDay = _clock.UtcNow.ToLocalTime().Date;
        foreach (var account in Accounts)
        {
            var mine = rows.Where(x => x.ConnectionId == account.Id).ToList();
            var today = mine.Count(x => x.Publication.PublishedUtc?.ToLocalTime().Date == localDay);
            var nextMine = mine.Where(x => x.Publication.ScheduledUtc >= _clock.UtcNow
                                           && x.Publication.Status is PublicationStatus.Scheduled or PublicationStatus.RetryScheduled)
                .MinBy(x => x.Publication.ScheduledUtc);
            account.Activity = $"Hôm nay: {today} · Chờ: {mine.Count(x => x.Publication.Status is PublicationStatus.Scheduled or PublicationStatus.RetryScheduled)}"
                + (nextMine is null ? "" : $" · Tiếp: {nextMine.Publication.ScheduledUtc!.Value.ToLocalTime():dd/MM HH:mm}");
            account.TodayCount = account.DailyLimit is { } limit ? $"{today} / {limit}" : today.ToString();
            var uploading = mine.FirstOrDefault(x => x.Publication.Status == PublicationStatus.Uploading)?.Publication;
            account.IsUploading = uploading is not null;
            account.UploadIndeterminate = uploading is not null && uploading.UploadBytesTotal is not > 0;
            account.UploadProgress = uploading?.UploadBytesTotal is > 0
                ? Math.Clamp(100.0 * uploading.UploadBytesConfirmed / uploading.UploadBytesTotal.Value, 0, 100) : 0;
            account.NextRun = uploading is not null ? "Đang upload" : mine.Any(x => x.Publication.Status == PublicationStatus.Processing) ? "Đang xử lý"
                : account.RunState == "Tạm dừng" ? "Tạm dừng"
                : !account.IsConnected ? "Cần kết nối" : nextMine?.Publication.ScheduledUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? "—";
        }
        AccountSummary = Accounts.Count.ToString();
        ConnectedSummary = Accounts.Count(x => x.Status == "Đã kết nối").ToString();
        AttentionSummary = Accounts.Count(x => !x.IsConnected || !string.IsNullOrEmpty(x.Warning) || !string.IsNullOrEmpty(x.BrowserVerification)).ToString();
        WaitingSummary = rows.Count(x => x.Publication.Status is PublicationStatus.Scheduled or PublicationStatus.RetryScheduled).ToString();
        TodaySummary = rows.Count(x => x.Publication.PublishedUtc?.ToLocalTime().Date == localDay).ToString();
        var next = rows.Where(x => x.Publication.ScheduledUtc >= _clock.UtcNow && x.Publication.Status is PublicationStatus.Scheduled or PublicationStatus.RetryScheduled)
            .MinBy(x => x.Publication.ScheduledUtc);
        NextSummary = next is null ? "Chưa có lịch đăng sắp tới" : $"Lượt tiếp theo: {next.Publication.ScheduledUtc!.Value.ToLocalTime():dd/MM HH:mm} · {next.ConnectionName}";
    }

    private static string StatusText(SocialConnection c) => c.ApiStatus switch
    {
        ApiStatus.Connected => "Đã kết nối",
        ApiStatus.NeedsReauth => "Cần kết nối lại (NeedsReauth)",
        ApiStatus.Revoked => "Đã thu hồi quyền",
        _ => "Chưa kết nối"
    };

    private static string TokenText(SocialConnection c) =>
        c.ApiStatus == ApiStatus.Connected
            ? c.TokenExpiresUtc is { } t ? $"Token hết hạn {t.ToLocalTime():dd/MM/yyyy HH:mm} (tự làm mới)" : "Token không có hạn"
            : c.StatusDetail ?? "Chưa có token";

    private async Task LoadAccountDetailAsync(AccountRow? a)
    {
        var version = ++_detailLoadVersion;
        Destinations.Clear();
        Queue.Clear();
        History.Clear();
        IpHistory.Clear();
        PendingApproval = null;
        PendingCountry = null;
        if (a is null) return;

        var posts = _publicationRows.Where(x => ShowAllAccountActivity || x.ConnectionId == a.Id).ToList();
        foreach (var row in posts.Where(x => x.Publication.Status is PublicationStatus.Scheduled or PublicationStatus.RetryScheduled or PublicationStatus.Uploading or PublicationStatus.Processing)
                     .OrderByDescending(x => x.Publication.Status == PublicationStatus.Uploading)
                     .ThenBy(x => x.Publication.ScheduledUtc ?? x.Publication.CreatedUtc).Take(6))
            Queue.Add(new PublicationEntry(row.Publication.Id, row.Publication.Title, $"{row.ConnectionName} · {row.DestinationTitle}",
                QueueState(row.Publication.Status), (row.Publication.ScheduledUtc ?? row.Publication.CreatedUtc).ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                row.Publication.UploadBytesTotal is > 0 ? Math.Clamp(100.0 * row.Publication.UploadBytesConfirmed / row.Publication.UploadBytesTotal.Value, 0, 100) : 0,
                row.Publication.Status == PublicationStatus.Uploading && row.Publication.UploadBytesTotal is not > 0));
        foreach (var row in posts.OrderByDescending(x => x.Publication.CreatedUtc).Take(30))
        {
            foreach (var attempt in await _publications.ListAttemptsAsync(row.Publication.Id, CancellationToken.None))
            {
                if (version != _detailLoadVersion || SelectedAccount?.Id != a.Id) return;
                if (History.Count >= 12) break;
                History.Add(new AttemptEntry($"{row.ConnectionName} · {row.Publication.Title}", attempt.StartedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    AttemptState(attempt.Outcome), attempt.Error ?? ""));
            }
            if (History.Count >= 12) break;
        }

        var conn = await _connections.GetAsync(a.Id, CancellationToken.None);
        if (version != _detailLoadVersion || SelectedAccount?.Id != a.Id) return;
        var policy = AccountIpPolicy.FromJson(conn?.IpPolicyJson);
        PolicyRouteId = policy?.RouteId ?? "";
        PolicyFallbackId = policy?.FallbackRouteIds?.FirstOrDefault() ?? "";
        PolicyCountry = policy?.CountryCode ?? "";
        PolicyIpMode = policy?.IpMode ?? IpModes.StableCountry;
        PolicyPinnedIp = policy?.PinnedIp ?? "";
        PolicyFailover = policy?.Failover ?? FailoverModes.Strict;
        PolicyCountryChange = policy?.CountryChange ?? CountryChangeModes.Hold;
        await LoadAccountMarketAsync(a.Id);
        await LoadIpHistoryAsync(a);
        if (SelectedAccount?.Id != a.Id) return;

        foreach (var d in await _oauth.ListDestinationsAsync(new ListDestinationsRequest(a.Id), CancellationToken.None))
            Destinations.Add(new DestinationRow(d.Title, d.Platform, d.Removed ? "Không còn trên nền tảng" : d.Enabled ? "Đang bật" : "Đang tắt",
                string.Join("; ", d.Capabilities.Select(c => $"{c.Capability}: {c.Status}{(string.IsNullOrEmpty(c.Note) ? "" : $" ({c.Note})")}")),
                d.AutoPublishBlockedReason));

        var ask = (await _actions.ListOpenByKindAsync(ActionKinds.RouteFailoverAsk, CancellationToken.None)).FirstOrDefault(x => x.RefId == a.Id);
        PendingApproval = ask?.Detail;
    }

    private static string QueueState(PublicationStatus status) => status switch
    {
        PublicationStatus.Scheduled => "Đã lên lịch",
        PublicationStatus.RetryScheduled => "Chờ thử lại",
        PublicationStatus.Uploading => "Đang tải lên",
        PublicationStatus.Processing => "Nền tảng đang xử lý",
        _ => Labels.Term(status.ToString())
    };

    private static string AttemptState(string? outcome) => outcome switch
    {
        null => "Đang chạy",
        "Succeeded" => "Thành công",
        "Retry" => "Chờ thử lại",
        "Deferred" => "Đã dời lịch",
        "Failed" => "Thất bại",
        "NeedsAction" => "Cần xử lý",
        "Unknown" => "Chưa rõ kết quả",
        "Cancelled" => "Đã huỷ",
        _ => Labels.Term(outcome)
    };

    [RelayCommand]
    private Task ImportAccountsAsync() => Guard(async () =>
    {
        var path = _dialogs.OpenFile("Nhập tài khoản", "CSV/Excel (*.csv;*.xlsx)|*.csv;*.xlsx");
        if (path is null) return;
        TableData table;
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            table = XlsxLite.Read(stream);
        }
        else table = CsvCodec.Read(await File.ReadAllTextAsync(path));
        var cols = table.Columns.Select(x => x.Trim().ToLowerInvariant()).ToList();
        var platformCol = cols.FindIndex(x => x is "platform" or "nền tảng" or "nentang");
        var nameCol = cols.FindIndex(x => x is "name" or "tên" or "ten" or "tên tài khoản");
        if (platformCol < 0 || nameCol < 0) throw new InvalidOperationException("File cần hai cột: platform và name.");
        var existing = (await _connections.ListAsync(CancellationToken.None))
            .Select(x => (x.Platform, Name: x.DisplayName.Trim().ToLowerInvariant())).ToHashSet();
        var pending = new List<(SocialPlatform Platform, string Name)>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var cells = table.Rows[i];
            if (cells.Count <= Math.Max(platformCol, nameCol)) throw new InvalidOperationException($"Dòng {i + 2}: thiếu nền tảng hoặc tên.");
            var platformText = cells[platformCol].Trim();
            var name = cells[nameCol].Trim();
            if (!Enum.TryParse<SocialPlatform>(platformText, true, out var platform) || !Platforms.Contains(platform.ToString()))
                throw new InvalidOperationException($"Dòng {i + 2}: nền tảng không hỗ trợ: {platformText}.");
            if (name.Length is 0 or > 100) throw new InvalidOperationException($"Dòng {i + 2}: tên phải có 1–100 ký tự.");
            if (existing.Add((platform, name.ToLowerInvariant()))) pending.Add((platform, name));
        }
        foreach (var (platform, name) in pending)
        {
            var now = _clock.UtcNow;
            await _connections.UpsertAsync(new SocialConnection
            {
                Id = Guid.NewGuid().ToString("N"), Platform = platform, DisplayName = name,
                HomeUrl = HomeUrls[platform.ToString()], CreatedUtc = now, UpdatedUtc = now
            }, CancellationToken.None);
        }
        await ReloadAccountsAsync(SelectedAccount?.Id);
        Status = $"Đã nhập {pending.Count} tài khoản; bỏ qua {table.Rows.Count - pending.Count} dòng trùng. Kết nối OAuth cho từng tài khoản trước khi đăng.";
        _dialogs.Info("Nhập tài khoản", Status);
    });

    [RelayCommand]
    private Task ApplyBulkRouteAsync() => Guard(async () =>
    {
        var selected = Accounts.Where(x => x.IsChecked).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Chọn ít nhất một tài khoản.");
        if (!_broker.ListRoutes().Any(x => x.Id == BulkRouteId && x.Enabled))
            throw new InvalidOperationException("Chọn một tuyến mạng đang bật.");
        foreach (var row in selected)
        {
            var conn = await _connections.GetAsync(row.Id, CancellationToken.None)
                ?? throw new InvalidOperationException($"Không tìm thấy tài khoản {row.Name}.");
            var policy = AccountIpPolicy.FromJson(conn.IpPolicyJson);
            conn.IpPolicyJson = (policy is null ? new AccountIpPolicy(BulkRouteId, null, null)
                : policy with { RouteId = BulkRouteId, ApprovedFallbackRouteId = null }).ToJson();
            await _connections.UpsertAsync(conn, CancellationToken.None);
        }
        await ReloadAccountsAsync(SelectedAccount?.Id);
        Status = $"Đã đổi tuyến mạng cho {selected.Count} tài khoản.";
        _dialogs.Info("Tuyến mạng", Status);
    });

    [RelayCommand]
    private Task PauseSelectedAsync() => SetSelectedPausedAsync(true);

    [RelayCommand]
    private Task ResumeSelectedAsync() => SetSelectedPausedAsync(false);

    private Task SetSelectedPausedAsync(bool paused) => Guard(async () =>
    {
        var selected = Accounts.Where(x => x.IsChecked).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Chọn ít nhất một tài khoản.");
        foreach (var row in selected)
        {
            await _publishSettings.SetAsync("account.paused:" + row.Id, paused ? "true" : "false", CancellationToken.None);
            row.RunState = paused ? "Tạm dừng" : "Đang chạy";
        }
        Status = $"Đã {(paused ? "tạm dừng" : "tiếp tục")} {selected.Count} tài khoản. Bài đang upload không bị ngắt.";
        _dialogs.Info("Tài khoản", Status);
        await ReloadPublicationsAsync();
    });

    [RelayCommand]
    private Task ResumeAllAsync() => Guard(async () =>
    {
        var paused = Accounts.Where(x => x.RunState == "Tạm dừng").ToList();
        foreach (var row in paused)
        {
            await _publishSettings.SetAsync("account.paused:" + row.Id, "false", CancellationToken.None);
            row.RunState = "Đang chạy";
        }
        Status = $"Đã tiếp tục {paused.Count} tài khoản.";
        _dialogs.Info("Tài khoản", Status);
        await ReloadPublicationsAsync();
    });

    // ===== Tuyến mạng =====

    [RelayCommand]
    private void NewRoute()
    {
        SelectedRoute = null;
        RouteName = RouteRef = RouteCountry = "";
        RouteKind = NetworkRoute.Proxy;
        RouteEnabled = true;
        RouteCheck = null;
    }

    [RelayCommand]
    private Task SaveRouteAsync() => Guard(async () =>
    {
        var name = RouteName.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Nhập tên tuyến.");
        var reference = RouteRef.Trim();
        if (RouteKind != NetworkRoute.Direct && reference.Length == 0) throw new InvalidOperationException("Chọn hoặc nhập tham chiếu của tuyến (hồ sơ proxy / tên VPN / file .conf).");
        var country = RouteCountry.Trim().ToUpperInvariant();
        if (country.Length is not (0 or 2)) throw new InvalidOperationException("Quốc gia là mã 2 chữ cái (vd US, VN, JP).");

        var id = SelectedRoute?.Id ?? Guid.NewGuid().ToString("N");
        var route = new NetworkRoute(id, name, RouteKind, RouteKind == NetworkRoute.Direct ? null : reference, country.Length == 0 ? null : country, RouteEnabled);
        var list = _broker.ListRoutes().ToList();
        var i = list.FindIndex(r => r.Id == id);
        if (i >= 0) list[i] = route; else list.Add(route);
        await SaveRoutesAsync(list);
        ReloadRoutes();
        SelectedRoute = Routes.FirstOrDefault(r => r.Id == id);
        Status = "Đã lưu tuyến.";
    });

    [RelayCommand]
    private Task DeleteRouteAsync() => Guard(async () =>
    {
        if (SelectedRoute is not { } route) throw new InvalidOperationException("Chọn một tuyến.");
        foreach (var c in await _connections.ListAsync(CancellationToken.None))
        {
            var p = AccountIpPolicy.FromJson(c.IpPolicyJson);
            if (p is not null && (p.RouteId == route.Id || p.FallbackRouteIds?.Contains(route.Id) == true))
                throw new InvalidOperationException($"Tuyến đang được tài khoản \"{c.DisplayName}\" dùng; gỡ khỏi chính sách IP trước.");
        }
        if (!await _dialogs.ConfirmAsync("Xoá tuyến", $"Xoá tuyến \"{route.Name}\"?")) return;
        await SaveRoutesAsync(_broker.ListRoutes().Where(r => r.Id != route.Id).ToList());
        ReloadRoutes();
        NewRoute();
    });

    [RelayCommand]
    private Task TestRouteAsync() => Guard(async () =>
    {
        if (SelectedRoute is not { } route) throw new InvalidOperationException("Lưu tuyến trước rồi chọn nó để kiểm tra.");
        if (route.IsMachineWide && !await _dialogs.ConfirmAsync("Kiểm tra IP",
                $"Tuyến \"{route.Name}\" là VPN cả máy: kiểm tra sẽ bật VPN rồi tắt lại và có thể làm gián đoạn kết nối của app. Tiếp tục?")) return;
        // Tài khoản đang chọn thuộc hồ sơ thị trường: kiểm như lúc đăng (quốc gia policy của tài khoản trên tuyến này → của tuyến → của hồ sơ).
        var account = SelectedAccount;
        var market = account is null ? null : _settings.Get<MarketSettings>(MarketSettings.Section).RequirementFor(account.Id);
        var own = account is null ? null : AccountIpPolicy.FromJson((await _connections.GetAsync(account.Id, CancellationToken.None))?.IpPolicyJson);
        var country = (own?.RouteId == route.Id ? own.CountryCode : null) ?? route.ExpectedCountry;
        // Đọc lại file .conf mỗi lần kiểm: người dùng có thể đã sửa AllowedIPs sau khi lưu tuyến.
        RouteKillSwitch = KillSwitchCheck.Warning(route.Kind, route.Ref);
        IsBusy = true;
        RouteCheck = "Đang kiểm tra…";
        try
        {
            // Policy giả buộc broker thực sự gọi dịch vụ kiểm IP (tuyến direct không policy thì bỏ qua). Chờ khoá có hạn: VPN cả máy đang chờ
            // sẽ chặn mọi lượt đăng mạng thường, không để nút kiểm tra treo vô hạn sau một lượt upload dài.
            await using var lease = await _broker.AcquireAsync(route, new AccountIpPolicy(route.Id, null, country), lockWait: TestLockWait, market: market);
            var expected = country ?? market?.Country;
            _testedIp = lease.EgressIp is { Length: > 0 } ip ? (route.Id, ip) : null;
            RouteCheck = $"IP {lease.EgressIp ?? "không rõ"} · quốc gia {lease.EgressCountry ?? "không tra được"}"
                + (expected is { } e ? (string.Equals(e, lease.EgressCountry, StringComparison.OrdinalIgnoreCase) ? " · khớp mong muốn" : $" · mong muốn {e}") : "")
                + (market is not null && country is null ? $" (theo hồ sơ \"{market.ProfileName}\" của tài khoản \"{account!.Name}\")" : "");
        }
        catch (Exception ex) when (ex is RoutePolicyViolationException or RouteBusyException)
        {
            _testedIp = null;
            RouteCheck = ex.Message;
        }
        finally { IsBusy = false; }
    });

    /// <summary>Điền IP vừa kiểm vào chính sách để khỏi chép tay; vẫn phải bấm "Lưu chính sách" mới áp dụng.</summary>
    [RelayCommand]
    private Task PinTestedIpAsync() => Guard(() =>
    {
        if (_testedIp is not var (routeId, ip))
            throw new InvalidOperationException("Chọn tuyến của tài khoản rồi bấm \"Kiểm tra IP & quốc gia\" trước.");
        if (PolicyRouteId.Length > 0 && PolicyRouteId != routeId)
            throw new InvalidOperationException("IP vừa kiểm thuộc tuyến khác với tuyến chính của tài khoản. Kiểm tra đúng tuyến chính rồi ghim lại.");
        PolicyRouteId = routeId;
        PolicyIpMode = IpModes.Pinned;
        PolicyPinnedIp = ip;
        RouteCheck = $"Đã điền IP {ip} vào \"IP cố định\". Bấm \"Lưu chính sách\" để áp dụng.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private void PickConf()
    {
        var path = _dialogs.OpenFile("Chọn file WireGuard", "WireGuard (*.conf)|*.conf");
        if (path is not null) RouteRef = path;
    }

    private async Task SaveRoutesAsync(List<NetworkRoute> routes)
    {
        var net = _settings.Get<NetworkSettings>("network");
        net.Routes = routes;
        await _settings.SetAsync("network", net);
    }

    private void ReloadRoutes()
    {
        var current = _broker.ListRoutes();
        Routes.Clear();
        RouteOptions.Clear();
        RouteOptions.Add(new Opt("", "(Không dùng tuyến riêng)"));
        foreach (var r in current)
        {
            Routes.Add(r);
            RouteOptions.Add(new Opt(r.Id, r.Name));
        }
    }

    private async Task RefreshRefOptionsAsync()
    {
        RefOptions.Clear();
        if (RouteKind == NetworkRoute.Proxy)
            foreach (var p in await _proxies.ListAsync()) RefOptions.Add(new Opt(p.Profile.Id, $"{p.Profile.Name} — {p.Profile.Scheme}://{p.Profile.Host}:{p.Profile.Port}"));
        else if (RouteKind == NetworkRoute.WindowsVpn)
            foreach (var n in await _vpn.ListAsync()) RefOptions.Add(new Opt(n, n));
    }

    // ===== Chính sách IP =====

    [RelayCommand]
    private Task SavePolicyAsync() => Guard(async () =>
    {
        var a = Require();
        var conn = await _connections.GetAsync(a.Id, CancellationToken.None) ?? throw new InvalidOperationException("Không tìm thấy tài khoản.");
        if (string.IsNullOrEmpty(PolicyRouteId)) conn.IpPolicyJson = null;
        else
        {
            var country = PolicyCountry.Trim().ToUpperInvariant();
            if (country.Length is not (0 or 2)) throw new InvalidOperationException("Quốc gia là mã 2 chữ cái (vd US, VN, JP).");
            if (PolicyIpMode == IpModes.Pinned && string.IsNullOrWhiteSpace(PolicyPinnedIp)) throw new InvalidOperationException("Chế độ IP cố định cần nhập IP.");
            if (PolicyFailover != FailoverModes.Strict && string.IsNullOrEmpty(PolicyFallbackId)) throw new InvalidOperationException("Chọn tuyến dự phòng cho chế độ failover này.");
            conn.IpPolicyJson = new AccountIpPolicy(PolicyRouteId, string.IsNullOrEmpty(PolicyFallbackId) ? null : [PolicyFallbackId],
                country.Length == 0 ? null : country, PolicyIpMode, PolicyIpMode == IpModes.Pinned ? PolicyPinnedIp.Trim() : null, PolicyFailover,
                CountryChange: PolicyCountryChange).ToJson();
        }
        await _connections.UpsertAsync(conn, CancellationToken.None);
        await ReloadAccountsAsync(a.Id);
        Status = "Đã lưu chính sách IP. Lần đăng tới sẽ kiểm IP/quốc gia trước khi gửi.";
    });

    [RelayCommand]
    private Task ApproveFallbackAsync() => Guard(async () =>
    {
        var a = Require();
        var conn = await _connections.GetAsync(a.Id, CancellationToken.None) ?? throw new InvalidOperationException("Không tìm thấy tài khoản.");
        var policy = AccountIpPolicy.FromJson(conn.IpPolicyJson);
        if (policy?.FallbackRouteIds?.FirstOrDefault() is not { } fb) throw new InvalidOperationException("Tài khoản chưa có tuyến dự phòng.");
        conn.IpPolicyJson = (policy with { ApprovedFallbackRouteId = fb }).ToJson();
        await _connections.UpsertAsync(conn, CancellationToken.None);
        await _actions.ResolveByRefAsync(ActionKinds.RouteFailoverAsk, a.Id, CancellationToken.None);
        PendingApproval = null;
        Status = "Đã duyệt tuyến dự phòng; bài dời sẽ tự thử lại.";
    });

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dialogs.Error(ex.Message);
        }
    }
}
