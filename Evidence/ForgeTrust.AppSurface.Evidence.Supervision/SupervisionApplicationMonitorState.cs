namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Orders application observation failure against the owner's intentional stop boundary.</summary>
/// <remarks>
/// This comparison state owns no task, deadline, process or admission. The native owner must retain and
/// join its original monitor and independently prove stopped units, empty groups and complete output.
/// Work-admission closure does not itself end observations of an already owned application.
/// </remarks>
internal sealed class SupervisionApplicationMonitorState
{
    private readonly object _gate = new();
    private bool _stopping;
    private bool _failed;

    /// <summary>Gets whether another live observation may begin before intentional containment.</summary>
    internal bool ShouldObserve { get { lock (_gate) return !_stopping && !_failed; } }

    /// <summary>Gets a failure that won the ordering race before stop; later stop never clears it.</summary>
    internal bool Failed { get { lock (_gate) return _failed; } }

    /// <summary>Closes observation admission before native containment can invalidate live process samples.</summary>
    /// <remarks>This neither cancels nor joins the original monitor task and grants no settlement.</remarks>
    internal void BeginStop() { lock (_gate) _stopping = true; }

    /// <summary>Atomically decides whether an observation error must fail the live workload.</summary>
    /// <returns>True when failure was recorded before stop, including an already latched failure.</returns>
    /// <remarks>
    /// An in-flight read can complete with a stopped-process error after intentional containment began.
    /// That observation cannot retrospectively fail the preceding live phase. A failure recorded first
    /// remains failed regardless of later stop, and the owner must still propagate it to its shared ledger.
    /// </remarks>
    internal bool RecordObservationFailure()
    {
        lock (_gate)
        {
            if (_failed) return true;
            if (_stopping) return false;
            _failed = true;
            return true;
        }
    }
}
