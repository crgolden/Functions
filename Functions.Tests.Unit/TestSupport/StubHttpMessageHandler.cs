namespace Functions.Tests.Unit.TestSupport;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<Task<HttpResponseMessage>> _send;

    private StubHttpMessageHandler(Func<Task<HttpResponseMessage>> send) => _send = send;

    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubHttpMessageHandler Returns(HttpResponseMessage response) =>
        new(() => Task.FromResult(response));

    public static StubHttpMessageHandler Throws(Exception toThrow) =>
        new(() => Task.FromException<HttpResponseMessage>(toThrow));

    public static StubHttpMessageHandler Always(Func<HttpResponseMessage> factory) =>
        new(() => Task.FromResult(factory()));

    public static StubHttpMessageHandler Responds(Func<Task<HttpResponseMessage>> respond) => new(respond);

    public static StubHttpMessageHandler Sequence(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        return new StubHttpMessageHandler(() => Task.FromResult(queue.Dequeue()));
    }

    public static StubHttpMessageHandler SequenceThen(HttpResponseMessage response, Exception toThrow)
    {
        var served = false;
        return new StubHttpMessageHandler(() =>
        {
            if (served)
            {
                return Task.FromException<HttpResponseMessage>(toThrow);
            }

            served = true;
            return Task.FromResult(response);
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _send();
    }
}
