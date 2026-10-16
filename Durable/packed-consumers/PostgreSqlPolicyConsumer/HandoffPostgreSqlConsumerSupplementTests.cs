using System.Data;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DurablePackedExecutionPolicyConsumer;

public sealed partial class HandoffPostgreSqlConsumerTests
{
    [Fact]
    public async Task Handoff_PublicScopeAndRetainedExecutorReferenceCannotCreateInvocationAuthority()
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var work = fixture.CreateWork("binding-lifetime");
        await fixture.SeedAuthorityAsync(work);
        var probe = new HandoffExecutionProbe();
        var fake = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fake);
        await using var connection = await fixture.RuntimeDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var callbacks = 0;
        ValueTask<bool> Callback(NpgsqlTransaction _, CancellationToken __)
        {
            callbacks++;
            return ValueTask.FromResult(true);
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var fence = scope.ServiceProvider.GetRequiredService<IDurableWorkHandoffFence>();
            var unbound = await fence.ExecuteAsync(transaction, Callback);
            Assert.Equal(DurableProblemCodes.ClaimLost, unbound.Problem!.Code);
        }
        _ = await fixture.EnqueueAsync(provider, work);
        Assert.Equal(1, (await RunOneAsync(provider)).Processed);
        Assert.Equal(DurableProblemCodes.ClaimLost, await probe.PreparationProblem.Task);
        Assert.Equal(0, probe.PreparationCallbackCalls);
        var expired = await probe.RetainedFence!.ExecuteAsync(transaction, Callback);
        Assert.Equal(DurableProblemCodes.ClaimLost, expired.Problem!.Code);
        Assert.Equal(0, callbacks);
        Assert.Equal(1, fake.Calls);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("native-select")]
    [InlineData("application-insert")]
    public async Task Handoff_MissingRuntimeGrantFailsClosedInTheActualPublicExecutor(string missingGrant)
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var work = fixture.CreateWork(missingGrant);
        await fixture.SeedAuthorityAsync(work);
        var probe = new HandoffExecutionProbe(holdBeforeFence: true);
        var fake = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fake);
        _ = await fixture.EnqueueAsync(provider, work);
        var running = RunOneAsync(provider);
        await probe.BeforeFence.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var permission = missingGrant == "native-select" ? "SELECT" : "INSERT";
        var table = missingGrant == "native-select" ? "appsurface_durable.effect_permit" : "handoff_consumer.ledger";
        var role = '"' + fixture.RuntimeRole.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        await using (var revoke = fixture.AdminDataSource.CreateCommand($"REVOKE {permission} ON {table} FROM {role};"))
            await revoke.ExecuteNonQueryAsync();
        probe.AllowFence();
        var failure = await probe.Failure.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(failure).SqlState);
        // Native completion may also need the removed grant. Restore only after the fence failed, never to make it pass.
        await using (var restore = fixture.AdminDataSource.CreateCommand($"GRANT {permission} ON {table} TO {role};"))
            await restore.ExecuteNonQueryAsync();
        _ = await Record.ExceptionAsync(async () => await running.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(missingGrant == "native-select" ? 0 : 1, probe.CallbackCalls);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(0L, await fixture.AdminScalarAsync<long>("SELECT count(*) FROM handoff_consumer.ledger;"));
    }

    [Fact]
    public async Task Handoff_ApplicationAndNativeRlsHideOtherScopeAndRejectCrossScopeLedgerWrite()
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var work = fixture.CreateWork("rls-current");
        var other = fixture.CreateWork("rls-other");
        await fixture.SeedAuthorityAsync(work);
        await fixture.SeedAuthorityAsync(other);
        var probe = new HandoffExecutionProbe { CrossScope = other.ScopeId };
        var fake = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fake);
        _ = await fixture.EnqueueAsync(provider, other);
        // Keep the other scoped Work out of this one-item runtime pass.
        await using (var defer = fixture.AdminDataSource.CreateCommand("""
            UPDATE appsurface_durable.dispatch
            SET due_at=greatest(now(), appsurface_durable.work_execution_now())+interval '1 day',
                execution_discovery_at=greatest(now(), appsurface_durable.work_execution_now())+interval '1 day'
            WHERE scope_id=@scope
            RETURNING due_at > now() AND execution_discovery_at > appsurface_durable.work_execution_now();
            """))
        {
            defer.Parameters.AddWithValue("scope", other.ScopeId);
            Assert.True((bool)(await defer.ExecuteScalarAsync())!);
        }
        _ = await fixture.EnqueueAsync(provider, work);
        _ = await RunOneAsync(provider).WaitAsync(TimeSpan.FromSeconds(20));
        var failure = await probe.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(failure).SqlState);
        Assert.True(probe.CrossScopeRowsHidden);
        Assert.Equal("caller-scope-before-handoff", probe.ScopeAfterFailure);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(0L, await fixture.AdminScalarAsync<long>("SELECT count(*) FROM handoff_consumer.ledger;"));
        Assert.True(string.IsNullOrEmpty(await fixture.RuntimeScalarAsync<string?>("SELECT current_setting('appsurface_durable.scope_id', true);")));
    }

    [Theory]
    [InlineData("AcknowledgementLost", true)]
    [InlineData("EstablishedRollback", false)]
    [InlineData("Unresolved", false)]
    public async Task Handoff_UncertainCommitAdapterUsesFreshScopedLedgerAndSuppressesProvider(
        string outcomeName, bool ledgerVisible)
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var outcome = Enum.Parse<HandoffCommitObservationMode>(outcomeName);
        var work = fixture.CreateWork("commit-" + outcome);
        await fixture.SeedAuthorityAsync(work);
        var probe = new HandoffExecutionProbe(commitMode: outcome);
        var fake = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fake);
        _ = await fixture.EnqueueAsync(provider, work);
        _ = await RunOneAsync(provider).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True((await probe.Prepared.Task).HandoffPrepared);
        Assert.Equal(ledgerVisible, await probe.CommitLedgerVisible.Task);
        if (outcome == HandoffCommitObservationMode.AcknowledgementLost) Assert.IsType<IOException>(probe.CommitFailure);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(ledgerVisible ? 1L : 0L, await fixture.AdminScalarAsync<long>("SELECT count(*) FROM handoff_consumer.ledger;"));
        // The unresolved variant observed absence while the transaction was still able to commit, then explicitly
        // rolled it back for owned fixture cleanup. This adapter tests consumer policy, not a wire-level fault.
    }

    [Fact]
    public async Task Handoff_CancellationAfterCommitAndResourceReleaseSuppressesProviderWithoutRemovingLedger()
    {
        await using var fixture = await HandoffPostgreSqlFixture.CreateAsync();
        var work = fixture.CreateWork("post-commit-cancellation");
        await fixture.SeedAuthorityAsync(work);
        var probe = new HandoffExecutionProbe(holdBeforeProvider: true);
        var fake = new HandoffFakeProvider(fixture.RuntimeDataSource);
        await using var provider = fixture.BuildProvider(probe, fake);
        _ = await fixture.EnqueueAsync(provider, work);
        using var stop = new CancellationTokenSource();
        var running = provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work), stop.Token).AsTask();
        Assert.True(await probe.ResourcesReleased.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        stop.Cancel();
        probe.AllowProvider();
        _ = await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsAssignableFrom<OperationCanceledException>(await probe.Failure.Task);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(1L, await fixture.AdminScalarAsync<long>("SELECT count(*) FROM handoff_consumer.ledger;"));
        Assert.Equal("suspended_ambiguous_external_outcome", await fixture.AdminScalarAsync<string>("SELECT state FROM appsurface_durable.work WHERE scope_id=@scope;", ("scope", work.ScopeId)));
    }
}
