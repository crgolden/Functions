namespace Functions.Tests.Unit.TestSupport;

using Azure;
using Moq;

internal sealed class FakeAsyncPageable<T> : AsyncPageable<T>
    where T : notnull
{
    private readonly IReadOnlyList<T> _items;

    public FakeAsyncPageable(IReadOnlyList<T> items) => _items = items;

    public override async IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
    {
        yield return Page<T>.FromValues(_items, null, Mock.Of<Response>());
        await Task.CompletedTask;
    }
}
