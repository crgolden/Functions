namespace Functions.Curator.OpenCritic;

public sealed class OpenCriticNetworkException : Exception
{
    private const string NetworkFailureMessage = "Network error while paginating OpenCritic's catalog.";

    public OpenCriticNetworkException(
        IReadOnlyList<OpenCriticGame> partialGames,
        int partialNextSkip,
        Exception innerException)
        : base(NetworkFailureMessage, innerException)
    {
        PartialGames = partialGames;
        PartialNextSkip = partialNextSkip;
    }

    public OpenCriticNetworkException()
        : base(NetworkFailureMessage)
    {
        PartialGames = [];
    }

    public OpenCriticNetworkException(string message)
        : base(message)
    {
        PartialGames = [];
    }

    public OpenCriticNetworkException(string message, Exception innerException)
        : base(message, innerException)
    {
        PartialGames = [];
    }

    public IReadOnlyList<OpenCriticGame> PartialGames { get; }

    public int PartialNextSkip { get; }
}
