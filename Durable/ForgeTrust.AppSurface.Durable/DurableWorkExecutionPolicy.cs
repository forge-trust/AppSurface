namespace ForgeTrust.AppSurface.Durable;

/// <summary>A fixed, acceptance-relative sequence of earliest Work attempt eligibility offsets.</summary>
/// <remarks>
/// Offsets are zero-based values for one-based execution attempt numbers. The sequence is immutable and copied at
/// construction. Version <c>attempt-plan-v1</c> is the only currently supported schedule contract.
/// </remarks>
public sealed class DurableAttemptPlan : IEquatable<DurableAttemptPlan>
{
    /// <summary>Initializes a validated attempt plan.</summary>
    /// <param name="version">Supported plan version; currently <c>attempt-plan-v1</c>.</param>
    /// <param name="elapsedOffsets">One to 256 strictly increasing offsets beginning at zero.</param>
    /// <param name="maximumCircuitDuration">Exclusive duration cutoff, greater than the final offset.</param>
    /// <exception cref="ArgumentNullException">Version or offsets are null.</exception>
    /// <exception cref="ArgumentException">The version is unsupported or offsets are empty, unordered, or invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A duration is negative, zero where prohibited, or sub-microsecond.</exception>
    public DurableAttemptPlan(string version, IEnumerable<TimeSpan> elapsedOffsets, TimeSpan maximumCircuitDuration)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(elapsedOffsets);
        Version = DurableIdentifier.Require(version, nameof(version), 100);
        if (!string.Equals(Version, SupportedVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Attempt plan version '{Version}' is not supported.", nameof(version));
        }

        RequireMicrosecondPrecision(maximumCircuitDuration, nameof(maximumCircuitDuration));
        if (maximumCircuitDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCircuitDuration));
        }

        var offsetList = new List<TimeSpan>(capacity: 8);
        foreach (var offset in elapsedOffsets)
        {
            if (offsetList.Count == MaximumOffsetCount)
            {
                throw new ArgumentException($"An attempt plan must contain between 1 and {MaximumOffsetCount} offsets.", nameof(elapsedOffsets));
            }

            offsetList.Add(offset);
        }

        if (offsetList.Count == 0)
        {
            throw new ArgumentException($"An attempt plan must contain between 1 and {MaximumOffsetCount} offsets.", nameof(elapsedOffsets));
        }

        var offsets = offsetList.ToArray();
        for (var index = 0; index < offsets.Length; index++)
        {
            RequireMicrosecondPrecision(offsets[index], nameof(elapsedOffsets));
            if (offsets[index] < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedOffsets), "Attempt offsets must not be negative.");
            }

            if (index == 0 && offsets[index] != TimeSpan.Zero)
            {
                throw new ArgumentException("The first attempt offset must be zero.", nameof(elapsedOffsets));
            }

            if (index > 0 && offsets[index] <= offsets[index - 1])
            {
                throw new ArgumentException("Attempt offsets must be strictly increasing.", nameof(elapsedOffsets));
            }
        }

        if (maximumCircuitDuration <= offsets[^1])
        {
            throw new ArgumentException("The exclusive circuit duration must be greater than the final attempt offset.", nameof(maximumCircuitDuration));
        }

        ElapsedOffsets = Array.AsReadOnly(offsets);
        MaximumCircuitDuration = maximumCircuitDuration;
    }

    /// <summary>The sole plan version currently understood by this Core package.</summary>
    internal const string SupportedVersion = "attempt-plan-v1";

    /// <summary>The maximum number of plan slots.</summary>
    internal const int MaximumOffsetCount = 256;

    /// <summary>Gets the stable plan version.</summary>
    public string Version { get; }

    /// <summary>Gets an immutable copy of zero-based earliest eligibility offsets.</summary>
    public IReadOnlyList<TimeSpan> ElapsedOffsets { get; }

    /// <summary>Gets the exclusive circuit duration from acceptance.</summary>
    public TimeSpan MaximumCircuitDuration { get; }

    /// <inheritdoc />
    public bool Equals(DurableAttemptPlan? other) => other is not null
        && string.Equals(Version, other.Version, StringComparison.Ordinal)
        && MaximumCircuitDuration == other.MaximumCircuitDuration
        && ElapsedOffsets.SequenceEqual(other.ElapsedOffsets);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DurableAttemptPlan other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version, StringComparer.Ordinal);
        hash.Add(MaximumCircuitDuration);
        foreach (var offset in ElapsedOffsets)
        {
            hash.Add(offset);
        }

        return hash.ToHashCode();
    }

    internal static void RequireMicrosecondPrecision(TimeSpan value, string parameterName)
    {
        if (value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Opt-in durable timing values must have microsecond precision.");
        }
    }

    internal static DateTimeOffset NormalizeUtc(DateTimeOffset value, string parameterName)
    {
        DateTimeOffset utc;
        try
        {
            utc = value.ToUniversalTime();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The timestamp cannot be represented as UTC. " + exception.Message);
        }

        if (utc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Opt-in durable timestamps must have microsecond precision.");
        }

        return utc;
    }
}

