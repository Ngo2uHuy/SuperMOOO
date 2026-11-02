using SuperMOOO.AI.Catalog;

namespace SuperMOOO.AI.Tts;

/// <summary>
/// Bảng ưu tiên model theo ngôn ngữ: mỗi ngôn ngữ dùng model chuyên nhất đã cài (chất lượng trước, tốc độ sau).
/// Chỉ xét trạng thái đã cài; model lỗi lúc chạy thì báo lỗi chứ không đổi model giữa job, để một video không lẫn nhiều giọng.
/// Ưu tiên 1 của từng ngôn ngữ đổi được ở tts.native → LanguagePriority.
/// </summary>
[Service]
public sealed class TtsLanguageRouter(NativeTtsServerManager server, ISettingsStore settings)
{
    /// <summary>Khoá bảng cho các ngôn ngữ không có dòng riêng.</summary>
    public const string Other = "*";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Defaults { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        ["vi"] = [VoiceIds.VieNeu, VoiceIds.OmniVoice],
        ["ja"] = [VoiceIds.Irodori, VoiceIds.OmniVoice, VoiceIds.Supertonic],
        ["ko"] = [VoiceIds.Qwen3, VoiceIds.OmniVoice, VoiceIds.Supertonic],
        ["zh"] = [VoiceIds.CosyVoice3, VoiceIds.Qwen3, VoiceIds.OmniVoice],
        ["zh-TW"] = [VoiceIds.Breeze, VoiceIds.CosyVoice3, VoiceIds.Qwen3, VoiceIds.OmniVoice],
        ["yue"] = [VoiceIds.CosyVoice3, VoiceIds.Qwen3, VoiceIds.OmniVoice],
        ["en"] = [VoiceIds.Chatterbox, VoiceIds.Qwen3, VoiceIds.OmniVoice, VoiceIds.Supertonic],
        [Other] = [VoiceIds.OmniVoice, VoiceIds.Edge]
    };

    private static readonly string[] Natives = [VoiceIds.VieNeu, VoiceIds.Irodori, VoiceIds.Qwen3, VoiceIds.CosyVoice3, VoiceIds.Chatterbox, VoiceIds.Breeze, VoiceIds.OmniVoice, VoiceIds.Supertonic];

    // Ngôn ngữ trong model_specs của audio.cpp v0.9.2 (qwen3_tts.json, cosyvoice3.json).
    private static readonly HashSet<string> Qwen3Codes = ["zh", "en", "ja", "ko", "de", "fr", "ru", "pt", "es", "it"];
    private static readonly HashSet<string> CosyCodes = ["zh", "en", "ja", "ko", "de", "es", "fr", "it", "ru", "yue"];

    /// <summary>Giọng dựng sẵn của Qwen3-TTS CustomVoice (đọc được cả 10 ngôn ngữ, hay nhất ở ngôn ngữ gốc).</summary>
    internal static IReadOnlyList<(string Speaker, string Gender, string Language, string Name)> Qwen3Speakers { get; } =
    [
        ("vivian", "female", "zh", "Vivian · Nữ trẻ, trong (Trung)"),
        ("serena", "female", "zh", "Serena · Nữ ấm, dịu (Trung)"),
        ("uncle_fu", "male", "zh", "Uncle Fu · Nam trầm (Trung)"),
        ("dylan", "male", "zh", "Dylan · Nam trẻ (Trung, giọng Bắc Kinh)"),
        ("eric", "male", "zh", "Eric · Nam (Trung, giọng Thành Đô)"),
        ("ryan", "male", "en", "Ryan · Nam (Anh)"),
        ("aiden", "male", "en", "Aiden · Nam (Anh–Mỹ)"),
        ("ono_anna", "female", "ja", "Ono Anna · Nữ (Nhật)"),
        ("sohee", "female", "ko", "Sohee · Nữ (Hàn)")
    ];

    // Ngôn ngữ thiếu giọng gốc cùng giới tính (đo Whisper turbo: mọi giọng thử đều CER 0): chọn giọng cao độ ổn định, ít khoảng lặng.
    private static readonly Dictionary<(string Language, string Gender), string> Qwen3Fallback = new()
    {
        [("ko", "male")] = "aiden", [("ja", "male")] = "aiden", [("en", "female")] = "serena"
    };

    public static bool Supports(string provider, string speech) => speech == "auto" ? provider != VoiceIds.Supertonic : provider switch
    {
        VoiceIds.VieNeu => speech == "vi",
        VoiceIds.Supertonic => LanguageCatalog.SupertonicSpeaks(speech),
        VoiceIds.Qwen3 => Qwen3Codes.Contains(speech),
        VoiceIds.CosyVoice3 => CosyCodes.Contains(speech),
        VoiceIds.Irodori => speech == "ja",
        // Chatterbox đa ngữ nhưng mới đo tiếng Anh; Breeze theo breeze_tts.json (zh, en).
        VoiceIds.Chatterbox => speech == "en",
        VoiceIds.Breeze => speech is "zh" or "en",
        _ => true
    };

    /// <summary>Provider có thể đặt ưu tiên 1 cho một dòng của bảng (S16).</summary>
    public static IReadOnlyList<string> Candidates(string key) =>
        key == Other ? Defaults[Other] : [.. Natives.Where(p => Supports(p, SpeechOf(key)))];

