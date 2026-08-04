using System.Text;
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
        Assert.False(await first.TryBeginPassAsync(CancellationToken.None));
        await first.ResumeAsync();
        Assert.True(await first.TryBeginPassAsync(CancellationToken.None));
        await first.RecordSuccessfulSweepAsync(new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero), CancellationToken.None);

        var replacement = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, options, Guid.NewGuid()),
            schema);
        var staleIdentity = await replacement.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Stale, staleIdentity.State);
        Assert.Equal(DurableProblemCodes.WorkerIdentityConflict, staleIdentity.ProblemCode);
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

    [Fact]
    public async Task GetAsync_ReportsSchemaEpochAndHeartbeatCompatibilityWithoutInventingLiveness()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        var missing = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-missing-worker"),
                Guid.NewGuid()),
            schema);
        var missingSnapshot = await missing.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Incompatible, missingSnapshot.State);
        Assert.Equal(DurableProblemCodes.SchemaMissing, missingSnapshot.ProblemCode);

        await schema.ApplyAsync();
        var activeEpoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(activeEpoch, "runtime-tests", "compatibility");
        var status = await schema.GetStatusAsync();
        var mismatched = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), status.StoreId),
                CreateOptions("runtime-health-mismatch-worker"),
                Guid.NewGuid()),
            schema);
        var mismatchSnapshot = await mismatched.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Incompatible, mismatchSnapshot.State);
        Assert.Equal(DurableProblemCodes.RecoveryEpochRequired, mismatchSnapshot.ProblemCode);

        var current = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(activeEpoch, status.StoreId),
                CreateOptions("runtime-health-stale-worker"),
                Guid.NewGuid()),
            schema);
        Assert.True(await current.TryBeginPassAsync(CancellationToken.None));
        Assert.False(await current.TryBeginPassAsync(CancellationToken.None));
        await current.RecordHeartbeatAsync(CancellationToken.None);
        await using (var stale = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.runtime_heartbeat SET last_heartbeat_at = clock_timestamp() - interval '1 hour';"))
        {
            Assert.Equal(1, await stale.ExecuteNonQueryAsync());
        }

        var staleSnapshot = await current.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Stale, staleSnapshot.State);
        Assert.Equal(DurableProblemCodes.ActivatorStale, staleSnapshot.ProblemCode);
        await current.RecordFailedPassAsync(CancellationToken.None);
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await current.GetAsync()).State);
    }

    [Fact]
    public async Task GetAsync_ReportsSchemaCompatibilityAndTransientReadFailuresWithoutOpeningTheRuntimeStore()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=durable_health;Username=durable;Password=not-opened");
        var options = CreateOptions("runtime-health-schema-worker");
        var workOptions = new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid());
        var expected = new Dictionary<DurableRuntimeSchemaCompatibility, string>
        {
            [DurableRuntimeSchemaCompatibility.Missing] = DurableProblemCodes.SchemaMissing,
            [DurableRuntimeSchemaCompatibility.UpgradeRequired] = DurableProblemCodes.SchemaUpgradeRequired,
            [DurableRuntimeSchemaCompatibility.StoreTooNew] = DurableProblemCodes.SchemaVersionUnsupported,
            [DurableRuntimeSchemaCompatibility.Inconsistent] = DurableProblemCodes.SchemaInconsistent,
        };
        foreach (var (compatibility, problemCode) in expected)
        {
            var health = new PostgreSqlDurableRuntimeHealth(
                CreateRegistration(dataSource, workOptions, options, Guid.NewGuid()),
                new StubSchemaManager(_ => ValueTask.FromResult(CreateStatus(compatibility))));

            var snapshot = await health.GetAsync();

            Assert.Equal(DurableRuntimeHealthState.Incompatible, snapshot.State);
            Assert.Equal(problemCode, snapshot.ProblemCode);
        }

        var transient = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(dataSource, workOptions, options, Guid.NewGuid()),
            new StubSchemaManager(_ => ValueTask.FromException<DurableRuntimeSchemaStatus>(new TimeoutException())));
        var transientSnapshot = await transient.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Incompatible, transientSnapshot.State);
        Assert.Equal(DurableProblemCodes.SchemaInconsistent, transientSnapshot.ProblemCode);
    }

    [Fact]
    public async Task GetAsync_MapsTransientWorkerReadToSchemaInconsistent()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=durable_health;Username=durable;Password=not-opened");
        var schemaStatus = CreateStatus(DurableRuntimeSchemaCompatibility.Compatible);
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                dataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-transient-worker"),
                Guid.NewGuid()),
            new StubSchemaManager(_ => ValueTask.FromResult(schemaStatus)));

        var snapshot = await health.GetAsync();

        Assert.Equal(DurableRuntimeHealthState.Incompatible, snapshot.State);
        Assert.Equal(DurableProblemCodes.SchemaInconsistent, snapshot.ProblemCode);
        Assert.False(snapshot.SchemaCompatible);
        Assert.Equal(schemaStatus.InstalledVersion, snapshot.InstalledSchemaVersion);
    }

    [Fact]
    public async Task GetAsync_MapsTransientDueReadToSchemaInconsistent()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "transient-due-read");
        var schemaStatus = await schema.GetStatusAsync();
        await using (var dropFunction = database.DataSource.CreateCommand(
            "DROP FUNCTION appsurface_durable.runtime_due_dispatch_health(integer);"))
        {
            await dropFunction.ExecuteNonQueryAsync();
        }

        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, schemaStatus.StoreId),
                CreateOptions("runtime-health-transient-due-worker"),
                Guid.NewGuid()),
            new StubSchemaManager(_ => ValueTask.FromResult(schemaStatus)));

        var snapshot = await health.GetAsync();

        Assert.Equal(DurableRuntimeHealthState.Incompatible, snapshot.State);
        Assert.Equal(DurableProblemCodes.SchemaInconsistent, snapshot.ProblemCode);
        Assert.False(snapshot.SchemaCompatible);
        Assert.Equal(schemaStatus.InstalledVersion, snapshot.InstalledSchemaVersion);
    }

    [Fact]
    public async Task GetAsync_ReportsMissingHeartbeatAndMissingMetadataWithoutInventingLiveness()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "missing-state");
        var status = await schema.GetStatusAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions("runtime-health-missing-state-worker"),
                Guid.NewGuid()),
            schema);

        var missingHeartbeat = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.NotStarted, missingHeartbeat.State);
        Assert.Equal(DurableProblemCodes.ActivatorStale, missingHeartbeat.ProblemCode);
        Assert.Null(missingHeartbeat.WorkerInstanceId);
        Assert.Null(missingHeartbeat.LastHeartbeatAtUtc);

        await using (var delete = database.DataSource.CreateCommand(
            "DELETE FROM appsurface_durable.store_metadata WHERE singleton;"))
        {
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        var metadataMissingHealth = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions("runtime-health-missing-state-worker"),
                Guid.NewGuid()),
            new StubSchemaManager(_ => ValueTask.FromResult(CreateStatus(DurableRuntimeSchemaCompatibility.Compatible))));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await metadataMissingHealth.GetAsync());
    }

    [Fact]
    public async Task TryBeginPassAsync_TakesOverOnlyWhenEpochDrainOrStalenessAllowsIt()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "takeover");
        var status = await schema.GetStatusAsync();
        var workOptions = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var incumbent = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, CreateOptions("runtime-health-takeover-worker"), Guid.NewGuid()),
            schema);
        Assert.True(await incumbent.TryBeginPassAsync(CancellationToken.None));
        await incumbent.RecordSuccessfulSweepAsync(
            new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero),
            CancellationToken.None);

        await using (var update = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET worker_instance_id = @instance_id, runtime_epoch = @runtime_epoch,
                draining = false, pass_active = false,
                last_heartbeat_at = clock_timestamp()
            WHERE worker_id = @worker_id;
            """))
        {
            update.Parameters.AddWithValue("instance_id", Guid.NewGuid());
            update.Parameters.AddWithValue("runtime_epoch", Guid.NewGuid());
            update.Parameters.AddWithValue("worker_id", "runtime-health-takeover-worker");
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var epochTakeover = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, CreateOptions("runtime-health-takeover-worker"), Guid.NewGuid()),
            schema);
        Assert.True(await epochTakeover.TryBeginPassAsync(CancellationToken.None));
        await epochTakeover.RecordSuccessfulSweepAsync(
            new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero),
            CancellationToken.None);

        await using (var update = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET worker_instance_id = @instance_id, draining = true, pass_active = false,
                last_heartbeat_at = clock_timestamp()
            WHERE worker_id = @worker_id;
            """))
        {
            update.Parameters.AddWithValue("instance_id", Guid.NewGuid());
            update.Parameters.AddWithValue("worker_id", "runtime-health-takeover-worker");
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var drainingTakeover = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, CreateOptions("runtime-health-takeover-worker"), Guid.NewGuid()),
            schema);
        Assert.True(await drainingTakeover.TryBeginPassAsync(CancellationToken.None));
        await drainingTakeover.RecordSuccessfulSweepAsync(
            new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero),
            CancellationToken.None);

        await using (var update = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET worker_instance_id = @instance_id, draining = false, pass_active = false,
                last_heartbeat_at = clock_timestamp() - interval '1 hour'
            WHERE worker_id = @worker_id;
            """))
        {
            update.Parameters.AddWithValue("instance_id", Guid.NewGuid());
            update.Parameters.AddWithValue("worker_id", "runtime-health-takeover-worker");
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var staleTakeover = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, CreateOptions("runtime-health-takeover-worker"), Guid.NewGuid()),
            schema);
        Assert.True(await staleTakeover.TryBeginPassAsync(CancellationToken.None));
        await staleTakeover.RecordSuccessfulSweepAsync(
            new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero),
            CancellationToken.None);

        await using (var update = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET worker_instance_id = @instance_id, draining = false, pass_active = false,
                last_heartbeat_at = clock_timestamp()
            WHERE worker_id = @worker_id;
            """))
        {
            update.Parameters.AddWithValue("instance_id", Guid.NewGuid());
            update.Parameters.AddWithValue("worker_id", "runtime-health-takeover-worker");
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var rejected = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, CreateOptions("runtime-health-takeover-worker"), Guid.NewGuid()),
            schema);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rejected.TryBeginPassAsync(CancellationToken.None));
        Assert.StartsWith(DurableProblemCodes.WorkerIdentityConflict, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ReportsDueAgeOnlyForPastDispatchesAndLeavesFutureDispatchesQuiescent()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "due-age");
        var status = await schema.GetStatusAsync();
        var workOptions = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var workerId = "runtime-health-due-age-worker";
        var options = CreateOptions(workerId);
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(database.DataSource, workOptions, options, Guid.NewGuid()),
            schema);
        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));
        await health.RecordSuccessfulSweepAsync(
            new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero),
            CancellationToken.None);

        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId));
        var accepted = await client.EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("runtime-health-due-age-scope"),
            new DurableCommandId("runtime-health-due-age-command"),
            "runtime-health-due-age-key",
            PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent),
            "v1",
            new DurableEncodedPayload(
                "tests.delete-provider-access",
                "v1",
                DurableDataClassification.ApprovedApplication,
                Encoding.UTF8.GetBytes("payload")),
            DurableProviderSafety.Idempotent,
            dueAtUtc: DateTimeOffset.UtcNow.AddHours(1)));
        Assert.True(accepted.IsSuccess);

        var future = await health.GetAsync();
        Assert.Equal(0, future.DueDispatchCount);
        Assert.Null(future.OldestDueAtUtc);
        Assert.Null(future.OldestDueAge);

        await using (var due = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.dispatch SET due_at = clock_timestamp() - interval '1 hour' WHERE aggregate_id = @work_id;"))
        {
            due.Parameters.AddWithValue("work_id", accepted.Value!.WorkId.Value);
            Assert.Equal(1, await due.ExecuteNonQueryAsync());
        }

        var past = await health.GetAsync();
        Assert.Equal(1, past.DueDispatchCount);
        Assert.NotNull(past.OldestDueAtUtc);
        Assert.NotNull(past.OldestDueAge);
        Assert.True(past.OldestDueAge >= TimeSpan.FromMinutes(59));
    }

    [Fact]
    public async Task RuntimeMutations_FailClosedWhenNoActivePassOrMatchingEpochCanOwnTheHeartbeat()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var activeEpoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(activeEpoch, "runtime-tests", "mutation-failures");
        var status = await schema.GetStatusAsync();
        var current = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(activeEpoch, status.StoreId),
                CreateOptions("runtime-health-no-pass-worker"),
                Guid.NewGuid()),
            schema);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await current.RecordHeartbeatAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await current.RecordFailedPassAsync(CancellationToken.None));

        var mismatched = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), status.StoreId),
                CreateOptions("runtime-health-epoch-worker"),
                Guid.NewGuid()),
            schema);
        var epochFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await mismatched.TryBeginPassAsync(CancellationToken.None));
        Assert.StartsWith(DurableProblemCodes.RecoveryEpochRequired, epochFailure.Message, StringComparison.Ordinal);
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

    private static DurableRuntimeSchemaStatus CreateStatus(DurableRuntimeSchemaCompatibility compatibility) => new(
        compatibility,
        Guid.NewGuid(),
        activeRuntimeEpoch: null,
        installedVersion: 3,
        requiredVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
        minimumReaderVersion: 1,
        maximumReaderVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
        minimumWriterVersion: 1,
        maximumWriterVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
        appliedVersions: [],
        pendingVersions: [],
        problem: null);

    private sealed class StubSchemaManager(
        Func<CancellationToken, ValueTask<DurableRuntimeSchemaStatus>> getStatus) : IDurableRuntimeSchemaManager
    {
        public ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            getStatus(cancellationToken);

        public string GenerateScript(int fromVersion = 0) => throw new NotSupportedException();

        public ValueTask<DurableRuntimeSchemaApplyResult> ApplyAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<DurableRuntimeEpochActivationResult> InitializeRuntimeEpochAsync(
            Guid initialEpoch,
            string actorId,
            string reasonCode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<DurableRuntimeEpochRotationResult> RotateRuntimeEpochAsync(
            Guid expectedActiveEpoch,
            Guid newActiveEpoch,
            string actorId,
            string reasonCode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
