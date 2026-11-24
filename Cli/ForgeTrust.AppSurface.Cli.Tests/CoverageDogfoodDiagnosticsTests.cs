using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Checks safe user-facing option diagnostics from the Dogfood executable.</summary>
public sealed class CoverageDogfoodDiagnosticsTests
{
    [Fact]
    public void NativeFilterMatchesTheOrderedEmbeddedSelectorInventory()
    {
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var selectorPath = TestPathUtils.PathUnder(repositoryRoot,
            ".github", "workflows", "native-coverage-test-selectors.txt");
        var selectors = File.ReadAllLines(selectorPath)
            .Select(line => line.Split('\t', 2))
            .Select(entry =>
            {
                Assert.Equal(2, entry.Length);
                return entry[1];
            });

        Assert.Equal(string.Join("|", selectors), NativeCoverageTestSelectorInventory.ReadNativeFilter());
    }

    [Theory]
    [InlineData("2", "appsurface-visual-baseline-png-v1", "diff-policy-version",
        "Pass --contract-version 3 when using --diff-policy-id.")]
    [InlineData("3", "private-selector-value", "unsupported-diff-policy-id",
        "Use appsurface-visual-baseline-png-v1 for --diff-policy-id.")]
    [InlineData("private-version-value", null, "ArgumentException",
        "Inspect retained native command result files and correct the explicit repository policy/configuration.")]
    public async Task RunAsyncReportsSafeOptionDiagnostics(string contractVersion, string? diffPolicyId,
        string expectedReason, string expectedRecovery)
    {
        using var temp = TestDirectory.Create();
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var testOutputDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var configuration = testOutputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("Could not determine the test configuration.");
        var dogfoodAssembly = TestPathUtils.PathUnder(repositoryRoot,
            $"tools/ForgeTrust.AppSurface.Coverage.Dogfood/bin/{configuration}/{testOutputDirectory.Name}/ForgeTrust.AppSurface.Coverage.Dogfood.dll");
        Assert.True(File.Exists(dogfoodAssembly), $"The Dogfood executable was not built: {dogfoodAssembly}");

        var arguments = new List<string>
        {
            dogfoodAssembly, "--operation", "fanin", "--repository", repositoryRoot,
            "--cli", Path.Join(repositoryRoot, "appsurface"), "--output", Path.Join(temp.Path, "result"),
            "--configuration", configuration, "--contract-version", contractVersion
        };
        if (diffPolicyId is not null)
            arguments.AddRange(["--diff-policy-id", diffPolicyId]);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new CoverageRunProcessRequest("dotnet", arguments, repositoryRoot, null, null, CoverageRunProcessLease.Detached());
        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, deadline.Token);
        var error = result.StandardError;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"ASCOV311 Dogfood failed at options ({expectedReason}).", error);
        Assert.Contains(expectedRecovery, error);
        Assert.DoesNotContain("private-", error, StringComparison.Ordinal);
    }
}
