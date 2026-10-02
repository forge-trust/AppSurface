using System.Diagnostics;
using System.Text.Json;
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
    public async Task RegisteredProducerNameAloneCannotBypassUnsupportedProfileBoundary()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: false, registeredProducerOnly: true);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath, fixture.PolicyPath, fixture.RepositoryPath, fixture.OutputDirectory, stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH202", stderr.ToString(), StringComparison.Ordinal);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(manifest.ProducerResults).Outcome);
    }

    [Fact]
    public async Task InvalidEvidenceBearingPlanMarksEverySelectedResultInvalid()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: false);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        await File.WriteAllBytesAsync(fixture.PlanPath, EvidenceCanonicalJson.Serialize(plan with { PlanDigest = new string('0', 64) }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath, fixture.PolicyPath, fixture.RepositoryPath, fixture.OutputDirectory, stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH104", stderr.ToString(), StringComparison.Ordinal);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceProducerOutcome.Invalid, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.Equal(EvidenceResourceOutcome.Invalid, Assert.Single(manifest.ResourceResults).Outcome);
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

    [Theory]
    [InlineData("blank-version")]
    [InlineData("long-version")]
    [InlineData("blank-digest")]
    [InlineData("long-digest")]
    [InlineData("blank-profile")]
    [InlineData("long-profile")]
    [InlineData("blank-resource")]
    [InlineData("too-many-resources")]
    [InlineData("blank-producer")]
    [InlineData("too-many-producers")]
    [InlineData("blank-obligation")]
    [InlineData("too-many-obligations")]
    public async Task PlanWithoutBoundedManifestIdentityCannotEmitAClaim(string defect)
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        var profile = plan.Profile;
        plan = defect switch
        {
            "blank-version" => plan with { ContractVersion = "" },
            "long-version" => plan with { ContractVersion = new string('v', 17) },
            "blank-digest" => plan with { PlanDigest = "" },
            "long-digest" => plan with { PlanDigest = new string('a', 129) },
            "blank-profile" => plan with { Profile = profile with { Id = "" } },
            "long-profile" => plan with { Profile = profile with { Id = new string('p', 129) } },
            "blank-resource" => plan with
            {
                Profile = profile with { Resources = [new EvidenceResourceDeclaration("", "completion", 1, [])] },
            },
            "too-many-resources" => plan with
            {
                Profile = profile with
                {
                    Resources = Enumerable.Range(0, EvidenceProfileLimits.MaximumResources + 1)
                        .Select(index => new EvidenceResourceDeclaration($"resource-{index}", "completion", 1, []))
                        .ToArray(),
                },
            },
            "blank-producer" => plan with
            {
                Profile = profile with { Producers = [new EvidenceProducerDeclaration("", "test", "1", [], [], [], 1)] },
            },
            "too-many-producers" => plan with
            {
                Profile = profile with
                {
                    Producers = Enumerable.Range(0, EvidenceProfileLimits.MaximumProducers + 1)
                        .Select(index => new EvidenceProducerDeclaration($"producer-{index}", "test", "1", [], [], [], 1))
                        .ToArray(),
                },
            },
            "blank-obligation" => plan with
            {
                Profile = profile with { Obligations = [new EvidenceObligation("", "test", "reason", [], "assertion")] },
            },
            "too-many-obligations" => plan with
            {
                Profile = profile with
                {
                    Obligations = Enumerable.Range(0, EvidenceProfileLimits.MaximumObligations + 1)
                        .Select(index => new EvidenceObligation($"obligation-{index}", "test", "reason", [], "assertion"))
                        .ToArray(),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        await File.WriteAllBytesAsync(fixture.PlanPath, EvidenceCanonicalJson.Serialize(plan));
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
        Assert.Empty(stdout.ToString());
        Assert.False(File.Exists(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
    }

    [Fact]
    public async Task SyntacticallyMalformedPlanFailsBeforeHostCreation()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        await File.WriteAllTextAsync(fixture.PlanPath, "{invalid-json");
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
        Assert.Empty(stdout.ToString());
        Assert.False(File.Exists(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missing-directory")]
    [InlineData("malformed")]
    public async Task UntrustedOrUnavailablePolicyCannotProduceACompleteClaim(string policyFailure)
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var policyPath = fixture.PolicyPath;
        switch (policyFailure)
        {
            case "missing":
                File.Delete(fixture.PolicyPath);
                break;
            case "missing-directory":
                policyPath = Path.Join(Path.GetDirectoryName(fixture.PolicyPath)!, "absent", "policy.json");
                break;
            case "malformed":
                await File.WriteAllTextAsync(fixture.PolicyPath, "{invalid-json");
                break;
        }

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            policyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains(policyFailure == "malformed" ? "ASEGH103" : "ASEGH102", stderr.ToString(), StringComparison.Ordinal);
        var manifestPath = Path.Join(fixture.OutputDirectory, "evidence-manifest.json");
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task PolicyDirectoryCannotBeReadAsAFileOrProduceACompleteClaim()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var policyDirectory = Path.Join(Path.GetDirectoryName(fixture.PolicyPath)!, "policy-directory");
        Directory.CreateDirectory(policyDirectory);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            policyDirectory,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH105", stderr.ToString(), StringComparison.Ordinal);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task InvalidTrustedPolicyPreservesItsPlanningDiagnostic()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(await File.ReadAllBytesAsync(fixture.PolicyPath));
        await File.WriteAllBytesAsync(
            fixture.PolicyPath,
            EvidenceCanonicalJson.Serialize(policy with { ConservativeProfileId = "missing-profile" }));
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
        Assert.Contains("ASEVD104", stderr.ToString(), StringComparison.Ordinal);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task InvalidReleasePlanEmitsSortedTerminalResultsWithInvalidEnvelope()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: false);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        var draft = plan with
        {
            Profile = plan.Profile with
            {
                Scope = EvidenceProfileScope.Release,
                Resources =
                [
                    new EvidenceResourceDeclaration("z-resource", "completion", 1, []),
                    new EvidenceResourceDeclaration("a-resource", "completion", 1, []),
                    .. plan.Profile.Resources,
                ],
                Producers =
                [
                    new EvidenceProducerDeclaration("z-producer", "source-check", "1.0", [], ["assertion"], [], 60),
                    new EvidenceProducerDeclaration("a-producer", "source-check", "1.0", [], ["assertion"], [], 60),
                    .. plan.Profile.Producers,
                ],
            },
            PlanDigest = string.Empty,
        };
        await File.WriteAllBytesAsync(
            fixture.PlanPath,
            EvidenceCanonicalJson.Serialize(draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) }));
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
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceEnvelopeStatus.Invalid, manifest.EnvelopeStatus);
        Assert.Equal(new[] { "a-resource", "database", "z-resource" }, manifest.ResourceResults.Select(static result => result.ResourceId));
        Assert.Equal(new[] { "a-producer", "source-check", "z-producer" }, manifest.ProducerResults.Select(static result => result.ProducerId));
    }

    [Fact]
    public async Task InvalidPlanPathIsReportedAsUnsafeInput()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath + "\0",
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH102", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString());
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Fact]
    public async Task UnexpectedOutputWriterFailureReturnsGenericHostFailure()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: false);
        using var stdout = new ThrowingTextWriter(new NotSupportedException("The caller's output writer is unavailable."));
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath,
            fixture.PolicyPath,
            fixture.RepositoryPath,
            fixture.OutputDirectory,
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH199", stderr.ToString(), StringComparison.Ordinal);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
    }

    [Fact]
    public async Task ReviewedFormattedPolicyCanProduceAnEmptyHostClaim()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        using var policyDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.PolicyPath));
        await File.WriteAllTextAsync(
            fixture.PolicyPath,
            JsonSerializer.Serialize(policyDocument.RootElement, new JsonSerializerOptions { WriteIndented = true }) + "\n");
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
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            await File.ReadAllBytesAsync(Path.Join(fixture.OutputDirectory, "evidence-manifest.json")));
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
    }

    [Fact]
    public async Task ExistingOutputFileCannotBeReplacedByAHostRun()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        Directory.CreateDirectory(fixture.OutputDirectory);
        var manifestPath = Path.Join(fixture.OutputDirectory, "evidence-manifest.json");
        await File.WriteAllTextAsync(manifestPath, "previous-output");
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
        Assert.Contains("ASEGH108", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal("previous-output", await File.ReadAllTextAsync(manifestPath));
        Assert.False(File.Exists(Path.Join(fixture.OutputDirectory, "evidence-summary.json")));
    }

    [Fact]
    public async Task OutputPathThatIsAFileFailsSafely()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        await File.WriteAllTextAsync(fixture.OutputDirectory, "output-path-file");
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
        Assert.Contains("ASEGH102", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString());
        Assert.Equal("output-path-file", await File.ReadAllTextAsync(fixture.OutputDirectory));
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
    public async Task CanonicalPlanExpansionBeyondOutputLimitFailsBeforeWritingResults()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        var expandedPlan = plan with { ChangedPaths = [new NormalizedDiffPath(new string('\u0080', 700_000))] };
        var canonicalBytes = EvidenceCanonicalJson.Serialize(expandedPlan);
        var canonicalJson = System.Text.Encoding.UTF8.GetString(canonicalBytes);
        var escapedCharacter = JsonSerializer.Serialize("\u0080")[1..^1];
        Assert.Equal("\\u0080", escapedCharacter);

        var boundedInputJson = canonicalJson.Replace(escapedCharacter, "\u0080", StringComparison.Ordinal);
        var boundedInputBytes = System.Text.Encoding.UTF8.GetBytes(boundedInputJson);
        Assert.True(boundedInputBytes.Length <= 4 * 1024 * 1024);
        Assert.True(canonicalBytes.Length > 4 * 1024 * 1024);
        await File.WriteAllBytesAsync(fixture.PlanPath, boundedInputBytes);
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
        Assert.Contains("ASEGH106", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString());
        Assert.False(File.Exists(Path.Join(fixture.OutputDirectory, "evidence-plan.json")));
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
    public async Task InvalidPlanIdentifiersAreRedactedInHostTerminalStatus()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        await File.WriteAllBytesAsync(fixture.PlanPath, EvidenceCanonicalJson.Serialize(plan with
        {
            Profile = plan.Profile with { Id = "unsafe\nprofile" },
            PlanDigest = new string('g', 64),
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await EvidenceHostRunner.ExecuteAsync(
            fixture.PlanPath, fixture.PolicyPath, fixture.RepositoryPath, fixture.OutputDirectory, stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("profile=[invalid-token]", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("plan=[invalid-digest]", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe\nprofile", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongLengthPlanDigestIsRedactedInHostTerminalStatus()
    {
        using var fixture = await GateFixture.CreateAsync(docsOnly: true);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(fixture.PlanPath));
        await File.WriteAllBytesAsync(fixture.PlanPath, EvidenceCanonicalJson.Serialize(plan with { PlanDigest = "short" }));
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
        Assert.Contains("plan=[invalid-digest]", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("plan=short", stdout.ToString(), StringComparison.Ordinal);
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

    private sealed class ThrowingTextWriter(Exception exception) : StringWriter
    {
        public override Task WriteLineAsync(string? value) => Task.FromException(exception);
    }

    private sealed class GateFixture(string root, string repositoryPath, string policyPath, string planPath, string outputDirectory) : IDisposable
    {
        public string RepositoryPath { get; } = repositoryPath;
        public string PolicyPath { get; } = policyPath;
        public string PlanPath { get; } = planPath;
        public string OutputDirectory { get; } = outputDirectory;

        public static async Task<GateFixture> CreateAsync(
            bool docsOnly,
            string emptyProfileId = "documentation-only",
            bool registeredProducerOnly = false)
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
                registeredProducerOnly ? "release-inspection" : "source-check",
                "source-check",
                "1.0",
                registeredProducerOnly ? [] : ["database"],
                ["source-assertion"],
                [],
                60);
            var resource = new EvidenceResourceDeclaration("database", "aspire_health", 60, []);
            var sourceProfile = new EvidenceProfile(
                "source",
                EvidenceProfileScope.Targeted,
                registeredProducerOnly ? [] : [resource],
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
