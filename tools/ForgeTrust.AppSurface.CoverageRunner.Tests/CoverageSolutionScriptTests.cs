namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class CoverageSolutionScriptTests
{
    [Fact]
    public void Script_ShouldDelegateDefaultLaneToSourceCliWithManagedEvidence()
    {
        var script = ReadScript();

        Assert.Contains("CLI_PROJECT=\"$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj\"", script, StringComparison.Ordinal);
        Assert.Contains("coverage", script, StringComparison.Ordinal);
        Assert.Contains("run", script, StringComparison.Ordinal);
        Assert.Contains("--include \"${INCLUDE_FILTER:-[ForgeTrust.AppSurface.*]*}\"", script, StringComparison.Ordinal);
        Assert.Contains("--exclude \"${EXCLUDE_FILTER:-[*.Tests]*,[*.IntegrationTests]*}\"", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.AppSurface.Web.Tailwind.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--test-results junit", script, StringComparison.Ordinal);
        Assert.Contains("--slow-test-diagnostics", script, StringComparison.Ordinal);
        Assert.Contains("--logger \"GitHubActions;report-warnings=false\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_ShouldKeepLegacyRunnerForCompatibilityBoundaries()
    {
        var script = ReadScript();

        Assert.Contains("COVERAGE_RUNNER_PROJECT=\"$ROOT_DIR/tools/ForgeTrust.AppSurface.CoverageRunner/ForgeTrust.AppSurface.CoverageRunner.csproj\"", script, StringComparison.Ordinal);
        Assert.Contains("if [[ \"$#\" -gt 0 ]]; then", script, StringComparison.Ordinal);
        Assert.Contains("if [[ \"${TEST_GROUP:-all}\" != \"all\" ]]; then", script, StringComparison.Ordinal);
        Assert.Contains("case \"${BUILD_SOLUTION:-true}\" in", script, StringComparison.Ordinal);
        Assert.Contains("exec dotnet \"${dotnet_run_args[@]}\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWorkflow_ShouldUseCoverageSolutionScriptForDefaultLane()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("BUILD_CONFIGURATION: Release", workflow, StringComparison.Ordinal);
        Assert.Contains("BUILD_NO_RESTORE: true", workflow, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_PARALLELISM: 2", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/coverage-solution.sh", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage run \\", workflow, StringComparison.Ordinal);
    }

    private static string ReadScript()
        => ReadRepositoryFile("scripts", "coverage-solution.sh");

    private static string ReadWorkflow()
        => ReadRepositoryFile(".github", "workflows", "build.yml");

    private static string ReadRepositoryFile(params string[] paths)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var file = Path.Join([directory.FullName, .. paths]);
            if (File.Exists(file))
            {
                return File.ReadAllText(file);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Join(paths)} from the test working directory.");
    }
}
