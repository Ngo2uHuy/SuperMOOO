using SuperMOOO.Data.Repositories.Files;
using SuperMOOO.Data.Repositories.Library;

namespace SuperMOOO.Social.Download;

/// <summary>Thư mục tải: tuỳ chọn trong job &gt; setting "download" &gt; &lt;thư mục xuất&gt;\download.</summary>
[Service]
public sealed class DownloadFolders(ISettingsStore settings)
{
    public string Resolve(string? overrideFolder = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideFolder)) return Path.GetFullPath(overrideFolder.Trim());
        var configured = settings.Get<DownloadSettings>("download").DownloadDir;
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
        var root = settings.Get<PathsSettings>("paths").OutputRoot;
        return Path.Combine(string.IsNullOrWhiteSpace(root) ? AppPaths.DefaultOutputRoot : root, "download");
    }
}

public sealed record DownloadJobRequest(string Url, DownloadFormat? Format = null, bool? NoWatermark = null, string? Folder = null,
    string? CookiePlatform = null);

/// <summary>Xếp job `download` (lane Network). Payload `{ url, format?, noWatermark?, folder?, cookiePlatform? }`.</summary>
[Service]
public sealed class DownloadService(IJobQueue jobs)
{
    public async Task<string> EnqueueAsync(DownloadJobRequest request, CancellationToken ct = default)
    {
        var url = (request.Url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Liên kết không hợp lệ: {url}", nameof(request));

        var payload = new JsonObject { ["url"] = url };
        if (request.Format is { } format) payload["format"] = format.ToString();
        if (request.NoWatermark is { } noWatermark) payload["noWatermark"] = noWatermark;
        if (!string.IsNullOrWhiteSpace(request.Folder)) payload["folder"] = request.Folder;
        if (!string.IsNullOrWhiteSpace(request.CookiePlatform)) payload["cookiePlatform"] = request.CookiePlatform;
        return await jobs.EnqueueAsync(new JobRequest(JobTypes.Download, url, payload, JobLane.Network), ct);
    }
}

/// <summary>Chạy job `download`: tải bằng IDownloader rồi ghi file vào media_file (kind download).</summary>
public sealed class DownloadJobHandler(IDownloader downloader, DownloadFolders folders, ISettingsStore settings,
    MediaFileRepository files, LibraryRepository library) : IJobHandler
{
    public string Type => JobTypes.Download;

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload.AsObject();
        var url = payload["url"]?.GetValue<string>() ?? throw new InvalidOperationException("Payload thiếu trường 'url'.");
        var defaults = settings.Get<DownloadSettings>("download");
        var format = payload["format"]?.GetValue<string>() is { } f && Enum.TryParse<DownloadFormat>(f, true, out var parsed) ? parsed : defaults.Format;
        var noWatermark = payload["noWatermark"]?.GetValue<bool>() ?? defaults.NoWatermark;

        var request = new DownloadRequest(url, folders.Resolve(payload["folder"]?.GetValue<string>()), format, noWatermark,
            payload["cookiePlatform"]?.GetValue<string>());
        var result = await downloader.DownloadAsync(request, context.Progress, ct);

        var mediaId = await files.UpsertAsync(new MediaFileRecord(result.FilePath, "download", format.ToString(), url, result.SizeBytes,
            DurationMs: (long?)result.Info.Duration?.TotalMilliseconds, JobId: context.JobId), ct);
        await library.LinkMediaByUrlAsync(url, mediaId, ct);       // thư viện trỏ tới đúng file vừa tải
        return new JsonObject
        {
            ["path"] = result.FilePath, ["sizeBytes"] = result.SizeBytes, ["title"] = result.Info.Title, ["platform"] = result.Info.Platform
        };
    }
}

/// <summary>Xoá file đã tải/xuất: chỉ trong thư mục cho phép (thư mục tải, thư mục xuất), xoá cả dòng trong Kho file.</summary>
[Service]
public sealed class MediaLibraryService(MediaFileRepository files, DownloadFolders folders, ISettingsStore settings)
{
    public Task<IReadOnlyList<MediaFileRecord>> HistoryAsync(string? text = null, int limit = 200, int offset = 0, CancellationToken ct = default) =>
        files.QueryAsync(["download", "output"], text, limit, offset, ct);

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!IsAllowed(full)) throw new ArgumentException("Chỉ được xoá file trong thư mục tải hoặc thư mục xuất.", nameof(path));

        var existed = File.Exists(full);
        if (existed) File.Delete(full);
        if (await files.GetByPathAsync(full, ct) is { } record) await files.DeleteAsync(record.Id, ct);
        return existed;
    }

    private bool IsAllowed(string full)
    {
        var output = settings.Get<PathsSettings>("paths").OutputRoot;
        foreach (var root in new[] { folders.Resolve(), string.IsNullOrWhiteSpace(output) ? AppPaths.DefaultOutputRoot : output })
        {
            var relative = Path.GetRelativePath(root, full);
            if (relative != "." && !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
