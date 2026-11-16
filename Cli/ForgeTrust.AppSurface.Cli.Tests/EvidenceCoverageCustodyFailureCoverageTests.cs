using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class EvidenceCoverageCustodyFailureCoverageTests
{
    [Fact]
    public async Task UnsafeOrdinaryCaptureKeepsThePassingFreshResultWithoutFollowingTheForeignLink()
    {
        using var directory = TestDirectory.Create();
        using var history = new EnvironmentVariableScope("APPSURFACE_ARTIFACT_HOME",
            TestPathUtils.PathUnder(directory.Path, "history"));
        var solution = await CreateSolutionAsync(directory.Path);
        var output = TestPathUtils.PathUnder(directory.Path, "run");
        var finalRoot = TestPathUtils.PathUnder(directory.Path, "current-reports");
        var sentinel = TestPathUtils.PathUnder(directory.Path, "foreign-report.xml");
        await File.WriteAllTextAsync(sentinel, "caller-owned sentinel");
        string? linkedPath = null;
        string? backup = null;
        void ChangeMetadata()
        {
            var coverage = TestPathUtils.PathUnder(output, "coverage");
            var metadata = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(coverage, ".timings.*.tmp").Single()))!;
            var relative = metadata["projects"]!.AsArray().Single()!["testResults"]!.AsArray().Single()!["path"]!.GetValue<string>();
            linkedPath = Path.Combine(coverage, relative.Replace('/', Path.DirectorySeparatorChar));
            backup = TestPathUtils.PathUnder(directory.Path, "original-test-results.xml");
            File.Move(linkedPath, backup);
            File.CreateSymbolicLink(linkedPath, sentinel);
        }
        var adapter = Adapter(out var runner, ChangeMetadata);
        try
        {
            var execution = await adapter.RunWithInventoryAsync(Declaration(), solution, output, null,
                CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None,
                finalReportRoot: finalRoot);

            Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
            Assert.Equal(Declaration().AssertionIds, execution.Result.SatisfiedAssertionIds);
            Assert.Null(execution.Inventory);
            Assert.Null(execution.Result.Artifacts);
            Assert.Equal("inventory-unavailable", execution.InventoryIneligibilityReason);
            Assert.Contains("Coverage gate passed", execution.Result.Diagnostic!, StringComparison.Ordinal);
            Assert.False(Directory.Exists(finalRoot));
            Assert.Equal("caller-owned sentinel", await File.ReadAllTextAsync(sentinel));
            Assert.True(File.Exists(backup));
            Assert.True(runner.TestInvocationCount > 0);
            AssertOriginalReports(output);
        }
        finally
        {
            if (linkedPath is not null && backup is not null && File.Exists(backup))
            {
                if (File.Exists(linkedPath)) File.Delete(linkedPath);
                File.Move(backup, linkedPath);
            }
        }
    }

    [Theory]
    [InlineData("missing-slot", "inventory-slot-missing")]
    [InlineData("occupied-custody-root", "inventory-custody-unavailable")]
    public async Task CustodyFailureKeepsThePassingFreshResultAndAllOriginalReports(string defect, string reason)
    {
        using var directory = TestDirectory.Create();
        using var history = new EnvironmentVariableScope("APPSURFACE_ARTIFACT_HOME",
            TestPathUtils.PathUnder(directory.Path, "history"));
        var solution = await CreateSolutionAsync(directory.Path);
        var declaration = Declaration() with
        { ArtifactSlots = defect == "missing-slot" ? [] : [CoverageEvidenceInventory.CreateSlot()] };
        var output = TestPathUtils.PathUnder(directory.Path, "run");
        var custody = TestPathUtils.PathUnder(directory.Path, "custody");
        if (defect == "occupied-custody-root") await File.WriteAllTextAsync(custody, "caller-owned sentinel");
        var writer = new EvidenceArtifactWriter(declaration, custody);
        var adapter = Adapter(out var runner);
        await using var children = EvidenceChildLifetimeSession.Start("custody-fault-" + Guid.NewGuid().ToString("N"));

        var execution = await adapter.RunWithInventoryAsync(declaration, solution, output, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None, writer, children);

        Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
        Assert.Equal(declaration.AssertionIds, execution.Result.SatisfiedAssertionIds);
        Assert.Null(execution.Inventory);
        Assert.Null(execution.Result.Artifacts);
        Assert.Equal(reason, execution.InventoryIneligibilityReason);
        Assert.Empty(writer.WrittenArtifacts);
        if (defect == "occupied-custody-root") Assert.Equal("caller-owned sentinel", await File.ReadAllTextAsync(custody));
        else Assert.False(Directory.Exists(custody));
        Assert.True(runner.TestInvocationCount > 0);
        AssertOriginalReports(output);
    }

    [Theory]
    [InlineData("null-slug")]
    [InlineData("null-test-result-path")]
    [InlineData("project-schema")]
    [InlineData("project-slug")]
    [InlineData("occupied-project-parent")]
    public async Task UnsafeOrdinaryPromotionCannotClaimDeliveredReportsAndPreservesExecutionFiles(string defect)
    {
        using var directory = TestDirectory.Create();
        using var history = new EnvironmentVariableScope("APPSURFACE_ARTIFACT_HOME",
            TestPathUtils.PathUnder(directory.Path, "history"));
        var solution = await CreateSolutionAsync(directory.Path);
        var output = TestPathUtils.PathUnder(directory.Path, "run");
        var finalRoot = TestPathUtils.PathUnder(directory.Path, "current-reports");
        var occupied = TestPathUtils.PathUnder(finalRoot, "coverage", "projects");
        if (defect == "occupied-project-parent")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
            await File.WriteAllTextAsync(occupied, "caller-owned sentinel");
        }
        void ChangeMetadata()
        {
            var coverage = TestPathUtils.PathUnder(output, "coverage");
            if (defect is "null-slug" or "null-test-result-path")
            {
                var path = Directory.GetFiles(coverage, ".timings.*.tmp").Single();
                var metadata = JsonNode.Parse(File.ReadAllText(path))!;
                var project = metadata["projects"]!.AsArray().Single()!;
                if (defect == "null-slug") project["slug"] = null;
                else project["testResults"]!.AsArray().Single()!["path"] = null;
                File.WriteAllText(path, metadata.ToJsonString());
            }
            else if (defect is "project-schema" or "project-slug")
            {
                var path = Directory.GetFiles(TestPathUtils.PathUnder(coverage, "projects"),
                    "coverage-project.json", SearchOption.AllDirectories).Single();
                var metadata = JsonNode.Parse(File.ReadAllText(path))!;
                if (defect == "project-schema") metadata["schemaVersion"] = 0;
                else metadata["slug"] = "another-project";
                File.WriteAllText(path, metadata.ToJsonString());
            }
        }
        var adapter = Adapter(out var runner, ChangeMetadata);

        var execution = await adapter.RunWithInventoryAsync(Declaration(), solution, output, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None,
            finalReportRoot: finalRoot);

        Assert.Equal(EvidenceProducerOutcome.Failed, execution.Result.Outcome);
        Assert.Empty(execution.Result.SatisfiedAssertionIds);
        Assert.Null(execution.Inventory);
        Assert.Null(execution.Result.Artifacts);
        Assert.Equal("inventory-result-not-passed", execution.InventoryIneligibilityReason);
        Assert.Contains("Original reports remain in the execution output", execution.Result.Diagnostic!, StringComparison.Ordinal);
        AssertOriginalReports(output);
        Assert.True(runner.TestInvocationCount > 0);
        if (defect == "occupied-project-parent")
            Assert.Equal("caller-owned sentinel", await File.ReadAllTextAsync(occupied));
    }

    private static CoverageEvidenceProducer Adapter(out EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner runner,
        Action? timingsStaged = null)
    {
        runner = new("tests/Sample.Tests/Sample.Tests.csproj");
        return new(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(runner,
            new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(), TimeProvider.System,
            timingsStaged: timingsStaged)));
    }

    private static EvidenceProducerDeclaration Declaration() => new("coverage", "coverage", "1", [],
        ["coverage/assertion@1"], [CoverageEvidenceInventory.CreateSlot()], 30, new EvidenceCoverageGateRequirements(95, 85))
    {
        CoverageExecution = new(TestResults: EvidenceCoverageTestResultFormat.Junit) { CooperativeChildLifetime = true },
    };

    private static async Task<string> CreateSolutionAsync(string root)
    {
        var solution = TestPathUtils.PathUnder(root, "sample.slnx");
        await File.WriteAllTextAsync(solution, "solution fixture");
        var project = TestPathUtils.PathUnder(root, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project />");
        return solution;
    }

    private static void AssertOriginalReports(string root)
    {
        foreach (var name in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
            Assert.True(File.Exists(TestPathUtils.PathUnder(root, "coverage", name)), name);
    }
}
