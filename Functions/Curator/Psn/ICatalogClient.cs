namespace Functions.Curator.Psn;

public interface ICatalogClient
{
    Task<TitleConcept> TitleConceptAsync(
        PsnSession session,
        string titleId,
        CancellationToken cancellationToken = default);
}
