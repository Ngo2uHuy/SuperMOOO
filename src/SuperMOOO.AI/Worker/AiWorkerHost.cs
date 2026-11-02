using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SuperMOOO.AI.Worker.V1;
using SuperMOOO.Core;
using SuperMOOO.Core.Abstractions;
using SuperMOOO.Core.Conventions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.AI.Worker;

public sealed record WorkerLaunch(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

public sealed class AiWorkerOptions
{
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxRestarts { get; init; } = 3;
}

/// <summary>Giữ worker sống trong lúc một lời gọi đang chạy; Dispose khi xong để bộ đếm rảnh chạy lại.</summary>
public sealed class WorkerLease(CallInvoker invoker, Action release) : IDisposable
{
    private int _released;

    public CallInvoker Invoker => invoker;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) release();
    }
}

public interface IAiWorkerHost : IAsyncDisposable
{
    bool IsRunning { get; }
    Task<WorkerLease> AcquireAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

/// <summary>Khởi động lười tiến trình Python, tự tắt khi rảnh, khởi động lại có giới hạn khi chết bất thường.</summary>
[Service(As = [typeof(IAiWorkerHost)])]
public sealed partial class AiWorkerHost : IAiWorkerHost, IDisposable
{
    [GeneratedRegex(@"^AIVS_WORKER_READY port=(\d+)\s*$")]
    private static partial Regex ReadyLine();

    private readonly Func<WorkerLaunch> _launch;
    private readonly AiWorkerOptions _options;
    private readonly ILogger _log;
    private readonly IRuntimeResources? _resources;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<DateTime> _failures = [];

    private Process? _process;
    private WorkerJob? _job;
    private IDisposable? _tracked;
    private GrpcChannel? _channel;
    private CallInvoker? _invoker;
    private Timer? _idleTimer;
    private int _active;
    private int _disposed;
    private long _lastActivity = Environment.TickCount64;

    public AiWorkerHost(IToolLocator tools, AppPaths paths, ILogger<AiWorkerHost> log, IRuntimeResources resources)
        : this(() => DefaultLaunch(tools, paths), new AiWorkerOptions(), log)
    {
        _resources = resources;
    }

    internal AiWorkerHost(Func<WorkerLaunch> launch, AiWorkerOptions options, ILogger log)
    {
        _launch = launch;
        _options = options;
        _log = log;
    }

    internal int StartCount { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public async Task<WorkerLease> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: true })
            {
                _log.LogWarning("AI worker thoát bất thường (mã {Code}).", _process.ExitCode);
                RecordFailure();
                Cleanup();
            }

