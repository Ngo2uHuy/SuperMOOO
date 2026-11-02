using System.Globalization;
using System.Text.Json;
using SuperMOOO.AI.Audio;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Http;
using SuperMOOO.AI.Keys;
using SuperMOOO.AI.Tts;

namespace SuperMOOO.AI.Asr;

/// <summary>ASR cloud: WAV 16 kHz mono (ffmpeg) cắt thành khối 10 phút (&lt; 20 MB/lần gửi), timestamp cộng offset khối.</summary>
public abstract class CloudAsrProvider(string id, string defaultModel, IHttpClientProvider http, ApiKeyPool keys, WavPreparer wav) : IAsrProvider
{
    private const int SampleRate = 16000;
    private const int ChunkSeconds = 600;

    public string Id => id;
    public bool IsCloud => true;

    protected IHttpClientProvider Http => http;
    protected string DefaultModel => defaultModel;

    /// <summary>Gửi một khối WAV, trả segment có timestamp tương đối khối.</summary>
    protected abstract Task<(string? Language, List<(double Start, double End, string Text)> Segments)> TranscribeChunkAsync(
        string key, byte[] wavChunk, string model, string? language, CancellationToken ct);

    public async Task<Transcript> TranscribeAsync(AsrRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) || request.Model == "turbo" ? defaultModel : request.Model;
        var code = request.Language is null || LanguageCatalog.LocaleCode(request.Language) == "auto" ? null : IsoCode(request.Language);

        using var prepared = await wav.PrepareAsync(request.AudioPath, SampleRate, ct);
        var all = await File.ReadAllBytesAsync(prepared.Path, ct);
        var (_, dataOffset, dataLength) = WavFile.Parse(all);
        var duration = (double)dataLength / (SampleRate * 2);
        if (duration <= 0) throw new ArgumentException("Cần file audio có tiếng hợp lệ.");

        var segments = new List<TranscriptSegment>();
        var detected = code ?? "auto";
        var chunkBytes = ChunkSeconds * SampleRate * 2;
        for (var offsetBytes = 0; offsetBytes < dataLength; offsetBytes += chunkBytes)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(chunkBytes, dataLength - offsetBytes);
            var offset = (double)offsetBytes / (SampleRate * 2);
            var chunk = WavFile.FromPcm16(all.AsSpan(dataOffset + offsetBytes, length), SampleRate);

            var (language, parsed) = await keys.ExecuteAsync(id, (key, c) => TranscribeChunkAsync(key, chunk, model, code, c), ct);
            if (language is not null) detected = language;
            var chunkSeconds = (double)length / (SampleRate * 2);
            foreach (var (start, end, text) in parsed)
            {
                if (!double.IsFinite(start + end) || start < 0 || end <= start || start >= chunkSeconds)
                    throw new ProviderException(id, ProviderErrorKind.BadResponse, "Provider trả timestamp phụ đề không hợp lệ.");
                var s = offset + start;
                var e = Math.Min(offset + end, duration);
                if (Math.Round(e, 3) <= Math.Round(s, 3))
                    throw new ProviderException(id, ProviderErrorKind.BadResponse, "Provider trả đoạn phụ đề quá ngắn hoặc ngoài timeline.");
                segments.Add(new TranscriptSegment(segments.Count + 1, TimeSpan.FromSeconds(Math.Round(s, 3)), TimeSpan.FromSeconds(Math.Round(e, 3)), text.Trim()));
            }

            progress?.Report(new ProgressInfo(Math.Min(1, (offset + chunkSeconds) / duration), $"Nhận diện cloud: {Math.Min(duration, offset + chunkSeconds):0}/{duration:0}s"));
        }

        return new Transcript(detected, TimeSpan.FromSeconds(duration), segments);
    }

    protected static string IsoCode(string language) => LanguageCatalog.LocaleCode(language).Split('-')[0] switch
    {
        "fil" => "tl", "arb" => "ar", "cmn" => "zh", "nb" => "no", "iw" => "he", var c => c
    };

    protected static string Str(JsonNode? node) => node?.GetValue<string>() ?? "";
    protected static double Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;
}

