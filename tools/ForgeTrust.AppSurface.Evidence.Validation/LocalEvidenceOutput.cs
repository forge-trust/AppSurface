using System.IO.Compression;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Owns the default local validation scratch and publishes bounded-history results in the worktree.</summary>
internal static class LocalEvidenceOutput
{
    private const string PendingCleanupFileName = "run.pending-cleanup";

    private sealed record RequiredArchiveEntry(long LengthBytes, string Sha256);

    private static readonly EnumerationOptions OutputFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MaxRecursionDepth = 64,
    };

    /// <summary>Runs local execution or managed planning under the worktree lease.</summary>
    /// <remarks>Both execution modes share the worktree lease and pending cleanup marker. Planning may run during recovery.</remarks>
    /// <param name="repository">The worktree root that owns validation state.</param>
    /// <param name="operation">The local execution or managed planning operation.</param>
    /// <param name="gitDirectory">The per-worktree Git metadata directory that holds the lease.</param>
    /// <param name="run">The callback that creates the current plan or completes validation.</param>
    /// <param name="explicitOutput">An optional caller-owned fresh output path that bypasses archive retention.</param>
    /// <returns>Zero after successful execution or planning.</returns>
    internal static async Task<int> RunAsync(string repository, string operation, string gitDirectory,
        Func<LocalEvidenceRun, Task<LocalEvidenceRunResult?>> run, string? explicitOutput = null)
    {
        var planOnly = operation == "local-plan";
        var control = RequireDirectory(PathUtils.PathUnder(gitDirectory, "appsurface-local-evidence"));
        var lockPath = PathUtils.PathUnder(control, "run.lock");
        RequirePlainPath(lockPath);
        using var lease = Acquire(lockPath);

        var pendingCleanup = planOnly ? null : PathUtils.PathUnder(control, PendingCleanupFileName);
        if (pendingCleanup is not null)
            RequireSettledWorktree(pendingCleanup);

        if (explicitOutput is not null)
        {
            await RunWithRetryGuardAsync(new LocalEvidenceRun(explicitOutput, null), pendingCleanup, run);
            return 0;
        }

        var results = RequireDirectory(PathUtils.PathUnder(repository, "TestResults"));
        var evidence = RequireDirectory(PathUtils.PathUnder(results, "repository-evidence"));
        var root = RequireDirectory(PathUtils.PathUnder(evidence, "local"));
        await RunManagedAsync(root, planOnly ? "latest-plan" : "latest", pendingCleanup, run);
        return 0;
    }

    private static async Task RunManagedAsync(string root, string archive, string? pendingCleanup,
        Func<LocalEvidenceRun, Task<LocalEvidenceRunResult?>> run)
    {
        // Package discovery excludes hidden ancestors, not every ignored Git path.
        var scratch = RequireDirectory(PathUtils.PathUnder(root, ".scratch"));
        var packageCache = PathUtils.PathUnder(scratch, "package-restore");
        if (pendingCleanup is not null)
            RequirePlainPath(packageCache);
        var workspace = EvidenceWorkspace.Create(scratch, new(MinimumFreeBytes: 0));
        var output = workspace.GetPath("run");
        var localRun = new LocalEvidenceRun(output, packageCache);
        Console.WriteLine("Local evidence scratch: " + output);
        try
        {
            var proof = await RunWithRetryGuardAsync(localRun, pendingCleanup, run);
            if (proof is not null)
                Publish(workspace, output, root, archive + ".zip", "Validation completed.", proof);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            var removed = false;
            try
            {
                if (Directory.Exists(output))
                {
                    var suffix = failure is OperationCanceledException ? "-cancelled.zip" : "-failure.zip";
                    Publish(workspace, output, root, archive + suffix, $"Failed: {failure.GetType().Name}: {failure.Message}");
                }
                else
                {
                    Console.Error.WriteLine($"Local evidence could not start: {failure.Message}");
                }
                if (CanCleanFailedRun(localRun, failure))
                {
                    await workspace.DisposeAsync();
                    removed = true;
                }
            }
            catch (Exception retentionFailure) when (retentionFailure is not OutOfMemoryException and not StackOverflowException)
            {
                Console.Error.WriteLine($"Could not finalize local evidence: {retentionFailure.Message}");
            }

            if (!removed)
                Console.Error.WriteLine("Local evidence scratch retained for inspection: " + workspace.RootPath);
            throw;
        }

        await workspace.DisposeAsync();
    }

    private static void RequireSettledWorktree(string pendingCleanup)
    {
        RequirePlainPath(pendingCleanup);
        if (!Path.Exists(pendingCleanup))
            return;

        var message = "Local evidence execution is blocked because a previous run may still have active commands. " +
            $"After confirming its processes have stopped, remove the cleanup marker at {pendingCleanup} and retry.";
        Console.Error.WriteLine(message);
        throw new InvalidOperationException(message);
    }

    private static async Task<LocalEvidenceRunResult?> RunWithRetryGuardAsync(LocalEvidenceRun localRun, string? pendingCleanup,
        Func<LocalEvidenceRun, Task<LocalEvidenceRunResult?>> run)
    {
        if (pendingCleanup is null)
            return await run(localRun);

        // Arm the guard before starting work so abrupt validator exit also blocks an unsafe retry.
        using (var stream = new FileStream(pendingCleanup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
            writer.WriteLine(localRun.OutputDirectory);

        LocalEvidenceRunResult? proof;
        try
        {
            proof = await run(localRun);
            if (localRun.CleanupUnconfirmed)
                throw new InvalidOperationException("Local evidence command cleanup could not be confirmed.");
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            if (CleanupUnconfirmed(localRun, failure))
            {
                Console.Error.WriteLine("Local evidence cleanup marker retained: " + pendingCleanup);
            }
            else
            {
                try { File.Delete(pendingCleanup); }
                catch (Exception markerFailure) when (markerFailure is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine("Could not clear the local evidence cleanup marker: " + markerFailure.Message);
                }
            }
            throw;
        }

        File.Delete(pendingCleanup);
        return proof;
    }

    private static bool CleanupUnconfirmed(LocalEvidenceRun run, Exception failure) =>
        run.CleanupUnconfirmed || failure is EvidenceCommandException { CleanupUnconfirmed: true };

    private static bool CanCleanFailedRun(LocalEvidenceRun run, Exception failure)
    {
        if (CleanupUnconfirmed(run, failure))
            return false;
        var proof = PathUtils.PathUnder(run.OutputDirectory, RepositoryEvidenceLayout.ProofDirectory);
        if (!Directory.Exists(proof))
            return true;
        var manifest = PathUtils.PathUnder(proof, RepositoryEvidenceLayout.ManifestFile);
        if (!File.Exists(manifest))
            return false;
        return EvidenceCanonicalJson.Deserialize<EvidenceManifest>(File.ReadAllBytes(manifest)).Metrics.CleanupCompleted;
    }

    private static void Publish(EvidenceWorkspace workspace, string output, string root, string name, string status,
        LocalEvidenceRunResult? proof = null)
    {
        // A rejected success ZIP must not occupy the staging path for its failure archive.
        var temporary = workspace.GetPath(name + ".staging");
        var requiredEntries = proof is null ? null : BuildRequiredEntries(proof);
        using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("run-status.txt").Open()))
                writer.WriteLine(status);

            if (Directory.Exists(output))
            {
                foreach (var file in Directory.EnumerateFiles(output, "*", OutputFiles))
                {
                    var relative = Path.GetRelativePath(output, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (ShouldRetain(relative, requiredEntries))
                        archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
            }
        }

        if (requiredEntries is not null)
            VerifyRequiredEntries(temporary, requiredEntries);

        var destination = PathUtils.PathUnder(root, name);
        RequirePlainPath(destination);
        File.Move(temporary, destination, overwrite: true);
        Console.WriteLine("Local evidence output: " + destination);
    }

    private static Dictionary<string, RequiredArchiveEntry> BuildRequiredEntries(LocalEvidenceRunResult proof)
    {
        var requiredEntries = new Dictionary<string, RequiredArchiveEntry>(StringComparer.Ordinal);
        foreach (var input in proof.ReplayInputs)
            requiredEntries.Add(input.RelativePath, new(input.LengthBytes, input.Sha256));

        var planBytes = EvidenceCanonicalJson.Serialize(proof.Plan);
        requiredEntries.Add(RepositoryEvidenceLayout.PlanEntry,
            new(planBytes.LongLength, EvidenceDigest.Sha256(planBytes)));
        if (proof.Manifest is not { } manifest)
            return requiredEntries;

        var manifestBytes = EvidenceCanonicalJson.Serialize(manifest);
        requiredEntries.Add(RepositoryEvidenceLayout.ManifestEntry,
            new(manifestBytes.LongLength, EvidenceDigest.Sha256(manifestBytes)));
        foreach (var producer in manifest.ProducerResults)
        {
            foreach (var artifact in producer.Artifacts ?? [])
            {
                var entry = RepositoryEvidenceLayout.ArtifactEntry(producer.ProducerId, artifact.RelativePath);
                requiredEntries.Add(entry, new(artifact.LengthBytes, artifact.Sha256));
            }
        }

        return requiredEntries;
    }

    private static void VerifyRequiredEntries(string path, IReadOnlyDictionary<string, RequiredArchiveEntry> requiredEntries)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var (name, required) in requiredEntries)
        {
            var entry = archive.GetEntry(name);
            if (entry is null || entry.Length != required.LengthBytes)
                throw new InvalidDataException($"Local evidence archive is missing the verified content for {name}.");
            using var stream = entry.Open();
            var archivedSha256 = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(archivedSha256, required.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Local evidence archive digest mismatch for {name}.");
        }
    }

    private static readonly HashSet<string> EvidenceFiles =
    [
        RepositoryEvidenceLayout.PatchFile, RepositoryEvidenceLayout.SourceStateFile, RepositoryEvidenceLayout.PackageBuildLog,
        RepositoryEvidenceLayout.SummaryFile,
        RepositoryEvidenceLayout.ProofPrefix + RepositoryEvidenceLayout.PlanFile,
        RepositoryEvidenceLayout.ProofPrefix + RepositoryEvidenceLayout.SummaryFile,
        RepositoryEvidenceLayout.ProofPrefix + RepositoryEvidenceLayout.SelectionFile,
    ];

    private static bool ShouldRetain(string path, IReadOnlyDictionary<string, RequiredArchiveEntry>? requiredEntries)
    {
        if (requiredEntries?.ContainsKey(path) == true
            || path.StartsWith(RepositoryEvidenceLayout.PlanPrefix, StringComparison.Ordinal)
            || path.StartsWith(RepositoryEvidenceLayout.ArtifactPrefix, StringComparison.Ordinal))
            return true;
        // Failure and cancellation can have partial proof files without a verified inventory.
        if (requiredEntries is null
            && path is RepositoryEvidenceLayout.ChangedPathsFile or RepositoryEvidenceLayout.LocalPolicyFile or RepositoryEvidenceLayout.ManifestEntry)
            return true;
        if (EvidenceFiles.Contains(path))
            return true;
        if (!path.StartsWith(RepositoryEvidenceLayout.ProofPrefix, StringComparison.Ordinal))
            return false;

        var proofRelativePath = path[RepositoryEvidenceLayout.ProofPrefix.Length..];
        return PackageChecksProducer.RetainsLocalDiagnostic(proofRelativePath)
            || SolutionCoverageProducer.RetainsLocalDiagnostic(proofRelativePath);
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
