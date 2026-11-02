using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuperMOOO.Core;

namespace SuperMOOO.AI.LipSync;

/// <summary>
/// MuseTalk 1.5 chạy CPU, một tiến trình riêng mỗi lần dựng (giải phóng RAM ~4 GB ngay khi xong; không dùng AI worker vì cần Python 3.10).
/// MuseTalk lặp avatar qua lại và dò mặt từng khung (~3 s/khung trên CPU) nên chỉ cắt avatar bằng độ dài lời thoại, làm tròn theo bậc
/// để các câu ngắn dùng chung cache; toạ độ mặt lưu cạnh avatar.
/// </summary>
[Service(As = [typeof(ILipSyncEngine)])]
public sealed partial class MuseTalkEngine(IProcessRunner runner, IToolLocator tools, IMediaProbe probe, AppPaths paths, MuseTalkPlanner planner,
    IRuntimeResources? resources = null) : ILipSyncEngine
{
    private static readonly int[] Buckets = [5, 10, 20, 40, 80, 180];
    private const double MaxAudioSeconds = 180;
    private const double MaxDriftSeconds = 0.25;
    private readonly SemaphoreSlim _gate = new(1, 1);

    [GeneratedRegex(@"(\d{1,3})%\|")]
    private static partial Regex Percent();

    public string Id => "musetalk";

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(GetStatus().Ready);

    public LipSyncStatus GetStatus()
    {
        var missing = new List<string>();
        if (!File.Exists(MuseTalkLocation.Python(paths))) missing.Add("Python 3.10 (venv)");
        if (!MuseTalkLocation.HasSource(paths)) missing.Add("mã MuseTalk");
        if (MuseTalkLocation.MissingWeights(paths).Count > 0) missing.Add("weights MuseTalk");
        if (!tools.TryResolve(ToolKind.Ffmpeg, out _)) missing.Add("FFmpeg");
        return missing.Count == 0
            ? new LipSyncStatus(true, [], "MuseTalk sẵn sàng (CPU).")
            : new LipSyncStatus(false, missing, $"Chưa đủ: {string.Join(", ", missing)}. Cài ở Cài đặt → Avatar khớp môi.");
    }

    public LipSyncEstimate Estimate(string avatarVideoPath, double audioSeconds)
    {
        var bucket = BucketFor(audioSeconds);
        var needed = !File.Exists(CachePath(avatarVideoPath, bucket, ".pkl"));
        return new LipSyncEstimate(planner.Plan(audioSeconds, needed ? bucket : 0).EtaSeconds, needed);
    }

    public async Task PrepareAvatarAsync(string avatarVideoPath, double forSeconds, IProgress<string>? progress, CancellationToken ct)
    {
        var bucket = BucketFor(forSeconds);
        // Một giây im lặng đủ để MuseTalk chạy qua bước dò mặt và lưu toạ độ.
        var silence = Path.Combine(paths.CacheDir("lipsync"), $"silence-{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(silence)!);
        try
        {
            await FfmpegAsync(["-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "1", "-c:a", "pcm_s16le", silence], ct);
            await ExecuteAsync(avatarVideoPath, silence, 1, bucket, null, progress, ct);
        }
        finally { TryDelete(silence); }
    }

    public async Task<LipSyncResult> RenderAsync(LipSyncRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(request.AvatarVideoPath)) throw new FileNotFoundException("Không tìm thấy video avatar.", request.AvatarVideoPath);
        if (!File.Exists(request.AudioPath)) throw new FileNotFoundException("Không tìm thấy audio lời thoại.", request.AudioPath);
        var audio = (await probe.ProbeAsync(request.AudioPath, ct)).Duration.TotalSeconds;
        if (audio <= 0) throw new InvalidOperationException("Không đọc được thời lượng audio.");
        if (audio > MaxAudioSeconds) throw new InvalidOperationException("Lời thoại dài hơn 3 phút. Tách thành nhiều đoạn ngắn hơn.");

        await ExecuteAsync(request.AvatarVideoPath, request.AudioPath, audio, BucketFor(audio), request.OutputPath, progress, ct);
        return new LipSyncResult(request.OutputPath, (await probe.ProbeAsync(request.OutputPath, ct)).Duration);
    }

    private static int BucketFor(double seconds) => Buckets.FirstOrDefault(b => b >= seconds, Buckets[^1]);

    /// <summary>Cache cạnh avatar thư viện (lipsync\avatars\&lt;id&gt;\musetalk); video ngoài thư viện dùng thư mục theo hash đường dẫn + kích thước + ngày sửa.</summary>
    private string CacheDir(string avatarPath)
    {
        var full = Path.GetFullPath(avatarPath);
        var library = Path.GetFullPath(MuseTalkLocation.Avatars(paths)) + Path.DirectorySeparatorChar;
        if (full.StartsWith(library, StringComparison.OrdinalIgnoreCase)) return Path.Combine(Path.GetDirectoryName(full)!, "musetalk");
        var info = new FileInfo(full);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..32];
        return Path.Combine(paths.DataRoot, "lipsync", "avatar_cache", key);
    }

    private string CachePath(string avatarPath, int bucket, string ext) =>
        Path.Combine(CacheDir(avatarPath), $"b{bucket}_s{MuseTalkPlanner.MaxSide}{ext}");

    private async Task ExecuteAsync(string avatarPath, string audioPath, double audioSeconds, int bucket, string? output, IProgress<string>? progress, CancellationToken ct)
    {
        var status = GetStatus();
        if (!status.Ready) throw new LipSyncUnavailableException(status.Message);

        await _gate.WaitAsync(ct);
        string? staging = null;
        try
        {
            var src = MuseTalkLocation.Source(paths);
            await EnsureModelsLinkAsync(src, ct);

            var avatar = (await probe.ProbeAsync(avatarPath, ct)).Duration.TotalSeconds;
            var avatarSeconds = Math.Min(avatar, bucket);
            var cachedVideo = CachePath(avatarPath, bucket, ".mp4");
            var cachedCoords = CachePath(avatarPath, bucket, ".pkl");
            var coordsCached = File.Exists(cachedCoords);
            var plan = planner.Plan(audioSeconds, coordsCached ? 0 : avatarSeconds);
            foreach (var note in plan.Notes) progress?.Report(note);
            Directory.CreateDirectory(CacheDir(avatarPath));

            // MuseTalk ghép lệnh shell không đặt dấu nháy quanh đường dẫn: chỉ dùng thư mục tạm tên ASCII sinh ra, đường dẫn tương đối so với thư mục mã.
            var name = Guid.NewGuid().ToString("N");
            var relative = "work/" + name;
            staging = Path.Combine(src, "work", name);
            Directory.CreateDirectory(staging);

            progress?.Report("Chuẩn hoá video và âm thanh");
            if (!File.Exists(cachedVideo))
            {
                var partial = cachedVideo + ".part.mp4";
                // Bản trung gian gần như không mất chất lượng vì nó là đầu vào của khớp môi.
                await FfmpegAsync(["-i", avatarPath, "-t", avatarSeconds.ToString("0.00", CultureInfo.InvariantCulture), "-an", "-vf",
                    $"scale='min({plan.MaxSide},iw)':'min({plan.MaxSide},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,fps={plan.Fps}",
                    "-c:v", "libx264", "-preset", plan.X264Preset, "-crf", "16", "-pix_fmt", "yuv420p", partial], ct);
                File.Move(partial, cachedVideo, overwrite: true);
            }

            File.Copy(cachedVideo, Path.Combine(staging, "avatar.mp4"), overwrite: true);
            if (coordsCached) File.Copy(cachedCoords, Path.Combine(staging, "avatar.pkl"), overwrite: true);
            await FfmpegAsync(["-i", audioPath, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", Path.Combine(staging, "speech.wav")], ct);
            await File.WriteAllTextAsync(Path.Combine(staging, "request.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["avatar"] = new Dictionary<string, string> { ["video_path"] = relative + "/avatar.mp4", ["audio_path"] = relative + "/speech.wav", ["result_name"] = "output.mp4" }
            }), ct);

            var ffmpeg = tools.Resolve(ToolKind.Ffmpeg);
            var binDir = Path.GetDirectoryName(ffmpeg)!;
            // MuseTalk gọi `ffmpeg` theo tên: nếu file đang dùng không tên ffmpeg.exe thì chép sang thư mục tạm.
            if (!string.Equals(Path.GetFileName(ffmpeg), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                binDir = Path.Combine(staging, "bin");
                Directory.CreateDirectory(binDir);
                File.Copy(ffmpeg, Path.Combine(binDir, "ffmpeg.exe"));
            }

            var threads = plan.Threads.ToString(CultureInfo.InvariantCulture);
            var env = new Dictionary<string, string>
            {
                ["PATH"] = binDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["PYTHONUTF8"] = "1", ["PYTHONUNBUFFERED"] = "1",
                ["OMP_NUM_THREADS"] = threads, ["MKL_NUM_THREADS"] = threads, ["OPENBLAS_NUM_THREADS"] = threads
            };
            var eta = plan.EtaSeconds > 0 ? $" · ước tính ~{Math.Max(1, (int)Math.Round(plan.EtaSeconds / 60.0))} phút" : "";
            progress?.Report($"MuseTalk đang tạo khẩu hình (CPU{eta}{(coordsCached ? "" : " · lần đầu dò khuôn mặt, các lần sau nhanh hơn")})");

            var lines = new Progress<string>(line =>
            {
                if (Percent().Match(line) is { Success: true } m) progress?.Report($"MuseTalk {m.Groups[1].Value}%");
            });
            var started = Stopwatch.GetTimestamp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(plan.TimeoutSeconds));
            ProcessResult result;
            try
            {
                result = await runner.RunAsync(new ProcessSpec(MuseTalkLocation.Python(paths),
                [
                    "-m", "scripts.inference", "--inference_config", relative + "/request.json", "--result_dir", relative + "/results",
                    "--unet_model_path", "models/musetalkV15/unet.pth", "--unet_config", "models/musetalkV15/musetalk.json", "--version", "v15",
                    "--batch_size", plan.BatchSize.ToString(CultureInfo.InvariantCulture), "--ffmpeg_path", binDir, "--saved_coord", "--use_saved_coord"
                ], src, env, Heavy: true), lines, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"MuseTalk quá {plan.TimeoutSeconds / 60} phút chưa xong nên đã dừng. Thử lời thoại ngắn hơn hoặc đóng bớt ứng dụng.");
            }

            var generated = Path.Combine(staging, "results", "v15", "output.mp4");
            if (result.ExitCode != 0 || !File.Exists(generated))
                throw new InvalidOperationException($"MuseTalk lỗi (mã {result.ExitCode}): {string.Join(" | ", result.OutputTail.TakeLast(4))}");

            planner.RecordRate(audioSeconds, Stopwatch.GetElapsedTime(started).TotalSeconds, coordsCached ? 0 : avatarSeconds);
            var coords = Path.Combine(staging, "avatar.pkl");
            if (!coordsCached && File.Exists(coords)) File.Copy(coords, cachedCoords, overwrite: true);
            if (output is null) return;

            if ((await probe.ProbeAsync(generated, ct)).Duration.TotalSeconds + 0.15 < audioSeconds)
                throw new InvalidOperationException("Video tạo ra ngắn hơn audio. Không xuất kết quả vì sẽ mất lời thoại.");

            progress?.Report("Ghép video và audio trên cùng timeline");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var temp = Path.ChangeExtension(output, ".part.mp4");
            // MuseTalk đã mã hoá H.264: -c:v copy tránh thêm một thế hệ mất chất lượng và một lần mã hoá chậm.
            await FfmpegAsync(["-i", generated, "-i", audioPath, "-map", "0:v:0", "-map", "1:a:0", "-af", "asetpts=PTS-STARTPTS", "-c:v", "copy",
                "-avoid_negative_ts", "make_zero", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", temp], ct);
            var drift = Math.Abs((await probe.ProbeAsync(temp, ct)).Duration.TotalSeconds - audioSeconds);
            if (drift > MaxDriftSeconds)
            {
                TryDelete(temp);
                throw new InvalidOperationException("Thời lượng đầu ra lệch audio quá 250 ms. Cần kiểm tra trước khi dùng.");
            }

            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            // Chỉ xoá thư mục tạm do chính lần chạy này sinh ra.
            if (staging is not null)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }

            _gate.Release();
        }
    }

    /// <summary>Mã MuseTalk đọc ./models/... theo thư mục chạy; weights thật ở thư mục model của app nên nối bằng junction (không cần quyền admin).</summary>
    private async Task EnsureModelsLinkAsync(string src, CancellationToken ct)
    {
        var link = Path.Combine(src, "models");
        if (Directory.Exists(link)) return;
        var result = await runner.RunAsync(new ProcessSpec("cmd.exe", ["/c", "mklink", "/J", link, MuseTalkLocation.Weights(paths)]), null, ct);
        if (result.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException($"Không tạo được liên kết tới weights MuseTalk: {string.Join(" | ", result.OutputTail.TakeLast(2))}");
    }

    private async Task FfmpegAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        using var lease = resources is null ? null : await resources.AcquireMediaAsync(ct);
        var all = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (resources?.CurrentMediaThreads() is > 0 and var n) all.AddRange(["-threads", n.ToString(CultureInfo.InvariantCulture)]);
        all.AddRange(args);
        var result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.Ffmpeg), all), null, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg lỗi: {string.Join(" | ", result.OutputTail.TakeLast(3))}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
