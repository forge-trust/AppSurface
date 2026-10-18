using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Workers;
using Npgsql;

namespace DurablePackedExecutionPolicyConsumer;

// docs:snippet durable-handoff-application-flow:start
internal interface IHandoffApplicationProvider
{
    ValueTask SendAsync(
        HandoffWork work,
        string activityId,
        CancellationToken cancellationToken);
}

internal sealed class HandoffApplicationExecutor(
    IDurableWorkHandoffFence handoffFence,
    NpgsqlDataSource runtimeDataSource,
    IHandoffApplicationProvider provider)
    : IDurableWorkExitExecutor<HandoffWork, HandoffResult>
{
    public async ValueTask<DurableWorkExit<HandoffResult>> ExecuteAsync(
        DurableWorkerEnvelope<HandoffWork> envelope,
        CancellationToken cancellationToken = default)
    {
        var work = envelope.Payload ?? throw new InvalidOperationException("The handoff Work payload was missing.");
        var identity = envelope.ExecutionIdentity ?? throw new InvalidOperationException("The native execution identity was missing.");
        await using (var callerConnection = await runtimeDataSource.OpenConnectionAsync(cancellationToken))
        {
            await using (var callerTransaction = await callerConnection.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                cancellationToken))
            {
                var prepared = await handoffFence.ExecuteAsync(
                    callerTransaction,
                    async (transaction, token) =>
                    {
                        await using var authority = new NpgsqlCommand("""
                            SELECT is_authorized
                            FROM handoff_consumer.authority
                            WHERE scope_id = @scope_id AND operation_key = @operation_key
                            FOR UPDATE;
                            """, transaction.Connection, transaction);
                        authority.Parameters.AddWithValue("scope_id", work.ScopeId);
                        authority.Parameters.AddWithValue("operation_key", work.AuthorityKey);
                        if (await authority.ExecuteScalarAsync(token) is not true)
                        {
                            return false;
                        }

                        await using var ledger = new NpgsqlCommand("""
                            INSERT INTO handoff_consumer.ledger (scope_id, activity_id, logical_operation)
                            VALUES (@scope_id, @activity_id, @operation)
                            ON CONFLICT DO NOTHING
                            RETURNING activity_id;
                            """, transaction.Connection, transaction);
                        ledger.Parameters.AddWithValue("scope_id", work.ScopeId);
                        ledger.Parameters.AddWithValue("activity_id", identity.ActivityId);
                        ledger.Parameters.AddWithValue("operation", work.LogicalOperation);
                        return await ledger.ExecuteScalarAsync(token) is not null;
                    },
                    cancellationToken);

                if (!prepared.IsSuccess || prepared.Value?.HandoffPrepared != true)
                {
                    return DurableWorkExit<HandoffResult>.RetryBeforeEffect(
                        prepared.IsSuccess ? "handoff_denied" : "handoff_refused");
                }

                cancellationToken.ThrowIfCancellationRequested();
                // A failed or uncertain commit propagates without sending. Reconcile the ledger before a later send.
                await callerTransaction.CommitAsync(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await provider.SendAsync(work, identity.ActivityId, cancellationToken);
        return DurableWorkExit<HandoffResult>.Succeeded(new HandoffResult(identity.ActivityId));
    }
}
// docs:snippet durable-handoff-application-flow:end

internal sealed class HandoffFakeApplicationProviderAdapter(HandoffFakeProvider fakeProvider)
    : IHandoffApplicationProvider
{
    public ValueTask SendAsync(
        HandoffWork work,
        string activityId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return fakeProvider.SendCommittedAsync(work, activityId, cancellationToken);
    }
}
