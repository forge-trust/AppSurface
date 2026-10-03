using System.Text;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>Evidence for a disposable candidate-package worker, including its checked graceful shutdown.</summary>
internal sealed record FixtureActivationEvidence(
    Guid StoreId, Guid RuntimeEpoch, string WorkerId, Guid WorkerInstanceId,
    string ScopeId, string WorkId, bool HeartbeatObserved, bool WorkCompleted,
    bool DrainPersisted, bool AdmissionClosed, bool HostedServicesStopped, bool SessionsReleased);

/// <summary>Deterministic lifecycle checkpoints exposed only to in-assembly tests.</summary>
internal enum FixtureActivationCheckpoint
{
    /// <summary>The real hosted worker entered the activation Work handler before it returned a result.</summary>
    WorkInvocationStartedBeforeCompletion,

    /// <summary>Host stop, persisted drain, closed admission, and owned-session release were all verified.</summary>
    DrainVerified,
}

/// <summary>Test-only asynchronous observer/barrier for fixture activation lifecycle checkpoints.</summary>
internal delegate ValueTask FixtureActivationCheckpointHook(
    FixtureActivationCheckpoint checkpoint,
    CancellationToken cancellationToken);

/// <summary>Activates the public PostgreSQL worker host on an already initialized disposable fixture.</summary>
/// <remarks>
/// The controller must retain its continuously monitored shared guard through activation and drain, and pass
/// its guard-linked token here. This helper never migrates or changes the epoch. Start returns only after the
/// production host has admitted a worker and committed one uniquely scoped Work result. Keep the lease alive
/// until the guarded callback finishes, then await DrainAsync with an independent cleanup token before accepting
/// evidence. Guard loss stops the host and makes the lease ineligible for successful evidence even if drain works.
/// </remarks>
internal static class FixtureActivationProof
{
    internal static async Task<FixtureActivationLease> StartAsync(
        string ownerConnectionString,
        LaneProofRolePair runtimePair,
        Guid storeId,
        Guid epoch,
        CancellationToken guardLinkedCancellationToken) =>
        await StartAsync(ownerConnectionString, runtimePair, storeId, epoch, guardLinkedCancellationToken, null)
            .ConfigureAwait(false);

