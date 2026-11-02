namespace SuperMOOO.Core.Persistence;

public interface ILiveGameStore
{
    Task SaveSessionAsync(LiveGameSession session, CancellationToken ct = default);
    Task SaveDonorAsync(string sessionId, DateOnly day, LiveGameDonor donor, CancellationToken ct = default);
    Task<IReadOnlyList<LiveGameDonor>> TopAsync(DateTimeOffset since, CancellationToken ct = default);
}
