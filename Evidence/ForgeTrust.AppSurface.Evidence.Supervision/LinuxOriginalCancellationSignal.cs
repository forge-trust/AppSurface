using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>One compile-fixed signal operation for the original authenticated live worker only.</summary>
/// <remarks>
/// The server retains its original async task before dispatch. The pidfd is owned before SIGINT,
/// and identity is rechecked across pidfd acquisition. A syscall receipt does not prove token delivery.
/// No arbitrary PID, boolean capability, callback transport, timer, selector or signal retry exists.
/// The same root owner deadline supplies cancellation; CAP_KILL/syscall qualification remains pending.
/// </remarks>
internal static partial class LinuxOriginalCancellationSignal
{
    /// <summary>Signals the original holder once under its unchanged authenticated owner guards.</summary>
    internal static void Send(LinuxWorkerProcess worker, LinuxProcessIdentity identity,
        LinuxOwnerActivation owner, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException();
        owner.RequireControlIdentity(token);
        if (!ReferenceEquals(identity, worker.RequireWorker(token))) throw LinuxSystemdBackend.InvalidControl();
        identity.Recheck(token);
        var fd = PidFdOpen(434, checked((int)identity.Pid), 0);
        if (fd < 0) throw LinuxSystemdBackend.InvalidControl();
        using var retained = new SafeFileHandle((nint)fd, ownsHandle: true);
        // Live PID/starttime/UID4/GID4/cgroup and retained proc nodes must still match after acquisition.
        if (!ReferenceEquals(identity, worker.RequireWorker(token))) throw LinuxSystemdBackend.InvalidControl();
        identity.Recheck(token);
        owner.RequireControlIdentity(token);
        token.ThrowIfCancellationRequested();
        if (PidFdSendSignal(424, fd, 2, 0, 0) != 0) throw LinuxSystemdBackend.InvalidControl();
        // Worker may exit after successful delivery; never require a fabricated post-exit live sample.
        owner.RequireControlIdentity(token);
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial int PidFdOpen(long number, int pid, uint flags);
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial int PidFdSendSignal(long number, int pidfd, int signal, nint info, uint flags);
}
