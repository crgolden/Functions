#pragma warning disable OPENAI001
namespace Functions;

using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;

public class EnrichmentWorker
{
    private readonly ResponsesClient _responsesClient;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly string _model;

    public EnrichmentWorker(
        ResponsesClient responsesClient,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory,
        IConfiguration configuration)
    {
        _responsesClient = responsesClient;
        _serviceBusClient = serviceBusClientFactory.CreateClient("crgolden");
        _model = configuration.GetRequired<string>("OpenAIModel");
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
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var partialJson = JsonSerializer.Serialize(payload.Partial);
        var prompt = $"""
            Extract structured church information from the partial data below.
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
            await using var sender = _serviceBusClient.CreateSender("geocoding-requests");
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
        catch (Exception ex)
        {
            Activity.Current?.AddException(ex);
        }

        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    internal static IReadOnlyList<ChurchAttributeData> EnrichmentAttributes(EnrichedData enriched)
    {
        var attributes = new List<ChurchAttributeData>();
        if (!string.IsNullOrWhiteSpace(enriched.Denomination))
        {
            attributes.Add(new ChurchAttributeData("denomination", enriched.Denomination, "enrichment", 0.6m));
        }

        if (enriched.WorshipStyle != 0)
        {
            attributes.Add(new ChurchAttributeData("worship_style", enriched.WorshipStyle.ToString(System.Globalization.CultureInfo.InvariantCulture), "enrichment", 0.6m));
        }

        return attributes;
    }

    internal static EnrichedData TryParseEnrichment(string json, EnrichmentPartialData partial)
    {
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var endInclusive = end + 1;
                json = json[start..endInclusive];
            }

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            static string? GetStr(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

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
                GetStr(root, "canonicalName") ?? partial.CanonicalName,
                GetStr(root, "city") ?? partial.City,
                GetStr(root, "state") ?? partial.State,
                GetStr(root, "zip") ?? partial.Zip,
                GetInt(root, "worshipStyle"),
                GetStr(root, "primaryLanguage") ?? "English",
                GetBool(root, "acceptsLGBTQ"),
                GetBool(root, "wheelchairAccessible"),
                GetBool(root, "hasNursery"),
                GetBool(root, "hasYouthProgram"),
                GetStr(root, "denomination"),
                ParseServiceSchedules(root),
                ParseMinistries(root),
                ParseCampuses(root));
        }
        catch
        {
            return new EnrichedData(partial.CanonicalName, partial.City, partial.State, partial.Zip, 0, "English", null, null, null, null, null, [], [], []);
        }
    }

    private static IReadOnlyList<CampusData> ParseCampuses(JsonElement root)
    {
        var campuses = new List<CampusData>();
        if (!root.TryGetProperty("campuses", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return campuses;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Str(element, "name");
            var city = Str(element, "city");
            var state = Str(element, "state");
            var zip = Str(element, "zip");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(city)
                || string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(zip))
            {
                continue;
            }

            campuses.Add(new CampusData(name, Str(element, "street"), city, state, zip));
        }

        return campuses;

        static string? Str(JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static IReadOnlyList<MinistryData> ParseMinistries(JsonElement root)
    {
        var ministries = new List<MinistryData>();
        if (!root.TryGetProperty("ministries", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return ministries;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description = element.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            ministries.Add(new MinistryData(name, description));
        }

        return ministries;
    }

    private static IReadOnlyList<ServiceScheduleData> ParseServiceSchedules(JsonElement root)
    {
        var schedules = new List<ServiceScheduleData>();
        if (!root.TryGetProperty("serviceSchedules", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return schedules;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var day = element.TryGetProperty("dayOfWeek", out var d) && d.TryGetInt32(out var n) ? n : -1;
            var start = element.TryGetProperty("startTime", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (day < 0 || day > 6 || string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var description = element.TryGetProperty("description", out var ds) && ds.ValueKind == JsonValueKind.String ? ds.GetString() : null;
            schedules.Add(new ServiceScheduleData((byte)day, start, description));
        }

        return schedules;
    }
}

internal sealed record EnrichmentRequest(Guid CrawlSourceId, string Url, EnrichmentPartialData Partial);

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
