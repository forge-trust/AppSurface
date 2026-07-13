namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed record ScheduleGenerationOccurrence
{
    public ScheduleGenerationOccurrence(long generation, ScheduleOccurrenceWindow window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
        Window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public long Generation { get; }

    public ScheduleOccurrenceWindow Window { get; }
}

internal sealed record SchedulePendingOccurrence
{
    public SchedulePendingOccurrence(
        long generation,
        DateTimeOffset nominalDueUtc,
        DateTimeOffset coalescedThroughUtc,
        long? coveredOccurrenceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
        NominalDueUtc = nominalDueUtc.ToUniversalTime();
        CoalescedThroughUtc = coalescedThroughUtc.ToUniversalTime();
        if (CoalescedThroughUtc < NominalDueUtc)
        {
            throw new ArgumentException("Coalesced-through instant must not precede the nominal due instant.", nameof(coalescedThroughUtc));
        }

        if (coveredOccurrenceCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coveredOccurrenceCount));
        }

        CoveredOccurrenceCount = coveredOccurrenceCount;
    }

    public long Generation { get; }

    public DateTimeOffset NominalDueUtc { get; }

    public DateTimeOffset CoalescedThroughUtc { get; }

    public long? CoveredOccurrenceCount { get; }

    public SchedulePendingOccurrence Coalesce(ScheduleOccurrenceWindow occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        long? combinedCount = CoveredOccurrenceCount.HasValue && occurrence.CoveredOccurrenceCount.HasValue
            ? checked(CoveredOccurrenceCount.Value + occurrence.CoveredOccurrenceCount.Value)
            : null;
        return new SchedulePendingOccurrence(
            Generation,
            NominalDueUtc,
            occurrence.CoveredThroughUtc > CoalescedThroughUtc ? occurrence.CoveredThroughUtc : CoalescedThroughUtc,
            combinedCount);
    }

    public ScheduleGenerationOccurrence ToOccurrence() => new(
        Generation,
        new ScheduleOccurrenceWindow(
            NominalDueUtc,
            CoalescedThroughUtc,
            CoveredOccurrenceCount,
            isRecovery: CoalescedThroughUtc > NominalDueUtc));
}

internal sealed record ScheduleRunSlotState
{
    public ScheduleRunSlotState(int activeRuns, SchedulePendingOccurrence? pendingOccurrence = null)
    {
        if (activeRuns < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activeRuns));
        }

        ActiveRuns = activeRuns;
        PendingOccurrence = pendingOccurrence;
    }

    public int ActiveRuns { get; }

    public SchedulePendingOccurrence? PendingOccurrence { get; }
}

internal enum ScheduleRunSlotDecisionKind
{
    StartNow,
    Queued,
    Coalesced,
    SkippedOverlap,
    HeldPaused,
    InvalidatedGeneration,
    InvalidatedDeleted,
    InvalidatedSuspended,
    Released,
    ReleasedAndStartedPending,
    ReleasedAndDiscardedPending,
}

internal sealed record ScheduleRunSlotDecision(
    ScheduleRunSlotDecisionKind Kind,
    ScheduleRunSlotState State,
    ScheduleGenerationOccurrence? OccurrenceToStart = null);

internal static class ScheduleRunSlotPolicyCalculator
{
    public static ScheduleRunSlotDecision ApplyOccurrence(
        ScheduleRunSlotState state,
        DurableScheduleState scheduleState,
        long activeGeneration,
        ScheduleOverlapPolicy overlapPolicy,
        ScheduleGenerationOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(overlapPolicy);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activeGeneration);

        if (occurrence.Generation != activeGeneration)
        {
            return new ScheduleRunSlotDecision(ScheduleRunSlotDecisionKind.InvalidatedGeneration, state);
        }

        if (scheduleState == DurableScheduleState.Deleted)
        {
            return new ScheduleRunSlotDecision(ScheduleRunSlotDecisionKind.InvalidatedDeleted, state);
        }

        if (scheduleState == DurableScheduleState.Suspended)
        {
            return new ScheduleRunSlotDecision(ScheduleRunSlotDecisionKind.InvalidatedSuspended, state);
        }

        if (scheduleState == DurableScheduleState.Paused)
        {
            var held = QueueOrCoalesce(state, occurrence, out var kind);
            return new ScheduleRunSlotDecision(
                kind == ScheduleRunSlotDecisionKind.Queued ? ScheduleRunSlotDecisionKind.HeldPaused : kind,
                held);
        }

