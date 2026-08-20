namespace Functions.Tests.Unit;

using System.Data;
using Curator.Jobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ExpiredLeaseReaperTests
{
    [Fact]
    public async Task Run_OpensNoConnection_WhenTheReaperIsNotEnabled()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var reaper = NewReaper(dataSource, new Dictionary<string, string?>());

        // Act
        await reaper.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task Run_OpensNoConnection_WhenTheReaperIsExplicitlyDisabled()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var reaper = NewReaper(dataSource, new Dictionary<string, string?>
        {
            [ExpiredLeaseReaper.EnabledSetting] = "false",
        });

        // Act
        await reaper.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, dataSource.ConnectionsCreated);
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
            [ExpiredLeaseReaper.EnabledSetting] = "true",
        });

        // Act
        await reaper.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, dataSource.ConnectionsCreated);
        Assert.Contains("UPDATE job_runs", dataSource.ExecutedCommands[0].CapturedCommandText);
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
            [ExpiredLeaseReaper.EnabledSetting] = "true",
        });

        // Act
        await reaper.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var parameters = dataSource.ExecutedCommands[0].Parameters;
        Assert.Equal(ExpiredLeaseReaper.AbandonedRunError, parameters[0].Value);
    }

    internal static DataTable RunIdTable(params Guid[] runIds)
    {
        var table = new DataTable();
        table.Columns.Add("run_id", typeof(Guid));
        foreach (var runId in runIds)
        {
            table.Rows.Add(runId);
        }

        return table;
    }

    private static ExpiredLeaseReaper NewReaper(FakeDbDataSource dataSource, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ExpiredLeaseReaper(new JobRunsRepository(dataSource), configuration);
    }
}
