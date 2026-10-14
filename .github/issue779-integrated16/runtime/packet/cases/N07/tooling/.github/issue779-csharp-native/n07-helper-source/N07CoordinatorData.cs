namespace Issue779.N07;

/// <summary>Closed argument/deadline/identity arithmetic only; this class opens nothing and grants no authority.</summary>
/// <remarks>Actual callers must still authenticate all native objects. Data controls use these exact
/// functions, but detached numbers do not become a lease, process pin, account or directory holder.</remarks>
internal static class N07CoordinatorData
{
    /// <summary>Five seconds are reserved from the inherited end, never appended to it.</summary>
    internal const long ReserveMilliseconds = 5000;

    /// <summary>Accepts only an existing original end with work remaining and at most the original 600 seconds.</summary>
    internal static bool ValidInitialDeadline(long end, long now) =>
        now >= 0 && end > now && end - now > ReserveMilliseconds && end - now <= 600000;

    /// <summary>Computes the work boundary without sampling or resetting a clock.</summary>
    internal static long WorkEnd(long originalEnd) => checked(originalEnd - ReserveMilliseconds);

    /// <summary>Checks work or cleanup against the same original absolute end; equality is expired.</summary>
    internal static bool WithinDeadline(long now, long originalEnd, bool cleanup) =>
        now >= 0 && now < (cleanup ? originalEnd : WorkEnd(originalEnd));

    /// <summary>Derives exactly one run root and one quarantine sibling; no path input is accepted.</summary>
    internal static ParentNames Names(string generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (generation.Length != 32 || generation.All(c => c == '0')
            || !generation.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException("n07-generation-rejected");
        return new("/run/appsurface-evidence-" + generation, "output", "output-n07-retained-" + generation);
    }

    /// <summary>Requires distinct nonzero inode numbers on the same actual device, without authenticating them.</summary>
    internal static bool DifferentOnSameDevice(uint oldMajor, uint oldMinor, ulong oldInode,
        uint newMajor, uint newMinor, ulong newInode) =>
        oldMajor == newMajor && oldMinor == newMinor && oldInode != 0 && newInode != 0 && oldInode != newInode;

    /// <summary>Returns only a bounded error from the fixed openat2 call; other failures remain unknown.</summary>
    /// <remarks>No exception message, path, process identity or nested exception is inspected. The number
    /// is private diagnostic data and cannot establish ownership, admission or a successful control.</remarks>
    internal static int? OpenFailureErrno(Exception? error) =>
        error is NativeOpenFailure failure ? failure.NativeErrno : null;

    /// <summary>Fixed private failure category that carries no path or exception text from the syscall.</summary>
    internal sealed class NativeOpenFailure(int nativeErrno) : IOException("n07-openat2-rejected")
    {
        /// <summary>Linux kernel errno range only; missing or out-of-range values stay unknown.</summary>
        internal int? NativeErrno { get; } = nativeErrno is > 0 and <= 4095 ? nativeErrno : null;
    }

    /// <summary>Fixed derived names, never user-selected paths or production permission.</summary>
    internal sealed record ParentNames(string Root, string Original, string Quarantine);
}
