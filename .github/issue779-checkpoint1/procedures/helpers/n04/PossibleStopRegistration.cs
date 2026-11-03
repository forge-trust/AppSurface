namespace Issue779.N04;

/// <summary>Retains a reference before a potentially stopping dispatch; this ordering helper performs no native operation.</summary>
/// <typeparam name="T">A reference already owned by the caller, or detached reference data in a control.</typeparam>
/// <remarks>
/// This internal serial-use helper cannot authenticate a process, open a pidfd or signal anything.
/// The actual coordinator supplies only its original retained ProcessPin and fixed native operations.
/// A failed stop remains possibly stopped even if dispatch failed before the physical side effect.
/// A failed resume also remains registered: dispatch may have completed before a later identity/deadline
/// check failed. Cleanup finds the same original reference; it receives no renewed deadline or grant.
/// Calls must be serialized by the caller's original retained task. Reentry is rejected before dispatch.
/// </remarks>
internal sealed class PossibleStopRegistration<T> where T : class
{
    private T? _possibleStop;
    private bool _dispatching;

    /// <summary>Gets the original possibly stopped reference; null is ordering data, not kernel liveness evidence.</summary>
    internal T? PossibleStop => _possibleStop;

    /// <summary>Registers the original reference before invoking the complete original stop operation.</summary>
    /// <param name="original">The original owned reference, never a replacement PID or newly created capability.</param>
    /// <param name="dispatch">The complete original operation, including its existing pre/post checks.</param>
    /// <remarks>Exceptions propagate unchanged; the reference remains registered after every dispatch failure.</remarks>
    internal void Stop(T original, Action dispatch)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(dispatch);
        Require(!_dispatching && _possibleStop is null);
        _possibleStop = original;
        _dispatching = true;
        try { dispatch(); }
        finally { _dispatching = false; }
    }

    /// <summary>Clears only after the same original reference's complete resume operation returns successfully.</summary>
    /// <param name="original">Reference-equal to the retained possible stop.</param>
    /// <param name="dispatch">The original resume plus its existing identity and deadline checks.</param>
    /// <remarks>No kernel state is inferred. A failed dispatch retains the original for cleanup.</remarks>
    internal void Resume(T original, Action dispatch)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(dispatch);
        Require(!_dispatching && _possibleStop is not null && ReferenceEquals(_possibleStop, original));
        _dispatching = true;
        try { dispatch(); _possibleStop = null; }
        finally { _dispatching = false; }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("n04-stop-registration-rejected");
    }
}
