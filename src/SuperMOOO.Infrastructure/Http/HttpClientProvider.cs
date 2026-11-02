using System.Collections.Concurrent;

namespace SuperMOOO.Infrastructure.Http;

/// <summary>HttpClient theo tên, dựng trên handler của NetworkRouter và tạo lại khi route đổi.</summary>
[Service(As = [typeof(IHttpClientProvider)])]
public sealed class HttpClientProvider : IHttpClientProvider
{
    private readonly INetworkRouter _router;
    private ConcurrentDictionary<string, HttpClient> _clients = new();

    public HttpClientProvider(INetworkRouter router)
    {
        _router = router;
        router.Changed += (_, _) => _clients = new ConcurrentDictionary<string, HttpClient>();
    }

    // Không dispose client cũ: có thể còn request đang chạy; handler tự đóng kết nối sau PooledConnectionLifetime.
    // Timeout vô hạn vì mỗi lời gọi tự đặt timeout riêng bằng CancellationToken (F36).
    public HttpClient Get(string name) =>
        _clients.GetOrAdd(name, _ => new HttpClient(_router.CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan });
}
