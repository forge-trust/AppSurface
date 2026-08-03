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
    public async Task Processor_CancelsBlockedDispatchClaim_AndCanRunAgain()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            new DurableWorkRegistry([]),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand(
                         "LOCK TABLE appsurface_durable.schedule_dispatch IN ACCESS EXCLUSIVE MODE;",
                         blocker,
                         blockerTransaction))
        {
            await lockTable.ExecuteNonQueryAsync();
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await processor.ProcessDueAsync(
                new PostgreSqlDurableScheduleProcessRequest("blocked-schedule-processor"),
                cancellation.Token));

        await blockerTransaction.RollbackAsync();
        Assert.Equal(
            new PostgreSqlDurableScheduleProcessResult(0, 0, 0, 0),
            await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("recovered-schedule-processor")));
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

        var duplicateSchedule = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("schedule-command-c"),
            "schedule-key-c",
            new DurableScheduleId("schedule-a"),
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));

        Assert.False(duplicateSchedule.IsSuccess);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleInvalid, duplicateSchedule.Problem!.Code);
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
            try
            {
                await Task.WhenAll(firstTask, secondTask);
            }
            catch
            {
                // Preserve the lock-observation failure after both commands have observed the rollback.
            }

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

        await ForceQueueOneTestScheduleDueAsync(database.DataSource, scope, scheduleId, wakeDispatch: true);
        var extendedPass = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("queue-one-scheduler", 1));
        Assert.Equal(0, extendedPass.MaterializedWorkTargets);
        Assert.Equal(0, extendedPass.RecordedOccurrences);
        Assert.Equal(1, await CountOccurrenceStateAsync(database.DataSource, scope, "pending"));

        var candidate = Assert.Single(await workStore.DiscoverAsync(1));
        var claim = await workStore.TryClaimAsync(candidate, "queue-one-worker");
        Assert.NotNull(claim);
        await SetScheduleDispatchLeaseAsync(database.DataSource, scope, scheduleId);
        var completion = await workStore.RecordCompletionAsync(
            claim!,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.FailedTerminal, "test_terminal", "{}"));
        Assert.Equal(PostgreSqlWorkObservationOutcome.Applied, completion.Outcome);
        Assert.Equal(DurableWorkState.FailedTerminal, completion.State);
        Assert.Equal("leased", await ReadScheduleDispatchStateAsync(database.DataSource, scope, scheduleId));

        await ForceQueueOneTestScheduleDueAsync(database.DataSource, scope, scheduleId, wakeDispatch: true);
        var terminalPass = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("queue-one-scheduler", 1));

        Assert.Equal(1, terminalPass.MaterializedWorkTargets);
        Assert.Equal(2, await CountAsync(database.DataSource, scope, "work"));
        Assert.Equal(2, await CountOccurrenceStateAsync(database.DataSource, scope, "materialized"));
        Assert.Equal(1, await CountOccurrenceStateAsync(database.DataSource, scope, "pending"));

        var exhaustedWork = Assert.Single(await workStore.DiscoverAsync(1));
        await ForceQueueOneWorkRetryExhaustionAsync(database.DataSource, scope, exhaustedWork.WorkId, scheduleId);
        Assert.Null(await workStore.TryClaimAsync(
            Assert.Single(await workStore.DiscoverAsync(1)),
            "queue-one-exhaustion-worker"));
        Assert.Equal("available", await ReadScheduleDispatchStateAsync(database.DataSource, scope, scheduleId));

        var requeuedPass = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("queue-one-scheduler", 1));
        Assert.Equal(1, requeuedPass.MaterializedWorkTargets);
        Assert.Equal(3, await CountAsync(database.DataSource, scope, "work"));
        Assert.Equal(3, await CountOccurrenceStateAsync(database.DataSource, scope, "materialized"));
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
    public async Task Client_RollsBackMutationsAndListsWhenTheRuntimeRoleIsWrong()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-role-rollback",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-role-rollback-payload",
            "v1");
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("not-the-connected-role"));
        var scope = new DurableScopeId("role-rollback-scope");
        var scheduleId = new DurableScheduleId("role-rollback-schedule");
        var target = DurableScheduleTarget.Work(
            contract.WorkName,
            contract.WorkVersion,
            new byte[] { 4, 2 },
            new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion));

        var create = new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("role-rollback-create"),
            "role-rollback-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target);
        var update = new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("role-rollback-update"),
            scheduleId,
            expectedRevision: 1,
            DurableSchedule.After(TimeSpan.FromMinutes(1)),
            target);
        var lifecycle = new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("role-rollback-pause"),
            scheduleId,
            "operator",
            "test",
            expectedRevision: 1);

        foreach (var operation in new Func<Task>[]
                 {
                     async () => { await client.CreateAsync(create); },
                     async () => { await client.UpdateAsync(update); },
                     async () => { await client.ApplyLifecycleCommandAsync(lifecycle); },
                     async () => { await client.ListAsync(new DurableScheduleListRequest(scope)); },
                 })
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(operation);
            Assert.StartsWith(DurableScheduleProblemCodes.AccessDenied, exception.Message, StringComparison.Ordinal);
        }
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

        var suspendedScheduleId = new DurableScheduleId("lifecycle-suspended-schedule");
        var suspendedCreated = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("lifecycle-suspended-create"),
            "lifecycle-suspended-key",
            suspendedScheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec)));
        Assert.True(suspendedCreated.IsSuccess);
        await SuspendAsync(database.DataSource, scope, suspendedScheduleId, Guid.NewGuid(), DurableScheduleProblemCodes.EvaluationChanged);
        var suspendedUpdate = await client.UpdateAsync(new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("lifecycle-suspended-update"),
            suspendedScheduleId,
            suspendedCreated.Value!.Revision,
            DurableSchedule.After(TimeSpan.FromMinutes(5)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec)));
        var evaluationRelease = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.ReleaseAfterRecovery,
            scope,
            new DurableCommandId("lifecycle-evaluation-release"),
            suspendedScheduleId,
            "operator",
            "test",
            suspendedCreated.Value.Revision));

        Assert.False(suspendedUpdate.IsSuccess);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleInvalid, suspendedUpdate.Problem!.Code);
        Assert.True(evaluationRelease.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Unchanged, evaluationRelease.Value!.Code);
        var suspendedSnapshot = await client.GetAsync(scope, suspendedScheduleId);
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

    [Fact]
    public async Task Create_RejectsDeferredCronFlowAndInvalidWorkTargetsBeforeOpeningPostgreSql()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=5432;Database=durable_contracts;Username=durable");
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-validation",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-validation-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            dataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            new PostgreSqlDurableScheduleOptions("durable"));
        var scope = new DurableScopeId("validation-scope");
        var workTarget = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);

        var cron = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("validation-cron"),
            "validation-cron-key",
            new DurableScheduleId("validation-cron"),
            DurableSchedule.Cron("* * * * *", "Etc/UTC"),
            workTarget));
        var flow = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("validation-flow"),
            "validation-flow-key",
            new DurableScheduleId("validation-flow"),
            DurableSchedule.At(DateTimeOffset.UtcNow),
            DurableScheduleTarget.Flow("tests.schedule-flow", "v1", new byte[] { 4, 2 }, codec)));
        var unknownWork = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("validation-unknown-work"),
            "validation-unknown-work-key",
            new DurableScheduleId("validation-unknown-work"),
            DurableSchedule.At(DateTimeOffset.UtcNow),
            DurableScheduleTarget.Work("tests.schedule-unknown", "v1", new byte[] { 4, 2 }, codec)));
        var mismatchedPayload = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("validation-mismatched-payload"),
            "validation-mismatched-payload-key",
            new DurableScheduleId("validation-mismatched-payload"),
            DurableSchedule.At(DateTimeOffset.UtcNow),
            DurableScheduleTarget.Work(
                contract.WorkName,
                contract.WorkVersion,
                new byte[] { 4, 2 },
                new SchedulePayloadCodec("tests.schedule-other-payload", "v1"))));

        Assert.Equal(DurableScheduleProblemCodes.DialectUnsupported, cron.Problem!.Code);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleInvalid, flow.Problem!.Code);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleInvalid, unknownWork.Problem!.Code);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleInvalid, mismatchedPayload.Problem!.Code);
    }

    [Fact]
    public async Task GetAndUpdate_ReturnNotFoundRevisionConflictAndDuplicateResults()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-update-results",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-update-results-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("update-results-scope");
        var scheduleId = new DurableScheduleId("update-results-schedule");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);

        var missing = await client.GetAsync(scope, scheduleId);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleNotFound, missing.Problem!.Code);

        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("update-results-create"),
            "update-results-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));
        Assert.True(created.IsSuccess);

        var missingUpdate = await client.UpdateAsync(new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("update-results-missing"),
            new DurableScheduleId("update-results-missing"),
            expectedRevision: 1,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));
        var staleUpdate = await client.UpdateAsync(new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("update-results-stale"),
            scheduleId,
            expectedRevision: created.Value!.Revision + 1,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(2)),
            target));
        var updateRequest = new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("update-results-update"),
            scheduleId,
            created.Value.Revision,
            DurableSchedule.After(TimeSpan.FromMinutes(5)),
            target,
            "updated schedule");
        var updated = await client.UpdateAsync(updateRequest);
        var duplicate = await client.UpdateAsync(updateRequest);

        Assert.Equal(DurableScheduleProblemCodes.ScheduleNotFound, missingUpdate.Problem!.Code);
        Assert.Equal(DurableScheduleProblemCodes.RevisionConflict, staleUpdate.Problem!.Code);
        Assert.Equal(DurableScheduleMutationCode.Updated, updated.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Duplicate, duplicate.Value!.Code);
        Assert.Equal(updated.Value.Revision, duplicate.Value.Revision);

        var paused = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("update-results-pause"),
            scheduleId,
            "operator",
            "test",
            updated.Value.Revision));
        Assert.True(paused.IsSuccess);
        var pausedUpdate = await client.UpdateAsync(new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("update-results-while-paused"),
            scheduleId,
            paused.Value!.Revision,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(2)),
            target));
        Assert.True(pausedUpdate.IsSuccess);
        Assert.Equal(DurableScheduleState.Paused, (await client.GetAsync(scope, scheduleId)).Value!.State);

        var deleted = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Delete,
            scope,
            new DurableCommandId("update-results-delete"),
            scheduleId,
            "operator",
            "test",
            pausedUpdate.Value!.Revision));
        Assert.True(deleted.IsSuccess);
        var updateDeleted = await client.UpdateAsync(new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("update-results-after-delete"),
            scheduleId,
            deleted.Value!.Revision,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(3)),
            target));

        Assert.Equal(DurableScheduleProblemCodes.ScheduleInvalid, updateDeleted.Problem!.Code);
        var deletedSnapshot = await client.GetAsync(scope, scheduleId);
        Assert.Equal(DurableScheduleState.Deleted, deletedSnapshot.Value!.State);
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, DurableDataClassification.Operational)]
    [InlineData(DurableProviderSafety.ProviderKeyed, DurableDataClassification.ApprovedApplication)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, DurableDataClassification.ApprovedApplication)]
    [InlineData(DurableProviderSafety.ManualResolution, DurableDataClassification.ApprovedApplication)]
    public async Task Get_RoundTripsWorkProviderSafetyAndPayloadClassification(
        DurableProviderSafety providerSafety,
        DurableDataClassification classification)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var suffix = providerSafety.ToString().ToLowerInvariant();
        var contract = new PostgreSqlTestWorkContract(
            $"tests.schedule-roundtrip-{suffix}",
            "v1",
            providerSafety,
            $"tests.schedule-roundtrip-{suffix}-payload",
            "v1",
            classification);
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion, classification);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId($"roundtrip-{suffix}-scope");
        var scheduleId = new DurableScheduleId($"roundtrip-{suffix}-schedule");
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId($"roundtrip-{suffix}-create"),
            $"roundtrip-{suffix}-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec)));

        Assert.True(created.IsSuccess);
        var snapshot = await client.GetAsync(scope, scheduleId);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(providerSafety, snapshot.Value!.Target.ProviderSafety);
        Assert.Equal(classification, snapshot.Value.Target.Input.Classification);
    }

    [Fact]
    public async Task Processor_ReclaimsExpiredDispatchLeasesAndSuspendsClockAnomalies()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        var options = await CreateOptionsAsync(database.DataSource, epoch);
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-lease-clock",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-lease-clock-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var scope = new DurableScopeId("lease-clock-scope");
        var leaseScheduleId = new DurableScheduleId("expired-lease-schedule");
        var anomalyScheduleId = new DurableScheduleId("clock-anomaly-schedule");
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            registry,
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var leaseCreated = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("expired-lease-create"),
            "expired-lease-key",
            leaseScheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            target));
        Assert.True(leaseCreated.IsSuccess);
        await SetExpiredDispatchLeaseAsync(database.DataSource, scope, leaseScheduleId);

        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var reclaimed = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("lease-reclaimer", 1));
        Assert.Equal(1, reclaimed.ClaimedSchedules);
        Assert.Equal(1, reclaimed.MaterializedWorkTargets);

        var anomalyCreated = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("clock-anomaly-create"),
            "clock-anomaly-key",
            anomalyScheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            target));
        Assert.True(anomalyCreated.IsSuccess);
        var strictProcessor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            options,
            new PostgreSqlDurableScheduleOptions("appsurface", maximumClockAdvance: TimeSpan.FromTicks(1)));
        var anomaly = await strictProcessor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("clock-fence", 1));

        Assert.Equal(1, anomaly.ClaimedSchedules);
        Assert.Equal(1, anomaly.SuspendedSchedules);
        Assert.Equal(DurableScheduleProblemCodes.EvaluationChanged, await ReadSuspensionCodeAsync(database.DataSource, scope, anomalyScheduleId));
    }

    [Fact]
    public async Task Update_SupersedesPendingQueueOneOccurrencesFromThePriorGeneration()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-update-coalesced",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-update-coalesced-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            options,
            scheduleOptions);
        var scope = new DurableScopeId("update-coalesced-scope");
        var scheduleId = new DurableScheduleId("update-coalesced-schedule");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("update-coalesced-create"),
            "update-coalesced-key",
            scheduleId,
            DurableSchedule.Every(TimeSpan.FromMilliseconds(250), DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2)),
            target));
        Assert.True(created.IsSuccess);

        await ForceQueueOneTestScheduleDueAsync(database.DataSource, scope, scheduleId, wakeDispatch: true);
        Assert.Equal(1, (await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("update-coalesced", 1))).MaterializedWorkTargets);
        await ForceQueueOneTestScheduleDueAsync(database.DataSource, scope, scheduleId, wakeDispatch: true);
        Assert.Equal(1, (await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("update-coalesced", 1))).RecordedOccurrences);
        Assert.Equal(1, await CountOccurrenceStateAsync(database.DataSource, scope, "pending"));

        var updated = await client.UpdateAsync(new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("update-coalesced-update"),
            scheduleId,
            created.Value!.Revision + 2,
            DurableSchedule.After(TimeSpan.FromMinutes(5)),
            target));

        Assert.True(updated.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Updated, updated.Value!.Code);
        Assert.Equal(2, updated.Value.Generation);
        Assert.Equal(1, await CountOccurrenceStateAsync(database.DataSource, scope, "superseded"));
    }

    [Fact]
    public async Task List_FiltersByLifecycleStateAndRecoveryFence()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-state-list",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-state-list-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("state-list-scope");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var identifiers = new Dictionary<DurableScheduleState, DurableScheduleId>();
        foreach (var (state, value) in new[]
                 {
                     (DurableScheduleState.Active, "state-list-active"),
                     (DurableScheduleState.Paused, "state-list-paused"),
                     (DurableScheduleState.Deleted, "state-list-deleted"),
                     (DurableScheduleState.Suspended, "state-list-suspended"),
                 })
        {
            var scheduleId = new DurableScheduleId(value);
            var created = await client.CreateAsync(new DurableScheduleCreateRequest(
                scope,
                new DurableCommandId($"{value}-create"),
                $"{value}-key",
                scheduleId,
                DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
                target));
            Assert.True(created.IsSuccess);
            identifiers.Add(state, scheduleId);
        }

        var paused = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("state-list-pause"),
            identifiers[DurableScheduleState.Paused],
            "operator",
            "test",
            expectedRevision: 1));
        var deleted = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Delete,
            scope,
            new DurableCommandId("state-list-delete"),
            identifiers[DurableScheduleState.Deleted],
            "operator",
            "test",
            expectedRevision: 1));
        Assert.True(paused.IsSuccess);
        Assert.True(deleted.IsSuccess);
        await SuspendAsync(
            database.DataSource,
            scope,
            identifiers[DurableScheduleState.Suspended],
            Guid.NewGuid(),
            DurableProblemCodes.RecoveryEpochRequired);

        foreach (var (state, scheduleId) in identifiers)
        {
            var result = await client.ListAsync(new DurableScheduleListRequest(scope, state: state));
            Assert.True(result.IsSuccess);
            Assert.Equal(scheduleId, Assert.Single(result.Value!.Schedules).ScheduleId);
        }

        var releaseRequired = await client.ListAsync(new DurableScheduleListRequest(scope, requiresRecoveryRelease: true));
        Assert.True(releaseRequired.IsSuccess);
        Assert.Equal(identifiers[DurableScheduleState.Suspended], Assert.Single(releaseRequired.Value!.Schedules).ScheduleId);
        var releaseNotRequired = await client.ListAsync(new DurableScheduleListRequest(scope, requiresRecoveryRelease: false));
        Assert.True(releaseNotRequired.IsSuccess);
        Assert.Equal(3, releaseNotRequired.Value!.Schedules.Count);
    }

    [Fact]
    public async Task Processor_HandlesEmptyNoDuePausedDeletedAndMissingClaims()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-claim-state",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-claim-state-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            options,
            scheduleOptions);
        Assert.Equal(
            new PostgreSqlDurableScheduleProcessResult(0, 0, 0, 0),
            await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("empty-schedule-pass", 1)));

        var scope = new DurableScopeId("claim-state-scope");
        var scheduleId = new DurableScheduleId("claim-state-schedule");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("claim-state-create"),
            "claim-state-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));
        Assert.True(created.IsSuccess);

        var store = new PostgreSqlDurableScheduleStore(database.DataSource, registry, options, scheduleOptions);
        Assert.Equal(
            ScheduleProcessOutcome.None,
            await store.ProcessClaimAsync(new ScheduleDispatchClaim(scope, scheduleId, created.Value!.Revision), CancellationToken.None));
        var paused = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("claim-state-pause"),
            scheduleId,
            "operator",
            "test",
            created.Value.Revision));
        Assert.True(paused.IsSuccess);
        Assert.Equal(
            ScheduleProcessOutcome.None,
            await store.ProcessClaimAsync(new ScheduleDispatchClaim(scope, scheduleId, paused.Value!.Revision), CancellationToken.None));
        var deleted = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Delete,
            scope,
            new DurableCommandId("claim-state-delete"),
            scheduleId,
            "operator",
            "test",
            paused.Value.Revision));
        Assert.True(deleted.IsSuccess);
        Assert.Equal(
            ScheduleProcessOutcome.None,
            await store.ProcessClaimAsync(new ScheduleDispatchClaim(scope, scheduleId, deleted.Value!.Revision), CancellationToken.None));
        Assert.Equal(
            ScheduleProcessOutcome.None,
            await store.ProcessClaimAsync(
                new ScheduleDispatchClaim(scope, new DurableScheduleId("claim-state-missing"), 1),
                CancellationToken.None));
    }

    [Fact]
    public async Task Processor_SuspendsPersistedFlowTargetsBeforeWorkMaterialization()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-persisted-flow",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-persisted-flow-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var scope = new DurableScopeId("persisted-flow-scope");
        var scheduleId = new DurableScheduleId("persisted-flow-schedule");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("persisted-flow-create"),
            "persisted-flow-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec)));
        Assert.True(created.IsSuccess);
        await SetPersistedTargetKindAsync(database.DataSource, scope, scheduleId, "flow");

        var store = new PostgreSqlDurableScheduleStore(database.DataSource, registry, options, scheduleOptions);
        var result = await store.ProcessClaimAsync(
            new ScheduleDispatchClaim(scope, scheduleId, created.Value!.Revision),
            CancellationToken.None);

        Assert.Equal(ScheduleProcessOutcome.Suspended, result);
        Assert.Equal(DurableScheduleProblemCodes.DialectUnsupported, await ReadSuspensionCodeAsync(database.DataSource, scope, scheduleId));
        Assert.Equal(3, await CountAsync(database.DataSource, scope, "schedule_history"));
        Assert.True(await HasHistoryEventAsync(database.DataSource, scope, scheduleId, "unsupported-target-suspended"));
        Assert.Equal(1, await CountAsync(database.DataSource, scope, "schedule_occurrence"));
        Assert.Equal(0, await CountAsync(database.DataSource, scope, "work"));
    }

    [Fact]
    public async Task DisabledScope_RejectsEveryClientMutationAndSuspendsAClaim()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-disabled-scope",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-disabled-scope-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var scope = new DurableScopeId("disabled-schedule-scope");
        var scheduleId = new DurableScheduleId("disabled-schedule");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("disabled-schedule-create"),
            "disabled-schedule-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));
        Assert.True(created.IsSuccess);
        await DisableScopeAsync(database.DataSource, scope);

        var create = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("disabled-schedule-create-again"),
            "disabled-schedule-key-again",
            new DurableScheduleId("disabled-schedule-new"),
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));
        var update = await client.UpdateAsync(new DurableScheduleUpdateRequest(
            scope,
            new DurableCommandId("disabled-schedule-update"),
            scheduleId,
            created.Value!.Revision,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(2)),
            target));
        var lifecycle = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("disabled-schedule-pause"),
            scheduleId,
            "operator",
            "test",
            created.Value.Revision));
        var get = await client.GetAsync(scope, scheduleId);
        var list = await client.ListAsync(new DurableScheduleListRequest(scope));
        var store = new PostgreSqlDurableScheduleStore(database.DataSource, registry, options, scheduleOptions);
        var claim = await store.ProcessClaimAsync(
            new ScheduleDispatchClaim(scope, scheduleId, created.Value.Revision),
            CancellationToken.None);

        Assert.Equal(DurableScheduleProblemCodes.AccessDenied, create.Problem!.Code);
        Assert.Equal(DurableScheduleProblemCodes.AccessDenied, update.Problem!.Code);
        Assert.Equal(DurableScheduleProblemCodes.AccessDenied, lifecycle.Problem!.Code);
        Assert.Equal(DurableScheduleProblemCodes.ScheduleNotFound, get.Problem!.Code);
        Assert.Empty(list.Value!.Schedules);
        Assert.Equal(ScheduleProcessOutcome.Suspended, claim);
    }

    [Fact]
    public async Task LifecycleCommands_ReturnNotFoundConflictsDuplicatesAndNoOpOutcomes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-lifecycle-outcomes",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-lifecycle-outcomes-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("lifecycle-outcomes-scope");
        var scheduleId = new DurableScheduleId("lifecycle-outcomes-schedule");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("lifecycle-outcomes-create"),
            "lifecycle-outcomes-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            target));
        Assert.True(created.IsSuccess);

        var missing = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("lifecycle-outcomes-missing"),
            new DurableScheduleId("lifecycle-outcomes-missing"),
            "operator",
            "test",
            expectedRevision: 1));
        var stale = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("lifecycle-outcomes-stale"),
            scheduleId,
            "operator",
            "test",
            expectedRevision: 2));
        var pauseRequest = new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("lifecycle-outcomes-pause"),
            scheduleId,
            "operator",
            "test",
            created.Value!.Revision);
        var paused = await client.ApplyLifecycleCommandAsync(pauseRequest);
        var duplicate = await client.ApplyLifecycleCommandAsync(pauseRequest);
        var commandConflict = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            pauseRequest.CommandId,
            scheduleId,
            "other-operator",
            "test",
            created.Value.Revision));
        var pauseAgain = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Pause,
            scope,
            new DurableCommandId("lifecycle-outcomes-pause-again"),
            scheduleId,
            "operator",
            "test",
            paused.Value!.Revision));
        var resumed = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Resume,
            scope,
            new DurableCommandId("lifecycle-outcomes-resume"),
            scheduleId,
            "operator",
            "test",
            paused.Value.Revision));
        var resumeAgain = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Resume,
            scope,
            new DurableCommandId("lifecycle-outcomes-resume-again"),
            scheduleId,
            "operator",
            "test",
            resumed.Value!.Revision));
        var deleted = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Delete,
            scope,
            new DurableCommandId("lifecycle-outcomes-delete"),
            scheduleId,
            "operator",
            "test",
            resumed.Value.Revision));
        var deleteAgain = await client.ApplyLifecycleCommandAsync(new DurableScheduleCommand(
            DurableScheduleCommandKind.Delete,
            scope,
            new DurableCommandId("lifecycle-outcomes-delete-again"),
            scheduleId,
            "operator",
            "test",
            deleted.Value!.Revision));

        Assert.Equal(DurableScheduleProblemCodes.ScheduleNotFound, missing.Problem!.Code);
        Assert.Equal(DurableScheduleProblemCodes.RevisionConflict, stale.Problem!.Code);
        Assert.Equal(DurableScheduleMutationCode.Paused, paused.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Duplicate, duplicate.Value!.Code);
        Assert.Equal(DurableScheduleProblemCodes.CommandConflict, commandConflict.Problem!.Code);
        Assert.Equal(DurableScheduleMutationCode.Unchanged, pauseAgain.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Resumed, resumed.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Unchanged, resumeAgain.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Deleted, deleted.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Unchanged, deleteAgain.Value!.Code);
    }

    [Fact]
    public async Task Get_ReadsPersistedLegacyPoliciesAndCronDefinitionsWithoutAdmittingNewOnes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-persisted-definitions",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-persisted-definitions-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var client = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateRegistry(contract),
            options,
            new PostgreSqlDurableScheduleOptions("appsurface"));
        var scope = new DurableScopeId("persisted-definitions-scope");
        var target = DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec);
        foreach (var scheduleId in new[] { "persisted-skip", "persisted-concurrent", "persisted-cron", "persisted-cron-standard", "persisted-flow" })
        {
            var created = await client.CreateAsync(new DurableScheduleCreateRequest(
                scope,
                new DurableCommandId($"{scheduleId}-create"),
                $"{scheduleId}-key",
                new DurableScheduleId(scheduleId),
                DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
                target));
            Assert.True(created.IsSuccess);
        }

        await SetPersistedScheduleDefinitionsAsync(database.DataSource, scope);
        var skip = await client.GetAsync(scope, new DurableScheduleId("persisted-skip"));
        var concurrent = await client.GetAsync(scope, new DurableScheduleId("persisted-concurrent"));
        var cron = await client.GetAsync(scope, new DurableScheduleId("persisted-cron"));
        var standardCron = await client.GetAsync(scope, new DurableScheduleId("persisted-cron-standard"));
        var flow = await client.GetAsync(scope, new DurableScheduleId("persisted-flow"));

        Assert.Equal(ScheduleOverlapPolicy.Skip, skip.Value!.Schedule.OverlapPolicy);
        Assert.Equal(ScheduleMisfirePolicy.Skip, skip.Value.Schedule.MisfirePolicy);
        Assert.Equal(ScheduleOverlapPolicyKind.AllowConcurrent, concurrent.Value!.Schedule.OverlapPolicy.Kind);
        Assert.Equal(2, concurrent.Value.Schedule.OverlapPolicy.MaximumConcurrentRuns);
        Assert.Equal(ScheduleMisfirePolicyKind.CatchUp, concurrent.Value.Schedule.MisfirePolicy.Kind);
        Assert.Equal(2, concurrent.Value.Schedule.MisfirePolicy.MaximumOccurrences);
        var persistedCron = Assert.IsType<DurableCronSchedule>(cron.Value!.Schedule);
        Assert.Equal("* * * * * *", persistedCron.Expression);
        Assert.Equal("Etc/UTC", persistedCron.IanaTimeZoneId);
        Assert.Equal(CronGrammar.IncludeSeconds, persistedCron.Grammar);
        Assert.Equal(CronGrammar.Standard, Assert.IsType<DurableCronSchedule>(standardCron.Value!.Schedule).Grammar);
        Assert.Equal(DurableScheduleTargetKind.Flow, flow.Value!.Target.Kind);
        Assert.Null(flow.Value.Target.ProviderSafety);
    }

    [Fact]
    public async Task Processor_EvaluatesPersistedAfterSchedulesAndExplainCoversPastAtAndCron()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-after-evaluation",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-after-evaluation-payload",
            "v1");
        var codec = new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion);
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var scope = new DurableScopeId("after-evaluation-scope");
        var scheduleId = new DurableScheduleId("after-evaluation-schedule");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("after-evaluation-create"),
            "after-evaluation-key",
            scheduleId,
            DurableSchedule.After(TimeSpan.FromMinutes(1)),
            DurableScheduleTarget.Work(contract.WorkName, contract.WorkVersion, new byte[] { 4, 2 }, codec)));
        Assert.True(created.IsSuccess);
        await ForceAfterScheduleDueAsync(database.DataSource, scope, scheduleId);
        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource,
            database.DataSource,
            registry,
            options,
            scheduleOptions);
        var processed = await processor.ProcessDueAsync(new PostgreSqlDurableScheduleProcessRequest("after-evaluation", 1));

        Assert.Equal(1, processed.RecordedOccurrences);
        Assert.Equal(1, processed.MaterializedWorkTargets);
        var anchor = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var pastAt = await client.ExplainNextOccurrencesAsync(new DurableScheduleExplainRequest(
            scope,
            new DurableScheduleId("past-at"),
            DurableSchedule.At(anchor - TimeSpan.FromMinutes(1)),
            anchor));
        var cron = await client.ExplainNextOccurrencesAsync(new DurableScheduleExplainRequest(
            scope,
            new DurableScheduleId("cron"),
            DurableSchedule.Cron("* * * * *", "Etc/UTC"),
            anchor));

        Assert.Empty(pastAt.Value!.NextOccurrencesUtc);
        Assert.Equal("At is earlier than the requested anchor, so no future occurrence remains.", Assert.Single(pastAt.Value!.Notes));
        Assert.Equal(DurableScheduleProblemCodes.DialectUnsupported, cron.Problem!.Code);
    }

    [Fact]
    public async Task Processor_LeavesFutureAfterAndEverySchedulesAvailableWithoutRecordingOccurrences()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var contract = new PostgreSqlTestWorkContract(
            "tests.schedule-no-due",
            "v1",
            DurableProviderSafety.Idempotent,
            "tests.schedule-no-due-payload",
            "v1");
        var registry = PostgreSqlTestWorkContracts.CreateRegistry(contract);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var store = new PostgreSqlDurableScheduleStore(database.DataSource, registry, options, scheduleOptions);
        var scope = new DurableScopeId("no-due-scope");
        var afterId = new DurableScheduleId("no-due-after");
        var everyId = new DurableScheduleId("no-due-every");
        var target = DurableScheduleTarget.Work(
            contract.WorkName,
            contract.WorkVersion,
            new byte[] { 4, 2 },
            new SchedulePayloadCodec(contract.ContractName, contract.ContractVersion));
        var after = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("no-due-after-create"),
            "no-due-after-key",
            afterId,
            DurableSchedule.After(TimeSpan.FromDays(1)),
            target));
        var every = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("no-due-every-create"),
            "no-due-every-key",
            everyId,
            DurableSchedule.Every(TimeSpan.FromDays(1)),
            target));
        Assert.True(after.IsSuccess);
        Assert.True(every.IsSuccess);

        await ForceScheduleEvaluationWindowAsync(database.DataSource, scope, afterId);
        await ForceScheduleEvaluationWindowAsync(database.DataSource, scope, everyId);

        Assert.Equal(
            ScheduleProcessOutcome.None,
            await store.ProcessClaimAsync(new ScheduleDispatchClaim(scope, afterId, after.Value!.Revision), CancellationToken.None));
        Assert.Equal(
            ScheduleProcessOutcome.None,
            await store.ProcessClaimAsync(new ScheduleDispatchClaim(scope, everyId, every.Value!.Revision), CancellationToken.None));
        Assert.Equal(0, await CountAsync(database.DataSource, scope, "schedule_occurrence"));
    }

    [Fact]
    public async Task Client_RejectsIncompatibleStoreMetadataBeforeReadingScheduleScope()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var options = await CreateOptionsAsync(database.DataSource, Guid.NewGuid());
        var registry = new DurableWorkRegistry([]);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var scope = new DurableScopeId("metadata-validation-scope");
        var scheduleId = new DurableScheduleId("metadata-validation-schedule");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var wrongStore = new PostgreSqlDurableScheduleClient(
            database.DataSource,
            registry,
            new PostgreSqlDurableWorkOptions(options.RuntimeEpoch, Guid.NewGuid()),
            scheduleOptions);

        var storeMismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await wrongStore.GetAsync(scope, scheduleId));
        Assert.StartsWith(DurableProblemCodes.StoreIdentityMismatch, storeMismatch.Message, StringComparison.Ordinal);

        await UpdateStoreMetadataAsync(database.DataSource, schemaVersion: 3, runtimeEpoch: options.RuntimeEpoch);
        var schemaUpgrade = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetAsync(scope, scheduleId));
        Assert.StartsWith(DurableProblemCodes.SchemaUpgradeRequired, schemaUpgrade.Message, StringComparison.Ordinal);

        await UpdateStoreMetadataAsync(database.DataSource, schemaVersion: 4, runtimeEpoch: Guid.NewGuid());
        var epochMismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetAsync(scope, scheduleId));
        Assert.StartsWith(DurableProblemCodes.RecoveryEpochRequired, epochMismatch.Message, StringComparison.Ordinal);
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

    private static async ValueTask SetExpiredDispatchLeaseAsync(
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

        await using var lease = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.schedule_dispatch
            SET state = 'leased',
                lease_owner = 'expired-test-owner',
                lease_expires_at = clock_timestamp() - interval '1 second',
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
            """,
            connection,
            transaction);
        lease.Parameters.AddWithValue("scope_id", scopeId.Value);
        lease.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        Assert.Equal(1, await lease.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask SetScheduleDispatchLeaseAsync(
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

        await using var lease = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.schedule_dispatch
            SET state = 'leased',
                lease_owner = 'active-test-owner',
                lease_expires_at = clock_timestamp() + interval '1 minute',
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
            """,
            connection,
            transaction);
        lease.Parameters.AddWithValue("scope_id", scopeId.Value);
        lease.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        Assert.Equal(1, await lease.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask<string> ReadScheduleDispatchStateAsync(
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
            "SELECT state FROM appsurface_durable.schedule_dispatch WHERE scope_id = @scope_id AND schedule_id = @schedule_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        var state = (string)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return state;
    }

    private static async ValueTask SetPersistedTargetKindAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        string targetKind)
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

        await using var target = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.schedule_generation
            SET target_kind = @target_kind,
                target_provider_safety = NULL
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id AND generation = 1;
            """,
            connection,
            transaction);
        target.Parameters.AddWithValue("target_kind", targetKind);
        target.Parameters.AddWithValue("scope_id", scopeId.Value);
        target.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        Assert.Equal(1, await target.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask DisableScopeAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId)
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

        await using var disable = new NpgsqlCommand(
            "UPDATE appsurface_durable.scope SET state = 'disabled', updated_at = clock_timestamp() WHERE scope_id = @scope_id;",
            connection,
            transaction);
        disable.Parameters.AddWithValue("scope_id", scopeId.Value);
        Assert.Equal(1, await disable.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask SetPersistedScheduleDefinitionsAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId)
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
            """
            UPDATE appsurface_durable.schedule_generation
            SET schedule_kind = CASE WHEN schedule_id IN ('persisted-cron', 'persisted-cron-standard') THEN 'cron' ELSE 'at' END,
                at_utc = CASE WHEN schedule_id IN ('persisted-cron', 'persisted-cron-standard') THEN NULL ELSE at_utc END,
                delay_interval = NULL,
                interval_value = NULL,
                anchor_utc = NULL,
                cron_expression = CASE WHEN schedule_id IN ('persisted-cron', 'persisted-cron-standard') THEN '* * * * * *' ELSE NULL END,
                cron_time_zone = CASE WHEN schedule_id IN ('persisted-cron', 'persisted-cron-standard') THEN 'Etc/UTC' ELSE NULL END,
                cron_dialect = CASE WHEN schedule_id IN ('persisted-cron', 'persisted-cron-standard') THEN 'cronos_v1' ELSE NULL END,
                cron_grammar = CASE
                    WHEN schedule_id = 'persisted-cron' THEN 'include_seconds'
                    WHEN schedule_id = 'persisted-cron-standard' THEN 'standard'
                    ELSE NULL
                END,
                overlap_kind = CASE
                    WHEN schedule_id = 'persisted-skip' THEN 'skip'
                    WHEN schedule_id = 'persisted-concurrent' THEN 'allow_concurrent'
                    ELSE 'queue_one'
                END,
                overlap_limit = CASE
                    WHEN schedule_id = 'persisted-concurrent' THEN 2
                    ELSE 1
                END,
                misfire_kind = CASE
                    WHEN schedule_id = 'persisted-skip' THEN 'skip'
                    WHEN schedule_id = 'persisted-concurrent' THEN 'catch_up'
                    ELSE 'run_once'
                END,
                misfire_limit = CASE
                    WHEN schedule_id = 'persisted-concurrent' THEN 2
                    WHEN schedule_id = 'persisted-skip' THEN 0
                    ELSE 1
                END,
                target_kind = CASE WHEN schedule_id = 'persisted-flow' THEN 'flow' ELSE 'work' END,
                target_provider_safety = CASE WHEN schedule_id = 'persisted-flow' THEN NULL ELSE target_provider_safety END
            WHERE scope_id = @scope_id
              AND schedule_id IN ('persisted-skip', 'persisted-concurrent', 'persisted-cron', 'persisted-cron-standard', 'persisted-flow');
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("scope_id", scopeId.Value);
        Assert.Equal(5, await update.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask UpdateStoreMetadataAsync(
        NpgsqlDataSource dataSource,
        int schemaVersion,
        Guid runtimeEpoch)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE appsurface_durable.store_metadata SET schema_version = @schema_version, active_runtime_epoch = @runtime_epoch WHERE singleton;",
            connection);
        command.Parameters.AddWithValue("schema_version", schemaVersion);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask ForceScheduleEvaluationWindowAsync(
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

        await using (var definition = new NpgsqlCommand(
                         "UPDATE appsurface_durable.schedule_definition SET accepted_at_utc = clock_timestamp(), cursor_utc = clock_timestamp() - interval '1 minute', next_due_utc = clock_timestamp(), updated_at = clock_timestamp() WHERE scope_id = @scope_id AND schedule_id = @schedule_id;",
                         connection,
                         transaction))
        {
            definition.Parameters.AddWithValue("scope_id", scopeId.Value);
            definition.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await definition.ExecuteNonQueryAsync());
        }

        await using (var dispatch = new NpgsqlCommand(
                         "UPDATE appsurface_durable.schedule_dispatch SET due_at = clock_timestamp(), state = 'available', lease_owner = NULL, lease_expires_at = NULL, updated_at = clock_timestamp() WHERE scope_id = @scope_id AND schedule_id = @schedule_id;",
                         connection,
                         transaction))
        {
            dispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
            dispatch.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await dispatch.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask ForceAfterScheduleDueAsync(
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

        await using (var definition = new NpgsqlCommand(
                         """
                         WITH forced AS (SELECT clock_timestamp() - interval '2 minutes' AS accepted_at_utc)
                         UPDATE appsurface_durable.schedule_definition AS schedule
                         SET accepted_at_utc = forced.accepted_at_utc,
                             cursor_utc = forced.accepted_at_utc,
                             next_due_utc = clock_timestamp(),
                             updated_at = clock_timestamp()
                         FROM forced
                         WHERE schedule.scope_id = @scope_id AND schedule.schedule_id = @schedule_id;
                         """,
                         connection,
                         transaction))
        {
            definition.Parameters.AddWithValue("scope_id", scopeId.Value);
            definition.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await definition.ExecuteNonQueryAsync());
        }

        await using (var dispatch = new NpgsqlCommand(
                         """
                         UPDATE appsurface_durable.schedule_dispatch
                         SET due_at = clock_timestamp(),
                             state = 'available',
                             lease_owner = NULL,
                             lease_expires_at = NULL,
                             updated_at = clock_timestamp()
                         WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
                         """,
                         connection,
                         transaction))
        {
            dispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
            dispatch.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await dispatch.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
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

            await Task.Delay(TimeSpan.FromMilliseconds(25));
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

    private static async ValueTask ForceQueueOneWorkRetryExhaustionAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId,
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

        long revision;
        await using (var exhaust = new NpgsqlCommand(
                         """
                         UPDATE appsurface_durable.work
                         SET state = 'retry_wait',
                             due_at = clock_timestamp(),
                             attempt_number = maximum_attempts,
                             lease_owner = NULL,
                             lease_started_at = NULL,
                             lease_expires_at = NULL,
                             updated_at = clock_timestamp(),
                             revision = revision + 1
                         WHERE scope_id = @scope_id AND work_id = @work_id
                         RETURNING revision;
                         """,
                         connection,
                         transaction))
        {
            exhaust.Parameters.AddWithValue("scope_id", scopeId.Value);
            exhaust.Parameters.AddWithValue("work_id", workId.Value);
            revision = (long)(await exhaust.ExecuteScalarAsync())!;
        }

        await using (var dispatch = new NpgsqlCommand(
                         """
                         UPDATE appsurface_durable.dispatch
                         SET state = 'available',
                             due_at = clock_timestamp(),
                             expected_revision = @revision,
                             updated_at = clock_timestamp()
                         WHERE scope_id = @scope_id
                           AND aggregate_kind = 'work'
                           AND aggregate_id = @work_id;
                         """,
                         connection,
                         transaction))
        {
            dispatch.Parameters.AddWithValue("revision", revision);
            dispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
            dispatch.Parameters.AddWithValue("work_id", workId.Value);
            Assert.Equal(1, await dispatch.ExecuteNonQueryAsync());
        }

        await using (var delayScheduleDispatch = new NpgsqlCommand(
                         """
                         UPDATE appsurface_durable.schedule_dispatch
                         SET state = 'available',
                             due_at = clock_timestamp() + interval '1 hour',
                             lease_owner = NULL,
                             lease_expires_at = NULL,
                             updated_at = clock_timestamp()
                         WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
                         """,
                         connection,
                         transaction))
        {
            delayScheduleDispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
            delayScheduleDispatch.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await delayScheduleDispatch.ExecuteNonQueryAsync());
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

    private static async ValueTask<bool> HasHistoryEventAsync(
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
            "SELECT EXISTS (SELECT 1 FROM appsurface_durable.schedule_history WHERE scope_id = @scope_id AND schedule_id = @schedule_id AND event_type = @event_type);",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("event_type", eventType);
        var exists = (bool)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return exists;
    }

    private static string StableIdentity(string prefix, params string[] values) =>
        $"{prefix}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values))))}";

    private sealed class SchedulePayloadCodec(
        string contractName,
        string contractVersion,
        DurableDataClassification classification = DurableDataClassification.ApprovedApplication) : IDurablePayloadCodec<byte[]>
    {
        public Type PayloadType => typeof(byte[]);

        public string ContractName { get; } = contractName;

        public string ContractVersion { get; } = contractVersion;

        public DurableDataClassification Classification { get; } = classification;

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
