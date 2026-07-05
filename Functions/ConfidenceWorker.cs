namespace Functions;

using System.Data;
using System.Data.Common;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;

// Recomputes a church's confidence score from its committed row + attribute count. Triggered by the
// writer after every ingestion write, and can be triggered by the Directory app at any later time
// (merge, edit, verification, periodic sweep) simply by publishing a ConfidenceRequest.
public sealed class ConfidenceWorker
{
    private readonly DbConnection _dbConnection;

    public ConfidenceWorker(DbConnection dbConnection) => _dbConnection = dbConnection;

    [Function("CalculateConfidenceScore")]
    public async Task Run(
        [ServiceBusTrigger("confidence-requests", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var payload = message.Body.ToObjectFromJson<ConfidenceRequest>();
        if (payload is null || payload.ChurchId == Guid.Empty)
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "malformed-payload", cancellationToken: cancellationToken);
            return;
        }

        await RecalculateAsync(payload.ChurchId, cancellationToken);
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    internal async Task RecalculateAsync(Guid churchId, CancellationToken ct)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(ct);
        }

        var inputs = await LoadInputsAsync(churchId, ct);
        if (inputs is null)
        {
            // Church not found (e.g. deleted between publish and processing) — nothing to score.
            return;
        }

        var attributeCount = await CountAttributesAsync(churchId, ct);
        var score = ConfidenceScoreCalculator.Calculate(inputs, attributeCount);

        await using var updateCmd = _dbConnection.CreateCommand();
        updateCmd.CommandText = "UPDATE [dbo].[Churches] SET [ConfidenceScore] = @Score, [UpdatedAt] = @Now WHERE [Id] = @Id";
        AddParam(updateCmd, "@Score", score);
        AddParam(updateCmd, "@Now", DateTimeOffset.UtcNow.UtcDateTime);
        AddParam(updateCmd, "@Id", churchId);
        await updateCmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private async Task<ConfidenceInputs?> LoadInputsAsync(Guid churchId, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT [CanonicalName], [City], [State], [Zip], [Latitude], [Longitude],
                   [PhoneNumber], [Website], [EmailAddress], [DenominationId], [WorshipStyle], [LastVerifiedAt]
            FROM [dbo].[Churches] WHERE [Id] = @Id
            """;
        AddParam(cmd, "@Id", churchId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ConfidenceInputs(
            reader[0] as string,
            reader[1] as string,
            reader[2] as string,
            reader[3] as string,
            reader[4] is double lat ? lat : 0,
            reader[5] is double lng ? lng : 0,
            reader[6] as string,
            reader[7] as string,
            reader[8] as string,
            reader[9] is Guid,
            reader[10] is int ws ? ws : 0,
            reader[11] is DateTime lv ? new DateTimeOffset(DateTime.SpecifyKind(lv, DateTimeKind.Utc)) : null);
    }

    private async Task<int> CountAttributesAsync(Guid churchId, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM [dbo].[ChurchAttributes] WHERE [ChurchId] = @Id";
        AddParam(cmd, "@Id", churchId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int n ? n : 0;
    }
}

public sealed record ConfidenceRequest(Guid ChurchId);
