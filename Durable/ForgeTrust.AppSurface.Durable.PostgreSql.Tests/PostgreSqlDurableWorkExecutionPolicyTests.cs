using System.Text;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableWorkExecutionPolicyTests
{
    internal static readonly DateTimeOffset Anchor = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task PlannedRetries_UseAcceptedOffsetsEvenWhenTheNextSlotIsOverdue()
    {
        await using var lab = await Lab.CreateAsync();
        var request = Request("offsets");
        var accepted = (await lab.Client.EnqueueAsync(request)).Value!;
        Assert.Equal(Anchor, accepted.AcceptedAtUtc);
        var elapsedMinutes = new[] { 0, 6, 21, 61, 181 };
        var expectedOffsets = new[] { 0, 5, 20, 60, 180 };
        for (var index = 0; index < elapsedMinutes.Length; index++)
        {
            await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(elapsedMinutes[index]));
            var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
            var claim = await lab.Store.TryClaimAsync(candidate, "offset-worker");
            Assert.NotNull(claim);
            Assert.Equal(index + 1, claim.AttemptNumber);
            Assert.Equal(Anchor.AddMinutes(expectedOffsets[index]), claim.Execution!.NextEligibilityAtUtc);
            var completion = await lab.Store.RecordCompletionAsync(claim,
                new(PostgreSqlWorkCompletionKind.Retry, "retry", "{}"));
            if (index == 4)
            {
                Assert.Equal(DurableWorkState.FailedTerminal, completion.State);
                Assert.Null(completion.NextDueAtUtc);
            }
            else
            {
                Assert.Equal(DurableWorkState.Ready, completion.State);
                Assert.Equal(Anchor.AddMinutes(expectedOffsets[index + 1]), completion.NextDueAtUtc);
            }
        }
        Assert.Equal("slots_exhausted", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Fact]
    public async Task IdenticalExpiredReplay_ReturnsOriginalAcceptance_WhileChangedDeadlineConflicts()
    {
        await using var lab = await Lab.CreateAsync();
        var request = Request("duplicate", deadline: Anchor.AddMinutes(1));
        var original = (await lab.Client.EnqueueAsync(request)).Value!;
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(2));
        var replay = await lab.Client.EnqueueAsync(request);
        Assert.True(replay.IsSuccess);
        Assert.Equal(original.WorkId, replay.Value!.WorkId);
        Assert.Equal(original.AcceptedAtUtc, replay.Value.AcceptedAtUtc);
        Assert.Equal(DurableWorkAcceptanceKind.Duplicate, replay.Value.Kind);
        var conflict = await lab.Client.EnqueueAsync(Request("duplicate", deadline: Anchor.AddMinutes(3)));
        Assert.Equal(DurableProblemCodes.CommandConflict, conflict.Problem!.Code);
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.dispatch;"));
    }

    [Fact]
    public async Task ExpiredNewAcceptance_LeavesCallerTransactionCommittableAndNoWork()
    {
        await using var lab = await Lab.CreateAsync();
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var before = new NpgsqlCommand("CREATE TABLE domain_fact(value integer); INSERT INTO domain_fact VALUES(1);", connection, transaction);
        await before.ExecuteNonQueryAsync();
        var rejected = await lab.Writer.EnqueueAsync(transaction, Request("expired", deadline: Anchor));
        Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, rejected.Problem!.Code);
        await using var after = new NpgsqlCommand("INSERT INTO domain_fact VALUES(2);", connection, transaction);
        await after.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        Assert.Equal(2, await lab.ScalarAsync<long>("SELECT count(*) FROM domain_fact;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work;"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DeadlineBoundary_UsesExclusiveMicrosecondCutoff(int microseconds)
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        await lab.Client.EnqueueAsync(Request("deadline-claim", deadline: deadline));
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(1));
        await lab.Database.SetExecutionTimeAsync(deadline.AddTicks(microseconds * 10));
        var claim = await lab.Store.TryClaimAsync(candidate, "deadline-worker");
        if (microseconds < 0)
        {
            Assert.NotNull(claim);
            Assert.Equal(deadline, claim.LeaseExpiresAtUtc);
        }
        else
        {
            Assert.Null(claim);
            Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, await lab.TextAsync("terminal_code"));
            Assert.Equal("failed", await lab.TextAsync("state"));
            Assert.Empty(await lab.Store.DiscoverAsync(10));
        }
    }

    [Theory]
    [InlineData("permit")]
    [InlineData("invocation")]
    public async Task RefusedPreInvocation_PersistsAbsenceAndDoesNotReopenWhenClockMovesBack(string boundary)
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        await lab.Client.EnqueueAsync(Request("pre-invocation", deadline: deadline));
        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;
        PostgreSqlEffectPermit? permit = null;
        if (boundary == "invocation") permit = await lab.Store.TryAcquireEffectPermitAsync(claim);
        await lab.Database.SetExecutionTimeAsync(deadline);
        if (boundary == "permit") Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(claim));
        else Assert.False(await lab.Store.TryAdmitInvocationAsync(permit!));
        Assert.Equal("failed", await lab.TextAsync("state"));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        if (permit is not null)
            Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE status='proven_no_effect';"));
        await lab.Database.SetExecutionTimeAsync(Anchor);
        Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(claim));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Fact]
    public async Task InvocationAdmission_IsOneUseEvenForConcurrentExactReplay()
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("one-use"));
        var admissions = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(async _ => await lab.Store.TryAdmitInvocationAsync(permit)));
        Assert.Single(admissions, static admitted => admitted);
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='invocation_admitted';"));
    }

    [Fact]
    public async Task ExactPermitReplay_ReturnsOriginalGrantAndAdmitsInvocationOnlyOnce()
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("permit-replay"));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");

        var replay = await lab.Store.TryAcquireEffectPermitAsync(permit.Claim);

        Assert.NotNull(replay);
        Assert.Equal(permit.PermitId, replay!.PermitId);
        Assert.Equal(permit.ProviderKey, replay.ProviderKey);
        Assert.Equal(permit.PermittedAtUtc, replay.PermittedAtUtc);
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='effect_permitted';"));

        Assert.True(await lab.Store.TryAdmitInvocationAsync(replay));
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='invocation_admitted';"));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent)]
    [InlineData(DurableProviderSafety.ProviderKeyed)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry)]
    [InlineData(DurableProviderSafety.ManualResolution)]
    public async Task LateCurrentSuccess_QuarantinesMetadataAndPreservesUnknownEffect(DurableProviderSafety safety)
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(Request("late", safety, deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(deadline);
        var result = new DurableEncodedPayload("tests.result", "v1", DurableDataClassification.ApprovedApplication,
            Encoding.UTF8.GetBytes("late secret"), "tests.late-retention");
        var completion = new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Succeeded, "success", "{}", result);
        var applied = await lab.Store.RecordCompletionAsync(permit.Claim, completion);
        Assert.Equal(DurableWorkState.Suspended, applied.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, await lab.TextAsync("terminal_code"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
        Assert.Equal("ambiguous", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal("tests.late-retention", await lab.ScalarAsync<string>("SELECT observation_retention_policy_id FROM appsurface_durable.work_history WHERE event_type='late_completion_succeeded';"));
        Assert.Equal(Convert.FromHexString(result.Sha256), await lab.ScalarAsync<byte[]>("SELECT observation_sha256 FROM appsurface_durable.work_history WHERE event_type='late_completion_succeeded';"));
        Assert.Equal(result.Content.ToArray(), await lab.ScalarAsync<byte[]>("SELECT observation_payload FROM appsurface_durable.work_history WHERE event_type='late_completion_succeeded';"));
        var before = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        await lab.Store.RecordCompletionAsync(permit.Claim, completion);
        await lab.Store.RecordCompletionAsync(permit.Claim, completion);
        Assert.Equal(before, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='stale_completion_succeeded';"));
    }

    [Theory]
    [InlineData((int)PostgreSqlWorkCompletionKind.Retry)]
    [InlineData((int)PostgreSqlWorkCompletionKind.ProvenNoEffect)]
    [InlineData((int)PostgreSqlWorkCompletionKind.AmbiguousExternalOutcome)]
    [InlineData((int)PostgreSqlWorkCompletionKind.FailedTerminal)]
    public async Task CutoffNeverInfersNoEffectFromAnAdmittedFailure(int kindValue)
    {
        var kind = (PostgreSqlWorkCompletionKind)kindValue;
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(Request("failure", deadline: deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(deadline.AddTicks(10));
        var outcome = await lab.Store.RecordCompletionAsync(permit.Claim, new(kind, "failure", "{}"));
        Assert.Equal(kind == PostgreSqlWorkCompletionKind.ProvenNoEffect ? DurableWorkState.FailedTerminal : DurableWorkState.Suspended, outcome.State);
        Assert.Equal(kind == PostgreSqlWorkCompletionKind.ProvenNoEffect ? DurableProblemCodes.ExecutionDeadlineReached : DurableProblemCodes.AmbiguousExternalOutcome,
            await lab.TextAsync("terminal_code"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent)]
    [InlineData(DurableProviderSafety.ProviderKeyed)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry)]
    [InlineData(DurableProviderSafety.ManualResolution)]
    public async Task AdmittedRetryBeforeCutoff_PreservesUnknownEffectAccordingToSafety(DurableProviderSafety safety)
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("admitted-retry", safety));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));

        var completed = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.Retry, "provider_retry", "{}"));

        var repeatIsSafe = safety is DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed;
        Assert.Equal(repeatIsSafe ? DurableWorkState.Ready : DurableWorkState.Suspended, completed.State);
        Assert.Equal(repeatIsSafe ? Anchor.AddMinutes(5) : null, completed.NextDueAtUtc);
        Assert.Equal(repeatIsSafe ? "provider_retry" : DurableProblemCodes.AmbiguousExternalOutcome,
            await lab.TextAsync("terminal_code"));
        var expectedState = repeatIsSafe ? "retry_wait"
            : safety == DurableProviderSafety.ReconcileBeforeRetry ? "suspended_reconciliation_required" : "suspended_manual_resolution";
        Assert.Equal(expectedState, await lab.TextAsync("state"));
        Assert.Equal(repeatIsSafe ? "available" : "suspended", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal((repeatIsSafe ? Anchor.AddMinutes(5) : Anchor).UtcDateTime,
            await lab.ScalarAsync<DateTime>("SELECT due_at FROM appsurface_durable.work;"));
        Assert.Equal("ambiguous", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal(permit.ProviderKey, await lab.ScalarAsync<string>("SELECT activity_id FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
    }

    [Fact]
    public async Task CircuitOnlyClosure_AllowsCurrentAdmittedSuccessAndLeaseRenewal()
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("circuit", circuitMinutes: 2, offsets: [0]));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(3));
        Assert.NotNull(await lab.Store.RenewLeaseAsync(permit.Claim));
        Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(permit.Claim));
        var result = new DurableEncodedPayload("tests.result", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 1 });
        var completed = await lab.Store.RecordCompletionAsync(permit.Claim, new(PostgreSqlWorkCompletionKind.Succeeded, "success", "{}", result));
        Assert.Equal(DurableWorkState.Succeeded, completed.State);
        Assert.Equal("circuit_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
    }

    [Fact]
    public async Task ExpiredFutureDueDeadlineOnly_IsDiscoveredAndPermanentlyDrained()
    {
        await using var lab = await Lab.CreateAsync();
        var policy = DurableWorkExecutionPolicy.FromRetryPolicy(Retry(5));
        var request = DurableWorkRequest.CreateWithExecutionPolicy(new("execution-tests"), new("future"), "future-key",
            PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent), "v1", Payload(),
            DurableProviderSafety.Idempotent, policy, new(Anchor.AddMinutes(1)), Anchor.AddHours(1));
        await lab.Client.EnqueueAsync(request);
        Assert.Empty(await lab.Store.DiscoverAsync(10));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        Assert.Equal(Anchor.AddHours(1), candidate.DueAtUtc);
        Assert.Null(await lab.Store.TryClaimAsync(candidate, "worker"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Fact]
    public async Task ProjectionFailure_RollsBackClosureAndEveryAuthoritativeRecord()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        await lab.Client.EnqueueAsync(Request("rollback", deadline: deadline));
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        await lab.Database.SetExecutionTimeAsync(deadline);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lab.Store.TryClaimAsync(candidate, "worker",
            onTransitionApplied: static (_, _, _, _) => throw new InvalidOperationException("projection failed")));
        Assert.Equal("pending", await lab.TextAsync("state"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.dispatch WHERE state='available';"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, false, false)]
    [InlineData(DurableProviderSafety.ProviderKeyed, false, false)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, false)]
    [InlineData(DurableProviderSafety.ManualResolution, false, false)]
    [InlineData(DurableProviderSafety.Idempotent, true, false)]
    [InlineData(DurableProviderSafety.ProviderKeyed, true, false)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, true, false)]
    [InlineData(DurableProviderSafety.ManualResolution, true, false)]
    [InlineData(DurableProviderSafety.Idempotent, false, true)]
    [InlineData(DurableProviderSafety.ProviderKeyed, false, true)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, true)]
    [InlineData(DurableProviderSafety.ManualResolution, false, true)]
    [InlineData(DurableProviderSafety.Idempotent, true, true)]
    [InlineData(DurableProviderSafety.ProviderKeyed, true, true)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, true, true)]
    [InlineData(DurableProviderSafety.ManualResolution, true, true)]
    public async Task LeaseRecovery_SeparatesUnconsumedPermitFromAdmittedUncertainty(
        DurableProviderSafety safety, bool admitted, bool afterDeadline)
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(40);
        var permit = await lab.AdmitReadyAsync(Request("recovery", safety, deadline));
        if (admitted) Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(afterDeadline ? 40 : 31));
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        var recovered = await lab.Store.TryClaimAsync(candidate, "recovery-worker");
        var safeRepeat = safety is DurableProviderSafety.Idempotent or DurableProviderSafety.ProviderKeyed;
        if (!afterDeadline && (!admitted || safeRepeat))
        {
            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.AttemptNumber);
            Assert.Equal(permit.Claim.ActivityId, recovered.ActivityId);
            Assert.Equal(Anchor.AddMinutes(5), recovered.Execution!.NextEligibilityAtUtc);
        }
        else
        {
            Assert.Null(recovered);
            Assert.Equal(admitted ? DurableProblemCodes.AmbiguousExternalOutcome : DurableProblemCodes.ExecutionDeadlineReached,
                await lab.TextAsync("terminal_code"));
            Assert.Empty(await lab.Store.DiscoverAsync(10));
        }
        if (!admitted)
            Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
    }

    [Fact]
    public async Task LostPreEffectClaim_ConsumesItsSlotAndWaitsForNextAcceptedOffset()
    {
        await using var lab = await Lab.CreateAsync();
        await lab.Client.EnqueueAsync(Request("early-recovery", circuitMinutes: 120, offsets: [0, 60]));
        var original = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(10)), "worker"))!;
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(31));
        var expired = Assert.Single(await lab.Store.DiscoverAsync(10));
        Assert.Null(await lab.Store.TryClaimAsync(expired, "recovery-worker"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal(Anchor.AddMinutes(60).UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT due_at FROM appsurface_durable.work;"));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(60));
        var next = Assert.Single(await lab.Store.DiscoverAsync(10));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(59));
        Assert.Null(await lab.Store.TryClaimAsync(next, "early-worker"));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(61));
        var recovered = await lab.Store.TryClaimAsync(next, "recovery-worker");
        Assert.NotNull(recovered);
        Assert.Equal(original.AttemptNumber + 1, recovered.AttemptNumber);
        Assert.Equal(Anchor.AddMinutes(60), recovered.Execution!.NextEligibilityAtUtc);
    }

    [Fact]
    public async Task StaleCompletion_CannotChangeTheWinningAttemptPermitOrResult()
    {
        await using var lab = await Lab.CreateAsync();
        var oldPermit = await lab.AdmitReadyAsync(Request("stale"));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(oldPermit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(31));
        var currentClaim = await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(10)), "winner");
        var currentPermit = (await lab.Store.TryAcquireEffectPermitAsync(currentClaim!))!;
        Assert.True(await lab.Store.TryAdmitInvocationAsync(currentPermit));
        var winner = new DurableEncodedPayload("tests.result", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 42 });
        await lab.Store.RecordCompletionAsync(currentPermit.Claim, new(PostgreSqlWorkCompletionKind.Succeeded, "winner", "{}", winner));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var oldResult = new DurableEncodedPayload("tests.result", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 13 });
        var stale = await lab.Store.RecordCompletionAsync(oldPermit.Claim, new(PostgreSqlWorkCompletionKind.Succeeded, "old", "{}", oldResult));
        Assert.Equal(PostgreSqlWorkObservationOutcome.AlreadyTerminal, stale.Outcome);
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(winner.Content.ToArray(), await lab.ScalarAsync<byte[]>("SELECT result_payload FROM appsurface_durable.work;"));
        Assert.Equal("known_succeeded", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number=2;"));
        Assert.Equal(oldResult.Content.ToArray(), await lab.ScalarAsync<byte[]>("SELECT observation_payload FROM appsurface_durable.work_history WHERE event_type='stale_completion_succeeded';"));
    }

    [Fact]
    public async Task ClaimWaitingForWorkLock_SamplesDeadlineAfterTheWait()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var accepted = (await lab.Client.EnqueueAsync(Request("lock-wait", deadline: deadline))).Value!;
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        await using var holder = await lab.Database.DataSource.OpenConnectionAsync();
        await using var held = await holder.BeginTransactionAsync();
        await using var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id','execution-tests',true);", holder, held);
        await scope.ExecuteNonQueryAsync();
        await using var locked = new NpgsqlCommand("SELECT work_id FROM appsurface_durable.work WHERE work_id=@work FOR UPDATE;", holder, held);
        locked.Parameters.AddWithValue("work", accepted.WorkId.Value);
        Assert.Equal(accepted.WorkId.Value, await locked.ExecuteScalarAsync());
        var claiming = lab.Store.TryClaimAsync(candidate, "waiting-worker").AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await lab.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE datname=pg_catalog.current_database() AND cardinality(pg_catalog.pg_blocking_pids(pid))>0);"))
        {
            Assert.False(claiming.IsCompleted, "The claim should be waiting for the held Work lock.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
        await lab.Database.SetExecutionTimeAsync(deadline);
        await held.CommitAsync();
        Assert.Null(await claiming.WaitAsync(timeout.Token));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, false, false)]
    [InlineData(DurableProviderSafety.Idempotent, true, false)]
    [InlineData(DurableProviderSafety.Idempotent, false, true)]
    [InlineData(DurableProviderSafety.Idempotent, true, true)]
    [InlineData(DurableProviderSafety.ProviderKeyed, false, false)]
    [InlineData(DurableProviderSafety.ProviderKeyed, true, false)]
    [InlineData(DurableProviderSafety.ProviderKeyed, false, true)]
    [InlineData(DurableProviderSafety.ProviderKeyed, true, true)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, false)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, true, false)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, true)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, true, true)]
    [InlineData(DurableProviderSafety.ManualResolution, false, false)]
    [InlineData(DurableProviderSafety.ManualResolution, true, false)]
    [InlineData(DurableProviderSafety.ManualResolution, false, true)]
    [InlineData(DurableProviderSafety.ManualResolution, true, true)]
    public async Task Cancellation_PreservesAdmissionEvidenceAtAndBeforeDeadline(
        DurableProviderSafety safety, bool admitted, bool atDeadline)
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(Request("cancel", safety, deadline));
        if (admitted) Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        if (atDeadline) await lab.Database.SetExecutionTimeAsync(deadline);
        var canceled = await lab.Store.RequestCancellationAsync(permit.Claim.ScopeId, permit.Claim.WorkId,
            "tests", "request-cancel", permit.Claim.Revision);
        Assert.Equal(PostgreSqlCancellationOutcome.Applied, canceled.Outcome);
        Assert.Equal(admitted ? atDeadline ? DurableWorkState.Suspended : DurableWorkState.CancelPending
            : DurableWorkState.CanceledBeforeEffect, canceled.State);
        var expectedStoredState = admitted && atDeadline
            ? safety switch
            {
                DurableProviderSafety.ReconcileBeforeRetry => "suspended_reconciliation_required",
                DurableProviderSafety.ManualResolution => "suspended_manual_resolution",
                _ => "suspended_ambiguous_external_outcome",
            }
            : admitted ? "cancel_pending" : "canceled_before_effect";
        Assert.Equal(expectedStoredState, await lab.TextAsync("state"));
        Assert.Equal(admitted ? 0 : 1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE status='proven_no_effect';"));
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='cancellation_requested';"));
        if (atDeadline) Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
    }

    [Fact]
    public async Task CancellationProjectionFailure_RollsBackClosureAndEveryAuthoritativeRecord()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(Request("cancel-projection-rollback", deadline: deadline));
        await lab.Database.SetExecutionTimeAsync(deadline);
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var historyCount = await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;");
        var projectionInvoked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await lab.Store.RequestCancellationAsync(permit.Claim.ScopeId, permit.Claim.WorkId,
                "tests", "projection-failure", permit.Claim.Revision,
                onProjectionApplied: (_, _, _) =>
                {
                    projectionInvoked = true;
                    throw new InvalidOperationException("projection failed");
                }));

        Assert.True(projectionInvoked);
        Assert.Equal("effect_permitted", await lab.TextAsync("state"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(historyCount, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("""
            SELECT count(*) FROM appsurface_durable.work
            WHERE cancellation_requested_at IS NOT NULL OR terminal_at IS NOT NULL OR terminal_code IS NOT NULL
                OR execution_admission_closed_at IS NOT NULL OR execution_admission_closed_reason IS NOT NULL
                OR execution_deadline_reached_at IS NOT NULL;
            """));
        Assert.Equal("granted", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE lease_owner IS NULL OR lease_started_at IS NULL OR lease_expires_at IS NULL;"));
        Assert.Equal("leased", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT expected_revision FROM appsurface_durable.dispatch;"));
    }

    [Fact]
    public async Task StaleCancellationAndForgedIdentity_MutateNoWinningTruth()
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("identity"));
        var revision = permit.Claim.Revision;
        var refused = await lab.Store.RequestCancellationAsync(permit.Claim.ScopeId, permit.Claim.WorkId,
            "tests", "cancel", revision - 1);
        Assert.Equal(PostgreSqlCancellationOutcome.RevisionConflict, refused.Outcome);
        foreach (var forged in new[]
        {
            permit.Claim with { LeaseGeneration = permit.Claim.LeaseGeneration + 1 },
            permit.Claim with { ScopeGeneration = permit.Claim.ScopeGeneration + 1 },
            permit.Claim with { RuntimeEpoch = Guid.NewGuid() },
            permit.Claim with { LeaseOwner = "other" },
            permit.Claim with { AttemptNumber = permit.Claim.AttemptNumber + 1 },
        })
        {
            Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(forged));
            Assert.Null(await lab.Store.RenewLeaseAsync(forged));
            Assert.False(await lab.Store.TryAdmitInvocationAsync(permit with { Claim = forged }));
        }
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE cancellation_requested_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
    }

    [Fact]
    public async Task RemovingSnapshotFromOptedInClaim_DoesNotBypassPersistedInvocationCutoff()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(Request("snapshot", deadline: deadline));
        await lab.Database.SetExecutionTimeAsync(deadline);
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit with { Claim = permit.Claim with { Execution = null } }));
        Assert.Equal("failed", await lab.TextAsync("state"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
    }

    [Fact]
    public async Task ConcurrentIdenticalAcceptances_FreezeExactlyOneWorkDispatchAndHistory()
    {
        await using var lab = await Lab.CreateAsync();
        var request = Request("concurrent");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ => await lab.Client.EnqueueAsync(request)));
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Problem?.Problem));
        Assert.Single(results, result => result.Value!.Kind == DurableWorkAcceptanceKind.Accepted);
        Assert.Single(results.Select(result => result.Value!.WorkId).Distinct());
        Assert.All(results, result => Assert.Equal(Anchor, result.Value!.AcceptedAtUtc));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.dispatch;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
    }

    [Fact]
    public async Task TimestampOverflow_RejectsWithoutPoisoningTheCaller()
    {
        await using var lab = await Lab.CreateAsync();
        await lab.Database.SetExecutionTimeAsync(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero));
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var overflow = await lab.Writer.EnqueueAsync(transaction, Request("overflow"));
        Assert.Equal(DurableProblemCodes.ValidationFailed, overflow.Problem!.Code);
        await using var domain = new NpgsqlCommand("CREATE TABLE domain_fact(value integer); INSERT INTO domain_fact VALUES(1);", connection, transaction);
        await domain.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM domain_fact;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work;"));
    }

    [Fact]
    public async Task SelectedDiscovery_DrainsExpiredFutureDueWorkInBoundedPagesAndSkipsOtherContracts()
    {
        await using var lab = await Lab.CreateAsync();
        var safe = DurableProviderSafety.ProviderKeyed;
        var policy = DurableWorkExecutionPolicy.FromRetryPolicy(Retry(5));
        var selectedRegistry = PostgreSqlTestWorkContracts.CreateRegistry(new PostgreSqlTestWorkContract(
            PostgreSqlTestWorkContracts.DeleteProviderAccessName(safe), "v1", safe, "tests.delete-provider-access", "v1"));
        var selection = new PostgreSqlDurableWorkContractSelection(selectedRegistry);
        for (var index = 0; index < 24; index++)
        {
            var request = DurableWorkRequest.CreateWithExecutionPolicy(new("execution-tests"), new($"expired-{index}"), $"key-{index}",
                PostgreSqlTestWorkContracts.DeleteProviderAccessName(safe), "v1", Payload(), safe, policy,
                new(Anchor.AddMinutes(1)), Anchor.AddHours(1));
            Assert.True((await lab.Client.EnqueueAsync(request)).IsSuccess);
        }
        await lab.Client.EnqueueAsync(Request("other-contract"));
        Assert.Empty(await lab.Store.DiscoverAsync(selection, 7));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));
        var drained = new HashSet<DurableWorkId>();
        for (var page = 0; page < 4; page++)
        {
            var candidates = await lab.Store.DiscoverAsync(selection, 7);
            Assert.InRange(candidates.Count, 1, 7);
            foreach (var candidate in candidates)
            {
                Assert.True(drained.Add(candidate.WorkId));
                Assert.Equal(Anchor.AddHours(1), candidate.DueAtUtc);
                Assert.Null(await lab.Store.TryClaimAsync(candidate, "drain-worker"));
            }
        }
        Assert.Equal(24, drained.Count);
        Assert.Empty(await lab.Store.DiscoverAsync(selection, 7));
        Assert.Single(await lab.Store.DiscoverAsync(7));
        Assert.Equal(24, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.dispatch WHERE state='terminal' AND execution_discovery_at IS NULL;"));
        Assert.Equal(24, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE terminal_code='ASDUR121' AND attempt_number=0;"));
    }

    [Fact]
    public async Task AdmissionClosureDuringLiveCircuit_ChangesOnlyAdmissionAndDoesNotSpinDiscovery()
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("live-circuit", circuitMinutes: 2, offsets: [0]));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(2));
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        Assert.Null(await lab.Store.TryClaimAsync(candidate, "closure-observer"));
        Assert.Equal("failed", await lab.TextAsync("state"));
        Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentlyExpiredLease_CannotCloseOrMutateOwningWork(bool admitted)
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("lost-lease", deadline: Anchor.AddMinutes(60), circuitMinutes: 40, offsets: [0]));
        if (admitted) Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(61));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var historyCount = await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;");
        Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(permit.Claim));
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit));
        Assert.Null(await lab.Store.RenewLeaseAsync(permit.Claim));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(historyCount, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal("effect_permitted", await lab.TextAsync("state"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
        Assert.Equal(admitted ? 1 : 0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal("granted", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
    }

    [Fact]
    public async Task DisabledScopeAndStaleDispatch_RefuseBeforeAnyDeadlineMutation()
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("disabled", deadline: Anchor.AddMinutes(1)));
        var candidate = new PostgreSqlDispatchCandidate(permit.Claim.DispatchId, permit.Claim.ScopeId, permit.Claim.WorkId,
            Anchor, permit.Claim.Revision, 0);
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(2));
        Assert.Null(await lab.Store.TryClaimAsync(candidate with { ExpectedRevision = candidate.ExpectedRevision - 1 }, "stale"));
        await using var disable = lab.Database.DataSource.CreateCommand("UPDATE appsurface_durable.scope SET state='disabled',generation=generation+1 WHERE scope_id='execution-tests';");
        await disable.ExecuteNonQueryAsync();
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        Assert.Null(await lab.Store.TryClaimAsync(candidate, "disabled"));
        Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(permit.Claim));
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit));
        Assert.Null(await lab.Store.RenewLeaseAsync(permit.Claim));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
    }

    [Fact]
    public async Task PermanentlyObservedDeadline_QuarantinesSuccessAfterClockMovesBackward()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(Request("backward-success", deadline: deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(deadline);
        Assert.Null(await lab.Store.RenewLeaseAsync(permit.Claim));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddSeconds(30));
        var result = new DurableEncodedPayload("tests.result", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 9 });
        var observation = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.Succeeded, "local_success", "{}", result));
        Assert.Equal(DurableWorkState.Suspended, observation.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, await lab.TextAsync("terminal_code"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
        Assert.Equal(result.Content.ToArray(), await lab.ScalarAsync<byte[]>("SELECT observation_payload FROM appsurface_durable.work_history WHERE event_type='late_completion_succeeded';"));
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_admission_closed_at FROM appsurface_durable.work;"));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent)]
    [InlineData(DurableProviderSafety.ProviderKeyed)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry)]
    [InlineData(DurableProviderSafety.ManualResolution)]
    public async Task CircuitClosureBeforeDeadline_StillQuarantinesSuccessAfterObservedDeadlineAndClockReversal(DurableProviderSafety safety)
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(20);
        var permit = await lab.AdmitReadyAsync(Request("circuit-then-deadline", safety, deadline, circuitMinutes: 10, offsets: [0, 5]));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(10));
        Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(permit.Claim));
        Assert.Equal("circuit_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        await lab.Database.SetExecutionTimeAsync(deadline);
        Assert.Null(await lab.Store.RenewLeaseAsync(permit.Claim));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(15));
        var result = new DurableEncodedPayload("tests.result", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 9 });
        var observation = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.Succeeded, "local_success", "{}", result));
        Assert.Equal(DurableWorkState.Suspended, observation.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, await lab.TextAsync("terminal_code"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
        Assert.Equal(result.Content.ToArray(), await lab.ScalarAsync<byte[]>("SELECT observation_payload FROM appsurface_durable.work_history WHERE event_type='late_completion_succeeded';"));
        Assert.Equal(Anchor.AddMinutes(10).UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_admission_closed_at FROM appsurface_durable.work;"));
        Assert.Equal("circuit_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_deadline_reached_at FROM appsurface_durable.work;"));
        Assert.Null(await lab.Store.RenewLeaseAsync(permit.Claim));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Fact]
    public async Task ExactInvocationReplayAtDeadline_PreservesDeadlineObservationAfterClockReversal()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(20);
        var permit = await lab.AdmitReadyAsync(Request("deadline-replay", deadline: deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(deadline);
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(15));
        var result = new DurableEncodedPayload("tests.result", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 9 });
        var observed = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.Succeeded, "local_success", "{}", result));
        Assert.Equal(DurableWorkState.Suspended, observed.State);
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_deadline_reached_at FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Fact]
    public async Task DelayedAcceptanceCommit_ConsumesWindowAndRollbackRemovesDomainAndWork()
    {
        await using var lab = await Lab.CreateAsync();
        await using (var connection = await lab.Database.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using var domain = new NpgsqlCommand("CREATE TABLE domain_fact(value integer); INSERT INTO domain_fact VALUES(1);", connection, transaction);
            await domain.ExecuteNonQueryAsync();
            var accepted = await lab.Writer.EnqueueAsync(transaction, Request("delayed", deadline: Anchor.AddMinutes(1)));
            Assert.True(accepted.IsSuccess);
            Assert.Equal(Anchor, accepted.Value!.AcceptedAtUtc);
            await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(2));
            await transaction.CommitAsync();
        }
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        Assert.Null(await lab.Store.TryClaimAsync(candidate, "delayed-worker"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM domain_fact;"));
        Assert.Equal(Anchor.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT accepted_at FROM appsurface_durable.work;"));
        Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, await lab.TextAsync("terminal_code"));
        await lab.Database.SetExecutionTimeAsync(Anchor);
        await using (var connection = await lab.Database.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using var domain = new NpgsqlCommand("INSERT INTO domain_fact VALUES(2);", connection, transaction);
            await domain.ExecuteNonQueryAsync();
            Assert.True((await lab.Writer.EnqueueAsync(transaction, Request("rolled-back"))).IsSuccess);
            await transaction.RollbackAsync();
        }
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM domain_fact;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.dispatch;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='accepted';"));
    }

    [Fact]
    public async Task ThrowingMetricListener_CannotPreventAuthoritativeDeadlineTransition()
    {
        await using var lab = await Lab.CreateAsync();
        await lab.Client.EnqueueAsync(Request("observer", deadline: Anchor.AddMinutes(1)));
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(10));
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Name == "durable.work.execution_fenced") current.EnableMeasurementEvents(instrument);
        };
        var invoked = false;
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            invoked = true;
            throw new InvalidOperationException("observer failed");
        });
        listener.Start();
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));
        Assert.Null(await lab.Store.TryClaimAsync(candidate, "observer-worker"));
        Assert.True(invoked);
        Assert.Equal("failed", await lab.TextAsync("state"));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Fact]
    public async Task LaterNoEffect_CannotEraseUnknownEffectFromAnEarlierAdmittedAttempt()
    {
        await using var lab = await Lab.CreateAsync();
        var original = await lab.AdmitReadyAsync(Request("prior-effect", deadline: Anchor.AddMinutes(40)));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(original));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(31));
        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(10)), "next-worker"))!;
        var permit = (await lab.Store.TryAcquireEffectPermitAsync(claim))!;
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(40));
        var observed = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.ProvenNoEffect, "no_current_effect", "{}"));
        Assert.Equal(DurableWorkState.Suspended, observed.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, await lab.TextAsync("terminal_code"));
        Assert.Equal("granted", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number=1;"));
        Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number=2;"));
        Assert.Empty(await lab.Store.DiscoverAsync(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OwningBoundaryWaitingForWorkLock_SamplesDeadlineAfterTheWait(int boundary)
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(Request("post-lock", deadline: deadline));
        if (boundary == 3) Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await using var holder = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using var hold = new NpgsqlCommand("SELECT work_id FROM appsurface_durable.work WHERE work_id=@work FOR UPDATE;", holder, transaction);
        hold.Parameters.AddWithValue("work", permit.Claim.WorkId.Value);
        Assert.Equal(permit.Claim.WorkId.Value, await hold.ExecuteScalarAsync());
        var localResult = new DurableEncodedPayload("tests.result", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 42 });
        Task deciding = boundary switch
        {
            0 => lab.Store.TryAcquireEffectPermitAsync(permit.Claim).AsTask(),
            1 => lab.Store.TryAdmitInvocationAsync(permit).AsTask(),
            2 => lab.Store.RenewLeaseAsync(permit.Claim).AsTask(),
            _ => lab.Store.RecordCompletionAsync(permit.Claim,
                new(PostgreSqlWorkCompletionKind.Succeeded, "local_success", "{}", localResult)).AsTask(),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await lab.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE datname=pg_catalog.current_database() AND cardinality(pg_catalog.pg_blocking_pids(pid))>0);"))
        {
            Assert.False(deciding.IsCompleted, "The owning decision should be waiting for the held Work lock.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
        await lab.Database.SetExecutionTimeAsync(deadline);
        await transaction.CommitAsync();
        await deciding.WaitAsync(timeout.Token);
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_admission_closed_at FROM appsurface_durable.work;"));
        Assert.Equal(boundary == 3 ? 1 : 0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
        if (boundary == 3)
        {
            Assert.Equal("suspended_ambiguous_external_outcome", await lab.TextAsync("state"));
            Assert.Equal(localResult.Content.ToArray(), await lab.ScalarAsync<byte[]>("SELECT observation_payload FROM appsurface_durable.work_history WHERE event_type='late_completion_succeeded';"));
        }
        else if (boundary != 2)
        {
            Assert.Equal("failed", await lab.TextAsync("state"));
            Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        }
    }

    [Theory]
    [InlineData((int)PostgreSqlWorkCompletionKind.ContractUnavailable, "suspended_contract_unavailable")]
    [InlineData((int)PostgreSqlWorkCompletionKind.FailedTerminal, "failed")]
    public async Task PreparationOrTerminalFailureBeforePermit_PreservesNoEffectAndOriginalObservation(
        int kindValue, string state)
    {
        var kind = (PostgreSqlWorkCompletionKind)kindValue;
        await using var lab = await Lab.CreateAsync();
        var request = Request("pre-effect-completion");
        var accepted = (await lab.Client.EnqueueAsync(request)).Value!;
        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;
        var completion = await lab.Store.RecordCompletionAsync(claim, new(kind, "reported_failure", "{}"));
        Assert.Equal(kind == PostgreSqlWorkCompletionKind.ContractUnavailable ? DurableWorkState.Suspended : DurableWorkState.FailedTerminal, completion.State);
        Assert.Equal(state, await lab.TextAsync("state"));
        Assert.Equal("reported_failure", await lab.TextAsync("terminal_code"));
        Assert.Equal(accepted.AcceptedAtUtc.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT due_at FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit;"));
        Assert.Empty(await lab.Store.DiscoverAsync(1));
    }

    [Fact]
    public async Task UnpermittedSuccess_IsObservationOnlyAndExactReplayIsIdempotent()
    {
        await using var lab = await Lab.CreateAsync();
        var accepted = (await lab.Client.EnqueueAsync(Request("success-without-permit"))).Value!;
        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var result = new DurableEncodedPayload("tests.delete-provider-access.result", "v1",
            DurableDataClassification.ApprovedApplication, new byte[] { 17, 18, 19 });
        var completion = new PostgreSqlWorkCompletion(PostgreSqlWorkCompletionKind.Succeeded,
            "unpermitted_success", "{}", result);

        var first = await lab.Store.RecordCompletionAsync(claim, completion);

        Assert.Equal(PostgreSqlWorkObservationOutcome.StaleObservation, first.Outcome);
        Assert.Equal(revision, first.Revision);
        Assert.Equal("leased", await lab.TextAsync("state"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit;"));
        Assert.Equal(accepted.AcceptedAtUtc.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT accepted_at FROM appsurface_durable.work;"));
        Assert.Equal("leased", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT expected_revision FROM appsurface_durable.dispatch;"));
        Assert.Equal(result.Content.ToArray(), await lab.ScalarAsync<byte[]>("SELECT observation_payload FROM appsurface_durable.work_history WHERE event_type='stale_completion_succeeded';"));

        var replay = await lab.Store.RecordCompletionAsync(claim, completion);

        Assert.Equal(PostgreSqlWorkObservationOutcome.StaleObservation, replay.Outcome);
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='stale_completion_succeeded';"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
        Assert.Equal("leased", await lab.TextAsync("state"));
    }

    [Fact]
    public async Task DeadlineOnlySafeRetry_RetainsLegacyBackoffAndCapsRenewalAtOriginalLeaseLifetime()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddHours(4);
        var request = DurableWorkRequest.CreateWithExecutionPolicy(new("execution-tests"), new("deadline-backoff"), "deadline-backoff-key",
            PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent), "v1", Payload(), DurableProviderSafety.Idempotent,
            DurableWorkExecutionPolicy.FromRetryPolicy(Retry(5)), new(deadline));
        await lab.Client.EnqueueAsync(request);
        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(2));
        var retried = await lab.Store.RecordCompletionAsync(claim, new(PostgreSqlWorkCompletionKind.Retry, "pre-effect-retry", "{}"));
        Assert.Equal(Anchor.AddMinutes(2).AddSeconds(1), retried.NextDueAtUtc);
        await lab.Database.SetExecutionTimeAsync(retried.NextDueAtUtc!.Value);
        var second = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;
        for (var minute = 20; minute <= 100; minute += 20)
        {
            await lab.Database.SetExecutionTimeAsync(second.LeaseStartedAtUtc.AddMinutes(minute));
            second = (await lab.Store.RenewLeaseAsync(second))!;
            Assert.NotNull(second);
        }
        Assert.Equal(second.LeaseStartedAtUtc.AddHours(2), second.LeaseExpiresAtUtc);
        await lab.Database.SetExecutionTimeAsync(second.LeaseExpiresAtUtc);
        Assert.Null(await lab.Store.RenewLeaseAsync(second));
        Assert.Equal(2, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_not_after FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
    }

    [Fact]
    public async Task DeadlineOnlyMaximumElapsedCutoff_ClosesBeforeAnotherAttemptIsClaimed()
    {
        await using var lab = await Lab.CreateAsync();
        var maximumElapsed = TimeSpan.FromMinutes(5);
        var elapsedCutoff = Anchor.Add(maximumElapsed);
        var deadline = Anchor.AddHours(1);
        var retry = new DurableWorkRetryPolicy(5, maximumElapsed, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), "exponential-v1");
        var request = DurableWorkRequest.CreateWithExecutionPolicy(new("execution-tests"), new("elapsed-only-cutoff"),
            "key-elapsed-only-cutoff", PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent),
            "v1", Payload(), DurableProviderSafety.Idempotent, DurableWorkExecutionPolicy.FromRetryPolicy(retry), new DurableExecutionDeadline(deadline));
        var accepted = await lab.Client.EnqueueAsync(request);
        Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);
        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));
        var retryWait = await lab.Store.RecordCompletionAsync(claim,
            new(PostgreSqlWorkCompletionKind.ProvenNoEffect, "proven_no_effect", "{}"));
        Assert.Equal(DurableWorkState.Ready, retryWait.State);
        Assert.Equal(Anchor.AddMinutes(1).AddSeconds(1), retryWait.NextDueAtUtc);

        await lab.Database.SetExecutionTimeAsync(elapsedCutoff);
        var due = Assert.Single(await lab.Store.DiscoverAsync(1));
        Assert.Null(await lab.Store.TryClaimAsync(due, "elapsed-cutoff-worker"));

        Assert.Equal("failed", await lab.TextAsync("state"));
        Assert.Equal("retry_policy_exhausted", await lab.TextAsync("terminal_code"));
        Assert.Equal("elapsed_exhausted", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(elapsedCutoff.UtcDateTime,
            await lab.ScalarAsync<DateTime>("SELECT execution_admission_closed_at FROM appsurface_durable.work;"));
        Assert.Equal(deadline.UtcDateTime,
            await lab.ScalarAsync<DateTime>("SELECT execution_not_after FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_deadline_reached_at IS NOT NULL;"));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal("terminal", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='invocation_admitted';"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='execution_transition' AND details->>'reason'='elapsed_exhausted';"));
    }

    [Fact]
    public async Task HugeLeaseDuration_IsCappedAtDeadlineForClaimAndRenewal()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(30);
        var hugeDuration = TimeSpan.FromDays(4_000_000);
        var retry = new DurableWorkRetryPolicy(5, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1),
            hugeDuration, TimeSpan.FromSeconds(1), hugeDuration, "exponential-v1");
        await lab.Client.EnqueueAsync(DeadlineOnlyRequest("huge-lease-deadline", deadline, retry));

        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;

        Assert.Equal(deadline, claim.LeaseExpiresAtUtc);
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT lease_expires_at FROM appsurface_durable.work;"));
        Assert.Equal("leased", await lab.TextAsync("state"));
        Assert.Equal("leased", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));

        await lab.Database.SetExecutionTimeAsync(Anchor.AddSeconds(1));
        var renewed = await lab.Store.RenewLeaseAsync(claim);

        Assert.NotNull(renewed);
        Assert.Equal(deadline, renewed!.LeaseExpiresAtUtc);
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT lease_expires_at FROM appsurface_durable.work;"));
        Assert.Equal(await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"),
            await lab.ScalarAsync<long>("SELECT expected_revision FROM appsurface_durable.dispatch;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='lease_renewed';"));
    }

    [Fact]
    public async Task HugeMaximumLeaseLifetime_DoesNotOverflowShortLeaseRenewal()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(30);
        var hugeLifetime = TimeSpan.FromDays(4_000_000);
        var retry = new DurableWorkRetryPolicy(5, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1), hugeLifetime, "exponential-v1");
        await lab.Client.EnqueueAsync(DeadlineOnlyRequest("huge-max-lifetime", deadline, retry));
        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;
        Assert.Equal(Anchor.AddMinutes(2), claim.LeaseExpiresAtUtc);

        var renewedAt = Anchor.AddSeconds(1);
        await lab.Database.SetExecutionTimeAsync(renewedAt);
        var renewed = await lab.Store.RenewLeaseAsync(claim);

        Assert.NotNull(renewed);
        Assert.Equal(renewedAt.AddMinutes(2), renewed!.LeaseExpiresAtUtc);
        Assert.Equal(renewedAt.AddMinutes(2).UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT lease_expires_at FROM appsurface_durable.work;"));
        Assert.Equal("leased", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal(await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"),
            await lab.ScalarAsync<long>("SELECT expected_revision FROM appsurface_durable.dispatch;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='lease_renewed';"));
    }

    [Fact]
    public async Task HugeDeadlineOnlyRetryDelay_IsCappedAtCutoffAndCannotAdmitAtCutoff()
    {
        await using var lab = await Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(30);
        var hugeDelay = TimeSpan.FromDays(4_000_000);
        var retry = new DurableWorkRetryPolicy(5, TimeSpan.FromHours(1), hugeDelay, hugeDelay,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(20), "exponential-v1");
        await lab.Client.EnqueueAsync(DeadlineOnlyRequest("huge-retry-delay", deadline, retry));
        var claim = (await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "worker"))!;
        var permit = (await lab.Store.TryAcquireEffectPermitAsync(claim))!;
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));

        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));
        var retried = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.ProvenNoEffect, "proven_no_effect", "{}"));

        Assert.Equal(DurableWorkState.Ready, retried.State);
        Assert.Equal(deadline, retried.NextDueAtUtc);
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT due_at FROM appsurface_durable.work;"));
        Assert.Equal("retry_wait", await lab.TextAsync("state"));
        Assert.Equal("available", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='invocation_admitted';"));

        await lab.Database.SetExecutionTimeAsync(deadline);
        var dueCandidate = Assert.Single(await lab.Store.DiscoverAsync(1));
        Assert.Null(await lab.Store.TryClaimAsync(dueCandidate, "next-worker"));

        Assert.Equal("failed", await lab.TextAsync("state"));
        Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, await lab.TextAsync("terminal_code"));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_admission_closed_at FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal("terminal", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='invocation_admitted';"));
    }

    [Fact]
    public async Task CurrentAdmittedSuccessAfterCancellation_RecordsSuccessAfterCancellationAndReplayIsTerminal()
    {
        await using var lab = await Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(Request("cancel-success"));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        var canceled = await lab.Store.RequestCancellationAsync(permit.Claim.ScopeId, permit.Claim.WorkId,
            "authorized-test", "cancel", permit.Claim.Revision);
        Assert.Equal(DurableWorkState.CancelPending, canceled.State);
        var result = new DurableEncodedPayload("tests.delete-provider-access.result", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 42 });
        var completion = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.Succeeded, "observed_success", "{}", result));
        Assert.Equal(DurableWorkState.SucceededAfterCancelRequested, completion.State);
        Assert.Equal(result.Content.ToArray(), await lab.ScalarAsync<byte[]>("SELECT result_payload FROM appsurface_durable.work;"));
        var replay = await lab.Store.RequestCancellationAsync(permit.Claim.ScopeId, permit.Claim.WorkId,
            "authorized-test", "cancel", completion.Revision);
        Assert.Equal(PostgreSqlCancellationOutcome.AlreadyTerminal, replay.Outcome);
        Assert.Equal(completion.Revision, replay.Revision);
        Assert.Equal("known_succeeded", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
    }

    internal static DurableWorkRequest Request(string command, DurableProviderSafety safety = DurableProviderSafety.Idempotent,
        DateTimeOffset? deadline = null, int circuitMinutes = 240, int[]? offsets = null)
    {
        offsets ??= [0, 5, 20, 60, 180];
        var plan = new DurableAttemptPlan("attempt-plan-v1", offsets.Select(static offset => TimeSpan.FromMinutes(offset)), TimeSpan.FromMinutes(circuitMinutes));
        return DurableWorkRequest.CreateWithExecutionPolicy(new("execution-tests"), new(command), $"key-{command}",
            PostgreSqlTestWorkContracts.DeleteProviderAccessName(safety), "v1", Payload(), safety,
            DurableWorkExecutionPolicy.ForAttemptPlan(Retry(offsets.Length), plan), deadline is { } value ? new(value) : null);
    }

    private static DurableEncodedPayload Payload() => new("tests.delete-provider-access", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 1, 2, 3 });
    private static DurableWorkRequest DeadlineOnlyRequest(string command, DateTimeOffset deadline, DurableWorkRetryPolicy retry) =>
        DurableWorkRequest.CreateWithExecutionPolicy(new("execution-tests"), new(command), $"key-{command}",
            PostgreSqlTestWorkContracts.DeleteProviderAccessName(DurableProviderSafety.Idempotent), "v1", Payload(), DurableProviderSafety.Idempotent,
            DurableWorkExecutionPolicy.FromRetryPolicy(retry), new(deadline));
    private static DurableWorkRetryPolicy Retry(int attempts) => new(attempts, TimeSpan.FromDays(1), TimeSpan.FromSeconds(1),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1), TimeSpan.FromHours(2), "exponential-v1");

    internal sealed class Lab(PostgreSqlIntegrationTestDatabase database, PostgreSqlDurableWorkClient client,
        PostgreSqlDurableWorkTransactionWriter writer, PostgreSqlDurableWorkStore store, Guid epoch, IDurableWorkRegistry registry) : IAsyncDisposable
    {
        internal Guid Epoch { get; } = epoch;
        internal IDurableWorkRegistry Registry { get; } = registry;
        internal PostgreSqlIntegrationTestDatabase Database { get; } = database;
        internal PostgreSqlDurableWorkClient Client { get; } = client;
        internal PostgreSqlDurableWorkTransactionWriter Writer { get; } = writer;
        internal PostgreSqlDurableWorkStore Store { get; } = store;

        internal static async Task<Lab> CreateAsync(IDurableWorkRegistry? suppliedRegistry = null)
        {
            var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
            await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
            await database.SetExecutionTimeAsync(Anchor);
            var epoch = Guid.NewGuid();
            await using var initialize = database.DataSource.CreateCommand("UPDATE appsurface_durable.store_metadata SET active_runtime_epoch=@epoch RETURNING store_id;");
            initialize.Parameters.AddWithValue("epoch", epoch);
            var storeId = (Guid)(await initialize.ExecuteScalarAsync())!;
            var options = new PostgreSqlDurableWorkOptions(epoch, storeId);
            var registry = suppliedRegistry ?? PostgreSqlTestWorkContracts.CreateDeleteProviderAccessRegistry();
            return new(database, new(database.DataSource, registry, options), new(database.DataSource, registry, options), new(database.DataSource, epoch), epoch, registry);
        }

        internal async Task<PostgreSqlEffectPermit> AdmitReadyAsync(DurableWorkRequest request)
        {
            var accepted = await Client.EnqueueAsync(request);
            Assert.True(accepted.IsSuccess, accepted.Problem?.Problem);
            var claim = await Store.TryClaimAsync(Assert.Single(await Store.DiscoverAsync(10)), "worker");
            return (await Store.TryAcquireEffectPermitAsync(claim!))!;
        }

        internal Task<string> TextAsync(string column) => ScalarAsync<string>($"SELECT {column} FROM appsurface_durable.work;");
        internal async Task<T> ScalarAsync<T>(string sql)
        {
            await using var connection = await Database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id','execution-tests',true);", connection, transaction);
            await scope.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
