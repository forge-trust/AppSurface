using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableWorkOperatorClientTests
{
    [Fact]
    public async Task ManualResolution_AppliedCommitsExactResult_AndDeduplicatesCommand()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var services = new ServiceCollection();
        services.AddDurableWork<OperatorWork, OperatorResult, ThrowingOperatorExecutor>(
            "tests.operator.manual",
            "v1",
            DurableProviderSafety.ManualResolution,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("operator-manual-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-manual-accept"),
            "operator-manual-idempotency",
            "tests.operator.manual",
            "v1",
            workCodec.Encode(new OperatorWork("safe-manual")),
            DurableProviderSafety.ManualResolution));
        await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var suspended = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.Equal(DurableWorkState.Suspended, suspended.Value!.State);

        var request = new DurableWorkManualResolutionRequest(
            scope,
            accepted.Value.WorkId,
            new DurableCommandId("operator-manual-resolve"),
            "operator-test",
            "provider-proof",
            suspended.Value.Revision,
            DurableManualResolutionKind.Applied,
            resultCodec.Encode(new OperatorResult("resolved")));
        var operatorClient = provider.GetRequiredService<IDurableWorkOperatorClient>();
        var resolved = await operatorClient.ResolveAsync(request);
        var duplicate = await operatorClient.ResolveAsync(request);
        var terminal = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value.WorkId));

        Assert.True(resolved.IsSuccess);
        Assert.Equal(DurableWorkOperatorOutcome.Applied, resolved.Value!.Outcome);
        Assert.Equal(DurableWorkState.Succeeded, resolved.Value.State);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(resolved.Value.Revision, duplicate.Value.Revision);
        Assert.Equal(new OperatorResult("resolved"), resultCodec.Decode(terminal.Value!.Result!));
    }

    [Fact]
    public async Task Reconcile_AppliedRunsSideEffectFreeReaderOnce_AndDeduplicatesCommand()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var capture = new ReconcilerCapture();
        var services = new ServiceCollection();
        services.AddSingleton(capture);
        services.AddDurableWorkWithReconciler<
            OperatorWork,
            OperatorResult,
            ThrowingOperatorExecutor,
            AppliedOperatorReconciler>(
            "tests.operator.reconcile",
            "v1",
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("operator-reconcile-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-reconcile-accept"),
            "operator-reconcile-idempotency",
            "tests.operator.reconcile",
            "v1",
            workCodec.Encode(new OperatorWork("safe-reconcile")),
            DurableProviderSafety.ReconcileBeforeRetry));
        await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var suspended = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        var request = new DurableWorkReconcileRequest(
            scope,
            accepted.Value.WorkId,
            new DurableCommandId("operator-reconcile-command"),
            "operator-test",
            "provider-read",
            suspended.Value!.Revision);
        var operatorClient = provider.GetRequiredService<IDurableWorkOperatorClient>();

        var reconciled = await operatorClient.ReconcileAsync(request);
        var duplicate = await operatorClient.ReconcileAsync(request);

        Assert.True(reconciled.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, reconciled.Value!.State);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(1, capture.Calls);
        Assert.Equal("asdur-v1-", capture.ProviderKeyPrefix);
    }

    [Theory]
    [InlineData(
        DurableEffectReconciliationKind.Applied,
        DurableWorkState.SucceededAfterCancelRequested,
        "known_succeeded")]
    [InlineData(
        DurableEffectReconciliationKind.NotApplied,
        DurableWorkState.CanceledBeforeEffect,
        "proven_no_effect")]
    public async Task Reconcile_AfterScopeDisable_CommitsProviderTruthWithoutReenablingExecution(
        DurableEffectReconciliationKind reconciliationKind,
        DurableWorkState expectedState,
        string expectedPermitStatus)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var capture = new ReconcilerCapture { ResultKind = reconciliationKind };
        var services = new ServiceCollection();
        services.AddSingleton(capture);
        services.AddDurableWorkWithReconciler<
            OperatorWork,
            OperatorResult,
            ThrowingOperatorExecutor,
            AppliedOperatorReconciler>(
            "tests.operator.disabled-reconcile",
            "v1",
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId($"operator-disabled-reconcile-{reconciliationKind}");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-disabled-reconcile-accept"),
            $"operator-disabled-reconcile-{reconciliationKind}",
            "tests.operator.disabled-reconcile",
            "v1",
            workCodec.Encode(new OperatorWork("safe-disabled-reconcile")),
            DurableProviderSafety.ReconcileBeforeRetry));
        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        var workControl = provider.GetRequiredService<IDurableWorkControlClient>();
        var suspended = await workControl.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        var scopeControl = provider.GetRequiredService<IDurableScopeControlClient>();
        var disabled = await scopeControl.DisableAsync(new DurableScopeDisableRequest(
            scope,
            "operator-test",
            "account_closed",
            expectedGeneration: 1));
        var projected = await workControl.GetAsync(new DurableWorkGetRequest(scope, accepted.Value.WorkId));

        var reconciled = await provider.GetRequiredService<IDurableWorkOperatorClient>().ReconcileAsync(
            new DurableWorkReconcileRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId("operator-disabled-reconcile-command"),
                "operator-test",
                "provider-read-after-disable",
                projected.Value!.Revision));
        var stillDisabled = await scopeControl.DisableAsync(new DurableScopeDisableRequest(
            scope,
            "operator-test",
            "duplicate-disable",
            expectedGeneration: disabled.Value!.Generation));
        var emptyPass = await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));

        Assert.True(reconciled.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, suspended.Value!.State);
        Assert.Equal(DurableWorkState.Suspended, projected.Value.State);
        Assert.Equal(expectedState, reconciled.Value!.State);
        Assert.Equal(DurableScopeDisableOutcome.AlreadyDisabled, stillDisabled.Value!.Outcome);
        Assert.Equal(expectedPermitStatus, await ReadPermitStatusAsync(database.DataSource, scope, accepted.Value.WorkId));
        Assert.Equal(0, emptyPass.Discovered);
        Assert.Equal(1, capture.Calls);
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, "suspended_manual_resolution")]
    public async Task DisableScope_PostPermitProjectsResolvableProviderTruthAndHonorsNotAppliedProof(
        DurableProviderSafety providerSafety,
        string expectedSuspendedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var epoch = Guid.NewGuid();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var services = new ServiceCollection();
        if (providerSafety == DurableProviderSafety.ReconcileBeforeRetry)
        {
            services.AddSingleton(new ReconcilerCapture
            {
                ResultKind = DurableEffectReconciliationKind.NotApplied,
            });
            services.AddDurableWorkWithReconciler<
                OperatorWork,
                OperatorResult,
                ThrowingOperatorExecutor,
                AppliedOperatorReconciler>(
                "tests.operator.disable-provider",
                "v1",
                workCodec,
                resultCodec);
        }
        else
        {
            services.AddDurableWork<OperatorWork, OperatorResult, ThrowingOperatorExecutor>(
                "tests.operator.disable-provider",
                "v1",
                providerSafety,
                workCodec,
                resultCodec);
        }

        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            epoch,
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId($"operator-disable-provider-{providerSafety}");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-disable-provider-accept"),
            $"operator-disable-provider-{providerSafety}",
            "tests.operator.disable-provider",
            "v1",
            workCodec.Encode(new OperatorWork("safe-disable-provider")),
            providerSafety));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "disable-provider-worker");
        var permit = await store.TryAcquireEffectPermitAsync(claim!);
        var disabled = await provider.GetRequiredService<IDurableScopeControlClient>().DisableAsync(
            new DurableScopeDisableRequest(
                scope,
                "operator-test",
                "account_closed",
                expectedGeneration: 1));
        var afterDisable = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value!.WorkId);
        var operatorClient = provider.GetRequiredService<IDurableWorkOperatorClient>();

        DurableOperationResult<DurableWorkOperatorResult> resolved;
        if (providerSafety == DurableProviderSafety.ReconcileBeforeRetry)
        {
            resolved = await operatorClient.ReconcileAsync(new DurableWorkReconcileRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId("operator-disable-provider-reconcile"),
                "operator-test",
                "provider-read-after-disable",
                afterDisable.Revision));
        }
        else
        {
            resolved = await operatorClient.ResolveAsync(new DurableWorkManualResolutionRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId("operator-disable-provider-resolve"),
                "operator-test",
                "provider-proof-after-disable",
                afterDisable.Revision,
                DurableManualResolutionKind.ProvenNotApplied));
        }

        var afterProof = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value.WorkId);
        Assert.True(disabled.IsSuccess);
        Assert.Equal(expectedSuspendedState, afterDisable.State);
        Assert.True(afterDisable.CancellationRequested);
        Assert.Equal("suspended", afterDisable.DispatchState);
        Assert.Equal("granted", afterDisable.PermitStatus);
        Assert.Equal(permit!.Claim.Revision + 1, afterDisable.Revision);
        Assert.True(resolved.IsSuccess);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, resolved.Value!.State);
        Assert.Equal("canceled_before_effect", afterProof.State);
        Assert.True(afterProof.IsTerminal);
        Assert.Equal("terminal", afterProof.DispatchState);
        Assert.Equal("proven_no_effect", afterProof.PermitStatus);
    }

    [Theory]
    [InlineData(
        DurableEffectReconciliationKind.Applied,
        DurableWorkState.SucceededAfterCancelRequested,
        "known_succeeded")]
    [InlineData(
        DurableEffectReconciliationKind.NotApplied,
        DurableWorkState.CanceledBeforeEffect,
        "proven_no_effect")]
    public async Task Reconcile_AfterSuspendedCancellation_CommitsProofAndHonorsIntent(
        DurableEffectReconciliationKind reconciliationKind,
        DurableWorkState expectedState,
        string expectedPermitStatus)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var capture = new ReconcilerCapture { ResultKind = reconciliationKind };
        var services = new ServiceCollection();
        services.AddSingleton(capture);
        services.AddDurableWorkWithReconciler<
            OperatorWork,
            OperatorResult,
            ThrowingOperatorExecutor,
            AppliedOperatorReconciler>(
            "tests.operator.canceled-reconcile",
            "v1",
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId($"operator-canceled-reconcile-{reconciliationKind}");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-canceled-reconcile-accept"),
            $"operator-canceled-reconcile-{reconciliationKind}",
            "tests.operator.canceled-reconcile",
            "v1",
            workCodec.Encode(new OperatorWork("safe-canceled-reconcile")),
            DurableProviderSafety.ReconcileBeforeRetry));
        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var suspended = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        var cancellation = await control.CancelAsync(new DurableWorkCancelRequest(
            scope,
            accepted.Value.WorkId,
            "operator-test",
            "consumer_requested",
            suspended.Value!.Revision));

        var reconciled = await provider.GetRequiredService<IDurableWorkOperatorClient>().ReconcileAsync(
            new DurableWorkReconcileRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId("operator-canceled-reconcile-command"),
                "operator-test",
                "provider-read-after-cancel",
                cancellation.Value!.Revision));
        var emptyPass = await pump.RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));

        Assert.True(cancellation.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, cancellation.Value.State);
        Assert.True(reconciled.IsSuccess);
        Assert.Equal(expectedState, reconciled.Value!.State);
        Assert.True(await ReadCancellationRequestedAsync(database.DataSource, scope, accepted.Value.WorkId));
        Assert.Equal(expectedPermitStatus, await ReadPermitStatusAsync(database.DataSource, scope, accepted.Value.WorkId));
        Assert.Equal(0, emptyPass.Discovered);
    }

    [Fact]
    public async Task SafeRetry_RejectsAmbiguousManualPermit_ThenAcceptsProvenNoEffect()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var services = new ServiceCollection();
        services.AddDurableWork<OperatorWork, OperatorResult, ThrowingOperatorExecutor>(
            "tests.operator.safe-retry",
            "v1",
            DurableProviderSafety.ManualResolution,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("operator-safe-retry-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-safe-retry-accept"),
            "operator-safe-retry-idempotency",
            "tests.operator.safe-retry",
            "v1",
            workCodec.Encode(new OperatorWork("safe-retry")),
            DurableProviderSafety.ManualResolution));
        await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var suspended = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        var operatorClient = provider.GetRequiredService<IDurableWorkOperatorClient>();

        var rejected = await operatorClient.RetrySafeAsync(new DurableWorkRetrySafeRequest(
            scope,
            accepted.Value.WorkId,
            new DurableCommandId("operator-unsafe-retry"),
            "operator-test",
            "unsafe-without-proof",
            suspended.Value!.Revision));
        var proven = await operatorClient.ResolveAsync(new DurableWorkManualResolutionRequest(
            scope,
            accepted.Value.WorkId,
            new DurableCommandId("operator-proven-no-effect"),
            "operator-test",
            "provider-proof",
            suspended.Value.Revision,
            DurableManualResolutionKind.ProvenNotApplied));

        Assert.False(rejected.IsSuccess);
        Assert.Equal(DurableProblemCodes.OperatorProofRequired, rejected.Problem!.Code);
        Assert.True(proven.IsSuccess);
        Assert.Equal(DurableWorkState.Ready, proven.Value!.State);
    }

    [Theory]
    [InlineData(
        DurableProviderSafety.Idempotent,
        "retry",
        DurableWorkState.Ready,
        false,
        "granted")]
    [InlineData(
        DurableProviderSafety.ProviderKeyed,
        "retry",
        DurableWorkState.Ready,
        false,
        "granted")]
    [InlineData(
        DurableProviderSafety.Idempotent,
        "applied",
        DurableWorkState.SucceededAfterCancelRequested,
        true,
        "known_succeeded")]
    [InlineData(
        DurableProviderSafety.ProviderKeyed,
        "applied",
        DurableWorkState.SucceededAfterCancelRequested,
        true,
        "known_succeeded")]
    [InlineData(
        DurableProviderSafety.Idempotent,
        "not-applied",
        DurableWorkState.CanceledBeforeEffect,
        true,
        "proven_no_effect")]
    [InlineData(
        DurableProviderSafety.ProviderKeyed,
        "not-applied",
        DurableWorkState.CanceledBeforeEffect,
        true,
        "proven_no_effect")]
    public async Task CanceledReplaySafeAmbiguity_RequiresAuditedRetryOverrideOrAcceptsProof(
        DurableProviderSafety providerSafety,
        string action,
        DurableWorkState expectedState,
        bool expectedCancellationRequested,
        string expectedPermitStatus)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var epoch = Guid.NewGuid();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var services = new ServiceCollection();
        services.AddDurableWork<OperatorWork, OperatorResult, ThrowingOperatorExecutor>(
            "tests.operator.canceled-replay-safe",
            "v1",
            providerSafety,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            epoch,
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId($"operator-canceled-{providerSafety}-{action}");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-canceled-replay-accept"),
            $"operator-canceled-{providerSafety}-{action}",
            "tests.operator.canceled-replay-safe",
            "v1",
            workCodec.Encode(new OperatorWork("safe-canceled-replay")),
            providerSafety));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var claim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "operator-test-worker");
        var permit = await store.TryAcquireEffectPermitAsync(claim!);
        await store.RequestCancellationAsync(
            scope,
            accepted.Value!.WorkId,
            "operator-test",
            "consumer_requested",
            permit!.Claim.Revision);
        await store.RecordCompletionAsync(
            permit.Claim,
            new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Retry, "provider_timeout", "{}"));
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var suspended = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value.WorkId));
        var operatorClient = provider.GetRequiredService<IDurableWorkOperatorClient>();

        DurableOperationResult<DurableWorkOperatorResult> resolved;
        if (action == "retry")
        {
            resolved = await operatorClient.RetrySafeAsync(new DurableWorkRetrySafeRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId("operator-canceled-retry"),
                "operator-test",
                "override-cancellation-and-retry",
                suspended.Value!.Revision));
        }
        else
        {
            var resolution = action == "applied"
                ? DurableManualResolutionKind.Applied
                : DurableManualResolutionKind.ProvenNotApplied;
            resolved = await operatorClient.ResolveAsync(new DurableWorkManualResolutionRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId($"operator-canceled-resolve-{action}"),
                "operator-test",
                "provider-proof-after-cancel",
                suspended.Value!.Revision,
                resolution,
                resolution == DurableManualResolutionKind.Applied
                    ? resultCodec.Encode(new OperatorResult("resolved-after-cancel"))
                    : null));
        }

        Assert.True(resolved.IsSuccess);
        Assert.Equal(expectedState, resolved.Value!.State);
        Assert.Equal(
            expectedCancellationRequested,
            await ReadCancellationRequestedAsync(database.DataSource, scope, accepted.Value.WorkId));
        Assert.Equal(expectedPermitStatus, await ReadPermitStatusAsync(database.DataSource, scope, accepted.Value.WorkId));
        if (action == "retry")
        {
            var retryClaim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "retry-worker");
            Assert.False(retryClaim!.CancellationRequested);
        }
        else
        {
            Assert.Empty(await store.DiscoverAsync(1));
        }
    }

    [Fact]
    public async Task RecoveryRelease_DirectlyRefencesFutureDueWorkAndPreservesDueAndEffectEvidence()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var oldEpoch = Guid.NewGuid();
        var newEpoch = Guid.NewGuid();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var oldServices = new ServiceCollection();
        oldServices.AddDurableWork<OperatorWork, OperatorResult, ThrowingOperatorExecutor>(
            "tests.operator.future-recovery",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        oldServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            oldEpoch,
            options => options.SendWakeNotifications = false);
        await using var oldProvider = oldServices.BuildServiceProvider();
        var scope = new DurableScopeId("operator-future-recovery");
        var dueAt = DateTimeOffset.UtcNow.AddHours(2);
        var accepted = await oldProvider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-future-recovery-accept"),
            "operator-future-recovery-idempotency",
            "tests.operator.future-recovery",
            "v1",
            workCodec.Encode(new OperatorWork("safe-future-recovery")),
            DurableProviderSafety.ProviderKeyed,
            dueAtUtc: dueAt));
        var before = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value!.WorkId);
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
            oldEpoch,
            newEpoch,
            "operator-test",
            "restore-complete");

        var newServices = new ServiceCollection();
        newServices.AddDurableWork<OperatorWork, OperatorResult, ThrowingOperatorExecutor>(
            "tests.operator.future-recovery",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        newServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            newEpoch,
            options => options.SendWakeNotifications = false);
        await using var newProvider = newServices.BuildServiceProvider();
        var request = new DurableWorkRecoveryReleaseRequest(
            scope,
            accepted.Value.WorkId,
            new DurableCommandId("operator-future-recovery-release"),
            "operator-test",
            "restore-verified",
            before.Revision);
        var operatorClient = newProvider.GetRequiredService<IDurableWorkOperatorClient>();

        var released = await operatorClient.ReleaseAfterRecoveryAsync(request);
        var duplicate = await operatorClient.ReleaseAfterRecoveryAsync(request);
        var after = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value.WorkId);

        Assert.True(released.IsSuccess);
        Assert.Equal(DurableWorkState.Ready, released.Value!.State);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal("retry_wait", after.State);
        Assert.Equal("available", after.DispatchState);
        Assert.Equal(before.WorkDueAtUtc, after.WorkDueAtUtc);
        Assert.Equal(before.DispatchDueAtUtc, after.DispatchDueAtUtc);
        Assert.Equal(newEpoch, after.RuntimeEpoch);
        Assert.Equal(0, after.PermitCount);
        Assert.Null(after.PermitStatus);
        Assert.False(after.IsTerminal);
        Assert.Equal(before.Revision + 1, after.Revision);
    }

    [Theory]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, "suspended_manual_resolution")]
    public async Task RecoveryRelease_PreservesAmbiguousAttemptAndFailClosedProviderState(
        DurableProviderSafety providerSafety,
        string expectedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var oldEpoch = Guid.NewGuid();
        var newEpoch = Guid.NewGuid();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var oldServices = new ServiceCollection();
        AddAmbiguousRecoveryRegistration(oldServices, providerSafety, workCodec, resultCodec);
        oldServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            oldEpoch,
            options => options.SendWakeNotifications = false);
        await using var oldProvider = oldServices.BuildServiceProvider();
        var scope = new DurableScopeId($"operator-ambiguous-recovery-{providerSafety}");
        var accepted = await oldProvider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-ambiguous-recovery-accept"),
            $"operator-ambiguous-recovery-{providerSafety}",
            "tests.operator.ambiguous-recovery",
            "v1",
            workCodec.Encode(new OperatorWork("safe-ambiguous-recovery")),
            providerSafety));
        await oldProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        var before = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value!.WorkId);
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
            oldEpoch,
            newEpoch,
            "operator-test",
            "restore-complete");

        var newServices = new ServiceCollection();
        AddAmbiguousRecoveryRegistration(newServices, providerSafety, workCodec, resultCodec);
        newServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            newEpoch,
            options => options.SendWakeNotifications = false);
        await using var newProvider = newServices.BuildServiceProvider();

        var released = await newProvider.GetRequiredService<IDurableWorkOperatorClient>().ReleaseAfterRecoveryAsync(
            new DurableWorkRecoveryReleaseRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId("operator-ambiguous-recovery-release"),
                "operator-test",
                "restore-verified",
                before.Revision));
        var after = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value.WorkId);

        Assert.True(released.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, released.Value!.State);
        Assert.Equal(expectedState, after.State);
        Assert.Equal("suspended", after.DispatchState);
        Assert.Equal(before.WorkDueAtUtc, after.WorkDueAtUtc);
        Assert.Equal(before.DispatchDueAtUtc, after.DispatchDueAtUtc);
        Assert.Equal(newEpoch, after.RuntimeEpoch);
        Assert.Equal(1, after.PermitCount);
        Assert.Equal("granted", after.PermitStatus);
        Assert.False(after.IsTerminal);
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ProviderKeyed, "suspended_ambiguous_external_outcome")]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, "suspended_reconciliation_required")]
    [InlineData(DurableProviderSafety.ManualResolution, "suspended_manual_resolution")]
    public async Task RecoveryRelease_OldEpochCancelPendingRemainsSuspendedAndNeverInvokesExecutor(
        DurableProviderSafety providerSafety,
        string expectedState)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var oldEpoch = Guid.NewGuid();
        var newEpoch = Guid.NewGuid();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var capture = new ExecutorCallCapture();
        var oldServices = new ServiceCollection();
        AddCancelPendingRecoveryRegistration(
            oldServices,
            providerSafety,
            workCodec,
            resultCodec,
            capture);
        oldServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            oldEpoch,
            options => options.SendWakeNotifications = false);
        await using var oldProvider = oldServices.BuildServiceProvider();
        var scope = new DurableScopeId($"operator-cancel-pending-recovery-{providerSafety}");
        var accepted = await oldProvider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-cancel-pending-recovery-accept"),
            $"operator-cancel-pending-recovery-{providerSafety}",
            "tests.operator.cancel-pending-recovery",
            "v1",
            workCodec.Encode(new OperatorWork("safe-cancel-pending-recovery")),
            providerSafety));
        var oldStore = new PostgreSqlDurableWorkStore(database.DataSource, oldEpoch);
        var claim = await oldStore.TryClaimAsync(Assert.Single(await oldStore.DiscoverAsync(1)), "old-worker");
        var permit = await oldStore.TryAcquireEffectPermitAsync(claim!);
        var cancellation = await oldStore.RequestCancellationAsync(
            scope,
            accepted.Value!.WorkId,
            "operator-test",
            "consumer_requested",
            permit!.Claim.Revision);
        var before = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value.WorkId);
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).RotateRuntimeEpochAsync(
            oldEpoch,
            newEpoch,
            "operator-test",
            "restore-complete");

        var newServices = new ServiceCollection();
        AddCancelPendingRecoveryRegistration(
            newServices,
            providerSafety,
            workCodec,
            resultCodec,
            capture);
        newServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            newEpoch,
            options => options.SendWakeNotifications = false);
        await using var newProvider = newServices.BuildServiceProvider();

        var released = await newProvider.GetRequiredService<IDurableWorkOperatorClient>().ReleaseAfterRecoveryAsync(
            new DurableWorkRecoveryReleaseRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId("operator-cancel-pending-recovery-release"),
                "operator-test",
                "restore-verified",
                cancellation.Revision));
        var after = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value.WorkId);
        var emptyPass = await newProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));

        Assert.True(released.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, released.Value!.State);
        Assert.Equal(expectedState, after.State);
        Assert.Equal("suspended", after.DispatchState);
        Assert.True(after.CancellationRequested);
        Assert.Equal(before.WorkDueAtUtc, after.WorkDueAtUtc);
        Assert.Equal(before.DispatchDueAtUtc, after.DispatchDueAtUtc);
        Assert.Equal(newEpoch, after.RuntimeEpoch);
        Assert.Equal(1, after.PermitCount);
        Assert.Equal("granted", after.PermitStatus);
        Assert.Equal(0, emptyPass.Discovered);
        Assert.Equal(0, capture.Calls);
    }

    [Theory]
    [InlineData(
        DurableManualResolutionKind.Applied,
        DurableWorkState.SucceededAfterCancelRequested,
        "known_succeeded")]
    [InlineData(
        DurableManualResolutionKind.ProvenNotApplied,
        DurableWorkState.CanceledBeforeEffect,
        "proven_no_effect")]
    public async Task HistoricalAttemptAmbiguity_PermitFallbackSuspendsAndProofClosesPriorPermit(
        DurableManualResolutionKind resolution,
        DurableWorkState expectedState,
        string expectedPermitStatus)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var epoch = Guid.NewGuid();
        var workCodec = CreateWorkCodec();
        var resultCodec = CreateResultCodec();
        var services = new ServiceCollection();
        services.AddDurableWork<OperatorWork, OperatorResult, ThrowingOperatorExecutor>(
            "tests.operator.historical-proof",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            epoch,
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId($"operator-historical-proof-{resolution}");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("operator-historical-proof-accept"),
            $"operator-historical-proof-{resolution}",
            "tests.operator.historical-proof",
            "v1",
            workCodec.Encode(new OperatorWork("safe-historical-proof")),
            DurableProviderSafety.ProviderKeyed));
        var store = new PostgreSqlDurableWorkStore(database.DataSource, epoch);
        var firstClaim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "first-worker");
        _ = await store.TryAcquireEffectPermitAsync(firstClaim!);
        await ForceRetryWaitAsync(database.DataSource, scope, accepted.Value!.WorkId);
        var retryClaim = await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(1)), "retry-worker");
        await ForceCancellationIntentOnlyAsync(database.DataSource, retryClaim!);
        DurableWorkState? projectionCallback = null;

        var refusedPermit = await store.TryAcquireEffectPermitAsync(
            retryClaim!,
            onTerminalApplied: (_, state, _, _) =>
            {
                projectionCallback = state;
                return ValueTask.CompletedTask;
            });
        var suspended = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value.WorkId));
        var beforeProof = await ReadRecoverySnapshotAsync(database.DataSource, scope, accepted.Value.WorkId);

        var resolved = await provider.GetRequiredService<IDurableWorkOperatorClient>().ResolveAsync(
            new DurableWorkManualResolutionRequest(
                scope,
                accepted.Value.WorkId,
                new DurableCommandId($"operator-historical-proof-{resolution}"),
                "operator-test",
                "historical-provider-proof",
                suspended.Value!.Revision,
                resolution,
                resolution == DurableManualResolutionKind.Applied
                    ? resultCodec.Encode(new OperatorResult("historical-applied"))
                    : null));

        Assert.Null(refusedPermit);
        Assert.Equal(DurableWorkState.Suspended, projectionCallback);
        Assert.Equal("suspended_ambiguous_external_outcome", beforeProof.State);
        Assert.True(beforeProof.CancellationRequested);
        Assert.Equal(1, beforeProof.PermitCount);
        Assert.True(resolved.IsSuccess);
        Assert.Equal(expectedState, resolved.Value!.State);
        Assert.Equal(expectedPermitStatus, await ReadPermitStatusAsync(database.DataSource, scope, accepted.Value.WorkId));
    }

    private static SystemTextJsonDurablePayloadCodec<OperatorWork> CreateWorkCodec() => new(
        "tests.operator.work",
        "v1",
        DurableDataClassification.Operational,
        OperatorJsonContext.Default.OperatorWork,
        static value => value.Code.StartsWith("safe-", StringComparison.Ordinal));

    private static SystemTextJsonDurablePayloadCodec<OperatorResult> CreateResultCodec() => new(
        "tests.operator.result",
        "v1",
        DurableDataClassification.Operational,
        OperatorJsonContext.Default.OperatorResult,
        static value => !string.IsNullOrWhiteSpace(value.Code));

    private static void AddAmbiguousRecoveryRegistration(
        IServiceCollection services,
        DurableProviderSafety providerSafety,
        IDurablePayloadCodec<OperatorWork> workCodec,
        IDurablePayloadCodec<OperatorResult> resultCodec)
    {
        if (providerSafety == DurableProviderSafety.ReconcileBeforeRetry)
        {
            services.AddSingleton(new ReconcilerCapture());
            services.AddDurableWorkWithReconciler<
                OperatorWork,
                OperatorResult,
                ThrowingOperatorExecutor,
                AppliedOperatorReconciler>(
                "tests.operator.ambiguous-recovery",
                "v1",
                workCodec,
                resultCodec);
            return;
        }

        services.AddDurableWork<OperatorWork, OperatorResult, ThrowingOperatorExecutor>(
            "tests.operator.ambiguous-recovery",
            "v1",
            providerSafety,
            workCodec,
            resultCodec);
    }

    private static void AddCancelPendingRecoveryRegistration(
        IServiceCollection services,
        DurableProviderSafety providerSafety,
        IDurablePayloadCodec<OperatorWork> workCodec,
        IDurablePayloadCodec<OperatorResult> resultCodec,
        ExecutorCallCapture capture)
    {
        services.AddSingleton(capture);
        if (providerSafety == DurableProviderSafety.ReconcileBeforeRetry)
        {
            services.AddSingleton(new ReconcilerCapture());
            services.AddDurableWorkWithReconciler<
                OperatorWork,
                OperatorResult,
                IgnoringCancellationOperatorExecutor,
                AppliedOperatorReconciler>(
                "tests.operator.cancel-pending-recovery",
                "v1",
                workCodec,
                resultCodec);
            return;
        }

        services.AddDurableWork<OperatorWork, OperatorResult, IgnoringCancellationOperatorExecutor>(
            "tests.operator.cancel-pending-recovery",
            "v1",
            providerSafety,
            workCodec,
            resultCodec);
    }

    internal sealed record OperatorWork(string Code);

    internal sealed record OperatorResult(string Code);

    internal sealed class ThrowingOperatorExecutor : IDurableWorkerExecutor<OperatorWork, OperatorResult>
    {
        public ValueTask<OperatorResult> ExecuteAsync(
            DurableWorkerEnvelope<OperatorWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<OperatorResult>(new TimeoutException("Unknown provider outcome."));
    }

    internal sealed class IgnoringCancellationOperatorExecutor(ExecutorCallCapture capture) :
        IDurableWorkerExecutor<OperatorWork, OperatorResult>
    {
        public ValueTask<OperatorResult> ExecuteAsync(
            DurableWorkerEnvelope<OperatorWork> work,
            CancellationToken cancellationToken = default)
        {
            capture.Calls++;
            return ValueTask.FromResult(new OperatorResult("unexpected-execution"));
        }
    }

    internal sealed class AppliedOperatorReconciler(ReconcilerCapture capture) :
        IDurableEffectReconciler<OperatorWork, OperatorResult>
    {
        public ValueTask<DurableEffectReconciliation<OperatorResult>> ReconcileAsync(
            DurableWorkerEnvelope<OperatorWork> work,
            CancellationToken cancellationToken = default)
        {
            capture.Calls++;
            capture.ProviderKeyPrefix = work.ExecutionIdentity!.ProviderKey[..9];
            var result = capture.ResultKind switch
            {
                DurableEffectReconciliationKind.Applied =>
                    DurableEffectReconciliation<OperatorResult>.Applied(new OperatorResult("reconciled")),
                DurableEffectReconciliationKind.NotApplied => DurableEffectReconciliation<OperatorResult>.NotApplied(),
                DurableEffectReconciliationKind.Unknown => DurableEffectReconciliation<OperatorResult>.Unknown(),
                _ => throw new ArgumentOutOfRangeException(nameof(capture)),
            };
            return ValueTask.FromResult(result);
        }
    }

    internal sealed class ReconcilerCapture
    {
        internal int Calls { get; set; }

        internal string? ProviderKeyPrefix { get; set; }

        internal DurableEffectReconciliationKind ResultKind { get; init; } =
            DurableEffectReconciliationKind.Applied;
    }

    internal sealed class ExecutorCallCapture
    {
        internal int Calls { get; set; }
    }

    private static async ValueTask<string> ReadPermitStatusAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new Npgsql.NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new Npgsql.NpgsqlCommand(
            """
            SELECT status
            FROM appsurface_durable.effect_permit
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<bool> ReadCancellationRequestedAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new Npgsql.NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new Npgsql.NpgsqlCommand(
            """
            SELECT cancellation_requested_at IS NOT NULL
            FROM appsurface_durable.work
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<RecoverySnapshot> ReadRecoverySnapshotAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new Npgsql.NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new Npgsql.NpgsqlCommand(
            """
            SELECT work.state, work.due_at, work.runtime_epoch, work.revision,
                   work.terminal_at IS NOT NULL, dispatch.state, dispatch.due_at,
                   (SELECT count(*)
                    FROM appsurface_durable.effect_permit AS permit
                    WHERE permit.scope_id = work.scope_id AND permit.work_id = work.work_id),
                   (SELECT max(status)
                    FROM appsurface_durable.effect_permit AS permit
                    WHERE permit.scope_id = work.scope_id AND permit.work_id = work.work_id),
                   work.cancellation_requested_at IS NOT NULL
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
        return new RecoverySnapshot(
            reader.GetString(0),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(1), TimeSpan.Zero),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.GetBoolean(4),
            reader.GetString(5),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(6), TimeSpan.Zero),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetBoolean(9));
    }

    private static async ValueTask ForceRetryWaitAsync(
        Npgsql.NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new Npgsql.NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            scope.Parameters.AddWithValue("scope_id", scopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        long revision;
        await using (var work = new Npgsql.NpgsqlCommand(
            """
            UPDATE appsurface_durable.work
            SET state = 'retry_wait',
                due_at = clock_timestamp() - interval '1 second',
                lease_owner = NULL,
                lease_started_at = NULL,
                lease_expires_at = NULL,
                revision = revision + 1
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

        await using (var dispatch = new Npgsql.NpgsqlCommand(
            """
            UPDATE appsurface_durable.dispatch
            SET state = 'available', due_at = clock_timestamp() - interval '1 second', expected_revision = @revision
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
    }

    private static async ValueTask ForceCancellationIntentOnlyAsync(
        Npgsql.NpgsqlDataSource dataSource,
        PostgreSqlDurableWorkClaim claim)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new Npgsql.NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            scope.Parameters.AddWithValue("scope_id", claim.ScopeId.Value);
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new Npgsql.NpgsqlCommand(
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

    private sealed record RecoverySnapshot(
        string State,
        DateTimeOffset WorkDueAtUtc,
        Guid RuntimeEpoch,
        long Revision,
        bool IsTerminal,
        string DispatchState,
        DateTimeOffset DispatchDueAtUtc,
        long PermitCount,
        string? PermitStatus,
        bool CancellationRequested);
}

[JsonSerializable(typeof(PostgreSqlDurableWorkOperatorClientTests.OperatorWork))]
[JsonSerializable(typeof(PostgreSqlDurableWorkOperatorClientTests.OperatorResult))]
internal sealed partial class OperatorJsonContext : JsonSerializerContext;
