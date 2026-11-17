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

    // Protects obligation closure: a producer must deliver the complete versioned acceptance,
    // not infer successful patch assurance from PatchAccepted alone. This fixture is not native execution.
    [Fact]
    public async Task V3EvidenceProducerDeliversAllDimensionsBeforeClosingThePlannedObligation()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CaptureAndComposeAsync();
        var input = await fixture.ReadCanonicalDiffAsync();
        var plan = new EvidencePlanner().ResolvePatch(PlannerPolicy(), input.Patch);
        var declaration = Assert.Single(plan.Profile.Producers);
        var producer = new EvidenceCoverageFanInProducer(declaration.Id,
            fixture.AcceptRequest(plan.PatchSha256!), Assert.Single(declaration.AssertionIds));
        var root = Path.Join(fixture.Temp.Path, "delivered-evidence");
        var writer = new EvidenceArtifactWriter(declaration, root);

        var result = await producer.ProduceAsync(new(plan, declaration, TimeProvider.System, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(declaration.AssertionIds, result.SatisfiedAssertionIds);
        var artifact = Assert.Single(writer.WrittenArtifacts);
        using var delivered = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(root, artifact.RelativePath)));
        var value = delivered.RootElement;
        Assert.Equal(3, value.GetProperty("contractVersion").GetInt32());
        Assert.Equal(CoveragePolicyEncoding.InitialReportProfileId, value.GetProperty("reportProfileId").GetString());
        Assert.True(value.GetProperty("accepted").GetBoolean());
        Assert.True(value.GetProperty("verified").GetBoolean());
        foreach (var set in new[] { "primary", "composed" })
            foreach (var dimension in new[] { "line", "branch" })
            {
                var metric = value.GetProperty("patchApplicability").GetProperty(set).GetProperty(dimension);
                Assert.Equal("measured", metric.GetProperty("outcome").GetString());
                Assert.Equal("reported-points", metric.GetProperty("reason").GetString());
                Assert.Equal(100m, metric.GetProperty("percent").GetDecimal());
            }
        Assert.DoesNotContain(fixture.Repository, value.GetRawText(), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> AssuranceMetricCases()
    {
        var measured = new CoveragePatchMetricResult(CoveragePatchApplicabilityOutcome.Measured,
            "reported-points", 1, 2, 2, 100m);
        var absent = new CoveragePatchMetricResult(CoveragePatchApplicabilityOutcome.NotApplicable,
            "no-reported-patch-points", 1, 0, 0, null);
        foreach (var valid in new[] { measured, absent }) yield return [valid, true];
        foreach (var invalid in new CoveragePatchMetricResult[]
        {
            measured with { Reason = "other" }, measured with { ChangedLines = null },
            measured with { ChangedLines = -1 }, measured with { Measurable = null },
            measured with { Measurable = 0 }, measured with { Covered = null },
            measured with { Covered = -1 }, measured with { Covered = 3 },
            measured with { Percent = null }, measured with { Percent = -0.01m },
            measured with { Percent = 100.01m }, absent with { Reason = "reported-points" },
            absent with { ChangedLines = null }, absent with { ChangedLines = -1 },
            absent with { Measurable = null }, absent with { Measurable = 1 },
            absent with { Covered = null }, absent with { Covered = 1 },
            absent with { Percent = 0 }, absent with { Outcome = CoveragePatchApplicabilityOutcome.Unverified },
            measured with { Outcome = (CoveragePatchApplicabilityOutcome)42 }
        }) yield return [invalid, false];
    }

    // Each dimension can independently prevent an Evidence obligation from being closed.
    [Theory]
    [MemberData(nameof(AssuranceMetricCases))]
    public void VersionThreeAssuranceRejectsIncompleteMetricsInEveryPosition(CoveragePatchMetricResult metric, bool accepted)
    {
        var valid = new CoveragePatchMetricResult(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 2, 2, 100);
        for (var position = 0; position < 4; position++)
        {
            var dimensions = new[] { valid, valid, valid, valid };
            dimensions[position] = metric;
            var applicability = new CoveragePatchApplicability(new(dimensions[0], dimensions[1]), new(dimensions[2], dimensions[3]));
            var result = AssuranceResult(applicability);
            Assert.Equal(accepted, EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, true, true, result));
            Assert.Equal(accepted, EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, false, true, result));
        }
    }

    [Fact]
    public void VersionThreeAssuranceRefusesMissingSetsAndCrossMetricDiffDisagreement()
    {
        var valid = new CoveragePatchMetricResult(CoveragePatchApplicabilityOutcome.Measured, "reported-points", 1, 1, 1, 100);
        foreach (var applicability in new CoveragePatchApplicability?[]
        {
            null, new(null!, new(valid, valid)), new(new(valid, valid), null!),
            new(new(null!, valid), new(valid, valid)), new(new(valid, valid), new(valid, null!)),
            new(new(valid, valid), new(valid, valid with { ChangedLines = 2 }))
        })
            Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, true, true, AssuranceResult(applicability)));
        Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(1, false, false, Result("PatchNotRequested")));
        Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(4, false, false, Result("PatchNotRequested")));
        Assert.True(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(2, false, false, Result("NotEvaluated")));
        Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, false, false, Result("PatchAccepted")));
        Assert.False(EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, false, false,
            AssuranceResult(new(new(valid, valid), new(valid, valid)), "PatchNotRequested")));
        Assert.Throws<ArgumentNullException>(() => EvidenceCoverageFanInProducer.SatisfiesPatchAssurance(3, true, true, null!));
    }

    private static CoverageFanInResult AssuranceResult(CoveragePatchApplicability? applicability, string patch = "PatchAccepted") =>
        new("accept", CoverageOperationOutcome.Succeeded, "operation", "policy", new(false, false, true, false, null), [], [],
            accepted: true, verified: true, patchOutcome: patch, patchApplicability: applicability);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V3AggregateOnlyProducerRetainsNumericAcceptanceWithoutInventingPatchApplicability(bool covered)
    {
        using var fixture = await Fixture.CreateAsync(aggregateOnly: true);
        await fixture.CaptureAndComposeAsync(covered);
        var policy = PlannerPolicy();
        var declaration = policy.Profiles.Single().Producers.Single() with
        {
            CoverageGate = new EvidenceCoverageGateRequirements(95, 85, PatchLineMode: "codecov", TolerancePercent: 0.5m)
        };
        var profile = policy.Profiles.Single() with { Producers = [declaration] };
        var plan = new EvidencePlanner().Resolve(policy with { Profiles = [profile] }, [new NormalizedDiffPath("src/Example.cs")]);
        var producer = new EvidenceCoverageFanInProducer(declaration.Id,
            new(fixture.Policy, fixture.Repository, fixture.PrimaryCapture, [], fixture.Composed, fixture.Accepted),
            declaration.AssertionIds.Single(), requirePatch: false);
        var root = Path.Join(fixture.Temp.Path, "aggregate-evidence");
        var writer = new EvidenceArtifactWriter(declaration, root);

        var result = await producer.ProduceAsync(new(plan, declaration, TimeProvider.System, writer), CancellationToken.None);

        Assert.Equal(covered ? EvidenceProducerOutcome.Passed : EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Equal(covered ? 1 : 0, result.SatisfiedAssertionIds.Count);
        var artifact = Assert.Single(writer.WrittenArtifacts);
        using var delivered = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(root, artifact.RelativePath)));
        Assert.Equal(JsonValueKind.Null, delivered.RootElement.GetProperty("patchApplicability").ValueKind);
        Assert.Equal(covered, delivered.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(covered, File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
        Assert.Equal(!covered, File.Exists(Path.Join(fixture.Accepted, "rejection.json")));
    }

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

        internal static async Task<Fixture> CreateAsync(bool aggregateOnly = false)
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
                ["checks/Verification.csproj"], [], aggregateOnly
                    ? new(CoverageGateMode.AggregateOnly)
                    : new(CoverageGateMode.AggregateAndPatch, DiffBase: baseCommit))
            {
                ContractVersion = 3,
                ReportProfileId = CoveragePolicyEncoding.InitialReportProfileId
            };
            return fixture;
        }

        internal async Task CaptureAndComposeAsync(bool covered = true)
        {
            Temp.WriteFile("producer/primary.xml",
                "<coverage lines-covered=\"2\" lines-valid=\"2\" branches-covered=\"4\" branches-valid=\"4\" line-rate=\"1\" branch-rate=\"1\"><packages><package name=\"fixture\"><classes><class name=\"Aggregate\" filename=\"src/Aggregate.cs\"><lines><line number=\"1\" hits=\"1\" branch=\"true\" condition-coverage=\"100% (2/2)\"/></lines></class><class name=\"Example\" filename=\"src/Example.cs\"><lines><line number=\"1\" hits=\"1\" branch=\"true\" condition-coverage=\"100% (2/2)\"/></lines></class></classes></package></packages></coverage>");
            if (!covered)
                File.WriteAllText(PrimaryReport, File.ReadAllText(PrimaryReport)
                    .Replace("lines-covered=\"2\"", "lines-covered=\"0\"", StringComparison.Ordinal)
                    .Replace("branches-covered=\"4\"", "branches-covered=\"0\"", StringComparison.Ordinal)
                    .Replace("hits=\"1\"", "hits=\"0\"", StringComparison.Ordinal)
                    .Replace("100% (2/2)", "0% (0/2)", StringComparison.Ordinal));
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
