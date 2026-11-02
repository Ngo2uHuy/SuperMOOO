using System.ComponentModel.DataAnnotations;
using SuperMOOO.Data.Repositories.Library;

namespace SuperMOOO.Social.Library;

public sealed class GetLibraryRequest
{
    [Field("Nền tảng", Help = "all hoặc id nền tảng (youtube, tiktok…).")]
    public string? Platform { get; set; }

    [Field("Dự án")]
    public string? Project { get; set; }

    [Field("Chiến dịch")]
    public string? Campaign { get; set; }

    [Field("Tìm trong tiêu đề / tác giả / liên kết")]
    public string? Text { get; set; }

    [Field("Chỉ mục yêu thích")]
    public bool FavoriteOnly { get; set; }

    [Field("Số dòng tối đa")]
    public int Limit { get; set; } = 100;
}

public sealed record LibraryRow(long Id, string Platform, string? Title, string? AuthorName, string? Url, string? Project,
    string? Campaign, string Tags, bool Favorite, string? LocalPath, bool FileMissing);

[Operation("operations.get_library", "Thư viện nội dung", OperationGroup.Reports)]
public sealed class GetLibraryOperation(LibraryService library) : IOperationHandler<GetLibraryRequest, IReadOnlyList<LibraryRow>>
{
    public async Task<IReadOnlyList<LibraryRow>> HandleAsync(GetLibraryRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        (await library.QueryAsync(new LibraryFilter(request.Platform, request.Project, request.Campaign, request.Text,
            request.FavoriteOnly ? true : null, Math.Clamp(request.Limit, 1, 1000)), ct)).Select(ToRow).ToList();

    internal static LibraryRow ToRow(LibraryEntry e) => new(e.Item.Id, e.Item.Platform, e.Item.Title, e.Item.AuthorName, e.Item.Url,
        e.Item.Project, e.Item.Campaign, string.Join(", ", e.Item.Tags), e.Item.Favorite, e.Item.LocalPath, e.FileMissing);
}

public sealed class PatchLibraryRequest
{
    [Required, Field("Mã mục")]
    public long Id { get; set; }

    [Field("Dự án")]
    public string? Project { get; set; }

    [Field("Chiến dịch")]
    public string? Campaign { get; set; }

    [Field("Nhãn (cách nhau bằng dấu phẩy)", Help = "Để trống = giữ nguyên.")]
    public string? Tags { get; set; }

    [Field("Yêu thích")]
    public bool? Favorite { get; set; }
}

[Operation("operations.patch_library", "Sửa nhãn / chiến dịch thư viện", OperationGroup.Reports)]
public sealed class PatchLibraryOperation(LibraryService library) : IOperationHandler<PatchLibraryRequest, LibraryRow?>
{
    public async Task<LibraryRow?> HandleAsync(PatchLibraryRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        IReadOnlyList<string>? tags = string.IsNullOrWhiteSpace(request.Tags) ? null
            : request.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var entry = await library.UpdateAsync(request.Id, new LibraryPatch(request.Project, request.Campaign, request.Favorite, tags), ct);
        return entry is null ? null : GetLibraryOperation.ToRow(entry);
    }
}

public sealed class DeleteLibraryRequest
{
    [Required, Field("Mã mục cần xoá", Kind = FieldKind.MultiLine, Help = "Mỗi dòng một mã.")]
    public List<long> Ids { get; set; } = [];
}

public sealed record DeletedCount(int Count);

[Operation("operations.delete_library", "Xóa mục thư viện", OperationGroup.Reports)]
public sealed class DeleteLibraryOperation(LibraryService library) : IOperationHandler<DeleteLibraryRequest, DeletedCount>
{
    public async Task<DeletedCount> HandleAsync(DeleteLibraryRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        new(await library.DeleteAsync(request.Ids, ct));
}

public sealed class MissingFilesRequest
{
    [Field("Số mục tối đa")]
    public int Limit { get; set; } = 200;
}

[Operation("operations.missing_files", "Tìm file bị thiếu", OperationGroup.Reports)]
public sealed class MissingFilesOperation(LibraryService library) : IOperationHandler<MissingFilesRequest, IReadOnlyList<LibraryRow>>
{
    public async Task<IReadOnlyList<LibraryRow>> HandleAsync(MissingFilesRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct) =>
        (await library.FindMissingFilesAsync(Math.Clamp(request.Limit, 1, 1000), ct)).Select(GetLibraryOperation.ToRow).ToList();
}
