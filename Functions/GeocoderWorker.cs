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

        var normalizedState = Normalizer.NormalizeState(payload.State);
        if (normalizedState is null)
        {
            // ChurchBuilder requires an exact 2-letter state code (Shared.Domain), so a record with
            // no resolvable state can never satisfy that invariant. There's no reliable way to infer
            // a state from a city name alone (too many duplicate city names across states), and
            // nobody manually triages the dead-letter queue, so dead-lettering this would just be
            // permanent, unactioned noise. Record it as a trace event and drop the message, matching
            // ScraperWorker's handling of other expected-and-unrecoverable failures.
            Telemetry.Tracing.RecordHandledFailure("geocoder.unresolvable-state", $"CrawlSourceId={payload.CrawlSourceId}");
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var normalizedZip = Normalizer.NormalizeZip(payload.Zip) ?? payload.Zip;
        if (string.IsNullOrWhiteSpace(normalizedZip) && !string.IsNullOrWhiteSpace(payload.City))
        {
            normalizedZip = await TryBackfillZipAsync(_httpClientFactory, payload.City, normalizedState, cancellationToken);
            Telemetry.Metrics.ZipBackfillAttempted(normalizedZip is null ? "failure" : "success");
        }

        if (string.IsNullOrWhiteSpace(normalizedZip))
        {
            // ChurchBuilder.WithZip throws ArgumentException on an empty value (Shared.Domain). The
            // zip-backfill lookup above already tried to resolve one from city/state; if that also
            // failed there's nothing left to try, so drop the message the same way as an unresolvable
            // state rather than dead-lettering something nobody will ever review.
            Telemetry.Tracing.RecordHandledFailure("geocoder.unresolvable-zip", $"CrawlSourceId={payload.CrawlSourceId}");
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var (lat, lng) = await GeocodeAsync(payload, cancellationToken);
        var campuses = await GeocodeCampusesAsync(payload.Campuses, cancellationToken);
        var normalizedCampuses = campuses
            .Select(campus => campus with { State = Normalizer.NormalizeState(campus.State) ?? string.Empty })
            .ToList();
        await _churchWriter.UpsertAsync(payload with { State = normalizedState, Zip = normalizedZip, Campuses = normalizedCampuses }, lat, lng, cancellationToken);
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    // Reverse-looks-up a zip from city/state via Zippopotam.us (free, no auth) when the source
    // never captured one. Best-effort: takes the first matching place when a city name resolves to
    // multiple zips, since an approximate zip beats permanently dropping an otherwise-good record.
    internal static async Task<string?> TryBackfillZipAsync(IHttpClientFactory httpClientFactory, string city, string state, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var url = $"https://api.zippopotam.us/us/{Uri.EscapeDataString(state)}/{Uri.EscapeDataString(city)}";
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("places", out var places) || places.GetArrayLength() == 0)
            {
                return null;
            }

            return places[0].TryGetProperty("post code", out var postCode) ? postCode.GetString() : null;
        }
        catch
        {
            return null;
        }
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
