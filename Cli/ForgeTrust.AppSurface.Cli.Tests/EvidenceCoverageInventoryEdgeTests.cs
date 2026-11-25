using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceCoverageInventoryEdgeTests
{
    [Fact]
    public void EscapedUnicodeMetadataBudgetIsCheckedBeforeAnySourceFileIsOpened()
    {
        using var directory = TestDirectory.Create();
        var missingRoot = TestPathUtils.PathUnder(directory.Path, "not-created");
        var prefix = "coverage/" + string.Join("/", Enumerable.Repeat(new string('\u0800', 110), 30));
        var files = Enumerable.Range(0, CoverageEvidenceInventory.MaximumFiles)
            .Select(index => new CoverageReportFile($"{prefix}/{index:D3}", 0, new string('a', 64))).ToArray();
        var inventory = new CoverageEvidenceInventory(CoverageEvidenceInventory.CurrentVersion, files);

        var failure = Assert.Throws<CoverageEvidenceInventoryException>(() => inventory.Validate(missingRoot));

        Assert.Equal("inventory-metadata-limit", failure.Reason);
        Assert.False(Directory.Exists(missingRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public void ReceiptPathValidationRejectsEmptyAndNonportablePaths(int pathLength)
    {
        var path = pathLength switch
        {
            0 => "",
            -1 => "../outside.xml",
            _ => new string('x', pathLength)
        };

        var error = Assert.Throws<CoverageEvidenceInventoryException>(() => CoverageReportFile.ValidatePath(path));

        Assert.Equal("inventory-path-invalid", error.Reason);
    }

    [Theory]
    [InlineData("coverage/file.xml")]
    [InlineData("coverage/nested/report.json")]
    public void ReceiptPathValidationAcceptsCanonicalRelativePaths(string path)
        => CoverageReportFile.ValidatePath(path);

    [Fact]
    public void ReceiptPathValidationRejectsMoreThanThirtyTwoComponents()
    {
        var path = "coverage/" + string.Join("/", Enumerable.Repeat("segment", 32));

        var error = Assert.Throws<CoverageEvidenceInventoryException>(() => CoverageReportFile.ValidatePath(path));

        Assert.Equal("inventory-path-invalid", error.Reason);
    }

    [Fact]
    public async Task ClosedInventoryValidationAcceptsExactBytesAndRejectsMutationAndCancellation()
    {
        using var directory = TestDirectory.Create();
        var report = TestPathUtils.PathUnder(directory.Path, "coverage", "report.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var bytes = "<coverage />"u8.ToArray();
        await File.WriteAllBytesAsync(report, bytes);
        var file = new CoverageReportFile("coverage/report.xml", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var inventory = new CoverageEvidenceInventory(CoverageEvidenceInventory.CurrentVersion, [file]);

        inventory.Validate(directory.Path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => inventory.Validate(directory.Path, cancelled.Token));

        await File.WriteAllTextAsync(report, "changed");
        var error = Assert.Throws<CoverageEvidenceInventoryException>(() => inventory.Validate(directory.Path));
        Assert.Equal("inventory-file-changed", error.Reason);
    }

    [Fact]
    public void CaptureRefusesFailedCollectionBeforeConsideringPublicationFiles()
    {
        var coveragePath = TestPathUtils.PathUnder(Path.GetTempPath(), "coverage.cobertura.xml");
        var gate = new CoverageGateResult(coveragePath, new(1, 1, 100), new(1, 1, 100), 95, 85, true,
            "unused-root/coverage-gate.json", "unused-root/coverage-gate.md");
        var execution = new CoverageEvidenceExecutionResult(new CoverageRunResult(false, "unused-root", coveragePath), gate);

        var error = Assert.Throws<CoverageEvidenceInventoryException>(() =>
            CoverageEvidenceInventory.Capture("unused-root", execution, [], CancellationToken.None));

        Assert.Equal("inventory-result-not-passed", error.Reason);
    }

    [Fact]
    public void CaptureRefusesMissingGateAfterSuccessfulCollection()
    {
        var coveragePath = TestPathUtils.PathUnder(Path.GetTempPath(), "coverage.cobertura.xml");
        var execution = new CoverageEvidenceExecutionResult(new CoverageRunResult(true, "unused-root", coveragePath), null);

        var error = Assert.Throws<CoverageEvidenceInventoryException>(() =>
            CoverageEvidenceInventory.Capture("unused-root", execution, [], CancellationToken.None));

        Assert.Equal("inventory-result-not-passed", error.Reason);
    }

    [Fact]
    public async Task CaptureRejectsMissingRequiredSummaryWithoutChangingTheAvailableReports()
    {
        using var directory = TestDirectory.Create();
        var coverageRoot = TestPathUtils.PathUnder(directory.Path, "coverage");
        var reportContents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["coverage/coverage.cobertura.xml"] = "<coverage />",
            ["coverage/coverage-gate.json"] = "{}",
            ["coverage/coverage-gate.md"] = "passed",
            ["coverage/timings.json"] = "{\"projects\":[]}",
        };
        var publications = new List<CoverageArtifactInventoryEntry>();
        foreach (var (relativePath, contents) in reportContents)
        {
            var path = TestPathUtils.PathUnder(directory.Path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            await File.WriteAllBytesAsync(path, bytes);
            publications.Add(new(relativePath["coverage/".Length..], bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        var coveragePath = TestPathUtils.PathUnder(directory.Path, "coverage", "coverage.cobertura.xml");
        var gate = new CoverageGateResult(coveragePath, new(10, 10, 100), new(10, 10, 100), 95, 85, true,
            TestPathUtils.PathUnder(directory.Path, "coverage", "coverage-gate.json"),
            TestPathUtils.PathUnder(directory.Path, "coverage", "coverage-gate.md"));
        var result = new CoverageEvidenceExecutionResult(new CoverageRunResult(true, directory.Path, coveragePath), gate);

        var error = Assert.Throws<CoverageEvidenceInventoryException>(() =>
            CoverageEvidenceInventory.Capture(directory.Path, result, publications, CancellationToken.None));

        Assert.Equal("inventory-required-file-missing", error.Reason);
        Assert.False(File.Exists(TestPathUtils.PathUnder(coverageRoot, "summary.txt")));
        foreach (var (relativePath, contents) in reportContents)
            Assert.Equal(contents, await File.ReadAllTextAsync(TestPathUtils.PathUnder(directory.Path, relativePath)));
    }

    [Fact]
    public async Task OversizedReceiptSlotKeepsPassingFreshReportsAndLeavesCustodyEmpty()
    {
        using var directory = TestDirectory.Create();
        var solution = TestPathUtils.PathUnder(directory.Path, "sample.slnx");
        var project = TestPathUtils.PathUnder(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        await File.WriteAllTextAsync(solution, "solution fixture");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project />");
        var slot = CoverageEvidenceInventory.CreateSlot() with { MaximumBytes = 1 };
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [slot], 30, new EvidenceCoverageGateRequirements(95, 85))
        {
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit)
            { CooperativeChildLifetime = true },
        };
        var runRoot = TestPathUtils.PathUnder(directory.Path, "run");
        var custodyRoot = TestPathUtils.PathUnder(directory.Path, "custody");
        var finalReportRoot = TestPathUtils.PathUnder(directory.Path, "ordinary-reports");
        var writer = new EvidenceArtifactWriter(declaration, custodyRoot);
        var runner = new FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj");
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new FixtureReportGenerator(), TimeProvider.System)));
        await using var childLifetime = EvidenceChildLifetimeSession.Start("inventory-cap-" + Guid.NewGuid().ToString("N"));

        var execution = await adapter.RunWithInventoryAsync(declaration, solution, runRoot, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None, writer,
            childLifetime, finalReportRoot);

        Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
        Assert.Equal(new[] { "coverage/assertion@1" }, execution.Result.SatisfiedAssertionIds);
        Assert.Null(execution.Inventory);
        Assert.Null(execution.Result.Artifacts);
        Assert.Equal("inventory-slot-ineligible", execution.InventoryIneligibilityReason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(custodyRoot));
        foreach (var report in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
        {
            Assert.True(File.Exists(TestPathUtils.PathUnder(runRoot, "coverage", report)), report);
            Assert.True(File.Exists(TestPathUtils.PathUnder(finalReportRoot, "coverage", report)), report);
        }
        Assert.True(runner.TestInvocationCount > 0);
    }

    [Theory]
    [InlineData("root-file")]
    [InlineData("different-report-length")]
    [InlineData("different-report-digest")]
    public async Task UnusableOrdinaryReportRootFailsFreshPromotionButRetainsExecutionReports(string collision)
    {
        using var directory = TestDirectory.Create();
        var solution = TestPathUtils.PathUnder(directory.Path, "sample.slnx");
        var project = TestPathUtils.PathUnder(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        await File.WriteAllTextAsync(solution, "solution fixture");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project />");
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"], [], 30,
            new EvidenceCoverageGateRequirements(95, 85))
        {
            CoverageArtifactRetention = "managed",
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit),
        };
        var runRoot = TestPathUtils.PathUnder(directory.Path, "run");
        var finalRoot = TestPathUtils.PathUnder(directory.Path, "ordinary-reports");
        var occupied = collision == "root-file" ? finalRoot
            : TestPathUtils.PathUnder(finalRoot, "coverage", "summary.txt");
        var sentinel = collision == "different-report-digest" ? new string('x', "fixture summary".Length)
            : "occupied by a regular file";
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        await File.WriteAllTextAsync(occupied, sentinel);
        var runner = new FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj");
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new FixtureReportGenerator(), TimeProvider.System)));

        var execution = await adapter.RunWithInventoryAsync(declaration, solution, runRoot, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None,
            finalReportRoot: finalRoot);

        Assert.Equal(EvidenceProducerOutcome.Failed, execution.Result.Outcome);
        Assert.Empty(execution.Result.SatisfiedAssertionIds);
        Assert.Null(execution.Result.Artifacts);
        Assert.Null(execution.Inventory);
        Assert.Equal("inventory-result-not-passed", execution.InventoryIneligibilityReason);
        Assert.Contains("Ordinary report promotion is unavailable", execution.Result.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("Original reports remain in the execution output", execution.Result.Diagnostic!, StringComparison.Ordinal);
        Assert.Equal(sentinel, await File.ReadAllTextAsync(occupied));
        foreach (var report in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
            Assert.True(File.Exists(TestPathUtils.PathUnder(runRoot, "coverage", report)), report);
        Assert.True(runner.TestInvocationCount > 0);
    }

    [Fact]
    public async Task OrdinaryReportsAlreadyAtRunRootRemainPassedWithoutReceiptCustody()
    {
        using var directory = TestDirectory.Create();
        var solution = TestPathUtils.PathUnder(directory.Path, "sample.slnx");
        var project = TestPathUtils.PathUnder(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        await File.WriteAllTextAsync(solution, "solution fixture");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project />");
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"], [], 30,
            new EvidenceCoverageGateRequirements(95, 85))
        {
            CoverageArtifactRetention = "managed",
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit),
        };
        var runRoot = TestPathUtils.PathUnder(directory.Path, "run");
        var runner = new FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj");
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new FixtureReportGenerator(), TimeProvider.System)));

        var execution = await adapter.RunWithInventoryAsync(declaration, solution, runRoot, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None,
            finalReportRoot: runRoot);

        Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
        Assert.Equal(new[] { "coverage/assertion@1" }, execution.Result.SatisfiedAssertionIds);
        Assert.Null(execution.Result.Artifacts);
        Assert.Null(execution.Inventory);
        Assert.Equal("reuse.child_lifetime_required", execution.InventoryIneligibilityReason);
        Assert.Contains(runRoot, execution.Result.Diagnostic!, StringComparison.Ordinal);
        Assert.Equal(Cobertura, await File.ReadAllTextAsync(TestPathUtils.PathUnder(runRoot, "coverage", "coverage.cobertura.xml")));
        foreach (var report in new[] { "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
            Assert.True(File.Exists(TestPathUtils.PathUnder(runRoot, "coverage", report)), report);
        Assert.NotEmpty(await File.ReadAllTextAsync(TestPathUtils.PathUnder(runRoot, "coverage", "summary.txt")));
        Assert.True(runner.TestInvocationCount > 0);
    }

    [Theory]
    [InlineData(0, "inventory-version-unsupported")]
    [InlineData(2, "inventory-version-unsupported")]
    public void ClosedInventoryRejectsUnsupportedVersionBeforeReadingFiles(int version, string reason)
    {
        var inventory = new CoverageEvidenceInventory(version,
            [new CoverageReportFile("coverage/missing.xml", 0, new string('0', 64))]);

        var error = Assert.Throws<CoverageEvidenceInventoryException>(() => inventory.Validate(Path.GetTempPath()));

        Assert.Equal(reason, error.Reason);
    }

    [Theory]
    [InlineData("unsupported-kind", EvidenceProducerOutcome.Unavailable, "inventory-result-not-passed")]
    [InlineData("missing-solution", EvidenceProducerOutcome.Unavailable, "inventory-result-not-passed")]
    [InlineData("missing-gate", EvidenceProducerOutcome.Invalid, "inventory-result-not-passed")]
    [InlineData("missing-diff", EvidenceProducerOutcome.Unavailable, "inventory-result-not-passed")]
    [InlineData("invalid-retention", EvidenceProducerOutcome.Invalid, "inventory-result-not-passed")]
    public async Task CoverageAdapterKeepsFreshAdmissionRefusalsDistinctAndDoesNotStartCore(
        string refusal, EvidenceProducerOutcome outcome, string inventoryReason)
    {
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            new NeverRunProcess(), new NeverGenerateReport(), TimeProvider.System)));
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"], [], 30,
            new EvidenceCoverageGateRequirements(95, 85))
        { CoverageArtifactRetention = "managed" };
        string? solution = "sample.slnx";
        switch (refusal)
        {
            case "unsupported-kind": declaration = declaration with { Kind = "browser-e2e" }; break;
            case "missing-solution": solution = null; break;
            case "missing-gate": declaration = declaration with { CoverageGate = null }; break;
            case "missing-diff": declaration = declaration with { CoverageGate = new EvidenceCoverageGateRequirements(95, 85, MinPatchLinePercent: 95) }; break;
            case "invalid-retention": declaration = declaration with { CoverageArtifactRetention = "unknown" }; break;
        }

        var result = await adapter.RunWithInventoryAsync(declaration, solution, "unused-output", null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal(outcome, result.Result.Outcome);
        Assert.Empty(result.Result.SatisfiedAssertionIds);
        Assert.Null(result.Inventory);
        Assert.Equal(inventoryReason, result.InventoryIneligibilityReason);
        Assert.NotEmpty(result.Result.Diagnostic!);
    }

    [Fact]
    public async Task MissingChildLifetimeSuppressesOnlyReceiptCustodyAndPreservesFreshReports()
    {
        using var directory = TestDirectory.Create();
        var solution = TestPathUtils.PathUnder(directory.Path, "sample.slnx");
        var project = TestPathUtils.PathUnder(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(solution, "solution fixture");
        await File.WriteAllTextAsync(project, "<Project />");
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [CoverageEvidenceInventory.CreateSlot()], 30, new EvidenceCoverageGateRequirements(95, 85))
        {
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var runRoot = TestPathUtils.PathUnder(directory.Path, "run");
        var custodyRoot = TestPathUtils.PathUnder(directory.Path, "custody");
        var reportRoot = TestPathUtils.PathUnder(directory.Path, "ordinary-reports");
        var writer = new EvidenceArtifactWriter(declaration, custodyRoot);
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            new FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj"), new FixtureReportGenerator(), TimeProvider.System)));

        var execution = await adapter.RunWithInventoryAsync(declaration, solution, runRoot, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None, writer,
            childLifetime: null, finalReportRoot: reportRoot);

        Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
        Assert.Equal(new[] { "coverage/assertion@1" }, execution.Result.SatisfiedAssertionIds);
        Assert.Null(execution.Inventory);
        Assert.Equal("reuse.child_lifetime_session_required", execution.InventoryIneligibilityReason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(custodyRoot));
        foreach (var report in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
            Assert.True(File.Exists(TestPathUtils.PathUnder(reportRoot, "coverage", report)), report);
        Assert.True(File.Exists(TestPathUtils.PathUnder(runRoot, "coverage", "coverage-gate.json")));
    }

    private sealed class NeverRunProcess : ICoverageRunProcessRunner
    {
        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Admission refusal must happen before process execution.");
    }

    private sealed class NeverGenerateReport : ICoverageRunReportGenerator
    {
        public Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles, string outputDirectory, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Admission refusal must happen before report generation.");
    }

    private const string Cobertura = "<coverage lines-covered=\"10\" lines-valid=\"10\" branches-covered=\"10\" branches-valid=\"10\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";

    internal sealed class FixtureProcessRunner(string projectPath, Action? onTestInvocation = null) : ICoverageRunProcessRunner
    {
        internal int InvocationCount { get; private set; }
        internal int TestInvocationCount { get; private set; }

        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            InvocationCount++;
            request.Lease.Complete();
            if (request.Arguments[0] == "sln") return new(0, $"Project(s)\n----------\n{projectPath}\n");
            if (request.Arguments[0] == "msbuild")
            {
                const string capabilities = """{"Properties":{"TestingPlatformDotnetTestSupport":"false","TargetFramework":"net10.0"},"Items":{"PackageReference":[{"Identity":"coverlet.collector"}]}}""";
                return new(0, capabilities, StandardOutput: capabilities);
            }
            if (request.OutputFile is not null)
                await File.WriteAllTextAsync(request.OutputFile, "fixture process output", cancellationToken);
            if (request.Arguments[0] == "test")
            {
                TestInvocationCount++;
                onTestInvocation?.Invoke();
                var resultIndex = request.Arguments.ToList().IndexOf("--results-directory");
                var raw = TestPathUtils.PathUnder(request.Arguments[resultIndex + 1], "collector");
                Directory.CreateDirectory(raw);
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(raw, "coverage.cobertura.xml"), Cobertura, cancellationToken);
                var junitLogger = request.Arguments.FirstOrDefault(argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal));
                if (junitLogger is not null)
                {
                    var junitPath = junitLogger["--logger:junit;LogFilePath=".Length..];
                    Directory.CreateDirectory(Path.GetDirectoryName(junitPath)!);
                    await File.WriteAllTextAsync(junitPath, "<testsuites><testsuite tests=\"1\" /></testsuites>", cancellationToken);
                }
            }
            return new(0, "fixture output");
        }
    }

    internal sealed class FixtureReportGenerator : ICoverageRunReportGenerator
    {
        public async Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles, string outputDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDirectory);
            var cobertura = TestPathUtils.PathUnder(outputDirectory, "Cobertura.xml");
            var summary = TestPathUtils.PathUnder(outputDirectory, "Summary.txt");
            await File.WriteAllTextAsync(cobertura, Cobertura, cancellationToken);
            await File.WriteAllTextAsync(summary, "fixture summary", cancellationToken);
            return new(0, cobertura, summary);
        }
    }
}
