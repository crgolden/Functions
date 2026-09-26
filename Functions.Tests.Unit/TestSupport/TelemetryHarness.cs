namespace Functions.Tests.Unit.TestSupport;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal sealed class TelemetryHarness : IDisposable
{
    private readonly ServiceProvider _services;

    internal TelemetryHarness()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        MeterFactory = _services.GetRequiredService<IMeterFactory>();
        Descriptions = new TelemetryOptions(
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription(),
            Generated.NewDescription());
        Telemetry = new Telemetry(MeterFactory, Options.Create(Descriptions));
    }

    internal static TelemetryHarness Shared { get; } = new();

    internal IMeterFactory MeterFactory { get; }

    internal TelemetryOptions Descriptions { get; }

    internal Telemetry Telemetry { get; }

    public void Dispose()
    {
        Telemetry.Dispose();
        _services.Dispose();
    }

    internal Dictionary<string, string?> PublishedDescriptions()
    {
        Dictionary<string, string?> descriptions = new(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, MeterFactory))
            {
                descriptions[instrument.Name] = instrument.Description;
            }
        };
        listener.Start();
        return descriptions;
    }
}
