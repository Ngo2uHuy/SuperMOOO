using SuperMOOO.AI.Tts;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Workflows.TtsStudio;

/// <summary>
/// Đọc đúng tốc độ người dùng chọn ở mọi nút nghe thử: provider có Rate thì đọc thẳng; giọng native và câu mẫu
/// thì chỉnh bằng atempo, giống bước tts khi chạy thật (VoiceSteps) để nghe thử khớp kết quả.
/// </summary>
[Service]
public sealed class VoiceSpeed(TtsService tts, FfmpegRunner ffmpeg)
{
    public async Task<string> SynthesizeAsync(TtsRequest request, double speed, CancellationToken ct)
    {
        var rate = tts.SupportsRate(request.VoiceId) ? speed : 1.0;
        var path = (await tts.SynthesizeAsync(request with { Rate = rate }, ct)).Path;
        return Math.Abs(rate - speed) < 0.001 ? path : await ApplyAsync(ffmpeg, path, speed, ct);
    }

    /// <summary>Câu mẫu của giọng (TtsService.PreviewAsync luôn đọc 1.0x) ở tốc độ đã chọn.</summary>
    public async Task<string> PreviewAsync(string voiceId, string? language, double speed, CancellationToken ct) =>
        await ApplyAsync(ffmpeg, (await tts.PreviewAsync(voiceId, language, ct)).Path, speed, ct);

    public static async Task<string> ApplyAsync(FfmpegRunner ffmpeg, string path, double speed, CancellationToken ct)
    {
        if (Math.Abs(speed - 1) < 0.001) return path;
        var output = Path.ChangeExtension(path, ".speed.wav");
        await ffmpeg.RunAsync(new FfmpegJob(["-i", path, "-af", Fmt.TempoChain(speed), "-c:a", "pcm_s16le"], output, Label: "Tốc độ đọc"), null, ct);
        return output;
    }
}
