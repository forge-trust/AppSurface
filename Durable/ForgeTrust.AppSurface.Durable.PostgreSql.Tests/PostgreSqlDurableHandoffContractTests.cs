using System.Data;
using ForgeTrust.AppSurface.Durable;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PolicyTests = ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkExecutionPolicyTests;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableHandoffContractTests
{
    [Fact]
    public async Task UnboundFenceRefusesWithoutCallingCallbackOrOwningCallerTransaction()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await CreateProbeTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertAsync(connection, transaction, "caller-before");
        var callbackCount = 0;

        var result = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbackCount++;
            return ValueTask.FromResult(true);
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.ClaimLost, result.Problem!.Code);
        Assert.Equal(0, callbackCount);
        Assert.Equal(1, await CountAsync(connection, transaction, "caller-before"));
        await InsertAsync(connection, transaction, "caller-after");
        await transaction.CommitAsync();
        Assert.Equal(2, await lab.CountProbeRowsAsync());
    }

    [Fact]
    public async Task InvocationAuthorityEndsOnDisposeEvenWhenThePermitRemainsCurrent()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var callbackCount = 0;

        using (lab.Fence.EnterInvocation(lab.Permit))
        {
            var prepared = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
            {
                callbackCount++;
                return ValueTask.FromResult(true);
            });
            Assert.True(prepared.IsSuccess);
            Assert.True(prepared.Value!.HandoffPrepared);
        }

        var afterExit = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbackCount++;
            return ValueTask.FromResult(true);
        });

        Assert.False(afterExit.IsSuccess);
        Assert.Equal(DurableProblemCodes.ClaimLost, afterExit.Problem!.Code);
        Assert.Equal(1, callbackCount);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InvocationMarkerBelongsToOneFenceAndNestedReferencesRestoreTheOuterMarker()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        var otherFence = new PostgreSqlDurableWorkHandoffFence(lab.Database.DataSource, lab.Options);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var callbackCount = 0;
        ValueTask<bool> Callback(NpgsqlTransaction _, CancellationToken __)
        {
            callbackCount++;
            return ValueTask.FromResult(true);
        }

        using (lab.Fence.EnterInvocation(lab.Permit))
        {
            Assert.Throws<InvalidOperationException>(() => lab.Fence.EnterInvocation(lab.Permit));
            using (otherFence.EnterInvocation(lab.Permit))
            {
                var borrowed = await lab.Fence.ExecuteAsync(transaction, Callback);
                Assert.False(borrowed.IsSuccess);
                Assert.Equal(DurableProblemCodes.ClaimLost, borrowed.Problem!.Code);

                var otherInstance = await otherFence.ExecuteAsync(transaction, Callback);
                Assert.True(otherInstance.IsSuccess);
            }

            var restored = await lab.Fence.ExecuteAsync(transaction, Callback);
            Assert.True(restored.IsSuccess);
        }

        Assert.Equal(2, callbackCount);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task SimultaneousAsyncContextsCannotBorrowEachOthersBoundFence()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        var otherFence = new PostgreSqlDurableWorkHandoffFence(lab.Database.DataSource, lab.Options);
        var bothBound = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundCount = 0;
        var callbackCount = 0;

        async Task<bool> AttemptBorrowAsync(PostgreSqlDurableWorkHandoffFence ownFence,
            PostgreSqlDurableWorkHandoffFence borrowedFence)
        {
            await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            using var invocation = ownFence.EnterInvocation(lab.Permit);
            if (Interlocked.Increment(ref boundCount) == 2) bothBound.TrySetResult(true);
            await bothBound.Task;

            var result = await borrowedFence.ExecuteAsync(transaction, (_, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                return ValueTask.FromResult(true);
            });
            await transaction.RollbackAsync();
            return result.IsSuccess;
        }

        var first = Task.Run(() => AttemptBorrowAsync(lab.Fence, otherFence));
        var second = Task.Run(() => AttemptBorrowAsync(otherFence, lab.Fence));
        var outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, Assert.False);
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task OverlappingAndReentrantCallsAreRejectedWhileSequentialCallsRemainAvailable()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;

        var first = lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, suppliedToken) =>
        {
            callbackCount++;
            Assert.Same(transaction, suppliedTransaction);
            callbackEntered.TrySetResult(true);
            await releaseCallback.Task.WaitAsync(suppliedToken);
            var reentrant = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await lab.Fence.ExecuteAsync(suppliedTransaction, (_, _) =>
                {
                    callbackCount++;
                    return ValueTask.FromResult(true);
                }));
            Assert.Contains("overlap or reenter", reentrant.Message, StringComparison.OrdinalIgnoreCase);
            return true;
        }).AsTask();

        await callbackEntered.Task;
        var overlap = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, (_, _) =>
            {
                callbackCount++;
                return ValueTask.FromResult(true);
            }));
        Assert.Contains("overlap or reenter", overlap.Message, StringComparison.OrdinalIgnoreCase);

        releaseCallback.TrySetResult(true);
        var firstResult = await first;
        Assert.True(firstResult.IsSuccess);
        Assert.Equal(1, callbackCount);

        var sequential = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbackCount++;
            return ValueTask.FromResult(true);
        });
        Assert.True(sequential.IsSuccess);
        Assert.Equal(2, callbackCount);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task NullClosedCompletedAndWrongTargetTransactionsAreRejectedBeforeCallback()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await using var wrongTargetLab = await PolicyTests.Lab.CreateAsync();
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;
        ValueTask<bool> Callback(NpgsqlTransaction _, CancellationToken __)
        {
            callbackCount++;
            return ValueTask.FromResult(true);
        }

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await lab.Fence.ExecuteAsync(null!, Callback));

        await using (var closedConnection = await lab.Database.DataSource.OpenConnectionAsync())
        {
            var closedTransaction = await closedConnection.BeginTransactionAsync();
            await closedConnection.CloseAsync();
            await using (closedTransaction)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await lab.Fence.ExecuteAsync(closedTransaction, Callback));
            }
        }

        await using (var completedConnection = await lab.Database.DataSource.OpenConnectionAsync())
        await using (var completedTransaction = await completedConnection.BeginTransactionAsync())
        {
            await completedTransaction.CommitAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await lab.Fence.ExecuteAsync(completedTransaction, Callback));
        }

        await using (var wrongConnection = await wrongTargetLab.Database.DataSource.OpenConnectionAsync())
        await using (var wrongTransaction = await wrongConnection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await lab.Fence.ExecuteAsync(wrongTransaction, Callback));
        }

        await using (var validConnection = await lab.Database.DataSource.OpenConnectionAsync())
        await using (var validTransaction = await validConnection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await lab.Fence.ExecuteAsync(validTransaction, null!));
            await validTransaction.RollbackAsync();
        }

        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task NonReadCommittedIsolationIsRejectedForEveryLevelNpgsqlCanCreateAsSuch()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        var testedLevels = new HashSet<IsolationLevel>();
        var unsupported = new[]
        {
            IsolationLevel.ReadUncommitted,
            IsolationLevel.RepeatableRead,
            IsolationLevel.Serializable,
            IsolationLevel.Snapshot,
            IsolationLevel.Chaos,
            IsolationLevel.Unspecified,
        };

        foreach (var requestedLevel in unsupported)
        {
            NpgsqlTransaction transaction;
            try
            {
                transaction = await connection.BeginTransactionAsync(requestedLevel);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            await using (transaction)
            {
                if (transaction.IsolationLevel == IsolationLevel.ReadCommitted)
                {
                    // PostgreSQL/Npgsql may normalize ReadUncommitted or Unspecified to ReadCommitted.
                    continue;
                }

                testedLevels.Add(requestedLevel);
                var callerIsolation = transaction.IsolationLevel;
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await lab.Fence.ExecuteAsync(transaction, static (_, _) => ValueTask.FromResult(true)));
                Assert.Equal(callerIsolation, transaction.IsolationLevel);
                await transaction.RollbackAsync();
            }
        }

        Assert.Contains(IsolationLevel.RepeatableRead, testedLevels);
        Assert.Contains(IsolationLevel.Serializable, testedLevels);
    }

    // Value: protects=pre-database cancellation preserves caller ownership; fails_when=the token check is deferred;
    // why_new=final-check cancellation does not cover entry; seam=existing invocation binding; no_claim=provider delivery.
    [Fact]
    public async Task CallerCancellationBeforeDatabaseWorkPreservesTransactionAndDoesNotEnterCallback()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', 'caller-before-cancel', true);", connection, transaction))
        {
            await scope.ExecuteNonQueryAsync();
        }
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callbacks = 0;
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, (_, _) =>
            {
                callbacks++;
                return ValueTask.FromResult(true);
            }, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(0, callbacks);
        Assert.Same(connection, transaction.Connection);
        Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
        await using var read = new NpgsqlCommand(
            "SELECT current_setting('appsurface_durable.scope_id', true);", connection, transaction);
        Assert.Equal("caller-before-cancel", await read.ExecuteScalarAsync());
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingAdmissionBindsBothOptedInAndLegacyPermitsWithoutPublicBinder(bool legacy)
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(legacy: legacy);
        Assert.Equal(legacy, lab.Permit.Claim.Execution is null);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        using (lab.Fence.EnterInvocation(lab.Permit))
        {
            var callbackCount = 0;
            var result = await lab.Fence.ExecuteAsync(transaction, (suppliedTransaction, suppliedToken) =>
            {
                callbackCount++;
                Assert.Same(transaction, suppliedTransaction);
                Assert.Equal(CancellationToken.None, suppliedToken);
                return ValueTask.FromResult(true);
            });

            Assert.True(result.IsSuccess);
            Assert.True(result.Value!.HandoffPrepared);
            Assert.Equal(1, callbackCount);
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ScopedRegistrationAliasesInterfaceAndConcreteInstanceButSeparatesScopes()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        var services = new ServiceCollection();
        var dispatcher = lab.Database.CreateDataSource();
        services.AddAppSurfaceDurablePostgreSql(
            dispatcher,
            lab.Database.DataSource,
            lab.Options,
            new PostgreSqlDurableScheduleOptions("durable_runtime"),
            options => options.SendWakeNotifications = false);

        await using var provider = services.BuildServiceProvider();
        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        var firstInterface = firstScope.ServiceProvider.GetRequiredService<IDurableWorkHandoffFence>();
        var firstConcrete = firstScope.ServiceProvider.GetRequiredService<PostgreSqlDurableWorkHandoffFence>();
        var secondInterface = secondScope.ServiceProvider.GetRequiredService<IDurableWorkHandoffFence>();
        var secondConcrete = secondScope.ServiceProvider.GetRequiredService<PostgreSqlDurableWorkHandoffFence>();

        Assert.Same(firstInterface, firstConcrete);
        Assert.Same(secondInterface, secondConcrete);
        Assert.NotSame(firstInterface, secondInterface);
    }

    private static async Task CreateProbeTableAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var command = lab.Database.DataSource.CreateCommand(
            "CREATE TABLE handoff_contract_probe (value text PRIMARY KEY);");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string value)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO handoff_contract_probe(value) VALUES (@value);", connection, transaction);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string value)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM handoff_contract_probe WHERE value = @value;", connection, transaction);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