    /// <summary>
    /// Starts the same fixture with a deterministic lifecycle observer for tests; production callers use the
    /// five-argument overload.
    /// </summary>
    internal static async Task<FixtureActivationLease> StartAsync(
        string ownerConnectionString,
        LaneProofRolePair runtimePair,
        Guid storeId,
        Guid epoch,
        CancellationToken guardLinkedCancellationToken,
        FixtureActivationCheckpointHook? testHook)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        ArgumentNullException.ThrowIfNull(runtimePair);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePair.DispatcherRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePair.RuntimeRole);
        if (storeId == Guid.Empty || epoch == Guid.Empty)
            throw new ArgumentException("Fixture activation requires an initialized StoreId and epoch.");
        guardLinkedCancellationToken.ThrowIfCancellationRequested();

        var lease = new FixtureActivationLease(
            ownerConnectionString,
            runtimePair,
            storeId,
            epoch,
            guardLinkedCancellationToken,
            testHook);
        try
        {
            await lease.StartAsync().ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Owns the actual host and all of its nonpooled connections until checked drain and disposal.</summary>
internal sealed class FixtureActivationLease : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly NpgsqlDataSource _owner;
    private readonly NpgsqlDataSource _dispatcher;
    private readonly NpgsqlDataSource _runtime;
    private readonly IHost _host;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ActivationWorkRegistration _work;
    private readonly CancellationToken _guardToken;
    private readonly CancellationTokenRegistration _guardRegistration;
    private readonly FixtureActivationCheckpointHook? _testHook;
    private readonly string _applicationName = $"fixture-activation-{Guid.NewGuid():N}";
    private readonly string _workerId = $"fixture-activation-{Guid.NewGuid():N}";
    private readonly DurableScopeId _scope = new($"fixture-activation-{Guid.NewGuid():N}");
    private readonly Guid _storeId;
    private readonly Guid _epoch;
    private readonly object _drainGate = new();
    private readonly TaskCompletionSource _startupFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _drainTask;
    private FixtureActivationEvidence? _evidence;
    private DurableWorkId _workId;
    private Guid _workerInstanceId;
    private bool _startAttempted;
    private int _guardLost;

    internal FixtureActivationLease(
        string ownerConnectionString,
        LaneProofRolePair runtimePair,
        Guid storeId,
        Guid epoch,
        CancellationToken guardToken,
        FixtureActivationCheckpointHook? testHook)
    {
        _storeId = storeId;
        _epoch = epoch;
        _guardToken = guardToken;
        _testHook = testHook;
        _work = new ActivationWorkRegistration(testHook);
        NpgsqlDataSource? owner = null;
        NpgsqlDataSource? dispatcher = null;
        NpgsqlDataSource? runtime = null;
        IHost? host = null;
        try
        {
            _owner = owner = CreateDataSource(ownerConnectionString, null, null, $"{_applicationName}-observer");
            _dispatcher = dispatcher = CreateDataSource(
                ownerConnectionString,
                runtimePair.DispatcherRole,
                runtimePair.DispatcherPassword,
                _applicationName);
            _runtime = runtime = CreateDataSource(
                ownerConnectionString,
                runtimePair.RuntimeRole,
                runtimePair.RuntimePassword,
                _applicationName);
            _host = host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);
                services.AddSingleton<DurableWorkRegistration>(_work);
                services.AddAppSurfaceDurablePostgreSql(_dispatcher, _runtime,
                    new PostgreSqlDurableWorkOptions(epoch, storeId),
                    new PostgreSqlDurableScheduleOptions(runtimePair.RuntimeRole), options =>
                    {
                        options.WorkerId = _workerId;
                        options.HostedSurfaces = DurableRuntimeSurface.Work;
                        options.MaximumItemsPerPass = 1;
                        options.TimeBudgetPerPass = TimeSpan.FromSeconds(1);
                        options.ShutdownReserve = TimeSpan.FromSeconds(2);
                        options.IdlePollingInterval = TimeSpan.FromMilliseconds(100);
                        options.SendWakeNotifications = false;
                        options.EnableHeartbeatMaintenance = false;
                    }).AddWorkerHost();
                }).Build();
            _lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            _guardRegistration = guardToken.Register(() =>
            {
                Interlocked.Exchange(ref _guardLost, 1);
                // Close production process admission immediately, then stop and drain independently of this token.
                _lifetime.StopApplication();
                _ = DrainAfterGuardLossAsync();
            });
        }
        catch
        {
            try { host?.Dispose(); }
            finally
            {
                try { runtime?.Dispose(); }
                finally
                {
                    try { dispatcher?.Dispose(); }
                    finally { owner?.Dispose(); }
                }
            }
            throw;
        }
    }

    /// <summary>Gets evidence only after successful activation, checked drain, and physical session cleanup.</summary>
    internal FixtureActivationEvidence Evidence
    {
        get
        {
            ThrowIfGuardLost();
            return _drainTask?.IsCompletedSuccessfully == true && _evidence is { DrainPersisted: true, SessionsReleased: true } evidence
                ? evidence : throw new InvalidOperationException("Fixture activation and checked drain have not completed.");
        }
    }

    internal async Task StartAsync()
    {
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            _startupFinished.TrySetResult();
        }
    }

    private async Task StartCoreAsync()
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_guardToken);
        startup.CancelAfter(TimeSpan.FromSeconds(30));
        var token = startup.Token;
        await AssertIdentityAsync(token);
        var accepted = await _host.Services.GetRequiredService<IDurableWorkClient>().EnqueueAsync(
            new DurableWorkRequest(_scope, DurableCommandId.New(), $"{_scope.Value}-key",
                ActivationWorkRegistration.Name, "v1", _work.WorkCodec.EncodeObject(Encoding.UTF8.GetBytes("activate")),
                DurableProviderSafety.Idempotent), token);
        if (!accepted.IsSuccess)
            throw new InvalidOperationException("Fixture activation could not enqueue its Work probe.");
        _workId = accepted.Value!.WorkId;
        ThrowIfGuardLost();
        _startAttempted = true;
        await _host.StartAsync(token);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.ApplicationStopping);
        while (true)
        {
            stopping.Token.ThrowIfCancellationRequested();
            var snapshot = await _host.Services.GetRequiredService<IDurableWorkControlClient>().GetAsync(
                new DurableWorkGetRequest(_scope, _workId), stopping.Token);
            if (!snapshot.IsSuccess)
                throw new InvalidOperationException("Fixture activation could not read its Work probe.");
            if (snapshot.Value!.State == DurableWorkState.Succeeded)
                break;
            await Task.Delay(50, stopping.Token);
        }
        await AssertCompletedWorkAsync(token);
        // Work finalization precedes the pass's sweep marker. Observe that actual marker rather than racing it.
        while (true)
        {
            stopping.Token.ThrowIfCancellationRequested();
            var heartbeat = await ReadHeartbeatAsync(stopping.Token);
            if (heartbeat is { } observed)
            {
                if (observed.Instance == Guid.Empty || observed.Draining || observed.Epoch != _epoch)
                    throw new InvalidOperationException("Fixture activation observed an invalid worker heartbeat.");
                if (observed.SuccessfulSweep)
                {
                    _workerInstanceId = observed.Instance;
                    break;
                }
            }
            await Task.Delay(50, stopping.Token);
        }
        if (_work.Invocations != 1)
            throw new InvalidOperationException("Fixture activation did not execute exactly one Work probe.");
        var health = await _host.Services.GetRequiredService<IDurableRuntimeHealth>().GetAsync(stopping.Token);
        if (!health.SchemaCompatible || !health.EpochCompatible || health.IsDraining
            || health.WorkerInstanceId != _workerInstanceId || health.ConfiguredRuntimeEpoch != _epoch
            || health.ActiveRuntimeEpoch != _epoch || health.LastSuccessfulSweepAtUtc is null)
            throw new InvalidOperationException("Fixture activation has no matching public runtime health observation.");
        await AssertIdentityAsync(token);
        ThrowIfGuardLost();
        _evidence = new(_storeId, _epoch, _workerId, _workerInstanceId, _scope.Value, _workId.Value,
            true, true, false, false, false, false);
    }

    /// <summary>Stops the real host, checks durable drain and closed admission, and releases owned sessions.</summary>
    /// <remarks>
    /// Cleanup has a fresh 15-second bound independent of guard loss. The supplied token bounds the caller's wait;
    /// it never cancels the owned cleanup task. A failed persisted marker, unfinished host, identity drift, or guard
    /// loss throws instead of producing successful evidence. Repeated calls share the same terminal cleanup task.
    /// </remarks>
    internal async Task<FixtureActivationEvidence> DrainAsync(CancellationToken independentCleanupToken)
    {
        await EnsureDrainStarted().WaitAsync(independentCleanupToken).ConfigureAwait(false);
        return Evidence;
    }

    public async ValueTask DisposeAsync()
    {
        await EnsureDrainStarted();
    }

    private async Task DrainAfterGuardLossAsync()
    {
        await _startupFinished.Task;
        try
        {
            await EnsureDrainStarted();
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            // The terminal cleanup task retains the failure for DrainAsync/DisposeAsync. No evidence is issued.
        }
    }

    private Task EnsureDrainStarted()
    {
        lock (_drainGate)
            return _drainTask ??= DrainCoreAsync();
    }

    private async Task DrainCoreAsync()
    {
        try
        {
            await StopAndObserveAsync();
        }
        finally
        {
            // Also release the observer if StopAsync, drain-marker verification, or any nested disposal fails.
            await _owner.DisposeAsync();
        }
    }

    private async Task StopAndObserveAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cleanup.Token;
        var drained = false;
        var stopped = false;
        var closed = false;
        try
        {
            if (_startAttempted)
            {
                _lifetime.StopApplication();
                await _host.StopAsync(token);
                stopped = _host.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                    .All(service => service.ExecuteTask is null || service.ExecuteTask.IsCompleted && !service.ExecuteTask.IsFaulted);
                if (!stopped)
                    throw new InvalidOperationException("Fixture activation left an unfinished or failed hosted service.");
                var heartbeat = await ReadHeartbeatAsync(token);
                drained = heartbeat is not null && heartbeat.Value.Epoch == _epoch
                    && (_workerInstanceId == Guid.Empty || heartbeat.Value.Instance == _workerInstanceId)
                    && heartbeat.Value.Draining && !heartbeat.Value.PassActive;
                if (!drained)
                    throw new InvalidOperationException("Fixture activation did not persist a completed worker drain.");
                var attempt = await _host.Services.GetRequiredService<IDurableRuntimePumpAdmission>().TryRunOnceAsync(
                    new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work), token);
                closed = attempt.Kind == DurableRuntimePumpAttemptKind.Refused;
                if (!closed)
                    throw new InvalidOperationException("Fixture activation admitted a new pass after host shutdown.");
                await AssertIdentityAsync(token);
            }
        }
        finally
        {
            // Registration disposal synchronizes with the guard callback before the host lifetime is disposed.
            await _guardRegistration.DisposeAsync();
            try
            {
                if (_host is IAsyncDisposable asyncHost)
                    await asyncHost.DisposeAsync();
                else
                    _host.Dispose();
            }
            finally
            {
                try { await _dispatcher.DisposeAsync(); }
                finally { await _runtime.DisposeAsync(); }
            }
        }
        while (true)
        {
            await using var sessions = _owner.CreateCommand(
                "SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE application_name = @application;");
            sessions.Parameters.AddWithValue("application", _applicationName);
            if (await sessions.ExecuteScalarAsync(token) is not long count)
                throw new InvalidOperationException("Fixture activation could not observe worker session cleanup.");
            if (count == 0)
                break;
            await Task.Delay(25, token);
        }
        if (_evidence is { } evidence)
            _evidence = evidence with
            {
                DrainPersisted = drained, AdmissionClosed = closed, HostedServicesStopped = stopped, SessionsReleased = true
            };
        await NotifyTestHookAsync(FixtureActivationCheckpoint.DrainVerified, token).AsTask()
            .WaitAsync(token).ConfigureAwait(false);
    }

    private void ThrowIfGuardLost()
    {
        if (Volatile.Read(ref _guardLost) != 0 || _guardToken.IsCancellationRequested)
            throw new OperationCanceledException("Fixture activation lost its controller guard.", _guardToken);
    }

    private async Task AssertIdentityAsync(CancellationToken token)
    {
        var status = await new PostgreSqlDurableRuntimeSchemaManager(_owner).GetStatusAsync(token);
        if (!status.IsCompatible || status.StoreId != _storeId || status.ActiveRuntimeEpoch != _epoch)
            throw new InvalidOperationException("Fixture activation observed incompatible or changed StoreId/epoch.");
    }

    private async Task<(Guid Instance, Guid Epoch, bool Draining, bool PassActive, bool SuccessfulSweep)?> ReadHeartbeatAsync(
        CancellationToken token)
    {
        await using var command = _owner.CreateCommand(
            """
            SELECT worker_instance_id, runtime_epoch, draining, pass_active,
                   last_heartbeat_at IS NOT NULL AND last_successful_sweep_at IS NOT NULL
            FROM appsurface_durable.runtime_heartbeat WHERE worker_id = @worker;
            """);
        command.Parameters.AddWithValue("worker", _workerId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var result = (reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4));
        if (await reader.ReadAsync(token))
            throw new InvalidOperationException("Fixture activation observed duplicate worker heartbeats.");
        return result;
    }

    private async Task AssertCompletedWorkAsync(CancellationToken token)
    {
        await using var connection = await _owner.OpenConnectionAsync(token);
        await using (var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope, false);", connection))
        {
            scope.Parameters.AddWithValue("scope", _scope.Value);
            await scope.ExecuteNonQueryAsync(token);
        }
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) = 1 FROM appsurface_durable.work
            WHERE scope_id = @scope AND work_id = @work AND state = 'succeeded'
              AND terminal_at IS NOT NULL AND result_payload = @result
              AND EXISTS (SELECT 1 FROM appsurface_durable.work_history history
                WHERE history.scope_id = @scope AND history.work_id = @work AND history.runtime_epoch = @epoch
                  AND history.event_type = 'completion_succeeded');
            """, connection);
        command.Parameters.AddWithValue("scope", _scope.Value);
        command.Parameters.AddWithValue("work", _workId.Value);
        command.Parameters.AddWithValue("result", Encoding.UTF8.GetBytes("fixture-activated"));
        command.Parameters.AddWithValue("epoch", _epoch);
        if (await command.ExecuteScalarAsync(token) is not true)
            throw new InvalidOperationException("Fixture activation did not commit its expected Work result and history.");
    }

    private ValueTask NotifyTestHookAsync(FixtureActivationCheckpoint checkpoint, CancellationToken token) =>
        _testHook is null ? ValueTask.CompletedTask : _testHook(checkpoint, token);

    private static NpgsqlDataSource CreateDataSource(string cs, string? role, string? password, string applicationName)
    {
        var settings = new NpgsqlConnectionStringBuilder(cs)
        {
            Pooling = false, Enlist = false, Multiplexing = false, ApplicationName = applicationName,
            Timeout = 5, CommandTimeout = 5, CancellationTimeout = -1
        };
        if (role is not null) { settings.Username = role; settings.Password = password; }
        return NpgsqlDataSource.Create(settings.ConnectionString);
    }

    private sealed class ActivationWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.postgresql-preflight.fixture-activation";
        private readonly FixtureActivationCheckpointHook? _testHook;
        private int _invocations;
        internal int Invocations => Volatile.Read(ref _invocations);
        internal ActivationWorkRegistration(FixtureActivationCheckpointHook? testHook) : base(
            Name,
            "v1",
            DurableProviderSafety.Idempotent,
            new ActivationCodec("fixture-activation-input"),
            new ActivationCodec("fixture-activation-result")) => _testHook = testHook;
        public override bool CanReconcile => false;
        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            if (!((byte[])WorkCodec.DecodeObject(work.Payload)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("activate")))
                throw new InvalidOperationException("Fixture activation received unexpected Work input.");
            return new ActivationPreparedWork(
                this,
                ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("fixture-activated")),
                _testHook);
        }
        public override ValueTask<DurableEncodedPayload> InvokeAsync(IServiceProvider services,
            DurableWorkExecutionContext work, CancellationToken cancellationToken = default) => Prepare(services, work).InvokeAsync(cancellationToken);
        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(IServiceProvider services,
            DurableWorkExecutionContext work, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fixture activation Work is idempotent and does not reconcile.");

        private sealed class ActivationPreparedWork(
            ActivationWorkRegistration registration,
            DurableEncodedPayload result,
            FixtureActivationCheckpointHook? testHook) : DurablePreparedWork
        {
            public override async ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
            {
                if (testHook is not null)
                {
                    await testHook(
                        FixtureActivationCheckpoint.WorkInvocationStartedBeforeCompletion,
                        cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref registration._invocations);
                return result;
            }
        }
    }

    private sealed class ActivationCodec(string name) : IDurablePayloadCodec<byte[]>
    {
        public Type PayloadType => typeof(byte[]);
        public string ContractName => name;
        public string ContractVersion => "v1";
        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;
        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;
        public DurableEncodedPayload Encode(byte[] value) => new(ContractName, ContractVersion, Classification, value, RetentionPolicyId);
        public byte[] Decode(DurableEncodedPayload payload)
        {
            if (payload.ContractName != ContractName || payload.ContractVersion != ContractVersion
                || payload.Classification != Classification || payload.RetentionPolicyId != RetentionPolicyId)
                throw new InvalidOperationException("Fixture activation payload violates its codec contract.");
            return payload.Content.ToArray();
        }
        public DurableEncodedPayload EncodeObject(object value) => value is byte[] bytes ? Encode(bytes)
            : throw new ArgumentException("Fixture activation codec requires a byte array.", nameof(value));
        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }
}
