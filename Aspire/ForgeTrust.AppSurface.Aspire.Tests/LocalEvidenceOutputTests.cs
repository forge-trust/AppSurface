using System.IO.Compression;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class LocalEvidenceOutputTests : IDisposable
{
    private readonly string _repository = Directory.CreateTempSubdirectory("local-evidence-output-tests-").FullName;

    [Theory]
    [InlineData("local", "latest.zip")]
    [InlineData("local-plan", "latest-plan.zip")]
    public async Task SuccessfulRun_PublishesOperationSpecificArchive(string operation, string archiveName)
    {
        var output = string.Empty;
        var exitCode = await LocalEvidenceOutput.RunAsync(_repository, operation, (scratch, _) =>
        {
            output = scratch;
            return Task.FromResult(0);
        });

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(output));
        using var archive = OpenArchive(archiveName);
        Assert.Contains("run-status.txt", EntryNames(archive));
        Assert.Contains("Exit code: 0", ReadEntry(archive, "run-status.txt"));
    }

    [Theory]
    [InlineData("local", "latest.zip", "latest-failure.zip")]
    [InlineData("local-plan", "latest-plan.zip", "latest-plan-failure.zip")]
    public async Task NonzeroRun_PublishesFailureAndPreservesPriorSuccess(
        string operation,
        string successArchiveName,
        string failureArchiveName)
    {
        var successOutput = await LocalEvidenceOutput.RunAsync(_repository, operation, async (scratch, _) =>
        {
            await WriteOutputFileAsync(scratch, "package-build.log", "prior success");
            return 0;
        });
        Assert.Equal(0, successOutput);
        var successPath = Under(LocalRoot, successArchiveName);
        var priorSuccess = await File.ReadAllBytesAsync(successPath);

        var failureScratch = string.Empty;
        var failureExitCode = await LocalEvidenceOutput.RunAsync(_repository, operation, async (scratch, _) =>
        {
            failureScratch = scratch;
            await WriteOutputFileAsync(scratch, "package-build.log", "current failure");
            return 7;
        });

        Assert.Equal(7, failureExitCode);
        Assert.False(Directory.Exists(failureScratch));
        Assert.Equal(priorSuccess, await File.ReadAllBytesAsync(successPath));
        using var priorArchive = OpenArchive(successArchiveName);
        Assert.Equal("prior success", ReadEntry(priorArchive, "package-build.log"));
        using var failureArchive = OpenArchive(failureArchiveName);
        Assert.Contains("Exit code: 7", ReadEntry(failureArchive, "run-status.txt"));
        Assert.Equal("current failure", ReadEntry(failureArchive, "package-build.log"));
    }

    [Theory]
    [InlineData("local", "latest-failure.zip")]
    [InlineData("local-plan", "latest-plan-failure.zip")]
    public async Task ThrownRunFailure_RetainsFailureArchiveAndRethrowsOriginalException(string operation, string archiveName)
    {
        var failure = new InvalidOperationException("expected test failure");
        var scratchPath = string.Empty;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalEvidenceOutput.RunAsync(_repository, operation, async (scratch, _) =>
            {
                scratchPath = scratch;
                await WriteOutputFileAsync(scratch, "package-build.log", "preserve this detail");
                throw failure;
            }));

        Assert.Same(failure, thrown);
        Assert.False(Directory.Exists(scratchPath));
        using var archive = OpenArchive(archiveName);
        Assert.Contains("Failed: InvalidOperationException: expected test failure", ReadEntry(archive, "run-status.txt"));
        Assert.Equal("preserve this detail", ReadEntry(archive, "package-build.log"));
    }

    [Fact]
    public async Task ArchiveCreationFailure_RetainsScratchAndRethrowsRunException()
    {
        var failure = new InvalidOperationException("expected archive failure");
        var scratchPath = string.Empty;
        var retainedArchivePath = string.Empty;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalEvidenceOutput.RunAsync(_repository, "local", async (scratch, _) =>
            {
                scratchPath = scratch;
                await WriteOutputFileAsync(scratch, "proof/coverage/failure-detail.log", "preserve this detail");
                retainedArchivePath = Under(Path.GetDirectoryName(scratch)!, "retained.zip");
                Directory.CreateDirectory(retainedArchivePath);
                throw failure;
            }));

        Assert.Same(failure, thrown);
        Assert.True(Directory.Exists(scratchPath));
        Assert.True(Directory.Exists(retainedArchivePath));
        Assert.False(File.Exists(Under(LocalRoot, "latest-failure.zip")));
    }

    [Fact]
    public async Task PublishedArchive_RetainsOnlyBoundedLogsReportsAndDeclaredEvidenceFiles()
    {
        var retained = new[]
        {
            "plan/evidence-plan.json",
            "plan/nested/detail.json",
            "proof/artifacts/producer/result.bin",
            "changed-paths.json",
            "changed.patch",
            "local-policy.json",
            "source-state.json",
            "package-build.log",
            "evidence-summary.md",
            "proof/evidence-plan.json",
            "proof/evidence-manifest.json",
            "proof/evidence-summary.md",
            "proof/selection.json",
            "proof/package-checks/gate.log",
            "proof/package-checks/verify.log",
            "proof/package-checks/verify-packages.log",
            "proof/package-checks/artifacts/report.md",
            "proof/coverage/build.LOG",
            "proof/coverage/nested/stage.log",
            "proof/coverage/junit-tests.xml",
            "proof/coverage/coverage.cobertura.xml",
            "proof/coverage/timings.json",
            "proof/coverage/summary.txt",
            "proof/coverage/reportgenerator-summary.txt",
            "proof/coverage/slow-test-diagnostics.json",
            "proof/coverage/slow-test-diagnostics.md",
            "proof/coverage/coverage-gate.json",
            "proof/coverage/coverage-gate.md",
            "proof/coverage/coverage-patch-targets.json",
            "proof/coverage/coverage-patch-targets.md",
        };
        var omitted = new[]
        {
            "unrelated.txt",
            "feed/candidate.nupkg",
            "proof/package-checks/artifacts/nested/report.md",
            "proof/package-checks/artifacts/report.txt",
            "proof/other-producer/output.log",
            "proof/coverage/nested/unlisted.bin",
        };

        await LocalEvidenceOutput.RunAsync(_repository, "local", async (scratch, _) =>
        {
            foreach (var path in retained.Concat(omitted))
                await WriteOutputFileAsync(scratch, path, path);
            return 0;
        });

        using var archive = OpenArchive("latest.zip");
        var entries = EntryNames(archive);
        foreach (var path in retained)
            Assert.Contains(path, entries);
        foreach (var path in omitted)
            Assert.DoesNotContain(path, entries);
    }

    [Theory]
    [InlineData("plan", true)]
    [InlineData("plan/nested", true)]
    [InlineData("proof", true)]
    [InlineData("proof/artifacts", true)]
    [InlineData("proof/artifacts/producer/nested", true)]
    [InlineData("proof/coverage", true)]
    [InlineData("proof/coverage/nested", true)]
    [InlineData("proof/package-checks", true)]
    [InlineData("proof/package-checks/artifacts", true)]
    [InlineData("feed", false)]
    [InlineData("proof/coverage-staging", false)]
    [InlineData("proof/other-producer", false)]
    [InlineData("proof/package-checks/nuget-packages", false)]
    [InlineData("proof/package-checks/artifacts/nested", false)]
    public void ArchiveTraversal_OnlyEntersDirectoriesThatCanContributeEntries(string directory, bool expected)
    {
        Assert.Equal(expected, LocalEvidenceOutput.CanTraverseArchiveDirectory(directory));
    }

    [Theory]
    [InlineData("no-proof", true)]
    [InlineData("missing-manifest", false)]
    [InlineData("invalid-manifest", false)]
    [InlineData("oversized-manifest", false)]
    [InlineData("incomplete-manifest", false)]
    [InlineData("completed-manifest", true)]
    [InlineData("diagnostic-only", true)]
    [InlineData("unjoined-cleanup", false)]
    public async Task NonzeroRun_CleansScratchOnlyWhenFailureEvidenceAllowsIt(string proofState, bool cleansScratch)
    {
        var scratchPath = string.Empty;
        var exitCode = await LocalEvidenceOutput.RunAsync(_repository, "local", async (scratch, managedRun) =>
        {
            scratchPath = scratch;
            switch (proofState)
            {
                case "missing-manifest":
                    Directory.CreateDirectory(Under(scratch, "proof"));
                    break;
                case "invalid-manifest":
                    await WriteManifestBytesAsync(scratch, "{");
                    break;
                case "oversized-manifest":
                    await WriteManifestAsync(scratch, cleanupCompleted: true, producerDiagnostic: null);
                    using (var writer = File.AppendText(Under(scratch, "proof", "evidence-manifest.json")))
                    {
                        // Keep the document valid while pushing it past the parser's byte ceiling.
                        var padding = new string(' ', 64 * 1024);
                        for (var written = 0; written < EvidenceCanonicalJson.MaximumInputBytes; written += padding.Length)
                        {
                            await writer.WriteAsync(padding);
                        }
                    }
                    break;
                case "incomplete-manifest":
                    await WriteManifestAsync(scratch, cleanupCompleted: false, producerDiagnostic: null);
                    break;
                case "completed-manifest":
                    await WriteManifestAsync(scratch, cleanupCompleted: true, producerDiagnostic: null);
                    break;
                case "diagnostic-only":
                    await WriteManifestAsync(scratch, cleanupCompleted: true, producerDiagnostic: "Older diagnostic mentioned cleanup_failed.");
                    break;
                case "unjoined-cleanup":
                    var failure = new EvidenceCommandException("cleanup_failed", "The child did not exit.");
                    var request = new EvidenceCommandRequest("test-command", [], scratch, TimeSpan.FromSeconds(1));
                    var command = managedRun.Settlement.Track((_, _) => Task.FromException<EvidenceCommandResult>(failure));
                    Assert.Same(failure, await Assert.ThrowsAsync<EvidenceCommandException>(() =>
                        command(request, CancellationToken.None)));
                    await WriteManifestAsync(scratch, cleanupCompleted: true, producerDiagnostic: "The coverage command failed.");
                    break;
            }

            return 9;
        });

        Assert.Equal(9, exitCode);
        Assert.Equal(!cleansScratch, Directory.Exists(scratchPath));
        using var archive = OpenArchive("latest-failure.zip");
        Assert.Contains("Exit code: 9", ReadEntry(archive, "run-status.txt"));
        Assert.Equal(proofState is not ("no-proof" or "missing-manifest"),
            archive.GetEntry("proof/evidence-manifest.json") is not null);
    }

    [Fact]
    public async Task CleanupFailedCommandException_RetainsScratchEvenWithoutProofDirectory()
    {
        var failure = new EvidenceCommandException("cleanup_failed", "owned child did not stop");
        var scratchPath = string.Empty;

        var thrown = await Assert.ThrowsAsync<EvidenceCommandException>(() =>
            LocalEvidenceOutput.RunAsync(_repository, "local", (scratch, _) =>
            {
                scratchPath = scratch;
                Directory.CreateDirectory(scratch);
                return Task.FromException<int>(failure);
            }));

        Assert.Same(failure, thrown);
        Assert.True(Directory.Exists(scratchPath));
        using var archive = OpenArchive("latest-failure.zip");
        Assert.Contains("Failed: EvidenceCommandException: owned child did not stop", ReadEntry(archive, "run-status.txt"));
    }

    [Fact]
    public async Task ConcurrentManagedRun_IsRejectedWhileWorktreeLockIsHeld()
    {
        var nestedRunRejected = false;
        var exitCode = await LocalEvidenceOutput.RunAsync(_repository, "local", async (_, _) =>
        {
            await Assert.ThrowsAsync<IOException>(() =>
                LocalEvidenceOutput.RunAsync(_repository, "local", (_, _) => Task.FromResult(0)));
            nestedRunRejected = true;
            return 0;
        });

        Assert.True(nestedRunRejected);
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Under(LocalRoot, "latest.zip")));
    }

    [Fact]
    public async Task LinkedLocalOutputRoot_IsRejectedBeforeTheRunDelegateStarts()
    {
        var results = Under(_repository, "TestResults");
        var evidence = Under(results, "repository-evidence");
        var target = Under(_repository, "alternate-local-output");
        Directory.CreateDirectory(evidence);
        Directory.CreateDirectory(target);
        var linkedRoot = Under(evidence, "local");
        if (!TryCreateDirectorySymbolicLink(linkedRoot, target))
            return;

        var runStarted = false;
        var failure = await Assert.ThrowsAsync<IOException>(() =>
            LocalEvidenceOutput.RunAsync(_repository, "local", (_, _) =>
            {
                runStarted = true;
                return Task.FromResult(0);
            }));

        Assert.False(runStarted);
        Assert.Contains("symbolic links or reparse points", failure.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    public void Dispose() => NativeEvidenceTestDirectory.Delete(_repository);

    private string LocalRoot => Under(_repository, "TestResults", "repository-evidence", "local");

    private ZipArchive OpenArchive(string name) =>
        new(File.OpenRead(Under(LocalRoot, name)), ZipArchiveMode.Read);

    private static HashSet<string> EntryNames(ZipArchive archive) =>
        archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    private static async Task WriteOutputFileAsync(string output, string relativePath, string contents)
    {
        var path = Under(output, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
    }

    private static async Task WriteManifestAsync(string output, bool cleanupCompleted, string? producerDiagnostic)
    {
        IReadOnlyList<EvidenceProducerResult> producerResults = producerDiagnostic is null
            ? []
            : [new EvidenceProducerResult("producer", EvidenceProducerOutcome.Failed, [], producerDiagnostic)];
        var draft = new EvidenceManifest(
            "1.0",
            "plan-digest",
            EvidenceExecutionVerdict.Incomplete,
            EvidenceClaimKind.None,
            EvidenceClaimEligibility.None,
            EvidenceEnvelopeStatus.NotRequired,
            [],
            [],
            [],
            [],
            producerResults,
            new EvidenceExecutionMetrics(CleanupCompleted: cleanupCompleted),
            string.Empty);
        var manifest = draft with { ManifestDigest = EvidenceDigest.CanonicalSha256(draft) };
        await WriteManifestBytesAsync(output, EvidenceCanonicalJson.Serialize(manifest));
    }

    private static Task WriteManifestBytesAsync(string output, string contents) =>
        WriteManifestBytesAsync(output, System.Text.Encoding.UTF8.GetBytes(contents));

    private static async Task WriteManifestBytesAsync(string output, byte[] contents)
    {
        var manifestPath = Under(output, "proof", "evidence-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllBytesAsync(manifestPath, contents);
    }

    private static string Under(string root, params string[] pathSegments) =>
        TestPathUtils.PathUnder(root, pathSegments.Length == 1
            ? pathSegments[0].Split('/', StringSplitOptions.RemoveEmptyEntries)
            : pathSegments);

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
