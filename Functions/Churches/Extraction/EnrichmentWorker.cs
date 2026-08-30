#pragma warning disable OPENAI001
namespace Functions.Churches.Extraction;

using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;

public class EnrichmentWorker
{
    internal const int MaxHtmlCharsInPrompt = 20_000;

    private readonly ResponsesClient _responsesClient;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _model;

    public EnrichmentWorker(
        ResponsesClient responsesClient,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory,
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        IConfiguration configuration)
    {
        _responsesClient = responsesClient;
        _serviceBusClient = serviceBusClientFactory.CreateClient(AzureClientNames.Crgolden);
        _blobServiceClient = blobServiceClientFactory.CreateClient(AzureClientNames.Crgolden);
        _model = configuration.GetRequired<string>(ChurchSettingKeys.OpenAIModel);
    }

    [Function(nameof(EnrichmentWorker))]
    public async Task Run(
        [ServiceBusTrigger("enrichment-requests", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var payload = message.Body.ToObjectFromJson<EnrichmentRequest>();
        if (payload is null)
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: DeadLetterReasons.MalformedPayload, cancellationToken: cancellationToken);
            return;
        }

        var partialJson = JsonSerializer.Serialize(payload.Partial);
        var html = await DownloadBlobAsync(payload.BlobPath, cancellationToken);
        var pageContent = BuildPageContent(html);
        var prompt = $"""
            Extract structured church information for the church below. The partial data was already
            extracted by an earlier pass and may be incomplete (missing city/state/zip, etc.) — use the
            raw page HTML as the primary source of truth to fill in whatever the partial data is missing,
            especially city/state/zip, which are required for this church to be locatable on a map.
            Return ONLY valid JSON with fields: canonicalName, city, state, zip,
            worshipStyle (0=Unknown 1=Traditional 2=Contemporary 3=Blended 4=Charismatic 5=Liturgical),
            primaryLanguage, denomination (e.g. "Baptist", "Roman Catholic", "Non-denominational", or null if unknown),
            acceptsLGBTQ (true/false/null), wheelchairAccessible (true/false/null),
            hasNursery (true/false/null), hasYouthProgram (true/false/null),
            serviceSchedules (array of objects each having dayOfWeek 0=Sunday..6=Saturday, startTime "HH:mm" 24-hour, and description; empty array if none found),
            ministries (array of objects each having name and description for the church's ministries/programs; empty array if none found),
            campuses (array of objects each having name, street, city, state, zip for additional/satellite locations; empty array if single-site).
            Source URL: {payload.Url}
            Partial data: {partialJson}
            Raw page HTML (may be truncated): {pageContent}
            """;

        try
        {
            var response = await _responsesClient.CreateResponseAsync(
                _model,
                [ResponseItem.CreateUserMessageItem(prompt)],
                cancellationToken: cancellationToken);
            var outputText = response?.Value?.GetOutputText()
                ?? throw new InvalidOperationException("OpenAI returned no output.");
            var enriched = TryParseEnrichment(outputText, payload.Partial);
            await SendGeocodingRequestAsync(enriched, payload, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (ClientResultException ex) when (message.DeliveryCount < 3)
        {
            Telemetry.Tracing.RecordHandledFailure("enrichment.retry", $"{ex.GetType().Name}: {payload.Url} (delivery {message.DeliveryCount})");
            await messageActions.AbandonMessageAsync(message, cancellationToken: cancellationToken);
        }
        catch (ClientResultException ex)
        {
            Telemetry.Tracing.RecordHandledFailure("enrichment.degraded", $"{ex.GetType().Name}: {payload.Url}");
            await SendGeocodingRequestAsync(BuildFallbackEnriched(payload.Partial), payload, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
    }

    internal static string BuildPageContent(string? html) =>
        html is null ? ChurchDefaults.PageContentUnavailable : html[..Math.Min(html.Length, MaxHtmlCharsInPrompt)];

    internal static IReadOnlyList<ChurchAttributeData> EnrichmentAttributes(EnrichedData enriched)
    {
        var attributes = new List<ChurchAttributeData>();
        if (!string.IsNullOrWhiteSpace(enriched.Denomination))
        {
            attributes.Add(new ChurchAttributeData(ChurchAttributeKeys.Denomination, enriched.Denomination, ChurchImportSources.Enrichment, ChurchImportConfidence.Enrichment));
        }

        if (enriched.WorshipStyle != 0)
        {
            attributes.Add(new ChurchAttributeData(ChurchAttributeKeys.WorshipStyle, enriched.WorshipStyle.ToString(CultureInfo.InvariantCulture), ChurchImportSources.Enrichment, ChurchImportConfidence.Enrichment));
        }

        return attributes;
    }

    internal static EnrichedData TryParseEnrichment(string json, EnrichmentPartialData partial)
    {
        try
        {
            var start = json.IndexOf('{', StringComparison.Ordinal);
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var endInclusive = end + 1;
                json = json[start..endInclusive];
            }

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            static bool? GetBool(JsonElement el, string key)
            {
                if (!el.TryGetProperty(key, out var v))
                {
                    return null;
                }

                if (v.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (v.ValueKind == JsonValueKind.False)
                {
                    return false;
                }

                return null;
            }

            static int GetInt(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : 0;

            return new EnrichedData(
                Normalizer.GetJsonString(root, EnrichmentResponseFields.CanonicalName) ?? partial.CanonicalName,
                Normalizer.GetJsonString(root, EnrichmentResponseFields.City) ?? partial.City,
                Normalizer.GetJsonString(root, EnrichmentResponseFields.State) ?? partial.State,
                Normalizer.GetJsonString(root, EnrichmentResponseFields.Zip) ?? partial.Zip,
                GetInt(root, EnrichmentResponseFields.WorshipStyle),
                Normalizer.GetJsonString(root, EnrichmentResponseFields.PrimaryLanguage) ?? ChurchDefaults.PrimaryLanguage,
                GetBool(root, EnrichmentResponseFields.AcceptsLgbtq),
                GetBool(root, EnrichmentResponseFields.WheelchairAccessible),
                GetBool(root, EnrichmentResponseFields.HasNursery),
                GetBool(root, EnrichmentResponseFields.HasYouthProgram),
                Normalizer.GetJsonString(root, EnrichmentResponseFields.Denomination),
                ParseServiceSchedules(root),
                ParseMinistries(root),
                ParseCampuses(root));
        }
        catch
        {
            return BuildFallbackEnriched(partial);
        }
    }

    private static EnrichedData BuildFallbackEnriched(EnrichmentPartialData partial) =>
        new(partial.CanonicalName, partial.City, partial.State, partial.Zip, ChurchWorshipStyles.Unknown, ChurchDefaults.PrimaryLanguage, null, null, null, null, null, [], [], []);

    private static List<CampusData> ParseCampuses(JsonElement root)
    {
        var campuses = new List<CampusData>();
        if (!root.TryGetProperty(EnrichmentResponseFields.Campuses, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return campuses;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Normalizer.GetJsonString(element, EnrichmentResponseFields.Name);
            var city = Normalizer.GetJsonString(element, EnrichmentResponseFields.City);
            var state = Normalizer.GetJsonString(element, EnrichmentResponseFields.State);
            var zip = Normalizer.GetJsonString(element, EnrichmentResponseFields.Zip);
            if (name is null || city is null || state is null || zip is null)
            {
                continue;
            }

            campuses.Add(new CampusData(name, Normalizer.GetJsonString(element, EnrichmentResponseFields.Street), city, state, zip));
        }

        return campuses;
    }

    private static List<MinistryData> ParseMinistries(JsonElement root)
    {
        var ministries = new List<MinistryData>();
        if (!root.TryGetProperty(EnrichmentResponseFields.Ministries, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return ministries;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Normalizer.GetJsonString(element, EnrichmentResponseFields.Name);
            if (name is null)
            {
                continue;
            }

            ministries.Add(new MinistryData(name, Normalizer.GetJsonString(element, EnrichmentResponseFields.Description)));
        }

        return ministries;
    }

    private static List<ServiceScheduleData> ParseServiceSchedules(JsonElement root)
    {
        var schedules = new List<ServiceScheduleData>();
        if (!root.TryGetProperty(EnrichmentResponseFields.ServiceSchedules, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return schedules;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var day = GetDayOfWeek(element);
            var start = Normalizer.GetJsonString(element, EnrichmentResponseFields.StartTime);
            if (day is null || start is null)
            {
                continue;
            }

            schedules.Add(new ServiceScheduleData(day.Value, start, Normalizer.GetJsonString(element, EnrichmentResponseFields.Description)));
        }

        return schedules;

        static byte? GetDayOfWeek(JsonElement el)
        {
            if (!el.TryGetProperty(EnrichmentResponseFields.DayOfWeek, out var v) || !v.TryGetInt32(out var n) || n is < 0 or > 6)
            {
                return null;
            }

            return (byte)n;
        }
    }

    private async Task<string?> DownloadBlobAsync(string? blobPath, CancellationToken ct)
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

    private async Task SendGeocodingRequestAsync(EnrichedData enriched, EnrichmentRequest payload, CancellationToken cancellationToken)
    {
        await using var sender = _serviceBusClient.CreateSender(ChurchQueueNames.GeocodingRequests);
        await sender.SendMessageAsync(
            new ServiceBusMessage(JsonSerializer.Serialize(new GeocodingRequest(
                payload.CrawlSourceId,
                enriched.CanonicalName,
                Street: null,
                enriched.City,
                enriched.State,
                enriched.Zip,
                PhoneNumber: null,
                Website: payload.Url,
                EmailAddress: null,
                enriched.WorshipStyle,
                enriched.PrimaryLanguage,
                enriched.AcceptsLGBTQ,
                enriched.WheelchairAccessible,
                enriched.HasNursery,
                enriched.HasYouthProgram,
                Confidence: 0.6m,
                DenominationName: enriched.Denomination)
            {
                Attributes = EnrichmentAttributes(enriched),
                ServiceSchedules = enriched.ServiceSchedules,
                Ministries = enriched.Ministries,
                Campuses = enriched.Campuses,
            })),
            cancellationToken);
    }
}

internal sealed record EnrichmentRequest(Guid CrawlSourceId, string Url, string? BlobPath, EnrichmentPartialData Partial);

internal sealed record EnrichmentPartialData(
    string? CanonicalName,
    string? City,
    string? State,
    string? Zip);

internal sealed record EnrichedData(
    string? CanonicalName,
    string? City,
    string? State,
    string? Zip,
    int WorshipStyle,
    string PrimaryLanguage,
    bool? AcceptsLGBTQ,
    bool? WheelchairAccessible,
    bool? HasNursery,
    bool? HasYouthProgram,
    string? Denomination,
    IReadOnlyList<ServiceScheduleData> ServiceSchedules,
    IReadOnlyList<MinistryData> Ministries,
    IReadOnlyList<CampusData> Campuses);
#pragma warning restore OPENAI001