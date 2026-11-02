using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using SuperMOOO.AI.Catalog;

namespace SuperMOOO.AI.Tts;

public sealed record NativeTtsStatus(bool ExeFound, string? ExePath, bool VieNeuInstalled, bool OmniVoiceInstalled, bool Running, int Port,
    string? SupertonicExePath = null, bool SupertonicInstalled = false, bool SupertonicRunning = false,
    string? VulkanExePath = null, bool VulkanRunning = false, bool VulkanFailed = false);

/// <summary>
/// Quản lý các tiến trình audiocpp_server.exe: kiểm cấu hình/cổng, khởi động lười, chờ /health, tổng hợp tuần tự, dừng bằng Job Object.
/// Ba server, mỗi server cổng/config/hàng đợi riêng:
/// - old-cpu (cổng native, server.json): VieNeu (runtime cũ nhanh nhất cho tiếng Việt), OmniVoice dự phòng khi không có Vulkan;
/// - v092-cpu (cổng native+1): Supertonic, Qwen3-TTS, CosyVoice3, Irodori, Chatterbox, Breeze (Qwen3/Chatterbox/Breeze chạy Vulkan chậm hơn, Irodori chạy Vulkan hỏng);
/// - v092-vulkan (cổng native+2, iGPU): OmniVoice. Trên CPU, OmniVoice đọc theo voice_ref sinh tiếng bíp ~0,4 s đầu câu và giọng kém giống;
///   Vulkan không bíp và nhanh hơn ~1,6 lần.
/// </summary>
[Service]
public sealed class NativeTtsServerManager : IAsyncDisposable
{
    internal const string ProviderId = "native";
    private const long MaxAudioBytes = 256L * 1024 * 1024;

    // Tên model trong payload → (thư mục model, file GGUF, family của audio.cpp).
    private static readonly Dictionary<string, (string Folder, string File, string Family)> Models = new()
    {
        ["vieneu"] = ("VieNeu", "vieneu-v3-turbo-q8_0.gguf", "vieneu_v3_turbo"),
        ["omnivoice"] = ("OmniVoice", "omnivoice-q8_0.gguf", "omnivoice"),
        ["supertonic"] = ("Supertonic3", "supertonic-3-q8_0.gguf", "supertonic"),
        ["qwen3"] = ("Qwen3TTS", "qwen3-tts-12hz-1.7b-customvoice-q8_0.gguf", "qwen3_tts"),
        ["cosyvoice3"] = ("CosyVoice3", "cosyvoice3-q8_0.gguf", "cosyvoice3"),
        ["irodori"] = ("Irodori", "irodori-tts-v4-small-q8_0.gguf", "irodori_tts"),
        ["chatterbox"] = ("Chatterbox", "chatterbox-q8_0.gguf", "chatterbox"),
        ["breeze"] = ("Breeze", "breeze-tts-2-q8_0.gguf", "breeze_tts")
    };

    private static readonly string[] CpuFamilies = ["supertonic", "qwen3", "cosyvoice3", "irodori", "chatterbox", "breeze"];

    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly ISettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly IRuntimeResources? _resources;
    private readonly Instance _main = new("old-cpu", "server.json", 0);
    private readonly Instance _cpu = new("v092-cpu", "server_v092_cpu.json", 1);
    // Thiếu GGML_VK_DISABLE_F16 thì iGPU Intel (UHD 730) cho audio hỏng.
    private readonly Instance _vulkan = new("v092-vulkan", "server_v092_vulkan.json", 2, new Dictionary<string, string> { ["GGML_VK_DISABLE_F16"] = "1" });
    private volatile bool _vulkanFailed;

    private sealed class Instance(string name, string configName, int portOffset, IReadOnlyDictionary<string, string>? environment = null)
    {
        public readonly string Name = name;
        public readonly string ConfigName = configName;
        public readonly int PortOffset = portOffset;
        public readonly IReadOnlyDictionary<string, string>? Environment = environment;
        public readonly SemaphoreSlim Lifecycle = new(1, 1);
        public readonly SemaphoreSlim Infer = new(1, 1);
        public readonly Queue<string> Log = new();
        public CancellationTokenSource? Cts;
        public Task? Process;
        public string? RunningConfig;
        public int Port;
        public bool Verified;       // đã tổng hợp thành công ít nhất một lượt kể từ lần khởi động này
        public bool IsRunning => Process is { IsCompleted: false };
    }