internal sealed class PostgreSqlDurableHandoffTestLab : IAsyncDisposable
{
    private PostgreSqlDurableHandoffTestLab(PolicyTests.Lab policyLab, PostgreSqlEffectPermit permit,
        PostgreSqlDurableWorkOptions options, PostgreSqlDurableWorkHandoffFence fence)
    {
        PolicyLab = policyLab;
        Permit = permit;
        Options = options;
        Fence = fence;
    }

    internal PolicyTests.Lab PolicyLab { get; }
    internal PostgreSqlIntegrationTestDatabase Database => PolicyLab.Database;
    internal PostgreSqlEffectPermit Permit { get; }
    internal PostgreSqlDurableWorkOptions Options { get; }
    internal PostgreSqlDurableWorkHandoffFence Fence { get; }

    internal static async Task<PostgreSqlDurableHandoffTestLab> CreateAsync(bool legacy = false,
        PostgreSqlDurableHandoffCheckpointHook? checkpoints = null, DateTimeOffset? deadline = null,
        string command = "handoff-contract")
    {
        var policyLab = await PolicyTests.Lab.CreateAsync();
        try
        {
            var request = PolicyTests.Request(command, deadline: deadline);
            if (legacy)
            {
                request = new DurableWorkRequest(request.ScopeId, request.CommandId, request.IdempotencyKey,
                    request.WorkName, request.WorkVersion, request.Payload, request.ProviderSafety, request.RetryPolicy);
            }

            var permit = await policyLab.AdmitReadyAsync(request);
            Assert.True(await policyLab.Store.TryAdmitInvocationAsync(permit));
            var storeId = await policyLab.ScalarAsync<Guid>("SELECT store_id FROM appsurface_durable.store_metadata;");
            var options = new PostgreSqlDurableWorkOptions(policyLab.Epoch, storeId);
            var fence = new PostgreSqlDurableWorkHandoffFence(policyLab.Database.DataSource, options, checkpoints);
            return new(policyLab, permit, options, fence);
        }
        catch
        {
            await policyLab.DisposeAsync();
            throw;
        }
    }

    internal async Task<long> CountProbeRowsAsync(string? value = null)
    {
        await using var command = Database.DataSource.CreateCommand(value is null
            ? "SELECT count(*) FROM handoff_contract_probe;"
            : "SELECT count(*) FROM handoff_contract_probe WHERE value = @value;");
        if (value is not null) command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public ValueTask DisposeAsync() => PolicyLab.DisposeAsync();
}
