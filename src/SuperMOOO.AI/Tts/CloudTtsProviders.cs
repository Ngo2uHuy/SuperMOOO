using System.Text.RegularExpressions;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Http;
using SuperMOOO.AI.Keys;
using SuperMOOO.AI.Translation;

namespace SuperMOOO.AI.Tts;

/// <summary>Khung chung của TTS cloud: chia câu theo giới hạn endpoint, gọi từng khối bằng key trong pool, nối WAV bằng C#.</summary>
public abstract partial class CloudTtsProvider(string id, int chunkChars, string defaultModel, IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : ITtsProvider
{
    protected static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    protected const int MaxAudioBytes = 64 * 1024 * 1024;
    private const int MaxChars = 20000;

    protected IHttpClientProvider Http => http;
    protected ISettingsStore Settings => settings;
    public string Id => id;
    public TtsCapabilities Capabilities => new(true, false, false, false, MaxChars);

    public abstract Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language, CancellationToken ct);

    protected abstract Task<byte[]> SynthesizeChunkAsync(string key, string text, string voice, string model, string? language, CancellationToken ct);

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct)
    {
        var text = request.Text.Trim();
        if (text.Length is 0 or > MaxChars) throw new ArgumentException($"Văn bản cần từ 1 đến {MaxChars:N0} ký tự.");
        if (VoiceIds.ProviderFor(request.VoiceId) != id)
            throw new ArgumentException($"Chọn giọng {id} hợp lệ.");

        var voice = request.VoiceId[(id.Length + 1)..];
        var configured = settings.Get<AiModelsSettings>("ai.models");
        var model = configured.TtsProvider == id && !string.IsNullOrWhiteSpace(configured.TtsModel) ? configured.TtsModel : defaultModel;

        var clips = new List<byte[]>();
        foreach (var chunk in TextChunker.Split(text, chunkChars))
            clips.Add(await keys.ExecuteAsync(id, (key, c) => SynthesizeChunkAsync(key, chunk, voice, model, request.Language, c), ct));

        byte[] wav;
        try { wav = WavFile.Concat(clips); }
        catch (InvalidDataException e) { throw new ProviderException(id, ProviderErrorKind.BadResponse, $"{id} trả audio không hợp lệ: {e.Message}"); }
        var (duration, rate) = WavFile.Describe(wav);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
        var temporary = request.OutputPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await File.WriteAllBytesAsync(temporary, wav, ct);
            File.Move(temporary, request.OutputPath, overwrite: true);
        }
        finally { File.Delete(temporary); }

        return new TtsResult(request.OutputPath, duration, rate);
    }

    protected static string ReadLanguage(string? language) => LanguageCatalog.TextLanguage(language ?? "vi");

    [GeneratedRegex(@"rate=(\d+)", RegexOptions.IgnoreCase)]
    protected static partial Regex RateInMime();
}

[Service(As = [typeof(ITtsProvider)])]
public sealed class OpenAiTtsProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : CloudTtsProvider(VoiceIds.OpenAi, 1200, "gpt-4o-mini-tts", http, keys, settings)
{
    private string OpenAiBase() => Settings.Get<AiModelsSettings>("ai.models").OpenAiTtsUrl.Trim().TrimEnd('/') is { Length: > 0 } u ? u : "https://api.openai.com/v1";

    internal static readonly string[] VoiceNames = ["marin", "cedar", "coral", "alloy", "ash", "ballad", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"];

    public override Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VoiceInfo>>(VoiceNames.Select(n => new VoiceInfo("openai_" + n, Id, n + " · openai", "auto")).ToList());

    protected override Task<byte[]> SynthesizeChunkAsync(string key, string text, string voice, string model, string? language, CancellationToken ct)
    {
        if (!VoiceNames.Contains(voice)) throw new ArgumentException("Chọn giọng OpenAI hợp lệ.");
        var body = new JsonObject
        {
            ["model"] = model, ["input"] = text, ["voice"] = voice, ["response_format"] = "wav",
            ["instructions"] = $"Read naturally in {ReadLanguage(language)}."
        };
        return AiHttp.SendAsync(Http, Id, AiHttp.Json(HttpMethod.Post, OpenAiBase() + "/audio/speech", body, bearer: key), RequestTimeout, ct, MaxAudioBytes);
    }
}

[Service(As = [typeof(ITtsProvider)])]
public sealed class GeminiTtsProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : CloudTtsProvider(VoiceIds.Gemini, 3500, "gemini-3.8-flash-lite-tts", http, keys, settings)
{
    internal static readonly string[] VoiceNames = ["Kore", "Puck", "Charon", "Fenrir", "Aoede", "Leda", "Orus", "Zephyr"];
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions";

