using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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
    internal const string PipeEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_PIPE";
    internal const string ExecutionEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_EXECUTION_ID";
    internal const string NonceEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_NONCE";

    private const int MaximumPendingBegins = 128;
    private const int MaximumPipeInstances = EvidenceOwnedChild.MaximumChildren + MaximumPendingBegins + 1;
    private static readonly TimeSpan BeginDeadline = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly string _nonce;
    private readonly string _pipeName;
    private readonly TimeProvider _clock;
    private readonly Action? _beforeListenerCreation;
    private readonly Func<NamedPipeServerStream, CancellationToken, Task>? _waitForConnectionForTests;
    private readonly CancellationTokenSource _acceptStop = new();
    private readonly CancellationTokenSource _pendingStop = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private readonly ConcurrentDictionary<long, NamedPipeServerStream> _pendingPipes = new();
    private readonly ConcurrentDictionary<long, NamedPipeServerStream> _activePipes = new();
    private readonly TaskCompletionSource<bool> _settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listenerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptTask;
    private NamedPipeServerStream? _listener;
    private int _admitted;
    private int _active;
    private int _pending;
    private long _handlerId;
    private bool _enrollmentClosed;
    private bool _unknown;
    private bool _listenerUnavailable;
    private Task? _disposeTask;
    private ExceptionDispatchInfo? _fatalFailure;

    private EvidenceChildLifetimeSession(string executionId, TimeProvider clock, Action? beforeListenerCreation,
        Func<NamedPipeServerStream, CancellationToken, Task>? waitForConnectionForTests)
    {
        ExecutionId = executionId;
        _clock = clock;
        _beforeListenerCreation = beforeListenerCreation;
        _waitForConnectionForTests = waitForConnectionForTests;
        // Keep the Unix-domain socket path formed by BCL named pipes below the platform limit,
        // even when the system temporary-directory path is long.
        _pipeName = RandomNumberGenerator.GetHexString(8).ToLowerInvariant();
        _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Environment = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipeEnvironmentName] = _pipeName,
            [ExecutionEnvironmentName] = executionId,
            [NonceEnvironmentName] = _nonce,
        });
        _acceptTask = AcceptLoopAsync();
        _listenerReady.Task.GetAwaiter().GetResult();
    }

    /// <summary>Gets the caller-supplied unique execution ID.</summary>
    internal string ExecutionId { get; }

    /// <summary>Gets only the run-specific child-channel environment variables to propagate to owned children.</summary>
    internal IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Gets whether enrollment is closed and every admitted client reported actual completion without unknown state.</summary>
    internal bool IsSettled { get { lock (_gate) return _enrollmentClosed && _active == 0 && !_unknown; } }

    /// <summary>Gets whether an admitted client disconnected or otherwise failed to report completion.</summary>
    internal bool IsUnknown { get { lock (_gate) return _unknown; } }

    /// <summary>Gets whether new cooperative work can be admitted; listener failure alone never means a child escaped.</summary>
    internal bool CanAcceptChildren { get { lock (_gate) return !_enrollmentClosed && !_listenerUnavailable; } }

    /// <summary>Controlled unexpected handler failure seam for lifecycle tests.</summary>
    internal Action? BeforeChildAcknowledgementForTests { get; set; }

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
        if (executionId.Length > 128 || executionId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
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
            _pending = 0;
            UpdateSettlementLocked();
        }
        _acceptStop.Cancel();
        _pendingStop.Cancel();
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
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaximumPipeInstances,
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
                if (!_enrollmentClosed && _pending < MaximumPendingBegins)
                {
                    _pending++;
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
        var admitted = false;
        var completed = false;
        try
        {
            using var beginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pendingStop.Token);
            using var beginTimer = _clock.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(),
                beginCancellation, BeginDeadline, Timeout.InfiniteTimeSpan);
            var begin = await EvidenceOwnedChildProtocol.ReadFrameAsync(pipe, beginCancellation.Token).ConfigureAwait(false);
            var parts = begin?.Split('|');
            var valid = parts is { Length: 3 }
                && string.Equals(parts[0], "BEGIN", StringComparison.Ordinal)
                && string.Equals(parts[1], ExecutionId, StringComparison.Ordinal)
                && ValidNonce(parts[2]);

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
                    admitted = true;
                    if (_pendingPipes.TryRemove(handlerId, out _)) _pending--;
                    _activePipes[handlerId] = pipe;
                    childId = Guid.NewGuid();
                }
                else childId = default;
            }

            if (rejection is not null)
            {
                await EvidenceOwnedChildProtocol.TryWriteFrameAsync(pipe, "REJECT|" + rejection, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            beginTimer.Dispose();
            BeforeChildAcknowledgementForTests?.Invoke();
            await EvidenceOwnedChildProtocol.WriteFrameAsync(pipe, $"ACK|{childId:N}", CancellationToken.None).ConfigureAwait(false);
            var complete = await EvidenceOwnedChildProtocol.ReadFrameAsync(pipe, CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(complete, $"COMPLETE|{childId:N}", StringComparison.Ordinal))
                throw new IOException("Admitted child did not send its completion frame.");

            lock (_gate)
            {
                _active--;
                admitted = false;
                completed = true;
                UpdateSettlementLocked();
            }
            await EvidenceOwnedChildProtocol.WriteFrameAsync(pipe, $"DONE|{childId:N}", CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!admitted) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            if (admitted && !completed) MarkUnknown();
            lock (_gate)
            {
                if (_pendingPipes.TryRemove(handlerId, out _)) _pending--;
                _activePipes.TryRemove(handlerId, out _);
            }
            pipe.Dispose();
        }
    }

    private bool ValidNonce(string candidate)
    {
        var received = Encoding.ASCII.GetBytes(candidate);
        var expected = Encoding.ASCII.GetBytes(_nonce);
        return received.Length == expected.Length && CryptographicOperations.FixedTimeEquals(received, expected);
    }

    private void MarkUnknown()
    {
        lock (_gate)
        {
            _unknown = true;
            if (_active > 0) _active--;
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
        _acceptStop.Dispose();
        _pendingStop.Dispose();
        Volatile.Read(ref _fatalFailure)?.Throw();
    }

    private void RecordFatal(Exception exception)
        => Interlocked.CompareExchange(ref _fatalFailure, ExceptionDispatchInfo.Capture(exception), null);
}

internal static class EvidenceOwnedChildProtocol
{
    internal static async Task WriteFrameAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        if (bytes.Length > EvidenceOwnedChild.MaximumFrameBytes) throw new IOException("Child protocol frame exceeds its limit.");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task TryWriteFrameAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        try { await WriteFrameAsync(stream, value, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    internal static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[EvidenceOwnedChild.MaximumFrameBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return length == 0 ? null : throw new IOException("Truncated child protocol frame.");
            if (bytes[length++] == (byte)'\n') return Encoding.UTF8.GetString(bytes, 0, length - 1);
        }
        throw new IOException("Child protocol frame exceeds its limit.");
    }
}
