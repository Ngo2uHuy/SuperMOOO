using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SuperMOOO.AI.Llm;

namespace SuperMOOO.Workflows.Live;

/// <summary>Model local đang bận: hàng chờ đầy hoặc chờ lượt quá lâu.</summary>
public sealed class LiveBusyException() : Exception("Model local đang bận. Chờ tác vụ hiện tại hoàn tất rồi thử lại.");

/// <summary>
/// Gọi LLM cho livestream: model local chỉ chạy một suy luận mỗi lúc (hàng chờ tối đa 8, chờ lượt tối đa 10 giây),
/// và câu trả lời giống hệt (cùng ngữ cảnh, cùng cài đặt) được dùng lại trong thời gian ngắn để bình luận lặp không tốn thêm suy luận.
/// </summary>
[Service]
public sealed partial class LiveInference(LlmProviderRegistry llm, ISettingsStore settings)
{
    private const int MaxPending = 8;
    private const int MaxCached = 128;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTime Expires, string Text)> _cache = [];
    private int _pending;

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex Think();

    /// <summary>Provider + model theo cài đặt "live"; <paramref name="cloud"/> chọn nhánh đám mây.</summary>
    public (ILlmProvider Provider, string? Model, bool IsLocal) Resolve(LiveSettings s, bool cloud)
    {
        if (cloud) return (llm.Get(s.CloudProvider), string.IsNullOrWhiteSpace(s.CloudModel) ? null : s.CloudModel.Trim(), false);
        return (llm.Get(s.LocalProvider), s.LocalProvider == LlmProviderSpec.Local.Id ? s.LocalModel : null, true);
    }

    public async Task<string> GenerateAsync(bool cloud, string? system, string user, int maxTokens, CancellationToken ct)
    {
        var s = settings.Get<LiveSettings>("live");
        var (provider, model, isLocal) = Resolve(s, cloud);
        var ttl = TimeSpan.FromSeconds(Math.Max(0, s.ReplyCacheSeconds));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', provider.Id, model, s.NumCtx, maxTokens, system, user))));
        if (ttl > TimeSpan.Zero && Cached(key) is { } hit) return hit;

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(system)) messages.Add(new ChatMessage(ChatRole.System, system));
        messages.Add(new ChatMessage(ChatRole.User, user));
        var request = new LlmRequest(messages, model, maxTokens, 0.2);

        if (!isLocal) return Store(key, ttl, await CompleteAsync(provider, request, ct));

        if (Volatile.Read(ref _pending) >= MaxPending) throw new LiveBusyException();
        Interlocked.Increment(ref _pending);
        var acquired = false;
        try
        {
            acquired = await _gate.WaitAsync(LockWait, ct);
            if (!acquired) throw new LiveBusyException();
            // Yêu cầu giống hệt xếp hàng cùng lúc dùng kết quả của lượt đầu.
            if (ttl > TimeSpan.Zero && Cached(key) is { } again) return again;
            return Store(key, ttl, await CompleteAsync(provider, request, ct));
        }
        finally
        {
            if (acquired) _gate.Release();
            Interlocked.Decrement(ref _pending);
        }
    }

    private static async Task<string> CompleteAsync(ILlmProvider provider, LlmRequest request, CancellationToken ct)
    {
        var text = Think().Replace((await provider.CompleteAsync(request, ct)).Text, "").Trim();
        if (text.Length == 0) throw new ProviderException(provider.Id, ProviderErrorKind.BadResponse, "Model không trả câu trả lời hoàn chỉnh.");
        return text;
    }

    private string? Cached(string key)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out var e)) return null;
            if (e.Expires > DateTime.UtcNow) return e.Text;
            _cache.Remove(key);
            return null;
        }
    }

    private string Store(string key, TimeSpan ttl, string text)
    {
        if (ttl <= TimeSpan.Zero) return text;
        lock (_cacheLock)
        {
            _cache[key] = (DateTime.UtcNow + ttl, text);
            while (_cache.Count > MaxCached) _cache.Remove(_cache.MinBy(kv => kv.Value.Expires).Key);
        }

        return text;
    }
}