    public override Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VoiceInfo>>(VoiceNames.Select(n => new VoiceInfo("gemini_" + n, Id, n + " · gemini", "auto")).ToList());

    protected override async Task<byte[]> SynthesizeChunkAsync(string key, string text, string voice, string model, string? language, CancellationToken ct)
    {
        if (!VoiceNames.Contains(voice)) throw new ArgumentException("Chọn giọng Gemini hợp lệ.");
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "user_input",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text", ["text"] = text,
                    ["annotations"] = new JsonArray(new JsonObject { ["type"] = "speech_metadata", ["style"] = $"Read naturally in {ReadLanguage(language)}." })
                })
            }),
            ["response_format"] = new JsonObject { ["type"] = "audio" },
            ["generation_config"] = new JsonObject { ["speech_config"] = new JsonArray(new JsonObject { ["voice"] = voice }) }
        };
        var json = await AiHttp.SendTextAsync(Http, Id, AiHttp.Json(HttpMethod.Post, Endpoint, body, googKey: key), RequestTimeout, ct, 96 * 1024 * 1024);
        return ParseAudio(json);
    }

    // Interactions API thật trả {"steps":[{"type":"model_output","content":[{"type":"audio","data":...,"mime_type":"audio/wav"}]}]}.
    private static JsonObject? InteractionAudio(JsonNode? list, string? nested) =>
        (list as JsonArray)?.SelectMany(item => nested is null ? [item] : (item?[nested] as JsonArray)?.AsEnumerable() ?? [])
            .OfType<JsonObject>().FirstOrDefault(o => o["type"]?.GetValue<string>() == "audio" && o["data"] is not null);

    /// <summary>F31: API có thể trả RIFF hoặc PCM L16 thô (audio/L16;rate=24000); PCM được bọc header WAV.</summary>
    internal static byte[] ParseAudio(string json)
    {
        var root = AiHttp.ParseJson(VoiceIds.Gemini, json);
        var audio = root["output_audio"] as JsonObject
            ?? InteractionAudio(root["steps"], "content") ?? InteractionAudio(root["outputs"], null)
            ?? (root["candidates"]?[0]?["content"]?["parts"] as JsonArray)?.Select(p => p?["inlineData"] as JsonObject).FirstOrDefault(p => p is not null)
            ?? throw new ProviderException(VoiceIds.Gemini, ProviderErrorKind.BadResponse, "Gemini không trả audio.");

        byte[] data;
        try { data = Convert.FromBase64String(audio["data"]?.GetValue<string>() ?? ""); }
        catch (FormatException) { throw new ProviderException(VoiceIds.Gemini, ProviderErrorKind.BadResponse, "Gemini không trả audio mã hóa hợp lệ."); }
        if (data.Length == 0) throw new ProviderException(VoiceIds.Gemini, ProviderErrorKind.BadResponse, "Gemini trả audio rỗng.");
        if (data.AsSpan().StartsWith("RIFF"u8)) return data;
        if (WavFile.LooksLikeContainer(data)) throw new ProviderException(VoiceIds.Gemini, ProviderErrorKind.BadResponse, "Gemini trả định dạng audio không hỗ trợ.");

        var mime = audio["mimeType"]?.GetValue<string>() ?? audio["mime_type"]?.GetValue<string>() ?? "";
        var rate = RateInMime().Match(mime) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 24000;
        return WavFile.FromPcm16(data, rate);
    }
}

