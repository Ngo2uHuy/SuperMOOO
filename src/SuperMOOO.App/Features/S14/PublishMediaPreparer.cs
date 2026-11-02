using System.IO;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Publishing.Contracts;
using SuperMOOO.Publishing.Library;

namespace SuperMOOO.App.Features.S14;

[Service]
public sealed class PublishMediaPreparer(IContentRepository contents, ContentIngestService ingest, FfmpegRunner ffmpeg, AppPaths paths)
{
    public async Task<List<PublicationTargetRequest>> PrepareAsync(List<PublicationTargetRequest> items,
        IReadOnlyDictionary<string, string> platforms, bool strip, bool crop, string? logo, IProgress<string> progress, CancellationToken ct)
    {
        if (!strip && !crop && logo is null) return items;
        if (logo is not null && !File.Exists(logo)) throw new IOException("Không tìm thấy ảnh logo.");
        var mapped = new Dictionary<(string, string), string>();
        var result = new List<PublicationTargetRequest>();
        foreach (var item in items)
        {
            var platform = platforms[item.DestinationId];
            var key = (item.ContentItemId, crop ? platform : "");
            if (!mapped.TryGetValue(key, out var id))
            {
                var detail = await contents.GetAsync(item.ContentItemId, ct) ?? throw new IOException("Nội dung không còn trong kho.");
                var assets = detail.Assets.Where(a => a.Kind is MediaKind.Video or MediaKind.Image).OrderBy(a => a.Id).ToList();
                if (assets.Count == 0) throw new IOException("Nội dung không có ảnh hoặc video.");
                var outputs = new List<string>();
                foreach (var asset in assets)
                {
                    progress.Report($"Đang chuẩn bị {detail.Item.Title} · {platform} ({outputs.Count + 1}/{assets.Count})");
                    var image = asset.Kind == MediaKind.Image;
                    var output = Path.Combine(paths.CacheDir("publish-media"), Guid.NewGuid().ToString("N") + (image ? ".jpg" : ".mp4"));
                    List<string> args = ["-i", asset.StoredPath];
                    if (logo is not null) args.AddRange(["-i", logo]);
                    var (width, height) = platform switch
                    {
                        "TikTok" => (1080, 1920),
                        "Instagram" when !image => (1080, 1920),
                        "Instagram" => (1080, 1350),
                        "YouTube" => (1920, 1080),
                        _ => (1080, 1080)
                    };
                    var filter = crop ? $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1" : "null";
                    if (logo is not null)
                    {
                        args.AddRange(["-filter_complex", $"[0:v]{filter}[base];[1:v]scale=120:-1[logo];[base][logo]overlay=W-w-20:H-h-20:shortest=0:eof_action=repeat[v]", "-map", "[v]"]);
                    }
                    else if (crop) args.AddRange(["-vf", filter]);
                    else args.AddRange(["-map", "0:v:0"]);
                    if (strip) args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1"]);
                    if (image) args.AddRange(["-frames:v", "1", "-q:v", "2", "-update", "1"]);
                    else
                    {
                        args.AddRange(["-map", "0:a?", "-c:v", crop || logo is not null ? "libx264" : "copy"]);
                        if (crop || logo is not null) args.AddRange(["-preset", "fast", "-crf", "20", "-pix_fmt", "yuv420p"]);
                        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
                    }
                    await ffmpeg.RunAsync(new FfmpegJob(args, output, Label: "Chuẩn bị media đăng bài"), null, ct);
                    outputs.Add(output);
                }
                id = assets[0].Kind == MediaKind.Image
                    ? (await ingest.IngestImagesAsync(outputs, detail.Item.Title, ct)).ContentId
                    : (await ingest.IngestAsync(LooseFileReader.Build(outputs[0], LibrarySources.Manual, null, false)
                        with { Title = detail.Item.Title, IsAiGenerated = detail.Item.IsAiGenerated }, ct)).ContentId;
                mapped.Add(key, id);
            }
            result.Add(item with { ContentItemId = id });
        }
        return result;
    }
}

public sealed partial class ComposeViewModel
{
    [ObservableProperty] public partial bool StripMetadata { get; set; }
    [ObservableProperty] public partial bool AutoCrop { get; set; }
    [ObservableProperty] public partial bool AddWatermark { get; set; }
    [ObservableProperty] public partial string LogoPath { get; set; } = "";
    [RelayCommand]
    private void PickLogo()
    {
        if (dialogs.OpenFile("Chọn logo", "Ảnh|*.png;*.jpg;*.jpeg;*.webp") is { } path) LogoPath = path;
    }
}
