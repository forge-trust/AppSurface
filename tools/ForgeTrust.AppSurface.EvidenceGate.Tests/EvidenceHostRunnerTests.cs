using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.EvidenceGate;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

public sealed class EvidenceHostRunnerTests
{
    [Fact]
    public async Task DocumentationPolicyProfileWritesCanonicalNoEvidenceManifestAndSucceeds()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        Assert.Contains("host=completed", stdout.ToString(), StringComparison.Ordinal);
        var planBytes = await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-plan.json"));
        var manifestBytes = await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json"));
        var summaryBytes = await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-summary.json"));
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(planBytes);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(manifestBytes);

        Assert.Equal("2.0", plan.ContractVersion);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.Equal(planBytes, EvidenceCanonicalJson.Serialize(plan));
        Assert.Equal(manifestBytes, EvidenceCanonicalJson.Serialize(manifest));
        Assert.Equal(summaryBytes, EvidenceCanonicalJson.Serialize(EvidenceCanonicalJson.Deserialize<System.Text.Json.JsonElement>(summaryBytes)));
    }

    [Fact]
    public async Task ArbitrarilyNamedEmptyProfileSucceedsWhenTrustedPolicyExplicitlySelectsIt()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true, emptyProfileId: "approved-empty-profile");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());
        Assert.Contains("profile=approved-empty-profile", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedProducerAndResourceProduceVerifiedIncompleteManifestAndFail()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: false);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH201", stderr.ToString(), StringComparison.Ordinal);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-plan.json")));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Contains(manifest.ProducerResults, result => result.Outcome == EvidenceProducerOutcome.Unavailable);
        Assert.Contains(manifest.ResourceResults, result => result.Outcome == EvidenceResourceOutcome.Unavailable);
        Assert.NotEmpty(manifest.UnmediatedObligationIds);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
    }

    [Fact]
    public async Task MalformedPlanWithoutBoundedIdentityDoesNotEmitManifest()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        await File.WriteAllTextAsync(fixture.PlanPath, "{\"not\":\"a plan\"}");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH103", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
    }

    [Fact]
    public async Task ParseableNonCanonicalPlanEmitsInvalidManifestAndFails()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var planBytes = await File.ReadAllBytesAsync(fixture.PlanPath);
        await File.WriteAllBytesAsync(fixture.PlanPath, [.. planBytes, (byte)' ']);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH103", stderr.ToString(), StringComparison.Ordinal);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task OversizedPlanFailsBeforeDeserialization()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        await File.WriteAllBytesAsync(fixture.PlanPath, new byte[4 * 1024 * 1024 + 1]);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH107", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
    }

    [Fact]
    public async Task TamperedPlanDigestEmitsInvalidManifestAndFailsAgainstTrustedGitSnapshot()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        await File.WriteAllBytesAsync(fixture.PlanPath, EvidenceCanonicalJson.Serialize(plan with { PlanDigest = new string('0', 64) }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH104", stderr.ToString(), StringComparison.Ordinal);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task UnselectedEmptyPolicyProfileCannotBeUsedAsAPlanBypass()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        var unselectedProfile = plan.PolicySnapshot!.Profiles.Single(static profile => profile.Id == "unselected-empty");
        var draft = plan with { Profile = unselectedProfile, PlanDigest = string.Empty };
        await File.WriteAllBytesAsync(fixture.PlanPath, EvidenceCanonicalJson.Serialize(draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH104", stderr.ToString(), StringComparison.Ordinal);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task CancellationBeforeExecutionReturnsNonzeroWithoutOutput()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr,
            cancellation.Token);

        Assert.Equal(130, exitCode);
        Assert.Contains("ASEGH130", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
    }

    private sealed class GateFixture(string root, string repositoryPath, string policyPath, string planPath, string outputDirectory) : IDisposable
    {
        public string RepositoryPath { get; } = repositoryPath;
        public string PolicyPath { get; } = policyPath;
        public string PlanPath { get; } = planPath;
        public string OutputDirectory { get; } = outputDirectory;

        public static async Task<GateFixture> CreateAsync(bool docsOnly, string emptyProfileId = "documentation-only")
        {
            var root = Path.Join(Path.GetTempPath(), "appsurface-evidencegate-" + Guid.NewGuid().ToString("N"));
            var repository = Path.Join(root, "repository");
            Directory.CreateDirectory(repository);
            await GitAsync(repository, "init", "--quiet");
            await GitAsync(repository, "config", "user.email", "evidencegate-tests@example.invalid");
            await GitAsync(repository, "config", "user.name", "EvidenceGate Tests");
            await File.WriteAllTextAsync(Path.Join(repository, "base.txt"), "base\n");
            await GitAsync(repository, "add", "base.txt");
            await GitAsync(repository, "commit", "--quiet", "-m", "base");
            var baseRevision = (await GitAsync(repository, "rev-parse", "HEAD")).Trim();

            await File.WriteAllTextAsync(Path.Join(repository, "docs.md"), "docs\n");
            if (!docsOnly)
            {
                await File.WriteAllTextAsync(Path.Join(repository, "source.cs"), "class Subject {}\n");
            }

            var changedFiles = docsOnly ? new[] { "docs.md" } : new[] { "source.cs" };
            if (docsOnly)
            {
                await File.WriteAllTextAsync(Path.Join(repository, "docs.md"), "docs\n");
            }
            else
            {
                await File.WriteAllTextAsync(Path.Join(repository, "source.cs"), "class Subject {}\n");
            }
            var addArguments = new[] { "add", "--" }.Concat(changedFiles).ToArray();
            await GitAsync(repository, addArguments);
            await GitAsync(repository, "commit", "--quiet", "-m", "subject");
            var headRevision = (await GitAsync(repository, "rev-parse", "HEAD")).Trim();

            var emptyProfile = new EvidenceProfile(emptyProfileId, EvidenceProfileScope.Targeted, [], [], []);
            var unselectedEmptyProfile = new EvidenceProfile("unselected-empty", EvidenceProfileScope.Targeted, [], [], []);
            var producer = new EvidenceProducerDeclaration(
                "source-check",
                "source-check",
                "1.0",
                ["database"],
                ["source-assertion"],
                [],
                60);
            var resource = new EvidenceResourceDeclaration("database", "aspire_health", 60, []);
            var sourceProfile = new EvidenceProfile(
                "source",
                EvidenceProfileScope.Targeted,
                [resource],
                [producer],
                [new EvidenceObligation("source-obligation", "source", "Run source checks", [producer.Id], "source-assertion")]);
            var policy = new EvidencePolicy(
                "appsurface-pr-evidence",
                "1",
                sourceProfile.Id,
                [emptyProfile, sourceProfile, unselectedEmptyProfile],
                [
                    new EvidencePolicyRule("docs", "docs.md", emptyProfile.Id, 10),
                    new EvidencePolicyRule("source", "source.cs", sourceProfile.Id, 10),
                ]);
            var changed = await EvidenceGitChangeCapture.CaptureAsync(repository, baseRevision, headRevision);
            var basePlan = new EvidencePlanner().ResolveForGate(policy, changed.ChangedPaths);
            var draft = basePlan with
            {
                ContractVersion = "2.0",
                BaseRevision = changed.BaseRevision,
                HeadRevision = changed.HeadRevision,
                SourceDiffDigest = changed.SourceDiffDigest,
                NameStatusDigest = changed.NameStatusDigest,
                PullRequestRunIdentity = new EvidencePullRequestRunIdentity(321, 321, 777, "main", 654321, 1),
                PlanDigest = string.Empty,
            };
            var plan = draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
            var policyPath = Path.Join(root, "policy.json");
            var planPath = Path.Join(root, "plan.json");
            var outputDirectory = Path.Join(root, "output");
            await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(policy));
            await File.WriteAllBytesAsync(planPath, EvidenceCanonicalJson.Serialize(plan));
            return new GateFixture(root, repository, policyPath, planPath, outputDirectory);
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
            var start = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true };
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
}