            if (_process is null) await StartAsync(ct);
            Interlocked.Increment(ref _active);
            _lastActivity = Environment.TickCount64;
            return new WorkerLease(_invoker!, Release);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await StopLockedAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Container có thể gọi cả hai đường dispose; lần sau không được đụng vào _gate đã huỷ.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await StopAsync(CancellationToken.None);
        _gate.Dispose();
    }

    // Container tắt đồng bộ (host.Dispose) nên phải có IDisposable, nếu không DI ném lỗi vì chỉ có IAsyncDisposable.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static WorkerLaunch DefaultLaunch(IToolLocator tools, AppPaths paths)
    {
        if (!tools.TryResolve(ToolKind.Python, out var python))
            throw new AiWorkerException(StatusCode.FailedPrecondition,
                "Chưa có Python cho AI worker. Bấm \"Cài đầy đủ thành phần\" ở Cài đặt > AI worker hoặc đặt đường dẫn Python.");
        var workerDir = WorkerLocation.Directory(paths);
        // -E -s thay cho -I: -I bỏ luôn thư mục hiện tại khỏi sys.path nên `-m aivs_worker` không tìm được package.
        return new WorkerLaunch(python,
            ["-E", "-X", "utf8", "-s", "-m", "aivs_worker", "--port", "0", "--models-dir", paths.Models, "--watch-stdin"], workerDir);
    }

    private async Task StartAsync(CancellationToken ct)
    {
        _failures.RemoveAll(t => DateTime.UtcNow - t > _options.RestartWindow);
        if (_failures.Count >= _options.MaxRestarts)
            throw new AiWorkerException(StatusCode.Unavailable,
                $"AI worker lỗi liên tục ({_failures.Count} lần trong {_options.RestartWindow.TotalMinutes:0} phút), tạm dừng khởi động lại. Kiểm tra ở Cài đặt > AI worker.");

        var launch = _launch();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var psi = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = launch.WorkingDirectory
        };
        foreach (var arg in launch.Arguments) psi.ArgumentList.Add(arg);
        psi.Environment["AIVS_WORKER_TOKEN"] = token;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        if (_resources is { } resources)
            foreach (var name in new[] { "OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS", "AIVS_CPU_THREADS" })
                psi.Environment[name] = resources.Plan.AiThreads.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && ReadyLine().Match(e.Data) is { Success: true } m)
                ready.TrySetResult(int.Parse(m.Groups[1].Value));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _log.LogDebug("worker: {Line}", e.Data);
        };
        process.Exited += (_, _) => ready.TrySetException(
            new AiWorkerException(StatusCode.Unavailable, $"AI worker thoát trước khi sẵn sàng (mã {SafeExitCode(process)})."));

        StartCount++;
        var job = new WorkerJob();
        try
        {
            process.Start();
            job.TryAssign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var port = await ready.Task.WaitAsync(_options.ReadyTimeout, ct);

            _process = process;
            _job = job;
            _tracked = _resources?.TrackProcess(process);
            _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}",
                new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { UseProxy = false } });
            _invoker = _channel.CreateCallInvoker().Intercept(new TokenInterceptor(token));
            _idleTimer = new Timer(_ => _ = OnIdleTickAsync(), null, TickInterval, TickInterval);
        }
        catch (Exception e)
        {
            KillQuietly(process);
            process.Dispose();
            job.Dispose();
            if (e is not OperationCanceledException) RecordFailure();
            if (e is OperationCanceledException or AiWorkerException) throw;
            throw e is TimeoutException
                ? new AiWorkerException(StatusCode.Unavailable, $"AI worker không sẵn sàng sau {_options.ReadyTimeout.TotalSeconds:0} giây.")
                : new AiWorkerException(StatusCode.Unavailable, $"Không khởi động được AI worker: {e.Message}", e);
        }
    }

    private TimeSpan TickInterval => TimeSpan.FromMilliseconds(Math.Clamp(_options.IdleTimeout.TotalMilliseconds / 4, 100, 5000));

    private async Task OnIdleTickAsync()
    {
        if (Volatile.Read(ref _active) > 0 || Environment.TickCount64 - _lastActivity < _options.IdleTimeout.TotalMilliseconds) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (_active == 0 && _process is not null)
            {
                _log.LogInformation("AI worker rảnh, tắt để giải phóng bộ nhớ.");
                await StopLockedAsync();
            }
        }
        catch (Exception e) { _log.LogWarning(e, "Tắt AI worker khi rảnh lỗi."); }
        finally { _gate.Release(); }
    }

    private async Task StopLockedAsync()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    using var cts = new CancellationTokenSource(_options.ShutdownGrace);
                    await new WorkerControl.WorkerControlClient(_invoker).ShutdownAsync(
                        new ShutdownRequest { GraceMs = (int)_options.ShutdownGrace.TotalMilliseconds }, cancellationToken: cts.Token);
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (Exception e) when (e is RpcException or OperationCanceledException) { /* không tắt êm được thì kill cây bên dưới */ }
            }
        }
        finally
        {
            KillQuietly(process);
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        _channel?.Dispose();
        _channel = null;
        _invoker = null;
        _tracked?.Dispose();
        _tracked = null;
        _process?.Dispose();
        _process = null;
        _job?.Dispose();
        _job = null;
    }

    private void Release()
    {
        Interlocked.Decrement(ref _active);
        _lastActivity = Environment.TickCount64;
    }

    private void RecordFailure() => _failures.Add(DateTime.UtcNow);

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* đã thoát */ }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}

/// <summary>Gắn token vào mọi lời gọi; worker từ chối RPC thiếu hoặc sai token.</summary>
internal sealed class TokenInterceptor(string token) : Interceptor
{
    public const string Header = "x-aivs-token";

    private ClientInterceptorContext<TRequest, TResponse> WithToken<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class where TResponse : class
    {
        var headers = new Metadata();
        if (context.Options.Headers is { } existing)
            foreach (var entry in existing) headers.Add(entry);
        headers.Add(Header, token);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, context.Options.WithHeaders(headers));
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithToken(context));

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithToken(context));
}
