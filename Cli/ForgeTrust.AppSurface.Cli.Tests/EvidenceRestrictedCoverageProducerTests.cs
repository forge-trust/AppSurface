using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceRestrictedCoverageProducerTests
{
    private const string AssertionId = "appsurface/coverage/behavioral-patch@1";
    private const string PassingCobertura = "<coverage lines-covered=\"100\" lines-valid=\"100\" branches-covered=\"100\" branches-valid=\"100\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";
    private const string FailingCobertura = "<coverage lines-covered=\"5\" lines-valid=\"10\" branches-covered=\"10\" branches-valid=\"10\" line-rate=\"0.5\" branch-rate=\"1\"><packages /></coverage>";

    [Fact]
    public async Task RunAsync_ShouldMergeNumericCoverageAndWriteOnlyDeclaredArtifactSlots()
    {
        using var directory = TestDirectory.Create();
        var transport = new FakeRestrictedRun([Report(PassingCobertura)]);
        var reporter = new CopyingReportGenerator();
        var producer = CreateProducer(transport, reporter);
        var declaration = CreateDeclaration(
        [
            new EvidenceArtifactSlot("coverage-report", "coverage/merged", "application/xml", Required: true, MaximumBytes: 1024 * 1024),
            new EvidenceArtifactSlot("coverage-summary", "coverage", "text/markdown", Required: true, MaximumBytes: 1024 * 1024),
            new EvidenceArtifactSlot("coverage-gate", "coverage", "application/json", Required: true, MaximumBytes: 1024 * 1024),
        ]);
        var artifactsDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts");
        var writer = new EvidenceArtifactWriter(declaration, artifactsDirectory);

        var result = await producer.RunAsync(declaration, Path.Join(directory.Path, "subject.slnx"), null, writer, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(new[] { AssertionId }, result.SatisfiedAssertionIds);
        Assert.Equal(1, transport.RunCount);
        Assert.Equal(1, transport.CollectCount);
        Assert.StartsWith("coverage-", transport.ResultsToken);
        Assert.Equal(1, reporter.MergeCount);
        Assert.Equal(3, result.Artifacts!.Count);
        Assert.All(result.Artifacts, artifact =>
        {
            var artifactPath = TestPathUtils.PathUnder(artifactsDirectory, artifact.RelativePath);
            Assert.Equal(artifact.LengthBytes, new FileInfo(artifactPath).Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant(), artifact.Sha256);
        });
        Assert.Contains(result.Artifacts, artifact => artifact.LogicalName == "coverage-report" && artifact.RelativePath == "coverage/merged/coverage.cobertura.xml");
        Assert.Contains(result.Artifacts, artifact => artifact.LogicalName == "coverage-gate" && artifact.MediaType == "application/json");
        var summary = result.Artifacts.Single(artifact => artifact.LogicalName == "coverage-summary");
        Assert.Equal("text/markdown", summary.MediaType);
        var summaryText = await File.ReadAllTextAsync(TestPathUtils.PathUnder(artifactsDirectory, summary.RelativePath));
        Assert.Contains("# Coverage Gate: PASS", summaryText, StringComparison.Ordinal);
        Assert.Contains("| Lines | 100.00% (100/100) | 95% | pass |", summaryText, StringComparison.Ordinal);
        Assert.Contains("| Branches | 100.00% (100/100) | 85% | pass |", summaryText, StringComparison.Ordinal);
        var gate = result.Artifacts.Single(artifact => artifact.LogicalName == "coverage-gate");
        Assert.Contains("\"passed\": true", await File.ReadAllTextAsync(TestPathUtils.PathUnder(artifactsDirectory, gate.RelativePath)), StringComparison.Ordinal);
        Assert.DoesNotContain("subject.slnx", string.Join("\n", reporter.InputFiles));
    }

    [Fact]
    public async Task RunAsync_ShouldPassNumericGateWithoutArtifactsWhenSampleDeclarationHasNoSlots()
    {
        using var directory = TestDirectory.Create();
        var declaration = CreateDeclaration([]);
        var artifactsDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts");
        var writer = new EvidenceArtifactWriter(declaration, artifactsDirectory);
        var producer = CreateProducer(new FakeRestrictedRun([Report(PassingCobertura)]), new CopyingReportGenerator());

        var result = await producer.RunAsync(declaration, Path.Join(directory.Path, "subject.slnx"), null, writer, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(new[] { AssertionId }, result.SatisfiedAssertionIds);
        Assert.Empty(result.Artifacts!);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(17, false)]
    [InlineData(17, true)]
    public async Task RunAsync_ShouldRejectTruncatedOutputOrFailedSubjectExitBeforeCollecting(int exitCode, bool truncated)
    {
        using var directory = TestDirectory.Create();
        var transport = new FakeRestrictedRun([Report(PassingCobertura)])
        {
            ProcessResult = new EvidenceRestrictedProcessResult(exitCode, "secret stdout", "secret stderr", truncated),
        };
        var producer = CreateProducer(transport, new CopyingReportGenerator());
        var declaration = CreateDeclaration([]);

        var result = await producer.RunAsync(declaration, Path.Join(directory.Path, "subject.slnx"), null,
            new EvidenceArtifactWriter(declaration, Path.Join(directory.Path, "artifacts")), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(0, transport.CollectCount);
        Assert.DoesNotContain("secret", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldTreatNumericGateFailureAsFailedDespitePassingSubjectText()
    {
        using var directory = TestDirectory.Create();
        var transport = new FakeRestrictedRun([Report(FailingCobertura)])
        {
            ProcessResult = new EvidenceRestrictedProcessResult(0, "Tests passed", string.Empty, false),
        };
        var declaration = CreateDeclaration(
        [new EvidenceArtifactSlot("coverage-gate", "coverage", "application/json", Required: true, MaximumBytes: 1024 * 1024)]);
        var artifactsDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts");
        var writer = new EvidenceArtifactWriter(declaration, artifactsDirectory);
        var producer = CreateProducer(transport, new CopyingReportGenerator());

        var result = await producer.RunAsync(declaration, Path.Join(directory.Path, "subject.slnx"), null, writer, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        var gatePath = TestPathUtils.PathUnder(artifactsDirectory, result.Artifacts!.Single().RelativePath);
        Assert.Contains("\"passed\": false", await File.ReadAllTextAsync(gatePath), StringComparison.Ordinal);
        Assert.Contains("Coverage gate failed", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectMissingInputsAndUnsupportedDeclarationsBeforeStartingTransport()
    {
        using var directory = TestDirectory.Create();
        var transport = new FakeRestrictedRun([Report(PassingCobertura)]);
        var producer = CreateProducer(transport, new CopyingReportGenerator());
        var declaration = CreateDeclaration([]);
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(directory.Path, "artifacts"));

        var missingSolution = await producer.RunAsync(declaration, null, null, writer, CancellationToken.None);
        var unsupportedKind = await producer.RunAsync(declaration with { Kind = "browser-e2e" }, "subject.slnx", null, writer, CancellationToken.None);
        var missingGate = await producer.RunAsync(declaration with { CoverageGate = null }, "subject.slnx", null, writer, CancellationToken.None);
        var missingDiff = await producer.RunAsync(
            declaration with { CoverageGate = new EvidenceCoverageGateRequirements(95, 85, MinPatchLinePercent: 90) },
            "subject.slnx", null, writer, CancellationToken.None);
        var extraAssertion = await producer.RunAsync(
            declaration with { AssertionIds = [AssertionId, "spoofed/assertion@1"] },
            "subject.slnx", null, writer, CancellationToken.None);
        var wrongVersion = await producer.RunAsync(declaration with { Version = "2.0.0" }, "subject.slnx", null, writer, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, unsupportedKind.Outcome);
        Assert.All(new[] { missingSolution, missingGate, missingDiff, extraAssertion, wrongVersion },
            result => Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome));
        Assert.Equal(0, transport.RunCount);
    }

    [Theory]
    [InlineData("<coverage")]
    [InlineData("<!DOCTYPE coverage [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><coverage>&x;</coverage>")]
    public async Task RunAsync_ShouldRejectMalformedOrDtdBearingReportsBeforeMerge(string xml)
    {
        using var directory = TestDirectory.Create();
        var reporter = new CopyingReportGenerator();
        var declaration = CreateDeclaration([]);
        var result = await CreateProducer(new FakeRestrictedRun([Report(xml)]), reporter).RunAsync(
            declaration, Path.Join(directory.Path, "subject.slnx"), null,
            new EvidenceArtifactWriter(declaration, Path.Join(directory.Path, "artifacts")), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Equal(0, reporter.MergeCount);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectTraversalDuplicateAndOverLimitReports()
    {
        using var directory = TestDirectory.Create();
        var declaration = CreateDeclaration([]);
        var traversal = await RunWithReportsAsync(declaration, directory.Path,
            [new RestrictedCoverageReport("../coverage.cobertura.xml", Encoding.UTF8.GetBytes(PassingCobertura))]);
        var duplicate = await RunWithReportsAsync(declaration, directory.Path,
            [Report(PassingCobertura), Report(PassingCobertura)]);
        var tooMany = await RunWithReportsAsync(declaration, directory.Path,
            Enumerable.Range(0, 65).Select(index => Report(PassingCobertura, $"shard-{index:D2}/coverage.cobertura.xml")).ToArray());
        var tooLarge = await RunWithReportsAsync(declaration, directory.Path,
            [new RestrictedCoverageReport("shard/coverage.cobertura.xml", new byte[(20 * 1024 * 1024) + 1])]);
        var aggregateBytes = new byte[(20 * 1024 * 1024) - 1024];
        Array.Fill(aggregateBytes, (byte)' ');
        Encoding.UTF8.GetBytes(PassingCobertura).CopyTo(aggregateBytes, 0);
        var aggregateReports = Enumerable.Range(0, 13)
            .Select(index => new RestrictedCoverageReport(
                $"shard-{index:D2}/coverage.cobertura.xml",
                new ReadOnlyMemory<byte>(aggregateBytes)))
            .ToArray();
        var aggregateTooLarge = await RunWithReportsAsync(declaration, directory.Path, aggregateReports);

        Assert.All(new[] { traversal, duplicate, tooMany, tooLarge, aggregateTooLarge },
            result => Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome));
        Assert.Contains("path is unsafe", traversal.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("unique normalized Cobertura paths", duplicate.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("report count exceeds", tooMany.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("byte limit", tooLarge.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("byte limit", aggregateTooLarge.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectAnEmptyReportCollectionWithoutInvokingTheMergeCore()
    {
        using var directory = TestDirectory.Create();
        var reporter = new CopyingReportGenerator();
        var declaration = CreateDeclaration([]);
        var result = await CreateProducer(new FakeRestrictedRun([]), reporter).RunAsync(
            declaration,
            Path.Join(directory.Path, "subject.slnx"),
            null,
            new EvidenceArtifactWriter(declaration, Path.Join(directory.Path, "artifacts")),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains("produced no Cobertura reports", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, reporter.MergeCount);
    }

    private static async Task<EvidenceProducerResult> RunWithReportsAsync(
        EvidenceProducerDeclaration declaration,
        string root,
        IReadOnlyList<RestrictedCoverageReport> reports)
        => await CreateProducer(new FakeRestrictedRun(reports), new CopyingReportGenerator()).RunAsync(
            declaration,
            Path.Join(root, Guid.NewGuid().ToString("N") + ".slnx"),
            null,
            new EvidenceArtifactWriter(declaration, Path.Join(root, Guid.NewGuid().ToString("N"))),
            CancellationToken.None);

    private static EvidenceRestrictedCoverageProducer CreateProducer(FakeRestrictedRun transport, CopyingReportGenerator reporter)
        => new(transport, new CoverageMergeWorkflow(reporter, TimeProvider.System));

    private static EvidenceProducerDeclaration CreateDeclaration(IReadOnlyList<EvidenceArtifactSlot> slots)
        => new("coverage", "coverage", "1.0.0", [], [AssertionId], slots, 60,
            new EvidenceCoverageGateRequirements(95, 85, TolerancePercent: 0));

    private static RestrictedCoverageReport Report(string xml, string relativePath = "test/coverage.cobertura.xml")
        => new(relativePath, Encoding.UTF8.GetBytes(xml));

    private sealed class FakeRestrictedRun(IReadOnlyList<RestrictedCoverageReport> reports) : IEvidenceRestrictedCoverageRun
    {
        public EvidenceRestrictedProcessResult ProcessResult { get; init; } = new(0, string.Empty, string.Empty, false);
        public int RunCount { get; private set; }
        public int CollectCount { get; private set; }
        public string ResultsToken { get; private set; } = string.Empty;

        public Task<EvidenceRestrictedProcessResult> RunAsync(string solutionPath, string resultsToken, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCount++;
            ResultsToken = resultsToken;
            return Task.FromResult(ProcessResult);
        }

        public Task<IReadOnlyList<RestrictedCoverageReport>> CollectReportsAsync(string resultsToken, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectCount++;
            Assert.Equal(ResultsToken, resultsToken);
            return Task.FromResult(reports);
        }
    }

    private sealed class CopyingReportGenerator : ICoverageRunReportGenerator
    {
        public int MergeCount { get; private set; }
        public List<string> InputFiles { get; } = [];

        public async Task<CoverageRunMergeResult> MergeAsync(
            IReadOnlyList<string> coverageFiles,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            MergeCount++;
            var glob = Assert.Single(coverageFiles);
            var wildcardMarker = Path.DirectorySeparatorChar + "**" + Path.DirectorySeparatorChar;
            var wildcardIndex = glob.IndexOf(wildcardMarker, StringComparison.Ordinal);
            Assert.True(wildcardIndex > 0, "Expected the shared merge workflow's recursive report glob.");
            var inputRoot = glob[..wildcardIndex];
            var inputs = Directory.GetFiles(inputRoot, "coverage.cobertura.xml", SearchOption.AllDirectories);
            Assert.NotEmpty(inputs);
            InputFiles.AddRange(inputs);
            Directory.CreateDirectory(outputDirectory);
            var coberturaPath = Path.Join(outputDirectory, "Cobertura.xml");
            var summaryPath = Path.Join(outputDirectory, "Summary.txt");
            await File.WriteAllTextAsync(coberturaPath, await File.ReadAllTextAsync(inputs[0], cancellationToken), cancellationToken);
            await File.WriteAllTextAsync(summaryPath, "Lines: 100%", cancellationToken);
            return new CoverageRunMergeResult(0, coberturaPath, summaryPath);
        }
    }
}
