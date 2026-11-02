using SuperMOOO.AI.Keys;

namespace SuperMOOO.AI.Tts;

/// <summary>Gom giọng của mọi provider đang dùng được (Edge chỉ khi bật, cloud chỉ khi có key).</summary>
[Service]
public sealed class VoiceCatalogService(IEnumerable<ITtsProvider> providers, ApiKeyPool keys, ISettingsStore settings)
{
    public async Task<IReadOnlyList<VoiceInfo>> ListAsync(string? language = null, string? providerId = null, CancellationToken ct = default)
    {
        var result = new List<VoiceInfo>();
        foreach (var provider in providers.Where(p => providerId is null || p.Id == providerId))
        {
            if (!await IsAvailableAsync(provider, ct)) continue;
            try { result.AddRange(await provider.ListVoicesAsync(language, ct)); }
            catch (ProviderException) when (providerId is null) { /* một provider lỗi không làm mất danh sách của provider khác */ }
        }

        return result;
    }

    private async Task<bool> IsAvailableAsync(ITtsProvider provider, CancellationToken ct) => provider.Id switch
    {
        VoiceIds.Edge => settings.Get<TtsEdgeSettings>("tts.edge").Enabled,
        VoiceIds.OpenAi or VoiceIds.Gemini or VoiceIds.ElevenLabs or VoiceIds.Google => await keys.HasKeyAsync(provider.Id, ct),
        _ => true      // native liệt kê theo giọng đã cài
    };
}
