using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>
/// Exercises the real child runner and one end-to-end producer run against a generated xunit fixture project.
/// </summary>
public sealed class TestEvidenceProducerProcessTests
{
    private const string HiddenTestName = "Fixture.Tests.Sample.Hidden_marker_case";

    [Fact]
    public async Task Runner_ShouldCaptureOutputAndExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var result = await new BoundedChildProcessRunner().RunAsync(Shell(directory.Path, "printf out; printf err 1>&2; pwd; exit 3"), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(3, result.ExitCode);
        Assert.StartsWith("out", result.StandardOutput, StringComparison.Ordinal);
        Assert.EndsWith(Path.GetFileName(directory.Path) + "\n", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("err", result.StandardError);
        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public async Task Runner_ShouldApplyTheFixedEnvironment()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var result = await new BoundedChildProcessRunner().RunAsync(
            Shell(directory.Path, "printf '%s|%s|%s|%s|%s|%s' \"$DOTNET_CLI_USE_MSBUILD_SERVER\" \"$MSBUILDDISABLENODEREUSE\" \"$DOTNET_NOLOGO\" \"$DOTNET_SKIP_FIRST_TIME_EXPERIENCE\" \"$VSLANG\" \"$DOTNET_CLI_UI_LANGUAGE\""),
            CancellationToken.None);

        Assert.Equal("0|1|1|1|1033|en", result.StandardOutput);
    }

    [Fact]
    public async Task Runner_ShouldReportAnExecutableThatCannotStart()
    {
        using var directory = TestDirectory.Create();

        var result = await new BoundedChildProcessRunner().RunAsync(
            new ChildProcessRequest(Path.Join(directory.Path, "no-such-dotnet"), ["--version"], directory.Path),
            CancellationToken.None);

        Assert.False(result.Started);
    }

    [Fact]
    public async Task Runner_ShouldCloseStandardInputSoAReadingChildEndsAtOnce()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var watch = Stopwatch.StartNew();

        var result = await new BoundedChildProcessRunner().RunAsync(Shell(directory.Path, "cat; echo after-stdin"), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("after-stdin\n", result.StandardOutput);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Runner_ShouldAbandonOutputHeldOpenByAGrandchildAfterExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var watch = Stopwatch.StartNew();

        var result = await new BoundedChildProcessRunner(TimeSpan.FromMilliseconds(300)).RunAsync(
            Shell(directory.Path, "echo first; (sleep 30) & exit 0"),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("first", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(result.OutputTruncated);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Runner_ShouldKillTheProcessTreeOnCancellationAndBoundTheDrain()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BoundedChildProcessRunner(TimeSpan.FromMilliseconds(300)).RunAsync(
            Shell(directory.Path, "(sleep 60) & sleep 60"),
            cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Runner_ShouldThrowWhenCancellationArrivesAfterTheChildExited()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // The child exits at once while a grandchild holds the output pipes, so cancellation lands during the drain.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BoundedChildProcessRunner(TimeSpan.FromSeconds(3)).RunAsync(
            Shell(directory.Path, "(sleep 30) & exit 0"),
            cancellation.Token));
    }

    [Fact]
    public async Task Runner_ShouldTruncateOutputAtTheCap()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();

        var result = await new BoundedChildProcessRunner().RunAsync(
            Shell(directory.Path, "head -c 9000000 /dev/zero | tr '\\0' 'a'"),
            CancellationToken.None);

        Assert.True(result.OutputTruncated);
        Assert.Equal(BoundedChildProcessRunner.MaximumCapturedBytesPerStream, result.StandardOutput.Length);
    }

    [Fact]
    public async Task Runner_ShouldRejectANullRequestAndAnAlreadyCancelledToken()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => new BoundedChildProcessRunner().RunAsync(null!, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new BoundedChildProcessRunner().RunAsync(new ChildProcessRequest("dotnet", [], "."), cancelled.Token));
    }