    public NativeTtsServerManager(IProcessRunner runner, IToolLocator tools, ISettingsStore settings, AppPaths paths, IRuntimeResources resources)
        : this(runner, tools, settings, paths, new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan })
    {
        _resources = resources;
        resources.MemoryCritical += (_, _) => _ = ReleaseIdleAsync();
    }

    private TtsNativeSettings EffectiveSettings()
    {
        var value = _settings.Get<TtsNativeSettings>("tts.native");
        if (_resources?.Plan is { Automatic: true } plan)
        {
            value.Threads = plan.AiThreads;
            if (_resources.Machine.TotalMemoryBytes < 16L * 1073741824) { value.MaxLoadedModels = 1; value.LowMemory = true; }
        }
        return value;
    }

    internal NativeTtsServerManager(IProcessRunner runner, IToolLocator tools, ISettingsStore settings, AppPaths paths, HttpClient http)
    {
        _runner = runner;
        _tools = tools;
        _settings = settings;
        _paths = paths;
        _http = http;
    }

    internal TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(60);
    internal TimeSpan SynthesisTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public bool IsRunning => _main.IsRunning;

    public string ModelPath(string family)
    {
        var (folder, file, _) = Models.GetValueOrDefault(family, Models["omnivoice"]);
        return Path.Combine(_paths.Models, "native_tts", folder, file);
    }

    public string VieNeuVoicesDir => Path.Combine(_paths.Models, "native_tts", "VieNeu", "voices");
    private string RuntimeDir => Path.Combine(_paths.DataRoot, "native_tts");

    /// <summary>Mẫu giọng neo của giọng dựng theo mô tả (xem NativeTtsProvider.AnchorAsync).</summary>
    public string AnchorsDir => Path.Combine(RuntimeDir, "anchors");

    public static TtsNativeSettings Validate(TtsNativeSettings s)
    {
        if (s.Backend is not ("cpu" or "cuda" or "vulkan")) throw new ArgumentException("Chọn CPU, CUDA hoặc Vulkan.");
        Range(nameof(s.Port), s.Port, 1024, 65535);
        Range(nameof(s.Threads), s.Threads, 1, 64);
        Range(nameof(s.Device), s.Device, 0, 16);
        Range(nameof(s.MaxLoadedModels), s.MaxLoadedModels, 1, 2);
        Range(nameof(s.ChunkSize), s.ChunkSize, 80, 1200);
        Range(nameof(s.Steps), s.Steps, 4, 64);
        LanguageCatalog.SpeechCode(s.Language);
        foreach (var (language, provider) in s.LanguagePriority)
            if (!TtsLanguageRouter.Candidates(language).Contains(provider))
                throw new ArgumentException($"Model {provider} không đọc được ngôn ngữ {language}.");
        return s;
    }

    private static void Range(string name, int value, int min, int max)
    {
        if (value < min || value > max) throw new ArgumentOutOfRangeException(name, value, $"{name} phải từ {min} đến {max}.");
    }

    public NativeTtsStatus GetStatus()
    {
        var found = _tools.TryResolve(ToolKind.NativeTtsServer, out var exe) && File.Exists(exe);
        return new NativeTtsStatus(found, found ? exe : null, found && IsInstalled("vieneu"), IsInstalled("omnivoice"),
            IsRunning, _settings.Get<TtsNativeSettings>("tts.native").Port, RuntimeExe("supertonic"), IsInstalled("supertonic"), _cpu.IsRunning,
            VulkanExe(), _vulkan.IsRunning, _vulkanFailed);
    }

    public bool IsInstalled(string family)
    {
        if (!Models.ContainsKey(family) || !IsGguf(ModelPath(family))) return false;
        if (CpuFamilies.Contains(family)) return RuntimeExe(family) is not null;
        if (family == "omnivoice" && UsesVulkan(family)) return true;
        if (!_tools.TryResolve(ToolKind.NativeTtsServer, out var exe) || !File.Exists(exe)) return false;
        return family != "vieneu" || (File.Exists(G2pLibrary(exe)) && File.Exists(G2pDictionary(exe)) && Directory.Exists(VieNeuVoicesDir));
    }

    /// <summary>OmniVoice chạy server Vulkan khi có runtime và backend chung là CPU (đã chọn CUDA/Vulkan thì server chính đã chạy GPU).</summary>
    public bool UsesVulkan(string family) =>
        family == "omnivoice" && !_vulkanFailed && VulkanExe() is not null && _settings.Get<TtsNativeSettings>("tts.native").Backend == "cpu";

    /// <summary>Thiết bị chạy model, hiện ở S16.</summary>
    public string DeviceLabel(string family) =>
        UsesVulkan(family) ? "GPU Vulkan"
        : family is "vieneu" or "omnivoice" && _settings.Get<TtsNativeSettings>("tts.native").Backend is "cuda" or "vulkan" ? "GPU"
        : "CPU";

    private static string G2pLibrary(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, "sea_g2p_rs.dll");
    private static string G2pDictionary(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, "sea_g2p.bin");

    private static bool IsGguf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && magic.SequenceEqual("GGUF"u8);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Runtime v0.9.2 của Supertonic/Qwen3/CosyVoice3/Irodori/Chatterbox/Breeze: bản AVX (audiocpp_v0.9.2_cpu) vì bản portable dựng baseline không SIMD, chậm ~5 lần.
    /// VieNeu/OmniVoice trả null (runtime cũ hoặc Vulkan).
    /// </summary>
    public string? RuntimeExe(string family) => CpuFamilies.Contains(family) ? V092Exe("audiocpp_v0.9.2_cpu", Models[family].Family) : null;

    /// <summary>Runtime v0.9.2 bản Vulkan cho OmniVoice trên GPU tích hợp.</summary>
    public string? VulkanExe() => V092Exe("audiocpp_v0.9.2_vulkan", "omnivoice");

    /// <summary>Tìm cạnh thư mục runtime cũ (dev đặt đường dẫn tay) rồi trong tools của app; runtime phải có model_specs của family.</summary>
    private string? V092Exe(string folder, string specFamily)
    {
        var roots = new List<string>();
        if (_tools.TryResolve(ToolKind.NativeTtsServer, out var exe) && Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(exe))) is { } parent)
            roots.Add(parent);
        roots.Add(_paths.ToolsDir);
        return roots.Select(r => Path.Combine(r, folder, "audiocpp_server.exe"))
            .FirstOrDefault(p => File.Exists(p) && File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "model_specs", specFamily + ".json")));
    }

    private Instance InstanceFor(string family) => family switch
    {
        "vieneu" => _main,
        "omnivoice" => UsesVulkan(family) ? _vulkan : _main,
        _ => _cpu
    };

    /// <summary>Qwen3 chiếm ~6 GB, Breeze ~9 GB khi nạp: chỉ máy từ 24 GB RAM mới giữ 2 model cùng lúc trên server v092-cpu.</summary>
    private int CpuMaxLoadedModels(TtsNativeSettings s) =>
        (_resources?.Machine.TotalMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) >= 24L * 1073741824 ? s.MaxLoadedModels : 1;

    private static JsonObject ServerConfig(int port, string backend, int device, int threads, int maxLoaded, JsonArray models) => new()
    {
        ["host"] = "127.0.0.1", ["port"] = port, ["backend"] = backend, ["device"] = device, ["threads"] = threads,
        ["lazy_load"] = true, ["max_loaded_models"] = maxLoaded, ["idle_unload_ms"] = 0, ["log_request_body"] = false,
        ["max_request_body_bytes"] = 4 * 1024 * 1024, ["busy_timeout_ms"] = 1_800_000, ["models"] = models, ["ui"] = false
    };

    private JsonObject ModelEntry(string family, JsonObject? sessionOptions = null) => new()
    {
        ["id"] = family, ["family"] = Models[family].Family, ["path"] = Path.GetFullPath(ModelPath(family)), ["task"] = "tts", ["mode"] = "offline",
        ["session_options"] = sessionOptions ?? new JsonObject()
    };

    internal JsonObject BuildServerConfig(TtsNativeSettings s, string exe, int port)
    {
        var models = new JsonArray();
        if (IsGguf(ModelPath("vieneu")) && File.Exists(G2pLibrary(exe)) && File.Exists(G2pDictionary(exe)))
            models.Add(ModelEntry("vieneu", new JsonObject
            {
                ["vieneu_v3_turbo.g2p_library"] = Path.GetFullPath(G2pLibrary(exe)),
                ["vieneu_v3_turbo.g2p_dict"] = Path.GetFullPath(G2pDictionary(exe))
            }));
        // OmniVoice luôn khai báo (lazy_load nên chưa tốn RAM): Vulkan lỗi thì chuyển về đây mà không phải khởi động lại server chính.
        if (IsGguf(ModelPath("omnivoice"))) models.Add(ModelEntry("omnivoice", new JsonObject { ["omnivoice.mem_saver"] = s.LowMemory }));
        return ServerConfig(port, s.Backend, s.Device, s.Threads, s.MaxLoadedModels, models);
    }

    internal JsonObject BuildCpuConfig(TtsNativeSettings s, int port)
    {
        var models = new JsonArray();
        foreach (var family in CpuFamilies.Where(f => IsGguf(ModelPath(f))))
            models.Add(ModelEntry(family, family is "qwen3" or "irodori" or "chatterbox" ? new JsonObject { [Models[family].Family + ".mem_saver"] = s.LowMemory } : null));
        return ServerConfig(port, "cpu", 0, s.Threads, CpuMaxLoadedModels(s), models);
    }

    // 2 luồng: phần nặng chạy trên GPU, thêm luồng CPU chỉ tranh với job khác.
    internal JsonObject BuildVulkanConfig(TtsNativeSettings s, int port) =>
        ServerConfig(port, "vulkan", s.Device, 2, 1, new JsonArray(ModelEntry("omnivoice", new JsonObject { ["omnivoice.mem_saver"] = s.LowMemory })));

    public async Task<TtsNativeSettings> EnsureStartedAsync(CancellationToken ct)
    {
        await EnsureStartedAsync(_main, ct);
        return Validate(EffectiveSettings());
    }

    private async Task<int> EnsureStartedAsync(Instance server, CancellationToken ct)
    {
        await server.Lifecycle.WaitAsync(ct);
        try
        {
            var settings = Validate(EffectiveSettings());
            string exe;
            Func<int, JsonObject> build;
            if (server == _main)
            {
                if (!_tools.TryResolve(ToolKind.NativeTtsServer, out var path) || !File.Exists(path))
                    throw new InvalidOperationException("Chưa có audiocpp_server.exe. Chạy scripts\\copy-dev-tools.ps1 hoặc đặt đường dẫn ở Cài đặt → Đường dẫn & công cụ.");
                exe = path;
                build = port => BuildServerConfig(settings, path, port);
            }
            else if (server == _cpu)
            {
                exe = CpuFamilies.Select(RuntimeExe).FirstOrDefault(p => p is not null)
                    ?? throw new InvalidOperationException("Chưa có runtime audio.cpp v0.9.2 (tools\\audiocpp_v0.9.2_cpu).");
                build = port => BuildCpuConfig(settings, port);
            }
            else
            {
                exe = VulkanExe() ?? throw new InvalidOperationException("Chưa có runtime audio.cpp v0.9.2 Vulkan (tools\\audiocpp_v0.9.2_vulkan).");
                build = port => BuildVulkanConfig(settings, port);
            }

            if (build(settings.Port)["models"] is JsonArray { Count: 0 })
                throw new InvalidOperationException("Chưa cài model GGUF / phát âm native (thư mục models\\native_tts).");
            await StartCoreAsync(server, exe, build, settings.Port, ct);
            return server.Port;
        }
        finally { server.Lifecycle.Release(); }
    }

    private async Task StartCoreAsync(Instance server, string exe, Func<int, JsonObject> build, int basePort, CancellationToken ct)
    {
        if (server.IsRunning && server.RunningConfig == exe + build(server.Port).ToJsonString()) return;

        await StopCoreAsync(server);
        var port = server.PortOffset == 0 ? EnsurePortFree(basePort) : FreePort(server, basePort);
        var json = build(port).ToJsonString();

        Directory.CreateDirectory(RuntimeDir);
        var configPath = Path.Combine(RuntimeDir, server.ConfigName);
        await File.WriteAllTextAsync(configPath, json, ct);

        var log = server.Log;
        lock (log) log.Clear();
        server.Cts = new CancellationTokenSource();
        var token = server.Cts.Token;
        var lines = new Progress<string>(line => AppendLog(server, line));
        server.Process = _runner.RunAsync(new ProcessSpec(exe, ["--config", configPath, "--no-ui"], Path.GetDirectoryName(exe), server.Environment, Heavy: true), lines, token);
        server.RunningConfig = exe + json;
        server.Port = port;

        try { await WaitHealthyAsync(server, port, ct); }
        catch
        {
            await StopCoreAsync(server);
            throw;
        }
    }

    private static void AppendLog(Instance server, string line)
    {
        lock (server.Log) { if (server.Log.Count == 100) server.Log.Dequeue(); server.Log.Enqueue(line); }
    }

    private static int EnsurePortFree(int port) =>
        IsPortFree(port) ? port : throw new InvalidOperationException($"Cổng {port} đang được dùng. Đổi cổng ở Cài đặt → TTS local (C++).");

    /// <summary>Server phụ dùng cổng native + offset; cổng bị chiếm thì thử tiếp theo bước 3 để không trùng cổng của hai server còn lại.</summary>
    private int FreePort(Instance server, int basePort)
    {
        var taken = new[] { _main, _cpu, _vulkan }.Where(i => i != server && i.IsRunning).Select(i => i.Port).Append(basePort).ToHashSet();
        for (var i = 0; i < 10; i++)
        {
            var offset = server.PortOffset + 3 * i;
            var port = basePort + offset <= 65535 ? basePort + offset : basePort - offset;
            if (!taken.Contains(port) && IsPortFree(port)) return port;
        }
        throw new InvalidOperationException($"Không tìm được cổng trống cho server {server.Name} gần cổng {basePort}. Đổi cổng ở Cài đặt → TTS local (C++).");
    }

    private static bool IsPortFree(int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, port);
        try
        {
            probe.Start();
            return true;
        }
        catch (SocketException) { return false; }
        finally { probe.Stop(); }
    }

    private async Task WaitHealthyAsync(Instance server, int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (server.Process!.IsCompleted)
                throw new InvalidOperationException($"Server native {server.Name} không khởi động. Kiểm tra backend và model. " + LogTail(server));
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(500));
                using var response = await _http.GetAsync($"http://127.0.0.1:{port}/health", cts.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { }

            await Task.Delay(100, ct);
        }

        throw new ProviderException(ProviderId, ProviderErrorKind.Timeout, $"Server native {server.Name} chưa sẵn sàng sau {StartTimeout.TotalSeconds:0} giây.");
    }

    private static string LogTail(Instance server)
    {
        lock (server.Log) return string.Join(" | ", server.Log.TakeLast(3));
    }

    /// <summary>
    /// Gửi một yêu cầu /v1/audio/speech tới server của model (tuần tự trong từng server) và ghi WAV qua file .part.
    /// Server Vulkan lỗi khi khởi động hoặc ở lượt đầu (driver/thiết bị không chạy được) thì OmniVoice về CPU đến hết phiên:
    /// vẫn cùng model nên không lẫn giọng. Model khác lỗi thì báo lỗi, không đổi sang model khác.
    /// </summary>
    public async Task SynthesizeToFileAsync(JsonObject payload, string outputPath, CancellationToken ct)
    {
        var server = InstanceFor(payload["model"]?.GetValue<string>() ?? "");
        try { await SynthesizeOnAsync(server, payload, outputPath, ct); }
        catch (Exception e) when (server == _vulkan && !server.Verified && !ct.IsCancellationRequested && e is ProviderException or InvalidOperationException)
        {
            _vulkanFailed = true;
            AppendLog(_main, "Vulkan lỗi, OmniVoice chuyển về CPU: " + e.Message);
            await StopLockedAsync(_vulkan);
            await SynthesizeOnAsync(_main, payload, outputPath, ct);
        }
    }

    private async Task SynthesizeOnAsync(Instance server, JsonObject payload, string outputPath, CancellationToken ct)
    {
        await server.Infer.WaitAsync(ct);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var port = await EnsureStartedAsync(server, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SynthesisTimeout);
            try
            {
                using var response = await _http.PostAsJsonAsync($"http://127.0.0.1:{port}/v1/audio/speech", payload, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cts.Token);
                    throw new ProviderException(ProviderId, ProviderErrorKind.BadResponse, $"TTS native HTTP {(int)response.StatusCode}: {Trim(error)}");
                }

                await using (var file = File.Create(temporary))
                await using (var body = await response.Content.ReadAsStreamAsync(cts.Token))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int read;
                    while ((read = await body.ReadAsync(buffer, cts.Token)) > 0)
                    {
                        total += read;
                        if (total > MaxAudioBytes) throw new ProviderException(ProviderId, ProviderErrorKind.BadResponse, "Audio quá lớn. Chia kịch bản thành các phần ngắn hơn.");
                        await file.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException(ProviderId, ProviderErrorKind.Timeout, "TTS native quá thời gian; chia văn bản ngắn hơn.");
            }
            catch (OperationCanceledException)
            {
                // Huỷ khi yêu cầu đã gửi: server vẫn tiếp tục suy luận (~6 nhân) nên phải dừng; lần dùng sau tự khởi động lại.
                await StopLockedAsync(server);
                throw;
            }
            catch (HttpRequestException)
            {
                throw new ProviderException(ProviderId, ProviderErrorKind.Network, "Mất kết nối TTS native. Kiểm tra server hoặc chia văn bản ngắn hơn.");
            }

            try { WavFile.DescribeFile(temporary); }
            catch (InvalidDataException) { throw new ProviderException(ProviderId, ProviderErrorKind.BadResponse, "Server native trả audio rỗng hoặc không phải WAV."); }
            File.Move(temporary, outputPath, overwrite: true);
            server.Verified = true;
        }
        finally
        {
            File.Delete(temporary);
            server.Infer.Release();
        }
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] : s;

    /// <summary>Dừng cả ba server (nút Dừng ở S16, thoát app).</summary>
    public async Task StopAsync()
    {
        foreach (var server in new[] { _main, _cpu, _vulkan })
        {
            await server.Lifecycle.WaitAsync();
            try { await StopCoreAsync(server); }
            finally { server.Lifecycle.Release(); }
        }
    }

    /// <summary>RAM cạn: dừng server không có lượt tổng hợp nào đang chạy để trả bộ nhớ model; SynthesizeToFileAsync tự khởi động lại.</summary>
    private async Task ReleaseIdleAsync()
    {
        foreach (var server in new[] { _main, _cpu, _vulkan })
        {
            if (!server.IsRunning || !server.Infer.Wait(0)) continue;
            try
            {
                if (!server.Lifecycle.Wait(0)) continue;
                try { await StopCoreAsync(server); }
                finally { server.Lifecycle.Release(); }
            }
            finally { server.Infer.Release(); }
        }
    }

    private static async Task StopLockedAsync(Instance server)
    {
        await server.Lifecycle.WaitAsync();
        try { await StopCoreAsync(server); }
        finally { server.Lifecycle.Release(); }
    }

    private static async Task StopCoreAsync(Instance server)
    {
        var cts = server.Cts;
        var process = server.Process;
        server.Cts = null;
        server.Process = null;
        server.RunningConfig = null;
        server.Verified = false;
        if (cts is null) return;

        await cts.CancelAsync();
        if (process is not null)
        {
            try { await process.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* tiến trình chết do huỷ hoặc đã lỗi từ trước; lỗi khởi động đã báo ở WaitHealthyAsync */ }
        }

        cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
