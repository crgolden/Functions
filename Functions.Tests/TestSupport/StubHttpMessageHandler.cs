namespace Functions.Tests.TestSupport;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<Task<HttpResponseMessage>> _send;

    private StubHttpMessageHandler(Func<Task<HttpResponseMessage>> send) => _send = send;

    public static StubHttpMessageHandler Returns(HttpResponseMessage response) =>
        new(() => Task.FromResult(response));

    public static StubHttpMessageHandler Throws(Exception toThrow) =>
        new(() => Task.FromException<HttpResponseMessage>(toThrow));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _send();
}
