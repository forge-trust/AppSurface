using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed pipe lifecycle failures without output, native errno or original exception text.</summary>
internal enum SupervisionOutputPipeFailure
{
    /// <summary>The platform or actual anonymous pipe descriptor checks failed.</summary>
    InvalidPipe,
    /// <summary>Collection was reused, never begun, or begun after disposal started.</summary>
    InvalidSequence,
    /// <summary>The original collector failed instead of returning joined observations.</summary>
    CollectionFailed,
    /// <summary>An owned read or local write close failed.</summary>
    CloseFailed,
}

/// <summary>Fixed pipe lifecycle rejection; it supplies no native completion or admission authority.</summary>
internal sealed class SupervisionOutputPipeException : Exception
{
    /// <summary>Creates a sanitized failure with no inner exception.</summary>
    internal SupervisionOutputPipeException(SupervisionOutputPipeFailure failure)
        : base("Protected output pipe ownership could not complete.") => Failure = failure;

    /// <summary>Gets the closed lifecycle failure.</summary>
    internal SupervisionOutputPipeFailure Failure { get; }
}

/// <summary>Owns actual root Linux x64 stdout/stderr anonymous pipes and their paired collector.</summary>
/// <remarks>
/// The private factory uses pipe2(O_CLOEXEC), validates access modes and FIFO identities, and transfers
/// read ends to owned FileStreams. No portable stream or decoded descriptor can construct this object.
/// These handles are output transport only, never a process, cgroup, admission or acceptance proof.
/// Begin collection before dispatch. Keep local write handles until the actual D-Bus start task has joined,
/// whether accepted or ambiguous; then close local copies. Systemd's transferred copies are independent.
/// Stop/contain the pending unit and its descendants before joining output and disposal. A writer that
/// remains alive can keep EOF pending: cancellation never supplies a fabricated EOF or detaches a pump.
/// </remarks>
internal sealed partial class LinuxOutputPipes : IAsyncDisposable
{
    private readonly SafeFileHandle _stdoutWrite;
    private readonly SafeFileHandle _stderrWrite;
    private readonly SupervisionOutputPipeOwnership _ownership;

    private LinuxOutputPipes(FileStream stdout, FileStream stderr,
        SafeFileHandle stdoutWrite, SafeFileHandle stderrWrite)
    {
        _stdoutWrite = stdoutWrite;
        _stderrWrite = stderrWrite;
        _ownership = new(stdout, stderr, stdoutWrite, stderrWrite);
    }

    /// <summary>Gets the borrowed stdout write handle for typed D-Bus WriteVariantHandle serialization.</summary>
    /// <remarks>Do not dispose, retarget or use after CloseWriteCopies; the actual start operation must join first.</remarks>
    internal SafeFileHandle StandardOutput { get { _ownership.RequireWritesOpen(); return _stdoutWrite; } }
    /// <summary>Gets the borrowed stderr write handle under the same start/join ownership contract.</summary>
    internal SafeFileHandle StandardError { get { _ownership.RequireWritesOpen(); return _stderrWrite; } }

    /// <summary>Creates two actual CLOEXEC anonymous pipes; no account, process or unit is started.</summary>
    /// <remarks>Requires real/effective root Linux x64; partial acquisition closes every acquired handle.</remarks>
    internal static LinuxOutputPipes Create()
    {
        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stdoutWrite = null;
        SafeFileHandle? stderrRead = null;
        SafeFileHandle? stderrWrite = null;
        FileStream? stdout = null;
        FileStream? stderr = null;
        try
        {
            LinuxProtectedDeployment.RequirePlatform();
            (stdoutRead, stdoutWrite) = CreatePair();
            (stderrRead, stderrWrite) = CreatePair();
            stdout = WrapOwnedRead(stdoutRead);
            stdoutRead = null; // FileStream owns the original read handle, without an extra descriptor copy.
            stderr = WrapOwnedRead(stderrRead);
            stderrRead = null;
            return new(stdout, stderr, stdoutWrite, stderrWrite);
        }
        catch (Exception error)
        {
            // Attempt all synchronous acquisition closes even when another close fails.
            foreach (var item in new IDisposable?[] { stderr, stdout, stderrRead, stdoutRead, stderrWrite, stdoutWrite })
            {
                try { item?.Dispose(); }
                catch (Exception closeError) when (SupervisionOutputPipeOwnership.Recoverable(closeError)) { }
            }
            if (!SupervisionOutputPipeOwnership.Recoverable(error)) throw;
            throw new SupervisionOutputPipeException(SupervisionOutputPipeFailure.InvalidPipe);
        }
    }

