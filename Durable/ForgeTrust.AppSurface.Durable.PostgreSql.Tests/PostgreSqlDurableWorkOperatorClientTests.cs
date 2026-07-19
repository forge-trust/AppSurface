using System.Text;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableWorkOperatorClientTests
{
    [Fact]
    public async Task Reconcile_CommitsAppliedNotAppliedAndUnknownProof_Idempotently()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registrations = new[]
        {
            new OperatorRegistration("operator.reconcile.applied", DurableProviderSafety.ReconcileBeforeRetry,
                new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Applied, Result("applied"))),
            new OperatorRegistration("operator.reconcile.not-applied", DurableProviderSafety.ReconcileBeforeRetry,
                new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.NotApplied, null)),
            new OperatorRegistration("operator.reconcile.unknown", DurableProviderSafety.ReconcileBeforeRetry,
                new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null)),
        };
        var registry = new DurableWorkRegistry(registrations);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            registry,
            await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(database.DataSource, registry, NullServices.Instance, epoch);

        var expected = new[]
        {
            DurableWorkState.Succeeded,
            DurableWorkState.Ready,
            DurableWorkState.Suspended,
        };
        for (var index = 0; index < registrations.Length; index++)
        {
            var scope = new DurableScopeId($"operator-reconcile-{index}");
            var accepted = await AcceptAndPermitAsync(
                client, database.DataSource, epoch, registrations[index], scope, $"reconcile-{index}");
            var revision = await ForceStateAsync(
                database.DataSource, scope, accepted.WorkId, "suspended_reconciliation_required", "ambiguous_external_outcome");
            var request = new DurableWorkReconcileRequest(
                scope,
                accepted.WorkId,
                new DurableCommandId($"operator-reconcile-command-{index}"),
                "operator-test",
                "provider-proof",
                revision);

            var applied = await operators.ReconcileAsync(request);
            var duplicate = await operators.ReconcileAsync(request);

            Assert.True(applied.IsSuccess);
            Assert.Equal(DurableWorkOperatorOutcome.Applied, applied.Value!.Outcome);
            Assert.Equal(expected[index], applied.Value.State);
            Assert.Equal(DurableWorkOperatorOutcome.Duplicate, duplicate.Value!.Outcome);
            Assert.Equal(1, registrations[index].ReconciliationCount);
            Assert.Equal(expected[index], await ReadStateAsync(database.DataSource, scope, accepted.WorkId));
        }
    }

    [Fact]
    public async Task ResolveRetrySafeAndRecoveryRelease_ApplyOnlyAuditedSafeTransitions()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var manual = new OperatorRegistration(
            "operator.manual", DurableProviderSafety.ManualResolution,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var replaySafe = new OperatorRegistration(
            "operator.replay-safe", DurableProviderSafety.ProviderKeyed,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([manual, replaySafe]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource,
            registry,
            await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(database.DataSource, registry, NullServices.Instance, epoch);

        var manualScope = new DurableScopeId("operator-manual");
        var manualAccepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, manual, manualScope, "manual");
        var manualRevision = await ForceStateAsync(
            database.DataSource, manualScope, manualAccepted.WorkId, "suspended_manual_resolution", "ambiguous_external_outcome");
        var resolve = new DurableWorkManualResolutionRequest(
            manualScope,
            manualAccepted.WorkId,
            new DurableCommandId("operator-manual-command"),
            "operator-test",
            "provider-applied",
            manualRevision,
            DurableManualResolutionKind.Applied,
            Result("manual-result"));

        var resolved = await operators.ResolveAsync(resolve);
        var resolvedDuplicate = await operators.ResolveAsync(resolve);
        Assert.Equal(DurableWorkState.Succeeded, resolved.Value!.State);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, resolvedDuplicate.Value!.Outcome);

        var retryScope = new DurableScopeId("operator-retry-safe");
        var retryAccepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, replaySafe, retryScope, "retry-safe");
        var retryRevision = await ForceStateAsync(
            database.DataSource,
            retryScope,
            retryAccepted.WorkId,
            "suspended_ambiguous_external_outcome",
            "ambiguous_external_outcome",
            cancellationRequested: true);
        var retry = await operators.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            retryScope,
            retryAccepted.WorkId,
            new DurableCommandId("operator-retry-command"),
            "operator-test",
            "authorized-replay",
            retryRevision));
        Assert.Equal(DurableWorkState.Ready, retry.Value!.State);
        Assert.False(await ReadCancellationRequestedAsync(database.DataSource, retryScope, retryAccepted.WorkId));

        var recoveryScope = new DurableScopeId("operator-recovery");
        var recoveryRequest = Request(recoveryScope, replaySafe, "recovery");
        var recoveryAccepted = (await client.EnqueueAsync(recoveryRequest)).Value!;
        var nextEpoch = Guid.NewGuid();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.RotateRuntimeEpochAsync(epoch, nextEpoch, "operator-test", "restore");
        var nextStore = new PostgreSqlDurableWorkStore(database.DataSource, nextEpoch);
        var candidate = (await nextStore.DiscoverAsync(100)).Single(item => item.WorkId == recoveryAccepted.WorkId);
        Assert.Null(await nextStore.TryClaimAsync(candidate, "recovery-worker"));
        var recoveryRevision = await ReadRevisionAsync(database.DataSource, recoveryScope, recoveryAccepted.WorkId);
        var nextOperators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, nextEpoch);
        var released = await nextOperators.ReleaseAfterRecoveryAsync(new DurableWorkRecoveryReleaseRequest(
            recoveryScope,
            recoveryAccepted.WorkId,
            new DurableCommandId("operator-recovery-command"),
            "operator-test",
            "restore-release",
            recoveryRevision));

        Assert.Equal(DurableWorkState.Ready, released.Value!.State);
        Assert.Equal(nextEpoch, await ReadEpochAsync(database.DataSource, recoveryScope, recoveryAccepted.WorkId));
        Assert.Equal(3, await CountAsync(database.DataSource, "SELECT count(*) FROM appsurface_durable.work_operator_command WHERE status = 'completed';"));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent)]
    [InlineData(DurableProviderSafety.ProviderKeyed)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry)]
    [InlineData(DurableProviderSafety.ManualResolution)]
    public async Task RecoveryRelease_MovesExactAmbiguousPermitToNewEpochAndLeavesResolutionAvailable(
        DurableProviderSafety safety)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var originalEpoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            $"operator.recovery-permit.{safety}",
            safety,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.NotApplied, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, originalEpoch));
        var scope = new DurableScopeId($"operator-recovery-permit-{safety}");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, originalEpoch, registration, scope, $"recovery-permit-{safety}");
        await MakeDispatchDueAsync(database.DataSource, scope, accepted.WorkId);
        var nextEpoch = Guid.NewGuid();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
            originalEpoch, nextEpoch, "operator-test", "restore");
        var nextStore = new PostgreSqlDurableWorkStore(database.DataSource, nextEpoch);
        var candidate = (await nextStore.DiscoverAsync(100)).Single(item => item.WorkId == accepted.WorkId);
        Assert.Null(await nextStore.TryClaimAsync(candidate, "recovery-permit-worker"));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, nextEpoch);
        var release = await operators.ReleaseAfterRecoveryAsync(new DurableWorkRecoveryReleaseRequest(
            scope,
            accepted.WorkId,
            new DurableCommandId($"operator-recovery-permit-release-{safety}"),
            "operator-test",
            "restore-release",
            await ReadRevisionAsync(database.DataSource, scope, accepted.WorkId)));

        Assert.True(release.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, release.Value!.State);
        Assert.Equal(nextEpoch, await ReadEpochAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(nextEpoch, await ReadPermitEpochAsync(database.DataSource, scope, accepted.WorkId));

        DurableOperationResult<DurableWorkOperatorResult> resolved;
        if (safety is DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed)
        {
            resolved = await operators.RetrySafeAsync(new DurableWorkRetrySafeRequest(
                scope,
                accepted.WorkId,
                new DurableCommandId($"operator-recovery-permit-retry-{safety}"),
                "operator-test",
                "authorized-retry",
                release.Value.Revision));
        }
        else if (safety == DurableProviderSafety.ReconcileBeforeRetry)
        {
            resolved = await operators.ReconcileAsync(new DurableWorkReconcileRequest(
                scope,
                accepted.WorkId,
                new DurableCommandId("operator-recovery-permit-reconcile"),
                "operator-test",
                "provider-proof",
                release.Value.Revision));
        }
        else
        {
            resolved = await operators.ResolveAsync(new DurableWorkManualResolutionRequest(
                scope,
                accepted.WorkId,
                new DurableCommandId("operator-recovery-permit-resolve"),
                "operator-test",
                "provider-not-applied",
                release.Value.Revision,
                DurableManualResolutionKind.ProvenNotApplied));
        }

        Assert.True(resolved.IsSuccess);
        Assert.Equal(DurableWorkState.Ready, resolved.Value!.State);
    }

    [Fact]
    public async Task RecoveryRelease_IgnoresHistoricalSafePermitWhenCurrentAttemptHasNoPermit()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var originalEpoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            "operator.recovery-historical-permit",
            DurableProviderSafety.ProviderKeyed,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, originalEpoch));
        var scope = new DurableScopeId("operator-recovery-historical-permit");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, originalEpoch, registration, scope, "recovery-historical-permit");
        var originalStore = new PostgreSqlDurableWorkStore(database.DataSource, originalEpoch);
        await ExpireLeaseAndDispatchAsync(database.DataSource, scope, accepted.WorkId);
        var retryCandidate = (await originalStore.DiscoverAsync(100)).Single(item => item.WorkId == accepted.WorkId);
        var secondAttempt = await originalStore.TryClaimAsync(retryCandidate, "second-attempt-worker");
        Assert.NotNull(secondAttempt);
        Assert.Equal(2, secondAttempt.AttemptNumber);

        await MakeDispatchDueAsync(database.DataSource, scope, accepted.WorkId);
        var nextEpoch = Guid.NewGuid();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
            originalEpoch, nextEpoch, "operator-test", "restore");
        var nextStore = new PostgreSqlDurableWorkStore(database.DataSource, nextEpoch);
        var recoveryCandidate = (await nextStore.DiscoverAsync(100)).Single(item => item.WorkId == accepted.WorkId);
        Assert.Null(await nextStore.TryClaimAsync(recoveryCandidate, "recovery-worker"));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, nextEpoch);

        var release = await operators.ReleaseAfterRecoveryAsync(new DurableWorkRecoveryReleaseRequest(
            scope,
            accepted.WorkId,
            new DurableCommandId("operator-recovery-historical-permit-release"),
            "operator-test",
            "restore-release",
            await ReadRevisionAsync(database.DataSource, scope, accepted.WorkId)));

        Assert.True(release.IsSuccess);
        Assert.Equal(DurableWorkState.Ready, release.Value!.State);
        Assert.Equal(nextEpoch, await ReadEpochAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(originalEpoch, await ReadPermitEpochAsync(database.DataSource, scope, accepted.WorkId));
        var releasedCandidate = (await nextStore.DiscoverAsync(100)).Single(item => item.WorkId == accepted.WorkId);
        Assert.NotNull(await nextStore.TryClaimAsync(releasedCandidate, "released-worker"));
    }

    [Fact]
    public async Task Reconcile_CorruptPayloadHashFailsBeforeProviderAndRollsBackStartedCommand()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            "operator.reconcile-corrupt-payload",
            DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.NotApplied, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var scope = new DurableScopeId("operator-reconcile-corrupt-payload");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, "reconcile-corrupt-payload");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        await CorruptPayloadAsync(database.DataSource, scope, accepted.WorkId);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new PostgreSqlDurableWorkOperatorClient(
                database.DataSource, registry, NullServices.Instance, epoch)
                .ReconcileAsync(new DurableWorkReconcileRequest(
                    scope,
                    accepted.WorkId,
                    new DurableCommandId("operator-reconcile-corrupt-payload-command"),
                    "operator-test",
                    "provider-proof",
                    revision)));

        Assert.Contains("payload hash", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, registration.ReconciliationCount);
        Assert.Equal(
            0,
            await CountAsync(
                database.DataSource,
                "SELECT count(*) FROM appsurface_durable.work_operator_command WHERE command_id = 'operator-reconcile-corrupt-payload-command';"));
    }

    [Fact]
    public async Task Resolve_PersistsFingerprintSchemaAndStructuredAudit_AndRequiresExactPermit()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var manual = new OperatorRegistration(
            "operator.audit", DurableProviderSafety.ManualResolution,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([manual]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId("operator-audit");
        var accepted = await AcceptAndPermitAsync(client, database.DataSource, epoch, manual, scope, "audit");
        var revision = await ForceStateAsync(
            database.DataSource, scope, accepted.WorkId, "suspended_manual_resolution", "ambiguous_external_outcome");
        var commandId = new DurableCommandId("operator-audit-command");
        var request = new DurableWorkManualResolutionRequest(
            scope,
            accepted.WorkId,
            commandId,
            "operator-test",
            "provider-not-applied",
            revision,
            DurableManualResolutionKind.ProvenNotApplied);

        var applied = await operators.ResolveAsync(request);

        Assert.Equal(DurableWorkState.Ready, applied.Value!.State);
        Assert.Equal(
            request.Fingerprint.SchemaId,
            await ReadOperatorScalarAsync<string>(database.DataSource, scope, commandId, "request_schema"));
        Assert.Equal(
            commandId.Value,
            await ReadHistoryScalarAsync<string>(database.DataSource, scope, accepted.WorkId, "command_id"));

        await UpdateOperatorSchemaAsync(database.DataSource, scope, commandId, "unsupported.operator.schema.v2");
        var unsupported = await operators.ResolveAsync(request);
        Assert.False(unsupported.IsSuccess);
        Assert.Equal(DurableProblemCodes.CommandConflict, unsupported.Problem!.Code);

        var missingPermitScope = new DurableScopeId("operator-missing-exact-permit");
        var missingPermit = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, manual, missingPermitScope, "missing-exact-permit");
        var missingPermitRevision = await ForceStateAsync(
            database.DataSource,
            missingPermitScope,
            missingPermit.WorkId,
            "suspended_manual_resolution",
            "ambiguous_external_outcome");
        missingPermitRevision = await AdvanceFenceWithoutPermitAsync(
            database.DataSource, missingPermitScope, missingPermit.WorkId, missingPermitRevision);

        var rejected = await operators.ResolveAsync(new DurableWorkManualResolutionRequest(
            missingPermitScope,
            missingPermit.WorkId,
            new DurableCommandId("operator-missing-exact-permit-command"),
            "operator-test",
            "unsafe-proof",
            missingPermitRevision,
            DurableManualResolutionKind.ProvenNotApplied));

        Assert.False(rejected.IsSuccess);
        Assert.Equal(DurableProblemCodes.OperatorTransitionRejected, rejected.Problem!.Code);
    }

    [Fact]
    public async Task Resolve_ConcurrentExactOrConflictingCommandsReturnDurableReplayOutcomes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var manual = new OperatorRegistration(
            "operator.concurrent", DurableProviderSafety.ManualResolution,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([manual]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId("operator-concurrent");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, manual, scope, "concurrent");
        var revision = await ForceStateAsync(
            database.DataSource, scope, accepted.WorkId, "suspended_manual_resolution", "ambiguous_external_outcome");
        var request = new DurableWorkManualResolutionRequest(
            scope,
            accepted.WorkId,
            new DurableCommandId("operator-concurrent-command"),
            "operator-test",
            "provider-not-applied",
            revision,
            DurableManualResolutionKind.ProvenNotApplied);

        await using var blockerConnection = await database.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await SetScopeAsync(blockerConnection, blockerTransaction, scope);
        await using (var blocker = new NpgsqlCommand(
            "SELECT 1 FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id FOR UPDATE;",
            blockerConnection,
            blockerTransaction))
        {
            blocker.Parameters.AddWithValue("scope_id", scope.Value);
            blocker.Parameters.AddWithValue("work_id", accepted.WorkId.Value);
            _ = await blocker.ExecuteScalarAsync();
        }

        var first = operators.ResolveAsync(request).AsTask();
        var second = operators.ResolveAsync(request).AsTask();
        await Task.Delay(100);
        await blockerTransaction.CommitAsync();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Contains(results, result => result.Value!.Outcome == DurableWorkOperatorOutcome.Applied);
        Assert.Contains(results, result => result.Value!.Outcome == DurableWorkOperatorOutcome.Duplicate);

        var conflictScope = new DurableScopeId("operator-concurrent-conflict");
        var conflictAccepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, manual, conflictScope, "concurrent-conflict");
        var conflictRevision = await ForceStateAsync(
            database.DataSource,
            conflictScope,
            conflictAccepted.WorkId,
            "suspended_manual_resolution",
            "ambiguous_external_outcome");
        var conflictCommand = new DurableCommandId("operator-concurrent-conflict-command");
        var conflictRequests = new[]
        {
            new DurableWorkManualResolutionRequest(
                conflictScope, conflictAccepted.WorkId, conflictCommand, "operator-test", "proof-a",
                conflictRevision, DurableManualResolutionKind.ProvenNotApplied),
            new DurableWorkManualResolutionRequest(
                conflictScope, conflictAccepted.WorkId, conflictCommand, "operator-test", "proof-b",
                conflictRevision, DurableManualResolutionKind.ProvenNotApplied),
        };
        await using var conflictConnection = await database.DataSource.OpenConnectionAsync();
        await using var conflictTransaction = await conflictConnection.BeginTransactionAsync();
        await SetScopeAsync(conflictConnection, conflictTransaction, conflictScope);
        await using (var blocker = new NpgsqlCommand(
            "SELECT 1 FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id FOR UPDATE;",
            conflictConnection,
            conflictTransaction))
        {
            blocker.Parameters.AddWithValue("scope_id", conflictScope.Value);
            blocker.Parameters.AddWithValue("work_id", conflictAccepted.WorkId.Value);
            _ = await blocker.ExecuteScalarAsync();
        }

        var conflictTasks = conflictRequests.Select(request => operators.ResolveAsync(request).AsTask()).ToArray();
        await Task.Delay(100);
        await conflictTransaction.CommitAsync();
        var conflictResults = await Task.WhenAll(conflictTasks);

        Assert.Single(conflictResults, result => result.IsSuccess);
        Assert.Single(conflictResults, result =>
            !result.IsSuccess && result.Problem!.Code == DurableProblemCodes.CommandConflict);
    }

    [Fact]
    public async Task OperatorFailures_ReturnTypedRecoveryProofAndRegistrationProblems()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var reconcile = new OperatorRegistration(
            "operator.typed-reconcile", DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.NotApplied, null));
        var manual = new OperatorRegistration(
            "operator.typed-manual", DurableProviderSafety.ManualResolution,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([reconcile, manual]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);

        var reconcileScope = new DurableScopeId("operator-typed-registration");
        var reconcileAccepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, reconcile, reconcileScope, "typed-registration");
        var reconcileRevision = await ForceStateAsync(
            database.DataSource,
            reconcileScope,
            reconcileAccepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        var reconcileRequest = new DurableWorkReconcileRequest(
            reconcileScope,
            reconcileAccepted.WorkId,
            new DurableCommandId("operator-typed-registration-command"),
            "operator-test",
            "provider-proof",
            reconcileRevision);
        var unavailable = await new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, new DurableWorkRegistry([]), NullServices.Instance, epoch)
            .ReconcileAsync(reconcileRequest);

        Assert.False(unavailable.IsSuccess);
        Assert.Equal(DurableProblemCodes.WorkContractUnavailable, unavailable.Problem!.Code);
        var resumed = await operators.ReconcileAsync(reconcileRequest);
        Assert.True(resumed.IsSuccess);
        Assert.Equal(1, reconcile.ReconciliationCount);

        var manualScope = new DurableScopeId("operator-typed-proof");
        var manualAccepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, manual, manualScope, "typed-proof");
        var manualRevision = await ForceStateAsync(
            database.DataSource,
            manualScope,
            manualAccepted.WorkId,
            "suspended_manual_resolution",
            "ambiguous_external_outcome");
        var proofRequired = await operators.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            manualScope,
            manualAccepted.WorkId,
            new DurableCommandId("operator-typed-proof-command"),
            "operator-test",
            "ordinary-retry",
            manualRevision));

        Assert.False(proofRequired.IsSuccess);
        Assert.Equal(DurableProblemCodes.OperatorProofRequired, proofRequired.Problem!.Code);

        var missingScope = await operators.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            new DurableScopeId("operator-missing-scope"),
            new DurableWorkId("operator-missing-work"),
            new DurableCommandId("operator-missing-scope-command"),
            "operator-test",
            "missing-scope",
            1));
        Assert.False(missingScope.IsSuccess);
        Assert.Equal(DurableProblemCodes.ScopeNotFound, missingScope.Problem!.Code);

        var disabledScope = new DurableScopeId("operator-disabled-scope");
        var disabledAccepted = (await client.EnqueueAsync(Request(disabledScope, manual, "disabled-scope"))).Value!;
        _ = await new PostgreSqlDurableWorkStore(database.DataSource, epoch)
            .DisableScopeAsync(disabledScope, "operator-test", "disabled", 1);
        var disabled = await operators.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            disabledScope,
            disabledAccepted.WorkId,
            new DurableCommandId("operator-disabled-scope-command"),
            "operator-test",
            "disabled-scope",
            1));
        Assert.False(disabled.IsSuccess);
        Assert.Equal(DurableProblemCodes.ScopeDisabled, disabled.Problem!.Code);

        var nextEpoch = Guid.NewGuid();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource)
            .RotateRuntimeEpochAsync(epoch, nextEpoch, "operator-test", "restore");
        var staleEpoch = await operators.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            manualScope,
            manualAccepted.WorkId,
            new DurableCommandId("operator-stale-epoch-command"),
            "operator-test",
            "stale-epoch",
            manualRevision));
        Assert.False(staleEpoch.IsSuccess);
        Assert.Equal(DurableProblemCodes.RecoveryEpochRequired, staleEpoch.Problem!.Code);
    }

    private static async ValueTask<Guid> InitializeAsync(NpgsqlDataSource dataSource)
    {
        var schema = new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "operator-test", "initial");
        return epoch;
    }

    private static async ValueTask<PostgreSqlDurableWorkOptions> OptionsAsync(NpgsqlDataSource dataSource, Guid epoch)
    {
        var status = await new PostgreSqlDurableRuntimeSchemaManager(dataSource).GetStatusAsync();
        return new PostgreSqlDurableWorkOptions(epoch, status.StoreId);
    }

    private static async ValueTask<DurableWorkAcceptance> AcceptAndPermitAsync(
        PostgreSqlDurableWorkClient client,
        NpgsqlDataSource dataSource,
        Guid epoch,
        OperatorRegistration registration,
        DurableScopeId scope,
        string identity)
    {
        var accepted = (await client.EnqueueAsync(Request(scope, registration, identity))).Value!;
        var store = new PostgreSqlDurableWorkStore(dataSource, epoch);
        var candidate = (await store.DiscoverAsync(100)).Single(item => item.WorkId == accepted.WorkId);
        var claim = await store.TryClaimAsync(candidate, $"worker-{identity}");
        Assert.NotNull(await store.TryAcquireEffectPermitAsync(claim!));
        return accepted;
    }

    private static DurableWorkRequest Request(
        DurableScopeId scope,
        OperatorRegistration registration,
        string identity) => new(
        scope,
        new DurableCommandId($"accept-{identity}"),
        $"idempotency-{identity}",
        registration.WorkName,
        registration.WorkVersion,
        registration.WorkCodec.EncodeObject(Encoding.UTF8.GetBytes(identity)),
        registration.ProviderSafety);

    private static DurableEncodedPayload Result(string value) => new(
        "operator.result",
        "v1",
        DurableDataClassification.Operational,
        Encoding.UTF8.GetBytes(value));

    private static async ValueTask<long> ForceStateAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work,
        string state,
        string terminalCode,
        bool cancellationRequested = false)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            """
            WITH changed AS
            (
                UPDATE appsurface_durable.work
                SET state = @state, terminal_code = @terminal_code,
                    cancellation_requested_at = CASE WHEN @cancel THEN clock_timestamp() ELSE cancellation_requested_at END,
                    lease_owner = NULL, lease_started_at = NULL, lease_expires_at = NULL,
                    revision = revision + 1
                WHERE scope_id = @scope_id AND work_id = @work_id
                RETURNING revision
            ), dispatched AS
            (
                UPDATE appsurface_durable.dispatch
                SET state = 'suspended', expected_revision = (SELECT revision FROM changed)
                WHERE scope_id = @scope_id AND aggregate_id = @work_id
            )
            SELECT revision FROM changed;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("terminal_code", terminalCode);
        command.Parameters.AddWithValue("cancel", cancellationRequested);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        var revision = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return revision;
    }

    private static async ValueTask<long> AdvanceFenceWithoutPermitAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work,
        long expectedRevision)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            """
            WITH changed AS
            (
                UPDATE appsurface_durable.work
                SET attempt_number = attempt_number + 1,
                    lease_generation = lease_generation + 1,
                    revision = revision + 1
                WHERE scope_id = @scope_id AND work_id = @work_id AND revision = @expected_revision
                RETURNING revision
            ), dispatched AS
            (
                UPDATE appsurface_durable.dispatch
                SET expected_revision = (SELECT revision FROM changed)
                WHERE scope_id = @scope_id AND aggregate_id = @work_id
            )
            SELECT revision FROM changed;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        command.Parameters.AddWithValue("expected_revision", expectedRevision);
        var revision = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return revision;
    }

    private static async ValueTask<T> ReadOperatorScalarAsync<T>(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableCommandId commandId,
        string projection)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            $"SELECT {projection} FROM appsurface_durable.work_operator_command WHERE scope_id = @scope_id AND command_id = @command_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<T> ReadHistoryScalarAsync<T>(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work,
        string projection)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            $"SELECT {projection} FROM appsurface_durable.work_history WHERE scope_id = @scope_id AND work_id = @work_id AND event_type = 'operator_manual_resolve' ORDER BY event_id DESC LIMIT 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask UpdateOperatorSchemaAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableCommandId commandId,
        string schema)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            """
            UPDATE appsurface_durable.work_operator_command
            SET request_schema = @schema
            WHERE scope_id = @scope_id AND command_id = @command_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask<DurableWorkState> ReadStateAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work)
    {
        var state = await ReadScalarAsync<string>(dataSource, scope, work, "state");
        return state switch
        {
            "succeeded" => DurableWorkState.Succeeded,
            "retry_wait" => DurableWorkState.Ready,
            _ when state.StartsWith("suspended_", StringComparison.Ordinal) => DurableWorkState.Suspended,
            _ => throw new InvalidDataException(state),
        };
    }

    private static ValueTask<long> ReadRevisionAsync(NpgsqlDataSource dataSource, DurableScopeId scope, DurableWorkId work) =>
        ReadScalarAsync<long>(dataSource, scope, work, "revision");

    private static ValueTask<Guid> ReadEpochAsync(NpgsqlDataSource dataSource, DurableScopeId scope, DurableWorkId work) =>
        ReadScalarAsync<Guid>(dataSource, scope, work, "runtime_epoch");

    private static async ValueTask<Guid> ReadPermitEpochAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            "SELECT runtime_epoch FROM appsurface_durable.effect_permit WHERE scope_id = @scope_id AND work_id = @work_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask MakeDispatchDueAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            "UPDATE appsurface_durable.dispatch SET due_at = clock_timestamp() - interval '1 second' WHERE scope_id = @scope_id AND aggregate_id = @work_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask ExpireLeaseAndDispatchAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            """
            WITH expired AS
            (
                UPDATE appsurface_durable.work
                SET lease_expires_at = clock_timestamp() - interval '1 second'
                WHERE scope_id = @scope_id AND work_id = @work_id
                RETURNING work_id
            )
            UPDATE appsurface_durable.dispatch
            SET due_at = clock_timestamp() - interval '1 second'
            FROM expired
            WHERE scope_id = @scope_id AND aggregate_id = expired.work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask CorruptPayloadAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            "UPDATE appsurface_durable.work SET payload = decode('00', 'hex') WHERE scope_id = @scope_id AND work_id = @work_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static ValueTask<bool> ReadCancellationRequestedAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work) =>
        ReadScalarAsync<bool>(dataSource, scope, work, "cancellation_requested_at IS NOT NULL");

    private static async ValueTask<T> ReadScalarAsync<T>(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work,
        string projection)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            $"SELECT {projection} FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask SetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scope)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class OperatorRegistration(
        string workName,
        DurableProviderSafety safety,
        DurableEncodedEffectReconciliation proof) : DurableWorkRegistration(
            workName,
            "v1",
            safety,
            new OperatorCodec("operator.work"),
            new OperatorCodec("operator.result"))
    {
        public int ReconciliationCount { get; private set; }

        public override bool CanReconcile => ProviderSafety == DurableProviderSafety.ReconcileBeforeRetry;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            throw new NotSupportedException();

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default)
        {
            ReconciliationCount++;
            return ValueTask.FromResult(proof);
        }
    }

    private sealed class OperatorCodec(string contractName) : IDurablePayloadCodec
    {
        public Type PayloadType => typeof(byte[]);
        public string ContractName => contractName;
        public string ContractVersion => "v1";
        public DurableDataClassification Classification => DurableDataClassification.Operational;
        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload EncodeObject(object value) => new(
            ContractName, ContractVersion, Classification, (byte[])value, RetentionPolicyId);

        public object DecodeObject(DurableEncodedPayload payload)
        {
            if (payload.ContractName != ContractName || payload.ContractVersion != ContractVersion
                || payload.Classification != Classification || payload.RetentionPolicyId != RetentionPolicyId)
            {
                throw new InvalidOperationException("Payload does not match the operator test codec.");
            }

            return payload.Content.ToArray();
        }
    }

    private sealed class NullServices : IServiceProvider
    {
        internal static NullServices Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }
}
