using SuperMOOO.AI.Image;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Workflows.LiveGame;

[Service(As = [typeof(IJobHandler)])]
public sealed class LiveGameThemeJob(ImageProviderRegistry images, FfmpegRunner ffmpeg, AppPaths paths) : IJobHandler
{
    public const string JobType = "livegame.theme";
    public string Type => JobType;

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var provider = images.Get(context.Payload["provider"]?.GetValue<string>() ?? throw new ArgumentException("Chưa chọn dịch vụ ảnh."));
        var dir = Path.Combine(paths.DataRoot, "themes", "temple", context.JobId);
        Directory.CreateDirectory(dir);
        var modes = new[] { ("morning", "soft morning sunlight"), ("afternoon", "warm late afternoon sunlight"), ("night", "quiet night with glowing lanterns and candles") };
        for (var i = 0; i < modes.Length; i++)
        {
            var (mode, light) = modes[i];
            context.Progress.Report(new ProgressInfo(i / 3.0, $"Tạo nền {i + 1}/3"));
            var image = await provider.GenerateAsync(new ImageRequest(
                $"Vertical Vietnamese Buddhist temple courtyard at {light}, tranquil hand painted cinematic illustration, symmetrical front view, distant golden Buddha altar centered at upper third, stone courtyard empty in lower half for game characters, no people, no text, no watermark, muted jade and antique gold palette",
                1080, 1920, Seed: 24001), ct);
            File.Copy(image.Path, Path.Combine(dir, mode + Path.GetExtension(image.Path)), false);
            // Zoom tiến rồi lùi để nối vòng không nhảy hình; runner tự xin media lease.
            await ffmpeg.RunAsync(new FfmpegJob(["-loop", "1", "-i", image.Path, "-an", "-vf",
                "scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,zoompan=z='1.015-0.015*cos(2*PI*on/600)':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s=1080x1920:fps=30",
                "-t", "20", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart"],
                Path.Combine(dir, mode + ".mp4"), TimeSpan.FromSeconds(20), Label: $"Dựng nền {i + 1}/3"), null, ct);
        }
        // Chỉ công bố gói đã hoàn tất cả ba nền; gói cũ vẫn còn để quay lại.
        var manifest = Path.Combine(paths.DataRoot, "themes", "temple", "active.json");
        var temp = Path.Combine(dir, "active.json");
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { folder = context.JobId }), ct);
        File.Move(temp, manifest, true);
        context.Progress.Report(new ProgressInfo(1, "Đã tạo ba nền. Bắt đầu lại phiên để dùng gói mới."));
        return new JsonObject { ["folder"] = dir };
    }
}
