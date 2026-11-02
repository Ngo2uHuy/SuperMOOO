namespace SuperMOOO.Infrastructure.Network;

/// <summary>
/// Canh tuyến cả máy (VPN Windows/WireGuard) trong lúc lease được giữ: mỗi 2 giây hỏi trạng thái bằng API cục bộ. Rớt thì huỷ ngay mọi request
/// đang chạy qua handler này và chặn mọi request sau, vì khi VPN cả máy rớt Windows tự đi đường mạng thật và phần upload còn lại sẽ lộ IP.
/// Lỗi trả về là <see cref="AccountRouteUnavailableException"/> để lớp đăng bài dời bài như khi cấp tuyến thất bại. Dừng canh khi handler bị Dispose.
/// </summary>
public sealed class RouteWatchHandler : DelegatingHandler
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task<bool>> _isUp;
    private readonly Func<Exception> _error;
    private readonly Action? _onLost;
    // Không Dispose hai nguồn huỷ này: vòng canh có thể còn đọc Token sau khi handler bị Dispose; không có timer nên để GC thu.
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stop = new();
    private int _tripped;

    public RouteWatchHandler(Func<CancellationToken, Task<bool>> isUp, Func<Exception> error, Action? onLost = null)
    {
        _isUp = isUp;
        _error = error;
        _onLost = onLost;
        // Chạy trên thread pool: không bám SynchronizationContext của UI khi lease được xin từ màn hình.
        _ = Task.Run(WatchAsync);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_lost.IsCancellationRequested) throw _error();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lost.Token);
        try
        {
            return await base.SendAsync(request, linked.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException)
        {
            // Lỗi kết nối có thể do VPN vừa rớt trước nhịp canh kế tiếp: hỏi ngay để connector không gửi lại bằng IP thật.
            if (!_lost.IsCancellationRequested && !await IsUpAsync(CancellationToken.None)) Trip();
            if (_lost.IsCancellationRequested) throw _error();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stop.Cancel();
        base.Dispose(disposing);
    }

    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                if (await IsUpAsync(_stop.Token)) continue;
                // Lease vừa nhả (broker có thể đang tắt VPN do chính nó bật): không tính là rớt.
                if (_stop.IsCancellationRequested) return;
                Trip();
                return;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task<bool> IsUpAsync(CancellationToken ct)
    {
        try { return await _isUp(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Không hỏi được trạng thái thì coi như rớt: thà dời bài còn hơn đăng bằng IP thật.
        catch (Exception) { return false; }
    }

    private void Trip()
    {
        if (Interlocked.Exchange(ref _tripped, 1) == 1) return;
        _lost.Cancel();
        _onLost?.Invoke();
    }
}
