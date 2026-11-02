using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Tts;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Steps;

namespace SuperMOOO.Workflows.TtsStudio;

public sealed record TtsStudioRequest(string Text, string VoiceId, string? Language = null, double Speed = 1, double PitchSemitones = 0,
    string? EffectId = null, double EffectPitchSemitones = 0, string Format = "wav", string? OutputFolder = null, string? Name = null);

/// <param name="DenoiseMode">Fast (afftdn) hoặc Ai (ONNX qua ai_worker).</param>
public sealed record AudioProcessRequest(string Source, bool Denoise = false, string DenoiseMode = "Ai", bool DetectSpeech = false,
    bool TrimSilence = false, string? OutputFolder = null);

/// <summary>S05: tạo giọng (đơn lẻ/hàng loạt kịch bản) và xử lý audio qua pipeline `voice`/`audio`; nghe thử và danh sách giọng gọi thẳng dịch vụ AI.</summary>
[Service]
public sealed class TtsStudioService(IJobQueue jobs, ProjectIndex projects, VoiceCatalogService voices, TtsService tts)
{
    public Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language = null, string? providerId = null, CancellationToken ct = default) =>
        voices.ListAsync(language, providerId, ct);

    public Task<TtsResult> PreviewAsync(string voiceId, string? language, CancellationToken ct = default) =>
        tts.PreviewAsync(voiceId, language, ct);

    public static string SampleText(string? language) => LanguageCatalog.SampleText(language);

    public Task<string> StartAsync(TtsStudioRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Nhập văn bản cần đọc.");
        if (string.IsNullOrWhiteSpace(request.VoiceId)) throw new ArgumentException("Chọn giọng đọc.");
        var name = request.Name ?? $"tts_studio_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}";
        var folder = request.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), "TtsStudio");
        var effect = !string.IsNullOrWhiteSpace(request.EffectId) && !request.EffectId.Equals("none", StringComparison.OrdinalIgnoreCase)
            || Math.Abs(request.EffectPitchSemitones) > 0.001;

        var input = new JsonObject
        {
            ["script"] = request.Text,
            ["outputDir"] = folder,
            ["name"] = name,
            ["voiceEffect"] = effect,
            ["stepOptions"] = new JsonObject
            {
                ["tts"] = PipelineResult.Node(new TtsStepOptions
                {
                    VoiceId = request.VoiceId, Language = request.Language, Speed = request.Speed, PitchSemitones = request.PitchSemitones
                }),
                ["effect"] = PipelineResult.Node(new VoiceEffectOptions { EffectId = request.EffectId, PitchSemitones = request.EffectPitchSemitones }),
                ["export"] = PipelineResult.Node(new ExportAudioOptions { Format = request.Format, Operation = "tts" })
            }
        };
        return jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Giọng đọc: {name}", PipelineResult.Pipeline("voice", input), JobLane.Media), ct);
    }

    /// <summary>Mỗi file .txt một job (tên file = tên audio). DOCX đọc ở bước script_read của P10.</summary>
    public async Task<IReadOnlyList<string>> StartBatchAsync(IReadOnlyList<string> scriptFiles, TtsStudioRequest template, CancellationToken ct = default)
    {
        if (scriptFiles.Count == 0) throw new ArgumentException("Chọn ít nhất một file kịch bản .txt.");
        var ids = new List<string>();
        foreach (var file in scriptFiles)
        {
            if (!Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Chỉ hỗ trợ kịch bản .txt: {Path.GetFileName(file)}");
            var text = await File.ReadAllTextAsync(file, ct);
            ids.Add(await StartAsync(template with { Text = text, Name = Path.GetFileNameWithoutExtension(file) }, ct));
        }

        return ids;
    }

    /// <summary>Khử nhiễu / phát hiện lời nói / cắt khoảng lặng cho một file audio (hf_voice.process_audio).</summary>
    public Task<string> StartAudioProcessAsync(AudioProcessRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(request.Source)) throw new FileNotFoundException("Không tìm thấy file audio.", request.Source);
        if (!request.Denoise && !request.DetectSpeech && !request.TrimSilence) throw new ArgumentException("Chọn ít nhất một thao tác xử lý.");

        var folder = request.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), "TtsStudio");
        var name = Path.GetFileNameWithoutExtension(request.Source);
        var input = new JsonObject
        {
            ["source"] = request.Source,
            ["outputDir"] = folder,
            ["name"] = name + (request.TrimSilence ? "_trimmed" : request.Denoise ? "_denoised" : "_vad"),
            ["denoise"] = request.Denoise,
            ["vad"] = request.DetectSpeech,
            ["trim"] = request.TrimSilence,
            ["export"] = request.Denoise || request.TrimSilence,
            ["stepOptions"] = new JsonObject
            {
                ["denoise"] = PipelineResult.Node(new DenoiseOptions { Mode = request.DenoiseMode }),
                ["vad"] = PipelineResult.Node(new VadOptions { Export = !request.Denoise && !request.TrimSilence })
            }
        };
        return jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Xử lý audio: {name}", PipelineResult.Pipeline("audio", input), JobLane.Media), ct);
    }
}
