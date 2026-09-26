namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using Functions.Curator.Jobs;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using static Functions.Tests.Unit.ExpiredLeaseReaperFixtureConstants;

[Trait("Category", "Unit")]
public sealed class ExpiredLeaseReaperTests
{
    private static readonly string ReaperEnabled = true.ToString(CultureInfo.InvariantCulture);
    private static readonly string ReaperDisabled = false.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void Constructor_Throws_WhenTheReaperSettingIsMissing()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();

        // Act
        var missing = Record.Exception(() => NewReaper(dataSource, new Dictionary<string, string?>()));

        // Assert
        Assert.IsType<InvalidOperationException>(missing);
    }

    [Fact]
    public async Task Run_OpensNoConnection_WhenTheReaperIsExplicitlyDisabled()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var reaper = NewReaper(dataSource, new Dictionary<string, string?>
        {
            [ExpiredLeaseReaper.EnabledSetting] = ReaperDisabled,
        });

        // Act
        await reaper.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(NoConnections, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task Run_ReapsAbandonedRuns_WhenTheReaperIsEnabled()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var abandonedRunId = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(RunIdTable(abandonedRunId)));
        var reaper = NewReaper(dataSource, new Dictionary<string, string?>
        {
            [ExpiredLeaseReaper.EnabledSetting] = ReaperEnabled,
        });

        // Act
        await reaper.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OneConnection, dataSource.ConnectionsCreated);
        Assert.Contains("UPDATE job_runs", dataSource.ExecutedCommands[0].CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RecordsTheAbandonedRunError_WhenItReaps()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var abandonedRunId = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(RunIdTable(abandonedRunId)));
        var reaper = NewReaper(dataSource, new Dictionary<string, string?>
        {
            [ExpiredLeaseReaper.EnabledSetting] = ReaperEnabled,
        });

        // Act
        await reaper.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var parameters = dataSource.ExecutedCommands[0].Parameters;
        Assert.Equal(ExpiredLeaseReaper.AbandonedRunError, parameters[0].Value);
    }

    [Fact]
    public async Task Run_ClassifiesWhatItReapsAsAbandoned_NotAsAnUncodedFailure()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var abandonedRunId = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(RunIdTable(abandonedRunId)));
        var reaper = NewReaper(dataSource, new Dictionary<string, string?>
        {
            [ExpiredLeaseReaper.EnabledSetting] = ReaperEnabled,
        });

        // Act
        await reaper.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var parameters = dataSource.ExecutedCommands[0].Parameters;
        Assert.Equal(JobErrorCodes.Abandoned, parameters[1].Value);
    }

    internal static DataTable RunIdTable(params Guid[] runIds)
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid));
        foreach (var runId in runIds)
        {
            table.Rows.Add(runId);
        }

        return table;
    }

    private static ExpiredLeaseReaper NewReaper(FakeDbDataSource dataSource, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ExpiredLeaseReaper(new JobRunsRepository(dataSource), configuration, TelemetryHarness.Shared.Telemetry);
    }
}
