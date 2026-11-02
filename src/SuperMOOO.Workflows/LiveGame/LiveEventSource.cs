using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Workflows.LiveGame;

public interface ILiveEventSource
{
    Task RunAsync(Func<LiveEvent, CancellationToken, ValueTask> receive, Action<string> status, CancellationToken ct);
}

public sealed partial class TikFinityEventSource(LiveGameSettings settings, ILogger log) : ILiveEventSource
{
    private static long _generated;
    private int _attempt;

    public async Task RunAsync(Func<LiveEvent, CancellationToken, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        var dedupe = new LiveEventDeduper();
        var idle = TimeSpan.FromMinutes(Math.Clamp(settings.IdleReconnectMinutes, 1, 120));
        var delay = 1;
        while (!ct.IsCancellationRequested)
        {
            // Mỗi lần nối có số thứ tự và token riêng; socket cũ bị huỷ hẳn trước khi mở socket mới nên không có hai kết nối cùng đẩy sự kiện.
            var attempt = ++_attempt;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var connected = false;
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(new Uri(settings.TikFinityUrl), connect.Token);
                }
                connected = true;
                status("Đã kết nối TikFinity");
                log.LogInformation("Đã kết nối TikFinity (lần nối {Attempt})", attempt);
                // Watchdog: WebSocket local có thể "treo" mà không đóng (TikFinity mất phòng live); không có gói nào quá N phút thì tự nối lại.
                connection.CancelAfter(idle);
                var buffer = new byte[8192];
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, connection.Token);
                        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("TikFinity đã đóng kết nối.");
                        if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 262144)
                            throw new IOException("Gói TikFinity không hợp lệ hoặc quá lớn.");
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    connection.CancelAfter(idle);
                    delay = 1;

                    var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    if (settings.DiagnosticLogging) log.LogInformation("TikFinity JSON: {Json}", json);
                    List<LiveEvent> events;
                    try { events = Parse(json); }
                    catch (JsonException ex) { log.LogWarning(ex, "Bỏ gói TikFinity JSON không hợp lệ"); continue; }
                    var now = Environment.TickCount64;
                    foreach (var item in events)
                    {
                        if (dedupe.IsDuplicate(item, now)) continue;
                        var e = item.MessageId.Length > 0 ? item : item with { MessageId = "tf-" + Interlocked.Increment(ref _generated) };
                        await receive(e, ct);
                    }
                    connection.CancelAfter(idle);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (connected && connection.IsCancellationRequested)
            {
                log.LogWarning("TikFinity im lặng quá {Minutes} phút (lần nối {Attempt}); đóng và nối lại", idle.TotalMinutes, attempt);
                status($"Mất kết nối TikFinity · không nhận gói nào trong {idle.TotalMinutes:0} phút, đang nối lại");
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
                log.LogWarning(ex, "Mất kết nối TikFinity (lần nối {Attempt}); thử lại sau {Delay}s", attempt, delay);
                status($"Mất kết nối TikFinity · thử lại sau {delay} giây");
            }
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            delay = Math.Min(30, delay * 2);
        }
    }

    /// <summary>
    /// TikFinity chuyển tiếp nhiều phiên bản payload: có/không bọc "data", gói đơn hoặc mảng, tên trường camelCase hoặc snake_case,
    /// người dùng nằm ở data.user / userData / author hoặc ngay trong data. Id người dùng giữ nguyên chuỗi (số 64-bit).
    /// </summary>
    public static List<LiveEvent> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        var list = new List<LiveEvent>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray().Take(100))
                if (ParseOne(item) is { } e) list.Add(e);
        }
        else if (ParseOne(root) is { } one) list.Add(one);
        return list;
    }

    private static LiveEvent? ParseOne(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        var data = Child(raw, "data");
        var wrapped = data is not null;
        var d = data ?? raw;
        var name = EventName(raw);
        if (name.Length == 0 && wrapped) name = EventName(d);
        var kind = name switch
        {
            "chat" or "comment" or "message" => LiveEventKind.Chat,
            "member" or "join" or "viewerjoin" => LiveEventKind.Member,
            "gift" => LiveEventKind.Gift, "like" => LiveEventKind.Like,
            "follow" => LiveEventKind.Follow, "share" => LiveEventKind.Share,
            "subscribe" or "sub" => LiveEventKind.Subscribe,
            // Bản cũ gửi theo dõi/chia sẻ dưới dạng "social", phân biệt bằng displayType.
            "social" => Text(d, "displayType", "label").ToLowerInvariant() switch
            {
                var t when t.Contains("follow") => LiveEventKind.Follow,
                var t when t.Contains("share") => LiveEventKind.Share,
                _ => (LiveEventKind?)null
            },
            "roomuser" or "roomstats" or "viewercount" => LiveEventKind.ViewerCount,
            _ => (LiveEventKind?)null
        };
        if (kind is null) return null;

        // Khi user nằm ngay trong data thì không đọc "id" (đó có thể là id gói hay id quà).
        var nested = Child(d, "user") ?? Child(d, "userData") ?? Child(d, "author");
        var user = nested ?? d;
        var uniqueId = First(Text(user, "uniqueId", "unique_id", "username"), Text(d, "uniqueId", "unique_id", "username"));
        var nickname = First(Text(user, "nickname", "displayName", "display_name"), nested is not null ? Text(user, "name") : "", Text(d, "nickname"), uniqueId);
        var userId = First(Text(user, "userId", "user_id"), nested is not null ? Text(user, "id") : "", Text(d, "userId", "user_id"), uniqueId, nickname);
        if (userId.Length == 0 && kind != LiveEventKind.ViewerCount) return null;
        var messageId = First(Text(d, "msgId", "msg_id", "messageId", "eventId"), Text(raw, "msgId", "eventId"), wrapped ? Text(raw, "id") : "");

        string giftId = "", giftName = "", giftImage = "";
        int coins = 0, repeat = 1;
        var ended = true;
        if (kind == LiveEventKind.Gift)
        {
            var gift = Child(d, "gift") ?? Child(d, "giftDetails") ?? Child(d, "extendedGiftInfo");
            giftId = First(Text(d, "giftId", "gift_id"), Text(gift, "id", "giftId", "gift_id"));
            giftName = First(Text(d, "giftName", "gift_name"), Text(gift, "giftName", "name"));
            giftImage = Https(Candidates(d, "giftPictureUrl").Concat(Candidates(gift, "giftPictureUrl", "imageUrl", "pictureUrl"))
                .Concat(Candidates(Child(gift, "giftImage"), "url", "giftPictureUrl")).Concat(Candidates(Child(gift, "image"), "url", "url_list"))
                .Concat(Candidates(Child(Child(d, "extendedGiftInfo"), "image"), "url", "url_list")), preferJpeg: false);
            repeat = (int)Math.Clamp(Number(d, "repeatCount", "repeat_count", "count") ?? 1, 1, 100000);
            var unit = Number(gift, "diamondCount", "diamond_count") ?? Number(d, "diamondCount", "diamond_count", "coins");
            if (unit is null && Number(d, "totalDiamondCount", "giftValue", "totalCoins") is { } total) unit = total / repeat;
            coins = (int)Math.Clamp(unit ?? 0, 0, 1000000);
            // Combo (giftType 1) gửi nhiều bản cập nhật repeatEnd=false rồi một bản kết thúc; thiếu repeatEnd coi như đã kết thúc.
            var streakable = (Number(d, "giftType") ?? Number(gift, "giftType")) is null or 1d;
            ended = !streakable || !Has(d, "repeatEnd") || Boolean(d, "repeatEnd");
        }
        var avatar = Https(Candidates(Child(user, "userDetails"), "profilePictureUrls").Concat(Candidates(Child(d, "userDetails"), "profilePictureUrls"))
            .Concat(Candidates(user, "profilePictureUrls")).Concat(Candidates(d, "profilePictureUrls"))
            .Concat(Candidates(Child(user, "profilePicture"), "urls", "url_list"))
            .Concat(Candidates(user, "profilePictureUrlHD", "profilePictureUrl", "avatarUrl", "avatar", "picture"))
            .Concat(Candidates(d, "profilePictureUrl", "avatarUrl", "avatar")), preferJpeg: true);

        return new LiveEvent(kind.Value, Limit(userId, 128), Limit(Clean(nickname), 80), avatar,
            Limit(Clean(Text(d, "comment", "text", "content", "message")), 1000), Limit(giftId, 128), Limit(Clean(giftName), 100),
            coins, repeat, ended, (int)Math.Clamp(Number(d, "likeCount", "like_count") ?? (kind == LiveEventKind.Like ? Number(d, "count") ?? 1 : 0), 0, 1000000),
            (int)Math.Clamp(Number(d, "viewerCount", "viewer_count") ?? 0, 0, int.MaxValue), Limit(messageId, 128), DateTimeOffset.UtcNow, giftImage);
    }

    private static string EventName(JsonElement e) => NotLetter().Replace(Text(e, "event", "eventType", "type", "name").ToLowerInvariant(), "");

    private static JsonElement? Child(JsonElement? e, string key) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string First(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    private static string Text(JsonElement? e, params string[] keys)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) return "";
        foreach (var key in keys)
            if (o.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number && v.ToString().Trim() is { Length: > 0 } text) return text;
        return "";
    }

    private static double? Number(JsonElement? e, params string[] keys)
    {
        foreach (var key in keys)
            if (double.TryParse(Text(e, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)) return n;
        return null;
    }

    private static bool Has(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null;
    private static bool Boolean(JsonElement e, string key) => e.TryGetProperty(key, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ToString() is "1" or "true" or "True");

    private static IEnumerable<string> Candidates(JsonElement? e, params string[] keys)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) yield break;
        foreach (var key in keys)
        {
            if (!o.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) yield return v.GetString()!;
            else if (v.ValueKind == JsonValueKind.Array)
                foreach (var item in v.EnumerateArray().Take(8))
                    if (item.ValueKind == JsonValueKind.String) yield return item.GetString()!;
        }
    }

    /// <summary>Chỉ nhận ảnh https (proxy avatar chỉ tải https); avatar ưu tiên bản JPEG/PNG vì WebP động hay bị trình duyệt cũ từ chối.</summary>
    private static string Https(IEnumerable<string> candidates, bool preferJpeg)
    {
        var valid = candidates.Where(c => c.Length <= 2048 && Uri.TryCreate(c, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0).ToList();
        return (preferJpeg ? valid.FirstOrDefault(c => JpegOrPng().IsMatch(c)) : null) ?? valid.FirstOrDefault() ?? "";
    }

    private static string Clean(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    private static string Limit(string text, int length) => text.Length > length ? text[..length] : text;

    [GeneratedRegex("[^a-z]")]
    private static partial Regex NotLetter();
    [GeneratedRegex(@"\.(jpe?g|png)([?~]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex JpegOrPng();
}

/// <summary>
/// Bỏ gói lặp: có msgId thì nhớ 10 phút (gói quà kèm số lần và cờ kết thúc vì cập nhật combo có thể dùng chung msgId);
/// thiếu msgId thì dùng khoá ghép loại+người+quà/chữ+số lần, nhớ 2,5 giây. Có trần để chạy dài ngày.
/// </summary>
internal sealed class LiveEventDeduper
{
    private const int MaxEntries = 20000;
    private readonly Dictionary<string, long> _until = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, long Until)> _order = new();

    public bool IsDuplicate(LiveEvent e, long now)
    {
        while (_order.TryPeek(out var head) && (head.Until <= now || _order.Count > MaxEntries))
        {
            _order.Dequeue();
            if (_until.TryGetValue(head.Key, out var until) && until == head.Until) _until.Remove(head.Key);
        }
        var gift = e.Kind == LiveEventKind.Gift ? $"|{e.RepeatCount}|{e.RepeatEnd}" : "";
        var key = e.MessageId.Length > 0 ? $"id|{e.Kind}|{e.MessageId}{gift}"
            : $"x|{e.Kind}|{e.UserId}|{e.GiftId}|{e.GiftName}|{e.Text}|{e.LikeCount}|{e.ViewerCount}{gift}";
        if (_until.TryGetValue(key, out var seen) && seen > now) return true;
        var expires = now + (e.MessageId.Length > 0 ? 600000 : 2500);
        _until[key] = expires;
        _order.Enqueue((key, expires));
        return false;
    }
}

public sealed class SimulatedEventSource : ILiveEventSource
{
    public Task RunAsync(Func<LiveEvent, CancellationToken, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        status("Chế độ giả lập · không kết nối TikTok");
        return Task.Delay(Timeout.Infinite, ct);
    }

    public static LiveEvent Create(LiveEventKind kind, int index) => new(kind, $"sim-{index % 400}", $"Phật tử {index % 400 + 1}",
        Text: index % 2 == 0 ? "Nam mô A Di Đà Phật" : "Cầu bình an cho gia đình", GiftId: "sim-gift",
        GiftName: "Hoa sen", Coins: new[] { 1, 5, 10, 100 }[index % 4], LikeCount: 10,
        MessageId: Guid.NewGuid().ToString("N"), At: DateTimeOffset.UtcNow);
}
