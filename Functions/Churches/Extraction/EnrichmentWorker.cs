#pragma warning disable OPENAI001
namespace Functions.Churches.Extraction;

using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using OpenAI.Responses;

public partial class EnrichmentWorker
{
    internal const int MaxHtmlCharsParsed = 200_000;

    internal const int MaxPageTextCharsInPrompt = 12_000;

    internal const int PromptFrameCharsAllowance = 3_000;

    internal const int CharsPerTokenEstimate = 4;

    internal const int MaxOutputTokens = 3_000;

    internal const int EstimatedTokensPerRequest =
        ((MaxPageTextCharsInPrompt + PromptFrameCharsAllowance) / CharsPerTokenEstimate) + MaxOutputTokens;

    internal const int MaxThrottledAttempts = 5;

    internal const int MaxGateDeferrals = 12;

    internal const int DefaultThrottleRetrySeconds = 60;

    internal const int MaxBackoffDoublings = 6;

    internal const string GateDeferralsProperty = "enrichment-gate-deferrals";

    internal const string ThrottledAttemptsProperty = "enrichment-throttled-attempts";

    internal const string RetryAfterHeader = "Retry-After";

    internal const string RetryAfterMillisecondsHeader = "retry-after-ms";

    private const string NonContentElements = "script, style, noscript, template, svg";

    private readonly ResponsesClient _responsesClient;
    private readonly IOpenAIRateLimiter _rateLimiter;
    private readonly ChurchQueueSenders _senders;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _model;
    private readonly TimeProvider _timeProvider;

