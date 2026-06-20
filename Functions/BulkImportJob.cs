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

public sealed class BulkImportJob
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

        await using var sender = _serviceBusClient.CreateSender("geocoding-requests");
        var published = 0;
        var skipped = 0;

        foreach (var record in records)
        {
            if (await ExistsInDbAsync(record.CanonicalName, record.State, cancellationToken))
            {
                skipped++;
                continue;
            }

            await sender.SendMessageAsync(
                new ServiceBusMessage(JsonSerializer.Serialize(record)),
                cancellationToken);
            published++;
        }

        _logger.LogInformation("BulkImportJob: published={Published} skipped={Skipped} source={Source}", published, skipped, source);
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
                Confidence: 0.5m);
        }
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
            var state = GetOsmTag(tags, "addr:state");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(state))
            {
                continue;
            }

            yield return new GeocodingRequest(
                CrawlSourceId: Guid.CreateVersion7(DateTimeOffset.UtcNow),
                CanonicalName: name,
                Street: GetOsmTag(tags, "addr:street"),
                City: GetOsmTag(tags, "addr:city"),
                State: state,
                Zip: GetOsmTag(tags, "addr:postcode"),
                PhoneNumber: GetOsmTag(tags, "phone"),
                Website: GetOsmTag(tags, "website"),
                EmailAddress: GetOsmTag(tags, "email"),
                WorshipStyle: 0,
                PrimaryLanguage: "English",
                AcceptsLGBTQ: null,
                WheelchairAccessible: null,
                HasNursery: null,
                HasYouthProgram: null,
                Confidence: 0.6m);
        }
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

    private async Task<bool> ExistsInDbAsync(string? name, string? state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        if (_dbConnection.State == System.Data.ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM [dbo].[Churches] WHERE [CanonicalName] = @Name AND [State] = @State";
        var p1 = cmd.CreateParameter();
        p1.ParameterName = "@Name";
        p1.Value = name;
        cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter();
        p2.ParameterName = "@State";
        p2.Value = state;
        cmd.Parameters.Add(p2);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }
}
