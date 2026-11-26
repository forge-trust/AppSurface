using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Tracks cooperative-v1 child enrollment and actual completion for one Evidence execution.</summary>
/// <remarks>
/// The named-pipe endpoint is current-user-only. The execution ID and random nonce authenticate
/// the invocation protocol; both are run-specific and must be omitted from content keys and logs.
/// This session proves only that admitted cooperative clients reported completion after their
/// owned task or root process settled. It cannot discover or contain non-enrolled descendants.
/// A disconnected admitted client permanently makes this session unknown.
/// </remarks>
internal sealed class EvidenceChildLifetimeSession : IAsyncDisposable
{
    internal const string PipeEnvironmentName = EvidenceOwnedChildChannel.PipeEnvironmentName;
    internal const string ExecutionEnvironmentName = EvidenceOwnedChildChannel.ExecutionEnvironmentName;
    internal const string NonceEnvironmentName = EvidenceOwnedChildChannel.NonceEnvironmentName;

    private const int MaximumPendingBegins = 128;
    private const int MaximumPipeInstances = EvidenceOwnedChild.MaximumChildren + MaximumPendingBegins + 1;
    private static readonly TimeSpan BeginDeadline = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly object _stopSourceGate = new();
    private readonly EvidenceOwnedChildChannel _channel;
    private readonly TimeProvider _clock;
    private readonly Action? _beforeListenerCreation;
    private readonly Func<NamedPipeServerStream, CancellationToken, Task>? _waitForConnectionForTests;
    private readonly CancellationTokenSource _acceptStop = new();
    private readonly CancellationTokenSource _pendingStop = new();
    private readonly TaskCompletionSource _stopCancellationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private readonly Dictionary<long, NamedPipeServerStream> _pendingPipes = new(); // Guarded by _gate.
    private readonly TaskCompletionSource<bool> _settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listenerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptTask;
    private NamedPipeServerStream? _listener;
    private int _admitted;
    private int _active;
    private long _handlerId;
    private bool _enrollmentClosed;
    private bool _unknown;
    private bool _listenerUnavailable;
    private Task? _disposeTask;
    private ExceptionDispatchInfo? _fatalFailure;

    private EvidenceChildLifetimeSession(string executionId, TimeProvider clock, Action? beforeListenerCreation,
        Func<NamedPipeServerStream, CancellationToken, Task>? waitForConnectionForTests)
    {
        _clock = clock;
        _beforeListenerCreation = beforeListenerCreation;
        _waitForConnectionForTests = waitForConnectionForTests;
        // Keep the Unix-domain socket path formed by BCL named pipes below the platform limit,
        // even when the system temporary-directory path is long.
        var pipeName = RandomNumberGenerator.GetHexString(8).ToLowerInvariant();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _channel = new(pipeName, executionId, nonce);
        _acceptTask = AcceptLoopAsync();
        _listenerReady.Task.GetAwaiter().GetResult();
    }

    /// <summary>Gets the caller-supplied unique execution ID.</summary>
    internal string ExecutionId => _channel.ExecutionId;

    /// <summary>Gets only the run-specific child-channel environment variables to propagate to owned children.</summary>
    internal IReadOnlyDictionary<string, string> Environment => _channel.Environment;

    /// <summary>Gets whether enrollment is closed and every admitted client reported actual completion without unknown state.</summary>
    internal bool IsSettled { get { lock (_gate) return _enrollmentClosed && _active == 0 && !_unknown; } }

    /// <summary>Gets whether an admitted client disconnected or otherwise failed to report completion.</summary>
    internal bool IsUnknown { get { lock (_gate) return _unknown; } }

    /// <summary>Gets whether new cooperative work can be admitted; listener failure alone never means a child escaped.</summary>
    internal bool CanAcceptChildren { get { lock (_gate) return !_enrollmentClosed && !_listenerUnavailable; } }

    /// <summary>Controlled unexpected handler failure seam for lifecycle tests.</summary>
    internal Action? BeforeChildAcknowledgementForTests { get; set; }

    /// <summary>Controlled pause between closing enrollment and canceling stop sources for lifecycle tests.</summary>
    internal Action? BeforeStopSourceCancellationForTests { get; set; }

