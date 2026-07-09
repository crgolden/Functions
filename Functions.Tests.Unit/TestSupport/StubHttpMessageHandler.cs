namespace Functions.Tests.Unit.TestSupport;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<Task<HttpResponseMessage>> _send;

    private StubHttpMessageHandler(Func<Task<HttpResponseMessage>> send) => _send = send;

    public static StubHttpMessageHandler Returns(HttpResponseMessage response) =>
        new(() => Task.FromResult(response));

    public static StubHttpMessageHandler Throws(Exception toThrow) =>
        new(() => Task.FromException<HttpResponseMessage>(toThrow));

    // Returns each response in order, one per call, for tests where a worker makes more than one
    // outbound HTTP call (e.g. a zip backfill lookup followed by a Census geocode call).
    public static StubHttpMessageHandler Sequence(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        return new StubHttpMessageHandler(() => Task.FromResult(queue.Dequeue()));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _send();
}
