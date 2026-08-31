namespace Functions.Tests.Unit;

using System.Data;
using Churches;
using Churches.Crawling;
using Microsoft.Extensions.Configuration;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class CrawlSchedulerWorkerTests
{
    [Fact]
    public async Task DispatchDueAsync_DueSources_PublishesAndMarksPending()
    {
        // Arrange
        var dueSourceCount = Random.Shared.Next(2, 10);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(SourcesTable(dueSourceCount)));
        var (factory, sent) = FakeServiceBus.Create();
        var worker = new CrawlSchedulerWorker(connection, factory, Config());

        // Act
        var dispatched = await worker.DispatchDueAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(dueSourceCount, dispatched);
        Assert.Equal(dueSourceCount, sent.Count);
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("UPDATE [dbo].[CrawlSources] SET [LastStatus]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchDueAsync_NoDueSources_DoesNothing()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(SourcesTable(0)));
        var (factory, sent) = FakeServiceBus.Create();
        var worker = new CrawlSchedulerWorker(connection, factory, Config());

        // Act
        var dispatched = await worker.DispatchDueAsync(TestContext.Current.CancellationToken);

        // Assert
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
            var crawlSourceId = Guid.NewGuid();
            var crawlSourceUrl = TestValues.NewWebsite();
            table.Rows.Add(crawlSourceId, crawlSourceUrl);
        }

        return table;
    }
}
