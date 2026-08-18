using System.Globalization;
using CliFx;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Describes one request to preview or remove <c>TestResults</c> directories through <c>coverage clean --all</c>.
/// </summary>
/// <param name="RootDirectory">Optional scan root; the current directory is used when omitted.</param>
/// <param name="Apply">Whether discovered directories may be deleted.</param>
internal sealed record TestResultsCleanupRequest(string? RootDirectory, bool Apply);

/// <summary>
/// Describes the completed <c>TestResults</c> cleanup operation.
/// </summary>
/// <param name="RootDirectory">Canonical absolute scan root.</param>
/// <param name="Directories">Canonical absolute directories found below the root.</param>
/// <param name="EstimatedBytes">Total regular-file bytes measured without following links.</param>
/// <param name="ReparsePointsSkipped">Symbolic links or reparse points not traversed or counted.</param>
/// <param name="Applied">Whether the listed directories were deleted.</param>
internal sealed record TestResultsCleanupResult(
    string RootDirectory,
    IReadOnlyList<string> Directories,
    long EstimatedBytes,
    int ReparsePointsSkipped,
    bool Applied);

/// <summary>
/// Discovers, sizes, and explicitly removes private <c>TestResults</c> directories.
/// </summary>
/// <remarks>
/// The workflow deliberately has a narrow target definition: it only considers descendant directories whose final
/// name is <c>TestResults</c>, compared without regard to case. It does not follow symbolic links or Windows reparse
/// points during discovery, sizing, or deletion. This protects an ordinary worktree cleanup from crossing into a
/// linked checkout or unrelated storage location.
/// </remarks>
internal sealed class TestResultsCleanupWorkflow
{
    private const string DocumentationAnchor = "Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-clean";
    private readonly Action<string, CancellationToken> _deleteDirectory;
    private readonly Action<string>? _afterRootExists;
    private readonly Action<string>? _beforeDiscovery;

    /// <summary>
    /// Initializes a workflow that uses the safe recursive deletion implementation.
    /// </summary>
    public TestResultsCleanupWorkflow()
        : this(DeleteDirectoryTree)
    {
    }

    /// <summary>
    /// Initializes a workflow with an explicit deletion operation for deterministic tests.
    /// </summary>
    /// <param name="deleteDirectory">Action that deletes one validated directory without following links.</param>
    /// <param name="afterRootExists">Optional test seam invoked after the root existence check and before root inspection.</param>
    /// <param name="beforeDiscovery">Optional test seam invoked after root inspection and before directory discovery.</param>
    internal TestResultsCleanupWorkflow(
        Action<string, CancellationToken> deleteDirectory,
        Action<string>? afterRootExists = null,
        Action<string>? beforeDiscovery = null)
    {
        _deleteDirectory = deleteDirectory ?? throw new ArgumentNullException(nameof(deleteDirectory));
        _afterRootExists = afterRootExists;
        _beforeDiscovery = beforeDiscovery;
    }

