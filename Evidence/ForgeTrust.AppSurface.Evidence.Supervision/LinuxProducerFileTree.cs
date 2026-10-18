using System.Runtime.InteropServices;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Copies retained audited files into a borrowed native directory without issuing workspace authority.</summary>
/// <remarks>
/// The actual workspace owner authenticates its accounts and retained destination path before and after
/// this operation. This utility cannot adopt a path, create a workspace, grant admission or release accounts.
/// Source and destination root remain caller-owned. Only child handles acquired here are closed here.
/// Call serially with the same budget/deadline across subject and package trees. Failed reservations and
/// partial destination contents remain; the production owner quarantines them instead of retrying/adopting.
/// </remarks>
internal static class LinuxProducerFileTree
{
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong PathFlags = 0x200000 | 0x80000;
    private const ulong DestinationFileFlags = 0x01 | 0x40 | 0x80 | 0x20000 | 0x80000;
    private const ulong ChildResolution = 0x01 | 0x02 | 0x04 | 0x08;
    private const ushort PrivatePermissions = LinuxWorkspaceMetadata.ProducerDirectoryMode;
    private const ushort PrivateFilePermissions = LinuxWorkspaceMetadata.ProducerFileMode;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Copies the deployment's actual retained inventory using exclusive native destination opens.</summary>
    /// <param name="source">Original audited deployment, independently rechecked during its actual reads.</param>
    /// <param name="destination">Borrowed already authenticated destination root; never closed by this method.</param>
    /// <param name="uid">Caller-validated actual subject UID; a numeric value is not an account reservation.</param>
    /// <param name="gid">Caller-validated actual subject GID, with the same independent ownership obligation.</param>
    /// <param name="budget">Original irreversible shared node/byte reservation ledger.</param>
    /// <param name="deadline">Original input deadline, without renewal for either copy or any file.</param>
    /// <param name="token">Original work token; cancellation cannot refund earlier reservations or writes.</param>
    /// <remarks>Requires root Linux x64. Native exclusive-open failures remain IOException; the workspace factory
    /// retains its existing ASEVD402 translation. No supplied decoded inventory or metadata is accepted.</remarks>
    internal static void Copy(LinuxProtectedDeployment source, SafeFileHandle destination, uint uid, uint gid,
        LinuxProducerCopyBudget budget, LinuxInputDeadline deadline, CancellationToken token)
    {
        deadline.Check(token);
        LinuxProtectedDeployment.RequirePlatform();
        var names = source.FileNames;
        if (names.Count is < 1 or > LinuxProducerWorkspaceLayout.MaximumNodes) throw LinuxProducerWorkspaceLayout.Invalid();
        var directories = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
        var ownedDirectories = new List<SafeFileHandle>();
        budget.ReserveDirectory(); // The already prepared destination root participates in the full copy inventory.
        try
        {
            foreach (var relative in names)
            {
                deadline.Check(token);
                var parts = ValidateRelativeFile(relative);
                var parent = destination;
                var prefix = string.Empty;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    prefix = prefix.Length == 0 ? parts[i] : prefix + "/" + parts[i];
                    if (!directories.TryGetValue(prefix, out var handle))
                    {
                        budget.ReserveDirectory();
                        if (MkdirAt(Fd(parent), parts[i], PrivatePermissions) != 0)
                            throw LinuxProducerWorkspaceLayout.Invalid();
                        handle = OpenAt2(Fd(parent), parts[i], DirectoryFlags, 0, ChildResolution);
                        ownedDirectories.Add(handle);
                        SetOwnershipAndMode(handle, uid, gid,
                            PrivatePermissions);
                        var metadata = LinuxProtectedMetadata.From(StatFd(handle));
                        LinuxProducerWorkspaceLayout.RequireDirectory(metadata, uid,
                            gid, PrivatePermissions);
                        RequireSameNamed(parent, parts[i], metadata);
                        directories.Add(prefix, handle);
                    }
                    parent = handle;
                }

                deadline.Check(token);
                var expectedLength = source.RequireFileLength(relative, token, deadline);
                budget.ReserveFile(expectedLength);
                using var file = OpenAt2(Fd(parent), parts[^1], DestinationFileFlags, PrivateFilePermissions,
                    ChildResolution);
                SetOwnershipAndMode(file, uid, gid,
                    PrivateFilePermissions);
                source.CopyFile(relative, file, token, deadline);
                var copied = LinuxProtectedMetadata.From(StatFd(file));
                RequireCopiedFile(copied);
                if (copied.Length != (ulong)expectedLength) throw LinuxProducerWorkspaceLayout.Invalid();
            }
        }
        finally { CloseHandles(ownedDirectories); }
    }

    private static void RequireCopiedFile(LinuxProtectedMetadata value)
    {
        if (!LinuxWorkspaceMetadata.MatchesCopiedProducerFile(value))
            throw LinuxProducerWorkspaceLayout.Invalid();
    }

    private static string[] ValidateRelativeFile(string relative)
    {
        try
        {
            if (string.IsNullOrEmpty(relative) || StrictUtf8.GetByteCount(relative) > 4095
                || relative.Any(static c => char.IsControl(c) || c is '\\' or ':' or '\0'))
                throw LinuxProducerWorkspaceLayout.Invalid();
            var parts = relative.Split('/');
            if (parts.Any(static part => part.Length is < 1 or > 255 || part is "." or ".."))
                throw LinuxProducerWorkspaceLayout.Invalid();
            return parts;
        }
        catch (EncoderFallbackException) { throw LinuxProducerWorkspaceLayout.Invalid(); }
    }

    private static void SetOwnershipAndMode(SafeFileHandle handle, uint uid, uint gid, ushort permissions)
    {
        if (Fchown(handle, uid, gid) != 0 || Fchmod(handle, permissions) != 0)
            throw LinuxProducerWorkspaceLayout.Invalid();
    }

    private static void RequireSameNamed(SafeFileHandle parent, string name, LinuxProtectedMetadata expected)
    {
        using var named = OpenAt2(Fd(parent), name, PathFlags, 0, ChildResolution);
        if (!LinuxProtectedMetadata.From(StatFd(named)).SameAncestorAs(expected))
            throw LinuxProducerWorkspaceLayout.Invalid();
    }

    private static void CloseHandles(IEnumerable<SafeFileHandle> handles)
    {
        foreach (var handle in handles) handle.Dispose();
    }

    [DllImport("libc", EntryPoint = "fchown", SetLastError = true)]
    private static extern int Fchown(SafeFileHandle handle, uint uid, uint gid);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(SafeFileHandle handle, uint permissions);
}
