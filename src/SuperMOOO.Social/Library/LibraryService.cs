using SuperMOOO.Data.Repositories.Library;

namespace SuperMOOO.Social.Library;

/// <summary>Mục thư viện kèm trạng thái file cục bộ (FileMissing = có đường dẫn nhưng file không còn).</summary>
public sealed record LibraryEntry(LibraryItem Item, bool FileMissing);

[Service]
public sealed class LibraryService(LibraryRepository repo)
{
    public async Task<IReadOnlyList<LibraryEntry>> QueryAsync(LibraryFilter filter, CancellationToken ct = default) =>
        (await repo.QueryAsync(filter, ct)).Select(Wrap).ToList();

    public async Task<LibraryEntry?> UpdateAsync(long id, LibraryPatch patch, CancellationToken ct = default) =>
        await repo.UpdateAsync(id, patch, ct) is { } item ? Wrap(item) : null;

    public Task<int> DeleteAsync(IReadOnlyList<long> ids, CancellationToken ct = default) => repo.DeleteAsync(ids, ct);

    public async Task<IReadOnlyList<LibraryEntry>> FindMissingFilesAsync(int limit = 200, CancellationToken ct = default) =>
        (await QueryAsync(new LibraryFilter(Limit: limit), ct)).Where(e => e.FileMissing).ToList();

    private static LibraryEntry Wrap(LibraryItem item) =>
        new(item, !string.IsNullOrEmpty(item.LocalPath) && !File.Exists(item.LocalPath));
}
