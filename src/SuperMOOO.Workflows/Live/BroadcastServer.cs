using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Workflows.Live;

/// <param name="Kind">"speech" (đọc giọng trên nền video lặp) hoặc "clip" (clip dựng sẵn, có tiếng riêng).</param>
public sealed record LivePlayItem(long Seq, string Kind, string Path, string? Text);

/// <summary>
/// Máy chủ HTTP loopback cho OBS Browser Source: trang broadcast, video nền lặp và hàng phát (câu trả lời/clip). Chỉ chạy khi người dùng bật;
/// chỉ phát đúng những file app đã đăng ký (không đọc đường dẫn tuỳ ý) và từ chối Host lạ.
/// </summary>
[Service]
public sealed class BroadcastServer(ISettingsStore settings, ILogger<BroadcastServer> log) : IAsyncDisposable
{
    private const int MaxQueue = 20;

    private readonly object _gate = new();
    private readonly List<LivePlayItem> _queue = [];
    private readonly Dictionary<string, string> _media = [];
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _seq;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public int QueueLength { get { lock (_gate) return _queue.Count; } }
    public event EventHandler? StateChanged;

    public void Start()
    {
        if (IsRunning) return;
        var port = settings.Get<LiveSettings>("live").Port;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException e)
        {
            listener.Close();
            throw new InvalidOperationException($"Không mở được cổng {port} (đang bị dùng hoặc bị chặn): {e.Message}. Đổi cổng ở tab Phát OBS.");
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        Port = port;
        IsRunning = true;
        _ = Task.Run(() => LoopAsync(listener, _cts.Token));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        var listener = _listener;
        var cts = _cts;
        _listener = null;
        _cts = null;
        if (cts is not null) await cts.CancelAsync();
        try { listener?.Close(); } catch (ObjectDisposedException) { }
        lock (_gate) _queue.Clear();
        cts?.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>URL cho OBS Browser Source theo nền tảng/khung/chế độ hình.</summary>
    public string BuildUrl(string platform, string layout, string fit) =>
        $"http://127.0.0.1:{(IsRunning ? Port : settings.Get<LiveSettings>("live").Port)}/broadcast?platform={Uri.EscapeDataString(platform)}&layout={Uri.EscapeDataString(layout)}&fit={Uri.EscapeDataString(fit)}";

    public void Enqueue(string kind, string path, string? text)
    {
        lock (_gate)
        {
            if (_queue.Count >= MaxQueue) _queue.RemoveAt(0);
            _queue.Add(new LivePlayItem(++_seq, kind, path, text));
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string Register(string path)
    {
        var full = Path.GetFullPath(path);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32].ToLowerInvariant();
        lock (_gate) _media[id] = full;
        return id;
    }

    private async Task LoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var host = ctx.Request.Url?.Host;
            if (host is not ("127.0.0.1" or "localhost")) { Respond(ctx, 403, "text/plain", "Forbidden"u8.ToArray()); return; }
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            var path = ctx.Request.Url!.AbsolutePath;
            var get = ctx.Request.HttpMethod is "GET" or "HEAD";

            if (get && path is "/" or "/broadcast") Respond(ctx, 200, "text/html; charset=utf-8", Resource("broadcast.html"));
            else if (get && path == "/static/broadcast.js") Respond(ctx, 200, "text/javascript; charset=utf-8", Resource("broadcast.js"));
            else if (get && path == "/static/broadcast-config.mjs") Respond(ctx, 200, "text/javascript; charset=utf-8", Resource("broadcast-config.mjs"));
            else if (get && path == "/api/now") Respond(ctx, 200, "application/json; charset=utf-8", Now());
            else if (ctx.Request.HttpMethod == "POST" && path == "/api/done") Done(ctx);
            else if (get && path.StartsWith("/media/", StringComparison.Ordinal)) await ServeMediaAsync(ctx, path[7..], ct);
            else Respond(ctx, 404, "text/plain", "Not found"u8.ToArray());
        }
        catch (Exception e) when (e is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // OBS đóng kết nối giữa chừng (tua video, đổi scene): không phải lỗi.
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Máy chủ phát OBS lỗi khi xử lý {Path}", ctx.Request.Url?.AbsolutePath);
            try { Respond(ctx, 500, "text/plain", "Internal error"u8.ToArray()); } catch (Exception) { }
        }
    }

    private byte[] Now()
    {
        var s = settings.Get<LiveSettings>("live");
        LivePlayItem? head;
        lock (_gate) head = _queue.FirstOrDefault();
        var node = new JsonObject
        {
            ["loop"] = !string.IsNullOrWhiteSpace(s.LoopVideoPath) && File.Exists(s.LoopVideoPath) ? "/media/" + Register(s.LoopVideoPath) : "",
            ["item"] = head is not null && File.Exists(head.Path)
                ? new JsonObject { ["seq"] = head.Seq, ["kind"] = head.Kind, ["url"] = "/media/" + Register(head.Path), ["text"] = head.Text }
                : null
        };
        // File đã bị dọn khỏi cache: bỏ mục để hàng phát không kẹt.
        if (head is not null && !File.Exists(head.Path)) Remove(head.Seq);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private void Done(HttpListenerContext ctx)
    {
        if (long.TryParse(ctx.Request.QueryString["seq"], out var seq)) Remove(seq);
        Respond(ctx, 204, "text/plain", []);
    }

    private void Remove(long seq)
    {
        lock (_gate) _queue.RemoveAll(i => i.Seq == seq);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static byte[] Resource(string name)
    {
        using var stream = typeof(BroadcastServer).Assembly.GetManifestResourceStream("Live.Web." + name)
            ?? throw new InvalidOperationException("Thiếu tài nguyên trang phát: " + name);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Respond(HttpListenerContext ctx, int status, string type, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = status == 204 ? 0 : body.Length;
        if (ctx.Request.HttpMethod != "HEAD" && body.Length > 0) ctx.Response.OutputStream.Write(body);
        ctx.Response.Close();
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        _ => "application/octet-stream"
    };

    private async Task ServeMediaAsync(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        string? file;
        lock (_gate) _media.TryGetValue(id, out file);
        if (file is null || !File.Exists(file)) { Respond(ctx, 404, "text/plain", "Not found"u8.ToArray()); return; }

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        var length = stream.Length;
        long start = 0, end = length - 1;
        var response = ctx.Response;
        response.ContentType = ContentType(file);
        response.Headers["Accept-Ranges"] = "bytes";

        // Trình duyệt cần Range để tua/lặp video; hỗ trợ "bytes=a-b", "bytes=a-" và "bytes=-n".
        var range = ctx.Request.Headers["Range"];
        if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var parts = range[6..].Split('-', 2);
            var ok = true;
            if (parts[0].Length == 0) ok = long.TryParse(parts[1], out var tail) && tail > 0 && (start = Math.Max(0, length - tail)) >= 0;
            else
            {
                ok = long.TryParse(parts[0], out start);
                if (ok && parts.Length > 1 && parts[1].Length > 0) ok = long.TryParse(parts[1], out end);
            }

            end = Math.Min(end, length - 1);
            if (!ok || start > end || start >= length)
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{length}";
                response.Close();
                return;
            }

            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
        }

        var count = end - start + 1;
        response.ContentLength64 = count;
        if (ctx.Request.HttpMethod == "HEAD") { response.Close(); return; }

        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
            if (read == 0) break;
            await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct);
            count -= read;
        }

        response.Close();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
