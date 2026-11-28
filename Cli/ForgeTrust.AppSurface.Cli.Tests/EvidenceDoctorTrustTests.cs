using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class EvidenceDoctorTrustTests
{
    private const string EnvironmentCanary = "doctor-protected-canary-779";

    [Theory]
    [InlineData(EvidenceProfileScope.Targeted, null)]
    [InlineData(EvidenceProfileScope.Targeted, "true")]
    [InlineData(EvidenceProfileScope.Targeted, "false")]
    [InlineData(EvidenceProfileScope.Release, null)]
    [InlineData(EvidenceProfileScope.Release, "true")]
    [InlineData(EvidenceProfileScope.Release, "false")]
    [InlineData(EvidenceProfileScope.Release, EnvironmentCanary)]
    public async Task Doctor_ShouldReportUnverifiedEnvelopeAndPreservePolicyDirectory(
        EvidenceProfileScope scope,
        string? githubActions)
    {
        using var environment = new EnvironmentVariableScope("GITHUB_ACTIONS", githubActions);
        using var directory = TestDirectory.Create();
        var policyPath = TestPathUtils.PathUnder(directory.Path, "evidence.policy.json");
        var profile = new EvidenceProfile(
            "contract",
            scope,
            [],
            [new EvidenceProducerDeclaration("contract", "contract", "1", [], ["contract/assertion@1"], [], 1)],
            [new EvidenceObligation("contract", "contract", "Contract behavior changed.", ["contract"], "contract/assertion@1")]);
        var policy = new EvidencePolicy("doctor-policy", "1", "contract", [profile], []);
        var bytes = EvidenceCanonicalJson.Serialize(policy);
        await File.WriteAllBytesAsync(policyPath, bytes);
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        var report = await workflow.DoctorAsync(
            new EvidencePlanningRequest(policyPath, ["src/Contract.cs"], null),
            CancellationToken.None);

        var envelope = Assert.Single(report.Checks, check => check.Id == "trusted-envelope");
        Assert.Equal("unverified", envelope.Status);
        Assert.Contains("grant no admission", envelope.Message, StringComparison.Ordinal);
        Assert.StartsWith("For release evidence, use the registered verifier", envelope.NextAction, StringComparison.Ordinal);
        Assert.Contains(report.Checks, check => check is { Id: "policy", Status: "ready" });
        Assert.Contains(report.Checks, check => check is { Id: "diff", Status: "ready" });
        Assert.All(report.Checks, check =>
        {
            Assert.DoesNotContain(EnvironmentCanary, check.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(EnvironmentCanary, check.NextAction ?? string.Empty, StringComparison.Ordinal);
        });
        Assert.Equal([policyPath], Directory.GetFiles(directory.Path));
        Assert.Empty(Directory.GetDirectories(directory.Path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(policyPath));
    }

    [Fact]
    public async Task Doctor_ShouldReportMissingBrowserRuntimeForAReleaseProfile()
    {
        using var browserPath = new EnvironmentVariableScope("PLAYWRIGHT_BROWSERS_PATH", null);
        using var directory = TestDirectory.Create();
        var policyPath = TestPathUtils.PathUnder(directory.Path, "evidence.policy.json");
        var profile = new EvidenceProfile(
            "release",
            EvidenceProfileScope.Release,
            [],
            [
                new EvidenceProducerDeclaration("database", "postgres-integration", "1", [], ["database/assertion@1"], [], 60),
                new EvidenceProducerDeclaration("browser", "playwright-browser", "1", [], ["browser/assertion@1"], [], 60),
            ],
            [
                new EvidenceObligation("database", "database", "Database behavior changed.", ["database"], "database/assertion@1"),
                new EvidenceObligation("browser", "browser", "Browser behavior changed.", ["browser"], "browser/assertion@1"),
            ]);
        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(new EvidencePolicy("release-policy", "1", "release", [profile], [])));
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        var report = await workflow.DoctorAsync(new EvidencePlanningRequest(policyPath, ["src/Feature.cs"], null), CancellationToken.None);

        Assert.Contains(report.Checks, check => check.Id == "docker:database");
        Assert.Contains(report.Checks, check => check.Id == "trusted-envelope");
        var browser = Assert.Single(report.Checks, check => check.Id == "browser:browser");
        Assert.Equal("external-prerequisite", browser.Status);
        Assert.Contains("Install the consumer browser runtime", browser.NextAction, StringComparison.Ordinal);
    }
}
