using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.EvidenceGate;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

public sealed class EvidenceGateVerifierTests
{
    [Fact]
    public async Task VerifyGateAcceptsTrustedEmptyProfileAndWritesCanonicalMachineResults()
    {
        using var fixture = await VerifyFixture.CreateAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceGateVerifier.ExecuteAsync(
            fixture.PlanPath,
            fixture.ManifestPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.IdentityPath,
            fixture.OutputDirectory,
            artifactHandoffRootPath: null,
            fixture.AuthorityProvider,
            artifactVerifier: null,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        Assert.Contains("gate=eligible; code=ASEVG000", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("claim=NoEvidenceRequired", stdout.ToString(), StringComparison.Ordinal);
        var resultBytes = await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-gate-verification.json"));
        var summaryBytes = await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-gate-summary.json"));
        var markdown = await File.ReadAllTextAsync(Path.Join(fixture.OutputDirectory, "evidence-gate-summary.md"));
        var result = EvidenceCanonicalJson.Deserialize<EvidencePullRequestGateVerificationResult>(resultBytes);
        Assert.True(result.IsEligible);
        Assert.Equal("ASEVG000", result.Code);
        Assert.Equal(resultBytes, EvidenceCanonicalJson.Serialize(result));
        Assert.Equal(summaryBytes, EvidenceCanonicalJson.Serialize(EvidenceCanonicalJson.Deserialize<System.Text.Json.JsonElement>(summaryBytes)));
        Assert.Equal(EvidenceGateResultRenderer.RenderMarkdown(result), markdown);
        Assert.Contains("Verdict: **eligible**", markdown, StringComparison.Ordinal);
        Assert.Contains("Verified claim: `NoEvidenceRequired`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyGateReturnsFixedIneligibleResultWhenAuthorityIsUnavailable()
    {
        using var fixture = await VerifyFixture.CreateAsync(authorityUnavailable: true);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceGateVerifier.ExecuteAsync(
            fixture.PlanPath,
            fixture.ManifestPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.IdentityPath,
            fixture.OutputDirectory,
            artifactHandoffRootPath: null,
            fixture.AuthorityProvider,
            artifactVerifier: null,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEVG006", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("ASEVG006", stderr.ToString(), StringComparison.Ordinal);
        var result = EvidenceCanonicalJson.Deserialize<EvidencePullRequestGateVerificationResult>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-gate-verification.json")));
        Assert.False(result.IsEligible);
        Assert.Equal("ASEVG006", result.Code);
    }

    [Fact]
    public async Task VerifyGateRejectsMalformedOrOversizedHandoffWithMachineFailureResult()
    {
        using var malformedFixture = await VerifyFixture.CreateAsync();
        await File.WriteAllTextAsync(malformedFixture.PlanPath, "not-json");
        using var malformedStdout = new StringWriter();
        using var malformedStderr = new StringWriter();

        var malformedExitCode = await EvidenceGateVerifier.ExecuteAsync(
            malformedFixture.PlanPath,
            malformedFixture.ManifestPath,
            malformedFixture.PolicyPath,
            malformedFixture.RepositoryPath,
            malformedFixture.IdentityPath,
            malformedFixture.OutputDirectory,
            null,
            malformedFixture.AuthorityProvider,
            null,
            malformedStdout,
            malformedStderr);

        Assert.Equal(2, malformedExitCode);
        Assert.Contains("ASEGG103", malformedStdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("ASEGG103", EvidenceCanonicalJson.Deserialize<EvidencePullRequestGateVerificationResult>(
            await File.ReadAllBytesAsync(Path.Join(malformedFixture.OutputDirectory, "evidence-gate-verification.json"))).Code);

        using var oversizedFixture = await VerifyFixture.CreateAsync();
        await File.WriteAllBytesAsync(oversizedFixture.ManifestPath, new byte[EvidencePullRequestGateVerifier.MaximumContractBytes + 1]);
        using var oversizedStdout = new StringWriter();
        using var oversizedStderr = new StringWriter();

        var oversizedExitCode = await EvidenceGateVerifier.ExecuteAsync(
            oversizedFixture.PlanPath,
            oversizedFixture.ManifestPath,
            oversizedFixture.PolicyPath,
            oversizedFixture.RepositoryPath,
            oversizedFixture.IdentityPath,
            oversizedFixture.OutputDirectory,
            null,
            oversizedFixture.AuthorityProvider,
            null,
            oversizedStdout,
            oversizedStderr);

        Assert.Equal(2, oversizedExitCode);
        Assert.Contains("ASEGG107", oversizedStdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("ASEGG107", EvidenceCanonicalJson.Deserialize<EvidencePullRequestGateVerificationResult>(
            await File.ReadAllBytesAsync(Path.Join(oversizedFixture.OutputDirectory, "evidence-gate-verification.json"))).Code);
    }

    [Fact]
    public async Task VerifyGateRejectsCanonicalTamperedPlanWithNonzeroResult()
    {
        using var fixture = await VerifyFixture.CreateAsync();
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        await File.WriteAllBytesAsync(fixture.PlanPath, EvidenceCanonicalJson.Serialize(plan with { PlanDigest = new string('0', 64) }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceGateVerifier.ExecuteAsync(
            fixture.PlanPath,
            fixture.ManifestPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.IdentityPath,
            fixture.OutputDirectory,
            null,
            fixture.AuthorityProvider,
            null,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEVG003", stdout.ToString(), StringComparison.Ordinal);
        Assert.False(EvidenceCanonicalJson.Deserialize<EvidencePullRequestGateVerificationResult>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-gate-verification.json"))).IsEligible);
    }

    [Fact]
    public async Task VerifyGateDoesNotReplaceAnExistingMarkdownSummary()
    {
        using var fixture = await VerifyFixture.CreateAsync();
        Directory.CreateDirectory(fixture.OutputDirectory);
        var markdownPath = Path.Join(fixture.OutputDirectory, "evidence-gate-summary.md");
        await File.WriteAllTextAsync(markdownPath, "existing trusted output");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceGateVerifier.ExecuteAsync(
            fixture.PlanPath,
            fixture.ManifestPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.IdentityPath,
            fixture.OutputDirectory,
            null,
            fixture.AuthorityProvider,
            null,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGG108", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("existing trusted output", await File.ReadAllTextAsync(markdownPath));
        Assert.False(File.Exists(Path.Join(fixture.OutputDirectory, "evidence-gate-verification.json")));
    }

    private sealed class VerifyFixture(string root, string repositoryPath, string planPath, string manifestPath, string policyPath, string identityPath, string outputDirectory, IEvidencePullRequestGateAuthorityProvider authorityProvider) : IDisposable
    {
        public string RepositoryPath { get; } = repositoryPath;
        public string PlanPath { get; } = planPath;
        public string ManifestPath { get; } = manifestPath;
        public string PolicyPath { get; } = policyPath;
        public string IdentityPath { get; } = identityPath;
        public string OutputDirectory { get; } = outputDirectory;
        public IEvidencePullRequestGateAuthorityProvider AuthorityProvider { get; } = authorityProvider;

        public static async Task<VerifyFixture> CreateAsync(
            EvidencePullRequestGateAuthoritySnapshot? authority = null,
            bool authorityUnavailable = false)
        {
            var root = Path.Join(Path.GetTempPath(), "appsurface-verifygate-" + Guid.NewGuid().ToString("N"));
            var repository = Path.Join(root, "repository");
            Directory.CreateDirectory(repository);
            await GitAsync(repository, "init", "--quiet");
            await GitAsync(repository, "config", "user.email", "verifygate-tests@example.invalid");
            await GitAsync(repository, "config", "user.name", "VerifyGate Tests");
            await File.WriteAllTextAsync(Path.Join(repository, "base.txt"), "base\n");
            await GitAsync(repository, "add", "base.txt");
            await GitAsync(repository, "commit", "--quiet", "-m", "base");
            var baseRevision = (await GitAsync(repository, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Join(repository, "docs.md"), "trusted docs change\n");
            await GitAsync(repository, "add", "docs.md");
            await GitAsync(repository, "commit", "--quiet", "-m", "docs");
            var headRevision = (await GitAsync(repository, "rev-parse", "HEAD")).Trim();

            var profile = new EvidenceProfile("documentation-only", EvidenceProfileScope.Targeted, [], [], []);
            var conservativeProducer = new EvidenceProducerDeclaration(
                "conservative-review",
                "manual_review",
                "1",
                [],
                ["review-complete"],
                [],
                60);
            var conservativeProfile = new EvidenceProfile(
                "conservative",
                EvidenceProfileScope.Targeted,
                [],
                [conservativeProducer],
                [new EvidenceObligation(
                    "conservative-obligation",
                    "conservative",
                    "Review changes without a more specific rule.",
                    [conservativeProducer.Id],
                    "review-complete")]);
            var policy = new EvidencePolicy(
                "verify-gate-tests",
                "1",
                conservativeProfile.Id,
                [profile, conservativeProfile],
                [new EvidencePolicyRule("docs", "docs.md", profile.Id, 10)]);
            var runIdentity = new EvidencePullRequestRunIdentity(321, 321, 777, "main", 654321, 1);
            var expectedIdentity = new EvidencePullRequestGateExpectedIdentity(
                "forge-trust/AppSurface",
                "pull_request_target",
                "evidence-gate.yml",
                "evidence-subject",
                runIdentity);
            var snapshot = await EvidenceGitChangeCapture.CaptureAsync(repository, baseRevision, headRevision);
            var plan = EvidenceRevisionPlanBuilder.ResolveForPullRequest(new EvidencePlanner(), policy, snapshot, runIdentity);
            var manifest = EvidenceManifestBuilder.Build(plan, []);
            if (!authorityUnavailable && authority is null)
            {
                authority = new EvidencePullRequestGateAuthoritySnapshot(
                    expectedIdentity.Repository,
                    expectedIdentity.EventName,
                    baseRevision,
                    headRevision,
                    expectedIdentity.WorkflowId,
                    runIdentity,
                    expectedIdentity.SubjectJobId,
                    headRevision,
                    "success");
            }

            var planPath = Path.Join(root, "plan.json");
            var manifestPath = Path.Join(root, "manifest.json");
            var policyPath = Path.Join(root, "policy.json");
            var identityPath = Path.Join(root, "identity.json");
            await File.WriteAllBytesAsync(planPath, EvidenceCanonicalJson.Serialize(plan));
            await File.WriteAllBytesAsync(manifestPath, EvidenceCanonicalJson.Serialize(manifest));
            await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(policy));
            await File.WriteAllBytesAsync(identityPath, EvidenceCanonicalJson.Serialize(expectedIdentity));

            return new VerifyFixture(
                root,
                repository,
                planPath,
                manifestPath,
                policyPath,
                identityPath,
                Path.Join(root, "output"),
                new SnapshotAuthorityProvider(authority));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static async Task<string> GitAsync(string repository, params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Git test process could not start.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git test setup failed: {stderr}");
            }

            return stdout;
        }
    }

    private sealed class SnapshotAuthorityProvider(EvidencePullRequestGateAuthoritySnapshot? snapshot) : IEvidencePullRequestGateAuthorityProvider
    {
        public Task<EvidencePullRequestGateAuthoritySnapshot?> ReadFreshAsync(
            EvidencePullRequestGateExpectedIdentity expectedIdentity,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
