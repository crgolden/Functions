namespace Functions.Curator.Enrichment;

public interface IProviderApiFailure
{
    int StatusCode { get; }

    double? RetryAfterSeconds { get; }

    string Message { get; }
}
