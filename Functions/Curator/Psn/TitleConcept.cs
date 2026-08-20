namespace Functions.Curator.Psn;

public sealed record TitleConcept
{
    public string? ConceptId { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    public string? Publisher { get; init; }

    public DateTimeOffset? ReleaseDate { get; init; }

    public int? MinimumAge { get; init; }

    public string? ContentRating { get; init; }

    public string? RatingAuthority { get; init; }

    public double? StarRating { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> TitleIds { get; init; } = [];

    public string? CoverImageUrl { get; init; }

    public bool? Multiplayer { get; init; }
}
