using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SuperMOOO.Publishing.OAuth;

public sealed record OAuthLoginState(string Status, string? Provider, string? Message, DateTimeOffset? ExpiresAt);

public delegate Task OAuthCodeHandler(OAuthSession session, string code, CancellationToken ct);

/// <summary>
/// Máy chủ loopback 127.0.0.1:8765 nhận callback OAuth: một phiên một lúc, kiểm state (hằng thời gian), tối đa 10 phút.
/// Không tự mở trình duyệt; người gọi mở URL.
/// </summary>
[Service]
public sealed class LoopbackOAuthListener(int port = LoopbackOAuthListener.DefaultPort) : IDisposable
{
    public const int DefaultPort = 8765;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private OAuthLoginState _state = new("idle", null, null, null);
    private CancellationTokenSource? _cancel;
    private Task _loop = Task.CompletedTask;

    public OAuthLoginState State
    {
        get { lock (_gate) return _state; }
    }

    /// <param name="host">127.0.0.1 hoặc localhost (Meta ở chế độ Development chỉ tự cho phép localhost).</param>
    public OAuthSession CreateSession(string provider, string host = "127.0.0.1") =>
        new(provider, Pkce.NewState(), Pkce.NewVerifier(), new Uri($"http://{host}:{port}/callback/{provider}"));

    public void Start(OAuthSession session, OAuthCodeHandler onCode, TimeSpan? lifetime = null)
    {
        var expires = DateTimeOffset.UtcNow + (lifetime ?? DefaultLifetime);
        HttpListener listener;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_state.Status == "waiting") throw new InvalidOperationException("Đang có phiên đăng nhập. Hoàn tất hoặc đợi phiên hết hạn.");
            if (!_loop.IsCompleted) throw new InvalidOperationException("Phiên đăng nhập trước đang đóng. Đợi giây lát rồi kết nối lại.");
            listener = new HttpListener();
            listener.Prefixes.Add($"http://{session.RedirectUri.Host}:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException)
            {
                listener.Close();
                throw new InvalidOperationException($"Cổng {port} đang bận; đóng ứng dụng đang dùng cổng này rồi thử lại.");
            }

            cts = _cancel = new CancellationTokenSource();
            _state = new OAuthLoginState("waiting", session.Platform, "Hoàn tất đăng nhập trong trình duyệt.", expires);
            _loop = Task.Run(() => LoopAsync(listener, session, onCode, expires, cts));
        }
    }

    public void Cancel()
    {
        lock (_gate) _cancel?.Cancel();     // _cancel chỉ khác null khi phiên còn sống
    }

    public void Dispose() => Cancel();

    private void Finish(string status, string message)
    {
        lock (_gate) _state = _state with { Status = status, Message = message };
    }

    private async Task LoopAsync(HttpListener listener, OAuthSession session, OAuthCodeHandler onCode, DateTimeOffset expires, CancellationTokenSource cts)
    {
        Task<HttpListenerContext>? pending = null;
        try
        {
            while (State.Status == "waiting")
            {
                var remaining = expires - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) { Finish("expired", "Phiên đăng nhập đã hết hạn."); break; }

                pending ??= listener.GetContextAsync();
                var winner = await Task.WhenAny(pending, Task.Delay(remaining, cts.Token));
                if (winner != pending)
                {
                    if (cts.IsCancellationRequested) Finish("cancelled", "Đã huỷ phiên đăng nhập.");
                    continue;
                }

                var context = await pending;
                pending = null;
                await HandleAsync(context, session, onCode, cts.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (State.Status == "waiting") Finish("error", "Phiên đăng nhập bị lỗi.");
        }
        finally
        {
            listener.Close();
            if (pending is not null) { try { await pending; } catch (Exception) { /* listener đã đóng */ } }
            lock (_gate) { _cancel = null; cts.Dispose(); }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, OAuthSession session, OAuthCodeHandler onCode, CancellationToken ct)
    {
        var query = context.Request.QueryString;
        var state = query["state"] ?? "";
        var valid = context.Request.Url?.AbsolutePath == "/callback/" + session.Platform
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(session.State));
        if (!valid)
        {
            await RespondAsync(context, 400, "Invalid login session");
            return;
        }

        string status, message;
        try
        {
            if (query["error"] is not null || string.IsNullOrEmpty(query["code"])) throw new InvalidOperationException("Đăng nhập bị từ chối.");
            await onCode(session, query["code"]!, ct);
            (status, message) = ("connected", "Đã kết nối. Bạn có thể đóng trang này và quay lại ứng dụng.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (status, message) = ("error", ex.Message);
        }

        Finish(status, message);
        await RespondAsync(context, 200, message);
    }

    private static async Task RespondAsync(HttpListenerContext context, int code, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = code;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }
}
