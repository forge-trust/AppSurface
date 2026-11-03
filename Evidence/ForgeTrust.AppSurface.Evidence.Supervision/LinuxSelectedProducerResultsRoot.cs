using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Retains the original native no-follow descriptor chain for a selected producer result directory.</summary>
/// <remarks>
/// This holder performs filesystem identity checks only. The authentic producer custody factory must first
/// authenticate its input, owner, account, workspace and settled producer, then select its actual results token.
/// Opening a directory here cannot create that producer, custody capability, admission or a successful report.
/// The caller retains its original deadline and must join all readers before disposing this chain.
/// </remarks>
internal sealed class LinuxSelectedProducerResultsRoot : IDisposable
{
    private const uint DirectoryMode = 0x41c0; // 0700.
    private const ulong PathFlags = 0x200000 | 0x80000; // O_PATH | O_CLOEXEC.
    private const ulong DirectoryFlags = 0x10000 | 0x80000; // O_DIRECTORY | O_CLOEXEC.
    private const ulong AncestorResolution = 0x02 | 0x04 | 0x08;

    private readonly List<DirectoryIdentity> _chain;
    private readonly uint _uid;
    private readonly uint _gid;
    private bool _closed;

    private LinuxSelectedProducerResultsRoot(List<DirectoryIdentity> chain, uint uid, uint gid)
    { _chain = chain; _uid = uid; _gid = gid; }

    /// <summary>Gets the actual retained selected directory; never an owner or report capability.</summary>
    internal SafeFileHandle Root => _chain[^1].Handle;
    /// <summary>Gets the original kernel metadata sampled from the selected directory descriptor.</summary>
    internal LinuxProtectedMetadata Metadata => _chain[^1].Metadata;

    /// <summary>Opens the exact ancestor chain with mandatory openat2 and compares the selected kernel identity.</summary>
    internal static LinuxSelectedProducerResultsRoot Open(string path, LinuxProtectedMetadata expectedRoot,
        uint uid, uint gid, LinuxInputDeadline deadline, CancellationToken token)
    {
        LinuxProtectedDeployment.ValidateAbsolute(path);
        LinuxRetainedReportSet.RequireRootIdentity(expectedRoot, expectedRoot, uid, gid);
        if (expectedRoot.Mode != DirectoryMode) throw LinuxProducerArtifactCustody.Invalid();
        var chain = new List<DirectoryIdentity>();
        SafeFileHandle? pending = null;
        try
        {
            deadline.Check(token);
            pending = OpenAt2(-100, "/", DirectoryFlags, 0, 0);
            var rootMetadata = LinuxProtectedMetadata.From(StatFd(pending));
            deadline.Check(token);
            rootMetadata.RequireAncestor(false);
            chain.Add(new(pending, null, "/", rootMetadata, IsSelectedRoot: false));
            pending = null;
            var parts = path[1..].Split('/');
            if (parts.Length < 2) throw LinuxProducerArtifactCustody.Invalid();
            for (var index = 0; index < parts.Length; index++)
            {
                deadline.Check(token);
                var parent = chain[^1];
                pending = OpenAt2(Fd(parent.Handle), parts[index], DirectoryFlags, 0, AncestorResolution);
                var metadata = LinuxProtectedMetadata.From(StatFd(pending));
                deadline.Check(token);
                if (index == parts.Length - 1)
                {
                    LinuxReportCustodyMetadata.RequireSelectedRoot(metadata, expectedRoot, uid, gid);
                }
                else if (index == parts.Length - 2)
                {
                    LinuxReportCustodyMetadata.RequireResultsParent(metadata);
                }
                else
                {
                    metadata.RequireAncestor(false);
                }
                chain.Add(new(pending, parent, parts[index], metadata, index == parts.Length - 1));
                pending = null;
            }
            var result = new LinuxSelectedProducerResultsRoot(chain, uid, gid);
            result.Recheck(deadline, token);
            return result;
        }
        catch
        {
            pending?.Dispose();
            foreach (var node in chain.AsEnumerable().Reverse()) node.Handle.Dispose();
            throw;
        }
    }

    /// <summary>Rechecks every retained descriptor and its original parent name without renewing the deadline.</summary>
    internal void Recheck(LinuxInputDeadline deadline, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        foreach (var node in _chain)
        {
            deadline.Check(token);
            var current = LinuxProtectedMetadata.From(StatFd(node.Handle));
            deadline.Check(token);
            if (node.IsSelectedRoot)
            {
                LinuxReportCustodyMetadata.RequireSelectedRoot(current, node.Metadata, _uid, _gid);
            }
            else
            {
                current.RequireAncestor(false);
                if (!current.SameAncestorAs(node.Metadata)) throw LinuxProducerArtifactCustody.Invalid();
            }
            if (node.Parent is not null)
            {
                using var named = OpenAt2(Fd(node.Parent.Handle), node.Name, PathFlags, 0, AncestorResolution);
                var namedMetadata = LinuxProtectedMetadata.From(StatFd(named));
                deadline.Check(token);
                if (!namedMetadata.SameAncestorAs(node.Metadata)) throw LinuxProducerArtifactCustody.Invalid();
            }
        }
    }

    /// <summary>Closes all retained descriptors in reverse acquisition order; repeated disposal is inert.</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        foreach (var node in _chain.AsEnumerable().Reverse()) node.Handle.Dispose();
    }

    private sealed record DirectoryIdentity(SafeFileHandle Handle, DirectoryIdentity? Parent,
        string Name, LinuxProtectedMetadata Metadata, bool IsSelectedRoot);
}
