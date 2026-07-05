namespace Functions;

using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;

public partial class ExtractorWorker
{
    private const decimal Tier2Threshold = 0.5m;

    private readonly BlobServiceClient _blobServiceClient;
    private readonly ServiceBusClient _serviceBusClient;

    public ExtractorWorker(
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory)
    {
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
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "malformed-payload", cancellationToken: cancellationToken);
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
            await using var geocodingSender = _serviceBusClient.CreateSender("geocoding-requests");
            await geocodingSender.SendMessageAsync(
                new ServiceBusMessage(JsonSerializer.Serialize(new GeocodingRequest(
                    payload.CrawlSourceId,
                    result.CanonicalName,
                    result.Street,
                    result.City,
                    result.State,
                    result.Zip,
                    result.PhoneNumber,
                    result.Website,
                    result.EmailAddress,
                    WorshipStyle: 0,
                    PrimaryLanguage: "English",
                    AcceptsLGBTQ: null,
                    WheelchairAccessible: null,
                    HasNursery: null,
                    HasYouthProgram: null,
                    result.Confidence))),
                cancellationToken);
        }
        else
        {
            await using var enrichmentSender = _serviceBusClient.CreateSender("enrichment-requests");
            await enrichmentSender.SendMessageAsync(
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
        var match = PhonePattern().Match(body);
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

    /// <summary>Returns a compiled regex that matches North American phone numbers.</summary>
    [GeneratedRegex(@"\(?\d{3}\)?[\s\-\.]\d{3}[\s\-\.]\d{4}")]
    private static partial Regex PhonePattern();

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
