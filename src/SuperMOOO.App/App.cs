using System.Windows;
using SuperMOOO.App.Features.S16;
using System.Windows.Threading;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Publications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace SuperMOOO.App;

public sealed class App(IHost host, SmokeOptions? smoke, bool background = false, EventWaitHandle? activation = null) : Application
{
    private int _faults;
    private MainWindow? _window;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandledException;
        // Lỗi ngoài luồng UI (thread nền, task không ai await) không đi qua DispatcherUnhandledException nên phải bắt riêng để có trong log.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            host.Services.GetRequiredService<ILogger<App>>().LogCritical(args.ExceptionObject as Exception, "Lỗi nền không xử lý (app sẽ thoát: {Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            host.Services.GetRequiredService<ILogger<App>>().LogError(args.Exception, "Lỗi trong tác vụ nền không được theo dõi");
            args.SetObserved();
        };

        // ThemeService thay bộ "theme" khi đổi Sáng/Tối; Tokens là token riêng tối thiểu của app.
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Pack("Themes/Tokens.xaml") });
        AccessibleNames.Install();
        InputHints.Install();
        // Như OnExit: hosted service await không ConfigureAwait sẽ chờ quay về UI thread đang bị chặn ở đây → deadlock.
        Task.Run(() => host.StartAsync()).GetAwaiter().GetResult();
        if (BindingTraceLogger.IsRequested) BindingTraceLogger.Attach(host.Services.GetRequiredService<ILogger<App>>());

        if (smoke is null)
        {
            _tray = new TrayController(ShowMainWindow, IsAutomationPausedAsync, ToggleAutomationPausedAsync, () => _ = ExitAsync(),
                host.Services.GetRequiredService<ILogger<App>>());
            StartActivationListener();
            // Chế độ nền: không dựng cửa sổ chính, chỉ hiện icon khay (host đã chạy JobQueue, Quartz, đăng bài).
            if (background) return;
        }

        if (EnsureWindow() is not { } window) return;
        if (smoke is null)
        {
            window.Show();
            return;
        }

        window.ShowActivated = false;
        if (smoke.ScreenshotDir is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowState = WindowState.Normal;
            (window.Left, window.Top, window.Width, window.Height) = (0, 0, SmokeRunner.ShotWidth, SmokeRunner.ShotHeight);
        }

        window.Show();
        _ = RunSmokeAsync(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Chạy trên thread pool: hosted service await lại về UI thread sẽ deadlock khi UI thread đang chờ ở đây.
        Task.Run(() => host.StopAsync()).GetAwaiter().GetResult();
        host.Dispose();
        base.OnExit(e);
    }

    public static Uri Pack(string relative) => new($"pack://application:,,,/SuperMOOO.App;component/{relative}");

    private async Task RunSmokeAsync(MainWindow window)
    {
        var code = await SmokeRunner.RunAsync(window, host.Services, smoke!);
        Shutdown(code != 0 || _faults > 0 ? 1 : 0);
    }

    private MainWindow? EnsureWindow()
    {
        if (_window is not null) return _window;
        try
        {
            _window = host.Services.GetRequiredService<MainWindow>();
        }
        catch (Exception ex)
        {
            // Không dựng được cửa sổ chính thì thoát hẳn thay vì treo tiến trình không có cửa sổ.
            host.Services.GetRequiredService<ILogger<App>>().LogCritical(ex, "Không mở được cửa sổ chính");
            Shutdown(1);
            return null;
        }

        MainWindow = _window;
        _window.Closed += (_, _) => Shutdown(0);
        if (smoke is null)
        {
            _window.Closing += OnMainWindowClosing;
            _window.Loaded += OnSetupLoaded;
        }
        return _window;
    }

    private async void OnSetupLoaded(object sender, RoutedEventArgs e)
    {
        _window!.Loaded -= OnSetupLoaded;
        try
        {
            if (!ComponentSetup.Required(host.Services.GetRequiredService<AppPaths>())) return;
            var screen = host.Services.GetRequiredService<ScreenRegistry>().Find("S16");
            if (screen is null) return;
            _window.ViewModel.Select(screen);
            if (_window.ViewModel.CurrentView is SettingsView settings) await settings.StartSetupAsync();
        }
        catch (Exception ex)
        {
            host.Services.GetRequiredService<IDialogService>().Error("Không thể thiết lập tự động: " + ex.Message);
        }
    }

    private void ShowMainWindow()
    {
        if (EnsureWindow() is not { } window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Maximized;
        // Activate đơn thuần có thể bị Windows chặn khi lệnh đến từ tiến trình khác.
        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
    }

    private void StartActivationListener()
    {
        if (activation is null) return;
        new Thread(() =>
        {
            while (activation.WaitOne()) Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true, Name = "ActivationListener" }.Start();
    }

    private async Task<bool> IsAutomationPausedAsync() =>
        await host.Services.GetRequiredService<IPublishSettingsStore>().GetAsync(SettingKeys.AutomationPaused, CancellationToken.None) == "true";

    private async Task ToggleAutomationPausedAsync()
    {
        var paused = await IsAutomationPausedAsync();
        await host.Services.GetRequiredService<IPublishSettingsStore>().SetAsync(SettingKeys.AutomationPaused, paused ? "false" : "true", CancellationToken.None);
    }

    /// <summary>Nút X / Alt+F4: chạy nền thì ẩn xuống khay (lịch đăng vẫn chạy), không thì hỏi thoát.</summary>
    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        var settings = host.Services.GetRequiredService<ISettingsStore>();
        if (settings.Get<BackgroundSettings>("background").RunInBackground) await HideToTrayAsync(settings);
        else await ExitAsync();
    }

    private async Task HideToTrayAsync(ISettingsStore settings)
    {
        var state = settings.Get<BackgroundSettings>("background");
        if (!state.HintShown)
        {
            state.HintShown = true;
            await settings.SetAsync("background", state);
            host.Services.GetRequiredService<IDialogService>().Info("SuperMOOO vẫn chạy nền",
                "Lịch đăng bài tiếp tục chạy. Bấm icon ở khay hệ thống để mở lại hoặc thoát hẳn.");
            // Thông báo nằm trong cửa sổ chính nên chờ người dùng kịp đọc rồi mới ẩn.
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        _window?.Hide();
    }

    /// <summary>
    /// Hỏi xác nhận (nêu số tác vụ đang chạy, bài sắp đăng), đồng ý thì thoát hẳn ngay.
    /// Thoát cứng thay vì dừng host tuần tự vì dừng ffmpeg/worker/server TTS có thể treo lâu; tiến trình con nằm trong Job Object nên chết theo app,
    /// job đang chạy dở được hàng đợi xử lý lại ở lần mở sau.
    /// </summary>
    private async Task ExitAsync()
    {
        // Hộp xác nhận nằm trong cửa sổ chính nên phải hiện cửa sổ trước (đang ẩn ở khay hoặc chưa dựng).
        ShowMainWindow();
        var services = host.Services;
        var active = 0;
        var scheduled = 0;
        try
        {
            var now = DateTimeOffset.UtcNow;
            scheduled = (await services.GetRequiredService<PublicationService>()
                .ListAsync(new ListPublicationsRequest(nameof(PublicationStatus.Scheduled), null, now, now.AddHours(24), 1, 1), CancellationToken.None)).Total;
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<App>>().LogWarning(ex, "Không đọc được số bài hẹn giờ khi thoát");
        }

        try
        {
            active = (await services.GetRequiredService<IJobQueue>().ListAsync(new JobQuery(Statuses: [JobStatus.Running, JobStatus.Queued]))).Count;
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<App>>().LogWarning(ex, "Không đọc được số tác vụ đang chạy khi thoát");
        }

        var message = active > 0
            ? $"Đang có {active} tác vụ chạy hoặc chờ. Thoát bây giờ sẽ dừng ngay; tác vụ dở sẽ tự chạy lại ở lần mở app sau."
            : "Bạn có chắc muốn thoát SuperMOOO?";
        if (scheduled > 0) message += $"\n\nCó {scheduled} bài đã hẹn giờ trong 24 giờ tới. Thoát thì các bài này sẽ không được đăng cho tới khi bạn mở lại app.";
        var timeline = services.GetRequiredService<Features.S17.TimelineViewModel>();
        if (timeline.IsDirty) message += "\n\nTimeline có thay đổi chưa lưu. Chọn Huỷ để quay lại lưu dự án.";
        if (timeline.IsRunning) message += "\n\nTác vụ preview/xuất Timeline đang chạy và sẽ bị dừng.";
        if (!await services.GetRequiredService<IDialogService>().ConfirmAsync("Thoát ứng dụng", message, "Thoát", "Huỷ")) return;

        services.GetRequiredService<ILogger<App>>().LogInformation("Người dùng thoát ứng dụng ({Active} tác vụ đang chạy/chờ, {Scheduled} bài hẹn giờ)", active, scheduled);
        _tray?.Dispose();
        Environment.Exit(0);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        host.Services.GetRequiredService<ILogger<App>>().LogError(e.Exception, "Lỗi UI không xử lý");
        _faults++;
        e.Handled = true;
    }
}
