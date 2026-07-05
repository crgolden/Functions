namespace Functions;

using System.Data;
using System.Data.Common;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;

public class ContributionProcessor
{
    private readonly DbConnection _dbConnection;

    public ContributionProcessor(DbConnection dbConnection)
    {
        _dbConnection = dbConnection;
    }

    [Function(nameof(ContributionProcessor))]
    public async Task Run(
        [ServiceBusTrigger("contributions", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var payload = message.Body.ToObjectFromJson<ContributionPayload>();
        if (payload is null)
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "malformed-payload", cancellationToken: cancellationToken);
            return;
        }

        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(cancellationToken);
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO [dbo].[UserCorrections]
                ([Id], [ChurchId], [UserId], [Field], [OldValue], [NewValue], [Status], [CreatedAt])
            VALUES (@Id, @ChurchId, @UserId, @Field, @OldValue, @NewValue, 0, @CreatedAt)
            """;
        AddParam(cmd, "@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
        AddParam(cmd, "@ChurchId", payload.ChurchId);
        AddParam(cmd, "@UserId", payload.UserId);
        AddParam(cmd, "@Field", payload.Field);
        AddParam(cmd, "@OldValue", (object?)payload.OldValue ?? DBNull.Value);
        AddParam(cmd, "@NewValue", payload.NewValue);
        AddParam(cmd, "@CreatedAt", DateTimeOffset.UtcNow.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}

internal sealed record ContributionPayload(
    Guid ChurchId,
    string UserId,
    string Field,
    string? OldValue,
    string NewValue);
