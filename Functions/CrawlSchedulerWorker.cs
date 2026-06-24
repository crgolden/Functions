namespace Functions;

using System.Data;
using System.Data.Common;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;

// Makes the crawl pipeline self-running: on a timer it finds CrawlSources that have never been crawled
// or are due for a refresh and publishes them to scrape-requests, so scrape -> extract -> enrich ->
// geocode runs without a moderator manually triggering each URL.
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
        _serviceBusClient = serviceBusClientFactory.CreateClient("crgolden");
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
            AddParam(selectCmd, "@Batch", _batchSize);
            AddParam(selectCmd, "@Threshold", DateTime.UtcNow.AddDays(-_recrawlAfterDays));
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

        await using var sender = _serviceBusClient.CreateSender("scrape-requests");
        var messages = due
            .Select(d => new ServiceBusMessage(BinaryData.FromObjectAsJson(new { CrawlSourceId = d.Id, d.Url })))
            .ToList();
        foreach (var batch in messages.Chunk(100))
        {
            await sender.SendMessagesAsync(batch, ct);
        }

        var now = DateTime.UtcNow;
        foreach (var (id, _) in due)
        {
            await using var updateCmd = _dbConnection.CreateCommand();
            updateCmd.CommandText = "UPDATE [dbo].[CrawlSources] SET [LastStatus] = 0, [UpdatedAt] = @Now WHERE [Id] = @Id";
            AddParam(updateCmd, "@Now", now);
            AddParam(updateCmd, "@Id", id);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        return due.Count;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
