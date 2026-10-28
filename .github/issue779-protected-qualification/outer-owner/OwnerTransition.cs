namespace EvidenceHost.CompletedIsolation;

/// <summary>One-use transition guard; no phase result or target wire can independently authorize closeout.</summary>
internal sealed class OwnerTransition
{
    private enum State { Initial, Prepared, Replayed, Verified, Closed, Failed }
    private State _state;

    internal void Prepared(PhaseResult result) => Advance(State.Initial, State.Prepared, result);
    internal void Replayed(PhaseResult result) => Advance(State.Prepared, State.Replayed, result);
    internal void Closed(PhaseResult result) => Advance(State.Verified, State.Closed, result);

    internal void CustodyVerified()
    {
        if (_state != State.Replayed) Reject();
        _state = State.Verified;
    }

    internal void RequireCloseAllowed()
    {
        if (_state != State.Verified) Reject();
    }

    private void Advance(State expected, State next, PhaseResult result)
    {
        if (_state != expected || !result.Settled || result.ExitCode != 0 || result.TimedOut || result.OutputOverflow)
            Reject();
        _state = next;
    }

    private void Reject()
    {
        _state = State.Failed;
        throw new IsolationException("phase-transition");
    }
}
