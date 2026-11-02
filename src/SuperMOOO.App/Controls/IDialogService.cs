using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>Mọi hộp thoại đi qua đây để ViewModel không gọi MessageBox/FileDialog trực tiếp (F22) và test thay được.</summary>
public interface IDialogService
{
    /// <summary>Thông báo nhẹ (Snackbar), không chặn thao tác.</summary>
    void Info(string title, string message);

    /// <summary>Lỗi nhẹ (Snackbar đỏ, hiện lâu hơn).</summary>
    void Error(string message);

    /// <summary>Hộp xác nhận (ContentDialog); true khi bấm nút đồng ý.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Đồng ý", string cancelText = "Huỷ");

    /// <summary>Văn bản dài chỉ để đọc (hướng dẫn).</summary>
    Task ShowTextAsync(string title, string text);

    string? OpenFile(string title, string filter);
    string[] OpenFiles(string title, string filter);
    string? SaveFile(string title, string filter, string defaultName);
    string? PickFolder(string title);
}

[Service(As = [typeof(IDialogService)])]
public sealed class WpfDialogService(ISnackbarService snackbar, IContentDialogService contentDialogs, ILogger<WpfDialogService> log) : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public void Info(string title, string message) =>
        snackbar.Show(title, message, ControlAppearance.Info, new SymbolIcon(SymbolRegular.Info24), TimeSpan.FromSeconds(4));

    public void Error(string message)
    {
        // Mọi lỗi người dùng thấy cũng vào log để giám sát; nơi gọi đã bắt exception nên chỉ còn thông điệp.
        log.LogWarning("Lỗi hiển thị cho người dùng: {Message}", message);
        snackbar.Show("Có lỗi", ErrorText.Clean(message), ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), TimeSpan.FromSeconds(8));
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Đồng ý", string cancelText = "Huỷ")
    {
        var result = await ShowAsync(dialog =>
        {
            dialog.Title = title;
            dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            dialog.PrimaryButtonText = confirmText;
            dialog.CloseButtonText = cancelText;
            dialog.DefaultButton = ContentDialogButton.Primary;
        });
        return result == ContentDialogResult.Primary;
    }

    public Task ShowTextAsync(string title, string text) => ShowAsync(dialog =>
    {
        dialog.Title = title;
        dialog.Content = HelpContent.Build(text);
        dialog.CloseButtonText = "Đóng";
        dialog.DialogMaxWidth = 720;
    });

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string[] OpenFiles(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public string? SaveFile(string title, string filter, string defaultName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    /// <summary>Hộp thoại hiện trong cửa sổ đang hoạt động (cửa sổ phụ như Clone giọng có host riêng), mặc định là cửa sổ chính.</summary>
    private Task<ContentDialogResult> ShowAsync(Action<ContentDialog> setup)
    {
        if (Owner is { } owner && owner != Application.Current.MainWindow && ContentDialogHost.GetForWindow(owner) is { } host)
        {
            var local = new ContentDialog(host);
            setup(local);
            return local.ShowAsync(CancellationToken.None);
        }

        var dialog = new ContentDialog();
        setup(dialog);
        return contentDialogs.ShowAsync(dialog, CancellationToken.None);
    }
}

public static partial class ErrorText
{
    /// <summary>Bỏ phần "(Parameter 'x')" mà ArgumentException tự thêm vào thông báo tiếng Việt.</summary>
    public static string Clean(string message) => ParamSuffix().Replace(message, "");

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\(Parameter '[^']*'\)\s*$")]
    private static partial System.Text.RegularExpressions.Regex ParamSuffix();
}
