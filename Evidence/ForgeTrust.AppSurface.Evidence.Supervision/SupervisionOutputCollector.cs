using System.Collections.Immutable;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed output-pump failure categories; no exception text or process output is included.</summary>
internal enum SupervisionOutputFailure
{
    /// <summary>Neither pump nor the caller has failed.</summary>
    None,
    /// <summary>Actual received bytes crossed the shared protected limit.</summary>
    QuotaExceeded,
    /// <summary>A read failed or returned an invalid byte count.</summary>
    ReadFailed,
    /// <summary>A pump or the caller observed cancellation.</summary>
    Cancelled,
}

/// <summary>Immutable observations from one joined output pump, without completion authority.</summary>
/// <param name="ReceivedBytes">Every byte actually returned by reads, including discarded or over-limit bytes.</param>
/// <param name="Prefix">An immutable bounded prefix; a rejected chunk is not retained.</param>
/// <param name="EndOfStream">Whether a read actually returned zero; an exception is never EOF.</param>
/// <param name="Failure">The pump's closed failure category.</param>
internal sealed record SupervisionOutputStreamReceipt(
    long ReceivedBytes,
    ImmutableArray<byte> Prefix,
    bool EndOfStream,
    SupervisionOutputFailure Failure)
{
    /// <summary>Actual received bytes not retained, including rejected and late in-flight bytes.</summary>
    internal long DiscardedBytes => ReceivedBytes - Prefix.Length;
}

/// <summary>Joined stdout/stderr observations, not an admission, lease or native acceptance capability.</summary>
/// <param name="Stdout">Observations from the actual joined stdout task.</param>
/// <param name="Stderr">Observations from the actual joined stderr task.</param>
/// <param name="ReceivedBytes">The shared counter, including bytes crossing the limit.</param>
/// <param name="ReceivedByteLimit">The root-selected protected limit for this pair.</param>
/// <param name="Failure">The first latched closed failure, preserved through sibling cancellation.</param>
/// <param name="QuotaExceeded">Whether any actual received charge crossed the shared limit.</param>
/// <param name="StopSignalFailed">Whether signalling owned cancellation threw a recoverable exception.</param>
internal sealed record SupervisionOutputReceipt(
    SupervisionOutputStreamReceipt Stdout,
    SupervisionOutputStreamReceipt Stderr,
    long ReceivedBytes,
    long ReceivedByteLimit,
    SupervisionOutputFailure Failure,
    bool QuotaExceeded,
    bool StopSignalFailed)
{
    /// <summary>Actual bytes discarded across both joined pumps.</summary>
    internal long DiscardedBytes => Stdout.DiscardedBytes + Stderr.DiscardedBytes;

    /// <summary>Whether both EOFs were observed, neither pump failed, and all received bytes fit the shared budget.</summary>
    /// <remarks>This predicate describes these two streams only; it establishes no process or cgroup exit.</remarks>
    internal bool Successful => Failure == SupervisionOutputFailure.None
        && !QuotaExceeded && !StopSignalFailed
        && Stdout.EndOfStream && Stderr.EndOfStream
        && Stdout.Failure == SupervisionOutputFailure.None && Stderr.Failure == SupervisionOutputFailure.None
        && ReceivedBytes == Stdout.ReceivedBytes + Stderr.ReceivedBytes
        && ReceivedBytes <= ReceivedByteLimit;
}

/// <summary>Owns a concurrent stdout/stderr pair with one protected received-byte budget and bounded prefixes.</summary>
/// <remarks>
/// Limits are root-selected build/run data, never permission derived from uploaded metadata. Each invocation
/// owns one pair; the run owner must account other pairs in its aggregate run budget. Streams remain owned
/// by the caller and are not disposed here. The caller's original root deadline supplies cancellation.
/// This type creates no timer, callback capability, admission or process-exit assertion.
/// </remarks>
internal sealed class SupervisionOutputCollector
{
    private const int ReadBufferBytes = 16 * 1024;
    private readonly long _receivedByteLimit;
    private readonly int _prefixByteLimit;
    private SupervisionCancellationPhaseObservation? _cancellationPhase;
    private SupervisionDescendantObservation? _descendant;
    private SupervisionN07PrecleanupObservation? _n07;

    /// <summary>Attaches fixed first-frame data before the original collector dispatch; no new reader.</summary>
    internal void ObserveCancellation(SupervisionCancellationPhaseObservation phase) => _cancellationPhase = phase;

    /// <summary>Attaches private descendant PID data before dispatch; it adds neither a pipe reader nor native authority.</summary>
    internal void ObserveDescendant(SupervisionDescendantObservation descendant) => _descendant = descendant;

    /// <summary>Attaches selected N07 data before dispatch, using the original stderr read task.</summary>
    internal void ObserveN07(SupervisionN07PrecleanupObservation observation) => _n07 = observation;

    /// <summary>Creates a collector whose protected limits may be lowered but never raised.</summary>
    /// <param name="receivedByteLimit">Positive shared limit, at most the Contracts 16 MiB maximum.</param>
    /// <param name="prefixByteLimit">Retained bytes per stream, from zero through the Contracts 1 MiB maximum.</param>
    internal SupervisionOutputCollector(
        long receivedByteLimit = EvidenceRunBudgetLimits.MaximumProcessOutputBytes,
        int prefixByteLimit = EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream)
    {
        if (receivedByteLimit <= 0 || receivedByteLimit > EvidenceRunBudgetLimits.MaximumProcessOutputBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(receivedByteLimit));
        }

