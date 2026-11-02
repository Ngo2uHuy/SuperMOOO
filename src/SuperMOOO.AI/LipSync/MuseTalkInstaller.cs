using System.ComponentModel;
using System.IO.Compression;
using SuperMOOO.AI.Worker;
using SuperMOOO.Core;

namespace SuperMOOO.AI.LipSync;

public sealed record LipSyncComponent(string Name, bool Ok, string Detail);

/// <summary>
/// Cài runtime MuseTalk (chỉ chạy khi người dùng bấm nút ở Cài đặt): venv Python 3.10 riêng, gói theo requirements-musetalk-cpu của AILive,
/// mã MuseTalk ghim theo commit đã dùng ở AILive, weights suy luận. Cũng nhập được bản đã cài sẵn từ thư mục AILive.
/// </summary>
[Service]
public sealed class MuseTalkInstaller(IProcessRunner runner, IToolLocator tools, IHttpClientProvider http, INetworkRouter network, ISettingsStore settings, AppPaths paths,
    WorkerEnvironmentInstaller workerEnvironment)
{
    public const string SizeNote = "Dung lượng ước tính ~5,8 GB (PyTorch + gói Python ~1,7 GB, weights ~4,1 GB).";
    public const string SpeedNote = "Chạy CPU rất chậm: khoảng 94 lần thời gian thực (câu 10 giây mất ~15 phút, lần đầu dò mặt còn lâu hơn). Nên dò mặt avatar trước và dùng lời thoại ngắn.";

    private const string CodeCommit = "0a89dec45a0192b824e3cf4daf96c239440c5ed8";
    private const string TorchIndex = "https://download.pytorch.org/whl/cpu";
    private const string MmcvWheel = "https://download.openmmlab.com/mmcv/dist/cpu/torch2.0.0/mmcv-2.0.1-cp310-cp310-win_amd64.whl";

    // Chép từ AILive/requirements-musetalk-cpu.txt (chỉ phần suy luận; torch/torchvision/mmcv cài riêng).
    private const string Requirements = """
        numpy==1.23.5
        setuptools==79.0.1
        diffusers==0.30.2
        accelerate==0.28.0
        opencv-python==4.9.0.80
        soundfile==0.12.1
        transformers==4.39.2
        huggingface_hub==0.30.2
        hf-xet==1.7.0
        librosa==0.11.0
        einops==0.8.1
        omegaconf==2.3.0
        mmengine==0.10.7
        mmpose==1.3.2
        mmdet==3.2.0
        gdown>=5,<6
        imageio-ffmpeg==0.6.0
        """;

    // Chuyển từ AILive/scripts/download_musetalk.py: nhận thư mục weights làm tham số.
    private const string DownloadScript = """
        from pathlib import Path
        import sys
        import urllib.request

        from huggingface_hub import hf_hub_download


        def main():
            import gdown
            models = Path(sys.argv[1]).resolve()
            for repo, destination, files in [
                ("TMElyralab/MuseTalk", models, ["musetalkV15/musetalk.json", "musetalkV15/unet.pth"]),
                ("stabilityai/sd-vae-ft-mse", models / "sd-vae", ["config.json", "diffusion_pytorch_model.bin"]),
                ("openai/whisper-tiny", models / "whisper", ["config.json", "pytorch_model.bin", "preprocessor_config.json"]),
                ("yzd-v/DWPose", models / "dwpose", ["dw-ll_ucoco_384.pth"]),
            ]:
                for filename in files:
                    print(f"Downloading {repo}/{filename}", flush=True)
                    hf_hub_download(repo, filename, local_dir=str(destination))
            face = models / "face-parse-bisent"
            face.mkdir(parents=True, exist_ok=True)
            target = face / "79999_iter.pth"
            if not target.exists():
                print("Downloading upstream face parsing weights", flush=True)
                partial = target.with_suffix(".part")
                result = gdown.download(id="154JgKpzCPW82qINcVieuPH3fZ2e0P812", output=str(partial), quiet=False)
                if result is None:
                    raise RuntimeError("Face parsing weights could not be downloaded.")
                partial.replace(target)
            resnet = face / "resnet18-5c106cde.pth"
            if not resnet.exists():
                partial = resnet.with_suffix(".part")
                urllib.request.urlretrieve("https://download.pytorch.org/models/resnet18-5c106cde.pth", partial)
                partial.replace(resnet)


        if __name__ == "__main__":
            main()
        """;

    public async Task<IReadOnlyList<LipSyncComponent>> CheckAsync(CancellationToken ct)
    {
        var python = await FindPython310Async(ct);
        var missingWeights = MuseTalkLocation.MissingWeights(paths);
        var hasFfmpeg = tools.TryResolve(ToolKind.Ffmpeg, out var ffmpeg);
        return
        [
            new("Python 3.10", python is not null, python ?? "Chưa tìm thấy (cài Python 3.10 hoặc chọn đường dẫn python.exe)."),
            new("Venv MuseTalk", File.Exists(MuseTalkLocation.Python(paths)), MuseTalkLocation.Venv(paths)),
            new("Mã MuseTalk", MuseTalkLocation.HasSource(paths), MuseTalkLocation.Source(paths)),
            new("Weights", missingWeights.Count == 0, missingWeights.Count == 0 ? MuseTalkLocation.Weights(paths) : $"Thiếu {missingWeights.Count}/{MuseTalkLocation.RequiredWeights.Count} file"),
            new("FFmpeg", hasFfmpeg, hasFfmpeg ? ffmpeg! : "Chưa tìm thấy FFmpeg.")
        ];
    }

    /// <summary>Đường dẫn người dùng chọn (nếu đúng 3.10), không thì `py -3.10`, không thì thư mục cài mặc định của python.org.</summary>
    public async Task<string?> FindPython310Async(CancellationToken ct)
    {
        var chosen = settings.Get<LipSyncSettings>("lipsync").Python310;
        if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen) && await IsPython310Async(chosen, ct)) return chosen;

        var bundled = Path.Combine(MuseTalkLocation.Root(paths), "python", "python.exe");
        if (File.Exists(bundled) && await IsPython310Async(bundled, ct)) return bundled;

        var viaLauncher = await TryRunAsync("py", ["-3.10", "-c", "import sys;print(sys.executable)"], ct);
        if (viaLauncher is { ExitCode: 0 } && viaLauncher.OutputTail.LastOrDefault(l => l.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase)) is { } exe && File.Exists(exe.Trim()))
            return exe.Trim();

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python310", "python.exe");
        return File.Exists(local) && await IsPython310Async(local, ct) ? local : null;
    }

    private async Task<bool> IsPython310Async(string python, CancellationToken ct)
    {
        var result = await TryRunAsync(python, ["-c", "import sys;print('%d.%d' % sys.version_info[:2])"], ct);
        return result is { ExitCode: 0 } && result.OutputTail.Any(l => l.Trim() == "3.10");
    }

    private async Task<ProcessResult?> TryRunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        try { return await runner.RunAsync(new ProcessSpec(file, args), null, ct); }
        catch (Win32Exception) { return null; }
    }

    /// <summary>Trả null khi xong; thông báo lỗi tiếng Việt khi thất bại. Bước đã có sẽ được bỏ qua nên chạy lại để tiếp tục sau khi mạng đứt.</summary>
    public async Task<string?> InstallAsync(IProgress<string> log, CancellationToken ct)
    {
        var root = MuseTalkLocation.Root(paths);
        Directory.CreateDirectory(root);
        var python = MuseTalkLocation.Python(paths);
        var chosen = settings.Get<LipSyncSettings>("lipsync").Python310;
        if (!string.IsNullOrWhiteSpace(chosen) && (!File.Exists(chosen) || !await IsPython310Async(chosen, ct)))
            return "Đường dẫn Python 3.10 đã chọn không hợp lệ. Kiểm tra lại trong Cài đặt > MuseTalk.";

        if (!File.Exists(python))
        {
            var basePython = await FindPython310Async(ct);
            basePython ??= await workerEnvironment.EnsurePython310Async(log, ct);
            log.Report("Tạo venv Python 3.10…");
            if (!await RunAsync(basePython, ["-m", "venv", MuseTalkLocation.Venv(paths)], null, log, ct)) return "Tạo venv thất bại. Xem nhật ký.";
        }

        log.Report("Cài PyTorch CPU 2.0.1 (~200 MB)…");
        if (!await RunAsync(python, ["-m", "pip", "install", "--disable-pip-version-check", "--no-cache-dir", "torch==2.0.1", "torchvision==0.15.2", "torchaudio==2.0.2",
                "--index-url", TorchIndex], null, log, ct)) return "Cài PyTorch thất bại. Xem nhật ký.";

        var requirements = Path.Combine(root, "requirements-musetalk-cpu.txt");
        await File.WriteAllTextAsync(requirements, Requirements, ct);
        log.Report("Cài gói MuseTalk (mmcv, mmpose, diffusers, transformers…)…");
        if (!await RunAsync(python, ["-m", "pip", "install", "--disable-pip-version-check", "--no-cache-dir", "-r", requirements, MmcvWheel], null, log, ct))
            return "Cài gói Python thất bại. Xem nhật ký.";

        if (!MuseTalkLocation.HasSource(paths))
        {
            log.Report("Tải mã MuseTalk…");
            await DownloadSourceAsync(ct);
        }

        if (MuseTalkLocation.MissingWeights(paths).Count > 0)
        {
            log.Report("Tải weights MuseTalk (~4 GB)…");
            var script = Path.Combine(root, "download_weights.py");
            await File.WriteAllTextAsync(script, DownloadScript, ct);
            Directory.CreateDirectory(MuseTalkLocation.Weights(paths));
            if (!await RunAsync(python, ["-X", "utf8", script, MuseTalkLocation.Weights(paths)], null, log, ct)) return "Tải weights thất bại. Xem nhật ký.";
        }

        var missing = MuseTalkLocation.MissingWeights(paths);
        return missing.Count == 0 ? null : $"Còn thiếu weights: {string.Join(", ", missing)}.";
    }

    private async Task DownloadSourceAsync(CancellationToken ct)
    {
        var temp = Path.Combine(MuseTalkLocation.Root(paths), "src_download");
        var zip = temp + ".zip";
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            using (var response = await http.Get("download").GetAsync($"https://codeload.github.com/TMElyralab/MuseTalk/zip/{CodeCommit}", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zip);
                await response.Content.CopyToAsync(file, ct);
            }

            ZipFile.ExtractToDirectory(zip, temp);
            var inner = Directory.GetDirectories(temp).Single();
            var target = MuseTalkLocation.Source(paths);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(inner, target);
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>Sao chép mã, weights và (nếu chạy được) venv từ một thư mục AILive đã cài MuseTalk. Trả null khi xong.</summary>
    public async Task<string?> ImportFromAiLiveAsync(string aiLiveDir, IProgress<string> log, CancellationToken ct)
    {
        var vendor = Path.Combine(aiLiveDir, "vendor", "MuseTalk");
        if (!File.Exists(Path.Combine(vendor, "scripts", "inference.py"))) return "Không phải thư mục AILive đã có vendor\\MuseTalk.";

        if (!MuseTalkLocation.HasSource(paths))
        {
            log.Report("Chép mã MuseTalk…");
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "models", "data", "results", ".git", "__pycache__" };
            await Task.Run(() => CopyTree(vendor, MuseTalkLocation.Source(paths), skip, ct), ct);
        }

        var models = Path.Combine(vendor, "models");
        if (Directory.Exists(models) && MuseTalkLocation.MissingWeights(paths).Count > 0)
        {
            log.Report("Chép weights (~4 GB, có thể mất vài phút)…");
            await Task.Run(() => CopyTree(models, MuseTalkLocation.Weights(paths), null, ct), ct);
        }

        var venv = Path.Combine(aiLiveDir, ".venv-musetalk");
        var message = "";
        if (!File.Exists(MuseTalkLocation.Python(paths)) && File.Exists(Path.Combine(venv, "Scripts", "python.exe")))
        {
            log.Report("Chép venv…");
            await Task.Run(() => CopyTree(venv, MuseTalkLocation.Venv(paths), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "__pycache__" }, ct), ct);
            // Venv trỏ về Python gốc theo pyvenv.cfg nên thường chạy được sau khi chép; kiểm tra thật rồi mới giữ.
            var probe = await TryRunAsync(MuseTalkLocation.Python(paths), ["-c", "import torch, mmpose, diffusers"], ct);
            if (probe is not { ExitCode: 0 })
            {
                log.Report("Venv chép sang không chạy được, bỏ bản chép.");
                Directory.Delete(MuseTalkLocation.Venv(paths), recursive: true);
                message = " Venv của AILive không dùng lại được: bấm Cài để tạo venv mới.";
            }
        }

        var missing = new List<string>();
        if (!File.Exists(MuseTalkLocation.Python(paths))) missing.Add("venv");
        if (MuseTalkLocation.MissingWeights(paths).Count > 0) missing.Add("weights");
        return missing.Count == 0 ? null : $"Đã nhập phần có sẵn, còn thiếu {string.Join(" và ", missing)}.{message}";
    }

    private static void CopyTree(string from, string to, ISet<string>? skipDirs, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length) continue;
            File.Copy(file, target, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
            if (skipDirs is null || !skipDirs.Contains(Path.GetFileName(dir)))
                CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)), skipDirs, ct);
    }

    private async Task<bool> RunAsync(string file, IReadOnlyList<string> args, string? cwd, IProgress<string> log, CancellationToken ct)
    {
        var cache = Path.Combine(MuseTalkLocation.Root(paths), "cache");
        Directory.CreateDirectory(cache);
        var environment = new Dictionary<string, string>
        {
            ["HF_HOME"] = Path.Combine(cache, "huggingface"),
            ["HF_HUB_CACHE"] = Path.Combine(cache, "huggingface", "hub"),
            ["TORCH_HOME"] = Path.Combine(cache, "torch"),
            ["XDG_CACHE_HOME"] = cache,
            ["PYTHONUTF8"] = "1",
            ["PYTHONUNBUFFERED"] = "1"
        };
        var proxy = network.ProxyUriForTools() ?? "";
        environment["HTTP_PROXY"] = proxy;
        environment["HTTPS_PROXY"] = proxy;
        environment["http_proxy"] = proxy;
        environment["https_proxy"] = proxy;
        environment["ALL_PROXY"] = "";
        environment["all_proxy"] = "";
        var result = await runner.RunAsync(new ProcessSpec(file, args, cwd, environment, Heavy: true), log, ct);
        return result.ExitCode == 0;
    }
}
