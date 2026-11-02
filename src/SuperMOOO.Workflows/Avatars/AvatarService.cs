using System.Text.RegularExpressions;
using SuperMOOO.AI.LipSync;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Steps;

namespace SuperMOOO.Workflows.Avatars;

public sealed class AvatarVideoOptions
{
    public string AvatarId { get; set; } = "";
    /// <summary>Lời thoại đọc bằng TTS; bỏ trống khi dùng <see cref="AudioPath"/>.</summary>
    public string Text { get; set; } = "";
    public string? AudioPath { get; set; }
    public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    public string Language { get; set; } = "vi";
    public double Speed { get; set; } = 1.0;
    public string? OutputFolder { get; set; }
    public string? Name { get; set; }
    /// <summary>Clip dựng cho S20: "product" hoặc "faq"; xong thì ghi clip_path của mục đó.</summary>
    public string? LiveKind { get; set; }
    public string? LiveId { get; set; }
    public string? LiveProductId { get; set; }
}

/// <summary>S19/S20: thư viện avatar (video ≤ 250 MB, ≤ 3 phút) và xếp job dò mặt / dựng video khớp môi. Lane AiWorker nên chỉ một job MuseTalk chạy mỗi lúc.</summary>
[Service]
public sealed partial class AvatarService(IAvatarStore store, IMediaProbe probe, FfmpegRunner ffmpeg, IJobQueue jobs, ILipSyncEngine engine,
    ProjectIndex projects, AppPaths paths)
{
    public const int MaxTextChars = 2500;

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidName();

    public Task<IReadOnlyList<Avatar>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    public async Task<Avatar> AddAsync(string sourcePath, string? name, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AvatarLimits.Extensions.Contains(ext)) throw new ArgumentException("Chỉ nhận video MP4, MOV, WebM hoặc MKV.");
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("Không tìm thấy video.", sourcePath);
        if (info.Length > AvatarLimits.MaxBytes) throw new ArgumentException("Video avatar tối đa 250 MB.");

        // Kiểm tra luồng thật bằng ffprobe, không tin phần mở rộng.
        var media = await probe.ProbeAsync(sourcePath, ct);
        if (media.Video is null) throw new ArgumentException("File không có luồng video hợp lệ.");
        if (media.Duration.TotalSeconds <= 0 || media.Duration.TotalSeconds > AvatarLimits.MaxSeconds) throw new ArgumentException("Video avatar dài tối đa 3 phút.");

        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(MuseTalkLocation.Avatars(paths), id);
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "source" + ext);
            File.Copy(sourcePath, source);
            var normalized = Path.Combine(dir, "avatar.mp4");
            // Video chuẩn hoá: không tiếng, ≤ 1280 px, 25 khung/giây, gần như không mất chất lượng (đầu vào của khớp môi).
            await ffmpeg.RunAsync(new FfmpegJob(["-i", source, "-an", "-vf",
                $"scale='min({MuseTalkPlanner.MaxSide},iw)':'min({MuseTalkPlanner.MaxSide},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,fps={MuseTalkPlanner.Fps}",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p", "-movflags", "+faststart"], normalized, media.Duration,
                Label: "Chuẩn hoá avatar"), null, ct);

            var title = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name.Trim();
            var avatar = new Avatar(id, title.Length > 100 ? title[..100] : title, source, normalized, AvatarFaceStatus.None, 0, media.Duration.TotalSeconds, DateTimeOffset.Now);
            await store.UpsertAsync(avatar, ct);
            return avatar;
        }
        catch
        {
            // Thư mục vừa tạo cho avatar này, chưa có bản ghi nào trỏ tới.
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Xoá bản ghi và thư mục thư viện của avatar (video gốc đã chép, video chuẩn hoá, toạ độ mặt đã dò). File gốc ngoài thư viện không bị đụng.</summary>
    public async Task DeleteAsync(Avatar avatar, CancellationToken ct = default)
    {
        await store.DeleteAsync(avatar.Id, ct);
        var dir = Path.GetFullPath(Path.Combine(MuseTalkLocation.Avatars(paths), avatar.Id));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public LipSyncEstimate Estimate(Avatar avatar, double audioSeconds) => engine.Estimate(avatar.NormalizedPath, audioSeconds);

    /// <summary>Dò mặt trước một lần cho lời thoại dài tới <paramref name="forSeconds"/> giây; kết quả cache cạnh avatar.</summary>
    public Task<string> PrepareFaceAsync(Avatar avatar, int forSeconds, CancellationToken ct = default)
    {
        var input = new JsonObject
        {
            ["avatarId"] = avatar.Id,
            ["avatarPath"] = avatar.NormalizedPath,
            ["stepOptions"] = new JsonObject { ["prepare"] = PipelineResult.Node(new LipSyncPrepareOptions { Seconds = forSeconds }) }
        };
        return jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Dò mặt: {avatar.Name}", PipelineResult.Pipeline("avatar_prepare", input), JobLane.AiWorker), ct);
    }

    public async Task<string> StartAsync(AvatarVideoOptions o, CancellationToken ct = default)
    {
        var avatar = await store.GetAsync(o.AvatarId, ct) ?? throw new ArgumentException("Chưa chọn avatar.");
        var useFile = !string.IsNullOrWhiteSpace(o.AudioPath);
        if (useFile && !File.Exists(o.AudioPath)) throw new FileNotFoundException("Không tìm thấy file audio.", o.AudioPath);
        if (!useFile)
        {
            if (string.IsNullOrWhiteSpace(o.Text)) throw new ArgumentException("Nhập lời thoại hoặc chọn file audio.");
            if (o.Text.Length > MaxTextChars) throw new ArgumentException($"Lời thoại tối đa {MaxTextChars} ký tự.");
            if (string.IsNullOrWhiteSpace(o.VoiceId)) throw new ArgumentException("Chưa chọn giọng đọc.");
        }

        if (o.Speed is < 0.5 or > 2.0) throw new ArgumentException("Tốc độ đọc từ 0.5 đến 2.0.");
        var status = engine.GetStatus();
        if (!status.Ready) throw new LipSyncUnavailableException(status.Message);

        var live = o.LiveKind is "product" or "faq" && !string.IsNullOrEmpty(o.LiveId);
        var folder = o.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), live ? Path.Combine("Avatar", "Live") : "Avatar");
        var stamp = DateTime.Now.ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var baseName = InvalidName().Replace(string.IsNullOrWhiteSpace(o.Name) ? avatar.Name : o.Name.Trim(), "_").Trim(' ', '.');
        var name = $"{(baseName.Length == 0 ? "avatar" : baseName)}_{stamp}";

        var input = new JsonObject
        {
            ["outputDir"] = folder,
            ["name"] = name,
            ["avatarPath"] = avatar.NormalizedPath,
            ["stepOptions"] = new JsonObject
            {
                ["audio"] = PipelineResult.Node(new AvatarAudioOptions
                {
                    Text = useFile ? "" : o.Text.Trim(), AudioPath = useFile ? o.AudioPath : null, VoiceId = o.VoiceId, Language = o.Language, Speed = o.Speed
                })
            }
        };
        if (live)
        {
            input["liveKind"] = o.LiveKind;
            input["liveId"] = o.LiveId;
            if (o.LiveProductId is { Length: > 0 }) input["liveProductId"] = o.LiveProductId;
        }

        return await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Avatar: {avatar.Name}", PipelineResult.Pipeline("avatar_video", input), JobLane.AiWorker), ct);
    }
}
