#if EVIDENCE_PRIVATE_N04 || EVIDENCE_PRIVATE_N07 || EVIDENCE_QUALIFICATION_DATA_TESTS
namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Pure detached-metadata predicates shared by fixed N04 and N07 checkpoint owners.</summary>
/// <remarks>These booleans do not inspect native objects, acquire ownership, or select an owner response.</remarks>
internal static class LinuxCheckpointMetadataPolicy
{
    /// <summary>Matches the owners' root-owned private directory or Unix-socket metadata rule.</summary>
    /// <remarks>Directory link count and length are intentionally ignored; the unsealed socket uses its original mask.</remarks>
    internal static bool IsPrivate(LinuxProtectedMetadata metadata, bool directory, bool sealedSocket = true) =>
        metadata.Uid == 0 && metadata.Gid == 0 && metadata.Inode != 0
        && (directory ? metadata.Mode == 0x41c0 : (metadata.Mode & 0xf000) == 0xc000)
        && (directory || (metadata.LinkCount == 1 && metadata.Length == 0
            && (sealedSocket ? metadata.Mode == 0xc180 : (metadata.Mode & 0x0c12) == 0)));

    /// <summary>Validates copied SO_PEERCRED values after the caller's original getsockopt-success check.</summary>
    /// <remarks>The caller retains native size initialization, syscall ordering, selector-specific rejection and PID pin assignment.</remarks>
    internal static bool ValidPeer(uint size, int pid, uint uid, uint gid, int selfPid, int pinnedPid) =>
        size == 12 && pid > 0 && pid != selfPid && uid == 0 && gid == 0
        && (pinnedPid == 0 || pinnedPid == pid);
}
#endif
