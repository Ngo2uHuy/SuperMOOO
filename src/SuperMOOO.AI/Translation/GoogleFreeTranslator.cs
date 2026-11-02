using System.Text;
using System.Text.Json;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Http;

namespace SuperMOOO.AI.Translation;

/// <summary>Endpoint gtx không cần key (O01). Lỗi báo rõ, không bao giờ trả văn bản nguồn thay bản dịch.</summary>
[Service(As = [typeof(ITranslator)])]
public sealed class GoogleFreeTranslator : ITranslator
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    private const int MaxChars = 1500;
    private const int Retries = 2;
    private const int Concurrency = 4;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private readonly IHttpClientProvider _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GoogleFreeTranslator(IHttpClientProvider http) : this(http, Task.Delay) { }

    internal GoogleFreeTranslator(IHttpClientProvider http, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = http;
        _delay = delay;
    }

    public string Id => "google-free";
    public bool RequiresKey => false;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, TranslateOptions options, CancellationToken ct)
    {
        var target = LanguageCatalog.LocaleCode(options.TargetLanguage);
        if (target == "auto") throw new ArgumentException("Chọn ngôn ngữ đích cụ thể để dịch.");
        var source = LanguageCatalog.LocaleCode(options.SourceLanguage);

        // Mỗi câu một request ~0,2 s gần như toàn bộ là chờ mạng: chạy song song có giới hạn (65 câu: ~13 s → ~3,5 s), ghi theo chỉ số nên giữ thứ tự.
        var result = new string[texts.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, texts.Count), new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            async (i, token) => result[i] = string.IsNullOrWhiteSpace(texts[i]) ? "" : await TranslateOneAsync(texts[i], source, target, token));

        return result;
    }

    private async Task<string> TranslateOneAsync(string text, string source, string target, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var chunk in TextChunker.Split(text, MaxChars)) parts.Add(await RequestWithRetryAsync(chunk, source, target, ct));
        return string.Join(' ', parts);
    }

    private async Task<string> RequestWithRetryAsync(string chunk, string source, string target, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await RequestAsync(chunk, source, target, ct); }
            catch (ProviderException e) when (attempt < Retries && e.Kind is ProviderErrorKind.Network or ProviderErrorKind.Timeout or ProviderErrorKind.RateLimited)
            {
                await _delay(TimeSpan.FromSeconds(attempt + 1), ct);
            }
        }
    }

    private async Task<string> RequestAsync(string text, string source, string target, CancellationToken ct)
    {
        var url = $"{Endpoint}?client=gtx&sl={Short(source)}&tl={Short(target, keepRegion: true)}&dt=t&q={Uri.EscapeDataString(text)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        var body = await AiHttp.SendTextAsync(_http, Id, request, RequestTimeout, ct);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var sb = new StringBuilder();
            foreach (var segment in doc.RootElement[0].EnumerateArray())
                if (segment.ValueKind == JsonValueKind.Array && segment[0].ValueKind == JsonValueKind.String) sb.Append(segment[0].GetString());
            var translated = sb.ToString().Trim();
            if (translated.Length == 0) throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Google Translate trả bản dịch rỗng.");
            return translated;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Google Translate trả dữ liệu không đọc được.");
        }
    }

    // gtx nhận "auto", "vi", "zh-CN"/"zh-TW"; các locale khác chỉ lấy mã ngôn ngữ.
    private static string Short(string locale, bool keepRegion = false) =>
        locale == "auto" ? "auto" : keepRegion && locale.StartsWith("zh-", StringComparison.Ordinal) ? locale : locale.Split('-')[0];
}
