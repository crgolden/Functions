namespace Functions.Churches.Crawling;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;

public class ScraperWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ChurchQueueSenders _senders;
    private readonly DbConnection _dbConnection;

    public ScraperWorker(
        DbConnection dbConnection,
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        ChurchQueueSenders senders,
        IHttpClientFactory httpClientFactory)
    {
        _dbConnection = dbConnection;
        _blobServiceClient = blobServiceClientFactory.CreateClient(AzureClientNames.Crgolden);
        _senders = senders;
        _httpClientFactory = httpClientFactory;
    }

    [Function(nameof(ScraperWorker))]
    public async Task Run(
        [ServiceBusTrigger("scrape-requests", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var payload = message.Body.ToObjectFromJson<ScrapeRequest>();
        if (payload is null)
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: DeadLetterReasons.MalformedPayload, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Churches-Bot/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            var response = await httpClient.GetAsync(payload.Url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await UpdateCrawlStatusAsync(payload.CrawlSourceId, CrawlStatuses.Failed, cancellationToken);
                await messageActions.CompleteMessageAsync(message, cancellationToken);
                return;
            }

            var html = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var blobPath = await StoreBlobAsync(payload.CrawlSourceId, html, cancellationToken);
            var sender = _senders.For(ChurchQueueNames.ExtractionRequests);
            var extractPayload = JsonSerializer.Serialize(new
            {
                payload.CrawlSourceId,
                BlobPath = blobPath,
                payload.Url,
            });
            await sender.SendMessageAsync(new ServiceBusMessage(extractPayload), cancellationToken);
            await UpdateCrawlStatusAsync(payload.CrawlSourceId, CrawlStatuses.Succeeded, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException)
            && !cancellationToken.IsCancellationRequested)
        {
            Telemetry.Tracing.RecordHandledFailure("scrape.expected-failure", $"{ex.GetType().Name}: {payload.Url}");
            await UpdateCrawlStatusAsync(payload.CrawlSourceId, CrawlStatuses.Failed, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception)
        {
            await UpdateCrawlStatusAsync(payload.CrawlSourceId, CrawlStatuses.Failed, cancellationToken);
            await messageActions.AbandonMessageAsync(message, cancellationToken: cancellationToken);
            throw;
        }
    }

    private async Task<string> StoreBlobAsync(Guid crawlSourceId, byte[] html, CancellationToken ct)
    {
        var container = _blobServiceClient.GetBlobContainerClient(BlobContainerNames.Churches);
        var blobName = $"{crawlSourceId}/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.html";
        var blob = container.GetBlobClient(blobName);
        await blob.UploadAsync(BinaryData.FromBytes(html), overwrite: true, cancellationToken: ct);
        return blobName;
    }

    private async Task UpdateCrawlStatusAsync(Guid crawlSourceId, int status, CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            UPDATE [dbo].[CrawlSources]
            SET [LastCrawledAt] = @Now, [LastStatus] = @Status, [UpdatedAt] = @Now
            WHERE [Id] = @Id
            """;
        cmd.AddParam(ChurchSqlParameters.Id, crawlSourceId);
        cmd.AddParam(ChurchSqlParameters.Status, status);
        cmd.AddParam(ChurchSqlParameters.Now, DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
