using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using SuperMOOO.AI.Asr;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Translation;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.Workflows.Steps;

public sealed class AsrOptions
{
    public string Language { get; set; } = "auto";
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public bool WordTimestamps { get; set; } = true;
    public bool VadFilter { get; set; } = true;
    public bool ReuseCache { get; set; } = true;
}

/// <summary>Nhận diện lời thoại; bỏ qua nếu người dùng đã đưa sẵn phụ đề (input.segmentsFile), cache kết quả theo dấu vân tay file + cấu hình.</summary>
public sealed class AsrStep(AsrService asr, AppPaths paths) : PipelineStep<AsrOptions>
{
    public override string Kind => "asr";

    protected override async Task<StepOutput> RunAsync(StepContext c, AsrOptions o, CancellationToken ct)
    {
        var output = Io.Work(c, "segments.json");
        var duration = Io.NumOrNull(c, "duration");

        if (Io.Str(c, "segmentsFile") is { } provided)
        {
            var given = await SubtitleEntries.ReadAsync(provided, ct);
            if (SubtitleEntries.Validate(given, duration) is { } bad) throw new InvalidDataException(bad);
            await SubtitleEntries.WriteAsync(output, given, ct);
            return Io.Out([("segmentsFile", output)], output);
        }

        var cache = CacheFile(c, o);
        if (o.ReuseCache && File.Exists(cache) && await TryReadCacheAsync(cache, duration, ct) is { } cached)
        {
            await SubtitleEntries.WriteAsync(output, cached, ct);
            return Io.Out([("segmentsFile", output), ("asrCached", "true")], output);
        }

        var transcript = await asr.TranscribeAsync(new AsrRequest(Io.State(c, "audio"), o.Language == "auto" ? null : o.Language, o.Model,
            o.WordTimestamps, o.VadFilter), c.Progress, ct, o.Provider);
        var entries = SubtitleEntries.FromTranscript(transcript).Where(e => e.End > e.Start && e.Text.Length > 0).ToList();
        if (entries.Count == 0) throw new InvalidOperationException("Không nhận diện được lời thoại nào trong file này.");

        await SubtitleEntries.WriteAsync(output, entries, ct);
        if (o.ReuseCache)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var temp = cache + ".tmp";
            await SubtitleEntries.WriteAsync(temp, entries, ct);
            File.Move(temp, cache, overwrite: true);
        }

        return Io.Out([("segmentsFile", output), ("sourceLanguage", transcript.Language)], output);
    }

    private string CacheFile(StepContext c, AsrOptions o)
    {
        var source = new FileInfo(Io.Source(c));
        var key = string.Join('|', source.FullName, source.Length, source.LastWriteTimeUtc.Ticks, o.Language, o.Provider, o.Model,
            o.WordTimestamps, Io.StateOrNull(c, "denoisedAudio") is null ? "raw" : "denoised");
        return Path.Combine(paths.CacheDir("asr"), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    }

    private static async Task<IReadOnlyList<SubtitleEntry>?> TryReadCacheAsync(string path, double? duration, CancellationToken ct)
    {
        try
        {
            var entries = await SubtitleEntries.ReadAsync(path, ct);
            return entries.Count > 0 && SubtitleEntries.Validate(entries, duration) is null ? entries : null;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException)
        {
            return null;    // cache hỏng thì nhận diện lại
        }
    }
}

public sealed class TranslateStepOptions
{
    public string SourceLanguage { get; set; } = "auto";
    [Required] public string TargetLanguage { get; set; } = "vi";
    public string? Context { get; set; }
}

public sealed class TranslateStep(TranslatorRouter translator) : PipelineStep<TranslateStepOptions>
{
    public override string Kind => "translate";

    protected override async Task<StepOutput> RunAsync(StepContext c, TranslateStepOptions o, CancellationToken ct)
    {
        var entries = (await SubtitleEntries.ReadAsync(Io.State(c, "segmentsFile"), ct)).ToList();
        // Dịch cả câu đã có bản dịch nhưng sai chữ viết của ngôn ngữ đích (bản dịch cũ từ S02 sang ngôn ngữ khác),
        // nếu không giọng đọc nhận câu ngoại ngữ: VieNeu đọc câu Hàn/Nhật thành rỗng và lỗi "requires non-empty text".
        var pending = entries.Select((e, i) => (e, i))
            .Where(x => !string.IsNullOrWhiteSpace(x.e.Text)
                        && (string.IsNullOrWhiteSpace(x.e.Translation) || !LanguageCatalog.ScriptMatches(x.e.Translation, o.TargetLanguage)))
            .ToList();
        if (pending.Count > 0)
        {
            c.Progress.Report(new ProgressInfo(-1, $"Dịch {pending.Count} câu sang {o.TargetLanguage}"));
            var translated = await translator.TranslateAsync(pending.Select(x => x.e.Text).ToList(),
                new SuperMOOO.Core.Abstractions.TranslateOptions(o.SourceLanguage, o.TargetLanguage, o.Context), ct);
            if (translated.Count != pending.Count)
                throw new InvalidOperationException($"Bộ dịch trả {translated.Count} câu cho {pending.Count} câu nguồn.");
            for (var k = 0; k < pending.Count; k++)
                entries[pending[k].i] = pending[k].e with { Translation = translated[k].Trim(), Words = null };
        }

        var output = Io.Work(c, "segments.translated.json");
        await SubtitleEntries.WriteAsync(output, entries, ct);
        return Io.Out([("segmentsFile", output)], output);
    }
}

public sealed class ExportSubtitlesOptions
{
    public bool Srt { get; set; } = true;
    public bool Vtt { get; set; } = true;
    public string Language { get; set; } = "vi";
    public bool UseTranslation { get; set; } = true;
}

public sealed class ExportSubtitlesStep(IOutputRegistry outputs) : PipelineStep<ExportSubtitlesOptions>
{
    public override string Kind => "export_subtitles";

    protected override async Task<StepOutput> RunAsync(StepContext c, ExportSubtitlesOptions o, CancellationToken ct)
    {
        var entries = await SubtitleEntries.ReadAsync(Io.State(c, "segmentsFile"), ct);
        var doc = SubtitleEntries.ToDocument(entries, o.UseTranslation);
        var folder = Io.OutputDir(c);
        var stem = $"{Io.Name(c)}_{o.Language}";
        var files = new List<string>();
        var values = new List<(string, string)>();

        async Task Save(ISubtitleFormat format, string key)
        {
            var path = Path.Combine(folder, stem + format.Extension);
            await doc.SaveAsync(path, format, ct);
            await outputs.RegisterOutputAsync(path, "subtitles", ct);
            files.Add(path);
            values.Add((key, path));
        }

        if (o.Srt) await Save(new SrtFormat(), "subtitleFile");
        if (o.Vtt) await Save(new VttFormat(), "vttFile");
        if (files.Count == 0) throw new InvalidOperationException("Chưa chọn định dạng phụ đề nào để xuất.");
        return Io.Out(values, [.. files]);
    }
}
