using System.Windows;
using System.Windows.Controls;
using SuperMOOO.Publishing.Library;

namespace SuperMOOO.App.Features.S14;

public partial class ComposeView : UserControl
{
    public ComposeView() => InitializeComponent();

    private void MediaDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void MediaDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not ComposeViewModel vm || vm.IsBusy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var images = paths.Where(MediaFiles.IsImage).ToArray();
        if (images.Length > 0) await vm.ImportImagesAsync(images);
        var videos = paths.Where(MediaFiles.IsVideo).ToArray();
        if (videos.Length > 0) await vm.ImportVideosAsync(videos);
        e.Handled = true;
    }
}
