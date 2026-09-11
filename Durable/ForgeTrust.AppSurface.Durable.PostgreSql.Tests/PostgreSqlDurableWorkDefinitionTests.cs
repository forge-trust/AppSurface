using System.Text;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableWorkDefinitionTests
{
    private const string WorkName = "tests.postgresql.typed-definition";
    private const string WorkVersion = "v1";
    private const string InputContract = "tests.postgresql.typed-definition.input";
    private const string ResultContract = "tests.postgresql.typed-definition.result";

    [Fact]
    public async Task DefinitionRequest_AndDirectRequestShareAcceptanceIdentityAndPersistedFacts()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = await InitializeAsync(database);
        var definition = CreateDefinition(CreatePolicy(maximumAttempts: 7));
        var scope = new DurableScopeId("typed-definition-factory-scope");
        var command = new DurableCommandId("typed-definition-factory-command");
        const string idempotencyKey = "typed-definition-factory-key";
        var dueAt = new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.FromHours(-4));
        var request = definition.CreateRequest(scope, command, idempotencyKey, new ProofWork("alpha"), dueAtUtc: dueAt);

        await using var provider = CreateProvider(database, schema, definition);
        var writer = provider.GetRequiredService<IDurableWorkTransactionWriter>();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        DurableWorkId acceptedWorkId;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var accepted = await writer.EnqueueAsync(transaction, request);
            Assert.True(accepted.IsSuccess);
            Assert.Equal(DurableWorkAcceptanceKind.Accepted, accepted.Value!.Kind);
            acceptedWorkId = accepted.Value.WorkId;
            await transaction.CommitAsync();
        }
        AssertPersistedMatches(request, await ReadPersistedWorkAsync(database.DataSource, scope, acceptedWorkId));

        var direct = new DurableWorkRequest(
            scope,
            command,
            idempotencyKey,
            definition.WorkName,
            definition.WorkVersion,
            definition.WorkCodec.Encode(new ProofWork("alpha")),
            definition.ProviderSafety,
            definition.DefaultRetryPolicy,
            dueAt.ToUniversalTime());

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var duplicate = await writer.EnqueueAsync(transaction, direct);
            Assert.True(duplicate.IsSuccess);
            Assert.Equal(DurableWorkAcceptanceKind.Duplicate, duplicate.Value!.Kind);
            await transaction.CommitAsync();
        }

        AssertPersistedMatches(request, await ReadPersistedWorkAsync(database.DataSource, scope, acceptedWorkId));
    }

    [Fact]
    public async Task ChangedDefinitionDefaultWithoutOverride_Conflicts_WhileExplicitOriginalOverrideRestoresDuplicate()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = await InitializeAsync(database);
        var originalPolicy = CreatePolicy(maximumAttempts: 5);
        var changedPolicy = CreatePolicy(maximumAttempts: 6);
        var definition = CreateDefinition(originalPolicy);
        var changedDefinition = CreateDefinition(changedPolicy);
        await using var provider = CreateProvider(database, schema, definition);
        var writer = provider.GetRequiredService<IDurableWorkTransactionWriter>();
        var scope = new DurableScopeId("typed-definition-conflict-scope");
        var command = new DurableCommandId("typed-definition-conflict-command");
        const string key = "typed-definition-conflict-key";

        var original = definition.CreateRequest(scope, command, key, new ProofWork("original"));
        await EnqueueAndCommitAsync(writer, database.DataSource, original);
        var originalWork = await FindWorkIdAsync(database.DataSource, scope);
        var originalPersisted = await ReadPersistedWorkAsync(database.DataSource, scope, originalWork);
        AssertPersistedMatches(original, originalPersisted);

        var changedDefaultRequest = changedDefinition.CreateRequest(scope, command, key, new ProofWork("original"));
        Assert.Equal(changedPolicy, changedDefaultRequest.RetryPolicy);
        var changedDefaultResult = await EnqueueAsync(writer, database.DataSource, changedDefaultRequest);
        Assert.False(changedDefaultResult.IsSuccess);
        Assert.Equal(DurableProblemCodes.CommandConflict, changedDefaultResult.Problem!.Code);
        AssertPersistedMatches(originalPersisted, await ReadPersistedWorkAsync(database.DataSource, scope, originalWork));

        var explicitPolicyConflict = definition.CreateRequest(
            scope,
            command,
            key,
            new ProofWork("original"),
            retryPolicy: changedPolicy);
        var explicitPolicyResult = await EnqueueAsync(writer, database.DataSource, explicitPolicyConflict);
        Assert.False(explicitPolicyResult.IsSuccess);
        Assert.Equal(DurableProblemCodes.CommandConflict, explicitPolicyResult.Problem!.Code);
        AssertPersistedMatches(originalPersisted, await ReadPersistedWorkAsync(database.DataSource, scope, originalWork));

        var payloadConflict = definition.CreateRequest(scope, command, key, new ProofWork("changed"));
        var payloadResult = await EnqueueAsync(writer, database.DataSource, payloadConflict);
        Assert.False(payloadResult.IsSuccess);
        Assert.Equal(DurableProblemCodes.CommandConflict, payloadResult.Problem!.Code);
        AssertPersistedMatches(originalPersisted, await ReadPersistedWorkAsync(database.DataSource, scope, originalWork));

        var explicitOverride = changedDefinition.CreateRequest(
            scope,
            command,
            key,
            new ProofWork("original"),
            retryPolicy: originalPolicy);
        var duplicate = await EnqueueAsync(writer, database.DataSource, explicitOverride);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(DurableWorkAcceptanceKind.Duplicate, duplicate.Value!.Kind);
        Assert.Equal(originalWork, duplicate.Value.WorkId);
        AssertPersistedMatches(originalPersisted, await ReadPersistedWorkAsync(database.DataSource, scope, originalWork));
    }

    [Fact]
    public async Task TransactionWriter_RollbackRemovesWorkAndCallerDomainRows()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = await InitializeAsync(database);
        var definition = CreateDefinition(CreatePolicy());
        await using var provider = CreateProvider(database, schema, definition);
        var writer = provider.GetRequiredService<IDurableWorkTransactionWriter>();
        var request = definition.CreateRequest(
            new DurableScopeId("typed-definition-rollback-scope"),
            new DurableCommandId("typed-definition-rollback-command"),
            "typed-definition-rollback-key",
            new ProofWork("rollback"));

        await using (var setup = database.DataSource.CreateCommand("CREATE TABLE domain_fact (fact_id text PRIMARY KEY);"))
        {
            await setup.ExecuteNonQueryAsync();
        }

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var domain = new NpgsqlCommand(
            "INSERT INTO domain_fact (fact_id) VALUES ('rolled-back');",
            connection,
            transaction))
        {
            await domain.ExecuteNonQueryAsync();
        }

        var accepted = await writer.EnqueueAsync(transaction, request);
        Assert.True(accepted.IsSuccess);
        await transaction.RollbackAsync();

        Assert.Equal(0, await ExecuteScalarAsync<long>(database.DataSource, "SELECT count(*) FROM domain_fact;"));
        Assert.Equal(0, await CountWorkAsync(database.DataSource, request.ScopeId));
    }

    [Fact]
    public async Task DefinitionBinding_UsesBoundedRuntimePumpAndReturnsExactTypedResult()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = await InitializeAsync(database);
        var definition = CreateDefinition(CreatePolicy(maximumAttempts: 3));
        var services = new ServiceCollection();
        services.AddDurableWork(definition.ExecutedBy<ProofExecutor>());
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(schema.RuntimeEpoch, schema.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "typed-definition-runtime-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDurableWorkClient>();
        var request = definition.CreateRequest(
            new DurableScopeId("typed-definition-runtime-scope"),
            new DurableCommandId("typed-definition-runtime-command"),
            "typed-definition-runtime-key",
            new ProofWork("runtime"));

        var accepted = await client.EnqueueAsync(request);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(DurableWorkAcceptanceKind.Accepted, accepted.Value!.Kind);

        var pump = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));
        Assert.Equal(1, pump.Discovered);
        Assert.Equal(1, pump.Claimed);
        Assert.Equal(1, pump.Processed);
        Assert.Equal(0, pump.Failed);

        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(request.ScopeId, accepted.Value.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.Value!.State);
        Assert.Equal(new ProofResult("processed:runtime"), definition.ResultCodec.Decode(snapshot.Value.Result!));
    }

    private static DurableWorkDefinition<ProofWork, ProofResult> CreateDefinition(DurableWorkRetryPolicy policy) =>
        DurableWork.Define(
            WorkName,
            WorkVersion,
            new ProofCodec<ProofWork>(InputContract),
            new ProofCodec<ProofResult>(ResultContract),
            DurableProviderSafety.Idempotent,
            policy);

    private static DurableWorkRetryPolicy CreatePolicy(int maximumAttempts = 4) => new(
        maximumAttempts,
        TimeSpan.FromMinutes(12),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(2),
        "exponential-v1");

    private static ServiceProvider CreateProvider(
        PostgreSqlIntegrationTestDatabase database,
        PostgreSqlDurableRuntimeSchemaStatus schema,
        DurableWorkDefinition<ProofWork, ProofResult> definition)
    {
        var services = new ServiceCollection();
        services.AddDurableWork(definition.ExecutedBy<ProofExecutor>());
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(schema.RuntimeEpoch, schema.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options => options.SendWakeNotifications = false);
        return services.BuildServiceProvider();
    }

    private static async ValueTask<PostgreSqlDurableRuntimeSchemaStatus> InitializeAsync(
        PostgreSqlIntegrationTestDatabase database)
    {
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "typed-definition-tests", "issue-800");
        var status = await schema.GetStatusAsync();
        return new PostgreSqlDurableRuntimeSchemaStatus(epoch, status.StoreId);
    }

    private static async ValueTask EnqueueAndCommitAsync(
        IDurableWorkTransactionWriter writer,
        NpgsqlDataSource dataSource,
        DurableWorkRequest request)
    {
        var result = await EnqueueAsync(writer, dataSource, request);
        Assert.True(result.IsSuccess);
        Assert.Equal(DurableWorkAcceptanceKind.Accepted, result.Value!.Kind);
    }

    private static async ValueTask<DurableOperationResult<DurableWorkAcceptance>> EnqueueAsync(
        IDurableWorkTransactionWriter writer,
        NpgsqlDataSource dataSource,
        DurableWorkRequest request)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await writer.EnqueueAsync(transaction, request);
        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask<PersistedWork> ReadPersistedWorkAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setScope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            setScope.Parameters.AddWithValue("scope_id", scope.Value);
            await setScope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT payload, encode(payload_sha256, 'hex'), contract_id, payload_schema_version, codec_id,
                   payload_classification, payload_retention, provider_safety, due_at,
                   request_fingerprint_schema, request_fingerprint_sha256, maximum_attempts, maximum_elapsed,
                   backoff_algorithm, initial_retry_delay, maximum_retry_delay, lease_duration,
                   lease_renewal_cadence, maximum_lease_lifetime
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        PersistedWork result;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            result = new PersistedWork(
                reader.GetFieldValue<byte[]>(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt32(11),
                reader.GetFieldValue<TimeSpan>(12),
                reader.GetString(13),
                reader.GetFieldValue<TimeSpan>(14),
                reader.GetFieldValue<TimeSpan>(15),
                reader.GetFieldValue<TimeSpan>(16),
                reader.GetFieldValue<TimeSpan>(17),
                reader.GetFieldValue<TimeSpan>(18));
        }
        await transaction.CommitAsync();
        return result;
    }

    private static void AssertPersistedMatches(DurableWorkRequest request, PersistedWork persisted)
    {
        Assert.Equal(request.Payload.Content.ToArray(), persisted.Payload);
        Assert.Equal(request.Payload.Sha256, persisted.PayloadSha256);
        Assert.Equal(request.Payload.ContractName, persisted.ContractId);
        Assert.Equal(request.Payload.ContractVersion, persisted.PayloadSchemaVersion);
        Assert.Equal($"{request.Payload.ContractName}@{request.Payload.ContractVersion}", persisted.CodecId);
        Assert.Equal("approved_application", persisted.PayloadClassification);
        Assert.Equal(request.Payload.RetentionPolicyId, persisted.PayloadRetention);
        Assert.Equal(request.ProviderSafety.ToString(), persisted.ProviderSafety, ignoreCase: true);
        if (request.DueAtUtc is { } dueAtUtc)
        {
            Assert.Equal(dueAtUtc, persisted.DueAtUtc);
        }
        else
        {
            Assert.NotEqual(default, persisted.DueAtUtc);
        }
        Assert.Equal(request.Fingerprint.SchemaId, persisted.FingerprintSchema);
        Assert.Equal(request.Fingerprint.Sha256, persisted.FingerprintSha256);
        Assert.Equal(request.RetryPolicy.MaximumAttempts, persisted.MaximumAttempts);
        Assert.Equal(request.RetryPolicy.MaximumElapsedTime, persisted.MaximumElapsed);
        Assert.Equal(request.RetryPolicy.BackoffAlgorithm, persisted.BackoffAlgorithm);
        Assert.Equal(request.RetryPolicy.InitialRetryDelay, persisted.InitialRetryDelay);
        Assert.Equal(request.RetryPolicy.MaximumRetryDelay, persisted.MaximumRetryDelay);
        Assert.Equal(request.RetryPolicy.LeaseDuration, persisted.LeaseDuration);
        Assert.Equal(request.RetryPolicy.RenewalCadence, persisted.LeaseRenewalCadence);
        Assert.Equal(request.RetryPolicy.MaximumLeaseLifetime, persisted.MaximumLeaseLifetime);
    }

    private static void AssertPersistedMatches(PersistedWork expected, PersistedWork actual)
    {
        Assert.Equal(expected.Payload, actual.Payload);
        Assert.Equal(expected.PayloadSha256, actual.PayloadSha256);
        Assert.Equal(expected.ContractId, actual.ContractId);
        Assert.Equal(expected.PayloadSchemaVersion, actual.PayloadSchemaVersion);
        Assert.Equal(expected.CodecId, actual.CodecId);
        Assert.Equal(expected.PayloadClassification, actual.PayloadClassification);
        Assert.Equal(expected.PayloadRetention, actual.PayloadRetention);
        Assert.Equal(expected.ProviderSafety, actual.ProviderSafety);
        Assert.Equal(expected.DueAtUtc, actual.DueAtUtc);
        Assert.Equal(expected.FingerprintSchema, actual.FingerprintSchema);
        Assert.Equal(expected.FingerprintSha256, actual.FingerprintSha256);
        Assert.Equal(expected.MaximumAttempts, actual.MaximumAttempts);
        Assert.Equal(expected.MaximumElapsed, actual.MaximumElapsed);
        Assert.Equal(expected.BackoffAlgorithm, actual.BackoffAlgorithm);
        Assert.Equal(expected.InitialRetryDelay, actual.InitialRetryDelay);
        Assert.Equal(expected.MaximumRetryDelay, actual.MaximumRetryDelay);
        Assert.Equal(expected.LeaseDuration, actual.LeaseDuration);
        Assert.Equal(expected.LeaseRenewalCadence, actual.LeaseRenewalCadence);
        Assert.Equal(expected.MaximumLeaseLifetime, actual.MaximumLeaseLifetime);
    }

    private static async ValueTask<DurableWorkId> FindWorkIdAsync(NpgsqlDataSource dataSource, DurableScopeId scope)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setScope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            setScope.Parameters.AddWithValue("scope_id", scope.Value);
            await setScope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT work_id FROM appsurface_durable.work WHERE scope_id = @scope_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        var workId = (string?)await command.ExecuteScalarAsync();
        Assert.NotNull(workId);
        await transaction.CommitAsync();
        return new DurableWorkId(workId!);
    }

    private static async ValueTask<long> CountWorkAsync(NpgsqlDataSource dataSource, DurableScopeId scope)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setScope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            setScope.Parameters.AddWithValue("scope_id", scope.Value);
            await setScope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM appsurface_durable.work WHERE scope_id = @scope_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private static async ValueTask<T> ExecuteScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed record PostgreSqlDurableRuntimeSchemaStatus(Guid RuntimeEpoch, Guid StoreId);

    private sealed record PersistedWork(
        byte[] Payload,
        string PayloadSha256,
        string ContractId,
        string PayloadSchemaVersion,
        string CodecId,
        string PayloadClassification,
        string PayloadRetention,
        string ProviderSafety,
        DateTimeOffset DueAtUtc,
        string FingerprintSchema,
        string FingerprintSha256,
        int MaximumAttempts,
        TimeSpan MaximumElapsed,
        string BackoffAlgorithm,
        TimeSpan InitialRetryDelay,
        TimeSpan MaximumRetryDelay,
        TimeSpan LeaseDuration,
        TimeSpan LeaseRenewalCadence,
        TimeSpan MaximumLeaseLifetime);

    private sealed record ProofWork(string Value);

    private sealed record ProofResult(string Value);

    private sealed class ProofCodec<T>(string contractName) : IDurablePayloadCodec<T>
    {
        public Type PayloadType => typeof(T);

        public string ContractName { get; } = contractName;

        public string ContractVersion => WorkVersion;

        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(T value) => new(
            ContractName,
            ContractVersion,
            Classification,
            Encoding.UTF8.GetBytes(value switch
            {
                ProofWork work => work.Value,
                ProofResult result => result.Value,
                _ => throw new InvalidOperationException($"Unexpected proof payload type '{typeof(T)}'."),
            }),
            RetentionPolicyId);

        public T Decode(DurableEncodedPayload payload)
        {
            var value = Encoding.UTF8.GetString(payload.Content.Span);
            return (T)(object)(typeof(T) == typeof(ProofWork)
                ? new ProofWork(value)
                : typeof(T) == typeof(ProofResult)
                    ? new ProofResult(value)
                    : throw new InvalidOperationException($"Unexpected proof payload type '{typeof(T)}'."));
        }

        public DurableEncodedPayload EncodeObject(object value) => Encode((T)value);

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload)!;
    }

    private sealed class ProofExecutor : IDurableWorkerExecutor<ProofWork, ProofResult>
    {
        public ValueTask<ProofResult> ExecuteAsync(
            DurableWorkerEnvelope<ProofWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProofResult($"processed:{work.Payload!.Value}"));
    }
}
