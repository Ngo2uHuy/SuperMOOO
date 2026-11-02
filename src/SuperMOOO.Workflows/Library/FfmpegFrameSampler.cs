using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Publishing.Algorithms;

namespace SuperMOOO.Workflows.Library;

/// <summary>
/// Lấy khung xám 9x8 bằng ffmpeg để tính chữ ký gần trùng. Một lease media cho cả lần lấy mẫu (lane Media, nhường job render dài), mỗi khung
/// là lệnh Light để khỏi xin lease lồng nhau. Chỉ được gọi khi cần (đánh giá rủi ro), không quét nền.
/// </summary>
[Service(As = [typeof(IFrameSampler)])]
public sealed class FfmpegFrameSampler(FfmpegRunner ffmpeg, IRuntimeResources resources) : IFrameSampler
{
    public async Task<FrameSampleResult> SampleAsync(string videoPath, double durationSeconds, int frameCount, CancellationToken ct)
    {
        if (!File.Exists(videoPath)) return new(FrameSampleStatus.Failed, null, "Không tìm thấy file video.");
        if (durationSeconds <= 0 || frameCount <= 0) return new(FrameSampleStatus.Failed, null, "Thời lượng hoặc số khung không hợp lệ.");

        var temp = Path.Combine(Path.GetTempPath(), "aivs_sig_" + Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            using var lease = await resources.AcquireMediaAsync(ct);
            var hashes = new List<ulong>();
            foreach (var ratio in VideoSignature.FrameRatios(frameCount))
            {
                string[] args =
                [
                    "-ss", (durationSeconds * ratio).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", videoPath,
                    "-an", "-frames:v", "1", "-vf", $"scale={PerceptualHash.Width}:{PerceptualHash.Height}:flags=area", "-pix_fmt", "gray", "-f", "rawvideo"
                ];
                await ffmpeg.RunAsync(new FfmpegJob(args, temp, Label: "Chữ ký video", Light: true), null, ct);
                var frame = await File.ReadAllBytesAsync(temp, ct);
                if (frame.Length != PerceptualHash.FrameBytes) return new(FrameSampleStatus.Failed, null, "ffmpeg không trả đủ dữ liệu khung hình.");
                hashes.Add(PerceptualHash.DHash(frame));
            }
            return new(FrameSampleStatus.Ok, hashes, null);
        }
        catch (FfmpegException e) { return new(FrameSampleStatus.Failed, null, e.Message); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return new(FrameSampleStatus.Unavailable, null, e.Message); }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* file tạm, lần sau ghi đè */ }
        }
    }
}
