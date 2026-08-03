using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRuntimeHealthTests
{
    [Fact]
    public async Task HeartbeatDrainAndGenerationTakeover_AreFencedByWorkerInstanceAndEpoch()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-tests", "initial");
        var workOptions = new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId);
        var options = CreateOptions("runtime-health-worker");
        var first = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, options, Guid.NewGuid()),
            schema);

        Assert.Equal(DurableRuntimeHealthState.NotStarted, (await first.GetAsync()).State);
        Assert.True(await first.TryBeginPassAsync(CancellationToken.None));
        await first.RecordSuccessfulSweepAsync(new DurableRuntimePumpResult(1, 1, 1, 0, 0, false, null, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await first.GetAsync()).State);

        await first.BeginDrainAsync();
        Assert.Equal(DurableRuntimeHealthState.Draining, (await first.GetAsync()).State);
        await first.ResumeAsync();
        Assert.True(await first.TryBeginPassAsync(CancellationToken.None));
        await first.RecordSuccessfulSweepAsync(new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero), CancellationToken.None);

        var replacement = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, options, Guid.NewGuid()),
            schema);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await replacement.TryBeginPassAsync(CancellationToken.None));
        Assert.StartsWith(DurableProblemCodes.WorkerIdentityConflict, conflict.Message, StringComparison.Ordinal);

        await using (var stale = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.runtime_heartbeat SET last_heartbeat_at = clock_timestamp() - interval '1 hour';"))
        {
            Assert.Equal(1, await stale.ExecuteNonQueryAsync());
        }

        Assert.True(await replacement.TryBeginPassAsync(CancellationToken.None));
        await replacement.RecordSuccessfulSweepAsync(
            new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero),
            CancellationToken.None);
        var staleGeneration = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.BeginDrainAsync());
        Assert.StartsWith(DurableProblemCodes.WorkerIdentityConflict, staleGeneration.Message, StringComparison.Ordinal);
    }

    private static AppSurfaceDurablePostgreSqlOptions CreateOptions(string workerId)
    {
        return new AppSurfaceDurablePostgreSqlOptions
        {
            WorkerId = workerId,
            HeartbeatStaleAfter = TimeSpan.FromSeconds(2),
        }.SnapshotAndValidate();
    }

    private static PostgreSqlDurableRuntimeRegistration CreateRegistration(
        NpgsqlDataSource dataSource,
        PostgreSqlDurableWorkOptions workOptions,
        AppSurfaceDurablePostgreSqlOptions options,
        Guid instanceId) => new(
        dataSource,
        dataSource,
        workOptions,
        new PostgreSqlDurableScheduleOptions("appsurface"),
        options,
        instanceId);
}