[Service(As = [typeof(ITtsProvider)])]
public sealed partial class ElevenLabsTtsProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : CloudTtsProvider(VoiceIds.ElevenLabs, 3500, "eleven_flash_v2_5", http, keys, settings)
{
    private const string Base = "https://api.elevenlabs.io";
    private const string DefaultVoiceId = "JBFqnCBsd6RMkjVDRZzb";   // George, giọng trong ví dụ API chính thức
    private static readonly string[] Models = ["eleven_flash_v2_5", "eleven_v3"];
    private static readonly HashSet<string> FlashLanguages =
    [
        "en", "ja", "zh", "de", "hi", "fr", "ko", "pt", "it", "es", "id", "nl", "tr", "fil", "pl", "sv", "bg", "ro", "ar", "cs", "el",
        "fi", "hr", "ms", "sk", "da", "ta", "uk", "ru", "hu", "no", "vi"
    ];

    private readonly ApiKeyPool _keys = keys;
    private IReadOnlyList<VoiceInfo>? _voices;

    [GeneratedRegex(@"^[A-Za-z0-9]{10,64}$")]
    private static partial Regex VoiceIdFormat();

    public override async Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language, CancellationToken ct)
    {
        if (_voices is not null) return _voices;
        var result = new List<VoiceInfo> { new("elevenlabs_" + DefaultVoiceId, Id, "George · ElevenLabs", "auto") };
        if (!await _keys.HasKeyAsync(Id, ct)) return result;

        string? token = null;
        for (var page = 0; page < 10; page++)
        {
            var url = $"{Base}/v2/voices?page_size=100&include_total_count=false" + (token is null ? "" : "&next_page_token=" + Uri.EscapeDataString(token));
            var json = await _keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(Http, Id,
                new HttpRequestMessage(HttpMethod.Get, url) { Headers = { { "xi-api-key", key } } }, TimeSpan.FromSeconds(60), c), ct);
            var root = AiHttp.ParseJson(Id, json);
            foreach (var row in (root["voices"] as JsonArray) ?? throw new ProviderException(Id, ProviderErrorKind.BadResponse, "ElevenLabs không trả danh sách giọng hợp lệ."))
            {
                var voiceId = row?["voice_id"]?.GetValue<string>();
                if (voiceId is null || !VoiceIdFormat().IsMatch(voiceId) || voiceId == DefaultVoiceId) continue;
                var category = row?["category"]?.GetValue<string>();
                result.Add(new VoiceInfo("elevenlabs_" + voiceId, Id, (row?["name"]?.GetValue<string>() ?? voiceId) + " · ElevenLabs", "auto",
                    null, category is "cloned" or "professional"));
            }

            if (root["has_more"]?.GetValue<bool>() != true) break;
            token = root["next_page_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token)) break;
        }

        return _voices = result;
    }

    protected override async Task<byte[]> SynthesizeChunkAsync(string key, string text, string voice, string model, string? language, CancellationToken ct)
    {
        if (!VoiceIdFormat().IsMatch(voice)) throw new ArgumentException("ElevenLabs voice ID cần 10–64 chữ/số, với tiền tố elevenlabs_.");
        if (!Models.Contains(model)) throw new ArgumentException("Chọn model ElevenLabs REST TTS được hỗ trợ.");

        var code = language is null ? "auto" : LanguageCatalog.LocaleCode(language).Split('-')[0] switch { "tl" => "fil", var c => c };
        if (model == "eleven_flash_v2_5" && code != "auto" && !FlashLanguages.Contains(code))
            throw new ArgumentException("Eleven Flash v2.5 chưa hỗ trợ ngôn ngữ này; chọn Eleven v3 hoặc dịch vụ TTS khác.");

        var body = new JsonObject { ["text"] = text, ["model_id"] = model };
        if (code != "auto") body["language_code"] = code;
        var request = AiHttp.Json(HttpMethod.Post, $"{Base}/v1/text-to-speech/{voice}?output_format=pcm_24000", body);
        request.Headers.TryAddWithoutValidation("xi-api-key", key);
        var pcm = await AiHttp.SendAsync(Http, Id, request, RequestTimeout, ct, MaxAudioBytes);

        if (pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.AsSpan().StartsWith("RIFF"u8) || WavFile.LooksLikeContainer(pcm))
            throw new ProviderException(Id, ProviderErrorKind.BadResponse, "ElevenLabs không trả PCM16 audio hợp lệ.");
        return WavFile.FromPcm16(pcm, 24000);
    }
}

