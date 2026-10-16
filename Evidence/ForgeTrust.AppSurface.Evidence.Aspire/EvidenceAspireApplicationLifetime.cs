namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Shares one application cleanup across the readiness registrations created by its owner.</summary>
/// <param name="disposeApplication">Stops and disposes the existing application once, after its final registration releases or its owner explicitly disposes.</param>
/// <remarks>
/// A registration releases only its own interest. The last registration closes admission and runs
/// cleanup; explicit owner disposal also closes admission. Callbacks run outside the admission lock.
/// Direct owner disposal is reserved for callers that have already stopped all readiness work.
/// </remarks>
internal sealed class EvidenceAspireApplicationLifetime(Func<ValueTask> disposeApplication) : IAsyncDisposable
{
    private sealed record CleanupFlow(EvidenceAspireApplicationLifetime Owner, CleanupFlow? Parent);

    private static readonly AsyncLocal<CleanupFlow?> CurrentCleanup = new();
    private readonly object _gate = new();
    private readonly Func<ValueTask> _disposeApplication = disposeApplication ?? throw new ArgumentNullException(nameof(disposeApplication));
    private int _registrations;
    private TaskCompletionSource? _cleanup;

    /// <summary>Acquires one independently disposable readiness registration before cleanup begins.</summary>
    /// <returns>A registration whose final release joins the shared application cleanup.</returns>
    /// <exception cref="ObjectDisposedException">Application cleanup has begun.</exception>
    internal IAsyncDisposable Acquire()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            _registrations++;
            return new Registration(this);
        }
    }

    /// <summary>Rejects creating readiness work after application cleanup has begun.</summary>
    /// <exception cref="ObjectDisposedException">Application cleanup has begun.</exception>
    internal void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
        }
    }

    /// <summary>Closes registration admission and joins the one application cleanup result.</summary>
    /// <returns>The shared cleanup completion, including its original failure.</returns>
    /// <remarks>Cleanup callback reentry returns immediately to avoid awaiting its own completion.</remarks>
    public ValueTask DisposeAsync()
    {
        if (IsCleanupFlow())
        {
            return ValueTask.CompletedTask;
        }

        TaskCompletionSource? startCleanup = null;
        Task completion;
        lock (_gate)
        {
            if (_cleanup is null)
            {
                _cleanup = startCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            completion = _cleanup.Task;
        }

        if (startCleanup is not null)
        {
            _ = CompleteCleanupAsync(startCleanup);
        }

        return new ValueTask(completion);
    }

    private ValueTask Release(Registration registration)
    {
        if (IsCleanupFlow())
        {
            return ValueTask.CompletedTask;
        }

        TaskCompletionSource? startCleanup = null;
        Task completion;
        lock (_gate)
        {
            if (registration.Completion is null)
            {
                _registrations--;
                if (_registrations == 0 && _cleanup is null)
                {
                    _cleanup = startCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                registration.Completion = _cleanup?.Task ?? Task.CompletedTask;
            }

            completion = registration.Completion;
        }

        if (startCleanup is not null)
        {
            _ = CompleteCleanupAsync(startCleanup);
        }

        return new ValueTask(completion);
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource completion)
    {
        var previous = CurrentCleanup.Value;
        CurrentCleanup.Value = new CleanupFlow(this, previous);
        try
        {
            await _disposeApplication().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            CurrentCleanup.Value = previous;
        }
    }

    private bool IsCleanupFlow()
    {
        for (var flow = CurrentCleanup.Value; flow is not null; flow = flow.Parent)
        {
            if (ReferenceEquals(flow.Owner, this))
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_cleanup is not null, this);

    private sealed class Registration(EvidenceAspireApplicationLifetime owner) : IAsyncDisposable
    {
        internal Task? Completion { get; set; }

        public ValueTask DisposeAsync() => owner.Release(this);
    }
}
