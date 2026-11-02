using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;

namespace SuperMOOO.App.Shell;

public sealed record SmokeOptions(IReadOnlyList<string> Screens, string? ScreenshotDir);

/// <summary>
/// `--smoke all|S01,S02`: mở từng màn, chờ khởi tạo, thoát mã 0 nếu không có lỗi binding/exception.
/// `--screenshot &lt;dir&gt;` (dev): chụp mỗi màn 1440x900 ở cả Sáng và Tối vào thư mục đó.
/// </summary>
public static class SmokeRunner
{
    public const int ShotWidth = 1440;
    public const int ShotHeight = 900;

    public static SmokeOptions? ParseArgs(string[] args)
    {
        var i = Index(args, "--smoke");
        if (i < 0) return null;
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : "all";
        IReadOnlyList<string> screens = value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? ["all"]
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var shot = Index(args, "--screenshot");
        return new SmokeOptions(screens, shot >= 0 && shot + 1 < args.Length ? Path.GetFullPath(args[shot + 1]) : null);
    }

    public static async Task<int> RunAsync(MainWindow window, IServiceProvider services, SmokeOptions options)
    {
        var log = services.GetRequiredService<ILogger<MainWindow>>();
        var registry = services.GetRequiredService<ScreenRegistry>();
        var theme = services.GetRequiredService<ThemeService>();
        var bindingErrors = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);

        IReadOnlyList<ScreenDescriptor?> screens = options.Screens is ["all"] ? [.. registry.All] : [.. options.Screens.Select(registry.Find)];
        if (options.ScreenshotDir is not null) Directory.CreateDirectory(options.ScreenshotDir);
        ApplicationTheme[] themes = options.ScreenshotDir is null ? [ApplicationTheme.Unknown] : [ApplicationTheme.Light, ApplicationTheme.Dark];

        var failures = 0;
        foreach (var pass in themes)
        {
            if (pass != ApplicationTheme.Unknown) theme.Preview(pass);
            foreach (var screen in screens)
            {
                if (screen is null)
                {
                    log.LogError("Smoke: màn không tồn tại");
                    failures++;
                    continue;
                }

                try
                {
                    window.ViewModel.Select(screen);
                    await Idle(window);
                    if (window.ViewModel.CurrentView is IAsyncScreen async) await async.WhenLoadedAsync();
                    await Idle(window);
                    if (options.ScreenshotDir is { } dir) await CaptureScreenAsync(window, dir, screen.Id, pass.ToString().ToLowerInvariant());

                    log.LogInformation("Smoke: {Screen} OK", screen.Id);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Smoke: {Screen} lỗi", screen.Id);
                    failures++;
                }
            }
        }

        foreach (var error in bindingErrors.Errors)
            log.LogError("Smoke: lỗi binding {Error}", error);
        return failures + bindingErrors.Errors.Count;
    }

    private static int Index(string[] args, string name) => Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static Task Idle(MainWindow window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

    /// <summary>Chụp màn; màn có tab (hoặc danh sách mục cài đặt) thì chụp thêm từng trang.</summary>
    private static async Task CaptureScreenAsync(MainWindow window, string dir, string id, string theme)
    {
        await Task.Delay(400);   // chờ hiệu ứng chuyển màn
        await Idle(window);
        Capture(window, Path.Combine(dir, $"{id}-{theme}.png"));
        if (FindPager(window.ViewModel.CurrentView as DependencyObject) is not { } pager) return;
        for (var i = 1; i < pager.Items.Count; i++)
        {
            pager.SelectedIndex = i;
            await Task.Delay(250);
            await Idle(window);
            Capture(window, Path.Combine(dir, $"{id}-p{i}-{theme}.png"));
        }

        pager.SelectedIndex = 0;
    }

    private static Selector? FindPager(DependencyObject? node)
    {
        if (node is null) return null;
        if (node is TabControl || node is ListBox { Name: "Sections" }) return (Selector)node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindPager(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    /// <summary>Mica không vẽ được vào bitmap nên lót nền theme trước rồi vẽ nội dung cửa sổ lên.</summary>
    private static void Capture(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var area = new Rect(0, 0, ShotWidth, ShotHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)window.FindResource("ApplicationBackgroundBrush"), null, area);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null,
                new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap(ShotWidth, ShotHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly List<string> _errors = [];
        public IReadOnlyList<string> Errors => _errors;

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _errors.Add(message);
        }
    }
}
