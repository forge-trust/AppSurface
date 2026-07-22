using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

[Collection("PostgreSQL reference evidence")]
public sealed class DurableSlice3ReferenceWorkloadTests
{
    [Fact]
    public async Task CallerOwnedTransaction_CommitsOrRollsBackDomainFactWithWorkAcceptance()
    {
        var evidence = new ReferenceWorkloadEvidence("caller-owned-transaction");
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var status = await schema.GetStatusAsync();
        var runtimeEpoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(runtimeEpoch, "reference-test", "initial-activation");
        evidence.Record("application", "schema.apply-and-activate", "compatible", "migration-owner");
        await using (var setup = database.DataSource.CreateCommand("CREATE TABLE domain_fact (fact_id text PRIMARY KEY);"))
        {
            await setup.ExecuteNonQueryAsync();
        }

        var writer = new PostgreSqlDurableWorkTransactionWriter(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            new PostgreSqlDurableWorkOptions(runtimeEpoch, status.StoreId));
        var request = CreateRequest("reference-atomic", "reference-atomic-command");
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var rollback = await connection.BeginTransactionAsync())
        {
            await InsertDomainFactAsync(connection, rollback, "rolled-back");
            Assert.True((await writer.EnqueueAsync(rollback, request)).IsSuccess);
            await rollback.RollbackAsync();
        }

        Assert.Equal(0, await CountAsync(database.DataSource, "SELECT count(*) FROM domain_fact;"));
        Assert.Equal(0, await CountWorkAsync(database.DataSource, request.ScopeId));
        evidence.Record("transaction", "domain-mutation-and-work-acceptance", "rolled-back", "caller-owned");

        await using (var commit = await connection.BeginTransactionAsync())
        {
            await InsertDomainFactAsync(connection, commit, "committed");
            var accepted = await writer.EnqueueAsync(commit, request);
            Assert.True(accepted.IsSuccess);
            Assert.Equal(DurableWorkAcceptanceKind.Accepted, accepted.Value!.Kind);
            await commit.CommitAsync();
        }

