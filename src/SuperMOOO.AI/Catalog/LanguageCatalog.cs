using System.Text.RegularExpressions;

namespace SuperMOOO.AI.Catalog;

public sealed record LanguageInfo(string Code, string Name, string Accent);

/// <summary>42 ngôn ngữ dùng chung: locale giữ nguyên cho văn bản, mã ISO ngắn cho giọng đọc (định tuyến model theo ngôn ngữ ở TtsLanguageRouter).</summary>
public static partial class LanguageCatalog
{
    // Mã giọng (theo SpeechCode) có trong model_specs/supertonic.json; zh/yue/th... không có nên định tuyến sang OmniVoice.
    private static readonly HashSet<string> SupertonicCodes =
        ["en", "ko", "ja", "arb", "cs", "da", "de", "el", "es", "fi", "fr", "hi", "hu", "id", "it", "nl", "pl", "pt", "ro", "ru", "sv", "tr", "uk", "vi"];

    public static bool SupertonicSpeaks(string speech) => SupertonicCodes.Contains(speech);

    // Accent chỉ có cho các biến thể tiếng Anh mà audio.cpp hỗ trợ.
    public static IReadOnlyList<LanguageInfo> All { get; } =
    [
        new("vi", "Tiếng Việt · Việt Nam", ""),
        new("en-US", "Tiếng Anh · Mỹ", "american accent"), new("en-GB", "Tiếng Anh · Anh", "british accent"),
        new("en-AU", "Tiếng Anh · Úc", "australian accent"), new("en-CA", "Tiếng Anh · Canada", "canadian accent"),
        new("en-IN", "Tiếng Anh · Ấn Độ", "indian accent"),
        new("zh-CN", "Tiếng Trung · giản thể", ""), new("zh-TW", "Tiếng Trung · phồn thể", ""),
        new("ja", "Tiếng Nhật · Nhật Bản", ""), new("ko", "Tiếng Hàn · Hàn Quốc", ""),
        new("fr-FR", "Tiếng Pháp · Pháp", ""), new("de-DE", "Tiếng Đức · Đức", ""),
        new("es-ES", "Tiếng Tây Ban Nha · Tây Ban Nha", ""), new("es-MX", "Tiếng Tây Ban Nha · Mexico", ""),
        new("pt-BR", "Tiếng Bồ Đào Nha · Brazil", ""), new("pt-PT", "Tiếng Bồ Đào Nha · Bồ Đào Nha", ""),
        new("it", "Tiếng Ý · Ý", ""), new("ru", "Tiếng Nga · Nga", ""),
        new("ar", "Tiếng Ả Rập", ""), new("hi", "Tiếng Hindi · Ấn Độ", ""),
        new("th", "Tiếng Thái · Thái Lan", ""), new("id", "Tiếng Indonesia", ""),
        new("ms", "Tiếng Mã Lai · Malaysia", ""), new("tl", "Tiếng Filipino · Philippines", ""),
        new("tr", "Tiếng Thổ Nhĩ Kỳ", ""), new("nl", "Tiếng Hà Lan", ""),
        new("pl", "Tiếng Ba Lan", ""), new("uk", "Tiếng Ukraina", ""),
        new("sv", "Tiếng Thụy Điển", ""), new("da", "Tiếng Đan Mạch", ""),
        new("fi", "Tiếng Phần Lan", ""), new("el", "Tiếng Hy Lạp", ""),
        new("cs", "Tiếng Séc", ""), new("ro", "Tiếng Romania", ""),
        new("hu", "Tiếng Hungary", ""), new("he", "Tiếng Hebrew · Israel", ""),
        new("fa", "Tiếng Ba Tư · Iran", ""), new("bn", "Tiếng Bengali", ""),
        new("ta", "Tiếng Tamil", ""), new("te", "Tiếng Telugu", ""),
        new("ur", "Tiếng Urdu · Pakistan", ""), new("sw", "Tiếng Swahili", "")
    ];

    [GeneratedRegex(@"\(([a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*|auto)\)$")]
    private static partial Regex LabelCode();

    [GeneratedRegex(@"^[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*$")]
    private static partial Regex Bcp47();

    // Mã ISO của OmniVoice cho 42 ngôn ngữ trên (tl → fil, ar → arb).
    private static readonly HashSet<string> SpeechCodes = [.. All.Select(l => ToSpeech(l.Code.Split('-')[0])), "auto"];

    private static string ToSpeech(string primary) => primary switch { "tl" => "fil", "ar" => "arb", "cmn" => "zh", "nb" => "no", "iw" => "he", _ => primary };

