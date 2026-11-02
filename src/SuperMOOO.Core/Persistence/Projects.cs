namespace SuperMOOO.Core.Persistence;

// Bảng project: kind = localize|review|youtube|talkshow|subtitle|reup; folder là khoá duy nhất.
public sealed record ProjectRecord(string Id, string Kind, string Name, string Folder, string? StateJson, DateTimeOffset UpdatedAt);

public interface IProjectStore
{
    Task<IReadOnlyList<ProjectRecord>> ListAsync(string kind, CancellationToken ct = default);
    Task<ProjectRecord?> GetByFolderAsync(string folder, CancellationToken ct = default);
    Task UpsertAsync(ProjectRecord project, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>Workflows chỉ tham chiếu Core: file kết quả được ghi vào media_file (kind=output) qua interface này.</summary>
public interface IOutputRegistry
{
    Task RegisterOutputAsync(string path, string operation, CancellationToken ct = default);
}
