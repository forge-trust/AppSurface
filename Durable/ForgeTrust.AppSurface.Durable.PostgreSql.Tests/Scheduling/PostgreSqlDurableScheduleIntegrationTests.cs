using System.Text;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests.Scheduling;

public sealed class PostgreSqlDurableScheduleIntegrationTests
{
    [Fact]
    public async Task Client_PersistsCrudGenerationAuditAndRls_WhenPostgreSqlIsConfigured()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var fixture = CreateFixture(database.DataSource);
        var scheduleId = new DurableScheduleId("schedule-crud");
        var createRequest = new DurableScheduleCreateRequest(
            new DurableScopeId("scope-schedule-crud"),
            new DurableCommandId("create-crud"),
            "create-crud-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow.AddHours(1)),
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("one")),
            "Daily cleanup");

        var created = await fixture.Client.CreateAsync(createRequest);
        var duplicate = await fixture.Client.CreateAsync(createRequest);
        var read = await fixture.Client.GetAsync(createRequest.ScopeId, scheduleId);
        var paused = await fixture.Client.PauseAsync(new DurableScheduleCommand(
            createRequest.ScopeId,
            new DurableCommandId("pause-crud"),
            scheduleId,
            "operator-a",
            "maintenance",
            1));
        var resumed = await fixture.Client.ResumeAsync(new DurableScheduleCommand(
            createRequest.ScopeId,
            new DurableCommandId("resume-crud"),
            scheduleId,
            "operator-b",
            "maintenance-complete",
            2));
        var updated = await fixture.Client.UpdateAsync(new DurableScheduleUpdateRequest(
            createRequest.ScopeId,
            new DurableCommandId("update-crud"),
            scheduleId,
            3,
            DurableSchedule.Cron("0 9 * * MON-FRI", "America/New_York"),
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("two")),
            "Weekday cleanup"));
        var deleted = await fixture.Client.DeleteAsync(new DurableScheduleCommand(
            createRequest.ScopeId,
            new DurableCommandId("delete-crud"),
            scheduleId,
            "operator-c",
            "retired",
            4));
        var list = await fixture.Client.ListAsync(new DurableScheduleListRequest(createRequest.ScopeId));

        Assert.True(created.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.Created, created.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Duplicate, duplicate.Value!.Code);
        Assert.True(read.IsSuccess);
        Assert.Equal("Daily cleanup", read.Value!.DisplayName);
        Assert.Equal(DurableScheduleTargetKind.Work, read.Value.Target.Kind);
        Assert.Equal(DurableProviderSafety.ProviderKeyed, read.Value.Target.ProviderSafety);
        Assert.Equal(DurableScheduleMutationCode.Paused, paused.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Resumed, resumed.Value!.Code);
        Assert.Equal(2, updated.Value!.Generation);
        Assert.Equal(4, updated.Value.Revision);
        Assert.Equal(DurableScheduleMutationCode.Deleted, deleted.Value!.Code);
        Assert.Equal(5, deleted.Value.Revision);
        Assert.Single(list.Value!.Schedules);
        Assert.Equal(DurableScheduleState.Deleted, list.Value.Schedules[0].State);

        var audit = await ReadAuditAsync(database.DataSource, createRequest.ScopeId, "pause-crud");
        Assert.Equal(("operator-a", "maintenance", "operator-a", "maintenance"), audit);
    }

    [Fact]
    public async Task List_IsScopedPagedPayloadFreeAndFiltersStateAndRecoveryEpoch_WhenPostgreSqlIsConfigured()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var initialEpoch = Guid.NewGuid();
        var replacementEpoch = Guid.NewGuid();
        var initial = CreateFixture(database.DataSource, initialEpoch);
        var scopeId = new DurableScopeId("scope-schedule-list");
        for (var index = 0; index < 3; index++)
        {
            var created = await initial.Client.CreateAsync(new DurableScheduleCreateRequest(
                scopeId,
                new DurableCommandId($"create-schedule-list-{index}"),
                $"create-schedule-list-key-{index}",
                new DurableScheduleId($"schedule-list-{index}"),
                DurableSchedule.At(DateTimeOffset.UtcNow.AddHours(index + 1)),
                DurableScheduleTarget.Work("test.work", "v1", new TestWork($"private-input-{index}")),
                $"Schedule {index}"));
            Assert.True(created.IsSuccess);
        }

        Assert.True((await initial.Client.CreateAsync(new DurableScheduleCreateRequest(
            new DurableScopeId("scope-schedule-list-other"),
            new DurableCommandId("create-schedule-list-other"),
            "create-schedule-list-other-key",
            new DurableScheduleId("schedule-list-other"),
            DurableSchedule.At(DateTimeOffset.UtcNow.AddHours(1)),
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("other-private-input"))))).IsSuccess);
        Assert.True((await initial.Client.PauseAsync(new DurableScheduleCommand(
            scopeId,
            new DurableCommandId("pause-schedule-list-2"),
            new DurableScheduleId("schedule-list-2"),
            "operator-list",
            "test-state-filter",
            expectedRevision: 1))).IsSuccess);

        var beforeRotation = await initial.Client.ListAsync(new DurableScheduleListRequest(
            scopeId,
            requiresRecoveryRelease: true));
        Assert.Empty(beforeRotation.Value!.Schedules);

        await RotateEpochAsync(database.DataSource, initialEpoch, replacementEpoch);
        var replacement = CreateFixture(database.DataSource, replacementEpoch);
        var first = await replacement.Client.ListAsync(new DurableScheduleListRequest(
            scopeId,
            pageSize: 2,
            requiresRecoveryRelease: true));
        var second = await replacement.Client.ListAsync(new DurableScheduleListRequest(
            scopeId,
            pageSize: 2,
            continuationToken: first.Value!.ContinuationToken,
            requiresRecoveryRelease: true));
        var paused = await replacement.Client.ListAsync(new DurableScheduleListRequest(
            scopeId,
            state: DurableScheduleState.Paused,
            requiresRecoveryRelease: true));
        var wrongScope = await replacement.Client.ListAsync(new DurableScheduleListRequest(
            new DurableScopeId("scope-schedule-list-missing"),
            requiresRecoveryRelease: true));

        Assert.Equal(2, first.Value.Schedules.Count);
        Assert.NotNull(first.Value.ContinuationToken);
        Assert.Single(second.Value!.Schedules);
        Assert.Null(second.Value.ContinuationToken);
        Assert.All(first.Value.Schedules.Concat(second.Value.Schedules), item =>
        {
            Assert.True(item.RequiresRecoveryRelease);
            Assert.Equal(DurableScheduleKind.At, item.ScheduleKind);
            Assert.Equal(DurableScheduleTargetKind.Work, item.TargetKind);
            Assert.Equal(DurableProviderSafety.ProviderKeyed, item.TargetProviderSafety);
            Assert.Equal("test.work", item.TargetName);
            Assert.Equal("v1", item.TargetVersion);
        });
        Assert.Single(paused.Value!.Schedules);
        Assert.Equal(new DurableScheduleId("schedule-list-2"), paused.Value.Schedules[0].ScheduleId);
        Assert.Equal(DurableScheduleState.Paused, paused.Value.Schedules[0].State);
        Assert.Empty(wrongScope.Value!.Schedules);
    }

    [Fact]
    public async Task Processor_AtomicallyStartsTargetAndReleasesSlot_WhenPostgreSqlIsConfigured()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var fixture = CreateFixture(database.DataSource);
        var scopeId = new DurableScopeId("scope-schedule-start");
        var scheduleId = new DurableScheduleId("schedule-start");
        var created = await fixture.Client.CreateAsync(new DurableScheduleCreateRequest(
            scopeId,
            new DurableCommandId("create-start"),
            "create-start-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow.AddMinutes(-1)),
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("start"))));

        var processed = await fixture.Processor.ProcessDueAsync();
        var snapshot = await fixture.Client.GetAsync(scopeId, scheduleId);
        var targetId = await ReadActiveTargetIdAsync(database.DataSource, scopeId, scheduleId);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await fixture.Processor.ReleaseTargetAsync(
                transaction,
                scopeId,
                DurableScheduleTargetKind.Work,
                targetId,
                "succeeded"));
            await transaction.CommitAsync();
        }

        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.False(await fixture.Processor.ReleaseTargetAsync(
                transaction,
                scopeId,
                DurableScheduleTargetKind.Work,
                targetId,
                "duplicate"));
            await transaction.CommitAsync();
        }

        Assert.True(created.IsSuccess);
        Assert.Equal(1, processed.Materialized);
        Assert.Equal(1, processed.Started);
        Assert.Equal(2, snapshot.Value!.Revision);
        Assert.Null(snapshot.Value.NextOccurrenceUtc);
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "work", "state = 'pending'"));
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_occurrence", "state = 'terminal'"));
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_run_slot", "state = 'released'"));
    }

    [Fact]
    public async Task QueueOneRunOnce_PersistsOneCoalescedPendingFollowUp_WhenPostgreSqlIsConfigured()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var fixture = CreateFixture(database.DataSource);
        var scopeId = new DurableScopeId("scope-schedule-queue");
        var scheduleId = new DurableScheduleId("schedule-queue");
        var created = await fixture.Client.CreateAsync(new DurableScheduleCreateRequest(
            scopeId,
            new DurableCommandId("create-queue"),
            "create-queue-key",
            scheduleId,
            DurableSchedule.Every(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddMinutes(-10)),
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("queue"))));
        Assert.True(created.IsSuccess);

        var first = await fixture.Processor.ProcessDueAsync();
        await ForceDueAsync(database.DataSource, scopeId, scheduleId, TimeSpan.FromMinutes(5));
        var queued = await fixture.Processor.ProcessDueAsync();
        await ForceDueAsync(database.DataSource, scopeId, scheduleId, TimeSpan.FromMinutes(4));
        var coalesced = await fixture.Processor.ProcessDueAsync();
        var originalTargetId = await ReadActiveTargetIdAsync(database.DataSource, scopeId, scheduleId);

        Assert.Equal(1, first.Started);
        Assert.Equal(1, queued.Queued);
        Assert.Equal(1, coalesced.Coalesced);
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_occurrence", "state = 'queued'"));
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_occurrence", "state = 'coalesced'"));
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_run_slot", "state = 'active'"));

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Assert.True(await fixture.Processor.ReleaseTargetAsync(
            transaction,
            scopeId,
            DurableScheduleTargetKind.Work,
            originalTargetId,
            "succeeded"));
        await transaction.CommitAsync();

        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_occurrence", "state = 'started'"));
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_run_slot", "state = 'active'"));
        Assert.Equal(2, await CountScopedAsync(database.DataSource, scopeId, "work", "state = 'pending'"));
    }

    [Fact]
    public async Task RecoveryRelease_PreservesQueueOnePendingAndPausedState_WhenPostgreSqlIsConfigured()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var initialEpoch = Guid.NewGuid();
        var replacementEpoch = Guid.NewGuid();
        var initial = CreateFixture(database.DataSource, initialEpoch);
        var scopeId = new DurableScopeId("scope-schedule-recovery");
        var queueScheduleId = new DurableScheduleId("schedule-recovery-queue");
        var pausedScheduleId = new DurableScheduleId("schedule-recovery-paused");
        Assert.True((await initial.Client.CreateAsync(new DurableScheduleCreateRequest(
            scopeId,
            new DurableCommandId("create-recovery-queue"),
            "create-recovery-queue-key",
            queueScheduleId,
            DurableSchedule.Every(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddMinutes(-10)),
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("queue"))))).IsSuccess);
        Assert.True((await initial.Client.CreateAsync(new DurableScheduleCreateRequest(
            scopeId,
            new DurableCommandId("create-recovery-paused"),
            "create-recovery-paused-key",
            pausedScheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow.AddHours(1)),
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("paused"))))).IsSuccess);
        Assert.True((await initial.Client.PauseAsync(new DurableScheduleCommand(
            scopeId,
            new DurableCommandId("pause-before-recovery"),
            pausedScheduleId,
            "operator-a",
            "planned-maintenance",
            1))).IsSuccess);

        Assert.Equal(1, (await initial.Processor.ProcessDueAsync()).Started);
        await ForceDueAsync(database.DataSource, scopeId, queueScheduleId, TimeSpan.FromMinutes(5));
        Assert.Equal(1, (await initial.Processor.ProcessDueAsync()).Queued);
        var activeTargetId = await ReadActiveTargetIdAsync(database.DataSource, scopeId, queueScheduleId);
        var beforeRotation = await ReadRecoveryRowAsync(database.DataSource, scopeId, queueScheduleId);
        Assert.NotNull(beforeRotation.PendingGeneration);

        await RotateEpochAsync(database.DataSource, initialEpoch, replacementEpoch);
        var replacement = CreateFixture(database.DataSource, replacementEpoch);
        await ForceDueAsync(database.DataSource, scopeId, queueScheduleId, TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await replacement.Processor.ProcessDueAsync()).Advanced);
        var suspended = await ReadRecoveryRowAsync(database.DataSource, scopeId, queueScheduleId);
        Assert.Equal("suspended", suspended.State);
        Assert.Equal("active", suspended.SuspendedFromState);
        Assert.Equal(DurableProblemCodes.RecoveryEpochRequired, suspended.SuspensionCode);
        Assert.Equal(beforeRotation.PendingGeneration, suspended.PendingGeneration);
        Assert.Equal("suspended", suspended.DispatchState);

        var queueRelease = new DurableScheduleCommand(
            scopeId,
            new DurableCommandId("release-recovery-queue"),
            queueScheduleId,
            "recovery-operator",
            "restore-verified",
            suspended.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await initial.Client.ReleaseAfterRecoveryAsync(queueRelease));
        var released = await replacement.Client.ReleaseAfterRecoveryAsync(queueRelease);
        var duplicate = await replacement.Client.ReleaseAfterRecoveryAsync(queueRelease);
        Assert.True(released.IsSuccess);
        Assert.Equal(DurableScheduleMutationCode.RecoveryReleased, released.Value!.Code);
        Assert.Equal(DurableScheduleMutationCode.Duplicate, duplicate.Value!.Code);
        var active = await ReadRecoveryRowAsync(database.DataSource, scopeId, queueScheduleId);
        Assert.Equal("active", active.State);
        Assert.Null(active.SuspendedFromState);
        Assert.Null(active.SuspensionCode);
        Assert.Equal(beforeRotation.PendingGeneration, active.PendingGeneration);
        Assert.Equal(replacementEpoch, active.RuntimeEpoch);
        Assert.Equal("available", active.DispatchState);

        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await replacement.Processor.ReleaseTargetAsync(
                transaction,
                scopeId,
                DurableScheduleTargetKind.Work,
                activeTargetId,
                "succeeded"));
            await transaction.CommitAsync();
        }

        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_occurrence", "state = 'terminal'"));
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_occurrence", "state = 'started'"));
        Assert.Equal(2, await CountScopedAsync(database.DataSource, scopeId, "work", "state = 'pending'"));

        var pausedBeforeRelease = await ReadRecoveryRowAsync(database.DataSource, scopeId, pausedScheduleId);
        Assert.Equal("paused", pausedBeforeRelease.State);
        Assert.Equal(initialEpoch, pausedBeforeRelease.RuntimeEpoch);
        var pausedRelease = await replacement.Client.ReleaseAfterRecoveryAsync(new DurableScheduleCommand(
            scopeId,
            new DurableCommandId("release-recovery-paused"),
            pausedScheduleId,
            "recovery-operator",
            "restore-verified",
            pausedBeforeRelease.Revision));
        Assert.True(pausedRelease.IsSuccess);
        var pausedAfterRelease = await ReadRecoveryRowAsync(database.DataSource, scopeId, pausedScheduleId);
        Assert.Equal("paused", pausedAfterRelease.State);
        Assert.Equal("suspended", pausedAfterRelease.DispatchState);
        Assert.Equal(replacementEpoch, pausedAfterRelease.RuntimeEpoch);
    }

    [Fact]
    public async Task RecoveryRelease_PreservesCatchUpContinuation_WhenPostgreSqlIsConfigured()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var initialEpoch = Guid.NewGuid();
        var replacementEpoch = Guid.NewGuid();
        var initial = CreateFixture(database.DataSource, initialEpoch);
        var scopeId = new DurableScopeId("scope-schedule-catchup-recovery");
        var scheduleId = new DurableScheduleId("schedule-catchup-recovery");
        var schedule = DurableSchedule
            .Every(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddMinutes(-20))
            .WithMisfire(ScheduleMisfirePolicy.CatchUp(10));
        Assert.True((await initial.Client.CreateAsync(new DurableScheduleCreateRequest(
            scopeId,
            new DurableCommandId("create-catchup-recovery"),
            "create-catchup-recovery-key",
            scheduleId,
            schedule,
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("catchup"))))).IsSuccess);

        var firstPass = await initial.Processor.ProcessDueAsync(new PostgreSqlScheduleProcessingRequest(
            maximumOccurrencesPerSchedule: 2));
        Assert.Equal(2, firstPass.Materialized);
        var beforeRotation = await ReadRecoveryRowAsync(database.DataSource, scopeId, scheduleId);
        Assert.Equal(8, beforeRotation.CatchUpRemaining);

        await RotateEpochAsync(database.DataSource, initialEpoch, replacementEpoch);
        var replacement = CreateFixture(database.DataSource, replacementEpoch);
        Assert.Equal(1, (await replacement.Processor.ProcessDueAsync()).Advanced);
        var suspended = await ReadRecoveryRowAsync(database.DataSource, scopeId, scheduleId);
        Assert.Equal("suspended", suspended.State);
        Assert.Equal(beforeRotation.CatchUpRemaining, suspended.CatchUpRemaining);
        Assert.Equal(beforeRotation.PendingGeneration, suspended.PendingGeneration);

        var released = await replacement.Client.ReleaseAfterRecoveryAsync(new DurableScheduleCommand(
            scopeId,
            new DurableCommandId("release-catchup-recovery"),
            scheduleId,
            "recovery-operator",
            "restore-verified",
            suspended.Revision));
        Assert.True(released.IsSuccess);
        var afterRelease = await ReadRecoveryRowAsync(database.DataSource, scopeId, scheduleId);
        Assert.Equal(beforeRotation.CatchUpRemaining, afterRelease.CatchUpRemaining);
        Assert.Equal(beforeRotation.PendingGeneration, afterRelease.PendingGeneration);

        var continued = await replacement.Processor.ProcessDueAsync(new PostgreSqlScheduleProcessingRequest(
            maximumOccurrencesPerSchedule: 2));
        var afterContinuation = await ReadRecoveryRowAsync(database.DataSource, scopeId, scheduleId);
        Assert.Equal(2, continued.Materialized);
        Assert.Equal(6, afterContinuation.CatchUpRemaining);
    }

    [Fact]
    public async Task OldEpochTargetRelease_DoesNotStartQueueOnePendingBeforeScheduleRelease()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var initialEpoch = Guid.NewGuid();
        var replacementEpoch = Guid.NewGuid();
        var initial = CreateFixture(database.DataSource, initialEpoch);
        var scopeId = new DurableScopeId("scope-schedule-target-recovery");
        var scheduleId = new DurableScheduleId("schedule-target-recovery");
        Assert.True((await initial.Client.CreateAsync(new DurableScheduleCreateRequest(
            scopeId,
            new DurableCommandId("create-target-recovery"),
            "create-target-recovery-key",
            scheduleId,
            DurableSchedule.Every(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddMinutes(-10)),
            DurableScheduleTarget.Work("test.work", "v1", new TestWork("target-recovery"))))).IsSuccess);
        Assert.Equal(1, (await initial.Processor.ProcessDueAsync()).Started);
        await ForceDueAsync(database.DataSource, scopeId, scheduleId, TimeSpan.FromMinutes(5));
        Assert.Equal(1, (await initial.Processor.ProcessDueAsync()).Queued);
        var targetId = await ReadActiveTargetIdAsync(database.DataSource, scopeId, scheduleId);
        var before = await ReadRecoveryRowAsync(database.DataSource, scopeId, scheduleId);
        Assert.NotNull(before.PendingGeneration);
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "work", "state = 'pending'"));

        await RotateEpochAsync(database.DataSource, initialEpoch, replacementEpoch);
        var replacement = CreateFixture(database.DataSource, replacementEpoch);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await replacement.Processor.ReleaseTargetAsync(
                transaction,
                scopeId,
                DurableScheduleTargetKind.Work,
                targetId,
                "operator_resolved_applied"));
            await transaction.CommitAsync();
        }

        var fenced = await ReadRecoveryRowAsync(database.DataSource, scopeId, scheduleId);
        Assert.Equal("suspended", fenced.State);
        Assert.Equal("active", fenced.SuspendedFromState);
        Assert.Equal(DurableProblemCodes.RecoveryEpochRequired, fenced.SuspensionCode);
        Assert.Equal(before.PendingGeneration, fenced.PendingGeneration);
        Assert.Equal("suspended", fenced.DispatchState);
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "work", "state = 'pending'"));
        Assert.Equal(1, await CountScopedAsync(database.DataSource, scopeId, "schedule_occurrence", "state = 'queued'"));

        var released = await replacement.Client.ReleaseAfterRecoveryAsync(new DurableScheduleCommand(
            scopeId,
            new DurableCommandId("release-target-recovery"),
            scheduleId,
            "recovery-operator",
            "restore-verified",
            fenced.Revision));
        Assert.True(released.IsSuccess);
        await ForceDueAsync(database.DataSource, scopeId, scheduleId, TimeSpan.FromMinutes(1));
        var resumed = await replacement.Processor.ProcessDueAsync();
        Assert.True(resumed.Started >= 1);
        Assert.Equal(2, await CountScopedAsync(database.DataSource, scopeId, "work", "state = 'pending'"));
    }

    private static ScheduleFixture CreateFixture(NpgsqlDataSource dataSource, Guid? runtimeEpoch = null)
    {
        var workCodec = new TestWorkCodec();
        var resultCodec = new TestResultCodec();
        var payloadCodecs = new DurablePayloadCodecRegistry();
        payloadCodecs.Register(workCodec);
        payloadCodecs.Register(resultCodec);
        var registration = new DurableWorkRegistration<TestWork, TestResult, TestExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        var workRegistry = new DurableWorkRegistry([registration]);
        var flowRegistry = new DurableFlowRegistry([]);
        var epoch = runtimeEpoch ?? Guid.NewGuid();
        return new ScheduleFixture(
            new PostgreSqlDurableScheduleClient(
                dataSource,
                payloadCodecs,
                workRegistry,
                flowRegistry,
                epoch),
            new PostgreSqlDurableScheduleProcessor(
                dataSource,
                workRegistry,
                flowRegistry,
                epoch,
                sendWakeNotification: false));
    }

    private static async ValueTask RotateEpochAsync(
        NpgsqlDataSource dataSource,
        Guid expectedEpoch,
        Guid replacementEpoch)
    {
        var result = await new PostgreSqlDurableRuntimeSchemaManager(dataSource).RotateRuntimeEpochAsync(
            expectedEpoch,
            replacementEpoch,
            "recovery-operator",
            "test-restore-complete");
        Assert.Equal(expectedEpoch, result.PreviousEpoch);
        Assert.Equal(replacementEpoch, result.ActiveEpoch);
    }

    private static async ValueTask<ScheduleRecoveryRow> ReadRecoveryRowAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PostgreSqlScheduleStorage.SetScopeAsync(connection, transaction, scopeId, default);
        const string sql = """
            SELECT current.state,
                   current.suspended_from_state,
                   current.suspension_code,
                   current.pending_generation,
                   current.catch_up_remaining,
                   current.runtime_epoch,
                   current.revision,
                   dispatch.state
            FROM appsurface_durable.schedule_current AS current
            JOIN appsurface_durable.dispatch AS dispatch
              ON dispatch.scope_id = current.scope_id
             AND dispatch.aggregate_kind = 'schedule'
             AND dispatch.aggregate_id = current.schedule_id
            WHERE current.scope_id = @scope_id AND current.schedule_id = @schedule_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new ScheduleRecoveryRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetGuid(5),
            reader.GetInt64(6),
            reader.GetString(7));
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask ApplySchemaAsync(PostgreSqlIntegrationTestDatabase database)
    {
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
    }

    private static async ValueTask<(
        string CommandActor,
        string CommandReason,
        string HistoryActor,
        string HistoryReason)> ReadAuditAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string commandId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PostgreSqlScheduleStorage.SetScopeAsync(connection, transaction, scopeId, default);
        await using var command = new NpgsqlCommand(
            """
            SELECT command.actor_id, command.reason_code, history.actor_id, history.reason_code
            FROM appsurface_durable.schedule_command AS command
            JOIN appsurface_durable.schedule_history AS history
              ON history.scope_id = command.scope_id
             AND history.schedule_id = command.schedule_id
             AND history.command_id = command.command_id
            WHERE command.scope_id = @scope_id AND command.command_id = @command_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask<string> ReadActiveTargetIdAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PostgreSqlScheduleStorage.SetScopeAsync(connection, transaction, scopeId, default);
        await using var command = new NpgsqlCommand(
            """
            SELECT target_id
            FROM appsurface_durable.schedule_run_slot
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id AND state = 'active';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        var targetId = (string)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return targetId;
    }

    private static async ValueTask ForceDueAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId,
        TimeSpan overdueBy)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PostgreSqlScheduleStorage.SetScopeAsync(connection, transaction, scopeId, default);
        var due = DateTimeOffset.UtcNow.Subtract(overdueBy);
        const string sql = """
            UPDATE appsurface_durable.schedule_current
            SET next_nominal_due_utc = @due
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;

            UPDATE appsurface_durable.dispatch
            SET due_at = @due, state = 'available'
            WHERE scope_id = @scope_id AND aggregate_kind = 'schedule' AND aggregate_id = @schedule_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        command.Parameters.AddWithValue("due", due);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async ValueTask<long> CountScopedAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string table,
        string predicate)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PostgreSqlScheduleStorage.SetScopeAsync(connection, transaction, scopeId, default);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM appsurface_durable.{table} WHERE scope_id = @scope_id AND {predicate};",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private sealed record ScheduleFixture(
        PostgreSqlDurableScheduleClient Client,
        PostgreSqlDurableScheduleProcessor Processor);

    private sealed record ScheduleRecoveryRow(
        string State,
        string? SuspendedFromState,
        string? SuspensionCode,
        long? PendingGeneration,
        int? CatchUpRemaining,
        Guid RuntimeEpoch,
        long Revision,
        string DispatchState);

    private sealed record TestWork(string Value);

    private sealed record TestResult(string Value);

    private sealed class TestWorkCodec : IDurablePayloadCodec<TestWork>
    {
        public Type PayloadType => typeof(TestWork);

        public string ContractName => "test.work";

        public string ContractVersion => "v1";

        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(TestWork value) => new(
            ContractName,
            ContractVersion,
            Classification,
            Encoding.UTF8.GetBytes(value.Value),
            RetentionPolicyId);

        public TestWork Decode(DurableEncodedPayload payload) => new(Encoding.UTF8.GetString(payload.Content.Span));

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<TestWork>(value));

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }

    private sealed class TestResultCodec : IDurablePayloadCodec<TestResult>
    {
        public Type PayloadType => typeof(TestResult);

        public string ContractName => "test.result";

        public string ContractVersion => "v1";

        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(TestResult value) => new(
            ContractName,
            ContractVersion,
            Classification,
            Encoding.UTF8.GetBytes(value.Value),
            RetentionPolicyId);

        public TestResult Decode(DurableEncodedPayload payload) => new(Encoding.UTF8.GetString(payload.Content.Span));

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<TestResult>(value));

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }

    private sealed class TestExecutor : IDurableWorkerExecutor<TestWork, TestResult>
    {
        public ValueTask<TestResult> ExecuteAsync(
            DurableWorkerEnvelope<TestWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TestResult(work.Payload!.Value));
    }
}
