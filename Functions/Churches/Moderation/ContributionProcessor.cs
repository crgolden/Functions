namespace Functions.Churches.Moderation;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Extensions;
using Microsoft.Azure.Functions.Worker;

public class ContributionProcessor
{
    internal const string OldValueParameter = "@OldValue";

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
        var payload = Read(message);
        if (payload is null)
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: DeadLetterReasons.MalformedPayload, cancellationToken: cancellationToken);
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
        cmd.AddParam("@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
        cmd.AddParam("@ChurchId", payload.ChurchId);
        cmd.AddParam("@UserId", payload.UserId);
        cmd.AddParam("@Field", payload.Field);
        cmd.AddParam(OldValueParameter, (object?)payload.OldValue ?? DBNull.Value);
        cmd.AddParam("@NewValue", payload.NewValue);
        cmd.AddParam("@CreatedAt", DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    private static ContributionPayload? Read(ServiceBusReceivedMessage message)
    {
        try
        {
            return message.Body.ToObjectFromJson<ContributionPayload>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record ContributionPayload(
    Guid ChurchId,
    Guid UserId,
    string Field,
    string? OldValue,
    string NewValue);