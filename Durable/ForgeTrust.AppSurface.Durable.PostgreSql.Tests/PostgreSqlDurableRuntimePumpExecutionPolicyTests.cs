using System.Text;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkExecutionPolicyTests;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRuntimePumpExecutionPolicyTests
{
    [Fact]
    public async Task Internal_checkpoint_hook_validates_and_delivers_immutable_observations()
    {
        await PostgreSqlDurableExecutionCheckpointHook.NoOp.ReachAsync(
            PostgreSqlDurableExecutionCheckpointName.BeforePermit,
            1);

        PostgreSqlDurableExecutionCheckpointObservation? observed = null;
        CancellationToken observedToken = default;
        using var cancellation = new CancellationTokenSource();
        var hook = new PostgreSqlDurableExecutionCheckpointHook((observation, token) =>
        {
            observed = observation;
            observedToken = token;
            return ValueTask.CompletedTask;
        });
        await hook.ReachAsync(
            PostgreSqlDurableExecutionCheckpointName.BeforeProviderCall,
            2,
            cancellation.Token);

        Assert.True(observed.HasValue);
        Assert.Equal(PostgreSqlDurableExecutionCheckpointName.BeforeProviderCall, observed.Value.Name);
        Assert.Equal(2, observed.Value.AttemptNumber);
        Assert.Equal(cancellation.Token, observedToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await hook.ReachAsync((PostgreSqlDurableExecutionCheckpointName)99, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await hook.ReachAsync(PostgreSqlDurableExecutionCheckpointName.BeforePermit, 0));
    }

    [Fact]
    public async Task Invocation_admission_refusal_after_deadline_advance_does_not_call_executor_and_persists_proof()
    {
        var registration = new PumpPolicyRegistration();
        await using var lab = await PumpLab.CreateAsync(registration);
        var deadline = Anchor.AddMinutes(1);
        var checkpoints = new List<PostgreSqlDurableExecutionCheckpointName>();
        var hook = new PostgreSqlDurableExecutionCheckpointHook(async (observation, _) =>
        {
            checkpoints.Add(observation.Name);
            if (observation.Name == PostgreSqlDurableExecutionCheckpointName.BeforeInvocationAdmission)
            {
                await lab.Database.SetExecutionTimeAsync(deadline);
            }
        });
        var pump = lab.CreatePump(hook, new FakeTimeProvider(Anchor));
        var accepted = await lab.EnqueueAsync("admission-refused", deadline);

        var result = await pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Deferred);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, registration.Execution.InvocationCount);
        Assert.Equal(
            new[]
            {
                PostgreSqlDurableExecutionCheckpointName.BeforePermit,
                PostgreSqlDurableExecutionCheckpointName.AfterPermitCommit,
                PostgreSqlDurableExecutionCheckpointName.BeforeInvocationAdmission,
            },
            checkpoints);

        var snapshot = await lab.GetAsync(accepted.WorkId);
        Assert.Equal(DurableWorkState.FailedTerminal, snapshot.State);
        Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, snapshot.TerminalCode);
        Assert.Equal("deadline_elapsed", await lab.ReadWorkTextAsync(accepted.WorkId, "execution_admission_closed_reason"));
        var permit = await lab.ReadPermitAsync(accepted.WorkId);
        Assert.Equal("proven_no_effect", permit.Status);
        Assert.Null(permit.InvocationAdmittedAtUtc);
    }

    [Fact]
    public async Task Circuit_cutoff_after_admission_does_not_cancel_current_invocation()
    {
        var registration = new PumpPolicyRegistration();
        await using var lab = await PumpLab.CreateAsync(registration);
        var circuitCutoff = Anchor.AddMinutes(6);
        var checkpoints = new List<PostgreSqlDurableExecutionCheckpointName>();
        var hook = new PostgreSqlDurableExecutionCheckpointHook(async (observation, _) =>
        {
            checkpoints.Add(observation.Name);
            if (observation.Name == PostgreSqlDurableExecutionCheckpointName.AfterInvocationAdmission)
            {
                await lab.Database.SetExecutionTimeAsync(circuitCutoff.AddSeconds(1));
            }
        });
        var pump = lab.CreatePump(hook, new FakeTimeProvider(Anchor));
        var accepted = await lab.EnqueueAsync("circuit-current", deadline: null, circuitMinutes: 6);

        var result = await pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, registration.Execution.InvocationCount);
        Assert.False(registration.Execution.InvocationTokenWasCanceled);
        Assert.Equal(
            new[]
            {
                PostgreSqlDurableExecutionCheckpointName.BeforePermit,
                PostgreSqlDurableExecutionCheckpointName.AfterPermitCommit,
                PostgreSqlDurableExecutionCheckpointName.BeforeInvocationAdmission,
                PostgreSqlDurableExecutionCheckpointName.AfterInvocationAdmission,
                PostgreSqlDurableExecutionCheckpointName.BeforeCompletion,
            },
            checkpoints);
        var snapshot = await lab.GetAsync(accepted.WorkId);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.State);
        Assert.Equal(circuitCutoff, snapshot.Execution!.AdmissionCutoffUtc);
        Assert.Equal("completed", snapshot.TerminalCode);
        var permit = await lab.ReadPermitAsync(accepted.WorkId);
        Assert.Equal("known_succeeded", permit.Status);
        Assert.NotNull(permit.InvocationAdmittedAtUtc);
    }

    [Fact]
    public async Task Deadline_cancels_an_admitted_cooperative_invocation_and_records_ambiguous_outcome()
    {
        var registration = new PumpPolicyRegistration(InvocationMode.WaitForCancellation);
        var time = new FakeTimeProvider(Anchor);
        await using var lab = await PumpLab.CreateAsync(registration);
        var deadline = Anchor.AddMinutes(1);
        var hook = new PostgreSqlDurableExecutionCheckpointHook(async (observation, _) =>
        {
            if (observation.Name == PostgreSqlDurableExecutionCheckpointName.BeforeCompletion)
            {
                await lab.Database.SetExecutionTimeAsync(deadline);
            }
        });
        var pump = lab.CreatePump(hook, time);
        var accepted = await lab.EnqueueAsync("deadline-cancel", deadline);
        var running = pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();

        await registration.Execution.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(deadline - Anchor);
        await registration.Execution.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, registration.Execution.InvocationCount);
        var snapshot = await lab.GetAsync(accepted.WorkId);
        Assert.Equal(DurableWorkState.Suspended, snapshot.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, snapshot.TerminalCode);
        var permit = await lab.ReadPermitAsync(accepted.WorkId);
        Assert.Equal("ambiguous", permit.Status);
        Assert.NotNull(permit.InvocationAdmittedAtUtc);
        Assert.Equal("deadline_elapsed", await lab.ReadWorkTextAsync(accepted.WorkId, "execution_admission_closed_reason"));
    }

    [Fact]
    public async Task Oversized_renewal_cadence_remains_deadline_bounded_without_abandoning_admitted_invocation()
    {
        var registration = new PumpPolicyRegistration(InvocationMode.WaitForCancellation);
        var time = new FakeTimeProvider(Anchor);
        await using var lab = await PumpLab.CreateAsync(registration);
        var deadline = Anchor.AddMinutes(1);
        var retry = new DurableWorkRetryPolicy(5, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1), TimeSpan.FromDays(4_000_000), TimeSpan.FromDays(3_999_999),
            TimeSpan.FromDays(4_000_000), "exponential-v1");
        var source = Request("oversized-cadence", deadline: deadline);
        var request = DurableWorkRequest.CreateWithExecutionPolicy(source.ScopeId, source.CommandId, source.IdempotencyKey,
            source.WorkName, source.WorkVersion, source.Payload, source.ProviderSafety,
            DurableWorkExecutionPolicy.FromRetryPolicy(retry), new(deadline));
        var accepted = await lab.Client.EnqueueAsync(request);
        Assert.True(accepted.IsSuccess);
        var hook = new PostgreSqlDurableExecutionCheckpointHook(async (observation, _) =>
        {
            if (observation.Name == PostgreSqlDurableExecutionCheckpointName.BeforeCompletion)
                await lab.Database.SetExecutionTimeAsync(deadline);
        });
        var running = lab.CreatePump(hook, time).RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.Execution.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(deadline - Anchor);
        await registration.Execution.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, registration.Execution.InvocationCount);
        var snapshot = await lab.GetAsync(accepted.Value!.WorkId);
        Assert.Equal(DurableWorkState.Suspended, snapshot.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, snapshot.TerminalCode);
    }

    [Fact]
    public async Task Finite_executor_that_ignores_deadline_cancellation_returns_only_quarantined_late_evidence()
    {
        var registration = new PumpPolicyRegistration(InvocationMode.IgnoreCancellation);
        var time = new FakeTimeProvider(Anchor);
        await using var lab = await PumpLab.CreateAsync(registration);
        var deadline = Anchor.AddMinutes(1);
        var hook = new PostgreSqlDurableExecutionCheckpointHook(async (observation, _) =>
        {
            if (observation.Name == PostgreSqlDurableExecutionCheckpointName.BeforeCompletion)
            {
                await lab.Database.SetExecutionTimeAsync(deadline);
            }
        });
        var pump = lab.CreatePump(hook, time);
        var accepted = await lab.EnqueueAsync("deadline-late-result", deadline);
        var running = pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();

        await registration.Execution.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(deadline - Anchor);
        await registration.Execution.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registration.Execution.Complete.TrySetResult(registration.EncodedResultCodec.EncodeObject(Encoding.UTF8.GetBytes("late result")));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, result.Failed);
        var snapshot = await lab.GetAsync(accepted.WorkId);
        Assert.Equal(DurableWorkState.Suspended, snapshot.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, snapshot.TerminalCode);
        Assert.Null(snapshot.Result);
        var permit = await lab.ReadPermitAsync(accepted.WorkId);
        Assert.Equal("ambiguous", permit.Status);
        Assert.NotNull(permit.InvocationAdmittedAtUtc);
        Assert.Equal(
            1,
            await lab.ReadWorkLongAsync(
                accepted.WorkId,
                "SELECT count(*) FROM appsurface_durable.work_history WHERE scope_id = @scope_id AND work_id = @work_id AND event_type = 'late_completion_succeeded';"));
        Assert.Equal(
            Encoding.UTF8.GetBytes("late result"),
            await lab.ReadLateObservationAsync(accepted.WorkId));
    }

    [Theory]
    [InlineData(PreparationFailure.Prepare)]
    [InlineData(PreparationFailure.Decode)]
    [InlineData(PreparationFailure.DependencyResolution)]
    public async Task Preparation_decode_and_dependency_failures_are_recorded_before_any_permit(PreparationFailure failure)
    {
        var registration = new PumpPolicyRegistration(prepareFailure: failure);
        await using var lab = await PumpLab.CreateAsync(registration);
        var hookNames = new List<PostgreSqlDurableExecutionCheckpointName>();
        var hook = new PostgreSqlDurableExecutionCheckpointHook((observation, _) =>
        {
            hookNames.Add(observation.Name);
            return ValueTask.CompletedTask;
        });
        var pump = lab.CreatePump(hook, new FakeTimeProvider(Anchor));
        var accepted = await lab.EnqueueAsync($"pre-permit-{failure}", Anchor.AddMinutes(30));

        var result = await pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, registration.Execution.InvocationCount);
        Assert.Equal(new[] { PostgreSqlDurableExecutionCheckpointName.BeforeCompletion }, hookNames);
        var snapshot = await lab.GetAsync(accepted.WorkId);
        Assert.Equal(DurableWorkState.Suspended, snapshot.State);
        Assert.Equal(DurableProblemCodes.WorkContractUnavailable, snapshot.TerminalCode);
        Assert.Equal(0, await lab.ReadWorkLongAsync(
            accepted.WorkId,
            "SELECT count(*) FROM appsurface_durable.effect_permit WHERE scope_id = @scope_id AND work_id = @work_id;"));
    }

    public enum PreparationFailure
    {
        Prepare,
        Decode,
        DependencyResolution,
    }

    internal enum InvocationMode
    {
        Complete,
        WaitForCancellation,
        IgnoreCancellation,
    }

    private sealed class PumpLab : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly DurableScopeId _scope = new("execution-tests");

        private PumpLab(
            PostgreSqlIntegrationTestDatabase database,
            ServiceProvider services,
            DurableWorkRegistration registration)
        {
            Database = database;
            _services = services;
            Registration = registration;
        }

        internal PostgreSqlIntegrationTestDatabase Database { get; }

        internal DurableWorkRegistration Registration { get; }

        internal IDurableWorkClient Client => _services.GetRequiredService<IDurableWorkClient>();

        internal async Task<DurableWorkAcceptance> EnqueueAsync(string command, DateTimeOffset? deadline, int circuitMinutes = 240)
        {
            var accepted = await Client.EnqueueAsync(Request(command, deadline: deadline, circuitMinutes: circuitMinutes, offsets: [0, 5]));
            Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);
            return accepted.Value!;
        }

        internal PostgreSqlDurableRuntimePump CreatePump(
            PostgreSqlDurableExecutionCheckpointHook checkpoints,
            TimeProvider timeProvider) =>
            new(
                _services.GetRequiredService<PostgreSqlDurableRuntimeRegistration>(),
                _services.GetRequiredService<IDurableRuntimeSchemaManager>(),
                _services.GetRequiredService<PostgreSqlDurableRuntimeHealth>(),
                _services.GetRequiredService<PostgreSqlDurableWorkStore>(),
                _services.GetRequiredService<PostgreSqlDurableFlowProcessor>(),
                _services.GetRequiredService<PostgreSqlDurableScheduleProcessor>(),
                _services.GetRequiredService<IDurableWorkRegistry>(),
                new PostgreSqlDurableWorkContractSelection(_services.GetRequiredService<IDurableWorkRegistry>()),
                _services.GetRequiredService<IServiceScopeFactory>(),
                _services.GetRequiredService<IDurableRuntimeExecutionBoundary>(),
                _services.GetRequiredService<DurableRuntimeAdmissionGate>(),
                NullLogger<PostgreSqlDurableRuntimePump>.Instance,
                passExecutor: null,
                heartbeatMaintenance: _services.GetRequiredService<PostgreSqlDurableHeartbeatMaintenance>(),
                timeProvider: timeProvider,
                executionCheckpoints: checkpoints);

        internal async Task<DurableWorkSnapshot> GetAsync(DurableWorkId workId)
        {
            var result = await _services.GetRequiredService<IDurableWorkControlClient>()
                .GetAsync(new DurableWorkGetRequest(_scope, workId));
            Assert.True(result.IsSuccess, result.Problem?.Problem);
            return result.Value!;
        }

        internal async Task<string> ReadWorkTextAsync(DurableWorkId workId, string column)
        {
            await using var connection = await Database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope_id", _scope.Value);
                await scope.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand(
                $"SELECT {column} FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", _scope.Value);
            command.Parameters.AddWithValue("work_id", workId.Value);
            return Assert.IsType<string>(await command.ExecuteScalarAsync());
        }

        internal async Task<long> ReadWorkLongAsync(DurableWorkId workId, string sql)
        {
            await using var connection = await Database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope_id", _scope.Value);
                await scope.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", _scope.Value);
            command.Parameters.AddWithValue("work_id", workId.Value);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        internal async Task<(string Status, DateTimeOffset? InvocationAdmittedAtUtc)> ReadPermitAsync(DurableWorkId workId)
        {
            await using var connection = await Database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope_id", _scope.Value);
                await scope.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand(
                "SELECT status, invocation_admitted_at FROM appsurface_durable.effect_permit WHERE scope_id = @scope_id AND work_id = @work_id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", _scope.Value);
            command.Parameters.AddWithValue("work_id", workId.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
        }

        internal async Task<byte[]> ReadLateObservationAsync(DurableWorkId workId)
        {
            await using var connection = await Database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope_id", _scope.Value);
                await scope.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand(
                "SELECT observation_payload FROM appsurface_durable.work_history WHERE scope_id = @scope_id AND work_id = @work_id AND event_type = 'late_completion_succeeded';",
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", _scope.Value);
            command.Parameters.AddWithValue("work_id", workId.Value);
            return Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await Database.DisposeAsync();
        }

        internal static async Task<PumpLab> CreateAsync(PumpPolicyRegistration registration)
        {
            var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
            await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
            await database.SetExecutionTimeAsync(Anchor);
            var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
            var epoch = Guid.NewGuid();
            await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-execution-policy-tests", Guid.NewGuid().ToString("N"));
            var status = await schema.GetStatusAsync();
            var registry = new DurableWorkRegistry([registration]);
            var services = new ServiceCollection();
            services.AddSingleton<DurableWorkRegistration>(registration);
            services.AddSingleton<IDurableWorkRegistry>(registry);
            services.AddAppSurfaceDurablePostgreSql(
                database.DataSource,
                database.CreateDataSource(),
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                new PostgreSqlDurableScheduleOptions("appsurface"),
                options =>
                {
                    options.WorkerId = $"pump-execution-policy-{Guid.NewGuid():N}";
                    options.SendWakeNotifications = false;
                });
            return new(database, services.BuildServiceProvider(), registration);
        }
    }

    private sealed class PumpPolicyRegistration : DurableWorkRegistration
    {
        private readonly InvocationMode _invocationMode;
        private readonly PreparationFailure? _prepareFailure;

        internal PumpPolicyRegistration(
            InvocationMode invocationMode = InvocationMode.Complete,
            PreparationFailure? prepareFailure = null)
            : this(
                invocationMode,
                prepareFailure,
                new PumpPayloadCodec(prepareFailure == PreparationFailure.Decode))
        {
        }

        private PumpPolicyRegistration(
            InvocationMode invocationMode,
            PreparationFailure? prepareFailure,
            PumpPayloadCodec inputCodec)
            : base(
                PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent),
                "v1",
                DurableProviderSafety.Idempotent,
                inputCodec,
                new PostgreSqlOpaqueTestCodec("tests.delete-provider-access.result", "v1"))
        {
            _invocationMode = invocationMode;
            _prepareFailure = prepareFailure;
            EncodedResultCodec = base.ResultCodec;
        }

        internal PumpExecution Execution { get; } = new();

        internal IDurablePayloadCodec EncodedResultCodec { get; }

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            if (_prepareFailure == PreparationFailure.Prepare)
            {
                throw new InvalidOperationException("Simulated execution-policy preparation failure.");
            }

            _ = WorkCodec.DecodeObject(work.Payload);
            if (_prepareFailure == PreparationFailure.DependencyResolution)
            {
                _ = services.GetRequiredService<MissingPumpDependency>();
            }

            return new PumpPreparedWork(Execution, _invocationMode, EncodedResultCodec);
        }

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent pump policy Work does not reconcile.");
    }

    private sealed class PumpPayloadCodec(bool throwOnDecode) : IDurablePayloadCodec<byte[]>
    {
        private int _decodeCount;

        public Type PayloadType => typeof(byte[]);

        public string ContractName => "tests.delete-provider-access";

        public string ContractVersion => "v1";

        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(byte[] value) => new(ContractName, ContractVersion, Classification, value, RetentionPolicyId);

        public byte[] Decode(DurableEncodedPayload payload)
        {
            if (throwOnDecode && Interlocked.Increment(ref _decodeCount) > 1)
            {
                throw new InvalidDataException("Simulated payload decoder failure.");
            }

            return payload.Content.ToArray();
        }

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<byte[]>(value));

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }

    private sealed class PumpExecution
    {
        private int _invocationCount;
        private int _invocationTokenWasCanceled;

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<DurableEncodedPayload> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        internal bool InvocationTokenWasCanceled => Volatile.Read(ref _invocationTokenWasCanceled) != 0;

        internal ValueTask<DurableEncodedPayload> InvokeAsync(
            InvocationMode mode,
            DurableEncodedPayload result,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            if (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _invocationTokenWasCanceled, 1);
            }

            _ = cancellationToken.Register(static state => ((PumpExecution)state!).CancellationObserved.TrySetResult(), this);
            Started.TrySetResult();
            return mode switch
            {
                InvocationMode.Complete => ValueTask.FromResult(result),
                InvocationMode.WaitForCancellation => new ValueTask<DurableEncodedPayload>(WaitForCancellationAsync(cancellationToken)),
                InvocationMode.IgnoreCancellation => new ValueTask<DurableEncodedPayload>(Complete.Task),
                _ => throw new InvalidDataException($"Unknown test invocation mode '{mode}'."),
            };
        }

        private static async Task<DurableEncodedPayload> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The infinite cancellation wait unexpectedly completed.");
        }
    }

    private sealed class PumpPreparedWork(
        PumpExecution execution,
        InvocationMode mode,
        IDurablePayloadCodec resultCodec) : DurablePreparedWork
    {
        private readonly DurableEncodedPayload _result = resultCodec.EncodeObject(Encoding.UTF8.GetBytes("pump result"));

        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
            execution.InvokeAsync(mode, _result, cancellationToken);
    }

    private sealed class MissingPumpDependency
    {
    }
}
