using SuperMOOO.App.Controls;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Infrastructure.Shell;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class BackgroundViewModel(ISettingsStore settings, StartupRegistration startup, IPublishSettingsStore publish, IDialogService dialogs)
    : ObservableObject
{
    private bool _loading;

    [ObservableProperty]
    public partial bool RunInBackground { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool AutomationPaused { get; set; }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            RunInBackground = settings.Get<BackgroundSettings>("background").RunInBackground;
            StartWithWindows = startup.IsEnabled;
            AutomationPaused = await publish.GetAsync(SettingKeys.AutomationPaused, CancellationToken.None) == "true";
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnRunInBackgroundChanged(bool value) => Apply(async () =>
    {
        var state = settings.Get<BackgroundSettings>("background");
        state.RunInBackground = value;
        await settings.SetAsync("background", state);
    });

    partial void OnStartWithWindowsChanged(bool value) => Apply(() =>
    {
        startup.SetEnabled(value);
        return Task.CompletedTask;
    });

    partial void OnAutomationPausedChanged(bool value) =>
        Apply(() => publish.SetAsync(SettingKeys.AutomationPaused, value ? "true" : "false", CancellationToken.None));

    // Mỗi công tắc lưu ngay khi đổi; lỗi ở biên I/O (registry, DB) báo qua dialog.
    private async void Apply(Func<Task> save)
    {
        if (_loading) return;
        try
        {
            await save();
        }
        catch (Exception ex)
        {
            dialogs.Error($"Không lưu được thiết lập: {ex.Message}");
        }
    }
}