    [Fact]
    public async Task Producer_ShouldReportAToolchainThatCannotStart()
    {
        using var directory = TestDirectory.Create();
        var repository = Path.GetFullPath(directory.Path);
        File.WriteAllText(Path.Join(repository, "A.csproj"), "<Project />");
        var producer = new TestEvidenceProducer("fixture-tests", new TestEvidenceProducerOptions(repository)
        {
            DotnetPath = Path.Join(repository, "no-such-dotnet"),
            Progress = TextWriter.Null,
        });

        var result = await producer.ProduceAsync(Context(repository, Path.Join(repository, "out"), new EvidenceTestProject("A.csproj")), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        var project = Assert.Single(producer.LastProjects);
        Assert.Equal(TestRunReason.ToolchainUnavailable, project.Reason);
        Assert.Contains("no-such-dotnet", project.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// One generated xunit project, four producer runs: a filter that selects only passing tests, the whole project
    /// (a failing and a skipped test), a filter that selects only the skipped test, and a filter that matches nothing.
    /// </summary>
    [Fact]
    public async Task Producer_ShouldReconcileARealFixtureProjectEndToEnd()
    {
        using var directory = TestDirectory.Create();
        var repository = Path.GetFullPath(Path.Join(directory.Path, "repo"));
        var output = Path.GetFullPath(Path.Join(directory.Path, "output"));
        WriteFixtureProject(repository);

        var passing = await RunAsync(repository, output, "FullyQualifiedName~Fixture.Tests.Sample.Pass|FullyQualifiedName~Hidden_marker_case", minimumTests: 3);
        Assert.True(
            passing.Result.Outcome == EvidenceProducerOutcome.Passed,
            $"{passing.Projects[0].Reason}/{passing.Projects[0].SubReason} at {passing.Projects[0].Phase}: {passing.Projects[0].Detail}");
        Assert.Equal([EvidenceTestProducerContract.AssertionId], passing.Result.SatisfiedAssertionIds);
        Assert.Equal(new TestRunCounts(4, 4, 4, 0, 0), passing.Projects[0].Counts);
        Assert.Equal("1 project(s), 4 discovered, 4 executed. No coverage claim was made.", passing.Result.Diagnostic);
        using (var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Join(output, "artifacts", "fixture-tests", "test", "test-receipt.json"))))
        {
            Assert.Equal("Complete", receipt.RootElement.GetProperty("projects")[0].GetProperty("reason").GetString());
            Assert.Equal(4, receipt.RootElement.GetProperty("projects")[0].GetProperty("discovered").GetInt32());
        }

        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("Hidden_marker_case", File.ReadAllText(file), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Hidden_marker_case", passing.Progress, StringComparison.Ordinal);
        Assert.Contains("[1/1] running 4 tests", passing.Progress, StringComparison.Ordinal);
        Assert.False(Directory.Exists(passing.Scratch));

        var whole = await RunAsync(repository, output, filter: null, minimumTests: null);
        Assert.Equal(EvidenceProducerOutcome.Failed, whole.Result.Outcome);
        Assert.Equal(TestRunReason.TestsFailed, whole.Projects[0].Reason);
        Assert.Equal(new TestRunCounts(6, 5, 4, 1, 1), whole.Projects[0].Counts);
        Assert.Empty(whole.Result.SatisfiedAssertionIds);

        var skipped = await RunAsync(repository, output, "FullyQualifiedName~Skipped_case", minimumTests: null);
        Assert.Equal(TestRunReason.TestsSkipped, skipped.Projects[0].Reason);

        var none = await RunAsync(repository, output, "FullyQualifiedName~No.Such.Test", minimumTests: null);
        Assert.Equal(EvidenceProducerOutcome.Failed, none.Result.Outcome);
        Assert.Equal(TestRunReason.EmptyDiscovery, none.Projects[0].Reason);
        Assert.Equal("discover", none.Projects[0].Phase);
    }

    private static async Task<(EvidenceProducerResult Result, IReadOnlyList<TestProjectOutcome> Projects, string Progress, string Scratch)> RunAsync(
        string repository,
        string output,
        string? filter,
        int? minimumTests)
    {
        var scratch = Path.Join(Path.GetDirectoryName(repository)!, "scratch-" + Guid.NewGuid().ToString("N"));
        var progress = new StringWriter();
        var producer = new TestEvidenceProducer("fixture-tests", new TestEvidenceProducerOptions(repository)
        {
            ScratchRoot = scratch,
            DotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet",
            DiagnosticsDirectory = Path.Join(output, "diagnostics"),
            Progress = progress,
        });
        if (Directory.Exists(Path.Join(output, "artifacts")))
        {
            Directory.Delete(Path.Join(output, "artifacts"), recursive: true);
        }

        var result = await producer.ProduceAsync(
            Context(repository, output, new EvidenceTestProject("tests/Fixture.Tests/Fixture.Tests.csproj") { Filter = filter, MinimumTests = minimumTests }),
            CancellationToken.None);
        return (result, producer.LastProjects, progress.ToString(), scratch);
    }

    private static EvidenceProducerContext Context(string repository, string output, EvidenceTestProject project)
    {
        _ = repository;
        var declaration = new EvidenceProducerDeclaration(
            "fixture-tests",
            EvidenceTestProducerContract.Kind,
            "1.0.0",
            [],
            [EvidenceTestProducerContract.AssertionId],
            [new EvidenceArtifactSlot(EvidenceTestProducerContract.ReceiptSlotLogicalName, "test", EvidenceTestProducerContract.ReceiptMediaType, true, 65536)],
            600)
        {
            Test = new EvidenceTestDeclaration([project]),
        };
        var policy = new EvidencePolicy(
            "sample",
            "1",
            "tests",
            [
                new EvidenceProfile(
                    "tests",
                    EvidenceProfileScope.Targeted,
                    [],
                    [declaration],
                    [new EvidenceObligation("tests-complete", "tooling", "Selected tests ran and passed.", ["fixture-tests"], EvidenceTestProducerContract.AssertionId)]),
            ],
            []);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("scripts/build.sh")]);
        return new EvidenceProducerContext(plan, declaration, TimeProvider.System, new EvidenceArtifactWriter(declaration, Path.Join(output, "artifacts", "fixture-tests")));
    }

    private static ChildProcessRequest Shell(string workingDirectory, string script) => new("/bin/sh", ["-c", script], workingDirectory);

    private static void WriteFixtureProject(string repository)
    {
        var projectDirectory = Path.Join(repository, "tests", "Fixture.Tests");
        Directory.CreateDirectory(projectDirectory);
        // Empty props, targets and package-version files stop the generated project from importing this repository's build settings.
        File.WriteAllText(Path.Join(repository, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Join(repository, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Join(repository, "Directory.Packages.props"), "<Project />");
        File.WriteAllText(
            Path.Join(projectDirectory, "Fixture.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <Nullable>enable</Nullable>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Join(projectDirectory, "Sample.cs"),
            """
            using Xunit;

            namespace Fixture.Tests;

            public class Sample
            {
                [Fact]
                public void Pass_one() { }

                [Theory]
                [InlineData(1)]
                [InlineData(2)]
                public void Pass_rows(int value) => Assert.True(value > 0);

                [Fact]
                public void Hidden_marker_case() { }

                [Fact]
                public void Broken_case() => Assert.True(false);

                [Fact(Skip = "fixture")]
                public void Skipped_case() { }
            }
            """);
    }
}
