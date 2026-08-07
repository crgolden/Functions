namespace Functions;

using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;

public class ScraperWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly DbConnection _dbConnection;

    public ScraperWorker(
        DbConnection dbConnection,
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory,
        IHttpClientFactory httpClientFactory)
    {
        _dbConnection = dbConnection;
        _blobServiceClient = blobServiceClientFactory.CreateClient("crgolden");
        _serviceBusClient = serviceBusClientFactory.CreateClient("crgolden");
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
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "malformed-payload", cancellationToken: cancellationToken);
            return;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Churches-Bot/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            var response = await httpClient.GetAsync(payload.Url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await UpdateCrawlStatusAsync(payload.CrawlSourceId, 2, cancellationToken);
                await messageActions.CompleteMessageAsync(message, cancellationToken);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var html = Encoding.UTF8.GetString(bytes);
            var blobPath = await StoreBlobAsync(payload.CrawlSourceId, html, cancellationToken);
            await using var sender = _serviceBusClient.CreateSender("extraction-requests");
            var extractPayload = JsonSerializer.Serialize(new
            {
                payload.CrawlSourceId,
                BlobPath = blobPath,
                payload.Url,
            });
            await sender.SendMessageAsync(new ServiceBusMessage(extractPayload), cancellationToken);
            await UpdateCrawlStatusAsync(payload.CrawlSourceId, 1, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException)
            && !cancellationToken.IsCancellationRequested)
        {
            Telemetry.Tracing.RecordHandledFailure("scrape.expected-failure", $"{ex.GetType().Name}: {payload.Url}");
            await UpdateCrawlStatusAsync(payload.CrawlSourceId, 2, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception)
        {
            await UpdateCrawlStatusAsync(payload.CrawlSourceId, 2, cancellationToken);
            await messageActions.AbandonMessageAsync(message, cancellationToken: cancellationToken);
            throw;
        }
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private async Task<string> StoreBlobAsync(Guid crawlSourceId, string html, CancellationToken ct)
    {
        var container = _blobServiceClient.GetBlobContainerClient("churches");
        var blobName = $"{crawlSourceId}/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.html";
        var blob = container.GetBlobClient(blobName);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        await blob.UploadAsync(stream, overwrite: true, cancellationToken: ct);
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
        AddParam(cmd, "@Id", crawlSourceId);
        AddParam(cmd, "@Status", status);
        AddParam(cmd, "@Now", DateTimeOffset.UtcNow.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal sealed record ScrapeRequest(Guid CrawlSourceId, string Url);