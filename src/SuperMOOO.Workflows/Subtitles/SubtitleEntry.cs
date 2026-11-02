using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.Workflows.Subtitles;

/// <summary>Một câu phụ đề có cả lời gốc lẫn bản dịch (mô hình dùng chung S01/S02 và các bước pipeline).</summary>
public sealed record SubtitleEntry(double Start, double End, string Text, string Translation = "", IReadOnlyList<WordTiming>? Words = null)
{
    /// <summary>Văn bản dùng để đọc/hiển thị: bản dịch nếu có, ngược lại lời gốc.</summary>
    public string Display => string.IsNullOrWhiteSpace(Translation) ? Text : Translation;
}

public static class SubtitleEntries
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Từng từ chỉ khớp lời gốc, nên bản dịch để AssWriter tự chia theo ký tự.</summary>
    public static SubtitleDocument ToDocument(IEnumerable<SubtitleEntry> entries, bool useTranslation = true) =>
        new(entries.Where(e => e.End > e.Start && !string.IsNullOrWhiteSpace(useTranslation ? e.Display : e.Text))
            .Select((e, i) => new SubtitleCue(i + 1, TimeSpan.FromSeconds(e.Start), TimeSpan.FromSeconds(e.End),
                (useTranslation ? e.Display : e.Text).Trim(), useTranslation && !string.IsNullOrWhiteSpace(e.Translation) ? null : e.Words))
            .ToList());

    public static IReadOnlyList<SubtitleEntry> FromDocument(SubtitleDocument document) =>
        document.Cues.Select(c => new SubtitleEntry(Math.Round(c.Start.TotalSeconds, 3), Math.Round(c.End.TotalSeconds, 3), c.Text, "", c.Words)).ToList();

    public static IReadOnlyList<SubtitleEntry> FromTranscript(Transcript t) =>
        t.Segments.Select(s => new SubtitleEntry(Math.Round(s.Start.TotalSeconds, 3), Math.Round(s.End.TotalSeconds, 3), s.Text.Trim(), "", s.Words)).ToList();

    public static async Task<IReadOnlyList<SubtitleEntry>> ReadAsync(string path, CancellationToken ct = default) =>
        JsonSerializer.Deserialize<List<SubtitleEntry>>(await File.ReadAllTextAsync(path, ct), Json)
        ?? throw new InvalidDataException($"File phụ đề không hợp lệ: {path}");

    public static async Task WriteAsync(string path, IEnumerable<SubtitleEntry> entries, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entries, Json), ct);
    }

    /// <summary>Kiểm tra mốc thời gian: hữu hạn, không âm, kết thúc sau bắt đầu (và không vượt quá tổng + 0.05 s nếu biết).</summary>
    public static string? Validate(IEnumerable<SubtitleEntry> entries, double? total = null) =>
        entries.Any(e => !double.IsFinite(e.Start) || !double.IsFinite(e.End) || e.Start < 0 || e.End <= e.Start || (total is { } t && e.End > t + 0.05))
            ? "Mốc thời gian phụ đề không hợp lệ (kết thúc phải sau bắt đầu, không âm, không vượt thời lượng)."
            : null;
}
