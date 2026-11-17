using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInConsumerIntegrationTests
{
    [Fact]
    public async Task PlannedCanonicalPatchHashAcceptsAndVerifiesAndDifferentBytesForSamePathAreRefused()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CaptureAndComposeAsync();

        var input = await fixture.ReadCanonicalDiffAsync();
        var planner = new EvidencePlanner();
        var plan = planner.ResolvePatch(PlannerPolicy(), input.Patch);
        Assert.Equal(Hash(input.Patch), plan.PatchSha256);
        Assert.Contains(plan.ChangedPaths, path => path.Path == "src/Example.cs");

        var acceptanceRequest = fixture.AcceptRequest(plan.PatchSha256!);
        Assert.Equal(plan.PatchSha256, acceptanceRequest.ExpectedPatchSha256);
        var accepted = await CoverageFanIn.AcceptAsync(acceptanceRequest);

        Assert.Equal(CoverageOperationOutcome.Succeeded, accepted.Outcome);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Verified);
        Assert.Equal("PatchAccepted", accepted.PatchOutcome);
        Assert.NotNull(accepted.PatchApplicability);
        Assert.True(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, requirePatch: true,
            patchRequested: true, accepted));

        var withoutApplicability = new CoverageFanInResult(accepted.Operation, accepted.Outcome,
            accepted.OperationId, accepted.PolicyDigest, accepted.Effects, accepted.Artifacts, accepted.Diagnostics,
            accepted.Accepted, accepted.Verified, accepted.PatchOutcome, accepted.Observation,
            accepted.EvaluationIntent, accepted.PrimaryImported);
        Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, requirePatch: true,
            patchRequested: true, withoutApplicability));

        var verified = await CoverageFanIn.VerifyAsync(fixture.VerifyRequest());
        Assert.Equal(CoverageOperationOutcome.Succeeded, verified.Outcome);
        Assert.True(verified.Verified);
        Assert.Equal(accepted.PolicyDigest, verified.PolicyDigest);
        Assert.True(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, requirePatch: true,
            patchRequested: true, verified));

        var differentBytes = input.Patch.Concat([(byte)'\n']).ToArray();
        var samePathDifferentPatchPlan = planner.ResolvePatch(PlannerPolicy(), differentBytes);
        Assert.Equal(plan.ChangedPaths, samePathDifferentPatchPlan.ChangedPaths);
        Assert.NotEqual(plan.PatchSha256, samePathDifferentPatchPlan.PatchSha256);

        var refused = await CoverageFanIn.AcceptAsync(acceptanceRequest with
        {
            OutputDirectory = fixture.DifferentBytesAccepted,
            ExpectedPatchSha256 = samePathDifferentPatchPlan.PatchSha256
        });
        Assert.NotEqual(CoverageOperationOutcome.Succeeded, refused.Outcome);
        Assert.False(refused.Accepted);
        Assert.NotEqual("PatchAccepted", refused.PatchOutcome);
        Assert.False(File.Exists(Path.Join(fixture.DifferentBytesAccepted, "acceptance.json")));
    }

    [Fact]
    public void VersionedEvidenceAssurancePreservesV2AndAllowsOnlyExplicitV3AggregateOnly()
    {
        var legacyPatchResult = Result("PatchAccepted");
        Assert.True(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(2, requirePatch: true,
            patchRequested: true, legacyPatchResult));
        Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(2, requirePatch: true,
            patchRequested: false, Result("PatchNotRequested")));

        var aggregateOnly = Result("PatchNotRequested");
        Assert.True(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, requirePatch: false,
            patchRequested: false, aggregateOnly));
        Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, requirePatch: true,
            patchRequested: false, aggregateOnly));
        Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(2, requirePatch: true,
            patchRequested: true, Result("PatchNotRequested")));
    }

    private static CoverageFanInResult Result(string patchOutcome) => new("accept", CoverageOperationOutcome.Succeeded,
        "operation", "policy", new(false, false, true, false, null), [], [], accepted: true, verified: true,
        patchOutcome: patchOutcome);

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static EvidencePolicy PlannerPolicy()
    {
        const string producerId = "coverage";
        const string assertionId = "coverage/patch@1";
        var producer = new EvidenceProducerDeclaration(producerId, "coverage", "1.0.0", [], [assertionId],
            [new EvidenceArtifactSlot("result", "coverage", "application/json", true, 1024 * 1024)], 300,
            new EvidenceCoverageGateRequirements(95, 85, 95, 85, "codecov", 0.5m));
        var profile = new EvidenceProfile("coverage", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("patch", "behavior", "Verify the changed source patch.", [producerId], assertionId)]);
        return new("consumer-integration", "1", "coverage", [profile],
            [new EvidencePolicyRule("source", "src/**", "coverage")]);
    }

    private sealed class Fixture : IDisposable
    {
        private const string DotnetPath = "dotnet";
        private readonly string? _priorArtifactHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");

        private Fixture()
        {
            Temp = TestDirectory.Create();
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(Temp.Path, "store"));
        }

        internal TestDirectory Temp { get; }
        internal string Repository => Path.Join(Temp.Path, "repo");
        internal string PrimaryCapture => Path.Join(Temp.Path, "primary-capture");
        internal string Composed => Path.Join(Temp.Path, "composed");
        internal string Accepted => Path.Join(Temp.Path, "accepted");
        internal string DifferentBytesAccepted => Path.Join(Temp.Path, "accepted-different-bytes");
        internal string PrimaryReport => Path.Join(Temp.Path, "producer", "primary.xml");
        internal string Timings => Path.Join(Temp.Path, "producer", "timings.json");
        internal CoverageFanInPolicy Policy { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Temp.WriteFile("repo/checks/Verification.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            fixture.Temp.WriteFile("repo/src/Aggregate.cs", "public sealed class Aggregate { }\n");
            fixture.Temp.WriteFile("repo/src/Example.cs", "namespace Fixture; public sealed class Example { public int Value => 1; }\n");
            await fixture.GitAsync("init");
            await fixture.GitAsync("config", "user.email", "coverage-consumer@example.invalid");
            await fixture.GitAsync("config", "user.name", "Coverage consumer integration");
            await fixture.GitAsync("add", ".");
            await fixture.GitAsync("commit", "-m", "coverage base");
            var baseCommit = await fixture.GitAsync("rev-parse", "HEAD");
            fixture.Temp.WriteFile("repo/src/Example.cs", "namespace Fixture; public sealed class Example { public int Value => 2; }\n");
            await fixture.GitAsync("add", ".");
            await fixture.GitAsync("commit", "-m", "coverage source change");
            var commit = await fixture.GitAsync("rev-parse", "HEAD");
            var tree = await fixture.GitAsync("rev-parse", "HEAD^{tree}");
            var sdk = await fixture.CommandAsync(DotnetPath, "--version");
            fixture.Policy = new(new(commit, tree, sdk, "Release"),
                new("github-actions", "coverage-consumer-integration", Guid.NewGuid().ToString("N"), 1),
                ["checks/Verification.csproj"], [], new(CoverageGateMode.AggregateAndPatch, DiffBase: baseCommit))
            {
                ContractVersion = 3,
                ReportProfileId = CoveragePolicyEncoding.InitialReportProfileId
            };
            return fixture;
        }

        internal async Task CaptureAndComposeAsync()
        {
            Temp.WriteFile("producer/primary.xml",
                "<coverage lines-covered=\"2\" lines-valid=\"2\" branches-covered=\"4\" branches-valid=\"4\" line-rate=\"1\" branch-rate=\"1\"><packages><package name=\"fixture\"><classes><class name=\"Aggregate\" filename=\"src/Aggregate.cs\"><lines><line number=\"1\" hits=\"1\" branch=\"true\" condition-coverage=\"100% (2/2)\"/></lines></class><class name=\"Example\" filename=\"src/Example.cs\"><lines><line number=\"1\" hits=\"1\" branch=\"true\" condition-coverage=\"100% (2/2)\"/></lines></class></classes></package></packages></coverage>");
            Temp.WriteFile("producer/timings.json", JsonSerializer.Serialize(new
            {
                configuration = "Release",
                coverageDriver = "collector",
                merge = new { exitCode = 0 },
                artifacts = new { coverageFiles = 1 },
                projects = new[] { new { project = "checks/Verification.csproj", exitCode = 0,
                    executionStatus = "completed", coverageArtifactStatus = "produced" } }
            }));
            var captured = await CoverageFanIn.CaptureAsync(new(Policy, Repository, PrimaryCapture, "primary",
                PrimaryReport, Timings));
            Assert.True(captured.Outcome == CoverageOperationOutcome.Succeeded, JsonSerializer.Serialize(captured));
            var composed = await CoverageFanIn.ComposeAsync(new(Policy, Repository, PrimaryCapture, [], Composed));
            Assert.True(composed.Outcome == CoverageOperationOutcome.Succeeded, JsonSerializer.Serialize(composed));
        }

        internal async Task<CoverageFanInDiffInput> ReadCanonicalDiffAsync()
        {
            var frozen = CoveragePolicyEncoding.Freeze(Policy);
            var expected = new CoverageFanInExpected(CoveragePolicyEncoding.ToElement(frozen),
                CoveragePolicyEncoding.Digest(frozen));
            var context = new CoverageFanInDiffContext(
                new CoverageFanInEngineRequest(expected, Repository),
                new CoverageFanInBudget(frozen.Limits!.TotalBytes));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await context.InitializeAsync(deadline.Token);
            return await context.ReadAsync(deadline.Token);
        }

        internal CoverageAcceptRequest AcceptRequest(string patchHash) => new(Policy, Repository, PrimaryCapture,
            [], Composed, Accepted)
        { ExpectedPatchSha256 = patchHash };

        internal CoverageVerifyRequest VerifyRequest() => new(Policy, Repository, PrimaryCapture, [], Composed, Accepted);

        private Task<string> GitAsync(params string[] args) => CommandAsync("git", args);

        private async Task<string> CommandAsync(string executable, params string[] args)
        {
            using var process = new Process
            {
                StartInfo = new(executable)
                {
                    WorkingDirectory = Repository,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error);
            return (await output).Trim();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _priorArtifactHome);
            Temp.Dispose();
        }
    }
}