    /// <summary>Dòng của bảng cho locale hoặc mã đọc: locale có dòng riêng (zh-TW) trước, rồi mã đọc (zh-CN → zh), còn lại "khác".</summary>
    public static string Key(string language)
    {
        if (language == Other) return Other;
        var locale = LanguageCatalog.LocaleCode(language);
        if (Defaults.ContainsKey(locale)) return locale;
        var speech = LanguageCatalog.SpeechCode(locale);
        return Defaults.ContainsKey(speech) ? speech : Other;
    }

    // Khoá bảng không có '-' đã là mã đọc (yue không có trong LanguageCatalog nên không qua SpeechCode).
    private static string SpeechOf(string language) =>
        language == Other || (Defaults.ContainsKey(language) && !language.Contains('-')) ? language : LanguageCatalog.SpeechCode(language);

    /// <summary>Thứ tự model cho locale/ngôn ngữ (ưu tiên 1 do người dùng chọn đứng đầu), chỉ gồm model đọc được ngôn ngữ đó.</summary>
    public IReadOnlyList<string> Chain(string language)
    {
        var key = Key(language);
        var speech = SpeechOf(language);
        var first = settings.Get<TtsNativeSettings>("tts.native").LanguagePriority.GetValueOrDefault(key);
        var chain = first is null ? Defaults[key] : Defaults[key].Prepend(first).Distinct();
        return [.. chain.Where(p => Supports(p, speech))];
    }

    public bool IsAvailable(string provider) => provider switch
    {
        VoiceIds.Edge => settings.Get<TtsEdgeSettings>("tts.edge").Enabled,
        _ when provider.StartsWith("native.", StringComparison.Ordinal) => server.IsInstalled(Family(provider)),
        _ => true
    };

    public static string Family(string provider) => provider["native.".Length..];

    /// <summary>Model đầu tiên đã cài trong bảng; chưa cài model nào thì trả model ưu tiên 1 để báo lỗi "chưa cài" rõ ràng.</summary>
    public string Pick(string language)
    {
        var chain = Chain(language);
        return chain.FirstOrDefault(IsAvailable) ?? chain.FirstOrDefault() ?? VoiceIds.OmniVoice;
    }

    /// <summary>Giọng mặc định theo ngôn ngữ và giới tính (Edge cần tra danh sách giọng nên do TtsService chọn).</summary>
    public (string Provider, string Voice) DefaultVoice(string? language, bool female)
    {
        var code = LanguageCatalog.LocaleCode(language);
        if (code == "auto") return (VoiceIds.VieNeu, VieNeuVoice(female));
        var chain = Chain(code).Where(p => p != VoiceIds.Edge).ToList();
        var provider = chain.FirstOrDefault(IsAvailable) ?? chain.FirstOrDefault() ?? VoiceIds.OmniVoice;
        return (provider, VoiceFor(provider, code, female)!);
    }

    public static string VieNeuVoice(bool female) => female ? "cpp_vieneu_ngoc_huyen" : "cpp_vieneu_minh_quan_pro";

    /// <summary>Giọng của provider cho locale/giới tính; null với provider không phải native (Edge, cloud).</summary>
    public static string? VoiceFor(string provider, string locale, bool female)
    {
        var gender = female ? "female" : "male";
        var speech = LanguageCatalog.SpeechCode(locale);
        return provider switch
        {
            VoiceIds.VieNeu => VieNeuVoice(female),
            VoiceIds.Supertonic => $"cpp_supertonic_{gender[0]}1",
            VoiceIds.Qwen3 => "cpp_qwen3_" + Qwen3Voice(speech, gender),
            VoiceIds.Irodori => "cpp_irodori_ja_" + gender,
            VoiceIds.CosyVoice3 => "cpp_cosy_" + InternationalSuffix(locale, gender),
            VoiceIds.Chatterbox => "cpp_chatterbox_" + InternationalSuffix(locale, gender),
            VoiceIds.Breeze => "cpp_breeze_" + InternationalSuffix(locale, gender),
            VoiceIds.OmniVoice => "cpp_omni_" + InternationalSuffix(locale, gender),
            _ => null
        };
    }

    private static string Qwen3Voice(string speech, string gender) =>
        Qwen3Speakers.FirstOrDefault(s => s.Language == speech && s.Gender == gender).Speaker
        ?? Qwen3Fallback.GetValueOrDefault((speech, gender))
        ?? (gender == "female" ? "serena" : "ryan");

    /// <summary>Phần sau "cpp_omni_" của giọng quốc tế đúng locale (hoặc cùng mã đọc); không có thì giọng "international".</summary>
    private static string InternationalSuffix(string locale, string gender)
    {
        var speech = LanguageCatalog.SpeechCode(locale);
        var selected = LanguageCatalog.All.FirstOrDefault(l => l.Code != "vi" && l.Code == locale)?.Code
            ?? LanguageCatalog.All.FirstOrDefault(l => l.Code != "vi" && LanguageCatalog.SpeechCode(l.Code) == speech)?.Code;
        return selected is null ? "international_" + gender : LanguageCatalog.InternationalVoiceId(selected, gender)["cpp_omni_".Length..];
    }
}