    /// <summary>
    /// Previews or deletes matching directories and writes a bounded operator summary.
    /// </summary>
    /// <param name="request">The root and explicit deletion confirmation.</param>
    /// <param name="console">Console used for summary output.</param>
    /// <param name="cancellationToken">Cancellation token observed during filesystem traversal.</param>
    /// <returns>A value describing the discovered directories and estimated byte count.</returns>
    /// <exception cref="CommandException">Thrown when the root is unsafe, cannot be inspected, or deletion fails.</exception>
    public async Task<TestResultsCleanupResult> CleanAsync(
        TestResultsCleanupRequest request,
        IConsole console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(console);

        var rootDirectory = ResolveRootDirectory(request.RootDirectory, _afterRootExists);
        _beforeDiscovery?.Invoke(rootDirectory);
        var discovery = Discover(rootDirectory, cancellationToken);
        var result = new TestResultsCleanupResult(
            rootDirectory,
            discovery.Directories,
            discovery.EstimatedBytes,
            discovery.ReparsePointsSkipped,
            request.Apply);

        await WriteDiscoveryAsync(console, result, cancellationToken);
        if (!request.Apply)
        {
            await console.Output.WriteLineAsync("Preview only. Re-run with --apply to delete the listed directories.");
            return result;
        }

        var deletedDirectories = 0;
        foreach (var directory in result.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureDirectoryIsNotReparsePoint(directory, "TestResults directory");
                _deleteDirectory(directory, cancellationToken);
                deletedDirectories++;
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw CreateDiagnostic(
                    "ASTEST103",
                    "A TestResults directory could not be deleted.",
                    $"Deletion stopped after removing {deletedDirectories.ToString(CultureInfo.InvariantCulture)} of {result.Directories.Count.ToString(CultureInfo.InvariantCulture)} discovered directories; the failing path was '{directory}'.",
                    "Close processes holding files in the listed directory, then rerun the command with --apply.",
                    exception);
            }
        }