    /// <summary>Starts the current-user-only local child enrollment channel.</summary>
    /// <param name="executionId">A unique printable ID for the execution; it is never part of a producer key.</param>
    /// <param name="clock">Optional time provider for bounded settlement waits.</param>
    /// <param name="beforeListenerCreation">Optional per-call fault seam before opening each listener; production omits it.</param>
    /// <param name="waitForConnectionForTests">Optional per-call replacement for the native listener wait, used by
    /// transport failure and shutdown tests. The session retains ownership of the supplied pipe and its cancellation
    /// token; the callback must return its actual operation task and must not dispose or retain the pipe after that task
    /// settles. Omission calls <see cref="NamedPipeServerStream.WaitForConnectionAsync(CancellationToken)"/> directly.
    /// Injected failures follow the ordinary listener recovery, custody, and disposal paths.</param>
    internal static EvidenceChildLifetimeSession Start(string executionId, TimeProvider? clock = null,
        Action? beforeListenerCreation = null,
        Func<NamedPipeServerStream, CancellationToken, Task>? waitForConnectionForTests = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        if (!EvidenceOwnedChildChannel.IsValidExecutionId(executionId))
            throw new ArgumentException("Execution ID must be at most 128 ASCII letters, digits, hyphens, or underscores.", nameof(executionId));
        return new EvidenceChildLifetimeSession(executionId, clock ?? TimeProvider.System, beforeListenerCreation, waitForConnectionForTests);
    }

    /// <summary>Closes further enrollment; existing admitted clients remain active until completion or become unknown.</summary>
    internal void CloseEnrollment()
    {
        NamedPipeServerStream? listener;
        NamedPipeServerStream[] pending;
        lock (_gate)
        {
            if (_enrollmentClosed) return;
            _enrollmentClosed = true;
            listener = _listener;
            _listener = null;
            pending = _pendingPipes.Values.ToArray();
            _pendingPipes.Clear();
            UpdateSettlementLocked();
        }
        try
        {
            BeforeStopSourceCancellationForTests?.Invoke();
            lock (_stopSourceGate)
            {
                _acceptStop.Cancel();
                _pendingStop.Cancel();
            }
        }
        finally
        {
            _stopCancellationCompleted.TrySetResult();
        }
        listener?.Dispose();
        foreach (var pipe in pending) pipe.Dispose();
    }

