using System.Diagnostics;
using System.Runtime.CompilerServices;
using ForgeTrust.AppSurface.Durable;

[assembly: InternalsVisibleTo("ForgeTrust.AppSurface.Durable.Testing.Tests")]

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>Identifies a deterministic Work execution boundary available to tests.</summary>
public enum DurableExecutionCheckpointName
{
    /// <summary>The attempt is about to request its provider effect permit.</summary>
    BeforePermit = 0,
    /// <summary>The provider effect permit committed.</summary>
    AfterPermitCommit = 1,
    /// <summary>The attempt is about to request one-use invocation admission.</summary>
    BeforeInvocationAdmission = 2,
    /// <summary>One-use invocation admission committed.</summary>
    AfterInvocationAdmission = 3,
    /// <summary>An adopter-owned fake executor is immediately before its provider operation.</summary>
    BeforeProviderCall = 4,
    /// <summary>An adopter-owned fake executor has returned from its provider operation.</summary>
    AfterProviderCall = 5,
    /// <summary>The runtime is about to persist the terminal Work observation.</summary>
    BeforeCompletion = 6,
}

/// <summary>Immutable, payload-free facts recorded when a test execution reaches a checkpoint.</summary>
public sealed record DurableExecutionCheckpointObservation(
    long Sequence,
    DurableExecutionCheckpointName Name,
    int AttemptNumber,
    DateTimeOffset ObservedAtUtc);

/// <summary>A safe deterministic exception emitted by a one-shot test checkpoint.</summary>
public sealed class DurableExecutionCheckpointException : Exception
{
    internal DurableExecutionCheckpointException(DurableExecutionCheckpointName name, int attemptNumber)
        : base($"Test execution checkpoint '{name}' threw once for attempt {attemptNumber}.")
    {
        Name = name;
        AttemptNumber = attemptNumber;
    }

    /// <summary>Gets the checkpoint that threw.</summary>
    public DurableExecutionCheckpointName Name { get; }

    /// <summary>Gets the attempt number supplied by the provider test seam.</summary>
    public int AttemptNumber { get; }
}

/// <summary>An immutable Testing-package view of accepted execution-policy facts.</summary>
public sealed record DurableWorkExecutionObservation(
    DurableWorkExecutionPolicy Policy,
    DurableExecutionDeadline? Deadline,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset? NextEligibilityAtUtc,
    DateTimeOffset AdmissionCutoffUtc)
{
    /// <summary>Copies the immutable descriptive facts from a Core execution snapshot.</summary>
    public static DurableWorkExecutionObservation Capture(DurableWorkExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot.Policy, snapshot.Deadline, snapshot.AcceptedAtUtc,
            snapshot.NextEligibilityAtUtc, snapshot.AdmissionCutoffUtc);
    }
}

/// <summary>Records bounded immutable checkpoint observations and optionally pauses or throws once at one stage.</summary>
/// <remarks>
/// A checkpoint does not grant execution authority or simulate a provider result. The caller must explicitly release
/// or cancel a configured pause. Both a pause and an observation wait have the configured maximum wait and also honor
/// their caller's cancellation token. Disposal releases every active pause by canceling it.
/// </remarks>
public sealed class DurableExecutionCheckpointController : IDisposable, IAsyncDisposable
{
    private const int MaximumObservationCapacity = 4_096;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maximumWait;
    private readonly int _maximumObservations;
    // Test-only seam for pausing a signaled waiter before its state recheck; WaitForObservationAsync bounds it by the remaining wait budget.
    private readonly Func<ValueTask>? _afterObservationWaitWake;
    private readonly Queue<DurableExecutionCheckpointObservation> _observations = new();
    private readonly Dictionary<DurableExecutionCheckpointName, DurableExecutionCheckpointObservation> _latestObservations = [];
    private readonly Dictionary<DurableExecutionCheckpointName, Arm> _armed = [];
    private readonly Dictionary<DurableExecutionCheckpointName, TaskCompletionSource> _activePauses = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _sequence;
    private bool _disposed;

    /// <summary>Creates a controller with bounded waits and bounded observation history.</summary>
    /// <param name="maximumWait">Maximum time for a pause or observation wait; defaults to five seconds.</param>
    /// <param name="maximumObservations">Maximum retained observations; defaults to 256.</param>
    /// <param name="timeProvider">Clock used only to timestamp observations; defaults to system UTC.</param>
    public DurableExecutionCheckpointController(
        TimeSpan? maximumWait = null,
        int maximumObservations = 256,
        TimeProvider? timeProvider = null)
        : this(maximumWait, maximumObservations, timeProvider, null)
    {
    }

