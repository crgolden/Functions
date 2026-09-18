namespace Functions.Tests.Unit;

using System.Diagnostics.Metrics;
using System.Globalization;
using Churches.Geocoding;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class TelemetryMetricsTests
{
    [Fact]
    public void ReGeocoded_EmitsOnTheDocumentedInstrumentWithTheResultTag()
    {
        // Arrange
        var churches = Random.Shared.Next(1, 500);
        var result = TestValues.NewLettersOnlyToken();
        using var recorder = new MeterRecorder(Telemetry.Metrics.ReGeocodedChurchesInstrumentName);

        // Act
        Telemetry.Metrics.ReGeocoded(churches, result);

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
        using var recorder = new MeterRecorder(Telemetry.Metrics.ReGeocodedChurchesInstrumentName);

        // Act
        Telemetry.Metrics.ReGeocoded(updated, ReGeocodeJob.UpdatedResult);
        Telemetry.Metrics.ReGeocoded(stillMissing, ReGeocodeJob.StillMissingResult);
        Telemetry.Metrics.ReGeocoded(notPersisted, ReGeocodeJob.NotPersistedResult);

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
            Telemetry.Metrics.BulkImportRowsInstrumentName,
            Telemetry.Metrics.ResultTagName,
            Telemetry.Metrics.SourceTagName,
        ];

        // Assert
        Assert.Equal(["functions.churches.regeocode.churches", "functions.churches.bulk_import.rows", "result", "source"], names);
    }

    [Fact]
    public void BulkImportRows_EmitsOnTheDocumentedInstrumentWithBothTags()
    {
        // Arrange
        var rows = Random.Shared.Next(1, 5000);
        var result = TestValues.NewLettersOnlyToken();
        var source = TestValues.NewLettersOnlyToken();
        using var recorder = new MeterRecorder(Telemetry.Metrics.BulkImportRowsInstrumentName);

        // Act
        Telemetry.Metrics.BulkImportRows(rows, result, source);

        // Assert
        var measurement = Assert.Single(recorder.Measurements);
        Assert.Equal(rows, measurement.Value);
        Assert.Equal(result, Assert.Contains(Telemetry.Metrics.ResultTagName, measurement.Tags));
        Assert.Equal(source, Assert.Contains(Telemetry.Metrics.SourceTagName, measurement.Tags));
    }

    private sealed class MeterRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(long Value, IReadOnlyDictionary<string, object?> Tags)> _measurements = [];
        private readonly Lock _gate = new();

        public MeterRecorder(string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var copied = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    copied[tag.Key] = tag.Value;
                }

                lock (_gate)
                {
                    _measurements.Add((value, copied));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<(long Value, IReadOnlyDictionary<string, object?> Tags)> Measurements
        {
            get
            {
                lock (_gate)
                {
                    return [.. _measurements];
                }
            }
        }

        public long TotalFor(string tagName, string tagValue) =>
            Measurements
                .Where(m => m.Tags.TryGetValue(tagName, out var v)
                    && string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), tagValue, StringComparison.Ordinal))
                .Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }
}
