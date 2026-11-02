namespace SuperMOOO.Core.Persistence;

public interface IAvatarStore
{
    Task<IReadOnlyList<Avatar>> ListAsync(CancellationToken ct = default);
    Task<Avatar?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(Avatar avatar, CancellationToken ct = default);
    Task SetFaceStatusAsync(string id, string status, int faceSeconds, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
