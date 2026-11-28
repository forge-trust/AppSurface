using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
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
            $"{PackageChecksDirectory}/{RepositoryEvidenceOutputLayout.ArtifactsDirectoryName}/{RepositoryEvidenceOutputLayout.PackageArtifactManifestFileName}",
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

    /// <summary>Tracks cleanup and verified archive entries for one execution or plan.</summary>
    /// <param name="packageRestoreCache">The private restore cache, or null for explicit output.</param>
    internal sealed class ManagedRun(string? packageRestoreCache)
    {
        private bool _hostCleanupUnconfirmed;

        /// <summary>Gets the private package-gate restore cache when output is managed.</summary>
        internal string? PackageRestoreCache { get; } = packageRestoreCache;

        /// <summary>Gets the tracker for command cleanup, including nested source checks.</summary>
        internal RepositoryCommandSettlement Settlement { get; } = new();

        /// <summary>Gets whether started commands and the evidence host have confirmed cleanup.</summary>
        internal bool CleanupUnconfirmed => _hostCleanupUnconfirmed || Settlement.HasUnconfirmedCleanup;

        /// <summary>Records that host execution must report cleanup before publishing a result.</summary>
        internal void BeginHostExecution() => _hostCleanupUnconfirmed = true;

        /// <summary>Records whether the host confirmed cleanup.</summary>
        internal void RecordHostCleanup(bool completed) => _hostCleanupUnconfirmed = !completed;

        /// <summary>Leaves the retained archive in place when the local inventory is empty.</summary>
        internal bool SkipArchive { get; set; }

        /// <summary>Gets the original replay inputs and verified artifact identities.</summary>
        internal IReadOnlyDictionary<string, ArchiveEntry>? VerifiedEntries { get; private set; }

        /// <summary>Binds the archive to the original plan, replay inputs and optional completed execution.</summary>
        internal void VerifyPublication(EvidencePlan plan, IReadOnlyDictionary<string, ArchiveEntry> replayInputs,
            EvidenceManifest? manifest)
        {
            var entries = new Dictionary<string, ArchiveEntry>(replayInputs, StringComparer.Ordinal)
            {
                [RepositoryEvidenceOutputLayout.PlanDirectoryName + "/" + RepositoryEvidenceOutputLayout.PlanFileName] =
                    ArchiveEntry.Snapshot(EvidenceCanonicalJson.Serialize(plan)),
            };
            if (manifest is not null)
            {
                entries.Add(ProofPrefix + RepositoryEvidenceOutputLayout.ManifestFileName,
                    ArchiveEntry.Snapshot(EvidenceCanonicalJson.Serialize(manifest)));
                foreach (var producer in manifest.ProducerResults)
                {
                    foreach (var artifact in producer.Artifacts ?? [])
                    {
                        var entry = $"{ProofPrefix}{RepositoryEvidenceOutputLayout.ArtifactsDirectoryName}/{producer.ProducerId}/{artifact.RelativePath}";
                        entries.Add(entry, new(artifact.LengthBytes, artifact.Sha256));
                    }
                }
            }
            VerifiedEntries = entries;
        }
    }

    /// <summary>The expected bytes for a replay entry written to a compact archive.</summary>
    /// <param name="Length">The original byte length.</param>
    /// <param name="Sha256">The original SHA-256 digest.</param>
    internal sealed record ArchiveEntry(long Length, string Sha256)
    {
        /// <summary>Records the identity of a replay input before execution can change it.</summary>
        internal static ArchiveEntry Snapshot(ReadOnlySpan<byte> bytes) => new(bytes.Length, EvidenceDigest.Sha256(bytes));
    }

    /// <summary>Runs against fresh scratch and replaces the matching compact archive after the run.</summary>
    /// <remarks>Explicit execution and managed output share the worktree's cleanup guard and lock.</remarks>
    /// <param name="repository">The worktree that owns the managed output.</param>
    /// <param name="operation">The local execution or planning operation.</param>
    /// <param name="gitDirectory">The worktree-specific Git metadata directory.</param>
    /// <param name="run">Receives fresh scratch and the managed run's services.</param>
    /// <param name="explicitOutput">The caller's fresh unpacked output directory, if supplied.</param>
    internal static async Task<int> RunAsync(string repository, string operation, string gitDirectory,
        Func<string, ManagedRun, Task<int>> run, string? explicitOutput = null)
    {
        var control = RequireDirectory(PathUtils.PathUnder(gitDirectory, "appsurface-local-evidence"));
        var lockPath = PathUtils.PathUnder(control, "run.lock");
        RequirePlainPath(lockPath);
        using var lease = Acquire(lockPath);
        var pending = operation == "local" ? PathUtils.PathUnder(control, "run.pending-cleanup") : null;
        if (pending is not null)
        {
            RequirePlainPath(pending);
            if (Path.Exists(pending))
            {
                var message = "Local evidence execution is blocked because a previous run may still have active commands. " +
                    $"After confirming its processes have stopped, remove the cleanup marker at {pending} and retry.";
                Console.Error.WriteLine(message);
                throw new InvalidOperationException(message);
            }
        }

        if (explicitOutput is not null)
            return await RunWithRetryGuardAsync(explicitOutput, new ManagedRun(null), pending, run);

        var results = RequireDirectory(PathUtils.PathUnder(repository, "TestResults"));
        var evidence = RequireDirectory(PathUtils.PathUnder(results, "repository-evidence"));
        var root = RequireDirectory(PathUtils.PathUnder(evidence, "local"));

        // Package discovery skips hidden directories. Generated consumer projects belong in scratch.
        var scratch = RequireDirectory(PathUtils.PathUnder(root, ".scratch"));
        var packageCache = PathUtils.PathUnder(scratch, "package-restore");
        RequirePlainPath(packageCache);
        var workspace = EvidenceWorkspace.Create(scratch, new(MinimumFreeBytes: 0));
        var output = workspace.GetPath("run");
        var state = new ManagedRun(packageCache);
        var cleanup = false;
        var runFailed = false;
        Console.WriteLine("Local evidence scratch: " + output);
        try
        {
            int exitCode;
            try
            {
                exitCode = await RunWithRetryGuardAsync(output, state, pending, run);
                if (!state.SkipArchive)
                    PublishArchive(exitCode == 0 ? ".zip" : "-failure.zip", $"Exit code: {exitCode}",
                        exitCode == 0 ? state.VerifiedEntries : null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                runFailed = true;
                try
                {
                    if (Directory.Exists(output))
                        PublishArchive(exception is OperationCanceledException ? "-cancelled.zip" : "-failure.zip",
                            $"Failed: {exception.GetType().Name}: {exception.Message}", null);
                    cleanup = !state.CleanupUnconfirmed && await CanCleanFailedRunAsync(output);
                }
                catch (Exception archiveFailure) when (archiveFailure is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Console.Error.WriteLine($"Could not retain the failed local evidence: {archiveFailure.Message}");
                }

                throw;
            }

            cleanup = !state.CleanupUnconfirmed && (exitCode == 0 || await CanCleanFailedRunAsync(output));
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

        void PublishArchive(string suffix, string status, IReadOnlyDictionary<string, ArchiveEntry>? verified)
        {
            var archivePrefix = operation == "local-plan" ? "latest-plan" : "latest";
            Publish(workspace, output, root, archivePrefix + suffix, status, verified);
        }
    }

    private static async Task<int> RunWithRetryGuardAsync(string output, ManagedRun state, string? pending,
        Func<string, ManagedRun, Task<int>> run)
    {
        if (pending is not null)
        {
            using var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(output);
        }

        int exitCode;
        try
        {
            exitCode = await run(output, state);
            if (exitCode == 0 && state.CleanupUnconfirmed)
                throw new InvalidOperationException("Local evidence cleanup could not be confirmed.");
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            state.Settlement.Observe(failure);
            if (pending is not null && !state.CleanupUnconfirmed)
            {
                try { File.Delete(pending); }
                catch (Exception markerFailure) when (markerFailure is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not clear the local evidence cleanup marker at {pending}: {markerFailure.Message}");
                }
            }
            throw;
        }

        if (pending is not null && !state.CleanupUnconfirmed)
            File.Delete(pending);
        return exitCode;
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

    private static void Publish(EvidenceWorkspace workspace, string output, string root, string name, string status,
        IReadOnlyDictionary<string, ArchiveEntry>? verified)
    {
        var temporary = workspace.GetPath(name + ".staging");
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
                        CanTraverseArchiveDirectory(RelativeArchivePath(output, entry.ToFullPath()), verified),
                };
                foreach (var file in files)
                {
                    var relative = RelativeArchivePath(output, file);
                    if (verified?.ContainsKey(relative) == true || ShouldRetain(relative))
                        archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
            }
        }

        if (verified is not null)
            VerifyArchive(temporary, verified);

        var destination = PathUtils.PathUnder(root, name);
        RequirePlainPath(destination);
        File.Move(temporary, destination, overwrite: true);
        Console.WriteLine("Local evidence output: " + destination);
    }

    private static void VerifyArchive(string path, IReadOnlyDictionary<string, ArchiveEntry> verified)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var (name, proof) in verified)
        {
            var entry = archive.GetEntry(name);
            if (entry is null || entry.Length != proof.Length)
                throw new InvalidDataException("Local evidence archive is missing the verified bytes for " + name);
            using var content = entry.Open();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), proof.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local evidence archive digest mismatch for " + name);
        }
    }

    /// <summary>Identifies directories that may contain entries eligible for the compact archive.</summary>
    /// <param name="path">The directory path relative to scratch, with forward slash separators.</param>
    /// <returns>Whether archive enumeration should enter the directory.</returns>
    /// <param name="verified">Additional directories containing verified replay entries.</param>
    internal static bool CanTraverseArchiveDirectory(string path, IReadOnlyDictionary<string, ArchiveEntry>? verified = null)
    {
        var prefix = path + "/";
        return ExactArchivePaths.Any(file => file.StartsWith(prefix, StringComparison.Ordinal))
            || ArchiveDirectories.Any(directory => directory.CanTraverse(prefix))
            || verified?.Keys.Any(file => file.StartsWith(prefix, StringComparison.Ordinal)) == true;
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
            Console.Error.WriteLine("Could not lock local evidence output. Wait for the other local validation in this worktree to finish.");
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
