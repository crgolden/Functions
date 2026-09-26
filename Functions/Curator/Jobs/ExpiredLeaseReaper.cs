namespace Functions.Curator.Jobs;

using System.Diagnostics;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

public sealed class ExpiredLeaseReaper
{
    public const string AbandonedRunError = "Abandoned: the processing lease expired and no processor renewed it.";

    internal const string EnabledSetting = "CuratorJobReaperEnabled";

    private const string ReaperSkippedEvent = "curator.jobs.reaper-skipped";
    private const string LeasesReapedEvent = "curator.jobs.leases-reaped";

    private readonly JobRunsRepository _repository;
    private readonly bool _enabled;
    private readonly Telemetry _telemetry;

    public ExpiredLeaseReaper(
        JobRunsRepository repository,
        IConfiguration configuration,
        Telemetry telemetry)
    {
        _repository = repository;
        _telemetry = telemetry;
        _enabled = configuration.GetRequired<bool>(EnabledSetting);
    }

    [Function(nameof(ExpiredLeaseReaper))]
    public async Task Run(
        [TimerTrigger("0 0 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            Telemetry.Tracing.RecordHandledFailure(
                ReaperSkippedEvent, $"{EnabledSetting} is false.");
            return;
        }

        var reaped = await _repository.ReapExpiredLeasesAsync(
            AbandonedRunError,
            JobErrorCodes.Abandoned,
            cancellationToken: cancellationToken);
        if (reaped.Count == 0)
        {
            return;
        }

        _telemetry.LeasesReaped(reaped.Count);
        Telemetry.Tracing.RecordEvent(LeasesReapedEvent, new ActivityTagsCollection
        {
            { "run.count", reaped.Count },
            { "run.ids", string.Join(", ", reaped) },
        });
    }
}
