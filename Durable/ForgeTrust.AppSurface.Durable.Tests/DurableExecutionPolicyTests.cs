using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableExecutionPolicyTests
{
    private static readonly DateTimeOffset Accepted = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Attempt_plan_copies_offsets_and_has_structural_value_equality()
    {
        var source = new[] { TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20) };
        var first = new DurableAttemptPlan(DurableAttemptPlan.SupportedVersion, source, TimeSpan.FromMinutes(30));
        source[1] = TimeSpan.Zero;
        var second = new DurableAttemptPlan(DurableAttemptPlan.SupportedVersion,
            [TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20)], TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(5), first.ElapsedOffsets[1]);
        Assert.IsNotType<TimeSpan[]>(first.ElapsedOffsets);
        Assert.True(first.Equals(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        var exposed = Assert.IsAssignableFrom<IList<TimeSpan>>(first.ElapsedOffsets);
        Assert.True(exposed.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposed[0] = TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Attempt_plan_accepts_one_and_maximum_slot_counts()
    {
        var one = new DurableAttemptPlan(DurableAttemptPlan.SupportedVersion, [TimeSpan.Zero], TimeSpan.FromTicks(10));
        var maximum = new DurableAttemptPlan(DurableAttemptPlan.SupportedVersion,
            Enumerable.Range(0, DurableAttemptPlan.MaximumOffsetCount).Select(index => TimeSpan.FromTicks(index * 10L)),
            TimeSpan.FromSeconds(1));

        Assert.Single(one.ElapsedOffsets);
        Assert.Equal(DurableAttemptPlan.MaximumOffsetCount, maximum.ElapsedOffsets.Count);
        Assert.Equal(TimeSpan.Zero, maximum.ElapsedOffsets[0]);
    }

    [Fact]
    public void Attempt_plan_rejects_null_empty_oversized_and_unsupported_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => new DurableAttemptPlan(null!, [TimeSpan.Zero], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new DurableAttemptPlan(DurableAttemptPlan.SupportedVersion, null!, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan(DurableAttemptPlan.SupportedVersion, [], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan("attempt-plan-v2", [TimeSpan.Zero], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan(new string('v', 101), [TimeSpan.Zero], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan("bad/version", [TimeSpan.Zero], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan(DurableAttemptPlan.SupportedVersion,
            Enumerable.Range(0, DurableAttemptPlan.MaximumOffsetCount + 1).Select(index => TimeSpan.FromTicks(index * 10L)),
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Attempt_plan_rejects_invalid_offset_order_sign_and_circuit_bounds()
    {
        var version = DurableAttemptPlan.SupportedVersion;
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan(version, [TimeSpan.FromTicks(10)], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAttemptPlan(version, [TimeSpan.Zero, TimeSpan.FromTicks(-10)], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan(version, [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan(version, [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)], TimeSpan.FromSeconds(3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAttemptPlan(version, [TimeSpan.Zero, TimeSpan.FromTicks(1)], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAttemptPlan(version, [TimeSpan.Zero], TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAttemptPlan(version, [TimeSpan.Zero], TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableAttemptPlan(version, [TimeSpan.Zero], TimeSpan.FromTicks(-10)));
        Assert.Throws<ArgumentException>(() => new DurableAttemptPlan(version, [TimeSpan.Zero, TimeSpan.FromSeconds(1)], TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Retry_policy_projection_preserves_legacy_precision_but_planned_composition_requires_microseconds()
    {
        var legacyPrecision = Retry(maximumElapsed: TimeSpan.FromTicks(TimeSpan.TicksPerMinute + 1));

        var legacyPolicy = DurableWorkExecutionPolicy.FromRetryPolicy(legacyPrecision);
        Assert.Same(legacyPrecision, legacyPolicy.RetryPolicy);

        var evaluation = DurableWorkTimingEvaluator.Evaluate(legacyPolicy, Accepted, 1, null, Accepted);
        Assert.Equal(DurableWorkTimingDecision.Eligible, evaluation.Decision);

        var cutoff = DurableWorkTimingEvaluator.GetAdmissionCutoff(legacyPolicy, Accepted, null);
        Assert.Equal(Accepted.Add(legacyPrecision.MaximumElapsedTime), cutoff);

        Assert.Throws<ArgumentOutOfRangeException>(() => DurableWorkExecutionPolicy.ForAttemptPlan(
            legacyPrecision, Plan([TimeSpan.Zero], TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void Planned_policy_requires_matching_attempt_count_and_elapsed_horizon()
    {
        var plan = Plan([TimeSpan.Zero, TimeSpan.FromMinutes(5)], TimeSpan.FromMinutes(10));
        Assert.Throws<ArgumentException>(() => DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 1), plan));
        Assert.Throws<ArgumentException>(() => DurableWorkExecutionPolicy.ForAttemptPlan(
            Retry(attempts: 2, maximumElapsed: TimeSpan.FromMinutes(9)), plan));
        Assert.Throws<ArgumentNullException>(() => DurableWorkExecutionPolicy.FromRetryPolicy(null!));
        Assert.Throws<ArgumentNullException>(() => DurableWorkExecutionPolicy.ForAttemptPlan(Retry(), null!));

        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 2, maximumElapsed: TimeSpan.FromMinutes(10)), plan);
        var equal = DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 2, maximumElapsed: TimeSpan.FromMinutes(10)),
            Plan([TimeSpan.Zero, TimeSpan.FromMinutes(5)], TimeSpan.FromMinutes(10)));
        Assert.True(policy.Equals(equal));
        Assert.Equal(policy.GetHashCode(), equal.GetHashCode());
    }

    [Fact]
    public void Deadline_normalizes_offset_and_compares_by_utc_instant()
    {
        var utc = new DateTimeOffset(2026, 10, 4, 16, 0, 0, TimeSpan.Zero);
        var offset = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(-4));
        var first = new DurableExecutionDeadline(utc);
        var second = new DurableExecutionDeadline(offset);

        Assert.Equal(utc, second.NotAfterUtc);
        Assert.True(first.Equals(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableExecutionDeadline(utc.AddTicks(1)));
    }

    [Fact]
    public void Execution_cutoff_is_the_checked_minimum_and_deadline_snapshot_validates_facts()
    {
        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 2, maximumElapsed: TimeSpan.FromMinutes(20)),
            Plan([TimeSpan.Zero, TimeSpan.FromMinutes(5)], TimeSpan.FromMinutes(10)));
        var deadline = new DurableExecutionDeadline(Accepted.AddMinutes(8));
        var cutoff = DurableWorkTimingEvaluator.GetAdmissionCutoff(policy, Accepted, deadline);
        var snapshot = new DurableWorkExecutionSnapshot(policy, deadline, Accepted, Accepted.AddMinutes(5), cutoff);

        Assert.Equal(Accepted.AddMinutes(8), snapshot.AdmissionCutoffUtc);
        Assert.Equal(policy, snapshot.Policy);
        Assert.Equal(deadline, snapshot.Deadline);
        Assert.Equal(Accepted, snapshot.AcceptedAtUtc);
        Assert.Equal(Accepted.AddMinutes(5), snapshot.NextEligibilityAtUtc);
        Assert.Throws<ArgumentException>(() => new DurableWorkExecutionSnapshot(policy, deadline, Accepted,
            Accepted.AddMinutes(5), Accepted.AddMinutes(9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkExecutionSnapshot(policy, deadline,
            Accepted, Accepted.AddTicks(1), cutoff));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableWorkTimingEvaluator.GetAdmissionCutoff(
            DurableWorkExecutionPolicy.FromRetryPolicy(Retry()),
            new DateTimeOffset(DateTime.MaxValue.Ticks - 9, TimeSpan.Zero), null));
    }

    [Fact]
    public void Admission_cutoff_reports_checked_tick_addition_overflow()
    {
        var policy = DurableWorkExecutionPolicy.FromRetryPolicy(Retry(maximumElapsed: TimeSpan.MaxValue));

        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DurableWorkTimingEvaluator.GetAdmissionCutoff(policy, Accepted, null));

        Assert.Equal("acceptedAtUtc", failure.ParamName);
        Assert.Contains("supported UTC timestamp range", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execution_snapshot_requires_an_open_window_after_acceptance()
    {
        var policy = DurableWorkExecutionPolicy.FromRetryPolicy(Retry());
        var atAcceptance = new DurableExecutionDeadline(Accepted);
        Assert.Throws<ArgumentException>(() => new DurableWorkExecutionSnapshot(policy, atAcceptance,
            Accepted, null, Accepted));
    }

    [Fact]
    public void Timing_evaluator_uses_one_based_attempts_fixed_offsets_and_exclusive_cutoffs()
    {
        var plan = Plan([TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20)], TimeSpan.FromMinutes(40));
        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 3, maximumElapsed: TimeSpan.FromMinutes(60)), plan);

        var first = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 1, null, Accepted);
        var early = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 2, null, Accepted.AddMinutes(5).AddTicks(-10));
        var second = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 2, null, Accepted.AddMinutes(5));
        var overdue = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 3, null, Accepted.AddMinutes(25));
        var exhausted = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 4, null, Accepted.AddMinutes(39));
        var circuit = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 4, null, Accepted.AddMinutes(40));

        Assert.Equal(Accepted, first.NextEligibilityAtUtc);
        Assert.Equal(DurableWorkTimingDecision.Eligible, first.Decision);
        Assert.Equal(Accepted.AddMinutes(5), early.NextEligibilityAtUtc);
        Assert.Equal(DurableWorkTimingDecision.NotYetEligible, early.Decision);
        Assert.Equal(DurableWorkTimingDecision.Eligible, second.Decision);
        Assert.Equal(Accepted.AddMinutes(20), overdue.NextEligibilityAtUtc);
        Assert.Equal(DurableWorkTimingDecision.Eligible, overdue.Decision);
        Assert.Equal(DurableWorkTimingDecision.AttemptPlanExhausted, exhausted.Decision);
        Assert.Equal(DurableWorkTimingReason.SlotsExhausted, exhausted.Reason);
        Assert.Equal(DurableWorkTimingDecision.AdmissionClosed, circuit.Decision);
        Assert.Equal(DurableWorkTimingReason.CircuitElapsed, circuit.Reason);
    }

    [Fact]
    public void Timing_evaluator_reports_deadline_and_legacy_elapsed_cutoffs_before_admission()
    {
        var policy = DurableWorkExecutionPolicy.FromRetryPolicy(Retry(maximumElapsed: TimeSpan.FromMinutes(10)));
        var deadline = new DurableExecutionDeadline(Accepted.AddMinutes(5));
        var deadlineResult = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 1, deadline, deadline.NotAfterUtc);
        var elapsedResult = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 1, null, Accepted.AddMinutes(10));
        var open = DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 1, null, Accepted.AddMinutes(9));

        Assert.Null(deadlineResult.NextEligibilityAtUtc);
        Assert.Equal(DurableWorkTimingReason.DeadlineElapsed, deadlineResult.Reason);
        Assert.Equal(DurableWorkTimingReason.ElapsedExhausted, elapsedResult.Reason);
        Assert.Equal(DurableWorkTimingDecision.Eligible, open.Decision);
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 0, null, Accepted));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableWorkTimingEvaluator.Evaluate(policy, Accepted, 1, null, Accepted.AddTicks(1)));
    }

    [Fact]
    public void Execution_context_preserves_legacy_constructor_and_adds_snapshot_without_authority()
    {
        var policy = DurableWorkExecutionPolicy.FromRetryPolicy(Retry());
        var cutoff = DurableWorkTimingEvaluator.GetAdmissionCutoff(policy, Accepted, null);
        var snapshot = new DurableWorkExecutionSnapshot(policy, null, Accepted, null, cutoff);
        var payload = new DurableEncodedPayload("tests.payload", "v1", DurableDataClassification.Operational, "x"u8.ToArray());
        var identity = DurableWorkerExecutionIdentity.CreateInitial("activity", 1, 1, "epoch");
        var legacy = new DurableWorkExecutionContext(new("scope"), new("work-id"), "work", "v1", payload,
            DurableProviderSafety.Idempotent, identity);
        var optedIn = DurableWorkExecutionContext.CreateWithExecution(new("scope"), new("work-id"), "work", "v1",
            payload, DurableProviderSafety.Idempotent, identity, snapshot);

        Assert.Null(legacy.Execution);
        Assert.Same(snapshot, optedIn.Execution);
        Assert.Throws<ArgumentNullException>(() => DurableWorkExecutionContext.CreateWithExecution(new("scope"),
            new("work-id"), "work", "v1", payload, DurableProviderSafety.Idempotent, identity, null!));
    }

    [Fact]
    public void Immutable_execution_values_compare_all_contract_fields_and_reject_other_object_types()
    {
        var plan = Plan([TimeSpan.Zero, TimeSpan.FromMinutes(5)], TimeSpan.FromMinutes(10));
        var samePlan = Plan([TimeSpan.Zero, TimeSpan.FromMinutes(5)], TimeSpan.FromMinutes(10));
        var otherOffsets = Plan([TimeSpan.Zero, TimeSpan.FromMinutes(6)], TimeSpan.FromMinutes(10));
        var otherCircuit = Plan([TimeSpan.Zero, TimeSpan.FromMinutes(5)], TimeSpan.FromMinutes(11));
        Assert.True(plan.Equals((object)samePlan));
        Assert.False(plan.Equals((DurableAttemptPlan?)null));
        Assert.False(plan.Equals(new object()));
        Assert.False(plan.Equals(otherOffsets));
        Assert.False(plan.Equals(otherCircuit));

        var deadline = new DurableExecutionDeadline(Accepted.AddMinutes(8));
        Assert.True(deadline.Equals((object)new DurableExecutionDeadline(deadline.NotAfterUtc)));
        Assert.False(deadline.Equals((DurableExecutionDeadline?)null));
        Assert.False(deadline.Equals(new object()));
        Assert.False(deadline.Equals(new DurableExecutionDeadline(Accepted.AddMinutes(9))));

        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 2), plan);
        var samePolicy = DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 2), samePlan);
        Assert.True(policy.Equals((object)samePolicy));
        Assert.Equal(policy.GetHashCode(), samePolicy.GetHashCode());
        Assert.False(policy.Equals((DurableWorkExecutionPolicy?)null));
        Assert.False(policy.Equals(new object()));
        Assert.False(policy.Equals(DurableWorkExecutionPolicy.FromRetryPolicy(Retry(attempts: 2))));
        Assert.False(policy.Equals(DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 2), otherOffsets)));
        Assert.False(policy.Equals(DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 2, maximumElapsed: TimeSpan.FromHours(2)), plan)));

        var snapshot = new DurableWorkExecutionSnapshot(policy, deadline, Accepted, Accepted.AddMinutes(5), deadline.NotAfterUtc);
        var sameSnapshot = new DurableWorkExecutionSnapshot(samePolicy, deadline, Accepted, Accepted.AddMinutes(5), deadline.NotAfterUtc);
        Assert.True(snapshot.Equals((object)sameSnapshot));
        Assert.Equal(snapshot.GetHashCode(), sameSnapshot.GetHashCode());
        Assert.False(snapshot.Equals((DurableWorkExecutionSnapshot?)null));
        Assert.False(snapshot.Equals(new object()));
        Assert.False(snapshot.Equals(new DurableWorkExecutionSnapshot(policy, deadline, Accepted, null, deadline.NotAfterUtc)));
        Assert.False(snapshot.Equals(new DurableWorkExecutionSnapshot(policy, deadline, Accepted.AddMinutes(1), Accepted.AddMinutes(6), deadline.NotAfterUtc)));
        Assert.False(snapshot.Equals(new DurableWorkExecutionSnapshot(policy, null, Accepted, Accepted.AddMinutes(5), Accepted.AddMinutes(10))));
        Assert.False(snapshot.Equals(new DurableWorkExecutionSnapshot(DurableWorkExecutionPolicy.ForAttemptPlan(Retry(attempts: 2), otherOffsets),
            deadline, Accepted, Accepted.AddMinutes(6), deadline.NotAfterUtc)));
    }

    [Fact]
    public void Execution_snapshot_rejects_null_policy_and_planned_eligibility_before_acceptance()
    {
        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(Retry(), Plan([TimeSpan.Zero], TimeSpan.FromMinutes(10)));
        var cutoff = Accepted.AddMinutes(10);
        Assert.Throws<ArgumentNullException>(() => new DurableWorkExecutionSnapshot(null!, null, Accepted, null, cutoff));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkExecutionSnapshot(policy, null, Accepted, Accepted.AddTicks(-10), cutoff));
        Assert.Throws<ArgumentNullException>(() => DurableWorkTimingEvaluator.Evaluate(null!, Accepted, 1, null, Accepted));
        Assert.Throws<ArgumentNullException>(() => DurableWorkTimingEvaluator.GetAdmissionCutoff(null!, Accepted, null));
    }

    [Fact]
    public void Execution_snapshot_accepts_exact_plan_slots_and_null_but_rejects_off_plan_due()
    {
        var plan = Plan([TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20)], TimeSpan.FromMinutes(25));
        var policy = DurableWorkExecutionPolicy.ForAttemptPlan(
            Retry(attempts: 3, maximumElapsed: TimeSpan.FromMinutes(30)), plan);
        var deadline = new DurableExecutionDeadline(Accepted.AddMinutes(3));
        var cutoff = deadline.NotAfterUtc;

        foreach (var offset in plan.ElapsedOffsets)
        {
            var snapshot = new DurableWorkExecutionSnapshot(policy, deadline, Accepted, Accepted + offset, cutoff);
            Assert.Equal(Accepted + offset, snapshot.NextEligibilityAtUtc);
        }

        var exhausted = new DurableWorkExecutionSnapshot(policy, deadline, Accepted, null, cutoff);
        Assert.Null(exhausted.NextEligibilityAtUtc);
        Assert.Throws<ArgumentException>(() => new DurableWorkExecutionSnapshot(
            policy, deadline, Accepted, Accepted.AddMinutes(1), cutoff));
    }

    private static DurableAttemptPlan Plan(IReadOnlyList<TimeSpan> offsets, TimeSpan circuit) =>
        new(DurableAttemptPlan.SupportedVersion, offsets, circuit);

    private static DurableWorkRetryPolicy Retry(int attempts = 1, TimeSpan? maximumElapsed = null) => new(
        attempts,
        maximumElapsed ?? TimeSpan.FromHours(1),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(5),
        "exponential-v1");
}
