using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkExecutionPolicyTests;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableExecutionOperatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpochRotation_SuspendsPlannedWorkAndRecoveryRetainsItsOffsetAndDeadline(bool expired)
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(40);
        var accepted = (await lab.Client.EnqueueAsync(Request("epoch-recovery", deadline: deadline))).Value!;
        var nextEpoch = Guid.NewGuid();
        await new PostgreSqlDurableRuntimeSchemaManager(lab.Database.DataSource)
            .RotateRuntimeEpochAsync(lab.Epoch, nextEpoch, "authorized-test", "restore");
        var store = new PostgreSqlDurableWorkStore(lab.Database.DataSource, nextEpoch);
        var candidate = Assert.Single(await store.DiscoverAsync(10));
        Assert.Null(await store.TryClaimAsync(candidate, "recovery-worker"));
        Assert.Equal("suspended_manual_resolution", await lab.TextAsync("state"));
        Assert.Equal("runtime_epoch_mismatch", await lab.TextAsync("terminal_code"));
        Assert.Empty(await store.DiscoverAsync(10));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit;"));
        await lab.Database.SetExecutionTimeAsync(expired ? deadline : Anchor.AddMinutes(2));
        using var services = new ServiceCollection().BuildServiceProvider();
        var operators = new PostgreSqlDurableWorkOperatorClient(lab.Database.DataSource, lab.Registry,
            services.GetRequiredService<IServiceScopeFactory>(), nextEpoch);
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var command = new DurableWorkRecoveryReleaseRequest(new("execution-tests"), accepted.WorkId,
            new("recovery-release"), "authorized-test", "restore-release", revision);
        var released = await operators.ReleaseAfterRecoveryAsync(command);
        Assert.True(released.IsSuccess, released.Problem?.Problem);
        Assert.Equal(expired ? DurableWorkState.FailedTerminal : DurableWorkState.Ready, released.Value!.State);
        Assert.Equal(nextEpoch, await lab.ScalarAsync<Guid>("SELECT runtime_epoch FROM appsurface_durable.work;"));
        Assert.Equal(Anchor.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT due_at FROM appsurface_durable.work;"));
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_not_after FROM appsurface_durable.work;"));
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, (await operators.ReleaseAfterRecoveryAsync(command)).Value!.Outcome);
        if (expired)
        {
            Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
            Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_deadline_reached_at FROM appsurface_durable.work;"));
            Assert.Empty(await store.DiscoverAsync(10));
        }
        else Assert.Equal(1, (await store.TryClaimAsync(Assert.Single(await store.DiscoverAsync(10)), "recovered-worker"))!.AttemptNumber);
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, true)]
    [InlineData(DurableProviderSafety.Idempotent, false)]
    [InlineData(DurableProviderSafety.ProviderKeyed, true)]
    [InlineData(DurableProviderSafety.ProviderKeyed, false)]
    [InlineData(DurableProviderSafety.ManualResolution, true)]
    [InlineData(DurableProviderSafety.ManualResolution, false)]
    public async Task TimingClosedManualProof_EstablishesTruthWithoutAnotherExecution(DurableProviderSafety safety, bool applied)
    {
        await using var lab = await Lab.CreateAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        var operators = new PostgreSqlDurableWorkOperatorClient(lab.Database.DataSource, lab.Registry,
            services.GetRequiredService<IServiceScopeFactory>(), lab.Epoch);
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(Request("manual", safety, deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(deadline);
        await lab.Store.RecordCompletionAsync(permit.Claim, new(PostgreSqlWorkCompletionKind.Retry, "unknown", "{}"));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var result = applied ? Result() : null;
        var request = new DurableWorkManualResolutionRequest(permit.Claim.ScopeId, permit.Claim.WorkId, new("resolve"),
            "authorized-test", "provider-proof", revision,
            applied ? DurableManualResolutionKind.Applied : DurableManualResolutionKind.ProvenNotApplied, result);
        var observed = await operators.ResolveAsync(request);
        Assert.True(observed.IsSuccess, observed.Problem?.Problem);
        Assert.Equal(applied ? DurableWorkState.Succeeded : DurableWorkState.FailedTerminal, observed.Value!.State);
        Assert.Equal(applied ? "known_succeeded" : "proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
        var duplicate = await operators.ResolveAsync(request);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_operator_command WHERE status='completed';"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='operator_manual_resolve';"));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrySafe_UsesNextAcceptedOffsetAndHonorsPermanentCutoff(bool closed)
    {
        await using var lab = await Lab.CreateAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        var operators = new PostgreSqlDurableWorkOperatorClient(lab.Database.DataSource, lab.Registry,
            services.GetRequiredService<IServiceScopeFactory>(), lab.Epoch);
        var deadline = Anchor.AddMinutes(40);
        var permit = await lab.AdmitReadyAsync(Request("retry-safe", deadline: deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        if (closed)
        {
            await lab.Database.SetExecutionTimeAsync(deadline);
            await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(10)), "recover");
        }
        else
        {
            await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));
            await lab.Store.RecordCompletionAsync(permit.Claim, new(PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome, "unknown", "{}"));
        }
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var request = new DurableWorkRetrySafeRequest(permit.Claim.ScopeId, permit.Claim.WorkId, new("release"), "authorized-test", "repeat-safe", revision);
        var released = await operators.RetrySafeAsync(request);
        Assert.True(released.IsSuccess, released.Problem?.Problem);
        Assert.Equal(closed ? DurableWorkState.Suspended : DurableWorkState.Ready, released.Value!.State);
        if (!closed) Assert.Equal(Anchor.AddMinutes(5).UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT due_at FROM appsurface_durable.work;"));
        else Assert.Empty(await lab.Store.DiscoverAsync(10));
        Assert.Equal(closed ? "granted" : "ambiguous", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
    }

    [Theory]
    [InlineData(DurableEffectReconciliationKind.Applied, false)]
    [InlineData(DurableEffectReconciliationKind.Applied, true)]
    [InlineData(DurableEffectReconciliationKind.NotApplied, false)]
    [InlineData(DurableEffectReconciliationKind.NotApplied, true)]
    [InlineData(DurableEffectReconciliationKind.Unknown, false)]
    [InlineData(DurableEffectReconciliationKind.Unknown, true)]
    public async Task ReadOnlyReconciliation_ResolvesExistingTruthWithoutReopeningAfterDeadline(DurableEffectReconciliationKind proofKind, bool closed)
    {
        var registration = new ProofRegistration(proofKind);
        await using var lab = await Lab.CreateAsync(new DurableWorkRegistry([registration]));
        using var services = new ServiceCollection().BuildServiceProvider();
        var operators = new PostgreSqlDurableWorkOperatorClient(lab.Database.DataSource, lab.Registry,
            services.GetRequiredService<IServiceScopeFactory>(), lab.Epoch);
        var deadline = Anchor.AddMinutes(40);
        var permit = await lab.AdmitReadyAsync(Request("reconcile", DurableProviderSafety.ReconcileBeforeRetry, deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(closed ? 40 : 1));
        await lab.Store.RecordCompletionAsync(permit.Claim, new(PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome, "unknown", "{}"));
        if (closed)
            Assert.Null(await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(10)), "deadline-recovery"));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var command = new DurableWorkReconcileRequest(permit.Claim.ScopeId, permit.Claim.WorkId, new("reconcile-proof"), "authorized-test", "provider-proof", revision);
        var reconciled = await operators.ReconcileAsync(command);
        Assert.True(reconciled.IsSuccess, reconciled.Problem?.Problem);
        var expected = proofKind == DurableEffectReconciliationKind.Applied ? DurableWorkState.Succeeded
            : proofKind == DurableEffectReconciliationKind.Unknown ? DurableWorkState.Suspended
            : closed ? DurableWorkState.FailedTerminal : DurableWorkState.Ready;
        Assert.Equal(expected, reconciled.Value!.State);
        Assert.Equal(1, registration.Count);
        Assert.NotNull(registration.Execution);
        var duplicate = await operators.ReconcileAsync(command);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(1, registration.Count);
        if (expected == DurableWorkState.Ready)
            Assert.Equal(Anchor.AddMinutes(5).UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT due_at FROM appsurface_durable.work;"));
        else Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Theory]
    [InlineData(DurableEffectReconciliationKind.Applied)]
    [InlineData(DurableEffectReconciliationKind.NotApplied)]
    [InlineData(DurableEffectReconciliationKind.Unknown)]
    public async Task SlowReadOnlyProof_CrossesDeadlineWithoutReopeningExecution(DurableEffectReconciliationKind proofKind)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new ProofRegistration(proofKind, entered, release);
        await using var lab = await Lab.CreateAsync(new DurableWorkRegistry([registration]));
        using var services = new ServiceCollection().BuildServiceProvider();
        var operators = new PostgreSqlDurableWorkOperatorClient(lab.Database.DataSource, lab.Registry,
            services.GetRequiredService<IServiceScopeFactory>(), lab.Epoch);
        var permit = await lab.AdmitReadyAsync(Request("slow-proof", DurableProviderSafety.ReconcileBeforeRetry, Anchor.AddMinutes(40)));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));
        await lab.Store.RecordCompletionAsync(permit.Claim, new(PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome, "unknown", "{}"));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var command = new DurableWorkReconcileRequest(permit.Claim.ScopeId, permit.Claim.WorkId, new("slow-reconcile"),
            "authorized-test", "read-only-proof", revision);
        var pending = operators.ReconcileAsync(command).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("suspended_reconciliation_required", await lab.TextAsync("state"));
            Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_operator_command WHERE status='started';"));
            await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(40));
        }
        finally { release.TrySetResult(true); }
        var observed = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(observed.IsSuccess, observed.Problem?.Problem);
        Assert.Equal(proofKind == DurableEffectReconciliationKind.Applied ? DurableWorkState.Succeeded
            : proofKind == DurableEffectReconciliationKind.NotApplied ? DurableWorkState.FailedTerminal : DurableWorkState.Suspended, observed.Value!.State);
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal(1, registration.Count);
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, (await operators.ReconcileAsync(command)).Value!.Outcome);
        Assert.Equal(1, registration.Count);
    }

    [Theory]
    [InlineData(DurableEffectReconciliationKind.Applied)]
    [InlineData(DurableEffectReconciliationKind.NotApplied)]
    [InlineData(DurableEffectReconciliationKind.Unknown)]
    public async Task OperatorDeadlineObservation_PersistsAfterEarlierCircuitClosureAndClockReversal(DurableEffectReconciliationKind proofKind)
    {
        var registration = new ProofRegistration(proofKind);
        await using var lab = await Lab.CreateAsync(new DurableWorkRegistry([registration]));
        using var services = new ServiceCollection().BuildServiceProvider();
        var operators = new PostgreSqlDurableWorkOperatorClient(lab.Database.DataSource, lab.Registry,
            services.GetRequiredService<IServiceScopeFactory>(), lab.Epoch);
        var circuit = Anchor.AddMinutes(5);
        var deadline = Anchor.AddMinutes(40);
        var permit = await lab.AdmitReadyAsync(Request("operator-deadline-witness",
            DurableProviderSafety.ReconcileBeforeRetry, deadline, circuitMinutes: 5, offsets: [0, 1]));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(circuit);
        await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome, "unknown", "{}"));
        Assert.Equal("circuit_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(0, await lab.ScalarAsync<long>(
            "SELECT count(*) FROM appsurface_durable.work WHERE execution_deadline_reached_at IS NOT NULL;"));

        await lab.Database.SetExecutionTimeAsync(deadline);
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var command = new DurableWorkReconcileRequest(permit.Claim.ScopeId, permit.Claim.WorkId,
            new("operator-deadline-proof"), "authorized-test", "read-only-proof", revision);
        var observed = await operators.ReconcileAsync(command);
        Assert.True(observed.IsSuccess, observed.Problem?.Problem);
        Assert.Equal(proofKind == DurableEffectReconciliationKind.Applied ? DurableWorkState.Succeeded
            : proofKind == DurableEffectReconciliationKind.NotApplied ? DurableWorkState.FailedTerminal
            : DurableWorkState.Suspended, observed.Value!.State);
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>(
            "SELECT execution_deadline_reached_at FROM appsurface_durable.work;"));
        Assert.Equal(circuit.UtcDateTime, await lab.ScalarAsync<DateTime>(
            "SELECT execution_admission_closed_at FROM appsurface_durable.work;"));
        Assert.Equal("circuit_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));

        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(2));
        Assert.Equal(DurableWorkOperatorOutcome.Duplicate, (await operators.ReconcileAsync(command)).Value!.Outcome);
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>(
            "SELECT execution_deadline_reached_at FROM appsurface_durable.work;"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
        Assert.Equal(1, registration.Count);
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>(
            "SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='operator_reconcile';"));
    }

    [Fact]
    public async Task FailedOperatorHistoryProjection_RollsBackAllTruthAndTheClosureWitness()
    {
        await using var lab = await Lab.CreateAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        var operators = new PostgreSqlDurableWorkOperatorClient(lab.Database.DataSource, lab.Registry,
            services.GetRequiredService<IServiceScopeFactory>(), lab.Epoch);
        var permit = await lab.AdmitReadyAsync(Request("operator-rollback", deadline: Anchor.AddMinutes(40)));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Store.RecordCompletionAsync(permit.Claim, new(PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome, "unknown", "{}"));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var historyCount = await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;");
        await using var failHistory = lab.Database.DataSource.CreateCommand("""
            CREATE FUNCTION public.issue765_skip_operator_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.event_type='operator_retry_safe' THEN RETURN NULL; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER issue765_skip_operator_history BEFORE INSERT ON appsurface_durable.work_history
            FOR EACH ROW EXECUTE FUNCTION public.issue765_skip_operator_history();
            """);
        await failHistory.ExecuteNonQueryAsync();
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(240));
        var command = new DurableWorkRetrySafeRequest(permit.Claim.ScopeId, permit.Claim.WorkId, new("rollback-release"),
            "authorized-test", "repeat-safe", revision);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await operators.RetrySafeAsync(command));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal("suspended_ambiguous_external_outcome", await lab.TextAsync("state"));
        Assert.Equal("ambiguous", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal(historyCount, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_deadline_reached_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_operator_command;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.dispatch WHERE state='suspended' AND expected_revision=" + revision + ";"));
    }

    private static DurableEncodedPayload Result() => new("tests.delete-provider-access.result", "v1",
        DurableDataClassification.ApprovedApplication, new byte[] { 42 });

    private sealed class ProofRegistration(DurableEffectReconciliationKind proof,
        TaskCompletionSource<bool>? entered = null, TaskCompletionSource<bool>? release = null) : DurableWorkRegistration(
        PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.ReconcileBeforeRetry), "v1",
        DurableProviderSafety.ReconcileBeforeRetry, new PostgreSqlOpaqueTestCodec("tests.delete-provider-access", "v1"),
        new PostgreSqlOpaqueTestCodec("tests.delete-provider-access.result", "v1"))
    {
        internal int Count { get; private set; }
        internal DurableWorkExecutionSnapshot? Execution { get; private set; }
        public override bool CanReconcile => true;
        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) => throw new NotSupportedException();
        public override ValueTask<DurableEncodedPayload> InvokeAsync(IServiceProvider services, DurableWorkExecutionContext work, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override async ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(IServiceProvider services, DurableWorkExecutionContext work, CancellationToken cancellationToken = default)
        {
            Count++;
            Execution = work.Execution;
            entered?.TrySetResult(true);
            if (release is not null) await release.Task.WaitAsync(cancellationToken);
            return new DurableEncodedEffectReconciliation(proof, proof == DurableEffectReconciliationKind.Applied ? Result() : null);
        }
    }
}