    /// <summary>Closes enrollment and waits for all admitted clients to complete, returning false on timeout or unknown state.</summary>
    /// <param name="grace">Finite positive join grace; expiry never claims settlement.</param>
    /// <param name="cancellationToken">Cancels only this wait, not enrolled work.</param>
    internal async Task<bool> WaitForSettlementAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        if (grace <= TimeSpan.Zero || grace == Timeout.InfiniteTimeSpan || grace.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(grace), "Settlement grace must be finite, positive, and within the platform timer range.");
        CloseEnrollment();
        try { return await _settlement.Task.WaitAsync(grace, _clock, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_acceptStop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                _beforeListenerCreation?.Invoke();
                pipe = new NamedPipeServerStream(_channel.PipeName, PipeDirection.InOut, MaximumPipeInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                or ArgumentOutOfRangeException or ArgumentException or System.Security.SecurityException)
            {
                lock (_gate) { _listenerUnavailable = true; UpdateSettlementLocked(); }
                _listenerReady.TrySetResult();
                return;
            }
            catch (Exception)
            {
                lock (_gate) { _listenerUnavailable = true; UpdateSettlementLocked(); }
                _listenerReady.TrySetResult();
                throw;
            }

            lock (_gate)
            {
                if (_enrollmentClosed)
                {
                    pipe.Dispose();
                    _listenerReady.TrySetResult();
                    return;
                }
                _listener = pipe;
                _listenerReady.TrySetResult();
            }

            try
            {
                var waiting = _waitForConnectionForTests is null
                    ? pipe.WaitForConnectionAsync(_acceptStop.Token)
                    : _waitForConnectionForTests(pipe, _acceptStop.Token);
                await waiting.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_acceptStop.IsCancellationRequested)
            { pipe.Dispose(); return; }
            catch (ObjectDisposedException) when (_acceptStop.IsCancellationRequested)
            { pipe.Dispose(); return; }
            catch (IOException)
            {
                pipe.Dispose();
                if (_acceptStop.IsCancellationRequested) return;
                continue;
            }
            catch (Exception)
            {
                pipe.Dispose();
                lock (_gate) { _listenerUnavailable = true; UpdateSettlementLocked(); }
                _listenerReady.TrySetResult();
                throw;
            }

            var id = Interlocked.Increment(ref _handlerId);
            var pendingAdded = false;
            lock (_gate)
            {
                if (ReferenceEquals(_listener, pipe)) _listener = null;
                if (!_enrollmentClosed && _pendingPipes.Count < MaximumPendingBegins)
                {
                    _pendingPipes[id] = pipe;
                    pendingAdded = true;
                }
            }
            if (!pendingAdded)
            {
                pipe.Dispose();
                continue;
            }
            var task = Task.Run(() => HandleConnectionAsync(id, pipe));
            _handlers[id] = task;
            _ = task.ContinueWith(static (completedTask, state) =>
            {
                var tuple = ((EvidenceChildLifetimeSession Session, long Id))state!;
                if (completedTask.IsFaulted)
                {
                    try { completedTask.GetAwaiter().GetResult(); }
                    catch (Exception exception) { tuple.Session.RecordFatal(exception); }
                }
                tuple.Session._handlers.TryRemove(tuple.Id, out _);
            }, (this, id), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(long handlerId, NamedPipeServerStream pipe)
    {
        var ownsEnrollment = false;
        try
        {
            using var beginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pendingStop.Token);
            string? begin;
            await using (var beginTimer = _clock.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(),
                beginCancellation, BeginDeadline, Timeout.InfiniteTimeSpan))
            {
                begin = await EvidenceOwnedChildWire.ReadFrameAsync(pipe, beginCancellation.Token).ConfigureAwait(false);
            }
            var valid = EvidenceOwnedChildWire.TryParseBegin(begin, out var beginExecutionId, out var beginNonce)
                && string.Equals(beginExecutionId, ExecutionId, StringComparison.Ordinal)
                && ValidNonce(beginNonce);

            Guid childId;
            string? rejection = null;
            lock (_gate)
            {
                if (!valid) rejection = "invalid";
                else if (_enrollmentClosed || _listenerUnavailable) rejection = "closed";
                else if (_admitted >= EvidenceOwnedChild.MaximumChildren) rejection = "capacity";
                if (rejection is null)
                {
                    _admitted++;
                    _active++;
                    ownsEnrollment = true;
                    _pendingPipes.Remove(handlerId);
                    childId = Guid.NewGuid();
                }
                else childId = default;
            }

            if (rejection is not null)
            {
                await TryWriteRejectionAsync(pipe, rejection).ConfigureAwait(false);
                return;
            }

            BeforeChildAcknowledgementForTests?.Invoke();
            await EvidenceOwnedChildWire.WriteFrameAsync(pipe,
                EvidenceOwnedChildWire.FormatAcknowledgement(childId), CancellationToken.None).ConfigureAwait(false);
            var complete = await EvidenceOwnedChildWire.ReadFrameAsync(pipe, CancellationToken.None).ConfigureAwait(false);
            if (!EvidenceOwnedChildWire.IsCompletion(complete, childId))
                throw new IOException("Admitted child did not send its completion frame.");

            ownsEnrollment = false;
            SettleEnrollment(completionConfirmed: true);
            await EvidenceOwnedChildWire.WriteFrameAsync(pipe,
                EvidenceOwnedChildWire.FormatDone(childId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ownsEnrollment) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            if (ownsEnrollment) SettleEnrollment(completionConfirmed: false);
            lock (_gate)
            {
                _pendingPipes.Remove(handlerId);
            }
            pipe.Dispose();
        }
    }

    private bool ValidNonce(string candidate)
    {
        var received = Encoding.ASCII.GetBytes(candidate);
        var expected = Encoding.ASCII.GetBytes(_channel.Nonce);
        return received.Length == expected.Length && CryptographicOperations.FixedTimeEquals(received, expected);
    }

    private void SettleEnrollment(bool completionConfirmed)
    {
        lock (_gate)
        {
            _active--;
            _unknown |= !completionConfirmed;
            UpdateSettlementLocked();
        }
    }

    private void UpdateSettlementLocked()
    {
        if (!_enrollmentClosed || _active != 0) return;
        _settlement.TrySetResult(!_unknown);
    }

    /// <summary>Stops the listener and joins its server and connection tasks; active disconnections become sticky unknown state.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        CloseEnrollment();
        await _stopCancellationCompleted.Task.ConfigureAwait(false);
        try { await _acceptTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_acceptStop.IsCancellationRequested) { }
        catch (Exception exception) { RecordFatal(exception); }
        while (!_handlers.IsEmpty)
        {
            var pending = _handlers.Values.ToArray();
            if (pending.Length == 0) break;
            foreach (var handler in pending)
            {
                try { await handler.ConfigureAwait(false); }
                catch (Exception exception) { RecordFatal(exception); }
            }
        }
        lock (_stopSourceGate)
        {
            _acceptStop.Dispose();
            _pendingStop.Dispose();
        }
        Volatile.Read(ref _fatalFailure)?.Throw();
    }

    private void RecordFatal(Exception exception)
        => Interlocked.CompareExchange(ref _fatalFailure, ExceptionDispatchInfo.Capture(exception), null);

    /// <summary>Attempts to report why a connection was refused before admission.</summary>
    /// <param name="stream">The unadmitted client's channel stream.</param>
    /// <param name="reason">The bounded rejection code.</param>
    /// <remarks>Expected transport failures leave the rejection in effect. Unexpected errors propagate to the owner.</remarks>
    internal static async Task TryWriteRejectionAsync(Stream stream, string reason)
    {
        try
        {
            await EvidenceOwnedChildWire.WriteFrameAsync(stream, EvidenceOwnedChildWire.FormatRejection(reason),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
}
