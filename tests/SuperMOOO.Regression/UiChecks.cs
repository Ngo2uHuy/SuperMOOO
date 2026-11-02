using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SuperMOOO.App;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S17;
using SuperMOOO.Core;
using SuperMOOO.Core.Models;
using SuperMOOO.Infrastructure.Processes;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Render;
using Wpf.Ui.Markup;

namespace SuperMOOO.Regression;

internal static class UiChecks
{
    public static void Run(string repo)
    {
        var output = Path.Combine(repo, "artifacts", "timeline-ui");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = SuperMOOO.App.App.Pack("Themes/Tokens.xaml") });
        var root = new UserControl();
        NameScope.SetNameScope(root, new NameScope());
        var host = new Border { Background = Brushes.Black };
        root.Content = host;
        var preview = new Grid();
        var frame = new Image { Name = "Frame", Stretch = Stretch.Uniform };
        var image = new DrawingImage(new GeometryDrawing(Brushes.DarkCyan, null, new RectangleGeometry(new Rect(0, 0, 200, 400))));
        frame.Source = image;
        root.RegisterName("Frame", frame);
        var overlay = new RegionOverlay();
        overlay.SetBinding(RegionOverlay.SourceProperty, new Binding("Source") { ElementName = "Frame" });
        preview.Children.Add(frame); preview.Children.Add(overlay); host.Child = preview;
        var window = new Window { Content = root, Width = 600, Height = 500, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var expanded = app.Windows.OfType<Window>().FirstOrDefault(w => w != window);
            try
            {
                if (expanded is null || host.Child is not null || overlay.Source != image) throw new Exception("Expanded frame lost its overlay binding.");
                Capture(expanded, Path.Combine(output, "preview-expanded.png"));
            }
            catch (Exception ex) { failure = ex; }
            finally { expanded?.Close(); }
        };
        timer.Start(); PreviewExpansion.Show(host, "Preview test");
        window.UpdateLayout();
        if (failure is not null) throw failure;
        if (host.Child != preview || overlay.Source != image) throw new Exception("Preview was not restored after closing expansion.");
        Console.WriteLine("PASS: preview expansion preserves overlay binding and restores original frame");

        var runner = new ProcessRunner();
        var tools = new TestTools(Path.Combine(repo, "tools"));
        var probe = new MediaProbe(runner, tools);
        var ffmpeg = new FfmpegRunner(runner, tools);
        var paths = AppPaths.Create(Path.Combine(output, "test-data"));
        var vm = new TimelineViewModel(probe, new TimelineRenderer(new ProjectRenderer(ffmpeg, probe), ffmpeg, probe, paths), paths,
            new JobTracker(null!), new TestDialogs());
        var v1 = TimelineClip.Media(TimelineTrack.Video, "C:/media/cảnh 1.mp4", 8);
        var v2 = TimelineClip.Media(TimelineTrack.Video, "C:/media/cảnh 2.mp4", 6);
        vm.ReplaceProjectAsync(TimelineEditing.Normalize(new TimelineProject(1, "Video mẫu", [v1, v2,
            TimelineClip.Media(TimelineTrack.Voiceover, "C:/media/lời bình.wav", 4, 2),
            TimelineClip.Media(TimelineTrack.Music, "C:/media/nhạc nền.wav", 10, 0),
            new TimelineClip(Guid.NewGuid(), TimelineTrack.Subtitle, "", 3, 0, 4, 0, "Xin chào")]))).GetAwaiter().GetResult();
        vm.SelectedClip = vm.Clips[0]; vm.Playhead = 4;
        using var vlc = new LibVlcHost();
        using var media = new MediaPreviewPane(vlc);
        var view = new TimelineView(vm, media);
        window.Content = view; window.Width = 1280; window.Height = 900; window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Capture(window, Path.Combine(output, "timeline-populated.png"));
        window.Close(); app.Shutdown();
        Console.WriteLine("PASS: populated timeline view rendered");
    }

    private static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)content.ActualWidth), Math.Max(1, (int)content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
}
