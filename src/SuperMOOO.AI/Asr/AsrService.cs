namespace SuperMOOO.AI.Asr;

/// <summary>Chọn provider ASR (local qua worker hoặc cloud) theo Cài đặt → Model AI, hoặc theo providerId chỉ định.</summary>
[Service]
public sealed class AsrService(IEnumerable<IAsrProvider> providers, ISettingsStore settings)
{
    public async Task<Transcript> TranscribeAsync(AsrRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct, string? providerId = null)
    {
        var configured = settings.Get<AiModelsSettings>("ai.models");
        var id = providerId ?? configured.AsrBackend;
        var provider = providers.FirstOrDefault(p => p.Id == id)
            ?? throw new ArgumentException($"Provider nhận diện '{id}' không hợp lệ.");

        // Model trong Cài đặt chỉ áp cho backend đang chọn; backend chỉ định riêng dùng model mặc định của nó.
        if (string.IsNullOrWhiteSpace(request.Model) && id == configured.AsrBackend)
            request = request with { Model = configured.AsrModel };
        return ClampToDuration(await provider.TranscribeAsync(request, progress, ct));
    }

    /// <summary>Whisper hay bịa câu ở đuôi/khoảng lặng với mốc vượt độ dài audio; các bước sau (storyboard, kiểm mốc phụ đề) từ chối mốc đó nên cắt về độ dài thật.</summary>
    internal static Transcript ClampToDuration(Transcript transcript)
    {
        var total = transcript.Duration;
        if (total <= TimeSpan.Zero || transcript.Segments.All(s => s.End <= total)) return transcript;
        var segments = transcript.Segments.Where(s => s.Start < total)
            .Select(s => s with
            {
                End = s.End > total ? total : s.End,
                Words = s.Words?.Where(w => w.Start < total).Select(w => w.End > total ? w with { End = total } : w).ToList()
            })
            .Where(s => s.End > s.Start).ToList();
        return transcript with { Segments = segments };
    }
}
