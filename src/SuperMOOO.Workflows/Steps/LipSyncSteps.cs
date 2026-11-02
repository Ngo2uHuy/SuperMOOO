using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using SuperMOOO.AI.LipSync;
using SuperMOOO.Workflows.TtsStudio;

namespace SuperMOOO.Workflows.Steps;

internal static partial class LipSyncProgress
{
    [GeneratedRegex(@"^MuseTalk (\d{1,3})%$")]
    private static partial Regex Percent();

    /// <summary>Engine báo tiến độ bằng chuỗi: "MuseTalk 45%" thành phần trăm, còn lại là thông điệp pha (fraction không xác định).</summary>
    public static IProgress<string> To(StepContext c) => new Progress<string>(m =>
        c.Progress.Report(Percent().Match(m) is { Success: true } p ? new ProgressInfo(int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0, m) : new ProgressInfo(-1, m)));
}

public sealed class AvatarAudioOptions
{
    public string Text { get; set; } = "";
    public string? AudioPath { get; set; }
    [Required] public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    public string Language { get; set; } = "vi";
    [Range(0.5, 2.0)] public double Speed { get; set; } = 1.0;
}

/// <summary>Lời thoại cho avatar: đọc bằng TTS của app (có cache) hoặc dùng file audio người dùng đưa.</summary>
public sealed class AvatarAudioStep(VoiceSpeed voice, IMediaProbe probe) : PipelineStep<AvatarAudioOptions>
{
    public override string Kind => "avatar_audio";

    protected override async Task<StepOutput> RunAsync(StepContext c, AvatarAudioOptions o, CancellationToken ct)
    {
        string audio;
        if (!string.IsNullOrWhiteSpace(o.AudioPath))
        {
            audio = o.AudioPath;
            if (!File.Exists(audio)) throw new FileNotFoundException("Không tìm thấy file audio.", audio);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(o.Text)) throw new InvalidOperationException("Chưa có lời thoại.");
            audio = await voice.SynthesizeAsync(new TtsRequest(o.Text, o.VoiceId, Io.Work(c, "speech.wav"), o.Language), o.Speed, ct);
        }

        var info = await probe.ProbeAsync(audio, ct);
        if (info.Audio is null || info.Duration <= TimeSpan.Zero) throw new MediaProbeException("File không có âm thanh hợp lệ.");
        return Io.Out([("audio", audio), ("audioSeconds", Io.Fmt(info.Duration.TotalSeconds))]);
    }
}

public sealed class LipSyncOptions;

/// <summary>Khớp môi bằng MuseTalk (CPU, tiến trình riêng). Lỗi hết thời gian/thiếu runtime báo rõ; không tự thử lại vì mỗi lần rất lâu.</summary>
public sealed class LipSyncStep(ILipSyncEngine engine) : PipelineStep<LipSyncOptions>
{
    public override string Kind => "lipsync";

    protected override async Task<StepOutput> RunAsync(StepContext c, LipSyncOptions o, CancellationToken ct)
    {
        var output = Io.Work(c, "lipsync.mp4");
        var result = await engine.RenderAsync(new LipSyncRequest(Io.Require(c, "avatarPath"), Io.State(c, "audio"), output), LipSyncProgress.To(c), ct);
        return Io.Out([("lipsyncFile", result.Path), ("lipsyncSeconds", Io.Fmt(result.Duration.TotalSeconds))]);
    }
}

public sealed class LipSyncFinalizeOptions;

/// <summary>Chép video khớp môi ra thư mục kết quả, đăng ký loại "lipsync" cho S12 và (clip dựng cho S20) ghi clip_path của sản phẩm/FAQ.</summary>
public sealed class LipSyncFinalizeStep(IOutputRegistry outputs, ILiveStore live) : PipelineStep<LipSyncFinalizeOptions>
{
    public override string Kind => "lipsync_finalize";

    protected override async Task<StepOutput> RunAsync(StepContext c, LipSyncFinalizeOptions o, CancellationToken ct)
    {
        var folder = Io.OutputDir(c);
        var final = Io.OverwritablePath(folder, Io.Name(c) + ".mp4");
        File.Copy(Io.State(c, "lipsyncFile"), final, overwrite: true);
        await outputs.RegisterOutputAsync(final, "lipsync", ct);
        await AttachToLiveAsync(c, final, ct);
        return Io.Out([("video", final), ("finalPath", final), ("outputKind", "lipsync")], final);
    }

    private async Task AttachToLiveAsync(StepContext c, string clip, CancellationToken ct)
    {
        var kind = Io.Str(c, "liveKind");
        var id = Io.Str(c, "liveId");
        if (kind is null || id is null) return;

        if (kind == "product")
        {
            if ((await live.ListProductsAsync(ct)).FirstOrDefault(p => p.Id == id) is { } product)
                await live.UpsertProductAsync(product with { ClipPath = clip }, ct);
        }
        else if (Io.Str(c, "liveProductId") is { } productId && (await live.ListFaqsAsync(productId, ct)).FirstOrDefault(f => f.Id == id) is { } faq)
            await live.UpsertFaqAsync(faq with { ClipPath = clip }, ct);
    }
}

public sealed class LipSyncPrepareOptions
{
    [Range(1, 180)] public int Seconds { get; set; } = 10;
}

/// <summary>Dò mặt avatar một lần (chậm nhất của MuseTalk); lần dựng sau đọc toạ độ đã lưu cạnh avatar.</summary>
public sealed class LipSyncPrepareStep(ILipSyncEngine engine, IAvatarStore avatars) : PipelineStep<LipSyncPrepareOptions>
{
    public override string Kind => "lipsync_prepare";

    protected override async Task<StepOutput> RunAsync(StepContext c, LipSyncPrepareOptions o, CancellationToken ct)
    {
        var id = Io.Require(c, "avatarId");
        try
        {
            await engine.PrepareAvatarAsync(Io.Require(c, "avatarPath"), o.Seconds, LipSyncProgress.To(c), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await avatars.SetFaceStatusAsync(id, AvatarFaceStatus.Failed, 0, CancellationToken.None);
            throw;
        }

        await avatars.SetFaceStatusAsync(id, AvatarFaceStatus.Ready, o.Seconds, ct);
        return Io.Out([("faceSeconds", o.Seconds.ToString(CultureInfo.InvariantCulture))]);
    }
}
