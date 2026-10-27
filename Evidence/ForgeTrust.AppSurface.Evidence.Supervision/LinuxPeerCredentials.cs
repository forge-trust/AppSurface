using System.Net.Sockets;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Reads the kernel peer tuple for an existing protected UNIX socket.</summary>
/// <remarks>This utility authenticates transport identity only; it creates no owner, connection or admission.</remarks>
internal static partial class LinuxPeerCredentials
{
    private const uint CredentialSize = 12;

    /// <summary>Reports whether both real and effective process UIDs are root.</summary>
    internal static bool IsCurrentProcessRoot() => GetUid() == 0 && GetEffectiveUid() == 0;

    /// <summary>Requires an existing socket's actual Linux peer tuple to match a retained nonroot identity.</summary>
    /// <param name="socket">The already-owned socket handle to inspect.</param>
    /// <param name="expectedPid">The retained process PID.</param>
    /// <param name="expectedUid">The retained nonroot UID.</param>
    /// <param name="expectedGid">The retained nonroot GID.</param>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 if root or peer credentials do not match.</exception>
    internal static void Require(SafeSocketHandle socket, int expectedPid, uint expectedUid, uint expectedGid)
    {
        uint length = CredentialSize;
        if (!IsCurrentProcessRoot() || expectedPid <= 0 || expectedUid == 0 || expectedGid == 0
            || GetPeerCredentials(socket, 1, 17, out var peer, ref length) != 0
            || length != CredentialSize || peer.Pid <= 0 || peer.Uid == 0 || peer.Gid == 0
            || peer.Pid != expectedPid || peer.Uid != expectedUid || peer.Gid != expectedGid)
            throw Rejected();
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD402", "The protected control peer could not be authenticated.");

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials
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
        out Credentials credentials, ref uint length);
}
