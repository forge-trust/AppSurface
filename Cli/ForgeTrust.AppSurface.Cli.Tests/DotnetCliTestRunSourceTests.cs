using System.Text;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class DotnetCliTestRunSourceTests
{
    private const string Marker = DotnetCliTestRunSource.ListingMarker;

    [Fact]
    public void TryParseListing_ShouldReadFourSpaceNamesAfterTheSingleMarker()
    {
        var output = $"Test run for /x/A.dll (.NETCoreApp,Version=v10.0)\n     ignored before marker\n{Marker}\n    A.One\n    A.Two(x: 1)\n\n    A.One\n";

        Assert.True(DotnetCliTestRunSource.TryParseListing(output, out var identities));
        Assert.Equal(["A.One", "A.Two(x: 1)", "A.One"], identities);
    }

    [Fact]
    public void TryParseListing_ShouldStripOneTrailingCarriageReturnAndKeepInnerText()
    {
        Assert.True(DotnetCliTestRunSource.TryParseListing($"{Marker}\r\n    A.One  trailing \r\n", out var identities));
        Assert.Equal(["A.One  trailing "], identities);
    }

    [Fact]
    public void TryParseListing_ShouldAcceptTheZeroMatchNoticeAsAnEmptyListing()
    {
        var output = $"{Marker}\nNo test matches the given testcase filter `FullyQualifiedName~Nope` in /x/A.dll\n";

        Assert.True(DotnetCliTestRunSource.TryParseListing(output, out var identities));
        Assert.Empty(identities);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    A.One\n")]
    [InlineData("The following Tests are available:\n    A.One\nThe following Tests are available:\n    A.One\n")]
    [InlineData("The following Tests are available:\n    A.One\nTest run for /x/B.dll (.NETCoreApp,Version=v9.0)\n")]
    [InlineData("The following Tests are available:\n     FiveSpaces\n")]
    [InlineData("The following Tests are available:\n   ThreeSpaces\n")]
    [InlineData("The following Tests are available:\n    \n")]
    [InlineData("The following Tests are available:\n\tTabbed\n")]
    [InlineData("The following Tests are available: \n    A.One\n")]
    public void TryParseListing_ShouldRejectEveryOtherShape(string output)
    {
        Assert.False(DotnetCliTestRunSource.TryParseListing(output, out var identities));
        Assert.Empty(identities);
    }

    [Fact]
    public void TryReadTrx_ShouldCountOnlyTopLevelResultsAndMapOutcomes()
    {
        var trx = Trx(
            total: 5,
            """
            <UnitTestResult testName="A.Pass" outcome="Passed" />
            <UnitTestResult testName="A.Fail" outcome="Failed" />
            <UnitTestResult testName="A.Skip" outcome="NotExecuted" />
            <UnitTestResult testName="A.Odd" outcome="Inconclusive" />
            <UnitTestResult testName="A.Parent">
              <InnerResults><UnitTestResult testName="A.Inner" outcome="Passed" /></InnerResults>
            </UnitTestResult>
            """);

        Assert.True(DotnetCliTestRunSource.TryReadTrx(Stream(trx), out var results));
        Assert.Equal(
            [
                new TestCaseResult("A.Pass", TestCaseOutcome.Passed),
                new TestCaseResult("A.Fail", TestCaseOutcome.Failed),
                new TestCaseResult("A.Skip", TestCaseOutcome.Skipped),
                new TestCaseResult("A.Odd", TestCaseOutcome.Failed),
                new TestCaseResult("A.Parent", TestCaseOutcome.Failed),
            ],
            results);
    }

    [Fact]
    public void TryReadTrx_ShouldAcceptAnEmptyRun() =>
        Assert.True(DotnetCliTestRunSource.TryReadTrx(Stream(Trx(0, string.Empty)), out _));

    [Fact]
    public void TryReadTrx_ShouldRejectCounterMismatchMissingCountersMissingNameMalformedXmlAndDtds()
    {
        const string one = """<UnitTestResult testName="A.Pass" outcome="Passed" />""";

        Assert.False(DotnetCliTestRunSource.TryReadTrx(Stream(Trx(2, one)), out _));
        Assert.False(DotnetCliTestRunSource.TryReadTrx(Stream($"<TestRun><Results>{one}</Results></TestRun>"), out _));
        Assert.False(DotnetCliTestRunSource.TryReadTrx(Stream(Trx(1, """<UnitTestResult outcome="Passed" />""")), out _));
        Assert.False(DotnetCliTestRunSource.TryReadTrx(Stream("<TestRun><Results>"), out _));
        Assert.False(DotnetCliTestRunSource.TryReadTrx(Stream("<!DOCTYPE TestRun [<!ENTITY x \"y\">]>" + Trx(1, one)), out _));
    }

    [Fact]
    public void TryReadResults_ShouldRequireExactlyTheFixedFile()
    {
        using var directory = TestDirectory.Create();
        var results = Path.Join(directory.Path, "results");

        Assert.False(DotnetCliTestRunSource.TryReadResults(results, out _));
        Directory.CreateDirectory(results);
        Assert.False(DotnetCliTestRunSource.TryReadResults(results, out _));
        File.WriteAllText(Path.Join(results, "other.trx"), Trx(0, string.Empty));
        Assert.False(DotnetCliTestRunSource.TryReadResults(results, out _));
        File.Move(Path.Join(results, "other.trx"), TestPathUtils.PathUnder(results, DotnetCliTestRunSource.ResultsFileName));
        Assert.True(DotnetCliTestRunSource.TryReadResults(results, out _));
        // Test frameworks keep result attachments in subdirectories; those are neither counted nor opened.
        Directory.CreateDirectory(Path.Join(results, "attachments"));
        File.WriteAllText(Path.Join(results, "attachments", "inner.trx"), "not a results file");
        Assert.True(DotnetCliTestRunSource.TryReadResults(results, out _));
        File.WriteAllText(Path.Join(results, "extra.txt"), "x");
        Assert.False(DotnetCliTestRunSource.TryReadResults(results, out _));
    }

    [Fact]
    public void TryReadResults_ShouldRejectAResultsFileOverTheSizeBound()
    {
        using var directory = TestDirectory.Create();
        using (var stream = File.Create(TestPathUtils.PathUnder(directory.Path, DotnetCliTestRunSource.ResultsFileName)))
        {
            // Setting the length does not write the bytes, so this stays cheap.
            stream.SetLength(DotnetCliTestRunSource.MaximumResultsBytes + 1);
        }

        Assert.False(DotnetCliTestRunSource.TryReadResults(directory.Path, out var results));
        Assert.Empty(results);
    }

    [Fact]
    public void TryReadResults_ShouldRejectAWellFormedResultsFileOverTheSizeBound()
    {
        // The file parses, so only the length check can reject it.
        using var directory = TestDirectory.Create();
        var path = TestPathUtils.PathUnder(directory.Path, DotnetCliTestRunSource.ResultsFileName);
        File.WriteAllText(path, Trx(0, string.Empty));
        var length = new FileInfo(path).Length;

        Assert.True(DotnetCliTestRunSource.TryReadResults(directory.Path, length, out _));
        Assert.False(DotnetCliTestRunSource.TryReadResults(directory.Path, length - 1, out var results));
        Assert.Empty(results);
    }

    [Fact]
    public void TryReadResults_ShouldRejectADirectoryNamedLikeTheResultsFile()
    {
        using var directory = TestDirectory.Create();
        Directory.CreateDirectory(TestPathUtils.PathUnder(directory.Path, DotnetCliTestRunSource.ResultsFileName));

        Assert.False(DotnetCliTestRunSource.TryReadResults(directory.Path, out _));
    }

    [Fact]
    public async Task EvaluateAsync_ShouldClassifyToolchainShapeAndRunner()
    {
        using var directory = TestDirectory.Create();
        var project = Project(directory.Path);

        Assert.Equal(TestRunReason.ToolchainUnavailable, (await Evaluate(project, new ChildProcessResult(false, -1, "", "", false))).Failure);
        Assert.Equal(
            TestRunReason.ToolchainUnavailable,
            (await Evaluate(project, new ChildProcessResult(true, 145, "", "A compatible .NET SDK was not found.", false))).Failure);
        Assert.Equal(TestRunReason.ToolchainUnavailable, (await Evaluate(project, new ChildProcessResult(true, 1, "No .NET SDKs were found.", "", false))).Failure);
        Assert.Equal(TestRunReason.BuildFailed, (await Evaluate(project, new ChildProcessResult(true, 1, "error MSB4025", "", false))).Failure);
        Assert.Equal(TestRunReason.BuildFailed, (await Evaluate(project, Ok("not json"))).Failure);
        Assert.Equal(TestRunReason.BuildFailed, (await Evaluate(project, Ok("[]"))).Failure);
        Assert.Equal(TestRunReason.BuildFailed, (await Evaluate(project, Ok("""{ "Properties": 1 }"""))).Failure);
        Assert.Equal(
            TestRunReason.MultiTargetUnsupported,
            (await Evaluate(project, Ok("""{ "Properties": { "TargetFrameworks": "net10.0;net9.0", "RunSettingsFilePath": "x", "TestingPlatformDotnetTestSupport": "true" } }"""))).Failure);
        Assert.Equal(
            TestRunReason.RunSettingsUnsupported,
            (await Evaluate(project, Ok("""{ "Properties": { "TargetFrameworks": "net10.0", "RunSettingsFilePath": "x", "TestingPlatformDotnetTestSupport": "true" } }"""))).Failure);
        var vsTestSetting = await Evaluate(project, Ok("""{ "Properties": { "TargetFramework": "net10.0", "RunSettingsFilePath": "", "VSTestSetting": "ci.runsettings", "TestingPlatformDotnetTestSupport": "true" } }"""));
        Assert.Equal(TestRunReason.RunSettingsUnsupported, vsTestSetting.Failure);
        Assert.Equal("VSTestSetting is set", vsTestSetting.Detail);
        Assert.Null((await Evaluate(project, Ok("""{ "Properties": { "TargetFramework": "net10.0", "VSTestSetting": "  " } }"""))).Failure);
        Assert.Null((await Evaluate(project, Ok("""{ "Properties": { "TargetFramework": "net10.0", "VSTestSetting": 5 } }"""))).Failure);
        Assert.Equal(
            TestRunReason.UnsupportedRunner,
            (await Evaluate(project, Ok("""{ "Properties": { "TargetFramework": "net10.0", "TestingPlatformDotnetTestSupport": "true" } }"""))).Failure);
        var supported = await Evaluate(project, Ok("""{ "Properties": { "TargetFramework": "net10.0", "TargetFrameworks": "", "RunSettingsFilePath": "", "TestingPlatformDotnetTestSupport": "" } }"""));
        Assert.Null(supported.Failure);
        Assert.Equal("evaluate", supported.Phase);
        Assert.Equal(
            ["dotnet-x", "msbuild", "A.Tests.csproj", "-property:Configuration=Debug", "-getProperty:TargetFrameworks", "-getProperty:TargetFramework", "-getProperty:RunSettingsFilePath", "-getProperty:VSTestSetting", "-getProperty:TestingPlatformDotnetTestSupport", "-nologo"],
            supported.Command);

        File.WriteAllText(Path.Join(project.Directory, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var unsupported = await Evaluate(project, Ok("""{ "Properties": { "TargetFramework": "net10.0" } }"""));
        Assert.Equal(TestRunReason.UnsupportedRunner, unsupported.Failure);
        Assert.Contains("Microsoft.Testing.Platform", unsupported.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreAndBuild_ShouldApplyLockedModeOnlyToRestore(bool lockedMode)
    {
        using var directory = TestDirectory.Create();
        var project = Project(directory.Path);
        var runner = new ScriptedRunner(Ok(""), new ChildProcessResult(true, 1, "", "", false), Ok(""), new ChildProcessResult(false, -1, "", "", false));
        var source = new DotnetCliTestRunSource(runner, "dotnet-x", "Release", lockedMode);

        Assert.Null((await source.RestoreAsync(project, CancellationToken.None)).Failure);
        Assert.Equal(TestRunReason.RestoreFailed, (await source.RestoreAsync(project, CancellationToken.None)).Failure);
        var build = await source.BuildAsync(project, CancellationToken.None);
        Assert.Null(build.Failure);
        Assert.Equal(TestRunReason.BuildFailed, (await source.BuildAsync(project, CancellationToken.None)).Failure);

        string[] lockArguments = lockedMode ? ["--locked-mode"] : [];
        Assert.Equal(["restore", "A.Tests.csproj", "-property:Configuration=Release", .. lockArguments, "--disable-build-servers", "-nodeReuse:false", "-tl:off", "--nologo"], runner.Requests[0].Arguments);
        Assert.Equal(["dotnet-x", "build", "A.Tests.csproj", "-c", "Release", "--no-restore", "--disable-build-servers", "-nodeReuse:false", "-tl:off", "--nologo"], build.Command);
        Assert.All(runner.Requests, request => Assert.Equal(project.Directory, request.WorkingDirectory));
        Assert.All(runner.Requests, static request => Assert.Equal("dotnet-x", request.FileName));
    }

    [Fact]
    public async Task DiscoverAsync_ShouldPassTheFilterAsOneArgumentAndClassifyFailures()
    {
        using var directory = TestDirectory.Create();
        var project = Project(directory.Path) with { Filter = "Name=a b|Name=c&d" };
        var runner = new ScriptedRunner(
            Ok($"{Marker}\n    A.One\n"),
            new ChildProcessResult(true, 1, "", "", false),
            new ChildProcessResult(false, -1, "", "", false),
            new ChildProcessResult(true, 0, $"{Marker}\n    A.One\n", "", true),
            Ok("unexpected"));
        var source = new DotnetCliTestRunSource(runner, "dotnet", "Debug");

        var listed = await source.DiscoverAsync(project, CancellationToken.None);
        Assert.Null(listed.Step.Failure);
        Assert.Equal(["A.One"], listed.Identities);
        Assert.Equal(
            ["test", "A.Tests.csproj", "-c", "Debug", "--no-build", "--nologo", "--disable-build-servers", "-nodeReuse:false", "-tl:off", "--filter", "Name=a b|Name=c&d", "--list-tests"],
            runner.Requests[0].Arguments);
        Assert.Equal("ExitCode", (await source.DiscoverAsync(project, CancellationToken.None)).Step.SubReason);
        Assert.Equal("ExitCode", (await source.DiscoverAsync(project, CancellationToken.None)).Step.SubReason);
        Assert.Equal("OutputTooLarge", (await source.DiscoverAsync(project, CancellationToken.None)).Step.SubReason);
        var unrecognized = await source.DiscoverAsync(project, CancellationToken.None);
        Assert.Equal(TestRunReason.DiscoveryFailed, unrecognized.Step.Failure);
        Assert.Equal("ListingShapeUnrecognized", unrecognized.Step.SubReason);
        Assert.Empty(unrecognized.Identities);
    }

    [Fact]
    public async Task RunAsync_ShouldReadResultsAndSeparateAbortedFromStale()
    {
        using var directory = TestDirectory.Create();
        var project = Project(directory.Path);
        const string passed = """<UnitTestResult testName="A.One" outcome="Passed" />""";
        const string failed = """<UnitTestResult testName="A.One" outcome="Failed" />""";

        var ok = await Run(project, Path.Join(directory.Path, "r1"), 0, Trx(1, passed));
        Assert.Null(ok.Step.Failure);
        Assert.Equal([new TestCaseResult("A.One", TestCaseOutcome.Passed)], ok.Results);
        Assert.Equal(["dotnet", "test", "A.Tests.csproj", "-c", "Debug", "--no-build", "--nologo", "--disable-build-servers", "-nodeReuse:false", "-tl:off"], ok.Step.Command);

        Assert.Null((await Run(project, Path.Join(directory.Path, "r2"), 1, Trx(1, failed))).Step.Failure);
        Assert.Equal(TestRunReason.RunAborted, (await Run(project, Path.Join(directory.Path, "r3"), 1, Trx(1, passed))).Step.Failure);
        Assert.Equal(TestRunReason.RunAborted, (await Run(project, Path.Join(directory.Path, "r4"), 134, null)).Step.Failure);
        Assert.Equal(TestRunReason.ResultsMissingOrStale, (await Run(project, Path.Join(directory.Path, "r5"), 0, null)).Step.Failure);
        Assert.Equal(TestRunReason.ResultsMissingOrStale, (await Run(project, Path.Join(directory.Path, "r6"), 0, Trx(3, passed))).Step.Failure);

        // A results directory that already exists is stale by definition, and the child is never started.
        var stale = new ScriptedRunner();
        var existing = await new DotnetCliTestRunSource(stale, "dotnet", "Debug").RunAsync(project, Path.Join(directory.Path, "r1"), CancellationToken.None);
        Assert.Equal(TestRunReason.ResultsMissingOrStale, existing.Step.Failure);
        Assert.Empty(stale.Requests);
    }

    [Fact]
    public void Constructor_ShouldValidateArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new DotnetCliTestRunSource(null!, "dotnet", "Debug"));
        Assert.ThrowsAny<ArgumentException>(() => new DotnetCliTestRunSource(new ScriptedRunner(), " ", "Debug"));
        Assert.ThrowsAny<ArgumentException>(() => new DotnetCliTestRunSource(new ScriptedRunner(), "dotnet", ""));
    }

    private static Task<TestRunStep> Evaluate(TestRunProject project, ChildProcessResult result) =>
        new DotnetCliTestRunSource(new ScriptedRunner(result), "dotnet-x", "Debug").EvaluateAsync(project, CancellationToken.None);

    private static Task<TestRunResults> Run(TestRunProject project, string resultsDirectory, int exitCode, string? trx)
    {
        var runner = new ScriptedRunner(new ChildProcessResult(true, exitCode, "", "", false))
        {
            OnRun = request =>
            {
                Assert.Equal(["--logger", "trx;LogFileName=results.trx", "--results-directory", resultsDirectory], request.Arguments.TakeLast(4));
                if (trx is not null)
                {
                    File.WriteAllText(TestPathUtils.PathUnder(resultsDirectory, DotnetCliTestRunSource.ResultsFileName), trx);
                }
            },
        };
        return new DotnetCliTestRunSource(runner, "dotnet", "Debug").RunAsync(project, resultsDirectory, CancellationToken.None);
    }

    private static TestRunProject Project(string root)
    {
        var path = Path.Join(root, "tests", "A.Tests", "A.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
        return new TestRunProject("tests/A.Tests/A.Tests.csproj", path, null);
    }

    private static ChildProcessResult Ok(string standardOutput) => new(true, 0, standardOutput, "", false);

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Trx(int total, string results) =>
        $"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>{results}</Results>
          <ResultSummary outcome="Completed"><Counters total="{total}" executed="{total}" /></ResultSummary>
        </TestRun>
        """;

    private sealed class ScriptedRunner(params ChildProcessResult[] results) : IChildProcessRunner
    {
        private readonly Queue<ChildProcessResult> _results = new(results);

        public List<ChildProcessRequest> Requests { get; } = [];

        public Action<ChildProcessRequest>? OnRun { get; init; }

        public Task<ChildProcessResult> RunAsync(ChildProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OnRun?.Invoke(request);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
