namespace Functions.Churches.Crawling;

using System.Data;
using System.Data.Common;
using Azure.Messaging.ServiceBus;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;

public sealed class CrawlSchedulerWorker
{
    private readonly DbConnection _dbConnection;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly int _recrawlAfterDays;
    private readonly int _batchSize;

    public CrawlSchedulerWorker(
        DbConnection dbConnection,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory,
        IConfiguration configuration)
    {
        _dbConnection = dbConnection;
        _serviceBusClient = serviceBusClientFactory.CreateClient(AzureClientNames.Crgolden);
        _recrawlAfterDays = configuration.GetValue<int?>("CrawlRefreshDays") ?? 30;
        _batchSize = configuration.GetValue<int?>("CrawlSchedulerBatchSize") ?? 100;
    }

    [Function(nameof(CrawlSchedulerWorker))]
    public async Task Run(
        [TimerTrigger("0 0 */6 * * *")] TimerInfo timer,
        CancellationToken cancellationToken = default)
    {
        await DispatchDueAsync(cancellationToken);
    }

    internal async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        var due = new List<(Guid Id, string Url)>();
        await using (var selectCmd = _dbConnection.CreateCommand())
        {
            selectCmd.CommandText = """
                SELECT TOP (@Batch) [Id], [Url] FROM [dbo].[CrawlSources]
                WHERE [LastCrawledAt] IS NULL OR [LastCrawledAt] < @Threshold
                ORDER BY [LastCrawledAt] ASC
                """;
            selectCmd.AddParam("@Batch", _batchSize);
            selectCmd.AddParam("@Threshold", DateTimeOffset.UtcNow.AddDays(-_recrawlAfterDays));
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                due.Add(((Guid)reader[0], (string)reader[1]));
            }
        }

        if (due.Count == 0)
        {
            return 0;
        }

        await using var sender = _serviceBusClient.CreateSender(ChurchQueueNames.ScrapeRequests);
        var messages = due
            .Select(d => new ServiceBusMessage(BinaryData.FromObjectAsJson(new { CrawlSourceId = d.Id, d.Url })))
            .ToList();
        foreach (var batch in messages.Chunk(100))
        {
            await sender.SendMessagesAsync(batch, ct);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (id, _) in due)
        {
            await using var updateCmd = _dbConnection.CreateCommand();
            updateCmd.CommandText = "UPDATE [dbo].[CrawlSources] SET [LastStatus] = 0, [UpdatedAt] = @Now WHERE [Id] = @Id";
            updateCmd.AddParam("@Now", now);
            updateCmd.AddParam("@Id", id);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        return due.Count;
    }
}