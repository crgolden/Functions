namespace Functions.Tests.Unit.TestSupport;

using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;

internal sealed class StubPipelineResponseHeaders(IReadOnlyDictionary<string, string> headers) : PipelineResponseHeaders
{
    public override bool TryGetValue(string name, [NotNullWhen(true)] out string? value) =>
        headers.TryGetValue(name, out value);

    public override bool TryGetValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
    {
        values = headers.TryGetValue(name, out var value) ? [value] : null;
        return values is not null;
    }

    public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => headers.GetEnumerator();
}
