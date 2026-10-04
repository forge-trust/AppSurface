using Npgsql;
using PolicyTests = ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkExecutionPolicyTests;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableInvocationBoundaryCoverageTests
{
    [Fact]
    public async Task PermitAndAdmissionRejectAClaimFromAnotherLeaseOwnerWithoutMutation()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PolicyTests.Request("wrong-owner"));
        var mismatched = permit.Claim with { LeaseOwner = "different-worker" };
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var historyCount = await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;");

        Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(mismatched));
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit with { Claim = mismatched }));
        Assert.Null(await lab.Store.RenewLeaseAsync(mismatched));

        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(historyCount, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal("effect_permitted", await lab.TextAsync("state"));
        Assert.Equal("granted", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
    }

    [Fact]
    public async Task LegacyInvocationAdmissionUsesThePersistedContractAndRejectsAnUnknownPermit()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var planned = PolicyTests.Request("legacy-admission");
        var legacy = new DurableWorkRequest(planned.ScopeId, planned.CommandId, planned.IdempotencyKey,
            planned.WorkName, planned.WorkVersion, planned.Payload, planned.ProviderSafety, planned.RetryPolicy);
        var permit = await lab.AdmitReadyAsync(legacy);

        Assert.Null(permit.Claim.Execution);
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit with { PermitId = Guid.NewGuid() }));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));

        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_policy_schema IS NULL;"));
        Assert.Equal("effect_permitted", await lab.TextAsync("state"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='invocation_admitted';"));
    }

    [Fact]
    public async Task InvocationHistoryFailureRollsBackTheMarkerAndDispatchBeforeAdmissionCanBeRetried()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PolicyTests.Request("admission-history-failure"));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var historyCount = await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;");
        var dispatchBefore = await lab.ScalarAsync<string>("SELECT row_to_json(dispatch)::text FROM appsurface_durable.dispatch;");
        await using (var installFailure = lab.Database.DataSource.CreateCommand("""
            CREATE FUNCTION public.issue765_fail_invocation_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.event_type = 'invocation_admitted' THEN
                    RAISE EXCEPTION 'injected invocation history failure';
                END IF;
                RETURN NEW;
            END; $$;
            CREATE TRIGGER issue765_fail_invocation_history BEFORE INSERT ON appsurface_durable.work_history
            FOR EACH ROW EXECUTE FUNCTION public.issue765_fail_invocation_history();
            """))
        {
            await installFailure.ExecuteNonQueryAsync();
        }

        var failure = await Assert.ThrowsAsync<PostgresException>(async () => await lab.Store.TryAdmitInvocationAsync(permit));

        Assert.Equal(PostgresErrorCodes.RaiseException, failure.SqlState);
        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(historyCount, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history;"));
        Assert.Equal(dispatchBefore, await lab.ScalarAsync<string>("SELECT row_to_json(dispatch)::text FROM appsurface_durable.dispatch;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal("granted", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));

        await using (var removeFailure = lab.Database.DataSource.CreateCommand(
            "DROP TRIGGER issue765_fail_invocation_history ON appsurface_durable.work_history;"))
        {
            await removeFailure.ExecuteNonQueryAsync();
        }

        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='invocation_admitted';"));
    }

    [Fact]
    public async Task ExistingPermitReplayReturnsTheCurrentClaimRevisionAfterRenewal()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PolicyTests.Request("permit-revision"));
        await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddMinutes(1));

        var renewed = await lab.Store.RenewLeaseAsync(permit.Claim);
        Assert.NotNull(renewed);
        Assert.True(renewed!.LeaseExpiresAtUtc > permit.Claim.LeaseExpiresAtUtc);

        var replay = await lab.Store.TryAcquireEffectPermitAsync(permit.Claim);

        Assert.NotNull(replay);
        Assert.Equal(permit.PermitId, replay!.PermitId);
        Assert.Equal(permit.ProviderKey, replay.ProviderKey);
        Assert.Equal(permit.PermittedAtUtc, replay.PermittedAtUtc);
        Assert.Equal(renewed.Revision, replay.Claim.Revision);
        Assert.Equal(permit.Claim.LeaseExpiresAtUtc, replay.Claim.LeaseExpiresAtUtc);
        Assert.Equal(renewed.Revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='effect_permitted';"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='lease_renewed';"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryLeaseExpiryRefusesPermitAdmissionAndRenewalWithoutChangingEvidence(bool admitted)
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PolicyTests.Request("ordinary-expiry"));
        if (admitted) Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddMinutes(31));
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
    public async Task DuplicateAdmissionAfterCircuitCutoffClosesAdmissionButKeepsTheCurrentMarker()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PolicyTests.Request("duplicate-after-cutoff", circuitMinutes: 2, offsets: [0]));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddMinutes(3));
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");

        Assert.False(await lab.Store.TryAdmitInvocationAsync(permit));
        Assert.Equal("circuit_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(permit.Claim));

        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal("effect_permitted", await lab.TextAsync("state"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE status='granted' AND invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='invocation_admitted';"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work WHERE execution_admission_closed_reason='circuit_elapsed';"));
    }

    [Fact]
    public async Task PreAdmissionCancellationPreservesAnUnresolvedEffectFromThePriorAttempt()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var firstPermit = await lab.AdmitReadyAsync(PolicyTests.Request("prior-uncertainty", offsets: [0, 5]));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(firstPermit));
        await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddMinutes(1));
        var retry = await lab.Store.RecordCompletionAsync(firstPermit.Claim,
            new(PostgreSqlWorkCompletionKind.Retry, "provider_retry", "{}"));
        Assert.Equal(DurableWorkState.Ready, retry.State);

        await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddMinutes(5));
        var secondClaim = await lab.Store.TryClaimAsync(Assert.Single(await lab.Store.DiscoverAsync(10)), "retry-worker");
        Assert.NotNull(secondClaim);
        var secondPermit = await lab.Store.TryAcquireEffectPermitAsync(secondClaim!);
        Assert.NotNull(secondPermit);
        Assert.Equal("ambiguous", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number=1;"));

        var canceled = await lab.Store.RequestCancellationAsync(secondPermit!.Claim.ScopeId, secondPermit.Claim.WorkId,
            "tests", "cancel-before-admission", secondPermit.Claim.Revision);

        Assert.Equal(PostgreSqlCancellationOutcome.Applied, canceled.Outcome);
        Assert.Equal(DurableWorkState.Suspended, canceled.State);
        Assert.False(await lab.Store.TryAdmitInvocationAsync(secondPermit));
        Assert.Null(await lab.Store.RenewLeaseAsync(secondPermit.Claim));
        Assert.Equal("suspended_ambiguous_external_outcome", await lab.TextAsync("state"));
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, await lab.TextAsync("terminal_code"));
        Assert.Equal("ambiguous", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number=1;"));
        Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit WHERE attempt_number=2;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE attempt_number=2 AND invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE attempt_number=1 AND invocation_admitted_at IS NOT NULL;"));
    }

    [Fact]
    public async Task AdmittedCallCanRenewAfterObservedCircuitCutoff()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var permit = await lab.AdmitReadyAsync(PolicyTests.Request("renew-after-circuit", circuitMinutes: 2, offsets: [0]));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddMinutes(3));

        Assert.Null(await lab.Store.TryAcquireEffectPermitAsync(permit.Claim));
        var renewed = await lab.Store.RenewLeaseAsync(permit.Claim);

        Assert.NotNull(renewed);
        Assert.True(renewed!.LeaseExpiresAtUtc > permit.Claim.LeaseExpiresAtUtc);
        Assert.Equal("circuit_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal("effect_permitted", await lab.TextAsync("state"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(renewed.Revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(renewed.Revision, await lab.ScalarAsync<long>("SELECT expected_revision FROM appsurface_durable.dispatch;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='lease_renewed';"));
    }

    [Fact]
    public async Task RenewalAtDeadlineClosesAdmissionWithoutExtendingAnAdmittedLease()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var deadline = PolicyTests.Anchor.AddMinutes(2);
        var permit = await lab.AdmitReadyAsync(PolicyTests.Request("renew-at-deadline", deadline: deadline));
        Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
        await lab.Database.SetExecutionTimeAsync(deadline);
        var revision = await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;");
        var originalExpiry = await lab.ScalarAsync<DateTime>("SELECT lease_expires_at FROM appsurface_durable.work;");

        Assert.Null(await lab.Store.RenewLeaseAsync(permit.Claim));

        Assert.Equal(revision, await lab.ScalarAsync<long>("SELECT revision FROM appsurface_durable.work;"));
        Assert.Equal(originalExpiry, await lab.ScalarAsync<DateTime>("SELECT lease_expires_at FROM appsurface_durable.work;"));
        Assert.Equal("effect_permitted", await lab.TextAsync("state"));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal(deadline.UtcDateTime, await lab.ScalarAsync<DateTime>("SELECT execution_deadline_reached_at FROM appsurface_durable.work;"));
        Assert.Equal(1, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.work_history WHERE event_type='lease_renewed';"));
    }

    [Fact]
    public async Task PermitDeadlineClosureInvokesTheTerminalProjectionWithPersistedNoEffect()
    {
        await using var lab = await PolicyTests.Lab.CreateAsync();
        var deadline = PolicyTests.Anchor.AddMinutes(1);
        var permit = await lab.AdmitReadyAsync(PolicyTests.Request("permit-projection", deadline: deadline));
        await lab.Database.SetExecutionTimeAsync(deadline);
        var projectionCalls = 0;

        var result = await lab.Store.TryAcquireEffectPermitAsync(permit.Claim,
            onTerminalApplied: (_, state, code, _) =>
            {
                projectionCalls++;
                Assert.Equal(DurableWorkState.FailedTerminal, state);
                Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, code);
                return ValueTask.CompletedTask;
            });

        Assert.Null(result);
        Assert.Equal(1, projectionCalls);
        Assert.Equal("failed", await lab.TextAsync("state"));
        Assert.Equal("deadline_elapsed", await lab.TextAsync("execution_admission_closed_reason"));
        Assert.Equal("proven_no_effect", await lab.ScalarAsync<string>("SELECT status FROM appsurface_durable.effect_permit;"));
        Assert.Equal(0, await lab.ScalarAsync<long>("SELECT count(*) FROM appsurface_durable.effect_permit WHERE invocation_admitted_at IS NOT NULL;"));
    }
}
