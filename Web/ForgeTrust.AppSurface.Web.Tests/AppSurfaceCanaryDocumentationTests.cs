using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class AppSurfaceCanaryDocumentationTests
{
    [Fact]
    public void NamedCanaryDocumentation_PreservesAdoptionAndReleaseContract()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var readme = File.ReadAllText(Path.Join(repositoryRoot, "Web", "ForgeTrust.AppSurface.Web", "README.md"));
        var packageIndex = File.ReadAllText(Path.Join(repositoryRoot, "packages", "package-index.yml"));
        var generatedChooser = File.ReadAllText(Path.Join(repositoryRoot, "packages", "README.md"));
        var unreleased = File.ReadAllText(Path.Join(repositoryRoot, "releases", "unreleased.md"));

        Assert.Contains("### Named Canary Endpoints", readme, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceCanaryCompletedResponseMode.StatusCode", readme, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceCanaryCompletedResponseMode.AlwaysOk", readme, StringComparison.Ordinal);
        Assert.Contains("caller triggers synthetic work", readme, StringComparison.Ordinal);
        Assert.Contains("issues/645", readme, StringComparison.Ordinal);
        Assert.Contains("compile-only evaluator skeleton", readme, StringComparison.Ordinal);
        Assert.Contains("The placeholder always returns `Pending`", readme, StringComparison.Ordinal);
        Assert.Contains("Budget **under 5 minutes**", readme, StringComparison.Ordinal);
        Assert.Contains("Budget **under 15 minutes**", readme, StringComparison.Ordinal);
        Assert.Contains("CompleteForwardingCanaryEvaluator.ProofKindDetailKey", readme, StringComparison.Ordinal);
        Assert.Contains("#### Parse the envelope and choose an action", readme, StringComparison.Ordinal);
        Assert.Contains("public static class CanaryEnvelopeConsumer", readme, StringComparison.Ordinal);
        Assert.Contains("jq -er", readme, StringComparison.Ordinal);
        Assert.Contains("#### Upgrade from the #623 status-only envelope", readme, StringComparison.Ordinal);
        Assert.Contains("#### Authoring-time validation rescue", readme, StringComparison.Ordinal);
        Assert.Contains("failure thrown inside `EvaluateAsync`", readme, StringComparison.Ordinal);
        Assert.Contains("protected preview named deploy evidence", packageIndex, StringComparison.Ordinal);
        Assert.Contains("protected preview named deploy evidence", generatedChooser, StringComparison.Ordinal);
        Assert.Contains(
            "[protected preview named deploy evidence](../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints)",
            packageIndex,
            StringComparison.Ordinal);
        Assert.Contains(
            "[protected preview named deploy evidence](../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints)",
            generatedChooser,
            StringComparison.Ordinal);
        Assert.Contains(
            "[`ForgeTrust.AppSurface.Web` named canary evaluation](../Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints)",
            unreleased,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedCanaryDocumentation_JqConsumerExecutesSemanticActionMatrix()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var readme = await File.ReadAllTextAsync(Path.Join(repositoryRoot, "Web", "ForgeTrust.AppSurface.Web", "README.md"));
        var filter = ExtractJqFilter(readme);
        var validCases = new (string Json, string Action)[]
        {
            ("{\"name\":\"proof\",\"status\":\"pass\",\"ready\":true}", "continue"),
            ("{\"ready\":false,\"status\":\"pending\",\"name\":\"proof\",\"future\":1}", "wait"),
            ("{\"name\":\"proof\",\"status\":\"stale\",\"ready\":false}", "refresh"),
            ("{\"name\":\"proof\",\"status\":\"not-configured\",\"ready\":false}", "configure"),
            ("{\"name\":\"proof\",\"status\":\"fail\",\"ready\":false}", "investigate"),
            ("{\"name\":\"proof\",\"status\":\"fail\",\"ready\":false,\"reasonCode\":\"checksum-mismatch\"}", "roll-back"),
        };
        var validRun = await RunJqAsync(filter, string.Join('\n', validCases.Select(testCase => testCase.Json)));

        Assert.True(validRun.ExitCode == 0, validRun.StandardError);
        var resultLines = validRun.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(validCases.Length, resultLines.Length);
        for (var index = 0; index < validCases.Length; index++)
        {
            using var result = JsonDocument.Parse(resultLines[index]);
            Assert.Equal(validCases[index].Action, result.RootElement.GetProperty("action").GetString());
        }

        var invalidRun = await RunJqAsync(
            filter,
            "{\"name\":\"proof\",\"status\":\"pending\",\"ready\":true}");
        Assert.NotEqual(0, invalidRun.ExitCode);
        Assert.Contains("ready does not match status", invalidRun.StandardError, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunJqAsync(
        string filter,
        string input)
    {
        var startInfo = new ProcessStartInfo("jq")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-ec");
        startInfo.ArgumentList.Add(filter);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The jq process did not start.");
        }
        catch (Win32Exception)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "jq is unavailable; install jq to execute the documented named-canary shell consumer locally.");
        }

        using (process)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, standardOutput, standardError);
        }
    }

    private static string ExtractJqFilter(string readme)
    {
        const string prefix = "jq -er '\n";
        const string suffix = "\n  end'";
        var start = readme.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, "The named-canary jq filter was not found.");
        start += prefix.Length;
        var end = readme.IndexOf(suffix, start, StringComparison.Ordinal);
        Assert.True(end >= 0, "The named-canary jq filter terminator was not found.");
        return readme[start..(end + "\n  end".Length)];
    }
}
