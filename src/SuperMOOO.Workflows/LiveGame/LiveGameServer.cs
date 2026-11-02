using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Workflows.LiveGame;

[Service]
public sealed partial class LiveGameServer(LiveGameEngine engine, AppPaths paths, ILogger<LiveGameServer> log) : IAsyncDisposable
{
    private const int MaxAudio = 40;
    private const int MaxAvatarMemory = 128;
    private const int MaxAvatarFiles = 2000;
    private const long MaxAvatarDiskBytes = 50L * 1024 * 1024;
    private const int MaxTracks = 300;
    private const long MaxTrackBytes = 200L << 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly ConcurrentDictionary<string, string> _media = new();
    private readonly Queue<string> _mediaOrder = new();
    private readonly Dictionary<string, string> _backgrounds = [];
    private readonly List<string> _tracks = [];
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly SemaphoreSlim _avatarGate = new(4);
    private readonly Dictionary<string, (byte[] Data, string Type)> _avatars = [];
    private readonly Queue<string> _avatarOrder = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ConnectPublicAsync }) { Timeout = TimeSpan.FromSeconds(8) };
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _requestId;
    private int _clients;
    private int _avatarWrites;
    private long _lastSimulation;
    private int _simulationIndex;
    private string _token = "";
    private string _theme = "temple";
    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/?key={_token}";
    /// <summary>URL phát: ẩn bảng giả lập, HUD và nút điều khiển (obs=1) để không lọt lên live.</summary>
    public string ObsUrl => Url + "&obs=1";
    public bool IsRunning => _listener is not null;
    public Dictionary<string, string> BackgroundUrls => _backgrounds.Keys.ToDictionary(k => k, k => $"/background/{k}?key={_token}");
    /// <summary>Nhạc người dùng đặt ở DataRoot\themes\temple\music; phục vụ theo số thứ tự để tên file (có dấu, khoảng trắng) không lọt vào URL.</summary>
    public List<string> MusicUrls => Enumerable.Range(0, _tracks.Count).Select(i => $"/theme/music/{i}?key={_token}").ToList();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}\.(json|png|jpg|webp|mp3|ogg|wav|mp4)$", RegexOptions.CultureInvariant)]
    private static partial Regex ThemeFile();

    public void Start(int port, string theme)
    {
        if (IsRunning) throw new InvalidOperationException("Máy chủ đang chạy.");
        _theme = theme;
        LoadBackgrounds();
        LoadTracks();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch { listener.Close(); throw; }
        Port = port;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _listener = listener;
        _cts = new();
        _loop = ListenAsync(listener, _cts.Token);
    }

    private void LoadBackgrounds()
    {
        _backgrounds.Clear();
        var root = LiveGameTheme.OverrideDir(paths, _theme);
        var manifest = Path.Combine(root, "active.json");
        if (!File.Exists(manifest)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var folder = doc.RootElement.GetProperty("folder").GetString();
            if (string.IsNullOrEmpty(folder) || folder.Length > 80 || folder.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidDataException("Tên thư mục gói nền không hợp lệ.");
            foreach (var mode in new[] { "morning", "afternoon", "night" })
            {
                var file = Path.Combine(root, folder, mode + ".mp4");
                if (File.Exists(file)) _backgrounds[mode] = file;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        { log.LogWarning(e, "Không nạp được gói nền Chùa; dùng cảnh vẽ sẵn"); }
    }

    private void LoadTracks()
    {
        _tracks.Clear();
        var dir = new DirectoryInfo(Path.Combine(LiveGameTheme.OverrideDir(paths, _theme), "music"));
        if (!dir.Exists) return;
        try
        {
            foreach (var file in dir.EnumerateFiles().Where(f => f.Extension.ToLowerInvariant() is ".mp3" or ".ogg" or ".wav")
                .Where(f => f.Length is > 0 and <= MaxTrackBytes && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(MaxTracks))
                _tracks.Add(file.FullName);
            log.LogInformation("Playlist nhạc nền LiveGame: {Count} bài", _tracks.Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning(e, "Không đọc được thư mục nhạc nền; dùng nhạc mặc định của chủ đề"); }
    }

    public string RegisterAudio(string path)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_mediaOrder)
        {
            _media[id] = path;
            _mediaOrder.Enqueue(id);
            // Bỏ đúng file cũ nhất theo thứ tự đăng ký; file vừa thêm (sắp phát) luôn ở cuối hàng.
            while (_mediaOrder.Count > MaxAudio) _media.TryRemove(_mediaOrder.Dequeue(), out _);
        }
        return $"/media/{id}?key={_token}";
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            if (_requests.Count >= 32) { ctx.Response.StatusCode = 503; ctx.Response.Close(); continue; }
            var id = Interlocked.Increment(ref _requestId);
            var task = HandleAsync(ctx, ct);
            _requests[id] = task;
            _ = task.ContinueWith(_ => _requests.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".json" => "application/json; charset=utf-8",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg", ".wav" => "audio/wav", ".mp4" => "video/mp4", _ => "application/octet-stream"
    };

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var request = ctx.Request;
            var response = ctx.Response;
            if (request.Url?.Host != "127.0.0.1" || request.Url.Port != Port || request.QueryString["key"] != _token)
            { response.StatusCode = 403; return; }
            if (request.HttpMethod is not ("GET" or "HEAD" or "POST")) { response.StatusCode = 405; return; }
            var origin = request.Headers["Origin"];
            if (origin is not null && origin != $"http://127.0.0.1:{Port}") { response.StatusCode = 403; return; }
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";
            // PixiJS biên dịch bộ đồng bộ shader bằng Function; mọi script, font, ảnh (kể cả avatar qua proxy) đều từ máy này.
            response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self' ws://127.0.0.1:{Port}; frame-ancestors 'none'; object-src 'none'; base-uri 'none'";
            var path = request.Url.AbsolutePath;
            if (request.HttpMethod == "POST")
            {
                if (path != "/api/live/simulate") { response.StatusCode = 404; return; }
                await SimulateAsync(ctx, ct);
                return;
            }
            if ((path is "/ws" or "/ws/live") && request.IsWebSocketRequest)
            {
                if (Interlocked.Increment(ref _clients) > 4) { Interlocked.Decrement(ref _clients); response.StatusCode = 503; return; }
                try { await SocketAsync(ctx, ct); }
                finally { Interlocked.Decrement(ref _clients); }
                return;
            }
            if (path is "/" or "/static/game.js" or "/static/pixi.min.js")
            {
                var file = path == "/" ? "index.html" : path[8..];
                using var stream = typeof(LiveGameServer).Assembly.GetManifestResourceStream("LiveGame.Web." + file);
                if (stream is null) { response.StatusCode = 404; return; }
                response.ContentType = ContentType(file);
                if (file == "index.html")
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var body = Encoding.UTF8.GetBytes((await reader.ReadToEndAsync(ct)).Replace("__KEY__", _token));
                    response.ContentLength64 = body.Length;
                    if (request.HttpMethod != "HEAD") await response.OutputStream.WriteAsync(body, ct);
                }
                else
                {
                    response.ContentLength64 = stream.Length;
                    if (request.HttpMethod != "HEAD") await stream.CopyToAsync(response.OutputStream, ct);
                }
            }
            else if (path.StartsWith("/theme/", StringComparison.Ordinal)) await ThemeAsync(ctx, path[7..], ct);
            else if (path.StartsWith("/media/", StringComparison.Ordinal) && _media.TryGetValue(path[7..], out var file) && File.Exists(file))
            {
                await MediaAsync(ctx, file, "audio/wav", ct);
            }
            else if (path.StartsWith("/background/", StringComparison.Ordinal) && _backgrounds.TryGetValue(path[12..], out var background))
                await MediaAsync(ctx, background, "video/mp4", ct);
            else if (path is "/avatar" or "/api/live/avatar") await AvatarAsync(ctx, ct);
            else response.StatusCode = 404;
        }
        catch (Exception e) when (e is IOException or WebSocketException or HttpListenerException or OperationCanceledException or ObjectDisposedException)
        { log.LogDebug(e, "Kết nối overlay đã đóng"); }
        catch (Exception e) { log.LogWarning(e, "Lỗi máy chủ LiveGame"); }
        finally { try { ctx.Response.Close(); } catch (ObjectDisposedException) { } }
    }

    private async Task ThemeAsync(HttpListenerContext ctx, string file, CancellationToken ct)
    {
        if (file.StartsWith("music/", StringComparison.Ordinal))
        {
            // Chỉ số trong danh sách đã duyệt lúc bắt đầu phiên; kiểm tra lại vì file có thể bị thay trong lúc phát.
            var track = int.TryParse(file[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < _tracks.Count ? new FileInfo(_tracks[index]) : null;
            if (track is null || !track.Exists || track.Length > MaxTrackBytes || track.Attributes.HasFlag(FileAttributes.ReparsePoint)) { ctx.Response.StatusCode = 404; return; }
            await MediaAsync(ctx, track.FullName, ContentType(track.Name), ct);
            return;
        }
        // Tên phẳng theo whitelist nên không có "..", "/" hay ":"; active.json là chỉ mục gói nền AI, không phải asset.
        if (!ThemeFile().IsMatch(file) || file.Equals("active.json", StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 404; return; }
        var type = ContentType(file);
        var limit = type.StartsWith("application/json", StringComparison.Ordinal) ? 1L << 20 : type.StartsWith("image/", StringComparison.Ordinal) ? 16L << 20 : 200L << 20;
        var root = Path.GetFullPath(LiveGameTheme.OverrideDir(paths, _theme));
        var custom = new FileInfo(Path.Combine(root, file));
        if (custom.Exists && string.Equals(custom.DirectoryName, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            // Liên kết tượng trưng có thể trỏ ra ngoài thư mục chủ đề; chỉ phục vụ file thật.
            if (custom.Length <= limit && !custom.Attributes.HasFlag(FileAttributes.ReparsePoint)) { await MediaAsync(ctx, custom.FullName, type, ct); return; }
            log.LogWarning("Bỏ qua asset chủ đề ghi đè {File}: quá {Limit} MB hoặc là liên kết", custom.Name, limit >> 20);
        }
        await using var stream = LiveGameTheme.OpenResource(_theme, file);
        if (stream is null) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = stream.Length;
        if (ctx.Request.HttpMethod != "HEAD") await stream.CopyToAsync(ctx.Response.OutputStream, ct);
    }

    private static async Task MediaAsync(HttpListenerContext ctx, string file, string type, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        var response = ctx.Response;
        response.ContentType = type;
        response.Headers["Accept-Ranges"] = "bytes";
        long start = 0, end = stream.Length - 1;
        if (ctx.Request.Headers["Range"] is { } range)
        {
            var parts = range.StartsWith("bytes=", StringComparison.Ordinal) ? range[6..].Split('-', 2) : [];
            var valid = parts.Length == 2;
            if (valid && parts[0].Length == 0)
            {
                valid = long.TryParse(parts[1], out var tail) && tail > 0;
                if (valid) start = Math.Max(0, stream.Length - tail);
            }
            else if (valid)
            {
                valid = long.TryParse(parts[0], out start) && start >= 0;
                if (valid && parts[1].Length > 0) valid = long.TryParse(parts[1], out end);
            }
            end = Math.Min(end, stream.Length - 1);
            if (!valid || start > end || start >= stream.Length)
            { response.StatusCode = 416; response.Headers["Content-Range"] = $"bytes */{stream.Length}"; return; }
            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{stream.Length}";
        }
        var left = Math.Max(0, end - start + 1);
        response.ContentLength64 = left;
        if (ctx.Request.HttpMethod == "HEAD") return;
        stream.Position = start;
        var buffer = new byte[81920];
        while (left > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (read == 0) break;
            await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct); left -= read;
        }
    }

    /// <summary>Trạng thái đã gửi cho một trang: lần đầu (hoặc đổi phiên) gửi đủ, sau đó chỉ gửi phần thay đổi.</summary>
    private sealed class ClientState
    {
        public string Session = "";
        public long ViewersVersion = -1;
        public readonly Dictionary<string, LiveGameViewer> Viewers = [];
        public long EffectSeq;
        public long AudioSeq;
        public byte[] Head = [];
        public long SentAt;
    }

    private static object ViewerDto(LiveGameViewer v) => new { id = v.Id, name = v.Name, avatar = v.Avatar, gold = v.Gold, title = v.Title, seen = v.LastSeen.ToUnixTimeMilliseconds() };

    private static object HeadDto(LiveGameSnapshot s) => new
    {
        status = s.Status, viewerCount = s.ViewerCount, incense = s.Incense, prayers = s.Prayers, likes = s.Likes, top = s.Top,
        calendar = s.Calendar, lighting = s.Lighting, holiday = s.Holiday, notice = s.Notice
    };

    private static byte[]? Message(LiveGameSnapshot s, ClientState c)
    {
        var head = JsonSerializer.SerializeToUtf8Bytes(HeadDto(s), Json);
        if (c.Session != s.Session)
        {
            c.Session = s.Session; c.ViewersVersion = s.ViewersVersion; c.Head = head;
            c.Viewers.Clear();
            foreach (var v in s.Viewers) c.Viewers[v.Id] = v;
            c.EffectSeq = s.Effects.Count > 0 ? s.Effects[^1].Seq : 0;
            c.AudioSeq = s.Audio?.Seq ?? 0;
            // Chỉ gửi cấu hình trang cần; không gửi danh sách từ cấm hay cổng TikFinity.
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                t = "full", session = s.Session, simulated = s.Simulated, head = HeadDto(s),
                settings = new { fps = s.Settings.Fps, volume = s.Settings.Volume, giftRules = s.Settings.GiftRules },
                backgrounds = s.Theme.Backgrounds, playlist = s.Theme.Playlist, viewers = s.Viewers.Select(ViewerDto), effects = s.Effects, audio = s.Audio
            }, Json);
        }
        object? viewers = null;
        if (s.ViewersVersion != c.ViewersVersion)
        {
            c.ViewersVersion = s.ViewersVersion;
            var upsert = new List<object>();
            var touch = new List<object[]>();
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in s.Viewers)
            {
                present.Add(v.Id);
                if (!c.Viewers.TryGetValue(v.Id, out var old) || old.Name != v.Name || old.Avatar != v.Avatar || old.Gold != v.Gold || old.Title != v.Title) upsert.Add(ViewerDto(v));
                else if (old.LastSeen != v.LastSeen) touch.Add([v.Id, v.LastSeen.ToUnixTimeMilliseconds()]);
                c.Viewers[v.Id] = v;
            }
            var remove = c.Viewers.Keys.Where(id => !present.Contains(id)).ToList();
            foreach (var id in remove) c.Viewers.Remove(id);
            if (upsert.Count + touch.Count + remove.Count > 0)
                viewers = new { upsert = upsert.Count > 0 ? upsert : null, touch = touch.Count > 0 ? touch : null, remove = remove.Count > 0 ? remove : null };
        }
        var effects = s.Effects.Where(e => e.Seq > c.EffectSeq).ToList();
        if (effects.Count > 0) c.EffectSeq = effects[^1].Seq;
        var audio = s.Audio is { } a && a.Seq != c.AudioSeq ? a : null;
        if (audio is not null) c.AudioSeq = audio.Seq;
        var headChanged = !head.AsSpan().SequenceEqual(c.Head);
        if (headChanged) c.Head = head;
        if (!headChanged && viewers is null && effects.Count == 0 && audio is null) return null;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            t = "delta", head = headChanged ? HeadDto(s) : null, viewers, effects = effects.Count > 0 ? effects : null, audio
        }, Json);
    }

    private async Task SocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = ReceiveAsync(socket, linked.Token);
        var client = new ClientState();
        var ping = "{\"t\":\"ping\"}"u8.ToArray();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var data = Message(engine.Snapshot(), client);
                // Không có gì đổi thì vẫn gửi nhịp 15 giây để watchdog của trang (60 giây) không tải lại oan.
                if (data is null && Environment.TickCount64 - client.SentAt >= 15000) data = ping;
                if (data is not null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(data, WebSocketMessageType.Text, true, timeout.Token);
                    client.SentAt = Environment.TickCount64;
                }
                await Task.Delay(500, ct);
            }
        }
        finally
        {
            await linked.CancelAsync(); socket.Abort();
            try { await receive; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        }
    }

    private static async Task ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            // Overlay chỉ nhận trạng thái, không có lệnh điều khiển từ web.
            if (!result.EndOfMessage || result.Count > 0) { socket.Abort(); return; }
        }
    }

    private async Task SimulateAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var snapshot = engine.Snapshot();
        if (!snapshot.Simulated) { ctx.Response.StatusCode = 409; return; }
        if (ctx.Request.ContentType?.Split(';')[0].Trim() != "application/json" || ctx.Request.ContentLength64 is < 1 or > 4096)
        { ctx.Response.StatusCode = 400; return; }
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastSimulation);
        if (now - last < 250 || Interlocked.CompareExchange(ref _lastSimulation, now, last) != last)
        { ctx.Response.StatusCode = 429; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var body = new byte[(int)ctx.Request.ContentLength64];
        await ctx.Request.InputStream.ReadExactlyAsync(body, timeout.Token);
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out var actionNode) || actionNode.ValueKind != JsonValueKind.String)
            { ctx.Response.StatusCode = 400; return; }
            var action = actionNode.GetString();
            var kind = action switch
            {
                "member" => LiveEventKind.Member, "chat" or "custom_chat" => LiveEventKind.Chat,
                "like" => LiveEventKind.Like, "follow" => LiveEventKind.Follow,
                "gift_incense" or "gift_bow" or "gift_lotus" or "gift_big" or "storm" => LiveEventKind.Gift,
                _ => (LiveEventKind?)null
            };
            if (kind is null) { ctx.Response.StatusCode = 400; return; }
            var giftAction = action switch { "gift_incense" => "incense", "gift_bow" => "bow", "gift_lotus" => "lotus", _ => "festival" };
            var rule = snapshot.Settings.GiftRules.FirstOrDefault(r => r.Action == giftAction);
            for (var i = 0; i < (action == "storm" ? 100 : 1); i++)
            {
                var e = SimulatedEventSource.Create(kind.Value, Interlocked.Increment(ref _simulationIndex));
                if (kind == LiveEventKind.Gift && action != "storm") e = e with { Coins = rule?.MinCoins ?? 1 };
                if (action == "custom_chat")
                {
                    if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 160)
                    { ctx.Response.StatusCode = 400; return; }
                    e = e with { Text = text.GetString()!, Nickname = "Tôi" };
                }
                if (!engine.TrySimulate(e)) { ctx.Response.StatusCode = 429; return; }
            }
            ctx.Response.StatusCode = 204;
        }
        catch (JsonException) { ctx.Response.StatusCode = 400; }
    }

    private string AvatarDir => paths.CacheDir("livegame_avatars");

    private async Task AvatarAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (!Uri.TryCreate(ctx.Request.QueryString["u"], UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length > 0 ||
            !new[] { "tiktokcdn.com", "tiktokcdn-us.com", "tiktokcdn-eu.com", "tiktokcdn-eu.net", "byteoversea.com", "ibytedtos.com" }.Any(d => uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase) || uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase)))
        { ctx.Response.StatusCode = 403; return; }
        await _avatarGate.WaitAsync(ct);
        try
        {
            (byte[] Data, string Type) image;
            lock (_avatars) _avatars.TryGetValue(uri.AbsoluteUri, out image);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..40];
            if (image.Data is null) image = ReadAvatar(hash);
            if (image.Data is null)
            {
                using var result = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                var type = result.Content.Headers.ContentType?.MediaType;
                if (!result.IsSuccessStatusCode || type is not ("image/jpeg" or "image/png" or "image/webp") || result.Content.Headers.ContentLength > 262144)
                { ctx.Response.StatusCode = 404; return; }
                await using var input = await result.Content.ReadAsStreamAsync(ct);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (output.Length + read > 262144) { ctx.Response.StatusCode = 413; return; }
                    output.Write(buffer, 0, read);
                }
                image = (output.ToArray(), type);
                WriteAvatar(hash, image);
            }
            lock (_avatars)
            {
                if (_avatars.TryAdd(uri.AbsoluteUri, image))
                {
                    _avatarOrder.Enqueue(uri.AbsoluteUri);
                    while (_avatarOrder.Count > MaxAvatarMemory) _avatars.Remove(_avatarOrder.Dequeue());
                }
            }
            ctx.Response.ContentType = image.Type;
            ctx.Response.ContentLength64 = image.Data.Length;
            if (ctx.Request.HttpMethod != "HEAD") await ctx.Response.OutputStream.WriteAsync(image.Data, ct);
        }
        catch (HttpRequestException e) { log.LogDebug(e, "Không tải được avatar TikTok"); ctx.Response.StatusCode = 502; }
        finally { _avatarGate.Release(); }
    }

    private static readonly (string Ext, string Type)[] AvatarTypes = [(".jpg", "image/jpeg"), (".png", "image/png"), (".webp", "image/webp")];

    private (byte[] Data, string Type) ReadAvatar(string hash)
    {
        try
        {
            foreach (var (ext, type) in AvatarTypes)
            {
                var file = new FileInfo(Path.Combine(AvatarDir, hash + ext));
                if (!file.Exists || file.Length is 0 or > 262144) continue;
                var data = File.ReadAllBytes(file.FullName);
                // Đánh dấu vừa dùng để lúc dọn hạn mức xoá ảnh lâu không ai xem trước.
                file.LastWriteTimeUtc = DateTime.UtcNow;
                return (data, type);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogDebug(e, "Không đọc được avatar trong cache đĩa"); }
        return default;
    }

    private void WriteAvatar(string hash, (byte[] Data, string Type) image)
    {
        try
        {
            Directory.CreateDirectory(AvatarDir);
            var file = Path.Combine(AvatarDir, hash + AvatarTypes.First(t => t.Type == image.Type).Ext);
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, image.Data);
            File.Move(temp, file, true);
            if (Interlocked.Increment(ref _avatarWrites) % 25 == 1) TrimAvatars();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogDebug(e, "Không ghi được avatar vào cache đĩa"); }
    }

    private void TrimAvatars()
    {
        var files = new DirectoryInfo(AvatarDir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
        var size = files.Sum(f => f.Length);
        var count = files.Count;
        foreach (var file in files)
        {
            if (count <= MaxAvatarFiles && size <= MaxAvatarDiskBytes) break;
            try { file.Delete(); count--; size -= file.Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogDebug(e, "Không xoá được avatar cũ trong cache"); }
        }
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPublic(a))
            ?? throw new HttpRequestException("CDN không có địa chỉ công khai hợp lệ.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }

    private static bool IsPublic(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync(); _listener?.Close(); _listener = null;
        if (_loop is not null) await _loop;
        await Task.WhenAll(_requests.Values);
        _cts.Dispose(); _cts = null;
        lock (_mediaOrder) { _media.Clear(); _mediaOrder.Clear(); }
        lock (_avatars) { _avatars.Clear(); _avatarOrder.Clear(); }
        _tracks.Clear();
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _http.Dispose(); _avatarGate.Dispose(); }
}