        await console.Output.WriteLineAsync(
            $"Removed {deletedDirectories.ToString(CultureInfo.InvariantCulture)} TestResults {Pluralize("directory", deletedDirectories)} and reclaimed approximately {FormatBytes(result.EstimatedBytes)}.");
        return result;
    }

    private static string ResolveRootDirectory(string? requestedRoot, Action<string>? afterRootExists)
    {
        var value = string.IsNullOrWhiteSpace(requestedRoot) ? Directory.GetCurrentDirectory() : requestedRoot.Trim();
        string rootDirectory;
        try
        {
            rootDirectory = Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw CreateDiagnostic(
                "ASTEST101",
                "The cleanup root is invalid.",
                "--root could not be converted to a filesystem path.",
                "Pass an existing working-tree directory such as --root .",
                exception);
        }

        if (!Directory.Exists(rootDirectory))
        {
            throw CreateDiagnostic(
                "ASTEST101",
                "The cleanup root was not found.",
                "--root must name an existing directory.",
                "Pass the worktree directory that contains the TestResults folders, for example --root .");
        }

        if (IsFilesystemRoot(rootDirectory))
        {
            throw CreateDiagnostic(
                "ASTEST102",
                "The filesystem root cannot be scanned for TestResults cleanup.",
                "A filesystem root is not a bounded working-tree directory.",
                "Pass one repository or worktree directory with --root.");
        }

        afterRootExists?.Invoke(rootDirectory);
        try
        {
            EnsureDirectoryIsNotReparsePoint(rootDirectory, "cleanup root");
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw CreateDiagnostic(
                "ASTEST101",
                "The cleanup root could not be inspected.",
                "Filesystem attributes for --root could not be read.",
                "Pass a readable physical worktree directory such as --root .",
                exception);
        }

        return rootDirectory;
    }

    private static DiscoveryResult Discover(string rootDirectory, CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        var estimatedBytes = 0L;
        var reparsePointsSkipped = 0;

        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                if (IsReparsePoint(directory))
                {
                    reparsePointsSkipped++;
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparsePoint(child))
                    {
                        reparsePointsSkipped++;
                        continue;
                    }

                    if (Path.GetFileName(child).Equals("TestResults", StringComparison.OrdinalIgnoreCase))
                    {
                        directories.Add(child);
                        var measurement = MeasureDirectory(child, cancellationToken);
                        estimatedBytes = SaturatingAdd(estimatedBytes, measurement.Bytes);
                        reparsePointsSkipped += measurement.ReparsePointsSkipped;
                        continue;
                    }

                    pending.Push(child);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw CreateDiagnostic(
                "ASTEST104",
                "TestResults discovery could not inspect the cleanup root.",
                "One or more directories could not be enumerated or measured.",
                "Use a readable worktree root and close processes that are changing its directory tree, then retry.",
                exception);
        }

        directories.Sort(StringComparer.Ordinal);
        return new DiscoveryResult(directories, estimatedBytes, reparsePointsSkipped);
    }

    private static MeasurementResult MeasureDirectory(string directory, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var bytes = 0L;
        var reparsePointsSkipped = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (IsReparsePoint(current))
            {
                reparsePointsSkipped++;
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reparsePointsSkipped++;
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                bytes = SaturatingAdd(bytes, new FileInfo(entry).Length);
            }
        }

        return new MeasurementResult(bytes, reparsePointsSkipped);
    }

    /// <summary>
    /// Removes one validated <c>TestResults</c> directory without traversing linked entries.
    /// </summary>
    /// <param name="directory">Directory or linked directory entry to remove.</param>
    /// <param name="cancellationToken">Cancellation token observed between directory entries.</param>
    /// <remarks>
    /// This internal test seam covers the race-safe final link check: a directory that is replaced by a link after
    /// discovery is unlinked instead of traversing its target.
    /// </remarks>
    internal static void DeleteDirectoryTree(string directory, CancellationToken cancellationToken)
    {
        if (IsReparsePoint(directory))
        {
            Directory.Delete(directory, recursive: false);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (isDirectory)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if (isDirectory)
            {
                DeleteDirectoryTree(entry, cancellationToken);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(directory, recursive: false);
    }

    private static async Task WriteDiscoveryAsync(
        IConsole console,
        TestResultsCleanupResult result,
        CancellationToken cancellationToken)
    {
        await console.Output.WriteLineAsync($"Root: {result.RootDirectory}");
        await console.Output.WriteLineAsync(
            $"Found {result.Directories.Count.ToString(CultureInfo.InvariantCulture)} TestResults {Pluralize("directory", result.Directories.Count)} using approximately {FormatBytes(result.EstimatedBytes)}.");

        foreach (var directory in result.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await console.Output.WriteLineAsync($"  {Path.GetRelativePath(result.RootDirectory, directory)}");
        }

        if (result.ReparsePointsSkipped > 0)
        {
            await console.Output.WriteLineAsync(
                $"Skipped {result.ReparsePointsSkipped.ToString(CultureInfo.InvariantCulture)} symbolic-link or reparse-point {Pluralize("entry", result.ReparsePointsSkipped)}; linked targets are never traversed.");
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string directory, string description)
    {
        if (IsReparsePoint(directory))
        {
            throw CreateDiagnostic(
                "ASTEST102",
                $"The {description} is a symbolic link or reparse point.",
                "Cleanup does not follow linked filesystem paths.",
                "Pass the physical worktree directory and remove only real TestResults directories.");
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    internal static bool IsFilesystemRoot(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(root),
            comparison);
    }

    /// <summary>Returns the non-negative sum without exceeding <see cref="long.MaxValue"/>.</summary>
    internal static long SaturatingAdd(long current, long next) =>
        long.MaxValue - current < next ? long.MaxValue : current + next;

    /// <summary>Formats a non-negative regular-file byte count for cleanup output.</summary>
    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[unitIndex]}"
            : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    /// <summary>Formats a singular noun for one or more cleanup entries.</summary>
    internal static string Pluralize(string singular, int count) =>
        count == 1
            ? singular
            : singular.EndsWith('y')
                ? singular[..^1] + "ies"
                : singular + "s";

    private static CommandException CreateDiagnostic(
        string code,
        string problem,
        string cause,
        string fix,
        Exception? exception = null)
    {
        var message = $"{code}: {problem}{Environment.NewLine}" +
                      $"Cause: {cause}{Environment.NewLine}" +
                      $"Fix: {fix}{Environment.NewLine}" +
                      $"Docs: {DocumentationAnchor}";
        _ = exception;
        return new CommandException(message);
    }

    private sealed record DiscoveryResult(IReadOnlyList<string> Directories, long EstimatedBytes, int ReparsePointsSkipped);

    private sealed record MeasurementResult(long Bytes, int ReparsePointsSkipped);
}
