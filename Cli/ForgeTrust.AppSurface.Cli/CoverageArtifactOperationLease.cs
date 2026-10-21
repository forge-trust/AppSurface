using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Holds a fail-fast cooperative shared or exclusive lease on one physical output.</summary>
/// <remarks>
/// Unix uses nonblocking flock on the retained directory object; Windows uses LockFileEx on a persistent,
/// no-follow regular lock file. Older binaries and external readers do not participate in this protocol.
/// Multiple-output callers must acquire their strongest modes in deterministic physical-identity order.
/// </remarks>
internal sealed partial class CoverageArtifactOperationLease : IDisposable
{
    private readonly CoverageRunOutputLease _directory;
    private readonly SafeFileHandle? _windowsLock;
    private readonly string? _windowsLockIdentity;
    private NativeOverlappedLayout _overlapped;
    private bool _disposed;

    private CoverageArtifactOperationLease(CoverageRunOutputLease directory)
    {
        _directory = directory;
        PhysicalIdentity = directory.ArtifactDirectoryIdentity;
    }

    /// <summary>Gets the physical volume/object identity used to order compatible operations.</summary>
    internal string PhysicalIdentity { get; }

    private CoverageArtifactOperationLease(CoverageRunOutputLease directory, SafeFileHandle windowsLock)
        : this(directory)
    {
        _windowsLock = windowsLock;
        _windowsLockIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(windowsLock);
    }

    /// <summary>Acquires one nonblocking strongest-mode lease without performing output cleanup.</summary>
    /// <param name="existingDirectory">An existing directory validated without following ancestor links.</param>
    /// <param name="exclusive">Whether the operation mutates this physical output.</param>
    /// <returns>A disposable lease; contention fails with ASCOV202 before caller mutation.</returns>
    internal static CoverageArtifactOperationLease Acquire(string existingDirectory, bool exclusive)
    {
        var directory = CoverageRunOutputLease.AcquireArtifactDirectory(existingDirectory, false, false, allowWindowsRenameSharing: true)
            ?? throw new DirectoryNotFoundException("The operation directory must exist before lease acquisition.");
        SafeFileHandle? lockHandle = null;
        var unixLocked = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                lockHandle = directory.OpenArtifactOperationLock();
                var lease = new CoverageArtifactOperationLease(directory, lockHandle);
                if (!LockFile(lockHandle, (exclusive ? 2u : 0u) | 1u, 0, 1, 0, ref lease._overlapped))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is 33 or 158) throw Busy();
                    throw new IOException("Native operation locking failed.", new Win32Exception(error));
                }
                lease.VerifyBinding();
                return lease;
            }
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Native operation locking is unavailable.");
            if (Flock(directory.ArtifactDirectoryHandle.DangerousGetHandle().ToInt32(), (exclusive ? 2 : 1) | 4) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 11 or 35) throw Busy();
                throw new IOException("Native operation locking failed.", new Win32Exception(error));
            }
            unixLocked = true;
            directory.VerifyArtifactBinding();
            return new CoverageArtifactOperationLease(directory);
        }
        catch
        {
            if (unixLocked) _ = Flock(directory.ArtifactDirectoryHandle.DangerousGetHandle().ToInt32(), 8);
            lockHandle?.Dispose();
            directory.Dispose();
            throw;
        }
    }

    /// <summary>Rejects a changed physical output binding before a participating operation publishes.</summary>
    internal void VerifyBinding()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowsLock is not null)
            _directory.VerifyArtifactOperationLockBinding(_windowsLock, _windowsLockIdentity!);
        else
            _directory.VerifyArtifactBinding();
        if (_directory.ArtifactDirectoryIdentity != PhysicalIdentity) throw new IOException("Operation output identity changed.");
    }

    /// <summary>Releases native authority once; lock files remain persistent and are never removed to force admission.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Explicit unlock releases the shared open-file description even if a child was
        // forked immediately before disposal and has not reached close-on-exec yet.
        if (!OperatingSystem.IsWindows())
            _ = Flock(_directory.ArtifactDirectoryHandle.DangerousGetHandle().ToInt32(), 8);
        _windowsLock?.Dispose();
        _directory.Dispose();
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
