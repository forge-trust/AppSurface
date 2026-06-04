namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class CoverageRunnerOptionsTests
{
    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_ShouldReturnUsageForHelp(string argument)
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            [argument],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Null(result.Options);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.ErrorMessage);
        Assert.True(result.ShowUsage);
    }

    [Fact]
    public void Parse_ShouldPreservePositionalSolutionAndOutput()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            ["custom.slnx", "artifacts/coverage"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.NotNull(result.Options);
        Assert.Equal(Path.Join(workspace.Root, "custom.slnx"), result.Options.SolutionPath);
        Assert.Equal(Path.Join(workspace.Root, "artifacts", "coverage"), result.Options.OutputDirectory);
        Assert.Equal("all", result.Options.GroupName);
        Assert.True(result.Options.BuildSolution);
    }

    [Fact]
    public void Parse_ShouldRejectUnexpectedThirdPositionalArgument()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            ["custom.slnx", "artifacts/coverage", "extra"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Null(result.Options);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Unexpected argument: extra", result.ErrorMessage);
        Assert.True(result.ShowUsage);
    }

    [Fact]
    public void Parse_ShouldResolveRelativeUserPathsFromScriptCallerDirectory()
    {
        using var workspace = TestRepo.Create();
        var caller = Path.Join(workspace.Root, "caller");
        Directory.CreateDirectory(caller);

        var result = CoverageRunnerOptions.Parse(
            ["custom.slnx", "artifacts/coverage"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_CALLER_DIRECTORY"] = caller });

        Assert.NotNull(result.Options);
        Assert.Equal(Path.Join(caller, "custom.slnx"), result.Options.SolutionPath);
        Assert.Equal(Path.Join(caller, "artifacts", "coverage"), result.Options.OutputDirectory);
    }

    [Fact]
    public void Parse_ShouldUseCurrentDirectoryWhenRepositoryRootCannotBeResolved()
    {
        var currentDirectory = Path.Join(Path.GetTempPath(), "coverage-runner-no-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(currentDirectory);
        try
        {
            var result = CoverageRunnerOptions.Parse(
                [],
                currentDirectory,
                new Dictionary<string, string?>());

            Assert.NotNull(result.Options);
            Assert.Equal(Path.GetFullPath(currentDirectory), result.Options.RepositoryRoot);
        }
        finally
        {
            Directory.Delete(currentDirectory, recursive: true);
        }
    }

    [Fact]
    public void Parse_ShouldDefaultNamedGroupsToSkipSolutionBuild()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            ["--group", "tools"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.NotNull(result.Options);
        Assert.Equal("tools", result.Options.GroupName);
        Assert.False(result.Options.BuildSolution);
    }

    [Fact]
    public void Parse_ShouldUseTestGroupEnvironmentDefault()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            [],
            workspace.Root,
            new Dictionary<string, string?> { ["TEST_GROUP"] = "docs" });

        Assert.NotNull(result.Options);
        Assert.Equal("docs", result.Options.GroupName);
        Assert.False(result.Options.BuildSolution);
    }

    [Fact]
    public void Parse_ShouldReadParallelismFromEnvironment()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            [],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = "2" });

        Assert.NotNull(result.Options);
        Assert.Equal(2, result.Options.Parallelism);
    }

    [Theory]
    [InlineData("--group", "--group requires a value")]
    [InlineData("--merge-only", "--merge-only requires a source directory")]
    [InlineData("--output", "--output requires a value")]
    [InlineData("--solution", "--solution requires a value")]
    public void Parse_ShouldRejectMissingOptionValues(string option, string expectedMessage)
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            [option],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Null(result.Options);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(expectedMessage, result.ErrorMessage);
        Assert.True(result.ShowUsage);
    }

    [Fact]
    public void Parse_ShouldRejectUnknownOption()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            ["--wat"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Null(result.Options);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Unknown option: --wat", result.ErrorMessage);
    }

    [Fact]
    public void Parse_ShouldRejectUnknownGroup()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            ["--group", "unknown"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.Null(result.Options);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Unknown test group: unknown", result.ErrorMessage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Parse_ShouldRejectInvalidParallelism(string value)
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            [],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_PARALLELISM"] = value });

        Assert.Null(result.Options);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("COVERAGE_PARALLELISM", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ShouldRejectInvalidBuildSolutionEnvironment()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            [],
            workspace.Root,
            new Dictionary<string, string?> { ["BUILD_SOLUTION"] = "maybe" });

        Assert.Null(result.Options);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("BUILD_SOLUTION must be true or false.", result.ErrorMessage);
    }

    [Fact]
    public void Parse_ShouldReadCoverageFilterEnvironment()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            [],
            workspace.Root,
            new Dictionary<string, string?>
            {
                ["BUILD_CONFIGURATION"] = "Release",
                ["BUILD_NO_RESTORE"] = "true",
                ["INCLUDE_FILTER"] = "[Sample]*",
                ["EXCLUDE_FILTER"] = "[Sample.Tests]*,[Sample.Generated]*",
            });

        Assert.NotNull(result.Options);
        Assert.Equal("Release", result.Options.BuildConfiguration);
        Assert.True(result.Options.BuildNoRestore);
        Assert.Equal("[Sample]*", result.Options.IncludeFilter);
        Assert.Equal("[Sample.Tests]*%2c[Sample.Generated]*", result.Options.ExcludeFilter);
    }

    [Fact]
    public void Parse_ShouldKeepMergeOnlyFromBuildingSolution()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            ["--merge-only", "coverage-shards"],
            workspace.Root,
            new Dictionary<string, string?> { ["BUILD_SOLUTION"] = "true" });

        Assert.NotNull(result.Options);
        Assert.True(result.Options.MergeOnly);
        Assert.False(result.Options.BuildSolution);
        Assert.Equal("merge-only", result.Options.GroupName);
    }
}
