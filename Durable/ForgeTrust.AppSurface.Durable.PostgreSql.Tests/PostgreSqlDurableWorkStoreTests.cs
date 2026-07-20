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
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()));
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
        var writer = CreateWriter(database.DataSource, epoch);
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
        await completedTransaction.DisposeAsync();
        var inactive = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await writer.EnqueueAsync(completedTransaction, CreateRequest("scope-a", "command-completed")));
        Assert.Contains("not active on an open connection", inactive.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransactionWriter_RejectsTransactionWhoseConnectionWasClosed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var writer = CreateWriter(database.DataSource, Guid.NewGuid());
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.CloseAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.EnqueueAsync(
                transaction,
                CreateRequest("scope-closed-connection", "command-closed-connection")));

        Assert.Contains("not active on an open connection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransactionWriter_RejectsProviderSafetyThatDiffersFromRegistration()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var writer = CreateWriter(database.DataSource, epoch);
        var registered = CreateRequest("scope-safety", "command-safety");
        var mismatched = new DurableWorkRequest(
            registered.ScopeId,
            registered.CommandId,
            registered.IdempotencyKey,
            registered.WorkName,
            registered.WorkVersion,
            registered.Payload,
            DurableProviderSafety.ManualResolution,
            registered.RetryPolicy,
            registered.DueAtUtc);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.EnqueueAsync(transaction, mismatched));

        Assert.Contains("provider-safety mode does not match", exception.Message, StringComparison.Ordinal);
        await using var stillUsable = new NpgsqlCommand("SELECT 1;", connection, transaction);
        Assert.Equal(1, (int)(await stillUsable.ExecuteScalarAsync())!);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task RuntimeEpochRotation_WaitsForInFlightAcceptanceAndFencesOldEpochAfterward()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var deployment = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await deployment.ApplyAsync();
        var oldEpoch = Guid.NewGuid();
        await deployment.InitializeRuntimeEpochAsync(oldEpoch, "tests", "initial");
        var oldWriter = CreateWriter(database.DataSource, oldEpoch);
        var oldClient = CreateClient(database.DataSource, oldEpoch);

        await using var acceptanceConnection = await database.DataSource.OpenConnectionAsync();
        await using var acceptanceTransaction = await acceptanceConnection.BeginTransactionAsync();
        var inFlight = await oldWriter.EnqueueAsync(
            acceptanceTransaction,
            CreateRequest("scope-epoch-in-flight", "command-epoch-in-flight"));
        Assert.True(inFlight.IsSuccess);

        var rotationApplicationName = $"slice3-epoch-rotation-{Guid.NewGuid():N}";
        var rotationConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = rotationApplicationName,
        };
        await using var rotationDataSource = NpgsqlDataSource.Create(rotationConnection.ConnectionString);
        var rotationManager = new PostgreSqlDurableRuntimeSchemaManager(rotationDataSource);
        var newEpoch = Guid.NewGuid();
        var rotation = rotationManager.RotateRuntimeEpochAsync(
            oldEpoch,
            newEpoch,
            "tests",
            "recovery").AsTask();

        await WaitForDatabaseLockAsync(database.DataSource, rotationApplicationName);
        Assert.False(rotation.IsCompleted);
        await acceptanceTransaction.CommitAsync();
        var rotated = await rotation.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(newEpoch, rotated.ActiveEpoch);

        var oldEpochFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await oldClient.EnqueueAsync(CreateRequest("scope-old-epoch", "command-old-epoch")));
        Assert.StartsWith(DurableProblemCodes.RecoveryEpochRequired, oldEpochFailure.Message, StringComparison.Ordinal);

        var newClient = CreateClient(database.DataSource, newEpoch);
        var accepted = await newClient.EnqueueAsync(CreateRequest("scope-new-epoch", "command-new-epoch"));
        Assert.True(accepted.IsSuccess);
        Assert.Equal(DurableWorkAcceptanceKind.Accepted, accepted.Value!.Kind);
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
        var epoch = Guid.NewGuid();
        _ = CreateOptions(database.DataSource, epoch);
        var writer = CreateWriter(limitedDataSource, epoch);
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

        var writer = CreateWriter(authoritative.DataSource, Guid.NewGuid());
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
    public async Task StoreTarget_MatchesResolvedEndpointWithinConfiguredHostList()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var configuredHosts = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Host = $"unreachable.invalid,{builder.Host}",
        };
        var expected = PostgreSqlDurableStoreTarget.Create(configuredHosts.ConnectionString);
        var wrong = PostgreSqlDurableStoreTarget.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Host = "unreachable.invalid",
        }.ConnectionString);
        await using var connection = await database.DataSource.OpenConnectionAsync();

        Assert.True(expected.Matches(connection));
        Assert.False(wrong.Matches(connection));
    }

    [Fact]
    public async Task TransactionWriter_RejectsWrongExpectedStoreIdWithoutPoisoningCallerTransaction()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        _ = CreateOptions(database.DataSource, epoch);
        var writer = new PostgreSqlDurableWorkTransactionWriter(
            database.DataSource,
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            new PostgreSqlDurableWorkOptions(epoch, Guid.NewGuid()));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.EnqueueAsync(
                transaction,
                CreateRequest("scope-wrong-store-id", "command-wrong-store-id")));

        Assert.StartsWith(DurableProblemCodes.StoreIdentityMismatch, exception.Message, StringComparison.Ordinal);
        await using var stillUsable = new NpgsqlCommand("SELECT 1;", connection, transaction);
        Assert.Equal(1, (int)(await stillUsable.ExecuteScalarAsync())!);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TransactionWriter_FailsClosedForIncompatibleCallerOwnedSchemaWithoutPoisoningTransaction()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var writer = CreateWriter(database.DataSource, Guid.NewGuid());
        var cases = new[]
        {
            (
                Mutation: """
                    UPDATE appsurface_durable.store_metadata
                    SET schema_version = 1,
                        minimum_reader_version = 1,
                        maximum_reader_version = 1,
                        minimum_writer_version = 1,
                        maximum_writer_version = 1
                    WHERE singleton;
                    """,
                Expected: DurableRuntimeSchemaCompatibility.UpgradeRequired),
            (
                Mutation: """
                    UPDATE appsurface_durable.store_metadata
                    SET schema_version = 2,
                        minimum_reader_version = 1,
                        maximum_reader_version = 1,
                        minimum_writer_version = 1,
                        maximum_writer_version = 1
                    WHERE singleton;
                    """,
                Expected: DurableRuntimeSchemaCompatibility.StoreTooNew),
            (
                Mutation: "DELETE FROM appsurface_durable.store_metadata WHERE singleton;",
                Expected: DurableRuntimeSchemaCompatibility.UpgradeRequired),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            if (index > 0)
            {
                await using var restore = database.DataSource.CreateCommand(
                    """
                    UPDATE appsurface_durable.store_metadata
                    SET schema_version = 2,
                        minimum_reader_version = 1,
                        maximum_reader_version = 2,
                        minimum_writer_version = 1,
                        maximum_writer_version = 2
                    WHERE singleton;
                    """);
                await restore.ExecuteNonQueryAsync();
            }

            await using (var mutate = database.DataSource.CreateCommand(cases[index].Mutation))
            {
                await mutate.ExecuteNonQueryAsync();
            }

            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var exception = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(async () =>
                await writer.EnqueueAsync(
                    transaction,
                    CreateRequest($"scope-schema-{index}", $"command-schema-{index}")));

            Assert.Equal(cases[index].Expected, exception.Status.Compatibility);
            await using var stillUsable = new NpgsqlCommand("SELECT 1;", connection, transaction);
            Assert.Equal(1, (int)(await stillUsable.ExecuteScalarAsync())!);
            await transaction.RollbackAsync();
        }
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
        var writer = CreateWriter(database.DataSource, epoch);
        var client = CreateClient(database.DataSource, epoch);
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
        Assert.Equal("worker-a", claim.LeaseOwner);
        Assert.Equal(request.Payload.Content.ToArray(), claim.Payload.Content.ToArray());
        var providerClaim = claim.ToProviderClaim();
        Assert.Equal(claim.ActivityId, providerClaim.ActivityId);
        Assert.Equal(claim.ActivityId, providerClaim.ToExecutionContext().ExecutionIdentity.ProviderKey);

        var wrongOwnerRenewal = await store.RenewLeaseAsync(claim with { LeaseOwner = "worker-b" });
        Assert.Null(wrongOwnerRenewal);
        var renewed = await store.RenewLeaseAsync(claim);
        Assert.NotNull(renewed);
        Assert.True(renewed!.LeaseExpiresAtUtc >= claim.LeaseExpiresAtUtc);

        var wrongOwnerPermit = await store.TryAcquireEffectPermitAsync(renewed with { LeaseOwner = "worker-b" });
        Assert.Null(wrongOwnerPermit);
        var permit = await store.TryAcquireEffectPermitAsync(renewed);
        var duplicatePermit = await store.TryAcquireEffectPermitAsync(renewed);
        Assert.NotNull(permit);
        Assert.Equal(permit!.PermitId, duplicatePermit!.PermitId);
        Assert.Equal(claim.WorkId.Value, claim.ActivityId);
        Assert.Equal(claim.ActivityId, permit.ProviderKey);
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

        var wrongOwnerCompletion = await store.RecordCompletionAsync(
            permit.Claim with { LeaseOwner = "worker-b" },
            completion,
            ReleaseInCompletionTransaction);
        var applied = await store.RecordCompletionAsync(permit.Claim, completion, ReleaseInCompletionTransaction);
        var stale = await store.RecordCompletionAsync(permit.Claim, completion, ReleaseInCompletionTransaction);

        Assert.Equal(PostgreSqlWorkObservationOutcome.StaleObservation, wrongOwnerCompletion.Outcome);
        Assert.Equal(PostgreSqlWorkObservationOutcome.Applied, applied.Outcome);
        Assert.Equal(DurableWorkState.Succeeded, applied.State);
        Assert.Equal(PostgreSqlWorkObservationOutcome.AlreadyTerminal, stale.Outcome);
        var persistedResult = await ReadPersistedResultAsync(
            database.DataSource,
            request.ScopeId,
            acceptance.Value.WorkId);
        Assert.Equal(resultPayload.ContractName, persistedResult.ContractName);
        Assert.Equal(resultPayload.ContractVersion, persistedResult.ContractVersion);
        Assert.Equal("operational", persistedResult.Classification);
        Assert.Equal(resultPayload.RetentionPolicyId, persistedResult.RetentionPolicyId);
        Assert.Equal(resultPayload.Content.ToArray(), persistedResult.Payload);
        Assert.Equal(Convert.FromHexString(resultPayload.Sha256), persistedResult.Sha256);
        Assert.Equal(1, await ExecuteScalarAsync<long>(database.DataSource, "SELECT count(*) FROM completion_hook;"));
        Assert.Empty(await store.DiscoverAsync(10));
        Assert.Equal(
            2,
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
    public async Task CompletionKinds_ProjectFailedRetryAndContractUnavailableStates()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var cases = new[]
        {
            (
                Kind: PostgreSqlWorkCompletionKind.FailedTerminal,
                ExpectedState: DurableWorkState.FailedTerminal,
                PersistedState: "failed",
                EventType: "completion_failed_terminal"),
            (
                Kind: PostgreSqlWorkCompletionKind.ProvenNoEffect,
                ExpectedState: DurableWorkState.Ready,
                PersistedState: "retry_wait",
                EventType: "completion_proven_no_effect"),
            (
                Kind: PostgreSqlWorkCompletionKind.ContractUnavailable,
                ExpectedState: DurableWorkState.Suspended,
                PersistedState: "suspended_contract_unavailable",
                EventType: "completion_contract_unavailable"),
            (
                Kind: PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome,
                ExpectedState: DurableWorkState.Suspended,
                PersistedState: "suspended_ambiguous_external_outcome",
                EventType: "completion_ambiguous_external_outcome"),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var request = CreateRequest($"scope-completion-{index}", $"command-completion-{index}");
            var accepted = (await client.EnqueueAsync(request)).Value!;
            var candidate = (await store.DiscoverAsync(10)).Single(item => item.ScopeId == request.ScopeId);
            var claim = await store.TryClaimAsync(candidate, $"completion-worker-{index}");
            Assert.NotNull(claim);

            var completion = cases[index].Kind == PostgreSqlWorkCompletionKind.ContractUnavailable
                ? await store.RecordPreparationFailureAsync(claim!)
                : await store.RecordCompletionAsync(
                    claim!,
                    new PostgreSqlWorkCompletion(cases[index].Kind, $"completion_code_{index}", "{}"));
            var persisted = await ReadWorkStateAndRevisionAsync(
                database.DataSource,
                request.ScopeId,
                accepted.WorkId);

            Assert.Equal(PostgreSqlWorkObservationOutcome.Applied, completion.Outcome);
            Assert.Equal(cases[index].ExpectedState, completion.State);
            Assert.Equal(cases[index].PersistedState, persisted.State);
            Assert.Equal(
                1,
                await CountHistoryAsync(
                    database.DataSource,
                    request.ScopeId,
                    accepted.WorkId,
                    cases[index].EventType));
        }
    }

    [Fact]
    public async Task CompletionForMissingWorkReturnsSuspendedStaleObservation()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-missing-completion", "command-missing-completion");
        await client.EnqueueAsync(request);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "completion-worker");

        var result = await store.RecordCompletionAsync(
            claim! with { WorkId = new DurableWorkId("missing-work") },
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, "transient_failure", "{}"));

        Assert.Equal(PostgreSqlWorkObservationOutcome.StaleObservation, result.Outcome);
        Assert.Equal(DurableWorkState.Suspended, result.State);
        Assert.Equal(claim.Revision, result.Revision);
        Assert.Null(result.NextDueAtUtc);
    }

    [Fact]
    public async Task WorkStore_RejectsInvalidCompletionAndProtocolInputsBeforeMutation()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-invalid-input", "command-invalid-input");
        await client.EnqueueAsync(request);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "validation-worker");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.RecordCompletionAsync(
                claim!,
                new PostgreSqlWorkCompletion((PostgreSqlWorkCompletionKind)int.MaxValue, "invalid-kind", "{}")));
        foreach (var invalidCode in new[] { "", new string('x', 201), "control\u0001code" })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await store.RecordCompletionAsync(
                    claim!,
                    new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, invalidCode, "{}")));
        }

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.RecordCompletionAsync(
                claim!,
                new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, "invalid-json-shape", "[]")));
        var oversizedDetails = "{\"value\":\"" + new string('x', 16_384) + "\"}";
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.RecordCompletionAsync(
                claim!,
                new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, "oversized-json", oversizedDetails)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.DisableScopeAsync(request.ScopeId, "tests", "invalid-generation", expectedGeneration: 0));

        var unsupportedPolicy = new DurableWorkRetryPolicy(
            maximumAttempts: 3,
            maximumElapsedTime: TimeSpan.FromHours(1),
            initialRetryDelay: TimeSpan.FromSeconds(1),
            maximumRetryDelay: TimeSpan.FromMinutes(1),
            leaseDuration: TimeSpan.FromMinutes(2),
            renewalCadence: TimeSpan.FromSeconds(30),
            maximumLeaseLifetime: TimeSpan.FromMinutes(10),
            backoffAlgorithm: "linear-v1");
        var unsupportedRequest = CreateRequest(
            "scope-unsupported-retry",
            "command-unsupported-retry",
            retryPolicy: unsupportedPolicy);
        var unsupported = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await client.EnqueueAsync(unsupportedRequest));
        Assert.Contains("linear-v1", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acceptance_WithWakeNotificationsEnabled_PublishesCommittedDispatchId()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        await using var listener = await database.DataSource.OpenConnectionAsync();
        var notification = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, args) => notification.TrySetResult(args.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN appsurface_durable_wake;", listener))
        {
            await listen.ExecuteNonQueryAsync();
        }

        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch, sendWakeNotification: true);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var accepted = await client.EnqueueAsync(CreateRequest("scope-wake", "command-wake"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!notification.Task.IsCompleted)
        {
            await listener.WaitAsync(timeout.Token);
        }

        var candidate = Assert.Single(await store.DiscoverAsync(1));
        Assert.True(accepted.IsSuccess);
        Assert.Equal(candidate.DispatchId.ToString("D"), await notification.Task);
    }

    [Theory]
    [InlineData(false, "leased", DurableWorkState.Claimed)]
    [InlineData(true, "cancel_pending", DurableWorkState.CancelPending)]
    public async Task SuccessfulCompletion_WithoutExactPermit_IsQuarantinedWithoutTerminalMutation(
        bool cancellationRequested,
        string expectedPersistedState,
        DurableWorkState expectedReportedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var suffix = cancellationRequested ? "cancel-pending" : "leased";
        var request = CreateRequest($"scope-success-no-permit-{suffix}", $"command-success-no-permit-{suffix}");
        await client.EnqueueAsync(request);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "no-permit-worker");
        Assert.NotNull(claim);

        if (cancellationRequested)
        {
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTestScopeAsync(connection, transaction, request.ScopeId);
            await using var command = new NpgsqlCommand(
                """
                UPDATE appsurface_durable.work
                SET state = 'cancel_pending', cancellation_requested_at = clock_timestamp()
                WHERE scope_id = @scope_id AND work_id = @work_id;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("work_id", claim!.WorkId.Value);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        var callbackInvoked = false;
        var completion = await store.RecordCompletionAsync(
            claim!,
            new PostgreSqlWorkCompletion(
                PostgreSqlWorkCompletionKind.Succeeded,
                "unpermitted_success",
                "{}",
                new DurableEncodedPayload(
                    "tests.unpermitted-result",
                    "v1",
                    DurableDataClassification.Operational,
                    Encoding.UTF8.GetBytes("must-not-terminalize"))),
            (_, _, _) =>
            {
                callbackInvoked = true;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(PostgreSqlWorkObservationOutcome.StaleObservation, completion.Outcome);
        Assert.Equal(expectedReportedState, completion.State);
        Assert.False(callbackInvoked);

        await using (var readConnection = await database.DataSource.OpenConnectionAsync())
        await using (var readTransaction = await readConnection.BeginTransactionAsync())
        {
            await SetTestScopeAsync(readConnection, readTransaction, request.ScopeId);
            await using var read = new NpgsqlCommand(
                """
                SELECT state, revision, terminal_at IS NOT NULL, result_payload IS NOT NULL,
                       (SELECT count(*) FROM appsurface_durable.effect_permit permit
                        WHERE permit.scope_id = work.scope_id AND permit.work_id = work.work_id)
                FROM appsurface_durable.work work
                WHERE scope_id = @scope_id AND work_id = @work_id;
                """,
                readConnection,
                readTransaction);
            read.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            read.Parameters.AddWithValue("work_id", claim.WorkId.Value);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(expectedPersistedState, reader.GetString(0));
            Assert.Equal(claim.Revision, reader.GetInt64(1));
            Assert.False(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
            Assert.Equal(0, reader.GetInt64(4));
        }

        Assert.Equal(
            1,
            await CountHistoryAsync(
                database.DataSource,
                request.ScopeId,
                claim.WorkId,
                "stale_completion_succeeded"));
    }

    [Fact]
    public async Task ClaimAndRenewal_UseSnapshottedCadenceAndMaximumLeaseLifetime()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
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
        var renewedAtMaximumLifetime = await store.RenewLeaseAsync(renewed!);

        Assert.NotNull(renewed);
        Assert.NotNull(renewedAtMaximumLifetime);
        Assert.Equal(TimeSpan.FromMilliseconds(125), renewed.LeaseRenewalCadence);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            renewed.LeaseExpiresAtUtc - forcedLeaseStartedAt);
        Assert.Equal(renewed.LeaseExpiresAtUtc, renewedAtMaximumLifetime.LeaseExpiresAtUtc);
        Assert.True(renewedAtMaximumLifetime.Revision > renewed.Revision);
    }

    [Fact]
    public async Task ProviderSafetyCancellationAndScopeGeneration_FailClosed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var writer = CreateWriter(database.DataSource, epoch);
        var client = CreateClient(database.DataSource, epoch);
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
            retryClaim!.AttemptNumber,
            retryRequest.RetryPolicy.InitialRetryDelay,
            retryRequest.RetryPolicy.MaximumRetryDelay);
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
        var terminalCancel = await store.RequestCancellationAsync(
            afterPermit.ScopeId,
            afterClaim.WorkId,
            "operator-test",
            "terminal_duplicate",
            succeededAfterCancel.Revision);
        Assert.Equal(PostgreSqlCancellationOutcome.AlreadyTerminal, terminalCancel.Outcome);
        Assert.Equal(DurableWorkState.SucceededAfterCancelRequested, terminalCancel.State);
        var missingCancel = await store.RequestCancellationAsync(
            afterPermit.ScopeId,
            new DurableWorkId("work-does-not-exist"),
            "operator-test",
            "missing_work",
            expectedRevision: 1);
        Assert.Equal(PostgreSqlCancellationOutcome.NotFound, missingCancel.Outcome);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.RequestCancellationAsync(
                afterPermit.ScopeId,
                afterClaim.WorkId,
                "operator-test",
                "invalid_revision",
                expectedRevision: 0));

        var fenced = CreateRequest("scope-fenced", "command-fenced");
        await client.EnqueueAsync(fenced);
        var fencedCandidate = (await store.DiscoverAsync(10)).Single(item => item.ScopeId == fenced.ScopeId);
        var fencedClaim = await store.TryClaimAsync(fencedCandidate, "worker");
        var disabled = await store.DisableScopeAsync(
            fenced.ScopeId,
            "operator-test",
            "account_closed",
            expectedGeneration: 1);
        var refusedClaim = await store.TryClaimAsync(fencedCandidate, "worker-after-disable");
        var refusedRenewal = await store.RenewLeaseAsync(fencedClaim!);
        var refusedPermit = await store.TryAcquireEffectPermitAsync(fencedClaim!);
        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);
        Assert.Equal(2, disabled.Generation);
        Assert.Null(refusedClaim);
        Assert.Null(refusedRenewal);
        Assert.Null(refusedPermit);
        var staleDisable = await store.DisableScopeAsync(
            fenced.ScopeId,
            "operator-test",
            "duplicate_request",
            expectedGeneration: 1);
        Assert.Equal(PostgreSqlScopeMutationOutcome.GenerationConflict, staleDisable.Outcome);
        await using (var disabledAcceptanceConnection = await database.DataSource.OpenConnectionAsync())
        await using (var disabledAcceptanceTransaction = await disabledAcceptanceConnection.BeginTransactionAsync())
        {
            var refusedAcceptance = await writer.EnqueueAsync(
                disabledAcceptanceTransaction,
                CreateRequest("scope-fenced", "command-fenced-after-disable"));
            Assert.False(refusedAcceptance.IsSuccess);
            Assert.Equal(DurableProblemCodes.ScopeDisabled, refusedAcceptance.Problem!.Code);
            await disabledAcceptanceTransaction.CommitAsync();
        }

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
        DurableWorkState? epochTransitionState = null;
        string? epochTransitionCode = null;
        var epochFenced = await postRestoreStore.TryClaimAsync(
            restoredCandidate,
            "post-restore-worker",
            onTransitionApplied: (_, state, code, _) =>
            {
                epochTransitionState = state;
                epochTransitionCode = code;
                return ValueTask.CompletedTask;
            });
        Assert.Null(epochFenced);
        Assert.Equal(DurableWorkState.Suspended, epochTransitionState);
        Assert.Equal("runtime_epoch_mismatch", epochTransitionCode);
    }

    [Fact]
    public async Task DisableScope_DistinguishesMissingConflictAndAlreadyDisabledOutcomes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-disable-outcomes", "command-disable-outcomes");
        await client.EnqueueAsync(request);

        var missing = await store.DisableScopeAsync(
            new DurableScopeId("scope-never-created"),
            "operator-test",
            "account_closed",
            expectedGeneration: 1);
        var applied = await store.DisableScopeAsync(
            request.ScopeId,
            "operator-test",
            "account_closed",
            expectedGeneration: 1);
        var conflict = await store.DisableScopeAsync(
            request.ScopeId,
            "operator-test",
            "stale_generation",
            expectedGeneration: 1);
        var alreadyDisabled = await store.DisableScopeAsync(
            request.ScopeId,
            "operator-test",
            "duplicate_request",
            expectedGeneration: applied.Generation);

        Assert.Equal(PostgreSqlScopeMutationOutcome.NotFound, missing.Outcome);
        Assert.Equal(0, missing.Generation);
        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, applied.Outcome);
        Assert.Equal(PostgreSqlScopeMutationOutcome.GenerationConflict, conflict.Outcome);
        Assert.Equal(applied.Generation, conflict.Generation);
        Assert.Equal(PostgreSqlScopeMutationOutcome.AlreadyDisabled, alreadyDisabled.Outcome);
        Assert.Equal(applied.Generation, alreadyDisabled.Generation);
    }

    [Fact]
    public async Task RecordCompletion_AfterScopeDisable_IsQuarantinedWithoutTerminalMutationOrCallback()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
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
        var client = CreateClient(database.DataSource, epoch);
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
        var client = CreateClient(database.DataSource, epoch);
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
        var client = CreateClient(database.DataSource, epoch);
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
        var client = CreateClient(database.DataSource, epoch);
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
        var client = CreateClient(database.DataSource, epoch);
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
        var client = CreateClient(database.DataSource, epoch);
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
        var client = CreateClient(database.DataSource, epoch);
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
        var constrainedConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            MaxPoolSize = 8,
        };
        await using var constrainedDataSource = NpgsqlDataSource.Create(constrainedConnection.ConnectionString);
        var client = CreateClient(constrainedDataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(constrainedDataSource, epoch);
        await client.EnqueueAsync(CreateRequest("scope-contention", "command-contention"));
        var candidate = Assert.Single(await store.DiscoverAsync(1));

        var claims = await Task.WhenAll(
                Enumerable.Range(0, 32)
                    .Select(index => store.TryClaimAsync(candidate, $"worker-{index}").AsTask()))
            .WaitAsync(TimeSpan.FromSeconds(30));

        var winner = Assert.Single(claims, claim => claim is not null);
        Assert.Equal(1, winner!.AttemptNumber);
        Assert.Equal(1, winner.LeaseGeneration);
    }

    [Fact]
    public async Task Claim_RejectsPayloadWhoseStoredHashDoesNotMatchAuthoritativeBytes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-corrupt-payload", "command-corrupt-payload");
        var accepted = (await client.EnqueueAsync(request)).Value!;
        await using (var corrupt = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.work
            SET payload_sha256 = decode(repeat('00', 32), 'hex')
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """))
        {
            corrupt.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            corrupt.Parameters.AddWithValue("work_id", accepted.WorkId.Value);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var candidate = Assert.Single(await store.DiscoverAsync(1));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.TryClaimAsync(candidate, "corrupt-payload-worker"));

        Assert.Contains("payload hash", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            ("pending", accepted.Revision),
            await ReadWorkStateAndRevisionAsync(database.DataSource, request.ScopeId, accepted.WorkId));
    }

    [Fact]
    public async Task ExpiredEffectPermit_SuspendsUnsafeProvider_AndReclaimsProviderKeyedWork()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
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
        DurableWorkState? unsafeTransitionState = null;
        string? unsafeTransitionCode = null;
        var refused = await store.TryClaimAsync(
            expiredUnsafeCandidate,
            "worker-b",
            onTransitionApplied: (_, state, code, _) =>
            {
                unsafeTransitionState = state;
                unsafeTransitionCode = code;
                return ValueTask.CompletedTask;
            });

        Assert.Null(refused);
        Assert.Equal(DurableWorkState.Suspended, unsafeTransitionState);
        Assert.Equal("expired_effect_permit_manual_resolution", unsafeTransitionCode);
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
        Assert.Equal(keyedPermit.ProviderKey, reclaimed.ActivityId);
    }

    [Fact]
    public async Task ClaimTimeProjection_MissingDispatchRollsBackWorkAndHistory()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var originalEpoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, originalEpoch);
        var request = CreateRequest("scope-claim-projection-rollback", "command-claim-projection-rollback");
        var accepted = (await client.EnqueueAsync(request)).Value!;
        var original = await ReadWorkStateAndRevisionAsync(database.DataSource, request.ScopeId, accepted.WorkId);
        var candidate = Assert.Single(
            await new PostgreSqlDurableWorkStore(database.DataSource, originalEpoch).DiscoverAsync(1));
        var nextEpoch = Guid.NewGuid();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
            originalEpoch, nextEpoch, "tests", "restore");
        await DeleteDispatchAsync(database.DataSource, request.ScopeId, accepted.WorkId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new PostgreSqlDurableWorkStore(database.DataSource, nextEpoch).TryClaimAsync(
                candidate,
                "projection-worker"));

        Assert.Contains("dispatch row", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            original,
            await ReadWorkStateAndRevisionAsync(database.DataSource, request.ScopeId, accepted.WorkId));
        Assert.Equal(
            0,
            await CountHistoryAsync(database.DataSource, request.ScopeId, accepted.WorkId, "runtime_epoch_mismatch"));
    }

    [Fact]
    public async Task RequestCancellation_MissingDispatchRollsBackWorkHistoryAndCallback()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-cancel-projection-rollback", "command-cancel-projection-rollback");
        var accepted = (await client.EnqueueAsync(request)).Value!;
        var original = await ReadWorkStateAndRevisionAsync(database.DataSource, request.ScopeId, accepted.WorkId);
        await DeleteDispatchAsync(database.DataSource, request.ScopeId, accepted.WorkId);
        var callbackInvoked = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RequestCancellationAsync(
                request.ScopeId,
                accepted.WorkId,
                "tests",
                "consumer-requested",
                accepted.Revision,
                onProjectionApplied: (_, _, _) =>
                {
                    callbackInvoked = true;
                    return ValueTask.CompletedTask;
                }));

        Assert.Contains("dispatch row", exception.Message, StringComparison.Ordinal);
        Assert.False(callbackInvoked);
        Assert.Equal(
            original,
            await ReadWorkStateAndRevisionAsync(database.DataSource, request.ScopeId, accepted.WorkId));
        Assert.Equal(
            0,
            await CountHistoryAsync(database.DataSource, request.ScopeId, accepted.WorkId, "canceled_before_effect"));
    }

    [Fact]
    public async Task DisableScope_MissingDispatchRollsBackScopeWorkAndHistory()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await ApplySchemaAsync(database);
        var epoch = Guid.NewGuid();
        var client = CreateClient(database.DataSource, epoch);
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var request = CreateRequest("scope-disable-projection-rollback", "command-disable-projection-rollback");
        var accepted = (await client.EnqueueAsync(request)).Value!;
        var original = await ReadWorkStateAndRevisionAsync(database.DataSource, request.ScopeId, accepted.WorkId);
        await DeleteDispatchAsync(database.DataSource, request.ScopeId, accepted.WorkId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.DisableScopeAsync(request.ScopeId, "tests", "account-closed", 1));

        Assert.Contains("every authoritative Work", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            original,
            await ReadWorkStateAndRevisionAsync(database.DataSource, request.ScopeId, accepted.WorkId));
        Assert.Equal(
            "active:1",
            await ExecuteScopedScalarAsync<string>(
                database.DataSource,
                request.ScopeId,
                "SELECT state || ':' || generation::text FROM appsurface_durable.scope;"));
        Assert.Equal(
            0,
            await ExecuteScopedScalarAsync<long>(
                database.DataSource,
                request.ScopeId,
                "SELECT count(*) FROM appsurface_durable.scope_history;"));
        Assert.Equal(
            0,
            await CountHistoryAsync(database.DataSource, request.ScopeId, accepted.WorkId, "scope_disabled"));
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
        _ = CreateOptions(database.DataSource, epoch);
        var runtimeClient = CreateClient(runtimeDataSource, epoch);
        var scopeA = new DurableScopeId("scope-rls-a");
        var scopeB = new DurableScopeId("scope-rls-b");
        await runtimeClient.EnqueueAsync(CreateRequest(scopeA.Value, "command-rls-a"));
        await runtimeClient.EnqueueAsync(CreateRequest(scopeB.Value, "command-rls-b"));
        var runtimeStore = new PostgreSqlDurableWorkStore(runtimeDataSource, epoch);
        var scopeBCandidate = (await runtimeStore.DiscoverAsync(10)).Single(candidate => candidate.ScopeId == scopeB);
        var scopeBClaim = await runtimeStore.TryClaimAsync(scopeBCandidate, "role-policy-worker");
        Assert.NotNull(scopeBClaim);
        var scopeBPermit = await runtimeStore.TryAcquireEffectPermitAsync(scopeBClaim!);
        Assert.NotNull(scopeBPermit);

        Assert.Equal(1, await ExecuteScopedNonQueryAsync(
            runtimeDataSource,
            scopeB,
            """
            UPDATE appsurface_durable.effect_permit
            SET status = status, observed_at = clock_timestamp(), details = details, runtime_epoch = runtime_epoch;
            """));
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeB,
            "UPDATE appsurface_durable.effect_permit SET activity_id = 'tampered';");
        Assert.Equal(1, await ExecuteScopedNonQueryAsync(
            runtimeDataSource,
            scopeB,
            """
            INSERT INTO appsurface_durable.work_operator_command
                (scope_id, work_id, command_id, command_type, actor_id, reason_code,
                 request_schema, request_sha256, status)
            SELECT scope_id, work_id, 'role-policy-command', 'retry_safe', 'tests', 'access-policy',
                   'tests.operator.v1', decode(repeat('a', 64), 'hex'), 'started'
            FROM appsurface_durable.work;
            """));
        Assert.Equal(1, await ExecuteScopedNonQueryAsync(
            runtimeDataSource,
            scopeB,
            """
            UPDATE appsurface_durable.work_operator_command
            SET status = 'completed', resulting_state = 'effect_permitted',
                resulting_revision = (SELECT revision FROM appsurface_durable.work),
                completed_at = clock_timestamp()
            WHERE command_id = 'role-policy-command';
            """));
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeB,
            "UPDATE appsurface_durable.work_operator_command SET actor_id = 'tampered';");

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

            await using var ownDispatch = new NpgsqlCommand(
                "UPDATE appsurface_durable.dispatch SET updated_at = updated_at WHERE scope_id = @scope_id;",
                connection,
                transaction);
            ownDispatch.Parameters.AddWithValue("scope_id", scopeA.Value);
            Assert.Equal(1, await ownDispatch.ExecuteNonQueryAsync());

            await using var otherDispatch = new NpgsqlCommand(
                "UPDATE appsurface_durable.dispatch SET updated_at = updated_at WHERE scope_id = @scope_id;",
                connection,
                transaction);
            otherDispatch.Parameters.AddWithValue("scope_id", scopeB.Value);
            Assert.Equal(0, await otherDispatch.ExecuteNonQueryAsync());
            await transaction.CommitAsync();
        }

        Assert.Equal(0, await ExecuteScalarAsync<long>(runtimeDataSource, "SELECT count(*) FROM appsurface_durable.work;"));
        var disabled = await runtimeStore.DisableScopeAsync(scopeA, "tests", "access-policy", 1);
        Assert.Equal(PostgreSqlScopeMutationOutcome.Applied, disabled.Outcome);
        Assert.True(await ExecuteScopedScalarAsync<bool>(
            runtimeDataSource,
            scopeA,
            "SELECT EXISTS (SELECT 1 FROM appsurface_durable.scope_history);"));
        Assert.True(await ExecuteScopedScalarAsync<bool>(
            runtimeDataSource,
            scopeA,
            "SELECT EXISTS (SELECT 1 FROM appsurface_durable.work_history);"));
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeA,
            "UPDATE appsurface_durable.work SET payload_sha256 = decode(repeat('0', 64), 'hex');");
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeA,
            "UPDATE appsurface_durable.dispatch SET priority = priority + 1;");
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeA,
            "UPDATE appsurface_durable.scope SET created_at = clock_timestamp();");
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeA,
            "UPDATE appsurface_durable.scope SET state = 'active';");
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeA,
            "UPDATE appsurface_durable.scope_history SET reason_code = 'tampered';");
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeA,
            "DELETE FROM appsurface_durable.scope_history;");
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeA,
            "UPDATE appsurface_durable.work_history SET details = '{}'::jsonb;");
        await AssertScopedMutationDeniedAsync(
            runtimeDataSource,
            scopeA,
            "DELETE FROM appsurface_durable.work_history;");

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

    private static PostgreSqlDurableWorkTransactionWriter CreateWriter(
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        bool sendWakeNotification = false) =>
        new(
            dataSource,
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            CreateOptions(dataSource, runtimeEpoch, sendWakeNotification));

    private static PostgreSqlDurableWorkClient CreateClient(
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        bool sendWakeNotification = false) =>
        new(
            dataSource,
            PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry(),
            CreateOptions(dataSource, runtimeEpoch, sendWakeNotification));

    private static PostgreSqlDurableWorkOptions CreateOptions(
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        bool sendWakeNotification = false)
    {
        using var connection = dataSource.OpenConnection();
        using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT store_id, active_runtime_epoch
            FROM appsurface_durable.store_metadata
            WHERE singleton;
            """;
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        var storeId = reader.GetGuid(0);
        var activeEpoch = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        reader.Close();
        if (activeEpoch is null)
        {
            using var initialize = connection.CreateCommand();
            initialize.CommandText = """
                UPDATE appsurface_durable.store_metadata
                SET active_runtime_epoch = @runtime_epoch,
                    updated_at = clock_timestamp()
                WHERE singleton AND active_runtime_epoch IS NULL;
                """;
            initialize.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
            Assert.Equal(1, initialize.ExecuteNonQuery());
        }
        else
        {
            Assert.Equal(runtimeEpoch, activeEpoch.Value);
        }

        return new PostgreSqlDurableWorkOptions(
            runtimeEpoch,
            storeId,
            sendWakeNotification
                ? PostgreSqlDurableWakeNotificationMode.Enabled
                : PostgreSqlDurableWakeNotificationMode.Disabled);
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

    private static async ValueTask<(
        string ContractName,
        string ContractVersion,
        string Classification,
        string RetentionPolicyId,
        byte[] Payload,
        byte[] Sha256)> ReadPersistedResultAsync(
            NpgsqlDataSource dataSource,
            DurableScopeId scopeId,
            DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            SELECT result_contract_id, result_schema_version, result_classification,
                   result_retention_policy_id, result_payload, result_sha256
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<byte[]>(4),
            reader.GetFieldValue<byte[]>(5));
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

    private static async ValueTask DeleteDispatchAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM appsurface_durable.dispatch
            WHERE scope_id = @scope_id AND aggregate_kind = 'work' AND aggregate_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask<(string State, long Revision)> ReadWorkStateAndRevisionAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            "SELECT state, revision FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt64(1));
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

    private static async ValueTask WaitForDatabaseLockAsync(
        NpgsqlDataSource dataSource,
        string applicationName)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_stat_activity
                    WHERE application_name = @application_name
                      AND wait_event_type = 'Lock'
                );
                """);
            command.Parameters.AddWithValue("application_name", applicationName);
            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException("Runtime epoch rotation did not wait for the in-flight acceptance transaction.");
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
            GRANT SELECT ON appsurface_durable.store_metadata TO "{runtimeRole}";
            GRANT SELECT ON appsurface_durable.schema_migration TO "{runtimeRole}";
            GRANT SELECT, INSERT, UPDATE
                ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch TO "{runtimeRole}";
            GRANT SELECT, INSERT ON appsurface_durable.scope TO "{runtimeRole}";
            REVOKE UPDATE
                ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch FROM "{runtimeRole}";
            GRANT UPDATE (generation, state, updated_at) ON appsurface_durable.scope TO "{runtimeRole}";
            GRANT SELECT, INSERT ON appsurface_durable.scope_history TO "{runtimeRole}";
            GRANT SELECT, INSERT ON appsurface_durable.work TO "{runtimeRole}";
            GRANT UPDATE (state, due_at, updated_at, terminal_at, cancellation_requested_at, attempt_number,
                lease_generation, lease_owner, lease_started_at, lease_expires_at, runtime_epoch, revision,
                result_contract_id, result_schema_version, result_codec_id, result_classification,
                result_retention_policy_id, result_payload, result_sha256, terminal_code)
                ON appsurface_durable.work TO "{runtimeRole}";
            GRANT SELECT, INSERT ON appsurface_durable.work_history TO "{runtimeRole}";
            GRANT SELECT, INSERT ON appsurface_durable.effect_permit TO "{runtimeRole}";
            GRANT UPDATE (status, observed_at, details, runtime_epoch) ON appsurface_durable.effect_permit TO "{runtimeRole}";
            GRANT SELECT, INSERT ON appsurface_durable.dispatch TO "{runtimeRole}";
            GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.dispatch TO "{runtimeRole}";
            GRANT SELECT, INSERT ON appsurface_durable.work_operator_command TO "{runtimeRole}";
            GRANT UPDATE (status, resulting_state, resulting_revision, completed_at)
                ON appsurface_durable.work_operator_command TO "{runtimeRole}";
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA appsurface_durable TO "{runtimeRole}";
            GRANT SELECT ON appsurface_durable.dispatch TO "{dispatcherRole}";
            """;
        await using var command = ownerDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<T> ExecuteScopedScalarAsync<T>(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask<int> ExecuteScopedNonQueryAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask AssertScopedMutationDeniedAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTestScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var denied = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private sealed record CompletionTruth(
        string State,
        bool IsTerminal,
        string PermitStatus,
        bool CancellationRequested);
}
