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
    internal const decimal AddressFieldConfidenceWeight = 0.2m;

    internal const decimal ContactConfidenceWeight = 0.1m;

    private const decimal Tier2Threshold = 0.5m;

    private readonly BlobServiceClient _blobServiceClient;
    private readonly ServiceBusClient _serviceBusClient;

    public ExtractorWorker(
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory)
    {
        _blobServiceClient = blobServiceClientFactory.CreateClient(AzureClientNames.Crgolden);
        _serviceBusClient = serviceBusClientFactory.CreateClient(AzureClientNames.Crgolden);
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
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: DeadLetterReasons.MalformedPayload, cancellationToken: cancellationToken);
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
            await using var geocodingSender = _serviceBusClient.CreateSender(ChurchQueueNames.GeocodingRequests);
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
            await using var enrichmentSender = _serviceBusClient.CreateSender(ChurchQueueNames.EnrichmentRequests);
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
        var itemprop = doc.QuerySelector(MicrodataProperties.Selector(MicrodataProperties.Telephone))?.TextContent?.Trim();
        if (!string.IsNullOrWhiteSpace(itemprop))
        {
            return itemprop;
        }

        var body = doc.Body?.TextContent;
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var match = PhonePattern().Match(body);
        return match.Success ? match.Value.Trim() : null;
    }

    internal static async Task<ExtractionResult> ExtractFromHtmlAsync(string html, string url)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(html));

        var name = Normalizer.NormalizeBlank(document.QuerySelector(MicrodataProperties.Selector(MicrodataProperties.Name))?.TextContent)
                   ?? Normalizer.NormalizeBlank(document.QuerySelector("h1")?.TextContent)
                   ?? Normalizer.NormalizeBlank(document.Title);
        var street = Normalizer.NormalizeBlank(document.QuerySelector(MicrodataProperties.Selector(MicrodataProperties.StreetAddress))?.TextContent);
        var city = Normalizer.NormalizeBlank(document.QuerySelector(MicrodataProperties.Selector(MicrodataProperties.AddressLocality))?.TextContent);
        var state = Normalizer.NormalizeBlank(document.QuerySelector(MicrodataProperties.Selector(MicrodataProperties.AddressRegion))?.TextContent);
        var zip = Normalizer.NormalizeBlank(document.QuerySelector(MicrodataProperties.Selector(MicrodataProperties.PostalCode))?.TextContent);
        var phone = ExtractPhone(document);
        var emailHref = document.QuerySelector($"{MicrodataProperties.Selector(MicrodataProperties.Email)}, a[href^='mailto:']")?.GetAttribute("href");
        var email = Normalizer.NormalizeBlank(emailHref?.Replace("mailto:", string.Empty));

        var confidence = 0m;
        if (!string.IsNullOrWhiteSpace(name))
        {
            confidence += AddressFieldConfidenceWeight;
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            confidence += AddressFieldConfidenceWeight;
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            confidence += AddressFieldConfidenceWeight;
        }

        if (!string.IsNullOrWhiteSpace(zip))
        {
            confidence += AddressFieldConfidenceWeight;
        }

        if (!string.IsNullOrWhiteSpace(phone) || !string.IsNullOrWhiteSpace(email))
        {
            confidence += ContactConfidenceWeight;
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

    [GeneratedRegex(@"\(?\d{3}\)?[\s\-\.]\d{3}[\s\-\.]\d{4}")]
    private static partial Regex PhonePattern();

    private async Task<string?> DownloadBlobAsync(string blobPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        var container = _blobServiceClient.GetBlobContainerClient(BlobContainerNames.Churches);
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