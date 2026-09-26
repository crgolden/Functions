namespace Functions.Churches.Moderation;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker;

public class ContributionProcessor
{
    internal const string OldValueParameter = ChurchSqlParameters.OldValue;

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
        cmd.AddParam(ChurchSqlParameters.Id, Guid.CreateVersion7(DateTimeOffset.UtcNow));
        cmd.AddParam(ChurchSqlParameters.ChurchId, payload.ChurchId);
        cmd.AddParam(ChurchSqlParameters.UserId, payload.UserId);
        cmd.AddParam(ChurchSqlParameters.Field, payload.Field);
        cmd.AddParam(OldValueParameter, (object?)payload.OldValue ?? DBNull.Value);
        cmd.AddParam(ChurchSqlParameters.NewValue, payload.NewValue);
        cmd.AddParam(ChurchSqlParameters.CreatedAt, DateTimeOffset.UtcNow);
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
