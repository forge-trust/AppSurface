namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed record ScheduleStateModel
{
    public ScheduleStateModel(DurableScheduleState state, long generation, long revision)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        State = state;
        Generation = generation;
        Revision = revision;
    }

    public DurableScheduleState State { get; }

    public long Generation { get; }

    public long Revision { get; }
}

internal enum ScheduleStateTransitionCode
{
    Applied,
    Unchanged,
    RevisionConflict,
    Deleted,
    Suspended,
}

internal sealed record ScheduleStateTransitionResult(
    ScheduleStateTransitionCode Code,
    ScheduleStateModel State,
    bool InvalidatePendingOccurrences,
    bool PendingOccurrenceBecameEligible);

internal static class ScheduleStateTransitions
{
    public static ScheduleStateTransitionResult Pause(ScheduleStateModel current, long expectedRevision)
    {
        var rejected = Reject(current, expectedRevision);
        if (rejected is not null)
        {
            return rejected;
        }

        return current.State == DurableScheduleState.Paused
            ? NoChange(current)
            : Applied(current, DurableScheduleState.Paused, incrementGeneration: false, invalidatePending: false);
    }

    public static ScheduleStateTransitionResult Resume(ScheduleStateModel current, long expectedRevision)
    {
        var rejected = Reject(current, expectedRevision);
        if (rejected is not null)
        {
            return rejected;
        }

        return current.State == DurableScheduleState.Active
            ? NoChange(current)
            : new ScheduleStateTransitionResult(
                ScheduleStateTransitionCode.Applied,
                new ScheduleStateModel(DurableScheduleState.Active, current.Generation, checked(current.Revision + 1)),
                InvalidatePendingOccurrences: false,
                PendingOccurrenceBecameEligible: true);
    }

    public static ScheduleStateTransitionResult Update(ScheduleStateModel current, long expectedRevision)
    {
        var rejected = Reject(current, expectedRevision);
        if (rejected is not null)
        {
            return rejected;
        }

        return Applied(
            current,
            current.State,
            incrementGeneration: true,
            invalidatePending: true);
    }

    public static ScheduleStateTransitionResult Delete(ScheduleStateModel current, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        if (current.Revision != expectedRevision)
        {
            return Rejected(ScheduleStateTransitionCode.RevisionConflict, current);
        }

        if (current.State == DurableScheduleState.Deleted)
        {
            return Rejected(ScheduleStateTransitionCode.Deleted, current);
        }

        if (current.State == DurableScheduleState.Suspended)
        {
            return Rejected(ScheduleStateTransitionCode.Suspended, current);
        }

        return Applied(
            current,
            DurableScheduleState.Deleted,
            incrementGeneration: false,
            invalidatePending: true);
    }

    private static ScheduleStateTransitionResult? Reject(ScheduleStateModel current, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        if (current.Revision != expectedRevision)
        {
            return Rejected(ScheduleStateTransitionCode.RevisionConflict, current);
        }

        return current.State switch
        {
            DurableScheduleState.Deleted => Rejected(ScheduleStateTransitionCode.Deleted, current),
            DurableScheduleState.Suspended => Rejected(ScheduleStateTransitionCode.Suspended, current),
            _ => null,
        };
    }

    private static ScheduleStateTransitionResult Applied(
        ScheduleStateModel current,
        DurableScheduleState state,
        bool incrementGeneration,
        bool invalidatePending) =>
        new(
            ScheduleStateTransitionCode.Applied,
            new ScheduleStateModel(
                state,
                incrementGeneration ? checked(current.Generation + 1) : current.Generation,
                checked(current.Revision + 1)),
            invalidatePending,
            PendingOccurrenceBecameEligible: false);

    private static ScheduleStateTransitionResult NoChange(ScheduleStateModel current) =>
        new(
            ScheduleStateTransitionCode.Unchanged,
            new ScheduleStateModel(current.State, current.Generation, checked(current.Revision + 1)),
            InvalidatePendingOccurrences: false,
            PendingOccurrenceBecameEligible: false);

    private static ScheduleStateTransitionResult Rejected(ScheduleStateTransitionCode code, ScheduleStateModel current) =>
        new(code, current, InvalidatePendingOccurrences: false, PendingOccurrenceBecameEligible: false);
}
