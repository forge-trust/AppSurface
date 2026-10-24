using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Configures the private scratch directory owned by an <see cref="EvidenceWorkspace"/>.</summary>
/// <param name="MaximumBytes">Maximum logical size of files beneath the workspace.</param>
/// <param name="MinimumFreeBytes">Minimum free bytes required on the volume containing the workspace.</param>
/// <param name="MaxEntries">Maximum number of entries that a workspace inventory may inspect.</param>
/// <param name="MaxDepth">Maximum directory depth below the workspace root.</param>
public sealed record EvidenceWorkspaceOptions(
    long MaximumBytes = 512L * 1024 * 1024,
    long MinimumFreeBytes = 256L * 1024 * 1024,
    int MaxEntries = 100_000,
    int MaxDepth = 64)
{
    internal void Validate()
    {
        if (MaximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumBytes));
        }

        if (MinimumFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeBytes));
        }

        if (MaxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntries));
        }

        if (MaxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        }
    }
}

/// <summary>Reports that an EvidenceWorkspace has exhausted its configured storage allowance.</summary>
public sealed class EvidenceWorkspaceBudgetException : IOException
{
    /// <summary>Creates a budget exception with a stable diagnostic message.</summary>
    public EvidenceWorkspaceBudgetException(string message, long logicalBytes, long maximumBytes, long freeBytes, long minimumFreeBytes)
        : base(message)
    {
        LogicalBytes = logicalBytes;
        MaximumBytes = maximumBytes;
        FreeBytes = freeBytes;
        MinimumFreeBytes = minimumFreeBytes;
    }

    /// <summary>Gets the observed logical file size.</summary>
    public long LogicalBytes { get; }

    /// <summary>Gets the configured logical size limit.</summary>
    public long MaximumBytes { get; }

    /// <summary>Gets the observed free bytes on the workspace volume.</summary>
    public long FreeBytes { get; }

    /// <summary>Gets the configured minimum free-space requirement.</summary>
    public long MinimumFreeBytes { get; }
}

/// <summary>
/// Owns a fresh random scratch directory beneath a caller-owned parent directory.
/// </summary>
/// <remarks>
/// The parent and any durable evidence beside the workspace remain caller-owned. Disposal removes only the fresh
/// workspace after validating its ownership marker and retained filesystem metadata. A missing, replaced, or linked
/// root is treated as invalid evidence. Inventory is bounded by <see cref="EvidenceWorkspaceOptions.MaxEntries"/>
/// and <see cref="EvidenceWorkspaceOptions.MaxDepth"/> before any cleanup deletion begins. The retained metadata
/// combines a unique marker with creation timestamps; this is a basic cross-platform identity check rather than a
/// kernel-held file handle or native inode/file-ID comparison. Callers must treat the workspace as trusted and must
/// not concurrently replace or mutate its paths while it is in use. No portable implementation can eliminate every
/// time-of-check/time-of-use race against a hostile concurrent process.
/// </remarks>
public sealed class EvidenceWorkspace : IAsyncDisposable
{
    private const string MarkerFileName = ".appsurface-evidence-workspace-owner";
    private readonly string _markerPath;
    private readonly string _identity;
    private readonly DateTime _rootCreationTimeUtc;
    private DateTime _markerCreationTimeUtc;
    private readonly EvidenceWorkspaceOptions _options;
    private readonly Func<string, long>? _logicalSize;
    private readonly Func<string, long>? _freeBytes;
    private int _disposed;

    private EvidenceWorkspace(string rootPath, string identity, EvidenceWorkspaceOptions options, Func<string, long>? logicalSize, Func<string, long>? freeBytes)
    {
        RootPath = rootPath;
        _markerPath = Path.Join(rootPath, MarkerFileName);
        _identity = identity;
        _rootCreationTimeUtc = new DirectoryInfo(rootPath).CreationTimeUtc;
        _markerCreationTimeUtc = new FileInfo(_markerPath).CreationTimeUtc;
        _options = options;
        _logicalSize = logicalSize;
        _freeBytes = freeBytes;
    }

    /// <summary>Gets the newly created absolute workspace path.</summary>
    public string RootPath { get; }

