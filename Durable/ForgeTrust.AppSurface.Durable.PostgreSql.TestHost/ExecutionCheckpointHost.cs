using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

/// <summary>Runs one existing opted-in Work to a named child-process crash boundary.</summary>
public static class ExecutionCheckpointHost
{
    private const string ConnectionEnvironmentVariable = "APPSURFACE_POSTGRES_REFERENCE_CONNECTION";

    /// <summary>Stable contract name used by the process proof and its parent fixture.</summary>
    public const string WorkName = "tests.issue765.execution-checkpoint";

    /// <summary>Version of the stable process-proof Work contract.</summary>
    public const string WorkVersion = "v1";

    /// <summary>Encoded input codec name used by the process-proof Work contract.</summary>
    public const string InputContractName = "tests.issue765.execution-checkpoint.input";

    /// <summary>Encoded result codec name used by the process-proof Work contract.</summary>
    public const string ResultContractName = "tests.issue765.execution-checkpoint.result";

    /// <summary>Creates the exact registration shared by the parent fixture and fresh child process.</summary>
    /// <param name="safety">Persisted provider-safety class under proof.</param>
    /// <param name="dataSource">Isolated PostgreSQL database used only by the fake provider if invoked.</param>
    /// <returns>A registration whose preparation is side-effect free.</returns>
    public static DurableWorkRegistration CreateRegistration(
        DurableProviderSafety safety,
        NpgsqlDataSource dataSource) => new ExecutionCheckpointRegistration(safety, dataSource);