/// <summary>Google Cloud Text-to-Speech (texttospeech.googleapis.com, key trong pool "google"), tương đương tts/google.ts của VideoC; trả LINEAR16 nên không cần chuyển mã.</summary>
[Service(As = [typeof(ITtsProvider)])]
public sealed class GoogleCloudTtsProvider(IHttpClientProvider http, ApiKeyPool keys, ISettingsStore settings)
    : CloudTtsProvider(VoiceIds.Google, 1200, "", http, keys, settings)
{
    private const string Base = "https://texttospeech.googleapis.com/v1";
    private const string FallbackVoice = "vi-VN-Neural2-A";

    private readonly ApiKeyPool _keys = keys;
    private IReadOnlyList<VoiceInfo>? _voices;

    public override async Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language, CancellationToken ct)
    {
        _voices ??= await LoadVoicesAsync(ct);
        if (language is null) return _voices;
        var primary = LanguageCatalog.LocaleCode(language).Split('-')[0];
        return [.. _voices.Where(v => v.Language.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase))];
    }

    private async Task<IReadOnlyList<VoiceInfo>> LoadVoicesAsync(CancellationToken ct)
    {
        var fallback = new List<VoiceInfo> { new("google_" + FallbackVoice, Id, FallbackVoice + " · Google", "vi-VN", "female") };
        if (!await _keys.HasKeyAsync(Id, ct)) return fallback;

        var json = await _keys.ExecuteAsync(Id, (key, c) => AiHttp.SendTextAsync(Http, Id,
            new HttpRequestMessage(HttpMethod.Get, Base + "/voices") { Headers = { { "x-goog-api-key", key } } }, TimeSpan.FromSeconds(60), c), ct);
        var rows = AiHttp.ParseJson(Id, json)["voices"] as JsonArray ?? throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Google TTS không trả danh sách giọng hợp lệ.");
        var list = new List<VoiceInfo>();
        foreach (var row in rows)
        {
            var name = row?["name"]?.GetValue<string>();
            var locale = (row?["languageCodes"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
            if (name is null || locale is null) continue;
            list.Add(new VoiceInfo("google_" + name, Id, name + " · Google", locale, row?["ssmlGender"]?.GetValue<string>()?.ToLowerInvariant()));
        }

        return list.Count > 0 ? list : fallback;
    }

    protected override async Task<byte[]> SynthesizeChunkAsync(string key, string text, string voice, string model, string? language, CancellationToken ct)
    {
        var parts = voice.Split('-');
        if (parts.Length < 3) throw new ArgumentException("Giọng Google dạng \"vi-VN-Neural2-A\" (mã ngôn ngữ-mã vùng-tên).");
        var body = new JsonObject
        {
            ["input"] = new JsonObject { ["text"] = text },
            ["voice"] = new JsonObject { ["languageCode"] = parts[0] + "-" + parts[1], ["name"] = voice },
            ["audioConfig"] = new JsonObject { ["audioEncoding"] = "LINEAR16" }
        };
        var json = await AiHttp.SendTextAsync(Http, Id, AiHttp.Json(HttpMethod.Post, Base + "/text:synthesize", body, googKey: key), RequestTimeout, ct, 96 * 1024 * 1024);
        try { return Convert.FromBase64String(AiHttp.ParseJson(Id, json)["audioContent"]?.GetValue<string>() ?? ""); }
        catch (FormatException) { throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Google TTS không trả audio mã hóa hợp lệ."); }
    }
}