    // Internal constructor exposes the waiter-interleaving seam only to friend tests; the hook is bounded by the remaining maximum-wait budget.
    internal DurableExecutionCheckpointController(
        TimeSpan? maximumWait,
        int maximumObservations,
        TimeProvider? timeProvider,
        Func<ValueTask>? afterObservationWaitWake)
    {
        _maximumWait = maximumWait ?? TimeSpan.FromSeconds(5);
        if (_maximumWait <= TimeSpan.Zero || _maximumWait > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWait), "Checkpoint waits must be greater than zero and no longer than five minutes.");
        }

        if (maximumObservations is < 1 or > MaximumObservationCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumObservations), maximumObservations,
                $"Observation capacity must be between 1 and {MaximumObservationCapacity}.");
        }

        _maximumObservations = maximumObservations;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _afterObservationWaitWake = afterObservationWaitWake;
    }

    /// <summary>Returns a defensive, read-only copy of the observations retained so far.</summary>
    public IReadOnlyList<DurableExecutionCheckpointObservation> Observations
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_observations.ToArray());
            }
        }
    }

    /// <summary>Configures one invocation at <paramref name="name"/> to pause once.</summary>
    /// <exception cref="InvalidOperationException">Another action is already armed for this checkpoint.</exception>
    public void PauseOnce(DurableExecutionCheckpointName name)
    {
        ValidateName(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            ArmOnce(name, new Arm(ArmKind.Pause, NewSignal()));
        }
    }

    /// <summary>Configures one invocation at <paramref name="name"/> to throw a safe test exception once.</summary>
    public void ThrowOnce(DurableExecutionCheckpointName name)
    {
        ValidateName(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            ArmOnce(name, new Arm(ArmKind.Throw, NewSignal()));
        }
    }

    /// <summary>Records a checkpoint, then applies its armed one-shot pause or exception, if any.</summary>
    /// <exception cref="DurableExecutionCheckpointException">A one-shot throw was armed at this stage.</exception>
    /// <exception cref="TimeoutException">An armed pause was not released before the configured maximum wait.</exception>
    public async ValueTask ReachAsync(
        DurableExecutionCheckpointName name,
        int attemptNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);
        cancellationToken.ThrowIfCancellationRequested();

        Arm? action;
        TaskCompletionSource? observationChanged;
        var observation = default(DurableExecutionCheckpointObservation)!;
        lock (_sync)
        {
            ThrowIfDisposed();
            observation = new DurableExecutionCheckpointObservation(
                checked(++_sequence),
                name,
                attemptNumber,
                _timeProvider.GetUtcNow());
            if (_observations.Count == _maximumObservations)
            {
                _observations.Dequeue();
            }

            _observations.Enqueue(observation);
            _latestObservations[name] = observation;
            observationChanged = _changed;
            _changed = NewSignal();
            if (_armed.Remove(name, out var configured))
            {
                action = configured;
                if (action.Kind == ArmKind.Pause)
                {
                    _activePauses.Add(name, action.Gate);
                }
            }
            else
            {
                action = null;
            }
        }

        observationChanged.TrySetResult();
        if (action is null)
        {
            return;
        }

        if (action.Kind == ArmKind.Throw)
        {
            throw new DurableExecutionCheckpointException(name, attemptNumber);
        }

        try
        {
            await action.Gate.Task.WaitAsync(_maximumWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Checkpoint '{name}' was not released within the configured maximum wait.", exception);
        }
        finally
        {
            lock (_sync)
            {
                if (_activePauses.TryGetValue(name, out var active) && ReferenceEquals(active, action.Gate))
                {
                    _activePauses.Remove(name);
                }
            }
        }
    }

    /// <summary>Waits for a matching observation, with the configured maximum wait and caller cancellation.</summary>
    /// <remarks>The latest observation for a stage remains waitable after bounded history evicts it.</remarks>
    public async ValueTask<DurableExecutionCheckpointObservation> WaitForObservationAsync(
        DurableExecutionCheckpointName name,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(DurableExecutionCheckpointController));
                }

                var found = _observations.FirstOrDefault(item => item.Name == name);
                if (found is null)
                {
                    _latestObservations.TryGetValue(name, out found);
                }

                if (found is not null)
                {
                    return found;
                }

                changed = _changed.Task;
            }

            var remaining = _maximumWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                throw ObservationTimeout(name);
            }

            try
            {
                await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                if (_afterObservationWaitWake is not null)
                {
                    var wakeRemaining = _maximumWait - Stopwatch.GetElapsedTime(started);
                    if (wakeRemaining <= TimeSpan.Zero)
                    {
                        throw ObservationTimeout(name);
                    }

                    await _afterObservationWaitWake().AsTask()
                        .WaitAsync(wakeRemaining, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                throw ObservationTimeout(name);
            }
        }
    }

    /// <summary>Releases a currently paused one-shot checkpoint; returns false when none is paused.</summary>
    public bool Release(DurableExecutionCheckpointName name)
    {
        ValidateName(name);
        lock (_sync)
        {
            return _activePauses.TryGetValue(name, out var pause) && pause.TrySetResult();
        }
    }

    /// <summary>Cancels an armed or active one-shot pause; returns false when nothing can be canceled.</summary>
    public bool Cancel(DurableExecutionCheckpointName name)
    {
        ValidateName(name);
        lock (_sync)
        {
            var canceled = _armed.Remove(name);
            if (_activePauses.TryGetValue(name, out var pause))
            {
                canceled |= pause.TrySetCanceled();
            }

            return canceled;
        }
    }

    /// <summary>Cancels all active pauses and prevents new checkpoints or arms.</summary>
    public void Dispose()
    {
        TaskCompletionSource[] activePauses;
        TaskCompletionSource changed;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _armed.Clear();
            activePauses = _activePauses.Values.ToArray();
            changed = _changed;
            _changed = NewSignal();
        }

        foreach (var pause in activePauses)
        {
            pause.TrySetCanceled();
        }

        changed.TrySetResult();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ArmOnce(DurableExecutionCheckpointName name, Arm arm)
    {
        if (_armed.ContainsKey(name) || _activePauses.ContainsKey(name))
        {
            throw new InvalidOperationException($"Checkpoint '{name}' already has a one-shot action.");
        }

        _armed.Add(name, arm);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DurableExecutionCheckpointController));
        }
    }

    private static TimeoutException ObservationTimeout(DurableExecutionCheckpointName name) =>
        new($"Checkpoint '{name}' was not observed within the configured maximum wait.");

    private static void ValidateName(DurableExecutionCheckpointName name)
    {
        if (!Enum.IsDefined(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private enum ArmKind
    {
        Pause,
        Throw,
    }

    private sealed record Arm(ArmKind Kind, TaskCompletionSource Gate);
}
