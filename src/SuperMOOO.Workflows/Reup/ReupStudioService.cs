using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Preview;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Auto;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Steps;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.Workflows.Reup;

public enum SubtitleMerge { Replace, Append }

/// <summary>Giọng đọc kịch bản sinh trong pipeline reup (bước tts + assemble_dub, cue tự sinh theo thời lượng thật).</summary>
public sealed record ReupVoiceover(string Script, string VoiceId, string? Language = null, double Speed = 1, double PitchSemitones = 0);

public sealed record ReupExportRequest(string Source, ReupEffectProfile Profile, string? OutputFolder = null, ReupVoiceover? Voiceover = null, DeliveryOptions? Delivery = null);

public sealed record VoiceoverResult(string AudioPath, TimeSpan Duration, IReadOnlyList<SubtitleEntry> Cues, IReadOnlyList<string> Warnings);

/// <summary>S06: preset, xem trước đúng filter graph xuất (D2), giọng đọc kịch bản, xuất đơn/hàng loạt qua pipeline `reup`.</summary>
[Service]
public sealed class ReupStudioService(ReupPresetCatalog presets, PreviewRenderer preview, TtsStep tts, AssembleDubStep assemble,
    IJobQueue jobs, ProjectIndex projects, AppPaths paths)
{
    public IReadOnlyList<ReupPreset> Presets => presets.All;

    /// <summary>Áp preset thay toàn bộ thông số hiệu ứng (F11), chỉ giữ tài nguyên người dùng đã chọn.</summary>
    public ReupEffectProfile ApplyPreset(string id, ReupEffectProfile current) => presets.Apply(id, current);

    public ReupEffectProfile DefaultProfile(string presetId = "tiktok") => presets.Get(presetId);

    public Task<ReupRenderResult> PreviewClipAsync(string source, ReupEffectProfile profile, TimeSpan start, double seconds,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.CacheDir("preview"));
        return preview.RenderClipAsync(source, profile, start, seconds, Path.Combine(paths.CacheDir("preview"), $"reup-{Guid.NewGuid():N}.mp4"), progress, ct);
    }

    /// <summary>Một khung hình đúng filter graph xuất, dùng khi kéo thanh tua.</summary>
    public async Task<string> PreviewFrameAsync(string source, ReupEffectProfile profile, TimeSpan at, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.CacheDir("frames"));
        var output = Path.Combine(paths.CacheDir("frames"), $"reup-{Guid.NewGuid():N}.jpg");
        await preview.RenderFrameAsync(source, profile, at, output, ct);
        return output;
    }

    /// <summary>R09: TTS từng câu, ghép nối tiếp, cue đúng thời lượng thật của từng câu (F09, retry mỗi câu theo F28).</summary>
    public async Task<VoiceoverResult> GenerateVoiceoverAsync(ReupVoiceover voice, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(voice.Script)) throw new ArgumentException("Nhập kịch bản cần đọc.");
        var work = Path.Combine(paths.CacheDir("voiceover"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        var state = new PipelineState();
        var ctx = new StepContext("voiceover", work, new JsonObject { ["script"] = voice.Script }, state, progress ?? new Progress<ProgressInfo>());
        async Task Run(IPipelineStep step, object options)
        {
            foreach (var (k, v) in (await step.ExecuteAsync(ctx, options, ct)).Values) state[k] = v;
        }

        await Run(tts, new TtsStepOptions { VoiceId = voice.VoiceId, Language = voice.Language, Speed = voice.Speed, PitchSemitones = voice.PitchSemitones });
        await Run(assemble, new AssembleDubOptions());

        return new VoiceoverResult(state["dubAudio"], TimeSpan.FromSeconds(double.Parse(state["dubSeconds"], CultureInfo.InvariantCulture)),
            await SubtitleEntries.ReadAsync(state["cuesFile"], ct), state.GetValueOrDefault("warnings")?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? []);
    }

    /// <summary>
    /// Gắn giọng đọc vào profile tại offset đã chốt lúc bấm (F10). Cue ở mốc thời gian ra; Replace thay phụ đề cũ, Append nối thêm.
    /// </summary>
    public static ReupEffectProfile ApplyVoiceover(ReupEffectProfile profile, VoiceoverResult voiceover, TimeSpan offset, SubtitleMerge merge)
    {
        var added = voiceover.Cues.Select(c => (Start: TimeSpan.FromSeconds(c.Start) + offset, End: TimeSpan.FromSeconds(c.End) + offset, c.Text));
        var cues = (merge == SubtitleMerge.Append && profile.Subtitles is { } existing ? existing.Document.Cues : [])
            .Concat(added.Select(a => new SubtitleCue(0, a.Start, a.End, a.Text)))
            .Select((c, i) => c with { Index = i + 1 }).ToList();
        return profile with
        {
            Audio = profile.Audio with { VoiceoverPath = voiceover.AudioPath, VoiceoverOffset = offset },
            Subtitles = new SubtitleBurn(new SubtitleDocument(cues), profile.Subtitles?.Style ?? new SubtitleStyle(PositionY: 82, Bold: true), "Output")
        };
    }

    public Task<string> StartExportAsync(ReupExportRequest request, CancellationToken ct = default)
    {
        var isUrl = SourceKinds.IsUrl(request.Source);
        if (!isUrl && !File.Exists(request.Source)) throw new FileNotFoundException("Không tìm thấy video nguồn.", request.Source);
        var name = SourceKinds.NameOf(request.Source);
        var folder = request.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), "Reup");
        // Hồ sơ đi vào job là bản sao: sửa tiếp trên UI không đổi job đã xếp (F22).
        var profile = PipelineResult.Node(request.Profile);
        var io = request.Profile.IntroOutro;
        var hasIntroOutro = io is not null && (!string.IsNullOrWhiteSpace(io.IntroPath) || !string.IsNullOrWhiteSpace(io.OutroPath));

        var steps = new JsonObject
        {
            ["render"] = PipelineResult.Node(new ReupRenderOptions { BurnCues = request.Voiceover is not null }),
            ["introOutro"] = PipelineResult.Node(new IntroOutroOptions { IntroPath = io?.IntroPath, OutroPath = io?.OutroPath }),
            ["separate"] = PipelineResult.Node(new SeparateVocalsOptions { Quality = request.Profile.Audio.SeparationQuality })
        };
        if (request.Voiceover is { } v)
            steps["tts"] = PipelineResult.Node(new TtsStepOptions { VoiceId = v.VoiceId, Language = v.Language, Speed = v.Speed, PitchSemitones = v.PitchSemitones });

        var input = new JsonObject
        {
            ["source"] = request.Source,
            ["outputDir"] = folder,
            ["name"] = name,
            ["profile"] = profile,
            ["voiceover"] = request.Voiceover is not null,
            ["introOutro"] = hasIntroOutro,
            ["separate"] = request.Profile.Audio is { SeparateVocals: true, MuteOriginal: false },
            ["stepOptions"] = steps
        };
        if (request.Voiceover is { } vo) input["script"] = vo.Script;
        SourceKinds.MarkDownload(input);
        PipelineResult.AttachDelivery(input, request.Delivery);

        return jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Reup: {name}", PipelineResult.Pipeline("reup", input), JobLane.Media), ct);
    }

    /// <summary>1 job mỗi file, chung hồ sơ.</summary>
    public async Task<IReadOnlyList<string>> StartBatchAsync(IReadOnlyList<string> sources, ReupEffectProfile profile, string? outputFolder, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("Chưa chọn video nào.");
        var ids = new List<string>();
        foreach (var source in sources) ids.Add(await StartExportAsync(new ReupExportRequest(source, profile, outputFolder), ct));
        return ids;
    }
}
