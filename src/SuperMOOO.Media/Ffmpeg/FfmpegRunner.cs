namespace SuperMOOO.Media.Ffmpeg;

public sealed class FfmpegException(int exitCode, IReadOnlyList<string> tail)
    : Exception($"FFmpeg lỗi (mã {exitCode}): {string.Join(" | ", tail.TakeLast(6))}")
{
    public int ExitCode => exitCode;
    public IReadOnlyList<string> Tail => tail;
}

/// <summary>Args không gồm file đầu ra; Output là đích cuối (ghi qua *.partial.ext rồi đổi tên). Output = null cho lệnh phân tích (-f null -).
/// Light: lệnh ngắn người dùng đang chờ (chụp một khung khi tua), không xếp lượt media để khỏi kẹt sau job render dài.
/// StdinWriter: đẩy dữ liệu vào stdin của ffmpeg (rawvideo qua pipe:0); null = giữ -nostdin như cũ. Hàm trả về là runner đóng stdin.</summary>
public sealed record FfmpegJob(IReadOnlyList<string> Args, string? Output = null, TimeSpan? ExpectedDuration = null,
    string? WorkingDirectory = null, string Label = "Đang xử lý", string LogLevel = "error", bool Light = false,
    Func<Stream, CancellationToken, Task>? StdinWriter = null);

public sealed record FfmpegResult(IReadOnlyList<string> Log);

[Service]
public sealed class FfmpegRunner(IProcessRunner runner, IToolLocator tools, IRuntimeResources? resources = null)
{
    private const int KeepLines = 200;
    private const int TailLines = 50;

    public async Task<FfmpegResult> RunAsync(FfmpegJob job, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var lease = resources is null || job.Light ? null : await resources.AcquireMediaAsync(ct);
        var args = new List<string> { "-hide_banner", "-y", "-loglevel", job.LogLevel, "-nostats", "-progress", "pipe:1" };
        if (job.StdinWriter is null) args.Insert(0, "-nostdin");
        if (resources is { } runtime)
        {
            var threads = runtime.CurrentMediaThreads().ToString(CultureInfo.InvariantCulture);
            if (!job.Args.Contains("-filter_threads")) args.AddRange(["-filter_threads", threads]);
            if (!job.Args.Contains("-filter_complex_threads")) args.AddRange(["-filter_complex_threads", threads]);
            foreach (var argument in job.Args)
            {
                if (argument == "-i" && !job.Args.Contains("-threads")) args.AddRange(["-threads", threads]);
                args.Add(argument);
            }
            if (!job.Args.Contains("-threads") && !job.Args.Contains("-threads:v")) args.AddRange(["-threads:v", threads]);
        }
        else args.AddRange(job.Args);

        string? partial = null;
        if (job.Output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.Output))!);
            partial = PartialPath(job.Output);
            args.Add(partial);
        }

        var log = new Queue<string>();
        var sink = new LineSink(line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                if (progress is not null && job.ExpectedDuration is { TotalSeconds: > 0 } total
                    && long.TryParse(line.AsSpan(12), out var us) && us >= 0)
                    progress.Report(new ProgressInfo(Math.Min(0.99, us / 1e6 / total.TotalSeconds), job.Label));
                return;
            }

            if (IsProgressKey(line)) return;
            lock (log)
            {
                if (log.Count == KeepLines) log.Dequeue();
                log.Enqueue(line);
            }
        });

        var ok = false;
        try
        {
            var spec = new ProcessSpec(tools.Resolve(ToolKind.Ffmpeg), args, job.WorkingDirectory, Heavy: true);
            var result = job.StdinWriter is null
                ? await runner.RunAsync(spec, sink, ct)
                : await RunWithStdinAsync(spec, sink, job.StdinWriter, ct);
            string[] lines;
            lock (log) lines = log.ToArray();
            if (result.ExitCode != 0) throw new FfmpegException(result.ExitCode, lines.TakeLast(TailLines).ToList());
            if (partial is not null)
            {
                if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
                    throw new FfmpegException(0, [.. lines.TakeLast(TailLines), "FFmpeg không tạo ra file đầu ra."]);
                File.Move(partial, job.Output!, overwrite: true);
            }

            ok = true;
            progress?.Report(new ProgressInfo(1, job.Label));
            return new FfmpegResult(lines);
        }
        finally
        {
            if (!ok && partial is not null)
                try { File.Delete(partial); } catch (IOException) { /* file còn bị giữ, lần sau ghi đè */ }
        }
    }

    // IProcessRunner đóng stdin ngay khi khởi động nên job rawvideo tự spawn; vẫn TrackProcess như mọi ffmpeg nặng khác.
    private async Task<ProcessResult> RunWithStdinAsync(ProcessSpec spec, IProgress<string> sink,
        Func<Stream, CancellationToken, Task> writer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new System.Diagnostics.ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? ""
        };
        foreach (var arg in spec.Arguments) psi.ArgumentList.Add(arg);

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) sink.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) sink.Report(e.Data); };
        process.Start();
        using var tracked = resources?.TrackProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* đã thoát */ }
        });

        try
        {
            await writer(process.StandardInput.BaseStream, ct);
            process.StandardInput.Close();
        }
        catch (IOException) { /* ffmpeg chết giữa chừng: mã thoát + log stderr báo lỗi thật bên dưới */ }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* đã thoát */ }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return new ProcessResult(process.ExitCode, []);
    }

    public static string PartialPath(string output) =>
        Path.Combine(Path.GetDirectoryName(output) ?? "", Path.GetFileNameWithoutExtension(output) + ".partial" + Path.GetExtension(output));

    private static bool IsProgressKey(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return false;
        return line[..eq] is "frame" or "fps" or "bitrate" or "total_size" or "out_time" or "out_time_ms" or "dup_frames"
            or "drop_frames" or "speed" or "progress" || line.StartsWith("stream_", StringComparison.Ordinal);
    }

    private sealed class LineSink(Action<string> onLine) : IProgress<string>
    {
        public void Report(string value) => onLine(value);
    }
}
