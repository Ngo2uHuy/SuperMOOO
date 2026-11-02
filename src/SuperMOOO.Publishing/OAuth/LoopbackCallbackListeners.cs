using SuperMOOO.Publishing.Publications;

namespace SuperMOOO.Publishing.OAuth;

/// <summary>Bọc <see cref="LoopbackOAuthListener"/> (một listener cho cả app) thành cổng chờ callback kiểu CenterPost.</summary>
[Service(As = [typeof(IOAuthCallbackListenerFactory)])]
public sealed class LoopbackCallbackListeners(LoopbackOAuthListener loopback) : IOAuthCallbackListenerFactory
{
    private static readonly TimeSpan StatePoll = TimeSpan.FromMilliseconds(500);

    public IOAuthCallbackListener Start(string provider, string host = "127.0.0.1")
    {
        if (host is not ("127.0.0.1" or "localhost")) throw new ArgumentException("Host callback chỉ được là 127.0.0.1 hoặc localhost.", nameof(host));
        return new Listener(loopback, provider, new Uri($"http://{host}:{LoopbackOAuthListener.DefaultPort}/callback/{provider}"));
    }

    private sealed class Listener(LoopbackOAuthListener loopback, string provider, Uri redirect) : IOAuthCallbackListener
    {
        private readonly TaskCompletionSource<string> _code = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;

        public Uri RedirectUri => redirect;

        public async Task<OAuthCallback> WaitAsync(string expectedState, CancellationToken ct)
        {
            // Listener của app tự kiểm path + state (hằng thời gian); code verifier không cần ở đây (người gọi tự giữ).
            loopback.Start(new OAuthSession(provider, expectedState, "", redirect), (_, code, _) =>
            {
                _code.TrySetResult(code);
                return Task.CompletedTask;
            });
            _started = true;

            while (true)
            {
                var done = await Task.WhenAny(_code.Task, Task.Delay(StatePoll, ct));
                if (done == _code.Task) return new OAuthCallback(await _code.Task, null);
                ct.ThrowIfCancellationRequested();
                // Người dùng từ chối, hết hạn hoặc lỗi: listener tự kết thúc phiên mà không gọi lại.
                if (loopback.State.Status is "expired" or "cancelled" or "error" && !_code.Task.IsCompleted)
                    return new OAuthCallback(null, loopback.State.Message ?? loopback.State.Status);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_started && loopback.State.Status == "waiting") loopback.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
