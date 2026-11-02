using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Numerics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuperMOOO.AI.Audio;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Http;
using SuperMOOO.AI.Translation;

namespace SuperMOOO.AI.Tts;

public sealed record EdgeFrame(bool IsBinary, byte[] Data);

/// <summary>Một phiên WebSocket tới endpoint Edge; tách ra để test timeout mà không cần mạng.</summary>
public interface IEdgeConnection : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken ct);
    Task<EdgeFrame> ReceiveAsync(CancellationToken ct);
}

/// <summary>Edge TTS (endpoint không chính thức, tuỳ chọn, mặc định tắt). Xuất WAV 24 kHz; có timeout theo khung và tổng (F08).</summary>
/// <remarks>
/// Đây là endpoint "Đọc to" nội bộ của trình duyệt Edge, Microsoft không công bố và có thể đổi bất kỳ lúc nào.
/// Token Sec-MS-GEC tự viết theo cách web client của Edge tính (đối chiếu token bắt được từ trình duyệt, không chép mã nguồn khác):
/// thời gian FILETIME làm tròn xuống 5 phút + TrustedClientToken; từ 10/2026 bắt buộc bản v2 (thêm chuỗi bản quyền làm tiền tố, CS64 + SHA-256).
/// </remarks>
[Service(As = [typeof(ITtsProvider)])]
public sealed class EdgeTtsProvider : ITtsProvider
{
    private const string WssBase = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const string VoiceListUrl = "https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";
    private const string GecSalt = "Copyright (C) Microsoft Corporation. All rights reserved.";
    private const string LocalVoiceHint = " Hãy chọn giọng local (VieNeu cho tiếng Việt, Supertonic cho ja/ko/en, OmniVoice cho zh) hoặc thử lại sau.";
    private const int MaxChars = 3000;
    private const int SampleRate = 24000;

    private static readonly VoiceInfo[] Fallback =
    [
        new("edge:vi-VN-HoaiMyNeural", VoiceIds.Edge, "Hoài My · Nữ", "vi-VN", "female"),
        new("edge:vi-VN-NamMinhNeural", VoiceIds.Edge, "Nam Minh · Nam", "vi-VN", "male"),
        new("edge:en-US-JennyNeural", VoiceIds.Edge, "Jenny · Nữ", "en-US", "female"),
        new("edge:en-US-GuyNeural", VoiceIds.Edge, "Guy · Nam", "en-US", "male"),
        new("edge:en-US-AriaNeural", VoiceIds.Edge, "Aria · Nữ", "en-US", "female")
    ];

    private readonly ISettingsStore _settings;
    private readonly IHttpClientProvider _http;
    private readonly WavPreparer _wav;
    private readonly INetworkRouter? _router;
    private readonly Func<Uri, CancellationToken, Task<IEdgeConnection>> _connect;
    private readonly SemaphoreSlim _spacing = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private long _clockSkewTicks;           // lệch giờ máy so với server, chỉnh theo header Date khi bị từ chối
    private IReadOnlyList<VoiceInfo>? _voices;

    public EdgeTtsProvider(ISettingsStore settings, IHttpClientProvider http, WavPreparer wav, INetworkRouter router)
        : this(settings, http, wav, null, router) { }

    internal EdgeTtsProvider(ISettingsStore settings, IHttpClientProvider http, WavPreparer wav,
        Func<Uri, CancellationToken, Task<IEdgeConnection>>? connect, INetworkRouter? router = null)
    {
        _settings = settings;
        _http = http;
        _wav = wav;
        _router = router;
        _connect = connect ?? ConnectWebSocketAsync;
    }

