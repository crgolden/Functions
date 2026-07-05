namespace Functions;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

public sealed class GeocoderWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChurchWriter _churchWriter;
    private readonly string _censusBaseUrl;

    public GeocoderWorker(IHttpClientFactory httpClientFactory, ChurchWriter churchWriter, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _churchWriter = churchWriter;
        _censusBaseUrl = configuration.GetRequired<string>("CensusGeocoderUrl");
    }

    [Function(nameof(GeocoderWorker))]
    public async Task Run(
        [ServiceBusTrigger("geocoding-requests", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var payload = message.Body.ToObjectFromJson<GeocodingRequest>();
        if (payload is null)
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "malformed-payload", cancellationToken: cancellationToken);
            return;
        }

        var (lat, lng) = await GeocodeAsync(payload, cancellationToken);
        var campuses = await GeocodeCampusesAsync(payload.Campuses, cancellationToken);
        await _churchWriter.UpsertAsync(payload with { Campuses = campuses }, lat, lng, cancellationToken);
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    internal static (decimal Lat, decimal Lng) ParseCensusResponse(string json)
    {
        var doc = JsonDocument.Parse(json);
        var matches = doc.RootElement
            .GetProperty("result")
            .GetProperty("addressMatches");

        if (matches.GetArrayLength() == 0)
        {
            return (0m, 0m);
        }

        var coords = matches[0].GetProperty("coordinates");
        var lng = (decimal)coords.GetProperty("x").GetDouble();
        var lat = (decimal)coords.GetProperty("y").GetDouble();
        return (lat, lng);
    }

    // Shared Census lookup for both the per-message worker path and the ReGeocodeJob cleanup pass.
    // Any miss or error yields a zero coordinate so callers never throw on a geocode failure.
    internal static async Task<(decimal Lat, decimal Lng)> GeocodeAddressCoreAsync(
        IHttpClientFactory httpClientFactory,
        string censusBaseUrl,
        string? street,
        string? city,
        string? state,
        string? zip,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(street))
        {
            return (0m, 0m);
        }

        try
        {
            var query = BuildCensusQuery(street, city, state, zip);
            var client = httpClientFactory.CreateClient();
            var response = await client.GetAsync($"{censusBaseUrl}?{query}", ct);
            if (!response.IsSuccessStatusCode)
            {
                Telemetry.Metrics.GeocoderFallback("http-error");
                return (0m, 0m);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var (lat, lng) = ParseCensusResponse(json);
            if (lat == 0m && lng == 0m)
            {
                Telemetry.Metrics.GeocoderFallback("no-match");
            }

            return (lat, lng);
        }
        catch
        {
            Telemetry.Metrics.GeocoderFallback("exception");
            return (0m, 0m);
        }
    }

    internal async Task<(decimal Lat, decimal Lng)> GeocodeAsync(GeocodingRequest req, CancellationToken ct)
    {
        // Sources that already carry authoritative coordinates (e.g. OSM) bypass Census.
        if (req.Latitude.HasValue && req.Longitude.HasValue)
        {
            return (req.Latitude.Value, req.Longitude.Value);
        }

        return await GeocodeAddressCoreAsync(_httpClientFactory, _censusBaseUrl, req.Street, req.City, req.State, req.Zip, ct);
    }

    internal async Task<IReadOnlyList<CampusData>> GeocodeCampusesAsync(IReadOnlyList<CampusData> campuses, CancellationToken ct)
    {
        if (campuses.Count == 0)
        {
            return campuses;
        }

        var resolved = new List<CampusData>(campuses.Count);
        foreach (var campus in campuses)
        {
            if (campus.Latitude.HasValue && campus.Longitude.HasValue)
            {
                resolved.Add(campus);
                continue;
            }

            var (lat, lng) = await GeocodeAddressCoreAsync(_httpClientFactory, _censusBaseUrl, campus.Street, campus.City, campus.State, campus.Zip, ct);
            resolved.Add(campus with { Latitude = lat, Longitude = lng });
        }

        return resolved;
    }

    private static string BuildCensusQuery(string? street, string? city, string? state, string? zip)
    {
        var parts = new List<string> { "benchmark=Public_AR_Current", "format=json" };
        if (!string.IsNullOrWhiteSpace(street))
        {
            parts.Add($"street={Uri.EscapeDataString(street)}");
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            parts.Add($"city={Uri.EscapeDataString(city)}");
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            parts.Add($"state={Uri.EscapeDataString(state)}");
        }

        if (!string.IsNullOrWhiteSpace(zip))
        {
            parts.Add($"zip={Uri.EscapeDataString(zip)}");
        }

        return string.Join("&", parts);
    }
}

public sealed record GeocodingRequest(
    Guid CrawlSourceId,
    string? CanonicalName,
    string? Street,
    string? City,
    string? State,
    string? Zip,
    string? PhoneNumber,
    string? Website,
    string? EmailAddress,
    int WorshipStyle,
    string PrimaryLanguage,
    bool? AcceptsLGBTQ,
    bool? WheelchairAccessible,
    bool? HasNursery,
    bool? HasYouthProgram,
    decimal Confidence,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? DenominationName = null)
{
    // Collection payloads default to empty (never null). Positional record parameters cannot default
    // to [] (not a compile-time constant), so these are declared as init properties instead.
    public IReadOnlyList<ChurchAttributeData> Attributes { get; init; } = [];

    public IReadOnlyList<ServiceScheduleData> ServiceSchedules { get; init; } = [];

    public IReadOnlyList<MinistryData> Ministries { get; init; } = [];

    public IReadOnlyList<CampusData> Campuses { get; init; } = [];
}

public sealed record ChurchAttributeData(string Key, string Value, string Source, decimal Confidence);

public sealed record ServiceScheduleData(byte DayOfWeek, string StartTime, string? Description);

public sealed record MinistryData(string Name, string? Description);

public sealed record CampusData(
    string Name,
    string? Street,
    string City,
    string State,
    string Zip,
    decimal? Latitude = null,
    decimal? Longitude = null);
