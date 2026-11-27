using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;
using Xunit.Abstractions;
using ValidationProgram = ForgeTrust.AppSurface.Evidence.Validation.Program;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidencePackedReuseJourneyTests(ITestOutputHelper output)
{
    private const string DiagnosticsParentVariable = "APPSURFACE_PACKED_JOURNEY_DIAGNOSTICS";
    private const string FeedVariable = "APPSURFACE_PACKED_JOURNEY_FEED";
    private const string VersionVariable = "APPSURFACE_PACKED_JOURNEY_VERSION";

    [Fact]
    public void PhysicalDirectoryPreservesFilesystemRoots()
    {
        var temporaryRoot = new DirectoryInfo(Path.GetTempPath()).Root;
        var applicationRoot = new DirectoryInfo(AppContext.BaseDirectory).Root;

        Assert.Equal(temporaryRoot.FullName, PhysicalDirectory(temporaryRoot));
        Assert.Equal(applicationRoot.FullName, PhysicalDirectory(applicationRoot));
    }

    [Fact]
    public void DiagnosticsParentRejectsRelativeMissingAndCheckoutDirectories()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        Assert.Throws<ArgumentException>(() => CreateScratchDirectory(repository, "relative-diagnostics"));
        Assert.Throws<ArgumentException>(() => CreateScratchDirectory(repository,
            Path.Join(Path.GetTempPath(), "missing-diagnostics-" + Guid.NewGuid().ToString("N"))));
        Assert.Throws<ArgumentException>(() => CreateScratchDirectory(repository, repository));
        Assert.Throws<ArgumentException>(() => CreateScratchDirectory(repository, AppContext.BaseDirectory));
    }

    [Fact]
    public void DiagnosticsContainmentUsesDirectoryIdentityOnTheActualVolume()
    {
        var root = Directory.CreateTempSubdirectory("packed-case-containment-").FullName;
        try
        {
            var checkout = Directory.CreateDirectory(Path.Join(root, "CheckoutMixedCase")).FullName;
            var alias = Directory.CreateDirectory(Path.Join(root, "checkoutmixedcase")).FullName;
            using var checkoutLease = CoverageRunOutputLease.AcquireArtifactDirectory(checkout, false, false)!;
            using var aliasLease = CoverageRunOutputLease.AcquireArtifactDirectory(alias, false, false)!;
            if (checkoutLease.ArtifactDirectoryIdentity == aliasLease.ArtifactDirectoryIdentity)
            {
                Assert.Throws<ArgumentException>(() => CreateScratchDirectory(checkout, alias));
                var child = Directory.CreateDirectory(Path.Join(alias, "nested")).FullName;
                Assert.Throws<ArgumentException>(() => CreateScratchDirectory(checkout, child));
            }
            else
            {
                var scratch = CreateScratchDirectory(checkout, alias);
                Assert.True(Directory.Exists(scratch));
                NativeEvidenceTestDirectory.Delete(scratch);
            }
        }
        finally
        {
            NativeEvidenceTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void DiagnosticsParentReceivesUniquePrivateChildrenAndPreservesExistingFiles()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var parent = Directory.CreateTempSubdirectory("packed-diagnostics-parent-").FullName;
        try
        {
            var sentinel = Path.Join(parent, "sentinel.txt");
            File.WriteAllText(sentinel, "caller-owned");
            var first = CreateScratchDirectory(repository, parent);
            var second = CreateScratchDirectory(repository, parent);
            Assert.NotEqual(first, second);
            Assert.Equal(PhysicalDirectory(new DirectoryInfo(parent)), Directory.GetParent(first)!.FullName);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectoryInfo(first).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                Assert.Equal(WindowsIdentity.GetCurrent().User, security.GetOwner(typeof(SecurityIdentifier)));
                var rule = Assert.Single(security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
                Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
                Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(first));
            }
            NativeEvidenceTestDirectory.Delete(first);
            NativeEvidenceTestDirectory.Delete(second);
            Assert.Equal("caller-owned", File.ReadAllText(sentinel));
        }
        finally
        {
            NativeEvidenceTestDirectory.Delete(parent);
        }
    }

    [Fact]
    public async Task RejectedPreparedFeedReportsExitCodeAndDiagnosticsWithoutAProofReport()
    {
        var scratch = Directory.CreateTempSubdirectory("rejected-packed-journey-").FullName;
        try
        {
            var feed = Directory.CreateDirectory(TestPathUtils.PathUnder(scratch, "empty-feed")).FullName;
            var proofRoot = TestPathUtils.PathUnder(scratch, "proof");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await ValidationProgram.RunAsync(
                ["--operation", "reuse-package-proof", "--feed", feed, "--version", "0.1.0-test",
                    "--output", proofRoot, "--dotnet", TestDotnetHost.ResolveExisting(), "--source-commit",
                    "0123456789abcdef0123456789abcdef01234567"], deadline.Token);

            Assert.Equal(1, result);
            Assert.False(Directory.Exists(proofRoot));
            var reportPath = TestPathUtils.PathUnder(proofRoot, "proof.json");
            var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(
                () => ReadJourneyReportAsync(result, reportPath, scratch, deadline.Token));
            Assert.Contains("Packed journey exited with code 1.", failure.Message, StringComparison.Ordinal);
            Assert.Contains(scratch, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            NativeEvidenceTestDirectory.Delete(scratch);
        }
    }

    [Fact]
    public async Task ActualPackedConsumersRestoreTheOriginalReportsAndSkipWarmHostStartup()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        // Assembly.Location uses the physical path on macOS; pin scratch to the same spelling
        // before the proof compares the independently loaded consumer assemblies to their root.
        var scratch = CreateScratchDirectory(repository, Environment.GetEnvironmentVariable(DiagnosticsParentVariable));
        var work = TestPathUtils.PathUnder(scratch, "work");
        var proofRoot = TestPathUtils.PathUnder(work, "proof");
        var dotnet = TestDotnetHost.ResolveExisting();
        using var progress = PackedProofProgress.TryCreate(TestPathUtils.PathUnder(scratch, "fixture-progress.jsonl"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var passed = false;
        try
        {
            Directory.CreateDirectory(work);
            var git = PackedProofProcess.CreateStartInfo("git", ["rev-parse", "HEAD"], repository);
            var source = await PackedProofProcess.RunAsync("source", git, scratch, deadline.Token);
            Assert.True(source.ExitCode == 0, $"Source lookup failed. Retained logs: {scratch}");
            var commit = source.StandardOutput.Trim();
            var feed = Environment.GetEnvironmentVariable(FeedVariable);
            var version = Environment.GetEnvironmentVariable(VersionVariable);
            if (feed is null && version is null)
            {
                feed = TestPathUtils.PathUnder(work, "feed");
                version = "0.0.0-packed-test." + Guid.NewGuid().ToString("N");
                // The test project's reference graph already restored the package projects.
                await PackedCandidateFeed.PrepareAsync(new(repository, dotnet, feed, version, commit,
                    PackedCandidateFeed.Mode.Reuse, LogDirectory: scratch, NoRestore: true), deadline.Token);
            }
            else if (feed is null || !Path.IsPathFullyQualified(feed) || string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException($"Set {FeedVariable} to an absolute feed path and {VersionVariable} to its version.");

            progress?.Record("consumer-proof", "started");
            var result = await ValidationProgram.RunAsync(
                ["--operation", "reuse-package-proof", "--feed", feed, "--version", version,
                    "--output", proofRoot, "--dotnet", dotnet, "--source-commit", commit], deadline.Token);
            progress?.Record("consumer-proof", "completed", result);
            var reportPath = TestPathUtils.PathUnder(proofRoot, "proof.json");
            progress?.Record("test-proof-validation", "started");
            var report = await ReadJourneyReportAsync(result, reportPath, scratch, deadline.Token);
            AssertJourneyProof(report, proofRoot, commit);
            progress?.Record("test-proof-validation", "completed");
            passed = true;
        }
        finally
        {
            if (passed)
            {
                // Keep the marker outside the payload being removed so a watchdog can retain it.
                progress?.Record("fixture-cleanup", "started");
                NativeEvidenceTestDirectory.Delete(work);
                progress?.Record("fixture-cleanup", "completed");
                progress?.Dispose();
                NativeEvidenceTestDirectory.Delete(scratch);
            }
            else output.WriteLine("Retained packed consumer diagnostics: " + scratch);
        }
    }

    private static async Task<string> ReadJourneyReportAsync(
        int exitCode, string reportPath, string scratch, CancellationToken cancellationToken)
    {
        if (exitCode == 0)
            return await File.ReadAllTextAsync(reportPath, cancellationToken);

        var failure = $"Packed journey exited with code {exitCode}. Retained diagnostics are in {scratch}.";
        try
        {
            var report = await File.ReadAllTextAsync(reportPath, cancellationToken);
            failure += $"{Environment.NewLine}Report from {reportPath}{Environment.NewLine}{report}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // A refused feed may never produce a report. Optional diagnostics cannot replace its exit code.
        }

        throw new Xunit.Sdk.XunitException(failure);
    }

    private static void AssertJourneyProof(string report, string proofRoot, string expectedCommit)
    {
        using var document = JsonDocument.Parse(report);
        var proof = document.RootElement;
        Assert.True(proof.GetProperty("passed").GetBoolean());
        Assert.Equal(expectedCommit, proof.GetProperty("expectedSourceCommit").GetString());
        Assert.Equal(8, proof.GetProperty("packages").GetArrayLength());
        Assert.Equal(["host-run", "host-published-run", "cli-fresh", "cli-warm", "restored-coverage-reader", "cli-audit"],
            proof.GetProperty("stages").EnumerateArray()
                .Where(stage => stage.GetProperty("SyntheticLocal").GetBoolean())
                .Select(stage => stage.GetProperty("Stage").GetString()));
        var negatives = proof.GetProperty("negatives").EnumerateArray().ToArray();
        Assert.Equal(["tool-graph-version", "host-private-edge"],
            negatives.Select(item => item.GetProperty("kind").GetString()));
        Assert.Equal(
            ["Native validation contract failed: packed-reuse/tool-bundled-graph-version",
                "Native validation contract failed: packed-reuse/exact-private-edge-in-every-framework-group"],
            negatives.Select(item => item.GetProperty("contract").GetString()));
        Assert.All(negatives, item =>
        {
            Assert.Equal(1, item.GetProperty("exitCode").GetInt32());
            Assert.False(item.GetProperty("outputCreated").GetBoolean());
            var rejectedOutput = Path.Join(proofRoot, "negative-" + item.GetProperty("kind").GetString(), "must-not-exist");
            Assert.False(Directory.Exists(rejectedOutput));
            Assert.False(File.Exists(rejectedOutput));
            Assert.False(File.Exists(rejectedOutput + ".packed-reuse.claim"));
        });
        foreach (var mode in new[] { "built", "publish" })
        {
            var host = proof.GetProperty("host").GetProperty(mode);
            Assert.Equal(1, host.GetProperty("firstFactoryCount").GetInt32());
            Assert.Equal(0, host.GetProperty("secondFactoryCount").GetInt32());
            Assert.Equal(1, host.GetProperty("cleanedApplications").GetInt32());
            Assert.Equal("Reused", host.GetProperty("secondOutcome").GetString());
        }

        var runs = proof.GetProperty("cli").GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(["fresh", "warm", "audit"], runs.Select(run => run.GetProperty("mode").GetString()));
        Assert.Equal(["Passed", "Reused", "Passed"], runs.Select(run => run.GetProperty("outcome").GetString()));
        Assert.Equal(runs[0].GetProperty("coverageSha256").GetString(), runs[1].GetProperty("coverageSha256").GetString());
        Assert.All(runs, run =>
        {
            Assert.Equal(6, run.GetProperty("testCaseCount").GetInt32());
            Assert.Equal(2, run.GetProperty("testProjectCount").GetInt32());
            var inventory = run.GetProperty("inventory").EnumerateArray().ToArray();
            Assert.True(run.GetProperty("artifactCount").GetInt32() > 5);
            Assert.Equal(inventory.Sum(artifact => artifact.GetProperty("LengthBytes").GetInt64()),
                run.GetProperty("artifactBytes").GetInt64());
            var coverage = Assert.Single(inventory,
                artifact => artifact.GetProperty("RelativePath").GetString() == "coverage/coverage.cobertura.xml");
            Assert.Equal(coverage.GetProperty("Sha256").GetString(), run.GetProperty("coverageSha256").GetString());
        });
    }

    /// <summary>Creates private test scratch, optionally beneath an existing diagnostics parent outside the checkout.</summary>
    /// <remarks>
    /// A configured parent receives a random private child (Unix mode 0700 or protected current-user Windows ACL),
    /// including when runner diagnostics and platform temporary storage occupy different volumes.
    /// Successful journeys delete only their child; failures retain it. Without a parent, BCL temporary creation is used.
    /// Physical paths reject a diagnostics parent that resolves into the checkout. This is trusted runner setup,
    /// not an adversarial filesystem sandbox. The caller owns cleanup and must not treat retained logs as acceptance.
    /// </remarks>
    internal static string CreateScratchDirectory(string repository, string? diagnosticsParent)
    {
        string? parent = null;
        if (!string.IsNullOrEmpty(diagnosticsParent))
        {
            if (!Path.IsPathFullyQualified(diagnosticsParent) || !Directory.Exists(diagnosticsParent))
                throw new ArgumentException("Packed journey diagnostics require an existing absolute parent.", nameof(diagnosticsParent));
            parent = PhysicalDirectory(new DirectoryInfo(diagnosticsParent));
            var checkout = Path.TrimEndingDirectorySeparator(PhysicalDirectory(new DirectoryInfo(repository)));
            using var checkoutLease = CoverageRunOutputLease.AcquireArtifactDirectory(checkout, false, false)
                ?? throw new IOException("The checkout identity is unavailable.");
            for (var ancestor = new DirectoryInfo(parent); ancestor is not null; ancestor = ancestor.Parent)
            {
                using var ancestorLease = CoverageRunOutputLease.AcquireArtifactDirectory(ancestor.FullName, false, false)
                    ?? throw new IOException("A diagnostics ancestor identity is unavailable.");
                if (ancestorLease.ArtifactDirectoryIdentity == checkoutLease.ArtifactDirectoryIdentity)
                    throw new ArgumentException("Packed journey diagnostics must be outside the checkout.", nameof(diagnosticsParent));
            }
        }

        if (parent is null) return PhysicalDirectory(Directory.CreateTempSubdirectory("evidence-packed-journey-"));
        var destination = Path.Join(parent, "evidence-packed-journey-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current Windows user has no security identifier.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(destination).Create(security);
        }
        else Directory.CreateDirectory(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return PhysicalDirectory(new DirectoryInfo(destination));
    }

    private static string PhysicalDirectory(DirectoryInfo directory)
    {
        // Volume and UNC roots are terminal; Windows cannot inspect them as links.
        if (directory.Parent is not { } parent) return directory.FullName;
        if (directory.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            return PhysicalDirectory(new DirectoryInfo(target.FullName));
        return TestPathUtils.PathUnder(PhysicalDirectory(parent), directory.Name);
    }
}
