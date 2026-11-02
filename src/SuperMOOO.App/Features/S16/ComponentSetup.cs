using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SuperMOOO.AI.Worker;
using SuperMOOO.AI.LipSync;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Infrastructure.Shell;

namespace SuperMOOO.App.Features.S16;

public sealed class ComponentSetupReceipt
{
    public string DataRoot { get; set; } = "";
    public string InstallRoot { get; set; } = "";
    public List<string> ExistingFiles { get; set; } = [];
    public Dictionary<string, long> InstalledFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Complete { get; set; }
    public bool InventoryStarted { get; set; }
}

/// <summary>Biên nhận nằm ngoài thư mục cài để dọn thành phần sau khi uninstaller đã thoát.</summary>
public static class ComponentSetup
{
    public const string JobType = "setup.components";
    private static readonly string[] Components = ["worker", "models", Path.Combine("lipsync", "musetalk")];
    private static string RegistryDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperMOOO.Setup");
    private static string InstallRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private static string ReceiptPath(AppPaths paths) => Path.Combine(RegistryDir,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataRoot.ToUpperInvariant()))) + ".json");

    public static bool Required(AppPaths paths)
    {
        if (!File.Exists(Path.Combine(paths.AppRoot, "setup-required.json"))) return false;
        var receipt = Read(paths);
        return !receipt.Complete || !string.Equals(receipt.InstallRoot, InstallRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static ComponentSetupReceipt Read(AppPaths paths) => File.Exists(ReceiptPath(paths))
        ? JsonSerializer.Deserialize<ComponentSetupReceipt>(File.ReadAllText(ReceiptPath(paths))) ?? throw new InvalidDataException("Biên nhận cài đặt không hợp lệ.")
        : new() { DataRoot = paths.DataRoot, InstallRoot = InstallRoot };

    public static void Prepare(AppPaths paths)
    {
        var receipt = Read(paths);
        if (!string.Equals(receipt.DataRoot, paths.DataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Biên nhận không khớp thư mục dữ liệu.");
        receipt.InstallRoot = InstallRoot;
        receipt.Complete = false;
        // Giữ mốc ban đầu nếu máy tắt giữa lúc cài, tránh coi file tải dở là dữ liệu có sẵn.
        if (!receipt.InventoryStarted)
            receipt.ExistingFiles = [.. Inventory(paths).Keys.Where(p => !receipt.InstalledFiles.ContainsKey(p))];
        receipt.InventoryStarted = true;
        Save(paths, receipt);
    }

    public static void RecordInstalled(AppPaths paths, bool complete = false)
    {
        var receipt = Read(paths);
        var existing = receipt.ExistingFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, size) in Inventory(paths))
            if (!existing.Contains(file)) receipt.InstalledFiles[file] = size;
        receipt.Complete = complete;
        receipt.InventoryStarted = !complete;
        Save(paths, receipt);
    }

    private static Dictionary<string, long> Inventory(AppPaths paths)
    {
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in Components)
        {
            var root = Path.Combine(paths.DataRoot, relative);
            if (!Directory.Exists(root)) continue;
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Thư mục thành phần không được là liên kết: " + root);
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
                files[Path.GetRelativePath(paths.DataRoot, file)] = new FileInfo(file).Length;
        }
        return files;
    }

    private static void Save(AppPaths paths, ComponentSetupReceipt receipt)
    {
        Directory.CreateDirectory(RegistryDir);
        var target = ReceiptPath(paths);
        File.WriteAllText(target + ".partial", JsonSerializer.Serialize(receipt));
        File.Move(target + ".partial", target, overwrite: true);
    }

    /// <summary>Gỡ app là xoá sạch dữ liệu app (chủ dự án yêu cầu, không hỏi lại); chỉ giữ video đã xuất ở thư mục Videos.</summary>
    public static void StartUninstallCleanup()
    {
        new StartupRegistration().SetEnabled(false);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ConfiguredDataRoot() };
        if (Directory.Exists(RegistryDir))
            foreach (var file in Directory.EnumerateFiles(RegistryDir, "*.json"))
                if (JsonSerializer.Deserialize<ComponentSetupReceipt>(File.ReadAllText(file)) is { DataRoot.Length: > 0 } receipt)
                    roots.Add(Path.GetFullPath(receipt.DataRoot));

        // Hook Velopack chỉ có 30 giây; tiến trình riêng (đặt ở %TEMP% vì thư mục biên nhận cũng bị xoá) chờ file hết khoá rồi dọn.
        var plan = Path.Combine(Path.GetTempPath(), "SuperMOOO-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plan);
        File.WriteAllLines(Path.Combine(plan, "roots.txt"), roots, Encoding.UTF8);
        var script = Path.Combine(plan, "cleanup.ps1");
        File.WriteAllText(script, CleanupScript, Encoding.UTF8);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Plan", plan, "-Registry", RegistryDir })
            start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    // Không gọi AppPaths.Create: nó có thể chuyển thư mục dữ liệu cũ, không được chạy lúc gỡ.
    private static string ConfiguredDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("Paths__DataRoot");
        var settings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (string.IsNullOrWhiteSpace(configured) && File.Exists(settings))
            configured = JsonNode.Parse(File.ReadAllText(settings))?["Paths"]?["DataRoot"]?.GetValue<string>();
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? AppPaths.DefaultDataRoot : configured);
    }

    private const string CleanupScript = """
        param([Parameter(Mandatory=$true)][string]$Plan, [Parameter(Mandatory=$true)][string]$Registry)
        $ErrorActionPreference = 'Stop'
        $log = Join-Path $Plan 'cleanup.log'

        function Remove-WithRetry([string]$Path) {
            for ($attempt = 0; $attempt -lt 30; $attempt++) {
                try { Remove-Item -LiteralPath $Path -Force; return }
                catch { if ($attempt -eq 29) { throw }; Start-Sleep -Seconds 2 }
            }
        }

        # Không đi theo liên kết: junction/symlink chỉ gỡ liên kết, không đụng thư mục đích.
        function Remove-Tree([string]$Path) {
            foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    if ($item.PSIsContainer) { [IO.Directory]::Delete($item.FullName) } else { [IO.File]::Delete($item.FullName) }
                } elseif ($item.PSIsContainer) { Remove-Tree $item.FullName }
                else { Remove-WithRetry $item.FullName }
            }
            Remove-WithRetry $Path
        }

        $failed = $false
        foreach ($root in Get-Content -LiteralPath (Join-Path $Plan 'roots.txt') -Encoding UTF8) {
            try {
                if (-not $root) { continue }
                $full = [IO.Path]::GetFullPath($root).TrimEnd('\')
                if (-not (Test-Path -LiteralPath $full)) { continue }
                # Chỉ xoá thư mục dữ liệu của app (có aivs.db hoặc tên *.Data), không bao giờ xoá gốc ổ hay thư mục hồ sơ người dùng.
                $forbidden = @([IO.Path]::GetPathRoot($full), $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA) | ForEach-Object { $_.TrimEnd('\') }
                $isAppData = (Test-Path -LiteralPath (Join-Path $full 'aivs.db')) -or ((Split-Path -Leaf $full) -like '*.Data')
                if (-not $isAppData -or ($forbidden -contains $full)) { "Bỏ qua $full" | Add-Content -LiteralPath $log; continue }
                if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($full); continue }
                # Python của AI worker/MuseTalk chạy từ venv trong thư mục dữ liệu sẽ giữ khoá file.
                Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
                Remove-Tree $full
            } catch { $failed = $true; $_ | Out-String | Add-Content -LiteralPath $log }
        }
        try { if (Test-Path -LiteralPath $Registry) { Remove-Tree $Registry } } catch { $failed = $true; $_ | Out-String | Add-Content -LiteralPath $log }
        if (-not $failed) { Remove-Tree $Plan }
        """;
}

