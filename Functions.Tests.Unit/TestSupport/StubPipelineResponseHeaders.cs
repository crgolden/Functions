namespace Functions.Tests.Unit.TestSupport;

using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;

internal sealed class StubPipelineResponseHeaders : PipelineResponseHeaders
{
    private readonly IReadOnlyDictionary<string, string> _headers;

    public StubPipelineResponseHeaders(IReadOnlyDictionary<string, string> headers)
    {
        _headers = headers;
    }

    public override bool TryGetValue(string name, [NotNullWhen(true)] out string? value) =>
        _headers.TryGetValue(name, out value);

    public override bool TryGetValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
    {
        values = _headers.TryGetValue(name, out var value) ? [value] : null;
        return values is not null;
    }

    public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();
}
