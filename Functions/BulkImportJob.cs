namespace Functions;

using System.Data;
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
    internal const int MaxPhoneLength = 20;

    internal const char MultiValueSeparator = ';';

    internal const string SourceQueryParameter = "source";

    internal const string BlobPathQueryParameter = "blobPath";

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
        _blobServiceClient = blobServiceClientFactory.CreateClient(AzureClientNames.Crgolden);
        _serviceBusClient = serviceBusClientFactory.CreateClient(AzureClientNames.Crgolden);
        _dbConnection = dbConnection;
        _logger = logger;
    }

    [Function(nameof(BulkImportJob))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Admin, "post", Route = "bulk-import")] HttpRequestData req,
        CancellationToken cancellationToken = default)
    {
        var source = req.Query[SourceQueryParameter] ?? ChurchImportSources.Irs;
        var blobPath = req.Query[BlobPathQueryParameter];
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync($"{BlobPathQueryParameter} query parameter is required.", cancellationToken);
            return bad;
        }

        var container = _blobServiceClient.GetBlobContainerClient(BlobContainerNames.Imports);
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

        var records = string.Equals(source, ChurchImportSources.Osm, StringComparison.Ordinal)
            ? ParseOsm(content)
            : ParseIrsCsv(content);

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

        await using var sender = _serviceBusClient.CreateSender(ChurchQueueNames.GeocodingRequests);
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
        using var reader = new StringReader(csv);
        var header = reader.ReadLine();
        if (header is null)
        {
            yield break;
        }

        var columns = SplitCsvRow(header);
        var nameIdx = IndexOf(columns, IrsCsvColumns.Name);
        var streetIdx = IndexOf(columns, IrsCsvColumns.Street);
        var cityIdx = IndexOf(columns, IrsCsvColumns.City);
        var stateIdx = IndexOf(columns, IrsCsvColumns.State);
        var zipIdx = IndexOf(columns, IrsCsvColumns.Zip);
        var nteeIdx = IndexOf(columns, IrsCsvColumns.NteeCode);

        var latIdx = IndexOf(columns, IrsCsvColumns.Latitude);
        var lngIdx = IndexOf(columns, IrsCsvColumns.Longitude);

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
                Confidence: ChurchImportConfidence.Irs,
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
        if (!doc.RootElement.TryGetProperty(OsmTags.Elements, out var elements))
        {
            yield break;
        }

        foreach (var element in elements.EnumerateArray())
        {
            if (!element.TryGetProperty(OsmTags.Tags, out var tags))
            {
                continue;
            }

            var name = PreferLatinName(GetOsmTag(tags, OsmTags.Name));
            var state = Normalizer.NormalizeState(GetOsmTag(tags, OsmTags.State));
            var city = GetOsmTag(tags, OsmTags.City);
            var zip = GetOsmTag(tags, OsmTags.Postcode);

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(state)
                || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(zip))
            {
                continue;
            }

            var (latitude, longitude) = GetOsmCoordinates(element);

            yield return new GeocodingRequest(
                CrawlSourceId: Guid.CreateVersion7(DateTimeOffset.UtcNow),
                CanonicalName: name,
                Street: CombineStreet(GetOsmTag(tags, OsmTags.HouseNumber), GetOsmTag(tags, OsmTags.Street)),
                City: city,
                State: state,
                Zip: zip,
                PhoneNumber: FirstPhone(GetOsmTag(tags, OsmTags.Phone)),
                Website: GetOsmTag(tags, OsmTags.Website),
                EmailAddress: GetOsmTag(tags, OsmTags.Email),
                WorshipStyle: ChurchWorshipStyles.Unknown,
                PrimaryLanguage: "English",
                AcceptsLGBTQ: null,
                WheelchairAccessible: null,
                HasNursery: null,
                HasYouthProgram: null,
                Confidence: ChurchImportConfidence.Osm,
                Latitude: latitude,
                Longitude: longitude,
                DenominationName: OsmDenominationToName(GetOsmTag(tags, OsmTags.Denomination)))
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
            attributes.Add(new ChurchAttributeData(ChurchAttributeKeys.NteeCode, ntee, ChurchImportSources.Irs, ChurchImportConfidence.Irs));
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
                attributes.Add(new ChurchAttributeData(key, value, ChurchImportSources.Osm, ChurchImportConfidence.Osm));
            }
        }

        Add(ChurchAttributeKeys.Denomination, GetOsmTag(tags, OsmTags.Denomination));
        Add(ChurchAttributeKeys.Website, GetOsmTag(tags, OsmTags.Website));
        Add(ChurchAttributeKeys.Phone, GetOsmTag(tags, OsmTags.Phone));
        Add(ChurchAttributeKeys.Email, GetOsmTag(tags, OsmTags.Email));
        return attributes;
    }

    internal static int NteeToWorshipStyle(string? ntee)
    {
        if (string.IsNullOrWhiteSpace(ntee))
        {
            return ChurchWorshipStyles.Unknown;
        }

        return ntee.ToUpperInvariant() switch
        {
            NteeCodes.Protestant => ChurchWorshipStyles.Liturgical,
            NteeCodes.RomanCatholic => ChurchWorshipStyles.Liturgical,
            _ => ChurchWorshipStyles.Unknown,
        };
    }

    internal static string? NteeToDenomination(string? ntee)
    {
        if (string.IsNullOrWhiteSpace(ntee))
        {
            return null;
        }

        return string.Equals(ntee, NteeCodes.RomanCatholic, StringComparison.OrdinalIgnoreCase)
            ? ChurchDenominations.RomanCatholic
            : null;
    }

    internal static string? OsmDenominationToName(string? denomination)
    {
        if (string.IsNullOrWhiteSpace(denomination))
        {
            return null;
        }

        return denomination.Trim().ToLowerInvariant() switch
        {
            "roman_catholic" or "catholic" => ChurchDenominations.RomanCatholic,
            "orthodox" or "eastern_orthodox" => "Eastern Orthodox",
            "greek_orthodox" => "Greek Orthodox",
            "coptic_orthodox" => "Coptic Orthodox",
            "baptist" => ChurchDenominations.Baptist,
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

    private static string? GetOsmTag(JsonElement tags, string key) => Normalizer.GetJsonString(tags, key);

    private static string? FirstPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var parts = phone.Split([MultiValueSeparator, ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        return parts[0].Length <= MaxPhoneLength ? parts[0] : null;
    }

    private static string? PreferLatinName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var segments = name.Split(MultiValueSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0 ? null : segments.OrderBy(s => s.Count(c => c > 127)).First();
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
        if (TryGetLatLon(element, out var lat, out var lon))
        {
            return (lat, lon);
        }

        if (element.TryGetProperty(OsmTags.Center, out var center) && TryGetLatLon(center, out var clat, out var clon))
        {
            return (clat, clon);
        }

        return (null, null);
    }

    private static bool TryGetLatLon(JsonElement element, out decimal latitude, out decimal longitude)
    {
        latitude = 0m;
        longitude = 0m;
        if (element.TryGetProperty(OsmTags.Latitude, out var lat) && lat.ValueKind == JsonValueKind.Number
            && element.TryGetProperty(OsmTags.Longitude, out var lon) && lon.ValueKind == JsonValueKind.Number)
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
        if (_dbConnection.State == ConnectionState.Closed)
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