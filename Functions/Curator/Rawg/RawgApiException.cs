namespace Functions.Curator.Rawg;

public sealed class RawgApiException : Exception
{
    public RawgApiException(int statusCode, double? retryAfterSeconds, string? providerDetail)
        : base($"RAWG request failed with status {statusCode}")
    {
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
        ProviderDetail = providerDetail;
    }

    public RawgApiException()
    {
    }

    public RawgApiException(string message)
        : base(message)
    {
    }

    public RawgApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int StatusCode { get; }

    public double? RetryAfterSeconds { get; }

    public string? ProviderDetail { get; }
}
