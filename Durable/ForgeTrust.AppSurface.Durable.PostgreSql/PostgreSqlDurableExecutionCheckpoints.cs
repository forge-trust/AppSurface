namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Names an internal boundary in one opt-in PostgreSQL Work execution.</summary>
internal enum PostgreSqlDurableExecutionCheckpointName
{
    BeforePermit = 0,
    AfterPermitCommit = 1,
    BeforeInvocationAdmission = 2,
    AfterInvocationAdmission = 3,
    BeforeProviderCall = 4,
    AfterProviderCall = 5,
    BeforeCompletion = 6,
}

/// <summary>Immutable payload-free facts passed to the internal execution checkpoint seam.</summary>
internal readonly record struct PostgreSqlDurableExecutionCheckpointObservation(
    PostgreSqlDurableExecutionCheckpointName Name,
    int AttemptNumber);

/// <summary>Observes an execution checkpoint without changing production policy or provider authority.</summary>
/// <remarks>
/// The production instance has no callback and completes synchronously. Test hosts may supply one callback. Provider-call
/// checkpoints are emitted only by an adopter-controlled fake executor that knows where its call occurs; the pump does
/// not claim to detect arbitrary effects inside application code.
/// </remarks>
internal sealed class PostgreSqlDurableExecutionCheckpointHook
{
    private readonly Func<PostgreSqlDurableExecutionCheckpointObservation, CancellationToken, ValueTask>? _callback;

    internal static PostgreSqlDurableExecutionCheckpointHook NoOp { get; } = new();

    internal PostgreSqlDurableExecutionCheckpointHook(
        Func<PostgreSqlDurableExecutionCheckpointObservation, CancellationToken, ValueTask>? callback = null)
    {
        _callback = callback;
    }

    internal ValueTask ReachAsync(
        PostgreSqlDurableExecutionCheckpointName name,
        int attemptNumber,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);
        return _callback?.Invoke(new(name, attemptNumber), cancellationToken) ?? ValueTask.CompletedTask;
    }
}
