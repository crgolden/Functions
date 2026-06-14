namespace Functions;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;

public class ExtractorWorker
{
    private const decimal Tier2Threshold = 0.5m;

    private readonly BlobServiceClient _blobServiceClient;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly DbConnection _dbConnection;

    public ExtractorWorker(
        DbConnection dbConnection,
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory)
    {
        _dbConnection = dbConnection;
        _blobServiceClient = blobServiceClientFactory.CreateClient("crgolden");
        _serviceBusClient = serviceBusClientFactory.CreateClient("crgolden");
    }

    [Function(nameof(ExtractorWorker))]
    public async Task Run(
        [ServiceBusTrigger("extraction-requests", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var payload = message.Body.ToObjectFromJson<ExtractionRequest>();
        if (payload is null)
        {
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var html = await DownloadBlobAsync(payload.BlobPath, cancellationToken);
        if (html is null)
        {
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var result = await ExtractFromHtmlAsync(html, payload.Url);

        if (result.Confidence >= Tier2Threshold && !string.IsNullOrWhiteSpace(result.City))
        {
            await UpsertChurchAsync(payload.CrawlSourceId, result, cancellationToken);
        }
        else
        {
            await using var sender = _serviceBusClient.CreateSender("enrichment-requests");
            await sender.SendMessageAsync(
                new ServiceBusMessage(JsonSerializer.Serialize(new
                {
                    payload.CrawlSourceId,
                    payload.BlobPath,
                    payload.Url,
                    Partial = new
                    {
                        result.CanonicalName,
                        result.City,
                        result.State,
                        result.Zip,
                    },
                })),
                cancellationToken);
        }

        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    internal static string? ExtractPhone(IDocument doc)
    {
        var itemprop = doc.QuerySelector("[itemprop='telephone']")?.TextContent?.Trim();
        if (!string.IsNullOrWhiteSpace(itemprop))
        {
            return itemprop;
        }

        var body = doc.Body?.TextContent ?? string.Empty;
        var match = Regex.Match(body, @"\(?\d{3}\)?[\s\-\.]\d{3}[\s\-\.]\d{4}");
        return match.Success ? match.Value.Trim() : null;
    }

    internal static async Task<ExtractionResult> ExtractFromHtmlAsync(string html, string url)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(html));

        var name = document.QuerySelector("[itemprop='name']")?.TextContent?.Trim()
                   ?? document.QuerySelector("h1")?.TextContent?.Trim()
                   ?? document.Title?.Trim();
        var street = document.QuerySelector("[itemprop='streetAddress']")?.TextContent?.Trim();
        var city = document.QuerySelector("[itemprop='addressLocality']")?.TextContent?.Trim();
        var state = document.QuerySelector("[itemprop='addressRegion']")?.TextContent?.Trim();
        var zip = document.QuerySelector("[itemprop='postalCode']")?.TextContent?.Trim();
        var phone = ExtractPhone(document);
        var emailHref = document.QuerySelector("[itemprop='email'], a[href^='mailto:']")?.GetAttribute("href");
        var email = emailHref?.Replace("mailto:", string.Empty).Trim();

        var confidence = 0m;
        if (!string.IsNullOrWhiteSpace(name))
        {
            confidence += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            confidence += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            confidence += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(zip))
        {
            confidence += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(phone) || !string.IsNullOrWhiteSpace(email))
        {
            confidence += 0.1m;
        }

        return new ExtractionResult(
            CanonicalName: name,
            Street: street,
            City: city,
            State: state,
            Zip: zip,
            PhoneNumber: phone,
            Website: url,
            EmailAddress: email,
            Confidence: confidence);
    }

    internal async Task UpsertChurchAsync(Guid crawlSourceId, ExtractionResult result, CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        await using var lookupCmd = _dbConnection.CreateCommand();
        lookupCmd.CommandText = "SELECT [ChurchId] FROM [dbo].[CrawlSources] WHERE [Id] = @Id";
        AddParam(lookupCmd, "@Id", crawlSourceId);
        var existingIdObj = await lookupCmd.ExecuteScalarAsync(ct);
        var isNew = existingIdObj is not Guid;
        var churchId = existingIdObj is Guid g ? g : Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        var slug = SlugHelper.ToSlug(result.CanonicalName ?? string.Empty)
                   + "-" + SlugHelper.ToSlug(result.City ?? string.Empty)
                   + "-" + (result.State ?? string.Empty).ToLowerInvariant().Trim();

        if (isNew)
        {
            await using var insertCmd = _dbConnection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO [dbo].[Churches]
                    ([Id], [CanonicalName], [Slug], [Latitude], [Longitude], [Street], [City], [State], [Zip],
                     [PhoneNumber], [Website], [EmailAddress], [PrimaryLanguage], [ConfidenceScore], [CreatedAt], [UpdatedAt], [IsActive])
                VALUES (@Id, @Name, @Slug, 0, 0, @Street, @City, @State, @Zip,
                        @Phone, @Website, @Email, N'English', @Score, @Now, @Now, 1)
                """;
            AddParam(insertCmd, "@Id", churchId);
            AddParam(insertCmd, "@Name", (object?)result.CanonicalName ?? DBNull.Value);
            AddParam(insertCmd, "@Slug", slug);
            AddParam(insertCmd, "@Street", (object?)result.Street ?? DBNull.Value);
            AddParam(insertCmd, "@City", (object?)result.City ?? DBNull.Value);
            AddParam(insertCmd, "@State", (object?)result.State ?? DBNull.Value);
            AddParam(insertCmd, "@Zip", (object?)result.Zip ?? DBNull.Value);
            AddParam(insertCmd, "@Phone", (object?)result.PhoneNumber ?? DBNull.Value);
            AddParam(insertCmd, "@Website", (object?)result.Website ?? DBNull.Value);
            AddParam(insertCmd, "@Email", (object?)result.EmailAddress ?? DBNull.Value);
            AddParam(insertCmd, "@Score", result.Confidence);
            AddParam(insertCmd, "@Now", now);
            await insertCmd.ExecuteNonQueryAsync(ct);

            await using var linkCmd = _dbConnection.CreateCommand();
            linkCmd.CommandText = "UPDATE [dbo].[CrawlSources] SET [ChurchId] = @ChurchId WHERE [Id] = @Id";
            AddParam(linkCmd, "@ChurchId", churchId);
            AddParam(linkCmd, "@Id", crawlSourceId);
            await linkCmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var updateCmd = _dbConnection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE [dbo].[Churches]
                SET [CanonicalName] = @Name, [Slug] = @Slug, [Street] = @Street, [City] = @City, [State] = @State,
                    [Zip] = @Zip, [PhoneNumber] = @Phone, [Website] = @Website, [EmailAddress] = @Email,
                    [ConfidenceScore] = @Score, [UpdatedAt] = @Now
                WHERE [Id] = @Id
                """;
            AddParam(updateCmd, "@Id", churchId);
            AddParam(updateCmd, "@Name", (object?)result.CanonicalName ?? DBNull.Value);
            AddParam(updateCmd, "@Slug", slug);
            AddParam(updateCmd, "@Street", (object?)result.Street ?? DBNull.Value);
            AddParam(updateCmd, "@City", (object?)result.City ?? DBNull.Value);
            AddParam(updateCmd, "@State", (object?)result.State ?? DBNull.Value);
            AddParam(updateCmd, "@Zip", (object?)result.Zip ?? DBNull.Value);
            AddParam(updateCmd, "@Phone", (object?)result.PhoneNumber ?? DBNull.Value);
            AddParam(updateCmd, "@Website", (object?)result.Website ?? DBNull.Value);
            AddParam(updateCmd, "@Email", (object?)result.EmailAddress ?? DBNull.Value);
            AddParam(updateCmd, "@Score", result.Confidence);
            AddParam(updateCmd, "@Now", now);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private async Task<string?> DownloadBlobAsync(string blobPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        var container = _blobServiceClient.GetBlobContainerClient("churches");
        var blob = container.GetBlobClient(blobPath);
        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        var download = await blob.DownloadContentAsync(ct);
        return download.Value.Content.ToString();
    }
}

internal sealed record ExtractionRequest(Guid CrawlSourceId, string BlobPath, string Url);

internal sealed record ExtractionResult(
    string? CanonicalName,
    string? Street,
    string? City,
    string? State,
    string? Zip,
    string? PhoneNumber,
    string? Website,
    string? EmailAddress,
    decimal Confidence);
