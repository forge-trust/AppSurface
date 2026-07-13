using System.Text;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableWorkStoreTests
{
    [Fact]
    public async Task TransactionWriter_MissingSchemaFailsWithoutTakingOrPoisoningCallerTransaction()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using (var setup = database.DataSource.CreateCommand("CREATE TABLE domain_fact (fact_id text PRIMARY KEY);"))
        {
            await setup.ExecuteNonQueryAsync();
        }

        var writer = new PostgreSqlDurableWorkTransactionWriter(
            database.DataSource,
            Guid.NewGuid(),
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            sendWakeNotification: false);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var before = new NpgsqlCommand("INSERT INTO domain_fact VALUES ('before');", connection, transaction))
        {
            await before.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await writer.EnqueueAsync(transaction, CreateRequest("scope", "command")));
        await using (var after = new NpgsqlCommand("INSERT INTO domain_fact VALUES ('after');", connection, transaction))
        {
            await after.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        Assert.Equal(2, await ExecuteScalarAsync<long>(database.DataSource, "SELECT count(*) FROM domain_fact;"));
    }

    [Fact]
    public async Task TransactionWriter_UsesCallerTransactionWithoutOwningIt_AndDeduplicatesExactRequest()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        await using (var setup = database.DataSource.CreateCommand("CREATE TABLE domain_fact (fact_id text PRIMARY KEY);"))
        {
            await setup.ExecuteNonQueryAsync();
        }

        var epoch = Guid.NewGuid();
        var writer = new PostgreSqlDurableWorkTransactionWriter(
            database.DataSource,
            epoch,
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry());
        var request = CreateRequest("scope-a", "command-a");
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var before = new NpgsqlCommand("INSERT INTO domain_fact VALUES ('before');", connection, transaction))
            {
                await before.ExecuteNonQueryAsync();
            }

            var accepted = await writer.EnqueueAsync(transaction, request);

            await using (var after = new NpgsqlCommand("INSERT INTO domain_fact VALUES ('after');", connection, transaction))
            {
                await after.ExecuteNonQueryAsync();
            }

            Assert.True(accepted.IsSuccess);
            Assert.Equal(DurableWorkAcceptanceKind.Accepted, accepted.Value!.Kind);
            Assert.Equal(0, await CountWorkAsync(database.DataSource, request.ScopeId));
            await transaction.CommitAsync();
        }

        Assert.Equal(1, await CountWorkAsync(database.DataSource, request.ScopeId));
        Assert.Equal(2, await ExecuteScalarAsync<long>(database.DataSource, "SELECT count(*) FROM domain_fact;"));

        await using (var duplicateTransaction = await connection.BeginTransactionAsync())
        {
            var duplicate = await writer.EnqueueAsync(duplicateTransaction, request);
            Assert.True(duplicate.IsSuccess);
            Assert.Equal(DurableWorkAcceptanceKind.Duplicate, duplicate.Value!.Kind);
            await duplicateTransaction.CommitAsync();
        }

        await using (var transportRetryTransaction = await connection.BeginTransactionAsync())
        {
            var transportRetry = CreateRequest(
                "scope-a",
                "command-a-transport-retry",
                idempotencyKey: request.IdempotencyKey);
            var duplicate = await writer.EnqueueAsync(transportRetryTransaction, transportRetry);
            Assert.True(duplicate.IsSuccess);
            Assert.Equal(DurableWorkAcceptanceKind.Duplicate, duplicate.Value!.Kind);
            Assert.Equal(request.CommandId, duplicate.Value.CommandId);
            await transportRetryTransaction.CommitAsync();
        }

        await using (var conflictTransaction = await connection.BeginTransactionAsync())
        {
            var changed = CreateRequest("scope-a", "command-a", payloadText: "changed");
            var conflict = await writer.EnqueueAsync(conflictTransaction, changed);
            Assert.False(conflict.IsSuccess);
            Assert.Equal(DurableProblemCodes.CommandConflict, conflict.Problem!.Code);
            var changedTransportRetry = CreateRequest(
                "scope-a",
                "command-a-changed-transport",
                payloadText: "changed",
                idempotencyKey: request.IdempotencyKey);
            var idempotencyConflict = await writer.EnqueueAsync(conflictTransaction, changedTransportRetry);
            Assert.False(idempotencyConflict.IsSuccess);
            Assert.Equal(DurableProblemCodes.CommandConflict, idempotencyConflict.Problem!.Code);
            await conflictTransaction.CommitAsync();
        }

        await using (var rolledBack = await connection.BeginTransactionAsync())
        {
            var notCommitted = await writer.EnqueueAsync(rolledBack, CreateRequest("scope-a", "command-rollback"));
            Assert.True(notCommitted.IsSuccess);
            await rolledBack.RollbackAsync();
        }

        Assert.Equal(1, await CountWorkAsync(database.DataSource, request.ScopeId));

        var completedTransaction = await connection.BeginTransactionAsync();
        await completedTransaction.CommitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await writer.EnqueueAsync(completedTransaction, CreateRequest("scope-a", "command-completed")));
        await completedTransaction.DisposeAsync();
    }

    [Fact]
    public async Task TransactionWriter_DoesNotOpenASecondConnection_WhenCallerOwnsTheOnlyPoolLease()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var limitedBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            MaxPoolSize = 1,
            MinPoolSize = 0,
        };
        await using var limitedDataSource = NpgsqlDataSource.Create(limitedBuilder.ConnectionString);
        var writer = new PostgreSqlDurableWorkTransactionWriter(
            limitedDataSource,
            Guid.NewGuid(),
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            sendWakeNotification: false);
        await using var connection = await limitedDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var accepted = await writer.EnqueueAsync(
                transaction,
                CreateRequest("scope-single-lease", "command-single-lease"))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(accepted.IsSuccess);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TransactionWriter_RejectsAnotherPhysicalTarget_EvenWhenStoreIdentityWasCopied()
    {
        await using var authoritative = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var copied = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(authoritative);
        await ApplySchemaAsync(copied);
        var copiedStoreId = await ExecuteScalarAsync<Guid>(
            authoritative.DataSource,
            "SELECT store_id FROM appsurface_durable.store_metadata WHERE singleton;");
        await using (var copyIdentity = copied.DataSource.CreateCommand(
            "UPDATE appsurface_durable.store_metadata SET store_id = @store_id WHERE singleton;"))
        {
            copyIdentity.Parameters.AddWithValue("store_id", copiedStoreId);
            await copyIdentity.ExecuteNonQueryAsync();
        }

        var writer = new PostgreSqlDurableWorkTransactionWriter(
            authoritative.DataSource,
            Guid.NewGuid(),
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            sendWakeNotification: false);
        await using var wrongConnection = await copied.DataSource.OpenConnectionAsync();
        await using var wrongTransaction = await wrongConnection.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.EnqueueAsync(
                wrongTransaction,
                CreateRequest("scope-copied-store", "command-copied-store")));

        Assert.StartsWith(DurableProblemCodes.StoreIdentityMismatch, exception.Message, StringComparison.Ordinal);
        await wrongTransaction.RollbackAsync();
    }

    [Fact]
    public async Task ClaimPermitCompletion_UsesFencingAndPreservesStaleTerminalObservation()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        await using (var setup = database.DataSource.CreateCommand(
            "CREATE TABLE completion_hook (work_id text PRIMARY KEY);"))
        {
            await setup.ExecuteNonQueryAsync();
        }

        var epoch = Guid.NewGuid();
        var writer = new PostgreSqlDurableWorkTransactionWriter(
            database.DataSource,
            epoch,
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            sendWakeNotification: false);
        var client = new PostgreSqlDurableWorkClient(database.DataSource, writer);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-lifecycle", "command-lifecycle", DurableProviderSafety.ProviderKeyed);
        var acceptance = await client.EnqueueAsync(request);

        var candidate = Assert.Single(await store.DiscoverAsync(10));
        var claim = await store.TryClaimAsync(candidate, "worker-a");
        var staleClaim = await store.TryClaimAsync(candidate, "worker-b");

        Assert.NotNull(claim);
        Assert.Null(staleClaim);
        Assert.Equal(acceptance.Value!.WorkId, claim!.WorkId);
        Assert.Equal(1, claim.AttemptNumber);
        Assert.Equal(1, claim.LeaseGeneration);
        Assert.Equal(request.Payload.Content.ToArray(), claim.Payload.Content.ToArray());

        var renewed = await store.RenewLeaseAsync(claim);
        Assert.NotNull(renewed);
        Assert.True(renewed!.LeaseExpiresAtUtc >= claim.LeaseExpiresAtUtc);

        var permit = await store.TryAcquireEffectPermitAsync(renewed);
        var duplicatePermit = await store.TryAcquireEffectPermitAsync(renewed);
        Assert.NotNull(permit);
        Assert.Equal(permit!.PermitId, duplicatePermit!.PermitId);
        Assert.Equal(claim.WorkId.Value, claim.ActivityId);
        Assert.StartsWith("asdur-v1-", permit.ProviderKey, StringComparison.Ordinal);
        Assert.NotEqual(request.IdempotencyKey, permit.ProviderKey);

        var resultPayload = new DurableEncodedPayload(
            "tests.delete-result",
            "v1",
            DurableDataClassification.Operational,
            Encoding.UTF8.GetBytes("done"));
        var completion = new PostgreSqlWorkCompletion(
            PostgreSqlWorkCompletionKind.Succeeded,
            "provider_deleted",
            "{\"provider\":\"test\"}",
            resultPayload);
        async ValueTask ReleaseInCompletionTransaction(
            NpgsqlTransaction transaction,
            DurableWorkState state,
            CancellationToken cancellationToken)
        {
            Assert.Equal(DurableWorkState.Succeeded, state);
            await using var command = new NpgsqlCommand(
                "INSERT INTO completion_hook VALUES (@work_id);",
                transaction.Connection,
                transaction);
            command.Parameters.AddWithValue("work_id", claim.WorkId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = await store.RecordCompletionAsync(permit.Claim, completion, ReleaseInCompletionTransaction);
        var stale = await store.RecordCompletionAsync(permit.Claim, completion, ReleaseInCompletionTransaction);

        Assert.Equal(PostgreSqlWorkObservationOutcome.Applied, applied.Outcome);
        Assert.Equal(DurableWorkState.Succeeded, applied.State);
        Assert.Equal(PostgreSqlWorkObservationOutcome.AlreadyTerminal, stale.Outcome);
        Assert.Equal(1, await ExecuteScalarAsync<long>(database.DataSource, "SELECT count(*) FROM completion_hook;"));
        Assert.Empty(await store.DiscoverAsync(10));
        Assert.Equal(
            1,
            await CountHistoryAsync(
                database.DataSource,
                request.ScopeId,
                acceptance.Value.WorkId,
                "stale_completion_succeeded"));
        Assert.Equal(
            resultPayload.Content.ToArray(),
            await ReadStaleObservationPayloadAsync(
                database.DataSource,
                request.ScopeId,
                acceptance.Value.WorkId));
    }

    [Fact]
    public async Task ClaimAndRenewal_UseSnapshottedCadenceAndMaximumLeaseLifetime()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest(
            "scope-custom-lease-policy",
            "command-custom-lease-policy",
            retryPolicy: new DurableWorkRetryPolicy(
                maximumAttempts: 3,
                maximumElapsedTime: TimeSpan.FromMinutes(1),
                initialRetryDelay: TimeSpan.FromSeconds(1),
                maximumRetryDelay: TimeSpan.FromSeconds(5),
                leaseDuration: TimeSpan.FromSeconds(1),
                renewalCadence: TimeSpan.FromMilliseconds(125),
                maximumLeaseLifetime: TimeSpan.FromSeconds(2),
                backoffAlgorithm: "exponential-v1"));
        var accepted = await client.EnqueueAsync(request);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "lease-policy-worker");

        Assert.Equal(TimeSpan.FromMilliseconds(125), claim!.LeaseRenewalCadence);

        DateTimeOffset forcedLeaseStartedAt;
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTestScopeAsync(connection, transaction, request.ScopeId);
            await using var command = new NpgsqlCommand(
                """
                UPDATE appsurface_durable.work
                SET lease_started_at = clock_timestamp() - interval '1500 milliseconds',
                    lease_expires_at = clock_timestamp() + interval '500 milliseconds'
                WHERE scope_id = @scope_id AND work_id = @work_id
                RETURNING lease_started_at;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("work_id", accepted.Value!.WorkId.Value);
            forcedLeaseStartedAt = new DateTimeOffset(
                (DateTime)(await command.ExecuteScalarAsync())!,
                TimeSpan.Zero);
            await transaction.CommitAsync();
        }

        var renewed = await store.RenewLeaseAsync(claim);

        Assert.NotNull(renewed);
        Assert.Equal(TimeSpan.FromMilliseconds(125), renewed.LeaseRenewalCadence);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            renewed.LeaseExpiresAtUtc - forcedLeaseStartedAt);
    }

    [Fact]
    public async Task ProviderSafetyCancellationAndScopeGeneration_FailClosed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var writer = new PostgreSqlDurableWorkTransactionWriter(
            database.DataSource,
            epoch,
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            sendWakeNotification: false);
        var client = new PostgreSqlDurableWorkClient(database.DataSource, writer);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);

        var reconcile = CreateRequest(
            "scope-reconcile",
            "command-reconcile",
            DurableProviderSafety.ReconcileBeforeRetry);
        await client.EnqueueAsync(reconcile);
        var reconcileClaim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(10)), "worker");
        var reconcilePermit = await store.TryAcquireEffectPermitAsync(reconcileClaim!);
        var reconcileResult = await store.RecordCompletionAsync(
            reconcilePermit!.Claim,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, "provider_timeout", "{}"));
        Assert.Equal(DurableWorkState.Suspended, reconcileResult.State);

        var retryRequest = CreateRequest("scope-retry", "command-retry");
        await client.EnqueueAsync(retryRequest);
        var retryCandidate = (await store.DiscoverAsync(10)).Single(item => item.ScopeId == retryRequest.ScopeId);
        var retryClaim = await store.TryClaimAsync(retryCandidate, "worker");
        var expectedRetryDelay = PostgreSqlDurableRetryDelayCalculator.Calculate(
            retryRequest.RetryPolicy.BackoffAlgorithm,
            1,
            retryClaim!.AttemptNumber,
            retryRequest.RetryPolicy.InitialRetryDelay,
            retryRequest.RetryPolicy.MaximumRetryDelay,
            DurableProviderKey.CreateJitterSeed(retryRequest.ScopeId, retryClaim.ActivityId));
        var beforeRetry = DateTimeOffset.UtcNow;
        var retryResult = await store.RecordCompletionAsync(
            retryClaim,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, "transient_failure", "{}"));
        var afterRetry = DateTimeOffset.UtcNow;
        Assert.Equal(DurableWorkState.Ready, retryResult.State);
        Assert.InRange(
            retryResult.NextDueAtUtc!.Value,
            beforeRetry + expectedRetryDelay - TimeSpan.FromSeconds(1),
            afterRetry + expectedRetryDelay + TimeSpan.FromSeconds(1));

        var beforePermit = CreateRequest("scope-cancel-before", "command-cancel-before");
        var beforeAcceptance = await client.EnqueueAsync(beforePermit);
        var canceled = await store.RequestCancellationAsync(
            beforePermit.ScopeId,
            beforeAcceptance.Value!.WorkId,
            "operator-test",
            "consumer_requested",
            beforeAcceptance.Value.Revision);
        Assert.Equal(PostgreSqlCancellationOutcome.Applied, canceled.Outcome);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, canceled.State);
        Assert.Equal(
            ("operator-test", "consumer_requested"),
            await ReadWorkAuditAsync(database.DataSource, beforePermit.ScopeId, beforeAcceptance.Value.WorkId));

        var afterPermit = CreateRequest("scope-cancel-after", "command-cancel-after");
        await client.EnqueueAsync(afterPermit);
        var afterCandidate = (await store.DiscoverAsync(10)).Single(item => item.ScopeId == afterPermit.ScopeId);
        var afterClaim = await store.TryClaimAsync(afterCandidate, "worker");
        var afterEffectPermit = await store.TryAcquireEffectPermitAsync(afterClaim!);
        var cancelPending = await store.RequestCancellationAsync(
            afterPermit.ScopeId,
            afterClaim!.WorkId,
            "operator-test",
            "consumer_requested",
            afterEffectPermit!.Claim.Revision);
        var succeededAfterCancel = await store.RecordCompletionAsync(
            afterEffectPermit!.Claim,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Succeeded, "known_success", "{}"));
        Assert.Equal(DurableWorkState.CancelPending, cancelPending.State);
        Assert.Equal(DurableWorkState.SucceededAfterCancelRequested, succeededAfterCancel.State);
        var staleCancel = await store.RequestCancellationAsync(
            afterPermit.ScopeId,
            afterClaim!.WorkId,
            "operator-test",
            "duplicate_request",
            afterEffectPermit!.Claim.Revision);
        Assert.Equal(PostgreSqlCancellationOutcome.RevisionConflict, staleCancel.Outcome);

        var fenced = CreateRequest("scope-fenced", "command-fenced");
        await client.EnqueueAsync(fenced);
        var fencedCandidate = (await store.DiscoverAsync(10)).Single(item => item.ScopeId == fenced.ScopeId);
        var fencedClaim = await store.TryClaimAsync(fencedCandidate, "worker");
        var disabled = await store.DisableScopeAsync(
            fenced.ScopeId,
            "operator-test",
            "account_closed",
            expectedGeneration: 1);
        var refusedPermit = await store.TryAcquireEffectPermitAsync(fencedClaim!);
        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);
        Assert.Equal(2, disabled.Generation);
        Assert.Null(refusedPermit);
        var staleDisable = await store.DisableScopeAsync(
            fenced.ScopeId,
            "operator-test",
            "duplicate_request",
            expectedGeneration: 1);
        Assert.Equal(PostgreSqlScopeMutationOutcome.GenerationConflict, staleDisable.Outcome);
        Assert.Equal(
            ("operator-test", "account_closed"),
            await ReadScopeAuditAsync(database.DataSource, fenced.ScopeId));

        var restored = CreateRequest("scope-restored", "command-restored");
        await client.EnqueueAsync(restored);
        var restoredCandidate = (await store.DiscoverAsync(10)).Single(item => item.ScopeId == restored.ScopeId);
        var postRestoreEpoch = Guid.NewGuid();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
            epoch,
            postRestoreEpoch,
            "operator-test",
            "restore-completed");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.TryClaimAsync(restoredCandidate, "stale-pre-restore-worker"));
        var postRestoreStore = new PostgreSqlDurableWorkStore(database.DataSource, postRestoreEpoch);
        var epochFenced = await postRestoreStore.TryClaimAsync(restoredCandidate, "post-restore-worker");
        Assert.Null(epochFenced);
    }

    [Fact]
    public async Task RecordCompletion_AfterScopeDisable_IsQuarantinedWithoutTerminalMutationOrCallback()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-disabled-completion", "command-disabled-completion");
        await client.EnqueueAsync(request);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "worker-before-disable");
        var permit = await store.TryAcquireEffectPermitAsync(claim!);
        var disabled = await store.DisableScopeAsync(
            request.ScopeId,
            "operator-test",
            "account_closed",
            expectedGeneration: 1);
        var callbackInvoked = false;

        var completion = await store.RecordCompletionAsync(
            permit!.Claim,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "provider_succeeded_after_disable",
                "{}",
                new DurableEncodedPayload(
                    "tests.disabled-result",
                    "v1",
                    DurableDataClassification.Operational,
                    Encoding.UTF8.GetBytes("result"))),
            (_, _, _) =>
            {
                callbackInvoked = true;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);
        Assert.Equal(PostgreSqlWorkObservationOutcome.StaleObservation, completion.Outcome);
        Assert.Equal(DurableWorkState.Suspended, completion.State);
        Assert.False(callbackInvoked);
        Assert.Equal(
            ("suspended_ambiguous_external_outcome", permit.Claim.Revision + 1, false, "granted"),
            await ReadWorkCompletionFenceAsync(database.DataSource, request.ScopeId, permit.Claim.WorkId));
        Assert.Equal(
            1,
            await CountHistoryAsync(
                database.DataSource,
                request.ScopeId,
                permit.Claim.WorkId,
                "stale_completion_succeeded"));
    }

    [Fact]
    public async Task DisableScope_SuspendsNonterminalFlowWithValidPriorStateEvidence()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-disable-flow", "command-disable-flow");
        await client.EnqueueAsync(request);
        await InsertReadyFlowAsync(database.DataSource, request.ScopeId, epoch);

        var disabled = await store.DisableScopeAsync(
            request.ScopeId,
            "operator-test",
            "account_closed",
            expectedGeneration: 1);

        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);
        Assert.Equal(
            ("suspended", "ready", DurableProblemCodes.ScopeDisabled, 2L),
            await ReadFlowSuspensionAsync(database.DataSource, request.ScopeId));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, false, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, false, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, false, "suspended_manual_resolution")]
    [InlineData(DurableProviderSafety.Idempotent, true, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, true, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, true, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, true, "suspended_manual_resolution")]
    public async Task RecordCompletion_UnknownPostPermitAtBudgetBoundarySuspendsByProviderSafety(
        DurableProviderSafety providerSafety,
        bool expireElapsedBudget,
        string expectedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var boundary = expireElapsedBudget ? "elapsed" : "attempt";
        var request = CreateRequest(
            $"scope-budget-{providerSafety}-{boundary}",
            $"command-budget-{providerSafety}-{boundary}",
            providerSafety,
            retryPolicy: new DurableWorkRetryPolicy(
                maximumAttempts: expireElapsedBudget ? 3 : 1,
                maximumElapsedTime: TimeSpan.FromHours(1),
                initialRetryDelay: TimeSpan.FromSeconds(1),
                maximumRetryDelay: TimeSpan.FromSeconds(5),
                leaseDuration: TimeSpan.FromMinutes(1),
                renewalCadence: TimeSpan.FromSeconds(15),
                maximumLeaseLifetime: TimeSpan.FromMinutes(2),
                backoffAlgorithm: "exponential-v1"));
        await client.EnqueueAsync(request);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "budget-worker");
        var permit = await store.TryAcquireEffectPermitAsync(claim!);
        if (expireElapsedBudget)
        {
            await ExpireMaximumElapsedAsync(database.DataSource, permit!.Claim);
        }

        var terminalCallbackInvoked = false;
        var completion = await store.RecordCompletionAsync(
            permit!.Claim,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, "provider_timeout", "{}"),
            (_, state, _) =>
            {
                terminalCallbackInvoked = state is
                    DurableWorkState.Succeeded or
                    DurableWorkState.SucceededAfterCancelRequested or
                    DurableWorkState.FailedTerminal or
                    DurableWorkState.CanceledBeforeEffect;
                return ValueTask.CompletedTask;
            });
        var persisted = await ReadCompletionTruthAsync(
            database.DataSource,
            request.ScopeId,
            permit.Claim.WorkId);

        Assert.Equal(PostgreSqlWorkObservationOutcome.Applied, completion.Outcome);
        Assert.Equal(DurableWorkState.Suspended, completion.State);
        Assert.Equal(expectedState, persisted.State);
        Assert.False(persisted.IsTerminal);
        Assert.Equal("granted", persisted.PermitStatus);
        Assert.False(terminalCallbackInvoked);
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, "suspended_manual_resolution")]
    public async Task RecordCompletion_CanceledPostPermitRoutesByProviderSafetyAndPreservesIntent(
        DurableProviderSafety providerSafety,
        string expectedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest(
            $"scope-cancel-policy-{providerSafety}",
            $"command-cancel-policy-{providerSafety}",
            providerSafety);
        await client.EnqueueAsync(request);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "cancel-policy-worker");
        var permit = await store.TryAcquireEffectPermitAsync(claim!);
        var cancellation = await store.RequestCancellationAsync(
            request.ScopeId,
            permit!.Claim.WorkId,
            "operator-test",
            "consumer_requested",
            permit.Claim.Revision);

        var completion = await store.RecordCompletionAsync(
            permit.Claim,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, "provider_timeout", "{}"));
        var persisted = await ReadCompletionTruthAsync(
            database.DataSource,
            request.ScopeId,
            permit.Claim.WorkId);

        Assert.Equal(DurableWorkState.CancelPending, cancellation.State);
        Assert.Equal(DurableWorkState.Suspended, completion.State);
        Assert.Equal(expectedState, persisted.State);
        Assert.True(persisted.CancellationRequested);
        Assert.False(persisted.IsTerminal);
        Assert.Equal("granted", persisted.PermitStatus);
    }

    [Theory]
    [InlineData("suspended_ambiguous_external_outcome", DurableProviderSafety.ProviderKeyed)]
    [InlineData("suspended_reconciliation_required", DurableProviderSafety.ReconcileBeforeRetry)]
    [InlineData("suspended_manual_resolution", DurableProviderSafety.ManualResolution)]
    [InlineData("suspended_contract_unavailable", DurableProviderSafety.Idempotent)]
    public async Task RequestCancellation_NoPermitSuspendedWorkTerminalizesBeforeEffect(
        string suspendedState,
        DurableProviderSafety providerSafety)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest(
            $"scope-cancel-suspended-{providerSafety}",
            $"command-cancel-suspended-{providerSafety}",
            providerSafety);
        var accepted = await client.EnqueueAsync(request);
        var suspendedRevision = await ForceSuspendedStateAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value!.WorkId,
            suspendedState);

        var cancellation = await store.RequestCancellationAsync(
            request.ScopeId,
            accepted.Value.WorkId,
            "operator-test",
            "consumer_requested",
            suspendedRevision);
        var persisted = await ReadCancellationStateAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value.WorkId);

        Assert.Equal(PostgreSqlCancellationOutcome.Applied, cancellation.Outcome);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, cancellation.State);
        Assert.Equal(("canceled_before_effect", true, suspendedRevision + 1, "terminal"), persisted);
        Assert.Equal(
            1,
            await CountHistoryAsync(
                database.DataSource,
                request.ScopeId,
                accepted.Value.WorkId,
                "canceled_before_effect"));
    }

    [Fact]
    public async Task TryAcquireEffectPermit_PreexistingCancellationTerminalizesBeforeNewPermitAndRunsCallback()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-cancel-before-permit", "command-cancel-before-permit");
        await client.EnqueueAsync(request);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "permit-boundary-worker");
        await ForceCancellationIntentOnlyAsync(database.DataSource, claim!);
        DurableWorkState? callbackState = null;
        string? callbackCode = null;

        var permit = await store.TryAcquireEffectPermitAsync(
            claim!,
            onTerminalApplied: (_, state, code, _) =>
            {
                callbackState = state;
                callbackCode = code;
                return ValueTask.CompletedTask;
            });
        var persisted = await ReadCancellationStateAsync(database.DataSource, request.ScopeId, claim!.WorkId);

        Assert.Null(permit);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, callbackState);
        Assert.Equal("canceled_before_effect", callbackCode);
        Assert.Equal("canceled_before_effect", persisted.State);
        Assert.True(persisted.CancellationRequested);
        Assert.Equal("terminal", persisted.DispatchState);
        Assert.Equal(0, await CountEffectPermitsAsync(database.DataSource, request.ScopeId, claim.WorkId));
    }

    [Theory]
    [InlineData(
        "suspended_ambiguous_external_outcome",
        DurableProviderSafety.Idempotent,
        false,
        "canceled_before_effect",
        "terminal")]
    [InlineData(
        "suspended_reconciliation_required",
        DurableProviderSafety.ReconcileBeforeRetry,
        false,
        "canceled_before_effect",
        "terminal")]
    [InlineData(
        "suspended_manual_resolution",
        DurableProviderSafety.ManualResolution,
        false,
        "canceled_before_effect",
        "terminal")]
    [InlineData(
        "suspended_contract_unavailable",
        DurableProviderSafety.ProviderKeyed,
        false,
        "canceled_before_effect",
        "terminal")]
    [InlineData(
        "suspended_ambiguous_external_outcome",
        DurableProviderSafety.Idempotent,
        true,
        "suspended_ambiguous_external_outcome",
        "suspended")]
    [InlineData(
        "suspended_reconciliation_required",
        DurableProviderSafety.ReconcileBeforeRetry,
        true,
        "suspended_reconciliation_required",
        "suspended")]
    [InlineData(
        "suspended_manual_resolution",
        DurableProviderSafety.ManualResolution,
        true,
        "suspended_manual_resolution",
        "suspended")]
    [InlineData(
        "suspended_contract_unavailable",
        DurableProviderSafety.ProviderKeyed,
        true,
        "suspended_ambiguous_external_outcome",
        "suspended")]
    public async Task DisableScope_ProjectsSuspendedWorkByEffectEvidenceBeforePriorState(
        string suspendedState,
        DurableProviderSafety providerSafety,
        bool createPermit,
        string expectedState,
        string expectedDispatchState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var evidence = createPermit ? "permit" : "no-permit";
        var request = CreateRequest(
            $"scope-disable-suspended-{providerSafety}-{evidence}",
            $"command-disable-suspended-{providerSafety}-{evidence}",
            providerSafety);
        var accepted = await client.EnqueueAsync(request);
        if (createPermit)
        {
            var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "disable-suspended-worker");
            _ = await store.TryAcquireEffectPermitAsync(claim!);
        }

        _ = await ForceSuspendedStateAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value!.WorkId,
            suspendedState);

        var disabled = await store.DisableScopeAsync(
            request.ScopeId,
            "operator-test",
            "account_closed",
            expectedGeneration: 1);
        var persisted = await ReadCancellationStateAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value.WorkId);

        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);
        Assert.Equal(expectedState, persisted.State);
        Assert.True(persisted.CancellationRequested);
        Assert.Equal(expectedDispatchState, persisted.DispatchState);
        Assert.Equal(createPermit ? 1 : 0, await CountEffectPermitsAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value.WorkId));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, false, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, false, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, false, "suspended_manual_resolution")]
    [InlineData(DurableProviderSafety.Idempotent, true, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, true, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, true, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, true, "suspended_manual_resolution")]
    public async Task RequestCancellation_HistoricalAmbiguousPermitNeverBecomesCanceledBeforeEffect(
        DurableProviderSafety providerSafety,
        bool claimRetryBeforeCancel,
        string expectedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var retryShape = claimRetryBeforeCancel ? "leased" : "backoff";
        var request = CreateRequest(
            $"scope-historical-cancel-{providerSafety}-{retryShape}",
            $"command-historical-cancel-{providerSafety}-{retryShape}",
            providerSafety);
        var accepted = await client.EnqueueAsync(request);
        var firstClaim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "first-worker");
        _ = await store.TryAcquireEffectPermitAsync(firstClaim!);
        var expectedRevision = await ForceRetryWaitAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value!.WorkId);
        if (claimRetryBeforeCancel)
        {
            var retryClaim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "retry-worker");
            expectedRevision = retryClaim!.Revision;
        }

        var callbackCount = 0;
        DurableWorkState? callbackState = null;
        var cancellation = await store.RequestCancellationAsync(
            request.ScopeId,
            accepted.Value.WorkId,
            "operator-test",
            "consumer_requested",
            expectedRevision,
            (_, projectedState, _) =>
            {
                callbackCount++;
                callbackState = projectedState;
                return ValueTask.CompletedTask;
            });
        var persisted = await ReadCancellationStateAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value.WorkId);

        Assert.Equal(PostgreSqlCancellationOutcome.Applied, cancellation.Outcome);
        Assert.Equal(DurableWorkState.Suspended, cancellation.State);
        Assert.Equal(expectedState, persisted.State);
        Assert.True(persisted.CancellationRequested);
        Assert.Equal("suspended", persisted.DispatchState);
        Assert.Equal(1, callbackCount);
        Assert.Equal(DurableWorkState.Suspended, callbackState);
        Assert.Equal(1, await CountEffectPermitsAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value.WorkId));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, false, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, false, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, false, "suspended_manual_resolution")]
    [InlineData(DurableProviderSafety.Idempotent, true, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, true, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, true, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, true, "suspended_manual_resolution")]
    public async Task Exhaustion_HistoricalAmbiguousPermitSuspendsDuringBackoffOrExpiredRetryLease(
        DurableProviderSafety providerSafety,
        bool expireRetryLease,
        string expectedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var retryShape = expireRetryLease ? "leased" : "backoff";
        var request = CreateRequest(
            $"scope-historical-exhaustion-{providerSafety}-{retryShape}",
            $"command-historical-exhaustion-{providerSafety}-{retryShape}",
            providerSafety,
            retryPolicy: new DurableWorkRetryPolicy(
                maximumAttempts: 3,
                maximumElapsedTime: TimeSpan.FromHours(1),
                initialRetryDelay: TimeSpan.FromSeconds(1),
                maximumRetryDelay: TimeSpan.FromSeconds(5),
                leaseDuration: TimeSpan.FromMinutes(1),
                renewalCadence: TimeSpan.FromSeconds(15),
                maximumLeaseLifetime: TimeSpan.FromMinutes(2),
                backoffAlgorithm: "exponential-v1"));
        var accepted = await client.EnqueueAsync(request);
        var firstClaim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "first-worker");
        _ = await store.TryAcquireEffectPermitAsync(firstClaim!);
        _ = await ForceRetryWaitAsync(database.DataSource, request.ScopeId, accepted.Value!.WorkId);
        PostgreSqlDurableWorkClaim? retryClaim = null;
        if (expireRetryLease)
        {
            retryClaim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "retry-worker");
        }

        await ExpireMaximumElapsedAndMakeDueAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value.WorkId,
            retryClaim?.DispatchId);
        DurableWorkState? transitionState = null;
        string? transitionCode = null;
        var candidate = Assert.Single(await store.DiscoverAsync(1));

        var refused = await store.TryClaimAsync(
            candidate,
            "exhausted-worker",
            onTransitionApplied: (_, state, code, _) =>
            {
                transitionState = state;
                transitionCode = code;
                return ValueTask.CompletedTask;
            });
        var persisted = await ReadCompletionTruthAsync(
            database.DataSource,
            request.ScopeId,
            accepted.Value.WorkId);

        Assert.Null(refused);
        Assert.Equal(DurableWorkState.Suspended, transitionState);
        Assert.Equal("retry_policy_exhausted_with_ambiguous_effect", transitionCode);
        Assert.Equal(expectedState, persisted.State);
        Assert.False(persisted.IsTerminal);
        Assert.Equal("granted", persisted.PermitStatus);
    }

    [Fact]
    public async Task ConcurrentClaimers_ProduceOneLeaseGenerationOwner()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        await client.EnqueueAsync(CreateRequest("scope-contention", "command-contention"));
        var candidate = Assert.Single(await store.DiscoverAsync(1));

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(index => store.TryClaimAsync(candidate, $"worker-{index}").AsTask()));

        var winner = Assert.Single(claims, claim => claim is not null);
        Assert.Equal(1, winner!.AttemptNumber);
        Assert.Equal(1, winner.LeaseGeneration);
    }

    [Fact]
    public async Task ExpiredEffectPermit_SuspendsUnsafeProvider_AndReclaimsProviderKeyedWork()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var unsafeRequest = CreateRequest(
            "scope-expired-unsafe",
            "command-expired-unsafe",
            DurableProviderSafety.ManualResolution);
        await client.EnqueueAsync(unsafeRequest);
        var unsafeCandidate = Assert.Single(await store.DiscoverAsync(10));
        var unsafeClaim = await store.TryClaimAsync(unsafeCandidate, "worker-a");
        var unsafePermit = await store.TryAcquireEffectPermitAsync(unsafeClaim!);
        await ExpireLeaseAndDispatchAsync(database.DataSource, unsafePermit!.Claim);

        var expiredUnsafeCandidate = Assert.Single(await store.DiscoverAsync(10));
        var refused = await store.TryClaimAsync(expiredUnsafeCandidate, "worker-b");

        Assert.Null(refused);
        Assert.Empty(await store.DiscoverAsync(10));

        var keyedRequest = CreateRequest(
            "scope-expired-keyed",
            "command-expired-keyed",
            DurableProviderSafety.ProviderKeyed);
        await client.EnqueueAsync(keyedRequest);
        var keyedCandidate = Assert.Single(await store.DiscoverAsync(10));
        var keyedClaim = await store.TryClaimAsync(keyedCandidate, "worker-a");
        var keyedPermit = await store.TryAcquireEffectPermitAsync(keyedClaim!);
        await ExpireLeaseAndDispatchAsync(database.DataSource, keyedPermit!.Claim);

        var reclaimedCandidate = Assert.Single(await store.DiscoverAsync(10));
        var reclaimed = await store.TryClaimAsync(reclaimedCandidate, "worker-b");

        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed!.AttemptNumber);
        Assert.Equal(2, reclaimed.LeaseGeneration);
        Assert.Equal(keyedPermit.ProviderKey, reclaimed.ProviderKey);
    }

    [Fact]
    public async Task RuntimeRoleRlsAndDispatcherRole_KeepPayloadScoped()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var roleSuffix = Guid.NewGuid().ToString("N");
        var runtimeRole = $"durable_runtime_{roleSuffix}";
        var dispatcherRole = $"durable_dispatch_{roleSuffix}";
        const string password = "appsurface-role-test-password";
        await CreateRuntimeRolesAsync(database.DataSource, runtimeRole, dispatcherRole, password);

        var runtimeBuilder = new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString)
        {
            Username = runtimeRole,
            Password = password,
        };
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeBuilder.ConnectionString);
        var epoch = Guid.NewGuid();
        var runtimeClient = new PostgreSqlDurableWorkClient(
            runtimeDataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                runtimeDataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
                sendWakeNotification: false));
        var scopeA = new DurableScopeId("scope-rls-a");
        var scopeB = new DurableScopeId("scope-rls-b");
        await runtimeClient.EnqueueAsync(CreateRequest(scopeA.Value, "command-rls-a"));
        await runtimeClient.EnqueueAsync(CreateRequest(scopeB.Value, "command-rls-b"));

        Assert.Equal(0, await ExecuteScalarAsync<long>(runtimeDataSource, "SELECT count(*) FROM appsurface_durable.work;"));
        await using (var connection = await runtimeDataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var scopeCommand = new NpgsqlCommand(
                "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                connection,
                transaction);
            scopeCommand.Parameters.AddWithValue("scope_id", scopeA.Value);
            await scopeCommand.ExecuteNonQueryAsync();
            await using var countCommand = new NpgsqlCommand(
                "SELECT count(*) FROM appsurface_durable.work;",
                connection,
                transaction);
            Assert.Equal(1, (long)(await countCommand.ExecuteScalarAsync())!);
            await transaction.CommitAsync();
        }

        Assert.Equal(0, await ExecuteScalarAsync<long>(runtimeDataSource, "SELECT count(*) FROM appsurface_durable.work;"));

        var dispatcherBuilder = new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString)
        {
            Username = dispatcherRole,
            Password = password,
        };
        await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherBuilder.ConnectionString);
        Assert.Equal(2, await ExecuteScalarAsync<long>(dispatcherDataSource, "SELECT count(*) FROM appsurface_durable.dispatch;"));
        var denied = await Assert.ThrowsAsync<PostgresException>(
            async () => await ExecuteScalarAsync<long>(dispatcherDataSource, "SELECT count(*) FROM appsurface_durable.work;"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private static async ValueTask ApplySchemaAsync(PostgreSqlIntegrationTestDatabase database)
    {
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
    }

    private static DurableWorkRequest CreateRequest(
        string scope,
        string command,
        DurableProviderSafety safety = DurableProviderSafety.Idempotent,
        string payloadText = "payload",
        string? idempotencyKey = null,
        DurableWorkRetryPolicy? retryPolicy = null,
        DateTimeOffset? dueAtUtc = null) =>
        new(
            new DurableScopeId(scope),
            new DurableCommandId(command),
            idempotencyKey ?? $"request-{command}",
            PostgreSqlTestWorkContracts.DeleteProviderAccessName(safety),
            "v1",
            new DurableEncodedPayload(
                "tests.delete-provider-access",
                "v1",
                DurableDataClassification.ApprovedApplication,
                Encoding.UTF8.GetBytes(payloadText)),
            safety,
            retryPolicy,
            dueAtUtc);

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

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM appsurface_durable.work WHERE scope_id = @scope_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private static async ValueTask<long> CountHistoryAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId,
        string eventType)
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
            """
            SELECT count(*)
            FROM appsurface_durable.work_history
            WHERE scope_id = @scope_id AND work_id = @work_id AND event_type = @event_type;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        command.Parameters.AddWithValue("event_type", eventType);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private static async ValueTask<byte[]> ReadStaleObservationPayloadAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT observation_payload
            FROM appsurface_durable.work_history
            WHERE scope_id = @scope_id AND work_id = @work_id AND is_stale_observation
              AND observation_payload IS NOT NULL
            ORDER BY event_id DESC
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<(string State, long Revision, bool HasResult, string PermitStatus)>
        ReadWorkCompletionFenceAsync(
            NpgsqlDataSource dataSource,
            DurableScopeId scopeId,
            DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT work.state, work.revision, work.result_payload IS NOT NULL, permit.status
            FROM appsurface_durable.work AS work
            JOIN appsurface_durable.effect_permit AS permit
              ON permit.scope_id = work.scope_id
             AND permit.work_id = work.work_id
            WHERE work.scope_id = @scope_id AND work.work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2), reader.GetString(3));
    }

    private static async ValueTask InsertReadyFlowAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        Guid runtimeEpoch)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO appsurface_durable.flow_instance
                (scope_id, flow_instance_id, start_idempotency_key, flow_id, flow_version,
                 authoring_model, command_schema_version, definition_fingerprint, current_node_id, state,
                 context_contract_id, context_schema_version, context_codec_id, context_payload, context_sha256,
                 context_classification, context_retention_policy_id, scope_generation, runtime_epoch)
            VALUES
                (@scope_id, 'flow-disable-test', 'flow-disable-start', 'tests.disable-flow', 'v1',
                 'compiled-v1', 'v1', @fingerprint, 'start', 'ready',
                 'tests.disable-context', 'v1', 'tests.disable-context@v1', @context_payload, @context_sha,
                 'operational', 'tests-30d', 1, @runtime_epoch);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("fingerprint", new byte[32]);
        command.Parameters.AddWithValue("context_payload", Encoding.UTF8.GetBytes("{}"));
        command.Parameters.AddWithValue("context_sha", new byte[32]);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async ValueTask<(string State, string SuspendedFromState, string TerminalCode, long Revision)>
        ReadFlowSuspensionAsync(NpgsqlDataSource dataSource, DurableScopeId scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT state, suspended_from_state, terminal_code, revision
            FROM appsurface_durable.flow_instance
            WHERE scope_id = @scope_id AND flow_instance_id = 'flow-disable-test';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
    }

    private static async ValueTask ExpireMaximumElapsedAsync(
        NpgsqlDataSource dataSource,
        PostgreSqlDurableWorkClaim claim)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, claim.ScopeId);
        await using var command = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.work
            SET accepted_at = clock_timestamp() - maximum_elapsed - interval '1 second'
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", claim.WorkId.Value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async ValueTask<CompletionTruth> ReadCompletionTruthAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT work.state, work.terminal_at IS NOT NULL, permit.status,
                   work.cancellation_requested_at IS NOT NULL
            FROM appsurface_durable.work AS work
            JOIN appsurface_durable.effect_permit AS permit
              ON permit.scope_id = work.scope_id
             AND permit.work_id = work.work_id
            WHERE work.scope_id = @scope_id AND work.work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new CompletionTruth(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetString(2),
            reader.GetBoolean(3));
    }

    private static async ValueTask<long> ForceSuspendedStateAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId,
        string suspendedState)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        long revision;
        await using (var work = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.work
            SET state = @state,
                terminal_code = 'test_suspension',
                revision = revision + 1,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND work_id = @work_id
            RETURNING revision;
            """,
            connection,
            transaction))
        {
            work.Parameters.AddWithValue("state", suspendedState);
            work.Parameters.AddWithValue("scope_id", scopeId.Value);
            work.Parameters.AddWithValue("work_id", workId.Value);
            revision = (long)(await work.ExecuteScalarAsync())!;
        }

        await using (var dispatch = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.dispatch
            SET state = 'suspended', expected_revision = @revision
            WHERE scope_id = @scope_id AND aggregate_kind = 'work' AND aggregate_id = @work_id;
            """,
            connection,
            transaction))
        {
            dispatch.Parameters.AddWithValue("revision", revision);
            dispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
            dispatch.Parameters.AddWithValue("work_id", workId.Value);
            await dispatch.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return revision;
    }

    private static async ValueTask<long> ForceRetryWaitAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        long revision;
        await using (var work = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.work
            SET state = 'retry_wait',
                due_at = clock_timestamp() - interval '1 second',
                lease_owner = NULL,
                lease_started_at = NULL,
                lease_expires_at = NULL,
                revision = revision + 1,
                updated_at = clock_timestamp()
            WHERE scope_id = @scope_id AND work_id = @work_id
            RETURNING revision;
            """,
            connection,
            transaction))
        {
            work.Parameters.AddWithValue("scope_id", scopeId.Value);
            work.Parameters.AddWithValue("work_id", workId.Value);
            revision = (long)(await work.ExecuteScalarAsync())!;
        }

        await using (var dispatch = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.dispatch
            SET state = 'available',
                due_at = clock_timestamp() - interval '1 second',
                expected_revision = @revision
            WHERE scope_id = @scope_id AND aggregate_kind = 'work' AND aggregate_id = @work_id;
            """,
            connection,
            transaction))
        {
            dispatch.Parameters.AddWithValue("revision", revision);
            dispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
            dispatch.Parameters.AddWithValue("work_id", workId.Value);
            await dispatch.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return revision;
    }

    private static async ValueTask ExpireMaximumElapsedAndMakeDueAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId,
        Guid? dispatchId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using (var work = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.work
            SET accepted_at = clock_timestamp() - maximum_elapsed - interval '1 second',
                due_at = clock_timestamp() - interval '1 second',
                lease_expires_at = CASE
                    WHEN @expire_lease THEN clock_timestamp() - interval '1 second'
                    ELSE lease_expires_at
                END
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction))
        {
            work.Parameters.AddWithValue("expire_lease", dispatchId is not null);
            work.Parameters.AddWithValue("scope_id", scopeId.Value);
            work.Parameters.AddWithValue("work_id", workId.Value);
            await work.ExecuteNonQueryAsync();
        }

        await using (var dispatch = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.dispatch
            SET due_at = clock_timestamp() - interval '1 second'
            WHERE scope_id = @scope_id AND aggregate_kind = 'work' AND aggregate_id = @work_id;
            """,
            connection,
            transaction))
        {
            dispatch.Parameters.AddWithValue("scope_id", scopeId.Value);
            dispatch.Parameters.AddWithValue("work_id", workId.Value);
            await dispatch.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask<(string State, bool CancellationRequested, long Revision, string DispatchState)>
        ReadCancellationStateAsync(
            NpgsqlDataSource dataSource,
            DurableScopeId scopeId,
            DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT work.state, work.cancellation_requested_at IS NOT NULL, work.revision, dispatch.state
            FROM appsurface_durable.work AS work
            JOIN appsurface_durable.dispatch AS dispatch
              ON dispatch.scope_id = work.scope_id
             AND dispatch.aggregate_kind = 'work'
             AND dispatch.aggregate_id = work.work_id
            WHERE work.scope_id = @scope_id AND work.work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2), reader.GetString(3));
    }

    private static async ValueTask ForceCancellationIntentOnlyAsync(
        NpgsqlDataSource dataSource,
        PostgreSqlDurableWorkClaim claim)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, claim.ScopeId);
        await using var command = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.work
            SET cancellation_requested_at = clock_timestamp(), revision = revision + 1
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", claim.WorkId.Value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async ValueTask<long> CountEffectPermitsAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM appsurface_durable.effect_permit
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask ExpireLeaseAndDispatchAsync(
        NpgsqlDataSource dataSource,
        PostgreSqlDurableWorkClaim claim)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            scope.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using (var work = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.work
            SET lease_expires_at = clock_timestamp() - interval '1 second'
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction))
        {
            work.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
            work.Parameters.AddWithValue("work_id", claim.WorkId.Value);
            await work.ExecuteNonQueryAsync();
        }

        await using (var dispatch = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.dispatch
            SET due_at = clock_timestamp() - interval '1 second'
            WHERE dispatch_id = @dispatch_id;
            """,
            connection,
            transaction))
        {
            dispatch.Parameters.AddWithValue("dispatch_id", claim.DispatchId);
            await dispatch.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask<(string ActorId, string ReasonCode)> ReadWorkAuditAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT actor_id, reason_code
            FROM appsurface_durable.work_history
            WHERE scope_id = @scope_id AND work_id = @work_id
              AND event_type IN ('canceled_before_effect', 'cancel_pending')
            ORDER BY event_id DESC
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async ValueTask<(string ActorId, string ReasonCode)> ReadScopeAuditAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT actor_id, reason_code
            FROM appsurface_durable.scope_history
            WHERE scope_id = @scope_id
            ORDER BY event_id DESC
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async ValueTask SetTestScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId)
    {
        await using var scope = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction);
        scope.Parameters.AddWithValue("scope_id", scopeId.Value);
        await scope.ExecuteNonQueryAsync();
    }

    private static async ValueTask<T> ExecuteScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask CreateRuntimeRolesAsync(
        NpgsqlDataSource ownerDataSource,
        string runtimeRole,
        string dispatcherRole,
        string password)
    {
        var sql = $"""
            CREATE ROLE "{runtimeRole}" LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE "{dispatcherRole}" LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS;
            GRANT USAGE ON SCHEMA appsurface_durable TO "{runtimeRole}", "{dispatcherRole}";
            GRANT EXECUTE ON FUNCTION appsurface_durable.initialize_runtime_epoch(uuid) TO "{runtimeRole}";
            GRANT SELECT ON appsurface_durable.store_metadata TO "{runtimeRole}";
            GRANT SELECT ON appsurface_durable.schema_migration TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.scope TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.scope_history TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.work TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.work_history TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.effect_permit TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE ON appsurface_durable.dispatch TO "{runtimeRole}";
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA appsurface_durable TO "{runtimeRole}";
            GRANT SELECT ON appsurface_durable.dispatch TO "{dispatcherRole}";
            """;
        await using var command = ownerDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record CompletionTruth(
        string State,
        bool IsTerminal,
        string PermitStatus,
        bool CancellationRequested);
}
