using System.Collections.ObjectModel;
using SuperMOOO.Infrastructure.Network;

namespace SuperMOOO.App.Features.S23;

public sealed class AccountBrowserPreferences
{
    public HashSet<string> NeedsVerification { get; set; } = [];
}

public sealed partial class AccountsViewModel
{
    public const int BrowserTabIndex = 3;
    public ObservableCollection<AccountRow> BrowserAccounts { get; } = [];
    public IReadOnlyList<Opt> BrowserColumnOptions { get; } =
        [new("0", "Tự động"), new("1", "1 cột"), new("2", "2 cột"), new("3", "3 cột"), new("4", "4 cột"), new("5", "5 cột"), new("6", "6 cột")];

    [ObservableProperty]
    public partial AccountRow? SelectedBrowserAccount { get; set; }
    [ObservableProperty]
    public partial string BrowserSearch { get; set; } = "";
    [ObservableProperty]
    public partial string BrowserPlatformFilter { get; set; } = "Tất cả";
    [ObservableProperty]
    public partial string BrowserColumns { get; set; } = "0";
    [ObservableProperty]
    public partial bool BrowserAutoArrange { get; set; } = true;
    [ObservableProperty]
    public partial string BrowserProgress { get; set; } = "Chọn tài khoản rồi bấm Mở đã chọn.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditBrowsers))]
    public partial bool BrowserBusy { get; set; }
    public bool CanEditBrowsers => !BrowserBusy;
    [ObservableProperty]
    public partial string BrowserSummary { get; set; } = "";

    partial void OnBrowserSearchChanged(string value) => FilterBrowserAccounts();
    partial void OnBrowserPlatformFilterChanged(string value) => FilterBrowserAccounts();

    private void FilterBrowserAccounts()
    {
        BrowserAccounts.Clear();
        foreach (var row in Accounts.Where(x => (BrowserPlatformFilter == "Tất cả" || x.Platform == BrowserPlatformFilter)
                     && x.Name.Contains(BrowserSearch.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            BrowserAccounts.Add(row);
    }

    [RelayCommand]
    private void SelectBrowserAccounts()
    {
        foreach (var row in BrowserAccounts) row.BrowserChecked = true;
    }

    [RelayCommand]
    private void ClearBrowserSelection()
    {
        foreach (var row in Accounts) row.BrowserChecked = false;
    }

    [RelayCommand]
    public void RefreshBrowserStates()
    {
        var states = _browser.GetWindowStates();
        var preferences = _settings.Get<AccountBrowserPreferences>("account.browser");
        foreach (var row in Accounts)
        {
            var state = states.GetValueOrDefault(row.Id);
            row.BrowserState = state?.Status ?? "Chưa mở trong phiên này";
            row.BrowserWindowCount = state?.WindowCount ?? 0;
            row.BrowserVerification = preferences.NeedsVerification.Contains(row.Id) ? "Cần xác minh (đánh dấu)" : "";
        }
        BrowserSummary = $"{Accounts.Count(x => x.BrowserWindowCount > 0)} tài khoản đang mở · {Accounts.Sum(x => x.BrowserWindowCount)} cửa sổ · {Accounts.Count(x => x.BrowserVerification.Length > 0)} cần xác minh";
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private Task OpenSelectedBrowsersAsync(CancellationToken ct) => Guard(async () =>
    {
        var selected = BrowserAccounts.Where(x => x.BrowserChecked).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Đánh dấu tài khoản cần mở trong danh sách đang hiển thị.");
        var errors = new List<string>();
        var notes = new HashSet<string>();
        var opened = 0;
        var handled = 0;
        BrowserBusy = true;
        try
        {
            foreach (var account in selected)
            {
                ct.ThrowIfCancellationRequested();
                BrowserProgress = $"Đang mở {++handled}/{selected.Count}: {account.Name}";
                try
                {
                    var connection = await _connections.GetAsync(account.Id, ct)
                        ?? throw new InvalidOperationException("Tài khoản không còn tồn tại.");
                    var policy = AccountIpPolicy.FromJson(connection.IpPolicyJson);
                    var url = string.IsNullOrWhiteSpace(connection.HomeUrl)
                        ? HomeUrls.GetValueOrDefault(connection.Platform.ToString(), "https://www.google.com") : connection.HomeUrl;
                    var launch = _browser.Open(account.Id, policy, url, reuseExisting: true);
                    if (!launch.Ok) throw new InvalidOperationException(launch.Message);
                    notes.Add(launch.Message);
                    for (var attempt = 0; attempt < 32; attempt++)
                    {
                        await Task.Delay(250, ct);
                        var state = _browser.GetWindowStates().GetValueOrDefault(account.Id);
                        if (state?.WindowCount > 0) { opened++; break; }
                        if (state?.Status == "Không theo dõi được") break;
                    }
                    if (_browser.GetWindowStates().GetValueOrDefault(account.Id)?.WindowCount is not > 0)
                        errors.Add($"{account.Name}: chưa tìm thấy cửa sổ. Nếu profile đang mở từ phiên app trước, hãy đóng cửa sổ đó rồi mở lại tại đây.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add($"{account.Name}: {ex.Message}"); }
                RefreshBrowserStates();
            }
            if (BrowserAutoArrange && opened > 0)
            {
                var arranged = _browser.ArrangeWindows(selected.Select(x => x.Id).ToArray(), int.Parse(BrowserColumns));
                if (!arranged.Ok) errors.Add(arranged.Message);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            BrowserBusy = false;
            RefreshBrowserStates();
            BrowserProgress = $"{(ct.IsCancellationRequested ? "Đã dừng" : "Hoàn tất")}: {opened}/{selected.Count} tài khoản có cửa sổ đang mở.";
            Status = string.Join(" ", notes);
            _dialogs.Info("Trình duyệt tài khoản", BrowserProgress);
            if (errors.Count > 0) _dialogs.Error(string.Join(Environment.NewLine, errors));
        }
    });

    [RelayCommand]
    private Task ArrangeBrowsersAsync() => Guard(() =>
    {
        var ids = BrowserAccounts.Where(x => x.BrowserChecked).Select(x => x.Id).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Đánh dấu tài khoản cần xếp cửa sổ.");
        var result = _browser.ArrangeWindows(ids, int.Parse(BrowserColumns));
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        BrowserProgress = result.Message;
        _dialogs.Info("Trình duyệt tài khoản", result.Message);
        RefreshBrowserStates();
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task FocusBrowserAsync() => Guard(() =>
    {
        var account = SelectedBrowserAccount ?? throw new InvalidOperationException("Chọn một dòng tài khoản.");
        var result = _browser.BringToFront(account.Id);
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task MarkBrowserVerificationAsync() => SetBrowserVerificationAsync(true);

    [RelayCommand]
    private Task ClearBrowserVerificationAsync() => SetBrowserVerificationAsync(false);

    private Task SetBrowserVerificationAsync(bool required) => Guard(async () =>
    {
        var account = SelectedBrowserAccount ?? throw new InvalidOperationException("Chọn một dòng tài khoản.");
        var preferences = _settings.Get<AccountBrowserPreferences>("account.browser");
        if (required) preferences.NeedsVerification.Add(account.Id);
        else preferences.NeedsVerification.Remove(account.Id);
        await _settings.SetAsync("account.browser", preferences);
        RefreshBrowserStates();
    });
}
