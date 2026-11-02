using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SuperMOOO.App.Controls;

namespace SuperMOOO.App.Features.S03;

/// <summary>Tab Talkshow của S03: khung xem trước sân khấu (PNG) và video kết quả (LibVLC).</summary>
public partial class TalkshowPanel : UserControl
{
    public TalkshowPanel()
    {
        InitializeComponent();
    }

    /// <summary>Gắn VM và khung phát video từ màn chủ (DI tạo MediaPreviewPane).</summary>
    public void Attach(TalkshowViewModel viewModel, MediaPreviewPane clip)
    {
        clip.EmptyText = "Video talkshow sẽ phát được ở đây khi dựng xong.";
        ClipHost.Content = clip;
        viewModel.ResultReady += (_, path) => clip.Open(path);
        viewModel.PropertyChanged += OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TalkshowViewModel.PreviewFramePath) || ((TalkshowViewModel)DataContext).PreviewFramePath is not { } path || !File.Exists(path)) return;
        // OnLoad để không giữ khoá file khung hình.
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        Frame.Source = image;
        FrameEmpty.Visibility = Visibility.Collapsed;
    }
}
