namespace Functions;

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;

public sealed partial class BulkImportJob
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly DbConnection _dbConnection;
    private readonly ILogger<BulkImportJob> _logger;

    public BulkImportJob(
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory,
        DbConnection dbConnection,
        ILogger<BulkImportJob> logger)
    {
        _blobServiceClient = blobServiceClientFactory.CreateClient("crgolden");
        _serviceBusClient = serviceBusClientFactory.CreateClient("crgolden");
        _dbConnection = dbConnection;
        _logger = logger;
    }

    [Function(nameof(BulkImportJob))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Admin, "post", Route = "bulk-import")] HttpRequestData req,
        CancellationToken cancellationToken = default)
    {
        var source = req.Query["source"] ?? "irs";
        var blobPath = req.Query["blobPath"];
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("blobPath query parameter is required.", cancellationToken);
            return bad;
        }

        var container = _blobServiceClient.GetBlobContainerClient("imports");
        var blobClient = container.GetBlobClient(blobPath);
        var exists = await blobClient.ExistsAsync(cancellationToken);
        if (!exists.Value)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync($"Blob not found: {blobPath}", cancellationToken);
            return notFound;
        }

        var download = await blobClient.DownloadContentAsync(cancellationToken);
        var content = download.Value.Content.ToString();

        var records = source == "osm"
            ? ParseOsm(content)
            : ParseIrsCsv(content);

        // Dedup is set-based: one query loads all existing name+state keys up front instead of a
        // round-trip per row, which kept large imports under the HTTP timeout. Adding each published
        // key to the set also collapses duplicates within the same file.
        var seenKeys = await LoadExistingKeysAsync(cancellationToken);
        var skipped = 0;
        var messages = new List<ServiceBusMessage>();

        foreach (var record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.CanonicalName) && !string.IsNullOrWhiteSpace(record.State)
                && !seenKeys.Add(DedupKey(record.CanonicalName, record.State)))
            {
                skipped++;
                continue;
            }

            messages.Add(new ServiceBusMessage(JsonSerializer.Serialize(record)));
        }

        // Send in batches: thousands of individual round-trips to Service Bus would exceed the HTTP
        // timeout, so messages go out in chunks.
        await using var sender = _serviceBusClient.CreateSender("geocoding-requests");
        foreach (var batch in messages.Chunk(100))
        {
            await sender.SendMessagesAsync(batch, cancellationToken);
        }

        var published = messages.Count;

        LogImportResult(_logger, published, skipped, source);
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteStringAsync(JsonSerializer.Serialize(new { published, skipped }), cancellationToken);
        return ok;
    }

    internal static IEnumerable<GeocodingRequest> ParseIrsCsv(string csv)
    {
        using var reader = new System.IO.StringReader(csv);
        var header = reader.ReadLine();
        if (header is null)
        {
            yield break;
        }

        var columns = SplitCsvRow(header);
        var nameIdx = IndexOf(columns, "NAME");
        var streetIdx = IndexOf(columns, "STREET");
        var cityIdx = IndexOf(columns, "CITY");
        var stateIdx = IndexOf(columns, "STATE");
        var zipIdx = IndexOf(columns, "ZIP");
        var nteeIdx = IndexOf(columns, "NTEE_CD");

        // Optional pre-geocoded coordinates (added by Tools/Seeding/Add-CensusBatchGeocode.ps1).
        // When present, GeocoderWorker short-circuits the per-message Census lookup.
        var latIdx = IndexOf(columns, "Latitude");
        var lngIdx = IndexOf(columns, "Longitude");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var fields = SplitCsvRow(line);
            if (fields.Length < columns.Length)
            {
                continue;
            }

            var name = SafeGet(fields, nameIdx);
            var state = SafeGet(fields, stateIdx);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(state))
            {
                continue;
            }

            var ntee = SafeGet(fields, nteeIdx);
            var (latitude, longitude) = ParseCoordinates(SafeGet(fields, latIdx), SafeGet(fields, lngIdx));
            yield return new GeocodingRequest(
                CrawlSourceId: Guid.CreateVersion7(DateTimeOffset.UtcNow),
                CanonicalName: name,
                Street: SafeGet(fields, streetIdx),
                City: SafeGet(fields, cityIdx),
                State: state,
                Zip: SafeGet(fields, zipIdx),
                PhoneNumber: null,
                Website: null,
                EmailAddress: null,
                WorshipStyle: NteeToWorshipStyle(ntee),
                PrimaryLanguage: "English",
                AcceptsLGBTQ: null,
                WheelchairAccessible: null,
                HasNursery: null,
                HasYouthProgram: null,
                Confidence: 0.5m,
                Latitude: latitude,
                Longitude: longitude,
                DenominationName: NteeToDenomination(ntee))
            {
                Attributes = IrsAttributes(ntee),
            };
        }
    }

    internal static (decimal? Latitude, decimal? Longitude) ParseCoordinates(string? lat, string? lng)
    {
        // Both must parse to a non-zero pair; a 0,0 (Census no-match) is treated as "not geocoded"
        // so the downstream worker can retry rather than persisting the null-island coordinate.
        if (decimal.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            && decimal.TryParse(lng, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
            && (latitude != 0m || longitude != 0m))
        {
            return (latitude, longitude);
        }

        return (null, null);
    }

    internal static IEnumerable<GeocodingRequest> ParseOsm(string json)
    {
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("elements", out var elements))
        {
            yield break;
        }

        foreach (var element in elements.EnumerateArray())
        {
            if (!element.TryGetProperty("tags", out var tags))
            {
                continue;
            }

            var name = GetOsmTag(tags, "name");
            var state = Normalizer.NormalizeState(GetOsmTag(tags, "addr:state"));
            var city = GetOsmTag(tags, "addr:city");
            var zip = GetOsmTag(tags, "addr:postcode");

            // [dbo].[Churches] requires non-null City and Zip, so records missing either are skipped
            // rather than dead-lettered downstream by GeocoderWorker.
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(state)
                || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(zip))
            {
                continue;
            }

            var (latitude, longitude) = GetOsmCoordinates(element);

            yield return new GeocodingRequest(
                CrawlSourceId: Guid.CreateVersion7(DateTimeOffset.UtcNow),
                CanonicalName: name,
                Street: CombineStreet(GetOsmTag(tags, "addr:housenumber"), GetOsmTag(tags, "addr:street")),
                City: city,
                State: state,
                Zip: zip,
                PhoneNumber: FirstPhone(GetOsmTag(tags, "phone")),
                Website: GetOsmTag(tags, "website"),
                EmailAddress: GetOsmTag(tags, "email"),
                WorshipStyle: 0,
                PrimaryLanguage: "English",
                AcceptsLGBTQ: null,
                WheelchairAccessible: null,
                HasNursery: null,
                HasYouthProgram: null,
                Confidence: 0.6m,
                Latitude: latitude,
                Longitude: longitude,
                DenominationName: OsmDenominationToName(GetOsmTag(tags, "denomination")))
            {
                Attributes = OsmAttributes(tags),
            };
        }
    }

    internal static IReadOnlyList<ChurchAttributeData> IrsAttributes(string? ntee)
    {
        var attributes = new List<ChurchAttributeData>();
        if (!string.IsNullOrWhiteSpace(ntee))
        {
            attributes.Add(new ChurchAttributeData("ntee_code", ntee, "irs", 0.5m));
        }

        return attributes;
    }

    internal static IReadOnlyList<ChurchAttributeData> OsmAttributes(JsonElement tags)
    {
        var attributes = new List<ChurchAttributeData>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                attributes.Add(new ChurchAttributeData(key, value, "osm", 0.6m));
            }
        }

        Add("denomination", GetOsmTag(tags, "denomination"));
        Add("website", GetOsmTag(tags, "website"));
        Add("phone", GetOsmTag(tags, "phone"));
        Add("email", GetOsmTag(tags, "email"));
        return attributes;
    }

    internal static int NteeToWorshipStyle(string? ntee)
    {
        if (string.IsNullOrWhiteSpace(ntee))
        {
            return 0;
        }

        return ntee.ToUpperInvariant() switch
        {
            "X21" => 5, // Catholic → Liturgical
            "X22" => 5, // Orthodox → Liturgical
            _ => 0,
        };
    }

    internal static string? NteeToDenomination(string? ntee)
    {
        if (string.IsNullOrWhiteSpace(ntee))
        {
            return null;
        }

        // NTEE only distinguishes Roman Catholic (X22) at a useful granularity; X21 "Protestant" is too
        // broad to be a denomination, so it (and everything else) is left unresolved.
        return string.Equals(ntee, "X22", StringComparison.OrdinalIgnoreCase) ? "Roman Catholic" : null;
    }

    internal static string? OsmDenominationToName(string? denomination)
    {
        if (string.IsNullOrWhiteSpace(denomination))
        {
            return null;
        }

        // OSM "denomination" values are lowercase slugs; map the common ones to canonical seed names.
        return denomination.Trim().ToLowerInvariant() switch
        {
            "roman_catholic" or "catholic" => "Roman Catholic",
            "orthodox" or "eastern_orthodox" => "Eastern Orthodox",
            "greek_orthodox" => "Greek Orthodox",
            "coptic_orthodox" => "Coptic Orthodox",
            "baptist" => "Baptist",
            "southern_baptist" => "Southern Baptist",
            "methodist" => "Methodist",
            "united_methodist" => "United Methodist",
            "lutheran" => "Lutheran",
            "presbyterian" => "Presbyterian",
            "anglican" => "Anglican",
            "episcopal" or "episcopalian" => "Episcopal",
            "pentecostal" => "Pentecostal",
            "assemblies_of_god" => "Assemblies of God",
            "nondenominational" or "non-denominational" => "Non-denominational",
            "evangelical" => "Evangelical",
            "reformed" => "Reformed",
            "congregational" => "Congregational",
            "adventist" => "Adventist",
            "seventh_day_adventist" => "Seventh-day Adventist",
            "mormon" or "latter_day_saints" => "Latter-day Saints",
            "jehovahs_witness" or "jehovahs_witnesses" => "Jehovah's Witnesses",
            "quaker" => "Quaker",
            "mennonite" => "Mennonite",
            "amish" => "Amish",
            "brethren" => "Brethren",
            "nazarene" => "Nazarene",
            "church_of_christ" => "Church of Christ",
            "disciples_of_christ" => "Disciples of Christ",
            "wesleyan" => "Wesleyan",
            "foursquare" => "Foursquare",
            "unitarian_universalist" or "unitarian" => "Unitarian Universalist",
            "salvation_army" => "Salvation Army",
            "apostolic" => "Apostolic",
            "holiness" => "Holiness",
            "charismatic" => "Charismatic",
            "messianic" or "messianic_jewish" => "Messianic Jewish",
            _ => null,
        };
    }

    private static string[] SplitCsvRow(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                fields.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        fields.Add(sb.ToString().Trim());
        return [.. fields];
    }

    private static int IndexOf(string[] columns, string name)
    {
        for (var i = 0; i < columns.Length; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? SafeGet(string[] fields, int index) =>
        index >= 0 && index < fields.Length && !string.IsNullOrWhiteSpace(fields[index])
            ? fields[index]
            : null;

    private static string? GetOsmTag(JsonElement tags, string key) =>
        tags.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? FirstPhone(string? phone)
    {
        // OSM "phone" tags can hold several numbers separated by ';' or ','; [dbo].[Churches].PhoneNumber
        // is NVARCHAR(20), so keep only the first and drop it entirely if it still would not fit.
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var parts = phone.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        return parts[0].Length <= 20 ? parts[0] : null;
    }

    private static string? CombineStreet(string? houseNumber, string? street)
    {
        if (string.IsNullOrWhiteSpace(street))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(houseNumber) ? street : $"{houseNumber} {street}";
    }

    private static (decimal? Latitude, decimal? Longitude) GetOsmCoordinates(JsonElement element)
    {
        // Nodes carry lat/lon directly; ways and relations expose them via "center" (Overpass "out center").
        if (TryGetLatLon(element, out var lat, out var lon))
        {
            return (lat, lon);
        }

        if (element.TryGetProperty("center", out var center) && TryGetLatLon(center, out var clat, out var clon))
        {
            return (clat, clon);
        }

        return (null, null);
    }

    private static bool TryGetLatLon(JsonElement element, out decimal latitude, out decimal longitude)
    {
        latitude = 0m;
        longitude = 0m;
        if (element.TryGetProperty("lat", out var lat) && lat.ValueKind == JsonValueKind.Number
            && element.TryGetProperty("lon", out var lon) && lon.ValueKind == JsonValueKind.Number)
        {
            latitude = (decimal)lat.GetDouble();
            longitude = (decimal)lon.GetDouble();
            return true;
        }

        return false;
    }

    private static string DedupKey(string? name, string? state) =>
        $"{name?.Trim()}|{state?.Trim()}";

    [LoggerMessage(Level = LogLevel.Information, Message = "BulkImportJob: published={Published} skipped={Skipped} source={Source}")]
    private static partial void LogImportResult(ILogger logger, int published, int skipped, string source);

    private async Task<HashSet<string>> LoadExistingKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_dbConnection.State == System.Data.ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = "SELECT [CanonicalName], [State] FROM [dbo].[Churches]";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(DedupKey(reader.GetString(0), reader.GetString(1)));
        }

        return keys;
    }
}