    /// <summary>Transfers an owned read handle to a 4096-byte buffered stream using the handle's actual mode.</summary>
    /// <param name="readHandle">An exclusively owned readable handle; the caller retains it if construction fails.</param>
    /// <returns>A read stream owning the same handle, without duplicating the native descriptor.</returns>
    /// <remarks>
    /// The three-argument constructor derives asynchronous mode from the SafeFileHandle. A raw Unix
    /// descriptor wrapped by SafeFileHandle is not marked asynchronous; forcing isAsync:true rejects it.
    /// ReadAsync remains available on the derived-mode stream and the paired collector owns every read.
    /// On Windows the constructor likewise follows the handle's mode; this helper does not change it.
    /// This intentional wrapping seam creates no LinuxOutputPipes instance or native identity authority.
    /// Production acquisition still validates platform, FIFO identity, access modes and CLOEXEC first.
    /// </remarks>
    internal static FileStream WrapOwnedRead(SafeFileHandle readHandle) =>
        new(readHandle, FileAccess.Read, bufferSize: 4096);

    /// <summary>Registers the one owned collector task before either pump can dispatch.</summary>
    /// <param name="token">Cancellation from the existing owner deadline/stop; no new timer is created.</param>
    /// <param name="receivedByteLimit">Shared pair maximum; defaults to 16 MiB and may only be lowered.</param>
    /// <param name="prefixByteLimit">Per-stream retained prefix; defaults to 1 MiB and may only be lowered.</param>
    /// <param name="cancellationPhase">Optional data-only observer fed by the original charged stderr pump;
    /// creates no second reader, independent task or process authority.</param>
    /// <remarks>Account utility callers may select 128 KiB/4 KiB. Run-wide accounting remains the owner's duty.</remarks>
    internal Task<SupervisionOutputReceipt> BeginCollectAsync(CancellationToken token,
        long receivedByteLimit = EvidenceRunBudgetLimits.MaximumProcessOutputBytes,
        int prefixByteLimit = EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream,
        SupervisionCancellationPhaseObservation? cancellationPhase = null) =>
        _ownership.BeginCollectAsync(token, receivedByteLimit, prefixByteLimit, cancellationPhase);

    /// <summary>Irreversibly closes both local write copies, only after the actual start task joined.</summary>
    /// <remarks>Attempts both closes and latches failure. This cannot close systemd's copies or stop a unit.</remarks>
    internal void CloseWriteCopies() => _ownership.CloseWriteCopies();

    /// <summary>Returns the original collector task; caller cancellation cannot replace its actual join.</summary>
    /// <remarks>Stop the owned unit/descendants first when cancellation or a stalled writer prevents completion.</remarks>
    internal Task<SupervisionOutputReceipt> JoinAsync() => _ownership.JoinAsync();

    /// <summary>Closes local writes, joins the original collector, then closes both reads exactly once.</summary>
    /// <remarks>
    /// Join the actual start and stop externally owned writers first. This can block while inherited writers
    /// ignore cancellation. Concurrent calls share the same disposal task, including its sticky failure.
    /// </remarks>
    public ValueTask DisposeAsync() => _ownership.DisposeAsync();

