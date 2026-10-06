using System.ComponentModel;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Sampled cgroup.events values; constructing these data establishes no kernel identity.</summary>
/// <param name="Populated">Whether the sampled cgroup or a descendant contained live processes.</param>
/// <param name="Frozen">Whether the sampled kernel freezer state was complete.</param>
internal readonly record struct LinuxCgroupEvents(bool Populated, bool Frozen);

/// <summary>Immutable cgroup sample, not a process lease, physical-exit receipt, or completion authority.</summary>
/// <param name="Exists">The exact generated leaf existed during descriptor-relative sampling.</param>
/// <param name="Populated">Recursive live-process state, or null when the leaf was absent.</param>
/// <param name="Frozen">Kernel freezer state, or null when the leaf was absent.</param>
/// <param name="DeviceMajor">Actual sampled kernel device major, or null for an absent leaf.</param>
/// <param name="DeviceMinor">Actual sampled kernel device minor, or null for an absent leaf.</param>
/// <param name="KernelInode">Actual sampled cgroup directory inode, or null for an absent leaf.</param>
internal sealed record LinuxCgroupSample(bool Exists, bool? Populated, bool? Frozen,
    uint? DeviceMajor, uint? DeviceMinor, ulong? KernelInode);

/// <summary>Samples one generated system.slice unit through actual root Linux x64 cgroup-v2 descriptors.</summary>
/// <remarks>
/// The fixed /sys/fs/cgroup mount is the only allowed mount boundary. Descendant opens disallow links,
/// magic links, escape, and mount crossings. No PID-list scan, caller pathname, fallback, timer, or write
/// exists. Missing/empty samples cannot prove exit: the owner must first join pending starts, stop/unit
/// procedures, and pumps, and then independently apply its completion policy and original deadline.
/// Read calls are synchronous; the external OS owner contains a native call that ignores cancellation.
/// See <see href="https://docs.kernel.org/admin-guide/cgroup-v2.html">cgroup-v2 documentation</see>
/// for recursive populated and frozen semantics. A collected utility unit can legitimately have no
/// retained leaf; worker admission still needs its separate actual live-process capture.
/// </remarks>
internal static class LinuxCgroupProbe
{
    /// <summary>Maximum complete cgroup.events record, before any parsing or text allocation.</summary>
    internal const int MaximumEventsBytes = 1024;
    /// <summary>Kernel cgroup-v2 filesystem magic, not evidence when supplied as metadata.</summary>
    internal const long Cgroup2Magic = 0x63677270;
    private const string MountPath = "/sys/fs/cgroup";
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong NamedFlags = 0x200000 | 0x80000;
    private const ulong ReadFlags = 0x80000 | 0x800;
    private const ulong MountResolution = 0x02 | 0x04;
    private const ulong DescendantResolution = MountResolution | 0x08 | 0x01;

