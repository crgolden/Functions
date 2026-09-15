namespace Functions.Tests.Unit.TestSupport;

using System.ClientModel.Primitives;

internal sealed class StubPipelineResponse : PipelineResponse
{
    public StubPipelineResponse(int status, IReadOnlyDictionary<string, string> headers)
    {
        Status = status;
        HeadersCore = new StubPipelineResponseHeaders(headers);
    }

    public override int Status { get; }

    public override string ReasonPhrase => nameof(StubPipelineResponse);

    public override Stream? ContentStream { get; set; }

    public override BinaryData Content => BinaryData.Empty;

    protected override PipelineResponseHeaders HeadersCore { get; }

    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;

    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(BinaryData.Empty);

    public override void Dispose()
    {
    }
}