/// <summary>An immutable exclusive UTC execution deadline.</summary>
public sealed class DurableExecutionDeadline : IEquatable<DurableExecutionDeadline>
{
    /// <summary>Initializes a deadline at the given instant, normalized to UTC.</summary>
    /// <param name="notAfterUtc">Exclusive deadline with microsecond precision.</param>
    public DurableExecutionDeadline(DateTimeOffset notAfterUtc)
    {
        NotAfterUtc = DurableAttemptPlan.NormalizeUtc(notAfterUtc, nameof(notAfterUtc));
    }

    /// <summary>Gets the exclusive UTC deadline.</summary>
    public DateTimeOffset NotAfterUtc { get; }

    /// <inheritdoc />
    public bool Equals(DurableExecutionDeadline? other) => other is not null && NotAfterUtc == other.NotAfterUtc;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DurableExecutionDeadline other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => NotAfterUtc.GetHashCode();
}

/// <summary>Combines a legacy retry/lease policy with an optional fixed attempt plan.</summary>
public sealed class DurableWorkExecutionPolicy : IEquatable<DurableWorkExecutionPolicy>
{
    private DurableWorkExecutionPolicy(DurableWorkRetryPolicy retryPolicy, DurableAttemptPlan? attemptPlan)
    {
        ArgumentNullException.ThrowIfNull(retryPolicy);
        if (attemptPlan is not null)
        {
            ValidateOptInRetryPrecision(retryPolicy);
            if (retryPolicy.MaximumAttempts != attemptPlan.ElapsedOffsets.Count)
            {
                throw new ArgumentException("Retry maximum attempts must equal the attempt-plan slot count.", nameof(retryPolicy));
            }

            if (retryPolicy.MaximumElapsedTime < attemptPlan.MaximumCircuitDuration)
            {
                throw new ArgumentException("Retry maximum elapsed time must be at least the attempt-plan circuit duration.", nameof(retryPolicy));
            }
        }

        RetryPolicy = retryPolicy;
        AttemptPlan = attemptPlan;
    }

    /// <summary>Creates the legacy completion-relative projection without a plan.</summary>
    public static DurableWorkExecutionPolicy FromRetryPolicy(DurableWorkRetryPolicy retryPolicy) =>
        new(retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy)), null);

    /// <summary>Creates a validated fixed-offset policy.</summary>
    public static DurableWorkExecutionPolicy ForAttemptPlan(DurableWorkRetryPolicy retryPolicy, DurableAttemptPlan attemptPlan) =>
        new(retryPolicy, attemptPlan ?? throw new ArgumentNullException(nameof(attemptPlan)));

    /// <summary>Gets the existing retry and lease settings.</summary>
    public DurableWorkRetryPolicy RetryPolicy { get; }

    /// <summary>Gets the fixed attempt plan, or null for the legacy timing projection.</summary>
    public DurableAttemptPlan? AttemptPlan { get; }

    /// <inheritdoc />
    public bool Equals(DurableWorkExecutionPolicy? other) => other is not null
        && RetryPolicy.Equals(other.RetryPolicy)
        && Equals(AttemptPlan, other.AttemptPlan);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DurableWorkExecutionPolicy other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(RetryPolicy, AttemptPlan);

    internal static void ValidateOptInRetryPrecision(DurableWorkRetryPolicy retryPolicy)
    {
        DurableAttemptPlan.RequireMicrosecondPrecision(retryPolicy.MaximumElapsedTime, nameof(retryPolicy));
        DurableAttemptPlan.RequireMicrosecondPrecision(retryPolicy.InitialRetryDelay, nameof(retryPolicy));
        DurableAttemptPlan.RequireMicrosecondPrecision(retryPolicy.MaximumRetryDelay, nameof(retryPolicy));
        DurableAttemptPlan.RequireMicrosecondPrecision(retryPolicy.LeaseDuration, nameof(retryPolicy));
        DurableAttemptPlan.RequireMicrosecondPrecision(retryPolicy.RenewalCadence, nameof(retryPolicy));
        DurableAttemptPlan.RequireMicrosecondPrecision(retryPolicy.MaximumLeaseLifetime, nameof(retryPolicy));
    }
}

