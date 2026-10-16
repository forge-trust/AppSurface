using System.Collections.Immutable;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed failures of a run's received-output ledger, without process output or exception text.</summary>
internal enum SupervisionRunOutputFailure
{
    /// <summary>No ledger failure was observed.</summary>
    None,
    /// <summary>A collector/stream was reused or registration was closed, invalid or over capacity.</summary>
    InvalidSequence,
    /// <summary>Actual reads exceeded the original aggregate byte limit.</summary>
    QuotaExceeded,
    /// <summary>An original pair failed, was cancelled or did not finish both EOFs.</summary>
    PairFailed,
    /// <summary>The registered task set and actual byte ledger did not reconcile.</summary>
    AccountingMismatch,
    /// <summary>Signalling the shared cancellation source threw.</summary>
    StopSignalFailed,
}

/// <summary>Final joined stream accounting; never evidence of process, cgroup or account settlement.</summary>
/// <param name="Pairs">Every registered pair's original joined receipt, in registration order.</param>
/// <param name="RegisteredPairs">Number of pairs accepted before irreversible closure.</param>
/// <param name="ReceivedBytes">All actual reads charged across pairs, including discarded and late bytes.</param>
/// <param name="ReceivedByteLimit">The original root-selected aggregate limit.</param>
/// <param name="Failure">First latched ledger failure.</param>
/// <param name="QuotaExceeded">Whether any charge crossed the aggregate limit.</param>
/// <param name="StopSignalFailed">Whether signalling cancellation threw.</param>
/// <param name="DrainFailed">Whether any non-quota failure occurred, even after quota was the first failure.</param>
internal sealed record SupervisionRunOutputReceipt(
    ImmutableArray<SupervisionOutputReceipt> Pairs, int RegisteredPairs,
    long ReceivedBytes, long ReceivedByteLimit, SupervisionRunOutputFailure Failure,
    bool QuotaExceeded, bool StopSignalFailed, bool DrainFailed = false)
{
    /// <summary>Complete actual stream drainage and byte reconciliation, allowing only a sticky quota failure.</summary>
    /// <remarks>This is stream data; native process, cgroup, server, filesystem and account guards remain separate.</remarks>
    internal bool Drained => !StopSignalFailed && !DrainFailed
        && Failure is SupervisionRunOutputFailure.None or SupervisionRunOutputFailure.QuotaExceeded
        && Pairs.Length == RegisteredPairs && Pairs.All(static pair => pair.Drained)
        && Pairs.Sum(static pair => pair.ReceivedBytes) == ReceivedBytes;

    /// <summary>Requires the complete registered set, exact byte reconciliation and successful EOFs.</summary>
    internal bool Successful => Failure == SupervisionRunOutputFailure.None && !QuotaExceeded && !StopSignalFailed
        && Pairs.Length == RegisteredPairs && Pairs.All(static pair => pair.Successful)
        && Pairs.Sum(static pair => pair.ReceivedBytes) == ReceivedBytes && ReceivedBytes <= ReceivedByteLimit;
}

/// <summary>Owns one received-byte ledger and original collection tasks across a run's output pairs.</summary>
/// <remarks>
/// The native owner registers each pair before dispatch, and passes this same instance to every owned
/// worker/subject/reporter pair. Independent counters cannot establish aggregate success. A pair receipt
/// may precede a later sibling failure; only <see cref="CompleteAsync"/> provides final stream accounting.
/// No deadline is created or renewed. Ignored cancellation remains owned through the original task join.
/// This component grants no execution, filesystem, admission or physical-exit authority.
/// </remarks>
internal sealed class SupervisionRunOutputBudget : IDisposable
{
    private readonly object _gate = new();
    private readonly object _signalGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _pumpStop = new();
    private readonly List<Task<SupervisionOutputReceipt>> _pairs = [];
    private readonly HashSet<SupervisionOutputCollector> _collectors = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Stream> _streams = new(ReferenceEqualityComparer.Instance);
    private readonly long _limit;
    private long _received;
    private SupervisionRunOutputFailure _failure;
    private bool _quotaExceeded;
    private bool _stopSignalFailed;
    private bool _drainFailed;
    private bool _closed;
    private bool _disposed;
    private Task<SupervisionRunOutputReceipt>? _completion;