    public EnrichmentWorker(
        ResponsesClient responsesClient,
        IOpenAIRateLimiter rateLimiter,
        ChurchQueueSenders senders,
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        TimeProvider? timeProvider = null)
    {
        _responsesClient = responsesClient;
        _rateLimiter = rateLimiter;
        _senders = senders;
        _blobServiceClient = blobServiceClientFactory.CreateClient(AzureClientNames.Crgolden);
        _model = configuration.GetRequired<string>(ChurchSettingKeys.OpenAIModel);
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        var gateDeferrals = CountProperty(message, GateDeferralsProperty);
        var secondsUntilGateRoom = await _rateLimiter.TryAcquireAsync(EstimatedTokensPerRequest, cancellationToken);
        if (secondsUntilGateRoom is not null && gateDeferrals >= MaxGateDeferrals)
        {
            Telemetry.Tracing.RecordHandledFailure("enrichment.gate-exhausted", $"{payload.Url} (deferral {gateDeferrals})");
            await SendGeocodingRequestAsync(BuildFallbackEnriched(payload.Partial), payload, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        if (secondsUntilGateRoom is { } earliestSeconds)
        {
            Telemetry.Tracing.RecordHandledFailure("enrichment.rate-gated", $"{payload.Url} (deferral {gateDeferrals + 1})");
            await DeferAsync(message, GateDeferralsProperty, gateDeferrals + 1, DeferralDelaySeconds(earliestSeconds, gateDeferrals), cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var partialJson = JsonSerializer.Serialize(payload.Partial);
        var html = await DownloadBlobAsync(payload.BlobPath, cancellationToken);
        var pageContent = await BuildPageContentAsync(html);
        var prompt = $"""
            Extract structured church information for the church below. The partial data was already
            extracted by an earlier pass and may be incomplete (missing city/state/zip, etc.) — use the
            page text as the primary source of truth to fill in whatever the partial data is missing,
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
            Page text (may be truncated): {pageContent}
            """;

        try
        {
            var options = new CreateResponseOptions(_model, [ResponseItem.CreateUserMessageItem(prompt)])
            {
                MaxOutputTokenCount = MaxOutputTokens,
                ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Minimal },
            };
            var response = await _responsesClient.CreateResponseAsync(options, cancellationToken);
            var outputText = response?.Value?.GetOutputText()
                ?? throw new InvalidOperationException("OpenAI returned no output.");
            var enriched = TryParseEnrichment(outputText, payload.Partial);
            await SendGeocodingRequestAsync(enriched, payload, cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (ClientResultException ex) when (ex.Status == (int)HttpStatusCode.TooManyRequests
                                               && CountProperty(message, ThrottledAttemptsProperty) < MaxThrottledAttempts)
        {
            var throttledAttempts = CountProperty(message, ThrottledAttemptsProperty) + 1;
            Telemetry.Tracing.RecordHandledFailure("enrichment.throttled", $"{payload.Url} (attempt {throttledAttempts})");
            await DeferAsync(message, ThrottledAttemptsProperty, throttledAttempts, DeferralDelaySeconds(RetryAfterSeconds(ex), throttledAttempts - 1), cancellationToken);
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (ClientResultException ex) when (ex.Status != (int)HttpStatusCode.TooManyRequests && message.DeliveryCount < 3)
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

    internal static async Task<string> BuildPageContentAsync(string? html)
    {
        if (html is null)
        {
            return ChurchDefaults.PageContentUnavailable;
        }

        using var context = BrowsingContext.New(Configuration.Default);
        var parsedHtml = html[..Math.Min(html.Length, MaxHtmlCharsParsed)];
        using var document = await context.OpenAsync(request => request.Content(parsedHtml));
        foreach (var element in document.QuerySelectorAll(NonContentElements))
        {
            element.Remove();
        }

        var text = Whitespace().Replace(document.Body?.TextContent ?? document.DocumentElement.TextContent, " ").Trim();
        return text[..Math.Min(text.Length, MaxPageTextCharsInPrompt)];
    }

    internal static double RetryAfterSeconds(ClientResultException exception)
    {
        if (exception.GetRawResponse() is not { } response)
        {
            return DefaultThrottleRetrySeconds;
        }

        if (response.Headers.TryGetValue(RetryAfterMillisecondsHeader, out var milliseconds)
            && double.TryParse(milliseconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedMilliseconds))
        {
            return parsedMilliseconds / 1000;
        }

        return response.Headers.TryGetValue(RetryAfterHeader, out var value)
               && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : DefaultThrottleRetrySeconds;
    }

    internal static double DeferralDelaySeconds(double earliestSeconds, int previousDeferrals) =>
        earliestSeconds
        + (Random.Shared.NextDouble() * RedisOpenAIRateLimiter.WindowSeconds * Math.Pow(2, Math.Min(previousDeferrals, MaxBackoffDoublings)));

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

    private static int CountProperty(ServiceBusReceivedMessage message, string name) =>
        message.ApplicationProperties.TryGetValue(name, out var value) && value is int count ? count : 0;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private async Task DeferAsync(
        ServiceBusReceivedMessage message,
        string counterProperty,
        int count,
        double delaySeconds,
        CancellationToken cancellationToken)
    {
        var deferred = new ServiceBusMessage(message.Body);
        foreach (var (key, value) in message.ApplicationProperties)
        {
            deferred.ApplicationProperties[key] = value;
        }

        deferred.ApplicationProperties[counterProperty] = count;
        await _senders
            .For(ChurchQueueNames.EnrichmentRequests)
            .ScheduleMessageAsync(deferred, _timeProvider.GetUtcNow().AddSeconds(delaySeconds), cancellationToken);
    }

    private async Task<string?> DownloadBlobAsync(string? blobPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        var container = _blobServiceClient.GetBlobContainerClient(BlobContainerNames.Churches);
        var blob = container.GetBlobClient(blobPath);
        try
        {
            var download = await blob.DownloadContentAsync(ct);
            return download.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task SendGeocodingRequestAsync(EnrichedData enriched, EnrichmentRequest payload, CancellationToken cancellationToken)
    {
        await _senders.For(ChurchQueueNames.GeocodingRequests).SendMessageAsync(
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