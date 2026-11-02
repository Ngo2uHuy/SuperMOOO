using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Tray.Controls;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace SuperMOOO.App.Shell;

/// <summary>Icon khay hệ thống. Cửa sổ ẩn làm chủ icon để khay vẫn hoạt động khi chưa dựng (hoặc đã ẩn) cửa sổ chính.</summary>
public sealed class TrayController : IDisposable
{
    private readonly Window _owner;
    private readonly NotifyIcon _icon;

    public TrayController(Action open, Func<Task<bool>> isPaused, Func<Task> togglePause, Action exit, ILogger log)
    {
        _owner = new Window
        {
            Width = 0, Height = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false, Visibility = Visibility.Hidden
        };
        new WindowInteropHelper(_owner).EnsureHandle();
        Application.Current.MainWindow = _owner;

        var openItem = new MenuItem { Header = "Mở SuperMOOO" };
        openItem.Click += (_, _) => open();
        var pauseItem = new MenuItem { Header = "Tạm dừng đăng tự động" };
        pauseItem.Click += async (_, _) => await RunAsync(togglePause, log);
        var exitItem = new MenuItem { Header = "Thoát" };
        exitItem.Click += (_, _) => exit();

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Opened += async (_, _) =>
        {
            try
            {
                pauseItem.Header = await isPaused() ? "Tiếp tục đăng tự động" : "Tạm dừng đăng tự động";
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Không đọc được trạng thái đăng tự động");
            }
        };

        _icon = new NotifyIcon { TooltipText = "SuperMOOO", Menu = menu, Icon = LoadIcon()! };
        _icon.LeftDoubleClick += delegate { open(); };
        _icon.Register();
    }

    private static async Task RunAsync(Func<Task> action, ILogger log)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Lỗi khi đổi trạng thái đăng tự động từ khay");
        }
    }

    private static BitmapSource? LoadIcon()
    {
        if (Environment.ProcessPath is not { } path) return null;
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon is null) return null;
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    public void Dispose()
    {
        _icon.Unregister();
        _icon.Dispose();
    }
}
