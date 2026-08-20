namespace Functions.Curator.OpenCritic;

public sealed class OpenCriticApiException : Exception
{
    public OpenCriticApiException(int statusCode, double? retryAfterSeconds, string? providerDetail)
        : base($"OpenCritic request failed with status {statusCode}")
    {
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
        ProviderDetail = providerDetail;
    }

    public OpenCriticApiException()
    {
    }

    public OpenCriticApiException(string message)
        : base(message)
    {
    }

    public OpenCriticApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int StatusCode { get; }

    public double? RetryAfterSeconds { get; }

    public string? ProviderDetail { get; }

    public IReadOnlyList<OpenCriticGame> PartialGames { get; set; } = [];

    public int? PartialNextSkip { get; set; }
}
