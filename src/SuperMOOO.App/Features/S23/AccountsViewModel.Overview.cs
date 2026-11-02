namespace SuperMOOO.App.Features.S23;

public sealed partial class AccountsViewModel
{
    [ObservableProperty] public partial string AttentionSummary { get; set; } = "0";
    [ObservableProperty] public partial bool ShowAllAccountActivity { get; set; } = true;
    public string AccountSelectionSummary => $"Đã chọn {Accounts.Count(x => x.IsChecked)} · Hiển thị {VisibleAccounts.Count}/{Accounts.Count} tài khoản";

    partial void OnShowAllAccountActivityChanged(bool value) => _ = Guard(() => LoadAccountDetailAsync(SelectedAccount));

    [RelayCommand]
    private void SelectVisibleAccounts()
    {
        foreach (var account in VisibleAccounts) account.IsChecked = true;
    }

    [RelayCommand]
    private void ClearAccountSelection()
    {
        foreach (var account in Accounts) account.IsChecked = false;
    }

    [RelayCommand]
    private void ShowAccountDetails(AccountRow? account)
    {
        if (account is null) return;
        SelectedAccount = account;
        SelectedTabIndex = 1;
    }

    [RelayCommand]
    private Task OpenAccountBrowserRowAsync(AccountRow? account) => Guard(async () =>
    {
        if (account is not null) await OpenAccountBrowserAsync(account);
    });

    [RelayCommand]
    private Task ResumeAccountRowAsync(AccountRow? account) => Guard(async () =>
    {
        if (account is null) return;
        await _publishSettings.SetAsync("account.paused:" + account.Id, "false", CancellationToken.None);
        account.RunState = "Đang chạy";
        await ReloadPublicationsAsync();
        _dialogs.Info("Tài khoản", $"Đã tiếp tục {account.Name}. Bài trong hàng đợi sẽ chạy khi đến lượt.");
    });
}
