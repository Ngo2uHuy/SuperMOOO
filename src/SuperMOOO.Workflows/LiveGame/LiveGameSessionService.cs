using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Workflows.LiveGame;

[Service]
public sealed class LiveGameSessionService(LiveGameEngine engine, LiveGameServer server, LiveGameSpeechService speech,
    IRuntimeResources resources, AppPaths paths, ILogger<LiveGameSessionService> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1);
    private CancellationTokenSource? _cts;
    private Task[] _tasks = [];
    private Task? _watch;
    private int _simulationIndex;
    public bool IsRunning => _cts is not null;
    public bool Simulated { get; private set; }
    /// <summary>URL phát (cửa sổ Edge, OBS): đã ẩn bảng giả lập và HUD.</summary>
    public string Url => server.ObsUrl;
    /// <summary>URL xem thử có bảng giả lập; không dùng để phát.</summary>
    public string PreviewUrl => server.Url;
    public event Action<string>? Error;

    public async Task StartAsync(LiveGameSettings settings, bool simulated)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (IsRunning) return;
            settings.Validate();
            server.Start(settings.Port, settings.ThemeId);
            var cts = new CancellationTokenSource();
            try { await engine.BeginAsync(settings, simulated, cts.Token); }
            catch { cts.Dispose(); await server.StopAsync(); throw; }
            _cts = cts; Simulated = simulated;
            engine.SetBackgrounds(server.BackgroundUrls);
            engine.SetPlaylist(server.MusicUrls);
            ILiveEventSource source = simulated ? new SimulatedEventSource() : new TikFinityEventSource(settings, log);
            // Scope chụp vào ExecutionContext của các tác vụ nền nên mọi log của phiên (engine, nguồn sự kiện, TTS) mang SessionId.
            using (log.BeginScope(new Dictionary<string, object> { ["SessionId"] = engine.SessionId }))
            {
                _tasks = [Task.Run(() => engine.RunAsync(cts.Token)), Task.Run(() => source.RunAsync(engine.ReceiveAsync, engine.Status, cts.Token)),
                    Task.Run(() => speech.RunAsync(engine, server, settings, e => Error?.Invoke(e), cts.Token))];
                _watch = WatchAsync(_tasks, cts);
                log.LogInformation("Bắt đầu LiveGame {SessionId} {Mode} tại cổng {Port}", engine.SessionId, simulated ? "giả lập" : "TikFinity", settings.Port);
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task WatchAsync(Task[] tasks, CancellationTokenSource cts)
    {
        var watched = tasks.ToList();
        while (watched.Count > 0 && !cts.IsCancellationRequested)
        {
            var task = await Task.WhenAny(watched); watched.Remove(task);
            if (!task.IsFaulted) continue;
            var error = task.Exception!.GetBaseException();
            log.LogError(error, "Phiên LiveGame bị dừng do lỗi");
            engine.Status("Phiên gặp lỗi · bấm Dừng để lưu và đóng");
            Error?.Invoke(error.Message);
            await cts.CancelAsync();
        }
    }

    public async Task SimulateAsync(LiveEventKind kind, bool storm, CancellationToken ct)
    {
        if (!IsRunning || !Simulated) throw new InvalidOperationException("Chỉ giả lập trong phiên giả lập để không lẫn sổ công đức thật.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts!.Token);
        for (var i = 0; i < (storm ? 600 : 1); i++)
        {
            await engine.ReceiveAsync(SimulatedEventSource.Create(kind, Interlocked.Increment(ref _simulationIndex)), linked.Token);
            if (storm) await Task.Delay(25, linked.Token);
        }
    }

    /// <summary>Áp dụng luật quà/bình luận cho phiên đang chạy (không cần dừng).</summary>
    public void ApplyRules(List<LiveGameRule> rules, int commandsPerMinute)
    {
        if (IsRunning) engine.UpdateRules(rules, commandsPerMinute);
    }

    /// <summary>Bắn thử một luật: chạy được cả trong phiên thật vì sự kiện mang cờ Simulated, không vào sổ công đức.</summary>
    public void TestRule(LiveGameRule rule, int coins)
    {
        if (!IsRunning) throw new InvalidOperationException("Bắt đầu phiên (có thể bật Chế độ giả lập) rồi mới bắn thử luật.");
        if (!engine.TryTest(rule, coins)) throw new InvalidOperationException("Hàng đợi sự kiện đang bận; thử lại sau.");
    }

    public void OpenWindow(bool preview = false)
    {
        if (!IsRunning) throw new InvalidOperationException("Bắt đầu phiên trước khi mở cửa sổ phát.");
        var edge = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
            .Select(p => Path.Combine(p, "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Không tìm thấy Microsoft Edge.");
        var info = new ProcessStartInfo(edge) { UseShellExecute = false };
        info.ArgumentList.Add("--app=" + (preview ? PreviewUrl : Url));
        info.ArgumentList.Add("--window-size=1080,1920");
        // Hồ sơ Edge riêng để cờ dòng lệnh luôn có hiệu lực (Edge đang mở sẽ bỏ qua cờ); tự phát âm thanh vì cửa sổ phát không có nút bật tiếng.
        info.ArgumentList.Add("--user-data-dir=" + paths.CacheDir("livegame_edge"));
        info.ArgumentList.Add("--autoplay-policy=no-user-gesture-required");
        info.ArgumentList.Add("--no-first-run");
        info.ArgumentList.Add("--no-default-browser-check");
        // Cửa sổ bị che vẫn phải vẽ và chạy timer để LIVE Studio bắt hình liên tục.
        info.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        info.ArgumentList.Add("--disable-renderer-backgrounding");
        var process = Process.Start(info) ?? throw new InvalidOperationException("Không mở được Edge.");
        var lease = resources.TrackProcess(process);
        _ = ReleaseAsync(process, lease);
    }

    private static async Task ReleaseAsync(Process process, IDisposable lease)
    {
        try { await process.WaitForExitAsync(); }
        finally { lease.Dispose(); process.Dispose(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_cts is null) return;
            await _cts.CancelAsync();
            try { await Task.WhenAll(_tasks); }
            catch (OperationCanceledException) { }
            catch (Exception e) { log.LogWarning(e, "Tác vụ LiveGame đã kết thúc với lỗi"); }
            if (_watch is not null) await _watch;
            try { await engine.SaveAsync(true, CancellationToken.None); }
            catch
            {
                engine.Status("Chưa lưu được thống kê · bấm Dừng để thử lưu lại");
                throw;
            }
            await server.StopAsync(); _cts.Dispose(); _cts = null; _tasks = [];
            engine.Status("Đã dừng");
            log.LogInformation("Đã dừng LiveGame {SessionId}", engine.SessionId);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
