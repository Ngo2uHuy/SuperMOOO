namespace SuperMOOO.Core.Persistence;

// Bảng voice_profile: chỉ giọng clone/tuỳ biến.
public sealed record VoiceProfile(string Id, string Provider, string Name, string? Language, string? Gender,
    string? RefPath, string? RefText, string Status = "ready", string? ExtraJson = null, DateTimeOffset? CreatedAt = null);

public interface IVoiceProfileStore
{
    Task<IReadOnlyList<VoiceProfile>> ListAsync(string provider, CancellationToken ct = default);
    Task<VoiceProfile?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(VoiceProfile profile, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
