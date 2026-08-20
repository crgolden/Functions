namespace Functions.Curator.Jobs;

using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Enrichment;
using Microsoft.Azure.Functions.Worker;
using OpenCritic;
using Psn;
using Rawg;

public sealed class LeasedJobRunner
{
    public static readonly TimeSpan LeaseHeartbeatInterval = TimeSpan.FromSeconds(30);

    internal const string MalformedPayload = "malformed-payload";
    internal const string ProcessingFailed = "processing-failed";

    internal const string GenericMessage =
        "The job failed unexpectedly. If this keeps happening, contact support.";

    private const int RateLimitStatusCode = 429;

    private const string StaleRedeliveryEvent = "curator.job.stale-redelivery";
    private const string StaleRedeliveryDeadLettered = "dead-lettered";
    private const string StaleRedeliverySettled = "settled";

    private const string RateLimitMessage = "Enrichment provider rate limit reached. Try again later.";

    private const string PsnLinkExpiredMessage =
        "Your PlayStation Network link has expired or was rejected. Re-link your account and try again.";

    private const string TimeBudgetPausedMessage =
        "Paused to stay inside the job time budget. The rest of the refresh is already queued.";

    private readonly JobRunsRepository _jobRuns;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeProvider _timeProvider;

    public LeasedJobRunner(
        JobRunsRepository jobRuns,
        TimeSpan? heartbeatInterval = null,
        TimeProvider? timeProvider = null)
    {
        _jobRuns = jobRuns;
        _heartbeatInterval = heartbeatInterval ?? LeaseHeartbeatInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static JobFailure ClassifyJobError(Exception exception) => exception switch
    {
        ContinuationScheduledException { Provider: { } provider } => new JobFailure(
            JobErrorCodes.ProviderRateLimited, $"Paused until {provider} accepts requests again."),
        ContinuationScheduledException => new JobFailure(
            JobErrorCodes.ProviderRateLimited, TimeBudgetPausedMessage),
        EnrichmentAuthException auth => new JobFailure(
            JobErrorCodes.ProviderKeyRejected, ProviderKeyRejectedMessage(auth.Provider)),
        RawgApiException { StatusCode: RateLimitStatusCode } => new JobFailure(
            JobErrorCodes.ProviderRateLimited, RateLimitMessage),
        OpenCriticApiException { StatusCode: RateLimitStatusCode } => new JobFailure(
            JobErrorCodes.ProviderRateLimited, RateLimitMessage),
        PsnAuthException => new JobFailure(JobErrorCodes.PsnLinkExpired, PsnLinkExpiredMessage),
        _ => new JobFailure(JobErrorCodes.Unexpected, GenericMessage),
    };

    public static string FriendlyJobError(Exception exception) => ClassifyJobError(exception).Message;

    public async Task RunAsync<TMessage>(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        Func<TMessage, CancellationToken, Task<object?>> work,
        CancellationToken cancellationToken = default)
        where TMessage : ICuratorJobMessage
    {
        TMessage payload;
        try
        {
            payload = message.Body.ToObjectFromJson<TMessage>()
                ?? throw new JsonException("body deserialized to null");
        }
        catch (JsonException exc)
        {
            await SettleAsync(actions, message, deadLetter: true, MalformedPayload, exc.Message, cancellationToken);
            return;
        }

        var runId = payload.RunId;
        var expectedSeq = payload.Seq;

        if (!await _jobRuns.TryBeginDeliveryAsync(runId, expectedSeq, cancellationToken: cancellationToken))
        {
            var current = await _jobRuns.GetAsync(runId, cancellationToken);
            if (current is { Status: JobRunStatuses.Failed })
            {
                Telemetry.Metrics.StaleRedelivery(StaleRedeliveryDeadLettered);
                Telemetry.Tracing.RecordEvent(StaleRedeliveryEvent, new ActivityTagsCollection
                {
                    { "run.id", runId },
                    { "disposition", StaleRedeliveryDeadLettered },
                });
                await SettleAsync(
                    actions, message, deadLetter: true, ProcessingFailed, current.Error ?? "run already failed", cancellationToken);
                return;
            }

            Telemetry.Metrics.StaleRedelivery(StaleRedeliverySettled);
            Telemetry.Tracing.RecordEvent(StaleRedeliveryEvent, new ActivityTagsCollection
            {
                { "run.id", runId },
                { "run.seq", expectedSeq },
                { "disposition", StaleRedeliverySettled },
            });
            await SettleAsync(actions, message, deadLetter: false, reason: null, description: null, cancellationToken);
            return;
        }

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(runId, heartbeatCancellation.Token);
        try
        {
            object? resultSummary;
            try
            {
                resultSummary = await work(payload, cancellationToken);
            }
            catch (ContinuationScheduledException)
            {
                await SettleAsync(actions, message, deadLetter: false, reason: null, description: null, cancellationToken);
                return;
            }
            catch (Exception exc)
            {
                Telemetry.Tracing.RecordHandledException("curator.job.dead-lettered", exc);
                var failure = ClassifyJobError(exc);
                await _jobRuns.MarkFailedAsync(runId, failure, cancellationToken);
                await SettleAsync(
                    actions, message, deadLetter: true, failure.ErrorCode, failure.Message, cancellationToken);
                return;
            }

            await _jobRuns.MarkSucceededAsync(runId, resultSummary, cancellationToken);
            await SettleAsync(actions, message, deadLetter: false, reason: null, description: null, cancellationToken);
        }
        finally
        {
            await heartbeatCancellation.CancelAsync();
            await heartbeat;
        }
    }

    private static string ProviderKeyRejectedMessage(EnrichmentProvider provider) =>
        $"Your {provider.ToWireName().ToUpperInvariant()} API key was rejected. Check that it's correct and try again.";

    private static async Task SettleAsync(
        ServiceBusMessageActions actions,
        ServiceBusReceivedMessage message,
        bool deadLetter,
        string? reason,
        string? description,
        CancellationToken cancellationToken)
    {
        try
        {
            if (deadLetter)
            {
                await actions.DeadLetterMessageAsync(
                    message, deadLetterReason: reason, deadLetterErrorDescription: description, cancellationToken: cancellationToken);
            }
            else
            {
                await actions.CompleteMessageAsync(message, cancellationToken);
            }
        }
        catch (ServiceBusException exc) when (exc.Reason == ServiceBusFailureReason.MessageLockLost)
        {
            Telemetry.Tracing.RecordHandledException("curator.job.settle-lock-lost", exc);
        }
    }

    private async Task HeartbeatAsync(string runId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_heartbeatInterval, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!await _jobRuns.RenewLeaseAsync(runId, cancellationToken: cancellationToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exc)
            {
                Telemetry.Tracing.RecordHandledException("curator.job.lease-renewal-failed", exc);
            }
        }
    }
}
