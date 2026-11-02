using System.Buffers;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Whiteboard;

/// <summary>Giải mã ảnh bằng ffmpeg ra rawvideo BGRA (không cần thư viện ảnh). Buffer thuê từ ArrayPool, người gọi trả lại.</summary>
internal static class RawImageDecoder
{
    private static async Task<byte[]> RunAsync(FfmpegRunner ffmpeg, IReadOnlyList<string> args, int byteCount, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "aivs_wb_" + Guid.NewGuid().ToString("N") + ".bgra");
        try
        {
            await ffmpeg.RunAsync(new FfmpegJob([.. args, "-frames:v", "1", "-f", "rawvideo"], temp, Label: "Giải mã ảnh", Light: true), null, ct);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                using var file = File.OpenHandle(temp);
                var read = 0;
                while (read < byteCount)
                {
                    var n = RandomAccess.Read(file, buffer.AsSpan(read, byteCount - read), read);
                    if (n == 0) break;
                    read += n;
                }

                if (read < byteCount) throw new InvalidDataException("Ảnh giải mã thiếu dữ liệu.");
                return buffer;
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }
        finally { try { File.Delete(temp); } catch (IOException) { /* thư mục tạm, dọn sau */ } }
    }

    /// <summary>Ảnh vừa khung (giữ tỉ lệ) giữa nền trắng, W x H. Ảnh trong suốt được trải trên nền trắng.</summary>
    public static Task<byte[]> FitOnWhiteAsync(FfmpegRunner ffmpeg, string path, int width, int height, CancellationToken ct) =>
        RunAsync(ffmpeg, ["-f", "lavfi", "-i", $"color=c=white:s={width}x{height}:d=1", "-i", path, "-filter_complex",
            $"[1:v]scale={width}:{height}:force_original_aspect_ratio=decrease[fg];[0:v][fg]overlay=(W-w)/2:(H-h)/2:format=auto,format=bgra"],
            width * height * 4, ct);

    /// <summary>Ảnh phủ kín khung (cắt giữa), W x H, trên nền trắng.</summary>
    public static Task<byte[]> CoverAsync(FfmpegRunner ffmpeg, string path, int width, int height, CancellationToken ct) =>
        RunAsync(ffmpeg, ["-f", "lavfi", "-i", $"color=c=white:s={width}x{height}:d=1", "-i", path, "-filter_complex",
            $"[1:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}[fg];[0:v][fg]overlay=0:0:format=auto,format=bgra"],
            width * height * 4, ct);

    /// <summary>Giữ alpha (không trải nền), đổi cỡ về W x H.</summary>
    public static Task<byte[]> ScaleKeepAlphaAsync(FfmpegRunner ffmpeg, string path, int width, int height, CancellationToken ct) =>
        RunAsync(ffmpeg, ["-i", path, "-vf", $"scale={width}:{height}:flags=lanczos,format=bgra"], width * height * 4, ct);
}
