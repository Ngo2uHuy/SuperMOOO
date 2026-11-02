using System.Text.Json.Nodes;

namespace SuperMOOO.Media.Ffmpeg;

[Service(As = [typeof(IMediaProbe)])]
public sealed class MediaProbe(IProcessRunner runner, IToolLocator tools) : IMediaProbe
{
    private const int CacheCapacity = 256;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (long Size, long Modified, MediaInfo Info, LinkedListNode<string> Node)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var gate = _gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(full) % (uint)_gates.Length];
        await gate.WaitAsync(ct);
        try
        {
            var file = new FileInfo(full);
            if (!file.Exists) throw new MediaProbeException($"Không tìm thấy file: {path}");
            var size = file.Length;
            var modified = file.LastWriteTimeUtc.Ticks;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(full, out var cached) && cached.Size == size && cached.Modified == modified)
                {
                    _recent.Remove(cached.Node); _recent.AddLast(cached.Node);
                    return cached.Info with { Path = path };
                }
            }
            var info = await ProbeUncachedAsync(full, ct);
            file.Refresh();
            if (file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == modified)
                lock (_cacheLock)
                {
                    if (_cache.Remove(full, out var previous)) _recent.Remove(previous.Node);
                    _cache[full] = (size, modified, info, _recent.AddLast(full));
                    while (_cache.Count > CacheCapacity && _recent.First is { } oldest)
                    { _cache.Remove(oldest.Value); _recent.RemoveFirst(); }
                }
            return info with { Path = path };
        }
        finally { gate.Release(); }
    }

    private async Task<MediaInfo> ProbeUncachedAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new MediaProbeException($"Không tìm thấy file: {path}");

        var output = new StringBuilder();
        var sink = new Sink(line => { lock (output) output.AppendLine(line); });
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.Ffprobe),
                ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path]), sink, ct);
        }
        catch (FileNotFoundException ex)
        {
            throw new MediaProbeException(ex.Message, ex);
        }

        string text;
        lock (output) text = output.ToString();
        if (result.ExitCode != 0)
            throw new MediaProbeException($"ffprobe lỗi (mã {result.ExitCode}) với {Path.GetFileName(path)}: {text.Trim()}");
        return Parse(text, path);
    }

    public static MediaInfo Parse(string json, string path)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new MediaProbeException("ffprobe trả dữ liệu không phải JSON.", ex); }

        var streams = root?["streams"] as JsonArray;
        if (streams is null || streams.Count == 0) throw new MediaProbeException($"{Path.GetFileName(path)} không có luồng media nào.");
        var format = root!["format"];

        var v = streams.FirstOrDefault(s => (string?)s?["codec_type"] == "video" && Num(s?["disposition"]?["attached_pic"]) != 1);
        var a = streams.FirstOrDefault(s => (string?)s?["codec_type"] == "audio");

        var duration = Num(format?["duration"]) ?? Num(v?["duration"]) ?? Num(a?["duration"]) ?? 0;
        var size = (long?)Num(format?["size"]) ?? new FileInfo(path).Length;

        VideoStreamInfo? video = null;
        if (v is not null)
        {
            var fps = ParseRate((string?)v["avg_frame_rate"]) ?? ParseRate((string?)v["r_frame_rate"]) ?? 0;
            video = new VideoStreamInfo((string?)v["codec_name"] ?? "", (int?)Num(v["width"]) ?? 0, (int?)Num(v["height"]) ?? 0,
                Math.Round(fps, 3), Rotation(v));
        }

        AudioStreamInfo? audio = a is null ? null
            : new AudioStreamInfo((string?)a["codec_name"] ?? "", (int?)Num(a["sample_rate"]) ?? 0, (int?)Num(a["channels"]) ?? 0);

        return new MediaInfo(path, TimeSpan.FromSeconds(duration), size, (string?)format?["format_name"], video, audio);
    }

    private static double? Num(JsonNode? node) =>
        node is not null && double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && double.IsFinite(d) ? d : null;

    private static double? ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return null;
        var parts = rate.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return null;
        var den = 1d;
        if (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out den)) return null;
        return den == 0 || num == 0 ? null : num / den;
    }

    private static int Rotation(JsonNode stream)
    {
        var raw = Num(stream["tags"]?["rotate"]);
        if (stream["side_data_list"] is JsonArray side)
            foreach (var item in side)
                if (Num(item?["rotation"]) is { } r) raw = r;
        return raw is null ? 0 : (((int)Math.Round(raw.Value) % 360) + 360) % 360;
    }

    private sealed class Sink(Action<string> onLine) : IProgress<string>
    {
        public void Report(string value) => onLine(value);
    }
}

public static class MediaInfoExtensions
{
    /// <summary>Kích thước hiển thị: ffmpeg tự xoay theo metadata nên 90/270 đổi chỗ rộng/cao.</summary>
    public static (int Width, int Height) DisplaySize(this MediaInfo info)
    {
        if (info.Video is not { } v) return (0, 0);
        return v.Rotation is 90 or 270 ? (v.Height, v.Width) : (v.Width, v.Height);
    }
}
