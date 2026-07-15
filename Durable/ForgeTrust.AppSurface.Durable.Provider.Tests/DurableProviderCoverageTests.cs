using System.Text;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class DurableProviderCoverageTests
{
    private static readonly DurableScopeId Scope = new("scope");
    private static readonly DurableWorkId Work = new("work");
    private static readonly DurableCommandId Command = new("command");
    private static readonly DateTimeOffset LocalTime = new(2026, 7, 15, 1, 2, 3, TimeSpan.FromHours(-4));
    private static readonly DurableEncodedPayload Payload = new(
        "test.payload",
        "v1",
        DurableDataClassification.Operational,
        new byte[] { 1, 2, 3 });

    [Fact]
    public void Operator_contracts_preserve_valid_values_and_normalize_timestamps()
    {
        var get = new DurableWorkGetRequest(Scope, Work);
        var snapshot = CreateSnapshot();
        var cancel = new DurableWorkCancelRequest(Scope, Work, "operator", "reason", 7);
        var cancelResult = new DurableWorkCancelResult(Work, DurableWorkCancelOutcome.Applied, DurableWorkState.CancelRequested, 8);
        var listRequest = new DurableWorkListRequest(Scope, DurableWorkState.Suspended, true, 25, "next.page");
        var item = CreateListItem();
        var listResult = new DurableWorkListResult([item], "next.page");
        var disable = new DurableScopeDisableRequest(Scope, "operator", "reason", 4);
        var disableResult = new DurableScopeDisableResult(Scope, DurableScopeDisableOutcome.Applied, 5);
        var operatorResult = new DurableWorkOperatorResult(Work, DurableWorkOperatorOutcome.Duplicate, DurableWorkState.Ready, 9);

        Assert.Equal(Scope, get.ScopeId);
        Assert.Equal(Work, get.WorkId);
        Assert.Equal(Scope, snapshot.ScopeId);
        Assert.Equal(Work, snapshot.WorkId);
        Assert.Equal("activity", snapshot.ActivityId);
        Assert.Equal("test.work", snapshot.WorkName);
        Assert.Equal("v1", snapshot.WorkVersion);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.State);
        Assert.Equal(DurableProviderSafety.ProviderKeyed, snapshot.ProviderSafety);
        Assert.Equal("activity", snapshot.ProviderKey);
        Assert.Equal(2, snapshot.AttemptNumber);
        Assert.Equal(3, snapshot.Revision);
        Assert.Equal(TimeSpan.Zero, snapshot.AcceptedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.DueAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.UpdatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.TerminalAtUtc!.Value.Offset);
        Assert.Equal("completed", snapshot.TerminalCode);
        Assert.Same(Payload, snapshot.Result);
        Assert.Equal(Scope, cancel.ScopeId);
        Assert.Equal(Work, cancel.WorkId);
        Assert.Equal("operator", cancel.ActorId);
        Assert.Equal("reason", cancel.ReasonCode);
        Assert.Equal(7, cancel.ExpectedRevision);
        Assert.Equal(Work, cancelResult.WorkId);
        Assert.Equal(DurableWorkCancelOutcome.Applied, cancelResult.Outcome);
        Assert.Equal(DurableWorkState.CancelRequested, cancelResult.State);
        Assert.Equal(8, cancelResult.Revision);
        Assert.Equal(Scope, listRequest.ScopeId);
        Assert.Equal(DurableWorkState.Suspended, listRequest.State);
        Assert.True(listRequest.RequiresRecoveryReleaseOnly);
        Assert.Equal(25, listRequest.PageSize);
        Assert.Equal("next.page", listRequest.ContinuationToken);
        Assert.Equal(Work, item.WorkId);
        Assert.Equal("activity", item.ActivityId);
        Assert.Equal("test.work", item.WorkName);
        Assert.Equal("v1", item.WorkVersion);
        Assert.Equal(DurableWorkState.Suspended, item.State);
        Assert.Equal(DurableProviderSafety.ReconcileBeforeRetry, item.ProviderSafety);
        Assert.Equal(2, item.AttemptNumber);
        Assert.Equal(3, item.Revision);
        Assert.Equal(TimeSpan.Zero, item.AcceptedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, item.DueAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, item.UpdatedAtUtc.Offset);
        Assert.Equal("suspended", item.TerminalCode);
        Assert.True(item.CancellationRequested);
        Assert.True(item.RequiresRecoveryRelease);
        Assert.Same(item, Assert.Single(listResult.Items));
        Assert.Equal("next.page", listResult.ContinuationToken);
        Assert.Equal(Scope, disable.ScopeId);
        Assert.Equal("operator", disable.ActorId);
        Assert.Equal("reason", disable.ReasonCode);
        Assert.Equal(4, disable.ExpectedGeneration);
        Assert.Equal(Scope, disableResult.ScopeId);
        Assert.Equal(DurableScopeDisableOutcome.Applied, disableResult.Outcome);
        Assert.Equal(5, disableResult.Generation);
        Assert.Equal(Work, operatorResult.WorkId);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, operatorResult.Outcome);
        Assert.Equal(DurableWorkState.Ready, operatorResult.State);
        Assert.Equal(9, operatorResult.Revision);
    }

    [Fact]
    public void Operator_contracts_reject_invalid_values()
    {
        Assert.Throws<ArgumentException>(() => new DurableWorkGetRequest(Scope, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(state: (DurableWorkState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(safety: (DurableProviderSafety)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(attemptNumber: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(revision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkCancelRequest(Scope, Work, "operator", "reason", 0));
        Assert.Throws<ArgumentException>(() => new DurableWorkCancelRequest(Scope, Work, "bad value", "reason", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkListRequest(Scope, (DurableWorkState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkListRequest(Scope, pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkListRequest(Scope, pageSize: 501));
        Assert.Throws<ArgumentException>(() => new DurableWorkListRequest(Scope, continuationToken: new string('a', 201)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(state: (DurableWorkState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(safety: (DurableProviderSafety)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(attemptNumber: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateListItem(revision: 0));
        Assert.Throws<ArgumentNullException>(() => new DurableWorkListResult(null!, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableScopeDisableRequest(Scope, "operator", "reason", 0));
        Assert.Throws<ArgumentException>(() => new DurableScopeDisableRequest(Scope, "operator", "line\nbreak", 1));
    }

    [Fact]
    public void Operator_mutations_cover_applied_payload_fingerprints_and_validation()
    {
        var reconcile = new DurableWorkReconcileRequest(Scope, Work, Command, "operator", "reason", 2);
        var retry = new DurableWorkRetrySafeRequest(Scope, Work, Command, "operator", "reason", 2);
        var release = new DurableWorkRecoveryReleaseRequest(Scope, Work, Command, "operator", "reason", 2);
        var applied = new DurableWorkManualResolutionRequest(
            Scope,
            Work,
            Command,
            "operator",
            "reason",
            2,
            DurableManualResolutionKind.Applied,
            Payload);

        AssertOperatorRequest(reconcile.ScopeId, reconcile.WorkId, reconcile.CommandId, reconcile.ActorId, reconcile.ReasonCode, reconcile.ExpectedRevision);
        Assert.Equal(64, reconcile.Fingerprint.Sha256.Length);
        AssertOperatorRequest(retry.ScopeId, retry.WorkId, retry.CommandId, retry.ActorId, retry.ReasonCode, retry.ExpectedRevision);
        Assert.Equal(64, retry.Fingerprint.Sha256.Length);
        AssertOperatorRequest(release.ScopeId, release.WorkId, release.CommandId, release.ActorId, release.ReasonCode, release.ExpectedRevision);
        Assert.Equal(64, release.Fingerprint.Sha256.Length);
        Assert.Equal(Scope, applied.ScopeId);
        Assert.Equal(Work, applied.WorkId);
        Assert.Equal(Command, applied.CommandId);
        Assert.Equal("operator", applied.ActorId);
        Assert.Equal("reason", applied.ReasonCode);
        Assert.Equal(2, applied.ExpectedRevision);
        Assert.Equal(DurableManualResolutionKind.Applied, applied.Resolution);
        Assert.Same(Payload, applied.Result);
        Assert.Equal(64, applied.Fingerprint.Sha256.Length);
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, applied.Fingerprint.Compare(
            new DurableWorkManualResolutionRequest(
                Scope,
                Work,
                Command,
                "operator",
                "reason",
                2,
                DurableManualResolutionKind.Applied,
                new DurableEncodedPayload(
                    "test.payload",
                    "v1",
                    DurableDataClassification.Operational,
                    new byte[] { 3, 2, 1 })).Fingerprint));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkManualResolutionRequest(
            Scope, Work, Command, "operator", "reason", 0, DurableManualResolutionKind.ProvenNotApplied));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkManualResolutionRequest(
            Scope, Work, Command, "operator", "reason", 1, (DurableManualResolutionKind)999));
        Assert.Throws<ArgumentException>(() => new DurableWorkManualResolutionRequest(
            Scope, Work, Command, "operator", "reason", 1, DurableManualResolutionKind.Applied));
        Assert.Throws<ArgumentException>(() => new DurableWorkManualResolutionRequest(
            Scope, Work, Command, "operator", "reason", 1, DurableManualResolutionKind.ProvenNotApplied, Payload));

        AssertOperatorRequestValidation((scope, work, command, revision) =>
            new DurableWorkReconcileRequest(scope, work, command, "operator", "reason", revision));
        AssertOperatorRequestValidation((scope, work, command, revision) =>
            new DurableWorkRetrySafeRequest(scope, work, command, "operator", "reason", revision));
        AssertOperatorRequestValidation((scope, work, command, revision) =>
            new DurableWorkRecoveryReleaseRequest(scope, work, command, "operator", "reason", revision));
    }

    [Fact]
    public void Optional_provider_values_accept_absence()
    {
        var snapshot = new DurableWorkSnapshot(
            Scope,
            Work,
            "activity",
            "test.work",
            "v1",
            DurableWorkState.Ready,
            DurableProviderSafety.Idempotent,
            "activity",
            0,
            1,
            LocalTime,
            LocalTime,
            LocalTime,
            null,
            null,
            null);
        var list = new DurableWorkListRequest(Scope);
        var pump = new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero);
        var health = new DurableRuntimeHealthSnapshot(
            DurableRuntimeHealthState.NotStarted,
            null,
            true,
            true,
            1,
            1,
            Guid.NewGuid(),
            null,
            "worker",
            null,
            DurableRuntimeSurface.Work,
            LocalTime,
            null,
            null,
            null,
            false,
            false,
            0,
            null,
            null);

        Assert.Null(snapshot.TerminalAtUtc);
        Assert.Null(snapshot.TerminalCode);
        Assert.Null(snapshot.Result);
        Assert.Null(list.State);
        Assert.Null(list.ContinuationToken);
        Assert.Null(pump.NextDueAtUtc);
        Assert.Null(health.ActiveRuntimeEpoch);
        Assert.Null(health.WorkerInstanceId);
        Assert.Null(health.StartedAtUtc);
        Assert.Null(health.LastHeartbeatAtUtc);
        Assert.Null(health.LastSuccessfulSweepAtUtc);
        Assert.Null(health.OldestDueAtUtc);
        Assert.Null(health.OldestDueAge);
    }

    [Fact]
    public void Runtime_contracts_preserve_values_and_reject_every_bound()
    {
        var request = new DurableRuntimePumpRequest(17, TimeSpan.FromSeconds(4), DurableRuntimeSurface.Work | DurableRuntimeSurface.Flow);
        var result = new DurableRuntimePumpResult(5, 4, 3, 2, 1, true, LocalTime, TimeSpan.FromSeconds(2));

        Assert.Equal(17, request.MaximumItems);
        Assert.Equal(TimeSpan.FromSeconds(4), request.TimeBudget);
        Assert.Equal(DurableRuntimeSurface.Work | DurableRuntimeSurface.Flow, request.Surfaces);
        Assert.Equal(5, result.Discovered);
        Assert.Equal(4, result.Claimed);
        Assert.Equal(3, result.Processed);
        Assert.Equal(2, result.Deferred);
        Assert.Equal(1, result.Failed);
        Assert.True(result.HasMore);
        Assert.Equal(TimeSpan.Zero, result.NextDueAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Elapsed);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(10_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(timeBudget: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(timeBudget: TimeSpan.FromMinutes(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(surfaces: (DurableRuntimeSurface)8));
        AssertPumpCountRejected((-1, 0, 0, 0, 0));
        AssertPumpCountRejected((0, -1, 0, 0, 0));
        AssertPumpCountRejected((0, 0, -1, 0, 0));
        AssertPumpCountRejected((0, 0, 0, -1, 0));
        AssertPumpCountRejected((0, 0, 0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void Runtime_health_contract_preserves_values_and_rejects_every_bound()
    {
        var epoch = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var snapshot = CreateHealth(epoch, instance);

        Assert.Equal(DurableRuntimeHealthState.Draining, snapshot.State);
        Assert.Equal("ASDUR501", snapshot.ProblemCode);
        Assert.True(snapshot.SchemaCompatible);
        Assert.True(snapshot.EpochCompatible);
        Assert.Equal(2, snapshot.InstalledSchemaVersion);
        Assert.Equal(1, snapshot.RequiredSchemaVersion);
        Assert.Equal(epoch, snapshot.ConfiguredRuntimeEpoch);
        Assert.Equal(epoch, snapshot.ActiveRuntimeEpoch);
        Assert.Equal("worker", snapshot.WorkerId);
        Assert.Equal(instance, snapshot.WorkerInstanceId);
        Assert.Equal(DurableRuntimeSurface.All, snapshot.HostedSurfaces);
        Assert.Equal(TimeSpan.Zero, snapshot.ObservedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.StartedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.LastHeartbeatAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.LastSuccessfulSweepAtUtc!.Value.Offset);
        Assert.True(snapshot.IsDraining);
        Assert.True(snapshot.IsPassActive);
        Assert.Equal(4, snapshot.DueDispatchCount);
        Assert.Equal(TimeSpan.Zero, snapshot.OldestDueAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot.OldestDueAge);

        AssertHealthRejected(state: (DurableRuntimeHealthState)999);
        AssertHealthRejected(installedSchemaVersion: -1);
        AssertHealthRejected(requiredSchemaVersion: 0);
        AssertHealthRejected(configuredRuntimeEpoch: Guid.Empty);
        AssertHealthRejected(workerId: " ");
        AssertHealthRejected(workerId: new string('a', 201));
        AssertHealthRejected(hostedSurfaces: DurableRuntimeSurface.None);
        AssertHealthRejected(hostedSurfaces: (DurableRuntimeSurface)8);
        AssertHealthRejected(dueDispatchCount: -1);
        AssertHealthRejected(oldestDueAge: TimeSpan.FromTicks(-1));
        AssertHealthRejected(problemCode: " ");
        AssertHealthRejected(problemCode: new string('a', 121));
    }

    [Fact]
    public async Task Provider_adapter_prepares_invokes_and_reconciles_without_provider_io()
    {
        var inputCodec = new StringCodec<TestInput>("test.input", value => value.Value, value => new TestInput(value));
        var resultCodec = new StringCodec<TestResult>("test.result", value => value.Value, value => new TestResult(value));
        var registration = new DurableWorkRegistration<TestInput, TestResult, TestExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ReconcileBeforeRetry,
            inputCodec,
            resultCodec,
            services => services.GetRequiredService<TestReconciler>());
        await using var services = new ServiceCollection()
            .AddSingleton<TestExecutor>()
            .AddSingleton<TestReconciler>()
            .BuildServiceProvider();
        var claim = CreateClaim(inputCodec.Encode(new TestInput("input")), DurableProviderSafety.ReconcileBeforeRetry);

        var invocation = DurableProviderWorkAdapter.Prepare(registration, services, claim);
        var invoked = await invocation.InvokeAsync();
        var reconciled = await DurableProviderWorkAdapter.ReconcileAsync(
            registration,
            services,
            claim);

        Assert.Equal(new TestResult("executed:input"), resultCodec.Decode(invoked));
        Assert.Equal(DurableEffectReconciliationKind.Applied, reconciled.Kind);
        Assert.Equal(new TestResult("reconciled:input"), resultCodec.Decode(reconciled.Result!));

        Assert.Throws<ArgumentNullException>(() => DurableProviderWorkAdapter.Prepare(null!, services, claim));
        Assert.Throws<ArgumentNullException>(() => DurableProviderWorkAdapter.Prepare(registration, null!, claim));
        Assert.Throws<ArgumentNullException>(() => DurableProviderWorkAdapter.Prepare(registration, services, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await DurableProviderWorkAdapter.ReconcileAsync(null!, services, claim));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await DurableProviderWorkAdapter.ReconcileAsync(registration, null!, claim));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await DurableProviderWorkAdapter.ReconcileAsync(registration, services, null!));
    }

    [Fact]
    public void Claimed_work_preserves_fences_and_rejects_invalid_values()
    {
        var claim = CreateClaim(Payload);
        var context = claim.ToExecutionContext();

        Assert.Equal(Scope, claim.ScopeId);
        Assert.Equal(Work, claim.WorkId);
        Assert.Equal("activity", claim.ActivityId);
        Assert.Equal("test.work", claim.WorkName);
        Assert.Equal("v1", claim.WorkVersion);
        Assert.Same(Payload, claim.Payload);
        Assert.Equal(DurableProviderSafety.ProviderKeyed, claim.ProviderSafety);
        Assert.Equal(2, claim.AttemptNumber);
        Assert.Equal(3, claim.LeaseGeneration);
        Assert.Equal(4, claim.ScopeGeneration);
        Assert.Equal("epoch", claim.RuntimeEpoch);
        Assert.Equal("activity", context.ExecutionIdentity.ProviderKey);
        Assert.Equal(3, context.ExecutionIdentity.LeaseGeneration);
        Assert.Equal(4, context.ExecutionIdentity.ScopeGeneration);
        Assert.Equal("epoch", context.ExecutionIdentity.RuntimeEpoch);

        Assert.Throws<ArgumentException>(() => new DurableClaimedWork(
            Scope,
            default,
            "activity",
            "test.work",
            "v1",
            Payload,
            DurableProviderSafety.ProviderKeyed,
            2,
            3,
            4,
            "epoch"));
        AssertClaimRejected(safety: (DurableProviderSafety)999);
        AssertClaimRejected(attemptNumber: 0);
        AssertClaimRejected(leaseGeneration: 0);
        AssertClaimRejected(scopeGeneration: 0);
        Assert.Throws<ArgumentNullException>(() => CreateClaim(null!));
        AssertClaimRejected(runtimeEpoch: "bad value");
    }

    private static DurableWorkSnapshot CreateSnapshot(
        DurableWorkState state = DurableWorkState.Succeeded,
        DurableProviderSafety safety = DurableProviderSafety.ProviderKeyed,
        int attemptNumber = 2,
        long revision = 3) =>
        new(
            Scope,
            Work,
            "activity",
            "test.work",
            "v1",
            state,
            safety,
            "activity",
            attemptNumber,
            revision,
            LocalTime,
            LocalTime,
            LocalTime,
            LocalTime,
            "completed",
            Payload);

    private static DurableWorkListItem CreateListItem(
        DurableWorkState state = DurableWorkState.Suspended,
        DurableProviderSafety safety = DurableProviderSafety.ReconcileBeforeRetry,
        int attemptNumber = 2,
        long revision = 3) =>
        new(
            Work,
            "activity",
            "test.work",
            "v1",
            state,
            safety,
            attemptNumber,
            revision,
            LocalTime,
            LocalTime,
            LocalTime,
            "suspended",
            true,
            true);

    private static void AssertOperatorRequestValidation(
        Func<DurableScopeId, DurableWorkId, DurableCommandId, long, object> create)
    {
        Assert.Throws<ArgumentException>(() => create(default, Work, Command, 1));
        Assert.Throws<ArgumentException>(() => create(Scope, default, Command, 1));
        Assert.Throws<ArgumentException>(() => create(Scope, Work, default, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => create(Scope, Work, Command, 0));
    }

    private static void AssertOperatorRequest(
        DurableScopeId scope,
        DurableWorkId work,
        DurableCommandId command,
        string actor,
        string reason,
        long revision)
    {
        Assert.Equal(Scope, scope);
        Assert.Equal(Work, work);
        Assert.Equal(Command, command);
        Assert.Equal("operator", actor);
        Assert.Equal("reason", reason);
        Assert.Equal(2, revision);
    }

    private static void AssertPumpCountRejected((int Discovered, int Claimed, int Processed, int Deferred, int Failed) counts) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpResult(
            counts.Discovered,
            counts.Claimed,
            counts.Processed,
            counts.Deferred,
            counts.Failed,
            false,
            null,
            TimeSpan.Zero));

    private static DurableRuntimeHealthSnapshot CreateHealth(
        Guid configuredRuntimeEpoch,
        Guid? workerInstanceId,
        DurableRuntimeHealthState state = DurableRuntimeHealthState.Draining,
        int installedSchemaVersion = 2,
        int requiredSchemaVersion = 1,
        string workerId = "worker",
        DurableRuntimeSurface hostedSurfaces = DurableRuntimeSurface.All,
        long dueDispatchCount = 4,
        TimeSpan? oldestDueAge = null,
        string? problemCode = "ASDUR501") =>
        new(
            state,
            problemCode,
            true,
            true,
            installedSchemaVersion,
            requiredSchemaVersion,
            configuredRuntimeEpoch,
            configuredRuntimeEpoch,
            workerId,
            workerInstanceId,
            hostedSurfaces,
            LocalTime,
            LocalTime,
            LocalTime,
            LocalTime,
            true,
            true,
            dueDispatchCount,
            LocalTime,
            oldestDueAge ?? TimeSpan.FromSeconds(3));

    private static void AssertHealthRejected(
        DurableRuntimeHealthState state = DurableRuntimeHealthState.Healthy,
        int installedSchemaVersion = 1,
        int requiredSchemaVersion = 1,
        Guid? configuredRuntimeEpoch = null,
        string workerId = "worker",
        DurableRuntimeSurface hostedSurfaces = DurableRuntimeSurface.All,
        long dueDispatchCount = 0,
        TimeSpan? oldestDueAge = null,
        string? problemCode = null) =>
        Assert.ThrowsAny<ArgumentException>(() => CreateHealth(
            configuredRuntimeEpoch ?? Guid.NewGuid(),
            Guid.NewGuid(),
            state,
            installedSchemaVersion,
            requiredSchemaVersion,
            workerId,
            hostedSurfaces,
            dueDispatchCount,
            oldestDueAge,
            problemCode));

    private static DurableClaimedWork CreateClaim(
        DurableEncodedPayload payload,
        DurableProviderSafety safety = DurableProviderSafety.ProviderKeyed,
        DurableWorkId? workId = null,
        int attemptNumber = 2,
        long leaseGeneration = 3,
        long scopeGeneration = 4,
        string runtimeEpoch = "epoch") =>
        new(
            Scope,
            workId ?? Work,
            "activity",
            "test.work",
            "v1",
            payload,
            safety,
            attemptNumber,
            leaseGeneration,
            scopeGeneration,
            runtimeEpoch);

    private static void AssertClaimRejected(
        DurableWorkId? workId = null,
        DurableProviderSafety safety = DurableProviderSafety.ProviderKeyed,
        int attemptNumber = 2,
        long leaseGeneration = 3,
        long scopeGeneration = 4,
        string runtimeEpoch = "epoch") =>
        Assert.ThrowsAny<ArgumentException>(() => CreateClaim(
            Payload,
            safety,
            workId,
            attemptNumber,
            leaseGeneration,
            scopeGeneration,
            runtimeEpoch));

    private sealed record TestInput(string Value);

    private sealed record TestResult(string Value);

    private sealed class TestExecutor : IDurableWorkerExecutor<TestInput, TestResult>
    {
        public ValueTask<TestResult> ExecuteAsync(
            DurableWorkerEnvelope<TestInput> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TestResult($"executed:{work.Payload!.Value}"));
    }

    private sealed class TestReconciler : IDurableEffectReconciler<TestInput, TestResult>
    {
        public ValueTask<DurableEffectReconciliation<TestResult>> ReconcileAsync(
            DurableWorkerEnvelope<TestInput> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DurableEffectReconciliation<TestResult>.Applied(
                new TestResult($"reconciled:{work.Payload!.Value}")));
    }

    private sealed class StringCodec<T>(
        string contractName,
        Func<T, string> encode,
        Func<string, T> decode) : IDurablePayloadCodec<T>
        where T : notnull
    {
        public Type PayloadType => typeof(T);

        public string ContractName => contractName;

        public string ContractVersion => "v1";

        public DurableDataClassification Classification => DurableDataClassification.Operational;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(T value) => new(
            ContractName,
            ContractVersion,
            Classification,
            Encoding.UTF8.GetBytes(encode(value)),
            RetentionPolicyId);

        public T Decode(DurableEncodedPayload payload) => decode(Encoding.UTF8.GetString(payload.Content.Span));

        public DurableEncodedPayload EncodeObject(object value) => Encode((T)value);

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload)!;
    }
}
