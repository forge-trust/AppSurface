using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Scoped patch coverage branch of the built-in coverage producer, against a two-assembly fixture.</summary>
/// <remarks>
/// The fixture declares one test project that references only <c>Alpha</c>. The fake report generator writes the merged
/// Cobertura report each case needs, so the tests drive the real run, gate, reader and classifier without a build.
/// </remarks>
public sealed partial class CoverageEvidenceProducerTests
{
    private const string ScopedProducerId = "alpha-patch";
    private const string ScopedProject = "tests/Alpha.Tests/Alpha.Tests.csproj";

    [Fact]
    public async Task Scoped_ShouldPassRunOnlyTheDeclaredProjectAndWriteTheReceipt()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(("Alpha", "src/Alpha/Feature.cs", 1, 1), ("Alpha", "src/Alpha/Feature.cs", 2, 1), ("Beta", "src/Beta/Other.cs", 1, 1)));

        var result = await fixture.RunAsync(Diff("src/Alpha/Feature.cs", 2));

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(new[] { EvidenceScopedCoverageContract.AssertionId }, result.SatisfiedAssertionIds);
        Assert.Contains("Scoped patch coverage Complete, 1 project, 2 sources: line 100% (2/2)", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("coverage-patch-targets.json", result.Diagnostic, StringComparison.Ordinal);
        Assert.EndsWith("The repository-wide gate was not evaluated.", result.Diagnostic, StringComparison.Ordinal);
        var artifact = Assert.Single(result.Artifacts!);
        Assert.Equal(ScopedCoverageReceipt.ReceiptRelativePath, artifact.RelativePath);
        var arguments = Assert.Single(fixture.Runner.TestArguments);
        Assert.Contains(TestPathUtils.PathUnder(fixture.Root, ScopedProject), arguments);
        Assert.True(File.Exists(TestPathUtils.PathUnder(fixture.Output, "coverage-scoped", ScopedProducerId, "coverage-patch-targets.json")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(fixture.Output, "coverage", "coverage-gate.json")));
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("Complete", receipt.RootElement.GetProperty("reason").GetString());
        Assert.True(receipt.RootElement.GetProperty("sources")[0].GetProperty("assemblyReported").GetBoolean());
    }

    [Fact]
    public async Task Scoped_ShouldFailBelowThreshold()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(("Alpha", "src/Alpha/Feature.cs", 1, 1), ("Alpha", "src/Alpha/Feature.cs", 2, 0), ("Beta", "src/Beta/Other.cs", 1, 1)));

        var result = await fixture.RunAsync(Diff("src/Alpha/Feature.cs", 2));

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("BelowThreshold", receipt.RootElement.GetProperty("reason").GetString());
        Assert.Equal(50m, receipt.RootElement.GetProperty("line").GetProperty("percent").GetDecimal());
    }

    [Fact]
    public async Task Scoped_ShouldFailWhenAChangedLineIsInAnAssemblyTheProjectDoesNotReference()
    {
        // Beta is declared but its assembly is not in the report, because the declared project does not reference it.
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(("Alpha", "src/Alpha/Feature.cs", 1, 1)));

        var result = await fixture.RunAsync(Diff("src/Beta/Other.cs", 1));

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains("Not in the report: Beta", result.Diagnostic, StringComparison.Ordinal);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("ScopeAssemblyNotInstrumented", receipt.RootElement.GetProperty("reason").GetString());
        Assert.Equal("src/Beta/Other.cs", receipt.RootElement.GetProperty("unknownFiles")[0].GetString());
        Assert.False(receipt.RootElement.GetProperty("sources")[1].GetProperty("assemblyReported").GetBoolean());
    }

    [Fact]
    public async Task Scoped_ShouldFailWhenNothingChangedInScope()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(("Alpha", "src/Alpha/Feature.cs", 1, 1), ("Beta", "src/Beta/Other.cs", 1, 1)));

        var result = await fixture.RunAsync(Diff("docs/readme.md", 1));

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("NothingInScope", receipt.RootElement.GetProperty("reason").GetString());
        Assert.Equal(1, receipt.RootElement.GetProperty("outOfScopeFiles").GetInt32());
    }

    [Fact]
    public async Task Scoped_ShouldReportAFailedTestAsRunFailed()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(("Alpha", "src/Alpha/Feature.cs", 1, 1)), testExitCode: 1);

        var result = await fixture.RunAsync(Diff("src/Alpha/Feature.cs", 1));

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("RunFailed", receipt.RootElement.GetProperty("reason").GetString());
        Assert.Equal("unverified", receipt.RootElement.GetProperty("line").GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("ASCOV901 the private core failed.", "ASCOV901")]
    [InlineData("private core failed.", "CoverageExecutionException")]
    public async Task Scoped_ShouldReportACoreFailureAsRunFailedWithoutItsDetail(string message, string expected)
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(), runner: new ThrowingCoverageRunProcessRunner(new CoverageExecutionException(message)));

        var result = await fixture.RunAsync(Diff("src/Alpha/Feature.cs", 1));

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains($"failed with {expected}", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(message, result.Diagnostic, StringComparison.Ordinal);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("RunFailed", receipt.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Scoped_ShouldReportAMalformedDiffAsRunFailed()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(("Alpha", "src/Alpha/Feature.cs", 1, 1), ("Beta", "src/Beta/Other.cs", 1, 1)));
        var bytes = Encoding.UTF8.GetBytes("@@ -0,0 +1 @@\n+no file header\n");

        var result = await fixture.RunAsync(new EvidenceDiffSnapshot(bytes, "bad.diff", Convert.ToHexString(SHA256.HashData(bytes))));

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("RunFailed", receipt.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Scoped_ShouldWriteADiffUnavailableReceiptWithoutStartingTheCore()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(), runner: new ThrowingCoverageRunProcessRunner(new InvalidOperationException("core must not start")));

        var result = await fixture.RunAsync(diff: null);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Contains("requires --diff-file", result.Diagnostic, StringComparison.Ordinal);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("DiffUnavailable", receipt.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Scoped_ShouldWriteASolutionUnavailableReceiptWithoutStartingTheCore()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(), runner: new ThrowingCoverageRunProcessRunner(new InvalidOperationException("core must not start")));

        var result = await fixture.RunAsync(Diff("src/Alpha/Feature.cs", 1), solution: null);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("SolutionUnavailable", receipt.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Scoped_ShouldWriteADeadlineExceededReceiptFromTheTimeoutHandler()
    {
        var clock = new DeadlineTimeProvider(TimeSpan.FromSeconds(3));
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(), runner: new DeadlineCoverageRunProcessRunner(clock, "test"), clock: clock, timeoutSeconds: 3);

        var result = await fixture.RunAsync(Diff("src/Alpha/Feature.cs", 1));

        Assert.Equal(EvidenceProducerOutcome.TimedOut, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("DeadlineExceeded", receipt.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Scoped_ShouldPropagateCallerCancellationWithoutAReceipt()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(), runner: new ThrowingCoverageRunProcessRunner(new OperationCanceledException("core cancellation")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(Diff("src/Alpha/Feature.cs", 1), cancellationToken: cancellation.Token));

        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task Scoped_ShouldWithholdTheAssertionWhenTheReceiptCannotBeWritten()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(("Alpha", "src/Alpha/Feature.cs", 1, 1), ("Beta", "src/Beta/Other.cs", 1, 1)));
        Directory.CreateDirectory(fixture.ArtifactRoot);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture.ArtifactRoot, EvidenceScopedCoverageContract.ReceiptRelativeRoot), "a file where the receipt directory belongs");

        var result = await fixture.RunAsync(Diff("src/Alpha/Feature.cs", 1));

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Contains("The receipt could not be written", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_ShouldReturnNoArtifactsWhenNoWriterIsSupplied()
    {
        using var fixture = await ScopedFixture.CreateAsync(Cobertura(("Alpha", "src/Alpha/Feature.cs", 1, 1), ("Beta", "src/Beta/Other.cs", 1, 1)));

        var execution = await fixture.Producer.RunWithInventoryAsync(fixture.Declaration, fixture.Solution, fixture.Output,
            Diff("src/Alpha/Feature.cs", 1), CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None,
            repositoryRoot: fixture.Root);

        Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
        Assert.Null(execution.Result.Artifacts);
        Assert.Null(execution.Inventory);
        Assert.Equal("scoped-coverage-not-reusable", execution.InventoryIneligibilityReason);
    }

    [Fact]
    public async Task OlderReader_ShouldFailClosedAtExecutionWhenItDropsTheScopeBlock()
    {
        // A reader that predates coverageScope drops the unknown property and sees a coverage producer with no gate.
        var producer = CreateProducer(new ThrowingCoverageRunProcessRunner(new InvalidOperationException("core must not start")));
        var dropped = EvidenceScopedCoveragePlannerTests.CreateProducer() with { CoverageScope = null };

        var result = await producer.RunAsync(dropped, "sample.slnx", "output", null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
    }

    /// <summary>Creates a producer whose fake run reports <paramref name="mergedCobertura"/> as the merged report.</summary>
    internal static CoverageEvidenceProducer CreateScopedProducer(string mergedCobertura, int testExitCode = 0) => new(
        new CoverageEvidenceExecutionWorkflow(
            new CoverageRunWorkflow(new ScopedTestRunner(testExitCode), new FixedReportGenerator(mergedCobertura), TimeProvider.System)));

    /// <summary>Builds a merged Cobertura report with one class per package and file.</summary>
    internal static string Cobertura(params (string Package, string File, int Line, int Hits)[] rows)
    {
        var builder = new StringBuilder("<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" line-rate=\"1\" branch-rate=\"1\"><packages>");
        foreach (var package in rows.GroupBy(static row => row.Package))
        {
            builder.Append($"<package name=\"{package.Key}\"><classes>");
            foreach (var file in package.GroupBy(static row => row.File))
            {
                builder.Append($"<class name=\"C\" filename=\"{file.Key}\"><lines>");
                foreach (var row in file)
                {
                    builder.Append($"<line number=\"{row.Line}\" hits=\"{row.Hits}\" />");
                }

                builder.Append("</lines></class>");
            }

            builder.Append("</classes></package>");
        }

        return builder.Append("</packages></coverage>").ToString();
    }

    private static EvidenceDiffSnapshot Diff(string path, int addedLines)
    {
        var body = string.Concat(Enumerable.Range(1, addedLines).Select(static line => $"+line {line}\n"));
        var bytes = Encoding.UTF8.GetBytes($"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -0,0 +1,{addedLines} @@\n{body}");
        return new EvidenceDiffSnapshot(bytes, "scoped.diff", Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private sealed class ScopedFixture : IDisposable
    {
        private readonly TestDirectory _directory;

        private ScopedFixture(TestDirectory directory, CoverageEvidenceProducer producer, ScopedTestRunner runner, int timeoutSeconds)
        {
            _directory = directory;
            Producer = producer;
            Runner = runner;
            Declaration = EvidenceScopedCoveragePlannerTests.CreateProducer() with
            {
                Id = ScopedProducerId,
                TimeoutSeconds = timeoutSeconds,
                CoverageScope = new EvidenceCoverageScope(
                    [ScopedProject],
                    [new EvidenceCoverageScopeSource("src/Alpha/**", "Alpha"), new EvidenceCoverageScopeSource("src/Beta/**", "Beta")],
                    MinPatchLinePercent: 80,
                    PatchLineMode: "measurable",
                    TolerancePercent: 0),
            };
        }

        public string Root => _directory.Path;

        public string Solution => TestPathUtils.PathUnder(Root, "sample.slnx");

        public string Output => TestPathUtils.PathUnder(Root, "output");

        public string ArtifactRoot => TestPathUtils.PathUnder(Output, "artifacts", ScopedProducerId);

        public string ReceiptPath => TestPathUtils.PathUnder(ArtifactRoot, ScopedCoverageReceipt.ReceiptRelativePath);

        public CoverageEvidenceProducer Producer { get; }

        public ScopedTestRunner Runner { get; }

        public EvidenceProducerDeclaration Declaration { get; }

        public static async Task<ScopedFixture> CreateAsync(
            string mergedCobertura,
            int testExitCode = 0,
            ICoverageRunProcessRunner? runner = null,
            TimeProvider? clock = null,
            int timeoutSeconds = 60)
        {
            var directory = TestDirectory.Create();
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(directory.Path, "sample.slnx"), "{}");
            var project = TestPathUtils.PathUnder(directory.Path, ScopedProject);
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            await File.WriteAllTextAsync(project, "<Project />");
            var testRunner = new ScopedTestRunner(testExitCode);
            var producer = new CoverageEvidenceProducer(
                new CoverageEvidenceExecutionWorkflow(
                    new CoverageRunWorkflow(runner ?? testRunner, new FixedReportGenerator(mergedCobertura), TimeProvider.System)),
                clock);
            return new ScopedFixture(directory, producer, testRunner, timeoutSeconds);
        }

        public async Task<EvidenceProducerResult> RunAsync(
            EvidenceDiffSnapshot? diff,
            string? solution = "",
            CancellationToken cancellationToken = default)
        {
            var execution = await Producer.RunWithInventoryAsync(
                Declaration,
                solution == string.Empty ? Solution : solution,
                Output,
                diff,
                CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null),
                cancellationToken,
                artifactWriter: new EvidenceArtifactWriter(Declaration, ArtifactRoot),
                repositoryRoot: Root);
            return execution.Result;
        }

        public JsonDocument ReadReceipt() => JsonDocument.Parse(File.ReadAllBytes(ReceiptPath));

        public void Dispose() => _directory.Dispose();
    }

    private sealed class ScopedTestRunner(int testExitCode) : ICoverageRunProcessRunner
    {
        private const string CapabilityOutput = """
            {
              "Properties": { "TestingPlatformDotnetTestSupport": "false", "TargetFramework": "net10.0" },
              "Items": { "PackageReference": [{ "Identity": "coverlet.collector" }] }
            }
            """;

        public List<IReadOnlyList<string>> TestArguments { get; } = [];

        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            switch (request.Arguments.FirstOrDefault())
            {
                case "msbuild":
                    return new CoverageRunProcessResult(0, CapabilityOutput, StandardOutput: CapabilityOutput);
                case "test":
                    TestArguments.Add(request.Arguments);
                    var resultsIndex = request.Arguments.ToList().FindIndex(static argument => argument == "--results-directory");
                    var collector = TestPathUtils.PathUnder(request.Arguments[resultsIndex + 1], "collector");
                    Directory.CreateDirectory(collector);
                    await File.WriteAllTextAsync(TestPathUtils.PathUnder(collector, "coverage.cobertura.xml"), "<coverage line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>", cancellationToken);
                    if (request.OutputFile is not null)
                    {
                        await File.WriteAllTextAsync(request.OutputFile, "coverage test output", cancellationToken);
                    }

                    return new CoverageRunProcessResult(testExitCode, "coverage test output");
                default:
                    return new CoverageRunProcessResult(0, "build output");
            }
        }
    }

    private sealed class FixedReportGenerator(string cobertura) : ICoverageRunReportGenerator
    {
        public async Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles, string outputDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDirectory);
            var coberturaPath = TestPathUtils.PathUnder(outputDirectory, "Cobertura.xml");
            var summaryPath = TestPathUtils.PathUnder(outputDirectory, "Summary.txt");
            await File.WriteAllTextAsync(coberturaPath, cobertura, cancellationToken);
            await File.WriteAllTextAsync(summaryPath, "coverage summary", cancellationToken);
            return new CoverageRunMergeResult(0, coberturaPath, summaryPath);
        }
    }
}
