namespace Functions.Curator.Library;

public sealed record ContinuationGame(
    Guid GameId,
    string Title,
    string? ProductId,
    string? TitleId,
    bool NativePs5);
