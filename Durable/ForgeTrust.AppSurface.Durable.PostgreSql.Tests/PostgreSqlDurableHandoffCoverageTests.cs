using System.Data;
using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

/// <summary>Proves refusal at invocation and admission boundaries that can change before callback entry.</summary>
public sealed class PostgreSqlDurableHandoffCoverageTests
{
    [Fact]
    public async Task InvocationExitingDuringNativeLockWaitRefusesBeforeCallbackAndPreservesCallerWrites()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-exit-lock-wait");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var blocker = await lab.Database.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var workLock = new NpgsqlCommand("""
            SELECT work_id FROM appsurface_durable.work
            WHERE scope_id=@scope AND work_id=@work FOR UPDATE;
            """, blocker, blockerTransaction))
        {
            workLock.Parameters.AddWithValue("scope", lab.Permit.Claim.ScopeId.Value);
            workLock.Parameters.AddWithValue("work", lab.Permit.Claim.WorkId.Value);
            Assert.Equal(lab.Permit.Claim.WorkId.Value, await workLock.ExecuteScalarAsync());
        }
        var blockerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(blocker, blockerTransaction);
        const string applicationName = "869-invocation-exits-waiting";
        await using var source = PostgreSqlDurableHandoffTestSupport.CreateDataSource(lab, applicationName);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(transaction, "earlier-caller-write");
        var fence = new PostgreSqlDurableWorkHandoffFence(source, lab.Options);
        using var invocation = fence.EnterInvocation(lab.Permit);
        var callbacks = 0;
        var operation = fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbacks++;
            return ValueTask.FromResult(true);
        }).AsTask();
        await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
            lab.Database.DataSource, applicationName, blockerPid, TimeSpan.FromSeconds(8));
        invocation.Dispose();
        await blockerTransaction.CommitAsync();

        var result = await operation.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.ClaimLost, result.Problem!.Code);
        Assert.Equal(0, callbacks);
        await transaction.CommitAsync();
        Assert.Equal(1, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab, "earlier-caller-write"));
    }

    [Fact]
    public async Task OptedInPermitWithoutInvocationAdmissionCannotEnterApplicationCallback()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-without-admission");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using (var admission = lab.Database.DataSource.CreateCommand("""
            UPDATE appsurface_durable.effect_permit SET invocation_admitted_at=NULL
            WHERE permit_id=@permit;
            """))
        {
            admission.Parameters.AddWithValue("permit", lab.Permit.PermitId);
            Assert.Equal(1, await admission.ExecuteNonQueryAsync());
        }
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbacks = 0;
        var result = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbacks++;
            return ValueTask.FromResult(true);
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.ClaimLost, result.Problem!.Code);
        Assert.Equal(0, callbacks);
        await transaction.CommitAsync();
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }
}
