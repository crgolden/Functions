namespace Functions.Tests.Unit;

using System.Diagnostics.Metrics;
using System.Globalization;

[Trait("Category", "Unit")]
public sealed class TelemetryMetricsTests
{
    [Fact]
    public void ReGeocoded_EmitsOnTheDocumentedInstrumentWithTheResultTag()
    {
        // Arrange
        var churches = Random.Shared.Next(1, 500);
        var result = LowercaseToken(9);
        using var recorder = new MeterRecorder("functions.churches.regeocode.churches");

        // Act
        Telemetry.Metrics.ReGeocoded(churches, result);

        // Assert
        var measurement = Assert.Single(recorder.Measurements);
        Assert.Equal(churches, measurement.Value);
        Assert.Equal(result, Assert.Contains("result", measurement.Tags));
    }

    [Fact]
    public void ReGeocoded_KeepsTheThreeOutcomesApartOnTheResultTag()
    {
        // Arrange
        var updated = Random.Shared.Next(1, 500);
        var stillMissing = Random.Shared.Next(1, 500);
        var notPersisted = Random.Shared.Next(1, 500);
        using var recorder = new MeterRecorder("functions.churches.regeocode.churches");

        // Act
        Telemetry.Metrics.ReGeocoded(updated, "updated");
        Telemetry.Metrics.ReGeocoded(stillMissing, "still_missing");
        Telemetry.Metrics.ReGeocoded(notPersisted, "not_persisted");

        // Assert
        Assert.Equal(updated, recorder.TotalFor("result", "updated"));
        Assert.Equal(stillMissing, recorder.TotalFor("result", "still_missing"));
        Assert.Equal(notPersisted, recorder.TotalFor("result", "not_persisted"));
    }

    [Fact]
    public void BulkImportRows_EmitsOnTheDocumentedInstrumentWithBothTags()
    {
        // Arrange
        var rows = Random.Shared.Next(1, 5000);
        var result = LowercaseToken(9);
        var source = LowercaseToken(11);
        using var recorder = new MeterRecorder("functions.churches.bulk_import.rows");

        // Act
        Telemetry.Metrics.BulkImportRows(rows, result, source);

        // Assert
        var measurement = Assert.Single(recorder.Measurements);
        Assert.Equal(rows, measurement.Value);
        Assert.Equal(result, Assert.Contains("result", measurement.Tags));
        Assert.Equal(source, Assert.Contains("source", measurement.Tags));
    }

    private static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

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
