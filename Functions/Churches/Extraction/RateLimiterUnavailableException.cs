namespace Functions.Churches.Extraction;

public sealed class RateLimiterUnavailableException : Exception
{
    public RateLimiterUnavailableException()
    {
    }

    public RateLimiterUnavailableException(string message)
        : base(message)
    {
    }

    public RateLimiterUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
