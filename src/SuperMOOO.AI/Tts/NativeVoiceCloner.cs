using System.Text.RegularExpressions;
using SuperMOOO.AI.Catalog;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.AI.Tts;

/// <summary>
/// Clone OmniVoice: chuẩn hoá mẫu 3–30 s thành WAV mono 24 kHz, lưu hồ sơ vào voice_profile.
/// Mẫu tiếng Việt thêm codes + embedding VieNeu cạnh audio mẫu để câu tiếng Việt đọc bằng VieNeu (RTF ~0,3 so với ~15 của OmniVoice);
/// ngôn ngữ khác vẫn đọc bằng OmniVoice từ cùng mẫu.
/// </summary>
[Service(As = [typeof(IVoiceCloner)])]
public sealed partial class NativeVoiceCloner(IProcessRunner runner, IToolLocator tools, IVoiceProfileStore profiles, AppPaths paths,
    VieNeuEnroller vieneu, ILogger<NativeVoiceCloner> log) : IVoiceCloner
{
    private const int SampleRate = 24000;
    private static readonly TimeSpan MinSample = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxSample = TimeSpan.FromSeconds(30);

    public string ProviderId => VoiceIds.OmniVoice;

    public async Task<VoiceInfo> EnrollAsync(VoiceCloneRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Tên giọng cần 1–80 ký tự.");
        if (string.IsNullOrWhiteSpace(request.ReferenceText)) throw new ArgumentException("Nhập đúng lời nói có trong audio mẫu.");
        if (!File.Exists(request.ReferenceAudioPath)) throw new ArgumentException("Không tìm thấy audio mẫu.");
        var locale = request.Language is null ? "auto" : LanguageCatalog.LocaleCode(request.Language);

        Directory.CreateDirectory(paths.Voices);
        var id = VoiceIds.CloneOmniPrefix + Guid.NewGuid().ToString("N");
        var output = Path.Combine(paths.Voices, id + ".wav");
        try
        {
            var result = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.Ffmpeg),
                ["-y", "-hide_banner", "-loglevel", "error", "-i", request.ReferenceAudioPath, "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-c:a", "pcm_s16le", output]), null, ct);
            if (result.ExitCode != 0 || !File.Exists(output))
                throw new ArgumentException("Không đọc được audio mẫu (cần file có tiếng).");

            var (duration, _) = WavFile.DescribeFile(output);
            if (duration < MinSample || duration > MaxSample)
                throw new ArgumentException($"Mẫu giọng có tiếng cần dài 3–30 giây (hiện {duration.TotalSeconds:0.0} giây).");

            var speech = LanguageCatalog.SpeechCode(locale);
            if (speech == "vi" || (speech == "auto" && VietnameseLetters().IsMatch(request.ReferenceText)))
                await TryEnrollVieNeuAsync(output, ct);

            await profiles.UpsertAsync(new VoiceProfile(id, ProviderId, name, locale, null, output, request.ReferenceText.Trim()), ct);
            return new VoiceInfo(id, ProviderId, name, locale, null, true);
        }
        catch
        {
            DeleteFiles(output);
            throw;
        }
    }

    /// <summary>Lỗi thì giữ clone OmniVoice và ghi cảnh báo, không chặn việc tạo giọng.</summary>
    private async Task TryEnrollVieNeuAsync(string sample, CancellationToken ct)
    {
        if (!vieneu.IsReady()) return;
        try { await vieneu.EnrollAsync(sample, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Không tạo được clone VieNeu, giọng này đọc tiếng Việt bằng OmniVoice.");
        }
    }

    public async Task DeleteAsync(string voiceId, CancellationToken ct)
    {
        if (!voiceId.StartsWith(VoiceIds.CloneOmniPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Chỉ được xóa giọng clone đã lưu.");
        var profile = await profiles.GetAsync(voiceId, ct) ?? throw new ArgumentException("Không tìm thấy giọng clone.");

        // Chỉ xoá file nằm đúng trong thư mục giọng clone, tránh profile bị sửa trỏ ra ngoài.
        var folder = Path.GetFullPath(paths.Voices) + Path.DirectorySeparatorChar;
        var reference = profile.RefPath is null ? null : Path.GetFullPath(profile.RefPath);
        if (reference is not null && !reference.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Audio mẫu không thuộc thư mục giọng clone.");

        await profiles.DeleteAsync(voiceId, ct);
        if (reference is not null) DeleteFiles(reference);
    }

    private static void DeleteFiles(string sample)
    {
        VieNeuEnroller.Delete(sample);
        File.Delete(sample);
    }

    // Chữ chỉ có trong tiếng Việt: mẫu để "tự nhận diện" mà lời mẫu có các chữ này thì coi là tiếng Việt.
    [GeneratedRegex("[ăđơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ]", RegexOptions.IgnoreCase)]
    private static partial Regex VietnameseLetters();
}
