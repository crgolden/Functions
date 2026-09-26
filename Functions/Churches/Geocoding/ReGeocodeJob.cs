namespace Functions.Churches.Geocoding;

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class ReGeocodeJob
{
    internal const string UpdatedResult = "updated";
    internal const string StillMissingResult = "still_missing";
    internal const string NotPersistedResult = "not_persisted";

    private readonly CensusGeocoder _geocoder;
    private readonly ChurchWriter _churchWriter;
    private readonly DbConnection _dbConnection;
    private readonly Telemetry _telemetry;

    public ReGeocodeJob(
        CensusGeocoder geocoder,
        ChurchWriter churchWriter,
        DbConnection dbConnection,
        Telemetry telemetry)
    {
        _geocoder = geocoder;
        _telemetry = telemetry;
        _churchWriter = churchWriter;
        _dbConnection = dbConnection;
    }

    [Function(nameof(ReGeocodeJob))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Admin, "post", Route = "re-geocode")] HttpRequestData req,
        CancellationToken cancellationToken = default)
    {
        var max = int.TryParse(req.Query["max"], out var parsed) && parsed > 0 ? parsed : 1000;
        var candidates = await LoadZeroCoordChurchesAsync(max, cancellationToken);
        var churches = await ReGeocodeAsync(candidates, _churchWriter.UpdateCoordinatesAsync, cancellationToken);
        _telemetry.ReGeocoded(churches.Updated, UpdatedResult);
        _telemetry.ReGeocoded(churches.StillMissing, StillMissingResult);
        _telemetry.ReGeocoded(churches.NotPersisted, NotPersistedResult);

        var campusCandidates = await LoadZeroCoordCampusesAsync(max, cancellationToken);
        var campuses = await ReGeocodeAsync(campusCandidates, _churchWriter.UpdateCampusCoordinatesAsync, cancellationToken);
        _telemetry.ReGeocodedCampuses(campuses.Updated, UpdatedResult);
        _telemetry.ReGeocodedCampuses(campuses.StillMissing, StillMissingResult);
        _telemetry.ReGeocodedCampuses(campuses.NotPersisted, NotPersistedResult);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        var body = JsonSerializer.Serialize(new
        {
            candidates = candidates.Count,
            updated = churches.Updated,
            stillMissing = churches.StillMissing,
            campusCandidates = campusCandidates.Count,
            campusesUpdated = campuses.Updated,
            campusesStillMissing = campuses.StillMissing,
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

    private async Task<ReGeocodeTally> ReGeocodeAsync(
        IReadOnlyList<ChurchLocation> candidates,
        Func<Guid, decimal, decimal, CancellationToken, Task<bool>> persistCoordinatesAsync,
        CancellationToken cancellationToken)
    {
        var tally = new ReGeocodeTally();
        foreach (var candidate in candidates)
        {
            var (lat, lng) = await _geocoder.GeocodeAsync(
                candidate.Street, candidate.City, candidate.State, candidate.Zip, cancellationToken);
            if (lat == 0m && lng == 0m)
            {
                tally = tally with { StillMissing = tally.StillMissing + 1 };
            }
            else if (await persistCoordinatesAsync(candidate.Id, lat, lng, cancellationToken))
            {
                tally = tally with { Updated = tally.Updated + 1 };
            }
            else
            {
                tally = tally with { NotPersisted = tally.NotPersisted + 1 };
            }
        }

        return tally;
    }
}
