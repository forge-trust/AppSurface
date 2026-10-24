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
    public async Task RunAsync_ShouldRejectAnUnsupportedExecutionResultFormatBeforeStartingTheCore()
    {
        using var directory = TestDirectory.Create();
        var output = Path.Join(directory.Path, "output");
        var producer = CreateProducer(new ThrowingCoverageRunProcessRunner(new InvalidOperationException("core must not start")));
        var declaration = CreateDeclaration() with
        {
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: (EvidenceCoverageTestResultFormat)123),
        };

        var result = await producer.RunAsync(declaration, Path.Join(directory.Path, "sample.slnx"), output, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains("ArgumentOutOfRangeException", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("core must not start", result.Diagnostic, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

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
    public async Task RunAsync_ProducerDeadline_ShouldKeepTimedOutOutcomeAndPointToOwnedTerminalTimings()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = TestPathUtils.PathUnder(directory.Path, "sample.slnx");
        var projectPath = TestPathUtils.PathUnder(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await File.WriteAllTextAsync(solutionPath, "{}");
        await File.WriteAllTextAsync(projectPath, "<Project />");
        var outputDirectory = TestPathUtils.PathUnder(directory.Path, "output");
        var producer = CreateProducer(new DeadlineCoverageRunProcessRunner());
        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();

        var result = await producer.RunAsync(
            CreateDeclaration(timeoutSeconds: 3),
            solutionPath,
            outputDirectory,
            null,
            CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.TimedOut, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains("coverage/timings.json", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(File.Exists(TestPathUtils.PathUnder(outputDirectory, "coverage", "timings.json")));
    }

    [Fact]
    public async Task RunAsync_ProducerDeadlineBeforeOutput_ShouldOmitTimingsHint()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = TestPathUtils.PathUnder(directory.Path, "sample.slnx");
        var projectPath = TestPathUtils.PathUnder(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await File.WriteAllTextAsync(solutionPath, "{}");
        await File.WriteAllTextAsync(projectPath, "<Project />");
        var outputDirectory = TestPathUtils.PathUnder(directory.Path, "output");
        var producer = CreateProducer(new DeadlineCoverageRunProcessRunner(waitOperation: "msbuild"));
        using var standardOutputWriter = new StringWriter();
        using var standardErrorWriter = new StringWriter();

        var result = await producer.RunAsync(
            CreateDeclaration(timeoutSeconds: 1),
            solutionPath,
            outputDirectory,
            null,
            CoverageTextWriters.Create(standardOutputWriter, standardErrorWriter),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.TimedOut, result.Outcome);
        Assert.DoesNotContain("coverage/timings.json", result.Diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(TestPathUtils.PathUnder(outputDirectory, "coverage", "timings.json")));
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

    [Fact]
    public async Task RunAsync_ShouldForwardConfiguredExecutionWithoutChangingCoverageScope()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        await File.WriteAllTextAsync(solutionPath, "{}");
        foreach (var project in new[] { "Sample1.Tests", "Sample2.Tests" })
        {
            var projectPath = Path.Join(directory.Path, "tests", project, $"{project}.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
            await File.WriteAllTextAsync(projectPath, "<Project />");
        }
        var processRunner = new PassingCoverageRunProcessRunner(projectCount: 2);
        var producer = CreateProducer(processRunner);
        var declaration = CreateDeclaration() with
        {
            CoverageExecution = new EvidenceCoverageExecutionOptions(
                Configuration: "Release",
                TestResults: EvidenceCoverageTestResultFormat.Junit,
                Parallelism: 2),
        };

        var result = await producer.RunAsync(
            declaration,
            solutionPath,
            Path.Join(directory.Path, "output"),
            null,
            CoverageTextWriters.Create(new StringWriter(), new StringWriter()),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(2, processRunner.MaxConcurrentTests);
        Assert.Equal(2, processRunner.TestArguments.Count);
        Assert.All(processRunner.TestArguments, arguments =>
        {
            Assert.Contains("--configuration", arguments);
            Assert.Contains("Release", arguments);
            Assert.Contains(arguments, argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal));
            Assert.DoesNotContain("--filter", arguments);
            Assert.Contains(arguments, argument => argument.Contains("Exclude=", StringComparison.Ordinal)
                && argument.Contains("[*.Tests]*", StringComparison.Ordinal)
                && argument.Contains("[*.IntegrationTests]*", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task RunAsync_ShouldPreserveLegacyExecutionDefaults()
    {
        using var directory = TestDirectory.Create();
        var solutionPath = Path.Join(directory.Path, "sample.slnx");
        await File.WriteAllTextAsync(solutionPath, "{}");
        var projectPath = Path.Join(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await File.WriteAllTextAsync(projectPath, "<Project />");
        var processRunner = new PassingCoverageRunProcessRunner();
        var producer = CreateProducer(processRunner);

        var result = await producer.RunAsync(
            CreateDeclaration(),
            solutionPath,
            Path.Join(directory.Path, "output"),
            null,
            CoverageTextWriters.Create(new StringWriter(), new StringWriter()),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Single(processRunner.TestArguments);
        Assert.Contains("Debug", processRunner.TestArguments[0]);
        Assert.DoesNotContain(processRunner.TestArguments[0], argument => argument.StartsWith("--logger:junit;", StringComparison.Ordinal));
    }

    private static CoverageEvidenceProducer CreateProducer(ICoverageRunProcessRunner processRunner) => new(
        new CoverageEvidenceExecutionWorkflow(
            new CoverageRunWorkflow(processRunner, new PassingCoverageReportGenerator(), TimeProvider.System)));

    private static EvidenceProducerDeclaration CreateDeclaration(
        string kind = "coverage",
        EvidenceCoverageGateRequirements? coverageGate = null,
        bool includeCoverageGate = true,
        int timeoutSeconds = 60) => new(
        "coverage",
        kind,
        "1.0.0",
        [],
        ["coverage/assertion@1"],
        [],
        timeoutSeconds,
        includeCoverageGate ? coverageGate ?? new EvidenceCoverageGateRequirements(95, 85, TolerancePercent: 0) : null);

    private sealed class ThrowingCoverageRunProcessRunner(Exception exception) : ICoverageRunProcessRunner
    {
        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            return Task.FromException<CoverageRunProcessResult>(exception);
        }
    }

    private sealed class DeadlineCoverageRunProcessRunner(string waitOperation = "test") : ICoverageRunProcessRunner
    {
        private const string CapabilityOutput = """
            {
              "Properties": { "TestingPlatformDotnetTestSupport": "false", "TargetFramework": "net10.0" },
              "Items": { "PackageReference": [{ "Identity": "coverlet.collector" }] }
            }
            """;

        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            if (request.Arguments.FirstOrDefault() == waitOperation)
            {
                return await WaitForDeadlineAsync(cancellationToken);
            }

            return request.Arguments.FirstOrDefault() switch
            {
                "sln" => new CoverageRunProcessResult(0, "Project(s)\n----------\ntests/Sample.Tests/Sample.Tests.csproj\n"),
                "msbuild" => new CoverageRunProcessResult(0, CapabilityOutput, StandardOutput: CapabilityOutput),
                _ => new CoverageRunProcessResult(0, "build output"),
            };
        }

        private static async Task<CoverageRunProcessResult> WaitForDeadlineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The producer deadline should cancel the test invocation.");
        }
    }

    private sealed class PassingCoverageRunProcessRunner(int projectCount = 1) : ICoverageRunProcessRunner
    {
        private const string CapabilityOutput = """
            {
              "Properties": { "TestingPlatformDotnetTestSupport": "false", "TargetFramework": "net10.0" },
              "Items": { "PackageReference": [{ "Identity": "coverlet.collector" }] }
            }
            """;

        private const string Cobertura = "<coverage lines-covered=\"10\" lines-valid=\"10\" branches-covered=\"10\" branches-valid=\"10\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";

        private int _activeTests;
        private int _maxConcurrentTests;

        public List<IReadOnlyList<string>> TestArguments { get; } = [];

        public int MaxConcurrentTests => _maxConcurrentTests;

        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            var operation = request.Arguments.FirstOrDefault();
            if (string.Equals(operation, "sln", StringComparison.Ordinal))
            {
                var projects = projectCount == 1
                    ? "tests/Sample.Tests/Sample.Tests.csproj"
                    : string.Join(Environment.NewLine, Enumerable.Range(1, projectCount).Select(index => $"tests/Sample{index}.Tests/Sample{index}.Tests.csproj"));
                return new CoverageRunProcessResult(0, $"Project(s)\n----------\n{projects}\n");
            }

            if (string.Equals(operation, "msbuild", StringComparison.Ordinal))
            {
                return new CoverageRunProcessResult(0, CapabilityOutput, StandardOutput: CapabilityOutput);
            }

            if (string.Equals(operation, "test", StringComparison.Ordinal))
            {
                lock (TestArguments)
                {
                    TestArguments.Add(request.Arguments);
                }
                var activeTests = Interlocked.Increment(ref _activeTests);
                InterlockedExtensions.Max(ref _maxConcurrentTests, activeTests);
                var resultsIndex = request.Arguments.ToList().FindIndex(argument => string.Equals(argument, "--results-directory", StringComparison.Ordinal));
                Assert.True(resultsIndex >= 0);
                var collectorDirectory = Path.Join(request.Arguments[resultsIndex + 1], "collector");
                Directory.CreateDirectory(collectorDirectory);
                try
                {
                    if (projectCount > 1)
                    {
                        await Task.Delay(100, cancellationToken);
                    }

                    await File.WriteAllTextAsync(Path.Join(collectorDirectory, "coverage.cobertura.xml"), Cobertura, cancellationToken);
                    if (request.OutputFile is not null)
                    {
                        await File.WriteAllTextAsync(request.OutputFile, "coverage test output", cancellationToken);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeTests);
                }

                return new CoverageRunProcessResult(0, "coverage test output");
            }

            return new CoverageRunProcessResult(0, "build output");
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (current >= value || Interlocked.CompareExchange(ref location, value, current) == current)
                {
                    return;
                }
            }
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
