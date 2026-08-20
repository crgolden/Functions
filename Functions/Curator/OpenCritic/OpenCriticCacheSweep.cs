namespace Functions.Curator.OpenCritic;

using System.Diagnostics;
using Enrichment;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

public sealed class OpenCriticCacheSweep
{
    public const int DefaultMaxPagesPerRun = OpenCriticAdminRefreshService.AdminRefreshMaxPages;

    private const string SweepSkippedEvent = "curator.opencritic.sweep-skipped";
    private const string SweepCursorContendedEvent = "curator.opencritic.sweep-cursor-contended";
    private const string SweepKeysRejectedEvent = "curator.opencritic.sweep-keys-rejected";
    private const string SweepKeysRateLimitedEvent = "curator.opencritic.sweep-keys-rate-limited";

    private static readonly string[] Platforms = ["ps4", "ps5"];

    private readonly OpenCriticCacheRepository _repository;
    private readonly IOpenCriticClient _client;
    private readonly IReadOnlyList<string> _rapidApiKeys;
    private readonly int _maxPagesPerRun;

    public OpenCriticCacheSweep(
        OpenCriticCacheRepository repository,
        IOpenCriticClient client,
        IConfiguration configuration)
    {
        _repository = repository;
        _client = client;
        _rapidApiKeys = configuration.ConfiguredValues("OpenCriticRapidApiKey");
        _maxPagesPerRun = configuration.GetValue<int?>("OpenCriticSweepMaxPages") ?? DefaultMaxPagesPerRun;
    }

    [Function(nameof(OpenCriticCacheSweep))]
    public async Task Run(
        [TimerTrigger("0 0 5 * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (_rapidApiKeys.Count == 0)
        {
            Telemetry.Tracing.RecordHandledFailure(
                SweepSkippedEvent, "No OpenCriticRapidApiKey is configured.");
            return;
        }

        var credentials = _rapidApiKeys
            .Select(key => new OpenCriticCredential { RapidApiKey = key })
            .ToArray();
        var refresher = new OpenCriticAdminRefreshService(
            _repository, _client, credentials, _maxPagesPerRun);

        try
        {
            var outcome = await refresher.RefreshCacheAsync(Platforms, cancellationToken);
            Telemetry.Metrics.OpenCriticSweepFetched(outcome.GamesFetched);
            if (outcome.ContendedPlatforms.Count > 0)
            {
                Telemetry.Tracing.RecordEvent(SweepCursorContendedEvent, new ActivityTagsCollection
                {
                    { "platforms", string.Join(", ", outcome.ContendedPlatforms) },
                });
            }
        }
        catch (EnrichmentAuthException exception)
        {
            Telemetry.Tracing.RecordHandledException(SweepKeysRejectedEvent, exception);
        }
        catch (EnrichmentRateLimitException exception)
        {
            Telemetry.Tracing.RecordHandledException(SweepKeysRateLimitedEvent, exception);
        }
    }
}
