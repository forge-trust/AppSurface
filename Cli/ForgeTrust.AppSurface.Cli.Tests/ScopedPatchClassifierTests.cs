using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using CommandException = ForgeTrust.AppSurface.Evidence.Coverage.CoverageExecutionException;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class ScopedPatchClassifierTests
{
    private const string Alpha = "Alpha";
    private const string Beta = "Beta";
    private const string AlphaFile = "src/Alpha/Feature.cs";
    private const string BetaFile = "src/Beta/Other.cs";

    private static readonly EvidenceCoverageScope Scope = new(
        ["tests/Alpha.Tests/Alpha.Tests.csproj"],
        [new EvidenceCoverageScopeSource("src/Alpha/**", Alpha), new EvidenceCoverageScopeSource("src/Beta/**", Beta)],
        MinPatchLinePercent: 80,
        MinPatchBranchPercent: 50,
        PatchLineMode: "measurable",
        TolerancePercent: 0);

    [Fact]
    public void Classify_ShouldScoreCoveredUncoveredAndNotApplicableLinesFromTheDeclaredAssembly()
    {
        var report = Report([Alpha, Beta], (Alpha, AlphaFile, 1, Row(true)), (Alpha, AlphaFile, 2, Row(true)), (Alpha, AlphaFile, 3, Row(true)),
            (Alpha, AlphaFile, 4, Row(true)), (Alpha, AlphaFile, 5, Row(false)));

        var result = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile), Lines((AlphaFile, [1, 2, 3, 4, 5, 6])), report);

        Assert.Equal(ScopedCoverageReason.Complete, result.Reason);
        Assert.Equal(new ScopedPatchMetric("measured", "reported-points", 6, 5, 4, 80m), result.Line);
        Assert.Equal(new ScopedPatchMetric("notApplicable", "no-reported-patch-points", 6, 0, 0, null), result.Branch);
        Assert.Equal(1, result.InScopeFiles);
        Assert.Equal(0, result.OutOfScopeFiles);
        Assert.All(result.Sources, static source => Assert.True(source.AssemblyReported));
        Assert.Empty(result.UnknownFiles);
    }

    [Fact]
    public void Classify_ShouldFailBelowThresholdOnlyForAMeasuredMetric()
    {
        var report = Report([Alpha, Beta], (Alpha, AlphaFile, 1, Row(true)), (Alpha, AlphaFile, 2, Row(false, 1, 4)));

        var result = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile), Lines((AlphaFile, [1, 2])), report);

        Assert.Equal(ScopedCoverageReason.BelowThreshold, result.Reason);
        Assert.Equal(50m, result.Line.Percent);
        Assert.Equal(new ScopedPatchMetric("measured", "reported-points", 2, 4, 1, 25m), result.Branch);
    }

    [Fact]
    public void Classify_ShouldApplyToleranceToTheThreshold()
    {
        var report = Report([Alpha, Beta], (Alpha, AlphaFile, 1, Row(true)), (Alpha, AlphaFile, 2, Row(true)), (Alpha, AlphaFile, 3, Row(true)),
            (Alpha, AlphaFile, 4, Row(false)));
        var tolerant = Scope with { MinPatchLinePercent = 76, TolerancePercent = 1 };

        Assert.Equal(ScopedCoverageReason.Complete, ScopedPatchClassifier.Classify(tolerant, Files(AlphaFile), Lines((AlphaFile, [1, 2, 3, 4])), report).Reason);
        Assert.Equal(ScopedCoverageReason.BelowThreshold, ScopedPatchClassifier.Classify(tolerant with { TolerancePercent = 0 }, Files(AlphaFile), Lines((AlphaFile, [1, 2, 3, 4])), report).Reason);
    }

    [Theory]
    [InlineData("measurable", 100)]
    [InlineData("codecov", 50)]
    [InlineData("CODECOV", 50)]
    public void Classify_ShouldDecidePartialBranchesByLineMode(string mode, int expectedPercent)
    {
        var report = Report([Alpha, Beta], (Alpha, AlphaFile, 1, Row(true, 1, 2)), (Alpha, AlphaFile, 2, Row(true, 2, 2)));
        var scope = Scope with { PatchLineMode = mode, MinPatchLinePercent = 0, MinPatchBranchPercent = null };

        var result = ScopedPatchClassifier.Classify(scope, Files(AlphaFile), Lines((AlphaFile, [1, 2])), report);

        Assert.Equal((decimal)expectedPercent, result.Line.Percent);
        Assert.Equal(ScopedCoverageReason.Complete, result.Reason);
    }

    [Fact]
    public void Classify_ShouldFailWhenADeclaredAssemblyIsMissingEvenIfTheDiffDidNotTouchIt()
    {
        var report = Report([Alpha], (Alpha, AlphaFile, 1, Row(true)));

        var result = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile), Lines((AlphaFile, [1])), report);

        Assert.Equal(ScopedCoverageReason.ScopeAssemblyNotInstrumented, result.Reason);
        Assert.Equal(new bool?[] { true, false }, result.Sources.Select(static source => source.AssemblyReported));
        Assert.Empty(result.UnknownFiles);
    }

    [Fact]
    public void Classify_ShouldListUnknownFilesWhenTheChangedAssemblyIsMissing()
    {
        var report = Report([Alpha], (Alpha, AlphaFile, 1, Row(true)));

        var result = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile, BetaFile), Lines((AlphaFile, [1]), (BetaFile, [7, 8])), report);

        Assert.Equal(ScopedCoverageReason.ScopeAssemblyNotInstrumented, result.Reason);
        Assert.Equal(new[] { BetaFile }, result.UnknownFiles);
        Assert.False(result.UnknownFilesTruncated);
        Assert.Equal(3, result.Line.ChangedLines);
        Assert.Equal(1, result.Line.Measurable);
    }

    [Fact]
    public void Classify_ShouldTreatALineWithNoRowInAReportedAssemblyAsNotApplicable()
    {
        var report = Report([Alpha, Beta]);

        var result = ScopedPatchClassifier.Classify(Scope, Files("src/Alpha/IFeature.cs"), Lines(("src/Alpha/IFeature.cs", [1, 2])), report);

        Assert.Equal(ScopedCoverageReason.Complete, result.Reason);
        Assert.Equal(new ScopedPatchMetric("notApplicable", "no-reported-patch-points", 2, 0, 0, null), result.Line);
    }

    [Fact]
    public void Classify_ShouldCountOnlyTheDeclaredAssemblyForAFileLinkedIntoTwoAssemblies()
    {
        // Beta also compiles the Alpha file and covers its line; only Alpha's own rows may count.
        var report = Report([Alpha, Beta], (Alpha, AlphaFile, 1, Row(false)), (Beta, AlphaFile, 1, Row(true)));

        var result = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile), Lines((AlphaFile, [1])), report);

        Assert.Equal(ScopedCoverageReason.BelowThreshold, result.Reason);
        Assert.Equal(0m, result.Line.Percent);
    }

    [Fact]
    public void Classify_ShouldFailWhenAChangedFileHasRowsOnlyUnderAnotherAssembly()
    {
        // Alpha is reported (it has other files), but the changed file is reported only under Beta: the declared name is wrong for it.
        var report = Report([Alpha, Beta], (Alpha, "src/Alpha/Other.cs", 1, Row(true)), (Beta, AlphaFile, 1, Row(true)));

        var result = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile), Lines((AlphaFile, [1])), report);

        Assert.Equal(ScopedCoverageReason.AssemblyMismatch, result.Reason);
        Assert.Equal(new[] { AlphaFile }, result.UnknownFiles);
        Assert.Equal("notApplicable", result.Line.Outcome);
        Assert.Contains("correct the source entry's assembly name", ScopedCoverageReceipt.FormatDiagnostic(Scope, result), StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_ShouldReportAssemblyMissingBeforeAMismatch()
    {
        var report = Report([Alpha], (Beta, AlphaFile, 1, Row(true)));

        var result = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile), Lines((AlphaFile, [1])), report);

        Assert.Equal(ScopedCoverageReason.ScopeAssemblyNotInstrumented, result.Reason);
    }

    [Fact]
    public void Serialize_ShouldFitTheMinimumSlotForAWorstCaseDeclaration()
    {
        var wide = new string('\u00e9', EvidenceScopedCoverageContract.MaximumPathLength - 8);
        var scope = new EvidenceCoverageScope(
            [.. Enumerable.Range(0, EvidenceScopedCoverageContract.MaximumProjects).Select(index => $"{index:00}/{wide}.csproj")],
            [.. Enumerable.Range(0, EvidenceScopedCoverageContract.MaximumSources).Select(index => new EvidenceCoverageScopeSource(
                $"{index:00}/{wide}/**", new string('A', EvidenceScopedCoverageContract.MaximumAssemblyNameLength)))],
            100,
            100,
            "measurable",
            100);
        var unknown = Enumerable.Range(0, EvidenceScopedCoverageContract.MaximumUnknownFiles + 5)
            .Select(index => $"{index:00}/{wide}/" + string.Concat(Enumerable.Repeat("\U0001F600", 400)))
            .ToArray();
        var classification = new ScopedPatchClassification(
            ScopedCoverageReason.ScopeAssemblyNotInstrumented,
            new ScopedPatchMetric("measured", "reported-points", int.MaxValue, int.MaxValue, int.MaxValue, 99.9999m),
            new ScopedPatchMetric("measured", "reported-points", int.MaxValue, int.MaxValue, int.MaxValue, 99.9999m),
            [.. scope.Sources.Select(static source => new ScopedSourceStatus(source.Pattern, source.Assembly, false))],
            int.MaxValue,
            int.MaxValue,
            [.. unknown.Take(EvidenceScopedCoverageContract.MaximumUnknownFiles).Select(static path => ScopedPatchClassifier.Cut(path).Path)],
            true);

        var bytes = ScopedCoverageReceipt.Serialize(scope, classification);

        Assert.True(bytes.Length <= EvidenceScopedCoverageContract.GetMinimumReceiptBytes(scope),
            $"Receipt of {bytes.Length} bytes exceeds the planner minimum {EvidenceScopedCoverageContract.GetMinimumReceiptBytes(scope)}.");
    }

    [Fact]
    public void Classify_ShouldFailWhenNothingChangedInScope()
    {
        var report = Report([Alpha, Beta]);

        var result = ScopedPatchClassifier.Classify(Scope, Files("docs/readme.md", "src/Gamma/G.cs"), Lines(("src/Gamma/G.cs", [1])), report);

        Assert.Equal(ScopedCoverageReason.NothingInScope, result.Reason);
        Assert.Equal(0, result.InScopeFiles);
        Assert.Equal(2, result.OutOfScopeFiles);
        Assert.Equal(0, result.Line.ChangedLines);
    }

    [Fact]
    public void Classify_ShouldPassATestOnlyChangeInsideADeclaredTestProjectDirectory()
    {
        var report = Report([Alpha, Beta]);

        var result = ScopedPatchClassifier.Classify(Scope, Files("tests/Alpha.Tests/FeatureTests.cs"), Lines(("tests/Alpha.Tests/FeatureTests.cs", [1, 2])), report);

        Assert.Equal(ScopedCoverageReason.Complete, result.Reason);
        Assert.Equal("notApplicable", result.Line.Outcome);
        Assert.Equal(1, result.InScopeFiles);
    }

    [Fact]
    public void Classify_ShouldNotTreatATopLevelTestProjectAsAWholeRepositoryDirectory()
    {
        var scope = Scope with { Projects = ["Root.Tests.csproj"] };

        var result = ScopedPatchClassifier.Classify(scope, Files("anything.cs"), Lines(), Report([Alpha, Beta]));

        Assert.Equal(ScopedCoverageReason.NothingInScope, result.Reason);
    }

    [Fact]
    public void Classify_ShouldScoreAMixedChangeOnItsInScopeLinesAndCountTheRest()
    {
        var report = Report([Alpha, Beta], (Alpha, AlphaFile, 1, Row(true)));

        var result = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile, "Web/Page.cs"), Lines((AlphaFile, [1]), ("Web/Page.cs", [1, 2, 3])), report);

        Assert.Equal(ScopedCoverageReason.Complete, result.Reason);
        Assert.Equal(1, result.Line.ChangedLines);
        Assert.Equal(1, result.OutOfScopeFiles);
    }

    [Fact]
    public void Classify_ShouldCountDeletionAndRenameRecordsByEitherPath()
    {
        var report = Report([Alpha, Beta]);
        IReadOnlyList<NormalizedDiffPath> files =
        [
            new("src/Alpha/Removed.cs", "deleted"),
            new("moved/Out.cs", "renamed", "src/Alpha/Out.cs"),
        ];

        var result = ScopedPatchClassifier.Classify(Scope, files, Lines(), report);

        Assert.Equal(ScopedCoverageReason.Complete, result.Reason);
        Assert.Equal(2, result.InScopeFiles);
    }

    [Fact]
    public void Classify_ShouldBoundAndCutUnknownFiles()
    {
        var longPath = "src/Beta/" + new string('x', EvidenceScopedCoverageContract.MaximumReceiptPathLength) + ".cs";
        var files = Enumerable.Range(0, EvidenceScopedCoverageContract.MaximumUnknownFiles + 1)
            .Select(static index => $"src/Beta/F{index}.cs")
            .Prepend(longPath)
            .ToArray();

        var result = ScopedPatchClassifier.Classify(Scope, Files(files), Lines(), Report([Alpha]));

        Assert.Equal(EvidenceScopedCoverageContract.MaximumUnknownFiles, result.UnknownFiles.Count);
        Assert.Equal(EvidenceScopedCoverageContract.MaximumReceiptPathLength, result.UnknownFiles[0].Length);
        Assert.True(result.UnknownFilesTruncated);
    }

    [Fact]
    public void Cut_ShouldNotSplitASurrogatePair()
    {
        var limit = EvidenceScopedCoverageContract.MaximumReceiptPathLength;
        var path = new string('a', limit - 1) + "\U0001F600" + "tail";

        var (cut, wasCut) = ScopedPatchClassifier.Cut(path);

        Assert.True(wasCut);
        Assert.Equal(limit - 1, cut.Length);
        Assert.Equal(("short", false), ScopedPatchClassifier.Cut("short"));
        Assert.Equal((new string('b', limit), true), ScopedPatchClassifier.Cut(new string('b', limit + 3)));
    }

    [Fact]
    public void Classify_ShouldRejectNullInputs()
    {
        var report = Report([Alpha]);

        Assert.Throws<ArgumentNullException>(() => ScopedPatchClassifier.Classify(null!, [], Lines(), report));
        Assert.Throws<ArgumentNullException>(() => ScopedPatchClassifier.Classify(Scope, null!, Lines(), report));
        Assert.Throws<ArgumentNullException>(() => ScopedPatchClassifier.Classify(Scope, [], null!, report));
        Assert.Throws<ArgumentNullException>(() => ScopedPatchClassifier.Classify(Scope, [], Lines(), null!));
        Assert.Throws<ArgumentNullException>(() => ScopedPatchClassification.Unclassified(null!, ScopedCoverageReason.RunFailed));
    }

    [Fact]
    public void Unclassified_ShouldCarryUnverifiedMetricsAndUnknownAssemblyStatus()
    {
        var result = ScopedPatchClassification.Unclassified(Scope, ScopedCoverageReason.DeadlineExceeded);

        Assert.Equal(ScopedPatchMetric.Unverified, result.Line);
        Assert.Equal(ScopedPatchMetric.Unverified, result.Branch);
        Assert.All(result.Sources, static source => Assert.Null(source.AssemblyReported));
        Assert.Null(result.InScopeFiles);
        Assert.Null(result.OutOfScopeFiles);
    }

    [Theory]
    [InlineData(nameof(ScopedCoverageReason.Complete), EvidenceProducerOutcome.Passed)]
    [InlineData(nameof(ScopedCoverageReason.BelowThreshold), EvidenceProducerOutcome.Failed)]
    [InlineData(nameof(ScopedCoverageReason.ScopeAssemblyNotInstrumented), EvidenceProducerOutcome.Failed)]
    [InlineData(nameof(ScopedCoverageReason.AssemblyMismatch), EvidenceProducerOutcome.Failed)]
    [InlineData(nameof(ScopedCoverageReason.NothingInScope), EvidenceProducerOutcome.Failed)]
    [InlineData(nameof(ScopedCoverageReason.RunFailed), EvidenceProducerOutcome.Failed)]
    [InlineData(nameof(ScopedCoverageReason.SolutionUnavailable), EvidenceProducerOutcome.Unavailable)]
    [InlineData(nameof(ScopedCoverageReason.DiffUnavailable), EvidenceProducerOutcome.Unavailable)]
    [InlineData(nameof(ScopedCoverageReason.DeadlineExceeded), EvidenceProducerOutcome.TimedOut)]
    public void ToOutcome_ShouldMapEveryReason(string reason, EvidenceProducerOutcome expected) =>
        Assert.Equal(expected, ScopedCoverageReceipt.ToOutcome(Enum.Parse<ScopedCoverageReason>(reason)));

    [Fact]
    public void Serialize_ShouldWriteTheTotalsOnlySchema()
    {
        var report = Report([Alpha], (Alpha, AlphaFile, 1, Row(true, 1, 2)));
        var classification = ScopedPatchClassifier.Classify(Scope with { MinPatchLinePercent = null }, Files(AlphaFile, BetaFile, "x.md"), Lines((AlphaFile, [1])), report);

        using var document = JsonDocument.Parse(ScopedCoverageReceipt.Serialize(Scope with { MinPatchLinePercent = null }, classification));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ScopeAssemblyNotInstrumented", root.GetProperty("reason").GetString());
        Assert.False(root.GetProperty("overallGateEvaluated").GetBoolean());
        Assert.Equal("tests/Alpha.Tests/Alpha.Tests.csproj", root.GetProperty("projects")[0].GetString());
        Assert.False(root.GetProperty("sources")[1].GetProperty("assemblyReported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("thresholds").GetProperty("minPatchLinePercent").ValueKind);
        Assert.Equal(50m, root.GetProperty("thresholds").GetProperty("minPatchBranchPercent").GetDecimal());
        Assert.Equal("measurable", root.GetProperty("thresholds").GetProperty("patchLineMode").GetString());
        Assert.Equal("measured", root.GetProperty("line").GetProperty("outcome").GetString());
        Assert.Equal(50m, root.GetProperty("branch").GetProperty("percent").GetDecimal());
        Assert.Equal(2, root.GetProperty("inScopeFiles").GetInt32());
        Assert.Equal(1, root.GetProperty("outOfScopeFiles").GetInt32());
        Assert.Equal(BetaFile, root.GetProperty("unknownFiles")[0].GetString());
        Assert.False(root.GetProperty("unknownFilesTruncated").GetBoolean());
    }

    [Fact]
    public void Serialize_ShouldWriteNullsForAnUnclassifiedRunAndEscapeNonAscii()
    {
        var scope = Scope with { Projects = ["tests/É/É.csproj"] };
        var bytes = ScopedCoverageReceipt.Serialize(scope, ScopedPatchClassification.Unclassified(scope, ScopedCoverageReason.RunFailed));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        Assert.All(Encoding.UTF8.GetString(bytes), static character => Assert.True(character < 128));
        Assert.Equal("tests/É/É.csproj", root.GetProperty("projects")[0].GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sources")[0].GetProperty("assemblyReported").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("line").GetProperty("changedLines").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("inScopeFiles").ValueKind);
        Assert.Throws<ArgumentNullException>(() => ScopedCoverageReceipt.Serialize(null!, ScopedPatchClassification.Unclassified(scope, ScopedCoverageReason.RunFailed)));
        Assert.Throws<ArgumentNullException>(() => ScopedCoverageReceipt.Serialize(scope, null!));
    }

    [Fact]
    public void FormatDiagnostic_ShouldNameMetricsScopeGapsAndTheUnevaluatedGate()
    {
        var measured = ScopedPatchClassifier.Classify(Scope, Files(AlphaFile, "web/x.cs"), Lines((AlphaFile, [1, 2])), Report([Alpha], (Alpha, AlphaFile, 1, Row(true))));
        var nothing = ScopedPatchClassifier.Classify(Scope, Files("web/x.cs"), Lines(), Report([Alpha, Beta]));

        var text = ScopedCoverageReceipt.FormatDiagnostic(Scope, measured, "Per-line detail: targets.json.");

        Assert.Equal(
            "Scoped patch coverage ScopeAssemblyNotInstrumented, 1 project, 2 sources: line 100% (1/1), branch not applicable. Changed files outside the scope, not scored: 1. Not in the report: Beta; declare a test project that references it, or correct the assembly name. Per-line detail: targets.json. The repository-wide gate was not evaluated.",
            text);
        Assert.Contains("No changed file is under a declared source pattern", ScopedCoverageReceipt.FormatDiagnostic(Scope, nothing), StringComparison.Ordinal);
        Assert.Contains("line unverified, branch unverified", ScopedCoverageReceipt.FormatDiagnostic(Scope, ScopedPatchClassification.Unclassified(Scope, ScopedCoverageReason.RunFailed)), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => ScopedCoverageReceipt.FormatDiagnostic(null!, nothing));
        Assert.Throws<ArgumentNullException>(() => ScopedCoverageReceipt.FormatDiagnostic(Scope, null!));
    }

    [Fact]
    public async Task ReadPackageCoverageAsync_ShouldKeepPackagesApartAndListEmptyPackages()
    {
        using var directory = TestDirectory.Create();
        var path = TestPathUtils.PathUnder(directory.Path, "coverage.cobertura.xml");
        var absolute = TestPathUtils.PathUnder(directory.Path, "src", "Alpha", "Feature.cs");
        await File.WriteAllTextAsync(path, $"""
            <?xml version="1.0"?>
            <coverage line-rate="1" branch-rate="1">
              <packages>
                <package name="Alpha">
                  <classes>
                    <class name="Feature" filename="{absolute}">
                      <methods><method name="Run"><lines><line number="1" hits="0" branch="True" condition-coverage="50% (1/2)" /></lines></method></methods>
                      <lines>
                        <line number="1" hits="2" branch="True" condition-coverage="50% (1/2)" />
                        <line number="2" hits="0" />
                        <line number="x" hits="1" />
                      </lines>
                    </class>
                    <class name="Empty" filename="src/Alpha/Empty.cs" />
                    <class name="Outside" filename="/elsewhere/Outside.cs"><lines><line number="1" hits="1" /></lines></class>
                  </classes>
                </package>
                <package name="Beta">
                  <classes>
                    <class name="Linked" filename="src/Alpha/Feature.cs"><lines><line number="2" hits="5" /></lines></class>
                  </classes>
                </package>
                <package name="Gamma" />
                <package name=""><classes><class filename="src/x.cs"><lines><line number="1" hits="1" /></lines></class></classes></package>
              </packages>
            </coverage>
            """);

        var coverage = await PatchCoverageEvaluator.ReadPackageCoverageAsync(path, directory.Path, CancellationToken.None);

        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, coverage.ReportedPackages.Order(StringComparer.Ordinal));
        var alpha = coverage.Rows[Alpha][AlphaFile];
        Assert.Equal(new CoberturaLineRow(true, 2, 4), alpha[1]);
        Assert.Equal(new CoberturaLineRow(false, null, null), alpha[2]);
        Assert.Equal(2, alpha.Count);
        Assert.Equal(new CoberturaLineRow(true, null, null), coverage.Rows[Beta][AlphaFile][2]);
        Assert.False(coverage.Rows.ContainsKey("Gamma"));
        Assert.False(coverage.Rows.ContainsKey(string.Empty));
        Assert.Single(coverage.Rows[Alpha]);
    }

    [Fact]
    public async Task ReadPackageCoverageAsync_ShouldReportMalformedXmlAndBadConditionsAsAscov006()
    {
        using var directory = TestDirectory.Create();
        var malformed = TestPathUtils.PathUnder(directory.Path, "malformed.xml");
        var badConditions = TestPathUtils.PathUnder(directory.Path, "conditions.xml");
        await File.WriteAllTextAsync(malformed, "<coverage><packages>");
        await File.WriteAllTextAsync(badConditions, """<coverage><packages><package name="A"><classes><class filename="a.cs"><lines><line number="1" hits="1" condition-coverage="(3/2)" /></lines></class></classes></package></packages></coverage>""");

        var parse = await Assert.ThrowsAsync<CommandException>(() => PatchCoverageEvaluator.ReadPackageCoverageAsync(malformed, directory.Path, CancellationToken.None));
        var range = await Assert.ThrowsAsync<CommandException>(() => PatchCoverageEvaluator.ReadPackageCoverageAsync(badConditions, directory.Path, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => PatchCoverageEvaluator.ReadPackageCoverageAsync(" ", directory.Path, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => PatchCoverageEvaluator.ReadPackageCoverageAsync(malformed, "", CancellationToken.None));

        Assert.StartsWith("ASCOV006", parse.Message, StringComparison.Ordinal);
        Assert.StartsWith("ASCOV006", range.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPackageCoverageAsync_ShouldHonorCancellation()
    {
        using var directory = TestDirectory.Create();
        var path = TestPathUtils.PathUnder(directory.Path, "coverage.xml");
        await File.WriteAllTextAsync(path, "<coverage />");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PatchCoverageEvaluator.ReadPackageCoverageAsync(path, directory.Path, cancellation.Token));
    }

    internal static CoberturaLineRow Row(bool covered, int? coveredConditions = null, int? validConditions = null) =>
        new(covered, coveredConditions, validConditions);

    internal static CoberturaPackageCoverage Report(string[] packages, params (string Package, string File, int Line, CoberturaLineRow Row)[] rows) =>
        new(
            packages.ToHashSet(StringComparer.Ordinal),
            rows.GroupBy(static row => row.Package).ToDictionary(
                static package => package.Key,
                static package => (IReadOnlyDictionary<string, IReadOnlyDictionary<int, CoberturaLineRow>>)package.GroupBy(static row => row.File).ToDictionary(
                    static file => file.Key,
                    static file => (IReadOnlyDictionary<int, CoberturaLineRow>)file.ToDictionary(static row => row.Line, static row => row.Row))));

    private static IReadOnlyList<NormalizedDiffPath> Files(params string[] paths) =>
        [.. paths.Select(static path => new NormalizedDiffPath(path))];

    private static IReadOnlyDictionary<string, IReadOnlySet<int>> Lines(params (string File, int[] Lines)[] lines) =>
        lines.ToDictionary(static entry => entry.File, static entry => (IReadOnlySet<int>)entry.Lines.ToHashSet(), StringComparer.Ordinal);
}
