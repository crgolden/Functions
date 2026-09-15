namespace Functions.Churches.Crawling;

using System.Data;
using System.Data.Common;
using Azure.Messaging.ServiceBus;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

public sealed class CrawlSchedulerWorker
{
    private readonly DbConnection _dbConnection;
    private readonly ChurchQueueSenders _senders;
    private readonly int _recrawlAfterDays;
    private readonly int _batchSize;

    public CrawlSchedulerWorker(
        DbConnection dbConnection,
        ChurchQueueSenders senders,
        IConfiguration configuration)
    {
        _dbConnection = dbConnection;
        _senders = senders;
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
        await using (var claimCmd = _dbConnection.CreateCommand())
        {
            claimCmd.CommandText = """
                WITH [Due] AS (
                    SELECT TOP (@Batch) [Id], [Url], [LastStatus], [UpdatedAt] FROM [dbo].[CrawlSources]
                    WHERE [LastCrawledAt] IS NULL OR [LastCrawledAt] < @Threshold
                    ORDER BY [LastCrawledAt] ASC
                )
                UPDATE [Due] SET [LastStatus] = 0, [UpdatedAt] = @Now
                OUTPUT [inserted].[Id], [inserted].[Url]
                """;
            claimCmd.AddParam("@Batch", _batchSize);
            claimCmd.AddParam("@Threshold", DateTimeOffset.UtcNow.AddDays(-_recrawlAfterDays));
            claimCmd.AddParam("@Now", DateTimeOffset.UtcNow);
            await using var reader = await claimCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                due.Add(((Guid)reader[0], (string)reader[1]));
            }
        }

        if (due.Count == 0)
        {
            return 0;
        }

        var sender = _senders.For(ChurchQueueNames.ScrapeRequests);
        var messages = due
            .Select(d => new ServiceBusMessage(BinaryData.FromObjectAsJson(new { CrawlSourceId = d.Id, d.Url })))
            .ToList();
        foreach (var batch in messages.Chunk(100))
        {
            await sender.SendMessagesAsync(batch, ct);
        }

        return due.Count;
    }
}