using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed framing failures without peer details, supplied bytes or exception text.</summary>
internal enum ControlLineFailure
{
    /// <summary>A connection was reused or a response preceded a validated request.</summary>
    InvalidSequence,
    /// <summary>The bounded LF frame was missing, empty, excessive or contained trailing bytes.</summary>
    InvalidFrame,
    /// <summary>The closed request codec or response JSON rejected the frame.</summary>
    InvalidJson,
    /// <summary>An actual stream operation failed.</summary>
    IoFailed,
    /// <summary>Cancellation closed the stream; the original I/O is still awaited.</summary>
    Cancelled,
    /// <summary>Disposal failed or the stream was already closed.</summary>
    Closed,
}

/// <summary>Sanitized framing rejection; it supplies no authentication or completion authority.</summary>
internal sealed class ControlLineException : Exception
{
    /// <summary>Creates a fixed diagnostic with a closed reason and no inner exception.</summary>
    /// <param name="failure">Closed framing reason.</param>
    internal ControlLineException(ControlLineFailure failure) : base("Protected control framing was rejected.") =>
        Failure = failure;

    /// <summary>Gets the closed rejection reason.</summary>
    internal ControlLineFailure Failure { get; }
}

/// <summary>Owns one accepted Linux UNIX stream socket authenticated against the actual worker kernel tuple.</summary>
/// <remarks>
/// Only the socket factory constructs this type. Decoded credentials and portable framing tests cannot create it.
/// Each network read/write repeats SO_PEERCRED authentication. This is transport authentication, not admission,
/// procedure selection or an OS-exit assertion. The caller must supply the existing root deadline token.
/// </remarks>
internal sealed partial class LinuxControlConnection : IDisposable, IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxProcessIdentity _worker;
    private readonly int _pid;
    private readonly uint _uid;
    private readonly uint _gid;
    private readonly SupervisionControlLineFraming _framing;
    private int _socketDisposed;

    private LinuxControlConnection(Socket socket, LinuxOwnerActivation owner, LinuxProcessIdentity worker)
    {
        _socket = socket;
        _owner = owner;
        _worker = worker;
        _pid = checked((int)worker.Pid);
        _uid = worker.Uid;
        _gid = worker.Gid;
        RequirePeer();
        _framing = new(new AuthenticatedStream(this));
    }

    /// <summary>Takes ownership of an actual accepted socket, authenticating before any stream is exposed.</summary>
    /// <param name="socket">Connected UNIX stream socket; ownership transfers even when authentication rejects.</param>
    /// <param name="worker">Actual retained kernel worker identity, not a tuple parsed from JSON.</param>
    /// <param name="owner">Actual root owner; its original deadline bounds every native transport check.</param>
    /// <returns>A connection whose private constructor checked the actual kernel peer.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 when platform, socket or peer checks reject.</exception>
    /// <remarks>
    /// Requires Linux x64 with real/effective UID zero. .NET Socket.GetSocketOption translates managed option
    /// enums and rejects SO_PEERCRED; libc getsockopt uses SOL_SOCKET=1/SO_PEERCRED=17 with an exact 12-byte ucred
    /// and SafeSocketHandle ownership, as in the existing client. No caller-provided tuple bypass exists.
    /// </remarks>
    internal static LinuxControlConnection Accept(Socket socket, LinuxOwnerActivation owner, LinuxProcessIdentity worker)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || RuntimeInformation.OSArchitecture != Architecture.X64
                || GetUid() != 0 || GetEffectiveUid() != 0 || owner is null || worker is null
                || worker.Role != LinuxProcessSamplingRole.Worker || worker.Pid is 0 or > int.MaxValue
                || worker.Uid == 0 || worker.Gid == 0 || !socket.Connected
                || socket.AddressFamily != AddressFamily.Unix || socket.SocketType != SocketType.Stream)
                throw PeerRejected();
            owner.RequireControlIdentity(default);
            LinuxControlListenerPolicy.RequireWorkerGeneration(owner.RunId, worker.Unit, worker.Role);
            worker.Recheck(default);
            owner.RequireControlIdentity(default);
            return new(socket, owner, worker);
        }
        catch (Exception error) when (Recoverable(error))
        {
            socket.Dispose();
            throw PeerRejected();
        }
    }

    /// <summary>Reads and decodes exactly one bounded request, with authentication before every network read.</summary>
    /// <param name="cancellationToken">Existing root I/O/run deadline cancellation.</param>
    /// <returns>Detached request data only after successful framing and codec validation.</returns>
    /// <remarks>Cancellation closes the actual socket and still awaits the original network operation.</remarks>
    internal Task<EvidenceControlRequest> ReadRequestAsync(CancellationToken cancellationToken) =>
        _framing.ReadRequestAsync(cancellationToken);

    /// <summary>Writes one bounded JSON response after a validated request, then closes the socket.</summary>
    /// <param name="utf8Json">Root-produced JSON object data, not a caller-selected execution capability.</param>
    /// <param name="cancellationToken">Existing root I/O/run deadline cancellation.</param>
    internal Task WriteResponseAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken) =>
        _framing.WriteResponseAsync(utf8Json, cancellationToken);

    /// <summary>Initiates owned close once; a concurrent caller may return while that actual close is still pending.</summary>
    /// <remarks>The handler owner must await DisposeAsync and all existing I/O tasks before final draining.</remarks>
    public void Dispose() => _framing.Dispose();

    /// <summary>Joins the actual stream/socket close, rejecting a failed close without detaching I/O.</summary>
    /// <remarks>Outstanding ReadRequestAsync/WriteResponseAsync tasks must also be joined by their owner.</remarks>
    public ValueTask DisposeAsync() => _framing.DisposeAsync();

    private void DisposeSocket()
    {
        if (Interlocked.Exchange(ref _socketDisposed, 1) == 0) _socket.Dispose();
    }

    private void RequirePeer(CancellationToken token = default)
    {
        // Per-request cancellation cannot poison the retained native identity used by later cleanup.
        // These synchronous checks are contained by the independent owner and its original deadline.
        token.ThrowIfCancellationRequested();
        _owner.RequireControlIdentity(default);
        _worker.Recheck(default);
        uint length = 12;
        if (Volatile.Read(ref _socketDisposed) != 0 || GetUid() != 0 || GetEffectiveUid() != 0
            || GetPeerCredentials(_socket.SafeHandle, 1, 17, out var peer, ref length) != 0
            || length != 12 || peer.Pid <= 0 || peer.Uid == 0 || peer.Gid == 0
            || peer.Pid != _pid || peer.Uid != _uid || peer.Gid != _gid)
            throw PeerRejected();
        _owner.RequireControlIdentity(default);
        token.ThrowIfCancellationRequested();
    }

    private static EvidenceAdmissionException PeerRejected() =>
        new("ASEVD402", "The protected control peer could not be authenticated.");

    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        internal int Pid;
        internal uint Uid;
        internal uint Gid;
    }

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetPeerCredentials(SafeSocketHandle socket, int level, int option,
        out PeerCredentials credentials, ref uint length);

    private sealed class AuthenticatedStream : Stream
    {
        private readonly LinuxControlConnection _owner;
        private readonly NetworkStream _stream;
        private int _disposed;

        internal AuthenticatedStream(LinuxControlConnection owner)
        {
            _owner = owner;
            _stream = new(owner._socket, ownsSocket: false);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _owner.RequirePeer(cancellationToken);
            return _stream.ReadAsync(buffer, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _owner.RequirePeer(cancellationToken);
            return _stream.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { _stream.Dispose(); }
                finally { _owner.DisposeSocket(); }
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Counted single-attempt JSON-line framing over an owned stream, without peer authentication.</summary>
/// <remarks>
/// This intentional portable data seam cannot construct LinuxControlConnection. Each read is capped by the
/// remaining 64 KiB line allowance, including LF. Bytes following LF in the same read reject before decoding.
/// It does not drain future bytes or require client EOF: the existing client awaits a reply on the same socket.
/// CAS permits only one request attempt and one response; later frames can never dispatch another request.
/// Cancellation disposes the stream but still awaits its original operation, even if that stream ignores stop.
/// </remarks>
internal sealed class SupervisionControlLineFraming : IDisposable, IAsyncDisposable
{
    /// <summary>Maximum complete request line, including its single LF terminator.</summary>
    internal const int MaximumRequestLineBytes = EvidenceControlProtocol.MaximumRequestBytes;
    /// <summary>Checkpoint-one response line bound including LF; larger artifact replies are not yet supported.</summary>
    internal const int MaximumResponseLineBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _stream;
    private int _readAttempt;
    private int _writeAttempt;
    private int _validated;
    private int _closeStarted;
    private int _disposeFailed;
    private readonly TaskCompletionSource _closeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Takes ownership of a stream for framing data controls, without credentials or admission.</summary>
    /// <param name="stream">Actual readable/writable stream; disposed on failure, cancellation or completed response.</param>
    internal SupervisionControlLineFraming(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>Consumes at most one bounded LF request and returns detached codec data.</summary>
    /// <param name="cancellationToken">Existing owner deadline/stop token; no new timer is created.</param>
    /// <returns>One decoded request, never authentication or permission.</returns>
    internal async Task<EvidenceControlRequest> ReadRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Interlocked.CompareExchange(ref _readAttempt, 1, 0) != 0) throw Reject(ControlLineFailure.InvalidSequence);
            using var cancellation = cancellationToken.Register(static state => ((SupervisionControlLineFraming)state!).Dispose(), this);
            var frame = new byte[MaximumRequestLineBytes];
            var used = 0;
            while (used < frame.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOpen();
                var capacity = Math.Min(4096, frame.Length - used);
                var count = await _stream.ReadAsync(frame.AsMemory(used, capacity), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOpen();
                if (count <= 0 || count > capacity) throw Reject(ControlLineFailure.InvalidFrame);
                var newline = Array.IndexOf(frame, (byte)'\n', used, count);
                used += count;
                if (newline < 0) continue;
                if (newline == 0 || newline != used - 1 || Array.IndexOf(frame, (byte)'\r', 0, used) >= 0)
                    throw Reject(ControlLineFailure.InvalidFrame);
                var request = EvidenceControlProtocol.Parse(frame.AsMemory(0, newline));
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOpen();
                Volatile.Write(ref _validated, 1);
                return request;
            }
            throw Reject(ControlLineFailure.InvalidFrame);
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested && Recoverable(error))
        {
            Dispose();
            throw new OperationCanceledException("Protected control I/O was cancelled.", cancellationToken);
        }
        catch (ControlLineException) { Dispose(); throw; }
        catch (EvidenceAdmissionException) { throw Reject(ControlLineFailure.InvalidJson); }
        catch (Exception error) when (Recoverable(error)) { throw Reject(ControlLineFailure.IoFailed); }
        finally
        {
            if (Volatile.Read(ref _closeStarted) != 0)
                await _closeCompleted.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Validates, snapshots and writes one JSON object plus LF, then closes the owned stream.</summary>
    /// <param name="utf8Json">Response object data; payload must leave one byte for LF.</param>
    /// <param name="cancellationToken">Existing owner token; actual write completion is awaited.</param>
    internal async Task WriteResponseAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
    {
        try
        {
            if (Volatile.Read(ref _validated) != 1 || Interlocked.CompareExchange(ref _writeAttempt, 1, 0) != 0)
                throw Reject(ControlLineFailure.InvalidSequence);
            using var cancellation = cancellationToken.Register(static state => ((SupervisionControlLineFraming)state!).Dispose(), this);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            if (utf8Json.Length is 0 or >= MaximumResponseLineBytes) throw Reject(ControlLineFailure.InvalidFrame);
            var frame = new byte[utf8Json.Length + 1];
            utf8Json.CopyTo(frame);
            _ = StrictUtf8.GetCharCount(frame.AsSpan(0, utf8Json.Length));
            if (Array.IndexOf(frame, (byte)'\n', 0, utf8Json.Length) >= 0
                || Array.IndexOf(frame, (byte)'\r', 0, utf8Json.Length) >= 0)
                throw Reject(ControlLineFailure.InvalidFrame);
            using (var json = JsonDocument.Parse(frame.AsMemory(0, utf8Json.Length), new JsonDocumentOptions { MaxDepth = 8 }))
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw Reject(ControlLineFailure.InvalidJson);
            frame[^1] = (byte)'\n';
            await _stream.WriteAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            await DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested && Recoverable(error))
        {
            Dispose();
            throw new OperationCanceledException("Protected control I/O was cancelled.", cancellationToken);
        }
        catch (ControlLineException) { Dispose(); throw; }
        catch (JsonException) { throw Reject(ControlLineFailure.InvalidJson); }
        catch (DecoderFallbackException) { throw Reject(ControlLineFailure.InvalidJson); }
        catch (Exception error) when (Recoverable(error)) { throw Reject(ControlLineFailure.IoFailed); }
        finally
        {
            if (Volatile.Read(ref _closeStarted) != 0)
                await _closeCompleted.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Initiates the actual owned close exactly once; repeated callers do not establish its completion.</summary>
    /// <remarks>
    /// The first caller may block in Stream.Dispose. No lock is held. Concurrent callers must await DisposeAsync
    /// before treating close as settled. Read/write failure paths independently join this completion before returning.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0) return;
        Volatile.Write(ref _disposeFailed, 1);
        try
        {
            _stream.Dispose();
            Volatile.Write(ref _disposeFailed, 0);
        }
        catch (Exception error) when (Recoverable(error)) { /* Preserve the closed failure flag without raw text. */ }
        finally { _closeCompleted.TrySetResult(); }
    }

    /// <summary>Joins actual close and rejects its failure; no cancellation can detach this join.</summary>
    /// <remarks>This joins close only. Existing I/O tasks remain independently owned and must also settle.</remarks>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _closeCompleted.Task.ConfigureAwait(false);
        if (Volatile.Read(ref _disposeFailed) != 0) throw new ControlLineException(ControlLineFailure.Closed);
    }

    private void EnsureOpen()
    {
        if (Volatile.Read(ref _closeStarted) != 0 || Volatile.Read(ref _disposeFailed) != 0)
            throw Reject(ControlLineFailure.Closed);
    }
    private ControlLineException Reject(ControlLineFailure failure)
    {
        Dispose();
        return new(failure);
    }
    private static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
