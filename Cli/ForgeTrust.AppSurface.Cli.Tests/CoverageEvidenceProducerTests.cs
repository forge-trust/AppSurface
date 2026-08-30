using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageEvidenceProducerTests
{
    [Fact]
    public void Constructor_ShouldRejectANullExecutionWorkflow()
    {
        Assert.Throws<ArgumentNullException>(() => new CoverageEvidenceProducer(null!));
    }

    [Fact]
    public void EvidenceRunCommand_ShouldRejectANullCoverageProducer()
    {
        Assert.Throws<ArgumentNullException>(() => new EvidenceRunCommand(new EvidenceCliWorkflow(new EvidencePlanner()), null!));
    }

    [Fact]
    public async Task RunAsync_ShouldExplainUnavailableAndInvalidCoverageInputsWithoutStartingTheCore()
    {
        var producer = CreateProducer(new ThrowingCoverageRunProcessRunner(new InvalidOperationException("The core should not start.")));
        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();
        var writers = CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter);

        var unsupportedKind = await producer.RunAsync(
            CreateDeclaration(kind: "browser"),
            "sample.slnx",
            "output",
            null,
            writers,
            CancellationToken.None);
        var missingSolution = await producer.RunAsync(
            CreateDeclaration(),
            null,
            "output",
            null,
            writers,
            CancellationToken.None);
        var missingGate = await producer.RunAsync(
            CreateDeclaration(includeCoverageGate: false),
            "sample.slnx",
            "output",
            null,
            writers,
            CancellationToken.None);
        var missingPatch = await producer.RunAsync(
            CreateDeclaration(coverageGate: new EvidenceCoverageGateRequirements(95, 85, MinPatchLinePercent: 95)),
            "sample.slnx",
            "output",
            null,
            writers,
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, unsupportedKind.Outcome);
        Assert.Contains("explicit consumer EvidenceHost registration", unsupportedKind.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, missingSolution.Outcome);
        Assert.Contains("requires --solution", missingSolution.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(EvidenceProducerOutcome.Invalid, missingGate.Outcome);
        Assert.Contains("explicit coverageGate requirements", missingGate.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, missingPatch.Outcome);
        Assert.Contains("requires --diff-file", missingPatch.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ASCOV901 the private core failed.", "ASCOV901")]
    [InlineData("private core failed.", "CoverageExecutionException")]
    public async Task RunAsync_ShouldTranslateNonFatalCoreFailuresWithoutLeakingTheirDetail(string message, string expectedDiagnostic)
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        await File.WriteAllTextAsync(solutionPath, "{}");
        var producer = CreateProducer(new ThrowingCoverageRunProcessRunner(new CoverageExecutionException(message)));
        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();
        var writers = CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter);

        var result = await producer.RunAsync(
            CreateDeclaration(),
            solutionPath,
            Path.Join(directory.Path, "output"),
            null,
            writers,
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains(expectedDiagnostic, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(message, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldClassifyAnUncancelledCoreCancellationAsFailed()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        await File.WriteAllTextAsync(solutionPath, "{}");
        var producer = CreateProducer(new ThrowingCoverageRunProcessRunner(new OperationCanceledException("core cancellation")));
        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();

        var result = await producer.RunAsync(
            CreateDeclaration(),
            solutionPath,
            Path.Join(directory.Path, "output"),
            null,
            CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains("OperationCanceledException", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldPropagateCallerCancellation()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        await File.WriteAllTextAsync(solutionPath, "{}");
        var producer = CreateProducer(new ThrowingCoverageRunProcessRunner(new OperationCanceledException("core cancellation")));
        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer.RunAsync(
            CreateDeclaration(),
            solutionPath,
            Path.Join(directory.Path, "output"),
            null,
            CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter),
            cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_ShouldNotTranslateTerminalRuntimeFailures()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        await File.WriteAllTextAsync(solutionPath, "{}");
        var terminalFailures = new Exception[]
        {
            new OutOfMemoryException("memory exhausted"),
            new StackOverflowException("stack exhausted"),
            new AccessViolationException("invalid memory access"),
            new AppDomainUnloadedException("application domain unloaded"),
        };

        foreach (var failure in terminalFailures)
        {
            var producer = CreateProducer(new ThrowingCoverageRunProcessRunner(failure));
            using var standardOutputWriter = new StringWriter();
            using var standardErrorWriter = new StringWriter();
            var observed = await Record.ExceptionAsync(() => producer.RunAsync(
                CreateDeclaration(),
                solutionPath,
                Path.Join(directory.Path, Guid.NewGuid().ToString("N")),
                null,
                CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter),
                CancellationToken.None));

            Assert.Same(failure, observed);
        }
    }

    [Fact]
    public async Task RunAsync_ShouldBuildCodeCovPatchGateFromThePlanningSnapshot()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        var projectPath = Path.Join(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await File.WriteAllTextAsync(solutionPath, "{}");
        await File.WriteAllTextAsync(projectPath, "<Project />");
        var diffBytes = Encoding.UTF8.GetBytes("--- a/src/Feature.cs\n+++ b/src/Feature.cs\n@@ -0,0 +1 @@\n+public sealed class Feature;\n");
        var snapshot = new EvidenceDiffSnapshot(diffBytes, "feature.diff", Convert.ToHexString(SHA256.HashData(diffBytes)));
        var producer = CreateProducer(new PassingCoverageRunProcessRunner());
        var outputDirectory = Path.Join(directory.Path, "output");
        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();

        var result = await producer.RunAsync(
            CreateDeclaration(coverageGate: new EvidenceCoverageGateRequirements(95, 85, PatchLineMode: "codecov", TolerancePercent: 0)),
            solutionPath,
            outputDirectory,
            snapshot,
            CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Contains("Coverage gate passed", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("Patch line mode: codecov", await File.ReadAllTextAsync(Path.Join(outputDirectory, "coverage", "coverage-gate.md")), StringComparison.Ordinal);
    }

    private static CoverageEvidenceProducer CreateProducer(ICoverageRunProcessRunner processRunner) => new(
        new CoverageEvidenceExecutionWorkflow(
            new CoverageRunWorkflow(processRunner, new PassingCoverageReportGenerator(), TimeProvider.System)));

    private static EvidenceProducerDeclaration CreateDeclaration(
        string kind = "coverage",
        EvidenceCoverageGateRequirements? coverageGate = null,
        bool includeCoverageGate = true) => new(
        "coverage",
        kind,
        "1.0.0",
        [],
        ["coverage/assertion@1"],
        [],
        60,
        includeCoverageGate ? coverageGate ?? new EvidenceCoverageGateRequirements(95, 85, TolerancePercent: 0) : null);

    private sealed class ThrowingCoverageRunProcessRunner(Exception exception) : ICoverageRunProcessRunner
    {
        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            return Task.FromException<CoverageRunProcessResult>(exception);
        }
    }

    private sealed class PassingCoverageRunProcessRunner : ICoverageRunProcessRunner
    {
        private const string CapabilityOutput = """
            {
              "Properties": { "TestingPlatformDotnetTestSupport": "false", "TargetFramework": "net10.0" },
              "Items": { "PackageReference": [{ "Identity": "coverlet.collector" }] }
            }
            """;

        private const string Cobertura = "<coverage lines-covered=\"10\" lines-valid=\"10\" branches-covered=\"10\" branches-valid=\"10\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";

        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            var operation = request.Arguments.FirstOrDefault();
            if (string.Equals(operation, "sln", StringComparison.Ordinal))
            {
                return new CoverageRunProcessResult(0, "Project(s)\n----------\ntests/Sample.Tests/Sample.Tests.csproj\n");
            }

            if (string.Equals(operation, "msbuild", StringComparison.Ordinal))
            {
                return new CoverageRunProcessResult(0, CapabilityOutput, StandardOutput: CapabilityOutput);
            }

            if (string.Equals(operation, "test", StringComparison.Ordinal))
            {
                var resultsIndex = request.Arguments.ToList().FindIndex(argument => string.Equals(argument, "--results-directory", StringComparison.Ordinal));
                Assert.True(resultsIndex >= 0);
                var collectorDirectory = Path.Join(request.Arguments[resultsIndex + 1], "collector");
                Directory.CreateDirectory(collectorDirectory);
                await File.WriteAllTextAsync(Path.Join(collectorDirectory, "coverage.cobertura.xml"), Cobertura, cancellationToken);
                if (request.OutputFile is not null)
                {
                    await File.WriteAllTextAsync(request.OutputFile, "coverage test output", cancellationToken);
                }

                return new CoverageRunProcessResult(0, "coverage test output");
            }

            return new CoverageRunProcessResult(0, "build output");
        }
    }

    private sealed class PassingCoverageReportGenerator : ICoverageRunReportGenerator
    {
        private const string Cobertura = "<coverage lines-covered=\"10\" lines-valid=\"10\" branches-covered=\"10\" branches-valid=\"10\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";

        public async Task<CoverageRunMergeResult> MergeAsync(
            IReadOnlyList<string> coverageFiles,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDirectory);
            var coberturaPath = Path.Join(outputDirectory, "Cobertura.xml");
            var summaryPath = Path.Join(outputDirectory, "Summary.txt");
            await File.WriteAllTextAsync(coberturaPath, Cobertura, cancellationToken);
            await File.WriteAllTextAsync(summaryPath, "coverage summary", cancellationToken);
            return new CoverageRunMergeResult(0, coberturaPath, summaryPath);
        }
    }
}
