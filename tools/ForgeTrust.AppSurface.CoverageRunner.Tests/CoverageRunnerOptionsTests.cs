namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class CoverageRunnerOptionsTests
{
    [Fact]
    public void Parse_ShouldPreservePositionalSolutionAndOutput()
    {
        using var workspace = TestRepo.Create();

        var result = CoverageRunnerOptions.Parse(
            ["custom.slnx", "artifacts/coverage"],
            workspace.Root,
            new Dictionary<string, string?>());

        Assert.NotNull(result.Options);
        Assert.Equal(Path.Combine(workspace.Root, "custom.slnx"), result.Options.SolutionPath);
        Assert.Equal(Path.Combine(workspace.Root, "artifacts", "coverage"), result.Options.OutputDirectory);
        Assert.Equal("all", result.Options.GroupName);
        Assert.True(result.Options.BuildSolution);
    }

    [Fact]
    public void Parse_ShouldResolveRelativeUserPathsFromScriptCallerDirectory()
    {
        using var workspace = TestRepo.Create();
        var caller = Path.Combine(workspace.Root, "caller");
        Directory.CreateDirectory(caller);

        var result = CoverageRunnerOptions.Parse(
            ["custom.slnx", "artifacts/coverage"],
            workspace.Root,
            new Dictionary<string, string?> { ["COVERAGE_CALLER_DIRECTORY"] = caller });

        Assert.NotNull(result.Options);
        Assert.Equal(Path.Combine(caller, "custom.slnx"), result.Options.SolutionPath);
        Assert.Equal(Path.Combine(caller, "artifacts", "coverage"), result.Options.OutputDirectory);
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
