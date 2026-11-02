using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Workflows.LiveGame;

public sealed record LiveGameObservedGift(string GiftId, string GiftName, string Image, int Coins, DateTimeOffset LastSeen)
{
    [JsonIgnore] public string Display => $"{GiftName} · {Coins:N0} xu" + (GiftId.Length > 0 ? $" · #{GiftId}" : "");
}

/// <summary>Quà thật đã thấy trong các phiên (tối đa 500, bỏ quà lâu không gặp nhất); S24 dùng để chọn quà khi lập luật.</summary>
[Service]
public sealed class LiveGameGiftCatalog(AppPaths paths, ILogger<LiveGameGiftCatalog> log) : IDisposable
{
    private const int MaxGifts = 500;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly object _fileGate = new();
    private Dictionary<string, LiveGameObservedGift>? _gifts;
    private Timer? _timer;
    private bool _pending;

    private string FilePath => Path.Combine(paths.DataRoot, "livegame", "observed-gifts.json");

    public IReadOnlyList<LiveGameObservedGift> List()
    {
        lock (_gate) return Gifts().Values.OrderBy(g => g.Coins).ThenBy(g => g.GiftName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public LiveGameObservedGift? Find(string giftId)
    {
        lock (_gate) return giftId.Length > 0 ? Gifts().GetValueOrDefault(giftId) : null;
    }

    public void Observe(LiveEvent e)
    {
        var name = LiveGameText.Cut(e.GiftName, 100);
        var id = LiveGameText.Cut(e.GiftId, 64);
        if (id.Length == 0 && name.Length == 0) return;
        var key = id.Length > 0 ? id : "name:" + LiveGameText.Fold(name);
        lock (_gate)
        {
            var gifts = Gifts();
            var old = gifts.GetValueOrDefault(key);
            gifts[key] = new(id, name.Length > 0 ? name : old?.GiftName ?? "Quà", e.GiftImage.Length > 0 ? e.GiftImage : old?.Image ?? "",
                e.Coins > 0 ? e.Coins : old?.Coins ?? 0, DateTimeOffset.UtcNow);
            if (gifts.Count > MaxGifts)
                foreach (var stale in gifts.OrderBy(g => g.Value.LastSeen).Take(gifts.Count - MaxGifts).Select(g => g.Key).ToList()) gifts.Remove(stale);
            // Ghi gộp: bão quà chỉ tạo một lần ghi sau vài giây, không ghi theo từng quà.
            if (_pending) return;
            _pending = true;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        List<LiveGameObservedGift> snapshot;
        lock (_gate)
        {
            if (!_pending || _gifts is null) return;
            _pending = false;
            snapshot = _gifts.Values.OrderByDescending(g => g.LastSeen).ToList();
        }
        lock (_fileGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json));
                File.Move(temp, FilePath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning(e, "Không lưu được danh sách quà đã thấy"); }
        }
    }

    private Dictionary<string, LiveGameObservedGift> Gifts()
    {
        if (_gifts is not null) return _gifts;
        _gifts = new(StringComparer.Ordinal);
        var file = new FileInfo(FilePath);
        if (!file.Exists) return _gifts;
        try
        {
            if (file.Length > 2 * 1024 * 1024) throw new InvalidDataException("observed-gifts.json lớn hơn 2 MB.");
            var rows = JsonSerializer.Deserialize<List<LiveGameObservedGift>>(File.ReadAllText(file.FullName), Json) ?? [];
            // File có thể bị sửa tay: kẹp lại như dữ liệu mới nhận.
            foreach (var row in rows.Where(r => r is not null).OrderByDescending(r => r.LastSeen).Take(MaxGifts))
            {
                var id = LiveGameText.Cut(row.GiftId, 64);
                var name = LiveGameText.Cut(row.GiftName, 100);
                if (id.Length == 0 && name.Length == 0) continue;
                var image = Uri.TryCreate(row.Image, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && row.Image.Length <= 2048 ? row.Image : "";
                _gifts.TryAdd(id.Length > 0 ? id : "name:" + LiveGameText.Fold(name), new(id, name.Length > 0 ? name : "Quà", image, Math.Clamp(row.Coins, 0, 1000000), row.LastSeen));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { log.LogWarning(e, "Bỏ qua danh sách quà đã thấy hỏng ở {Path}", file.FullName); }
        return _gifts;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Flush();
    }
}
