namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

/// <summary>Real PostgreSQL regressions for opted-in claim and completion transition fences.</summary>
public sealed class PostgreSqlDurableExecutionTransitionCoverageTests
{
    private static readonly DateTimeOffset Anchor = PostgreSqlDurableWorkExecutionPolicyTests.Anchor;

    [Fact]
    public async Task Claim_RefusesStaleDispatchAndRevisionCandidatesWithoutChangingWork()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var request = PostgreSqlDurableWorkExecutionPolicyTests.Request("transition-stale-candidate");
        Assert.True((await lab.Client.EnqueueAsync(request)).IsSuccess);
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(1));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");

        Assert.Null(await lab.Store.TryClaimAsync(candidate with { ExpectedRevision = candidate.ExpectedRevision + 1 }, "stale-revision"));
        Assert.Null(await lab.Store.TryClaimAsync(candidate with { DispatchId = Guid.NewGuid() }, "stale-dispatch"));

        Assert.Equal("pending", await lab.TextAsync("state"));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT expected_revision FROM appsurface_durable.dispatch;"));
        var remaining = Assert.Single(await lab.Store.DiscoverAsync(10));
        Assert.Equal(candidate.DispatchId, remaining.DispatchId);
        Assert.Equal(candidate.ExpectedRevision, remaining.ExpectedRevision);
    }

    [Fact]
    public async Task Claim_RefusesActiveScopeGenerationThatNoLongerMatchesAcceptedWork()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var request = PostgreSqlDurableWorkExecutionPolicyTests.Request("transition-stale-scope-generation");
        Assert.True((await lab.Client.EnqueueAsync(request)).IsSuccess);
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(1));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");

        await using var advanceScope = lab.Database.DataSource.CreateCommand("""
            UPDATE appsurface_durable.scope SET generation = generation + 1
            WHERE scope_id = 'execution-tests' AND state = 'active';
            """);
        Assert.Equal(1, await advanceScope.ExecuteNonQueryAsync());

        Assert.Null(await lab.Store.TryClaimAsync(candidate, "stale-scope-worker"));

        Assert.Equal("pending", await lab.TextAsync("state"));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_at IS NOT NULL;"));
        Assert.Equal("available", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
    }

    [Fact]
    public async Task Claim_DoesNotReclaimCurrentAttemptWhileItsLeaseIsUnexpired()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PostgreSqlDurableWorkExecutionPolicyTests.Request("transition-live-lease"));
        var claim = permit.Claim;
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var currentCandidate = new PostgreSqlDispatchCandidate(claim.DispatchId, claim.ScopeId, claim.WorkId,
            Anchor, claim.Revision, 0);

        Assert.Null(await lab.Store.TryClaimAsync(currentCandidate, "second-worker"));

        Assert.Equal("effect_permitted", await lab.TextAsync("state"));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit;"));
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT expected_revision FROM appsurface_durable.dispatch;"));
    }

    [Fact]
    public async Task Claim_PersistsAuditedSuspensionForPendingWorkFromPriorRuntimeEpoch()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var request = PostgreSqlDurableWorkExecutionPolicyTests.Request("transition-prior-epoch");
        Assert.True((await lab.Client.EnqueueAsync(request)).IsSuccess);
        var candidate = Assert.Single(await lab.Store.DiscoverAsync(1));
        var previousEpoch = Guid.NewGuid();
        await using var moveWorkToPriorEpoch = lab.Database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET runtime_epoch = @epoch WHERE scope_id = 'execution-tests' AND work_id = @work_id;");
        moveWorkToPriorEpoch.Parameters.AddWithValue("epoch", previousEpoch);
        moveWorkToPriorEpoch.Parameters.AddWithValue("work_id", candidate.WorkId.Value);
        Assert.Equal(1, await moveWorkToPriorEpoch.ExecuteNonQueryAsync());
        DurableWorkState? projectedState = null;
        string? projectedCode = null;

        Assert.Null(await lab.Store.TryClaimAsync(candidate, "new-epoch-worker",
            onTransitionApplied: (_, state, code, _) =>
            {
                projectedState = state;
                projectedCode = code;
                return ValueTask.CompletedTask;
            }));

        Assert.Equal(DurableWorkState.Suspended, projectedState);
        Assert.Equal("runtime_epoch_mismatch", projectedCode);
        Assert.Equal("suspended_manual_resolution", await lab.TextAsync("state"));
        Assert.Equal("runtime_epoch_mismatch", await lab.TextAsync("terminal_code"));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
        Assert.Equal("suspended", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='execution_transition';"));
    }

    [Theory]
    [InlineData(DurableProviderSafety.Idempotent, false, true)]
    [InlineData(DurableProviderSafety.ProviderKeyed, false, true)]
    [InlineData(DurableProviderSafety.ReconcileBeforeRetry, false, false)]
    [InlineData(DurableProviderSafety.ManualResolution, false, false)]
    [InlineData(DurableProviderSafety.Idempotent, true, false)]
    public async Task ExpiredAdmittedAttempt_ReplaysOnlyWhenSafeAndNotCanceled(
        DurableProviderSafety safety, bool cancellationRequested, bool shouldClaim)
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(120);
        var permit = await lab.AdmitReadyAsync(PostgreSqlDurableWorkExecutionPolicyTests.Request(
            $"transition-expired-{safety}-{cancellationRequested}", safety, deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        if (cancellationRequested)
        {
            var canceled = await lab.Store.RequestCancellationAsync(permit.Claim.ScopeId, permit.Claim.WorkId,
                "coverage-test", "expired-cancel", permit.Claim.Revision);
            Assert.Equal(PostgreSqlCancellationOutcome.Applied, canceled.Outcome);
        }

        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(31));
        var expiredCandidate = Assert.Single(await lab.Store.DiscoverAsync(1));
        DurableWorkState? projectedState = null;
        var recovered = await lab.Store.TryClaimAsync(expiredCandidate, "expired-worker",
            onTransitionApplied: (_, state, _, _) =>
            {
                projectedState = state;
                return ValueTask.CompletedTask;
            });

        if (shouldClaim)
        {
            Assert.NotNull(recovered);
            Assert.Equal(2, recovered!.AttemptNumber);
            Assert.Equal("leased", await lab.TextAsync("state"));
            Assert.Equal(2, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
            Assert.Null(projectedState);
            Assert.Equal("granted", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number = 1;"));
        }
        else
        {
            Assert.Null(recovered);
            Assert.Equal(DurableWorkState.Suspended, projectedState);
            var expected = safety switch
            {
                DurableProviderSafety.ReconcileBeforeRetry => "suspended_reconciliation_required",
                DurableProviderSafety.ManualResolution => "suspended_manual_resolution",
                _ => "suspended_ambiguous_external_outcome",
            };
            Assert.Equal(expected, await lab.TextAsync("state"));
            Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, await lab.TextAsync("terminal_code"));
            Assert.Equal(1, await lab.ScalarAsync<int>("SELECT attempt_number FROM appsurface_durable.work;"));
            Assert.Equal("suspended", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        }
    }

    [Fact]
    public async Task CircuitClosureDuringCanceledReplay_DoesNotErasePriorAdmittedUncertainty()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var deadline = Anchor.AddHours(3);
        var template = PostgreSqlDurableWorkExecutionPolicyTests.Request("transition-circuit-prior", deadline: deadline,
            circuitMinutes: 120, offsets: [0, 5]);
        var retry = new DurableWorkRetryPolicy(2, TimeSpan.FromHours(4), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(3), TimeSpan.FromMinutes(1), TimeSpan.FromHours(3), "exponential-v1");
        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(retry,
            new DurableAttemptPlan("attempt-plan-v1", [TimeSpan.Zero, TimeSpan.FromMinutes(5)], TimeSpan.FromMinutes(120)));
        var request = DurableWorkRequest.CreateWithExecutionPolicy(template.ScopeId, template.CommandId, template.IdempotencyKey,
            template.WorkName, template.WorkVersion, template.Payload, template.ProviderSafety, policy,
            new DurableExecutionDeadline(deadline));
        var first = await lab.AdmitReadyAsync(request);
        Assert.True(await lab.Store.TryAdmitInvocationAsync(first));
        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(1));
        var safeRetry = await lab.Store.RecordCompletionAsync(first.Claim,
            new(PostgreSqlWorkCompletionKind.Retry, "repeat-safe", "{}"));
        Assert.Equal(DurableWorkState.Ready, safeRetry.State);

        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(5));
        var secondClaim = await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(1)), "second-attempt");
        Assert.NotNull(secondClaim);
        var second = await lab.Store.TryAcquireEffectPermitAsync(secondClaim!);
        Assert.NotNull(second);
        Assert.True(await lab.Store.TryAdmitInvocationAsync(second!));
        var cancel = await lab.Store.RequestCancellationAsync(second!.Claim.ScopeId, second.Claim.WorkId,
            "coverage-test", "cancel-with-prior-uncertainty", second.Claim.Revision);
        Assert.Equal(DurableWorkState.CancelPending, cancel.State);

        await lab.Database.SetExecutionTimeAsync(Anchor.AddMinutes(120));
        var circuitCandidate = new PostgreSqlDispatchCandidate(second.Claim.DispatchId, second.Claim.ScopeId,
            second.Claim.WorkId, Anchor.AddMinutes(5), cancel.Revision, 0);
        Assert.Null(await lab.Store.TryClaimAsync(circuitCandidate, "circuit-observer"));
        Assert.Equal("cancel_pending", await lab.TextAsync("state"));
        Assert.Equal("circuit_elapsed", await lab.TextAsync("execution_admission_closed_reason"));

        var completed = await lab.Store.RecordCompletionAsync(second.Claim,
            new(PostgreSqlWorkCompletionKind.ProvenNoEffect, "current-not-applied", "{}"));

        Assert.Equal(DurableWorkState.Suspended, completed.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, await lab.TextAsync("terminal_code"));
        Assert.Equal("ambiguous", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number = 1;"));
        Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number = 2;"));
        Assert.Equal("suspended", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
    }

    [Fact]
    public async Task CanceledCurrentAttempt_ExplicitNoEffectProjectsCanceledTerminalState()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PostgreSqlDurableWorkExecutionPolicyTests.Request("transition-cancel-no-effect"));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        var canceled = await lab.Store.RequestCancellationAsync(permit.Claim.ScopeId, permit.Claim.WorkId,
            "coverage-test", "cancel-before-no-effect", permit.Claim.Revision);
        Assert.Equal(DurableWorkState.CancelPending, canceled.State);
        DurableWorkState? projectedState = null;

        var completed = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.ProvenNoEffect, "provider-proved-absent", "{}"),
            onProjectionApplied: (_, state, _) =>
            {
                projectedState = state;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(DurableWorkState.CanceledBeforeEffect, completed.State);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, projectedState);
        Assert.Equal("canceled_before_effect", await lab.TextAsync("terminal_code"));
        Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal("terminal", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
    }

    [Fact]
    public async Task DeadlineCappedLease_AllowsExactCutoffNoEffectProofAndClosesTerminally()
    {
        await using var lab = await PostgreSqlDurableWorkExecutionPolicyTests.Lab.CreateAsync();
        var deadline = Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(PostgreSqlDurableWorkExecutionPolicyTests.Request("transition-cutoff-no-effect", deadline: deadline));
        Assert.Equal(deadline, permit.Claim.LeaseExpiresAtUtc);
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(deadline);
        DurableWorkState? projectedState = null;

        var completed = await lab.Store.RecordCompletionAsync(permit.Claim,
            new(PostgreSqlWorkCompletionKind.ProvenNoEffect, "deadline-no-effect", "{}"),
            onProjectionApplied: (_, state, _) =>
            {
                projectedState = state;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(DurableWorkState.FailedTerminal, completed.State);
        Assert.Equal(DurableWorkState.FailedTerminal, projectedState);
        Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, await lab.TextAsync("terminal_code"));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_deadline_reached_at FROM appsurface_durable.work;"));
        Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal("terminal", await lab.ScalarAsync<string>("SELECT state FROM appsurface_durable.dispatch;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE result_payload IS NOT NULL;"));
    }
}
