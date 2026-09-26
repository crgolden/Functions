namespace Functions.Churches.Confidence;

using System.Data;
using System.Data.Common;
using Azure.Messaging.ServiceBus;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker;

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
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: DeadLetterReasons.MalformedPayload, cancellationToken: cancellationToken);
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

        if (await LoadScoringInputsAsync(churchId, ct) is not { } scoring)
        {
            return;
        }

        var score = ConfidenceScoreCalculator.Calculate(scoring.Inputs, scoring.AttributeCount);

        await using var updateCmd = _dbConnection.CreateCommand();
        updateCmd.CommandText = "UPDATE [dbo].[Churches] SET [ConfidenceScore] = @Score, [UpdatedAt] = @Now WHERE [Id] = @Id";
        updateCmd.AddParam(ChurchSqlParameters.Score, score);
        updateCmd.AddParam(ChurchSqlParameters.Now, DateTimeOffset.UtcNow);
        updateCmd.AddParam(ChurchSqlParameters.Id, churchId);
        await updateCmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(ConfidenceInputs Inputs, int AttributeCount)?> LoadScoringInputsAsync(Guid churchId, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT [CanonicalName], [City], [State], [Zip], [Latitude], [Longitude],
                   [PhoneNumber], [Website], [EmailAddress], [DenominationId], [WorshipStyle], [LastVerifiedAt],
                   (SELECT COUNT(1) FROM [dbo].[ChurchAttributes] WHERE [ChurchId] = @Id) AS [AttributeCount]
            FROM [dbo].[Churches] WHERE [Id] = @Id
            """;
        cmd.AddParam(ChurchSqlParameters.Id, churchId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var inputs = new ConfidenceInputs(
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
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11));
        return (inputs, reader[12] is int attributeCount ? attributeCount : 0);
    }
}
