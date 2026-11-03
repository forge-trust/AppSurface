using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Compares one actual directory name with metadata sampled through the caller's retained handle.</summary>
/// <remarks>
/// The caller supplies its original parent descriptor and exact resolution policy. This procedure opens and
/// stats that name with O_PATH|O_CLOEXEC, then closes only its temporary handle. It does not validate a
/// workspace, acquire ownership, translate native failures or issue admission. Callers retain their original
/// fixed rejection when comparison returns false; unsupported openat2 and native errors propagate unchanged.
/// </remarks>
internal static class LinuxNamedEntry
{
    private const ulong PathFlags = 0x200000 | 0x80000;

    /// <summary>Opens and stats the selected name before applying the shared exact-file/stable-directory rule.</summary>
    /// <param name="parent">The borrowed live parent descriptor, or the caller's original AT_FDCWD value.</param>
    /// <param name="name">The caller's selected name; no path normalization or fallback is performed.</param>
    /// <param name="expected">Previously sampled metadata; constructing it grants no retained handle.</param>
    /// <param name="resolution">The caller's unchanged openat2 resolution flags.</param>
    /// <returns>Whether the named object still has the required sampled identity and metadata.</returns>
    internal static bool Matches(int parent, string name, LinuxProtectedMetadata expected, ulong resolution)
    {
        using var named = OpenAt2(parent, name, PathFlags, 0, resolution);
        return LinuxNamedMetadataComparison.Matches(LinuxProtectedMetadata.From(StatFd(named)), expected);
    }
}
