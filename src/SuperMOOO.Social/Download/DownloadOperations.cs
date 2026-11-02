using System.ComponentModel.DataAnnotations;
using SuperMOOO.Data.Repositories.Files;

namespace SuperMOOO.Social.Download;

public sealed class StartDownloadRequest
{
    [Required, Field("Liên kết video", Kind = FieldKind.Url, Example = "https://www.youtube.com/watch?v=...")]
    public string Url { get; set; } = "";

    [Field("Định dạng")]
    public DownloadFormat Format { get; set; } = DownloadFormat.Best;

    [Field("Không watermark", Help = "Áp dụng mọi nền tảng: bỏ nguồn được đánh dấu có watermark khi có thông tin. Không xoá logo gắn trong hình.")]
    public bool NoWatermark { get; set; } = true;
}

public sealed record JobStarted(string JobId);

[Operation("download.start_download", "Tải video", OperationGroup.Download)]
public sealed class StartDownloadOperation(DownloadService downloads) : IOperationHandler<StartDownloadRequest, JobStarted>
{
    public async Task<JobStarted> HandleAsync(StartDownloadRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        new(await downloads.EnqueueAsync(new DownloadJobRequest(request.Url, request.Format, request.NoWatermark), ct));
}

public sealed class BatchDownloadOperationRequest
{
    [Field("Danh sách liên kết", Kind = FieldKind.UrlList, Help = "Mỗi dòng một liên kết.")]
    public List<string> Urls { get; set; } = [];

    [Field("Hoặc nhập từ file Excel / CSV", Kind = FieldKind.File, Help = "Chấp nhận .xlsx, .csv, .txt (không hỗ trợ .xls).")]
    public string? FilePath { get; set; }

    [Field("Định dạng")]
    public DownloadFormat Format { get; set; } = DownloadFormat.Best;

    [Field("Không watermark", Help = "Áp dụng mọi nền tảng khi nguồn hỗ trợ; không xoá logo đã gắn trong hình.")]
    public bool NoWatermark { get; set; } = true;
}

public sealed record BatchStarted(IReadOnlyList<string> JobIds);

[Operation("download.batch_download", "Tải hàng loạt / nhập Excel", OperationGroup.Download)]
public sealed class BatchDownloadOperation(BatchDownloadService batch) : IOperationHandler<BatchDownloadOperationRequest, BatchStarted>
{
    public async Task<BatchStarted> HandleAsync(BatchDownloadOperationRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        new(string.IsNullOrWhiteSpace(request.FilePath)
            ? await batch.EnqueueAsync(new BatchDownloadRequest(request.Urls, request.Format, request.NoWatermark), ct)
            : await batch.EnqueueFromFileAsync(request.FilePath, request.Format, request.NoWatermark, ct: ct));
}

public sealed class VideoInfoRequest
{
    [Required, Field("Liên kết video", Kind = FieldKind.Url)]
    public string Url { get; set; } = "";
}

[Operation("download.get_video_info", "Xem thông tin video", OperationGroup.Download)]
public sealed class VideoInfoOperation(IDownloader downloader) : IOperationHandler<VideoInfoRequest, RemoteMediaInfo>
{
    public Task<RemoteMediaInfo> HandleAsync(VideoInfoRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        downloader.GetInfoAsync(request.Url, ct);
}

public sealed class DownloadHistoryRequest
{
    [Field("Tìm trong đường dẫn / liên kết")]
    public string? Text { get; set; }

    [Field("Số dòng tối đa")]
    public int Limit { get; set; } = 200;
}

[Operation("download.get_history", "Kho file đã tải", OperationGroup.Download, LegacyKeys = ["settings.db_history"])]
public sealed class DownloadHistoryOperation(MediaLibraryService library)
    : IOperationHandler<DownloadHistoryRequest, IReadOnlyList<MediaFileRecord>>
{
    public Task<IReadOnlyList<MediaFileRecord>> HandleAsync(DownloadHistoryRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        library.HistoryAsync(request.Text, Math.Clamp(request.Limit, 1, 1000), ct: ct);
}

public sealed class DeleteDownloadedFileRequest
{
    [Required, Field("File cần xoá", Kind = FieldKind.File, Help = "Chỉ xoá được file trong thư mục tải hoặc thư mục xuất.")]
    public string FilePath { get; set; } = "";
}

public sealed record DeleteResult(bool Deleted);

[Operation("download.delete_file_endpoint", "Xóa file đã tải", OperationGroup.Download)]
public sealed class DeleteDownloadedFileOperation(MediaLibraryService library) : IOperationHandler<DeleteDownloadedFileRequest, DeleteResult>
{
    public async Task<DeleteResult> HandleAsync(DeleteDownloadedFileRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        new(await library.DeleteAsync(request.FilePath, ct));
}

public sealed class ImportUrlListRequest
{
    [Required, Field("File Excel / CSV", Kind = FieldKind.File)]
    public string FilePath { get; set; } = "";
}

[Operation("excel.import_excel", "Nhập danh sách link từ Excel / CSV", OperationGroup.Download)]
public sealed class ImportUrlListOperation(UrlListImporter importer) : IOperationHandler<ImportUrlListRequest, UrlImportResult>
{
    public Task<UrlImportResult> HandleAsync(ImportUrlListRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        importer.ReadAsync(request.FilePath, ct);
}

public sealed class SaveTemplateRequest
{
    [Required, Field("Lưu file mẫu tại", Kind = FieldKind.File, Example = @"C:\Users\...\Link_Download_Template.xlsx")]
    public string OutputPath { get; set; } = "";
}

public sealed record TemplateSaved(string Path);

[Operation("excel.download_template_endpoint", "Lưu Excel mẫu", OperationGroup.Download)]
public sealed class SaveTemplateOperation(UrlListImporter importer) : IOperationHandler<SaveTemplateRequest, TemplateSaved>
{
    public async Task<TemplateSaved> HandleAsync(SaveTemplateRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        await importer.WriteTemplateAsync(request.OutputPath, ct);
        return new(request.OutputPath);
    }
}
