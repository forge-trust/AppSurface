using System.IO.Compression;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Owns the default local validation scratch and publishes bounded-history results in the worktree.</summary>
internal static class LocalEvidenceOutput
{
    private static readonly EnumerationOptions OutputFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MaxRecursionDepth = 64,
    };

    /// <summary>Runs against fresh scratch and replaces the matching compact archive after the run.</summary>
    /// <remarks>Explicit output paths bypass this lifecycle. The package-gate build cache remains separate from scratch.</remarks>
    internal static async Task<int> RunAsync(string repository, string operation,
        Func<string, string, Task<(EvidencePlan Plan, EvidenceManifest Manifest)?>> run)
    {
        var results = RequireDirectory(PathUtils.PathUnder(repository, "TestResults"));
        var evidence = RequireDirectory(PathUtils.PathUnder(results, "repository-evidence"));
        var root = RequireDirectory(PathUtils.PathUnder(evidence, "local"));
        var lockPath = PathUtils.PathUnder(root, "run.lock");
        RequirePlainPath(lockPath);
        using var lease = Acquire(lockPath);

        // Package discovery excludes hidden ancestors, not every ignored Git path.
        var scratch = RequireDirectory(PathUtils.PathUnder(root, ".scratch"));
        var packageCache = PathUtils.PathUnder(scratch, "package-restore");
        RequirePlainPath(packageCache);
        var workspace = EvidenceWorkspace.Create(scratch, new(MinimumFreeBytes: 0));
        var output = workspace.GetPath("run");
        var archive = operation == "local-plan" ? "latest-plan" : "latest";
        Console.WriteLine("Local evidence scratch: " + output);
        (EvidencePlan Plan, EvidenceManifest Manifest)? proof;
        try
        {
            proof = await run(output, packageCache);
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
                if (CanCleanFailedRun(output, failure))
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

        Publish(workspace, output, root, archive + ".zip", "Validation completed.", proof);
        await workspace.DisposeAsync();
        return 0;
    }

    private static bool CanCleanFailedRun(string output, Exception failure)
    {
        if (failure is EvidenceCommandException { Code: "cleanup_failed" })
            return false;
        var proof = PathUtils.PathUnder(output, "proof");
        if (!Directory.Exists(proof))
            return true;
        var manifest = PathUtils.PathUnder(proof, "evidence-manifest.json");
        if (!File.Exists(manifest))
            return false;
        var result = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(File.ReadAllBytes(manifest));
        return result.Metrics.CleanupCompleted && !result.ProducerResults.Any(producer =>
            producer.Diagnostic?.Contains("cleanup_failed", StringComparison.Ordinal) == true);
    }

    private static void Publish(EvidenceWorkspace workspace, string output, string root, string name, string status,
        (EvidencePlan Plan, EvidenceManifest Manifest)? proof = null)
    {
        var temporary = workspace.GetPath("retained.zip");
        using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("run-status.txt").Open()))
                writer.WriteLine(status);

            if (Directory.Exists(output))
            {
                foreach (var file in Directory.EnumerateFiles(output, "*", OutputFiles))
                {
                    var relative = Path.GetRelativePath(output, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (ShouldRetain(relative))
                        archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
            }
        }

        if (proof is { } verified)
            VerifyArchivedProof(temporary, verified.Plan, verified.Manifest);

        var destination = PathUtils.PathUnder(root, name);
        RequirePlainPath(destination);
        File.Move(temporary, destination, overwrite: true);
        Console.WriteLine("Local evidence output: " + destination);
    }

    private static void VerifyArchivedProof(string path, EvidencePlan plan, EvidenceManifest manifest)
    {
        using var archive = ZipFile.OpenRead(path);
        var planBytes = EvidenceCanonicalJson.Serialize(plan);
        var manifestBytes = EvidenceCanonicalJson.Serialize(manifest);
        VerifyEntry(archive, "plan/evidence-plan.json", planBytes.LongLength, EvidenceDigest.Sha256(planBytes));
        VerifyEntry(archive, "proof/evidence-manifest.json", manifestBytes.LongLength, EvidenceDigest.Sha256(manifestBytes));
        foreach (var producer in manifest.ProducerResults)
        {
            foreach (var artifact in producer.Artifacts ?? [])
            {
                var entry = $"proof/artifacts/{producer.ProducerId}/{artifact.RelativePath}";
                VerifyEntry(archive, entry, artifact.LengthBytes, artifact.Sha256);
            }
        }
    }

    private static void VerifyEntry(ZipArchive archive, string name, long length, string sha256)
    {
        var entry = archive.GetEntry(name);
        if (entry is null || entry.Length != length)
            throw new InvalidDataException($"Local evidence archive is missing the verified content for {name}.");
        using var stream = entry.Open();
        var archivedSha256 = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(archivedSha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Local evidence archive digest mismatch for {name}.");
    }

    private static readonly HashSet<string> EvidenceFiles =
    [
        "changed-paths.json", "changed.patch", "local-policy.json", "source-state.json", "package-build.log",
        "evidence-summary.md", "proof/evidence-plan.json", "proof/evidence-manifest.json", "proof/evidence-summary.md",
        "proof/selection.json", "proof/package-checks/gate.log", "proof/package-checks/verify.log", "proof/package-checks/verify-packages.log",
    ];

    private static readonly HashSet<string> CoverageFiles =
    [
        "coverage.cobertura.xml", "timings.json", "summary.txt", "reportgenerator-summary.txt",
        "slow-test-diagnostics.json", "slow-test-diagnostics.md", "coverage-gate.json", "coverage-gate.md",
        "coverage-patch-targets.json", "coverage-patch-targets.md",
    ];

    private static bool ShouldRetain(string path)
    {
        if (path.StartsWith("plan/", StringComparison.Ordinal) || path.StartsWith("proof/artifacts/", StringComparison.Ordinal))
            return true;
        if (EvidenceFiles.Contains(path))
            return true;
        const string packageReports = "proof/package-checks/artifacts/";
        if (path.StartsWith(packageReports, StringComparison.Ordinal))
        {
            var report = path[packageReports.Length..];
            return !report.Contains('/') && (report.EndsWith(".md", StringComparison.Ordinal) || report == "package-artifact-manifest.json");
        }
        if (!path.StartsWith("proof/coverage/", StringComparison.Ordinal))
            return false;

        var name = Path.GetFileName(path);
        return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("junit-", StringComparison.Ordinal)
            || CoverageFiles.Contains(name);
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