    /// <summary>Creates a run ledger with a positive limit no greater than the existing 16 MiB contract.</summary>
    /// <param name="receivedByteLimit">Original aggregate received-byte limit, which may only be lowered.</param>
    /// <param name="drainOnQuotaExceeded">Fixed native policy to stop writers while charging/discarding through EOF;
    /// false preserves ordinary stop-on-quota behavior. The original caller token still bounds every read.</param>
    internal SupervisionRunOutputBudget(long receivedByteLimit = EvidenceRunBudgetLimits.MaximumProcessOutputBytes,
        bool drainOnQuotaExceeded = false)
    {
        if (receivedByteLimit is <= 0 or > EvidenceRunBudgetLimits.MaximumProcessOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(receivedByteLimit));
        _limit = receivedByteLimit;
        DrainOnQuotaExceeded = drainOnQuotaExceeded;
    }

    /// <summary>Immediate execution-stop signal on any ledger failure; not proof of stopped writers or joined streams.</summary>
    internal CancellationToken StopToken => _stop.Token;

    /// <summary>Fixed native composition policy: overflow stops execution while original pumps drain to real EOF.</summary>
    internal bool DrainOnQuotaExceeded { get; }

    /// <summary>Pump cancellation; quota alone leaves this live only for the explicit native drain policy.</summary>
    internal CancellationToken PumpStopToken => _pumpStop.Token;

    /// <summary>Requires explicit final accounting before a native owner can use quota drainage for failed-run cleanup.</summary>
    internal bool Drained { get { lock (_gate) return _completion is { IsCompletedSuccessfully: true }
        && _completion.Result.Drained; } }

    /// <summary>Sticky actual aggregate-overflow data, also sampled by the currently reading pair.</summary>
    internal bool QuotaExceeded { get { lock (_gate) return _quotaExceeded; } }

    /// <summary>Registers both actual streams and their original task before either pump starts.</summary>
    /// <remarks>
    /// A collector or stream may participate only once in this run. Admission closes on failure or finalization;
    /// an invalid registration cancels existing pairs and never dispatches the rejected streams. At most the
    /// existing workload-ledger maximum of 64 pairs is retained. Streams remain caller-owned.
    /// </remarks>
    internal Task<SupervisionOutputReceipt> CollectAsync(SupervisionOutputCollector collector,
        Stream stdout, Stream stderr, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SupervisionOutputReceipt>? task = null;
        lock (_gate)
        {
            if (!_closed && !_disposed && _failure == SupervisionRunOutputFailure.None
                && _pairs.Count < SupervisionWorkRegistry.MaximumActiveWorkloads
                && !_collectors.Contains(collector) && !ReferenceEquals(stdout, stderr)
                && !_streams.Contains(stdout) && !_streams.Contains(stderr) && stdout.CanRead && stderr.CanRead)
            {
                _collectors.Add(collector);
                _streams.Add(stdout);
                _streams.Add(stderr);
                task = CollectRegisteredAsync(collector, stdout, stderr, token, dispatch.Task);
                _pairs.Add(task);
            }
        }
        if (task is null)
        {
            Fail(SupervisionRunOutputFailure.InvalidSequence);
            throw new InvalidOperationException("Run output registration is closed or invalid.");
        }
        dispatch.SetResult();
        return task;
    }

    private async Task<SupervisionOutputReceipt> CollectRegisteredAsync(SupervisionOutputCollector collector,
        Stream stdout, Stream stderr, CancellationToken token, Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        try
        {
            var receipt = await collector.CollectRegisteredAsync(stdout, stderr, this, token).ConfigureAwait(false);
            if (!receipt.Successful && !(DrainOnQuotaExceeded && receipt.Drained && QuotaExceeded))
                Fail(SupervisionRunOutputFailure.PairFailed);
            return receipt;
        }
        catch
        {
            Fail(SupervisionRunOutputFailure.PairFailed);
            throw;
        }
    }