    /// <summary>Chuẩn hoá "Tiếng Việt (vi)", "en_us", "auto" thành locale dạng en-US; không hợp lệ thì ném ArgumentException.</summary>
    public static string LocaleCode(string? value)
    {
        value = (value ?? "auto").Trim();
        if (LabelCode().Match(value) is { Success: true } label) value = label.Groups[1].Value;
        value = value.Replace('_', '-');
        if (value.Length == 0 || value.Equals("auto", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "auto";
        if (!Bcp47().IsMatch(value)) throw new ArgumentException("Chọn ngôn ngữ hoặc nhập mã như en-US, ja, ko.");
        var parts = value.Split('-');
        return string.Join('-', new[] { parts[0].ToLowerInvariant() }.Concat(parts.Skip(1).Select(p => p.Length == 2 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant())));
    }

    public static string SpeechCode(string? value)
    {
        var code = LocaleCode(value);
        if (code == "auto") return "auto";
        var speech = ToSpeech(code.Split('-')[0]);
        return SpeechCodes.Contains(speech) ? speech : throw new ArgumentException($"Giọng native chưa hỗ trợ mã ngôn ngữ: {speech}.");
    }

    /// <summary>
    /// Chữ viết của câu có khớp ngôn ngữ không (chỉ phân biệt theo bảng chữ: Hangul, Kana, Hán, còn lại).
    /// Dùng để phát hiện bản dịch cũ của ngôn ngữ khác (vd. dịch tiếng Hàn ở S02 rồi lồng tiếng Việt ở S01); không phân biệt được Việt/Anh.
    /// </summary>
    public static bool ScriptMatches(string text, string? language)
    {
        var code = LocaleCode(language).Split('-')[0];
        if (code == "auto" || string.IsNullOrWhiteSpace(text)) return true;
        bool hangul = false, kana = false, han = false;
        foreach (var ch in text)
        {
            if (ch is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏') hangul = true;
            else if (ch is >= '぀' and <= 'ヿ') kana = true;
            else if (ch is >= '一' and <= '鿿' or >= '㐀' and <= '䶿') han = true;
        }

        return code switch
        {
            "ko" => hangul,
            "ja" => (kana || han) && !hangul,
            "zh" or "yue" => han && !kana && !hangul,
            _ => !hangul && !kana && !han
        };
    }

    public static string TextLanguage(string? value)
    {
        var code = LocaleCode(value);
        if (code == "auto") return "ngôn ngữ nguồn tự nhận diện";
        var name = All.FirstOrDefault(l => l.Code == code)?.Name ?? All.FirstOrDefault(l => l.Code.Split('-')[0] == code.Split('-')[0])?.Name ?? code;
        return $"{name} [{code}]";
    }

    public static string SampleText(string? value) => SpeechCode(value) switch
    {
        "vi" => "Xin chào. Chào mừng bạn đến với Video Studio.",
        "ja" => "こんにちは。ビデオスタジオへようこそ。",
        "ko" => "안녕하세요. 비디오 스튜디오에 오신 것을 환영합니다.",
        "zh" => "你好，欢迎来到视频工作室。",
        "fr" => "Bonjour. Bienvenue dans notre studio vidéo.",
        "de" => "Hallo. Willkommen in unserem Videostudio.",
        "es" => "Hola. Bienvenido a nuestro estudio de vídeo.",
        "pt" => "Olá. Bem-vindo ao nosso estúdio de vídeo.",
        "ru" => "Здравствуйте. Добро пожаловать в нашу видеостудию.",
        "it" => "Ciao. Benvenuti nel nostro studio video.",
        "id" => "Halo. Selamat datang di studio video kami.",
        "th" => "สวัสดี ยินดีต้อนรับสู่สตูดิโอวิดีโอของเรา",
        "tr" => "Merhaba. Video stüdyomuza hoş geldiniz.",
        _ => "Hello. Welcome to Video Studio."
    };

    public static string InternationalVoiceId(string locale, string gender) =>
        $"cpp_omni_{locale.ToLowerInvariant().Replace('-', '_')}_{gender}";

    /// <summary>Giọng OmniVoice dựng sẵn theo ngôn ngữ (không gồm tiếng Việt, thuộc VieNeu).</summary>
    public static IEnumerable<(string Id, string Name, string Locale, string Language, string Gender, string Instruct, int Seed)> InternationalVoices()
    {
        foreach (var l in All.Where(l => l.Code != "vi"))
            foreach (var (gender, title) in new[] { ("male", "Nam"), ("female", "Nữ") })
                yield return (InternationalVoiceId(l.Code, gender), $"{l.Name} · {title}", l.Code, SpeechCode(l.Code), gender,
                    $"{gender}, young adult, moderate pitch" + (l.Accent.Length > 0 ? ", " + l.Accent : ""), gender == "male" ? 42 : 43);
    }
}
