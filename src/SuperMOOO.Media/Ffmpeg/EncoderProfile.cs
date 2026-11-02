namespace SuperMOOO.Media.Ffmpeg;

/// <summary>Bảng preset riêng cho từng encoder: không truyền preset x264 cho NVENC/QSV.</summary>
public sealed record EncoderProfile(string Key, string Codec, IReadOnlyList<string> Presets, string DefaultPreset, string PixFmt)
{
    public static readonly EncoderProfile X264 = new("x264", "libx264",
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"], "medium", "yuv420p");

    public static readonly EncoderProfile Nvenc = new("nvenc", "h264_nvenc", ["p1", "p2", "p3", "p4", "p5", "p6", "p7"], "p4", "yuv420p");

    public static readonly EncoderProfile Qsv = new("qsv", "h264_qsv",
        ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"], "medium", "nv12");

    public static readonly EncoderProfile Amf = new("amf", "h264_amf", ["speed", "balanced", "quality"], "balanced", "nv12");

    public static IReadOnlyList<EncoderProfile> All { get; } = [X264, Nvenc, Qsv, Amf];

    public static EncoderProfile For(string? key) => (key ?? "x264").Trim().ToLowerInvariant() switch
    {
        "x264" or "libx264" or "cpu" => X264,
        "nvenc" or "h264_nvenc" => Nvenc,
        "qsv" or "h264_qsv" => Qsv,
        "amf" or "h264_amf" => Amf,
        _ => throw new ArgumentException($"Encoder không hỗ trợ: {key}", nameof(key))
    };

    /// <summary>Preset hợp lệ của encoder này; tên preset kiểu x264 được ánh xạ sang mức tương đương.</summary>
    public string NormalizePreset(string? preset)
    {
        var p = (preset ?? "").Trim().ToLowerInvariant();
        if (Presets.Contains(p)) return p;
        if (this == Nvenc)
            return p switch
            {
                "ultrafast" or "superfast" => "p1", "veryfast" => "p2", "faster" or "fast" => "p3", "medium" => "p4",
                "slow" => "p5", "slower" => "p6", "veryslow" => "p7", _ => DefaultPreset
            };
        if (this == Amf)
            return p switch { "ultrafast" or "superfast" or "veryfast" => "speed", "slow" or "slower" or "veryslow" => "quality", _ => DefaultPreset };
        return DefaultPreset;
    }

    /// <summary>Tham số video: bitrate cố định nếu có, ngược lại chất lượng không đổi (crf/cq/global_quality).</summary>
    public string[] VideoArgs(string? preset, int? bitrateKbps, int quality = 21)
    {
        var args = new List<string> { "-c:v", Codec, "-preset", NormalizePreset(preset) };
        if (bitrateKbps is > 0)
            args.AddRange(["-b:v", bitrateKbps + "k"]);
        else if (this == Nvenc)
            args.AddRange(["-rc", "vbr", "-cq", quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0"]);
        else if (this == Amf)
            args.AddRange(["-rc", "cqp", "-qp_i", quality.ToString(CultureInfo.InvariantCulture), "-qp_p", quality.ToString(CultureInfo.InvariantCulture)]);
        else if (this == Qsv)
            args.AddRange(["-global_quality", quality.ToString(CultureInfo.InvariantCulture)]);
        else
            args.AddRange(["-crf", quality.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-pix_fmt", PixFmt]);
        return [.. args];
    }
}

/// <summary>Dò encoder phần cứng khả dụng: có trong -encoders và encode thử 0.2 s thành công.</summary>
[Service]
public sealed class EncoderDetector(IProcessRunner runner, IToolLocator tools)
{
    private IReadOnlyList<EncoderProfile>? _available;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<EncoderProfile>> AvailableAsync(CancellationToken ct)
    {
        if (_available is not null) return _available;
        await _gate.WaitAsync(ct);
        try { return _available ??= await DetectAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<EncoderProfile>> DetectAsync(CancellationToken ct)
    {
        var ffmpeg = tools.Resolve(ToolKind.Ffmpeg);
        var listing = new StringBuilder();
        await runner.RunAsync(new ProcessSpec(ffmpeg, ["-hide_banner", "-encoders"]), new Sink(l => { lock (listing) listing.AppendLine(l); }), ct);
        string text;
        lock (listing) text = listing.ToString();

        var found = new List<EncoderProfile> { EncoderProfile.X264 };
        foreach (var profile in new[] { EncoderProfile.Nvenc, EncoderProfile.Qsv, EncoderProfile.Amf })
        {
            if (!text.Contains(profile.Codec, StringComparison.Ordinal)) continue;
            var probe = await runner.RunAsync(new ProcessSpec(ffmpeg,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2:r=25",
                 "-frames:v", "5", "-c:v", profile.Codec, "-pix_fmt", profile.PixFmt, "-f", "null", "-"]), null, ct);
            if (probe.ExitCode == 0) found.Add(profile);
        }

        return _available = found;
    }

    public async Task<EncoderProfile> SelectAsync(string preference, CancellationToken ct)
    {
        if (preference == "x264") return EncoderProfile.X264;
        var all = await AvailableAsync(ct);
        return preference == "auto"
            ? all.FirstOrDefault(p => p == EncoderProfile.Nvenc) ?? all.FirstOrDefault(p => p == EncoderProfile.Qsv) ?? all.FirstOrDefault(p => p == EncoderProfile.Amf) ?? EncoderProfile.X264
            : all.FirstOrDefault(p => p.Key == preference) ?? EncoderProfile.X264;
    }

    /// <summary>Encoder nhanh nhất dùng được (phần cứng trước, x264 sau).</summary>
    public async Task<EncoderProfile> BestAsync(CancellationToken ct)
    {
        var all = await AvailableAsync(ct);
        return all.FirstOrDefault(p => p == EncoderProfile.Nvenc) ?? all.FirstOrDefault(p => p == EncoderProfile.Qsv) ?? all.FirstOrDefault(p => p == EncoderProfile.Amf) ?? EncoderProfile.X264;
    }

    private sealed class Sink(Action<string> onLine) : IProgress<string>
    {
        public void Report(string value) => onLine(value);
    }
}
