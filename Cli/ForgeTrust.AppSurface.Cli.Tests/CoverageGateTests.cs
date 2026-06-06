using System.Diagnostics;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

[CollectionDefinition("CoverageGate process state", DisableParallelization = true)]
public sealed class CoverageGateProcessStateCollection
{
}

[Collection("CoverageGate process state")]
public sealed class CoverageGateTests
{
    [Fact]
    public async Task EvaluateAsync_Passes_WhenCoverageMeetsThresholds()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage lines-covered="90" lines-valid="100" branches-covered="45" branches-valid="50" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 85, 80, false, null);

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        await CoverageGateReportWriter.WriteAsync(result, request, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(90, result.LineCoverage.Covered);
        Assert.Equal(100, result.LineCoverage.Valid);
        Assert.Equal(90, result.LineCoverage.Percent);
        Assert.Equal(90, result.BranchCoverage.Percent);
        Assert.True(File.Exists(Path.Join(temp.Path, "coverage-gate.json")));
        Assert.True(File.Exists(Path.Join(temp.Path, "coverage-gate.md")));
        Assert.Contains("\"passed\": true", File.ReadAllText(Path.Join(temp.Path, "coverage-gate.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WritesReportsAndConsoleOutput_WhenGatePasses()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="95" lines-valid="100" branches-covered="9" branches-valid="10" />
            """);
        var command = new CoverageGateCommand
        {
            CoveragePath = coverage,
            OutputDirectory = temp.Path,
            MinLine = 90,
            MinBranch = 80,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console, CancellationToken.None);

        var output = console.ReadOutputString();
        Assert.Contains("Coverage gate PASS", output, StringComparison.Ordinal);
        Assert.Contains("lines 95.00% >= 90%", output, StringComparison.Ordinal);
        Assert.Contains("branches 90.00% >= 80%", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(temp.Path, "coverage-gate.json")));
        Assert.True(File.Exists(Path.Join(temp.Path, "coverage-gate.md")));
    }

    [Fact]
    public async Task ExecuteAsync_WritesPatchLineAndBranchThresholds_WhenDiffBaseIsConfigured()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        Directory.CreateDirectory(Path.Join(temp.Path, "src"));
        File.WriteAllText(Path.Join(temp.Path, "src", "Foo.cs"), "base" + Environment.NewLine);
        await RunGitAsync(temp.Path, "init");
        await RunGitAsync(temp.Path, "config", "user.email", "tests@example.invalid");
        await RunGitAsync(temp.Path, "config", "user.name", "AppSurface Tests");
        await RunGitAsync(temp.Path, "add", ".");
        await RunGitAsync(temp.Path, "commit", "-m", "base");
        await File.AppendAllTextAsync(Path.Join(temp.Path, "src", "Foo.cs"), "changed" + Environment.NewLine);
        await RunGitAsync(temp.Path, "add", ".");
        await RunGitAsync(temp.Path, "commit", "-m", "change");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="src/Foo.cs">
                      <lines>
                        <line number="2" hits="1" branch="true" condition-coverage="100% (2/2)" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(temp.Path);
            var command = new CoverageGateCommand
            {
                CoveragePath = coverage,
                OutputDirectory = temp.Path,
                MinLine = 100,
                MinBranch = 100,
                DiffBase = "HEAD~1",
                MinPatchLine = 100,
                MinPatchBranch = 100,
                NoGithubSummary = true
            };
            using var console = new FakeInMemoryConsole();

            await command.ExecuteAsync(console, CancellationToken.None);

            var output = console.ReadOutputString();
            Assert.Contains("patch lines 100.00% >= 100%", output, StringComparison.Ordinal);
            Assert.Contains("patch branches 100.00% >= 100%", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsDiagnostic_WhenGateFails_AfterWritingReports()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="79" lines-valid="100" branches-covered="8" branches-valid="10" />
            """);
        var command = new CoverageGateCommand
        {
            CoveragePath = coverage,
            OutputDirectory = temp.Path,
            MinLine = 80,
            MinBranch = 80,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV020", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Coverage gate FAIL", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(temp.Path, "coverage-gate.json")));
        Assert.True(File.Exists(Path.Join(temp.Path, "coverage-gate.md")));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsBlankCoveragePath_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var command = new CoverageGateCommand
        {
            CoveragePath = "  ",
            OutputDirectory = temp.Path,
            MinLine = 0,
            MinBranch = 0,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV001", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsInvalidCoveragePath_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var command = new CoverageGateCommand
        {
            CoveragePath = "coverage\0.cobertura.xml",
            OutputDirectory = temp.Path,
            MinLine = 0,
            MinBranch = 0,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV001", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsInvalidOutputPath_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var command = new CoverageGateCommand
        {
            CoveragePath = coverage,
            OutputDirectory = "coverage-output\0",
            MinLine = 0,
            MinBranch = 0,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV009", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public async Task ExecuteAsync_RejectsInvalidThresholds_WithDiagnostic(decimal threshold)
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var command = new CoverageGateCommand
        {
            CoveragePath = coverage,
            OutputDirectory = temp.Path,
            MinLine = threshold,
            MinBranch = 0,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV007", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public async Task ExecuteAsync_RejectsInvalidBranchThresholds_WithDiagnostic(decimal threshold)
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var command = new CoverageGateCommand
        {
            CoveragePath = coverage,
            OutputDirectory = temp.Path,
            MinLine = 0,
            MinBranch = threshold,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV007", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--min-branch", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public async Task ExecuteAsync_RejectsInvalidPatchThresholds_WithDiagnostic(decimal threshold)
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var command = new CoverageGateCommand
        {
            CoveragePath = coverage,
            OutputDirectory = temp.Path,
            MinLine = 0,
            MinBranch = 0,
            DiffBase = "origin/main",
            MinPatchLine = threshold,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV007", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--min-patch-line", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public async Task ExecuteAsync_RejectsInvalidPatchBranchThresholds_WithDiagnostic(decimal threshold)
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var command = new CoverageGateCommand
        {
            CoveragePath = coverage,
            OutputDirectory = temp.Path,
            MinLine = 0,
            MinBranch = 0,
            DiffBase = "origin/main",
            MinPatchBranch = threshold,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV007", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--min-patch-branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_FailsGate_WhenCoverageIsBelowThreshold()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage line-rate="0.745" branch-rate="0.5" lines-covered="149" lines-valid="200" branches-covered="1" branches-valid="2" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 75, 40, false, null);

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        var markdown = CoverageGateReportWriter.RenderMarkdown(result);

        Assert.False(result.Passed);
        Assert.Equal(74.5m, result.LineCoverage.Percent);
        Assert.Contains("# Coverage Gate: FAIL", markdown, StringComparison.Ordinal);
        Assert.Contains("| Lines | 74.50% (149/200) | 75% | fail |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_UsesCounts_WhenRateAndCountsDisagree()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage line-rate="0.9" branch-rate="0.9" lines-covered="1" lines-valid="100" branches-covered="1" branches-valid="10" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 85, 75, false, null);

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        var markdown = CoverageGateReportWriter.RenderMarkdown(result);

        Assert.False(result.Passed);
        Assert.Equal(1, result.LineCoverage.Percent);
        Assert.Equal(10, result.BranchCoverage.Percent);
        Assert.Contains("| Lines | 1.00% (1/100) | 85% | fail |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RateOnlyCoverage_RendersCountsAsUnavailable()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage line-rate="0.9" branch-rate="0.8" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 85, 75, false, null);

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        var markdown = CoverageGateReportWriter.RenderMarkdown(result);
        await CoverageGateReportWriter.WriteAsync(result, request, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.LineCoverage.Covered);
        Assert.Null(result.LineCoverage.Valid);
        Assert.Equal(90, result.LineCoverage.Percent);
        Assert.Contains("| Lines | 90.00% (count unavailable) | 85% | pass |", markdown, StringComparison.Ordinal);
        Assert.Contains("\"covered\": null", File.ReadAllText(Path.Join(temp.Path, "coverage-gate.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_ComputesPatchLineCoverage_FromChangedMeasurableLines()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="100" lines-valid="100" branches-covered="10" branches-valid="10">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="src/Foo.cs">
                      <lines>
                        <line number="11" hits="1" />
                        <line number="12" hits="0" />
                        <line number="20" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            95,
            85,
            false,
            null,
            new CoveragePatchRequest(temp.Path, "origin/main", 50, _ => Task.FromResult("""
                diff --git a/src/Foo.cs b/src/Foo.cs
                index 0000000..1111111 100644
                --- a/src/Foo.cs
                +++ b/src/Foo.cs
                @@ -10,0 +11,3 @@
                +covered
                +uncovered
                +not measurable
                @@ -19,0 +20,1 @@
                +covered elsewhere
                diff --git a/README.md b/README.md
                index 0000000..1111111 100644
                --- a/README.md
                +++ b/README.md
                @@ -1,0 +1,1 @@
                +docs
                """)));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        await CoverageGateReportWriter.WriteAsync(result, request, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(result.PatchLineCoverage);
        Assert.Equal(5, result.PatchLineCoverage.ChangedLines);
        Assert.Equal(3, result.PatchLineCoverage.MeasurableLines);
        Assert.Equal(2, result.PatchLineCoverage.CoveredLines);
        Assert.Equal(66.6667m, result.PatchLineCoverage.Percent);

        var markdown = File.ReadAllText(Path.Join(temp.Path, "coverage-gate.md"));
        var json = File.ReadAllText(Path.Join(temp.Path, "coverage-gate.json"));
        Assert.Contains("| Patch lines | 66.67% (2/3 measurable, 5 changed) | 50% | pass |", markdown, StringComparison.Ordinal);
        Assert.Contains("\"patchLine\": 50", json, StringComparison.Ordinal);
        Assert.Contains("\"measurable\": 3", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_ComputesPatchLineAndBranchCoverage_ConsistentWithFullCoverage()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="3" lines-valid="4" branches-covered="3" branches-valid="4">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="src/Foo.cs">
                      <lines>
                        <line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" />
                        <line number="2" hits="0" branch="true" condition-coverage="50% (1/2)" />
                        <line number="3" hits="1" />
                        <line number="4" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            75,
            75,
            false,
            null,
            new CoveragePatchRequest(
                temp.Path,
                "origin/main",
                75,
                _ => Task.FromResult("""
                    diff --git a/src/Foo.cs b/src/Foo.cs
                    index 0000000..1111111 100644
                    --- a/src/Foo.cs
                    +++ b/src/Foo.cs
                    @@ -0,0 +1,4 @@
                    +covered branch line
                    +uncovered branch line
                    +covered line
                    +covered line
                    diff --git a/README.md b/README.md
                    index 0000000..1111111 100644
                    --- a/README.md
                    +++ b/README.md
                    @@ -0,0 +1,1 @@
                    +docs
                    """),
                MinPatchBranchPercent: 75));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        var markdown = CoverageGateReportWriter.RenderMarkdown(result);
        await CoverageGateReportWriter.WriteAsync(result, request, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(result.LineCoverage.Percent, result.PatchLineCoverage?.Percent);
        Assert.Equal(result.BranchCoverage.Percent, result.PatchBranchCoverage?.Percent);
        Assert.Equal(5, result.PatchLineCoverage?.ChangedLines);
        Assert.Equal(4, result.PatchLineCoverage?.MeasurableLines);
        Assert.Equal(3, result.PatchLineCoverage?.CoveredLines);
        Assert.Equal(4, result.PatchBranchCoverage?.MeasurableBranches);
        Assert.Equal(3, result.PatchBranchCoverage?.CoveredBranches);
        Assert.Contains("| Patch lines | 75.00% (3/4 measurable, 5 changed) | 75% | pass |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Patch branches | 75.00% (3/4 measurable, 5 changed) | 75% | pass |", markdown, StringComparison.Ordinal);

        var json = File.ReadAllText(Path.Join(temp.Path, "coverage-gate.json"));
        Assert.Contains("\"patchLine\": 75", json, StringComparison.Ordinal);
        Assert.Contains("\"patchBranch\": 75", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_FailsGate_WhenPatchBranchCoverageIsBelowThreshold()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="10" lines-valid="10" branches-covered="10" branches-valid="10">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="src/Foo.cs">
                      <lines>
                        <line number="1" hits="1" branch="true" condition-coverage="50% (1/2)" />
                        <line number="2" hits="1" branch="true" condition-coverage="0% (0/2)" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            100,
            100,
            false,
            null,
            new CoveragePatchRequest(
                temp.Path,
                "origin/main",
                100,
                _ => Task.FromResult("""
                    diff --git a/src/Foo.cs b/src/Foo.cs
                    index 0000000..1111111 100644
                    --- a/src/Foo.cs
                    +++ b/src/Foo.cs
                    @@ -0,0 +1,2 @@
                    +covered line with partial branch
                    +covered line with uncovered branches
                    """),
                MinPatchBranchPercent: 50));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        var markdown = CoverageGateReportWriter.RenderMarkdown(result);

        Assert.False(result.Passed);
        Assert.Equal(100, result.PatchLineCoverage?.Percent);
        Assert.Equal(25, result.PatchBranchCoverage?.Percent);
        Assert.Contains("| Patch lines | 100.00% (2/2 measurable, 2 changed) | 100% | pass |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Patch branches | 25.00% (1/4 measurable, 2 changed) | 50% | fail |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_MergesDuplicateCoberturaLines_ForPatchLineAndBranchCoverage()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="3" lines-valid="4" branches-covered="6" branches-valid="8">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="src/Foo.cs">
                      <lines>
                        <line number="7" hits="0" />
                        <line number="8" hits="1" branch="true" condition-coverage="100% (2/2)" />
                        <line number="9" hits="0" />
                        <line number="10" hits="0" branch="true" condition-coverage="50% (1/2)" />
                      </lines>
                    </class>
                    <class name="Example.Foo.Partial" filename="src/Foo.cs">
                      <lines>
                        <line number="7" hits="1" branch="true" condition-coverage="50% (1/2)" />
                        <line number="8" hits="0" />
                        <line number="9" hits="0" />
                        <line number="10" hits="1" branch="true" condition-coverage="100% (2/2)" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            75,
            75,
            false,
            null,
            new CoveragePatchRequest(
                temp.Path,
                "origin/main",
                75,
                _ => Task.FromResult("""
                    diff --git a/src/Foo.cs b/src/Foo.cs
                    index 0000000..1111111 100644
                    --- a/src/Foo.cs
                    +++ b/src/Foo.cs
                    @@ -0,0 +7,4 @@
                    +line seven
                    +line eight
                    +line nine
                    +line ten
                    """),
                MinPatchBranchPercent: 75));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(4, result.PatchLineCoverage?.MeasurableLines);
        Assert.Equal(3, result.PatchLineCoverage?.CoveredLines);
        Assert.Equal(75, result.PatchLineCoverage?.Percent);
        Assert.Equal(8, result.PatchBranchCoverage?.MeasurableBranches);
        Assert.Equal(6, result.PatchBranchCoverage?.CoveredBranches);
        Assert.Equal(75, result.PatchBranchCoverage?.Percent);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsMalformedLineConditionCoverage_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="src/Foo.cs">
                      <lines>
                        <line number="1" hits="1" branch="true" condition-coverage="100% (2)" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            0,
            0,
            false,
            null,
            new CoveragePatchRequest(
                temp.Path,
                "origin/main",
                null,
                _ => Task.FromResult("""
                    diff --git a/src/Foo.cs b/src/Foo.cs
                    index 0000000..1111111 100644
                    --- a/src/Foo.cs
                    +++ b/src/Foo.cs
                    @@ -0,0 +1,1 @@
                    +changed
                    """)));

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV006", exception.Message, StringComparison.Ordinal);
        Assert.Contains("condition coverage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_FailsGate_WhenPatchLineCoverageIsBelowThreshold()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="100" lines-valid="100" branches-covered="10" branches-valid="10">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="src/Foo.cs">
                      <lines>
                        <line number="1" hits="1" />
                        <line number="2" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            95,
            85,
            false,
            null,
            new CoveragePatchRequest(temp.Path, "origin/main", 75, _ => Task.FromResult("""
                diff --git a/src/Foo.cs b/src/Foo.cs
                index 0000000..1111111 100644
                --- a/src/Foo.cs
                +++ b/src/Foo.cs
                @@ -0,0 +1,2 @@
                +covered
                +uncovered
                """)));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        var markdown = CoverageGateReportWriter.RenderMarkdown(result);

        Assert.False(result.Passed);
        Assert.Equal(50, result.PatchLineCoverage?.Percent);
        Assert.Contains("| Patch lines | 50.00% (1/2 measurable, 2 changed) | 75% | fail |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_PassesPatchGate_WhenDiffHasNoMeasurableChangedLines()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="100" lines-valid="100" branches-covered="10" branches-valid="10" />
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            95,
            85,
            false,
            null,
            new CoveragePatchRequest(temp.Path, "origin/main", 95, _ => Task.FromResult("""
                diff --git a/README.md b/README.md
                index 0000000..1111111 100644
                --- a/README.md
                +++ b/README.md
                @@ -1,0 +1,2 @@
                +docs
                +more docs
                """)));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        var markdown = CoverageGateReportWriter.RenderMarkdown(result);

        Assert.True(result.Passed);
        Assert.Equal(100, result.PatchLineCoverage?.Percent);
        Assert.Equal(0, result.PatchLineCoverage?.MeasurableLines);
        Assert.Contains(
            "| Patch lines | 100.00% (no measurable changed lines, 2 changed) | 95% | pass |",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsPatchCoverage_WhenNoPatchThresholdIsConfigured()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="100" lines-valid="100" branches-covered="10" branches-valid="10">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="src/Foo.cs">
                      <lines>
                        <line number="1" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            95,
            85,
            false,
            null,
            new CoveragePatchRequest(temp.Path, "origin/main", null, _ => Task.FromResult("""
                diff --git a/src/Foo.cs b/src/Foo.cs
                index 0000000..1111111 100644
                --- a/src/Foo.cs
                +++ b/src/Foo.cs
                @@ -0,0 +1,1 @@
                +covered
                """)));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
        var markdown = CoverageGateReportWriter.RenderMarkdown(result);

        Assert.True(result.Passed);
        Assert.Null(result.MinPatchLinePercent);
        Assert.Contains("| Patch lines | 100.00% (1/1 measurable, 1 changed) | report | reported |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_NormalizesAbsoluteCoveragePathsUnderRepositoryRoot()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var sourceDirectory = Path.Join(temp.Path, "src");
        Directory.CreateDirectory(sourceDirectory);
        var sourceFile = Path.Join(sourceDirectory, "Foo.cs");
        var coverage = temp.WriteCoverage($$"""
            <coverage lines-covered="100" lines-valid="100" branches-covered="10" branches-valid="10">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="{{sourceFile}}">
                      <lines>
                        <line number="3" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            95,
            85,
            false,
            null,
            new CoveragePatchRequest(temp.Path, "origin/main", 100, _ => Task.FromResult("""
                diff --git a/src/Foo.cs b/src/Foo.cs
                index 0000000..1111111 100644
                --- a/src/Foo.cs
                +++ b/src/Foo.cs
                @@ -0,0 +3,1 @@
                +covered
                """)));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(1, result.PatchLineCoverage?.MeasurableLines);
        Assert.Equal(1, result.PatchLineCoverage?.CoveredLines);
    }

    [Fact]
    public async Task EvaluateAsync_IgnoresAbsoluteCoveragePathsOutsideRepositoryRoot()
    {
        using var repo = TempDirectory.Create("appsurface-coverage-repo-");
        using var outside = TempDirectory.Create("appsurface-coverage-outside-");
        var sourceFile = Path.Join(outside.Path, "Foo.cs");
        var coverage = repo.WriteCoverage($$"""
            <coverage lines-covered="100" lines-valid="100" branches-covered="10" branches-valid="10">
              <packages>
                <package name="Example">
                  <classes>
                    <class name="Example.Foo" filename="{{sourceFile}}">
                      <lines>
                        <line number="3" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        var request = new CoverageGateRequest(
            coverage,
            repo.Path,
            95,
            85,
            false,
            null,
            new CoveragePatchRequest(repo.Path, "origin/main", 100, _ => Task.FromResult("""
                diff --git a/src/Foo.cs b/src/Foo.cs
                index 0000000..1111111 100644
                --- a/src/Foo.cs
                +++ b/src/Foo.cs
                @@ -0,0 +3,1 @@
                +covered
                """)));

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, result.PatchLineCoverage?.MeasurableLines);
    }

    [Fact]
    public void ParseChangedLines_TracksContextLinesAndIgnoresDeletedFiles()
    {
        var changedLines = PatchCoverageEvaluator.ParseChangedLines("""
            diff --git a/src/Foo.cs b/src/Foo.cs
            index 0000000..1111111 100644
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -10,2 +10,3 @@
             existing
            -removed
            +added after context
             existing again
            +added after second context
            diff --git a/src/Deleted.cs b/src/Deleted.cs
            deleted file mode 100644
            --- a/src/Deleted.cs
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -removed
            -removed
            """);

        Assert.True(changedLines.TryGetValue("src/Foo.cs", out var fooLines));
        Assert.Contains(11, fooLines);
        Assert.Contains(13, fooLines);
        Assert.DoesNotContain("src/Deleted.cs", changedLines.Keys);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsPatchThresholdWithoutDiffBase_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var command = new CoverageGateCommand
        {
            CoveragePath = coverage,
            OutputDirectory = temp.Path,
            MinLine = 0,
            MinBranch = 0,
            MinPatchLine = 85,
            MinPatchBranch = 85,
            NoGithubSummary = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("ASCOV007", exception.Message, StringComparison.Ordinal);
        Assert.Contains("require --diff-base", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadDiffAsync_ThrowsDiagnostic_WhenRepositoryRootDoesNotExist()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var missingRepositoryRoot = Path.Join(temp.Path, "missing");

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => GitDiffReader.ReadDiffAsync(missingRepositoryRoot, "origin/main", CancellationToken.None));

        Assert.Contains("ASCOV010", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadDiffAsync_ThrowsDiagnostic_WhenGitDiffExitsNonZero()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => GitDiffReader.ReadDiffAsync(temp.Path, "origin/main", CancellationToken.None));

        Assert.Contains("ASCOV010", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failed to read git diff", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsMissingCoverageFile_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var request = new CoverageGateRequest(Path.Join(temp.Path, "missing.xml"), temp.Path, 0, 0, false, null);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV001", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsBlankCoveragePath_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var request = new CoverageGateRequest(" ", temp.Path, 0, 0, false, null);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV001", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsInvalidPatchThresholdInRequest_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            0,
            0,
            false,
            null,
            new CoveragePatchRequest(temp.Path, "origin/main", 101, _ => Task.FromResult(string.Empty)));

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV007", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--min-patch-line", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsInvalidPatchBranchThresholdInRequest_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var request = new CoverageGateRequest(
            coverage,
            temp.Path,
            0,
            0,
            false,
            null,
            new CoveragePatchRequest(
                temp.Path,
                "origin/main",
                null,
                _ => Task.FromResult(string.Empty),
                MinPatchBranchPercent: 101));

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV007", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--min-patch-branch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsMalformedXml_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("<coverage lines-covered=\"1\"");
        var request = new CoverageGateRequest(coverage, temp.Path, 0, 0, false, null);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV006", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failed to parse", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsXmlWithoutCoverageRoot_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("<not-coverage />");
        var request = new CoverageGateRequest(coverage, temp.Path, 0, 0, false, null);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV006", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not contain", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsOutOfRangeRate_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage line-rate="1.1" branch-rate="0.8" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 0, 0, false, null);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV006", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line-rate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsOutOfRangeCounts_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="2" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 0, 0, false, null);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV006", exception.Message, StringComparison.Ordinal);
        Assert.Contains("counts are out of range", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsZeroValidCoverage_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="0" lines-valid="0" branches-covered="0" branches-valid="1" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 0, 0, false, null);

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("ASCOV006", exception.Message, StringComparison.Ordinal);
        Assert.Contains("zero valid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_IgnoresCoberturaDtd_WhenCoverageUsesReportGeneratorHeader()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 0, 0, false, null);

        var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_ObservesCancellationWhileReadingCoverage()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="1" />
            """);
        var request = new CoverageGateRequest(coverage, temp.Path, 0, 0, false, null);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CoverageGateEvaluator.EvaluateAsync(request, cancellation.Token));
    }

    [Fact]
    public async Task WriteAsync_AppendsSanitizedGithubSummary()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var summary = Path.Join(temp.Path, "github-step-summary.md");
        var request = new CoverageGateRequest(Path.Join(temp.Path, "coverage.cobertura.xml"), temp.Path, 50, 50, true, summary);
        var result = new CoverageGateResult(
            "coverage|with`markdown\u0001.cobertura.xml",
            new CoverageMetric(8, 10, 80),
            new CoverageMetric(7, 10, 70),
            50,
            50,
            true,
            Path.Join(temp.Path, "coverage-gate.json"),
            Path.Join(temp.Path, "coverage-gate.md"));

        await CoverageGateReportWriter.WriteAsync(result, request, CancellationToken.None);

        var text = File.ReadAllText(summary);
        Assert.Contains("Cobertura: coverage\\|with\\`markdown.cobertura.xml", text, StringComparison.Ordinal);
        Assert.Contains("coverage\\|with\\`markdown.cobertura.xml", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cobertura: `", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0001', text);
    }

    [Fact]
    public async Task WriteAsync_ThrowsDiagnostic_WhenGithubSummaryCannotBeWritten()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var request = new CoverageGateRequest(
            Path.Join(temp.Path, "coverage.cobertura.xml"),
            temp.Path,
            50,
            50,
            true,
            temp.Path);
        var result = new CoverageGateResult(
            Path.Join(temp.Path, "coverage.cobertura.xml"),
            new CoverageMetric(8, 10, 80),
            new CoverageMetric(7, 10, 70),
            50,
            50,
            true,
            Path.Join(temp.Path, "coverage-gate.json"),
            Path.Join(temp.Path, "coverage-gate.md"));

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => CoverageGateReportWriter.WriteAsync(result, request, CancellationToken.None));

        Assert.Contains("ASCOV008", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReportOutput_AllowsCoverageDirectory_WhenItIsCurrentDirectory()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(temp.Path);
            var coverage = temp.WriteCoverage("<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");

            CoverageOutputPathPolicy.ValidateReportOutput(coverage, temp.Path);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void ValidateReportOutput_RejectsCurrentDirectory_WhenItIsNotCoverageDirectory()
    {
        using var cwd = TempDirectory.Create("appsurface-coverage-cwd-");
        using var coverageDirectory = TempDirectory.Create("appsurface-coverage-input-");
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(cwd.Path);
            var coverage = coverageDirectory.WriteCoverage("<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");

            var exception = Assert.Throws<CommandException>(
                () => CoverageOutputPathPolicy.ValidateReportOutput(coverage, cwd.Path));

            Assert.Contains("ASCOV009", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void ValidateReportOutput_RejectsExistingFile_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");
        var output = Path.Join(temp.Path, "coverage-output");
        File.WriteAllText(output, string.Empty);

        var exception = Assert.Throws<CommandException>(
            () => CoverageOutputPathPolicy.ValidateReportOutput(coverage, output));

        Assert.Contains("ASCOV009", exception.Message, StringComparison.Ordinal);
        Assert.Contains("coverage report directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReportOutput_RejectsFilesystemRoot_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var coverage = temp.WriteCoverage("<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");
        var root = Path.GetPathRoot(temp.Path);
        Assert.False(string.IsNullOrWhiteSpace(root));

        var exception = Assert.Throws<CommandException>(
            () => CoverageOutputPathPolicy.ValidateReportOutput(coverage, root!));

        Assert.Contains("ASCOV009", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReportOutput_RejectsDirectoryContainingCoverageFile_WithDiagnostic()
    {
        using var temp = TempDirectory.Create("appsurface-coverage-gate-");
        var nested = Directory.CreateDirectory(Path.Join(temp.Path, "nested")).FullName;
        var coverage = Path.Join(nested, "coverage.cobertura.xml");
        File.WriteAllText(coverage, "<coverage lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" />");

        var exception = Assert.Throws<CommandException>(
            () => CoverageOutputPathPolicy.ValidateReportOutput(coverage, temp.Path));

        Assert.Contains("ASCOV009", exception.Message, StringComparison.Ordinal);
        Assert.Contains("must not contain", exception.Message, StringComparison.Ordinal);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var waitTask = process.WaitForExitAsync();
        await Task.WhenAll(standardOutputTask, standardErrorTask, waitTask);
        var standardError = await standardErrorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {standardError}");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create(string prefix)
        {
            var safePrefix = System.IO.Path.GetFileName(prefix);
            var path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                safePrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public string WriteCoverage(string contents)
        {
            var path = System.IO.Path.Join(Path, "coverage.cobertura.xml");
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
