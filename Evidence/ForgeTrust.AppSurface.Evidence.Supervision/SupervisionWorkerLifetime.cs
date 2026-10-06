using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Owns one entire worker startup procedure and its shared stop/join/finalize sequence.</summary>
/// <remarks>
/// This is procedure bookkeeping only: it creates no process, identity, admission or exit capability.
/// Startup is reserved before acquisition, not merely before the final OS start call. Stop closes admission,
/// signals the original startup token, attempts containment, joins the entire original startup, and attempts
/// finalization even after an earlier failure. An ignored cancellation or synchronous callback stall remains
/// owned; the independently armed OS owner must contain it. No canceled wait replaces an actual task join.
/// The caller's two stop procedures supply the original protected cleanup bound; this class starts no timer.
/// </remarks>
internal sealed class SupervisionWorkerLifetime
{
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _insideProcedure = new();
    private TaskCompletionSource? _start;
    private TaskCompletionSource? _stop;
    private CancellationTokenSource? _startupCancellation;
    private CancellationToken _callerToken;
    private bool _closed;
    private bool _startJoined;
    private bool _stopJoined;
    private bool _failed;

    /// <summary>Gets whether another startup is permanently forbidden.</summary>
    internal bool IsClosed { get { lock (_gate) return _closed || _callerToken.IsCancellationRequested; } }
    /// <summary>Gets whether the whole reserved startup, including acquisition/publication, actually finished.</summary>
    internal bool StartJoined { get { lock (_gate) return _startJoined; } }
    /// <summary>Gets whether both stop phases and the original startup finished; failure may remain latched.</summary>
    internal bool StopJoined { get { lock (_gate) return _stopJoined; } }
    /// <summary>Gets sticky procedure failure, without claiming anything about native process settlement.</summary>
    internal bool Failed { get { lock (_gate) return _failed || _callerToken.IsCancellationRequested; } }

    /// <summary>Reserves and executes one startup using a retained linked cancellation source.</summary>
    /// <remarks>The linked source remains alive through finalization, so a returned startup can retain its token for monitoring.</remarks>
    internal Task StartAsync(Func<CancellationToken, Task> start, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(start);
        RequireExternalCaller();
        TaskCompletionSource completion;
        CancellationToken ownedToken;
        lock (_gate)
        {
            if (_closed || _start is not null) throw Rejected();
            _callerToken = token;
            _startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            ownedToken = _startupCancellation.Token;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _start = completion;
        }
        _ = RunStartAsync(start, ownedToken, completion);
        return completion.Task;
    }

    /// <summary>Closes startup and shares containment, original startup join, and finalization across callers.</summary>
    /// <param name="contain">Close pending OS dispatch and contain any accepted or ambiguous unit before startup join.</param>
    /// <param name="finalize">After startup really joins, inspect/stop the owned group and join all original pumps/monitors.</param>
    /// <remarks>
    /// Both procedures are attempted once even if cancellation signalling, containment or startup fails.
    /// Stop before startup forbids future startup. Replay joins the same task including sticky failure;
    /// it never retries a failed native cleanup. Callbacks run outside the bookkeeping lock. A callback
    /// cannot await this lifetime's startup/stop through reentrant use of these APIs.
    /// </remarks>
    internal Task StopAsync(Func<Task> contain, Func<Task> finalize)
    {
        ArgumentNullException.ThrowIfNull(contain);
        ArgumentNullException.ThrowIfNull(finalize);
        RequireExternalCaller();
        TaskCompletionSource completion;
        Task? startup;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _closed = true;
            if (_stop is not null) return _stop.Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = completion;
            startup = _start?.Task;
            cancellation = _startupCancellation;
        }
        _ = RunStopAsync(contain, finalize, startup, cancellation, completion);
        return completion.Task;
    }

    private async Task RunStartAsync(Func<CancellationToken, Task> start, CancellationToken token,
        TaskCompletionSource completion)
    {
        var previous = _insideProcedure.Value;
        _insideProcedure.Value = true;
        try
        {
            token.ThrowIfCancellationRequested();
            await (start(token) ?? throw Rejected()).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate) _startJoined = true;
            completion.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            Fail();
            lock (_gate) _startJoined = true;
            completion.TrySetCanceled(token);
        }
        catch (Exception)
        {
            Fail();
            lock (_gate) _startJoined = true;
            completion.TrySetException(Rejected());
        }
        finally { _insideProcedure.Value = previous; }
    }

    private async Task RunStopAsync(Func<Task> contain, Func<Task> finalize, Task? startup,
        CancellationTokenSource? cancellation, TaskCompletionSource completion)
    {
        try { cancellation?.Cancel(); }
        catch (Exception) { Fail(); }
        await AttemptAsync(contain).ConfigureAwait(false);
        if (startup is not null)
        {
            try { await startup.ConfigureAwait(false); }
            catch (Exception) { Fail(); }
        }
        await AttemptAsync(finalize).ConfigureAwait(false);
        try { cancellation?.Dispose(); }
        catch (Exception) { Fail(); }
        bool failed;
        lock (_gate) { _stopJoined = true; failed = _failed || _callerToken.IsCancellationRequested; }
        if (failed) completion.TrySetException(Rejected());
        else completion.TrySetResult();
    }

    private async Task AttemptAsync(Func<Task> procedure)
    {
        var previous = _insideProcedure.Value;
        _insideProcedure.Value = true;
        try { await (procedure() ?? throw Rejected()).ConfigureAwait(false); }
        catch (Exception) { Fail(); }
        finally { _insideProcedure.Value = previous; }
    }

    private void Fail() { lock (_gate) { _failed = true; _closed = true; } }
    private void RequireExternalCaller() { if (_insideProcedure.Value) throw Rejected(); }
    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The owned worker startup or completion procedure was rejected.");
}
