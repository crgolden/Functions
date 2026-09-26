namespace Functions.Churches.Geocoding;

using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Shared.Domain;

public sealed class GeocoderWorker
{
    private readonly CensusGeocoder _geocoder;
    private readonly ChurchWriter _churchWriter;
    private readonly Telemetry _telemetry;

    public GeocoderWorker(CensusGeocoder geocoder, ChurchWriter churchWriter, Telemetry telemetry)
    {
        _geocoder = geocoder;
        _churchWriter = churchWriter;
        _telemetry = telemetry;
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
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: DeadLetterReasons.MalformedPayload, cancellationToken: cancellationToken);
            return;
        }

        var normalizedState = Normalizer.NormalizeState(payload.State);
        if (normalizedState is null)
        {
            Telemetry.Tracing.RecordHandledFailure("geocoder.unresolvable-state", $"CrawlSourceId={payload.CrawlSourceId}");
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var normalizedZip = Normalizer.NormalizeZip(payload.Zip) ?? payload.Zip;
        if (string.IsNullOrWhiteSpace(normalizedZip) && !string.IsNullOrWhiteSpace(payload.City))
        {
            normalizedZip = await _geocoder.TryBackfillZipAsync(payload.City, normalizedState, cancellationToken);
            _telemetry.ZipBackfillAttempted(normalizedZip is null ? "failure" : "success");
        }

        if (string.IsNullOrWhiteSpace(normalizedZip))
        {
            Telemetry.Tracing.RecordHandledFailure("geocoder.unresolvable-zip", $"CrawlSourceId={payload.CrawlSourceId}");
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var normalizedName = Normalizer.NormalizeBlank(payload.CanonicalName);
        if (normalizedName is null)
        {
            Telemetry.Tracing.RecordHandledFailure("geocoder.unresolvable-name", $"CrawlSourceId={payload.CrawlSourceId}");
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var normalizedCity = Normalizer.NormalizeBlank(payload.City);
        if (normalizedCity is null)
        {
            Telemetry.Tracing.RecordHandledFailure("geocoder.unresolvable-city", $"CrawlSourceId={payload.CrawlSourceId}");
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var normalizedLanguage = Normalizer.NormalizeBlank(payload.PrimaryLanguage) ?? ChurchDefaults.PrimaryLanguage;

        var normalizedWorshipStyle = payload.WorshipStyle is >= ChurchBuilder.MinWorshipStyle and <= ChurchBuilder.MaxWorshipStyle
            ? payload.WorshipStyle
            : ChurchWorshipStyles.Unknown;
        if (normalizedWorshipStyle != payload.WorshipStyle)
        {
            Telemetry.Tracing.RecordHandledFailure("geocoder.invalid-worship-style", $"CrawlSourceId={payload.CrawlSourceId}");
        }

        var (lat, lng) = await GeocodeAsync(payload, cancellationToken);
        var campuses = await GeocodeCampusesAsync(payload.Campuses, cancellationToken);
        var normalizedCampuses = campuses
            .Select(campus => Normalizer.NormalizeState(campus.State) is { } campusState
                ? campus with { State = campusState }
                : null)
            .OfType<CampusData>()
            .ToList();
        await _churchWriter.UpsertAsync(
            payload with
            {
                CanonicalName = normalizedName,
                City = normalizedCity,
                State = normalizedState,
                Zip = normalizedZip,
                PrimaryLanguage = normalizedLanguage,
                WorshipStyle = normalizedWorshipStyle,
                Campuses = normalizedCampuses,
            },
            lat,
            lng,
            cancellationToken);
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    internal async Task<(decimal Lat, decimal Lng)> GeocodeAsync(GeocodingRequest req, CancellationToken ct)
    {
        if (req.Latitude.HasValue && req.Longitude.HasValue)
        {
            if (IsValidCoordinate(req.Latitude.Value, req.Longitude.Value))
            {
                return (req.Latitude.Value, req.Longitude.Value);
            }

            Telemetry.Tracing.RecordHandledFailure("geocoder.invalid-coordinates", $"CrawlSourceId={req.CrawlSourceId}");
        }

        return await _geocoder.GeocodeAsync(req.Street, req.City, req.State, req.Zip, ct);
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
                if (IsValidCoordinate(campus.Latitude.Value, campus.Longitude.Value))
                {
                    resolved.Add(campus);
                    continue;
                }

                Telemetry.Tracing.RecordHandledFailure("geocoder.invalid-coordinates", $"CampusName={campus.Name}");
            }

            var (lat, lng) = await _geocoder.GeocodeAsync(campus.Street, campus.City, campus.State, campus.Zip, ct);
            resolved.Add(campus with { Latitude = lat, Longitude = lng });
        }

        return resolved;
    }

    private static bool IsValidCoordinate(decimal latitude, decimal longitude) =>
        latitude is >= -90m and <= 90m && longitude is >= -180m and <= 180m;
}
