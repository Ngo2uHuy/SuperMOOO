using System.Text;
using System.Text.RegularExpressions;
using SuperMOOO.Infrastructure.Tabular;

namespace SuperMOOO.Social.Download;

public sealed record UrlImportResult(string FileName, IReadOnlyList<string> Urls);

/// <summary>Đọc danh sách link từ .xlsx/.csv/.txt (mọi ô, theo thứ tự, bỏ trùng). Không hỗ trợ .xls (đã chốt bỏ).</summary>
[Service]
public sealed partial class UrlListImporter
{
    [GeneratedRegex(@"https?://[^\s,;""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private static readonly TableData Template = new(["STT", "URL Video / Reel / Shorts", "Ghi chú"],
    [
        ["1", "https://www.youtube.com/watch?v=6uVJqD2hSGQ", "Link YouTube mẫu"],
        ["2", "https://www.tiktok.com/@tiktok/video/7123456789", "Link TikTok mẫu"],
        ["3", "https://www.facebook.com/reel/123456789", "Link Facebook Reel mẫu"]
    ]);

    public async Task<UrlImportResult> ReadAsync(string path, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return Read(bytes, Path.GetFileName(path));
    }

    public UrlImportResult Read(byte[] content, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".xls") throw new NotSupportedException("Không hỗ trợ file .xls. Hãy lưu lại thành .xlsx hoặc .csv.");

        var text = ext is ".xlsx" or ".xlsm" ? FlattenTable(content) : Decode(content);
        var urls = UrlPattern().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ')')).Distinct(StringComparer.Ordinal).ToList();
        return new UrlImportResult(fileName, urls);
    }

    public async Task WriteTemplateAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.Create(path);
        XlsxLite.Write(stream, Template, "Danh sách link");
        await stream.FlushAsync(ct);
    }

    private static string FlattenTable(byte[] content)
    {
        using var stream = new MemoryStream(content);
        var table = XlsxLite.Read(stream);
        return string.Join('\n', table.Columns.Concat(table.Rows.SelectMany(r => r)));
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]) return Encoding.Unicode.GetString(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        return text.TrimStart('﻿');
    }
}

public sealed record BatchDownloadRequest(IReadOnlyList<string> Urls, DownloadFormat? Format = null, bool? NoWatermark = null,
    string? Folder = null);

/// <summary>Xếp nhiều job tải từ danh sách hoặc file; bỏ trùng, tối đa 500 liên kết/lần, một liên kết sai thì không xếp gì.</summary>
[Service]
public sealed class BatchDownloadService(DownloadService downloads, UrlListImporter importer)
{
    public const int MaxUrls = 500;

    public async Task<IReadOnlyList<string>> EnqueueAsync(BatchDownloadRequest request, CancellationToken ct = default)
    {
        var urls = request.Urls.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (urls.Count == 0) throw new ArgumentException("Danh sách liên kết đang trống.", nameof(request));
        if (urls.Count > MaxUrls) throw new ArgumentException($"Tối đa {MaxUrls} liên kết mỗi lần.", nameof(request));
        if (urls.FirstOrDefault(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) is { } bad)
            throw new ArgumentException($"Liên kết không hợp lệ: {bad}", nameof(request));

        var ids = new List<string>();
        foreach (var url in urls)
            ids.Add(await downloads.EnqueueAsync(new DownloadJobRequest(url, request.Format, request.NoWatermark, request.Folder), ct));
        return ids;
    }

    public async Task<IReadOnlyList<string>> EnqueueFromFileAsync(string path, DownloadFormat? format = null, bool? noWatermark = null,
        string? folder = null, CancellationToken ct = default)
    {
        var imported = await importer.ReadAsync(path, ct);
        return await EnqueueAsync(new BatchDownloadRequest(imported.Urls, format, noWatermark, folder), ct);
    }
}