    /// <summary>Runs the execution-checkpoint child protocol.</summary>
    /// <param name="args">Runtime epoch, store id, safety, scope id, Work id, and checkpoint name.</param>
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 6
            || !Guid.TryParse(args[0], out var runtimeEpoch)
            || !Guid.TryParse(args[1], out var storeId)
            || !Enum.TryParse<DurableProviderSafety>(args[2], out var safety)
            || !Enum.IsDefined(safety)
            || !Enum.TryParse<ExecutionCheckpoint>(args[5], out var stopAt)
            || !Enum.IsDefined(stopAt))
        {
            throw new ArgumentException(
                "Expected runtime epoch, store id, provider safety, scope id, Work id, and checkpoint.");
        }

        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionEnvironmentVariable} is required.");
        }

        var scopeId = new DurableScopeId(args[3]);
        var workId = new DurableWorkId(args[4]);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlDurableWorkStore(dataSource, runtimeEpoch);
        var candidate = (await store.DiscoverAsync(20)).Single(item =>
            item.ScopeId == scopeId && item.WorkId == workId);
        var claim = await store.TryClaimAsync(candidate, "issue765-checkpoint-child")
            ?? throw new InvalidOperationException("The checkpoint child could not claim the accepted Work.");

        await StopAtAsync(stopAt, ExecutionCheckpoint.BeforePermit, claim, null, false, false, false);

        var permit = await store.TryAcquireEffectPermitAsync(claim)
            ?? throw new InvalidOperationException("The checkpoint child could not commit the effect permit.");
        await StopAtAsync(stopAt, ExecutionCheckpoint.AfterPermit, permit.Claim, permit.ProviderKey, false, false, false);

        if (!await store.TryAdmitInvocationAsync(permit))
        {
            throw new InvalidOperationException("The checkpoint child was refused invocation admission.");
        }

        var replayAdmitted = await store.TryAdmitInvocationAsync(permit);
        await StopAtAsync(
            stopAt,
            ExecutionCheckpoint.AfterAdmission,
            permit.Claim,
            permit.ProviderKey,
            true,
            true,
            replayAdmitted);

        var registration = CreateRegistration(safety, dataSource);
        var prepared = DurableProviderWorkAdapter.Prepare(
            registration,
            EmptyServiceProvider.Instance,
            permit.Claim.ToProviderClaim());
        await StopAtAsync(
            stopAt,
            ExecutionCheckpoint.BeforeProvider,
            permit.Claim,
            permit.ProviderKey,
            true,
            true,
            replayAdmitted);

        var result = await prepared.InvokeAsync();
        await StopAtAsync(
            stopAt,
            ExecutionCheckpoint.AfterProviderMutation,
            permit.Claim,
            permit.ProviderKey,
            true,
            true,
            replayAdmitted);

        var completion = new PostgreSqlWorkCompletion(
            PostgreSqlWorkCompletionKind.Succeeded,
            "execution-checkpoint-completed",
            "{}",
            result);
        await StopAtAsync(
            stopAt,
            ExecutionCheckpoint.BeforeCompletion,
            permit.Claim,
            permit.ProviderKey,
            true,
            true,
            replayAdmitted);

        _ = await store.RecordCompletionAsync(permit.Claim, completion);
        throw new InvalidOperationException("The requested execution checkpoint was not reached.");
    }

    /// <summary>Records one committed fake-provider invocation and one idempotent logical mutation.</summary>
    /// <param name="dataSource">The isolated PostgreSQL integration-test database.</param>
    /// <param name="providerKey">Stable provider operation key.</param>
    /// <param name="activityId">Stable durable activity identity.</param>
    /// <param name="attemptNumber">Current provider-authoritative attempt.</param>
    public static async ValueTask RecordProviderMutationAsync(
        NpgsqlDataSource dataSource,
        string providerKey,
        string activityId,
        int attemptNumber)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var invocation = new NpgsqlCommand("""
            INSERT INTO public.issue765_provider_invocation (provider_key, activity_id, attempt_number)
            VALUES (@provider_key, @activity_id, @attempt_number);
            """, connection, transaction))
        {
            invocation.Parameters.AddWithValue("provider_key", providerKey);
            invocation.Parameters.AddWithValue("activity_id", activityId);
            invocation.Parameters.AddWithValue("attempt_number", attemptNumber);
            await invocation.ExecuteNonQueryAsync();
        }

        await using (var mutation = new NpgsqlCommand("""
            INSERT INTO public.issue765_provider_effect (provider_key, activity_id, applied)
            VALUES (@provider_key, @activity_id, true)
            ON CONFLICT (provider_key) DO NOTHING;
            """, connection, transaction))
        {
            mutation.Parameters.AddWithValue("provider_key", providerKey);
            mutation.Parameters.AddWithValue("activity_id", activityId);
            await mutation.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask StopAtAsync(
        ExecutionCheckpoint requested,
        ExecutionCheckpoint current,
        PostgreSqlDurableWorkClaim claim,
        string? providerKey,
        bool invocationAdmitted,
        bool exactPermitReplayAttempted,
        bool exactPermitReplayAdmitted)
    {
        if (requested != current)
        {
            return;
        }

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new ExecutionCheckpointReport(
            current.ToString(),
            claim.ScopeId.Value,
            claim.WorkId.Value,
            claim.ActivityId,
            providerKey,
            claim.AttemptNumber,
            claim.Revision,
            claim.LeaseExpiresAtUtc,
            invocationAdmitted,
            exactPermitReplayAttempted,
            exactPermitReplayAdmitted)));
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private sealed class ExecutionCheckpointRegistration(
        DurableProviderSafety safety,
        NpgsqlDataSource dataSource) : DurableWorkRegistration(
            ExecutionCheckpointHost.WorkName,
            ExecutionCheckpointHost.WorkVersion,
            safety,
            new ExecutionCheckpointCodec(ExecutionCheckpointHost.InputContractName),
            new ExecutionCheckpointCodec(ExecutionCheckpointHost.ResultContractName))
    {
        private readonly ExecutionCheckpointCodec _resultCodec = new(ExecutionCheckpointHost.ResultContractName);

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(work);
            return new ExecutionCheckpointPreparedWork(
                dataSource,
                work.ExecutionIdentity.ProviderKey,
                work.ExecutionIdentity.ActivityId,
                work.ExecutionIdentity.AttemptNumber,
                _resultCodec);
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
            throw new NotSupportedException("The execution checkpoint fake provider has no reconciler.");
    }

    private sealed class ExecutionCheckpointPreparedWork(
        NpgsqlDataSource dataSource,
        string providerKey,
        string activityId,
        int attemptNumber,
        ExecutionCheckpointCodec resultCodec) : DurablePreparedWork
    {
        public override async ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecordProviderMutationAsync(dataSource, providerKey, activityId, attemptNumber);
            return resultCodec.EncodeObject(new byte[] { 1 });
        }
    }

    private sealed class ExecutionCheckpointCodec(string contractName) : IDurablePayloadCodec
    {
        public Type PayloadType => typeof(byte[]);

        public string ContractName { get; } = contractName;

        public string ContractVersion => WorkVersion;

        public DurableDataClassification Classification => DurableDataClassification.Operational;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload EncodeObject(object value) => new(
            ContractName,
            ContractVersion,
            Classification,
            AssertBytes(value),
            RetentionPolicyId);

        public object DecodeObject(DurableEncodedPayload payload)
        {
            if (!string.Equals(payload.ContractName, ContractName, StringComparison.Ordinal)
                || !string.Equals(payload.ContractVersion, ContractVersion, StringComparison.Ordinal)
                || payload.Classification != Classification
                || !string.Equals(payload.RetentionPolicyId, RetentionPolicyId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The checkpoint payload does not match its registered codec.");
            }

            return payload.Content.ToArray();
        }

        private static byte[] AssertBytes(object value) => value as byte[]
            ?? throw new ArgumentException("The execution checkpoint codec accepts byte arrays only.", nameof(value));
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        internal static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>Process-loss checkpoints in execution order.</summary>
public enum ExecutionCheckpoint
{
    /// <summary>The process has claimed Work and has not acquired an effect permit.</summary>
    BeforePermit,

    /// <summary>The effect permit committed and invocation admission has not been attempted.</summary>
    AfterPermit,

    /// <summary>The one-use invocation marker committed and provider preparation has not begun.</summary>
    AfterAdmission,

    /// <summary>The provider invocation is prepared but no fake-provider mutation has occurred.</summary>
    BeforeProvider,

    /// <summary>The fake-provider mutation committed and completion has not been recorded.</summary>
    AfterProviderMutation,

    /// <summary>The encoded completion was constructed and is immediately before persistence.</summary>
    BeforeCompletion,
}

/// <summary>Credential-free child checkpoint facts consumed by the parent integration test.</summary>
public sealed record ExecutionCheckpointReport(
    string Checkpoint,
    string ScopeId,
    string WorkId,
    string ActivityId,
    string? ProviderKey,
    int AttemptNumber,
    long Revision,
    DateTimeOffset LeaseExpiresAtUtc,
    bool InvocationAdmitted,
    bool ExactPermitReplayAttempted,
    bool ExactPermitReplayAdmitted);