/// <summary>Endpoint /audio/transcriptions kiểu OpenAI (OpenAI, Groq): multipart + verbose_json.</summary>
public abstract class OpenAiStyleAsrProvider(string id, string baseUrl, string defaultModel, IHttpClientProvider http, ApiKeyPool keys, WavPreparer wav)
    : CloudAsrProvider(id, defaultModel, http, keys, wav)
{
    protected override async Task<(string?, List<(double, double, string)>)> TranscribeChunkAsync(
        string key, byte[] wavChunk, string model, string? language, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent("verbose_json"), "response_format" },
            { new StringContent("segment"), "timestamp_granularities[]" },
            { new StringContent("0"), "temperature" },
            { new ByteArrayContent(wavChunk) { Headers = { ContentType = new("audio/wav") } }, "file", "audio.wav" }
        };
        if (language is not null) form.Add(new StringContent(language), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/audio/transcriptions") { Content = form };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        var json = await AiHttp.SendTextAsync(Http, Id, request, TimeSpan.FromMinutes(10), ct);

        var root = AiHttp.ParseJson(Id, json);
        var items = root["segments"] as JsonArray
            ?? throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Provider không trả phụ đề có timestamp.");
        var result = items.Select(i => (Num(i?["start"]), Num(i?["end"]), Str(i?["text"]))).Where(s => s.Item3.Trim().Length > 0).ToList();
        return (NormalizeLanguage(root["language"]?.GetValue<string>()), result);
    }

    // OpenAI trả tên tiếng Anh ("vietnamese"), Groq có thể trả mã; chỉ lấy khi là mã ngắn.
    private static string? NormalizeLanguage(string? raw) =>
        raw is { Length: 2 or 3 } && raw.All(char.IsLetter) ? raw.ToLowerInvariant() : null;
}

[Service(As = [typeof(IAsrProvider)])]
public sealed class OpenAiAsrProvider(IHttpClientProvider http, ApiKeyPool keys, WavPreparer wav)
    : OpenAiStyleAsrProvider("openai", "https://api.openai.com/v1", "whisper-1", http, keys, wav);

[Service(As = [typeof(IAsrProvider)])]
public sealed class GroqAsrProvider(IHttpClientProvider http, ApiKeyPool keys, WavPreparer wav)
    : OpenAiStyleAsrProvider("groq", "https://api.groq.com/openai/v1", "whisper-large-v3-turbo", http, keys, wav);

/// <summary>Gemini không có endpoint timestamp chuẩn: nhờ model trả JSON segment (timestamp là ước lượng của model).</summary>
[Service(As = [typeof(IAsrProvider)])]
public sealed class GeminiAsrProvider(IHttpClientProvider http, ApiKeyPool keys, WavPreparer wav)
    : CloudAsrProvider("gemini", "gemini-3.8-flash", http, keys, wav)
{
    protected override async Task<(string?, List<(double, double, string)>)> TranscribeChunkAsync(
        string key, byte[] wavChunk, string model, string? language, CancellationToken ct)
    {
        var prompt = "Transcribe this audio verbatim" + (language is null ? "" : $" in language '{language}'") +
                     ". Return only a JSON array of objects {\"start\": seconds, \"end\": seconds, \"text\": string} covering the speech in order; start/end are seconds from the beginning of this audio.";
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = prompt },
                    new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(wavChunk) } })
            }),
            ["generationConfig"] = new JsonObject { ["responseMimeType"] = "application/json", ["temperature"] = 0 }
        };
        var json = await AiHttp.SendTextAsync(Http, Id,
            AiHttp.Json(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent", body, googKey: key),
            TimeSpan.FromMinutes(10), ct);

        var text = AiHttp.ParseJson(Id, json)["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>()
            ?? throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Gemini không trả phụ đề.");
        try
        {
            var items = JsonNode.Parse(text) as JsonArray ?? throw new JsonException();
            return (null, items.Select(i => (Num(i?["start"]), Num(i?["end"]), Str(i?["text"]))).Where(s => s.Item3.Trim().Length > 0).ToList());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Gemini trả phụ đề không đúng định dạng JSON.");
        }
    }
}
