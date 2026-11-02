using System.Windows;
using Microsoft.Extensions.Hosting;
using SuperMOOO.App.Controls;
using SuperMOOO.Infrastructure.Operations;

namespace SuperMOOO.App.Shell;

/// <summary>Hiện toast khi ổ dữ liệu/thư mục xuất xuống dưới ngưỡng cảnh báo; mục Cần xử lý do DiskMonitor tạo.</summary>
[Service]
public sealed class DiskAlertNotifier(DiskMonitor monitor, IDialogService dialogs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        monitor.LowSpace += OnLowSpace;
        _ = Task.Run(() => monitor.EvaluateAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        monitor.LowSpace -= OnLowSpace;
        return Task.CompletedTask;
    }

    private void OnLowSpace(DiskStatus disk) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
            dialogs.Info("Ổ đĩa sắp đầy", $"Ổ {disk.Root} chỉ còn {disk.FreeBytes / (1024.0 * 1024 * 1024):0.0} GB. Dọn dung lượng ở Cài đặt > Sao lưu & lưu trữ."));
}
