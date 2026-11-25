using System.Text.Json;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;
using CliFx;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary><c>appsurface evidence run</c> and <c>explain</c> with a scoped coverage producer.</summary>
public sealed class EvidenceRunScopedCoverageTests
{
    private const string ProducerId = "alpha-patch";
    private const string ProjectPath = "tests/Alpha.Tests/Alpha.Tests.csproj";
    private const string Changed = "src/Alpha/Feature.cs";

    [Fact]
    public async Task Run_ShouldCloseTheScopedObligationWriteTheReceiptAndVerify()
    {
        using var fixture = Fixture.Create();

        await fixture.RunAsync(CoverageEvidenceProducerTests.Cobertura(("Alpha", Changed, 1, 1)));

        Assert.Contains("Evidence result: TargetedComplete", fixture.Console.ReadOutputString(), StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.ReceiptPath));
        var verified = await fixture.VerifyAsync();
        Assert.Equal(EvidenceClaimKind.TargetedComplete, verified.ClaimKind);
        Assert.Equal(["config-patch"], verified.ClosedObligationIds);
        Assert.True(File.Exists(Path.Join(fixture.Output, "coverage-scoped", ProducerId, "coverage.cobertura.xml")));
    }

    [Fact]
    public async Task Run_ShouldRetireAnEarlierReceiptAndFailIncompleteWhenTheAssemblyIsMissing()
    {
        using var fixture = Fixture.Create();
        var stale = Path.Join(fixture.Output, "artifacts", ProducerId, "stale.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        await File.WriteAllTextAsync(stale, "{}");

        var exception = await Assert.ThrowsAsync<CommandException>(() => fixture.RunAsync(CoverageEvidenceProducerTests.Cobertura(("Other", "src/Other/X.cs", 1, 1))));

        Assert.StartsWith("ASEVD211", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(stale));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(fixture.ReceiptPath));
        Assert.Equal("ScopeAssemblyNotInstrumented", receipt.RootElement.GetProperty("reason").GetString());
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, (await fixture.VerifyAsync()).ExecutionVerdict);
    }

    [Fact]
    public async Task Explain_ShouldListProjectsSourcesAndThresholds()
    {
        using var fixture = Fixture.Create();
        var command = new EvidenceExplainCommand(new EvidenceCliWorkflow(new EvidencePlanner()))
        {
            PolicyPath = fixture.PolicyPath,
            Paths = [Changed],
            OutputDirectory = fixture.Output,
        };

        await command.ExecuteAsync(fixture.Console);

        var output = fixture.Console.ReadOutputString();
        Assert.Contains("Scoped coverage producer 'alpha-patch' (timeout 900s, patch line 80%, patch branch not declared, mode measurable, tolerance 0%):", output, StringComparison.Ordinal);
        Assert.Contains($"  project {ProjectPath}", output, StringComparison.Ordinal);
        Assert.Contains("  source src/Alpha/** -> assembly Alpha", output, StringComparison.Ordinal);
        Assert.Contains("The repository-wide gate is not evaluated", output, StringComparison.Ordinal);
        using var summary = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Output, "evidence-summary.json")));
        var producer = summary.RootElement.GetProperty("scopedCoverageProducers")[0];
        Assert.Equal(ProducerId, producer.GetProperty("id").GetString());
        Assert.Equal(ProjectPath, producer.GetProperty("projects")[0].GetString());
        Assert.Equal("Alpha", producer.GetProperty("sources")[0].GetProperty("assembly").GetString());
        Assert.False(producer.GetProperty("overallGateEvaluated").GetBoolean());
    }

    [Fact]
    public void Reuse_ShouldBeRefusedForAScopedProducer()
    {
        var plan = new EvidencePlanner().Resolve(Fixture.Policy(), [new NormalizedDiffPath(Changed)]);

        var exception = Assert.Throws<CommandException>(() => EvidenceReuseCommandOptions.ValidateContext(true, plan, static _ => null));

        Assert.StartsWith("ASEVD220", exception.Message, StringComparison.Ordinal);
        EvidenceReuseCommandOptions.ValidateContext(false, plan, static _ => null);
    }

    [Fact]
    public void FormatAndDescribePlan_ShouldBeEmptyWithoutAScopedProducer()
    {
        var policy = EvidenceScopedCoveragePlannerTests.CreatePolicy(new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/a@1"], [], 60, new EvidenceCoverageGateRequirements(90, 80)));
        policy = policy with { Profiles = [policy.Profiles[0] with { Obligations = [] }] };
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath(Changed)]);

        Assert.Empty(ScopedCoverageReceipt.FormatPlan(plan));
        Assert.Empty(ScopedCoverageReceipt.DescribePlan(plan));
        Assert.Throws<ArgumentNullException>(() => ScopedCoverageReceipt.FormatPlan(null!));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory;

        private Fixture(TestDirectory directory)
        {
            _directory = directory;
            Root = Path.GetFullPath(directory.Path);
            Output = Path.Join(Root, "evidence");
            PolicyPath = Path.Join(Root, "evidence.policy.json");
            File.WriteAllBytes(PolicyPath, EvidenceCanonicalJson.Serialize(Policy()));
            var project = Path.Join(Repository, ProjectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project />");
            File.WriteAllText(Path.Join(Repository, "sample.slnx"), "{}");
            File.WriteAllText(DiffPath, $"diff --git a/{Changed} b/{Changed}\n--- a/{Changed}\n+++ b/{Changed}\n@@ -0,0 +1 @@\n+line\n");
        }

        public string Root { get; }

        public string Repository => Path.Join(Root, "repo");

        public string Output { get; }

        public string PolicyPath { get; }

        public string DiffPath => Path.Join(Root, "change.diff");

        public string ReceiptPath => Path.Join(Output, "artifacts", ProducerId, ScopedCoverageReceipt.ReceiptRelativePath);

        public FakeInMemoryConsole Console { get; } = new();

        public static Fixture Create() => new(TestDirectory.Create());

        public static EvidencePolicy Policy()
        {
            var producer = EvidenceScopedCoveragePlannerTests.CreateProducer() with
            {
                Id = ProducerId,
                CoverageScope = new EvidenceCoverageScope([ProjectPath], [new EvidenceCoverageScopeSource("src/Alpha/**", "Alpha")], MinPatchLinePercent: 80, TolerancePercent: 0),
            };
            return EvidenceScopedCoveragePlannerTests.CreatePolicy(producer);
        }

        public async Task RunAsync(string mergedCobertura)
        {
            var command = new EvidenceRunCommand(new EvidenceCliWorkflow(new EvidencePlanner()), CoverageEvidenceProducerTests.CreateScopedProducer(mergedCobertura))
            {
                PolicyPath = PolicyPath,
                DiffFile = DiffPath,
                SolutionPath = Path.Join(Repository, "sample.slnx"),
                OutputDirectory = Output,
                RepositoryRootResolver = () => Repository,
                StepSummaryPathResolver = static () => null,
            };
            await command.ExecuteAsync(Console);
        }

        public async Task<EvidenceManifest> VerifyAsync() =>
            (await new EvidenceCliWorkflow(new EvidencePlanner()).VerifyAsync(
                Path.Join(Output, "evidence-plan.json"),
                Path.Join(Output, "evidence-manifest.json"),
                CancellationToken.None)).Manifest;

        public void Dispose()
        {
            Console.Dispose();
            _directory.Dispose();
        }
    }
}