/// <summary>Immutable policy and timing facts captured for one accepted Work execution.</summary>
/// <remarks>This snapshot is descriptive only; the provider store remains the authority for every admission decision.</remarks>
public sealed class DurableWorkExecutionSnapshot : IEquatable<DurableWorkExecutionSnapshot>
{
    /// <summary>Initializes a validated snapshot of accepted execution timing facts.</summary>
    /// <param name="policy">Accepted execution policy.</param>
    /// <param name="deadline">Optional absolute deadline.</param>
    /// <param name="acceptedAtUtc">Authoritative acceptance timestamp.</param>
    /// <param name="nextEligibilityAtUtc">Current next-slot eligibility, or null when no slot remains.</param>
    /// <param name="admissionCutoffUtc">Exclusive minimum of all applicable immutable cutoffs.</param>
    public DurableWorkExecutionSnapshot(DurableWorkExecutionPolicy policy, DurableExecutionDeadline? deadline,
        DateTimeOffset acceptedAtUtc, DateTimeOffset? nextEligibilityAtUtc, DateTimeOffset admissionCutoffUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var optedIntoExecutionTiming = policy.AttemptPlan is not null || deadline is not null;
        if (optedIntoExecutionTiming)
        {
            DurableWorkExecutionPolicy.ValidateOptInRetryPrecision(policy.RetryPolicy);
        }

        var accepted = DurableAttemptPlan.NormalizeUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        DateTimeOffset? nextEligibility = nextEligibilityAtUtc is { } next
            ? DurableAttemptPlan.NormalizeUtc(next, nameof(nextEligibilityAtUtc)) : null;
        var cutoff = DurableAttemptPlan.NormalizeUtc(admissionCutoffUtc, nameof(admissionCutoffUtc));
        var expectedCutoff = DurableWorkTimingEvaluator.GetAdmissionCutoff(policy, accepted, deadline);
        if (cutoff != expectedCutoff)
        {
            throw new ArgumentException("Admission cutoff must equal the minimum of the accepted policy and deadline cutoffs.", nameof(admissionCutoffUtc));
        }

        if (cutoff <= accepted)
        {
            throw new ArgumentException("An accepted execution snapshot must have an open admission window after acceptance.", nameof(admissionCutoffUtc));
        }

        if (policy.AttemptPlan is { } attemptPlan && nextEligibility is { } due)
        {
            if (due < accepted)
            {
                throw new ArgumentOutOfRangeException(nameof(nextEligibilityAtUtc),
                    "A planned next-slot eligibility must not precede its acceptance anchor.");
            }

            if (!attemptPlan.ElapsedOffsets.Contains(due - accepted))
            {
                throw new ArgumentException(
                    "A planned next-slot eligibility must match an immutable offset from acceptance.",
                    nameof(nextEligibilityAtUtc));
            }
        }

        Policy = policy;
        Deadline = deadline;
        AcceptedAtUtc = accepted;
        NextEligibilityAtUtc = nextEligibility;
        AdmissionCutoffUtc = cutoff;
    }

    /// <summary>Gets the accepted policy.</summary>
    public DurableWorkExecutionPolicy Policy { get; }

    /// <summary>Gets the optional accepted absolute deadline.</summary>
    public DurableExecutionDeadline? Deadline { get; }

    /// <summary>Gets the authoritative acceptance instant.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }

    /// <summary>
    /// Gets the next unconsumed plan slot's eligibility timestamp, including during a claimed invocation.
    /// A deadline-only request may carry a due time earlier than acceptance; null means no eligibility remains.
    /// </summary>
    public DateTimeOffset? NextEligibilityAtUtc { get; }

    /// <summary>Gets the exclusive admission cutoff.</summary>
    public DateTimeOffset AdmissionCutoffUtc { get; }

    /// <inheritdoc />
    public bool Equals(DurableWorkExecutionSnapshot? other) => other is not null
        && Policy.Equals(other.Policy)
        && Equals(Deadline, other.Deadline)
        && AcceptedAtUtc == other.AcceptedAtUtc
        && NextEligibilityAtUtc == other.NextEligibilityAtUtc
        && AdmissionCutoffUtc == other.AdmissionCutoffUtc;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DurableWorkExecutionSnapshot other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Policy, Deadline, AcceptedAtUtc, NextEligibilityAtUtc, AdmissionCutoffUtc);
}

