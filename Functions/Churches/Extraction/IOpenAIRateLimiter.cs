namespace Functions.Churches.Extraction;

public interface IOpenAIRateLimiter
{
    Task<double?> TryAcquireAsync(int estimatedTokens);
}