    internal TimeSpan FrameTimeout { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public string Id => VoiceIds.Edge;
    public TtsCapabilities Capabilities => new(true, true, true, false, MaxChars);

    public async Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language, CancellationToken ct)
    {
        var voices = _voices ??= await LoadVoicesAsync(ct);
        if (language is null || LanguageCatalog.LocaleCode(language) == "auto") return voices;
        var primary = LanguageCatalog.LocaleCode(language).Split('-')[0];
        return voices.Where(v => v.Language.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<IReadOnlyList<VoiceInfo>> LoadVoicesAsync(CancellationToken ct)
    {
        var settings = _settings.Get<TtsEdgeSettings>("tts.edge");
        try
        {
            string json;
            try { json = await FetchVoiceListAsync(settings, ct); }
            catch (ProviderException e) when (e.Kind is ProviderErrorKind.Forbidden or ProviderErrorKind.BadResponse)
            {
                if (!await SyncClockAsync(ct)) throw;
                json = await FetchVoiceListAsync(settings, ct);
            }
            var list = JsonNode.Parse(json) as JsonArray;
            var voices = list?.OfType<JsonObject>().Select(v => new VoiceInfo("edge:" + v["ShortName"]!.GetValue<string>(), Id,
                    (v["FriendlyName"]?.GetValue<string>() ?? v["ShortName"]!.GetValue<string>()), v["Locale"]?.GetValue<string>() ?? "auto",
                    v["Gender"]?.GetValue<string>()?.ToLowerInvariant())).ToList();
            return voices is { Count: > 0 } ? voices : Fallback;
        }
        catch (Exception e) when (e is ProviderException or JsonException or NullReferenceException or InvalidOperationException)
        {
            return Fallback;       // endpoint danh sách có thể bị chặn; 5 giọng cố định vẫn dùng được
        }
    }

    private async Task<string> FetchVoiceListAsync(TtsEdgeSettings settings, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{VoiceListUrl}?trustedclienttoken={settings.TrustedClientToken}{GecQuery(settings)}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Cookie", MuidCookie());
        return await AiHttp.SendTextAsync(_http, Id, request, TimeSpan.FromSeconds(15), ct);
    }

    private static string MuidCookie() => $"muid={Convert.ToHexString(RandomNumberGenerator.GetBytes(16))};";

    // Lấy giờ server qua header Date (không cần token) để sửa lệch đồng hồ máy.
    private async Task<bool> SyncClockAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, VoiceListUrl);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.Get(AiHttp.ClientName).SendAsync(request, timeout.Token);
            return AdjustClock(response.Headers.Date);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool AdjustClock(DateTimeOffset? serverNow)
    {
        if (serverNow is null) return false;
        Interlocked.Exchange(ref _clockSkewTicks, (serverNow.Value - DateTimeOffset.UtcNow).Ticks);
        return true;
    }

    private string GecQuery(TtsEdgeSettings settings) =>
        $"&Sec-MS-GEC={SecMsGec(DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _clockSkewTicks)), settings.TrustedClientToken, settings.SecMsGecVersion)}" +
        $"&Sec-MS-GEC-Version={Uri.EscapeDataString(settings.SecMsGecVersion)}";

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct)
    {
        var settings = _settings.Get<TtsEdgeSettings>("tts.edge");
        if (!settings.Enabled)
            throw new ProviderException(Id, ProviderErrorKind.Forbidden, "Edge TTS đang tắt. Bật ở Cài đặt → Model AI (endpoint không chính thức, có thể bị chặn).");
        if (!request.VoiceId.StartsWith(VoiceIds.EdgePrefix, StringComparison.Ordinal))
            throw new ArgumentException("Giọng Edge phải có dạng edge:<tên>, ví dụ edge:vi-VN-HoaiMyNeural.");
        var text = request.Text.Trim();
        if (text.Length == 0) throw new ArgumentException("Nội dung không được để trống.");

        var voice = request.VoiceId[VoiceIds.EdgePrefix.Length..];
        var locale = request.Language is not null && LanguageCatalog.LocaleCode(request.Language) != "auto"
            ? LanguageCatalog.LocaleCode(request.Language) : string.Join('-', voice.Split('-').Take(2));

        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TotalTimeout);
        var temporary = request.OutputPath + "." + Guid.NewGuid().ToString("N") + ".mp3";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
        try
        {
            // Endpoint Edge chỉ còn trả MP3/Opus (PCM bị từ chối "Unsupported Edge output format"); khung MP3 nối thẳng được rồi đổi sang WAV.
            await using (var mp3 = File.Create(temporary))
            {
                foreach (var chunk in TextChunker.Split(text, MaxChars))
                {
                    await WaitSpacingAsync(settings.MinIntervalMs, total.Token);
                    await mp3.WriteAsync(await SynthesizeChunkAsync(chunk, voice, locale, request.Rate, request.PitchSemitones, settings, total.Token), total.Token);
                }
            }

            using (var wav = await _wav.PrepareAsync(temporary, SampleRate, total.Token))
                File.Move(wav.Path, request.OutputPath, overwrite: true);
            var (duration, rate) = WavFile.DescribeFile(request.OutputPath);
            return new TtsResult(request.OutputPath, duration, rate);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(Id, ProviderErrorKind.Timeout, $"Edge TTS quá thời gian ({TotalTimeout.TotalSeconds:0}s tổng, {FrameTimeout.TotalSeconds:0}s mỗi khung).");
        }
        finally { File.Delete(temporary); }
    }

    private async Task WaitSpacingAsync(int minIntervalMs, CancellationToken ct)
    {
        await _spacing.WaitAsync(ct);
        try
        {
            var wait = _lastRequest.AddMilliseconds(minIntervalMs) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTime.UtcNow;
        }
        finally { _spacing.Release(); }
    }

    private async Task<byte[]> SynthesizeChunkAsync(string text, string voice, string locale, double rate, double pitchSt,
        TtsEdgeSettings settings, CancellationToken ct)
    {
        IEdgeConnection? connection = null;
        for (var attempt = 0; connection is null; attempt++)
        {
            var skew = Interlocked.Read(ref _clockSkewTicks);
            var url = new Uri($"{WssBase}?TrustedClientToken={settings.TrustedClientToken}{GecQuery(settings)}&ConnectionId={Guid.NewGuid():N}");
            try { connection = await _connect(url, ct); }
            catch (WebSocketException) when (attempt == 0 && Interlocked.Read(ref _clockSkewTicks) != skew)
            {
                // Token gắn với cửa sổ 5 phút: đồng hồ máy lệch thì server từ chối; đã chỉnh theo Date của server, thử lại một lần.
                await WaitSpacingAsync(settings.MinIntervalMs, ct);
            }
            catch (WebSocketException e)
            {
                throw new ProviderException(Id, ProviderErrorKind.Forbidden,
                    $"Không kết nối được Edge TTS ({e.Message}). Microsoft có thể đã chặn hoặc đổi giao thức của endpoint không chính thức này." + LocalVoiceHint);
            }
        }

        await using (connection)
        {
            await connection.SendAsync("Content-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}", ct);
            await connection.SendAsync($"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nPath:ssml\r\n\r\n{BuildSsml(text, voice, locale, rate, pitchSt)}", ct);

            using var audio = new MemoryStream();
            while (true)
            {
                using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                frameCts.CancelAfter(FrameTimeout);
                EdgeFrame frame;
                try { frame = await connection.ReceiveAsync(frameCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ProviderException(Id, ProviderErrorKind.Timeout, $"Edge TTS không phản hồi trong {FrameTimeout.TotalSeconds:0}s.");
                }

                if (frame.IsBinary)
                {
                    // 2 byte đầu = độ dài header (big-endian), phần sau header là audio.
                    if (frame.Data.Length > 2)
                    {
                        var start = 2 + ((frame.Data[0] << 8) | frame.Data[1]);
                        if (frame.Data.Length > start) audio.Write(frame.Data, start, frame.Data.Length - start);
                    }
                }
                else if (Encoding.UTF8.GetString(frame.Data).Contains("Path:turn.end", StringComparison.Ordinal)) break;
            }

            if (audio.Length < 100) throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Edge TTS không trả audio.");
            return audio.ToArray();
        }
    }

    internal static string BuildSsml(string text, string voice, string locale, double rate, double pitchSemitones)
    {
        var ratePercent = (int)Math.Round((rate - 1.0) * 100);
        var pitch = (int)Math.Round(pitchSemitones);
        return $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{SecurityElement.Escape(locale)}'>" +
               $"<voice name='{SecurityElement.Escape(voice)}'><prosody pitch='{pitch:+0;-0;+0}st' rate='{ratePercent:+0;-0;+0}%'>{SecurityElement.Escape(text)}</prosody></voice></speak>";
    }

    // Token theo cửa sổ 5 phút (giờ Windows FILETIME) cùng TrustedClientToken.
    // v1: SHA-256 của chuỗi đó. v2: thêm GecSalt làm tiền tố, trả "<CS64 16 hex>-<SHA-256 64 hex>".
    internal static string SecMsGec(DateTimeOffset now, string token, string version)
    {
        var ticks = now.ToUnixTimeSeconds() + 11644473600L;
        ticks -= ticks % 300;
        if (version.StartsWith("1-", StringComparison.Ordinal))
            return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"{ticks * 10_000_000L}{token}")));

        var data = Encoding.ASCII.GetBytes($"{GecSalt}{ticks * 10_000_000L}{token}");
        var digest = SHA256.HashData(data);
        return Cs64(data, digest) + "-" + Convert.ToHexString(digest);
    }

    // Checksum 64-bit của web client: duyệt từng cặp từ 32-bit little-endian (đệm 0 tới bội 8 byte), khoá = 8 byte đầu của SHA-256 (ép lẻ).
    private static string Cs64(byte[] data, byte[] digest)
    {
        var padded = new byte[(data.Length + 7) / 8 * 8];
        data.CopyTo(padded, 0);
        var keyA = BinaryPrimitives.ReadUInt32LittleEndian(digest) | 1;
        var keyB = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4)) | 1;
        uint mix = 0, total = 0, swap = 0, last = 0;
        for (var i = 0; i < padded.Length; i += 8)
        {
            var lo = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i));
            var hi = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i + 4));
            uint x = lo * keyA, y = hi * keyB;

            var q = Rol16(Rol16(lo) * 0xEF0569FB + x) * 0x76F50F34;
            swap += Rol16(q) * 0xC3EFEA97 + q;
            q = Rol16(Rol16(hi) * 0xC31713DB + y) * 0x1881CE3C;
            last = Rol16(q) * 0x35BD1EC9 + q;
            swap += last;

            mix = Rol16(mix * keyA + x) * 0xCF98B111;
            mix = Rol16(mix) * 0x87085B9F;
            mix = Rol16(mix) * 0x12CEB96D;
            mix = Rol16(mix) * 0x257E1D83;
            total += mix;
            mix = Rol16(mix * keyB + y) * 0xA27416F5;
            mix = Rol16(mix) * 0xD38396FF;
            mix = Rol16(mix) * 0x7C932B89;
            mix = Rol16(mix) * 0xBFA49F69;
            total += mix;
        }

        var result = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(result, last ^ mix);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), swap ^ total);
        return Convert.ToHexString(result);
    }

    private static uint Rol16(uint value) => BitOperations.RotateLeft(value, 16);

    private async Task<IEdgeConnection> ConnectWebSocketAsync(Uri url, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Pragma", "no-cache");
        socket.Options.SetRequestHeader("Cache-Control", "no-cache");
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        socket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        socket.Options.SetRequestHeader("Cookie", MuidCookie());
        if (_router?.ProxyUriForTools() is { Length: > 0 } proxy) socket.Options.Proxy = new WebProxy(proxy);
        try { await socket.ConnectAsync(url, ct); }
        catch (WebSocketException)
        {
            if (socket.HttpStatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest
                && socket.HttpResponseHeaders?.TryGetValue("Date", out var date) == true
                && DateTimeOffset.TryParseExact(date.FirstOrDefault(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var serverNow))
                AdjustClock(serverNow);
            socket.Dispose();
            throw;
        }
        return new WebSocketConnection(socket);
    }

    private sealed class WebSocketConnection(ClientWebSocket socket) : IEdgeConnection
    {
        public Task SendAsync(string text, CancellationToken ct) =>
            socket.SendAsync((ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).AsTask();

        public async Task<EdgeFrame> ReceiveAsync(CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new ProviderException(VoiceIds.Edge, ProviderErrorKind.Network, "Edge TTS đóng kết nối giữa chừng.");
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return new EdgeFrame(result.MessageType == WebSocketMessageType.Binary, ms.ToArray());
        }

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
