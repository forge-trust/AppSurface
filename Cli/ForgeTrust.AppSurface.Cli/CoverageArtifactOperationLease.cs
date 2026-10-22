using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Reports whether one native operation-lock request acquired the lock and its native error code.</summary>
/// <param name="Acquired">Whether the lock request succeeded.</param>
/// <param name="ErrorCode">The native error captured immediately after a failed request; zero on success.</param>
internal readonly record struct CoverageArtifactNativeLockResult(bool Acquired, int ErrorCode);

/// <summary>Provides per-acquisition observations for coverage artifact operation-lock orchestration.</summary>
/// <remarks>
/// Unset delegates use the production filesystem and native calls. Setting <see cref="IsWindows"/> selects only
/// the operation-lock branch: directory acquisition and physical identity inspection always use the real host OS.
/// To exercise the Windows branch on another OS, callers must supply a real open lock-file handle through
/// <see cref="OpenWindowsLock"/> and fake <see cref="TryWindowsLock"/> and
/// <see cref="VerifyWindowsLockBinding"/> delegates. Disposing that handle is the Windows lock-release operation.
/// These delegates verify orchestration only; they do not emulate or validate <c>LockFileEx</c> or Windows path
/// binding. Native acceptance must be verified separately on the target OS. Instances are passed to one
/// <see cref="CoverageArtifactOperationLease.Acquire(string, bool, CoverageArtifactOperationLeaseOptions?)"/>
/// call and are never stored globally.
/// </remarks>
internal sealed class CoverageArtifactOperationLeaseOptions
{
    /// <summary>Gets the optional lock-platform selector; <see langword="null"/> uses the real host OS.</summary>
    internal bool? IsWindows { get; init; }

    /// <summary>Gets an optional Unix flock operation receiving the retained directory descriptor and flags.</summary>
    /// <remarks>It receives lock flags <c>5</c> for shared and <c>6</c> for exclusive acquisition, and <c>8</c> for unlock.</remarks>
    internal Func<int, int, CoverageArtifactNativeLockResult>? UnixFlock { get; init; }

    /// <summary>Gets an optional Windows lock-file opener for the retained directory.</summary>
    /// <remarks>The returned handle is owned by the operation lease and is disposed on failure or lease disposal.</remarks>
    internal Func<CoverageRunOutputLease, SafeFileHandle>? OpenWindowsLock { get; init; }

    /// <summary>Gets an optional Windows lock request receiving the owned file handle and LockFileEx flags.</summary>
    /// <remarks>It receives flags <c>1</c> for shared and <c>3</c> for exclusive nonblocking acquisition.</remarks>
    internal Func<SafeFileHandle, uint, CoverageArtifactNativeLockResult>? TryWindowsLock { get; init; }

    /// <summary>Gets an optional verifier for the retained Unix directory binding.</summary>
    /// <remarks>It runs after successful acquisition and on each live lease binding verification.</remarks>
    internal Action<CoverageRunOutputLease>? VerifyUnixBinding { get; init; }

    /// <summary>Gets an optional verifier for the retained Windows lock-file binding.</summary>
    /// <remarks>
    /// It receives the retained directory, lock handle, and identity captured when the lease was constructed. The
    /// default verifier checks the current named lock file against that identity using the host's native APIs.
    /// </remarks>
    internal Action<CoverageRunOutputLease, SafeFileHandle, string>? VerifyWindowsLockBinding { get; init; }
}

/// <summary>Holds a fail-fast cooperative shared or exclusive lease on one physical output.</summary>
/// <remarks>
/// Unix uses nonblocking flock on the retained directory object; Windows uses LockFileEx on a persistent,
/// no-follow regular lock file. Older binaries and external readers do not participate in this protocol.
/// Multiple-output callers must acquire their strongest modes in deterministic physical-identity order.
/// </remarks>
internal sealed partial class CoverageArtifactOperationLease : IDisposable
{
    private readonly CoverageRunOutputLease _directory;
    private readonly CoverageArtifactOperationLeaseOptions _options;
    private readonly bool _isWindows;
    private readonly SafeFileHandle? _windowsLock;
    private readonly string? _windowsLockIdentity;
    private NativeOverlappedLayout _overlapped;
    private bool _disposed;

