namespace SuperMOOO.Core.Abstractions;

public interface IAsrProvider
{
    string Id { get; }                                   // "local.whisper" | "openai" | "groq" | "gemini"
    bool IsCloud { get; }
    Task<Transcript> TranscribeAsync(AsrRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct);
}

public sealed record AsrRequest(string AudioPath, string? Language = null, string? Model = null,
    bool WordTimestamps = false, bool VadFilter = true, string? Prompt = null);
public sealed record Transcript(string Language, TimeSpan Duration, IReadOnlyList<TranscriptSegment> Segments);
public sealed record TranscriptSegment(int Index, TimeSpan Start, TimeSpan End, string Text, IReadOnlyList<WordTiming>? Words = null);
public sealed record WordTiming(TimeSpan Start, TimeSpan End, string Text, double Probability);
