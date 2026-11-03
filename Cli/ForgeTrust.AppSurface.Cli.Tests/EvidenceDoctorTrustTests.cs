using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class EvidenceDoctorTrustTests
{
    [Theory]
    [InlineData(EvidenceProfileScope.Targeted, null)]
    [InlineData(EvidenceProfileScope.Targeted, "true")]
    [InlineData(EvidenceProfileScope.Targeted, "false")]
    [InlineData(EvidenceProfileScope.Release, null)]
    [InlineData(EvidenceProfileScope.Release, "true")]
    [InlineData(EvidenceProfileScope.Release, "false")]
    public async Task Doctor_ShouldReportProtectedFactsUnverifiedWithoutExecutingSubject(
        EvidenceProfileScope scope,
        string? githubActions)
    {
        using var environment = new EnvironmentVariableScope("GITHUB_ACTIONS", githubActions);
        using var directory = TestDirectory.Create();
        var policyPath = Path.Join(directory.Path, "evidence.policy.json");
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
        Assert.False(envelope.Satisfied);
        Assert.Equal("unverified", envelope.Status);
        Assert.Contains("do not grant admission", envelope.Message, StringComparison.Ordinal);
        Assert.Contains("registered verifier", envelope.NextAction, StringComparison.Ordinal);
        Assert.Contains("issue779-consumer-acceptance.md", envelope.NextAction, StringComparison.Ordinal);
        Assert.Equal("ready_with_external_prerequisites", report.Status);
        Assert.Contains(report.Checks, check => check.Id == "policy" && check.Satisfied && check.Status == "ready");
        Assert.Contains(report.Checks, check => check.Id == "diff" && check.Satisfied && check.Status == "ready");
        Assert.Equal([policyPath], Directory.GetFiles(directory.Path));
        Assert.Empty(Directory.GetDirectories(directory.Path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(policyPath));
    }

    [Fact]
    public async Task Doctor_ShouldNotEchoEnvironmentValueInItsDiagnostics()
    {
        const string canary = "doctor-protected-canary-779";
        using var environment = new EnvironmentVariableScope("GITHUB_ACTIONS", canary);
        using var directory = TestDirectory.Create();
        var policyPath = Path.Join(directory.Path, "evidence.policy.json");
        var profile = new EvidenceProfile(
            "contract",
            EvidenceProfileScope.Targeted,
            [],
            [new EvidenceProducerDeclaration("contract", "contract", "1", [], ["contract/assertion@1"], [], 1)],
            [new EvidenceObligation("contract", "contract", "Contract behavior changed.", ["contract"], "contract/assertion@1")]);
        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(
            new EvidencePolicy("doctor-policy", "1", "contract", [profile], [])));

        var report = await new EvidenceCliWorkflow(new EvidencePlanner()).DoctorAsync(
            new EvidencePlanningRequest(policyPath, ["src/Contract.cs"], null),
            CancellationToken.None);

        Assert.All(report.Checks, check =>
        {
            Assert.DoesNotContain(canary, check.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, check.NextAction ?? string.Empty, StringComparison.Ordinal);
        });
    }
}