        if (scheduleState != DurableScheduleState.Active)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduleState));
        }

        return overlapPolicy.Kind switch
        {
            ScheduleOverlapPolicyKind.QueueOne => ApplyQueueOne(state, occurrence),
            ScheduleOverlapPolicyKind.Skip => ApplySkip(state, occurrence),
            ScheduleOverlapPolicyKind.AllowConcurrent => ApplyConcurrent(state, overlapPolicy, occurrence),
            _ => throw new ArgumentOutOfRangeException(nameof(overlapPolicy), "Unknown overlap policy."),
        };
    }

    public static ScheduleRunSlotDecision Release(
        ScheduleRunSlotState state,
        DurableScheduleState scheduleState,
        long activeGeneration,
        ScheduleOverlapPolicy overlapPolicy)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(overlapPolicy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activeGeneration);
        if (state.ActiveRuns == 0)
        {
            throw new InvalidOperationException("Cannot release a schedule run slot when no run is active.");
        }

        var released = new ScheduleRunSlotState(state.ActiveRuns - 1, state.PendingOccurrence);
        if (released.PendingOccurrence is null)
        {
            return new ScheduleRunSlotDecision(ScheduleRunSlotDecisionKind.Released, released);
        }

        if (released.PendingOccurrence.Generation != activeGeneration
            || scheduleState is DurableScheduleState.Deleted or DurableScheduleState.Suspended)
        {
            return new ScheduleRunSlotDecision(
                ScheduleRunSlotDecisionKind.ReleasedAndDiscardedPending,
                new ScheduleRunSlotState(released.ActiveRuns));
        }

        if (scheduleState == DurableScheduleState.Paused
            || released.ActiveRuns >= overlapPolicy.MaximumConcurrentRuns)
        {
            return new ScheduleRunSlotDecision(ScheduleRunSlotDecisionKind.Released, released);
        }

        var toStart = released.PendingOccurrence.ToOccurrence();
        return new ScheduleRunSlotDecision(
            ScheduleRunSlotDecisionKind.ReleasedAndStartedPending,
            new ScheduleRunSlotState(released.ActiveRuns + 1),
            toStart);
    }

    public static ScheduleRunSlotDecision ActivatePending(
        ScheduleRunSlotState state,
        DurableScheduleState scheduleState,
        long activeGeneration,
        ScheduleOverlapPolicy overlapPolicy)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(overlapPolicy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activeGeneration);

        if (state.PendingOccurrence is null
            || scheduleState != DurableScheduleState.Active
            || state.ActiveRuns >= overlapPolicy.MaximumConcurrentRuns)
        {
            return new ScheduleRunSlotDecision(ScheduleRunSlotDecisionKind.Released, state);
        }

        if (state.PendingOccurrence.Generation != activeGeneration)
        {
            return new ScheduleRunSlotDecision(
                ScheduleRunSlotDecisionKind.ReleasedAndDiscardedPending,
                new ScheduleRunSlotState(state.ActiveRuns));
        }

        var toStart = state.PendingOccurrence.ToOccurrence();
        return new ScheduleRunSlotDecision(
            ScheduleRunSlotDecisionKind.ReleasedAndStartedPending,
            new ScheduleRunSlotState(state.ActiveRuns + 1),
            toStart);
    }

    private static ScheduleRunSlotDecision ApplyQueueOne(
        ScheduleRunSlotState state,
        ScheduleGenerationOccurrence occurrence)
    {
        if (state.ActiveRuns == 0 && state.PendingOccurrence is null)
        {
            return Start(state, occurrence);
        }

        var queued = QueueOrCoalesce(state, occurrence, out var kind);
        return new ScheduleRunSlotDecision(kind, queued);
    }

    private static ScheduleRunSlotDecision ApplySkip(
        ScheduleRunSlotState state,
        ScheduleGenerationOccurrence occurrence) =>
        state.ActiveRuns == 0 && state.PendingOccurrence is null
            ? Start(state, occurrence)
            : new ScheduleRunSlotDecision(ScheduleRunSlotDecisionKind.SkippedOverlap, state);

    private static ScheduleRunSlotDecision ApplyConcurrent(
        ScheduleRunSlotState state,
        ScheduleOverlapPolicy policy,
        ScheduleGenerationOccurrence occurrence) =>
        state.ActiveRuns < policy.MaximumConcurrentRuns && state.PendingOccurrence is null
            ? Start(state, occurrence)
            : new ScheduleRunSlotDecision(ScheduleRunSlotDecisionKind.SkippedOverlap, state);

    private static ScheduleRunSlotDecision Start(
        ScheduleRunSlotState state,
        ScheduleGenerationOccurrence occurrence) =>
        new(
            ScheduleRunSlotDecisionKind.StartNow,
            new ScheduleRunSlotState(state.ActiveRuns + 1, state.PendingOccurrence),
            occurrence);

    private static ScheduleRunSlotState QueueOrCoalesce(
        ScheduleRunSlotState state,
        ScheduleGenerationOccurrence occurrence,
        out ScheduleRunSlotDecisionKind kind)
    {
        if (state.PendingOccurrence is null)
        {
            kind = ScheduleRunSlotDecisionKind.Queued;
            return new ScheduleRunSlotState(
                state.ActiveRuns,
                new SchedulePendingOccurrence(
                    occurrence.Generation,
                    occurrence.Window.NominalDueUtc,
                    occurrence.Window.CoveredThroughUtc,
                    occurrence.Window.CoveredOccurrenceCount));
        }

        if (state.PendingOccurrence.Generation != occurrence.Generation)
        {
            throw new InvalidOperationException("Cannot coalesce occurrences from different schedule generations.");
        }

        kind = ScheduleRunSlotDecisionKind.Coalesced;
        return new ScheduleRunSlotState(
            state.ActiveRuns,
            state.PendingOccurrence.Coalesce(occurrence.Window));
    }
}
