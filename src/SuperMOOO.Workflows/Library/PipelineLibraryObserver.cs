using Microsoft.Extensions.Logging;
using SuperMOOO.Publishing.Library;

namespace SuperMOOO.Workflows.Library;

/// <summary>
/// Điểm kết thúc chung của mọi pipeline: có video thành phẩm thì ghi thẳng vào kho (kèm SEO, request_id, cờ AI/reup theo pipeline) qua
/// <see cref="ContentIngestService"/>. Lỗi chỉ log (PipelineJobHandler cũng bắt) để không làm hỏng job đã xong.
/// </summary>
public sealed class PipelineLibraryObserver(ContentIngestService ingest, ILogger<PipelineLibraryObserver> log) : IPipelineCompletionObserver
{
    // Pipeline không có video thành phẩm (chỉ audio/tải về).
    private static readonly IReadOnlySet<string> NoVideo = new HashSet<string> { "audio", "voice", "download" };

    // S03 (talkshow, youtube_creator), S18 (whiteboard), S19 (avatar_video) tạo nội dung bằng AI; S06/S08 (reup) là nội dung đăng lại.
    private static readonly IReadOnlySet<string> Ai = new HashSet<string> { "talkshow", "youtube_creator", "whiteboard", "avatar_video" };
    private const string Reup = "reup";

    public async Task OnCompletedAsync(PipelineCompletion c, CancellationToken ct)
    {
        if (NoVideo.Contains(c.PipelineId)) return;
        var video = PipelineResult.Value(c.Result, "video");
        if (video is null || !File.Exists(video)) return;

        var folder = Path.GetDirectoryName(video)!;
        var whiteboard = c.PipelineId == "whiteboard";
        // Pipeline whiteboard ghi sẵn meta.json/SEO.txt/subtitles.srt cạnh video: lấy tiêu đề, mô tả, tags từ đó.
        var seo = whiteboard ? ReadSeo(Path.Combine(folder, "SEO.txt")) : null;
        var title = (whiteboard ? ReadMetaTitle(Path.Combine(folder, "meta.json")) : null) ?? Str(c.Input, "title") ?? Str(c.Input, "name") ?? "";
        var srt = Path.ChangeExtension(video, ".srt");
        if (!File.Exists(srt)) srt = whiteboard && File.Exists(Path.Combine(folder, "subtitles.srt")) ? Path.Combine(folder, "subtitles.srt") : null;
        string? thumb = null;
        foreach (var name in new[] { "thumbnail.png", "thumbnail.jpg" })
            if (File.Exists(Path.Combine(folder, name))) { thumb = Path.Combine(folder, name); break; }
        var tags = seo?.Tags ?? [.. (c.Input["tags"] as JsonArray ?? []).Select(t => t?.ToString() ?? "").Where(t => t.Length > 0)];

        try
        {
            await ingest.IngestAsync(new IngestRequest(video, title, LibrarySources.Pipeline, ContentIngestService.PipelineContentId(c.JobId, Path.GetFileName(video)), c.JobId,
                seo?.Description ?? Str(c.Input, "caption"), Language(c.Input), tags, Ai.Contains(c.PipelineId), c.PipelineId == Reup,
                Str(c.Input, "requestId") ?? Str(c.Input, "centerPostRequestId"), ThumbnailPath: thumb, SubtitlePath: srt), ct);
        }
        catch (IngestException ex) { log.LogWarning(ex, "Không ghi được video vào kho thành phẩm: {Video}", video); }
    }

    private static string? ReadMetaTitle(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path))?["title"]?.GetValue<string>() is { Length: > 0 } t ? t : null; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return null; }
    }

    // SEO.txt do WhiteboardFinalizeStep ghi: các mục "MÔ TẢ:" và "TAGS (...):" mỗi mục kết thúc ở tiêu đề mục kế tiếp.
    private static (string Description, List<string> Tags)? ReadSeo(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (IOException) { return null; }

        var description = new List<string>();
        var tags = new List<string>();
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("MÔ TẢ:", StringComparison.Ordinal)) current = description;
            else if (line.StartsWith("TAGS", StringComparison.Ordinal)) current = tags;
            else if (line.StartsWith("TIÊU ĐỀ:", StringComparison.Ordinal) || line.StartsWith("HASHTAGS:", StringComparison.Ordinal)) current = null;
            else current?.Add(line);
        }

        return (string.Join('\n', description).Trim(),
            [.. string.Join(',', tags).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]);
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static string? Language(JsonObject input)
    {
        var steps = input["stepOptions"] as JsonObject;
        return Str(input, "language") ?? (steps?["stage"] as JsonObject is { } stage ? Str(stage, "language") : null)
            ?? (steps?["tts"] as JsonObject is { } tts ? Str(tts, "language") : null);
    }
}
