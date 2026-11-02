namespace SuperMOOO.Data.Repositories.Live;

[Service(As = [typeof(ILiveGameStore)])]
public sealed class LiveGameRepository(IDbConnectionFactory factory) : ILiveGameStore
{
    public async Task SaveSessionAsync(LiveGameSession s, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO livegame_session(id, started_at, ended_at, incense, prayers, likes, simulated)
            VALUES (@Id, @started, @ended, @Incense, @Prayers, @Likes, @Simulated)
            ON CONFLICT(id) DO UPDATE SET ended_at=excluded.ended_at, incense=excluded.incense,
                prayers=excluded.prayers, likes=excluded.likes
            """, new { s.Id, started = s.StartedAt.ToUnixTimeMilliseconds(), ended = s.EndedAt?.ToUnixTimeMilliseconds(), s.Incense, s.Prayers, s.Likes, s.Simulated }, cancellationToken: ct));
    }

    public async Task SaveDonorAsync(string sessionId, DateOnly day, LiveGameDonor d, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO livegame_donor(session_id, user_id, nickname, coins, day) VALUES (@sessionId, @UserId, @Nickname, @Coins, @date)
            ON CONFLICT(session_id, user_id, day) DO UPDATE SET nickname=excluded.nickname, coins=excluded.coins
            """, new { sessionId, d.UserId, d.Nickname, d.Coins, date = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<LiveGameDonor>> TopAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var db = factory.Open();
        var rows = await db.QueryAsync<(string UserId, string Nickname, long Coins)>(new CommandDefinition("""
            SELECT d.user_id AS UserId, MAX(d.nickname) AS Nickname, SUM(d.coins) AS Coins
            FROM livegame_donor d JOIN livegame_session s ON s.id=d.session_id
            WHERE d.day>=@since AND s.simulated=0 GROUP BY d.user_id ORDER BY Coins DESC LIMIT 50
            """, new { since = since.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken: ct));
        return rows.Select(row => new LiveGameDonor(row.UserId, row.Nickname, row.Coins)).ToList();
    }
}