    /// <summary>
    /// Creates a new random workspace beneath <paramref name="parent"/>.
    /// </summary>
    /// <param name="parent">An existing caller-owned directory. It is never deleted by this type.</param>
    /// <param name="options">Optional scratch and free-space limits.</param>
    /// <returns>An owned workspace lease.</returns>
    public static EvidenceWorkspace Create(string parent, EvidenceWorkspaceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        var resolvedOptions = options ?? new EvidenceWorkspaceOptions();
        resolvedOptions.Validate();
        var parentPath = Path.GetFullPath(parent);
        if (!Directory.Exists(parentPath))
        {
            throw new DirectoryNotFoundException(parentPath);
        }

        if (new DirectoryInfo(parentPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Evidence workspace parent cannot be a symbolic link or reparse point.");
        }

        var root = Path.Join(parentPath, $"evidence-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var identity = Guid.NewGuid().ToString("N");
        var workspace = new EvidenceWorkspace(root, identity, resolvedOptions, null, null);
        try
        {
            File.WriteAllText(workspace._markerPath, identity, Encoding.UTF8);
            workspace._markerCreationTimeUtc = new FileInfo(workspace._markerPath).CreationTimeUtc;
            workspace.CheckBudget();
            return workspace;
        }
        catch
        {
            TryDeleteOwnedRoot(workspace);
            throw;
        }
    }

    /// <summary>Checks logical scratch usage and actual volume free space.</summary>
    /// <exception cref="EvidenceWorkspaceBudgetException">The workspace exceeds either configured limit.</exception>
    public void CheckBudget()
    {
        ThrowIfDisposed();
        ValidateOwnedRoot();
        var measuredBytes = MeasureLogicalBytes(RootPath, _options.MaximumBytes);
        var logicalBytes = _logicalSize?.Invoke(RootPath) ?? measuredBytes;
        var freeBytes = _freeBytes?.Invoke(RootPath) ?? FindContainingVolume(RootPath).AvailableFreeSpace;
        if (logicalBytes > _options.MaximumBytes)
        {
            throw new EvidenceWorkspaceBudgetException("Evidence workspace logical storage budget exceeded.", logicalBytes, _options.MaximumBytes, freeBytes, _options.MinimumFreeBytes);
        }

        if (freeBytes < _options.MinimumFreeBytes)
        {
            throw new EvidenceWorkspaceBudgetException("Evidence workspace volume free-space budget exceeded.", logicalBytes, _options.MaximumBytes, freeBytes, _options.MinimumFreeBytes);
        }
    }

    /// <summary>Returns a normalized absolute path for an owned artifact beneath the workspace.</summary>
    /// <param name="relativePath">A non-rooted path that stays within the workspace.</param>
    public string GetPath(string relativePath)
    {
        ThrowIfDisposed();
        ValidateOwnedRoot();
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Workspace artifact paths must be relative.", nameof(relativePath));
        }

        var candidate = Path.GetFullPath(Path.Join(RootPath, relativePath));
        if (!IsDescendant(RootPath, candidate))
        {
            throw new ArgumentException("Workspace artifact path escapes the workspace.", nameof(relativePath));
        }

        ValidateExistingAncestors(candidate);

        return candidate;
    }

    /// <summary>Removes only the owned workspace after revalidating its identity.</summary>
    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.CompletedTask;
        }

        ValidateOwnedRoot();
        var inventory = Inventory(RootPath, _options);
        ValidateOwnedRoot();
        DeleteOwnedTree(RootPath, inventory);
        Volatile.Write(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    internal static EvidenceWorkspace CreateForTests(
        string parent,
        EvidenceWorkspaceOptions? options,
        Func<string, long> logicalSize,
        Func<string, long> freeBytes)
    {
        ArgumentNullException.ThrowIfNull(logicalSize);
        ArgumentNullException.ThrowIfNull(freeBytes);
        var resolvedOptions = options ?? new EvidenceWorkspaceOptions();
        resolvedOptions.Validate();
        // Allocate with permissive physical limits; the injected counters are intentionally exercised by the caller's
        // first explicit CheckBudget invocation rather than during this factory's setup.
        var workspace = Create(parent, new EvidenceWorkspaceOptions(long.MaxValue, 0, 100_000, 64));
        return new EvidenceWorkspace(workspace.RootPath, workspace._identity, resolvedOptions, logicalSize, freeBytes);
    }

    private void ValidateOwnedRoot()
    {
        if (!Directory.Exists(RootPath))
        {
            throw new IOException("Evidence workspace root is missing or was replaced.");
        }

        var rootInfo = new DirectoryInfo(RootPath);
        var markerInfo = new FileInfo(_markerPath);
        if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            rootInfo.CreationTimeUtc != _rootCreationTimeUtc ||
            !markerInfo.Exists ||
            markerInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            markerInfo.CreationTimeUtc != _markerCreationTimeUtc)
        {
            throw new IOException("Evidence workspace root is missing or was replaced.");
        }

        var marker = File.ReadAllText(_markerPath, Encoding.UTF8);
        if (!string.Equals(marker, _identity, StringComparison.Ordinal))
        {
            throw new IOException("Evidence workspace ownership marker is invalid.");
        }
    }