    private static unsafe (SafeFileHandle Read, SafeFileHandle Write) CreatePair()
    {
        var descriptors = new[] { -1, -1 };
        fixed (int* nativeDescriptors = descriptors)
        {
            if (Pipe2(nativeDescriptors, 0x80000) != 0)
                throw new SupervisionOutputPipeException(SupervisionOutputPipeFailure.InvalidPipe);
        }
        SafeFileHandle? read = null;
        SafeFileHandle? write = null;
        try
        {
            if (descriptors[0] < 0 || descriptors[1] < 0 || descriptors[0] == descriptors[1])
                throw new SupervisionOutputPipeException(SupervisionOutputPipeFailure.InvalidPipe);
            read = new SafeFileHandle((nint)descriptors[0], ownsHandle: true);
            descriptors[0] = -1;
            write = new SafeFileHandle((nint)descriptors[1], ownsHandle: true);
            descriptors[1] = -1;
            var readFlags = Fcntl(read, 3); // F_GETFL
            var writeFlags = Fcntl(write, 3);
            var readDescriptorFlags = Fcntl(read, 1); // F_GETFD; -1 is failure, not a set CLOEXEC bit.
            var writeDescriptorFlags = Fcntl(write, 1);
            if (readFlags < 0 || writeFlags < 0 || (readFlags & 3) != 0 || (writeFlags & 3) != 1
                || readDescriptorFlags < 0 || writeDescriptorFlags < 0
                || (readDescriptorFlags & 1) != 1 || (writeDescriptorFlags & 1) != 1)
                throw new SupervisionOutputPipeException(SupervisionOutputPipeFailure.InvalidPipe);
            var reader = StatFd(read);
            var writer = StatFd(write);
            if ((reader.Mode & 0xf000) != 0x1000 || (writer.Mode & 0xf000) != 0x1000 || reader.Inode == 0
                || reader.DeviceMajor != writer.DeviceMajor || reader.DeviceMinor != writer.DeviceMinor
                || reader.Inode != writer.Inode || reader.Uid != 0 || reader.Gid != 0 || writer.Uid != 0 || writer.Gid != 0)
                throw new SupervisionOutputPipeException(SupervisionOutputPipeFailure.InvalidPipe);
            return (read, write);
        }
        catch
        {
            try { read?.Dispose(); }
            finally
            {
                try { write?.Dispose(); }
                finally { CloseFd(descriptors[0]); CloseFd(descriptors[1]); }
            }
            throw;
        }
    }

    [LibraryImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    private static unsafe partial int Pipe2(int* descriptors, int flags);
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(SafeFileHandle handle, int command);
}

/// <summary>Portable owned-stream lifecycle primitive; it cannot construct or authenticate Linux pipes.</summary>
/// <remarks>
/// The constructor is an intentional testing seam. Supplied streams/local closers are ownership data only,
/// not a transport callback or protected capability. The Linux factory supplies real reads and write handles.
/// Only this owner closes reads, after the original collector joins. Do not recursively dispose this owner
/// from a supplied closer. Consumers must separately retain any accepted/ambiguous unit and stop its writers.
/// </remarks>
internal sealed class SupervisionOutputPipeOwnership : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly object _writeSync = new();
    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly IDisposable _stdoutWrite;
    private readonly IDisposable _stderrWrite;
    private Task<SupervisionOutputReceipt>? _collection;
    private Task? _disposal;
    private bool _disposing;
    private bool _writesClosed;
    private bool _writeCloseFailed;

