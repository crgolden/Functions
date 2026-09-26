namespace Functions.Churches.Geocoding;

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

public sealed class ReGeocodeJob
{
    internal const string UpdatedResult = "updated";
    internal const string StillMissingResult = "still_missing";
    internal const string NotPersistedResult = "not_persisted";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChurchWriter _churchWriter;
    private readonly DbConnection _dbConnection;
    private readonly string _censusBaseUrl;
    private readonly Telemetry _telemetry;

    public ReGeocodeJob(
        IHttpClientFactory httpClientFactory,
        ChurchWriter churchWriter,
        DbConnection dbConnection,
        IConfiguration configuration,
        Telemetry telemetry)
    {
        _httpClientFactory = httpClientFactory;
        _telemetry = telemetry;
        _churchWriter = churchWriter;
        _dbConnection = dbConnection;
        _censusBaseUrl = configuration.GetRequired<string>("CensusGeocoderUrl");
    }

    [Function(nameof(ReGeocodeJob))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Admin, "post", Route = "re-geocode")] HttpRequestData req,
        CancellationToken cancellationToken = default)
    {
        var max = int.TryParse(req.Query["max"], out var parsed) && parsed > 0 ? parsed : 1000;
        var candidates = await LoadZeroCoordChurchesAsync(max, cancellationToken);

        var updated = 0;
        var stillMissing = 0;
        var notPersisted = 0;
        foreach (var church in candidates)
        {
            var (lat, lng) = await GeocoderWorker.GeocodeAddressCoreAsync(
                _telemetry,
                _httpClientFactory,
                _censusBaseUrl,
                church.Street,
                church.City,
                church.State,
                church.Zip,
                cancellationToken);
            if (lat == 0m && lng == 0m)
            {
                stillMissing++;
                continue;
            }

            if (await _churchWriter.UpdateCoordinatesAsync(church.Id, lat, lng, cancellationToken))
            {
                updated++;
            }
            else
            {
                notPersisted++;
            }
        }

        _telemetry.ReGeocoded(updated, UpdatedResult);
        _telemetry.ReGeocoded(stillMissing, StillMissingResult);
        _telemetry.ReGeocoded(notPersisted, NotPersistedResult);

        var campusCandidates = await LoadZeroCoordCampusesAsync(max, cancellationToken);
        var campusesUpdated = 0;
        var campusesStillMissing = 0;
        var campusesNotPersisted = 0;
        foreach (var campus in campusCandidates)
        {
            var (lat, lng) = await GeocoderWorker.GeocodeAddressCoreAsync(
                _telemetry,
                _httpClientFactory,
                _censusBaseUrl,
                campus.Street,
                campus.City,
                campus.State,
                campus.Zip,
                cancellationToken);
            if (lat == 0m && lng == 0m)
            {
                campusesStillMissing++;
                continue;
            }

            if (await _churchWriter.UpdateCampusCoordinatesAsync(campus.Id, lat, lng, cancellationToken))
            {
                campusesUpdated++;
            }
            else
            {
                campusesNotPersisted++;
            }
        }

        _telemetry.ReGeocodedCampuses(campusesUpdated, UpdatedResult);
        _telemetry.ReGeocodedCampuses(campusesStillMissing, StillMissingResult);
        _telemetry.ReGeocodedCampuses(campusesNotPersisted, NotPersistedResult);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        var body = JsonSerializer.Serialize(new
        {
            candidates = candidates.Count,
            updated,
            stillMissing,
            campusCandidates = campusCandidates.Count,
            campusesUpdated,
            campusesStillMissing,
        });
        await ok.WriteStringAsync(body, cancellationToken);
        return ok;
    }

    internal async Task<IReadOnlyList<ChurchLocation>> LoadZeroCoordChurchesAsync(int max, CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@Max) [Id], [Street], [City], [State], [Zip]
            FROM [dbo].[Churches]
            WHERE [Latitude] = 0 AND [Longitude] = 0 AND [IsActive] = 1
              AND [Street] NOT LIKE 'PO BOX%' AND [Street] NOT LIKE 'P O BOX%'
              AND [Street] NOT LIKE 'P.O. BOX%' AND [Street] NOT LIKE 'P.O BOX%'
            ORDER BY NEWID()
            """;
        var p = cmd.CreateParameter();
        p.ParameterName = ChurchSqlParameters.Max;
        p.Value = max;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ChurchLocation>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ChurchLocation(
                (Guid)reader[0],
                reader[1] is DBNull ? null : (string)reader[1],
                reader[2] is DBNull ? null : (string)reader[2],
                reader[3] is DBNull ? null : (string)reader[3],
                reader[4] is DBNull ? null : (string)reader[4]));
        }

        return list;
    }

    internal async Task<IReadOnlyList<ChurchLocation>> LoadZeroCoordCampusesAsync(int max, CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@Max) cm.[Id], cm.[Street], cm.[City], cm.[State], cm.[Zip]
            FROM [dbo].[Campuses] cm
            INNER JOIN [dbo].[Churches] ch ON ch.[Id] = cm.[ChurchId]
            WHERE cm.[Latitude] = 0 AND cm.[Longitude] = 0 AND ch.[IsActive] = 1
              AND cm.[Street] NOT LIKE 'PO BOX%' AND cm.[Street] NOT LIKE 'P O BOX%'
              AND cm.[Street] NOT LIKE 'P.O. BOX%' AND cm.[Street] NOT LIKE 'P.O BOX%'
            ORDER BY NEWID()
            """;
        var p = cmd.CreateParameter();
        p.ParameterName = ChurchSqlParameters.Max;
        p.Value = max;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ChurchLocation>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ChurchLocation(
                (Guid)reader[0],
                reader[1] is DBNull ? null : (string)reader[1],
                reader[2] is DBNull ? null : (string)reader[2],
                reader[3] is DBNull ? null : (string)reader[3],
                reader[4] is DBNull ? null : (string)reader[4]));
        }

        return list;
    }
}
