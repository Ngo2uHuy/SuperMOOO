using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Infrastructure.Network;

namespace SuperMOOO.App.Features.S16;

public sealed record ProxyRow(string Id, string Name, string Address, string Username, bool Favorite)
{
    public string Display => $"{(Favorite ? "★ " : "")}{Name} — {Address}";
}

[Service(ServiceLifetime.Transient)]
public sealed partial class NetworkViewModel : ObservableObject
{
    private readonly NetworkRouter _router;
    private readonly ProxyProfileService _profiles;
    private readonly WindowsVpnService _vpn;
    private readonly IDialogService _dialogs;

    public NetworkViewModel(NetworkRouter router, ProxyProfileService profiles, WindowsVpnService vpn, IDialogService dialogs)
    {
        _router = router;
        _profiles = profiles;
        _vpn = vpn;
        _dialogs = dialogs;
        _router.Changed += (_, _) => UpdateRoute();
        UpdateRoute();
    }

    public IReadOnlyList<string> Schemes { get; } = ["http", "https", "socks5", "socks4", "socks4a"];
    public ObservableCollection<ProxyRow> Profiles { get; } = [];
    public ObservableCollection<string> Vpns { get; } = [];

    [ObservableProperty]
    public partial string Route { get; set; } = "";

    [ObservableProperty]
    public partial ProxyRow? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string? SelectedVpn { get; set; }

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial string NewScheme { get; set; } = "http";

    [ObservableProperty]
    public partial string NewHost { get; set; } = "";

    [ObservableProperty]
    public partial string NewPort { get; set; } = "";

    [ObservableProperty]
    public partial string NewUsername { get; set; } = "";

    [ObservableProperty]
    public partial string ImportText { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    public async Task LoadAsync()
    {
        await ReloadProfilesAsync();
        await RefreshVpnsAsync();
    }

    /// <summary>Lưu hồ sơ từ ô nhập; mật khẩu lấy từ PasswordBox nên truyền riêng, không đi qua binding.</summary>
    public async Task SaveProfileAsync(string password)
    {
        if (!int.TryParse(NewPort, out var port))
        {
            Status = "Cổng phải là số.";
            return;
        }

        await Guard(async () =>
        {
            var name = string.IsNullOrWhiteSpace(NewName) ? $"{NewHost}:{port}" : NewName.Trim();
            await _profiles.SaveAsync(new ProxyProfile("", name, NewScheme, NewHost.Trim(), port, string.IsNullOrWhiteSpace(NewUsername) ? null : NewUsername.Trim()), password);
            NewName = NewHost = NewPort = NewUsername = "";
            await ReloadProfilesAsync();
            Status = "Đã lưu hồ sơ.";
        });
    }

    [RelayCommand]
    private Task ImportAsync() => Guard(async () =>
    {
        var count = await _profiles.ImportAsync(ImportText);
        ImportText = "";
        await ReloadProfilesAsync();
        Status = $"Đã nhập {count} hồ sơ.";
    });

    [RelayCommand]
    private Task ConnectAsync() => Guard(async () =>
    {
        if (SelectedProfile is null) throw new InvalidOperationException("Chọn một hồ sơ để áp dụng.");
        await _router.ConnectAsync(SelectedProfile.Id);
        Status = "Đã áp dụng proxy.";
    });

    [RelayCommand]
    private Task DisconnectAsync() => Guard(async () =>
    {
        await _router.DisconnectAsync();
        Status = "Đã ngắt, quay về kết nối trực tiếp.";
    });

    [RelayCommand]
    private Task DeleteAsync() => Guard(async () =>
    {
        if (SelectedProfile is null || !await _dialogs.ConfirmAsync("Xoá hồ sơ", $"Xoá hồ sơ \"{SelectedProfile.Name}\"?")) return;
        await _profiles.DeleteAsync(SelectedProfile.Id);
        await ReloadProfilesAsync();
    });

    [RelayCommand]
    private Task ToggleFavoriteAsync() => Guard(async () =>
    {
        if (SelectedProfile is null) return;
        await _profiles.SetFavoriteAsync(SelectedProfile.Id, !SelectedProfile.Favorite);
        await ReloadProfilesAsync();
    });

    [RelayCommand]
    private async Task VerifyAsync()
    {
        Status = "Đang kiểm tra IP…";
        var result = await _router.VerifyAsync(CancellationToken.None);
        Status = result.Ok ? $"IP {result.Ip} · {result.Country ?? "không rõ quốc gia"}" : $"Không kiểm tra được: {result.Message}";
    }

    [RelayCommand]
    private Task RefreshVpnsAsync() => Guard(async () =>
    {
        Vpns.Clear();
        foreach (var name in await _vpn.ListAsync()) Vpns.Add(name);
    });

    [RelayCommand]
    private Task ConnectVpnAsync() => Guard(async () =>
    {
        if (SelectedVpn is null) throw new InvalidOperationException("Chọn một VPN.");
        await _router.ConnectVpnAsync(SelectedVpn);
        Status = $"Đã nối VPN {SelectedVpn}.";
    });

    private async Task ReloadProfilesAsync()
    {
        var entries = await _profiles.ListAsync();
        Profiles.Clear();
        foreach (var e in entries)
            Profiles.Add(new ProxyRow(e.Profile.Id, e.Profile.Name, $"{e.Profile.Scheme}://{e.Profile.Host}:{e.Profile.Port}", e.Profile.Username ?? "", e.Favorite));
    }

    private void UpdateRoute() => Route = _router.Current.Mode switch
    {
        "proxy" => $"Đang đi qua proxy {_router.Current.ProxyUri}",
        "vpn" => $"Đang dùng VPN {_router.Current.VpnName}",
        _ => "Kết nối trực tiếp"
    };

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            Status = ex.Message;
        }
    }
}
