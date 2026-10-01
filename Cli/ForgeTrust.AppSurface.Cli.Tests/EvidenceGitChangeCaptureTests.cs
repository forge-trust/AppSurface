using System.Diagnostics;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceGitChangeCaptureTests
{
    [Fact]
    public void ParseNameStatus_ShouldIncludeNonTextChangesAndBothRenamePaths()
    {
        var bytes = Encoding.UTF8.GetBytes("A\0src/new.cs\0M\0images/logo.bin\0T\0links/current\0R100\0src/old.cs\0src/moved.cs\0C75\0src/base.cs\0src/copied.cs\0D\0src/gone.cs\0");

        var paths = EvidenceGitChangeCapture.ParseNameStatus(bytes);

        Assert.Collection(
            paths,
            path => Assert.Equal(("src/new.cs", "added", (string?)null), (path.Path, path.Kind, path.PreviousPath)),
            path => Assert.Equal(("images/logo.bin", "modified", (string?)null), (path.Path, path.Kind, path.PreviousPath)),
            path => Assert.Equal(("links/current", "typechanged", (string?)null), (path.Path, path.Kind, path.PreviousPath)),
            path => Assert.Equal(("src/moved.cs", "renamed", "src/old.cs"), (path.Path, path.Kind, path.PreviousPath)),
            path => Assert.Equal(("src/copied.cs", "copied", "src/base.cs"), (path.Path, path.Kind, path.PreviousPath)),
            path => Assert.Equal(("src/gone.cs", "deleted", (string?)null), (path.Path, path.Kind, path.PreviousPath)));
    }

    [Theory]
    [InlineData("A\0src/file.cs")]
    [InlineData("U\0src/file.cs\0")]
    [InlineData("R1000\0src/old.cs\0src/new.cs\0")]
    [InlineData("R101\0src/old.cs\0src/new.cs\0")]
    [InlineData("R50\0src/old.cs\0")]
    [InlineData("A\0../outside\0")]
    [InlineData("A\0src//file.cs\0")]
    [InlineData("A\0src\\file.cs\0")]
    [InlineData("A\0src/file\n.cs\0")]
    [InlineData("A\0src/file\t.cs\0")]
    [InlineData("A\0src/file.cs\0A\0src/file.cs\0")]
    public void ParseNameStatus_ShouldRejectMalformedOrUnsafeRecords(string output)
    {
        var exception = Assert.Throws<EvidencePlanningException>(
            () => EvidenceGitChangeCapture.ParseNameStatus(Encoding.UTF8.GetBytes(output)));

        Assert.Equal("ASEVD135", exception.Code);
    }

    [Fact]
    public void ParseNameStatus_ShouldRejectInvalidUtf8AndOversizedPath()
    {
        var invalidUtf8 = new byte[] { (byte)'A', 0, 0xff, 0 };
        var longPath = Encoding.UTF8.GetBytes($"A\0{new string('x', EvidenceGitChangeCapture.MaximumPathBytes + 1)}\0");

        Assert.Equal("ASEVD135", Assert.Throws<EvidencePlanningException>(
            () => EvidenceGitChangeCapture.ParseNameStatus(invalidUtf8)).Code);
        Assert.Equal("ASEVD135", Assert.Throws<EvidencePlanningException>(
            () => EvidenceGitChangeCapture.ParseNameStatus(longPath)).Code);
    }

    [Fact]
    public void ParseNameStatus_ShouldBoundRecordCount()
    {
        var builder = new StringBuilder();
        for (var index = 0; index <= EvidenceGitChangeCapture.MaximumChangedRecords; index++)
        {
            builder.Append("A\0src/file").Append(index).Append(".cs\0");
        }

        var exception = Assert.Throws<EvidencePlanningException>(
            () => EvidenceGitChangeCapture.ParseNameStatus(Encoding.UTF8.GetBytes(builder.ToString())));

        Assert.Equal("ASEVD134", exception.Code);
    }

    [Fact]
    public async Task CaptureAndVerify_ShouldBindExactCommitsAndIncludeBinaryPath()
    {
        using var repository = new GitFixture();
        File.WriteAllText(Path.Join(repository.Path, "code.cs"), "before\n");
        repository.Run("add", "code.cs");
        repository.Commit("base");
        var baseRevision = repository.Run("rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Join(repository.Path, "code.cs"), "after\n");
        File.WriteAllBytes(Path.Join(repository.Path, "blob.bin"), [0, 1, 2, 255, 0, 254]);
        repository.Run("add", "code.cs", "blob.bin");
        repository.Commit("head");
        var headRevision = repository.Run("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Join(repository.Path, "unrelated.txt"), "later\n");
        repository.Run("add", "unrelated.txt");
        repository.Commit("ambient-head-moved");
        repository.Run("config", "diff.external", "false");

        var snapshot = await EvidenceGitChangeCapture.CaptureAsync(repository.Path, baseRevision, headRevision);
        await EvidenceGitChangeCapture.VerifyAsync(repository.Path, snapshot);

        Assert.Equal(baseRevision, snapshot.BaseRevision);
        Assert.Equal(headRevision, snapshot.HeadRevision);
        Assert.Equal(64, snapshot.SourceDiffDigest.Length);
        Assert.Equal(64, snapshot.NameStatusDigest.Length);
        Assert.Contains(snapshot.ChangedPaths, path => path.Path == "blob.bin" && path.Kind == "added");
        Assert.Contains(snapshot.ChangedPaths, path => path.Path == "code.cs" && path.Kind == "modified");
        Assert.DoesNotContain(snapshot.ChangedPaths, path => path.Path == "unrelated.txt");
        Assert.Contains("diff --git", Encoding.UTF8.GetString(snapshot.SourceDiff.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_ShouldSelectRenameModeSymlinkAndSubmoduleWithoutTextHunks()
    {
        using var repository = new GitFixture();
        File.WriteAllText(Path.Join(repository.Path, "mode.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Join(repository.Path, "old.txt"), "unchanged\n");
        repository.Run("add", "mode.sh", "old.txt");
        repository.Commit("base");
        var baseRevision = repository.Run("rev-parse", "HEAD").Trim();

        repository.Run("mv", "old.txt", "moved.txt");
        repository.Run("update-index", "--chmod=+x", "mode.sh");
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Join(repository.Path, "linked"), "moved.txt");
            repository.Run("add", "linked");
        }
        repository.Run("update-index", "--add", "--cacheinfo", $"160000,{baseRevision},submodule");
        repository.Commit("nontext");
        var headRevision = repository.Run("rev-parse", "HEAD").Trim();

        var snapshot = await EvidenceGitChangeCapture.CaptureAsync(repository.Path, baseRevision, headRevision);

        Assert.Contains(snapshot.ChangedPaths, path => path.Path == "moved.txt" && path.PreviousPath == "old.txt" && path.Kind == "renamed");
        Assert.Contains(snapshot.ChangedPaths, path => path.Path == "mode.sh" && path.Kind == "modified");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(snapshot.ChangedPaths, path => path.Path == "linked" && path.Kind == "added");
        }
        Assert.Contains(snapshot.ChangedPaths, path => path.Path == "submodule" && path.Kind == "added");
    }

    [Theory]
    [InlineData("main")]
    [InlineData("abc123")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Capture_ShouldRejectRefsAndNonCanonicalObjectIds(string revision)
    {
        using var repository = new GitFixture();

        var exception = await Assert.ThrowsAsync<EvidencePlanningException>(
            () => EvidenceGitChangeCapture.CaptureAsync(repository.Path, revision, revision));

        Assert.Equal("ASEVD130", exception.Code);
    }

    [Fact]
    public async Task Capture_ShouldRejectMissingCommitObject()
    {
        using var repository = new GitFixture();
        var missingRevision = new string('a', 40);

        var exception = await Assert.ThrowsAsync<EvidencePlanningException>(
            () => EvidenceGitChangeCapture.CaptureAsync(repository.Path, missingRevision, missingRevision));

        Assert.Equal("ASEVD136", exception.Code);
    }

    [Fact]
    public async Task RevisionBoundCli_ShouldRecreateV2IdentityAndRejectTamperedDiffOrPolicy()
    {
        using var repository = new GitFixture();
        Directory.CreateDirectory(Path.Join(repository.Path, "docs"));
        var documentPath = Path.Join(repository.Path, "docs", "readme.md");
        File.WriteAllText(documentPath, "before\n");
        repository.Run("add", "docs/readme.md");
        repository.Commit("base");
        var baseRevision = repository.Run("rev-parse", "HEAD").Trim();
        File.WriteAllText(documentPath, "after\n");
        repository.Run("add", "docs/readme.md");
        repository.Commit("head");
        var headRevision = repository.Run("rev-parse", "HEAD").Trim();
        var snapshot = await EvidenceGitChangeCapture.CaptureAsync(repository.Path, baseRevision, headRevision);
        var diffPath = Path.Join(repository.Path, "change.diff");
        await File.WriteAllBytesAsync(diffPath, snapshot.SourceDiff.ToArray());
        var policyPath = Path.Join(repository.Path, "policy.json");
        var policy = CreateGatePolicy();
        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(policy));
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var request = new EvidencePlanningRequest(policyPath, [], diffPath, baseRevision, headRevision, repository.Path, GateMode: true);

        var first = await workflow.ExplainAsync(request, CancellationToken.None);
        var second = await workflow.ExplainAsync(request, CancellationToken.None);
        Assert.Equal(first.PlanDigest, second.PlanDigest);
        Assert.Equal("2.0", first.ContractVersion);
        Assert.Equal(snapshot.SourceDiffDigest, first.SourceDiffDigest);
        Assert.Equal(snapshot.NameStatusDigest, first.NameStatusDigest);
        var manifest = EvidenceManifestBuilder.Build(first, []);
        Assert.True(EvidenceManifestBuilder.Verify(first, manifest));
        Assert.Equal(baseRevision, manifest.BaseRevision);
        Assert.Equal(headRevision, manifest.HeadRevision);

        var capturedIdentity = new EvidencePullRequestRunIdentity(123, 123, 777, "main", 456, 1);
        var identifiedPlan = EvidenceRevisionPlanBuilder.ResolveForPullRequest(new EvidencePlanner(), policy, snapshot, capturedIdentity);
        var identifiedManifest = EvidenceManifestBuilder.Build(identifiedPlan, []);
        Assert.Equal(capturedIdentity, identifiedManifest.PullRequestRunIdentity);
        await EvidenceRevisionPlanBuilder.VerifyAsync(new EvidencePlanner(), policy, repository.Path, identifiedPlan, expectedRunIdentity: capturedIdentity);
        Assert.Equal("ASEVD139", (await Assert.ThrowsAsync<EvidencePlanningException>(() =>
            EvidenceRevisionPlanBuilder.VerifyAsync(new EvidencePlanner(), policy, repository.Path, identifiedPlan,
                expectedRunIdentity: capturedIdentity with { WorkflowRunAttempt = 2 }))).Code);
        var identityPath = Path.Join(repository.Path, "pr-run-identity.json");
        await File.WriteAllBytesAsync(identityPath, EvidenceCanonicalJson.Serialize(capturedIdentity));
        var cliIdentified = await workflow.ExplainAsync(request with { PullRequestRunIdentityFile = identityPath }, CancellationToken.None);
        Assert.Equal(identifiedPlan.PlanDigest, cliIdentified.PlanDigest);

        var output = Path.Join(repository.Path, "output");
        await workflow.WritePlanAsync(first, output, CancellationToken.None);
        await workflow.WriteManifestAsync(manifest, output, CancellationToken.None);
        var planPath = Path.Join(output, "evidence-plan.json");
        var manifestPath = Path.Join(output, "evidence-manifest.json");
        await workflow.VerifyAsync(planPath, manifestPath, CancellationToken.None, policyPath, repository.Path);
        Assert.Contains("ASEVD232", (await Assert.ThrowsAsync<EvidenceCliException>(
            () => workflow.VerifyAsync(planPath, manifestPath, CancellationToken.None))).Message, StringComparison.Ordinal);

        await File.AppendAllTextAsync(manifestPath, "\n");
        Assert.Contains("ASEVD209", (await Assert.ThrowsAsync<EvidenceCliException>(
            () => workflow.VerifyAsync(planPath, manifestPath, CancellationToken.None, policyPath, repository.Path))).Message, StringComparison.Ordinal);
        await workflow.WriteManifestAsync(manifest, output, CancellationToken.None);

        File.AppendAllText(diffPath, "tampered");
        Assert.Contains("ASEVD231", (await Assert.ThrowsAsync<EvidenceCliException>(
            () => workflow.ExplainAsync(request, CancellationToken.None))).Message, StringComparison.Ordinal);

        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(policy with { Id = "substituted" }));
        Assert.Equal("ASEVD139", (await Assert.ThrowsAsync<EvidencePlanningException>(
            () => EvidenceRevisionPlanBuilder.VerifyAsync(new EvidencePlanner(), policy with { Id = "substituted" }, repository.Path, first))).Code);
        Assert.Contains("ASEVD203", (await Assert.ThrowsAsync<EvidenceCliException>(
            () => workflow.VerifyAsync(planPath, manifestPath, CancellationToken.None, policyPath, repository.Path))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevisionBoundCli_ShouldRejectPartialAndPathOnlyGateInputs()
    {
        using var repository = new GitFixture();
        var policyPath = Path.Join(repository.Path, "policy.json");
        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(CreateGatePolicy()));
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        var partial = new EvidencePlanningRequest(policyPath, [], null, BaseRevision: new string('a', 40));
        Assert.Contains("ASEVD230", (await Assert.ThrowsAsync<EvidenceCliException>(
            () => workflow.ExplainAsync(partial, CancellationToken.None))).Message, StringComparison.Ordinal);
        var pathOnly = new EvidencePlanningRequest(policyPath, ["docs/readme.md"], null, GateMode: true);
        Assert.Contains("ASEVD230", (await Assert.ThrowsAsync<EvidenceCliException>(
            () => workflow.ExplainAsync(pathOnly, CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Contains("ASEVD233", (await Assert.ThrowsAsync<EvidenceCliException>(
            () => workflow.ExplainAsync(pathOnly with { GateMode = false, PullRequestRunIdentityFile = "identity.json" }, CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    private static EvidencePolicy CreateGatePolicy()
    {
        var all = new EvidenceProfile(
            "all", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("build", "build", "1", [], ["build/pass"], [], 60)],
            [new EvidenceObligation("build", "code", "Code requires a build.", ["build"], "build/pass")]);
        var docs = new EvidenceProfile("docs", EvidenceProfileScope.Targeted, [], [], []);
        return new EvidencePolicy("gate", "1", "all", [all, docs], [new EvidencePolicyRule("docs", "docs/**", "docs")]);
    }

    private sealed class GitFixture : IDisposable
    {
        public GitFixture()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"appsurface-evidence-git-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Run("init", "-q");
        }

        public string Path { get; }

        public void Commit(string message) => Run(
            "-c", "user.name=AppSurface Test", "-c", "user.email=appsurface@example.invalid",
            "commit", "-qm", message);

        public string Run(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = Path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
            return output;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
