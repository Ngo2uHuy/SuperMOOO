using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Social.Platforms;

namespace SuperMOOO.App.Features.S16;

public sealed record PlatformRowView(PlatformStatus Status)
{
    public string Id => Status.Platform.Id;
    public string Name => Status.Platform.Name;
    public string State => Status.Status == "configured" ? "Đã cấu hình" : "Chưa cấu hình";
    public string AuthType => Status.AuthType;
    public string Message => Status.Message ?? "";
}

[Service(ServiceLifetime.Transient)]
public sealed partial class PlatformsViewModel(PlatformSessionService sessions, IDialogService dialogs) : ObservableObject
{
    public ObservableCollection<PlatformRowView> Rows { get; } = [];
    public ObservableCollection<string> SecretNames { get; } = [];

    [ObservableProperty]
    public partial PlatformRowView? Selected { get; set; }

    [ObservableProperty]
    public partial string? SelectedSecretName { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial string? Status { get; set; }

    partial void OnSelectedChanged(PlatformRowView? value)
    {
        SecretNames.Clear();
        foreach (var name in value?.Status.Platform.SecretNames ?? []) SecretNames.Add(name);
        SelectedSecretName = SecretNames.FirstOrDefault();
    }

    public async Task LoadAsync()
    {
        var selectedId = Selected?.Id;
        var list = await sessions.ListAsync();
        Rows.Clear();
        foreach (var s in list) Rows.Add(new PlatformRowView(s));
        Selected = Rows.FirstOrDefault(r => r.Id == selectedId) ?? Rows.FirstOrDefault();
    }

    [RelayCommand]
    private Task SaveAsync() => Guard(async () =>
    {
        if (Selected is null || SelectedSecretName is null) return;
        await sessions.SaveAsync(Selected.Id, Value, SelectedSecretName);
        Value = "";
        Status = "Đã lưu (mã hoá DPAPI).";
        await LoadAsync();
    });

    [RelayCommand]
    private Task CheckAsync() => Guard(async () =>
    {
        if (Selected is null) return;
        var result = await sessions.CheckAsync(Selected.Id);
        Status = result.Message;
        await LoadAsync();
    });

    [RelayCommand]
    private Task ClearAsync() => Guard(async () =>
    {
        if (Selected is null || !await dialogs.ConfirmAsync("Xoá thông tin đăng nhập", $"Xoá toàn bộ cookie/token đã lưu của {Selected.Name}?")) return;
        await sessions.ClearAsync(Selected.Id);
        Status = "Đã xoá.";
        await LoadAsync();
    });

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ArgumentException ex)
        {
            dialogs.Error(ex.Message);
        }
    }
}
