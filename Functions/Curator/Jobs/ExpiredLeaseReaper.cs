namespace Functions.Curator.Jobs;

using System.Diagnostics;
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

    public ExpiredLeaseReaper(
        JobRunsRepository repository,
        IConfiguration configuration)
    {
        _repository = repository;
        _enabled = configuration.GetValue<bool?>(EnabledSetting) ?? false;
    }

    [Function(nameof(ExpiredLeaseReaper))]
    public async Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            Telemetry.Tracing.RecordHandledFailure(
                ReaperSkippedEvent, "CuratorJobReaperEnabled is not set.");
            return;
        }

        var reaped = await _repository.ReapExpiredLeasesAsync(
            AbandonedRunError,
            cancellationToken: cancellationToken);
        if (reaped.Count == 0)
        {
            return;
        }

        Telemetry.Metrics.LeasesReaped(reaped.Count);
        Telemetry.Tracing.RecordEvent(LeasesReapedEvent, new ActivityTagsCollection
        {
            { "run.count", reaped.Count },
            { "run.ids", string.Join(", ", reaped) },
        });
    }
}