internal enum DurableWorkTimingDecision
{
    Eligible = 0,
    NotYetEligible = 1,
    AttemptPlanExhausted = 2,
    AdmissionClosed = 3,
}

internal enum DurableWorkTimingReason
{
    SlotsExhausted = 0,
    CircuitElapsed = 1,
    DeadlineElapsed = 2,
    ElapsedExhausted = 3,
}

internal readonly record struct DurableWorkTimingEvaluation(
    DateTimeOffset? NextEligibilityAtUtc,
    DateTimeOffset AdmissionCutoffUtc,
    DurableWorkTimingDecision Decision,
    DurableWorkTimingReason? Reason);

/// <summary>Computes immutable eligibility and cutoff facts without I/O, authority, or effect classification.</summary>
internal static class DurableWorkTimingEvaluator
{
    internal static DurableWorkTimingEvaluation Evaluate(DurableWorkExecutionPolicy policy,
        DateTimeOffset acceptedAtUtc, int nextAttemptNumber, DurableExecutionDeadline? deadline,
        DateTimeOffset authoritativeNowUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var optedIntoExecutionTiming = policy.AttemptPlan is not null || deadline is not null;
        if (optedIntoExecutionTiming)
        {
            DurableWorkExecutionPolicy.ValidateOptInRetryPrecision(policy.RetryPolicy);
        }

        if (nextAttemptNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttemptNumber));
        }

        var accepted = DurableAttemptPlan.NormalizeUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        var now = DurableAttemptPlan.NormalizeUtc(authoritativeNowUtc, nameof(authoritativeNowUtc));
        var cutoff = GetAdmissionCutoff(policy, accepted, deadline);
        DateTimeOffset? next = policy.AttemptPlan is { } plan && nextAttemptNumber <= plan.ElapsedOffsets.Count
            ? AddChecked(accepted, plan.ElapsedOffsets[nextAttemptNumber - 1], nameof(acceptedAtUtc))
            : null;

        if (deadline is not null && now >= deadline.NotAfterUtc)
        {
            return new(next, cutoff, DurableWorkTimingDecision.AdmissionClosed, DurableWorkTimingReason.DeadlineElapsed);
        }

        if (policy.AttemptPlan is { } attemptPlan)
        {
            var circuitCutoff = AddChecked(accepted, attemptPlan.MaximumCircuitDuration, nameof(acceptedAtUtc));
            if (now >= circuitCutoff)
            {
                return new(next, cutoff, DurableWorkTimingDecision.AdmissionClosed, DurableWorkTimingReason.CircuitElapsed);
            }

            if (nextAttemptNumber > attemptPlan.ElapsedOffsets.Count)
            {
                return new(next, cutoff, DurableWorkTimingDecision.AttemptPlanExhausted, DurableWorkTimingReason.SlotsExhausted);
            }
        }

        var elapsedCutoff = AddChecked(accepted, policy.RetryPolicy.MaximumElapsedTime, nameof(acceptedAtUtc));
        if (now >= elapsedCutoff)
        {
            return new(next, cutoff, DurableWorkTimingDecision.AdmissionClosed, DurableWorkTimingReason.ElapsedExhausted);
        }

        return next is { } eligibility && now < eligibility
            ? new(next, cutoff, DurableWorkTimingDecision.NotYetEligible, null)
            : new(next, cutoff, DurableWorkTimingDecision.Eligible, null);
    }

    internal static DateTimeOffset GetAdmissionCutoff(DurableWorkExecutionPolicy policy,
        DateTimeOffset acceptedAtUtc, DurableExecutionDeadline? deadline)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var optedIntoExecutionTiming = policy.AttemptPlan is not null || deadline is not null;
        var accepted = DurableAttemptPlan.NormalizeUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        var cutoff = AddChecked(accepted, policy.RetryPolicy.MaximumElapsedTime, nameof(acceptedAtUtc));
        if (policy.AttemptPlan is { } plan)
        {
            cutoff = Min(cutoff, AddChecked(accepted, plan.MaximumCircuitDuration, nameof(acceptedAtUtc)));
        }

        return deadline is null ? cutoff : Min(cutoff, deadline.NotAfterUtc);
    }

    private static DateTimeOffset AddChecked(DateTimeOffset instant, TimeSpan duration, string parameterName)
    {
        try
        {
            return new DateTimeOffset(checked(instant.UtcTicks + duration.Ticks), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Execution timing is outside the supported UTC timestamp range. " + exception.Message);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Execution timing is outside the supported UTC timestamp range. " + exception.Message);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
