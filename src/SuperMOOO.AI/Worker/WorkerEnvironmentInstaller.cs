using System.Security.Cryptography;
using System.IO.Compression;
using System.ComponentModel;

namespace SuperMOOO.AI.Worker;

/// <summary>Cài Python riêng và các gói đã khóa cho AI worker.</summary>
[Service]
public sealed class WorkerEnvironmentInstaller(IProcessRunner runner, IHttpClientProvider http, INetworkRouter network, AppPaths paths)
{
    private const string PythonVersion = "3.14.10";
    // Ghim nội dung gói Python chính thức của CPython trên NuGet.
    private const string PackageSha256 = "f54ba67c9951c3d773ea47d2bb095150c037c399f0423a968bcda22809241784";
    private const string MuseTalkPythonVersion = "3.10.11";
    private const string MuseTalkPackageSha256 = "7c6f99b160a36a7e09492dfcff2b0a3a60bb5229ca44cdcc3ecb32871a6144d0";

    public Task<string> EnsurePython310Async(IProgress<string> progress, CancellationToken ct) =>
        EnsurePythonAsync(Path.Combine(paths.DataRoot, "lipsync", "musetalk", "python"),
            MuseTalkPythonVersion, MuseTalkPackageSha256, progress, ct);

    public async Task InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var requirements = WorkerLocation.RequirementsLock(paths);
        if (!File.Exists(requirements))
            throw new FileNotFoundException("Không tìm thấy requirements.lock của AI worker.", requirements);

        var workerRoot = Path.Combine(paths.DataRoot, "worker");
        var pythonRoot = Path.Combine(workerRoot, "python");
        var venvPython = Path.Combine(paths.WorkerVenv, "Scripts", "python.exe");

        if (!await IsPythonAsync(venvPython, paths.WorkerVenv, null, "3.14", ct))
        {
            var config = Path.Combine(paths.WorkerVenv, "pyvenv.cfg");
            if (File.Exists(config) && File.ReadLines(config).Any(line =>
                    line.StartsWith("version =", StringComparison.OrdinalIgnoreCase) &&
                    !line.Split('=', 2)[1].Trim().StartsWith("3.14.", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Venv tại {paths.WorkerVenv} dùng Python khác 3.14.");

            var basePython = await EnsurePythonAsync(pythonRoot, PythonVersion, PackageSha256, progress, ct);
            progress.Report("Tạo môi trường Python cho AI worker…");
            await RunCheckedAsync(basePython, ["-m", "venv", paths.WorkerVenv], progress, ct);
            if (!await IsPythonAsync(venvPython, paths.WorkerVenv, null, "3.14", ct))
                throw new InvalidOperationException("Không xác nhận được venv Python vừa tạo.");
        }

        var pip = await runner.RunAsync(new ProcessSpec(venvPython, ["-m", "pip", "--version"]), null, ct);
        if (pip.ExitCode != 0)
        {
            progress.Report("Khôi phục pip cho AI worker…");
            await RunCheckedAsync(venvPython, ["-m", "ensurepip", "--upgrade"], progress, ct);
        }

        progress.Report("Cài gói Python cho AI worker…");
        await RunCheckedAsync(venvPython, ["-m", "pip", "install", "--no-cache-dir", "--disable-pip-version-check", "-r", requirements], progress, ct, heavy: true);
        await RunCheckedAsync(venvPython, ["-m", "pip", "check"], progress, ct);
        progress.Report("Môi trường AI worker đã sẵn sàng.");
    }

    private async Task<string> EnsurePythonAsync(string pythonRoot, string version, string packageSha256,
        IProgress<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Bộ cài Python hiện hỗ trợ Windows x64.");

        var python = Path.Combine(pythonRoot, "python.exe");
        if (await IsPythonAsync(python, pythonRoot, pythonRoot, version, ct)) return python;
        if (Directory.Exists(pythonRoot))
            throw new InvalidOperationException($"Python tại {pythonRoot} chưa hợp lệ. Hãy kiểm tra hoặc di chuyển thư mục này trước khi cài lại.");

        var root = Path.GetDirectoryName(pythonRoot)!;
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, $"python-{version}-{Guid.NewGuid():N}.nupkg");
        var staging = Path.Combine(root, $"python-staging-{Guid.NewGuid():N}");
        try
        {
            progress.Report($"Tải Python {version}…");
            var url = $"https://api.nuget.org/v3-flatcontainer/python/{version}/python.{version}.nupkg";
            using var response = await http.Get("download").GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var file = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(file, ct);

            await using (var downloaded = File.OpenRead(package))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, ct));
                if (!hash.Equals(packageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Gói Python tải về không khớp SHA-256 đã ghim.");
            }

            progress.Report($"Giải nén Python {version}…");
            await Task.Run(() => ZipFile.ExtractToDirectory(package, staging), ct);
            var tools = Path.Combine(staging, "tools");
            if (!File.Exists(Path.Combine(tools, "python.exe")) ||
                !File.Exists(Path.Combine(tools, "Lib", "venv", "__init__.py")) ||
                !File.Exists(Path.Combine(tools, "Lib", "ensurepip", "__init__.py")))
                throw new InvalidDataException("Gói Python thiếu python.exe, venv hoặc ensurepip.");
            Directory.Move(tools, pythonRoot);
        }
        finally
        {
            if (File.Exists(package)) File.Delete(package);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        if (!await IsPythonAsync(python, pythonRoot, pythonRoot, version, ct))
            throw new InvalidOperationException("Python đã cài nhưng không chạy đúng phiên bản hoặc đường dẫn.");
        return python;
    }

    private async Task<bool> IsPythonAsync(string executable, string expectedPrefix, string? expectedBasePrefix, string version, CancellationToken ct)
    {
        if (!File.Exists(executable)) return false;
        try
        {
            var result = await runner.RunAsync(new ProcessSpec(executable,
                ["-c", "import os, sys; v='.'.join(map(str, sys.version_info[:3])); print((v == sys.argv[3] or sys.argv[3] == '3.14' and sys.version_info[:2] == (3, 14)) and os.path.normcase(sys.prefix) == os.path.normcase(sys.argv[1]) and (not sys.argv[2] or os.path.normcase(sys.base_prefix) == os.path.normcase(sys.argv[2])))", expectedPrefix, expectedBasePrefix ?? "", version]), null, ct);
            return result.ExitCode == 0 && result.OutputTail.Any(line => line.Trim() == "True");
        }
        catch (Win32Exception) { return false; }
    }

    private async Task RunCheckedAsync(string executable, IReadOnlyList<string> arguments, IProgress<string> progress, CancellationToken ct, bool heavy = false)
    {
        var environment = new Dictionary<string, string> { ["PYTHONUTF8"] = "1", ["PYTHONUNBUFFERED"] = "1" };
        var proxy = network.ProxyUriForTools() ?? "";
        environment["HTTP_PROXY"] = proxy;
        environment["HTTPS_PROXY"] = proxy;
        environment["http_proxy"] = proxy;
        environment["https_proxy"] = proxy;
        environment["ALL_PROXY"] = "";
        environment["all_proxy"] = "";
        var result = await runner.RunAsync(new ProcessSpec(executable, arguments, Environment: environment, Heavy: heavy), progress, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Lệnh Python thất bại (mã {result.ExitCode}): {string.Join(" ", arguments.Take(3))}.");
    }
}
