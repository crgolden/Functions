namespace Functions.Tests.Unit.TestSupport;

using System.Diagnostics.Metrics;
using System.Globalization;

internal sealed class MeterRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(long Value, IReadOnlyDictionary<string, object?> Tags)> _measurements = [];
    private readonly Lock _gate = new();

    public MeterRecorder(IMeterFactory meterFactory, string instrumentName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, meterFactory) &&
                string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
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
