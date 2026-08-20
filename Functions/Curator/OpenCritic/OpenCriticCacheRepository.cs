namespace Functions.Curator.OpenCritic;

using System.Data.Common;
using Functions.Extensions;

public sealed class OpenCriticCacheRepository
{
    private readonly DbDataSource _dataSource;

    public OpenCriticCacheRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public Task<AdvisoryLockHandle> TryLockCursorAsync(
        string platform,
        CancellationToken cancellationToken = default) =>
        AdvisoryLockHandle.TryAcquireAsync(
            _dataSource, CuratorAdvisoryLocks.OpenCriticCacheRefresh, platform, cancellationToken);

    public async Task<int> GetCursorAsync(string platform, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT next_skip FROM opencritic_pagination_cursor WHERE platform = @platform";
        cmd.AddParam("@platform", platform);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    public async Task SetCursorAsync(string platform, int nextSkip, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO opencritic_pagination_cursor (platform, next_skip) VALUES (@platform, @next_skip)
            ON CONFLICT (platform) DO UPDATE SET next_skip = EXCLUDED.next_skip, updated_at = now()
            """;
        cmd.AddParam("@platform", platform);
        cmd.AddParam("@next_skip", nextSkip);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveGamesAsync(
        IReadOnlyCollection<OpenCriticGame> games,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var game in games)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO opencritic_cache (oc_game_id, name, top_critic_score, tier, percent_recommended, raw)
                VALUES (@oc_game_id, @name, @top_critic_score, @tier, @percent_recommended, @raw::jsonb)
                ON CONFLICT (oc_game_id) DO UPDATE SET
                    name = EXCLUDED.name,
                    top_critic_score = EXCLUDED.top_critic_score,
                    tier = EXCLUDED.tier,
                    percent_recommended = EXCLUDED.percent_recommended,
                    raw = COALESCE(EXCLUDED.raw, opencritic_cache.raw),
                    fetched_at = now()
                """;
            cmd.AddParam("@oc_game_id", game.OcGameId);
            cmd.AddParam("@name", game.Name);
            cmd.AddParam("@top_critic_score", game.TopCriticScore);
            cmd.AddParam("@tier", game.Tier);
            cmd.AddParam("@percent_recommended", game.PercentRecommended);
            cmd.AddParam("@raw", game.Raw);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
