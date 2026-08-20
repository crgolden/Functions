namespace Functions.Curator.Library;

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;

public sealed class LibraryRefreshQueuePublisher
{
    public const string Queue = "curator-library-refresh";

    public const string ContinuationQueue = "curator-library-refresh-continuation";

    private readonly ServiceBusSender _continuationSender;
    private readonly TimeProvider _timeProvider;

    public LibraryRefreshQueuePublisher(
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory,
        TimeProvider? timeProvider = null)
    {
        _continuationSender = serviceBusClientFactory.CreateClient("crgolden").CreateSender(ContinuationQueue);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task PublishContinuationAsync(
        string runId,
        string identitySub,
        IReadOnlyList<string> remainingGameIds,
        string? provider,
        double retryAfterSeconds,
        int seq,
        CancellationToken cancellationToken = default)
    {
        var body = BinaryData.FromObjectAsJson(new LibraryRefreshContinuationMessage
        {
            RunId = runId,
            IdentitySub = identitySub,
            RemainingGameIds = remainingGameIds,
            Provider = provider,
            RetryAfterSeconds = retryAfterSeconds,
            Seq = seq,
        });
        await _continuationSender.ScheduleMessageAsync(
            new ServiceBusMessage(body),
            _timeProvider.GetUtcNow().AddSeconds(retryAfterSeconds),
            cancellationToken).ConfigureAwait(false);
    }
}
