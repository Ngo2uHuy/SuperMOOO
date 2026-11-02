using System.Security.Cryptography;
using System.Text;
using SuperMOOO.AI.Catalog;

namespace SuperMOOO.AI.Tts;

/// <summary>Điểm vào TTS: định tuyến theo voice id, cache theo chữ ký, preview.</summary>
[Service]
public sealed class TtsService(IEnumerable<ITtsProvider> providers, ISettingsStore settings, AppPaths paths, TtsLanguageRouter router,
    VieNeuEnroller vieneuEnroller)
{
    private const int MaxChars = 20000;

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct)
    {
        var text = request.Text.Trim();
        if (text.Length is 0 or > MaxChars) throw new ArgumentException($"Văn bản cần từ 1 đến {MaxChars:N0} ký tự.");

        var (provider, voice) = await RouteAsync(request, ct);
        request = request with { Text = text, VoiceId = voice };

        var cached = await CachePathAsync(provider, request, ct);
        if (File.Exists(cached) && TryCopyFromCache(cached, request.OutputPath, out var hit)) return hit;

        var result = await provider.SynthesizeAsync(request, ct);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.Copy(result.Path, cached, overwrite: true);
        }
        catch (IOException) { /* cache chỉ để tăng tốc, lỗi ghi không làm hỏng kết quả */ }

        return result;
    }

    /// <summary>Nạp trước model/giọng mà job sẽ dùng (định tuyến theo ngôn ngữ như lúc đọc thật); provider không phải native thì bỏ qua.</summary>
    public async Task WarmAsync(string voiceId, string? language, CancellationToken ct)
    {
        var (provider, voice) = await RouteAsync(new TtsRequest("", voiceId, "", language), ct);
        if (provider is NativeTtsProvider native) await native.WarmAsync(voice, language, ct);
    }

    /// <summary>Đọc câu mẫu của ngôn ngữ giọng để nghe thử; file nằm trong cache\preview.</summary>
    public Task<TtsResult> PreviewAsync(string voiceId, string? language, CancellationToken ct)
    {
        var locale = language ?? LocaleOfVoice(voiceId) ?? settings.Get<TtsNativeSettings>("tts.native").Language;
        var output = Path.Combine(paths.CacheDir("preview"), $"preview-{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(paths.CacheDir("preview"));
        return SynthesizeAsync(new TtsRequest(LanguageCatalog.SampleText(locale), voiceId, output, locale), ct);
    }

    /// <summary>Provider của giọng có tự đọc theo Rate không; không thì nơi gọi phải chỉnh tốc độ bằng atempo.</summary>
    public bool SupportsRate(string voiceId) => Find(VoiceIdMigrator.Migrate(null, voiceId).ProviderId).Capabilities.SupportsRate;

    private static string? LocaleOfVoice(string voiceId) =>
        voiceId.StartsWith(VoiceIds.EdgePrefix, StringComparison.Ordinal) ? string.Join('-', voiceId[VoiceIds.EdgePrefix.Length..].Split('-').Take(2)) : null;

    /// <summary>
    /// Giọng VieNeu (mặc định) đọc theo bảng ưu tiên ngôn ngữ của TtsLanguageRouter; giọng native khác do người dùng chọn được giữ,
    /// chỉ chuyển khi model đó không đọc được ngôn ngữ. Luôn giữ giới tính của giọng đã chọn; clone và giọng cloud giữ nguyên.
    /// </summary>
    private async Task<(ITtsProvider Provider, string Voice)> RouteAsync(TtsRequest request, CancellationToken ct)
    {
        var migrated = VoiceIdMigrator.Migrate(null, request.VoiceId);
        var provider = Find(migrated.ProviderId);
        if (provider is not NativeTtsProvider || request.Language is null) return (provider, migrated.VoiceId);
        var locale = LanguageCatalog.LocaleCode(request.Language);
        var speech = LanguageCatalog.SpeechCode(locale);
        // Clone mẫu tiếng Việt có codes + embedding VieNeu: câu tiếng Việt đọc bằng VieNeu (nhanh hơn OmniVoice ~40 lần), ngôn ngữ khác vẫn OmniVoice.
        if (speech == "vi" && migrated.VoiceId.StartsWith(VoiceIds.CloneOmniPrefix, StringComparison.Ordinal)
            && router.Pick(speech) == VoiceIds.VieNeu && router.IsAvailable(VoiceIds.VieNeu))
        {
            var clone = VoiceIds.CloneVieNeuPrefix + migrated.VoiceId[VoiceIds.CloneOmniPrefix.Length..];
            var vieneu = Find(VoiceIds.VieNeu);
            if ((await vieneu.ListVoicesAsync(null, ct)).Any(v => v.Id == clone)) return (vieneu, clone);
        }
        // Giọng "Quốc tế" đọc tiếng Việt: VieNeu đọc theo giọng neo OmniVoice. Job quyết định ở WarmAsync (cũng qua đây);
        // thiếu worker/model thì giữ OmniVoice, còn lỗi lúc tính embedding thì báo lỗi chứ không đổi model giữa job.
        if (speech == "vi" && migrated.VoiceId.StartsWith("cpp_omni_international_", StringComparison.Ordinal)
            && router.Pick(speech) == VoiceIds.VieNeu && router.IsAvailable(VoiceIds.VieNeu) && vieneuEnroller.IsReady())
            return (Find(VoiceIds.VieNeu), "cpp_vieneu_" + migrated.VoiceId["cpp_omni_".Length..]);
        if (speech == "auto" || (provider.Id != VoiceIds.VieNeu && TtsLanguageRouter.Supports(provider.Id, speech))) return (provider, migrated.VoiceId);

        // Theo locale: zh-TW có dòng riêng trong bảng, không gộp vào zh.
        var target = router.Pick(locale);
        if (target == provider.Id) return (provider, migrated.VoiceId);
        var voices = await provider.ListVoicesAsync(null, ct);
        var current = voices.FirstOrDefault(v => v.Id == migrated.VoiceId);
        // VieNeu chưa cài thì không có danh sách giọng: vẫn chuyển theo bảng, giới tính đoán theo giọng mặc định.
        if (current is null && provider.Id != VoiceIds.VieNeu) throw new ArgumentException("Giọng native không tồn tại: " + migrated.VoiceId);
        // Clone của CosyVoice3/Irodori đọc ngôn ngữ model không có thì dùng chính mẫu clone đó qua OmniVoice.
        if (current?.IsCloned == true)
            return migrated.VoiceId.IndexOf("_clone_", StringComparison.Ordinal) is > 0 and var i ? (Find(VoiceIds.OmniVoice), "cpp_omni" + migrated.VoiceId[i..]) : (provider, migrated.VoiceId);
        var female = current is null ? migrated.VoiceId == TtsLanguageRouter.VieNeuVoice(true) : current.Gender == "female";
        if (TtsLanguageRouter.VoiceFor(target, locale, female) is { } voice) return (Find(target), voice);

        // Edge (ngôn ngữ OmniVoice chưa cài): lấy giọng Edge đúng ngôn ngữ, ưu tiên cùng giới tính.
        var edge = await Find(target).ListVoicesAsync(locale, ct);
        var pick = edge.FirstOrDefault(v => v.Gender == (female ? "female" : "male")) ?? edge.FirstOrDefault()
            ?? throw new ArgumentException($"Chưa có giọng đọc cho ngôn ngữ {speech}; cài OmniVoice hoặc bật Edge TTS.");
        return (Find(target), pick.Id);
    }

    private ITtsProvider Find(string id) => providers.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException($"Provider TTS '{id}' chưa được đăng ký.");

    private async Task<string> CachePathAsync(ITtsProvider provider, TtsRequest r, CancellationToken ct)
    {
        var stamp = provider is ITtsCacheStamp s ? await s.CacheStampAsync(r, ct) : "";
        var key = string.Join('\u001f', provider.Id, r.VoiceId, r.Language, r.Rate.ToString("R"), r.PitchSemitones.ToString("R"), stamp, r.Text);
        return Path.Combine(paths.CacheDir("tts"), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".wav");
    }

    private static bool TryCopyFromCache(string cached, string output, out TtsResult result)
    {
        result = default!;
        try
        {
            var (duration, rate) = WavFile.DescribeFile(cached);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.Copy(cached, output, overwrite: true);
            result = new TtsResult(output, duration, rate);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return false;       // cache hỏng thì tổng hợp lại
        }
    }
}
