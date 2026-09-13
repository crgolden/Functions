namespace Functions.Curator.Store;

public sealed class StoreQueryRotatedException : Exception
{
    public StoreQueryRotatedException()
    {
    }

    public StoreQueryRotatedException(string message)
        : base(message)
    {
    }

    public StoreQueryRotatedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