    private CoverageArtifactOperationLease(
        CoverageRunOutputLease directory,
        CoverageArtifactOperationLeaseOptions options,
        bool isWindows)
    {
        _directory = directory;
        _options = options;
        _isWindows = isWindows;
        PhysicalIdentity = directory.ArtifactDirectoryIdentity;
    }

    private CoverageArtifactOperationLease(
        CoverageRunOutputLease directory,
        CoverageArtifactOperationLeaseOptions options,
        bool isWindows,
        SafeFileHandle windowsLock)
        : this(directory, options, isWindows)
    {
        _windowsLock = windowsLock;
        _windowsLockIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(windowsLock);
    }

    /// <summary>Gets the physical volume/object identity used to order compatible operations.</summary>
    internal string PhysicalIdentity { get; }

    /// <summary>Acquires one nonblocking strongest-mode lease without performing output cleanup.</summary>
    /// <param name="existingDirectory">An existing directory validated without following ancestor links.</param>
    /// <param name="exclusive">Whether the operation mutates this physical output.</param>
    /// <param name="options">Optional per-call lock and binding observations; omitted in production.</param>
    /// <returns>A disposable lease; contention fails with ASCOV202 before caller mutation.</returns>
    /// <remarks>
    /// The directory is always opened and validated with the actual host's filesystem APIs. Supplying
    /// <paramref name="options"/> can substitute only the lock orchestration observations for this call. A forced
    /// Windows branch on a non-Windows host requires an injected lock-file opener, lock result, and binding observer,
    /// so it cannot accidentally call Windows native exports on that host.
    /// </remarks>
    internal static CoverageArtifactOperationLease Acquire(
        string existingDirectory,
        bool exclusive,
        CoverageArtifactOperationLeaseOptions? options = null)
    {
        options ??= new CoverageArtifactOperationLeaseOptions();
        var isWindows = options.IsWindows ?? OperatingSystem.IsWindows();
        if (isWindows && !OperatingSystem.IsWindows()
            && (options.OpenWindowsLock is null
                || options.TryWindowsLock is null
                || options.VerifyWindowsLockBinding is null))
        {
            throw new ArgumentException(
                "A simulated Windows lock requires injected open, lock, and binding delegates.",
                nameof(options));
        }

        var directory = CoverageRunOutputLease.AcquireArtifactDirectory(
                existingDirectory,
                createMissing: false,
                requirePrivate: false,
                allowWindowsRenameSharing: true)
            ?? throw new DirectoryNotFoundException("The operation directory must exist before lease acquisition.");
        SafeFileHandle? lockHandle = null;
        var unixLocked = false;
        try
        {
            if (isWindows)
            {
                lockHandle = options.OpenWindowsLock?.Invoke(directory) ?? directory.OpenArtifactOperationLock();
                var lease = new CoverageArtifactOperationLease(directory, options, isWindows, lockHandle);
                var flags = (exclusive ? 2u : 0u) | 1u;
                var attempt = TryWindowsLock(lockHandle, flags, lease, options);
                if (!attempt.Acquired)
                {
                    if (attempt.ErrorCode is 33 or 158) throw Busy();
                    throw new IOException("Native operation locking failed.", new Win32Exception(attempt.ErrorCode));
                }

                lease.VerifyBinding();
                return lease;
            }

            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && options.UnixFlock is null)
                throw new PlatformNotSupportedException("Native operation locking is unavailable.");

            var descriptor = directory.ArtifactDirectoryHandle.DangerousGetHandle().ToInt32();
            var unixAttempt = TryUnixFlock(descriptor, (exclusive ? 2 : 1) | 4, options);
            if (!unixAttempt.Acquired)
            {
                if (unixAttempt.ErrorCode is 11 or 35) throw Busy();
                throw new IOException("Native operation locking failed.", new Win32Exception(unixAttempt.ErrorCode));
            }

            unixLocked = true;
            VerifyUnixBinding(directory, options);
            return new CoverageArtifactOperationLease(directory, options, isWindows);
        }
        catch
        {
            try
            {
                if (unixLocked)
                {
                    var descriptor = directory.ArtifactDirectoryHandle.DangerousGetHandle().ToInt32();
                    _ = TryUnixFlock(descriptor, 8, options);
                }
            }
            finally
            {
                lockHandle?.Dispose();
                directory.Dispose();
            }

            throw;
        }
    }

    /// <summary>Rejects a changed physical output binding before a participating operation publishes.</summary>
    internal void VerifyBinding()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowsLock is not null)
        {
            if (_options.VerifyWindowsLockBinding is { } verifyLockBinding)
            {
                _directory.VerifyArtifactBinding();
                verifyLockBinding(_directory, _windowsLock, _windowsLockIdentity!);
                _directory.VerifyArtifactBinding();
            }
            else
            {
                _directory.VerifyArtifactOperationLockBinding(_windowsLock, _windowsLockIdentity!);
            }
        }
        else
        {
            VerifyUnixBinding(_directory, _options);
        }

        if (_directory.ArtifactDirectoryIdentity != PhysicalIdentity)
            throw new IOException("Operation output identity changed.");
    }

    /// <summary>Releases native authority once; lock files remain persistent and are never removed to force admission.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Explicit unlock releases the shared open-file description even if a child was
            // forked immediately before disposal and has not reached close-on-exec yet.
            if (!_isWindows)
            {
                var descriptor = _directory.ArtifactDirectoryHandle.DangerousGetHandle().ToInt32();
                _ = TryUnixFlock(descriptor, 8, _options);
            }
        }
        finally
        {
            _windowsLock?.Dispose();
            _directory.Dispose();
        }
    }

    private static CoverageArtifactNativeLockResult TryUnixFlock(
        int descriptor,
        int operation,
        CoverageArtifactOperationLeaseOptions options)
    {
        if (options.UnixFlock is { } flock) return flock(descriptor, operation);

        var result = Flock(descriptor, operation);
        return result == 0
            ? new CoverageArtifactNativeLockResult(Acquired: true, ErrorCode: 0)
            : new CoverageArtifactNativeLockResult(Acquired: false, ErrorCode: Marshal.GetLastPInvokeError());
    }

    private static CoverageArtifactNativeLockResult TryWindowsLock(
        SafeFileHandle handle,
        uint flags,
        CoverageArtifactOperationLease lease,
        CoverageArtifactOperationLeaseOptions options)
    {
        if (options.TryWindowsLock is { } tryLock) return tryLock(handle, flags);

        var acquired = LockFile(handle, flags, 0, 1, 0, ref lease._overlapped);
        return acquired
            ? new CoverageArtifactNativeLockResult(Acquired: true, ErrorCode: 0)
            : new CoverageArtifactNativeLockResult(Acquired: false, ErrorCode: Marshal.GetLastPInvokeError());
    }

    private static void VerifyUnixBinding(CoverageRunOutputLease directory, CoverageArtifactOperationLeaseOptions options)
    {
        if (options.VerifyUnixBinding is { } verifyBinding) verifyBinding(directory);
        else directory.VerifyArtifactBinding();
    }

    private static CoverageExecutionException Busy() => new(
        "ASCOV202 Coverage output is busy with a compatible operation. sideEffects=none; waitMs=0. Wait for that operation to finish, then retry once or choose a fresh --output. Do not delete lock files. See guides/coverage-storage.md#ascov202.", 1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedLayout
    {
        internal nint Internal;
        internal nint InternalHigh;
        internal uint Offset;
        internal uint OffsetHigh;
        internal nint Event;
    }

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int descriptor, int operation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "LockFileEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockFile(SafeFileHandle handle, uint flags, uint reserved, uint lowBytes, uint highBytes, ref NativeOverlappedLayout overlapped);
}
