namespace Functions.Curator.Psn;

public sealed class PsnAuthException : Exception
{
    public PsnAuthException()
    {
    }

    public PsnAuthException(string message)
        : base(message)
    {
    }

    public PsnAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
