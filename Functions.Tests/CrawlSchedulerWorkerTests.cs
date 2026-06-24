namespace Functions.Tests;

using System.Data;
using Microsoft.Extensions.Configuration;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class CrawlSchedulerWorkerTests
{
    [Fact]
    public async Task DispatchDueAsync_DueSources_PublishesAndMarksPending()
    {
        // Arrange — two sources are due for crawling
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(SourcesTable(2)));
        var (factory, sent) = FakeServiceBus.Create();
        var worker = new CrawlSchedulerWorker(connection, factory, Config());

        // Act
        var dispatched = await worker.DispatchDueAsync(TestContext.Current.CancellationToken);

        // Assert — both published to scrape-requests and marked pending
        Assert.Equal(2, dispatched);
        Assert.Equal(2, sent.Count);
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("UPDATE [dbo].[CrawlSources] SET [LastStatus]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchDueAsync_NoDueSources_DoesNothing()
    {
        // Arrange — nothing due
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(SourcesTable(0)));
        var (factory, sent) = FakeServiceBus.Create();
        var worker = new CrawlSchedulerWorker(connection, factory, Config());

        // Act
        var dispatched = await worker.DispatchDueAsync(TestContext.Current.CancellationToken);

        // Assert — only the SELECT ran; nothing published
        Assert.Equal(0, dispatched);
        Assert.Empty(sent);
        Assert.Single(connection.ExecutedCommands);
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static DataTable SourcesTable(int rows)
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("Url", typeof(string));
        for (var i = 0; i < rows; i++)
        {
            table.Rows.Add(Guid.NewGuid(), $"https://church{i}.example");
        }

        return table;
    }
}