[Service(As = [typeof(IJobHandler)])]
public sealed class ComponentSetupJobHandler(WorkerEnvironmentInstaller installer, MuseTalkInstaller museTalk,
    IProcessRunner runner, AppPaths paths, INetworkRouter network) : IJobHandler
{
    public string Type => ComponentSetup.JobType;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var prepared = false;
        var complete = false;
        try
        {
            ComponentSetup.Prepare(paths);
            prepared = true;
            var log = new Progress<string>(line => context.Progress.Report(new ProgressInfo(-1, line)));
            await installer.InstallAsync(log, ct);
            ComponentSetup.RecordInstalled(paths);
            var environment = new Dictionary<string, string>
            {
                ["HF_HOME"] = Path.Combine(paths.DataRoot, "worker", "huggingface"),
                ["HF_HUB_CACHE"] = Path.Combine(paths.DataRoot, "worker", "huggingface", "hub"),
                ["PYTHONUNBUFFERED"] = "1", ["PYTHONUTF8"] = "1"
            };
            var proxy = network.ProxyUriForTools() ?? "";
            environment["HTTPS_PROXY"] = environment["HTTP_PROXY"] = environment["https_proxy"] = environment["http_proxy"] = proxy;
            environment["ALL_PROXY"] = environment["all_proxy"] = "";
            var models = new[] { "turbo", "denoiser", "speaker", "textdet", "inpaint", "separation", "roformer",
                "vieneu", "omnivoice", "supertonic", "qwen3", "cosyvoice3", "irodori", "chatterbox", "breeze", "hymt2", "livellm" };
            for (var index = 0; index < models.Length; index++)
            {
                context.Progress.Report(new ProgressInfo((index + 1.0) / (models.Length + 3), $"Đang cài model {index + 1}/{models.Length}: {models[index]}"));
                var result = await runner.RunAsync(new ProcessSpec(Path.Combine(paths.WorkerVenv, "Scripts", "python.exe"),
                    ["-E", "-X", "utf8", "-s", "-m", "aivs_worker.install_models", "--models-dir", paths.Models, "--models", models[index]],
                    WorkerLocation.Directory(paths), environment, Heavy: true), log, ct);
                if (result.ExitCode != 0) throw new InvalidOperationException($"Cài model {models[index]} thất bại (mã {result.ExitCode}). {string.Join(Environment.NewLine, result.OutputTail.TakeLast(5))}");
                ComponentSetup.RecordInstalled(paths);
            }
            context.Progress.Report(new ProgressInfo(0.95, "Đang cài thành phần khớp môi MuseTalk…"));
            if (await museTalk.InstallAsync(log, ct) is { } error) throw new InvalidOperationException(error);
            var check = await runner.RunAsync(new ProcessSpec(Path.Combine(paths.WorkerVenv, "Scripts", "python.exe"),
                ["-E", "-X", "utf8", "-s", "-c", "import grpc, av, faster_whisper, onnxruntime, torch, kaldi_native_fbank, soxr"],
                WorkerLocation.Directory(paths), environment, Heavy: true), log, ct);
            if (check.ExitCode != 0) throw new InvalidOperationException("AI worker chưa nạp được thư viện: " + string.Join(Environment.NewLine, check.OutputTail.TakeLast(5)));
            check = await runner.RunAsync(new ProcessSpec(MuseTalkLocation.Python(paths),
                ["-c", "import torch, mmcv, mmpose, diffusers"], MuseTalkLocation.Source(paths), Heavy: true), log, ct);
            if (check.ExitCode != 0) throw new InvalidOperationException("MuseTalk chưa nạp được thư viện: " + string.Join(Environment.NewLine, check.OutputTail.TakeLast(5)));
            complete = true;
            context.Progress.Report(new ProgressInfo(1, "Đã cài đầy đủ môi trường và model AI."));
            return new JsonObject { ["message"] = "Đã cài đầy đủ môi trường và model AI." };
        }
        finally
        {
            try { if (prepared) ComponentSetup.RecordInstalled(paths, complete); }
            finally { Gate.Release(); }
        }
    }
}
