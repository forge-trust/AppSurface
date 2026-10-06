using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed workspace and evidence node roles; a role is comparison data, never a selected native object.</summary>
internal enum LinuxCustodyNodeKind
{
    /// <summary>The generation directory containing worker, output and raw-results.</summary>
    Generation,
    /// <summary>The root-owned worker control directory.</summary>
    Control,
    /// <summary>The root-owned broker directory.</summary>
    Broker,
    /// <summary>The worker-owned artifact parent.</summary>
    Output,
    /// <summary>The subject-owned private raw-results directory.</summary>
    RawResults,
    /// <summary>The exclusively allocated worker evidence slot.</summary>
    Slot,
    /// <summary>The fixed sealed worker descriptor.</summary>
    Descriptor,
    /// <summary>The fixed UNIX control socket.</summary>
    Socket,
    /// <summary>The fixed evidence plan JSON file.</summary>
    Plan,
    /// <summary>The fixed evidence manifest JSON file.</summary>
    Manifest,
    /// <summary>The fixed evidence summary JSON file.</summary>
    Summary,
}

/// <summary>Pure metadata and closed-inventory guards for checkpoint-one terminal custody.</summary>
/// <remarks>
/// These guards neither open nor seal objects, authenticate sampling, join consumers, release accounts or issue
/// admission/proof. The native holder must first apply the original account policy to actual retained samples,
/// stop/join all users, perform descriptor-relative sealing, and validate terminal samples and names again.
/// Matching metadata does not establish byte integrity; authentic hashes and successful evidence verification
/// remain separate. Partial slot inventories are permitted for failed/cancelled custody only.
/// </remarks>
internal static class LinuxCustodyData
{
    /// <summary>Maximum original sealed descriptor length.</summary>
    internal const ulong MaximumDescriptorBytes = 64 * 1024;
    /// <summary>Maximum original evidence JSON length, using the existing canonical JSON bound.</summary>
    internal const ulong MaximumEvidenceBytes = EvidenceCanonicalJson.MaximumInputBytes;

    /// <summary>Requires the exact live role, owner, group, permissions, type and file-size policy.</summary>
    /// <param name="kind">One defined closed node role.</param>
    /// <param name="value">Sampled metadata; constructing it confers no native ownership.</param>
    /// <param name="accounts">Consistent nonroot account comparison data, not a live account holder.</param>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 without supplied values or an inner exception.</exception>
    internal static void RequireOriginal(LinuxCustodyNodeKind kind, LinuxProtectedMetadata value,
        LinuxRunAccountSnapshot accounts)
    {
        RequireAccounts(accounts);
        RequireShape(kind, value);
        var uid = kind switch
        {
            LinuxCustodyNodeKind.Generation or LinuxCustodyNodeKind.Control or LinuxCustodyNodeKind.Broker
                or LinuxCustodyNodeKind.Descriptor or LinuxCustodyNodeKind.Socket => 0u,
            LinuxCustodyNodeKind.RawResults => accounts.SubjectUid,
            _ => accounts.WorkerUid,
        };
        var gid = kind == LinuxCustodyNodeKind.RawResults ? accounts.ResultsGid : accounts.WorkerGid;
        if (value.Uid != uid || value.Gid != gid || value.Mode != (Type(kind) | OriginalPermissions(kind)))
            throw Rejected();
    }

    /// <summary>Requires exact terminal root custody while preserving retained identity and content metadata.</summary>
    /// <param name="kind">The same previously validated role.</param>
    /// <param name="before">Actual original sample, independently checked by <see cref="RequireOriginal"/>.</param>
    /// <param name="after">Actual sample after the native holder's intentional ownership/permission changes.</param>
    /// <remarks>
    /// Only UID, GID, permission bits and ctime may change. Device, inode, type, links, length and mtime must match.
    /// This comparison cannot substitute for the initial account-policy check or retained byte/name verification.
    /// </remarks>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 without supplied values or an inner exception.</exception>
    internal static void RequireTerminal(LinuxCustodyNodeKind kind, LinuxProtectedMetadata before,
        LinuxProtectedMetadata after)
    {
        RequireShape(kind, before);
        RequireShape(kind, after);
        var permissions = IsDirectory(kind) ? 0x1c0 : kind == LinuxCustodyNodeKind.Socket ? 0x180 : 0x100;
        if (after.Uid != 0 || after.Gid != 0 || after.Mode != (Type(kind) | permissions)
            || before.DeviceMajor != after.DeviceMajor || before.DeviceMinor != after.DeviceMinor
            || before.Inode != after.Inode || before.LinkCount != after.LinkCount || before.Length != after.Length
            || before.ModifySeconds != after.ModifySeconds || before.ModifyNanoseconds != after.ModifyNanoseconds)
            throw Rejected();
    }

