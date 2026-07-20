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

    [Theory]
    [InlineData(DurableEffectReconciliationKind.Applied)]
    [InlineData(DurableEffectReconciliationKind.NotApplied)]
    public async Task Reconcile_CancellationDuringProviderCallRejectsStaleProofWithoutMutation(
        DurableEffectReconciliationKind proofKind)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var providerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new OperatorRegistration(
            $"operator.reconcile-cancel-race.{proofKind}",
            DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(
                proofKind,
                proofKind == DurableEffectReconciliationKind.Applied ? Result("cancel-race") : null),
            providerEntered,
            releaseProvider);
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId($"operator-reconcile-cancel-race-{proofKind}");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, $"reconcile-cancel-race-{proofKind}");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        var commandId = new DurableCommandId($"operator-reconcile-cancel-race-command-{proofKind}");
        var request = new DurableWorkReconcileRequest(
            scope,
            accepted.WorkId,
            commandId,
            "operator-test",
            "provider-proof",
            revision);

        var reconciliation = operators.ReconcileAsync(request).AsTask();
        await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        PostgreSqlCancellationResult cancellation;
        try
        {
            cancellation = await store.RequestCancellationAsync(
                scope,
                accepted.WorkId,
                "operator-test",
                "cancel-during-reconciliation",
                revision);
        }
        finally
        {
            releaseProvider.TrySetResult(true);
        }

        var result = await reconciliation;

        Assert.Equal(DurableWorkState.Suspended, cancellation.State);
        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.WorkRevisionConflict, result.Problem!.Code);
        Assert.Equal(DurableWorkState.Suspended, await ReadStateAsync(database.DataSource, scope, accepted.WorkId));
        Assert.True(await ReadCancellationRequestedAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(
            cancellation.Revision,
            await ReadRevisionAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(
            "started",
            await ReadOperatorScalarAsync<string>(database.DataSource, scope, commandId, "status"));
        Assert.Equal(1, registration.ReconciliationCount);
    }

    [Fact]
    public async Task Reconcile_RuntimeEpochRotationDuringProviderCallRejectsProofAndReplay()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var providerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new OperatorRegistration(
            "operator.reconcile-epoch-race",
            DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Applied, Result("epoch-race")),
            providerEntered,
            releaseProvider);
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId("operator-reconcile-epoch-race");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, "reconcile-epoch-race");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        var commandId = new DurableCommandId("operator-reconcile-epoch-race-command");
        var request = new DurableWorkReconcileRequest(
            scope,
            accepted.WorkId,
            commandId,
            "operator-test",
            "provider-proof",
            revision);

        var reconciliation = operators.ReconcileAsync(request).AsTask();
        await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var nextEpoch = Guid.NewGuid();
        try
        {
            await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
                epoch, nextEpoch, "operator-test", "rotate-during-reconciliation");
        }
        finally
        {
            releaseProvider.TrySetResult(true);
        }

        var result = await reconciliation;
        var replay = await operators.ReconcileAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.RecoveryEpochRequired, result.Problem!.Code);
        Assert.False(replay.IsSuccess);
        Assert.Equal(DurableProblemCodes.RecoveryEpochRequired, replay.Problem!.Code);
        Assert.Equal(DurableWorkState.Suspended, await ReadStateAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(revision, await ReadRevisionAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(
            "started",
            await ReadOperatorScalarAsync<string>(database.DataSource, scope, commandId, "status"));
        Assert.Equal(1, registration.ReconciliationCount);
    }

    [Theory]
    [InlineData("wrong-safety", DurableProblemCodes.OperatorTransitionRejected)]
    [InlineData("wrong-state", DurableProblemCodes.OperatorTransitionRejected)]
    [InlineData("no-exact-permit", DurableProblemCodes.OperatorTransitionRejected)]
    [InlineData("missing", DurableProblemCodes.WorkNotFound)]
    [InlineData("stale", DurableProblemCodes.WorkRevisionConflict)]
    [InlineData("terminal", DurableProblemCodes.AlreadyTerminal)]
    public async Task Reconcile_RejectsInvalidStartingTruthWithoutCallingProvider(
        string scenario,
        string expectedProblemCode)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var safety = scenario == "wrong-safety"
            ? DurableProviderSafety.ProviderKeyed
            : DurableProviderSafety.ReconcileBeforeRetry;
        var registration = new OperatorRegistration(
            $"operator.reconcile-start.{scenario}",
            safety,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.NotApplied, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId($"operator-reconcile-start-{scenario}");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, $"reconcile-start-{scenario}");
        var workId = accepted.WorkId;
        var revision = accepted.Revision;

        if (scenario == "missing")
        {
            workId = new DurableWorkId("operator-reconcile-start-missing-work");
        }
        else if (scenario == "stale")
        {
            revision++;
        }
        else
        {
            var state = scenario switch
            {
                "wrong-state" => "suspended_manual_resolution",
                "terminal" => "succeeded",
                _ => "suspended_reconciliation_required",
            };
            revision = await ForceStateAsync(
                database.DataSource,
                scope,
                workId,
                state,
                scenario == "terminal" ? "operator-test-terminal" : "ambiguous_external_outcome");
            if (scenario == "no-exact-permit")
            {
                revision = await AdvanceFenceWithoutPermitAsync(database.DataSource, scope, workId, revision);
            }
        }

        var commandId = new DurableCommandId($"operator-reconcile-start-command-{scenario}");
        var result = await operators.ReconcileAsync(new DurableWorkReconcileRequest(
            scope,
            workId,
            commandId,
            "operator-test",
            "invalid-starting-truth",
            revision));

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedProblemCode, result.Problem!.Code);
        Assert.Equal(0, registration.ReconciliationCount);
        Assert.Equal(0, await CountOperatorCommandsAsync(database.DataSource, scope, commandId));
    }

    [Theory]
    [InlineData(
        DurableProviderSafety.ManualResolution,
        DurableManualResolutionKind.Applied,
        DurableWorkState.SucceededAfterCancelRequested,
        "succeeded_after_cancel_requested")]
    [InlineData(
        DurableProviderSafety.ManualResolution,
        DurableManualResolutionKind.ProvenNotApplied,
        DurableWorkState.CanceledBeforeEffect,
        "canceled_before_effect")]
    [InlineData(
        DurableProviderSafety.ProviderKeyed,
        DurableManualResolutionKind.Applied,
        DurableWorkState.SucceededAfterCancelRequested,
        "succeeded_after_cancel_requested")]
    [InlineData(
        DurableProviderSafety.Idempotent,
        DurableManualResolutionKind.ProvenNotApplied,
        DurableWorkState.CanceledBeforeEffect,
        "canceled_before_effect")]
    public async Task Resolve_CancellationAwareSafeTransitionsBecomeTerminal(
        DurableProviderSafety safety,
        DurableManualResolutionKind resolution,
        DurableWorkState expectedState,
        string expectedPersistedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            $"operator.cancel-aware-resolution.{safety}.{resolution}",
            safety,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId($"operator-cancel-aware-resolution-{safety}-{resolution}");
        var accepted = await AcceptAndPermitAsync(
            client,
            database.DataSource,
            epoch,
            registration,
            scope,
            $"cancel-aware-resolution-{safety}-{resolution}");
        var state = safety == DurableProviderSafety.ManualResolution
            ? "suspended_manual_resolution"
            : "suspended_ambiguous_external_outcome";
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            state,
            "ambiguous_external_outcome",
            cancellationRequested: true);

        var result = await operators.ResolveAsync(new DurableWorkManualResolutionRequest(
            scope,
            accepted.WorkId,
            new DurableCommandId($"operator-cancel-aware-resolution-command-{safety}-{resolution}"),
            "operator-test",
            "cancellation-aware-proof",
            revision,
            resolution,
            resolution == DurableManualResolutionKind.Applied ? Result("cancel-aware-resolution") : null));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedState, result.Value!.State);
        Assert.Equal(
            expectedPersistedState,
            await ReadScalarAsync<string>(database.DataSource, scope, accepted.WorkId, "state"));
        Assert.Equal(revision + 1, result.Value.Revision);
    }

    [Fact]
    public async Task RetrySafe_ContractUnavailableWithoutPermitReturnsWorkToReady()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            "operator.retry-safe-preparation",
            DurableProviderSafety.Idempotent,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId("operator-retry-safe-preparation");
        var accepted = (await client.EnqueueAsync(Request(scope, registration, "retry-safe-preparation"))).Value!;
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_contract_unavailable",
            "work_contract_unavailable",
            cancellationRequested: true);

        var result = await operators.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            scope,
            accepted.WorkId,
            new DurableCommandId("operator-retry-safe-preparation-command"),
            "operator-test",
            "registration-restored",
            revision));

        Assert.True(result.IsSuccess);
        Assert.Equal(DurableWorkState.Ready, result.Value!.State);
        Assert.False(await ReadCancellationRequestedAsync(database.DataSource, scope, accepted.WorkId));
    }

    [Theory]
    [InlineData(
        DurableEffectReconciliationKind.Applied,
        DurableWorkState.SucceededAfterCancelRequested,
        "succeeded_after_cancel_requested")]
    [InlineData(
        DurableEffectReconciliationKind.NotApplied,
        DurableWorkState.CanceledBeforeEffect,
        "canceled_before_effect")]
    public async Task Reconcile_CancellationObservedWithoutRevisionChangeAppliesProvenTerminalOutcome(
        DurableEffectReconciliationKind proofKind,
        DurableWorkState expectedState,
        string expectedPersistedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var providerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new OperatorRegistration(
            $"operator.reconcile-cancellation-observed.{proofKind}",
            DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(
                proofKind,
                proofKind == DurableEffectReconciliationKind.Applied ? Result("cancellation-observed") : null),
            providerEntered,
            releaseProvider);
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId($"operator-reconcile-cancellation-observed-{proofKind}");
        var accepted = await AcceptAndPermitAsync(
            client,
            database.DataSource,
            epoch,
            registration,
            scope,
            $"reconcile-cancellation-observed-{proofKind}");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        var commandId = new DurableCommandId($"operator-reconcile-cancellation-observed-command-{proofKind}");
        var request = new DurableWorkReconcileRequest(
            scope,
            accepted.WorkId,
            commandId,
            "operator-test",
            "provider-proof",
            revision);

        var reconciliation = operators.ReconcileAsync(request).AsTask();
        await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await SetCancellationRequestedWithoutRevisionAsync(
                database.DataSource, scope, accepted.WorkId);
        }
        finally
        {
            releaseProvider.TrySetResult(true);
        }

        var result = await reconciliation;
        var replay = await operators.ReconcileAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedState, result.Value!.State);
        Assert.Equal(revision + 1, result.Value.Revision);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, replay.Value!.Outcome);
        Assert.Equal(expectedState, replay.Value.State);
        Assert.Equal(
            expectedPersistedState,
            await ReadScalarAsync<string>(database.DataSource, scope, accepted.WorkId, "state"));
        Assert.Equal(1, registration.ReconciliationCount);
    }

    [Fact]
    public async Task Reconcile_PreExistingExactStartedCommandResumesAndCompletes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            "operator.reconcile-resume-started",
            DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.NotApplied, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId("operator-reconcile-resume-started");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, "reconcile-resume-started");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        var request = new DurableWorkReconcileRequest(
            scope,
            accepted.WorkId,
            new DurableCommandId("operator-reconcile-resume-started-command"),
            "operator-test",
            "resume-started",
            revision);
        await SeedOperatorCommandAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            request.CommandId,
            "reconcile",
            request.ActorId,
            request.ReasonCode,
            request.Fingerprint,
            false,
            "suspended_reconciliation_required",
            revision);

        var result = await operators.ReconcileAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(DurableWorkState.Ready, result.Value!.State);
        Assert.Equal(1, registration.ReconciliationCount);
        Assert.Equal(
            "completed",
            await ReadOperatorScalarAsync<string>(database.DataSource, scope, request.CommandId, "status"));
    }

    [Theory]
    [InlineData(false, DurableProblemCodes.CommandConflict)]
    [InlineData(true, null)]
    public async Task Reconcile_PreExistingCommandReturnsConflictOrCompletedReplay(
        bool completed,
        string? expectedProblemCode)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var scenario = completed ? "completed" : "conflict";
        var registration = new OperatorRegistration(
            $"operator.reconcile-preexisting-{scenario}",
            DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.NotApplied, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId($"operator-reconcile-preexisting-{scenario}");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, $"reconcile-preexisting-{scenario}");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        var commandId = new DurableCommandId($"operator-reconcile-preexisting-command-{scenario}");
        var request = new DurableWorkReconcileRequest(
            scope,
            accepted.WorkId,
            commandId,
            "operator-test",
            "authoritative-request",
            revision);
        var persistedFingerprint = completed
            ? request.Fingerprint
            : new DurableWorkReconcileRequest(
                scope,
                accepted.WorkId,
                commandId,
                "operator-test",
                "different-semantic-input",
                revision).Fingerprint;
        await SeedOperatorCommandAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            commandId,
            "reconcile",
            request.ActorId,
            completed ? request.ReasonCode : "different-semantic-input",
            persistedFingerprint,
            completed,
            "suspended_reconciliation_required",
            revision);

        var result = await operators.ReconcileAsync(request);

        Assert.Equal(0, registration.ReconciliationCount);
        if (completed)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(DurableWorkOperatorOutcome.Duplicate, result.Value!.Outcome);
            Assert.Equal(DurableWorkState.Suspended, result.Value.State);
        }
        else
        {
            Assert.False(result.IsSuccess);
            Assert.Equal(expectedProblemCode, result.Problem!.Code);
        }

        Assert.Equal(revision, await ReadRevisionAsync(database.DataSource, scope, accepted.WorkId));
    }

    [Theory]
    [InlineData("delete", DurableProblemCodes.OperatorTransitionRejected)]
    [InlineData("replace", DurableProblemCodes.CommandConflict)]
    public async Task Reconcile_CommandAuthorityChangedDuringProviderCallRejectsProof(
        string mutation,
        string expectedProblemCode)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var providerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new OperatorRegistration(
            $"operator.reconcile-command-race.{mutation}",
            DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Applied, Result("command-race")),
            providerEntered,
            releaseProvider);
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId($"operator-reconcile-command-race-{mutation}");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, $"reconcile-command-race-{mutation}");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        var commandId = new DurableCommandId($"operator-reconcile-command-race-command-{mutation}");
        var request = new DurableWorkReconcileRequest(
            scope,
            accepted.WorkId,
            commandId,
            "operator-test",
            "provider-proof",
            revision);

        var reconciliation = operators.ReconcileAsync(request).AsTask();
        await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await MutateOperatorCommandAuthorityAsync(database.DataSource, scope, commandId, mutation);
        }
        finally
        {
            releaseProvider.TrySetResult(true);
        }

        var result = await reconciliation;

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedProblemCode, result.Problem!.Code);
        Assert.Equal(DurableWorkState.Suspended, await ReadStateAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(revision, await ReadRevisionAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(1, registration.ReconciliationCount);
        Assert.Equal(
            mutation == "delete" ? 0 : 1,
            await CountOperatorCommandsAsync(database.DataSource, scope, commandId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrySafe_PreExistingExactCommandReturnsInProgressOrDuplicate(bool completed)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var scenario = completed ? "completed" : "started";
        var registration = new OperatorRegistration(
            $"operator.retry-safe-preexisting.{scenario}",
            DurableProviderSafety.Idempotent,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId($"operator-retry-safe-preexisting-{scenario}");
        var accepted = (await client.EnqueueAsync(Request(scope, registration, $"retry-safe-preexisting-{scenario}"))).Value!;
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_contract_unavailable",
            "work_contract_unavailable");
        var request = new DurableWorkRetrySafeRequest(
            scope,
            accepted.WorkId,
            new DurableCommandId($"operator-retry-safe-preexisting-command-{scenario}"),
            "operator-test",
            "registration-restored",
            revision);
        await SeedOperatorCommandAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            request.CommandId,
            "retry_safe",
            request.ActorId,
            request.ReasonCode,
            request.Fingerprint,
            completed,
            "retry_wait",
            revision + 1);

        var result = await operators.RetrySafeAsync(request);

        if (completed)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(DurableWorkOperatorOutcome.Duplicate, result.Value!.Outcome);
            Assert.Equal(DurableWorkState.Ready, result.Value.State);
            Assert.Equal(revision + 1, result.Value.Revision);
        }
        else
        {
            Assert.False(result.IsSuccess);
            Assert.Equal(DurableProblemCodes.OperatorCommandInProgress, result.Problem!.Code);
        }

        Assert.Equal(
            "suspended_contract_unavailable",
            await ReadScalarAsync<string>(database.DataSource, scope, accepted.WorkId, "state"));
        Assert.Equal(revision, await ReadRevisionAsync(database.DataSource, scope, accepted.WorkId));
    }

    [Fact]
    public async Task RetrySafe_CompletedReplayProjectsEveryPersistedWorkStateAndRejectsUnknown()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            "operator.retry-safe-replay-state",
            DurableProviderSafety.Idempotent,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var cases = new[]
        {
            (Persisted: "retry_wait", Projected: DurableWorkState.Ready),
            (Persisted: "leased", Projected: DurableWorkState.Claimed),
            (Persisted: "cancel_pending", Projected: DurableWorkState.CancelPending),
            (Persisted: "succeeded", Projected: DurableWorkState.Succeeded),
            (Persisted: "succeeded_after_cancel_requested", Projected: DurableWorkState.SucceededAfterCancelRequested),
            (Persisted: "failed", Projected: DurableWorkState.FailedTerminal),
            (Persisted: "canceled_before_effect", Projected: DurableWorkState.CanceledBeforeEffect),
            (Persisted: "reconciling", Projected: DurableWorkState.Suspended),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var scope = new DurableScopeId($"operator-retry-safe-replay-state-{index}");
            var accepted = (await client.EnqueueAsync(Request(scope, registration, $"retry-safe-replay-state-{index}"))).Value!;
            var request = new DurableWorkRetrySafeRequest(
                scope,
                accepted.WorkId,
                new DurableCommandId($"operator-retry-safe-replay-state-command-{index}"),
                "operator-test",
                "completed-replay",
                accepted.Revision);
            await SeedOperatorCommandAsync(
                database.DataSource,
                scope,
                accepted.WorkId,
                request.CommandId,
                "retry_safe",
                request.ActorId,
                request.ReasonCode,
                request.Fingerprint,
                true,
                cases[index].Persisted,
                accepted.Revision);

            var replay = await operators.RetrySafeAsync(request);

            Assert.True(replay.IsSuccess);
            Assert.Equal(DurableWorkOperatorOutcome.Duplicate, replay.Value!.Outcome);
            Assert.Equal(cases[index].Projected, replay.Value.State);
        }

        var corruptScope = new DurableScopeId("operator-retry-safe-replay-state-corrupt");
        var corruptAccepted = (await client.EnqueueAsync(
            Request(corruptScope, registration, "retry-safe-replay-state-corrupt"))).Value!;
        var corruptRequest = new DurableWorkRetrySafeRequest(
            corruptScope,
            corruptAccepted.WorkId,
            new DurableCommandId("operator-retry-safe-replay-state-command-corrupt"),
            "operator-test",
            "completed-replay",
            corruptAccepted.Revision);
        await SeedOperatorCommandAsync(
            database.DataSource,
            corruptScope,
            corruptAccepted.WorkId,
            corruptRequest.CommandId,
            "retry_safe",
            corruptRequest.ActorId,
            corruptRequest.ReasonCode,
            corruptRequest.Fingerprint,
            true,
            "corrupt",
            corruptAccepted.Revision);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await operators.RetrySafeAsync(corruptRequest));

        Assert.Contains("Unknown persisted durable Work state", exception.Message, StringComparison.Ordinal);
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
        var prematureRelease = await operators.ReleaseAfterRecoveryAsync(new DurableWorkRecoveryReleaseRequest(
            recoveryScope,
            recoveryAccepted.WorkId,
            new DurableCommandId("operator-premature-recovery-command"),
            "operator-test",
            "premature-release",
            recoveryAccepted.Revision));
        Assert.False(prematureRelease.IsSuccess);
        Assert.Equal(DurableProblemCodes.OperatorTransitionRejected, prematureRelease.Problem!.Code);
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
    public async Task Reconcile_UnsupportedPayloadClassificationFailsBeforeProviderAndRollsBackStartedCommand()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            "operator.reconcile-unsupported-classification",
            DurableProviderSafety.ReconcileBeforeRetry,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.NotApplied, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var scope = new DurableScopeId("operator-reconcile-unsupported-classification");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, "reconcile-unsupported-classification");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        await UpdatePayloadClassificationAsync(database.DataSource, scope, accepted.WorkId, "unsupported");
        var commandId = new DurableCommandId("operator-reconcile-unsupported-classification-command");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new PostgreSqlDurableWorkOperatorClient(
                database.DataSource, registry, NullServices.Instance, epoch)
                .ReconcileAsync(new DurableWorkReconcileRequest(
                    scope,
                    accepted.WorkId,
                    commandId,
                    "operator-test",
                    "provider-proof",
                    revision)));

        Assert.Contains("Unknown persisted classification", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, registration.ReconciliationCount);
        Assert.Equal(0, await CountOperatorCommandsAsync(database.DataSource, scope, commandId));
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

        var mismatchScope = new DurableScopeId("operator-typed-safety-mismatch");
        var mismatchAccepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, reconcile, mismatchScope, "typed-safety-mismatch");
        var mismatchRevision = await ForceStateAsync(
            database.DataSource,
            mismatchScope,
            mismatchAccepted.WorkId,
            "suspended_reconciliation_required",
            "ambiguous_external_outcome");
        var mismatchedRegistration = new OperatorRegistration(
            reconcile.WorkName,
            DurableProviderSafety.ProviderKeyed,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var mismatched = await new PostgreSqlDurableWorkOperatorClient(
            database.DataSource,
            new DurableWorkRegistry([mismatchedRegistration]),
            NullServices.Instance,
            epoch).ReconcileAsync(new DurableWorkReconcileRequest(
                mismatchScope,
                mismatchAccepted.WorkId,
                new DurableCommandId("operator-typed-safety-mismatch-command"),
                "operator-test",
                "provider-proof",
                mismatchRevision));
        Assert.False(mismatched.IsSuccess);
        Assert.Equal(DurableProblemCodes.OperatorTransitionRejected, mismatched.Problem!.Code);
        Assert.Equal(0, mismatchedRegistration.ReconciliationCount);

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

    [Theory]
    [InlineData("missing", DurableProblemCodes.WorkNotFound)]
    [InlineData("stale", DurableProblemCodes.WorkRevisionConflict)]
    [InlineData("succeeded", DurableProblemCodes.AlreadyTerminal)]
    [InlineData("succeeded_after_cancel_requested", DurableProblemCodes.AlreadyTerminal)]
    [InlineData("failed", DurableProblemCodes.AlreadyTerminal)]
    [InlineData("canceled_before_effect", DurableProblemCodes.AlreadyTerminal)]
    public async Task RetrySafe_ReturnsTypedSnapshotFailuresWithoutRecordingACommand(
        string scenario,
        string expectedProblemCode)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            $"operator.snapshot-failure.{scenario}",
            DurableProviderSafety.ProviderKeyed,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, registry, NullServices.Instance, epoch);
        var scope = new DurableScopeId($"operator-snapshot-failure-{scenario}");
        var accepted = (await client.EnqueueAsync(Request(scope, registration, $"snapshot-failure-{scenario}"))).Value!;
        var commandId = new DurableCommandId($"operator-snapshot-failure-command-{scenario}");
        var workId = accepted.WorkId;
        var expectedRevision = accepted.Revision;

        if (scenario == "missing")
        {
            workId = new DurableWorkId("operator-snapshot-failure-missing-work");
        }
        else if (scenario == "stale")
        {
            expectedRevision++;
        }
        else
        {
            expectedRevision = await ForceStateAsync(
                database.DataSource,
                scope,
                workId,
                scenario,
                $"operator-test-{scenario}");
        }

        var result = await operators.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            scope,
            workId,
            commandId,
            "operator-test",
            "snapshot-validation",
            expectedRevision));

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedProblemCode, result.Problem!.Code);
        Assert.Equal(
            0,
            await CountOperatorCommandsAsync(database.DataSource, scope, commandId));
    }

    [Fact]
    public async Task Resolve_WithResultAndUnavailableRegistration_ReturnsTypedFailureWithoutMutation()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var epoch = await InitializeAsync(database.DataSource);
        var registration = new OperatorRegistration(
            "operator.direct-result-registration",
            DurableProviderSafety.ManualResolution,
            new DurableEncodedEffectReconciliation(DurableEffectReconciliationKind.Unknown, null));
        var registry = new DurableWorkRegistry([registration]);
        var client = new PostgreSqlDurableWorkClient(
            database.DataSource, registry, await OptionsAsync(database.DataSource, epoch));
        var scope = new DurableScopeId("operator-direct-result-registration");
        var accepted = await AcceptAndPermitAsync(
            client, database.DataSource, epoch, registration, scope, "direct-result-registration");
        var revision = await ForceStateAsync(
            database.DataSource,
            scope,
            accepted.WorkId,
            "suspended_manual_resolution",
            "ambiguous_external_outcome");
        var commandId = new DurableCommandId("operator-direct-result-registration-command");
        var operators = new PostgreSqlDurableWorkOperatorClient(
            database.DataSource, new DurableWorkRegistry([]), NullServices.Instance, epoch);

        var result = await operators.ResolveAsync(new DurableWorkManualResolutionRequest(
            scope,
            accepted.WorkId,
            commandId,
            "operator-test",
            "provider-applied",
            revision,
            DurableManualResolutionKind.Applied,
            Result("direct-result")));

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.WorkContractUnavailable, result.Problem!.Code);
        Assert.Equal(revision, await ReadRevisionAsync(database.DataSource, scope, accepted.WorkId));
        Assert.Equal(
            0,
            await CountOperatorCommandsAsync(database.DataSource, scope, commandId));
    }

    [Theory]
    [InlineData("dataSource", "dataSource", typeof(ArgumentNullException))]
    [InlineData("registry", "registry", typeof(ArgumentNullException))]
    [InlineData("services", "services", typeof(ArgumentNullException))]
    [InlineData("runtimeEpoch", "runtimeEpoch", typeof(ArgumentException))]
    public void Constructor_RejectsInvalidDependenciesAndEpoch(
        string invalidArgument,
        string expectedParameter,
        Type expectedException)
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Username=operator-guard;Password=operator-guard;Database=operator-guard");
        var registry = new DurableWorkRegistry([]);
        var epoch = Guid.NewGuid();

        var exception = Record.Exception(() => _ = invalidArgument switch
        {
            "dataSource" => new PostgreSqlDurableWorkOperatorClient(
                null!, registry, NullServices.Instance, epoch),
            "registry" => new PostgreSqlDurableWorkOperatorClient(
                dataSource, null!, NullServices.Instance, epoch),
            "services" => new PostgreSqlDurableWorkOperatorClient(
                dataSource, registry, null!, epoch),
            "runtimeEpoch" => new PostgreSqlDurableWorkOperatorClient(
                dataSource, registry, NullServices.Instance, Guid.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidArgument)),
        });

        Assert.IsType(expectedException, exception);
        Assert.Equal(expectedParameter, Assert.IsAssignableFrom<ArgumentException>(exception).ParamName);
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

    private static async ValueTask<long> CountOperatorCommandsAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableCommandId commandId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM appsurface_durable.work_operator_command WHERE scope_id = @scope_id AND command_id = @command_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask SeedOperatorCommandAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work,
        DurableCommandId commandId,
        string commandType,
        string actorId,
        string reasonCode,
        DurableCommandFingerprint fingerprint,
        bool completed,
        string resultingState,
        long resultingRevision)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO appsurface_durable.work_operator_command
                (scope_id, work_id, command_id, command_type, actor_id, reason_code,
                 request_schema, request_sha256, status, resulting_state, resulting_revision, completed_at)
            VALUES
                (@scope_id, @work_id, @command_id, @command_type, @actor_id, @reason_code,
                 @request_schema, @request_sha256,
                 CASE WHEN @completed THEN 'completed' ELSE 'started' END,
                 CASE WHEN @completed THEN @resulting_state ELSE NULL END,
                 CASE WHEN @completed THEN @resulting_revision ELSE NULL END,
                 CASE WHEN @completed THEN clock_timestamp() ELSE NULL END);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        command.Parameters.AddWithValue("command_type", commandType);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("reason_code", reasonCode);
        command.Parameters.AddWithValue("request_schema", fingerprint.SchemaId);
        command.Parameters.AddWithValue("request_sha256", Convert.FromHexString(fingerprint.Sha256));
        command.Parameters.AddWithValue("completed", completed);
        command.Parameters.AddWithValue("resulting_state", resultingState);
        command.Parameters.AddWithValue("resulting_revision", resultingRevision);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask MutateOperatorCommandAuthorityAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableCommandId commandId,
        string mutation)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        var sql = mutation switch
        {
            "delete" =>
                "DELETE FROM appsurface_durable.work_operator_command WHERE scope_id = @scope_id AND command_id = @command_id;",
            "replace" =>
                "UPDATE appsurface_durable.work_operator_command SET request_sha256 = @replacement_sha256 WHERE scope_id = @scope_id AND command_id = @command_id;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        if (mutation == "replace")
        {
            command.Parameters.AddWithValue("replacement_sha256", new byte[32]);
        }

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
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

    private static async ValueTask UpdatePayloadClassificationAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work,
        string classification)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            "UPDATE appsurface_durable.work SET payload_classification = @classification WHERE scope_id = @scope_id AND work_id = @work_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("classification", classification);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", work.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async ValueTask SetCancellationRequestedWithoutRevisionAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId work)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand(
            "UPDATE appsurface_durable.work SET cancellation_requested_at = clock_timestamp() WHERE scope_id = @scope_id AND work_id = @work_id;",
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
        DurableEncodedEffectReconciliation proof,
        TaskCompletionSource<bool>? providerEntered = null,
        TaskCompletionSource<bool>? releaseProvider = null) : DurableWorkRegistration(
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

        public override async ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default)
        {
            ReconciliationCount++;
            providerEntered?.TrySetResult(true);
            if (releaseProvider is not null)
            {
                await releaseProvider.Task.WaitAsync(cancellationToken);
            }

            return proof;
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
