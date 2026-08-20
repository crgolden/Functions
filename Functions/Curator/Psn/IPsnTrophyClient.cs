namespace Functions.Curator.Psn;

public interface IPsnTrophyClient
{
    Task<IReadOnlyList<TrophyTitle>> TrophyTitlesAsync(
        PsnSession session,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, TrophyTitle>> TrophyTitlesByTitleIdAsync(
        PsnSession session,
        IReadOnlyList<string> titleIds,
        CancellationToken cancellationToken = default);
}
