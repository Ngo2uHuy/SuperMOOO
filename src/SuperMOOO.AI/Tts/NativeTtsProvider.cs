using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuperMOOO.AI.Catalog;

namespace SuperMOOO.AI.Tts;

/// <summary>Provider có thể khoá cache bằng trạng thái model/giọng (đổi file model thì bản cache cũ vô hiệu).</summary>
public interface ITtsCacheStamp
{
    Task<string> CacheStampAsync(TtsRequest request, CancellationToken ct);
}

internal sealed record NativeVoice(string Id, string Name, string Gender, string Locale, string Language, bool Designed, bool Cloned,
    string? Instruct = null, int? Seed = null, string? ReferenceCodesFile = null, string? SpeakerEmbeddingFile = null,
    string? VoiceRef = null, string? ReferenceText = null);

/// <summary>
/// VieNeu/OmniVoice/Supertonic/Qwen3-TTS/CosyVoice3/Irodori/Chatterbox/Breeze qua audiocpp_server (HTTP cục bộ). Tốc độ/cao độ do TempoFitter và voice effect xử lý, không do provider.
/// CosyVoice3, Irodori, Chatterbox và Breeze chỉ đọc theo giọng mẫu: giọng dựng sẵn dùng giọng neo do OmniVoice tạo (AnchorAsync), giọng clone dùng audio mẫu của clone OmniVoice.
/// </summary>
public abstract class NativeTtsProvider(string family, NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles)
    : ITtsProvider, ITtsCacheStamp
{
    private const int MaxChars = 20000;
    private const string SupertonicPrefix = "cpp_supertonic_";
    private const string Qwen3Prefix = "cpp_qwen3_";
    private readonly bool _vieneu = family == "vieneu";
    private readonly bool _supertonic = family == "supertonic";
    private readonly bool _optional = family is not ("vieneu" or "omnivoice");

    public string Family => family;
    public string Id => "native." + family;
    public TtsCapabilities Capabilities => new(false, false, false, family == "omnivoice", MaxChars);

    private string DisplayName => family switch
    {
        "supertonic" => "Supertonic", "qwen3" => "Qwen3-TTS", "cosyvoice3" => "CosyVoice3", "irodori" => "Irodori",
        "chatterbox" => "Chatterbox", "breeze" => "Breeze TTS 2", _ => "OmniVoice"
    };

    // Model ngoài VieNeu/OmniVoice là tuỳ chọn (model/runtime tải riêng): chưa cài thì không hiện giọng trong danh sách chọn.
    public async Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? language, CancellationToken ct) =>
        _optional && !server.IsInstalled(family) ? [] : (await LoadVoicesAsync(language, ct)).Values
        .Where(v => !(_vieneu && v.Designed)).Select(v => new VoiceInfo(v.Id, Id, v.Name, v.Locale, v.Gender, v.Cloned)).ToList();

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct)
    {
        var text = request.Text.Trim();
        if (text.Length is 0 or > MaxChars) throw new ArgumentException($"Văn bản cần từ 1 đến {MaxChars:N0} ký tự.");
        var voices = await LoadVoicesAsync(null, ct);
        if (!voices.TryGetValue(request.VoiceId, out var voice))
            throw new ArgumentException("Giọng native không tồn tại; cập nhật danh sách hoặc cài giọng mẫu.");

        var config = settings.Get<TtsNativeSettings>("tts.native");
        var requested = LanguageCatalog.LocaleCode(request.Language ?? (voice.Locale != "auto" ? voice.Locale : config.Language));
        var speech = LanguageCatalog.SpeechCode(requested);
        // Server VieNeu từ chối "english" dù model card ghi hỗ trợ; ngôn ngữ khác đã được TtsLanguageRouter chuyển sang model khác.
        if (_vieneu && speech is not ("vi" or "auto")) throw new ArgumentException("Giọng quốc tế cần chọn OmniVoice.");
        if (_supertonic && speech == "auto") throw new ArgumentException("Supertonic cần chọn ngôn ngữ đọc cụ thể.");
        if (!TtsLanguageRouter.Supports(Id, speech)) throw new ArgumentException($"{DisplayName} chưa hỗ trợ ngôn ngữ {speech}; chọn giọng OmniVoice.");
        // Accent mẫu chỉ mô tả tiếng Anh; không dùng lại cho ngôn ngữ khác.
        if (speech != "auto" && voice.Designed && voice.Language != "auto" && voice.Language != speech)
            throw new ArgumentException("Ngôn ngữ đọc không khớp giọng mẫu; chọn giọng đúng ngôn ngữ hoặc giọng Quốc tế.");
        if (!server.IsInstalled(family)) throw new InvalidOperationException($"Chưa cài đầy đủ {family} native (server, model, phát âm).");

        if (voice.Designed && family != "omnivoice" && !server.IsInstalled("omnivoice"))
            throw new InvalidOperationException($"Giọng dựng sẵn của {DisplayName} cần OmniVoice để tạo giọng mẫu; cài OmniVoice hoặc chọn giọng clone.");
        if (voice.Designed) voice = await FromAnchorAsync(await AnchorAsync(voice, requested, speech, config, ct), ct);
        var payload = BuildPayload(family, voice, text, requested, speech, config);
        await server.SynthesizeToFileAsync(payload, request.OutputPath, ct);
        var (duration, rate) = WavFile.DescribeFile(request.OutputPath);
        return new TtsResult(request.OutputPath, duration, rate);
    }

    private static readonly SemaphoreSlim AnchorGate = new(1, 1);

    /// <summary>Giọng dựng sẵn đã có giọng neo (VoiceRef); VieNeu đổi neo thành codes + embedding.</summary>
    private protected virtual Task<NativeVoice> FromAnchorAsync(NativeVoice voice, CancellationToken ct) => Task.FromResult(voice);

    /// <summary>
    /// Giọng dựng theo mô tả (instructions + seed) sinh lại người nói ở mỗi câu nên một video nghe như nhiều người.
    /// Tạo một lần câu mẫu của giọng đó bằng OmniVoice rồi đọc mọi câu bằng voice_ref của mẫu này để giữ cùng một giọng.
    /// CosyVoice3/Irodori dùng chung mẫu với giọng OmniVoice tương ứng; khoá có thời điểm sửa model OmniVoice nên đổi model thì tạo lại.
    /// </summary>
    private async Task<NativeVoice> AnchorAsync(NativeVoice voice, string requested, string speech, TtsNativeSettings config, CancellationToken ct)
    {
        var sample = LanguageCatalog.SampleText(requested);
        var omni = family == "omnivoice" ? voice : voice with { Id = "cpp_omni_" + voice.Id[(voice.Id.IndexOf('_', 4) + 1)..] };
        var model = server.ModelPath("omnivoice");
        var key = JsonSerializer.Serialize(new { omni.Id, omni.Instruct, omni.Seed, requested, config.Steps, config.ChunkSize, model = File.Exists(model) ? File.GetLastWriteTimeUtc(model).Ticks : 0 });
        var anchor = Path.Combine(server.AnchorsDir, $"{omni.Id}_{requested}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12]}.wav");

        await AnchorGate.WaitAsync(ct);
        try
        {
            if (!File.Exists(anchor))
            {
                Directory.CreateDirectory(server.AnchorsDir);
                var temp = anchor + ".tmp.wav";
                await server.SynthesizeToFileAsync(BuildPayload("omnivoice", omni, sample, requested, speech, config), temp, ct);
                File.Move(temp, anchor, overwrite: true);
            }
        }
        finally
        {
            AnchorGate.Release();
        }

        return voice with { Instruct = null, VoiceRef = anchor, ReferenceText = sample };
    }

    internal static JsonObject BuildPayload(string family, NativeVoice voice, string text, string requestedLocale, string speech, TtsNativeSettings config)
    {
        // Chunk mặc định của Supertonic (120 ký tự ja/ko, 300 còn lại) hợp model hơn ChunkSize chung; seed mặc định cố định nên giọng ổn định.
        if (family == "supertonic")
            return new JsonObject
            {
                ["model"] = family, ["input"] = text, ["response_format"] = "wav", ["language"] = speech == "arb" ? "ar" : speech,
                ["options"] = new JsonObject { ["voice_id"] = voice.Id[SupertonicPrefix.Length..].ToUpperInvariant() }
            };

        // Qwen3 CustomVoice: giọng dựng sẵn theo voice_id, không cần giọng mẫu; seed cố định để đọc lại ra cùng kết quả.
        if (family == "qwen3")
            return new JsonObject
            {
                ["model"] = family, ["input"] = text, ["response_format"] = "wav", ["language"] = speech, ["seed"] = voice.Seed ?? 42,
                ["options"] = new JsonObject { ["voice_id"] = voice.Id[Qwen3Prefix.Length..] }
            };

        // Bản GGUF của Chatterbox không kèm giọng dựng sẵn (conds.pt) nên luôn đọc theo giọng mẫu; Breeze bắt buộc transcript của mẫu.
        if (family is "chatterbox" or "breeze")
        {
            var request = new JsonObject
            {
                ["model"] = family, ["input"] = text, ["response_format"] = "wav", ["language"] = speech, ["voice_ref"] = voice.VoiceRef,
                ["seed"] = voice.Seed ?? 42
            };
            if (family == "breeze") request["reference_text"] = voice.ReferenceText;
            return request;
        }

        if (family is "cosyvoice3" or "irodori")
        {
            JsonObject extra;
            if (family == "irodori") extra = new JsonObject { ["num_inference_steps"] = 20 };
            else
            {
                // zero_shot cần transcript cùng ngôn ngữ với câu đọc; mẫu clone khác ngôn ngữ thì dùng cross_lingual (bỏ transcript).
                var zeroShot = !string.IsNullOrEmpty(voice.ReferenceText) && (voice.Language == "auto" || voice.Language == speech);
                extra = new JsonObject { ["template_name"] = zeroShot ? "zero_shot" : "cross_lingual", ["text_chunk_size"] = config.ChunkSize };
                if (!zeroShot) voice = voice with { ReferenceText = null };
            }

            var request = new JsonObject
            {
                ["model"] = family, ["input"] = text, ["response_format"] = "wav", ["language"] = family == "irodori" ? "ja" : speech,
                ["voice_ref"] = voice.VoiceRef, ["options"] = extra
            };
            if (voice.Seed is { } s) request["seed"] = s;
            if (family == "cosyvoice3" && !string.IsNullOrEmpty(voice.ReferenceText)) request["reference_text"] = voice.ReferenceText;
            return request;
        }

        var vieneu = family == "vieneu";
        var options = new JsonObject { ["text_chunk_size"] = config.ChunkSize };
        if (vieneu)
        {
            options["reference_codes_file"] = voice.ReferenceCodesFile;
            options["speaker_embedding_file"] = voice.SpeakerEmbeddingFile;
            options["max_tokens"] = 1000;
        }
        else options["num_inference_steps"] = config.Steps;

        var payload = new JsonObject
        {
            ["model"] = family, ["input"] = text, ["response_format"] = "wav", ["options"] = options,
            ["language"] = vieneu ? "auto" : speech
        };
        if (voice.Seed is { } seed) payload["seed"] = seed;
        if (!string.IsNullOrEmpty(voice.Instruct))
        {
            var instruct = voice.Instruct;
            if (voice.Designed && speech == "en" && LanguageCatalog.All.FirstOrDefault(l => l.Code == requestedLocale)?.Accent is { Length: > 0 } accent)
                instruct = string.Join(", ", instruct.Split(',').Select(p => p.Trim()).Where(p => !p.Contains("accent")).Append(accent));
            payload["instructions"] = instruct;
        }

        if (!string.IsNullOrEmpty(voice.VoiceRef)) payload["voice_ref"] = voice.VoiceRef;
        if (!string.IsNullOrEmpty(voice.ReferenceText)) payload["reference_text"] = voice.ReferenceText;
        return payload;
    }

    /// <summary>Nạp model và giọng mặc định bằng một câu mẫu để lần đọc đầu không bị trễ.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        var voices = await LoadVoicesAsync(null, ct);
        if (voices.Count == 0) throw new InvalidOperationException("Chưa có giọng mẫu native.");
        var voice = voices.GetValueOrDefault("cpp_vieneu_minh_quan_pro") ?? voices.Values.First();
        var locale = voice.Locale == "auto" ? settings.Get<TtsNativeSettings>("tts.native").Language : voice.Locale;
        if (!TtsLanguageRouter.Supports(Id, LanguageCatalog.SpeechCode(locale))) locale = family == "irodori" ? "ja" : "en-US";
        var output = Path.Combine(Path.GetTempPath(), "aivs-warm-" + Guid.NewGuid().ToString("N") + ".wav");
        try { await SynthesizeAsync(new TtsRequest(LanguageCatalog.SampleText(locale), voice.Id, output, locale), ct); }
        finally { File.Delete(output); }
    }

    /// <summary>Nạp model bằng đúng giọng/ngôn ngữ job sắp dùng (giọng dựng theo mô tả thì tạo luôn giọng neo cho các câu sau).</summary>
    public async Task WarmAsync(string voiceId, string? language, CancellationToken ct)
    {
        var voices = await LoadVoicesAsync(null, ct);
        if (!voices.TryGetValue(voiceId, out var voice)) return;
        var locale = language ?? (voice.Locale == "auto" ? settings.Get<TtsNativeSettings>("tts.native").Language : voice.Locale);
        var output = Path.Combine(Path.GetTempPath(), "aivs-warm-" + Guid.NewGuid().ToString("N") + ".wav");
        try { await SynthesizeAsync(new TtsRequest(LanguageCatalog.SampleText(locale), voiceId, output, locale), ct); }
        finally { File.Delete(output); }
    }

    public async Task<string> CacheStampAsync(TtsRequest request, CancellationToken ct)
    {
        var voices = await LoadVoicesAsync(null, ct);
        var voice = voices.GetValueOrDefault(request.VoiceId);
        var stamps = new List<object>();
        var designed = voice?.Designed == true;
        // Giọng neo của CosyVoice3/Irodori do OmniVoice tạo nên đổi model OmniVoice cũng phải đọc lại.
        var anchorModel = designed && family != "omnivoice" ? server.ModelPath("omnivoice") : null;
        foreach (var path in new[] { server.ModelPath(family), server.RuntimeExe(family), anchorModel, voice?.VoiceRef, voice?.ReferenceCodesFile, voice?.SpeakerEmbeddingFile })
            if (path is not null && File.Exists(path)) stamps.Add(new { path, File.GetLastWriteTimeUtc(path).Ticks, new FileInfo(path).Length });
        // Giọng dựng theo mô tả đọc qua giọng neo (AnchorAsync): khoá cache phải khác bản cũ, nếu không các câu đọc kiểu cũ
        // (mỗi câu một người nói) bị lấy lại từ cache và video vẫn nhiều giọng.
        var mode = designed ? "anchor-v1" : null;
        // OmniVoice CPU đọc voice_ref có tiếng bíp đầu câu: chuyển sang Vulkan thì không lấy lại các câu cũ từ cache.
        if (family == "omnivoice" && server.UsesVulkan(family)) mode += "+vulkan";
        // Chỉ các trường ảnh hưởng audio (xem BuildPayload); Threads/Port/MaxLoadedModels/LowMemory đổi không làm mất cache.
        var config = settings.Get<TtsNativeSettings>("tts.native");
        var chunked = family is "vieneu" or "omnivoice" or "cosyvoice3" || designed;
        var audio = new { config.Language, ChunkSize = chunked ? config.ChunkSize : 0, Steps = family == "omnivoice" || designed ? config.Steps : 0 };
        var json = JsonSerializer.Serialize(new { config = audio, voice, stamps, mode });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    internal async Task<Dictionary<string, NativeVoice>> LoadVoicesAsync(string? language, CancellationToken ct)
    {
        var all = family switch
        {
            "vieneu" => await AddVieNeuClonesAsync(LoadVieNeu(), ct),
            "supertonic" => LoadSupertonic(),
            "qwen3" => LoadQwen3(),
            "cosyvoice3" or "irodori" or "chatterbox" or "breeze" => await LoadReferenceVoicesAsync(ct),
            _ => await LoadOmniVoiceAsync(ct)
        };
        if (language is null || LanguageCatalog.LocaleCode(language) == "auto") return all;

        var requested = LanguageCatalog.LocaleCode(language);
        var exact = all.Values.Any(v => v.Locale == requested);
        var speech = LanguageCatalog.SpeechCode(requested);
        if (!TtsLanguageRouter.Supports(Id, speech)) return new();
        return all.Where(p => p.Value.Cloned || p.Value.Language == "auto" || p.Value.Locale == requested || (!exact && p.Value.Language == speech))
            .ToDictionary(p => p.Key, p => p.Value);
    }

    private Dictionary<string, NativeVoice> LoadVieNeu()
    {
        var root = server.VieNeuVoicesDir;
        var manifest = Path.Combine(root, "manifest.json");
        var result = new Dictionary<string, NativeVoice>();
        if (!File.Exists(manifest)) return result;

        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var item in doc.RootElement.GetProperty("voices").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var codes = Path.Combine(root, id, "ref_codes.txt");
            var embedding = Path.Combine(root, id, "speaker.emb.txt");
            if (!File.Exists(codes) || !File.Exists(embedding)) continue;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            result["cpp_vieneu_" + id] = new NativeVoice("cpp_vieneu_" + id, name, label.Contains("Nữ") ? "female" : "male", "vi-VN", "vi",
                false, false, ReferenceCodesFile: codes, SpeakerEmbeddingFile: embedding);
        }

        // Giọng "Quốc tế" của OmniVoice đọc tiếng Việt bằng VieNeu qua giọng neo (TtsService chuyển sang, không hiện trong danh sách chọn).
        foreach (var (gender, title) in new[] { ("male", "Nam"), ("female", "Nữ") })
            result["cpp_vieneu_international_" + gender] = new NativeVoice("cpp_vieneu_international_" + gender, "VieNeu · Quốc tế · " + title, gender,
                "vi-VN", "vi", true, false, gender + ", young adult, moderate pitch", gender == "male" ? 42 : 43);
        return result;
    }

    /// <summary>Clone OmniVoice có codes + embedding VieNeu (mẫu tiếng Việt, xem NativeVoiceCloner); không đặt voice_ref để VieNeu đọc theo embedding.</summary>
    private async Task<Dictionary<string, NativeVoice>> AddVieNeuClonesAsync(Dictionary<string, NativeVoice> result, CancellationToken ct)
    {
        foreach (var p in await profiles.ListAsync(VoiceIds.OmniVoice, ct))
        {
            if (p.RefPath is null || !p.Id.StartsWith(VoiceIds.CloneOmniPrefix, StringComparison.Ordinal)) continue;
            var (codes, embedding) = VieNeuEnroller.Files(p.RefPath);
            if (!File.Exists(codes) || !File.Exists(embedding)) continue;
            var id = VoiceIds.CloneVieNeuPrefix + p.Id[VoiceIds.CloneOmniPrefix.Length..];
            result[id] = new NativeVoice(id, "VieNeu · " + p.Name, p.Gender ?? "", "vi-VN", "vi", false, true,
                ReferenceCodesFile: codes, SpeakerEmbeddingFile: embedding);
        }
        return result;
    }

    /// <summary>10 giọng dựng sẵn của Supertonic 3 (M1–M5, F1–F5), đọc được mọi ngôn ngữ model hỗ trợ.</summary>
    private static Dictionary<string, NativeVoice> LoadSupertonic()
    {
        var result = new Dictionary<string, NativeVoice>();
        foreach (var (gender, title, code) in new[] { ("male", "Nam", 'm'), ("female", "Nữ", 'f') })
            for (var i = 1; i <= 5; i++)
            {
                var id = $"{SupertonicPrefix}{code}{i}";
                result[id] = new NativeVoice(id, $"Supertonic · {title} {i}", gender, "auto", "auto", false, false);
            }
        return result;
    }

    private static Dictionary<string, NativeVoice> LoadQwen3() => TtsLanguageRouter.Qwen3Speakers.ToDictionary(s => Qwen3Prefix + s.Speaker,
        s => new NativeVoice(Qwen3Prefix + s.Speaker, "Qwen3 · " + s.Name, s.Gender, "auto", "auto", false, false, Seed: 42));

    /// <summary>
    /// CosyVoice3/Irodori không có giọng dựng sẵn: giọng theo ngôn ngữ dùng mô tả của giọng OmniVoice tương ứng (đọc qua giọng neo),
    /// giọng clone dùng lại audio mẫu + transcript của clone OmniVoice.
    /// </summary>
    private async Task<Dictionary<string, NativeVoice>> LoadReferenceVoicesAsync(CancellationToken ct)
    {
        var prefix = family switch { "irodori" => "cpp_irodori_", "chatterbox" => "cpp_chatterbox_", "breeze" => "cpp_breeze_", _ => "cpp_cosy_" };
        var result = new Dictionary<string, NativeVoice>();
        foreach (var v in LanguageCatalog.InternationalVoices().Where(v => TtsLanguageRouter.Supports(Id, v.Language)))
        {
            var id = prefix + v.Id["cpp_omni_".Length..];
            result[id] = new NativeVoice(id, $"{DisplayName} · {v.Name}", v.Gender, v.Locale, v.Language, true, false, v.Instruct, v.Seed);
        }
        if (family == "cosyvoice3")
            foreach (var (gender, title) in new[] { ("male", "Nam"), ("female", "Nữ") })
                result[prefix + "international_" + gender] = new NativeVoice(prefix + "international_" + gender, $"{DisplayName} · Quốc tế · {title}", gender,
                    "auto", "auto", true, false, gender + ", young adult, moderate pitch", gender == "male" ? 42 : 43);

        foreach (var p in await profiles.ListAsync(VoiceIds.OmniVoice, ct))
        {
            if (p.RefPath is null || !File.Exists(p.RefPath) || (family == "breeze" && string.IsNullOrEmpty(p.RefText))) continue;
            var id = prefix + (p.Id.StartsWith(VoiceIds.CloneOmniPrefix, StringComparison.Ordinal) ? p.Id["cpp_omni_".Length..] : "clone_" + p.Id);
            var locale = p.Language ?? "auto";
            // Mã đọc của mẫu clone quyết định CosyVoice3 dùng zero_shot hay cross_lingual (BuildPayload).
            var speech = LanguageCatalog.SpeechCode(locale);
            result[id] = new NativeVoice(id, $"{DisplayName} · {p.Name}", p.Gender ?? "", locale, speech, false, true,
                VoiceRef: p.RefPath, ReferenceText: p.RefText);
        }
        return result;
    }

    private async Task<Dictionary<string, NativeVoice>> LoadOmniVoiceAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, NativeVoice>();
        foreach (var v in LanguageCatalog.InternationalVoices())
            result[v.Id] = new NativeVoice(v.Id, v.Name, v.Gender, v.Locale, v.Language, true, false, v.Instruct, v.Seed);
        foreach (var (gender, title) in new[] { ("male", "Nam"), ("female", "Nữ") })
            result["cpp_omni_international_" + gender] = new NativeVoice("cpp_omni_international_" + gender, "Quốc tế · " + title, gender, "auto", "auto",
                true, false, gender + ", young adult, moderate pitch", gender == "male" ? 42 : 43);

        foreach (var p in await profiles.ListAsync(VoiceIds.OmniVoice, ct))
            if (p.RefPath is not null && File.Exists(p.RefPath))
                result[p.Id] = new NativeVoice(p.Id, p.Name, p.Gender ?? "", p.Language ?? "auto", "auto", false, true, VoiceRef: p.RefPath, ReferenceText: p.RefText);
        return result;
    }
}

