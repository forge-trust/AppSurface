using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

[Collection("PostgreSQL reference evidence")]
public sealed class PostgreSqlDurableWorkExecutionProcessTests
{
    private static readonly DateTimeOffset Anchor = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = Anchor.AddHours(1);
    private static readonly DurableProviderSafety[] SafetyClasses = Enum.GetValues<DurableProviderSafety>();
    private static readonly ExecutionCheckpoint[] Checkpoints = Enum.GetValues<ExecutionCheckpoint>();

    public static IEnumerable<object[]> CrashCases =>
        from checkpoint in Checkpoints
        from safety in SafetyClasses
        from afterCutoff in new[] { false, true }
        select new object[] { checkpoint, safety, afterCutoff };

    [Theory]
    [MemberData(nameof(CrashCases))]
    public async Task FreshProcessLoss_RecoversFromDatabaseTruthAtEachCheckpoint(
        ExecutionCheckpoint checkpoint,
        DurableProviderSafety safety,
        bool afterCutoff)
    {
        await using var lab = await Lab.CreateAsync(safety);
        var accepted = await lab.Client.EnqueueAsync(lab.Request);
        Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);
        var acceptance = accepted.Value!;

        var report = await RunUntilCheckpointAndTerminateAsync(
            lab.Database.ConnectionString,
            lab.Epoch,
            lab.StoreId,
            safety,
            lab.ScopeId,
            acceptance.WorkId,
            checkpoint);

        Assert.Equal(checkpoint.ToString(), report.Checkpoint);
        Assert.Equal(lab.ScopeId.Value, report.ScopeId);
        Assert.Equal(acceptance.WorkId.Value, report.WorkId);
        Assert.Equal(1, report.AttemptNumber);

        var wasAdmitted = checkpoint >= ExecutionCheckpoint.AfterAdmission;
        var providerWasInvoked = checkpoint is ExecutionCheckpoint.AfterProviderMutation or ExecutionCheckpoint.BeforeCompletion;
        Assert.Equal(wasAdmitted, report.InvocationAdmitted);
        Assert.Equal(wasAdmitted, report.ExactPermitReplayAttempted);
        Assert.False(report.ExactPermitReplayAdmitted);
        Assert.Equal(checkpoint == ExecutionCheckpoint.BeforePermit, report.ProviderKey is null);

        Assert.Equal(providerWasInvoked ? 1L : 0L, await CountAsync(
            lab.Database.DataSource,
            "SELECT count(*) FROM public.issue765_provider_invocation;"));
        Assert.Equal(providerWasInvoked ? 1L : 0L, await CountAsync(
            lab.Database.DataSource,
            "SELECT count(*) FROM public.issue765_provider_effect WHERE applied;"));
        Assert.Equal(wasAdmitted ? 1L : 0L, await CountAdmissionMarkersAsync(
            lab.Database.DataSource,
            lab.ScopeId,
            acceptance.WorkId));

        var retrySafe = safety is DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed;
        var canRecover = !afterCutoff && (!wasAdmitted || retrySafe);
        var restartTime = afterCutoff
            ? Deadline.AddTicks(10)
            : report.LeaseExpiresAtUtc.AddTicks(10);
        Assert.Equal(afterCutoff, restartTime >= Deadline);
        await lab.Database.SetExecutionTimeAsync(restartTime);

        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        var recovered = await lab.Store.TryClaimAsync(candidate, "issue765-fresh-restart");
        if (!canRecover)
        {
            Assert.Null(recovered);
            var stored = await ReadExecutionStateAsync(lab.Database.DataSource, lab.ScopeId, acceptance.WorkId);
            Assert.Equal(
                wasAdmitted ? DurableProblemCodes.AmbiguousExternalOutcome : DurableProblemCodes.ExecutionDeadlineReached,
                stored.TerminalCode);
            Assert.Equal(wasAdmitted, stored.State == DurableWorkState.Suspended);
            Assert.Equal(providerWasInvoked ? 1L : 0L, await CountAsync(
                lab.Database.DataSource,
                "SELECT count(*) FROM public.issue765_provider_invocation;"));
            Assert.Equal(providerWasInvoked ? 1L : 0L, await CountAsync(
                lab.Database.DataSource,
                "SELECT count(*) FROM public.issue765_provider_effect WHERE applied;"));
            return;
        }

        Assert.NotNull(recovered);
        Assert.Equal(report.ActivityId, recovered.ActivityId);
        Assert.Equal(2, recovered.AttemptNumber);
        Assert.NotNull(recovered.Execution);
        Assert.Equal(Anchor.AddSeconds(1), recovered.Execution!.NextEligibilityAtUtc);

