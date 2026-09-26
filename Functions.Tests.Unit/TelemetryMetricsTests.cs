namespace Functions.Tests.Unit;

using Functions.Churches.Geocoding;
using Functions.Tests.Unit.TestSupport;

[Trait("Category", "Unit")]
public sealed class TelemetryMetricsTests : IDisposable
{
    private readonly TelemetryHarness _harness = new();

    [Fact]
    public void EveryInstrument_CarriesItsConfiguredDescription()
    {
        // Arrange
        Dictionary<string, string?> expected = new(StringComparer.Ordinal)
        {
            [Telemetry.Metrics.ExceptionsInstrumentName] = _harness.Descriptions.ExceptionsDescription,
            [Telemetry.Metrics.GeocoderFallbacksInstrumentName] = _harness.Descriptions.GeocoderFallbacksDescription,
            [Telemetry.Metrics.ZipBackfillInstrumentName] = _harness.Descriptions.ZipBackfillDescription,
            [Telemetry.Metrics.BulkImportRowsInstrumentName] = _harness.Descriptions.BulkImportRowsDescription,
            [Telemetry.Metrics.ReGeocodedChurchesInstrumentName] = _harness.Descriptions.ReGeocodedChurchesDescription,
            [Telemetry.Metrics.ReGeocodedCampusesInstrumentName] = _harness.Descriptions.ReGeocodedCampusesDescription,
            [Telemetry.Metrics.EnrichmentGateUnavailableInstrumentName] = _harness.Descriptions.EnrichmentGateUnavailableDescription,
            [Telemetry.Metrics.EnrichmentGamesInstrumentName] = _harness.Descriptions.EnrichmentGamesDescription,
            [Telemetry.Metrics.ProviderDisabledInstrumentName] = _harness.Descriptions.ProviderDisabledDescription,
            [Telemetry.Metrics.StaleRedeliveriesInstrumentName] = _harness.Descriptions.StaleRedeliveriesDescription,
            [Telemetry.Metrics.TransientRetriesInstrumentName] = _harness.Descriptions.TransientRetriesDescription,
            [Telemetry.Metrics.ReapedLeasesInstrumentName] = _harness.Descriptions.ReapedLeasesDescription,
            [Telemetry.Metrics.OpenCriticSweepGamesInstrumentName] = _harness.Descriptions.OpenCriticSweepGamesDescription,
            [Telemetry.Metrics.PsnSessionRotationsInstrumentName] = _harness.Descriptions.PsnSessionRotationsDescription,
            [Telemetry.Metrics.StoreProductsInstrumentName] = _harness.Descriptions.StoreProductsDescription,
            [Telemetry.Metrics.QueueActiveInstrumentName] = _harness.Descriptions.QueueActiveDescription,
            [Telemetry.Metrics.QueueDeadLetterInstrumentName] = _harness.Descriptions.QueueDeadLetterDescription,
        };

        // Act
        var actual = _harness.PublishedDescriptions();

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ReGeocoded_EmitsOnTheDocumentedInstrumentWithTheResultTag()
    {
        // Arrange
        var churches = Random.Shared.Next(1, 500);
        var result = Generated.NewLettersOnlyToken();
        using var recorder = new MeterRecorder(_harness.MeterFactory, Telemetry.Metrics.ReGeocodedChurchesInstrumentName);

        // Act
        _harness.Telemetry.ReGeocoded(churches, result);

        // Assert
        var measurement = Assert.Single(recorder.Measurements);
        Assert.Equal(churches, measurement.Value);
        Assert.Equal(result, Assert.Contains(Telemetry.Metrics.ResultTagName, measurement.Tags));
    }

    [Fact]
    public void ReGeocoded_KeepsTheThreeOutcomesApartOnTheResultTag()
    {
        // Arrange
        var updated = Random.Shared.Next(1, 500);
        var stillMissing = Random.Shared.Next(1, 500);
        var notPersisted = Random.Shared.Next(1, 500);
        using var recorder = new MeterRecorder(_harness.MeterFactory, Telemetry.Metrics.ReGeocodedChurchesInstrumentName);

        // Act
        _harness.Telemetry.ReGeocoded(updated, ReGeocodeJob.UpdatedResult);
        _harness.Telemetry.ReGeocoded(stillMissing, ReGeocodeJob.StillMissingResult);
        _harness.Telemetry.ReGeocoded(notPersisted, ReGeocodeJob.NotPersistedResult);

        // Assert
        Assert.Equal(updated, recorder.TotalFor(Telemetry.Metrics.ResultTagName, ReGeocodeJob.UpdatedResult));
        Assert.Equal(stillMissing, recorder.TotalFor(Telemetry.Metrics.ResultTagName, ReGeocodeJob.StillMissingResult));
        Assert.Equal(notPersisted, recorder.TotalFor(Telemetry.Metrics.ResultTagName, ReGeocodeJob.NotPersistedResult));
    }

    [Fact]
    public void ReGeocodedCampuses_EmitsOnItsOwnInstrumentRatherThanTheChurchOne()
    {
        // Arrange
        var campuses = Random.Shared.Next(1, 500);
        using var campusRecorder = new MeterRecorder(_harness.MeterFactory, Telemetry.Metrics.ReGeocodedCampusesInstrumentName);
        using var churchRecorder = new MeterRecorder(_harness.MeterFactory, Telemetry.Metrics.ReGeocodedChurchesInstrumentName);

        // Act
        _harness.Telemetry.ReGeocodedCampuses(campuses, ReGeocodeJob.UpdatedResult);

        // Assert
        var measurement = Assert.Single(campusRecorder.Measurements);
        Assert.Equal(campuses, measurement.Value);
        Assert.Equal(ReGeocodeJob.UpdatedResult, Assert.Contains(Telemetry.Metrics.ResultTagName, measurement.Tags));
        Assert.Empty(churchRecorder.Measurements);
    }

    [Fact]
    public void ReGeocodedCampuses_KeepsTheThreeOutcomesApartOnTheResultTag()
    {
        // Arrange
        var updated = Random.Shared.Next(1, 500);
        var stillMissing = Random.Shared.Next(1, 500);
        var notPersisted = Random.Shared.Next(1, 500);
        using var recorder = new MeterRecorder(_harness.MeterFactory, Telemetry.Metrics.ReGeocodedCampusesInstrumentName);

        // Act
        _harness.Telemetry.ReGeocodedCampuses(updated, ReGeocodeJob.UpdatedResult);
        _harness.Telemetry.ReGeocodedCampuses(stillMissing, ReGeocodeJob.StillMissingResult);
        _harness.Telemetry.ReGeocodedCampuses(notPersisted, ReGeocodeJob.NotPersistedResult);

        // Assert
        Assert.Equal(updated, recorder.TotalFor(Telemetry.Metrics.ResultTagName, ReGeocodeJob.UpdatedResult));
        Assert.Equal(stillMissing, recorder.TotalFor(Telemetry.Metrics.ResultTagName, ReGeocodeJob.StillMissingResult));
        Assert.Equal(notPersisted, recorder.TotalFor(Telemetry.Metrics.ResultTagName, ReGeocodeJob.NotPersistedResult));
    }

    [Fact]
    public void ReGeocodeResults_KeepTheSpellingsTheReconciliationQueryFiltersOn()
    {
        // Act
        string[] results = [ReGeocodeJob.UpdatedResult, ReGeocodeJob.StillMissingResult, ReGeocodeJob.NotPersistedResult];

        // Assert
        Assert.Equal(["updated", "still_missing", "not_persisted"], results);
    }

    [Fact]
    public void ChurchInstrumentsAndTags_KeepTheNamesTheDashboardsQuery()
    {
        // Act
        string[] names =
        [
            Telemetry.Metrics.ReGeocodedChurchesInstrumentName,
            Telemetry.Metrics.ReGeocodedCampusesInstrumentName,
            Telemetry.Metrics.BulkImportRowsInstrumentName,
            Telemetry.Metrics.EnrichmentGateUnavailableInstrumentName,
            Telemetry.Metrics.ResultTagName,
            Telemetry.Metrics.SourceTagName,
            Telemetry.Metrics.ExceptionTypeTagName,
        ];

        // Assert
        Assert.Equal(
            [
                "functions.churches.regeocode.churches",
                "functions.churches.regeocode.campuses",
                "functions.churches.bulk_import.rows",
                "functions.churches.enrichment.gate_unavailable",
                "result",
                "source",
                "exception.type",
            ],
            names);
    }

    [Fact]
    public void BulkImportRows_EmitsOnTheDocumentedInstrumentWithBothTags()
    {
        // Arrange
        var rows = Random.Shared.Next(1, 5000);
        var result = Generated.NewLettersOnlyToken();
        var source = Generated.NewLettersOnlyToken();
        using var recorder = new MeterRecorder(_harness.MeterFactory, Telemetry.Metrics.BulkImportRowsInstrumentName);

        // Act
        _harness.Telemetry.BulkImportRows(rows, result, source);

        // Assert
        var measurement = Assert.Single(recorder.Measurements);
        Assert.Equal(rows, measurement.Value);
        Assert.Equal(result, Assert.Contains(Telemetry.Metrics.ResultTagName, measurement.Tags));
        Assert.Equal(source, Assert.Contains(Telemetry.Metrics.SourceTagName, measurement.Tags));
    }

    public void Dispose() => _harness.Dispose();
}