[Service(As = [typeof(ITtsProvider)])]
public sealed class VieNeuTtsProvider(NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles, VieNeuEnroller enroller)
    : NativeTtsProvider("vieneu", server, settings, profiles)
{
    private static readonly SemaphoreSlim EnrollGate = new(1, 1);

    /// <summary>Codes + embedding tính một lần từ giọng neo OmniVoice, lưu cạnh file neo (tên neo đã chứa khoá giọng/model nên neo đổi thì tính lại).</summary>
    private protected override async Task<NativeVoice> FromAnchorAsync(NativeVoice voice, CancellationToken ct)
    {
        var anchor = voice.VoiceRef!;
        var (codes, embedding) = VieNeuEnroller.Files(anchor);
        await EnrollGate.WaitAsync(ct);
        try
        {
            if (!File.Exists(codes) || !File.Exists(embedding)) await enroller.EnrollAsync(anchor, ct);
        }
        finally
        {
            EnrollGate.Release();
        }
        return voice with { Instruct = null, VoiceRef = null, ReferenceText = null, ReferenceCodesFile = codes, SpeakerEmbeddingFile = embedding };
    }
}

[Service(As = [typeof(ITtsProvider)])]
public sealed class OmniVoiceTtsProvider(NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles)
    : NativeTtsProvider("omnivoice", server, settings, profiles);

[Service(As = [typeof(ITtsProvider)])]
public sealed class SupertonicTtsProvider(NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles)
    : NativeTtsProvider("supertonic", server, settings, profiles);

[Service(As = [typeof(ITtsProvider)])]
public sealed class Qwen3TtsProvider(NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles)
    : NativeTtsProvider("qwen3", server, settings, profiles);

[Service(As = [typeof(ITtsProvider)])]
public sealed class CosyVoice3TtsProvider(NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles)
    : NativeTtsProvider("cosyvoice3", server, settings, profiles);

[Service(As = [typeof(ITtsProvider)])]
public sealed class IrodoriTtsProvider(NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles)
    : NativeTtsProvider("irodori", server, settings, profiles);

[Service(As = [typeof(ITtsProvider)])]
public sealed class ChatterboxTtsProvider(NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles)
    : NativeTtsProvider("chatterbox", server, settings, profiles);

[Service(As = [typeof(ITtsProvider)])]
public sealed class BreezeTtsProvider(NativeTtsServerManager server, ISettingsStore settings, IVoiceProfileStore profiles)
    : NativeTtsProvider("breeze", server, settings, profiles);
