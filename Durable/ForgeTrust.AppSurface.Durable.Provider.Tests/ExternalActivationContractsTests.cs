using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class ExternalActivationContractsTests
{
    private static readonly TimeSpan MaximumRequestBudget = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    [Fact]
    public void Request_preserves_its_explicit_pump_request_and_accepts_the_timer_bounds()
    {
        var pumpRequest = new DurableRuntimePumpRequest(7, TimeSpan.FromMilliseconds(250), DurableRuntimeSurface.Work);

        var smallest = new DurableExternalActivationRequest(pumpRequest, TimeSpan.FromTicks(1));
        var maximum = new DurableExternalActivationRequest(pumpRequest, MaximumRequestBudget);

        Assert.Same(pumpRequest, smallest.PumpRequest);
        Assert.Equal(TimeSpan.FromTicks(1), smallest.RequestBudget);
        Assert.Same(pumpRequest, maximum.PumpRequest);
        Assert.Equal(MaximumRequestBudget, maximum.RequestBudget);
        Assert.Equal(
            MaximumRequestBudget - TimeSpan.FromTicks(1),
            new DurableExternalActivationRequest(pumpRequest, MaximumRequestBudget - TimeSpan.FromTicks(1)).RequestBudget);
    }

    [Fact]
    public void Request_rejects_null_pump_limits_and_every_nonpositive_or_oversized_budget()
    {
        var pumpRequest = new DurableRuntimePumpRequest();

        Assert.Throws<ArgumentNullException>(() => new DurableExternalActivationRequest(null!, TimeSpan.FromSeconds(1)));

        foreach (var budget in new[]
                 {
                     TimeSpan.Zero,
                     TimeSpan.FromTicks(-1),
                     Timeout.InfiniteTimeSpan,
                     MaximumRequestBudget + TimeSpan.FromTicks(1),
                     TimeSpan.MaxValue,
                 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExternalActivationRequest(pumpRequest, budget));
        }
    }

    [Fact]
    public void Outcome_numbers_are_the_closed_public_contract()
    {
        Assert.Equal(
        [
            DurableExternalActivationOutcomeKind.Unavailable,
            DurableExternalActivationOutcomeKind.Incompatible,
            DurableExternalActivationOutcomeKind.Draining,
            DurableExternalActivationOutcomeKind.Busy,
            DurableExternalActivationOutcomeKind.CanceledBeforeAdmission,
            DurableExternalActivationOutcomeKind.RequestBudgetExceeded,
            DurableExternalActivationOutcomeKind.Completed,
            DurableExternalActivationOutcomeKind.ActivationFailed,
            DurableExternalActivationOutcomeKind.PumpCanceled,
            DurableExternalActivationOutcomeKind.PumpFailed,
        ],
        Enum.GetValues<DurableExternalActivationOutcomeKind>());

        Assert.Equal(0, (int)DurableExternalActivationOutcomeKind.Unavailable);
        Assert.Equal(1, (int)DurableExternalActivationOutcomeKind.Incompatible);
        Assert.Equal(2, (int)DurableExternalActivationOutcomeKind.Draining);
        Assert.Equal(3, (int)DurableExternalActivationOutcomeKind.Busy);
        Assert.Equal(4, (int)DurableExternalActivationOutcomeKind.CanceledBeforeAdmission);
        Assert.Equal(5, (int)DurableExternalActivationOutcomeKind.RequestBudgetExceeded);
        Assert.Equal(6, (int)DurableExternalActivationOutcomeKind.Completed);
        Assert.Equal(7, (int)DurableExternalActivationOutcomeKind.ActivationFailed);
        Assert.Equal(8, (int)DurableExternalActivationOutcomeKind.PumpCanceled);
        Assert.Equal(9, (int)DurableExternalActivationOutcomeKind.PumpFailed);
    }

    [Fact]
    public void Result_constructor_accepts_each_normative_state_code_and_aggregate_combination()
    {
        var pumpResult = CreatePumpResult();
        var eligibleStates = new[]
        {
            DurableRuntimeHealthState.NotStarted,
            DurableRuntimeHealthState.Healthy,
            DurableRuntimeHealthState.Stale,
        };
        var compatibilityCodes = new[]
        {
            DurableProblemCodes.RecoveryEpochRequired,
            DurableProblemCodes.SchemaMissing,
            DurableProblemCodes.SchemaUpgradeRequired,
            DurableProblemCodes.SchemaVersionUnsupported,
            DurableProblemCodes.SchemaInconsistent,
        };

        AssertConstructs(
            DurableExternalActivationOutcomeKind.Unavailable,
            DurableRuntimeHealthState.Unavailable,
            DurableProblemCodes.StoreUnavailable,
            null);

        foreach (var code in compatibilityCodes)
        {
            AssertConstructs(DurableExternalActivationOutcomeKind.Incompatible, DurableRuntimeHealthState.Healthy, code, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.Incompatible, DurableRuntimeHealthState.Incompatible, code, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.NotStarted, code, null);
        }

        AssertConstructs(DurableExternalActivationOutcomeKind.Draining, DurableRuntimeHealthState.Draining, null, null);

        foreach (var state in eligibleStates)
        {
            AssertConstructs(DurableExternalActivationOutcomeKind.Busy, state, null, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.PumpCanceled, state, null, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.PumpFailed, state, DurableProblemCodes.StoreUnavailable, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.PumpFailed, state, DurableProblemCodes.ExternalActivationFailed, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.Completed, state, null, pumpResult);
            AssertConstructs(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, state, null, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, state, null, null);
        }

        foreach (var code in new[] { DurableProblemCodes.ActivatorStale, DurableProblemCodes.WorkerIdentityConflict })
        {
            AssertConstructs(DurableExternalActivationOutcomeKind.Busy, DurableRuntimeHealthState.Stale, code, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.PumpCanceled, DurableRuntimeHealthState.Stale, code, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.Completed, DurableRuntimeHealthState.Stale, code, pumpResult);
            AssertConstructs(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, DurableRuntimeHealthState.Stale, code, null);
            AssertConstructs(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, DurableRuntimeHealthState.Stale, code, null);
        }

        foreach (var state in Enum.GetValues<DurableRuntimeHealthState>().Cast<DurableRuntimeHealthState?>().Prepend(null))
        {
            AssertConstructs(
                DurableExternalActivationOutcomeKind.ActivationFailed,
                state,
                DurableProblemCodes.ExternalActivationFailed,
                null);
        }

        var completed = new DurableExternalActivationResult(
            DurableExternalActivationOutcomeKind.Completed,
            DurableRuntimeHealthState.Healthy,
            null,
            pumpResult);
        Assert.Same(pumpResult, completed.PumpResult);
    }

    [Fact]
    public void Result_constructor_rejects_undefined_enums_and_every_unlisted_or_contradictory_shape()
    {
        var pumpResult = CreatePumpResult();

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExternalActivationResult(
            (DurableExternalActivationOutcomeKind)int.MaxValue,
            null,
            DurableProblemCodes.ExternalActivationFailed,
            null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExternalActivationResult(
            DurableExternalActivationOutcomeKind.ActivationFailed,
            (DurableRuntimeHealthState)int.MaxValue,
            DurableProblemCodes.ExternalActivationFailed,
            null));

        AssertRejected(DurableExternalActivationOutcomeKind.Unavailable, DurableRuntimeHealthState.Healthy, DurableProblemCodes.StoreUnavailable, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Unavailable, DurableRuntimeHealthState.Unavailable, null, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Unavailable, DurableRuntimeHealthState.Unavailable, "asdur103", null);
        AssertRejected(DurableExternalActivationOutcomeKind.Incompatible, null, DurableProblemCodes.SchemaMissing, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Incompatible, DurableRuntimeHealthState.Unavailable, DurableProblemCodes.SchemaMissing, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Incompatible, DurableRuntimeHealthState.Healthy, null, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Incompatible, DurableRuntimeHealthState.Healthy, DurableProblemCodes.ActivatorStale, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Draining, DurableRuntimeHealthState.Healthy, null, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Draining, DurableRuntimeHealthState.Draining, DurableProblemCodes.ExternalActivationFailed, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Busy, DurableRuntimeHealthState.Incompatible, null, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Busy, DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Busy, DurableRuntimeHealthState.Stale, "ASDUR404 ", null);
        AssertRejected(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale, null);
        AssertRejected(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, DurableRuntimeHealthState.Healthy, DurableProblemCodes.WorkerIdentityConflict, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Completed, DurableRuntimeHealthState.Healthy, null, null);
        AssertRejected(DurableExternalActivationOutcomeKind.Busy, DurableRuntimeHealthState.Healthy, null, pumpResult);
        AssertRejected(DurableExternalActivationOutcomeKind.Completed, DurableRuntimeHealthState.Draining, null, pumpResult);
        AssertRejected(DurableExternalActivationOutcomeKind.Completed, DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale, null);
        AssertRejected(DurableExternalActivationOutcomeKind.ActivationFailed, null, null, null);
        AssertRejected(DurableExternalActivationOutcomeKind.ActivationFailed, null, DurableProblemCodes.StoreUnavailable, null);
        AssertRejected(DurableExternalActivationOutcomeKind.ActivationFailed, null, "ASDUR407 ", null);
        AssertRejected(DurableExternalActivationOutcomeKind.PumpCanceled, DurableRuntimeHealthState.Draining, null, null);
        AssertRejected(DurableExternalActivationOutcomeKind.PumpCanceled, DurableRuntimeHealthState.Stale, DurableProblemCodes.ExternalActivationFailed, null);
        AssertRejected(DurableExternalActivationOutcomeKind.PumpFailed, null, DurableProblemCodes.StoreUnavailable, null);
        AssertRejected(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, null, null);
        AssertRejected(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, DurableProblemCodes.ActivatorStale, null);

        foreach (var state in new[]
                 {
                     DurableRuntimeHealthState.NotStarted,
                     DurableRuntimeHealthState.Healthy,
                     DurableRuntimeHealthState.Stale,
                     DurableRuntimeHealthState.Draining,
                     DurableRuntimeHealthState.Incompatible,
                     DurableRuntimeHealthState.Unavailable,
                 })
        {
            AssertRejected(DurableExternalActivationOutcomeKind.Completed, state, "ASDUR999", pumpResult);
        }

        foreach (var code in new[] { DurableProblemCodes.DoctorCanceled, DurableProblemCodes.DoctorContractFailed })
        {
            AssertRejected(DurableExternalActivationOutcomeKind.Unavailable, DurableRuntimeHealthState.Unavailable, code, null);
            AssertRejected(DurableExternalActivationOutcomeKind.Incompatible, DurableRuntimeHealthState.Healthy, code, null);
            AssertRejected(DurableExternalActivationOutcomeKind.Busy, DurableRuntimeHealthState.Stale, code, null);
            AssertRejected(DurableExternalActivationOutcomeKind.ActivationFailed, null, code, null);
            AssertRejected(DurableExternalActivationOutcomeKind.PumpFailed, DurableRuntimeHealthState.Healthy, code, null);
        }
    }

    private static void AssertConstructs(
        DurableExternalActivationOutcomeKind kind,
        DurableRuntimeHealthState? state,
        string? problemCode,
        DurableRuntimePumpResult? pumpResult)
    {
        var result = new DurableExternalActivationResult(kind, state, problemCode, pumpResult);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(state, result.ObservedHealthState);
        Assert.Equal(problemCode, result.ProblemCode);
        Assert.Same(pumpResult, result.PumpResult);
    }

    private static void AssertRejected(
        DurableExternalActivationOutcomeKind kind,
        DurableRuntimeHealthState? state,
        string? problemCode,
        DurableRuntimePumpResult? pumpResult)
    {
        Assert.Throws<ArgumentException>(() => new DurableExternalActivationResult(kind, state, problemCode, pumpResult));
    }

    private static DurableRuntimePumpResult CreatePumpResult() => new(
        discovered: 4,
        claimed: 3,
        processed: 2,
        deferred: 1,
        failed: 1,
        hasMore: true,
        nextDueAtUtc: new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.FromHours(-4)),
        elapsed: TimeSpan.FromTicks(123));
}
