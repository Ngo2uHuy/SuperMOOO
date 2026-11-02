using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Audio;

public sealed record DubSegment(string Path, TimeSpan Start);

/// <summary>
/// Đặt từng câu vào đúng mốc trên timeline (port assemble_dubbed_track). Chia theo khối: tối đa BatchSize file mỗi lệnh ffmpeg
/// (tránh giới hạn độ dài dòng lệnh Windows), khối trung gian rồi trộn tiếp, không giải mã thủ công.
/// </summary>
[Service]
public sealed class DubTrackAssembler(FfmpegRunner ffmpeg)
{
    public const int BatchSize = 40;

    public async Task AssembleAsync(IReadOnlyList<DubSegment> segments, TimeSpan total, string output, int sampleRate,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (total <= TimeSpan.Zero || sampleRate <= 0) throw new ArgumentException("Thời lượng timeline và tần số lấy mẫu phải lớn hơn 0.");
        foreach (var s in segments)
        {
            if (s.Start < TimeSpan.Zero) throw new ArgumentException("Mốc bắt đầu audio phải không âm.");
            if (!File.Exists(s.Path)) throw new FileNotFoundException($"Không tìm thấy audio câu: {s.Path}");
        }

        if (segments.Count == 0)
        {
            await ffmpeg.RunAsync(new FfmpegJob(["-f", "lavfi", "-i", $"anullsrc=r={sampleRate}:cl=mono", "-t", Fmt.N(total.TotalSeconds, 3), "-c:a", "pcm_s16le"],
                output, total, Label: "Track im lặng"), progress, ct);
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "aivs-dub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var items = segments.OrderBy(s => s.Start).Select(s => (s.Path, Ms: (long)s.Start.TotalMilliseconds)).ToList();
            var round = 0;
            while (items.Count > BatchSize)
            {
                var next = new List<(string Path, long Ms)>();
                foreach (var chunk in items.Chunk(BatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    var baseMs = chunk[0].Ms;
                    var part = Path.Combine(work, $"p{round}_{next.Count}.wav");
                    await MixAsync(chunk.Select(c => (c.Path, c.Ms - baseMs)).ToList(), null, part, sampleRate, null, ct);
                    next.Add((part, baseMs));
                    progress?.Report(new ProgressInfo(0.8 * Math.Min(1.0, (double)next.Count * BatchSize / items.Count), "Ghép khối âm thanh"));
                }

                items = next;
                round++;
            }

            await MixAsync(items, total, output, sampleRate, progress, ct);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* thư mục tạm, dọn lần sau */ }
        }
    }

    private async Task MixAsync(IReadOnlyList<(string Path, long Ms)> items, TimeSpan? total, string output, int sampleRate,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var args = new List<string>();
        var parts = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            args.AddRange(["-i", items[i].Path]);
            parts.Add($"[{i}:a]aresample={sampleRate},aformat=channel_layouts=mono,adelay={items[i].Ms}:all=1[a{i}]");
        }

        var mix = $"{string.Concat(Enumerable.Range(0, items.Count).Select(i => $"[a{i}]"))}amix=inputs={items.Count}:duration=longest:normalize=0:dropout_transition=0";
        // Khối trung gian không đệm/cắt: độ dài = câu kết thúc muộn nhất. Khối cuối đệm và cắt đúng tổng thời lượng.
        if (total is { } t) mix += $",apad,atrim=duration={Fmt.N(t.TotalSeconds, 3)}";
        parts.Add(mix + "[outa]");
        args.AddRange(["-filter_complex", string.Join(';', parts), "-map", "[outa]", "-c:a", "pcm_s16le", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture)]);
        await ffmpeg.RunAsync(new FfmpegJob(args, output, total, Label: "Ghép track lồng tiếng"), progress, ct);
    }
}
