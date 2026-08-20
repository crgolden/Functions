namespace Functions.Curator.Library;

public sealed record ContinuationGame(
    string GameId,
    string Title,
    string? ProductId,
    string? TitleId,
    bool NativePs5);
