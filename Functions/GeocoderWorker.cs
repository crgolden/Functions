namespace Functions;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

public sealed class GeocoderWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DbConnection _dbConnection;
    private readonly string _censusBaseUrl;

    public GeocoderWorker(IHttpClientFactory httpClientFactory, DbConnection dbConnection, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _dbConnection = dbConnection;
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
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var (lat, lng) = await GeocodeAsync(payload, cancellationToken);
        await UpsertChurchAsync(payload, lat, lng, cancellationToken);
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

    internal async Task<(decimal Lat, decimal Lng)> GeocodeAsync(GeocodingRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.City) && string.IsNullOrWhiteSpace(req.Street))
        {
            return (0m, 0m);
        }

        try
        {
            var query = BuildCensusQuery(req);
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync($"{_censusBaseUrl}?{query}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return (0m, 0m);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseCensusResponse(json);
        }
        catch
        {
            return (0m, 0m);
        }
    }

    internal async Task UpsertChurchAsync(GeocodingRequest req, decimal lat, decimal lng, CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        await using var lookupCmd = _dbConnection.CreateCommand();
        lookupCmd.CommandText = "SELECT [ChurchId] FROM [dbo].[CrawlSources] WHERE [Id] = @Id";
        AddParam(lookupCmd, "@Id", req.CrawlSourceId);
        var existingIdObj = await lookupCmd.ExecuteScalarAsync(ct);
        var isNew = existingIdObj is not Guid;
        var churchId = existingIdObj is Guid g ? g : Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        var slug = SlugHelper.ToSlug(req.CanonicalName ?? string.Empty)
                   + "-" + SlugHelper.ToSlug(req.City ?? string.Empty)
                   + "-" + (req.State ?? string.Empty).ToLowerInvariant().Trim();

        if (isNew)
        {
            await using var insertCmd = _dbConnection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO [dbo].[Churches]
                    ([Id], [CanonicalName], [Slug], [Latitude], [Longitude], [Street], [City], [State], [Zip],
                     [PhoneNumber], [Website], [EmailAddress], [WorshipStyle], [PrimaryLanguage],
                     [AcceptsLGBTQ], [WheelchairAccessible], [HasNursery], [HasYouthProgram],
                     [ConfidenceScore], [CreatedAt], [UpdatedAt], [IsActive])
                VALUES (@Id, @Name, @Slug, @Lat, @Lng, @Street, @City, @State, @Zip,
                        @Phone, @Website, @Email, @Ws, @Lang, @Lgbtq, @Wa, @Nursery, @Youth, @Score, @Now, @Now, 1)
                """;
            BindAll(insertCmd, churchId, req, lat, lng, slug, now);
            await insertCmd.ExecuteNonQueryAsync(ct);

            await using var linkCmd = _dbConnection.CreateCommand();
            linkCmd.CommandText = "UPDATE [dbo].[CrawlSources] SET [ChurchId] = @ChurchId WHERE [Id] = @Id";
            AddParam(linkCmd, "@ChurchId", churchId);
            AddParam(linkCmd, "@Id", req.CrawlSourceId);
            await linkCmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var updateCmd = _dbConnection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE [dbo].[Churches]
                SET [CanonicalName] = @Name, [Slug] = @Slug, [Latitude] = @Lat, [Longitude] = @Lng,
                    [Street] = @Street, [City] = @City, [State] = @State, [Zip] = @Zip,
                    [PhoneNumber] = @Phone, [Website] = @Website, [EmailAddress] = @Email,
                    [WorshipStyle] = @Ws, [PrimaryLanguage] = @Lang, [AcceptsLGBTQ] = @Lgbtq,
                    [WheelchairAccessible] = @Wa, [HasNursery] = @Nursery, [HasYouthProgram] = @Youth,
                    [ConfidenceScore] = @Score, [UpdatedAt] = @Now
                WHERE [Id] = @Id
                """;
            BindAll(updateCmd, churchId, req, lat, lng, slug, now);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string BuildCensusQuery(GeocodingRequest req)
    {
        var parts = new List<string> { "benchmark=Public_AR_Current", "format=json" };
        if (!string.IsNullOrWhiteSpace(req.Street))
        {
            parts.Add($"street={Uri.EscapeDataString(req.Street)}");
        }

        if (!string.IsNullOrWhiteSpace(req.City))
        {
            parts.Add($"city={Uri.EscapeDataString(req.City)}");
        }

        if (!string.IsNullOrWhiteSpace(req.State))
        {
            parts.Add($"state={Uri.EscapeDataString(req.State)}");
        }

        if (!string.IsNullOrWhiteSpace(req.Zip))
        {
            parts.Add($"zip={Uri.EscapeDataString(req.Zip)}");
        }

        return string.Join("&", parts);
    }

    private static void BindAll(DbCommand cmd, Guid id, GeocodingRequest req, decimal lat, decimal lng, string slug, DateTime now)
    {
        AddParam(cmd, "@Id", id);
        AddParam(cmd, "@Name", (object?)req.CanonicalName ?? DBNull.Value);
        AddParam(cmd, "@Slug", slug);
        AddParam(cmd, "@Lat", lat);
        AddParam(cmd, "@Lng", lng);
        AddParam(cmd, "@Street", (object?)req.Street ?? DBNull.Value);
        AddParam(cmd, "@City", (object?)req.City ?? DBNull.Value);
        AddParam(cmd, "@State", (object?)req.State ?? DBNull.Value);
        AddParam(cmd, "@Zip", (object?)req.Zip ?? DBNull.Value);
        AddParam(cmd, "@Phone", (object?)req.PhoneNumber ?? DBNull.Value);
        AddParam(cmd, "@Website", (object?)req.Website ?? DBNull.Value);
        AddParam(cmd, "@Email", (object?)req.EmailAddress ?? DBNull.Value);
        AddParam(cmd, "@Ws", req.WorshipStyle);
        AddParam(cmd, "@Lang", req.PrimaryLanguage);
        AddParam(cmd, "@Lgbtq", req.AcceptsLGBTQ.HasValue ? req.AcceptsLGBTQ.Value : DBNull.Value);
        AddParam(cmd, "@Wa", req.WheelchairAccessible.HasValue ? req.WheelchairAccessible.Value : DBNull.Value);
        AddParam(cmd, "@Nursery", req.HasNursery.HasValue ? req.HasNursery.Value : DBNull.Value);
        AddParam(cmd, "@Youth", req.HasYouthProgram.HasValue ? req.HasYouthProgram.Value : DBNull.Value);
        AddParam(cmd, "@Score", req.Confidence);
        AddParam(cmd, "@Now", now);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}

internal sealed record GeocodingRequest(
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
    decimal Confidence);
