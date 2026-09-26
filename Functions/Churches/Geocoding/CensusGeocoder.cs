namespace Functions.Churches.Geocoding;

using System.Text.Json;

public sealed class CensusGeocoder
{
    internal const string NoStreetReason = "no-street";
    internal const string HttpErrorReason = "http-error";
    internal const string NoMatchReason = "no-match";
    internal const string ExceptionReason = "exception";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _censusBaseAddress;
    private readonly Telemetry _telemetry;

    public CensusGeocoder(IHttpClientFactory httpClientFactory, Uri censusBaseAddress, Telemetry telemetry)
    {
        _httpClientFactory = httpClientFactory;
        _censusBaseAddress = censusBaseAddress;
        _telemetry = telemetry;
    }

    public async Task<(decimal Lat, decimal Lng)> GeocodeAsync(
        string? street,
        string? city,
        string? state,
        string? zip,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(street))
        {
            _telemetry.GeocoderFallback(NoStreetReason);
            return (0m, 0m);
        }

        try
        {
            var requestUri = new UriBuilder(_censusBaseAddress) { Query = BuildCensusQuery(street, city, state, zip) }.Uri;
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(requestUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                _telemetry.GeocoderFallback(HttpErrorReason);
                return (0m, 0m);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var (lat, lng) = ParseCensusResponse(json);
            if (lat == 0m && lng == 0m)
            {
                _telemetry.GeocoderFallback(NoMatchReason);
            }

            return (lat, lng);
        }
        catch
        {
            _telemetry.GeocoderFallback(ExceptionReason);
            return (0m, 0m);
        }
    }

    public async Task<string?> TryBackfillZipAsync(string city, string state, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://api.zippopotam.us/us/{Uri.EscapeDataString(state)}/{Uri.EscapeDataString(city)}";
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(ZipLookupFields.Places, out var places) || places.GetArrayLength() == 0)
            {
                return null;
            }

            return places[0].TryGetProperty(ZipLookupFields.PostCode, out var postCode) ? postCode.GetString() : null;
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
            .GetProperty(CensusGeocoderFields.Result)
            .GetProperty(CensusGeocoderFields.AddressMatches);

        if (matches.GetArrayLength() == 0)
        {
            return (0m, 0m);
        }

        var coords = matches[0].GetProperty(CensusGeocoderFields.Coordinates);
        var lng = (decimal)coords.GetProperty(CensusGeocoderFields.Longitude).GetDouble();
        var lat = (decimal)coords.GetProperty(CensusGeocoderFields.Latitude).GetDouble();
        return (lat, lng);
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
