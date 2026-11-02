using System.Net;
using System.Net.Sockets;

namespace SuperMOOO.AI.Llm;

public sealed record LlamaServerSpec(string Exe, string ModelPath, string ModelLabel, Func<int, IReadOnlyList<string>> Args);

/// <summary>
/// Một tiến trình llama-server dùng chung cho dịch offline và LLM đóng gói: khởi động khi cần, cổng trống ngẫu nhiên,
/// dừng bằng Job Object, tự tắt khi rảnh lâu hoặc khi RAM cạn. Người dùng giữ <see cref="UseLock"/> suốt lượt gọi
/// để việc tắt khi rảnh không cắt ngang yêu cầu đang chạy.
/// </summary>
public sealed class LlamaServerProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly string _id;
    private readonly IProcessRunner _runner;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly Queue<string> _log = new();
    private readonly Timer _idle;

    private CancellationTokenSource? _cts;
    private Task? _process;
    private int _port;
    private long _lastUse = Environment.TickCount64;

    public LlamaServerProcess(string id, IProcessRunner runner, IRuntimeResources? resources, TimeSpan idleTimeout)
    {
        _id = id;
        _runner = runner;
        _idleTimeout = idleTimeout;
        Http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _idle = new Timer(_ => _ = ReleaseIdleAsync(false), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        if (resources is not null) resources.MemoryCritical += (_, _) => _ = ReleaseIdleAsync(true);
    }

    public SemaphoreSlim UseLock { get; } = new(1, 1);
    public HttpClient Http { get; }

    public void Touch() => Interlocked.Exchange(ref _lastUse, Environment.TickCount64);

    // Gọi trong UseLock; _start chỉ tuần tự hoá việc khởi động giữa các yêu cầu song song.
    public async Task<int> EnsureStartedAsync(LlamaServerSpec spec, CancellationToken ct)
    {
        if (_process is { IsCompleted: false }) return _port;
        await _start.WaitAsync(ct);
        try { return await StartCoreAsync(spec, ct); }
        finally { _start.Release(); }
    }

    private async Task<int> StartCoreAsync(LlamaServerSpec spec, CancellationToken ct)
    {
        if (_process is { IsCompleted: false }) return _port;
        await StopAsync();

        if (!File.Exists(spec.Exe))
            throw new InvalidOperationException("Chưa có tools\\llama_cpp\\llama-server.exe.");
        if (!File.Exists(spec.ModelPath))
            throw new InvalidOperationException($"Chưa có {spec.ModelLabel}: {spec.ModelPath}");

        _port = FreePort();
        _log.Clear();
        _cts = new CancellationTokenSource();
        var lines = new Progress<string>(line => { lock (_log) { if (_log.Count == 50) _log.Dequeue(); _log.Enqueue(line); } });
        _process = _runner.RunAsync(new ProcessSpec(spec.Exe, spec.Args(_port), Path.GetDirectoryName(spec.Exe), Heavy: true), lines, _cts.Token);

        try { await WaitHealthyAsync(ct); }
        catch
        {
            await StopAsync();
            throw;
        }

        return _port;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private async Task WaitHealthyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process!.IsCompleted)
                throw new InvalidOperationException("llama-server không khởi động. " + LogTail());
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(500));
                using var response = await Http.GetAsync($"http://127.0.0.1:{_port}/health", cts.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { }

            await Task.Delay(100, ct);
        }

        throw new ProviderException(_id, ProviderErrorKind.Timeout, $"llama-server chưa sẵn sàng sau {StartTimeout.TotalSeconds:0} giây.");
    }

    private string LogTail()
    {
        lock (_log) return string.Join(" | ", _log.TakeLast(3));
    }

    /// <summary>Tắt llama-server khi rảnh quá lâu hoặc RAM cạn; chỉ khi không có lượt gọi nào đang giữ khoá.</summary>
    private async Task ReleaseIdleAsync(bool memoryCritical)
    {
        if (_process is not { IsCompleted: false }) return;
        if (!memoryCritical && Environment.TickCount64 - Interlocked.Read(ref _lastUse) < _idleTimeout.TotalMilliseconds) return;
        if (!UseLock.Wait(0)) return;
        try { await StopAsync(); }
        finally { UseLock.Release(); }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var process = _process;
        _cts = null;
        _process = null;
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
        await _idle.DisposeAsync();
        await UseLock.WaitAsync();
        try { await StopAsync(); }
        finally { UseLock.Release(); }
        Http.Dispose();
    }
}