        if (prefixByteLimit < 0 || prefixByteLimit > EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixByteLimit));
        }

        _receivedByteLimit = receivedByteLimit;
        _prefixByteLimit = prefixByteLimit;
    }

    /// <summary>Drains both streams and returns observations only after both owned tasks actually settle.</summary>
    /// <param name="stdout">Readable stdout, distinct from stderr; remains caller-owned.</param>
    /// <param name="stderr">Readable stderr; remains caller-owned.</param>
    /// <param name="cancellationToken">Cancellation from the existing root I/O deadline or run stop.</param>
    /// <returns>An immutable joined receipt. Cancellation and read failures yield unsuccessful observations.</returns>
    /// <remarks>
    /// A quota/read failure cancels the sibling, but no timeout or cancelled wait detaches either pump.
    /// If a stream ignores cancellation, this method still awaits it. The independent core/unit stop must
    /// contain that stall. Already returned bytes are charged before retention, including in-flight bytes
    /// returned after failure. Fatal runtime exceptions propagate only after Task.WhenAll has settled.
    /// </remarks>
    internal async Task<SupervisionOutputReceipt> CollectAsync(
        Stream stdout, Stream stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (ReferenceEquals(stdout, stderr) || !stdout.CanRead || !stderr.CanRead)
        {
            throw new ArgumentException("Two distinct readable output streams are required.");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new CollectionState(_receivedByteLimit, stop);
        // Independent dispatch also owns a Stream implementation that stalls before returning its ValueTask.
        var stdoutTask = Task.Run(() => PumpAsync(stdout, state, stop.Token, null, null, null), CancellationToken.None);
        var stderrTask = Task.Run(() => PumpAsync(stderr, state, stop.Token, _cancellationPhase, _descendant, _n07), CancellationToken.None);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            state.Latch(SupervisionOutputFailure.Cancelled);
        }

        return new SupervisionOutputReceipt(
            await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false),
            state.ReceivedBytes, _receivedByteLimit, state.Failure, state.QuotaExceeded, state.StopSignalFailed);
    }

    private async Task<SupervisionOutputStreamReceipt> PumpAsync(
        Stream stream, CollectionState state, CancellationToken cancellationToken,
        SupervisionCancellationPhaseObservation? phase, SupervisionDescendantObservation? descendant, SupervisionN07PrecleanupObservation? n07)
    {
        var buffer = new byte[ReadBufferBytes];
        using var prefix = new MemoryStream(_prefixByteLimit);
        long receivedBytes = 0;
        var eof = false;
        var failure = SupervisionOutputFailure.None;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count < 0 || count > buffer.Length)
                {
                    throw new IOException("Invalid output read count.");
                }

                if (count == 0)
                {
                    eof = true;
                    break;
                }

                receivedBytes += count;
                if (!state.Charge(count))
                {
                    failure = state.QuotaExceeded
                        ? SupervisionOutputFailure.QuotaExceeded : SupervisionOutputFailure.Cancelled;
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                phase?.Feed(buffer.AsSpan(0, count)); // After original received-byte charge, no second read.
                descendant?.Feed(buffer.AsSpan(0, count));
                n07?.Feed(buffer.AsSpan(0, count));
                var retain = Math.Min(count, _prefixByteLimit - (int)prefix.Length);
                prefix.Write(buffer, 0, retain);
            }
        }
        catch (OperationCanceledException)
        {
            failure = SupervisionOutputFailure.Cancelled;
            state.Stop(failure);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            failure = SupervisionOutputFailure.ReadFailed;
            state.Stop(failure);
        }

        phase?.Complete();
        descendant?.Complete();
        n07?.Complete(eof && failure == SupervisionOutputFailure.None);
        return new SupervisionOutputStreamReceipt(receivedBytes, ImmutableArray.CreateRange(prefix.ToArray()), eof, failure);
    }

    private sealed class CollectionState(long limit, CancellationTokenSource stop)
    {
        private long _receivedBytes;
        private int _failure;
        private int _quotaExceeded;
        private int _stopSignalFailed;

        internal long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
        internal SupervisionOutputFailure Failure => (SupervisionOutputFailure)Volatile.Read(ref _failure);
        internal bool QuotaExceeded => Volatile.Read(ref _quotaExceeded) != 0;
        internal bool StopSignalFailed => Volatile.Read(ref _stopSignalFailed) != 0;

        internal bool Charge(int count)
        {
            // Count actual bytes even when the sibling has already failed or this charge crosses the limit.
            var total = Interlocked.Add(ref _receivedBytes, count);
            if (total > limit)
            {
                Volatile.Write(ref _quotaExceeded, 1);
                Stop(SupervisionOutputFailure.QuotaExceeded);
                return false;
            }

            return Failure == SupervisionOutputFailure.None;
        }

        internal void Latch(SupervisionOutputFailure failure) =>
            Interlocked.CompareExchange(ref _failure, (int)failure, (int)SupervisionOutputFailure.None);

        internal void Stop(SupervisionOutputFailure failure)
        {
            Latch(failure);
            try
            {
                stop.Cancel();
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                Volatile.Write(ref _stopSignalFailed, 1);
            }
        }
    }
}
