using System.Text.Json;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(EvidenceReuseSyntheticLocalContextCollection.Name)]
public sealed class EvidenceReuseRunCommandCoverageTests
{
    [Fact]
    public async Task ReuseRunRejectsAnOccupiedExplicitOutputAndPreservesItsContents()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await WriteEmptyPolicyAsync(repository);
        var occupiedOutput = TestPathUtils.PathUnder(repository.Root, "claimed-output");
        await File.WriteAllTextAsync(occupiedOutput, "owned by the caller");
        var context = SyntheticLocalContext.Capture();

        try
        {
            context.Enter(repository.Root);
            using var console = new FakeInMemoryConsole();
            var command = CreateRunCommand(out var runner);
            command.PolicyPath = TestPathUtils.PathUnder(repository.Root, "evidence.policy.json");
            command.Paths = ["src/input.txt"];
            command.OutputDirectory = occupiedOutput;
            command.ReceiptStore = TestPathUtils.PathUnder(repository.Root, "collision-store");
            command.Reuse = true;

            var error = await Assert.ThrowsAsync<CommandException>(() => command.ExecuteAsync(console).AsTask());

            Assert.Contains("ASEVD217", error.Message, StringComparison.Ordinal);
            Assert.Contains("reuse.output_unavailable", error.Message, StringComparison.Ordinal);
            Assert.Equal("owned by the caller", await File.ReadAllTextAsync(occupiedOutput));
            Assert.False(File.Exists(TestPathUtils.PathUnder(occupiedOutput, "evidence-plan.json")));
            Assert.Equal(0, runner.InvocationCount);
            Assert.False(Directory.Exists(command.ReceiptStore));
        }
        finally
        {
            context.Restore();
        }
    }

    [Theory]
    [InlineData("filesystem-root")]
    [InlineData("reserved-component")]
    public async Task ReuseRunMapsInvalidOutputPathsToAsevd217WithoutTouchingCallerFiles(string defect)
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await WriteEmptyPolicyAsync(repository);
        var sentinel = TestPathUtils.PathUnder(repository.Root, "caller-owned.txt");
        await File.WriteAllTextAsync(sentinel, "caller-owned bytes");
        var output = defect == "filesystem-root"
            ? Path.GetPathRoot(repository.Root)!
            : TestPathUtils.PathUnder(repository.Root, "CON");
        var store = TestPathUtils.PathUnder(repository.Root, "private-store");
        var context = SyntheticLocalContext.Capture();

        try
        {
            context.Enter(repository.Root);
            using var console = new FakeInMemoryConsole();
            var command = CreateRunCommand(out var runner);
            command.PolicyPath = TestPathUtils.PathUnder(repository.Root, "evidence.policy.json");
            command.Paths = ["src/input.txt"];
            command.OutputDirectory = output;
            command.ReceiptStore = store;
            command.Reuse = true;

            var error = await Assert.ThrowsAsync<CommandException>(() => command.ExecuteAsync(console).AsTask());

            Assert.Contains("ASEVD217", error.Message, StringComparison.Ordinal);
            Assert.Contains("reuse.output_unavailable", error.Message, StringComparison.Ordinal);
            Assert.Equal("caller-owned bytes", await File.ReadAllTextAsync(sentinel));
            Assert.Equal(0, runner.InvocationCount);
            Assert.False(Directory.Exists(store));
            if (defect == "reserved-component") Assert.False(Directory.Exists(output));
        }
        finally
        {
            context.Restore();
        }
    }

    [Fact]
    public async Task ReuseRunWithAnEmptyProfileClaimsDefaultFreshChildAndWritesSuccessfulNoEvidenceManifest()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await WriteEmptyPolicyAsync(repository);
        var runsRoot = TestPathUtils.PathUnder(repository.Root, "TestResults", "evidence", "runs");
        Assert.False(Directory.Exists(runsRoot));
        var context = SyntheticLocalContext.Capture();

        try
        {
            context.Enter(repository.Root);
            using var console = new FakeInMemoryConsole();
            var command = CreateRunCommand(out var runner);
            command.PolicyPath = TestPathUtils.PathUnder(repository.Root, "evidence.policy.json");
            command.Paths = ["src/input.txt"];
            command.ReceiptStore = TestPathUtils.PathUnder(repository.Root, "empty-profile-store");
            command.Reuse = true;
            await command.ExecuteAsync(console);

            var runRoot = Assert.Single(Directory.EnumerateDirectories(runsRoot));
            Assert.NotEqual(Path.GetFullPath(TestPathUtils.PathUnder(repository.Root, "TestResults", "evidence")), runRoot);
            Assert.True(File.Exists(TestPathUtils.PathUnder(runRoot, "evidence-plan.json")));
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
                await File.ReadAllBytesAsync(TestPathUtils.PathUnder(runRoot, "evidence-manifest.json")));
            Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
            Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
            Assert.Empty(manifest.ProducerResults);
            Assert.Empty(manifest.ResourceResults);
            Assert.Equal(0, runner.InvocationCount);
            using var summary = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(runRoot, "evidence-summary.json")));
            Assert.Equal("manifest", summary.RootElement.GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(summary.RootElement.GetProperty("next").GetString()));
            Assert.Contains("No evidence is required", console.ReadOutputString(), StringComparison.Ordinal);
        }
        finally
        {
            context.Restore();
        }
    }

    private static EvidenceRunCommand CreateRunCommand(out EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner runner)
    {
        runner = new EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj");
        var producer = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(), TimeProvider.System)));
        return new(new EvidenceCliWorkflow(new EvidencePlanner()), producer);
    }

    private static async Task WriteEmptyPolicyAsync(EvidenceIdentityCoverageTests.TestRepository repository)
    {
        await repository.WriteAsync("src/input.txt", "explicitly selected input");
        var profile = new EvidenceProfile("empty", EvidenceProfileScope.Targeted, [], [], []);
        var fallbackProducer = new EvidenceProducerDeclaration("fallback", "coverage", "1", [], ["fallback/assertion@1"], [], 30,
            new EvidenceCoverageGateRequirements(95, 85));
        var fallback = new EvidenceProfile("conservative", EvidenceProfileScope.Targeted, [], [fallbackProducer],
            [new EvidenceObligation("fallback-check", "test", "Conservative coverage", [fallbackProducer.Id], "fallback/assertion@1")]);
        var policy = new EvidencePolicy("empty-policy", "1", fallback.Id, [fallback, profile],
            [new EvidencePolicyRule("source", "src/**", profile.Id)]);
        await repository.WriteAsync("evidence.policy.json",
            System.Text.Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(policy)));
    }

    private sealed record SyntheticLocalContext(string OriginalDirectory, string? OriginalActions)
    {
        internal static SyntheticLocalContext Capture()
            => new(Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

        internal void Enter(string repositoryRoot)
        {
            Directory.SetCurrentDirectory(repositoryRoot);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
        }

        internal void Restore()
        {
            Directory.SetCurrentDirectory(OriginalDirectory);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", OriginalActions);
        }
    }
}