    /// <summary>Requires a duplicate-free, at-most-three-name inventory for one fixed directory role.</summary>
    /// <param name="kind">A directory role; file, socket and undefined roles reject even empty inventories.</param>
    /// <param name="names">Actual decoded child-name comparison data; order is immaterial.</param>
    /// <remarks>
    /// Output may lack its evidence slot; a slot may contain any subset of the three fixed JSON names after failure.
    /// Successful evidence verification must independently require the complete three-file set and valid contents.
    /// </remarks>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD402 without supplied names or an inner exception.</exception>
    internal static void RequireInventory(LinuxCustodyNodeKind kind, IReadOnlyList<string> names)
    {
        if (!IsDirectory(kind) || names is null || names.Count is < 0 or > 3) throw Rejected();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (unique.Count >= 3 || !AllowedName(kind, name) || !unique.Add(name)) throw Rejected();
        var valid = kind switch
        {
            LinuxCustodyNodeKind.Generation => unique.SetEquals(["worker", "output", "raw-results"]),
            LinuxCustodyNodeKind.Control => unique.SetEquals(["broker", "worker-control.json"]),
            LinuxCustodyNodeKind.Broker => unique.SetEquals(["control.sock"]),
            LinuxCustodyNodeKind.RawResults => unique.Count == 0,
            LinuxCustodyNodeKind.Output => unique.Count == 0 || unique.SetEquals(["evidence"]),
            LinuxCustodyNodeKind.Slot => true, // Every retained name was checked; failed custody may be partial.
            _ => false,
        };
        if (!valid) throw Rejected();
    }

    private static bool AllowedName(LinuxCustodyNodeKind kind, string? name) => kind switch
    {
        LinuxCustodyNodeKind.Generation => name is "worker" or "output" or "raw-results",
        LinuxCustodyNodeKind.Control => name is "broker" or "worker-control.json",
        LinuxCustodyNodeKind.Broker => name == "control.sock",
        LinuxCustodyNodeKind.Output => name == "evidence",
        LinuxCustodyNodeKind.Slot => name is "evidence-plan.json" or "evidence-manifest.json" or "evidence-summary.json",
        _ => false,
    };

    private static void RequireAccounts(LinuxRunAccountSnapshot accounts)
    {
        if (accounts is null || accounts.WorkerUid is 0 or uint.MaxValue || accounts.SubjectUid is 0 or uint.MaxValue
            || accounts.WorkerUid == accounts.SubjectUid || accounts.WorkerGid is 0 or uint.MaxValue
            || accounts.SubjectGid is 0 or uint.MaxValue || accounts.ResultsGid is 0 or uint.MaxValue
            || accounts.WorkerGid == accounts.SubjectGid || accounts.WorkerGid == accounts.ResultsGid
            || accounts.SubjectGid == accounts.ResultsGid) throw Rejected();
    }

    private static void RequireShape(LinuxCustodyNodeKind kind, LinuxProtectedMetadata value)
    {
        if (value.Inode == 0 || value.LinkCount == 0 || (value.Mode & 0xf000) != Type(kind)
            || value.ChangeNanoseconds >= 1_000_000_000 || value.ModifyNanoseconds >= 1_000_000_000)
            throw Rejected();
        if (IsDirectory(kind)) return;
        if (value.LinkCount != 1 || (kind == LinuxCustodyNodeKind.Socket ? value.Length != 0
            : value.Length is 0 || value.Length > (kind == LinuxCustodyNodeKind.Descriptor
                ? MaximumDescriptorBytes : MaximumEvidenceBytes))) throw Rejected();
    }

    private static bool IsDirectory(LinuxCustodyNodeKind kind) => kind is LinuxCustodyNodeKind.Generation
        or LinuxCustodyNodeKind.Control or LinuxCustodyNodeKind.Broker or LinuxCustodyNodeKind.Output
        or LinuxCustodyNodeKind.RawResults or LinuxCustodyNodeKind.Slot;

    private static int Type(LinuxCustodyNodeKind kind) => kind switch
    {
        LinuxCustodyNodeKind.Generation or LinuxCustodyNodeKind.Control or LinuxCustodyNodeKind.Broker
            or LinuxCustodyNodeKind.Output or LinuxCustodyNodeKind.RawResults or LinuxCustodyNodeKind.Slot => 0x4000,
        LinuxCustodyNodeKind.Socket => 0xc000,
        LinuxCustodyNodeKind.Descriptor or LinuxCustodyNodeKind.Plan or LinuxCustodyNodeKind.Manifest
            or LinuxCustodyNodeKind.Summary => 0x8000,
        _ => throw Rejected(),
    };

    private static int OriginalPermissions(LinuxCustodyNodeKind kind) => kind switch
    {
        LinuxCustodyNodeKind.Generation => 0x1e8, // 0750.
        LinuxCustodyNodeKind.Control or LinuxCustodyNodeKind.Broker or LinuxCustodyNodeKind.RawResults => 0x1c8, // 0710.
        LinuxCustodyNodeKind.Output or LinuxCustodyNodeKind.Slot => 0x1c0, // 0700.
        LinuxCustodyNodeKind.Descriptor => 0x120, // 0440.
        LinuxCustodyNodeKind.Socket => 0x1b0, // 0660.
        LinuxCustodyNodeKind.Plan or LinuxCustodyNodeKind.Manifest or LinuxCustodyNodeKind.Summary => 0x180, // 0600.
        _ => throw Rejected(),
    };

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD402", "The protected custody metadata or inventory was rejected.");
}
