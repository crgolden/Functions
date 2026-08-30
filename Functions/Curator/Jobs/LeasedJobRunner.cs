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

    private const string MalformedPayloadEvent = "curator.job.malformed-payload";
    private const string InterruptedEvent = "curator.job.interrupted";
    private const string StoodDownEvent = "curator.job.stood-down";

    private const string JobOutcomeSucceeded = "succeeded";
    private const string JobOutcomeFailed = "failed";
    private const string JobOutcomeContinued = "continued";
    private const string JobOutcomeInterrupted = "interrupted";
    private const string JobOutcomeStoodDown = "stood-down";
    private const string JobOutcomeStaleDeadLettered = "stale-" + StaleRedeliveryDeadLettered;
    private const string JobOutcomeStaleSettled = "stale-" + StaleRedeliverySettled;

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
        using var jobRun = Telemetry.Tracing.StartJobRun(typeof(TMessage).Name);
        try
        {
            TMessage payload;
            try
            {
                payload = message.Body.ToObjectFromJson<TMessage>()
                    ?? throw new JsonException("body deserialized to null");
            }
            catch (JsonException exc)
            {
                Telemetry.Tracing.RecordHandledException(MalformedPayloadEvent, exc);
                Telemetry.Tracing.RecordJobOutcome(jobRun, MalformedPayload);
                await SettleAsync(actions, message, deadLetter: true, MalformedPayload, exc.Message, cancellationToken);
                return;
            }

            var runId = payload.RunId;
            var expectedSeq = payload.Seq;

            Telemetry.Tracing.RecordJobIdentity(jobRun, runId, expectedSeq);

            if (!await _jobRuns.TryBeginDeliveryAsync(runId, expectedSeq, cancellationToken: cancellationToken))
            {
                var current = await _jobRuns.GetAsync(runId, cancellationToken);
                if (current is { Status: JobRunStatuses.Failed })
                {
                    Telemetry.Metrics.StaleRedelivery(StaleRedeliveryDeadLettered);
                    Telemetry.Tracing.RecordJobOutcome(jobRun, JobOutcomeStaleDeadLettered);
                    Telemetry.Tracing.RecordEvent(StaleRedeliveryEvent, new ActivityTagsCollection
                    {
                        { Telemetry.Tracing.RunIdTagName, runId },
                        { "disposition", StaleRedeliveryDeadLettered },
                    });
                    await SettleAsync(
                        actions, message, deadLetter: true, ProcessingFailed, current.Error ?? "run already failed", cancellationToken);
                    return;
                }

                Telemetry.Metrics.StaleRedelivery(StaleRedeliverySettled);
                Telemetry.Tracing.RecordJobOutcome(jobRun, JobOutcomeStaleSettled);
                Telemetry.Tracing.RecordEvent(StaleRedeliveryEvent, new ActivityTagsCollection
                {
                    { Telemetry.Tracing.RunIdTagName, runId },
                    { Telemetry.Tracing.RunSeqTagName, expectedSeq },
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
                    Telemetry.Tracing.RecordJobOutcome(jobRun, JobOutcomeContinued);
                    await SettleAsync(actions, message, deadLetter: false, reason: null, description: null, cancellationToken);
                    return;
                }
                catch (JobRunStoodDownException exc)
                {
                    Telemetry.Tracing.RecordHandledException(StoodDownEvent, exc);
                    Telemetry.Tracing.RecordJobOutcome(jobRun, JobOutcomeStoodDown);
                    await SettleAsync(actions, message, deadLetter: false, reason: null, description: null, cancellationToken);
                    return;
                }
                catch (Exception exc)
                {
                    Telemetry.Tracing.RecordHandledException("curator.job.dead-lettered", exc);
                    var failure = ClassifyJobError(exc);
                    if (!await _jobRuns.TryMarkFailedAsync(runId, failure, cancellationToken))
                    {
                        Telemetry.Tracing.RecordJobOutcome(jobRun, JobOutcomeStoodDown);
                        await SettleAsync(actions, message, deadLetter: false, reason: null, description: null, cancellationToken);
                        return;
                    }

                    Telemetry.Tracing.RecordJobOutcome(jobRun, JobOutcomeFailed, failure.ErrorCode);
                    await SettleAsync(
                        actions, message, deadLetter: true, failure.ErrorCode, failure.Message, cancellationToken);
                    return;
                }

                var marked = await _jobRuns.TryMarkSucceededAsync(runId, resultSummary, cancellationToken);
                Telemetry.Tracing.RecordJobOutcome(jobRun, marked ? JobOutcomeSucceeded : JobOutcomeStoodDown);
                await SettleAsync(actions, message, deadLetter: false, reason: null, description: null, cancellationToken);
            }
            finally
            {
                await heartbeatCancellation.CancelAsync();
                await heartbeat;
            }
        }
        catch (Exception exc)
        {
            Telemetry.Tracing.RecordHandledException(InterruptedEvent, exc);
            throw;
        }
        finally
        {
            Telemetry.Tracing.RecordJobOutcomeIfAbsent(jobRun, JobOutcomeInterrupted);
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