    private long MeasureLogicalBytes(string root, long limit)
    {
        long total = 0;
        foreach (var entry in Inventory(root, _options))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            try { total = checked(total + new FileInfo(entry.Path).Length); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if (total > limit)
            {
                return total;
            }
        }

        return total;
    }

    private static void DeleteOwnedTree(string root, IReadOnlyList<WorkspaceEntry> inventory)
    {
        foreach (var entry in inventory.Where(static entry => !entry.IsDirectory))
        {
            var info = new FileInfo(entry.Path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Evidence workspace cleanup encountered a symbolic link or reparse point.");
            }

            File.Delete(entry.Path);
        }

        foreach (var entry in inventory.Where(static entry => entry.IsDirectory).OrderByDescending(static entry => entry.Depth))
        {
            var info = new DirectoryInfo(entry.Path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Evidence workspace cleanup encountered a symbolic link or reparse point.");
            }

            Directory.Delete(entry.Path);
        }

        Directory.Delete(root);
    }

    private static void TryDeleteOwnedRoot(EvidenceWorkspace workspace)
    {
        try
        {
            if (Directory.Exists(workspace.RootPath) && File.Exists(workspace._markerPath))
            {
                var inventory = Inventory(workspace.RootPath, workspace._options);
                workspace.ValidateOwnedRoot();
                DeleteOwnedTree(workspace.RootPath, inventory);
            }
        }
        catch
        {
            // Preserve the creation failure; no caller-owned path is touched here.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ValidateExistingAncestors(string candidate)
    {
        var current = Path.GetDirectoryName(candidate);
        while (current is not null && IsDescendantOrSelf(RootPath, current))
        {
            var info = new DirectoryInfo(current);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Evidence workspace artifact path contains a symbolic link or reparse point.");
            }

            if (string.Equals(current, RootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        if (File.Exists(candidate) && new FileInfo(candidate).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Evidence workspace artifact path contains a symbolic link or reparse point.");
        }
    }

    private static IReadOnlyList<WorkspaceEntry> Inventory(string root, EvidenceWorkspaceOptions options)
    {
        var entries = new List<WorkspaceEntry>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            string[] paths;
            try { paths = Directory.EnumerateFileSystemEntries(directory).Take((int)Math.Min((long)options.MaxEntries + 1, int.MaxValue)).ToArray(); }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var path in paths)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("Evidence workspace contains a symbolic link or reparse point.");
                }

                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var entryDepth = depth + 1;
                if (entryDepth > options.MaxDepth)
                {
                    throw new IOException("Evidence workspace inventory depth limit exceeded.");
                }

                if (entries.Count == options.MaxEntries)
                {
                    throw new IOException("Evidence workspace inventory entry limit exceeded.");
                }

                entries.Add(new WorkspaceEntry(path, isDirectory, entryDepth));
                if (isDirectory)
                {
                    pending.Push((path, entryDepth));
                }
            }
        }

        return entries;
    }

    private static DriveInfo FindContainingVolume(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return DriveInfo.GetDrives()
            .Where(drive => IsDescendantOrSelf(drive.RootDirectory.FullName, fullPath))
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
            .FirstOrDefault() ?? new DriveInfo(Path.GetPathRoot(fullPath)!);
    }

    private static bool IsDescendantOrSelf(string root, string candidate) =>
        string.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || IsDescendant(root, candidate);

    private readonly record struct WorkspaceEntry(string Path, bool IsDirectory, int Depth);

    private static bool IsDescendant(string root, string candidate) =>
        candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