    /// <summary>Charges one actual read even after another pair has failed; false forbids retaining its bytes.</summary>
    internal bool Charge(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        bool exceeded;
        lock (_gate)
        {
            _received = checked(_received + count);
            exceeded = _received > _limit;
            _quotaExceeded |= exceeded;
            if (!exceeded) return _failure == SupervisionRunOutputFailure.None;
        }
        Fail(SupervisionRunOutputFailure.QuotaExceeded);
        return false;
    }

    /// <summary>Signals all registered pairs, preserving the first failure and every original task.</summary>
    internal void Fail(SupervisionRunOutputFailure failure)
    {
        lock (_gate)
        {
            if (_failure == SupervisionRunOutputFailure.None) _failure = failure;
            if (failure != SupervisionRunOutputFailure.QuotaExceeded) _drainFailed = true;
            _closed = true;
            if (_disposed) return;
        }
        // Never hold the state gate while callbacks run. Cancel/Dispose must not race, and
        // final accounting joins this synchronous signal before sampling callback failures.
        lock (_signalGate)
        {
            lock (_gate) if (_disposed) return;
            Signal(_stop);
            if (!DrainOnQuotaExceeded || failure != SupervisionRunOutputFailure.QuotaExceeded) Signal(_pumpStop);
        }
    }

    private void Signal(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (Exception error) when (Recoverable(error))
        {
            lock (_gate)
            {
                _stopSignalFailed = true;
                _drainFailed = true;
                if (_failure == SupervisionRunOutputFailure.None) _failure = SupervisionRunOutputFailure.StopSignalFailed;
            }
        }
    }

    /// <summary>Irreversibly closes registration and joins the complete original task set exactly once.</summary>
    /// <remarks>
    /// There is no cancellable proxy wait. The native owner must contain ignored reads using its existing
    /// process/unit deadline. Failure and final byte accounting are sampled after every pair has settled.
    /// Repeated callers receive the same task and receipt, including failure.
    /// </remarks>
    internal Task<SupervisionRunOutputReceipt> CompleteAsync()
    {
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SupervisionRunOutputReceipt> completion;
        lock (_gate)
        {
            if (_completion is not null) return _completion;
            if (_disposed) throw new ObjectDisposedException(nameof(SupervisionRunOutputBudget));
            _closed = true;
            completion = _completion = CompleteCoreAsync(_pairs.ToArray(), dispatch.Task);
        }
        dispatch.SetResult();
        return completion;
    }

    private async Task<SupervisionRunOutputReceipt> CompleteCoreAsync(Task<SupervisionOutputReceipt>[] tasks, Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception error) when (Recoverable(error)) { Fail(SupervisionRunOutputFailure.PairFailed); }
        var pairs = tasks.Where(static task => task.IsCompletedSuccessfully)
            .Select(static task => task.Result).ToImmutableArray();
        lock (_signalGate)
        lock (_gate)
        {
            if (pairs.Length != tasks.Length || pairs.Sum(static pair => pair.ReceivedBytes) != _received)
            {
                _drainFailed = true;
                if (_failure == SupervisionRunOutputFailure.None) _failure = SupervisionRunOutputFailure.AccountingMismatch;
            }
            return new(pairs, tasks.Length, _received, _limit, _failure, _quotaExceeded, _stopSignalFailed, _drainFailed);
        }
    }

    /// <summary>Closes cancellation resources only after explicit finalization and all original joins.</summary>
    /// <remarks>Premature disposal rejects without releasing anything; the owner must still stop and join.</remarks>
    public void Dispose()
    {
        lock (_signalGate)
        lock (_gate)
        {
            if (_disposed) return;
            if (_completion?.IsCompleted != true) throw new InvalidOperationException("Run output collection must join before disposal.");
            _disposed = true;
            _stop.Dispose();
            _pumpStop.Dispose();
        }
    }

    private static bool Recoverable(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
