using System.Diagnostics;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageSolutionScriptTests
{
    [Fact]
    public void Script_ShouldDelegateDefaultLaneToSourceCliWithManagedEvidence()
    {
        var script = ReadScript();

        Assert.Contains("CLI_PROJECT=\"$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj\"", script, StringComparison.Ordinal);
        Assert.Contains("coverage", script, StringComparison.Ordinal);
        Assert.Contains("run", script, StringComparison.Ordinal);
        Assert.DoesNotContain("COVERAGE_RUNNER_PROJECT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  --include ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  --exclude ", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.AppSurface.Config.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project AuthWebRazorWireProofExample.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.RazorWire.Cli.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--exclusive-test-project ForgeTrust.AppSurface.Web.Tailwind.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--test-results junit", script, StringComparison.Ordinal);
        Assert.Contains("--slow-test-diagnostics", script, StringComparison.Ordinal);
        Assert.Contains("--logger \"GitHubActions;report-warnings=false\"", script, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_GATE_DIFF_BASE", script, StringComparison.Ordinal);
        Assert.Contains("coverage\n  gate", script, StringComparison.Ordinal);
        Assert.Contains("--min-line 95", script, StringComparison.Ordinal);
        Assert.Contains("--min-branch 85", script, StringComparison.Ordinal);
        Assert.Contains("--diff-base \"$COVERAGE_GATE_DIFF_BASE\"", script, StringComparison.Ordinal);
        Assert.Contains("--min-patch-line 95", script, StringComparison.Ordinal);
        Assert.Contains("--min-patch-branch 85", script, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(script, "--no-restore"));
        Assert.Contains("if [[ -n \"$COVERAGE_GATE_DIFF_BASE\" ]]; then", script, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredWrapperInputs))]
    public async Task Script_ShouldRejectRetiredInputsBeforeStartingDotnetOrCreatingOutput(
        string input,
        string[] arguments,
        string? environmentName,
        string? environmentValue,
        string expectedReplacement)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync(arguments, environmentName, environmentValue);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(input, result.StandardError, StringComparison.Ordinal);
        Assert.Contains(expectedReplacement, result.StandardError, StringComparison.Ordinal);
        Assert.False(result.DotnetInvoked, "A rejected wrapper form must not invoke dotnet.");
        Assert.False(result.CoverageOutputCreated, "A rejected wrapper form must not create coverage output.");
    }

    [Fact]
    public void BuildWorkflow_ShouldUseCoverageSolutionScriptForDefaultLane()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("BUILD_CONFIGURATION: Release", workflow, StringComparison.Ordinal);
        Assert.Contains("BUILD_NO_RESTORE: true", workflow, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_PARALLELISM: 2", workflow, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_GATE_DIFF_BASE: ${{ github.event_name == 'pull_request' && 'HEAD^1' || '' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/coverage-solution.sh", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "files: 'TestResults/coverage-merged/coverage.cobertura.xml'\n          disable_search: true",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("coverage run \\", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Gate PR coverage with AppSurface CLI", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Gate baseline coverage with AppSurface CLI", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void RazorWireCoverageProofSources_ShouldContainExistingProductionSources()
    {
        Assert.True(RazorWireCoverageProofSources.All.Count >= 2);

        foreach (var source in RazorWireCoverageProofSources.All)
        {
            Assert.True(File.Exists(FindRepositoryFile(source.Split('/'))), $"Expected RazorWire proof source '{source}' to exist.");
        }
    }

    [Fact]
    public void RazorWireCoverageProofSources_ShouldSelectConfiguredCoveredSourceInPreferenceOrder()
    {
        var secondSource = RazorWireCoverageProofSources.All[1];

        Assert.Equal(
            secondSource,
            RazorWireCoverageProofSources.SelectCoveredSource([secondSource.Replace('/', '\\')]));
        Assert.Equal(
            RazorWireCoverageProofSources.All[0],
            RazorWireCoverageProofSources.SelectCoveredSource([secondSource, RazorWireCoverageProofSources.All[0]]));
    }

    [Fact]
    public void RazorWireCoverageProofSources_ShouldRejectWhenNoConfiguredSourceIsCovered()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => RazorWireCoverageProofSources.SelectCoveredSource(["Web/Unrelated/NotCovered.cs"]));

        Assert.Equal(
            $"Merged Cobertura did not contain a maintained RazorWire proof source. Expected one of: {string.Join(", ", RazorWireCoverageProofSources.All)}.",
            exception.Message);
    }

    [Fact]
    public void LegacyCoverageRunnerSources_ShouldNotExist()
    {
        var repositoryRoot = Path.GetDirectoryName(FindRepositoryFile("ForgeTrust.AppSurface.slnx"))!;

        Assert.False(File.Exists(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.CoverageRunner", "ForgeTrust.AppSurface.CoverageRunner.csproj")));
        Assert.False(File.Exists(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.CoverageRunner", "Program.cs")));
        Assert.False(File.Exists(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.CoverageRunner.Tests", "ForgeTrust.AppSurface.CoverageRunner.Tests.csproj")));
        Assert.False(File.Exists(Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.CoverageRunner.Tests", "CoverageRunnerApplicationTests.cs")));
    }

    [Theory]
    [InlineData("origin/main", true)]
    [InlineData("", false)]
    public async Task Script_ShouldForwardPatchGateArgumentsOnlyWhenDiffBaseIsNonEmpty(string diffBase, bool expectsPatchGate)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync([], "COVERAGE_GATE_DIFF_BASE", diffBase, dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        var invocations = result.DotnetInvocations;
        Assert.Equal(2, invocations.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(argument => argument == "coverage"));
        Assert.Contains("run", invocations, StringComparison.Ordinal);
        Assert.Contains("gate", invocations, StringComparison.Ordinal);

        if (expectsPatchGate)
        {
            Assert.Contains("--diff-base\norigin/main\n--min-patch-line\n95\n--min-patch-branch\n85", invocations, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("--diff-base", invocations, StringComparison.Ordinal);
            Assert.DoesNotContain("--min-patch-line", invocations, StringComparison.Ordinal);
            Assert.DoesNotContain("--min-patch-branch", invocations, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Script_ShouldDefaultPatchGateToOriginMainWhenDiffBaseIsUnset()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync([], null, null, dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "--diff-base\norigin/main\n--min-patch-line\n95\n--min-patch-branch\n85",
            result.DotnetInvocations,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_ShouldForwardNoRestoreToCoverageRunWhenRequested()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync([], "BUILD_NO_RESTORE", "true", dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "--logger\nGitHubActions;report-warnings=false\n--no-restore",
            result.DotnetInvocations,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_ShouldForwardResourceIntensiveSuitesAsExclusiveCoverageProjects()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunScriptAsync([], null, null, dotnetExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "--exclusive-test-project\nForgeTrust.AppSurface.Config.Tests.csproj\n--exclusive-test-project\nAuthWebRazorWireProofExample.Tests.csproj\n--exclusive-test-project\nForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj\n--exclusive-test-project\nForgeTrust.RazorWire.Cli.Tests.csproj\n--exclusive-test-project\nForgeTrust.AppSurface.Web.Tailwind.Tests.csproj",
            result.DotnetInvocations,
            StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> RetiredWrapperInputs()
    {
        yield return new object[] { "arguments", new[] { "custom.slnx", "custom-output" }, null!, null!, "appsurface coverage run --solution" };
        yield return new object[] { "--group", new[] { "--group", "web" }, null!, null!, "appsurface coverage run --test-project" };
        yield return new object[] { "--list-groups", new[] { "--list-groups" }, null!, null!, "appsurface coverage run --help" };
        yield return new object[] { "--merge-only", new[] { "--merge-only", "coverage-shards" }, null!, null!, "appsurface coverage merge --source" };
        yield return new object[] { "arguments", new[] { "--output", "custom-output" }, null!, null!, "appsurface coverage run --solution" };
        yield return new object[] { "TEST_GROUP", Array.Empty<string>(), "TEST_GROUP", "all", "appsurface coverage run --test-project" };
        yield return new object[] { "INCLUDE_FILTER", Array.Empty<string>(), "INCLUDE_FILTER", "[Sample]*", "appsurface coverage run --include" };
        yield return new object[] { "EXCLUDE_FILTER", Array.Empty<string>(), "EXCLUDE_FILTER", "[Generated]*", "appsurface coverage run --exclude" };
        yield return new object[] { "BUILD_SOLUTION", Array.Empty<string>(), "BUILD_SOLUTION", "true", "appsurface coverage run --no-build" };
    }

    private static string ReadScript()
        => ReadRepositoryFile("scripts", "coverage-solution.sh");

    private static string ReadWorkflow()
        => ReadRepositoryFile(".github", "workflows", "build.yml");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while (true)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            startIndex = index + value.Length;
        }
    }

    private static async Task<ScriptResult> RunScriptAsync(
        IReadOnlyList<string> arguments,
        string? environmentName,
        string? environmentValue,
        int dotnetExitCode = 97)
    {
        using var workspace = TemporaryDirectory.Create("appsurface-coverage-script-");
        var scriptPath = Path.Join(workspace.Path, "scripts", "coverage-solution.sh");
        var binDirectory = Path.Join(workspace.Path, "bin");
        var dotnetInvocationMarker = Path.Join(workspace.Path, "dotnet-invoked");
        var coverageOutputDirectory = Path.Join(workspace.Path, "TestResults", "coverage-merged");
        var dotnetStub = Path.Join(binDirectory, "dotnet");

        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        Directory.CreateDirectory(binDirectory);
        await File.WriteAllTextAsync(scriptPath, ReadScript());
        await File.WriteAllTextAsync(
            dotnetStub,
            "#!/usr/bin/env bash\nprintf '%s\\n' \"$@\" >> \"$DOTNET_INVOCATION_MARKER\"\nexit \"$DOTNET_STUB_EXIT_CODE\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                dotnetStub,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workspace.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var retiredEnvironmentName in new[] { "TEST_GROUP", "INCLUDE_FILTER", "EXCLUDE_FILTER", "BUILD_SOLUTION" })
        {
            startInfo.Environment.Remove(retiredEnvironmentName);
        }

        startInfo.Environment.Remove("COVERAGE_GATE_DIFF_BASE");

        startInfo.Environment["DOTNET_INVOCATION_MARKER"] = dotnetInvocationMarker;
        startInfo.Environment["DOTNET_STUB_EXIT_CODE"] = dotnetExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["PATH"] = string.Concat(binDirectory, Path.PathSeparator, Environment.GetEnvironmentVariable("PATH"));
        if (environmentName is not null)
        {
            startInfo.Environment[environmentName] = environmentValue;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start bash.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), standardOutputTask, standardErrorTask);

        return new ScriptResult(
            process.ExitCode,
            await standardErrorTask,
            File.Exists(dotnetInvocationMarker),
            File.Exists(dotnetInvocationMarker) ? await File.ReadAllTextAsync(dotnetInvocationMarker) : string.Empty,
            Directory.Exists(coverageOutputDirectory));
    }

    private static string ReadRepositoryFile(params string[] paths)
        => File.ReadAllText(FindRepositoryFile(paths));

    private static string FindRepositoryFile(params string[] paths)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var file = Path.Join([directory.FullName, .. paths]);
            if (File.Exists(file))
            {
                return file;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Join(paths)} from the test working directory.");
    }

    private sealed record ScriptResult(
        int ExitCode,
        string StandardError,
        bool DotnetInvoked,
        string DotnetInvocations,
        bool CoverageOutputCreated);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), string.Concat(prefix, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
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