        Assert.Equal(1, await CountAsync(database.DataSource, "SELECT count(*) FROM domain_fact;"));
        Assert.Equal(1, await CountWorkAsync(database.DataSource, request.ScopeId));
        evidence.Record("transaction", "domain-mutation-and-work-acceptance", "committed", "caller-owned");
        _ = await evidence.WriteAsync("accepted");
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, true, "succeeded")]
    [InlineData(DurableProviderSafety.ProviderKeyed, true, "succeeded")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, false, "suspended_manual_resolution")]
    public async Task ProcessLossAfterPermit_RecoversAccordingToProviderSafety(
        DurableProviderSafety providerSafety,
        bool shouldReclaim,
        string expectedState)
    {
        var evidence = new ReferenceWorkloadEvidence($"process-loss-{providerSafety.ToString().ToLowerInvariant()}");
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var status = await schema.GetStatusAsync();
        var runtimeEpoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(runtimeEpoch, "reference-test", "initial-activation");
        evidence.Record("application", "schema.apply-and-activate", "compatible", "migration-owner");

        var checkpoint = await RunUntilPermitAndTerminateAsync(
            database.ConnectionString,
            runtimeEpoch,
            status.StoreId,
            providerSafety);
        foreach (var childEvent in checkpoint.Events)
        {
            evidence.Record(
                "child-process",
                childEvent.Operation,
                childEvent.Outcome,
                childEvent.TransactionBoundary,
                childEvent.ElapsedMilliseconds);
        }

        evidence.Record("application", "process.force-terminate", "terminated-after-permit", "none");
        await Task.Delay(TimeSpan.FromMilliseconds(1_250));

        var store = new PostgreSqlDurableWorkStore(database.DataSource, runtimeEpoch);
        var candidate = (await store.DiscoverAsync(20)).Single(item =>
            item.WorkId.Value == checkpoint.WorkId && item.ScopeId.Value == checkpoint.ScopeId);
        var recovered = await store.TryClaimAsync(candidate, "reference-parent");
        evidence.Record(
            "recovery",
            "work.reclaim-after-lease-expiry",
            recovered is null ? "suspended-by-provider-safety" : "reclaimed",
            "provider-owned");

        if (shouldReclaim)
        {
            var claimed = recovered ?? throw new InvalidOperationException("Expected the expired Work claim to be reclaimed.");
            Assert.Equal(checkpoint.ActivityId, claimed.ActivityId);
            Assert.Equal(checkpoint.AttemptNumber + 1, claimed.AttemptNumber);
            var registration = new ReferenceCompletionRegistration(providerSafety);
            var prepared = DurableProviderWorkAdapter.Prepare(
                registration,
                ReferenceServiceProvider.Instance,
                claimed.ToProviderClaim());
            var permit = await store.TryAcquireEffectPermitAsync(claimed);
            Assert.NotNull(permit);
            evidence.Record("transaction", "effect-permit.acquire", "committed", "provider-owned");
            var result = await prepared.InvokeAsync();
            evidence.Record("provider", "provider.invoke", "completed", "no-database-transaction");
            var completion = await store.RecordCompletionAsync(
                permit!.Claim,
                new PostgreSqlWorkCompletion(
                    PostgreSqlWorkCompletionKind.Succeeded,
                    "reference-completed",
                    "{}",
                    result));
            Assert.Equal(DurableWorkState.Succeeded, completion.State);
            evidence.Record("transaction", "work.complete", "committed", "provider-owned");
        }
        else
        {
            Assert.Null(recovered);
        }

        Assert.Equal(expectedState, await ReadStateAsync(
            database.DataSource,
            new DurableScopeId(checkpoint.ScopeId),
            new DurableWorkId(checkpoint.WorkId)));
        _ = await evidence.WriteAsync(expectedState);
    }

    [Fact]
    public async Task OperatorDisableScope_CommitsAuditAndRejectsFurtherAcceptance()
    {
        var evidence = new ReferenceWorkloadEvidence("operator-disable-scope");
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var status = await schema.GetStatusAsync();
        var runtimeEpoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(runtimeEpoch, "reference-test", "initial-activation");
        evidence.Record("application", "schema.apply-and-activate", "compatible", "migration-owner");

        var registry = PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry();
        var options = new PostgreSqlDurableWorkOptions(runtimeEpoch, status.StoreId);
        var client = new PostgreSqlDurableWorkClient(database.DataSource, registry, options);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, runtimeEpoch);
        var scopeId = new DurableScopeId("reference-operator-disable");
        var accepted = await client.EnqueueAsync(CreateRequest(scopeId.Value, "reference-before-disable"));
        Assert.True(accepted.IsSuccess);
        evidence.Record("application", "work.accept", "committed", "provider-owned");

        var disabled = await store.DisableScopeAsync(
            scopeId,
            "reference-operator",
            "account-closed",
            expectedGeneration: 1);
        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);
        Assert.Equal(2, disabled.Generation);
        evidence.Record("operator", "scope.disable", "applied-and-audited", "provider-owned");

        var refused = await client.EnqueueAsync(CreateRequest(scopeId.Value, "reference-after-disable"));
        Assert.False(refused.IsSuccess);
        Assert.Equal(DurableProblemCodes.ScopeDisabled, refused.Problem!.Code);
        evidence.Record("application", "work.accept", "scope-disabled", "provider-owned");
        _ = await evidence.WriteAsync("disabled");
    }

    private static async ValueTask<ReferenceCheckpoint> RunUntilPermitAndTerminateAsync(
        string connectionString,
        Guid runtimeEpoch,
        Guid storeId,
        DurableProviderSafety providerSafety)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(ReferenceWorkloadHostMarker).Assembly.Location);
        startInfo.ArgumentList.Add(runtimeEpoch.ToString("D"));
        startInfo.ArgumentList.Add(storeId.ToString("D"));
        startInfo.ArgumentList.Add(providerSafety.ToString());
        startInfo.Environment["APPSURFACE_POSTGRES_REFERENCE_CONNECTION"] = connectionString;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The reference child process could not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                var error = await process.StandardError.ReadToEndAsync(timeout.Token);
                throw new InvalidOperationException($"Reference child exited before its permit checkpoint: {error}");
            }

            var checkpoint = JsonSerializer.Deserialize<ReferenceCheckpoint>(line)
                ?? throw new InvalidOperationException("Reference child returned an invalid checkpoint.");
            Assert.Equal("permit-committed", checkpoint.Phase);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            return checkpoint;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async ValueTask<string> ReadStateAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
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
            "SELECT state FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Reference Work row was not found."));
    }

    private static DurableWorkRequest CreateRequest(string scope, string command) => new(
        new DurableScopeId(scope),
        new DurableCommandId(command),
        $"request-{command}",
        PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent),
        "v1",
        new DurableEncodedPayload(
            "tests.delete-provider-access",
            "v1",
            DurableDataClassification.ApprovedApplication,
            Encoding.UTF8.GetBytes("reference-payload")),
        DurableProviderSafety.Idempotent);

    private static async ValueTask InsertDomainFactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string factId)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO domain_fact VALUES (@fact_id);",
            connection,
            transaction);
        command.Parameters.AddWithValue("fact_id", factId);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<long> CountWorkAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId)
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
            "SELECT count(*) FROM appsurface_durable.work WHERE scope_id = @scope_id;",
            connection,
            transaction);
        count.Parameters.AddWithValue("scope_id", scopeId.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}

internal sealed class ReferenceCompletionRegistration : DurableWorkRegistration
{
    private readonly DurableEncodedPayload _result = new(
        "tests.reference-result",
        "v1",
        DurableDataClassification.Operational,
        "completed"u8.ToArray());

    internal ReferenceCompletionRegistration(DurableProviderSafety providerSafety)
        : base(
            $"reference.{providerSafety.ToString().ToLowerInvariant()}",
            "v1",
            providerSafety,
            new PostgreSqlOpaqueTestCodec(
                "reference.payload",
                "v1",
                DurableDataClassification.Operational),
            new PostgreSqlOpaqueTestCodec(
                "tests.reference-result",
                "v1",
                DurableDataClassification.Operational))
    {
    }

    public override bool CanReconcile => false;

    public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(work);
        _ = WorkCodec.DecodeObject(work.Payload);
        return new ReferencePreparedWork(_result);
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
        throw new NotSupportedException("Safe reference Work does not require reconciliation.");
}

internal sealed class ReferencePreparedWork(DurableEncodedPayload result) : DurablePreparedWork
{
    public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(result);
}

internal sealed class ReferenceServiceProvider : IServiceProvider
{
    internal static ReferenceServiceProvider Instance { get; } = new();

    public object? GetService(Type serviceType) => null;
}
