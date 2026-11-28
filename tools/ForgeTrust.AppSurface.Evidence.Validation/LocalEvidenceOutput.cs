using System.IO.Compression;
using System.IO.Enumeration;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Owns the default local validation scratch and publishes bounded-history results in the worktree.</summary>
internal static class LocalEvidenceOutput
{
    private const string ProofPrefix = RepositoryEvidenceOutputLayout.ProofDirectoryName + "/";
    private const string PackageChecksDirectory = ProofPrefix + RepositoryEvidenceOutputLayout.PackageChecksDirectoryName;

    private static readonly HashSet<string> ExactArchivePaths = CreateExactArchivePaths();

    private static readonly ArchiveDirectory[] ArchiveDirectories =
    [
        new(RepositoryEvidenceOutputLayout.PlanDirectoryName, recursive: true),
        new(ProofPrefix + RepositoryEvidenceOutputLayout.ArtifactsDirectoryName, recursive: true),
        new(ProofPrefix + "coverage", recursive: true, ShouldRetainCoverageFile),
        new(PackageChecksDirectory + "/artifacts", recursive: false,
            static path => path.EndsWith(".md", StringComparison.Ordinal)),
    ];

    private static HashSet<string> CreateExactArchivePaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            "changed-paths.json",
            "changed.patch",
            "local-policy.json",
            "source-state.json",
            "package-build.log",
            RepositoryEvidenceOutputLayout.SummaryFileName,
            ProofPrefix + RepositoryEvidenceOutputLayout.PlanFileName,
            ProofPrefix + RepositoryEvidenceOutputLayout.ManifestFileName,
            ProofPrefix + RepositoryEvidenceOutputLayout.SummaryFileName,
            ProofPrefix + RepositoryEvidenceOutputLayout.SelectionFileName,
        };

        foreach (var stageName in RepositoryEvidenceOutputLayout.PackageCheckStageNames)
            paths.Add(RepositoryEvidenceOutputLayout.PackageCheckLogArchiveEntryName(stageName));

        return paths;
    }

    private static readonly EnumerationOptions OutputFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MaxRecursionDepth = 64,
    };

    /// <summary>The services that belong to a managed local run for the lifetime of its output lease.</summary>
    /// <param name="PackageRestoreCache">The worktree's private package-gate restore cache.</param>
    /// <param name="Settlement">The tracker that records unconfirmed command cleanup in this run.</param>
    internal sealed record ManagedRun(string PackageRestoreCache, RepositoryCommandSettlement Settlement);

    /// <summary>Runs against fresh scratch and replaces the matching compact archive after the run.</summary>
    /// <remarks>Explicit output paths bypass this lifecycle. The package-gate build cache remains separate from scratch.</remarks>
    /// <param name="repository">The worktree that owns the managed output.</param>
    /// <param name="operation">The local execution or planning operation.</param>
    /// <param name="run">Receives fresh scratch and the managed run's services.</param>
    internal static async Task<int> RunAsync(string repository, string operation,
        Func<string, ManagedRun, Task<int>> run)
    {
        var results = RequireDirectory(PathUtils.PathUnder(repository, "TestResults"));
        var evidence = RequireDirectory(PathUtils.PathUnder(results, "repository-evidence"));
        var root = RequireDirectory(PathUtils.PathUnder(evidence, "local"));
        var lockPath = PathUtils.PathUnder(root, "run.lock");
        RequirePlainPath(lockPath);
        using var lease = Acquire(lockPath);

        var packageCache = PathUtils.PathUnder(root, "package-restore");
        RequirePlainPath(packageCache);
        // Package discovery skips hidden directories. Generated consumer projects belong in scratch.
        var scratch = RequireDirectory(PathUtils.PathUnder(root, ".scratch"));
        var workspace = EvidenceWorkspace.Create(scratch, new(MinimumFreeBytes: 0));
        var output = workspace.GetPath("run");
        var settlement = new RepositoryCommandSettlement();
        var cleanup = false;
        var runFailed = false;
        Console.WriteLine("Local evidence scratch: " + output);
        try
        {
            int exitCode;
            try
            {
                exitCode = await run(output, new ManagedRun(packageCache, settlement));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                runFailed = true;
                settlement.Observe(exception);
                try
                {
                    cleanup = await PublishAndCheckCleanupAsync(succeeded: false, $"Failed: {exception.GetType().Name}: {exception.Message}");
                }
                catch (Exception archiveFailure) when (archiveFailure is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Console.Error.WriteLine($"Could not retain the failed local evidence: {archiveFailure.Message}");
                }

                throw;
            }

            cleanup = await PublishAndCheckCleanupAsync(succeeded: exitCode == 0, $"Exit code: {exitCode}");
            return exitCode;
        }
        finally
        {
            if (!cleanup)
            {
                Console.Error.WriteLine("Local evidence scratch retained for inspection: " + workspace.RootPath);
            }
            else
            {
                try
                {
                    await workspace.DisposeAsync();
                }
                catch (Exception exception) when (runFailed && exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not remove local evidence scratch at {workspace.RootPath}: {exception.Message}");
                }
            }
        }

        // Keep scratch until the archive has been published and the run's cleanup is confirmed.
        async Task<bool> PublishAndCheckCleanupAsync(bool succeeded, string status)
        {
            var archivePrefix = operation == "local-plan" ? "latest-plan" : "latest";
            var archiveName = archivePrefix + (succeeded ? ".zip" : "-failure.zip");
            Publish(workspace, output, root, archiveName, status);
            return !settlement.HasUnconfirmedCleanup && (succeeded || await CanCleanFailedRunAsync(output));
        }
    }

    private static async Task<bool> CanCleanFailedRunAsync(string output)
    {
        var proof = PathUtils.PathUnder(output, RepositoryEvidenceOutputLayout.ProofDirectoryName);
        if (!Directory.Exists(proof))
            return true;
        var manifest = PathUtils.PathUnder(proof, RepositoryEvidenceOutputLayout.ManifestFileName);
        if (!File.Exists(manifest))
            return false;
        try
        {
            // Cleanup still needs this result if the run ended through cancellation.
            await using var input = File.OpenRead(manifest);
            var result = await EvidenceCanonicalJson.DeserializeAsync<EvidenceManifest>(input);
            return result.Metrics.CleanupCompleted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static void Publish(EvidenceWorkspace workspace, string output, string root, string name, string status)
    {
        var temporary = workspace.GetPath("retained.zip");
        using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("run-status.txt").Open()))
                writer.WriteLine(status);

            if (Directory.Exists(output))
            {
                var files = new FileSystemEnumerable<string>(output, static (ref FileSystemEntry entry) => entry.ToFullPath(), OutputFiles)
                {
                    ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
                    ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                        CanTraverseArchiveDirectory(RelativeArchivePath(output, entry.ToFullPath())),
                };
                foreach (var file in files)
                {
                    var relative = RelativeArchivePath(output, file);
                    if (ShouldRetain(relative))
                        archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
            }
        }

        var destination = PathUtils.PathUnder(root, name);
        RequirePlainPath(destination);
        File.Move(temporary, destination, overwrite: true);
        Console.WriteLine("Local evidence output: " + destination);
    }

    /// <summary>Identifies directories that may contain entries eligible for the compact archive.</summary>
    /// <param name="path">The directory path relative to scratch, with forward slash separators.</param>
    /// <returns>Whether archive enumeration should enter the directory.</returns>
    internal static bool CanTraverseArchiveDirectory(string path)
    {
        var prefix = path + "/";
        return ExactArchivePaths.Any(file => file.StartsWith(prefix, StringComparison.Ordinal))
            || ArchiveDirectories.Any(directory => directory.CanTraverse(prefix));
    }

    private static string RelativeArchivePath(string output, string path) =>
        Path.GetRelativePath(output, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool ShouldRetain(string path)
    {
        if (ExactArchivePaths.Contains(path))
            return true;
        foreach (var directory in ArchiveDirectories)
        {
            if (directory.ShouldRetain(path))
                return true;
        }

        return false;
    }

    private static bool ShouldRetainCoverageFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("junit-", StringComparison.Ordinal)
            || name is "coverage.cobertura.xml" or "timings.json" or "summary.txt" or "reportgenerator-summary.txt"
                or "slow-test-diagnostics.json" or "slow-test-diagnostics.md" or "coverage-gate.json" or "coverage-gate.md"
                or "coverage-patch-targets.json" or "coverage-patch-targets.md";
    }

    private sealed class ArchiveDirectory(string path, bool recursive, Func<string, bool>? retainFile = null)
    {
        private readonly string _prefix = path + "/";

        public bool CanTraverse(string directoryPrefix) =>
            _prefix.StartsWith(directoryPrefix, StringComparison.Ordinal)
            || (recursive && directoryPrefix.StartsWith(_prefix, StringComparison.Ordinal));

        public bool ShouldRetain(string file) =>
            file.StartsWith(_prefix, StringComparison.Ordinal)
            && (recursive || !file.AsSpan(_prefix.Length).Contains('/'))
            && (retainFile?.Invoke(file) ?? true);
    }

    private static FileStream Acquire(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Could not lock local evidence output. Wait for any other managed validation in this worktree to finish.");
            throw;
        }
    }

    private static string RequireDirectory(string path)
    {
        RequirePlainPath(path);
        Directory.CreateDirectory(path);
        RequirePlainPath(path);
        return path;
    }

    private static void RequirePlainPath(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Local evidence storage cannot use symbolic links or reparse points: " + path);
        }
        catch (FileNotFoundException)
        {
            // A destination is allowed to be absent before it is created.
        }
    }
}