    /// <summary>Reads exact generated unit data while retaining and rechecking the kernel parent and leaf.</summary>
    /// <param name="unit">Closed generated name; its creation alone supplies no unit ownership.</param>
    /// <param name="token">Original owner deadline/stop token, checked around each synchronous operation.</param>
    /// <returns>Sampled values only; absent samples retain null population/freezer/identity.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 for platform, metadata, I/O, or record rejection.</exception>
    /// <remarks>
    /// ENOENT is accepted only for the selected leaf beneath an already verified kernel parent, after
    /// another absence probe and parent/name checks. Pruning after a successful open rejects instead of
    /// returning an earlier empty value. Every acquired FD is owned before fallible metadata inspection.
    /// </remarks>
    internal static LinuxCgroupSample Read(LinuxUnitName unit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var handles = new List<SafeFileHandle>();
        LinuxCgroupSample? result = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            RequirePlatform();
            var mount = OpenNode(null, MountPath, true, handles, token);
            var parent = OpenNode(mount, "system.slice", true, handles, token);
            RequireNode(parent, token);
            var leaf = OpenSelectedLeaf(parent, unit.Value, handles, token);
            if (leaf is null)
            {
                RequireNode(parent, token);
                RequireAbsent(parent, unit.Value, token);
                RequireNode(parent, token);
                result = new(false, null, null, null, null, null);
            }
            else
            {
                var events = OpenNode(leaf, "cgroup.events", false, handles, token);
                RequireNode(events, token);
                var values = ParseEvents(ReadEvents(events.Handle, token));
                RequireNode(events, token);
                RequireNode(leaf, token);
                RequireNode(parent, token);
                result = new(true, values.Populated, values.Frozen, leaf.Metadata.DeviceMajor,
                    leaf.Metadata.DeviceMinor, leaf.Metadata.Inode);
            }
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException error) { failure = error; }
        catch (Exception) { failure = Rejected(); }
        finally
        {
            for (var i = handles.Count - 1; i >= 0; i--)
            {
                try { handles[i].Dispose(); }
                catch (Exception) { failure ??= Rejected(); }
            }
        }
        if (failure is OperationCanceledException) throw new OperationCanceledException(token);
        if (failure is not null || result is null) throw Rejected();
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Parses exactly one populated and one frozen LF row with closed 0/1 values.</summary>
    /// <param name="bytes">Bounded kernel-record data, not a native filesystem capability.</param>
    /// <returns>Detached values; unknown, duplicate, missing, or malformed rows reject.</returns>
    internal static LinuxCgroupEvents ParseEvents(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumEventsBytes) throw Rejected();
        bool? populated = null;
        bool? frozen = null;
        ReadOnlySpan<byte> populatedPrefix = "populated "u8;
        ReadOnlySpan<byte> frozenPrefix = "frozen "u8;
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            if (newline < 0) throw Rejected();
            var row = bytes[..newline];
            if (row.StartsWith(populatedPrefix))
            {
                if (populated is not null) throw Rejected();
                populated = Bit(row[populatedPrefix.Length..]);
            }
            else if (row.StartsWith(frozenPrefix))
            {
                if (frozen is not null) throw Rejected();
                frozen = Bit(row[frozenPrefix.Length..]);
            }
            else throw Rejected();
            bytes = bytes[(newline + 1)..];
        }
        if (populated is null || frozen is null) throw Rejected();
        return new(populated.Value, frozen.Value);
    }

    /// <summary>Checks sampled kernel type/access/link facts as data only; it cannot return a live node.</summary>
    /// <param name="value">Detached statx facts; zero pseudo-file size does not establish EOF.</param>
    /// <param name="filesystemType">Detached fstatfs type, required to be cgroup-v2.</param>
    /// <param name="directory">Whether the selected node is a retained directory or cgroup.events file.</param>
    internal static void RequireMetadata(LinuxProtectedMetadata value, long filesystemType, bool directory)
    {
        if (filesystemType != Cgroup2Magic || value.Inode == 0 || value.Uid != 0 || value.Gid != 0
            || (value.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || (value.Mode & 0x0c12) != 0
            || (directory ? (value.Mode & 0x140) != 0x140 || value.LinkCount < 2
                : (value.Mode & 0x100) == 0 || value.LinkCount != 1 || value.Length > MaximumEventsBytes))
            throw Rejected();
    }

    /// <summary>Compares immutable object/access identity, ignoring legitimate kernel child/event churn.</summary>
    /// <remarks>Both samples must independently pass metadata validation; identity alone cannot accept pruning.</remarks>
    /// <param name="before">Identity retained when the node was acquired.</param>
    /// <param name="after">Rechecked descriptor or exact named-node identity.</param>
    /// <returns>Identity equality only, with no filesystem or ownership authority.</returns>
    internal static bool SameObject(LinuxProtectedMetadata before, LinuxProtectedMetadata after) =>
        before.SameAncestorAs(after);

    /// <summary>Classifies exact ENOENT data only; the caller still must verify the retained kernel parent.</summary>
    /// <remarks>Exception text, permission errors, unsupported APIs, and missing ancestors prove no leaf absence.</remarks>
    /// <param name="error">The error from descriptor-relative acquisition.</param>
    /// <returns>Whether the native inner error is exact ENOENT, not whether a unit is settled.</returns>
    internal static bool IsAbsentError(Exception error) =>
        error is IOException { InnerException: Win32Exception { NativeErrorCode: 2 } };

    /// <summary>Distinguishes initial acquisition absence from every post-acquisition validation failure.</summary>
    /// <param name="acquire">Initial open only, after parent validation; it must not inspect or recheck a node.</param>
    /// <param name="validateAcquired">Registers acquired ownership first, then performs all validation.</param>
    /// <returns>False only for initial-open ENOENT; true means the procedures returned, not native ownership or exit.</returns>
    /// <remarks>
    /// This internal procedure seam returns no handle, node, lease, or authority. Portable controls exercise
    /// sequencing only. Production supplies the fixed descriptor-relative open and private node validation.
    /// Validation exceptions, including ENOENT from pruning, propagate to Read's fixed rejection boundary.
    /// </remarks>
    internal static bool AcquireThenValidateLeaf(Action acquire, Action validateAcquired)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(validateAcquired);
        try { acquire(); }
        catch (IOException error) when (IsAbsentError(error)) { return false; }
        validateAcquired();
        return true;
    }

    private static bool Bit(ReadOnlySpan<byte> value)
    {
        if (value.Length != 1 || (value[0] != (byte)'0' && value[0] != (byte)'1')) throw Rejected();
        return value[0] == (byte)'1';
    }

    private static KernelNode OpenNode(KernelNode? parent, string name, bool directory,
        List<SafeFileHandle> handles, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (parent is not null) RequireNode(parent, token);
        var handle = OpenAt2(parent is null ? -100 : Fd(parent.Handle), name,
            directory ? DirectoryFlags : ReadFlags, 0, parent is null ? MountResolution : DescendantResolution);
        handles.Add(handle);
        token.ThrowIfCancellationRequested();
        var metadata = Inspect(handle, directory, token);
        var node = new KernelNode(handle, parent, name, directory, metadata);
        RequireNode(node, token);
        return node;
    }

    private static KernelNode? OpenSelectedLeaf(KernelNode parent, string name,
        List<SafeFileHandle> handles, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireNode(parent, token);
        SafeFileHandle? handle = null;
        KernelNode? leaf = null;
        var acquired = AcquireThenValidateLeaf(
            () => handle = OpenAt2(Fd(parent.Handle), name, DirectoryFlags, 0, DescendantResolution),
            () =>
            {
                handles.Add(handle!); // Registered before cancellation, inspection, or named recheck can fail.
                token.ThrowIfCancellationRequested();
                var metadata = Inspect(handle!, true, token);
                leaf = new KernelNode(handle!, parent, name, true, metadata);
                RequireNode(leaf, token);
            });
        return acquired ? leaf ?? throw Rejected() : null;
    }

    private static void RequireNode(KernelNode node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.Parent is not null) RequireNode(node.Parent, token);
        var current = Inspect(node.Handle, node.Directory, token);
        if (!SameObject(node.Metadata, current)) throw Rejected();
        using var named = OpenAt2(node.Parent is null ? -100 : Fd(node.Parent.Handle), node.Name,
            NamedFlags, 0, node.Parent is null ? MountResolution : DescendantResolution);
        if (!SameObject(node.Metadata, Inspect(named, node.Directory, token))) throw Rejected();
        token.ThrowIfCancellationRequested();
    }

    private static LinuxProtectedMetadata Inspect(SafeFileHandle handle, bool directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (FstatFs(handle, out var fs) != 0) throw Rejected();
        token.ThrowIfCancellationRequested();
        var metadata = LinuxProtectedMetadata.From(StatFd(handle));
        RequireMetadata(metadata, fs.Type, directory);
        token.ThrowIfCancellationRequested();
        return metadata;
    }

    private static void RequireAbsent(KernelNode parent, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var present = OpenAt2(Fd(parent.Handle), name, NamedFlags, 0, DescendantResolution);
        }
        catch (IOException error) when (IsAbsentError(error)) { token.ThrowIfCancellationRequested(); return; }
        throw Rejected();
    }

    private static byte[] ReadEvents(SafeFileHandle handle, CancellationToken token)
    {
        var bytes = new byte[MaximumEventsBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = RandomAccess.Read(handle, bytes.AsSpan(length), length);
            token.ThrowIfCancellationRequested();
            if (count == 0) return bytes[..length];
            length += count;
            if (length > MaximumEventsBytes) throw Rejected();
        }
        throw Rejected();
    }

    private static void RequirePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || RuntimeInformation.OSArchitecture != Architecture.X64 || GetUid() != 0 || GetEffectiveUid() != 0)
            throw Rejected();
    }

    private static EvidenceAdmissionException Rejected() =>
        new("ASEVD410", "The selected cgroup-v2 sample is unavailable or changed.");

    // Private acquisition-only holder. Pure record/metadata controls cannot construct retained kernel nodes.
    private sealed record KernelNode(SafeFileHandle Handle, KernelNode? Parent, string Name,
        bool Directory, LinuxProtectedMetadata Metadata);

    // Linux x64 struct statfs: asm-generic/statfs.h, 120 bytes; only f_type is needed.
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct FileSystem { [FieldOffset(0)] internal long Type; }
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FstatFs(SafeFileHandle handle, out FileSystem value);
    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
}
