namespace SuperMOOO.Core.Models;

public sealed record VideoStreamInfo(string Codec, int Width, int Height, double Fps, int Rotation = 0);
public sealed record AudioStreamInfo(string Codec, int SampleRate, int Channels);
public sealed record MediaInfo(string Path, TimeSpan Duration, long SizeBytes, string? Container,
    VideoStreamInfo? Video, AudioStreamInfo? Audio);

public sealed class MediaProbeException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null, bool Heavy = false); // Heavy: tiến trình nặng CPU/RAM, theo dõi để hạ ưu tiên khi máy bận
public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> OutputTail);

public sealed record CapabilityItem(string Name, bool Available, string? Detail = null);
public sealed record CapabilityReport(string Area, IReadOnlyList<CapabilityItem> Items);
