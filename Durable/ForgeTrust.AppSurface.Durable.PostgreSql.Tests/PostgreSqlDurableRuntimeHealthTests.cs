using System.Text;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRuntimeHealthTests
{
    [Fact]
    public void Constructor_RejectsNullRegistrationAndSchemaManager()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=durable_health;Username=durable;Password=not-opened");
        var registration = CreateRegistration(
            dataSource,
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            CreateOptions("runtime-health-constructor-worker"),
            Guid.NewGuid());
        var schema = new StubSchemaManager(_ => ValueTask.FromResult(CreateStatus(DurableRuntimeSchemaCompatibility.Compatible)));

        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableRuntimeHealth(null!, schema));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableRuntimeHealth(registration, null!));
    }

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
    public async Task GetAsync_ReportsMissingSchemaWithoutSelfStarvingASingleConnectionPool()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = "runtime-health-single-connection",
            MaxPoolSize = 1,
        }.ConnectionString;
        await using var runtimeDataSource = NpgsqlDataSource.Create(connectionString);
        var schema = new PostgreSqlDurableRuntimeSchemaManager(runtimeDataSource);
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                runtimeDataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-single-connection-worker"),
                Guid.NewGuid()),
            schema);

        var snapshot = await health.GetAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DurableRuntimeHealthState.Incompatible, snapshot.State);
        Assert.Equal(DurableProblemCodes.SchemaMissing, snapshot.ProblemCode);
        Assert.True(snapshot.WasStoreObserved);
    }

    [Fact]
    public async Task GetAsync_MapsSharedSchemaConnectionFailureToUnavailable()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=durable_health;Username=durable;Password=not-opened;Timeout=1");
        var schema = new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                dataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-shared-schema-unavailable-worker"),
                Guid.NewGuid()),
            schema);

        var snapshot = await health.GetAsync();

        Assert.Equal(DurableRuntimeHealthState.Unavailable, snapshot.State);
        Assert.Equal(DurableProblemCodes.StoreUnavailable, snapshot.ProblemCode);
        Assert.False(snapshot.WasStoreObserved);
        Assert.Equal(0, snapshot.InstalledSchemaVersion);
        Assert.Equal(PostgreSqlDurableRuntimeSchemaManager.RequiredVersion, snapshot.RequiredSchemaVersion);
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
        var databaseObservedAtUtc = new DateTimeOffset(2026, 9, 10, 12, 34, 56, TimeSpan.Zero);
        foreach (var (compatibility, problemCode) in expected)
        {
            var health = new PostgreSqlDurableRuntimeHealth(
                CreateRegistration(dataSource, workOptions, options, Guid.NewGuid()),
                new StubSchemaManager(_ => ValueTask.FromResult(CreateStatus(compatibility))),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgreSqlDurableRuntimeHealth>.Instance,
                _ => ValueTask.FromResult(databaseObservedAtUtc));

            var snapshot = await health.GetAsync();

            Assert.Equal(DurableRuntimeHealthState.Incompatible, snapshot.State);
            Assert.Equal(problemCode, snapshot.ProblemCode);
            Assert.Equal(databaseObservedAtUtc, snapshot.ObservedAtUtc);
            Assert.True(snapshot.WasStoreObserved);
        }

        var transient = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(dataSource, workOptions, options, Guid.NewGuid()),
            new StubSchemaManager(_ => ValueTask.FromException<DurableRuntimeSchemaStatus>(new TimeoutException())));
        var beforeTransientAssessment = DateTimeOffset.UtcNow;
        var transientSnapshot = await transient.GetAsync();
        var afterTransientAssessment = DateTimeOffset.UtcNow;
        Assert.Equal(DurableRuntimeHealthState.Unavailable, transientSnapshot.State);
        Assert.Equal(DurableProblemCodes.StoreUnavailable, transientSnapshot.ProblemCode);
        Assert.False(transientSnapshot.WasStoreObserved);
        Assert.InRange(
            transientSnapshot.ObservedAtUtc,
            beforeTransientAssessment,
            afterTransientAssessment);
    }

    [Fact]
    public async Task GetAsync_ReportsUnavailableWhenSchemaWasObservedButItsDatabaseTimestampWasNot()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=durable_health;Username=durable;Password=not-opened");
        var schemaStatus = CreateStatus(DurableRuntimeSchemaCompatibility.UpgradeRequired);
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                dataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-schema-timestamp-worker"),
                Guid.NewGuid()),
            new StubSchemaManager(_ => ValueTask.FromResult(schemaStatus)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgreSqlDurableRuntimeHealth>.Instance,
            _ => ValueTask.FromException<DateTimeOffset>(new TimeoutException()));

        var beforeAssessment = DateTimeOffset.UtcNow;
        var snapshot = await health.GetAsync();
        var afterAssessment = DateTimeOffset.UtcNow;

        Assert.Equal(DurableRuntimeHealthState.Unavailable, snapshot.State);
        Assert.Equal(DurableProblemCodes.StoreUnavailable, snapshot.ProblemCode);
        Assert.False(snapshot.WasStoreObserved);
        Assert.Equal(schemaStatus.InstalledVersion, snapshot.InstalledSchemaVersion);
        Assert.InRange(snapshot.ObservedAtUtc, beforeAssessment, afterAssessment);
    }

    [Fact]
    public async Task GetAsync_PropagatesCallerCancellationFromSchemaStatus()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=durable_health;Username=durable;Password=not-opened");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                dataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-schema-cancellation-worker"),
                Guid.NewGuid()),
            new StubSchemaManager(
                token => ValueTask.FromCanceled<DurableRuntimeSchemaStatus>(token)));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await health.GetAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task GetAsync_PropagatesCallerCancellationFromSchemaObservationTimestamp()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=durable_health;Username=durable;Password=not-opened");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                dataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-schema-timestamp-cancellation-worker"),
                Guid.NewGuid()),
            new StubSchemaManager(
                _ => ValueTask.FromResult(CreateStatus(DurableRuntimeSchemaCompatibility.UpgradeRequired))),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgreSqlDurableRuntimeHealth>.Instance,
            token => ValueTask.FromCanceled<DateTimeOffset>(token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await health.GetAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task GetAsync_PropagatesCallerCancellationFromRuntimeObservation()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=durable_health;Username=durable;Password=not-opened");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                dataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-observation-cancellation-worker"),
                Guid.NewGuid()),
            new StubSchemaManager(
                _ => ValueTask.FromResult(CreateStatus(DurableRuntimeSchemaCompatibility.Compatible))));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await health.GetAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task GetAsync_MapsTransientRuntimeObservationToUnavailable()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=durable_health;Username=durable;Password=not-opened;Timeout=1");
        var schemaStatus = CreateStatus(DurableRuntimeSchemaCompatibility.Compatible);
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                dataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            CreateOptions("runtime-health-transient-worker"),
            Guid.NewGuid()),
            new StubSchemaManager(_ => ValueTask.FromResult(schemaStatus)));

        var beforeAssessment = DateTimeOffset.UtcNow;
        var snapshot = await health.GetAsync();
        var afterAssessment = DateTimeOffset.UtcNow;

        Assert.Equal(DurableRuntimeHealthState.Unavailable, snapshot.State);
        Assert.Equal(DurableProblemCodes.StoreUnavailable, snapshot.ProblemCode);
        Assert.False(snapshot.WasStoreObserved);
        Assert.False(snapshot.SchemaCompatible);
        Assert.Equal(schemaStatus.InstalledVersion, snapshot.InstalledSchemaVersion);
        Assert.InRange(snapshot.ObservedAtUtc, beforeAssessment, afterAssessment);
    }

    [Fact]
    public async Task GetAsync_MapsRuntimeHealthPermissionFailureToUnavailable()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "permission-failure");
        var status = await schema.GetStatusAsync();
        var role = $"runtime_health_denied_{Guid.NewGuid():N}";
        const string password = "runtime-health-test-password";
        await using (var createRole = database.DataSource.CreateCommand(
            $"""
            CREATE ROLE {role}
                LOGIN PASSWORD '{password}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            GRANT USAGE ON SCHEMA appsurface_durable TO {role};
            GRANT SELECT ON TABLE
                appsurface_durable.store_metadata,
                appsurface_durable.runtime_heartbeat
                TO {role};
            """))
        {
            await createRole.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false,
        }.ConnectionString;
        try
        {
            await using var runtimeDataSource = NpgsqlDataSource.Create(connectionString);
            var health = new PostgreSqlDurableRuntimeHealth(
                CreateRegistration(
                    runtimeDataSource,
                    new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                    CreateOptions("runtime-health-permission-worker"),
                    Guid.NewGuid()),
                new StubSchemaManager(_ => ValueTask.FromResult(status)));

            var snapshot = await health.GetAsync();

            Assert.Equal(DurableRuntimeHealthState.Unavailable, snapshot.State);
            Assert.Equal(DurableProblemCodes.StoreUnavailable, snapshot.ProblemCode);
            Assert.False(snapshot.WasStoreObserved);
        }
        finally
        {
            await using var dropRole = database.DataSource.CreateCommand(
                $"DROP OWNED BY {role}; DROP ROLE {role};");
            await dropRole.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task GetAsync_PropagatesAnUndefinedDueHealthFunction()
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

        var exception = await Assert.ThrowsAsync<PostgresException>(
            async () => await health.GetAsync());

        Assert.Equal(PostgresErrorCodes.UndefinedFunction, exception.SqlState);
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
    public async Task GetAsync_ReportsNotStartedForNullHeartbeatAndHealthyAfterStaleHeartbeatIsRefreshed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "null-heartbeat");
        var status = await schema.GetStatusAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions("runtime-health-null-heartbeat-worker"),
                Guid.NewGuid()),
            schema);

        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));
        await health.RecordSuccessfulSweepAsync(
            new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero),
            CancellationToken.None);

        await using (var allowNull = database.DataSource.CreateCommand(
            "ALTER TABLE appsurface_durable.runtime_heartbeat ALTER COLUMN last_heartbeat_at DROP NOT NULL;"))
        {
            await allowNull.ExecuteNonQueryAsync();
        }

        await using (var nullHeartbeat = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET last_heartbeat_at = NULL
            WHERE worker_id = @worker_id;
            """))
        {
            nullHeartbeat.Parameters.AddWithValue("worker_id", "runtime-health-null-heartbeat-worker");
            Assert.Equal(1, await nullHeartbeat.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await health.GetAsync());

        await using (var staleHeartbeat = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET last_heartbeat_at = clock_timestamp() - interval '1 hour'
            WHERE worker_id = @worker_id;
            """))
        {
            staleHeartbeat.Parameters.AddWithValue("worker_id", "runtime-health-null-heartbeat-worker");
            Assert.Equal(1, await staleHeartbeat.ExecuteNonQueryAsync());
        }

        var stale = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Stale, stale.State);
        Assert.Equal(DurableProblemCodes.ActivatorStale, stale.ProblemCode);

        await using (var freshHeartbeat = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET last_heartbeat_at = clock_timestamp(), draining = false
            WHERE worker_id = @worker_id;
            """))
        {
            freshHeartbeat.Parameters.AddWithValue("worker_id", "runtime-health-null-heartbeat-worker");
            Assert.Equal(1, await freshHeartbeat.ExecuteNonQueryAsync());
        }

        var healthy = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Healthy, healthy.State);
        Assert.Null(healthy.ProblemCode);
    }

    [Fact]
    public async Task GetAsync_RejectsAPartialWorkerRow()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "partial-worker-row");
        var status = await schema.GetStatusAsync();
        const string workerId = "runtime-health-partial-worker-row";
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions(workerId),
                Guid.NewGuid()),
            schema);
        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));

        await using (var corrupt = database.DataSource.CreateCommand(
            """
            ALTER TABLE appsurface_durable.runtime_heartbeat
                ALTER COLUMN worker_instance_id DROP NOT NULL;
            UPDATE appsurface_durable.runtime_heartbeat
            SET worker_instance_id = NULL
            WHERE worker_id = @worker_id;
            """))
        {
            corrupt.Parameters.AddWithValue("worker_id", workerId);
            await corrupt.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await health.GetAsync());
        Assert.Contains("partial worker row", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_RejectsNullWorkerLifecycleFlags()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "null-lifecycle-flags");
        var status = await schema.GetStatusAsync();
        const string workerId = "runtime-health-null-lifecycle-flags";
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions(workerId),
                Guid.NewGuid()),
            schema);
        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));

        await using (var relax = database.DataSource.CreateCommand(
            """
            ALTER TABLE appsurface_durable.runtime_heartbeat
                ALTER COLUMN draining DROP NOT NULL,
                ALTER COLUMN pass_active DROP NOT NULL;
            """))
        {
            await relax.ExecuteNonQueryAsync();
        }

        await using (var nullDraining = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET draining = NULL
            WHERE worker_id = @worker_id;
            """))
        {
            nullDraining.Parameters.AddWithValue("worker_id", workerId);
            Assert.Equal(1, await nullDraining.ExecuteNonQueryAsync());
        }

        var drainingException = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await health.GetAsync());
        Assert.Contains("null lifecycle flag", drainingException.Message, StringComparison.Ordinal);

        await using (var nullPassActive = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET draining = false, pass_active = NULL
            WHERE worker_id = @worker_id;
            """))
        {
            nullPassActive.Parameters.AddWithValue("worker_id", workerId);
            Assert.Equal(1, await nullPassActive.ExecuteNonQueryAsync());
        }

        var passActiveException = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await health.GetAsync());
        Assert.Contains("null lifecycle flag", passActiveException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_RejectsEveryInvalidHostedSurfaceMask()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "invalid-hosted-surfaces");
        var status = await schema.GetStatusAsync();
        const string workerId = "runtime-health-invalid-hosted-surfaces";
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions(workerId),
                Guid.NewGuid()),
            schema);
        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));

        await using (var relax = database.DataSource.CreateCommand(
            """
            ALTER TABLE appsurface_durable.runtime_heartbeat
                DROP CONSTRAINT runtime_heartbeat_hosted_surfaces_check;
            """))
        {
            await relax.ExecuteNonQueryAsync();
        }

        foreach (var invalidMask in new short[] { 0, 8 })
        {
            await using (var corrupt = database.DataSource.CreateCommand(
                """
                UPDATE appsurface_durable.runtime_heartbeat
                SET hosted_surfaces = @hosted_surfaces
                WHERE worker_id = @worker_id;
                """))
            {
                corrupt.Parameters.AddWithValue("hosted_surfaces", invalidMask);
                corrupt.Parameters.AddWithValue("worker_id", workerId);
                Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await health.GetAsync());
            Assert.Contains("invalid hosted-surface mask", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetAsync_RejectsEveryContradictoryDueFactShape()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "contradictory-due-facts");
        var status = await schema.GetStatusAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions("runtime-health-contradictory-due-facts"),
                Guid.NewGuid()),
            schema);

        foreach (var (dueCount, oldestDueSql) in new (long DueCount, string OldestDueSql)[]
                 {
                     (-1, "NULL"),
                     (0, "statement_timestamp()"),
                     (1, "NULL"),
                     (1, "statement_timestamp() + interval '1 minute'"),
                 })
        {
            await using (var replace = database.DataSource.CreateCommand(
                $"""
                CREATE OR REPLACE FUNCTION appsurface_durable.runtime_due_dispatch_health(p_surfaces integer)
                RETURNS TABLE
                (
                    due_count bigint,
                    oldest_due_at timestamp with time zone
                )
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, appsurface_durable, pg_temp
                AS $test$
                    SELECT {dueCount}::bigint, CAST({oldestDueSql} AS timestamp with time zone);
                $test$;
                """))
            {
                await replace.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await health.GetAsync());
            Assert.Contains("contradictory due-dispatch facts", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetAsync_RejectsEmptyAndMultipleDueHealthObservations()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
                CreateOptions("runtime-health-row-shape-worker"),
                Guid.NewGuid()),
            schema);

        await using (var replace = database.DataSource.CreateCommand(
            """
            CREATE OR REPLACE FUNCTION appsurface_durable.runtime_due_dispatch_health(p_surfaces integer)
            RETURNS TABLE
            (
                due_count bigint,
                oldest_due_at timestamp with time zone
            )
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path = pg_catalog, appsurface_durable, pg_temp
            AS $test$
                SELECT 0::bigint, NULL::timestamp with time zone WHERE false;
            $test$;
            """))
        {
            await replace.ExecuteNonQueryAsync();
        }

        var empty = await Assert.ThrowsAsync<InvalidDataException>(async () => await health.GetAsync());
        Assert.Contains("returned no row", empty.Message, StringComparison.Ordinal);

        await using (var replace = database.DataSource.CreateCommand(
            """
            CREATE OR REPLACE FUNCTION appsurface_durable.runtime_due_dispatch_health(p_surfaces integer)
            RETURNS TABLE
            (
                due_count bigint,
                oldest_due_at timestamp with time zone
            )
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path = pg_catalog, appsurface_durable, pg_temp
            AS $test$
                SELECT 0::bigint, NULL::timestamp with time zone
                UNION ALL
                SELECT 0::bigint, NULL::timestamp with time zone;
            $test$;
            """))
        {
            await replace.ExecuteNonQueryAsync();
        }

        var multiple = await Assert.ThrowsAsync<InvalidDataException>(async () => await health.GetAsync());
        Assert.Contains("more than one row", multiple.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryBeginPassWithOutcomeAsync_PreservesTypedAndLegacyEpochMismatchSemantics()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var configuredEpoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(configuredEpoch, "runtime-health-tests", "typed-epoch-mismatch");
        var status = await schema.GetStatusAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(configuredEpoch, status.StoreId),
                CreateOptions("runtime-health-typed-epoch-worker"),
                Guid.NewGuid()),
            schema);

        await using (var clearEpoch = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = NULL WHERE singleton;"))
        {
            Assert.Equal(1, await clearEpoch.ExecuteNonQueryAsync());
        }

        var typed = await health.TryBeginPassWithOutcomeAsync(CancellationToken.None);
        Assert.Equal(PostgreSqlDurableStoreAdmissionKind.EpochMismatch, typed.Kind);
        Assert.NotNull(typed.LegacyException);
        Assert.StartsWith(
            DurableProblemCodes.RecoveryEpochRequired,
            typed.LegacyException!.SourceException.Message,
            StringComparison.Ordinal);

        var legacy = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await health.TryBeginPassAsync(CancellationToken.None));
        Assert.StartsWith(DurableProblemCodes.RecoveryEpochRequired, legacy.Message, StringComparison.Ordinal);

        var differentEpoch = Guid.NewGuid();
        await using (var changeEpoch = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = @epoch WHERE singleton;"))
        {
            changeEpoch.Parameters.AddWithValue("epoch", differentEpoch);
            Assert.Equal(1, await changeEpoch.ExecuteNonQueryAsync());
        }

        var mismatched = await health.TryBeginPassWithOutcomeAsync(CancellationToken.None);
        Assert.Equal(PostgreSqlDurableStoreAdmissionKind.EpochMismatch, mismatched.Kind);
        Assert.NotNull(mismatched.LegacyException);
    }

    [Fact]
    public void StoreAdmissionFactories_RejectInvalidKindsAndNullLegacyExceptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostgreSqlDurableStoreAdmission.Refused(PostgreSqlDurableStoreAdmissionKind.Admitted));
        Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlDurableStoreAdmission.WithLegacyException(
                PostgreSqlDurableStoreAdmissionKind.EpochMismatch,
                null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostgreSqlDurableStoreAdmission.WithLegacyException(
                PostgreSqlDurableStoreAdmissionKind.Draining,
                new InvalidOperationException("legacy")));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableEpochMismatchSignal(null!));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableWorkerGenerationSignal(null!));
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

    [Fact]
    public async Task GetAsync_ReportsRecoveryEpochRequiredWhenMetadataEpochDisappears()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "missing-active-epoch");
        var status = await schema.GetStatusAsync();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions("runtime-health-missing-active-epoch-worker"),
                Guid.NewGuid()),
            new StubSchemaManager(_ => ValueTask.FromResult(CreateStatus(DurableRuntimeSchemaCompatibility.Compatible))));

        await using (var clearEpoch = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = NULL WHERE singleton;"))
        {
            Assert.Equal(1, await clearEpoch.ExecuteNonQueryAsync());
        }

        var snapshot = await health.GetAsync();

        Assert.Equal(DurableRuntimeHealthState.Incompatible, snapshot.State);
        Assert.Equal(DurableProblemCodes.RecoveryEpochRequired, snapshot.ProblemCode);
        Assert.False(snapshot.EpochCompatible);
    }

    [Fact]
    public async Task GetAsync_ReportsIdentityConflictWhenOnlyHeartbeatEpochChanges()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "heartbeat-epoch-conflict");
        var status = await schema.GetStatusAsync();
        var instanceId = Guid.NewGuid();
        var workerId = "runtime-health-heartbeat-epoch-conflict-worker";
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions(workerId),
                instanceId),
            schema);

        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));
        await using (var changeEpoch = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET runtime_epoch = @runtime_epoch
            WHERE worker_id = @worker_id;
            """))
        {
            changeEpoch.Parameters.AddWithValue("runtime_epoch", Guid.NewGuid());
            changeEpoch.Parameters.AddWithValue("worker_id", workerId);
            Assert.Equal(1, await changeEpoch.ExecuteNonQueryAsync());
        }

        var snapshot = await health.GetAsync();

        Assert.Equal(DurableRuntimeHealthState.Stale, snapshot.State);
        Assert.Equal(DurableProblemCodes.WorkerIdentityConflict, snapshot.ProblemCode);
    }

    [Fact]
    public async Task TryBeginPassAsync_ReturnsFalseWhenPassActivationLosesItsUpdateRace()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "pass-activation-race");
        var status = await schema.GetStatusAsync();
        var workerId = "runtime-health-pass-activation-race-worker";

        await using (var trigger = database.DataSource.CreateCommand(
            """
            CREATE OR REPLACE FUNCTION appsurface_durable.test_runtime_health_skip_pass_activation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW.pass_active AND NOT OLD.pass_active THEN
                    RETURN NULL;
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER test_runtime_health_skip_pass_activation
            BEFORE UPDATE ON appsurface_durable.runtime_heartbeat
            FOR EACH ROW
            EXECUTE FUNCTION appsurface_durable.test_runtime_health_skip_pass_activation();
            """))
        {
            await trigger.ExecuteNonQueryAsync();
        }

        try
        {
            var health = new PostgreSqlDurableRuntimeHealth(
                CreateRegistration(
                    database.DataSource,
                    new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                    CreateOptions(workerId),
                    Guid.NewGuid()),
                schema);

            Assert.False(await health.TryBeginPassAsync(CancellationToken.None));
        }
        finally
        {
            await using var cleanup = database.DataSource.CreateCommand(
                """
                DROP TRIGGER IF EXISTS test_runtime_health_skip_pass_activation
                    ON appsurface_durable.runtime_heartbeat;
                DROP FUNCTION IF EXISTS appsurface_durable.test_runtime_health_skip_pass_activation();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RecordHeartbeatAsync_PreservesProcessingFailureWhenRollbackLosesTransport()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "rollback-transport");
        var status = await schema.GetStatusAsync();
        var workerId = "runtime-health-rollback-transport-worker";
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions(workerId),
                Guid.NewGuid()),
            schema);
        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));

        await using (var trigger = database.DataSource.CreateCommand(
            """
            CREATE OR REPLACE FUNCTION appsurface_durable.test_runtime_health_terminate_backend()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                PERFORM pg_terminate_backend(pg_backend_pid());
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER test_runtime_health_terminate_backend
            BEFORE UPDATE ON appsurface_durable.runtime_heartbeat
            FOR EACH ROW
            EXECUTE FUNCTION appsurface_durable.test_runtime_health_terminate_backend();
            """))
        {
            await trigger.ExecuteNonQueryAsync();
        }

        try
        {
            await Assert.ThrowsAnyAsync<NpgsqlException>(
                async () => await health.RecordHeartbeatAsync(CancellationToken.None));
        }
        finally
        {
            await using var cleanup = database.DataSource.CreateCommand(
                """
                DROP TRIGGER IF EXISTS test_runtime_health_terminate_backend
                    ON appsurface_durable.runtime_heartbeat;
                DROP FUNCTION IF EXISTS appsurface_durable.test_runtime_health_terminate_backend();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task GetAsync_ReportsDrainingAndIdentityConflictBeforeLiveness()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "state-order");
        var status = await schema.GetStatusAsync();
        var instanceId = Guid.NewGuid();
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions("runtime-health-state-order-worker"),
                instanceId),
            schema);

        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));
        var active = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Healthy, active.State);
        Assert.True(active.IsPassActive);

        await health.BeginDrainAsync();
        var draining = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Draining, draining.State);
        Assert.Null(draining.ProblemCode);
        Assert.True(draining.IsDraining);

        await using (var identity = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET worker_instance_id = @worker_instance_id
            WHERE worker_id = @worker_id;
            """))
        {
            identity.Parameters.AddWithValue("worker_instance_id", Guid.NewGuid());
            identity.Parameters.AddWithValue("worker_id", "runtime-health-state-order-worker");
            Assert.Equal(1, await identity.ExecuteNonQueryAsync());
        }

        var conflict = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Stale, conflict.State);
        Assert.Equal(DurableProblemCodes.WorkerIdentityConflict, conflict.ProblemCode);
    }

    [Fact]
    public async Task RuntimeMutations_RejectLostPassOwnershipAndPreserveFailedPassSemantics()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "mutation-ownership");
        var status = await schema.GetStatusAsync();
        var workerId = "runtime-health-mutation-ownership-worker";
        var health = new PostgreSqlDurableRuntimeHealth(
            CreateRegistration(
                database.DataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                CreateOptions(workerId),
                Guid.NewGuid()),
            schema);

        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));
        await health.RecordHeartbeatAsync(CancellationToken.None);
        await using (var losePass = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET pass_active = false, pass_started_at = NULL
            WHERE worker_id = @worker_id;
            """))
        {
            losePass.Parameters.AddWithValue("worker_id", workerId);
            Assert.Equal(1, await losePass.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await health.RecordFailedPassAsync(CancellationToken.None));

        Assert.True(await health.TryBeginPassAsync(CancellationToken.None));
        await using (var changeIdentity = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET worker_instance_id = @worker_instance_id
            WHERE worker_id = @worker_id;
            """))
        {
            changeIdentity.Parameters.AddWithValue("worker_instance_id", Guid.NewGuid());
            changeIdentity.Parameters.AddWithValue("worker_id", workerId);
            Assert.Equal(1, await changeIdentity.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await health.RecordSuccessfulSweepAsync(
                new DurableRuntimePumpResult(3, 2, 1, 0, 0, false, null, TimeSpan.FromMilliseconds(4)),
                CancellationToken.None));
    }

    [Fact]
    public async Task TryBeginPassAsync_ReportsMissingHeartbeatAfterRegistrationIsDiscarded()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-health-tests", "missing-heartbeat-row");
        var status = await schema.GetStatusAsync();
        var workerId = "runtime-health-missing-heartbeat-row-worker";

        await using (var trigger = database.DataSource.CreateCommand(
            """
            CREATE OR REPLACE FUNCTION appsurface_durable.test_runtime_health_skip_heartbeat_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RETURN NULL;
            END;
            $$;
            CREATE TRIGGER test_runtime_health_skip_heartbeat_insert
            BEFORE INSERT ON appsurface_durable.runtime_heartbeat
            FOR EACH ROW
            EXECUTE FUNCTION appsurface_durable.test_runtime_health_skip_heartbeat_insert();
            """))
        {
            await trigger.ExecuteNonQueryAsync();
        }

        try
        {
            var health = new PostgreSqlDurableRuntimeHealth(
                CreateRegistration(
                    database.DataSource,
                    new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                    CreateOptions(workerId),
                    Guid.NewGuid()),
                schema);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await health.TryBeginPassAsync(CancellationToken.None));
            Assert.Contains("heartbeat could not be registered", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await using var cleanup = database.DataSource.CreateCommand(
                """
                DROP TRIGGER IF EXISTS test_runtime_health_skip_heartbeat_insert
                    ON appsurface_durable.runtime_heartbeat;
                DROP FUNCTION IF EXISTS appsurface_durable.test_runtime_health_skip_heartbeat_insert();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
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
