namespace SuperMOOO.Core.Abstractions;

public interface ITtsProvider
{
    string Id { get; }   // "native.vieneu" | "native.omnivoice" | "edge" | "openai" | "gemini" | "elevenlabs"
    TtsCapabilities Capabilities { get; }
    Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language, CancellationToken ct);
    Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct);
}

public sealed record TtsCapabilities(bool IsCloud, bool SupportsRate, bool SupportsPitch, bool SupportsClone, int MaxCharsPerRequest);
public sealed record VoiceInfo(string Id, string ProviderId, string DisplayName, string Language, string? Gender = null, bool IsCloned = false);
public sealed record TtsRequest(string Text, string VoiceId, string OutputPath, string? Language = null,
    double Rate = 1.0, double PitchSemitones = 0);
public sealed record TtsResult(string Path, TimeSpan Duration, int SampleRate);

public interface IVoiceCloner
{
    string ProviderId { get; }                                                       // "native.omnivoice"
    Task<VoiceInfo> EnrollAsync(VoiceCloneRequest request, CancellationToken ct);   // mẫu 3–30 s, lời mẫu bắt buộc
    Task DeleteAsync(string voiceId, CancellationToken ct);
}

public sealed record VoiceCloneRequest(string Name, string ReferenceAudioPath, string ReferenceText, string? Language = null);
