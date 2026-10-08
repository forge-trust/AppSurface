using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableDerivedExecutionPolicyTests
{
    private static readonly DateTimeOffset AcceptanceAnchor = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly TimeSpan[] Offsets =
    [
        TimeSpan.Zero,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(180),
    ];

    [Fact]
    public async Task FlowChild_UsesCapturedAttemptPlanAndDatabaseAcceptanceAnchor()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "derived-flow-execution-policy");
        var status = await schema.GetStatusAsync();
        await database.SetExecutionTimeAsync(AcceptanceAnchor);

        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.derived-flow.context", "v1");
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.derived-flow.work", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.derived-flow.result", "v1");
        var workRegistration = CreatePlannedRegistration(
            "tests.derived-flow.work", workCodec, resultCodec, DurableProviderSafety.Idempotent);
        var workRegistry = new DurableWorkRegistry([workRegistration]);
        var payloads = new DurablePayloadCodecRegistry([contextCodec, workCodec, resultCodec]);
        var flowRegistration = new ActivityFlowRegistration(contextCodec, workRegistration, workCodec);
        var flows = new DurableFlowRegistry([flowRegistration], workRegistry, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, payloads, options);
        var processor = new PostgreSqlDurableFlowProcessor(
            database.DataSource, database.DataSource, flows, workRegistry, payloads, options);
        var scope = new DurableScopeId("derived-flow-execution-policy");
        var instance = new DurableFlowInstanceId("planned-child");

        var started = await client.StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("planned-flow-start"),
            "planned-flow-start-key",
            instance,
            flowRegistration.FlowId,
            flowRegistration.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 })));
        Assert.True(started.IsSuccess);

        var processed = await processor.TryProcessAsync(
            Assert.Single(await processor.DiscoverAsync()), "planned-flow-worker");
        Assert.NotNull(processed.ChildWorkId);

        var stored = await ReadStoredPolicyAsync(database.DataSource, scope, "tests.derived-flow.work");
        AssertAttemptPlan(stored);
    }

    [Fact]
    public async Task ScheduleTarget_UsesCapturedAttemptPlanAndDatabaseAcceptanceAnchor()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "derived-schedule-execution-policy");
        var status = await schema.GetStatusAsync();
        await database.SetExecutionTimeAsync(AcceptanceAnchor);

        var workCodec = new PostgreSqlOpaqueTestCodec("tests.derived-schedule.work", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.derived-schedule.result", "v1");
        var workRegistration = CreatePlannedRegistration(
            "tests.derived-schedule.work", workCodec, resultCodec, DurableProviderSafety.Idempotent);
        var registry = new DurableWorkRegistry([workRegistration]);
        var workOptions = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, workOptions, scheduleOptions);
        var processor = new PostgreSqlDurableScheduleProcessor(
            database.DataSource, database.DataSource, registry, workOptions, scheduleOptions);
        var scope = new DurableScopeId("derived-schedule-execution-policy");

        var created = await client.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("planned-schedule-create"),
            "planned-schedule-create-key",
            new DurableScheduleId("planned-schedule"),
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)),
            DurableScheduleTarget.Work(
                workRegistration.WorkName,
                workRegistration.WorkVersion,
                new byte[] { 2 },
                workCodec)));
        Assert.True(created.IsSuccess);

        var processed = await processor.ProcessDueAsync(
            new PostgreSqlDurableScheduleProcessRequest("planned-schedule-worker", maximumSchedules: 1));
        Assert.Equal(1, processed.MaterializedWorkTargets);

        var stored = await ReadStoredPolicyAsync(database.DataSource, scope, workRegistration.WorkName);
        AssertAttemptPlan(stored);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FlowChild_CircuitOutcomeProjectsAtomicallyAndProjectionFailureRollsBack(bool admitted, bool failProjection)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "derived-flow-projection");
        var status = await schema.GetStatusAsync();
        await database.SetExecutionTimeAsync(AcceptanceAnchor);
        var context = new PostgreSqlOpaqueTestCodec("tests.derived-flow.context", "v1");
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.derived-flow.work", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.derived-flow.result", "v1");
        var registration = CreatePlannedRegistration("tests.derived-flow.work", workCodec, resultCodec, DurableProviderSafety.Idempotent);
        var works = new DurableWorkRegistry([registration]);
        var codecs = new DurablePayloadCodecRegistry([context, workCodec, resultCodec]);
        var flow = new ActivityFlowRegistration(context, registration, workCodec);
        var flows = new DurableFlowRegistry([flow], works, codecs);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var client = new PostgreSqlDurableFlowClient(database.DataSource, flows, codecs, options);
        var processor = new PostgreSqlDurableFlowProcessor(database.DataSource, database.DataSource, flows, works, codecs, options);
        var scope = new DurableScopeId("derived-flow-execution-policy");
        var started = await client.StartAsync(new DurableFlowStartRequest(scope, new("projection-start"), "projection-key",
            new("projection-flow"), flow.FlowId, flow.FlowVersion, context.EncodeObject(new byte[] { 1 })));
        Assert.True(started.IsSuccess);
        var created = await processor.TryProcessAsync(Assert.Single(await processor.DiscoverAsync()), "flow-worker");
        Assert.NotNull(created.ChildWorkId);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var claim = (await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(10)), "work-worker"))!;
        var permit = (await store.TryAcquireEffectPermitAsync(claim))!;
        if (admitted) Assert.True(await store.TryAdmitInvocationAsync(permit));
        var revision = await ScalarAsync<long>(database.DataSource, "SELECT revision FROM appsurface_durable.work;");
        var history = await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.work_history;");
        await database.SetExecutionTimeAsync(AcceptanceAnchor.AddMinutes(240));
        var recoveryCandidate = Assert.Single(await store.DiscoverAsync(10));
        if (failProjection)
        {
            await using var remove = database.DataSource.CreateCommand("DELETE FROM appsurface_durable.flow_dispatch;");
            await remove.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.TryClaimAsync(recoveryCandidate, "recovery-worker"));
            Assert.Equal(revision, await ScalarAsync<long>(database.DataSource, "SELECT revision FROM appsurface_durable.work;"));
            Assert.Equal(history, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.work_history;"));
            Assert.Equal(0, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
            Assert.Equal("effect_permitted", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.work;"));
            Assert.Equal("active", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.flow_wait;"));
            Assert.Equal("waiting_activity", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.flow_instance;"));
            Assert.Equal("granted", await ScalarAsync<string>(database.DataSource, "SELECT status FROM appsurface_durable.effect_permit;"));
            return;
        }
        Assert.Null(await store.TryClaimAsync(recoveryCandidate, "recovery-worker"));
        Assert.Equal(admitted ? "suspended_ambiguous_external_outcome" : "failed",
            await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.work;"));
        Assert.Equal("suspended", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.flow_instance;"));
        Assert.Equal("suspended", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.flow_wait;"));
        Assert.Equal("suspended", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.flow_dispatch;"));
        Assert.Equal(admitted ? "suspended" : "terminal", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal(1, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.flow_history WHERE transition_kind='activity_attention_required';"));
        Assert.Equal(1, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='execution_transition';"));
        Assert.Empty(await store.DiscoverAsync(10));
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ScheduleQueueOne_CircuitTruthControlsRequeueAndProjectionRollback(bool admitted, bool failProjection)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "tests", "derived-schedule-projection");
        var status = await schema.GetStatusAsync();
        await database.SetExecutionTimeAsync(AcceptanceAnchor);
        var input = new PostgreSqlOpaqueTestCodec("tests.schedule-projection.input", "v1");
        var output = new PostgreSqlOpaqueTestCodec("tests.schedule-projection.output", "v1");
        var registration = CreatePlannedRegistration("tests.schedule-projection", input, output, DurableProviderSafety.Idempotent);
        var registry = new DurableWorkRegistry([registration]);
        var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
        var scheduleOptions = new PostgreSqlDurableScheduleOptions("appsurface");
        var client = new PostgreSqlDurableScheduleClient(database.DataSource, registry, options, scheduleOptions);
        var processor = new PostgreSqlDurableScheduleProcessor(database.DataSource, database.DataSource, registry, options, scheduleOptions);
        var scope = new DurableScopeId("derived-schedule-execution-policy");
        var scheduleId = new DurableScheduleId("queue-schedule");
        Assert.True((await client.CreateAsync(new DurableScheduleCreateRequest(scope, new("queue-create"), "queue-key",
            scheduleId, DurableSchedule.Every(TimeSpan.FromMilliseconds(250), DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2)),
            DurableScheduleTarget.Work(registration.WorkName, registration.WorkVersion, new byte[] { 1 }, input)))).IsSuccess);
        await ForceQueueOneScheduleDueAsync(database.DataSource, scope, scheduleId);
        Assert.Equal(1, (await processor.ProcessDueAsync(new("queue-worker", 1))).MaterializedWorkTargets);
        await ForceQueueOneScheduleDueAsync(database.DataSource, scope, scheduleId);
        Assert.Equal(0, (await processor.ProcessDueAsync(new("queue-worker", 1))).MaterializedWorkTargets);
        Assert.Equal(1, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.schedule_occurrence WHERE state='pending';"));
        await using (var delay = database.DataSource.CreateCommand("UPDATE appsurface_durable.schedule_dispatch SET due_at=clock_timestamp()+interval '1 day';")) await delay.ExecuteNonQueryAsync();
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var permit = (await store.TryAcquireEffectPermitAsync((await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(10)), "work-worker"))!))!;
        if (admitted) Assert.True(await store.TryAdmitInvocationAsync(permit));
        await database.SetExecutionTimeAsync(AcceptanceAnchor.AddMinutes(240));
        var candidate = Assert.Single(await store.DiscoverAsync(10));
        if (failProjection)
        {
            await using var failure = database.DataSource.CreateCommand("""
                CREATE FUNCTION public.issue765_fail_schedule_history() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW.event_type='work-terminal-requeued' THEN RAISE EXCEPTION 'deliberate projection failure'; END IF; RETURN NEW; END; $$;
                CREATE TRIGGER issue765_fail_schedule_history BEFORE INSERT ON appsurface_durable.schedule_history
                FOR EACH ROW EXECUTE FUNCTION public.issue765_fail_schedule_history();
                """);
            await failure.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<PostgresException>(async () => await store.TryClaimAsync(candidate, "recover"));
            Assert.Equal("effect_permitted", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.work;"));
            Assert.Equal("granted", await ScalarAsync<string>(database.DataSource, "SELECT status FROM appsurface_durable.effect_permit;"));
            Assert.Equal(0, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
            Assert.Equal(0, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='execution_transition';"));
        }
        else
        {
            Assert.Null(await store.TryClaimAsync(candidate, "recover"));
            Assert.Equal(admitted ? "suspended_ambiguous_external_outcome" : "failed", await ScalarAsync<string>(database.DataSource, "SELECT state FROM appsurface_durable.work;"));
        }
        Assert.Equal(!admitted && !failProjection, await ScalarAsync<bool>(database.DataSource,
            "SELECT due_at<=clock_timestamp() FROM appsurface_durable.schedule_dispatch;"));
        Assert.Equal(!admitted && !failProjection ? 1 : 0, await ScalarAsync<long>(database.DataSource,
            "SELECT count(*) FROM appsurface_durable.schedule_history WHERE event_type='work-terminal-requeued';"));
        Assert.Equal(1, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.schedule_occurrence WHERE state='pending';"));
        Assert.Equal(1, await ScalarAsync<long>(database.DataSource, "SELECT count(*) FROM appsurface_durable.schedule_occurrence WHERE state='materialized';"));
    }

    private static async Task ForceQueueOneScheduleDueAsync(
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

        const string wakeDispatchSql = """
            UPDATE appsurface_durable.schedule_dispatch
            SET due_at = clock_timestamp(),
                state = 'available',
                lease_owner = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND schedule_id = @schedule_id;
            """;
        await using (var wakeDispatch = new NpgsqlCommand(wakeDispatchSql, connection, transaction))
        {
            wakeDispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
            wakeDispatch.Parameters.AddWithValue("schedule_id", scheduleId.Value);
            Assert.Equal(1, await wakeDispatch.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
    }

    private static DurableWorkRegistration CreatePlannedRegistration(
        string workName,
        IDurablePayloadCodec<byte[]> workCodec,
        IDurablePayloadCodec<byte[]> resultCodec,
        DurableProviderSafety providerSafety)
    {
        var retryPolicy = new DurableWorkRetryPolicy(
            maximumAttempts: Offsets.Length,
            maximumElapsedTime: TimeSpan.FromHours(6),
            initialRetryDelay: TimeSpan.FromMinutes(1),
            maximumRetryDelay: TimeSpan.FromMinutes(15),
            leaseDuration: TimeSpan.FromMinutes(2),
            renewalCadence: TimeSpan.FromSeconds(30),
            maximumLeaseLifetime: TimeSpan.FromMinutes(10),
            backoffAlgorithm: "exponential-v1");
        var executionPolicy = DurableWorkExecutionPolicy.ForAttemptPlan(
            retryPolicy,
            new DurableAttemptPlan("attempt-plan-v1", Offsets, TimeSpan.FromMinutes(240)));
        var definition = DurableWork.DefineWithExecutionPolicy(
            workName, "v1", workCodec, resultCodec, providerSafety, executionPolicy);
        return new DurableWorkRegistration<byte[], byte[], StoredWorkExecutor>(definition.Snapshot, frozen: true);
    }

    private static async Task<StoredPolicy> ReadStoredPolicyAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string workName)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT request_fingerprint_schema, execution_policy_schema, attempt_plan_version,
                   attempt_plan_offsets, maximum_circuit_microseconds, execution_not_after,
                   accepted_at, due_at
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_name = @work_name;
            """);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_name", workName);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new StoredPolicy(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<long[]>(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static void AssertAttemptPlan(StoredPolicy stored)
    {
        Assert.Equal("appsurface.durable.work.enqueue.v2", stored.FingerprintSchema);
        Assert.Equal("work-execution-v1", stored.ExecutionPolicySchema);
        Assert.Equal("attempt-plan-v1", stored.AttemptPlanVersion);
        Assert.Equal(Offsets.Select(static offset => offset.Ticks / 10), stored.AttemptPlanOffsetsMicroseconds);
        Assert.Equal(TimeSpan.FromMinutes(240).Ticks / 10, stored.MaximumCircuitMicroseconds);
        Assert.Null(stored.ExecutionNotAfter);
        Assert.Equal(AcceptanceAnchor, stored.AcceptedAtUtc);
        Assert.Equal(AcceptanceAnchor, stored.DueAtUtc);
    }

    private sealed record StoredPolicy(
        string FingerprintSchema,
        string? ExecutionPolicySchema,
        string? AttemptPlanVersion,
        long[]? AttemptPlanOffsetsMicroseconds,
        long? MaximumCircuitMicroseconds,
        DateTimeOffset? ExecutionNotAfter,
        DateTimeOffset AcceptedAtUtc,
        DateTimeOffset DueAtUtc);

    private sealed class StoredWorkExecutor : IDurableWorkerExecutor<byte[], byte[]>
    {
        public ValueTask<byte[]> ExecuteAsync(
            DurableWorkerEnvelope<byte[]> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Array.Empty<byte>());
    }

    private sealed class ActivityFlowRegistration(
        IDurablePayloadCodec contextCodec,
        DurableWorkRegistration workRegistration,
        IDurablePayloadCodec<byte[]> workCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.derived-flow";
        public override string FlowVersion => "v1";
        public override string ImplementationVersion => "tests-derived-flow-v1";
        public override string StartNodeId => "activity";
        public override string DefinitionFingerprint => new('d', 64);
        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;
        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];
        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations { get; } = [workRegistration];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DurableFlowEvaluationResult(
                FlowTransitionKind.Activity,
                input.NodeId,
                input.Context,
                null,
                null,
                null,
                null,
                new DurableFlowActivityCommand(
                    "call-provider",
                    1,
                    workRegistration.WorkName,
                    workRegistration.WorkVersion,
                    workRegistration.ProviderSafety,
                    workCodec.EncodeObject(new byte[] { 7 }))));
    }
}
