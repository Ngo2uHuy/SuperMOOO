using System.ComponentModel.DataAnnotations;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Render;
using SuperMOOO.Social.Download;
using SuperMOOO.Workflows.Creator;
using SuperMOOO.Workflows.Subtitles;

namespace SuperMOOO.Workflows.Steps;

public sealed class DownloadOptions
{
    public DownloadFormat Format { get; set; } = DownloadFormat.Best;
    public bool NoWatermark { get; set; } = true;
    public string? Folder { get; set; }
}

/// <summary>Nguồn là URL: tải về rồi thay `source` cho các bước sau (tiền tố tự động của Auto/Reup).</summary>
public sealed class DownloadStep(IDownloader downloader, DownloadFolders folders, IOutputRegistry outputs) : PipelineStep<DownloadOptions>
{
    public override string Kind => "download";

    protected override async Task<StepOutput> RunAsync(StepContext c, DownloadOptions o, CancellationToken ct)
    {
        var url = Io.Require(c, "source");
        if (!Auto.SourceKinds.IsUrl(url)) throw new InvalidOperationException("Bước tải chỉ nhận liên kết http(s).");
        var result = await downloader.DownloadAsync(new DownloadRequest(url, folders.Resolve(o.Folder), o.Format, o.NoWatermark,
            Io.Str(c, "cookiePlatform")), c.Progress, ct);
        await outputs.RegisterOutputAsync(result.FilePath, "download", ct);
        return Io.Out([("source", result.FilePath), ("sourceUrl", url), ("sourceTitle", result.Info.Title ?? "")], result.FilePath);
    }
}

public sealed class SegmentSourceOptions
{
    [Range(5, 600)] public double Seconds { get; set; } = 60;
}

/// <summary>Chia nguồn thành các phần bằng nhau khi chưa có thoại để dựng review/tóm tắt (port segment_source).</summary>
public sealed class SegmentSourceStep(SegmentSourceService segments) : PipelineStep<SegmentSourceOptions>
{
    public override string Kind => "segment_source";

    protected override async Task<StepOutput> RunAsync(StepContext c, SegmentSourceOptions o, CancellationToken ct)
    {
        var clips = await segments.SplitAsync(Io.Source(c), o.Seconds, ct);
        var file = Io.Work(c, "clips.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(clips, SubtitleEntries.Json), ct);
        return Io.Out([("clipsFile", file)], file);
    }
}

public sealed class ScriptReadOptions
{
    [Range(1, 200_000)] public int MaxChars { get; set; } = 20_000;
}

/// <summary>Kịch bản: văn bản có sẵn trong input.script hoặc file TXT/DOCX (input.source). Kết quả ở state `script`.</summary>
public sealed class ScriptReadStep(ScriptLibrary scripts) : PipelineStep<ScriptReadOptions>
{
    public override string Kind => "script_read";

    protected override async Task<StepOutput> RunAsync(StepContext c, ScriptReadOptions o, CancellationToken ct)
    {
        var text = Io.Str(c, "script") ?? await scripts.ReadAsync(Io.Require(c, "source"), ct);
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("Kịch bản rỗng.");
        if (text.Length > o.MaxChars) throw new InvalidOperationException($"Kịch bản tối đa {o.MaxChars:N0} ký tự (hiện {text.Length:N0}).");
        return Io.Out([("script", text)]);
    }
}
