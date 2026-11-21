using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises the public v3 workflow against pinned Git objects for screenshot baseline replacements.</summary>
[Collection("CoverageGate process state")]
public sealed class CoverageFanInVisualBaselineGitFixtureTests
{
    private const string VisualBaselineDiffPolicy = "appsurface-visual-baseline-png-v1";
    private const string StrictDiffPolicy = "appsurface-git-patch-v1";
    private const string ScreenshotPath =
        "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight/home-desktop-1440x1000.png";

    private static readonly byte[] OriginalPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
    private static readonly byte[] ReplacementPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYPgPAAEDAQAIicLsAAAAAElFTkSuQmCC");

    [Fact]
    public async Task Visual_baseline_and_measured_source_patch_are_accepted_and_verified()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommitPatchAsync(replaceScreenshot: true, changeSource: true);
        fixture.UseVisualBaselinePolicy();
        await fixture.CaptureAsync(Report(sourceLine: 2, hits: 1));
        await fixture.ComposeAsync();
        var patchHash = await fixture.ReadPatchHashAsync();

        var accepted = await CoverageFanIn.AcceptAsync(fixture.AcceptRequest() with
        { ExpectedPatchSha256 = patchHash });

        Assert.Equal(CoverageOperationOutcome.Succeeded, accepted.Outcome);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Verified);
        AssertMeasured(accepted.PatchApplicability!.Primary.Line, changedLines: 1, measurable: 1, covered: 1);
        AssertMeasured(accepted.PatchApplicability.Composed.Line, changedLines: 1, measurable: 1, covered: 1);
        AssertMeasured(accepted.PatchApplicability.Composed.Branch, changedLines: 1, measurable: 2, covered: 2);
        AssertVisualBaselineRetained(fixture.Accepted, ScreenshotPath);

        var verified = await CoverageFanIn.VerifyAsync(fixture.VerifyRequest());

        Assert.Equal(CoverageOperationOutcome.Succeeded, verified.Outcome);
        Assert.True(verified.Verified);
        Assert.Equal(accepted.PatchApplicability, verified.PatchApplicability);
    }

    [Fact]
    public async Task Screenshot_only_patch_verifies_zero_measured_changes()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommitPatchAsync(replaceScreenshot: true, changeSource: false);
        fixture.UseVisualBaselinePolicy();
        await fixture.CaptureAsync(Report(sourceLine: 1, hits: 1));
        await fixture.ComposeAsync();
        var patchHash = await fixture.ReadPatchHashAsync();

        var accepted = await CoverageFanIn.AcceptAsync(fixture.AcceptRequest() with
        { ExpectedPatchSha256 = patchHash });

        Assert.Equal(CoverageOperationOutcome.Succeeded, accepted.Outcome);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Verified);
        AssertNotApplicable(accepted.PatchApplicability!.Primary.Line, changedLines: 0);
        AssertNotApplicable(accepted.PatchApplicability.Primary.Branch, changedLines: 0);
        AssertNotApplicable(accepted.PatchApplicability.Composed.Line, changedLines: 0);
        AssertNotApplicable(accepted.PatchApplicability.Composed.Branch, changedLines: 0);
        AssertVisualBaselineRetained(fixture.Accepted, ScreenshotPath);

        var verified = await CoverageFanIn.VerifyAsync(fixture.VerifyRequest());

        Assert.Equal(CoverageOperationOutcome.Succeeded, verified.Outcome);
        Assert.True(verified.Verified);
        AssertNotApplicable(verified.PatchApplicability!.Composed.Line, changedLines: 0);
        AssertNotApplicable(verified.PatchApplicability.Composed.Branch, changedLines: 0);
    }

    [Fact]
    public async Task Visual_baseline_opt_in_keeps_uncovered_text_below_threshold()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommitPatchAsync(replaceScreenshot: true, changeSource: true);
        fixture.UseVisualBaselinePolicy(minPatchLine: 95, minPatchBranch: 85);
        await fixture.CaptureAsync(Report(sourceLine: 2, hits: 0, aggregateLineCovered: 0, aggregateBranchCovered: 0));
        await fixture.ComposeAsync();
        var patchHash = await fixture.ReadPatchHashAsync();

        var rejected = await CoverageFanIn.AcceptAsync(fixture.AcceptRequest() with
        { ExpectedPatchSha256 = patchHash });

        Assert.Equal(CoverageOperationOutcome.Rejected, rejected.Outcome);
        Assert.False(rejected.Accepted);
        Assert.False(rejected.Verified);
        AssertMeasured(rejected.PatchApplicability!.Composed.Line, changedLines: 1, measurable: 1, covered: 0);
        AssertMeasured(rejected.PatchApplicability.Composed.Branch, changedLines: 1, measurable: 2, covered: 0);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Cause == "threshold-failed");
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "diff.patch")));
        AssertVisualBaselineRetained(fixture.Accepted, ScreenshotPath);
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
    }

    [Fact]
    public async Task Omitted_visual_baseline_selector_keeps_the_strict_v3_diff_default()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommitPatchAsync(replaceScreenshot: true, changeSource: false);
        fixture.UseStrictV3Policy();
        await fixture.CaptureAsync(Report(sourceLine: 1, hits: 1));
        await fixture.ComposeAsync();
        var patchHash = await fixture.ReadPatchHashAsync();

        var rejected = await CoverageFanIn.AcceptAsync(fixture.AcceptRequest() with
        { ExpectedPatchSha256 = patchHash });

        Assert.Equal(CoverageOperationOutcome.Rejected, rejected.Outcome);
        Assert.False(rejected.Accepted);
        Assert.False(rejected.Verified);
        var diagnostic = Assert.Single(rejected.Diagnostics);
        Assert.Equal(StrictDiffPolicy, diagnostic.Context!["diffPolicyId"]);
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("rename-out")]
    [InlineData("rename-within")]
    public async Task VisualBaselinePolicyRefusesStructuralScreenshotChanges(string operation)
    {
        using var fixture = await Fixture.CreateAsync();
        var existing = Path.Join(fixture.Repository, ScreenshotPath);
        var sibling = Path.Join(Path.GetDirectoryName(existing), "another-home.png");
        switch (operation)
        {
            case "add": File.Copy(existing, sibling); break;
            case "delete": File.Delete(existing); break;
            case "rename-out": File.Move(existing, Path.Join(fixture.Repository, "other.png")); break;
            case "rename-within": File.Move(existing, sibling); break;
        }
        await fixture.CommitPatchAsync(replaceScreenshot: false, changeSource: false);
        fixture.UseVisualBaselinePolicy();
        await fixture.CaptureAsync(Report(sourceLine: 1, hits: 1));
        await fixture.ComposeAsync();

        var result = await CoverageFanIn.AcceptAsync(fixture.AcceptRequest() with
        { ExpectedPatchSha256 = await fixture.ReadPatchHashAsync() });

        Assert.Equal("unsupported-diff", Assert.Single(result.Diagnostics).Cause);
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("diff=custom")]
    public async Task PinnedAttributesForcingTextDiffRejectAcceptance(string attribute)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Temp.WriteFile("repo/.gitattributes", $"*.cs {attribute}\n");
        await fixture.CommitPatchAsync(replaceScreenshot: true, changeSource: true);
        fixture.UseVisualBaselinePolicy();
        await fixture.CaptureAsync(Report(sourceLine: 2, hits: 1));
        await fixture.ComposeAsync();

        var result = await CoverageFanIn.AcceptAsync(fixture.AcceptRequest() with
        { ExpectedPatchSha256 = await fixture.ReadPatchHashAsync() });

        Assert.Equal("unsupported-diff", Assert.Single(result.Diagnostics).Cause);
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
    }

    [Fact]
    public async Task ScreenshotListedAsInstrumentedSourceRejectsAcceptance()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommitPatchAsync(replaceScreenshot: true, changeSource: false);
        fixture.UseVisualBaselinePolicy();
        var report = Report(sourceLine: 1, hits: 1).Replace("src/Example.cs", ScreenshotPath, StringComparison.Ordinal);
        await fixture.CaptureAsync(report);
        await fixture.ComposeAsync();

        var rejected = await CoverageFanIn.AcceptAsync(fixture.AcceptRequest() with
        { ExpectedPatchSha256 = await fixture.ReadPatchHashAsync() });

        Assert.False(rejected.Accepted);
        Assert.Equal("report-invalid", Assert.Single(rejected.Diagnostics).Cause);
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
    }

    private static string Report(int sourceLine, int hits, int aggregateLineCovered = 1, int aggregateBranchCovered = 2) => $$"""
        <coverage lines-covered="{{aggregateLineCovered}}" lines-valid="1" branches-covered="{{aggregateBranchCovered}}" branches-valid="2" line-rate="{{aggregateLineCovered}}" branch-rate="{{aggregateBranchCovered / 2}}">
          <packages><package name="Fixture"><classes>
            <class name="Example" filename="src/Example.cs"><lines>
              <line number="{{sourceLine}}" hits="{{hits}}" branch="true" condition-coverage="{{(hits == 0 ? "0" : "100")}}% ({{(hits == 0 ? "0" : "2")}}/2)"/>
            </lines></class>
          </classes></package></packages>
        </coverage>
        """;

    private static void AssertMeasured(CoveragePatchMetricResult metric, int changedLines,
        int measurable, int covered)
    {
        Assert.Equal(CoveragePatchApplicabilityOutcome.Measured, metric.Outcome);
        Assert.Equal("reported-points", metric.Reason);
        Assert.Equal(changedLines, metric.ChangedLines);
        Assert.Equal(measurable, metric.Measurable);
        Assert.Equal(covered, metric.Covered);
        Assert.Equal(measurable == 0 ? null : covered * 100m / measurable, metric.Percent);
    }

    private static void AssertNotApplicable(CoveragePatchMetricResult metric, int changedLines)
    {
        Assert.Equal(CoveragePatchApplicabilityOutcome.NotApplicable, metric.Outcome);
        Assert.Equal("no-reported-patch-points", metric.Reason);
        Assert.Equal(changedLines, metric.ChangedLines);
        Assert.Equal(0, metric.Measurable);
        Assert.Equal(0, metric.Covered);
        Assert.Null(metric.Percent);
    }

    private static void AssertVisualBaselineRetained(string acceptedDirectory, string expectedPath)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Join(acceptedDirectory, "diff-metadata.json")));
        var change = Assert.Single(metadata.RootElement.GetProperty("changes").EnumerateArray(),
            item => item.GetProperty("newPath").GetString() == expectedPath);
        Assert.Equal("modified", change.GetProperty("kind").GetString());
        Assert.Equal(expectedPath, change.GetProperty("oldPath").GetString());
        Assert.Empty(change.GetProperty("addedRanges").EnumerateArray());

        var patch = File.ReadAllText(Path.Join(acceptedDirectory, "diff.patch"));
        Assert.Contains(expectedPath, patch, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private const string SourcePath = "src/Example.cs";
        private const string ProjectPath = "checks/Verification.csproj";
        private const string InitialSource = "public class Example {\n}\n";
        private const string ChangedSource = "public class Example {\n    public int Added => 1;\n}\n";
        private const string TimingsJson = """{"configuration":"Release","coverageDriver":"collector","merge":{"exitCode":0},"artifacts":{"coverageFiles":1},"projects":[{"project":"checks/Verification.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"}]}""";

        private static readonly string PolicyStoreEnvironment = "APPSURFACE_ARTIFACT_HOME";
        private readonly string? _previousStore = Environment.GetEnvironmentVariable(PolicyStoreEnvironment);

        internal TestDirectory Temp { get; } = TestDirectory.Create();
        internal string Repository => Path.Join(Temp.Path, "repo");
        internal string Captured => Path.Join(Temp.Path, "captured");
        internal string Composed => Path.Join(Temp.Path, "composed");
        internal string Accepted => Path.Join(Temp.Path, "accepted");
        internal string ReportPath => Path.Join(Temp.Path, "producer", "coverage.xml");
        internal string TimingsPath => Path.Join(Temp.Path, "producer", "timings.json");
        internal string BaseCommit { get; private set; } = null!;
        internal CoverageFanInPolicy Policy { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Environment.SetEnvironmentVariable(PolicyStoreEnvironment, Path.Join(fixture.Temp.Path, "store"));
            fixture.Temp.WriteFile("producer/coverage.xml", string.Empty);
            fixture.Temp.WriteFile("producer/timings.json", TimingsJson);
            fixture.Temp.WriteFile(Path.Join("repo", ProjectPath),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            fixture.Temp.WriteFile(Path.Join("repo", SourcePath), InitialSource);
            fixture.Temp.WriteFile("repo/.gitattributes", "*.png -diff\n");
            fixture.WritePng(OriginalPng);
            await fixture.GitAsync("init");
            await fixture.GitAsync("config", "user.email", "coverage-tests@example.invalid");
            await fixture.GitAsync("config", "user.name", "AppSurface coverage tests");
            await fixture.GitAsync("add", "-A");
            await fixture.GitAsync("commit", "-m", "fixture baseline");
            fixture.BaseCommit = await fixture.GitOutputAsync("rev-parse", "HEAD");
            var tree = await fixture.GitOutputAsync("rev-parse", "HEAD^{tree}");
            var sdkVersion = await fixture.DotnetVersionAsync();
            fixture.Policy = new(new(fixture.BaseCommit, tree, sdkVersion, "Release"),
                new("local", "visual-baseline-tests", Guid.NewGuid().ToString("N"), 1),
                [ProjectPath], [], new(CoverageGateMode.AggregateOnly));
            return fixture;
        }

        internal void UseVisualBaselinePolicy(decimal minPatchLine = 95, decimal minPatchBranch = 85) =>
            UseV3Policy(VisualBaselineDiffPolicy, minPatchLine, minPatchBranch);

        internal void UseStrictV3Policy() => UseV3Policy(null, minPatchLine: 95, minPatchBranch: 85);

        private void UseV3Policy(string? diffPolicyId, decimal minPatchLine, decimal minPatchBranch)
        {
            Policy = Policy with
            {
                ContractVersion = 3,
                ReportProfileId = "coverlet-collector-cobertura-v1",
                DiffPolicyId = diffPolicyId,
                Gate = new(CoverageGateMode.AggregateAndPatch, MinLine: 0, MinBranch: 0,
                    MinPatchLine: minPatchLine, MinPatchBranch: minPatchBranch, DiffBase: BaseCommit)
            };
        }

        internal async Task CommitPatchAsync(bool replaceScreenshot, bool changeSource)
        {
            if (replaceScreenshot) WritePng(ReplacementPng);
            if (changeSource) Temp.WriteFile(Path.Join("repo", SourcePath), ChangedSource);
            await GitAsync("add", "-A");
            await GitAsync("commit", "-m", "visual baseline and optional source change");
            Policy = Policy with
            {
                Source = Policy.Source with
                {
                    Commit = await GitOutputAsync("rev-parse", "HEAD"),
                    Tree = await GitOutputAsync("rev-parse", "HEAD^{tree}")
                }
            };
        }

        internal async Task CaptureAsync(string report)
        {
            await File.WriteAllTextAsync(ReportPath, report);
            var result = await CoverageFanIn.CaptureAsync(new(Policy, Repository, Captured, "primary",
                ReportPath, TimingsPath));
            Assert.Equal(CoverageOperationOutcome.Succeeded, result.Outcome);
        }

        internal async Task ComposeAsync()
        {
            var result = await CoverageFanIn.ComposeAsync(new(Policy, Repository, Captured, [], Composed));
            Assert.Equal(CoverageOperationOutcome.Succeeded, result.Outcome);
        }

        internal CoverageAcceptRequest AcceptRequest() => new(Policy, Repository, Captured, [], Composed, Accepted);

        internal CoverageVerifyRequest VerifyRequest() => new(Policy, Repository, Captured, [], Composed, Accepted);

        internal async Task<string> ReadPatchHashAsync()
        {
            var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var args = new List<string>
            {
                "--no-pager", "--no-replace-objects", "-c", "core.fsmonitor=false", "-c", "core.quotePath=true",
                "-c", "core.attributesFile=" + nullDevice, "-c", "diff.renames=true", "-c", "diff.renameLimit=1000",
                "-c", "diff.orderFile=" + nullDevice, "-c", "diff.mnemonicPrefix=false", "-c", "diff.noprefix=false",
                "-c", "diff.suppressBlankEmpty=false", "diff", "--no-color", "--no-ext-diff", "--no-textconv",
                "--no-relative", "--no-renames", "--find-renames=50%", "--no-rename-empty", "-l1000",
                "--diff-algorithm=myers", "--no-indent-heuristic", "--ignore-submodules=none", "--submodule=short",
                "--patch", "--unified=0", "--inter-hunk-context=0", "--full-index", "--src-prefix=a/",
                "--dst-prefix=b/", "--output-indicator-new=+", "--output-indicator-old=-",
                "--output-indicator-context= ", Policy.Gate.DiffBase!, Policy.Source.Commit, "--"
            };
            var patch = await RunProcessAsync("git", Repository, args, isolateGitEnvironment: true,
                gitAttributeSource: Policy.Source.Commit, trimOutput: false);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(patch)));
        }

        private void WritePng(byte[] bytes)
        {
            var path = TestPathUtils.PathUnder(Repository, ScreenshotPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        private async Task<string> DotnetVersionAsync() =>
            (await RunProcessAsync("dotnet", Temp.Path, ["--version"])).Trim();

        private Task GitAsync(params string[] args) =>
            RunProcessAsync("git", Repository, args, isolateGitEnvironment: true);

        private Task<string> GitOutputAsync(params string[] args) =>
            RunProcessAsync("git", Repository, args, isolateGitEnvironment: true);

        private static async Task<string> RunProcessAsync(string executable, string workingDirectory,
            IEnumerable<string> arguments, bool isolateGitEnvironment = false, string? gitAttributeSource = null,
            bool trimOutput = true)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (isolateGitEnvironment)
            {
                var environment = process.StartInfo.Environment;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                foreach (var name in environment.Keys.Where(name => name.StartsWith("GIT_", comparison)).ToArray())
                    environment.Remove(name);

                var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                environment["GIT_CONFIG_NOSYSTEM"] = "1";
                environment["GIT_CONFIG_GLOBAL"] = nullDevice;
                environment["GIT_CONFIG_SYSTEM"] = nullDevice;
                environment["GIT_ATTR_NOSYSTEM"] = "1";
                if (gitAttributeSource is not null) environment["GIT_ATTR_SOURCE"] = gitAttributeSource;
                environment["GIT_TERMINAL_PROMPT"] = "0";
                environment["GIT_OPTIONAL_LOCKS"] = "0";
                environment["GIT_NO_LAZY_FETCH"] = "1";
                environment["LC_ALL"] = "C";
                environment["LANG"] = "C";
            }
            Assert.True(process.Start());
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var outputText = await output;
            Assert.True(process.ExitCode == 0, await error);
            return trimOutput ? outputText.TrimEnd('\r', '\n') : outputText;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PolicyStoreEnvironment, _previousStore);
            Temp.Dispose();
        }
    }
}
