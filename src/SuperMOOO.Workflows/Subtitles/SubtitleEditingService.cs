using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Translation;
using SuperMOOO.AI.Tts;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Projects;

namespace SuperMOOO.Workflows.Subtitles;

/// <summary>Thao tác thuần trên danh sách câu của S02 (không đụng I/O để VM và test dùng trực tiếp).</summary>
public static class SubtitleEditing
{
    /// <summary>"90", "1:30.5", "00:01:30,500" → giây; sai định dạng hoặc phút/giây ngoài 0–60 thì ném FormatException.</summary>
    public static double ParseTime(string text) =>
        TryParseTime(text, out var value) ? value : throw new FormatException($"Mốc thời gian không hợp lệ: '{text}'. Nhập dạng 00:00:01.500.");

    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        var parts = (text ?? "").Trim().Replace(',', '.').Split(':');
        var nums = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (parts.Length > 3 || !double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out nums[i]) || !double.IsFinite(nums[i]) || nums[i] < 0)
                return false;

        // Phút và giây phải dưới 60 khi có nhiều thành phần.
        if (parts.Length > 1 && nums.Skip(1).Any(n => n >= 60)) return false;
        seconds = Math.Round(nums.Aggregate(0.0, (acc, n) => acc * 60 + n), 3);
        return true;
    }

    public static string FormatTime(double seconds) => TimeFormat.Srt(TimeSpan.FromSeconds(seconds));

    /// <summary>Thêm câu mới sau câu cuối: cách 0.5 s, dài 2.5 s (như bản cũ).</summary>
    public static IReadOnlyList<SubtitleEntry> Add(IReadOnlyList<SubtitleEntry> list)
    {
        var last = list.Count == 0 ? 0 : list.Max(e => e.End);
        return [.. list, new SubtitleEntry(Math.Round(last + 0.5, 3), Math.Round(last + 3.0, 3), "(Câu thoại mới)", "(Bản dịch mới)")];
    }

    public static IReadOnlyList<SubtitleEntry> Remove(IReadOnlyList<SubtitleEntry> list, int index) =>
        index < 0 || index >= list.Count ? throw new ArgumentOutOfRangeException(nameof(index)) : [.. list.Where((_, i) => i != index)];

    public static IReadOnlyList<SubtitleEntry> Update(IReadOnlyList<SubtitleEntry> list, int index, SubtitleEntry entry)
    {
        if (index < 0 || index >= list.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (SubtitleEntries.Validate([entry]) is { } error) throw new ArgumentException(error);
        // Sửa lời gốc thì mốc từng từ không còn khớp.
        var words = entry.Text == list[index].Text ? list[index].Words : null;
        return [.. list.Select((e, i) => i == index ? entry with { Words = words } : e)];
    }
}

/// <summary>S02: mở/lưu SRT, dịch từng câu hoặc cả file, nghe thử câu, áp lại vào dự án lồng tiếng.</summary>
[Service]
public sealed class SubtitleEditingService(TranslatorRouter translator, TtsService tts, ModelProfileService models, ProjectIndex projects, AppPaths paths)
{
    public async Task<IReadOnlyList<SubtitleEntry>> OpenSrtAsync(string path, CancellationToken ct = default)
    {
        var entries = SubtitleEntries.FromDocument(await SrtCodec.ParseFileAsync(path, ct));
        return entries.Count > 0 ? entries : throw new InvalidDataException("Không tìm thấy câu phụ đề SRT hợp lệ.");
    }

    public async Task SaveSrtAsync(IReadOnlyList<SubtitleEntry> entries, string path, bool useTranslation = true, CancellationToken ct = default)
    {
        if (entries.Count == 0) throw new InvalidOperationException("Chưa có phụ đề nào để xuất.");
        if (SubtitleEntries.Validate(entries) is { } error) throw new InvalidDataException(error);
        await SubtitleEntries.ToDocument(entries, useTranslation).SaveAsync(path, new SrtFormat(), ct);
    }

    public async Task<string> TranslateLineAsync(string text, string target, string? context = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Nhập lời thoại gốc cần dịch.");
        return (await translator.TranslateAsync([text.Trim()], new TranslateOptions("auto", target, context), ct))[0].Trim();
    }

    /// <summary>Dịch lại toàn bộ lời gốc (ghi đè bản dịch cũ), giữ đúng thứ tự và mốc thời gian.</summary>
    public async Task<IReadOnlyList<SubtitleEntry>> TranslateAllAsync(IReadOnlyList<SubtitleEntry> entries, string source, string target,
        string? context = null, CancellationToken ct = default)
    {
        var idx = entries.Select((e, i) => (e, i)).Where(x => !string.IsNullOrWhiteSpace(x.e.Text)).ToList();
        var translated = await translator.TranslateAsync(idx.Select(x => x.e.Text).ToList(), new TranslateOptions(source, target, context), ct);
        var result = entries.ToList();
        for (var k = 0; k < idx.Count; k++) result[idx[k].i] = idx[k].e with { Translation = translated[k].Trim(), Words = null };
        return result;
    }

    /// <summary>Đọc thử một câu bằng giọng mặc định của ngôn ngữ (giọng khách), trả đường dẫn WAV để AudioPlayer phát.</summary>
    public async Task<string> PreviewLineAsync(SubtitleEntry entry, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Display)) throw new ArgumentException("Câu này chưa có nội dung để đọc.");
        var (_, voice) = models.DefaultVoice(language, "guest");
        var output = Path.Combine(paths.CacheDir("preview"), $"line-{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(paths.CacheDir("preview"));
        return (await tts.SynthesizeAsync(new TtsRequest(entry.Display, voice, output, language), ct)).Path;
    }

    /// <summary>Lưu phụ đề vào dự án lồng tiếng của video nguồn để S01 dùng thay cho ASR.</summary>
    public async Task ApplyToProjectAsync(string source, IReadOnlyList<SubtitleEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) throw new InvalidOperationException("Chưa có phụ đề nào để áp dụng.");
        if (SubtitleEntries.Validate(entries) is { } error) throw new InvalidDataException(error);
        var folder = await projects.FolderForAsync(ProjectIndex.Localize, source, ct);
        var file = Path.Combine(folder, "subtitles.json");
        await SubtitleEntries.WriteAsync(file, entries, ct);
        await projects.SaveAsync(ProjectIndex.Localize, Path.GetFileNameWithoutExtension(source), folder, s =>
        {
            s["source"] = source;
            s["segmentsFile"] = file;
        }, ct);
    }
}
