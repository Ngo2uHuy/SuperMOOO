using SuperMOOO.Core.Publishing;
using System.IO;
using System.Windows.Media;
using SuperMOOO.App.Controls;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.App.Features.S21;

/// <summary>
/// Thumbnail cho danh sách kho: dùng ảnh đi kèm video nếu có, không thì chụp một khung bằng ffmpeg (lệnh Light, không xếp sau job render) và cache
/// theo sha256. Chỉ chụp khi dòng thật sự hiện ra (xem <see cref="ContentRow.Thumbnail"/>), tối đa 2 lệnh một lúc.
/// </summary>
[Service]
public sealed class ContentThumbnails(AppPaths paths, FfmpegRunner ffmpeg, ThumbnailLoader loader)
{
    private readonly SemaphoreSlim _slots = new(2);

    public async Task<ImageSource?> LoadAsync(FinishedContent item, string? attachedImage)
    {
        var file = attachedImage is { Length: > 0 } && File.Exists(attachedImage) ? attachedImage : await CaptureAsync(item);
        return file is null ? null : await loader.LoadAsync(new Uri(file).AbsoluteUri);
    }

    private async Task<string?> CaptureAsync(FinishedContent item)
    {
        var cached = Path.Combine(paths.CacheDir("thumbs"), item.VideoSha256 + ".jpg");
        if (File.Exists(cached)) return cached;
        if (!File.Exists(item.OriginalPath)) return null;
        await _slots.WaitAsync();
        try
        {
            var at = item.DurationMs is > 0 ? Math.Min(item.DurationMs.Value / 10000.0, 10) : 1;
            string[] args = ["-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", item.OriginalPath, "-an", "-frames:v", "1",
                "-vf", "scale=-2:112", "-q:v", "5"];
            await ffmpeg.RunAsync(new FfmpegJob(args, cached, Label: "Thumbnail kho", Light: true), null, CancellationToken.None);
            return cached;
        }
        catch (Exception e) when (e is FfmpegException or IOException or InvalidOperationException) { return null; }
        finally { _slots.Release(); }
    }
}
