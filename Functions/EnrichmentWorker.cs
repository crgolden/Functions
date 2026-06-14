#pragma warning disable OPENAI001
namespace Functions;

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;

public class EnrichmentWorker
{
    private readonly ResponsesClient _responsesClient;
    private readonly DbConnection _dbConnection;
    private readonly string _model;

    public EnrichmentWorker(
        ResponsesClient responsesClient,
        DbConnection dbConnection,
        IConfiguration configuration)
    {
        _responsesClient = responsesClient;
        _dbConnection = dbConnection;
        _model = configuration.GetRequired<string>("OpenAIModel");
    }

    [Function(nameof(EnrichmentWorker))]
    public async Task Run(
        [ServiceBusTrigger("enrichment-requests", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var payload = message.Body.ToObjectFromJson<EnrichmentRequest>();
        if (payload is null)
        {
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var partialJson = JsonSerializer.Serialize(payload.Partial);
        var prompt = $"""
            Extract structured church information from the partial data below.
            Return ONLY valid JSON with fields: canonicalName, city, state, zip,
            worshipStyle (0=Unknown 1=Traditional 2=Contemporary 3=Blended 4=Charismatic 5=Liturgical),
            primaryLanguage, acceptsLGBTQ (true/false/null), wheelchairAccessible (true/false/null),
            hasNursery (true/false/null), hasYouthProgram (true/false/null).
            Source URL: {payload.Url}
            Partial data: {partialJson}
            """;

        try
        {
            var response = await _responsesClient.CreateResponseAsync(
                _model,
                [ResponseItem.CreateUserMessageItem(prompt)],
                cancellationToken: cancellationToken);
            var outputText = response?.Value?.GetOutputText()
                ?? throw new InvalidOperationException("OpenAI returned no output.");
            var enriched = TryParseEnrichment(outputText, payload.Partial);
            await UpsertChurchAsync(payload.CrawlSourceId, enriched, cancellationToken);
        }
        catch (Exception ex)
        {
            Activity.Current?.AddException(ex);
        }

        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    internal static EnrichedData TryParseEnrichment(string json, EnrichmentPartialData partial)
    {
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var endInclusive = end + 1;
                json = json[start..endInclusive];
            }

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            static string? GetStr(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

            static bool? GetBool(JsonElement el, string key)
            {
                if (!el.TryGetProperty(key, out var v))
                {
                    return null;
                }

                if (v.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (v.ValueKind == JsonValueKind.False)
                {
                    return false;
                }

                return null;
            }

            static int GetInt(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : 0;

            return new EnrichedData(
                GetStr(root, "canonicalName") ?? partial.CanonicalName,
                GetStr(root, "city") ?? partial.City,
                GetStr(root, "state") ?? partial.State,
                GetStr(root, "zip") ?? partial.Zip,
                GetInt(root, "worshipStyle"),
                GetStr(root, "primaryLanguage") ?? "English",
                GetBool(root, "acceptsLGBTQ"),
                GetBool(root, "wheelchairAccessible"),
                GetBool(root, "hasNursery"),
                GetBool(root, "hasYouthProgram"));
        }
        catch
        {
            return new EnrichedData(partial.CanonicalName, partial.City, partial.State, partial.Zip, 0, "English", null, null, null, null);
        }
    }

    internal async Task UpsertChurchAsync(Guid crawlSourceId, EnrichedData data, CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        await using var lookupCmd = _dbConnection.CreateCommand();
        lookupCmd.CommandText = "SELECT [ChurchId] FROM [dbo].[CrawlSources] WHERE [Id] = @Id";
        AddParam(lookupCmd, "@Id", crawlSourceId);
        var existingIdObj = await lookupCmd.ExecuteScalarAsync(ct);
        var isNew = existingIdObj is not Guid;
        var churchId = existingIdObj is Guid g ? g : Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow.UtcDateTime;

        if (isNew)
        {
            await using var insertCmd = _dbConnection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO [dbo].[Churches]
                    ([Id], [CanonicalName], [Slug], [Latitude], [Longitude], [City], [State], [Zip],
                     [WorshipStyle], [PrimaryLanguage], [AcceptsLGBTQ], [WheelchairAccessible],
                     [HasNursery], [HasYouthProgram], [ConfidenceScore], [CreatedAt], [UpdatedAt], [IsActive])
                VALUES (@Id, @Name, @Slug, 0, 0, @City, @State, @Zip,
                        @Ws, @Lang, @Lgbtq, @Wa, @Nursery, @Youth, 0.6, @Now, @Now, 1)
                """;
            BindEnriched(insertCmd, churchId, data, now);
            await insertCmd.ExecuteNonQueryAsync(ct);

            await using var linkCmd = _dbConnection.CreateCommand();
            linkCmd.CommandText = "UPDATE [dbo].[CrawlSources] SET [ChurchId] = @ChurchId WHERE [Id] = @Id";
            AddParam(linkCmd, "@ChurchId", churchId);
            AddParam(linkCmd, "@Id", crawlSourceId);
            await linkCmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var updateCmd = _dbConnection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE [dbo].[Churches]
                SET [CanonicalName] = @Name, [City] = @City, [State] = @State, [Zip] = @Zip,
                    [WorshipStyle] = @Ws, [PrimaryLanguage] = @Lang, [AcceptsLGBTQ] = @Lgbtq,
                    [WheelchairAccessible] = @Wa, [HasNursery] = @Nursery, [HasYouthProgram] = @Youth,
                    [ConfidenceScore] = 0.6, [UpdatedAt] = @Now
                WHERE [Id] = @Id
                """;
            BindEnriched(updateCmd, churchId, data, now);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void BindEnriched(DbCommand cmd, Guid id, EnrichedData d, DateTime now)
    {
        var slug = SlugHelper.ToSlug(d.CanonicalName ?? string.Empty)
                   + "-" + SlugHelper.ToSlug(d.City ?? string.Empty)
                   + "-" + (d.State ?? string.Empty).ToLowerInvariant().Trim();
        AddParam(cmd, "@Id", id);
        AddParam(cmd, "@Name", (object?)d.CanonicalName ?? DBNull.Value);
        AddParam(cmd, "@Slug", slug);
        AddParam(cmd, "@City", (object?)d.City ?? DBNull.Value);
        AddParam(cmd, "@State", (object?)d.State ?? DBNull.Value);
        AddParam(cmd, "@Zip", (object?)d.Zip ?? DBNull.Value);
        AddParam(cmd, "@Ws", d.WorshipStyle);
        AddParam(cmd, "@Lang", d.PrimaryLanguage);
        AddParam(cmd, "@Lgbtq", d.AcceptsLGBTQ.HasValue ? d.AcceptsLGBTQ.Value : DBNull.Value);
        AddParam(cmd, "@Wa", d.WheelchairAccessible.HasValue ? d.WheelchairAccessible.Value : DBNull.Value);
        AddParam(cmd, "@Nursery", d.HasNursery.HasValue ? d.HasNursery.Value : DBNull.Value);
        AddParam(cmd, "@Youth", d.HasYouthProgram.HasValue ? d.HasYouthProgram.Value : DBNull.Value);
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

internal sealed record EnrichmentRequest(Guid CrawlSourceId, string Url, EnrichmentPartialData Partial);

internal sealed record EnrichmentPartialData(
    string? CanonicalName,
    string? City,
    string? State,
    string? Zip);

internal sealed record EnrichedData(
    string? CanonicalName,
    string? City,
    string? State,
    string? Zip,
    int WorshipStyle,
    string PrimaryLanguage,
    bool? AcceptsLGBTQ,
    bool? WheelchairAccessible,
    bool? HasNursery,
    bool? HasYouthProgram);
#pragma warning restore OPENAI001
