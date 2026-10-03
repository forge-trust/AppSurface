using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Procedure unit controls using internal fake transports; these do not establish Trusted or native acceptance.</summary>
public sealed class EvidenceRestrictedCoverageFailureTests
{
    private const string AssertionId = "appsurface/coverage/behavioral-patch@1";
    private const string PassingCobertura = "<coverage lines-covered=\"100\" lines-valid=\"100\" branches-covered=\"100\" branches-valid=\"100\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";
    private const string SecretMarker = "restricted-coverage-secret-canary-779";

    [Fact]
    public async Task RunAsync_ShouldPropagateCancellationFromReportCollection()
    {
        using var directory = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeRestrictedRun([Report(PassingCobertura)]);
        var reportGenerator = new RecordingReportGenerator();
        transport.CollectHandler = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<IReadOnlyList<RestrictedCoverageReport>>(token);
        };
        var declaration = CreateDeclaration([]);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProducer(transport, reportGenerator).RunAsync(
            declaration,
            TestPathUtils.PathUnder(directory.Path, "subject.slnx"),
            null,
            CreateWriter(directory.Path, declaration),
            cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, transport.RunCount);
        Assert.Equal(1, transport.CollectCount);
        Assert.Equal(0, reportGenerator.MergeCount);
        Assert.Equal(cancellation.Token, transport.RunCancellationToken);
        Assert.Equal(cancellation.Token, transport.CollectCancellationToken);
    }

    [Theory]
    [InlineData("transport-run")]
    [InlineData("report-merge")]
    public async Task RunAsync_ShouldSanitizeNonfatalTransportAndMergerExceptions(string stage)
    {
        using var directory = TestDirectory.Create();
        var transport = new FakeRestrictedRun([Report(PassingCobertura)]);
        var reportGenerator = new RecordingReportGenerator();
        var exception = new InvalidOperationException(SecretMarker);

        if (stage == "transport-run")
        {
            transport.RunHandler = _ => Task.FromException<EvidenceRestrictedProcessResult>(exception);
        }
        else
        {
            reportGenerator.MergeHandler = (_, _) => Task.FromException<CoverageRunMergeResult>(exception);
        }

        var declaration = CreateDeclaration([]);
        var producer = CreateProducer(transport, reportGenerator);
        var result = await producer.RunAsync(
            declaration,
            TestPathUtils.PathUnder(directory.Path, "subject.slnx"),
            null,
            CreateWriter(directory.Path, declaration),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains(nameof(InvalidOperationException), result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(result.SatisfiedAssertionIds);
        AssertNoArtifacts(result);
        Assert.Equal(stage == "report-merge" ? 1 : 0, reportGenerator.MergeCount);
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("missing-merged-file")]
    [InlineData("malformed-merged-cobertura")]
    public async Task RunAsync_ShouldFailWhenMergeDoesNotProduceUsableCobertura(string failure)
    {
        using var directory = TestDirectory.Create();
        var transport = new FakeRestrictedRun([Report(PassingCobertura)]);
        var reportGenerator = failure switch
        {
            "nonzero" => new RecordingReportGenerator { ExitCode = 23 },
            "missing-merged-file" => new RecordingReportGenerator { WriteCobertura = false },
            "malformed-merged-cobertura" => new RecordingReportGenerator { CoberturaContents = "<coverage" },
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var declaration = CreateDeclaration([]);
        var result = await CreateProducer(transport, reportGenerator).RunAsync(
            declaration,
            TestPathUtils.PathUnder(directory.Path, "subject.slnx"),
            null,
            CreateWriter(directory.Path, declaration),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains("CoverageExecutionException", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(result.SatisfiedAssertionIds);
        AssertNoArtifacts(result);
        Assert.Equal(1, transport.CollectCount);
        Assert.Equal(1, reportGenerator.MergeCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task RunAsync_ShouldMapProtectedDiffPathsAndApplyTheNumericPatchGate(int hits, bool shouldPass)
    {
        using var directory = TestDirectory.Create();
        Directory.CreateDirectory(TestPathUtils.PathUnder(directory.Path, ".git"));
        Directory.CreateDirectory(TestPathUtils.PathUnder(directory.Path, "src"));
        var solutionPath = TestPathUtils.PathUnder(directory.Path, "src", "subject.slnx");
        await File.WriteAllTextAsync(solutionPath, string.Empty);
        var changedFile = TestPathUtils.PathUnder(directory.Path, "src", "Changed.cs");
        var shard = CreateCoberturaWithFile(changedFile, hits);
        var transport = new FakeRestrictedRun([Report(shard)]);
        var reportGenerator = new RecordingReportGenerator { CoberturaContents = shard };
        var declaration = CreateDeclaration(
        [
            new EvidenceArtifactSlot("coverage-gate", "coverage", "application/json", Required: true, MaximumBytes: 1024 * 1024),
            new EvidenceArtifactSlot("coverage-patch-targets", "coverage", "application/json", Required: true, MaximumBytes: 1024 * 1024),
        ],
        new EvidenceCoverageGateRequirements(95, 85, MinPatchLinePercent: 100, TolerancePercent: 0));
        var diffBytes = Encoding.UTF8.GetBytes("""
            diff --git a/src/Changed.cs b/src/Changed.cs
            index 0000000..1111111 100644
            --- a/src/Changed.cs
            +++ b/src/Changed.cs
            @@ -0,0 +1,1 @@
            +changed
            """);
        var snapshot = new EvidenceRestrictedCoverageDiffSnapshot(
            diffBytes,
            "protected-change.diff",
            Convert.ToHexString(SHA256.HashData(diffBytes)).ToLowerInvariant());

        var result = await CreateProducer(transport, reportGenerator).RunAsync(
            declaration,
            solutionPath,
            snapshot,
            CreateWriter(directory.Path, declaration),
            CancellationToken.None);

        var expectedOutcome = shouldPass ? EvidenceProducerOutcome.Passed : EvidenceProducerOutcome.Failed;
        Assert.True(result.Outcome == expectedOutcome,
            $"Expected {expectedOutcome}, actual {result.Outcome}. Diagnostic: {result.Diagnostic}");
        if (shouldPass)
        {
            Assert.Equal(new[] { AssertionId }, result.SatisfiedAssertionIds);
        }
        else
        {
            Assert.Empty(result.SatisfiedAssertionIds);
        }

        Assert.True(result.Artifacts?.Any(artifact => artifact.LogicalName == "coverage-patch-targets") == true,
            $"Expected the declared patch targets artifact. Diagnostic: {result.Diagnostic}");
        var targets = result.Artifacts!.Single(artifact => artifact.LogicalName == "coverage-patch-targets");
        var targetsJson = await File.ReadAllTextAsync(TestPathUtils.PathUnder(
            TestPathUtils.PathUnder(directory.Path, "artifacts"), targets.RelativePath));
        using var targetsDocument = JsonDocument.Parse(targetsJson);
        var targetsRoot = targetsDocument.RootElement;
        var summary = targetsRoot.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("changed").GetInt32());
        Assert.Equal(1, summary.GetProperty("measurable").GetInt32());
        Assert.Equal("protected-change.diff", targetsRoot
            .GetProperty("patch")
            .GetProperty("diffSource")
            .GetProperty("label")
            .GetString());
        var targetLines = targetsRoot.GetProperty("targets").EnumerateArray().ToArray();
        if (shouldPass)
        {
            Assert.Equal(0, summary.GetProperty("targets").GetInt32());
            Assert.Empty(targetLines);
        }
        else
        {
            var targetLine = Assert.Single(targetLines);
            Assert.Equal("src/Changed.cs", targetLine.GetProperty("path").GetString());
            Assert.Equal(1, targetLine.GetProperty("line").GetInt32());
            Assert.False(targetLine.GetProperty("lineCovered").GetBoolean());
        }
    }

    [Fact]
    public async Task RunAsync_ShouldRejectANullReportCollectionBeforeMerging()
    {
        using var directory = TestDirectory.Create();
        var transport = new FakeRestrictedRun(null);
        transport.CollectHandler = _ => Task.FromResult<IReadOnlyList<RestrictedCoverageReport>>(null!);
        var reportGenerator = new RecordingReportGenerator();
        var declaration = CreateDeclaration([]);

        var result = await CreateProducer(transport, reportGenerator).RunAsync(
            declaration,
            TestPathUtils.PathUnder(directory.Path, "subject.slnx"),
            null,
            CreateWriter(directory.Path, declaration),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Contains("produced no Cobertura reports", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(1, transport.CollectCount);
        Assert.Equal(0, reportGenerator.MergeCount);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectARequiredArtifactWhoseDeclaredByteQuotaIsTooSmall()
    {
        using var directory = TestDirectory.Create();
        var transport = new FakeRestrictedRun([Report(PassingCobertura)]);
        var declaration = CreateDeclaration(
        [
            new EvidenceArtifactSlot("future-optional", "future", "application/octet-stream", Required: false, MaximumBytes: 1024),
            new EvidenceArtifactSlot("coverage-report", "coverage", "application/xml", Required: true, MaximumBytes: 1),
        ]);

        var result = await CreateProducer(transport, new RecordingReportGenerator()).RunAsync(
            declaration,
            TestPathUtils.PathUnder(directory.Path, "subject.slnx"),
            null,
            CreateWriter(directory.Path, declaration),
            CancellationToken.None);

        Assert.True(result.Outcome == EvidenceProducerOutcome.Invalid,
            $"Expected Invalid, actual {result.Outcome}. Diagnostic: {result.Diagnostic}");
        Assert.Contains("exceeds its declared byte limit", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(result.SatisfiedAssertionIds);
        AssertNoArtifacts(result);
    }


    [Theory]
    [InlineData("measurable", true)]
    [InlineData("codecov", false)]
    public async Task RunAsync_ShouldDistinguishHitPartialBranchLinesFromFullyCoveredPatchLines(
        string lineMode, bool shouldPass)
    {
        using var directory = TestDirectory.Create();
        Directory.CreateDirectory(TestPathUtils.PathUnder(directory.Path, ".git"));
        Directory.CreateDirectory(TestPathUtils.PathUnder(directory.Path, "src"));
        var solutionPath = TestPathUtils.PathUnder(directory.Path, "src", "subject.slnx");
        await File.WriteAllTextAsync(solutionPath, string.Empty);
        var changedFile = TestPathUtils.PathUnder(directory.Path, "src", "Changed.cs");
        var shard = CreateCoberturaWithPartialBranch(changedFile);
        var transport = new FakeRestrictedRun([Report(shard)]);
        var reporter = new RecordingReportGenerator { CoberturaContents = shard };
        var declaration = CreateDeclaration(
        [
            new EvidenceArtifactSlot("coverage-gate", "coverage", "application/json", Required: true, MaximumBytes: 1024 * 1024),
            new EvidenceArtifactSlot("coverage-patch-targets", "coverage", "application/json", Required: true, MaximumBytes: 1024 * 1024),
        ],
        new EvidenceCoverageGateRequirements(95, 85, MinPatchLinePercent: 100,
            TolerancePercent: 0, PatchLineMode: lineMode));
        var writer = CreateWriter(directory.Path, declaration);
        var diffBytes = Encoding.UTF8.GetBytes("""
            diff --git a/src/Changed.cs b/src/Changed.cs
            index 0000000..1111111 100644
            --- a/src/Changed.cs
            +++ b/src/Changed.cs
            @@ -0,0 +1,1 @@
            +return condition ? 1 : 0;
            """);
        var diffSha256 = Convert.ToHexString(SHA256.HashData(diffBytes)).ToLowerInvariant();
        var snapshot = new EvidenceRestrictedCoverageDiffSnapshot(diffBytes, "partial-branch.diff", diffSha256);

        var result = await CreateProducer(transport, reporter).RunAsync(
            declaration, solutionPath, snapshot, writer, CancellationToken.None);

        Assert.Equal(shouldPass ? EvidenceProducerOutcome.Passed : EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Equal(shouldPass ? new[] { AssertionId } : [], result.SatisfiedAssertionIds);
        Assert.Equal(1, transport.RunCount);
        Assert.Equal(1, transport.CollectCount);
        Assert.Equal(1, reporter.MergeCount);
        Assert.Equal(2, writer.WrittenArtifacts.Count);
        var artifactRoot = TestPathUtils.PathUnder(directory.Path, "artifacts");
        var gateArtifact = Assert.Single(writer.WrittenArtifacts, artifact => artifact.LogicalName == "coverage-gate");
        using var gateDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            TestPathUtils.PathUnder(artifactRoot, gateArtifact.RelativePath)));
        var gate = gateDocument.RootElement;
        Assert.Equal(shouldPass, gate.GetProperty("passed").GetBoolean());
        Assert.Equal(lineMode, gate.GetProperty("patchLineMode").GetString());
        Assert.Equal(95m, gate.GetProperty("thresholds").GetProperty("line").GetDecimal());
        Assert.Equal(85m, gate.GetProperty("thresholds").GetProperty("branch").GetDecimal());
        Assert.Equal(100m, gate.GetProperty("line").GetProperty("percent").GetDecimal());
        Assert.Equal(95m, gate.GetProperty("branch").GetProperty("percent").GetDecimal());
        var patchLine = gate.GetProperty("patchLine");
        Assert.Equal(1, patchLine.GetProperty("changed").GetInt32());
        Assert.Equal(1, patchLine.GetProperty("measurable").GetInt32());
        Assert.Equal(shouldPass ? 1 : 0, patchLine.GetProperty("covered").GetInt32());
        Assert.Equal(shouldPass ? 100m : 0m, patchLine.GetProperty("percent").GetDecimal());
        Assert.Equal(1, gate.GetProperty("patchBranch").GetProperty("covered").GetInt32());
        Assert.Equal(2, gate.GetProperty("patchBranch").GetProperty("measurable").GetInt32());
        var targetsArtifact = Assert.Single(writer.WrittenArtifacts,
            artifact => artifact.LogicalName == "coverage-patch-targets");
        using var targetsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            TestPathUtils.PathUnder(artifactRoot, targetsArtifact.RelativePath)));
        var targets = targetsDocument.RootElement;
        Assert.Equal(lineMode, targets.GetProperty("patch").GetProperty("lineMode").GetString());
        Assert.Equal(diffSha256, targets.GetProperty("patch").GetProperty("diffSource").GetProperty("sha256").GetString());
        Assert.Equal(1, targets.GetProperty("summary").GetProperty("changed").GetInt32());
        Assert.Equal(1, targets.GetProperty("summary").GetProperty("targets").GetInt32());
        var target = Assert.Single(targets.GetProperty("targets").EnumerateArray());
        Assert.Equal("src/Changed.cs", target.GetProperty("path").GetString());
        Assert.Equal(1, target.GetProperty("line").GetInt32());
        Assert.True(target.GetProperty("lineCovered").GetBoolean());
        Assert.Equal(1, target.GetProperty("conditions").GetProperty("covered").GetInt32());
        Assert.Equal(2, target.GetProperty("conditions").GetProperty("valid").GetInt32());
        Assert.Equal(new[] { "partial-condition" },
            target.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.Equal(shouldPass ? new[] { "patchBranch" } : ["patchLine", "patchBranch"],
            target.GetProperty("gateDimensions").EnumerateArray().Select(dimension => dimension.GetString()));
    }

    [Fact]
    public async Task RunAsync_ShouldRejectAStagingGateLinkBeforeWritingDeclaredArtifacts()
    {
        if (OperatingSystem.IsWindows())
        {
            // This control requires ordinary Unix symbolic-link support.
            return;
        }

        using var directory = TestDirectory.Create();
        using var sentinel = TestDirectory.Create();
        var sentinelPath = TestPathUtils.PathUnder(sentinel.Path, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, SecretMarker);
        var sentinelBytes = await File.ReadAllBytesAsync(sentinelPath);
        var transport = new FakeRestrictedRun([Report(PassingCobertura)]);
        string? stagingRoot = null;
        var reporter = new RecordingReportGenerator
        {
            MergeHandler = async (outputDirectory, token) =>
            {
                stagingRoot = Directory.GetParent(outputDirectory)!.Parent!.FullName;
                Directory.CreateSymbolicLink(TestPathUtils.PathUnder(stagingRoot, "gate"), sentinel.Path);
                var coveragePath = TestPathUtils.PathUnder(outputDirectory, "Cobertura.xml");
                var summaryPath = TestPathUtils.PathUnder(outputDirectory, "Summary.txt");
                await File.WriteAllTextAsync(coveragePath, PassingCobertura, token);
                await File.WriteAllTextAsync(summaryPath, "Unit fixture merger output", token);
                return new CoverageRunMergeResult(0, coveragePath, summaryPath);
            },
        };
        var declaration = CreateDeclaration(
        [new EvidenceArtifactSlot("coverage-gate", "coverage", "application/json", Required: true, MaximumBytes: 1024 * 1024)]);
        var writer = CreateWriter(directory.Path, declaration);

        var result = await CreateProducer(transport, reporter).RunAsync(
            declaration, TestPathUtils.PathUnder(directory.Path, "subject.slnx"), null, writer, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains(nameof(IOException), result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(result.SatisfiedAssertionIds);
        AssertNoArtifacts(result);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Equal(1, transport.RunCount);
        Assert.Equal(1, transport.CollectCount);
        Assert.Equal(1, reporter.MergeCount);
        Assert.NotNull(stagingRoot);
        Assert.False(Directory.Exists(stagingRoot));
        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinelPath));
        Assert.Equal(new[] { sentinelPath }, Directory.GetFileSystemEntries(sentinel.Path));
    }

    [Fact]
    public async Task RunAsync_ShouldDowngradeAPassingGateWhenPrivateStagingCleanupIsDenied()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // No cleanup-denial coverage is claimed on platforms without Unix modes.
            return;
        }

        using var directory = TestDirectory.Create();
        var probe = TestPathUtils.PathUnder(directory.Path, "permission-probe");
        Directory.CreateDirectory(probe);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(probe, "held.txt"), "owned probe");
        var probeMode = File.GetUnixFileMode(probe);
        var permissionsDenyEnumeration = false;
        try
        {
            File.SetUnixFileMode(probe, UnixFileMode.None);
            try
            {
                _ = Directory.GetFileSystemEntries(probe);
            }
            catch (UnauthorizedAccessException)
            {
                permissionsDenyEnumeration = true;
            }
        }
        finally
        {
            File.SetUnixFileMode(probe, probeMode);
        }

        if (!permissionsDenyEnumeration)
        {
            // Root or another privileged runner can bypass chmod; it cannot exercise this control.
            return;
        }

        var transport = new FakeRestrictedRun([Report(PassingCobertura)]);
        string? stagingRoot = null;
        string? blocker = null;
        var reporter = new RecordingReportGenerator
        {
            MergeHandler = async (outputDirectory, token) =>
            {
                stagingRoot = Directory.GetParent(outputDirectory)!.Parent!.FullName;
                blocker = TestPathUtils.PathUnder(stagingRoot, "cleanup-blocker");
                Directory.CreateDirectory(blocker);
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(blocker, "held.txt"), "unused owned file", token);
                var coveragePath = TestPathUtils.PathUnder(outputDirectory, "Cobertura.xml");
                var summaryPath = TestPathUtils.PathUnder(outputDirectory, "Summary.txt");
                await File.WriteAllTextAsync(coveragePath, PassingCobertura, token);
                await File.WriteAllTextAsync(summaryPath, "Unit fixture merger output", token);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(blocker, UnixFileMode.None);
                }
                else
                {
                    throw new PlatformNotSupportedException("The owned cleanup control requires Unix file modes.");
                }
                return new CoverageRunMergeResult(0, coveragePath, summaryPath);
            },
        };
        var declaration = CreateDeclaration(
        [new EvidenceArtifactSlot("coverage-gate", "coverage", "application/json", Required: true, MaximumBytes: 1024 * 1024)]);
        var writer = CreateWriter(directory.Path, declaration);
        try
        {
            var result = await CreateProducer(transport, reporter).RunAsync(
                declaration, TestPathUtils.PathUnder(directory.Path, "subject.slnx"), null, writer, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Equal("The protected coverage procedure could not remove its private staging directory.", result.Diagnostic);
            Assert.Empty(result.SatisfiedAssertionIds);
            Assert.Equal(1, transport.RunCount);
            Assert.Equal(1, transport.CollectCount);
            Assert.Equal(1, reporter.MergeCount);
            var written = Assert.Single(writer.WrittenArtifacts);
            Assert.Equal("coverage-gate", written.LogicalName);
            Assert.Equal(written, Assert.Single(result.Artifacts!));
            using var gateDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                TestPathUtils.PathUnder(TestPathUtils.PathUnder(directory.Path, "artifacts"), written.RelativePath)));
            Assert.True(gateDocument.RootElement.GetProperty("passed").GetBoolean());
            Assert.NotNull(blocker);
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(blocker));
            Assert.True(Directory.Exists(stagingRoot));
        }
        finally
        {
            if (blocker is not null && Directory.Exists(blocker))
            {
                File.SetUnixFileMode(blocker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (stagingRoot is not null && Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static string CreateCoberturaWithPartialBranch(string fileName)
    {
        // Consistent unit report: twenty hit lines, nineteen of twenty branch outcomes.
        var lines = Enumerable.Range(1, 20).Select(number =>
        {
            var line = new XElement("line", new XAttribute("number", number), new XAttribute("hits", "1"));
            if (number <= 10)
            {
                line.Add(new XAttribute("branch", "true"),
                    new XAttribute("condition-coverage", number == 1 ? "50% (1/2)" : "100% (2/2)"));
            }

            return line;
        });
        return new XElement("coverage",
            new XAttribute("lines-covered", "20"), new XAttribute("lines-valid", "20"),
            new XAttribute("branches-covered", "19"), new XAttribute("branches-valid", "20"),
            new XAttribute("line-rate", "1"), new XAttribute("branch-rate", "0.95"),
            new XElement("packages", new XElement("package", new XAttribute("name", "Fixture"),
                new XElement("classes", new XElement("class",
                    new XAttribute("name", "Fixture.PartialBranch"), new XAttribute("filename", fileName),
                    new XElement("lines", lines)))))).ToString(SaveOptions.DisableFormatting);
    }

    private static EvidenceProducerDeclaration CreateDeclaration(
        IReadOnlyList<EvidenceArtifactSlot> slots,
        EvidenceCoverageGateRequirements? gate = null)
        => new("coverage", "coverage", "1.0.0", [], [AssertionId], slots, 60,
            gate ?? new EvidenceCoverageGateRequirements(95, 85, TolerancePercent: 0));

    private static EvidenceArtifactWriter CreateWriter(string root, EvidenceProducerDeclaration declaration)
        => new(declaration, TestPathUtils.PathUnder(root, "artifacts"));

    private static EvidenceRestrictedCoverageProducer CreateProducer(
        FakeRestrictedRun transport,
        RecordingReportGenerator reportGenerator)
        => new(transport, new CoverageMergeWorkflow(reportGenerator, TimeProvider.System));

    private static RestrictedCoverageReport Report(string xml)
        => new("shard/coverage.cobertura.xml", Encoding.UTF8.GetBytes(xml));

    private static string CreateCoberturaWithFile(string fileName, int hits)
        => new XElement("coverage",
            new XAttribute("lines-covered", "100"),
            new XAttribute("lines-valid", "100"),
            new XAttribute("branches-covered", "100"),
            new XAttribute("branches-valid", "100"),
            new XAttribute("line-rate", "1"),
            new XAttribute("branch-rate", "1"),
            new XElement("packages",
                new XElement("package",
                    new XAttribute("name", "Fixture"),
                    new XElement("classes",
                        new XElement("class",
                            new XAttribute("name", "Fixture.Changed"),
                            new XAttribute("filename", fileName),
                            new XElement("lines",
                                new XElement("line",
                                    new XAttribute("number", "1"),
                                    new XAttribute("hits", hits.ToString(System.Globalization.CultureInfo.InvariantCulture)))))))))
            .ToString(SaveOptions.DisableFormatting);

    private static void AssertNoArtifacts(EvidenceProducerResult result)
        => Assert.True(result.Artifacts is null || result.Artifacts.Count == 0);

    private sealed class FakeRestrictedRun(IReadOnlyList<RestrictedCoverageReport>? reports) : IEvidenceRestrictedCoverageRun
    {
        public Func<CancellationToken, Task<EvidenceRestrictedProcessResult>>? RunHandler { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<RestrictedCoverageReport>>>? CollectHandler { get; set; }
        public int RunCount { get; private set; }
        public int CollectCount { get; private set; }
        public CancellationToken RunCancellationToken { get; private set; }
        public CancellationToken CollectCancellationToken { get; private set; }

        public Task<EvidenceRestrictedProcessResult> RunAsync(
            string solutionPath,
            string resultsToken,
            CancellationToken cancellationToken)
        {
            _ = solutionPath;
            _ = resultsToken;
            RunCount++;
            RunCancellationToken = cancellationToken;
            return RunHandler?.Invoke(cancellationToken)
                ?? Task.FromResult(new EvidenceRestrictedProcessResult(0, string.Empty, string.Empty, false));
        }

        public Task<IReadOnlyList<RestrictedCoverageReport>> CollectReportsAsync(
            string resultsToken,
            CancellationToken cancellationToken)
        {
            _ = resultsToken;
            CollectCount++;
            CollectCancellationToken = cancellationToken;
            return CollectHandler?.Invoke(cancellationToken)
                ?? Task.FromResult(reports!);
        }
    }

    private sealed class RecordingReportGenerator : ICoverageRunReportGenerator
    {
        public Func<string, CancellationToken, Task<CoverageRunMergeResult>>? MergeHandler { get; set; }
        public int ExitCode { get; init; }
        public bool WriteCobertura { get; init; } = true;
        public bool WriteSummary { get; init; } = true;
        public string CoberturaContents { get; init; } = PassingCobertura;
        public int MergeCount { get; private set; }
        public CancellationToken MergeCancellationToken { get; private set; }

        public async Task<CoverageRunMergeResult> MergeAsync(
            IReadOnlyList<string> coverageFiles,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            MergeCount++;
            MergeCancellationToken = cancellationToken;
            var reportGlob = Assert.Single(coverageFiles);
            var wildcardMarker = Path.DirectorySeparatorChar + "**" + Path.DirectorySeparatorChar;
            var wildcardIndex = reportGlob.IndexOf(wildcardMarker, StringComparison.Ordinal);
            Assert.True(wildcardIndex > 0, "Expected the shared merge workflow's recursive report glob.");
            var inputRoot = reportGlob[..wildcardIndex];
            Assert.NotEmpty(Directory.GetFiles(inputRoot, "coverage.cobertura.xml", SearchOption.AllDirectories));
            Directory.CreateDirectory(outputDirectory);

            if (MergeHandler is { } handler)
            {
                return await handler(outputDirectory, cancellationToken).ConfigureAwait(false);
            }

            var coberturaPath = TestPathUtils.PathUnder(outputDirectory, "Cobertura.xml");
            var summaryPath = TestPathUtils.PathUnder(outputDirectory, "Summary.txt");
            if (WriteCobertura)
            {
                await File.WriteAllTextAsync(coberturaPath, CoberturaContents, cancellationToken).ConfigureAwait(false);
            }

            if (WriteSummary)
            {
                await File.WriteAllTextAsync(summaryPath, "ReportGenerator summary", cancellationToken).ConfigureAwait(false);
            }

            return new CoverageRunMergeResult(ExitCode, coberturaPath, summaryPath);
        }
    }
}
