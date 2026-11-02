using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Social.Download;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>Xem trước một video từ bảng kết quả trước khi tải: yt-dlp lấy luồng trực tiếp, LibVLC phát ngay trong app.</summary>
[Service(ServiceLifetime.Transient)]
public sealed class VideoPreviewWindow : FluentWindow
{
    private readonly YtDlpDownloader _downloader;
    private readonly ShellService _shell;
    private readonly IDialogService _dialogs;
    // Tự tạo thay vì lấy từ DI: transient IDisposable do container gốc giữ tới khi tắt app, mỗi lần xem trước sẽ rò một player.
    private readonly MediaPreviewPane _player;
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBlock _heading = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button _download = new() { Content = "Đưa vào tải", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.ArrowDownload24), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _browser = new() { Content = "Mở trên trình duyệt", Icon = new SymbolIcon(SymbolRegular.Globe24), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _close = new() { Content = "Đóng" };
    private string _url = "";
    private string? _cached;

    public VideoPreviewWindow(LibVlcHost host, YtDlpDownloader downloader, ShellService shell, IDialogService dialogs)
    {
        _downloader = downloader;
        _shell = shell;
        _dialogs = dialogs;
        _player = new MediaPreviewPane(host) { EmptyText = "Đang lấy luồng video…" };

        Title = "Xem trước video";
        Width = 760;
        Height = 620;
        MinWidth = 480;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;
        WindowBackdropType = ThemeService.Backdrop;
        DialogKeys.CloseOnEscape(this);

        var titleBar = new TitleBar { Title = Title, ShowMinimize = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(_download);
        actions.Children.Add(_browser);
        actions.Children.Add(_close);
        DockPanel.SetDock(_heading, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        var body = new DockPanel { Margin = new Thickness(24, 8, 24, 24), Children = { _heading, actions, _player } };
        Grid.SetRow(body, 1);
        var dialogHost = new ContentDialogHost();
        Grid.SetRowSpan(dialogHost, 2);
        Content = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() },
            Children = { titleBar, body, dialogHost }
        };

        _download.Click += async (_, _) => await SendAsync();
        _browser.Click += (_, _) => OpenInBrowser();
        _close.Click += (_, _) => Close();
        Loaded += async (_, _) => await LoadStreamAsync();
        Closed += (_, _) => Release();
    }

    /// <summary>"Đưa vào tải" gọi lại lưới để job mang đúng owner/tiêu đề như nút cùng tên trên lưới; true khi đã xếp job.</summary>
    public Func<Task<bool>>? SendToDownload { get; set; }

    /// <summary>Mở modal: mỗi lần chỉ một player chạy, đóng là dừng phát và giải phóng.</summary>
    public void Preview(Window? owner, string url, string title)
    {
        _url = url;
        _heading.Text = title;
        _heading.ToolTip = title;
        _download.Visibility = SendToDownload is null ? Visibility.Collapsed : Visibility.Visible;
        Owner = owner;
        ShowDialog();
    }

    private async Task SendAsync()
    {
        if (SendToDownload is null) return;
        _download.IsEnabled = false;
        if (await SendToDownload()) _download.Content = "Đã đưa vào tải";
        else _download.IsEnabled = true;
    }

    private async Task LoadStreamAsync()
    {
        try
        {
            var stream = await _downloader.ResolveStreamAsync(_url, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            if (stream.NeedsCookie)
            {
                _player.EmptyText = "Đang tải bản xem trước…";
                var progress = new Progress<ProgressInfo>(p => _player.EmptyText = $"Đang tải bản xem trước… {p.Fraction:P0}");
                _cached = await _downloader.CacheStreamAsync(stream, progress, _cts.Token);
                // Tải xong đúng lúc cửa sổ vừa đóng: Release đã chạy nên tự dọn file.
                if (_cts.IsCancellationRequested) DeleteCached();
                else _player.Open(_cached, autoPlay: true);
            }
            else
            {
                _player.OpenStream(stream.VideoUrl, stream.AudioUrl, stream.Headers, autoPlay: true);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Cửa sổ đã đóng giữa chừng.
        }
        catch (Exception ex) when (ex is DownloadException or ArgumentException or FileNotFoundException or IOException or HttpRequestException)
        {
            if (_cts.IsCancellationRequested) return;     // yt-dlp bị kill khi đóng cửa sổ cũng thoát mã lỗi, không phải lỗi thật
            _player.EmptyText = "Không phát được video này. Thử mở trên trình duyệt.";
            _dialogs.Error(ex.Message);
        }
    }

    private void OpenInBrowser()
    {
        try
        {
            _shell.OpenUrl(_url);
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
        {
            _dialogs.Error(ex.Message);
        }
    }

    // Không Dispose _cts: LoadStreamAsync có thể vẫn đang chờ và còn đọc token sau khi cửa sổ đóng.
    private void Release()
    {
        _cts.Cancel();
        _player.Dispose();
        DeleteCached();
    }

    private void DeleteCached()
    {
        if (_cached is null) return;
        try
        {
            File.Delete(_cached);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nằm trong cache, janitor sẽ dọn */ }
    }
}