        var registration = ExecutionCheckpointHost.CreateRegistration(safety, lab.Database.DataSource);
        var permit = await lab.Store.TryAcquireEffectPermitAsync(recovered);
        Assert.NotNull(permit);
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit!));
        var prepared = DurableProviderWorkAdapter.Prepare(
            registration,
            EmptyServiceProvider.Instance,
            permit!.Claim.ToProviderClaim());
        var result = await prepared.InvokeAsync();
        var completion = await lab.Store.RecordCompletionAsync(
            permit.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "issue765-process-proof-completed",
                "{}",
                result));
        Assert.Equal(DurableWorkState.Succeeded, completion.State);

        var expectedInvocationCount = providerWasInvoked ? 2L : 1L;
        Assert.Equal(expectedInvocationCount, await CountAsync(
            lab.Database.DataSource,
            "SELECT count(*) FROM public.issue765_provider_invocation;"));
        Assert.Equal(1L, await CountAsync(
            lab.Database.DataSource,
            "SELECT count(*) FROM public.issue765_provider_effect WHERE applied;"));
        var invocationFacts = await ReadProviderInvocationsAsync(lab.Database.DataSource);
        Assert.Equal(1, invocationFacts.ProviderKeyCount);
        Assert.All(invocationFacts.ProviderKeys, key => Assert.Equal(invocationFacts.ProviderKeys[0], key));
        Assert.All(invocationFacts.ActivityIds, activityId => Assert.Equal(report.ActivityId, activityId));
        var expectedAttempts = providerWasInvoked ? new[] { 1, 2 } : [2];
        Assert.Equal(expectedAttempts, invocationFacts.AttemptNumbers);
        if (report.ProviderKey is not null)
        {
            Assert.All(invocationFacts.ProviderKeys, key => Assert.Equal(report.ProviderKey, key));
        }

        // Each admitted permit retains its own marker; replaying its exact prior permit never adds one.
        Assert.Equal(wasAdmitted ? 2L : 1L,
            await CountAdmissionMarkersAsync(lab.Database.DataSource, lab.ScopeId, acceptance.WorkId));
    }

    private static async ValueTask<ExecutionCheckpointReport> RunUntilCheckpointAndTerminateAsync(
        string connectionString,
        Guid runtimeEpoch,
        Guid storeId,
        DurableProviderSafety safety,
        DurableScopeId scopeId,
        DurableWorkId workId,
        ExecutionCheckpoint checkpoint)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(ExecutionCheckpointHost).Assembly.Location);
        startInfo.ArgumentList.Add("execution-checkpoint");
        startInfo.ArgumentList.Add(runtimeEpoch.ToString("D"));
        startInfo.ArgumentList.Add(storeId.ToString("D"));
        startInfo.ArgumentList.Add(safety.ToString());
        startInfo.ArgumentList.Add(scopeId.Value);
        startInfo.ArgumentList.Add(workId.Value);
        startInfo.ArgumentList.Add(checkpoint.ToString());
        startInfo.Environment["APPSURFACE_POSTGRES_REFERENCE_CONNECTION"] = connectionString;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The execution-checkpoint child process could not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var standardErrorDrain = process.StandardError.ReadToEndAsync();
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                await process.WaitForExitAsync(timeout.Token);
                _ = await standardErrorDrain.WaitAsync(timeout.Token);
                throw new InvalidOperationException(
                    $"The execution-checkpoint child exited before {checkpoint} (exit code {process.ExitCode}).");
            }

            var report = JsonSerializer.Deserialize<ExecutionCheckpointReport>(line)
                ?? throw new InvalidOperationException("The execution-checkpoint child returned an invalid report.");
            await KillAndWaitAsync(process, timeout.Token);
            _ = await standardErrorDrain.WaitAsync(timeout.Token);
            return report;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The execution-checkpoint child did not reach {checkpoint} within 30 seconds.");
        }
        finally
        {
            if (!process.HasExited)
            {
                await KillAndWaitAsync(process, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
        }
    }

    private static async ValueTask KillAndWaitAsync(Process process, CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                return;
            }
        }

        await process.WaitForExitAsync(cancellationToken);
    }

    private static async ValueTask<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<long> CountAdmissionMarkersAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var configureScope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            configureScope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await configureScope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM appsurface_durable.effect_permit
            WHERE scope_id = @scope_id AND work_id = @work_id AND invocation_admitted_at IS NOT NULL;
            """, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<(DurableWorkState State, string? TerminalCode)> ReadExecutionStateAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var configureScope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            configureScope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await configureScope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("""
            SELECT state, terminal_code
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var state = ParseWorkState(reader.GetString(0));
        var terminalCode = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (state, terminalCode);
    }

    private static DurableWorkState ParseWorkState(string persistedState) => persistedState switch
    {
        "failed" => DurableWorkState.FailedTerminal,
        "suspended_ambiguous_external_outcome" or "suspended_reconciliation_required"
            or "suspended_manual_resolution" or "suspended_contract_unavailable" => DurableWorkState.Suspended,
        _ => throw new InvalidOperationException("The recovered Work is not in a terminal or suspended state."),
    };

    private static async ValueTask<ProviderInvocationFacts> ReadProviderInvocationsAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                count(DISTINCT provider_key),
                COALESCE(array_agg(provider_key ORDER BY invocation_id), ARRAY[]::text[]),
                COALESCE(array_agg(activity_id ORDER BY invocation_id), ARRAY[]::text[]),
                COALESCE(array_agg(attempt_number ORDER BY invocation_id), ARRAY[]::integer[])
            FROM public.issue765_provider_invocation;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ProviderInvocationFacts(
            reader.GetInt64(0),
            reader.GetFieldValue<string[]>(1),
            reader.GetFieldValue<string[]>(2),
            reader.GetFieldValue<int[]>(3));
    }

    private sealed record ProviderInvocationFacts(
        long ProviderKeyCount,
        string[] ProviderKeys,
        string[] ActivityIds,
        int[] AttemptNumbers);

    private sealed class Lab : IAsyncDisposable
    {
        private Lab(
            PostgreSqlIntegrationTestDatabase database,
            Guid epoch,
            Guid storeId,
            DurableScopeId scopeId,
            DurableWorkRequest request,
            PostgreSqlDurableWorkClient client,
            PostgreSqlDurableWorkStore store)
        {
            Database = database;
            Epoch = epoch;
            StoreId = storeId;
            ScopeId = scopeId;
            Request = request;
            Client = client;
            Store = store;
        }

        internal PostgreSqlIntegrationTestDatabase Database { get; }

        internal Guid Epoch { get; }

        internal Guid StoreId { get; }

        internal DurableScopeId ScopeId { get; }

        internal DurableWorkRequest Request { get; }

        internal PostgreSqlDurableWorkClient Client { get; }

        internal PostgreSqlDurableWorkStore Store { get; }

        internal static async Task<Lab> CreateAsync(DurableProviderSafety safety)
        {
            var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
            try
            {
                var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
                await schema.ApplyAsync();
                await database.SetExecutionTimeAsync(Anchor);
                var status = await schema.GetStatusAsync();
                var epoch = Guid.NewGuid();
                await schema.InitializeRuntimeEpochAsync(epoch, "issue765-process-proof", "initial-activation");
                await using (var setup = database.DataSource.CreateCommand("""
                    CREATE TABLE public.issue765_provider_invocation (
                        invocation_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                        provider_key text NOT NULL,
                        activity_id text NOT NULL,
                        attempt_number integer NOT NULL
                    );
                    CREATE TABLE public.issue765_provider_effect (
                        provider_key text PRIMARY KEY,
                        activity_id text NOT NULL,
                        applied boolean NOT NULL
                    );
                    """))
                {
                    await setup.ExecuteNonQueryAsync();
                }

                var scopeId = new DurableScopeId($"issue765-process-{Guid.NewGuid():N}");
                var registration = ExecutionCheckpointHost.CreateRegistration(safety, database.DataSource);
                var registry = new DurableWorkRegistry([registration]);
                var retry = new DurableWorkRetryPolicy(
                    maximumAttempts: 3,
                    maximumElapsedTime: TimeSpan.FromDays(1),
                    initialRetryDelay: TimeSpan.FromSeconds(1),
                    maximumRetryDelay: TimeSpan.FromMinutes(1),
                    leaseDuration: TimeSpan.FromSeconds(5),
                    renewalCadence: TimeSpan.FromSeconds(1),
                    maximumLeaseLifetime: TimeSpan.FromMinutes(1),
                    backoffAlgorithm: "exponential-v1");
                var attemptPlan = new DurableAttemptPlan(
                    "attempt-plan-v1",
                    [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
                    TimeSpan.FromHours(2));
                var executionPolicy = DurableWorkExecutionPolicy.ForAttemptPlan(retry, attemptPlan);
                var request = DurableWorkRequest.CreateWithExecutionPolicy(
                    scopeId,
                    new DurableCommandId($"command-{Guid.NewGuid():N}"),
                    $"key-{Guid.NewGuid():N}",
                    registration.WorkName,
                    registration.WorkVersion,
                    registration.WorkCodec.EncodeObject(new byte[] { 7, 6, 5 }),
                    safety,
                    executionPolicy,
                    new DurableExecutionDeadline(Deadline));
                var options = new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
                return new Lab(
                    database,
                    epoch,
                    status.StoreId,
                    scopeId,
                    request,
                    new PostgreSqlDurableWorkClient(database.DataSource, registry, options),
                    new PostgreSqlDurableWorkStore(database.DataSource, epoch));
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        internal static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