    /// <summary>Takes ownership of two distinct readable streams and two distinct local write closers.</summary>
    internal SupervisionOutputPipeOwnership(Stream stdout, Stream stderr, IDisposable stdoutWrite, IDisposable stderrWrite)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(stdoutWrite);
        ArgumentNullException.ThrowIfNull(stderrWrite);
        if (ReferenceEquals(stdout, stderr) || ReferenceEquals(stdoutWrite, stderrWrite)
            || ReferenceEquals(stdout, stdoutWrite) || ReferenceEquals(stdout, stderrWrite)
            || ReferenceEquals(stderr, stdoutWrite) || ReferenceEquals(stderr, stderrWrite)
            || !stdout.CanRead || !stderr.CanRead) throw Rejected(SupervisionOutputPipeFailure.InvalidPipe);
        _stdout = stdout;
        _stderr = stderr;
        _stdoutWrite = stdoutWrite;
        _stderrWrite = stderrWrite;
    }

    /// <summary>Registers one collector before pump dispatch; invalid limit data does not consume the attempt.</summary>
    internal Task<SupervisionOutputReceipt> BeginCollectAsync(CancellationToken token,
        long receivedByteLimit = EvidenceRunBudgetLimits.MaximumProcessOutputBytes,
        int prefixByteLimit = EvidenceRunBudgetLimits.RetainedOutputPrefixBytesPerStream,
        SupervisionCancellationPhaseObservation? cancellationPhase = null)
    {
        var collector = new SupervisionOutputCollector(receivedByteLimit, prefixByteLimit);
        if (cancellationPhase is not null) collector.ObserveCancellation(cancellationPhase);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SupervisionOutputReceipt> task;
        lock (_sync)
        {
            if (_disposing || _collection is not null) throw Rejected(SupervisionOutputPipeFailure.InvalidSequence);
            task = CollectOwnedAsync(collector, gate.Task, token);
            _collection = task;
        }
        gate.SetResult();
        return task;
    }

    /// <summary>Returns the same owned task, never a timeout/cancelled proxy that detaches output.</summary>
    internal Task<SupervisionOutputReceipt> JoinAsync()
    {
        lock (_sync) return _collection ?? throw Rejected(SupervisionOutputPipeFailure.InvalidSequence);
    }

    /// <summary>Checks local write ownership before borrowed handles are offered to the start serializer.</summary>
    internal void RequireWritesOpen()
    {
        lock (_sync)
        {
            if (_disposing) throw Rejected(SupervisionOutputPipeFailure.InvalidSequence);
            lock (_writeSync)
                if (_writesClosed) throw Rejected(SupervisionOutputPipeFailure.InvalidSequence);
        }
    }

    /// <summary>Closes both local writers synchronously once; concurrent callers wait for actual close completion.</summary>
    internal void CloseWriteCopies()
    {
        lock (_writeSync)
        {
            if (!_writesClosed)
            {
                _writesClosed = true;
                foreach (var writer in new[] { _stdoutWrite, _stderrWrite })
                {
                    try { writer.Dispose(); }
                    catch (Exception error) when (Recoverable(error)) { _writeCloseFailed = true; }
                }
            }
            if (_writeCloseFailed) throw Rejected(SupervisionOutputPipeFailure.CloseFailed);
        }
    }

    /// <summary>Registers disposal once, joins output before read closure, and repeats the same failed task.</summary>
    public ValueTask DisposeAsync()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task disposal;
        lock (_sync)
        {
            if (_disposal is not null) return new ValueTask(_disposal);
            _disposing = true;
            disposal = DisposeOwnedAsync(gate.Task, _collection);
            _disposal = disposal;
        }
        gate.SetResult();
        return new ValueTask(disposal);
    }

    private async Task<SupervisionOutputReceipt> CollectOwnedAsync(SupervisionOutputCollector collector,
        Task dispatch, CancellationToken token)
    {
        await dispatch.ConfigureAwait(false);
        try { return await collector.CollectAsync(_stdout, _stderr, token).ConfigureAwait(false); }
        catch (Exception error) when (Recoverable(error)) { throw Rejected(SupervisionOutputPipeFailure.CollectionFailed); }
    }

    private async Task DisposeOwnedAsync(Task dispatch, Task<SupervisionOutputReceipt>? collection)
    {
        await dispatch.ConfigureAwait(false);
        SupervisionOutputPipeFailure? failure = null;
        try
        {
            try
            {
                try { CloseWriteCopies(); }
                catch (Exception error) when (Recoverable(error)) { failure = SupervisionOutputPipeFailure.CloseFailed; }
            }
            finally
            {
                // Even a fatal write-close failure does not bypass the original pump join.
                if (collection is not null)
                {
                    try { await collection.ConfigureAwait(false); }
                    catch (Exception error) when (Recoverable(error)) { failure ??= SupervisionOutputPipeFailure.CollectionFailed; }
                }
            }
        }
        finally
        {
            // Both pumps are settled (or none dispatched) before any read disposal begins.
            foreach (var reader in new[] { _stdout, _stderr })
            {
                try { await reader.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error)) { failure ??= SupervisionOutputPipeFailure.CloseFailed; }
            }
        }
        if (failure is not null) throw Rejected(failure.Value);
    }

    /// <summary>Identifies failures that may be sanitized after all owned operations have settled.</summary>
    internal static bool Recoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static SupervisionOutputPipeException Rejected(SupervisionOutputPipeFailure failure) => new(failure);
}
