using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Inventories one actual retained application source tree against the selected closed bundle rows.</summary>
/// <remarks>
/// The workspace owner opens and retains the original root, authenticates the definition and owners, and keeps
/// every acquired child in its quarantine list. This native inventory utility borrows those objects: it grants no workspace,
/// owner, account, admission, or cleanup authority and never closes a descriptor. A child is appended to the
/// caller's retained-node list immediately after OpenChild returns, before path, depth, selection, metadata, or
/// hash validation. If a later check fails, the caller therefore still has every acquired descriptor to quarantine.
/// </remarks>
internal sealed class LinuxApplicationBundleAudit
{
    private const int MaximumTreeDepth = 32;

    private readonly IReadOnlyList<LinuxApplicationBundleFile> _files;
    private readonly IReadOnlyDictionary<string, LinuxApplicationBundleFile> _selectedFiles;
    private readonly List<LinuxProtectedNode> _retainedNodes;
    private readonly LinuxInputDeadline _deadline;

    private readonly Dictionary<string, LinuxProtectedNode> _sourceDirectories;
    private readonly Dictionary<string, LinuxProtectedNode> _sourceFiles;
    private readonly Dictionary<string, LinuxProtectedNode> _sourceDirectoryHandles;

    /// <summary>Uses the workspace's existing inventory maps and borrows its actual retained-node list.</summary>
    internal LinuxApplicationBundleAudit(IReadOnlyList<LinuxApplicationBundleFile> files,
        IReadOnlyDictionary<string, LinuxApplicationBundleFile> selectedFiles,
        List<LinuxProtectedNode> retainedNodes, LinuxInputDeadline deadline,
        Dictionary<string, LinuxProtectedNode> sourceDirectories,
        Dictionary<string, LinuxProtectedNode> sourceFiles,
        Dictionary<string, LinuxProtectedNode> sourceDirectoryHandles)
    {
        _files = files;
        _selectedFiles = selectedFiles;
        _retainedNodes = retainedNodes;
        _deadline = deadline;
        _sourceDirectories = sourceDirectories;
        _sourceFiles = sourceFiles;
        _sourceDirectoryHandles = sourceDirectoryHandles;

        // Preserve the workspace's original root-key reservation before it opens the source root.
        _sourceDirectories[string.Empty] = null!;
    }

    /// <summary>Audits the borrowed actual root and leaves child-handle lifetime with the caller.</summary>
    /// <param name="borrowedRoot">The real LinuxProtectedNode opened and retained by the application workspace.</param>
    /// <param name="token">The original workspace token; no new cancellation source is created.</param>
    internal void Inventory(LinuxProtectedNode borrowedRoot, CancellationToken token)
    {
        InventoryDirectory(borrowedRoot, string.Empty, 0, token);
        var rows = BuildRows(_sourceDirectoryHandles, _sourceFiles, _deadline, token);
        LinuxApplicationWorkspaceLayout.RequireExactInventory(_files, rows);
    }

    private void InventoryDirectory(LinuxProtectedNode directory, string relative, int depth,
        CancellationToken token)
    {
        _deadline.Check(token);
        RequireProtectedDirectory(directory.Metadata);
        if (relative.Length != 0 && _sourceDirectories.ContainsKey(relative))
            throw LinuxApplicationWorkspaceLayout.Invalid();
        _sourceDirectories[relative] = directory;
        _sourceDirectoryHandles[relative] = directory;
        if (_retainedNodes.Count > LinuxApplicationWorkspaceLayout.MaximumTreeEntries)
            throw LinuxApplicationWorkspaceLayout.Invalid();

        foreach (var name in directory.ReadNames(token))
        {
            _deadline.Check(token);
            var childPath = relative.Length == 0 ? name : relative + "/" + name;
            var isDirectory = directory.IsChildDirectory(name);
            var child = directory.OpenChild(name, isDirectory, token);
            _retainedNodes.Add(child);

            if (isDirectory)
            {
                if (depth >= MaximumTreeDepth) throw LinuxApplicationWorkspaceLayout.Invalid();
                InventoryDirectory(child, childPath, depth + 1, token);
            }
            else
            {
                ValidateSourcePath(childPath);
                if (!_sourceFiles.TryAdd(childPath, child))
                    throw LinuxApplicationWorkspaceLayout.Invalid();
                var expected = _selectedFiles.GetValueOrDefault(childPath);
                if (expected is null || child.Metadata.Length != (ulong)expected.LengthBytes
                    || child.Metadata.Mode != (ushort)(0x8000 | expected.Mode)
                    || child.Metadata.Uid != 0 || child.Metadata.Gid != 0 || child.Metadata.LinkCount != 1
                    || child.Hash(token, _deadline) != expected.Sha256)
                    throw LinuxApplicationWorkspaceLayout.Invalid();
            }

            if (_retainedNodes.Count > LinuxApplicationWorkspaceLayout.MaximumTreeEntries)
                throw LinuxApplicationWorkspaceLayout.Invalid();
        }
    }

    /// <summary>Builds observed rows from the actual borrowed descriptor maps used by audit and workspace rechecks.</summary>
    internal static IReadOnlyList<LinuxApplicationObservedEntry> BuildRows(
        IReadOnlyDictionary<string, LinuxProtectedNode> sourceDirectoryHandles,
        IReadOnlyDictionary<string, LinuxProtectedNode> sourceFiles,
        LinuxInputDeadline deadline, CancellationToken token)
    {
        var rows = new List<LinuxApplicationObservedEntry>();
        foreach (var pair in sourceDirectoryHandles)
        {
            var metadata = pair.Value.Metadata;
            rows.Add(new(pair.Key, true, metadata.Uid, metadata.Gid, metadata.Mode, metadata.LinkCount,
                metadata.Inode, checked((long)metadata.Length), null));
        }
        foreach (var pair in sourceFiles)
        {
            token.ThrowIfCancellationRequested();
            var metadata = pair.Value.Metadata;
            rows.Add(new(pair.Key, false, metadata.Uid, metadata.Gid, metadata.Mode, metadata.LinkCount,
                metadata.Inode, checked((long)metadata.Length), pair.Value.Hash(token, deadline)));
        }
        return Array.AsReadOnly(rows.ToArray());
    }

    private static void RequireProtectedDirectory(LinuxProtectedMetadata metadata)
    {
        if (!LinuxWorkspaceMetadata.MatchesDirectory(metadata, 0, 0, 0x16d))
            throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private static void ValidateSourcePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Any(static character => !char.IsAsciiLetterOrDigit(character)
            && character is not ('-' or '_' or '.' or '/')) || path.Split('/').Any(static part => part.Length is < 1 or > 255
                || part is "." or "..")) throw LinuxApplicationWorkspaceLayout.Invalid();
    }
}
