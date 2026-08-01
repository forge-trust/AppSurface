using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

/// <summary>Real PostgreSQL coverage for the Work-first Schedule Gate A bridge.</summary>
public sealed class PostgreSqlDurableScheduleTests
{
    [Fact]
    public async Task AtWorkSchedule_CapturesOneAnchor_DeduplicatesCreate_AndMaterializesOneWork()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        var options = await CreateOptionsAsync(database.DataSource, epoch);
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-work",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-payload",
            "v1");
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            registry,
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("schedule-scope");
        var scheduleId = new DurableScheduleId("schedule-at");
        var create = new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("schedule-create"),
            "schedule-create-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec));

        var accepted = await client.CreateAsync(create);
        var duplicate = await client.CreateAsync(create);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Created, accepted.Value!.Code);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Duplicate, duplicate.Value!.Code);
        Assert.Equal(accepted.Value.CommittedAtUtc, duplicate.Value.CommittedAtUtc);
        var bounds = await ReadBoundsAsync(database.DataSource, scope, scheduleId);
        Assert.True(
            bounds.AtUtc > bounds.CursorUtc,
            $"Expected the persisted At instant {bounds.AtUtc:O} to be after its cursor {bounds.CursorUtc:O}.");
        Assert.True(
            bounds.CutoffUtc - bounds.CursorUtc < TimeSpan.FromDays(31),
            $"Expected the PostgreSQL clock {bounds.CutoffUtc:O} to remain within the default safety window of cursor {bounds.CursorUtc:O}.");
        var process = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("schedule-test", 1));

        Assert.True(
            process.ClaimedSchedules == 1 && process.RecordedOccurrences == 1 && process.MaterializedWorkTargets == 1,
            $"Expected one claimed Schedule, one occurrence, and one Work; got claimed={process.ClaimedSchedules}, occurrences={process.RecordedOccurrences}, work={process.MaterializedWorkTargets}, suspended={process.SuspendedSchedules}.");
        Assert.Equal(1, await CountAsync(database.DataSource, scope, "work"));
        Assert.Equal(1, await CountAsync(database.DataSource, scope, "schedule_occurrence"));
        var snapshot = await client.GetAsync(scope, scheduleId);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableScheduleState.Active, snapshot.Value!.State);
        Assert.Null(snapshot.Value.NextOccurrenceUtc);
    }

    [Fact]
    public async Task Create_RejectsConflictingCommandAndIdempotencyIdentitiesInsteadOfReturningAnArbitraryDuplicate()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        var options = await CreateOptionsAsync(database.DataSource, epoch);
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-command-conflict",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-command-conflict-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("schedule-command-conflict-scope");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var first = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("schedule-command-a"),
            "schedule-key-a",
            new DurableScheduleId("schedule-a"),
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));
        var second = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("schedule-command-b"),
            "schedule-key-b",
            new DurableScheduleId("schedule-b"),
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        var mixedIdentity = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("schedule-command-a"),
            "schedule-key-b",
            new DurableScheduleId("schedule-c"),
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));

        Assert.False(mixedIdentity.IsSuccess);
        Assert.Equal(DurableScheduleProblemCodes.CommandConflict, mixedIdentity.Problem!.Code);
        Assert.Equal(2, await CountAsync(database.DataSource, scope, "schedule_definition"));
    }

    [Fact]
    public async Task Create_ConcurrentSameScopeIdempotencyKey_ReturnsOneCreateAndOneCommandConflict()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        var options = await CreateOptionsAsync(database.DataSource, epoch);
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-concurrent-command",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-concurrent-command-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("schedule-concurrent-command-scope");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        await CreateScopeAsync(database.DataSource, scope);
        await using var blockingConnection = await database.DataSource.OpenConnectionAsync();
        await using var blockingTransaction = await blockingConnection.BeginTransactionAsync();
        await using (var setScope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         blockingConnection,
                         blockingTransaction))
        {
            setScope.Parameters.AddWithValue("scope_id", scope.Value);
            await setScope.ExecuteNonQueryAsync();
        }

        await using (var lockScope = new NpgsqlCommand(
                         "SELECT generation FROM appsurface_durable.scope WHERE scope_id = @scope_id FOR UPDATE;",
                         blockingConnection,
                         blockingTransaction))
        {
            lockScope.Parameters.AddWithValue("scope_id", scope.Value);
            Assert.NotNull(await lockScope.ExecuteScalarAsync());
        }

        var firstTask = client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("schedule-concurrent-command-a"),
            "shared-concurrent-key",
            new DurableScheduleId("schedule-concurrent-a"),
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target)).AsTask();
        var secondTask = client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("schedule-concurrent-command-b"),
            "shared-concurrent-key",
            new DurableScheduleId("schedule-concurrent-b"),
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target)).AsTask();
        try
        {
            await WaitForLockWaitersAsync(database.DataSource, expectedCount: 2);
            await blockingTransaction.CommitAsync();
        }
        catch
        {
            await blockingTransaction.RollbackAsync();
            throw;
        }

        var outcomes = await Task.WhenAll(firstTask, secondTask);

        var created = Assert.Single(outcomes, outcome => outcome.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Created, created.Value!.Code);
        var conflict = Assert.Single(outcomes, outcome => !outcome.IsSuccess);
        Assert.Equal(DurableScheduleProblemCodes.CommandConflict, conflict.Problem!.Code);
        Assert.Equal(1, await CountAsync(database.DataSource, scope, "schedule_definition"));
        Assert.Equal(1, await CountAsync(database.DataSource, scope, "schedule_command"));
    }

    [Fact]
    public async Task EveryQueueOne_CoalescesWhileWorkIsNonTerminal_AndRequeuesWhenWorkBecomesTerminal()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        var options = await CreateOptionsAsync(database.DataSource, epoch);
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-queue-one",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-queue-one-payload",
            "v1");
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            options,
            scheduleOptions);
        var workStore = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var scope = new DurableScopeId("queue-one-scope");
        var scheduleId = new DurableScheduleId("queue-one-schedule");
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("queue-one-create"),
            "queue-one-key",
            scheduleId,
            DurableSchedule.Every(
                TimeSpan.FromMilliseconds(250),
                DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec)));
        Assert.True(created.IsSuccess);

        await ForceQueueOneTestScheduleDueAsync(database.DataSource, scope, scheduleId, wakeDispatch: true);
        var firstPass = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("queue-one-scheduler", 1));
        Assert.Equal(1, firstPass.MaterializedWorkTargets);
        Assert.Equal(1, await CountAsync(database.DataSource, scope, "work"));

        await ForceQueueOneTestScheduleDueAsync(database.DataSource, scope, scheduleId, wakeDispatch: true);
        var secondPass = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("queue-one-scheduler", 1));
        Assert.Equal(0, secondPass.MaterializedWorkTargets);
        Assert.Equal(1, secondPass.RecordedOccurrences);
        Assert.Equal(1, await CountAsync(database.DataSource, scope, "work"));
        Assert.Equal(1, await CountOccurrenceStateAsync(database.DataSource, scope, "pending"));
        Assert.Equal(1, await CountOccurrenceStateAsync(database.DataSource, scope, "materialized"));

        var candidate = Assert.Single(await workStore.DiscoverAsync(1));
        var claim = await workStore.TryClaimAsync(candidate, "queue-one-worker");
        Assert.NotNull(claim);
        var completion = await workStore.RecordCompletionAsync(
            claim!,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.FailedTerminal, "test_terminal", "{}"));
        Assert.Equal(PostgreSqlWorkObservationOutcome.Applied, completion.Outcome);
        Assert.Equal(DurableWorkState.FailedTerminal, completion.State);

        await ForceQueueOneTestScheduleDueAsync(database.DataSource, scope, scheduleId, wakeDispatch: false);
        var terminalPass = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("queue-one-scheduler", 1));

        Assert.Equal(1, terminalPass.MaterializedWorkTargets);
        Assert.Equal(2, await CountAsync(database.DataSource, scope, "work"));
        Assert.Equal(2, await CountOccurrenceStateAsync(database.DataSource, scope, "materialized"));
        Assert.Equal(1, await CountOccurrenceStateAsync(database.DataSource, scope, "pending"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Processor_SuspendsBeforeMaterializationWhenPersistedRecoveryFencesAreStale(bool rotateRuntimeEpoch)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        var options = await CreateOptionsAsync(database.DataSource, epoch);
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-recovery-fence",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-recovery-fence-payload",
            "v1");
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var scope = new DurableScopeId($"recovery-fence-scope-{rotateRuntimeEpoch}");
        var scheduleId = new DurableScheduleId($"recovery-fence-schedule-{rotateRuntimeEpoch}");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId($"recovery-fence-create-{rotateRuntimeEpoch}"),
            $"recovery-fence-key-{rotateRuntimeEpoch}",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec)));
        Assert.True(created.IsSuccess);

        var processingOptions = options;
        var expectedSuspensionCode = DurableProblemCodes.ScopeGenerationConflict;
        if (rotateRuntimeEpoch)
        {
            var nextEpoch = Guid.NewGuid();
            await manager.RotateRuntimeEpochAsync(epoch, nextEpoch, "tests", "schedule-recovery-fence");
            processingOptions = await CreateOptionsAsync(database.DataSource, nextEpoch);
            expectedSuspensionCode = DurableProblemCodes.RecoveryEpochRequired;
        }
        else
        {
            await AdvanceScopeGenerationAsync(database.DataSource, scope);
        }

        var verificationClient = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            registry,
            processingOptions,
            scheduleOptions);
        if (rotateRuntimeEpoch)
        {
            var paused = await verificationClient.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
                DurableScheduleCommandKind.Pause,
                scope,
                new DurableCommandId("recovery-fence-pause"),
                scheduleId,
                "operator",
                "recovery",
                created.Value!.Revision));
            Assert.True(paused.IsSuccess);
            var resumed = await verificationClient.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
                DurableScheduleCommandKind.Resume,
                scope,
                new DurableCommandId("recovery-fence-resume"),
                scheduleId,
                "operator",
                "recovery",
                paused.Value!.Revision));
            Assert.True(resumed.IsSuccess);
        }

        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            processingOptions,
            scheduleOptions);
        var pass = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("recovery-fence-scheduler", 1));

        Assert.Equal(1, pass.ClaimedSchedules);
        Assert.Equal(1, pass.SuspendedSchedules);
        Assert.Equal(0, pass.RecordedOccurrences);
        Assert.Equal(0, pass.MaterializedWorkTargets);
        Assert.Equal(0, await CountAsync(database.DataSource, scope, "schedule_occurrence"));
        Assert.Equal(0, await CountAsync(database.DataSource, scope, "work"));
        Assert.Equal(expectedSuspensionCode, await ReadSuspensionCodeAsync(database.DataSource, scope, scheduleId));

        var snapshot = await verificationClient.GetAsync(scope, scheduleId);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableScheduleState.Suspended, snapshot.Value!.State);
        if (rotateRuntimeEpoch)
        {
            var release = await verificationClient.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
                DurableScheduleCommandKind.ReleaseAfterRecovery,
                scope,
                new DurableCommandId("recovery-fence-release"),
                scheduleId,
                "operator",
                "recovery",
                snapshot.Value.Revision));
            Assert.True(release.IsSuccess);
            Assert.Equal(DurableScheduleMutationCode.RecoveryReleased, release.Value!.Code);
        }
    }

    [Fact]
    public async Task Explain_UsesRequestAnchorForAfterAndImplicitEveryWithoutOpeningPostgreSql()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var client = new PostgreSqlDurableScheduleClient(
            dataSource,
            new DurableWorkRegistry([]),
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            new PostgreSqlDurableScheduleOptions("durable"));
        var anchor = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        var after = await client.ExplainNextOccurrencesAsync(new DurableScheduleExplainRequest(
            new DurableScopeId("scope"),
            new DurableScheduleId("after"),
            DurableSchedule.After(TimeSpan.FromMinutes(2)),
            anchor));
        var every = await client.ExplainNextOccurrencesAsync(new DurableScheduleExplainRequest(
            new DurableScopeId("scope"),
            new DurableScheduleId("every"),
            DurableSchedule.Every(TimeSpan.FromMinutes(1)),
            anchor,
            occurrenceCount: 2));
        var anchoredEvery = await client.ExplainNextOccurrencesAsync(new DurableScheduleExplainRequest(
            new DurableScopeId("scope"),
            new DurableScheduleId("anchored-every"),
            DurableSchedule.Every(TimeSpan.FromMinutes(1), anchor - TimeSpan.FromMinutes(2)),
            anchor,
            occurrenceCount: 2));

        Assert.True(after.IsSuccess);
        Assert.Equal(anchor + TimeSpan.FromMinutes(2), Assert.Single(after.Value!.NextOccurrencesUtc));
        Assert.True(every.IsSuccess);
        Assert.Equal([anchor + TimeSpan.FromMinutes(1), anchor + TimeSpan.FromMinutes(2)], every.Value!.NextOccurrencesUtc);
        Assert.True(anchoredEvery.IsSuccess);
        Assert.Equal(
            [anchor + TimeSpan.FromMinutes(1), anchor + TimeSpan.FromMinutes(2)],
            anchoredEvery.Value!.NextOccurrencesUtc);
    }

    [Fact]
    public void Options_RejectsScheduleDispatchLeasesLongerThanTenMinutes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostgreSqlDurableScheduleOptions("appsurface", leaseDuration: TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Client_RejectsAMismatchedRuntimeRoleBeforeSettingScheduleScope()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            new DurableWorkRegistry([]),
            options,
            new PostgreSqlDurableScheduleOptions("not-the-connected-role"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetAsync(new DurableScopeId("role-fence-scope"), new DurableScheduleId("role-fence-schedule")));

        Assert.StartsWith(DurableScheduleProblemCodes.AccessDenied, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_RejectsPoliciesThatTheWorkFirstProcessorDoesNotImplementBeforeOpeningPostgreSql()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-policy",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-policy-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            dataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            new PostgreSqlDurableScheduleOptions("durable"));
        var request = new DurableScheduleCreateRequest(
            new DurableScopeId("policy-scope"),
            new DurableCommandId("policy-command"),
            "policy-key",
            new DurableScheduleId("policy-schedule"),
            DurableSchedule.Every(TimeSpan.FromMinutes(1))
                .WithOverlap(ScheduleOverlapPolicy.Skip)
                .WithMisfire(ScheduleMisfirePolicy.CatchUp(2)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec));

        var result = await client.CreateAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleInvalid, result.Problem!.Code);
    }

    [Fact]
    public async Task LifecycleCommands_DoNotReviveDeletedSchedulesOrClearEvaluationSuspensions()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        var options = await CreateOptionsAsync(database.DataSource, epoch);
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-lifecycle",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-lifecycle-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("lifecycle-scope");
        var scheduleId = new DurableScheduleId("lifecycle-schedule");
        var create = new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("lifecycle-create"),
            "lifecycle-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec));

        var created = await client.CreateAsync(create);
        Assert.True(created.IsSuccess);
        var deleted = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Delete,
            scope,
            new DurableCommandId("lifecycle-delete"),
            scheduleId,
            "operator",
            "test",
            created.Value!.Revision));
        var resumed = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Resume,
            scope,
            new DurableCommandId("lifecycle-resume"),
            scheduleId,
            "operator",
            "test",
            deleted.Value!.Revision));

        Assert.True(deleted.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Deleted, deleted.Value!.Code);
        Assert.True(resumed.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Unchanged, resumed.Value!.Code);
        Assert.Equal(deleted.Value.Revision, resumed.Value.Revision);
        var deleteHistory = await ReadHistoryDetailsAsync(database.DataSource, scope, scheduleId, "deleted");
        Assert.Equal("operator", deleteHistory.ActorId);
        Assert.Equal("test", deleteHistory.ReasonCode);
        var deletedSnapshot = await client.GetAsync(scope, scheduleId);
        Assert.True(deletedSnapshot.IsSuccess);
        Assert.Equal(DurableScheduleState.Deleted, deletedSnapshot.Value!.State);

        await SuspendAsync(database.DataSource, scope, scheduleId, Guid.NewGuid(), DurableScheduleProblemCodes.EvaluationChanged);
        var evaluationRelease = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.ReleaseAfterRecovery,
            scope,
            new DurableCommandId("lifecycle-evaluation-release"),
            scheduleId,
            "operator",
            "test",
            deletedSnapshot.Value.Revision));

        Assert.True(evaluationRelease.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Unchanged, evaluationRelease.Value!.Code);
        var suspendedSnapshot = await client.GetAsync(scope, scheduleId);
        Assert.True(suspendedSnapshot.IsSuccess);
        Assert.Equal(DurableScheduleState.Suspended, suspendedSnapshot.Value!.State);
    }

    [Fact]
    public async Task Processor_RollsBackOccurrenceAndCursorWhenTheWorkBridgeRejectsItsDerivedIdentity()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        var options = await CreateOptionsAsync(database.DataSource, epoch);
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-rollback",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-rollback-payload",
            "v1");
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            registry,
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("rollback-scope");
        var scheduleId = new DurableScheduleId("rollback-schedule");
        var atUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
        var create = new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("rollback-create"),
            "rollback-key",
            scheduleId,
            DurableSchedule.At(atUtc),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec));

        var created = await client.CreateAsync(create);
        Assert.True(created.IsSuccess);
        var persistedAtUtc = await ReadAtUtcAsync(database.DataSource, scope, scheduleId);
        var occurrenceId = StableIdentity(
            "schedule-occurrence",
            scope.Value,
            scheduleId.Value,
            "1",
            "nominal",
            persistedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        var commandId = StableIdentity("schedule-work-command", occurrenceId);
        var idempotencyKey = StableIdentity("schedule-work-idempotency", occurrenceId);
        var workClient = new PostgreSqlDurableWorkClient(database.DataSource, registry, options);
        var conflictingWork = await workClient.EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId(commandId),
            idempotencyKey,
            contract.WorkName,
            contract.WorkVersion,
            codec.Encode(new byte[] { 9, 9 }),
            contract.ProviderSafety));
        Assert.True(conflictingWork.IsSuccess);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("rollback-test")));

        Assert.Contains(DurableProblemCodes.CommandConflict, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(database.DataSource, scope, "schedule_occurrence"));
        var snapshot = await client.GetAsync(scope, scheduleId);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(persistedAtUtc, snapshot.Value!.NextOccurrenceUtc);
    }

    [Fact]
    public async Task Processor_DiscardsAStaleDispatchLeaseWithoutMaterializingTheCurrentGeneration()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-stale-lease",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-stale-lease-payload",
            "v1");
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var scope = new DurableScopeId("stale-lease-scope");
        var scheduleId = new DurableScheduleId("stale-lease-schedule");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var schedule = DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("stale-lease-create"),
            "stale-lease-key",
            scheduleId,
            schedule,
            target));
        Assert.True(created.IsSuccess);
        var updated = await client.UpdateAsync(new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("stale-lease-update"),
            scheduleId,
            created.Value!.Revision,
            schedule,
            target));
        Assert.True(updated.IsSuccess);

        var store = new PostgreSqlDurableScheduleStore(database.DataSource, registry, options, scheduleOptions);
        var outcome = await store.ProcessClaimAsync(
            new ScheduleDispatchClaim(scope, scheduleId, created.Value.Revision),
            CancellationToken.None);

        Assert.Equal(ScheduleProcessOutcome.None, outcome);
        Assert.Equal(0, await CountAsync(database.DataSource, scope, "schedule_occurrence"));
        Assert.Equal(0, await CountAsync(database.DataSource, scope, "work"));
    }

    [Fact]
    public async Task List_ReturnsAnOpaqueCursorForTheNextBoundedPage()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-list",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-list-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("list-scope");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var schedule = DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1));
        foreach (var scheduleId in new[] { "list-schedule-a", "list-schedule-b" })
        {
            var created = await client.CreateAsync(new DurableScheduleCreateRequest(
                scope,
                new DurableCommandId($"create-{scheduleId}"),
                $"key-{scheduleId}",
                new DurableScheduleId(scheduleId),
                schedule,
                target));
            Assert.True(created.IsSuccess);
        }

        var firstPage = await client.ListAsync(new DurableScheduleListRequest(scope, pageSize: 1));
        Assert.True(firstPage.IsSuccess);
        Assert.Equal("list-schedule-a", Assert.Single(firstPage.Value!.Schedules).ScheduleId.Value);
        Assert.NotNull(firstPage.Value.ContinuationToken);
        var secondPage = await client.ListAsync(new DurableScheduleListRequest(
            scope,
            pageSize: 1,
            continuationToken: firstPage.Value.ContinuationToken));

        Assert.True(secondPage.IsSuccess);
        Assert.Equal("list-schedule-b", Assert.Single(secondPage.Value!.Schedules).ScheduleId.Value);
        Assert.Null(secondPage.Value.ContinuationToken);
    }

    private static async ValueTask<PostgreSqlDurableWorkOptions> CreateOptionsAsync(NpgsqlDataSource dataSource, Guid epoch)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var read = new NpgsqlCommand(
            "SELECT store_id, active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton;",
            connection);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var storeId = reader.GetGuid(0);
        var activeEpoch = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        await reader.DisposeAsync();
        if (activeEpoch is null)
        {
            await using var initialize = new NpgsqlCommand(
                "UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = @epoch WHERE singleton;",
                connection);
            initialize.Parameters.AddWithValue("epoch", epoch);
            Assert.Equal(1, await initialize.ExecuteNonQueryAsync());
        }
        else
        {
            Assert.Equal(epoch, activeEpoch.Value);
        }

        return new PostgreSqlDurableWorkOptions(epoch, storeId);
    }

    private static async ValueTask<long> CountAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId, string table)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM appsurface_durable.{table} WHERE scope_id = @scope_id;",
            connection,
            transaction);
        count.Parameters.AddWithValue("scope_id", scopeId.Value);
        var value = (long)(await count.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return value;
    }

    private static async ValueTask CreateScopeAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO appsurface_durable.scope (scope_id) VALUES (@scope_id);",
            connection,
            transaction);
        insert.Parameters.AddWithValue("scope_id", scopeId.Value);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask WaitForLockWaitersAsync(NpgsqlDataSource dataSource, int expectedCount)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE datname = current_database() AND state = 'active' AND wait_event_type = 'Lock';");
            if ((long)(await command.ExecuteScalarAsync())! >= expectedCount)
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException($"Expected {expectedCount} Schedule commands to wait on the scoped PostgreSQL row lock.");
    }

    // The QueueOne test creates an explicitly anchored 250 ms Every schedule. Rewinding its
    // evaluation state makes the next pass deterministic without relying on wall-clock cadence.
    private static async ValueTask ForceQueueOneTestScheduleDueAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        bool wakeDispatch)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        const string forceGenerationSql = """
            WITH forced AS (SELECT clock_timestamp() - interval '1 minute' AS anchor_utc)
            UPDATE appsurface_durable.schedule_generation AS generation
            SET anchor_utc = forced.anchor_utc
            FROM forced
            WHERE generation.scope_id = @scope_id
              AND generation.schedule_id = @schedule_id
              AND generation.schedule_kind = 'every'
              AND generation.generation =
              (
                  SELECT definition.active_generation
                  FROM appsurface_durable.schedule_definition AS definition
                  WHERE definition.scope_id = @scope_id AND definition.schedule_id = @schedule_id
              );
            """;
        await using (var forceGeneration = new NpgsqlCommand(forceGenerationSql, connection, transaction))
        {
            forceGeneration.Parameters.AddWithValue("scope_id", scopeId.Value);
            forceGeneration.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await forceGeneration.ExecuteNonQueryAsync());
        }

        const string forceDefinitionSql = """
            WITH forced AS (SELECT clock_timestamp() - interval '1 minute' AS cursor_utc)
            UPDATE appsurface_durable.schedule_definition AS definition
            SET cursor_utc = forced.cursor_utc,
                next_due_utc = forced.cursor_utc,
                updated_at = clock_timestamp()
            FROM forced
            WHERE definition.scope_id = @scope_id AND definition.schedule_id = @schedule_id;
            """;
        await using (var forceDefinition = new NpgsqlCommand(forceDefinitionSql, connection, transaction))
        {
            forceDefinition.Parameters.AddWithValue("scope_id", scopeId.Value);
            forceDefinition.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await forceDefinition.ExecuteNonQueryAsync());
        }

        if (wakeDispatch)
        {
            const string wakeDispatchSql = """
                UPDATE appsurface_durable.schedule_dispatch
                SET due_at = clock_timestamp(),
                    state = 'available',
                    lease_owner = NULL,
                    lease_expires_at = NULL,
                    updated_at = clock_timestamp()
                WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
                """;
            await using var wake = new NpgsqlCommand(wakeDispatchSql, connection, transaction);
            wake.Parameters.AddWithValue("scope_id", scopeId.Value);
            wake.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await wake.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask<long> CountOccurrenceStateAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string state)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM appsurface_durable.schedule_occurrence
            WHERE scope_id = @scope_id AND state = @state;
            """,
            connection,
            transaction);
        count.Parameters.AddWithValue("scope_id", scopeId.Value);
        count.Parameters.AddWithValue("state", state);
        var value = (long)(await count.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return value;
    }

    private static async ValueTask AdvanceScopeGenerationAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var update = new NpgsqlCommand(
            "UPDATE appsurface_durable.scope SET generation = generation + 1 WHERE scope_id = @scope_id;",
            connection,
            transaction);
        update.Parameters.AddWithValue("scope_id", scopeId.Value);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask<string> ReadSuspensionCodeAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var read = new NpgsqlCommand(
            "SELECT suspension_code FROM appsurface_durable.schedule_definition WHERE scope_id = @scope_id AND schedule_id = @schedule_id;",
            connection,
            transaction);
        read.Parameters.AddWithValue("scope_id", scopeId.Value);
        read.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        var suspensionCode = (string?)await read.ExecuteScalarAsync();
        await transaction.CommitAsync();
        Assert.NotNull(suspensionCode);
        return suspensionCode;
    }

    private static async ValueTask<(DateTimeOffset AtUtc, DateTimeOffset CursorUtc, DateTimeOffset CutoffUtc)> ReadBoundsAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT generation.at_utc, definition.cursor_utc, clock_timestamp()
            FROM appsurface_durable.schedule_definition AS definition
            JOIN appsurface_durable.schedule_generation AS generation
              ON generation.scope_id = definition.scope_id
             AND generation.schedule_id = definition.schedule_id
             AND generation.generation = definition.active_generation
            WHERE definition.scope_id = @scope_id AND definition.schedule_id = @schedule_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (
            reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2));
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask<DateTimeOffset> ReadAtUtcAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT at_utc FROM appsurface_durable.schedule_generation WHERE scope_id = @scope_id AND schedule_id = @schedule_id AND generation = 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        DateTimeOffset result;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            result = reader.GetFieldValue<DateTimeOffset>(0);
        }

        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask SuspendAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        Guid oldEpoch,
        string suspensionCode)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var update = new NpgsqlCommand(
            "UPDATE appsurface_durable.schedule_definition SET state = 'suspended', runtime_epoch = @runtime_epoch, suspension_code = @suspension_code WHERE scope_id = @scope_id AND schedule_id = @schedule_id;",
            connection,
            transaction);
        update.Parameters.AddWithValue("runtime_epoch", oldEpoch);
        update.Parameters.AddWithValue("suspension_code", suspensionCode);
        update.Parameters.AddWithValue("scope_id", scopeId.Value);
        update.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask<(string ActorId, string ReasonCode)> ReadHistoryDetailsAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        string eventType)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
                         "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                         connection,
                         transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT details ->> 'actor_id', details ->> 'reason_code' FROM appsurface_durable.schedule_history WHERE scope_id = @scope_id AND schedule_id = @schedule_id AND event_type = @event_type ORDER BY observed_at DESC, event_id DESC LIMIT 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("event_type", eventType);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (reader.GetString(0), reader.GetString(1));
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static string StableIdentity(string prefix, params string[] values) =>
        $"{prefix}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values))))}";

    private sealed class SchedulePayloadCodec(string contractName, string contractVersion) : IDurablePayloadCodec<byte[]>
    {
        public Type PayloadType => typeof(byte[]);

        public string ContractName { get; } = contractName;

        public string ContractVersion { get; } = contractVersion;

        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(byte[] value) => new(
            ContractName,
            ContractVersion,
            Classification,
            value,
            RetentionPolicyId);

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<byte[]>(value));

        public byte[] Decode(DurableEncodedPayload payload) => (byte[])DecodeObject(payload);

        public object DecodeObject(DurableEncodedPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.ContractName != ContractName || payload.ContractVersion != ContractVersion)
            {
                throw new InvalidOperationException("Schedule test payload does not match its registration.");
            }

            return payload.Content.ToArray();
        }
    }
}
