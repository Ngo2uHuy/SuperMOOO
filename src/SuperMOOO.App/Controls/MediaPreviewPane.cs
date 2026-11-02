using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using SuperMOOO.App.Shell;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SuperMOOO.App.Controls;

/// <summary>Xem trước video/âm thanh bằng LibVLC với thanh điều khiển bo góc: phát/tạm dừng, tua, âm lượng, mốc thời gian.</summary>
[Service(ServiceLifetime.Transient)]
public sealed class MediaPreviewPane : UserControl, IDisposable
{
    private readonly LibVlcHost _host;
    private readonly VideoView _view = new();
    private readonly Grid _stage = new();
    private readonly EmptyState _empty = new() { Icon = SymbolRegular.Video24, Text = "Chưa có video để xem trước. Kết quả sẽ hiện ở đây khi chạy xong." };
    private readonly Button _toggle = new() { Icon = new SymbolIcon(SymbolRegular.Play24), ToolTip = "Phát", IsEnabled = false };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = false, ToolTip = "Vị trí phát" };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 80, Width = 96, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Âm lượng" };
    private readonly TextBlock _time = new() { Text = "00:00 / 00:00", MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    private readonly Button _expand = new() { Icon = new SymbolIcon(SymbolRegular.FullScreenMaximize24), ToolTip = "Phóng to (Esc để đóng)", IsEnabled = false };
    private MediaPlayer? _player;
    private bool _updatingSeek;
    private Action<MediaPreviewPane>? _reopen;
    private long _startAt;
    private long _resumeAt;
    private float _resumePosition;

    public MediaPreviewPane(LibVlcHost host)
    {
        _host = host;
        _toggle.SetResourceReference(StyleProperty, "IconButton");
        _toggle.Margin = new Thickness(0);
        _expand.SetResourceReference(StyleProperty, "IconButton");
        _expand.Margin = new Thickness(8, 0, 0, 0);
        _time.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var speaker = new SymbolIcon(SymbolRegular.Speaker224) { Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var bar = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_toggle, Dock.Left);
        DockPanel.SetDock(_expand, Dock.Right);
        DockPanel.SetDock(_volume, Dock.Right);
        DockPanel.SetDock(speaker, Dock.Right);
        DockPanel.SetDock(_time, Dock.Right);
        bar.Children.Add(_toggle);
        bar.Children.Add(_expand);
        bar.Children.Add(_volume);
        bar.Children.Add(speaker);
        bar.Children.Add(_time);
        System.Windows.Automation.AutomationProperties.SetName(_seek, "Vị trí phát");
        bar.Children.Add(_seek);

        var transport = Rounded(bar, "CardBackgroundFillColorDefaultBrush");
        transport.Padding = new Thickness(8, 6, 12, 6);
        transport.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(transport, Dock.Bottom);

        _stage.Children.Add(_empty);
        var stage = Rounded(_stage, "SolidBackgroundFillColorTertiaryBrush");
        stage.ClipToBounds = true;
        Content = new DockPanel { Children = { transport, stage } };

        _toggle.Click += (_, _) => TogglePlay();
        _expand.Click += (_, _) => Expand();
        _seek.ValueChanged += (_, e) =>
        {
            if (!_updatingSeek && _player is { IsSeekable: true }) _player.Position = (float)e.NewValue;
        };
        _volume.ValueChanged += (_, e) =>
        {
            if (_player is not null) _player.Volume = (int)e.NewValue;
        };
        Unloaded += (_, _) => Release();
        Loaded += (_, _) => Restore();
    }

    /// <summary>View được giữ cache nên Unloaded chỉ là chuyển màn: dừng và nhả media (giữ MediaPlayer để dùng lại), nhớ mốc đang xem.</summary>
    private void Release()
    {
        if (_player is not { Media: not null } player) return;
        _resumeAt = Math.Max(0, player.Time);
        _resumePosition = Math.Max(0, player.Position);
        player.Stop();
        player.Media = null;
    }

    /// <summary>Quay lại màn: nạp lại media đã nhả (chưa phát), mốc cũ áp khi bấm phát hoặc khi được tua.</summary>
    private void Restore()
    {
        if (_reopen is null || _player?.Media is not null) return;
        var at = _resumeAt;
        _reopen(this);
        _startAt = at;
        _updatingSeek = true;
        _seek.Value = _resumePosition;
        _updatingSeek = false;
    }

    /// <summary>Hiện nút phóng to; tắt ở chính cửa sổ phóng to.</summary>
    public bool AllowExpand
    {
        get => _expand.Visibility == Visibility.Visible;
        set => _expand.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public event EventHandler<double>? TimeChanged;
    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds) || _player is null) return;
        var ms = (long)(Math.Max(0, seconds) * 1000);
        if (_player.IsSeekable) _player.Time = ms;
        else _startAt = ms;
    }
    public void Pause() => _player?.Pause();
    public void Clear()
    {
        Dispose();
        _stage.Children.Remove(_view);
        if (!_stage.Children.Contains(_empty)) _stage.Children.Add(_empty);
        _reopen = null; _startAt = 0;
        _seek.Value = 0;
        _toggle.IsEnabled = _seek.IsEnabled = _expand.IsEnabled = false;
        _time.Text = "00:00 / 00:00";
    }

    /// <summary>Câu hướng dẫn khi chưa có media.</summary>
    public string EmptyText
    {
        get => _empty.Text ?? "";
        set => _empty.Text = value;
    }

    /// <summary>Nạp file cục bộ hoặc URL; mặc định chưa phát để người dùng chủ động.</summary>
    public void Open(string pathOrUrl, bool autoPlay = false)
    {
        EnsurePlayer();
        var type = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? FromType.FromLocation : FromType.FromPath;
        using var media = new LibVLCSharp.Shared.Media(_host.Instance, pathOrUrl, type);
        _reopen = pane => pane.Open(pathOrUrl);
        Load(media, autoPlay);
    }

    /// <summary>Phát luồng mạng: tiếng tách riêng (YouTube) gắn làm slave để LibVLC đồng bộ với hình; header gửi qua tuỳ chọn http của LibVLC.</summary>
    public void OpenStream(string videoUrl, string? audioUrl, IReadOnlyDictionary<string, string> headers, bool autoPlay = false)
    {
        EnsurePlayer();
        using var media = new LibVLCSharp.Shared.Media(_host.Instance, videoUrl, FromType.FromLocation);
        if (headers.TryGetValue("User-Agent", out var agent)) media.AddOption(":http-user-agent=" + agent);
        if (headers.TryGetValue("Referer", out var referrer)) media.AddOption(":http-referrer=" + referrer);
        if (audioUrl is not null) media.AddSlave(MediaSlaveType.Audio, 4, audioUrl);
        _reopen = pane => pane.OpenStream(videoUrl, audioUrl, headers);
        Load(media, autoPlay);
    }

    public void Dispose()
    {
        var player = Interlocked.Exchange(ref _player, null);
        if (player is null) return;
        _view.MediaPlayer = null;
        player.Stop();
        player.Dispose();
    }

    private static Border Rounded(UIElement child, string background)
    {
        var border = new Border { Child = child, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.CornerRadiusProperty, "Radius");
        border.SetResourceReference(Border.BackgroundProperty, background);
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    private void Load(LibVLCSharp.Shared.Media media, bool autoPlay)
    {
        _player!.Media = media;
        _seek.Value = 0;
        _toggle.IsEnabled = _seek.IsEnabled = _expand.IsEnabled = true;
        if (autoPlay) _player.Play();
    }

    private void TogglePlay()
    {
        if (_player?.Media is null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    /// <summary>
    /// Mở cùng media trong cửa sổ lớn (phóng to toàn màn hình) và phát tiếp từ mốc đang xem; đóng thì quay lại khung nhỏ ở mốc mới.
    /// Dùng player riêng vì VideoView của LibVLC gắn với cửa sổ chủ, không chuyển sang cửa sổ khác được.
    /// </summary>
    private void Expand()
    {
        if (_reopen is null || _player is null) return;
        var resume = _player.Time;
        _player.Pause();

        var pane = new MediaPreviewPane(_host) { AllowExpand = false, Margin = new Thickness(16, 0, 16, 16) };
        var window = new FluentWindow
        {
            Title = "Xem video", Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 1280, Height = 800, WindowState = WindowState.Maximized, ShowInTaskbar = false,
            ExtendsContentIntoTitleBar = true, WindowBackdropType = ThemeService.Backdrop
        };
        var titleBar = new TitleBar { Title = window.Title, ShowMinimize = false };
        Grid.SetRow(pane, 1);
        window.Content = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, Children = { titleBar, pane } };
        DialogKeys.CloseOnEscape(window);
        window.Loaded += (_, _) =>
        {
            _reopen(pane);
            pane._startAt = resume;
            pane._player?.Play();
        };
        window.Closed += (_, _) =>
        {
            var at = pane._player is { Time: > 0 } p ? p.Time : pane._resumeAt;
            pane.Dispose();
            if (_player is { IsSeekable: true } && at > 0) _player.Time = at;
        };
        window.ShowDialog();
    }

    private void ShowPlaying(bool playing)
    {
        _toggle.Icon = new SymbolIcon(playing ? SymbolRegular.Pause24 : SymbolRegular.Play24);
        _toggle.ToolTip = playing ? "Tạm dừng" : "Phát";
    }

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        // VideoView dựng cửa sổ phủ khi Loaded nên chỉ đưa vào cây giao diện khi thật sự có media để phát.
        _stage.Children.Remove(_empty);
        if (!_stage.Children.Contains(_view)) _stage.Children.Add(_view);
        _player =new MediaPlayer(_host.Instance) { Volume = (int)_volume.Value };
        _view.MediaPlayer = _player;
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ShowPlaying(true);
            // Không gọi MediaPlayer trong callback của LibVLC (dễ treo) nên tua tới mốc tiếp nối ở UI thread.
            if (_startAt > 0 && _player is not null) _player.Time = _startAt;
            _startAt = 0;
        });
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(() => ShowPlaying(false));
        _player.Stopped += (_, _) => Dispatcher.BeginInvoke(() => ShowPlaying(false));
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => ShowPlaying(false));
        _player.PositionChanged += (_, e) => Dispatcher.BeginInvoke(() => ShowPosition(e.Position));
    }

    private void ShowPosition(float position)
    {
        if (_player is null) return;
        _updatingSeek = true;
        _seek.Value = position;
        _updatingSeek = false;
        _time.Text = $"{Format(_player.Time)} / {Format(_player.Length)}";
        TimeChanged?.Invoke(this, Math.Max(0, _player.Time) / 1000d);
    }

    private static string Format(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(ms >= 3_600_000 ? @"h\:mm\:ss" : @"mm\:ss");
}
