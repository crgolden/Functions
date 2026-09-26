namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using Functions.Churches;
using Functions.Churches.Crawling;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Extensions.Configuration;

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
        var (senders, sent) = FakeServiceBus.CreateSenders();
        var worker = new CrawlSchedulerWorker(connection, senders, Config());

        // Act
        var dispatched = await worker.DispatchDueAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(dueSourceCount, dispatched);
        Assert.Equal(dueSourceCount, sent.Count);
        var claim = Assert.Single(connection.ExecutedCommands);
        Assert.Contains("UPDATE [Due] SET [LastStatus] = 0", claim.CommandText, StringComparison.Ordinal);
        Assert.Contains("OUTPUT [inserted].[Id], [inserted].[Url]", claim.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchDueAsync_NoDueSources_DoesNothing()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(SourcesTable(0)));
        var (senders, sent) = FakeServiceBus.CreateSenders();
        var worker = new CrawlSchedulerWorker(connection, senders, Config());

        // Act
        var dispatched = await worker.DispatchDueAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, dispatched);
        Assert.Empty(sent);
        Assert.Single(connection.ExecutedCommands);
    }

    private static IConfiguration Config()
    {
        var refreshDays = Random.Shared.Next(1, 61);
        var batchSize = Random.Shared.Next(10, 201);
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ChurchSettingKeys.CrawlRefreshDays] = refreshDays.ToString(CultureInfo.InvariantCulture),
            [ChurchSettingKeys.CrawlSchedulerBatchSize] = batchSize.ToString(CultureInfo.InvariantCulture),
        }).Build();
    }

    private static DataTable SourcesTable(int rows)
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string));
        for (var i = 0; i < rows; i++)
        {
            var crawlSourceId = Guid.NewGuid();
            var crawlSourceUrl = Generated.NewWebsite();
            table.Rows.Add(crawlSourceId, crawlSourceUrl);
        }

        return table;
    }
}
