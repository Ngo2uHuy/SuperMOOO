using SuperMOOO.AI.Keys;
using SuperMOOO.AI.Tts;

namespace SuperMOOO.AI;

/// <summary>Báo cáo AI cho S16 Kiểm tra hệ thống: key theo provider, TTS local, Edge/Windows.</summary>
public sealed class AiCapabilityReporter(ApiKeyPool keys, NativeTtsServerManager native, ISettingsStore settings) : ICapabilityReporter
{
    private static readonly string[] KeyProviders = ["openai", "gemini", "groq", "openrouter", "deepseek", "hf", "anthropic", "elevenlabs", "google"];

    public string Area => "AI";

    public async Task<CapabilityReport> ReportAsync(CancellationToken ct)
    {
        var items = new List<CapabilityItem>();
        foreach (var provider in KeyProviders)
        {
            var has = await keys.HasKeyAsync(provider, ct);
            items.Add(new CapabilityItem($"API key {provider}", has, has ? null : "Chưa nhập key hợp lệ"));
        }

        var status = native.GetStatus();
        items.Add(new CapabilityItem("TTS local: audiocpp_server", status.ExeFound, status.ExePath ?? "Chưa tìm thấy"));
        items.Add(new CapabilityItem("TTS local: VieNeu", status.VieNeuInstalled, status.VieNeuInstalled ? null : "Thiếu model/giọng/phát âm"));
        items.Add(new CapabilityItem("TTS local: OmniVoice", status.OmniVoiceInstalled, status.OmniVoiceInstalled ? null : "Thiếu model"));
        items.Add(new CapabilityItem("TTS local: Supertonic (tuỳ chọn)", status.SupertonicInstalled,
            status.SupertonicInstalled ? status.SupertonicExePath : "Thiếu runtime tools\\audiocpp_v0.9.2_cpu hoặc model Supertonic3"));
        items.Add(new CapabilityItem("TTS local: OmniVoice trên GPU Vulkan (tuỳ chọn)", status.VulkanExePath is not null && !status.VulkanFailed,
            status.VulkanFailed ? "Vulkan lỗi, OmniVoice đang chạy CPU" : status.VulkanExePath ?? "Thiếu runtime tools\\audiocpp_v0.9.2_vulkan (OmniVoice chạy CPU)"));
        foreach (var (family, name, folder) in new[] { ("qwen3", "Qwen3-TTS", "Qwen3TTS"), ("cosyvoice3", "CosyVoice3", "CosyVoice3"), ("irodori", "Irodori", "Irodori"),
            ("chatterbox", "Chatterbox", "Chatterbox"), ("breeze", "Breeze TTS 2", "Breeze") })
        {
            var installed = native.IsInstalled(family);
            items.Add(new CapabilityItem($"TTS local: {name} (tuỳ chọn)", installed,
                installed ? native.RuntimeExe(family) : $"Thiếu runtime tools\\audiocpp_v0.9.2_cpu hoặc model {folder}"));
        }
        items.Add(new CapabilityItem("Edge TTS (tuỳ chọn)", settings.Get<TtsEdgeSettings>("tts.edge").Enabled, "Endpoint không chính thức"));

        return new CapabilityReport(Area, items);
    }
}
